using System;

namespace Materialklang
{
    // =========================================================================================
    //  0.8.6: Reihum-Abarbeitung fuer AudioSourceCulling (ohne Unity, im Offline-Test geprueft)
    //
    //  Spielfehler (EFT 4.1.6, Assembly-CSharp, Typ AudioSourceCulling, Coroutine Work()):
    //    jedes Bild   num2 = _audioSources.Count - 1;  while (num2 >= 0 && num < int_1) { ... num2--; }  yield return null;
    //    int_1 = Math.Min(_audioSources.Count, 10)  (Register/Remove/Null-Eintrag)
    //  -> jedes Bild wieder ab dem Listenende, hoechstens 10 Eintraege: nur die LETZTEN 10 angemeldeten Quellen werden je
    //  geschaltet (enabled/priority nach Abstand, Play bei NeedRestart). Alle davor bleiben fuer den ganzen Raid im Zustand,
    //  in dem sie angemeldet wurden, und werden auch nie als Null-Eintrag entfernt.
    //
    //  Fix: ein Zeiger wandert ueber die ganze Liste (Wrap am Ende). Budget je Bild = so viel wie das Spiel (int_1),
    //  mehr nur, wenn sonst eine Runde laenger als 'runde' Sekunden dauern wuerde. Entfernen vor dem Zeiger schiebt ihn mit,
    //  damit kein Eintrag uebersprungen wird; Leeren setzt ihn auf 0. Wirft ein Eintrag, rueckt der Zeiger trotzdem weiter.
    //  Schalten wie das Original (enabled = inReichweite), ausgenommen nur Quellen, die ein Skript nachweislich abgeschaltet
    //  hat (Steuerung/SkriptAus) - erkannt an den steuernden Komponenten, nicht an einem eigenen Merker.
    // =========================================================================================
    public interface IReihumListe
    {
        int Anzahl { get; }
        /// <summary>false = Null-Eintrag (AudioSource oder Transform zerstoert) -> wird wie im Spiel entfernt.</summary>
        bool Gueltig(int i);
        void Bearbeiten(int i);
        /// <summary>Eintrag i entfernen (Liste + InstanceID-Menge + int_1 nachfuehren, wie im Spiel).</summary>
        void Entfernen(int i);
    }

    public static class CullingKern
    {
        /// <summary>MAX_UPDATES_PER_FRAME des Spiels.</summary>
        public const int SpielBudget = 10;
        /// <summary>Jede Quelle kommt spaetestens nach so vielen Sekunden wieder dran (bei Bildzeit dt).</summary>
        public const double RundeSek = 0.5;

        /// <summary>Wird der Eintrag index (0 = zuerst angemeldet) von n vom ORIGINAL-Work je geschaltet? (nur die letzten 10)</summary>
        public static bool VomSpielErreicht(int index, int n) => index >= 0 && index < n && index >= n - Math.Min(n, SpielBudget);

        /// <summary>Eintraege je Bild: mindestens das Spielbudget (int_1 = min(n, 10)), so viele, dass eine ganze Runde
        /// hoechstens 'runde' Sekunden dauert, nie mehr als n.</summary>
        public static int Budget(int n, int spielBudget, double dt, double runde = RundeSek)
        {
            if (n <= 0) return 0;
            int b = Math.Max(0, Math.Min(spielBudget, n));
            if (dt > 0 && runde > 0)
            {
                double proBild = n * Math.Min(dt, 1.0) / runde;
                int noetig = (int)Math.Ceiling(proBild - 1e-9);
                if (noetig > b) b = noetig;
            }
            return Math.Min(Math.Max(b, Math.Min(1, n)), n);
        }

        /// <summary>Ein Bild: bis zu 'budget' Eintraege ab 'zeiger' bearbeiten (Null-Eintraege entfernen, zaehlt wie im Spiel
        /// zum Budget). Gibt die Zahl der Schritte zurueck; 'runden' zaehlt jeden Umlauf ueber das Listenende.</summary>
        public static int Bild(IReihumListe l, ref int zeiger, int budget, ref int runden)
        {
            int schritte = 0;
            if (zeiger < 0) zeiger = 0;
            while (schritte < budget)
            {
                int n = l.Anzahl;
                if (n <= 0) { zeiger = 0; break; }
                if (zeiger >= n) { zeiger = 0; runden++; }
                if (l.Gueltig(zeiger))
                {
                    // Wirft ein Eintrag, rueckt der Zeiger trotzdem weiter (sonst stuende das Culling der ganzen Liste an
                    // diesem Eintrag still); die Ausnahme geht an den Aufrufer, das naechste Bild macht beim Nachfolger weiter.
                    try { l.Bearbeiten(zeiger); }
                    finally { zeiger++; }
                }
                else l.Entfernen(zeiger);          // Zeiger bleibt: der naechste Eintrag rueckt nach
                schritte++;
            }
            if (zeiger >= l.Anzahl && l.Anzahl > 0) { zeiger = 0; runden++; }
            return schritte;
        }

