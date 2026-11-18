using System;
using System.Collections.Generic;

namespace Materialklang
{
    /// <summary>Geschaetzte Quellstaerke einer ortsfesten Kartenquelle.</summary>
    internal enum QuellKlasse { Klein = 0, Mittel = 1, Laut = 2, Fahrzeug = 3 }

    // 0.8.5 - 17. All fixed sources: gemeinsame Regel fuer alle ortsfesten 3D-Quellen der Karten (reine Rechnung, im Offline-Test).
    //
    // Inventur aller Kartenszenen (541 level*-Dateien, 3514 Quellen, 08.11.2026): fast jede ortsfeste Quelle hat eine Kurve, die bei
    // MaxDistance auf 0 geht (linear oder Custom), und faellt ab r0 steiler als 1/r - bei 0,8 x MaxDistance 4-15 dB unter einer
    // Punktquelle. Drei Wege ins Spiel:
    //   Ambient  = BaseAmbientSoundPlayer (Abschnitt 15, ohne Okklusion),
    //   Occluded = SourceOccluder -> SimpleSource im Okklusionssystem (Abschnitt 16, mit Waenden/Raeumen/Luft von True Acoustics),
    //   Plain    = rohe Szenen-AudioSource ohne steuerndes Skript (neu, ohne Okklusion: Feuer, Fackeln, Generator Streets ...).
    // Regel fuer alle drei: bis zum Bezugsabstand die Spielkurve (0.8.6, UmgebungKern.Bezugsabstand; bis dahin r0), danach das
    // Abstandsgesetz der Quellart (1/r bzw. Flaeche), Reichweite = alt x Faktor, gedeckelt nach Klasse, weicher Auslauf
    // (UmgebungKern.Bauen). Klasse aus dem Namen von Quelle/Eltern/Grosseltern/Clip:
    //   Laut   (Signale, Abschnitt 16-Stichwoerter)            Faktor 15, hoechstens 300 m
    //   Mittel (Maschinen, Generatoren, Turbinen, Grossfeuer)  Faktor 4,  hoechstens 80 m
    //   Klein  (alles andere: Brummen, Tropfen, Lampen ...)    Faktor 4,  hoechstens 30 m
    //   Fahrzeug (laufende Motoren: V-Ex-Taxi, BTR, Transporter) Faktor 10, hoechstens 200 m
    // Ausgeschlossen: Flaechen-/Teppichklaenge (Meer, Strand, Fluss, Grillen, Wind ...), 2D, Radios, Ereignis-Musik, Zonen-Emitter,
    // Quellen mit steuerndem Skript (Lampen, Lok, Umgebungsmanager ...). Fahrzeuge (V-Ex-Taxi, BTR) laufen ueber den Fahrzeugweg.
    internal static class QuellRegelKern
    {
        public const double KleinGrenze = 30.0;     // m
        public const double MittelGrenze = 80.0;    // m
        public const double MittelFaktor = 4.0;
        public const double LautFaktor = 15.0;      // 40 m x 15 = 600 m >= jede Reichweite-Einstellung: Alarme (40-120 m) genau wie bisher in Abschnitt 16
        public const int FernTakt = 8;              // Okklusions-Aktualisierung ferner Quellen: hoechstens jede 9. Runde

        // Fahrzeuge (0.8.5, Meldung Customs 09.11.2026 00:38, V-Ex-Taxi "nicht weit genug hoerbar"): der Leerlauf des V-Ex-Autos
        // (CarExtraction -> WheelDrive.AudioSource, Clip suv_idle, linear 2 -> 20 m) war ab 20 m stumm. Ein PKW-Motor im Leerlauf hat
        // ~70-80 dB(A) in 1 m, Anlassen ~90-100 dB(A): im Freien mit 1/r (-6 dB je Verdopplung) bei 100 m noch ~35 dB(A), bei 200 m
        // ~29 dB(A) - an der Hoergrenze ruhiger Umgebung. Luft (ISO 9613-1, 500 Hz-1 kHz, 20 C / 70 %) kostet auf 200 m nur 0,5-1 dB.
        // Daher Faktor 10, hoechstens 200 m (V-Ex 20 -> 200 m, BTR-Motor/Streets-Transporter ebenso; nie kuerzer als im Spiel).
        public const double FahrzeugFaktor = 10.0;
        public const double FahrzeugGrenze = 200.0; // m
        // BTR-Motor/Ketten (0.8.5, 18. Scripted sources): Prefab-Reichweite 200 m (10 Instanzen in resources.assets), BTR-80-Diesel
        // ~90 dB(A) in 7,5 m (~107 dB @ 1 m) -> real 500-1000 m. Faktor wie alle Fahrzeuge (10), eigene Grenze 600 m (V-Ex bleibt 200).
        public const double BtrGrenze = 600.0;      // m