        /// <summary>Wer steuert enabled einer Quelle ausser dem Culling? (Beleg EFT 4.1.6, Assembly-CSharp)
        /// <para>Wunsch: das Skript setzt enabled gezielt und der letzte Befehl ist bekannt - ToggleAudioSourceByEvent
        /// (OnNeedToEnable/OnNeedToDisable: _audioSource.enabled = true/false; Lighthouse-Serverraum, Uhren des Leuchtturmwaerters;
        /// Startzustand = enabled beim Awake) und ClientBroadcastPlayer.ChangeAudibleState (Mute: Stop + _sourceA/_sourceB.enabled =
        /// false, Unmute: true).</para>
        /// <para>Lok: Locomotive.Sources - FadeIn setzt enabled = true und volume = 0 und blendet hoch, FadeOut blendet volume auf 0,
        /// dann Stop + enabled = false. Ausgeblendet = aus und volume &lt;= 0.</para></summary>
        public enum Steuerung : byte { Keine = 0, Wunsch = 1, Lok = 2 }

        /// <summary>Hat ein Skript die Quelle bewusst abgeschaltet? Nur dann darf das Culling sie in Reichweite nicht einschalten.
        /// Keine: nie (Original-Verhalten). Wunsch: letzter Skriptbefehl war "aus". Lok: aus und ausgeblendet (volume &lt;= 0) -
        /// eine Lok-Quelle, die das Culling aus Entfernung abgeschaltet hat, hat noch Lautstaerke und kommt wieder; eine gerade
        /// eingeblendete (an, volume 0) bleibt an.</summary>
        public static bool SkriptAus(Steuerung st, bool wunschAn, bool eingeschaltet, float lautstaerke)
        {
            switch (st)
            {
                case Steuerung.Wunsch: return !wunschAn;
                case Steuerung.Lok: return !eingeschaltet && !(lautstaerke > 0f);
                default: return false;
            }
        }

        /// <summary>Neuer enabled-Zustand: wie das Original (enabled = inReichweite) fuer jeden Eintrag - auch fuer Quellen, die beim
        /// Register schon aus waren (SourceOccluder.SetupSources meldet alle Kinder an) oder die nach Remove/Register neu
        /// angemeldet wurden (BaseAmbientSoundPlayer Play/Stop). Einzige Ausnahme: ein Skript hat sie bewusst abgeschaltet
        /// (SkriptAus) - das Original ueberschrieb das jedes Mal, reihum traefe es jede solche Quelle.</summary>
        public static bool Schalter(bool inReichweite, bool skriptAus) => inReichweite && !skriptAus;

        /// <summary>Vor CleanRegisteredAudioSources mit laufender Arbeit (CameraManager.method_7 meldet danach neu an, aber nur
        /// isActiveAndEnabled-Quellen): eine vom Culling ausgeschaltete Quelle einschalten, damit sie wieder angemeldet wird -
        /// sonst bliebe sie fuer den Raid stumm. Nicht beim Abbau (AmbientAudioSystem.Dispose: StopWork vor dem Leeren) und
        /// nicht, wenn ein Skript sie abgeschaltet hat.</summary>
        public static bool VorLeerenEinschalten(bool arbeitet, bool eingeschaltet, bool skriptAus) => arbeitet && !eingeschaltet && !skriptAus;

        /// <summary>Nach einem Audio-Neustart durch Unity (AudioSettings.OnAudioConfigurationChanged, z. B. Geraetewechsel
        /// HDMI/Headset) neu starten? Nur was beim letzten Besuch spielte, jetzt steht, an, aktiv, in Reichweite ist und
        /// das Culling nicht selbst neu startet (NeedRestart). Eine vom Skript angehaltene Quelle (Lampe Stop) spielte schon
        /// vorher nicht.</summary>
        public static bool NeustartNachKonfig(bool spielteZuletzt, bool spielt, bool eingeschaltet, bool aktiv, bool inReichweite, bool needRestart)
            => spielteZuletzt && !spielt && eingeschaltet && aktiv && inReichweite && !needRestart;

        /// <summary>Eintrag index wurde von aussen entfernt (AudioSourceCulling.Remove): Zeiger dahinter rueckt mit.</summary>
        public static void Entfernt(ref int zeiger, int index)
        {
            if (index >= 0 && index < zeiger) zeiger--;
        }
    }
}