        public const string MittelListe = "generator, turbine, engine, motor, pump, compressor, reactor, diesel, locomotive, machine, idle, fire_car, car_fire, house_fire";
        /// <summary>Stichwoerter fuer laufende Fahrzeugmotoren roher Szenenquellen (Clip suv_idle: Streets-Transporter "sound_suv").
        /// Nicht "suv" allein: "sound_suv (1)" auf Streets ist ein PC (Clip icenter_level2_pc_working).</summary>
        public const string FahrzeugListe = "suv_idle, car_idle, truck_idle, engine_idle, vehicle_idle";
        public const string AusListe = "sea, beach, river, surf, wave, cicada, seagull, gull, wind, cricket, forest, rain, thunder, music, christmas, scary, ghost, zombie";

        /// <summary>Skripte, die eine rohe Szenen-AudioSource nicht steuern (auf Quelle oder Eltern erlaubt).</summary>
        public static readonly string[] ErlaubteSkripte =
        {
            "MultisceneSharedOccluder", "ToggleAudioSourceByEvent", "MultiFlareLightHelper", "DisablerCullingObject",
            "GuidComponent", "ConfigurableJointSpawner", "RigidbodySpawner"
        };

        /// <summary>Skripte, die ihre Klaenge selbst fuehren (irgendwo in der Elternkette) - Quelle bleibt wie im Spiel.</summary>
        public static readonly string[] SteuerSkripte =
        {
            "SourceOccluder", "ClientBroadcastPlayer", "ClientSpatialBroadcastPlayer", "LampController", "Locomotive", "CarExtraction",
            "EnvironmentManager", "WindAmbientBlender", "PrecipitationAmbientBlender", "DayTimeAmbientBlender", "AreaLevelAudio",
            "HideoutAreaLevel", "AudioAmbiance", "AmbianceController", "InteractiveSubscriber", "QueuePlayer", "MetaXRAudioSource",
            "SyncLoopSoundPlayer", "AmbientSoundPlayerGroup"
        };

        public static string Name(QuellKlasse k) => k == QuellKlasse.Fahrzeug ? "vehicle" : k == QuellKlasse.Laut ? "loud" : k == QuellKlasse.Mittel ? "medium" : "small";

        /// <summary>Fahrzeug-Skripte, deren Motorquelle ueber den Fahrzeugweg laeuft (an der Quelle bleibt alles wie im Spiel):
        /// CarExtraction (V-Ex-Taxi: WheelDrive.AudioSource fuer Leerlauf + Anlassen, setzt nur die Mixergruppe).</summary>
        public static bool FahrzeugKette(IEnumerable<string> kette)
        {
            foreach (var t in kette) if (t == "CarExtraction") return true;
            return false;
        }

        /// <summary>Signalfackel am Ausgang (0.8.5, Abschnitt 18): rohe Szenenquelle unter MultiFlareLightHelper, deren Name/Eltern/Clip
        /// mit "flare" beginnt ("Emergency_Signal_Flare/Flare_Audio", Clip flare_loop, linear 1 -> 10 m, Vol 0,18). Nicht: Kruzak-V-Ex
        /// "suv_sound" (ebenfalls unter MultiFlareLightHelper), Hideout-Lampen (amb_point_gaslamp).</summary>
        public static bool FackelKette(IEnumerable<string> kette, IList<string> namen)
        {
            bool helfer = false;
            foreach (var t in kette) if (t == "MultiFlareLightHelper") { helfer = true; break; }
            return helfer && LautSignalKern.Treffer(namen, new[] { "flare" }) != null;
        }

        /// <summary>Klasse aus den Namen (Quelle, Eltern, Grosseltern, Clip). false = ausgeschlossen (wort = Ausschluss-Stichwort).
        /// Reihenfolge: Signal-Stichwort, dann Ausschluss, dann Fahrzeug, dann Maschinen-Stichwort, sonst klein.
        /// fahrzeug (optional, nur Weg "raw scene"): Motor-Stichwoerter nach dem Ausschluss, vor den Maschinen.</summary>
        public static bool Einordnen(IList<string> namen, string[] laut, string[] mittel, string[] aus, out QuellKlasse k, out string wort, string[] fahrzeug = null)
        {
            k = QuellKlasse.Klein;
            wort = LautSignalKern.Treffer(namen, laut);
            if (wort != null) { k = QuellKlasse.Laut; return true; }
            wort = LautSignalKern.Treffer(namen, aus);
            if (wort != null) return false;
            if (fahrzeug != null)
            {
                wort = LautSignalKern.Treffer(namen, fahrzeug);
                if (wort != null) { k = QuellKlasse.Fahrzeug; return true; }
            }
            wort = LautSignalKern.Treffer(namen, mittel);
            if (wort != null) k = QuellKlasse.Mittel;
            return true;
        }

        /// <summary>Neue Reichweite: alt x Faktor, hoechstens Grenze, nie kuerzer als die alte.</summary>
        public static double Reichweite(double alt, double faktor, double grenze)
            => Math.Max(alt, Math.Min(alt * Math.Max(1.0, faktor), grenze));

        /// <summary>Lohnt die Umstellung? (neue Reichweite merklich groesser)</summary>
        public static bool Lohnt(double alt, double neu) => neu > alt * 1.0001;

        /// <summary>Allgemeine Bedingungen: 3D, gueltige Reichweite, Pegel an der Quelle.</summary>
        public static bool Kandidat(float raumAnteil, float maxDistanz, float pegelNull)
            => raumAnteil >= 0.5f && maxDistanz > 0f && maxDistanz < 10000f && pegelNull > 0f;

        /// <summary>Darf eine rohe Szenen-AudioSource umgestellt werden? nahe = Skripte auf Quelle + Eltern, kette = alle Vorfahren.</summary>
        public static bool RoheQuelle(IEnumerable<string> nahe, IEnumerable<string> kette, out string grund)
        {
            grund = null;
            foreach (var t in kette)
                if (Array.IndexOf(SteuerSkripte, t) >= 0 || t.EndsWith("AmbientSoundPlayer", StringComparison.Ordinal) || t.StartsWith("Event", StringComparison.Ordinal) && t.EndsWith("Player", StringComparison.Ordinal))
                { grund = "script " + t; return false; }
            foreach (var t in nahe)
                if (Array.IndexOf(ErlaubteSkripte, t) < 0) { grund = "script " + t; return false; }
            return true;
        }

        /// <summary>[Vehicle]-Zeile im Klang-Log faellig? Nur in Hoerweite (bis 1,25 x Reichweite), erste Zeile sofort,
        /// dann bei 2 m Bewegung oder alle 10 s.</summary>
        public static bool FahrzeugZeile(double d, double dLetzt, double sekSeit, double reichweite)
            => d <= reichweite * 1.25 && (dLetzt < 0 || Math.Abs(d - dLetzt) >= 2.0 || sekSeit >= 10.0);

        /// <summary>0.8.6: wie oben, zusaetzlich sofort, wenn sich der Zustand der Quelle geaendert hat (spielt / spielt nicht / aus).</summary>
        public static bool FahrzeugZeile(double d, double dLetzt, double sekSeit, double reichweite, bool zustandNeu)
            => d <= reichweite * 1.25 && (zustandNeu || dLetzt < 0 || Math.Abs(d - dLetzt) >= 2.0 || sekSeit >= 10.0);

        /// <summary>Zustand einer Fahrzeugquelle fuer die [Vehicle]-Zeile (0 spielt, 1 aus: Objekt inaktiv, 2 aus: Quelle abgeschaltet, 3 steht).</summary>
        public static int FahrzeugZustand(bool objektAktiv, bool quelleAn, bool spielt)
            => !objektAktiv ? 1 : !quelleAn ? 2 : spielt ? 0 : 3;

        public static string ZustandText(int z)
            => z == 0 ? "playing" : z == 1 ? "NOT PLAYING - object inactive (V-Ex gone / not present)" : z == 2 ? "NOT PLAYING - source disabled (game culling: beyond range)"
             : "NOT PLAYING - stopped (active, enabled, but no sound)";

        // ---- 0.8.6: stumme Leerlauf-Schleifen (Meldung Ground Zero 09.11.2026 04:04, MARK 2 am V-Ex-Taxi) ----
        // Ursache (EFT 4.1.6, Assembly-CSharp; Szenen level505/509/514; Logs 09.11.), behoben in CullingFix/CullingKern:
        //  1. LocalGame ruft beim Raidstart AudioUtils.ResetAudioBuffer -> AudioSettings.Reset; das haelt die Quellen an,
        //     playOnAwake-Schleifen starten nicht von selbst neu.
        //  2. AudioSourceCulling.Work() beginnt jedes Bild am Listenende (num2 = Count - 1) und bearbeitet hoechstens 10
        //     Eintraege - nur die LETZTEN 10 angemeldeten Quellen werden je geschaltet (CullingKern.VomSpielErreicht).
        //  3. Ground Zero laedt Design_Main (V-Ex-Taxi, 1 Schleife) vor Sandbox_Sound (12 Feuer): das Taxi ist Eintrag 13 von
        //     hinten -> nie geschaltet -> stumm, auch ohne Mod. Customs: Taxi drittletzter -> geschaltet -> hoerbar (Kaans Messung
        //     09.11. 00:38: stumm ab 20,9 m = Abschaltabstand sqrt(20^2 + 25) = 20,6 m).
        // Der fruehere Neustart-Umweg (NeustartTick) ist entfernt; geblieben ist nur die Diagnose, ob eine Schleife trotzdem stumm ist.

        /// <summary>Diagnose: so lange (s) muss eine Schleife in Reichweite stumm sein, bevor sie gemeldet wird (das Culling
        /// erreicht jede Quelle spaetestens alle 0,5 s).</summary>
        public const float StummFrist = 2f;

        /// <summary>Diagnose fuer eine Quelle, deren Objekt aktiv ist und die nicht spielt (prueft der Aufrufer vorher):
        /// Dauerschleife (playOnAwake + loop + Clip) in Reichweite, die kein Skript bewusst abgeschaltet hat
        /// (CullingFix.SkriptAus: ToggleAudioSourceByEvent, Radio stumm, Lok ausgeblendet) -> verbleibender Fehler.</summary>
        public static bool SchleifeStumm(bool playOnAwake, bool loop, bool hatClip, bool vomSkriptAus, double d, double reichweite)
            => playOnAwake && loop && hatClip && !vomSkriptAus && d >= 0 && d <= reichweite;

        /// <summary>Okklusions-Takt: ferne Quellen (jenseits der alten Reichweite) hoechstens jede (fern+1). Runde, sonst wie im Spiel.</summary>
        public static int Takt(int basis, double abstandQ, double altQ, int fern)
            => fern > 0 && abstandQ > altQ ? Math.Max(basis, fern) : basis;

        /// <summary>Grundtakt des Spiels je Prioritaet (SoundOcclusionProcessor.method_5): hoch 0, mittel 1, niedrig 2.</summary>
        public static int Grundtakt(int prio) => prio == 3 ? 0 : prio == 2 ? 1 : 2;

        /// <summary>Art einer Quelle fuer Log/Zusammenfassung: Name ohne " (3)", "_12", Ziffern am Ende.</summary>
        public static string Art(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            string n = name.Trim();
            int k = n.LastIndexOf(" (", StringComparison.Ordinal);
            if (k > 0 && n.EndsWith(")", StringComparison.Ordinal)) n = n.Substring(0, k).TrimEnd();
            int e = n.Length;
            while (e > 0 && char.IsDigit(n[e - 1])) e--;
            if (e > 0 && e < n.Length && (n[e - 1] == '_' || n[e - 1] == ' ')) e--;
            return e > 0 ? n.Substring(0, e) : n;
        }
    }
}
