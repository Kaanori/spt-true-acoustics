using System;
using System.Collections.Generic;
using System.Globalization;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    // 0.8.8 - Richtung ueber die Oeffnung (Ursache, Beleg und Regel: RichtungKern.cs). Spiel-Teil:
    //  - Raumweg.ApplyEnde meldet jede Quelle, auf die gerade ein Raumweg angewendet wurde (Melden): alle Quellen ihres Containers
    //    bekommen einen Eintrag und werden sofort - noch vor dem Audio-Update dieses Bilds - in die Richtung des Austritts gesetzt.
    //  - SpatialAudioSystem.LateUpdate (Postfix, nach allen Okklusionen des Bilds): jede eingetragene Quelle wird nachgefuehrt
    //    (Hoerer bewegt sich, Bot laeuft, Routen blenden) oder zurueckgesetzt, sobald kein Raumweg mehr gilt.
    //  - Die Quelle wird nur ueber ihr eigenes Transform verschoben (Abstand bleibt). Was das Spiel selbst an Position/Eltern
    //    setzt, wird erkannt (Wert weicht vom zuletzt geschriebenen ab) und als neue echte Position uebernommen; beim
    //    Zuruecksetzen wird nur zurueckgeschrieben, wenn das Spiel inzwischen nichts geaendert hat.
    //  - Haengt der Container an der Quelle selbst (Einmal-Klaenge der Welt), gleicht _positionOffset die Verschiebung aus, damit
    //    die Okklusion weiter vom echten Ort rechnet.
    //  - Eigene Rechnungen, die den Ort der Quelle brauchen (Klang-Log, Hoerbarkeit, SEB-Richtwirkung, Wald), lesen Echt().
    internal static class Richtung
    {
        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<bool> KanteAktiv;   // wegrichtung (13.11.): Hindernis-Fall, Richtung der letzten Kante (Wegtabelle)
        internal static ConfigEntry<float> GlaettungMs;
        internal static ConfigEntry<bool> DiffusAusOeffnung;   // tunneldiffus (17.11.): Diffusanteil kommt aus den Oeffnungen (RichtungKern.MischRichtung/RaumGewicht)
        internal static bool DiffusAusOeffnungAn => DiffusAusOeffnung != null && DiffusAusOeffnung.Value;
        /// <summary>tunneldiffus: Teil des Diffusanteils, der trocken an der Position spielt (fuer die Richtung): 1, wenn er trocken hoerbar ist
        /// (diffushoerbar an oder keine Aufteilung in den Hall), sonst 0 (Diffusanteil nur im Hall, Verhalten 16.11.).</summary>
        internal static double DiffusTrockenKappa()
        {
            if (!DiffusAusOeffnungAn) return 0;
            bool alsHall = Physik.DiffusAlsHall != null && Physik.DiffusAlsHall.Value;
            bool hoerbar = Physik.DiffusHoerbar != null && Physik.DiffusHoerbar.Value;
            return !alsHall || hoerbar ? 1.0 : 0.0;
        }
        // wegrichtung2 (17.11.2026, review-20261110/wegrichtung2-STAND.md)
        internal static ConfigEntry<bool> Fuehrend;   // Richtung vom fuehrenden Weg (RichtungKern.VorrangSeite) + Wandanteil ungeglaettet (AnteilWand)
        internal static ConfigEntry<bool> PraezedenzAlle;   // raid2242richtung: erste Wellenfront aus allen Routen (nicht nur Rang 1-3)
        internal static ConfigEntry<bool> EinmalAktiv; // Einmal-Klaenge (MultiSource: Einschlaege, Huelsen, Tueren, Fenster, ferne Explosionen)
        internal static bool FuehrendAn => Fuehrend != null && Fuehrend.Value;
        internal static bool EinmalAn => EinmalAktiv != null && EinmalAktiv.Value;
        internal static ConfigEntry<bool> ZweitAktiv;  // zweitweg (17.11.): zweiter Weg als eigene Stimme (Zweitstimme.cs, zuerst nur die Testquelle)
        internal static ConfigEntry<int> StimmenJeKlang, VorratGroesse;   // mehrweg (17.11.): bis zu K Stimmen je Klang, eigener Vorrat
        internal static ConfigEntry<float> StartUnsicher;                 // mehrwegc: Startunsicherheit (ms) der Spielklaenge, 0 = ein DSP-Puffer
        internal static ConfigEntry<bool> MehrSchritte, MehrStimmen, MehrEinmal;   // mehrwegc: Unterschalter je Klangklasse
        /// <summary>mehrwegc: Klasse an (Zweitstimme.KSchritt/KStimme/KEinmal; Testquelle immer).</summary>
        internal static bool KlasseAn(int k)
            => k == Zweitstimme.KSchritt ? MehrSchritte == null || MehrSchritte.Value
             : k == Zweitstimme.KStimme ? MehrStimmen == null || MehrStimmen.Value
             : k == Zweitstimme.KEinmal ? MehrEinmal == null || MehrEinmal.Value : true;
        internal static bool ZweitAn => An && ZweitAktiv != null && ZweitAktiv.Value;
        const int MaxEintraege = 512;

        sealed class Eintrag
        {
            public BetterSource S;
            public Transform T, Eltern;
            public Vector3 BasisLokal, Geschrieben;
            public bool Hat;                       // Position ist gerade von uns gesetzt
            public int Schieben;                   // 0 = ungeprueft, 1 = reines Audio-Objekt (verschiebbar), -1 = traegt Sichtbares
            public Transform SchiebEltern;
            public AudioSourceContainer C;
            public bool Selbst;                    // Container haengt an dieser Quelle
            public Vector3 BasisOffset, GeschriebenOffset;
            public bool HatOffset;
            public SpatialAudioOccluder Occ;
            public ParallelSoundPropagationCalculator Pc;
            public bool EigenerOcc;                // Occluder gehoert nur zu diesem Container (nicht MultiSource)
            public float GesamtDb = float.NaN;     // Ergebnis nach Wand + Raumweg (dB), MultiSource: beim Anwenden gemerkt
            public V3 Dir;                         // nachgefuehrte Richtung vom Hoerer (Einheitsvektor)
            public double Gewicht, Anteil, WinkelZiel, WinkelJetzt;
            public bool Ersatz;
            public int Bild;
            public double Zeit = double.NaN;       // letzte Nachfuehrung (realtimeSinceStartup)
            public double Buendel = 1;
            // wegrichtung (13.11.): 0 = Raumweg ("from the opening"), 1 = Hindernis-Weg aus der Wegtabelle ("from the edge")
            public int Art;
            public int Paare;                      // Sondenpaare im Vektormittel (0 = gespeicherter Kantenpunkt der letzten Abfrage)
            public double DtMs;                    // Laufzeitunterschied gebeugter Weg - Direktlinie (ms), Praezedenz
            public bool Gesperrt;                  // Ziel gerechnet, aber das Objekt traegt Sichtbares (Verschiebbar = false)
            public bool Getragen;                  // lampenwand: statt des Objekts spielt der Traeger (Traeger.cs) an der Zielposition
            public string SchiebGrund;             // lampentraeger: warum nicht verschiebbar (Logtext; Wurzel ist nicht "sichtbare Teile")
            // wegrichtung2: Einmal-Klang (MultiSource) - das Spiel wendet ihn einmal an und gibt danach den Rechner zurueck; Austritt und
            // Anteil der Anwendung gelten fuer den Rest des Klangs (Ort der Quelle fest, Ohr bewegt sich - die Richtung folgt dem Ohr).
            public bool Einmal, HatHalt, Gehalten;
            public V3 HaltAus;
            public double HaltAnteil;
        }

        // 0.8.8: letzte Richtung je Container (Figur), damit neue Einmal-Klaenge derselben Figur nicht springen
        sealed class Spur { public V3 Dir; public double Zeit; }
        const double SpurS = 1.0;
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AudioSourceContainer, Spur> Spuren =
            new System.Runtime.CompilerServices.ConditionalWeakTable<AudioSourceContainer, Spur>();
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AudioSourceContainer, Spur>.CreateValueCallback NeueSpur = _ => new Spur();
        static int dWeiter; static double dSummeBuendel; static int dBuendelN;

        static readonly Dictionary<BetterSource, Eintrag> Tabelle = new Dictionary<BetterSource, Eintrag>();
        static readonly List<Eintrag> Liste = new List<Eintrag>();
        static int letztesBild = -1;

        // Diagnose (30 s)
        static int dGesetzt, dZurueck, dUebernommen, dVoll;
        static double dSummeWinkel; static int dWinkelN;
        static double dMaxWinkel;

        internal static bool An => Raumweg.An && Aktiv != null && Aktiv.Value;
        /// <summary>wegrichtung: Richtung der letzten Kante im Hindernis-Fall (Wegtabelle) an.</summary>
        internal static bool KanteAn => Wege.An && KanteAktiv != null && KanteAktiv.Value;
        static bool IrgendAn => An || KanteAn;
        /// <summary>wegrichtung: hoechste Drehgeschwindigkeit der Kantenrichtung (Grad/s). Annahme statt Messung: die Tabelle springt beim
        /// Zellwechsel bis 112 Grad in einem Bild (Messplatz wegtabelle/mess, Modus richtung); 180 Grad/s = 3 Grad je Bild bei 60 Bildern/s,
        /// mittlerer Nachlauf gemessen 1,3-2,4 Grad (ohne Grenze 1,3-2,4, groesster Schritt 14,6 statt 3,0 Grad).</summary>
        const double KanteMaxGradS = 180.0;
        static readonly Schallweg.TabErgebnis kErg = new Schallweg.TabErgebnis();
        static readonly V3[] kEinzel = new V3[1]; static readonly double[] kEinzelE = { 1.0 };
        internal static void Binden(ConfigFile c)
        {
            const string s = "0. Physics Mode (real values)";
            Aktiv = c.Bind(s, "Room paths: direction from the opening", true,
                "Sound that reaches you through doors/windows (room path) is heard from the direction of the opening it comes out of, not from the " +
                "source behind the wall. Distance and loudness stay the same; only the direction moves, weighted by how much of the sound takes the openings " +
                "(a thin wall that lets most of it through keeps the direction at the source). Off = the game: always from the source.");
            GlaettungMs = c.Bind(s, "Room paths: direction smoothing (ms)", 120f,
                new ConfigDescription("Time constant with which the direction follows changes (you walk, the source walks, routes blend). A new sound starts at once in the right direction. 0 = no smoothing.",
                    new AcceptableValueRange<float>(0f, 1000f)));
            Aktiv.SettingChanged += (_, __) => { if (!Aktiv.Value) AllesZurueck(); };
            DiffusAusOeffnung = c.Bind(s, "Room paths: diffuse part from the openings", true,
                "On (default since 0.8.8): when the diffuse part of a room path is played dry ('Diffuse sound stays audible'), its direction is " +
                "the opening it comes in through - weighted with its full energy - and the direction is turned fully to the openings, also when they lie " +
                "on different sides (then the energy-weighted middle). Before, only the small directed rest set the direction and the sound stayed partly " +
                "at the source, i.e. it came through the ceiling or wall. Off = behaviour up to 0.8.5. Applies to new calculations.");
            // wegrichtung (13.11.): Hindernis-Fall
            KanteAktiv = c.Bind(s, "Obstacle paths: direction from the edge", true,
                "Same room, obstacles in between and the game finds no way around them: the level then comes from the precomputed path table (section 19). " +
                "On = you also hear the sound from the direction of the edges it bends around (average over the table's nearby paths, as seen from where you " +
                "stand), not through the obstacle from the source. Distance and loudness stay the same; uses the same direction smoothing. Only sound objects " +
                "without visible parts are moved. Off = from the source. Applies live.");
            KanteAktiv.SettingChanged += (_, __) => { if (!KanteAktiv.Value) AllesZurueck(); };
            // wegrichtung2 (17.11.)
            Fuehrend = c.Bind(s, "Room paths: direction from the leading path", true,
                "On (default since 0.8.8): when a sound comes in through openings on different sides, you hear it from the side of the path that " +
                "arrives first (or is clearly louder) - your ear locates by the first sound front. Paths on the same side are averaged; paths more " +
                "than 60-90 degrees away from the leading one no longer pull the direction. Also: how much comes through the wall instead of the " +
                "openings is taken from the current values, not the game's smoothed one. Off = energy-weighted middle of all openings " +
                "which can point at the wall between two doors. Applies to new calculations.");
            // raid2242richtung (17.11.): Praezedenz mit der vollen Energie jeder Route (RichtungKern.PraezedenzOhneRang)
            PraezedenzAlle = c.Bind(s, "Room paths: first sound front from all paths", true,
                "On (default since 0.8.8): which path arrives first is decided with the full energy of every path the game delivers. " +
                "Before, only the three loudest paths counted fully (a smoothing choice for the volume sum), so an early, somewhat quieter " +
                "path through a nearby opening could lose against a louder path that arrives 40-50 ms later from behind. Volume is unchanged. " +
                "Off = behaviour up to 0.8.5. Applies live.");
            RichtungKern.PraezedenzOhneRang = PraezedenzAlle.Value;
            PraezedenzAlle.SettingChanged += (_, __) => RichtungKern.PraezedenzOhneRang = PraezedenzAlle.Value;
            EinmalAktiv = c.Bind(s, "Room paths: direction for one-shot sounds", true,
                "On (default since 0.8.8): bullet impacts, shell casings, doors, windows and distant explosions in other rooms are also heard from " +
                "the opening they come through, like footsteps and voices. Before, these one-shot sounds always came straight from their source " +
                "through the wall (the mod looked up the wrong sound). Off = from the source. Applies to new sounds.");
            EinmalAktiv.SettingChanged += (_, __) => { if (!EinmalAktiv.Value) AllesZurueck(); };
            // zweitweg (17.11.)
            ZweitAktiv = c.Bind(s, "Room paths: second path as its own voice", true,
                "On (default since 0.8.8): when a sound reaches you through openings in clearly different directions, each " +
                "direction is played as its own copy of the sound: from its opening, with the energy that really comes that way (all copies together " +
                "are exactly as loud as before), its own timbre and later by its extra travel time - your ear then hears all ways as in reality. Paths " +
                "within ~10-20 degrees share one copy; a copy is left out only when it is surely inaudible (more than 30 dB below the rest) or no free " +
                "voice is left - its energy then goes to the nearest direction. Used for enemy footsteps/gear, voices and one-shot sounds (sub-switches " +
                "below) and the test source; never for your own sounds; not for gunshots. Needs 'Room paths: direction from the opening'. Off = one " +
                "copy from the leading path. Applies to the next sound.");
            ZweitAktiv.SettingChanged += (_, __) => { if (!ZweitAktiv.Value) Zweitstimme.AllesFrei(); };
            StimmenJeKlang = c.Bind(s, "Room paths: voices per sound", 3,
                new ConfigDescription("At most this many copies (voices) per sound, the first one is the sound itself. 1 = like the switch off. " +
                    "Default 3: in test logs a 4th direction above the audible limit occurs in 12 % of the room-path lines, at a median of " +
                    "-16 dB of the sum; with 3 its energy goes to the nearest direction. Applies to the next sound.", new AcceptableValueRange<int>(1, 4)));
            VorratGroesse = c.Bind(s, "Room paths: voice pool size", 16,
                new ConfigDescription("How many extra playback sources the mod borrows from the game's pool for the extra voices (once per raid, " +
                    "reused). When all are busy, a louder new copy takes the voice of the quietest one, which fades out. 0 = no extra voices. Applies live.",
                    new AcceptableValueRange<int>(0, 64)));
            // mehrwegc (17.11. abends): echte Gegner
            StartUnsicher = c.Bind(s, "Room paths: start uncertainty (ms)", 0f,
                new ConfigDescription("How exactly the mod knows when a game sound really started (an enemy step plays 'as soon as possible', at " +
                    "the next audio block). An extra voice is only played when it comes at least this much after the sound itself - otherwise it could " +
                    "come BEFORE it and you would hear the wrong opening first. 0 = automatic = one audio buffer (read from the game, usually 1024 " +
                    "samples = 21.3 ms). A later measurement can lower it without a new build. Applies to the next sound.", new AcceptableValueRange<float>(0f, 100f)));
            MehrSchritte = c.Bind(s, "Room paths: extra voices for footsteps and gear", true,
                "Enemy footsteps and gear sounds in other rooms get the extra voices (each step its own). Applies to the next sound.");
            MehrStimmen = c.Bind(s, "Room paths: extra voices for voices", true,
                "Enemy voices (speech) in other rooms get the extra voices. Applies to the next sound.");
            MehrEinmal = c.Bind(s, "Room paths: extra voices for one-shot sounds", true,
                "Bullet impacts, shell casings, doors, windows, grenades and other one-shot sounds of the world in other rooms get the extra " +
                "voices (needs 'Room paths: direction for one-shot sounds'). Applies to the next sound.");
            Nachhall.Binden(c, s);   // nachhallstimme (17.11.): Diffusanteil als echter Nachhall (Nachhall.cs)
            Traeger.Binden(c, s);    // lampenwand (17.11.): Klangtraeger fuer Lampen mit sichtbaren Teilen (Traeger.cs)
        }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var lu = AccessTools.DeclaredMethod(typeof(SpatialAudioSystem), "LateUpdate");
                if (lu == null) { Plugin.Log.LogWarning("Direction from the opening: SpatialAudioSystem.LateUpdate not found -> sounds stay at their source."); return; }
                h.Patch(lu, postfix: new HarmonyMethod(typeof(Richtung), nameof(SystemSpaetNach)));
                Zweitstimme.Anwenden(h);   // mehrwegc: Ereignisse (SimpleSource.Play) und Teilen nach dem Anwenden
                // 10.11. (R1a): Ende des Besitzes - der Eintrag gehoert zum Klang, nicht zum Pool-Objekt
                var rel = AccessTools.DeclaredMethod(typeof(BetterSource), "Release", Type.EmptyTypes);
                if (rel != null) h.Patch(rel, prefix: new HarmonyMethod(typeof(Richtung), nameof(FreigabeVor)) { priority = Priority.First });
                else Plugin.Log.LogWarning("Direction from the opening: BetterSource.Release not found -> a reused pool source may report its previous owner's position for one frame.");
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        internal static void SzeneGewechselt() { AllesZurueck(); }

        /// <summary>Echte Position einer Quelle (ohne unsere Verschiebung). Die gemerkte Basis gilt nur, solange die Verschiebung
        /// nachweislich noch von uns stammt (10.11., R1a: dieselbe Pruefung wie Uebernehmen - hat das Spiel die Quelle inzwischen
        /// selbst gesetzt, z. B. ein neuer Besitzer aus dem Pool, ist ihre Position die echte).</summary>
        internal static Vector3 Echt(BetterSource s)
        {
            if (s == null) return Vector3.zero;
            if (Tabelle.Count > 0 && Tabelle.TryGetValue(s, out var e) && e.T != null
                && WiederverwendungKern.BasisGilt(e.Hat, e.T.parent == e.Eltern, (e.T.localPosition - e.Geschrieben).sqrMagnitude))
                return e.Eltern != null ? e.Eltern.TransformPoint(e.BasisLokal) : e.BasisLokal;
            return s.Position;
        }

        /// <summary>10.11. (R1a): Prefix BetterSource.Release - die Quelle geht an den Pool zurueck. Eintrag sofort vergessen
        /// (unsere Verschiebung zuruecknehmen, solange das Spiel nichts geaendert hat), damit ein neuer Besitzer, der sie noch im
        /// selben Update holt, nichts vom alten erbt. Haengt der Container an der Quelle selbst, gehoert auch seine Spur (letzte
        /// Richtung der "Figur") dem alten Klang.</summary>
        static void FreigabeVor(BetterSource __instance)
        {
            // hallsend (12.11., Spielcode-Fund 1): der angehobene Reverb-Send gehoert dem Klang, nicht dem Pool-Objekt - zuerst zuruecksetzen
            // (Priority.First: vor GAL und den anderen Release-Prefixen; keiner davon ueberspringt das Original). Eigenes try in SendFreigeben.
            Physik.SendFreigeben(__instance);
            Zweitstimme.Freigabe(__instance);   // mehrwegc: weitere Stimmen gehoeren dem Klang (eigenes try)
            if (Tabelle.Count == 0 || __instance == null) return;
            try
            {
                if (!Tabelle.TryGetValue(__instance, out var e)) return;
                Zurueck(e);
                Traeger.Frei(__instance);   // lampenwand
                if (e.Selbst && e.C != null) Spuren.Remove(e.C);
                Tabelle.Remove(__instance);
                int i = Liste.IndexOf(e);
                if (i >= 0) { int last = Liste.Count - 1; Liste[i] = Liste[last]; Liste.RemoveAt(last); }
                dFreigabe++;
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }
        static int dFreigabe;

        // ---------------------------------------------------------------- Melden (aus Raumweg.ApplyEnde)

        internal static void Melden(SpatialAudioOccluder occ, BetterSource source)
        {
            if (!IrgendAn || occ == null || source == null) return;
            try
            {
                bool eigen = !(occ is MultiSourceSpatialAudioOccluder);
                // wegrichtung2: beim MultiSource-Occluder ist SourceContainer der zuletzt angemeldete Einmal-Klang, nicht der angewandte
                var c = !eigen && EinmalAn ? Raumweg.MultiBehaelter : occ.SourceContainer;
                if (c == null) return;
                var pc = Raumweg.RechnerVon(occ);
                // Raumweg (Oeffnung) oder - wegrichtung - Hindernis-Weg aus der Wegtabelle (Kante; nur eigener Combined-Occluder)
                bool raum = An && pc != null && Raumweg.RichtungsDaten(pc, c, out _, out _, out _);
                if (!raum && !(KanteAn && eigen && Wege.KantenZustand(occ, out _))) return;
                float gesamt = Physik.Dynamik.Value * Mathf.Clamp01(occ.PropagationEffect);
                for (int i = 0; i < c.Count; i++)
                {
                    var s = c[i];
                    if (s == null) continue;
                    if (!Tabelle.TryGetValue(s, out var e))
                    {
                        if (Tabelle.Count >= MaxEintraege) return;
                        e = new Eintrag { S = s, T = s.transform };
                        e.Eltern = e.T.parent; e.BasisLokal = e.T.localPosition;
                        Tabelle[s] = e; Liste.Add(e);
                    }
                    if (!ReferenceEquals(e.C, c))
                    {
                        if (e.HatOffset) OffsetZurueck(e);
                        e.C = c;
                        var pt = c._parentTransform;
                        e.Selbst = pt != null && (pt == e.T || pt.IsChildOf(e.T));
                        e.HatOffset = false;
                    }
                    e.Occ = occ; e.Pc = pc; e.EigenerOcc = eigen; e.GesamtDb = gesamt;
                    if (e.Einmal != !eigen) { e.Einmal = !eigen; e.HatHalt = false; }
                    Setzen(e, true);
                }
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        // ---------------------------------------------------------------- jedes Bild

        static void SystemSpaetNach()
        {
            if (Liste.Count == 0) return;
            int bild = Time.frameCount;
            if (bild == letztesBild) return;
            letztesBild = bild;
            if (!IrgendAn) { AllesZurueck(); return; }
            try
            {
                for (int i = Liste.Count - 1; i >= 0; i--)
                {
                    var e = Liste[i];
                    if (e.Bild == bild) continue;      // gerade beim Anwenden gesetzt
                    if (!Setzen(e, false)) Entfernen(i);
                }
                Traeger.Bild();   // lampenwand: Traeger auf den Stand ihrer Originale (nach allen Okklusionen des Bilds)
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        /// <summary>Ziel rechnen und schreiben. false = kein Raumweg mehr (Eintrag zuruecksetzen und entfernen).</summary>
        static bool Setzen(Eintrag e, bool neu)
        {
            var s = e.S;
            if (s == null || e.T == null) return false;
            Uebernehmen(e);
            if (s.PlayBackState == BetterSource.EPlayBackState.Released || e.C == null) { Zurueck(e); return false; }
            if (e.EigenerOcc && (e.Occ == null || !ReferenceEquals(e.Occ.SourceContainer, e.C))) { Zurueck(e); return false; }
            var lt = e.Occ != null ? e.Occ.ListenerTransform : null;
            if (lt == null) { Zurueck(e); return false; }
            Vector3 hv = lt.position;
            Vector3 qv = EchtVon(e);
            V3 h = V(hv), q = V(qv);
            V3 dir; double w, anteil, buendel; bool ersatz = false;
            if (An && e.Pc != null && Raumweg.RichtungsDaten(e.Pc, e.C, out V3 aus, out float routeDb, out ersatz, out buendel))
            {
                // mehrweg (17.11.): spielen weitere Stimmen, zeigt die erste auf ihr eigenes Buendel (sonst: Seite des fuehrenden Wegs = Rueckfall)
                if (ZweitAn && Zweitstimme.Austritt1(s, out V3 aus1)) aus = aus1;
                if (e.Art != 0) e.Dir = default;   // Wechsel Kante -> Oeffnung: neue Art, neuer Anfang (die Spur der Figur gilt weiter)
                e.Art = 0;
                if (e.EigenerOcc) e.GesamtDb = Physik.Dynamik.Value * Mathf.Clamp01(e.Occ.PropagationEffect);
                // wegrichtung2: Wandanteil aus den ungeglaetteten Werten (Route gegen Wand zwischen den Raeumen), nicht gegen die geglaettete Wirkung
                anteil = FuehrendAn ? RichtungKern.AnteilWand(routeDb, WandDb(e.Pc)) : RichtungKern.Anteil(routeDb, e.GesamtDb);
                // 0.8.8: Gewicht = Energieanteil der Oeffnungen x Buendelung ihrer Richtungen (kommen sie gleich stark von entgegengesetzten
                // Seiten, gibt es keine Richtung "der Oeffnung" -> bleibt bei der Quelle)
                // tunneldiffus (17.11.): Gewicht ohne Buendel, wenn der Diffusanteil aus den Oeffnungen kommt (RichtungKern.RaumGewicht)
                // wegrichtung2: ebenso mit dem fuehrenden Weg - die Richtung ist dann die seine, das Buendel nur noch die Breite (Log)
                double rg = RichtungKern.RaumGewicht(anteil, buendel, DiffusTrockenKappa() > 0 || FuehrendAn);
                if (e.Einmal) { e.HaltAus = aus; e.HaltAnteil = rg; e.HatHalt = true; }
                e.Gehalten = false;
                if (!RichtungKern.Ziel(h, q, aus, rg, out dir, out w)) { Zurueck(e); e.Dir = default; return true; }
            }
            else if (e.Einmal && e.HatHalt && EinmalAn && An)
            {
                // wegrichtung2: Einmal-Klang nach seiner einzigen Anwendung - Austritt und Gewicht von dort, Richtung vom heutigen Ohr
                if (e.Art != 0) e.Dir = default;
                e.Art = 0; e.Gehalten = true;
                anteil = e.HaltAnteil; buendel = e.Buendel; ersatz = false;
                if (!RichtungKern.Ziel(h, q, e.HaltAus, e.HaltAnteil, out dir, out w)) { Zurueck(e); e.Dir = default; return true; }
            }
            else if (KanteAn && e.EigenerOcc && Wege.KantenZustand(e.Occ, out var z))
            {
                if (e.Art != 1) e.Dir = default;
                e.Art = 1;
                if (!KantenZiel(e, z, h, q, out dir, out w, out anteil, out buendel)) { Zurueck(e); e.Dir = default; return true; }
            }
            else { Zurueck(e); return false; }
            e.Buendel = buendel;
            double jetzt = Time.realtimeSinceStartup;
            double dt = double.IsNaN(e.Zeit) ? 0 : Math.Max(0, jetzt - e.Zeit);
            e.Zeit = jetzt;
            // neuer Klang (noch nie gesetzt): sofort in der Zielrichtung; danach mit der Zeitkonstante nachfuehren.
            // 0.8.8: hatte derselbe Container (Figur) eben schon eine Richtung (vorige Schritte, Ausruestung, Stimme), geht es von
            // dort aus weiter - sonst springt jeder neue Einmal-Klang (Log 09.11.: 7 % der Folgemeldungen derselben Quelle > 45 Grad).
            // wegrichtung: die Kantenrichtung zusaetzlich hoechstens KanteMaxGradS (Zellwechsel der Tabelle springen sonst durch).
            double tau = GlaettungMs != null ? GlaettungMs.Value / 1000.0 : 0;
            double grenze = e.Art == 1 ? KanteMaxGradS : 0;
            bool erst = e.Dir.IstNull;
            if (erst && e.C != null && Spuren.TryGetValue(e.C, out var sp) && jetzt - sp.Zeit < SpurS && !sp.Dir.IstNull)
            {
                e.Dir = RichtungKern.NachfuehrenBegrenzt(sp.Dir, dir, Math.Max(0, jetzt - sp.Zeit), tau, grenze);
                dWeiter++;
            }
            else e.Dir = !erst ? RichtungKern.NachfuehrenBegrenzt(e.Dir, dir, dt, tau, grenze) : dir;
            if (e.C != null) { var spur = Spuren.GetValue(e.C, NeueSpur); spur.Dir = e.Dir; spur.Zeit = jetzt; }
            double abstand = (q - h).Laenge;
            V3 u = (q - h) * (1.0 / Math.Max(abstand, 1e-6));
            e.Gewicht = w; e.Anteil = anteil; e.Ersatz = ersatz;
            e.WinkelZiel = RichtungKern.Winkel(u, dir);
            e.WinkelJetzt = RichtungKern.Winkel(u, e.Dir);
            e.Gesperrt = false;
            if (e.WinkelJetzt < 0.2 && w < 0.01) { Zurueck(e); return true; }   // nichts zu drehen (Wand dominiert)
            // 10.11.2026 (Kaan: "manche Lampen wandern quer durch den Raum"): das Objekt der Quelle darf nur verschoben werden, wenn
            // an ihm nichts Sichtbares/Physikalisches haengt - sonst wandert das Lampenmodell mit dem Klang (Verschiebbar).
            // Abstand bleibt der echte (auch fuer die Kante): die Wegpegel enthalten die Kugelwelle ueber die ganze Weglaenge relativ zur
            // Direktlinie (WegeKern.Beugung: - 20 lg((s1 + s2) / d)); in Weglaenge-Abstand rechnete die Abstandskurve von Unity/SEB sie noch einmal.
            V3 ziel = RichtungKern.Position(h, e.Dir, abstand);
            Vector3 zv = new Vector3((float)ziel.X, (float)ziel.Y, (float)ziel.Z);
            // lampenwand (17.11.): traegt das Objekt Sichtbares, spielt ein unsichtbarer Traeger die Schleife an der Zielposition
            // (Traeger.cs/TraegerKern.cs); das Objekt selbst bleibt stehen. Ohne Traeger (One-Shot, Spatializer, Schalter aus): wie bisher.
            if (!Verschiebbar(e))
            {
                e.Getragen = Traeger.Setzen(s, zv);
                if (e.Getragen) { dGetragen++; e.Bild = Time.frameCount; return true; }
                dNichtVerschiebbar++; e.Gesperrt = true; if (e.Art == 1) dKanteGesperrt++; return true;
            }
            e.T.position = zv;
            e.Geschrieben = e.T.localPosition;
            if (erst) dGesetzt++;
            e.Hat = true;
            if (e.Selbst)
            {
                if (!e.HatOffset) { e.BasisOffset = e.C._positionOffset; e.HatOffset = true; }
                e.C._positionOffset = e.BasisOffset - (zv - qv);
                e.GeschriebenOffset = e.C._positionOffset;
            }
            e.Bild = Time.frameCount;
            dWinkelN++; dSummeWinkel += e.WinkelJetzt; if (e.WinkelJetzt > dMaxWinkel) dMaxWinkel = e.WinkelJetzt;
            dSummeBuendel += buendel; dBuendelN++;
            if (w > 0.99) dVoll++;
            if (e.Art == 1) { dKanteN++; dKanteWinkel += e.WinkelJetzt; dKanteFokus += buendel; }
            return true;
        }

        /// <summary>zweitweg: gemischte Routen (nur lesen) und Hoerer im Job-Bezug fuer eine Quelle, deren Raumweg hier gerade gilt.</summary>
        internal static bool Routen(BetterSource s, out List<RoutenEintrag> mix, out V3 hJob)
        {
            mix = null; hJob = default;
            if (s == null || !Tabelle.TryGetValue(s, out var e) || e.Pc == null || e.C == null || e.Art != 0) return false;
            return Raumweg.Mischung(e.Pc, e.C, out mix, out hJob);
        }

        /// <summary>mehrweg: Energieanteil des Raumwegs (gegen die Wand zwischen den Raeumen) der letzten Nachfuehrung dieser Quelle.</summary>
        internal static bool RaumAnteil(BetterSource s, out double anteil, out bool einmal)
        {
            anteil = 1; einmal = false;
            if (s == null || !Tabelle.TryGetValue(s, out var e) || e.Art != 0 || double.IsNaN(e.Anteil)) return false;
            einmal = e.Einmal;
            anteil = Math.Max(0, Math.Min(1, e.Anteil));
            return true;
        }

        /// <summary>wegrichtung2: ungeglaettete Daemmung der Wand zwischen den Raeumen (dB, Bezugsband, wie ScorePostfix), NaN = keine Messung.</summary>
        static double WandDb(ParallelSoundPropagationCalculator pc)
        {
            if (pc == null || !WandZwischenRaeumen.WandFuer(pc, out float score, out _, out _, out _) || score < 0f) return double.NaN;
            return score * Physik.Dynamik.Value;
        }

        /// <summary>wegrichtung (13.11.): Zielrichtung im Hindernis-Fall (Regel und Messung: RichtungKern, review-20261110/wegrichtung-STAND.md).
        /// Die Tabelle wird je Bild am ECHTEN Ort von Quelle und Ohr nachgeschlagen (die Rechnung des Spiels und ihr Tabellenwert koennen
        /// Sekunden alt sein); ohne Wert dort gilt der Kantenpunkt der letzten Abfrage (z.SKante). Richtung = Vektormittel der 2 x 2 Paare vom
        /// Ohr aus, Gewicht = Anteil (gebeugter Weg gegen T/R/D, mit Praezedenz) x Fokus. Keine Speicheranforderung.</summary>
        static bool KantenZiel(Eintrag e, WegZustand z, V3 h, V3 q, out V3 dir, out double w, out double anteil, out double fokus)
        {
            dir = default; w = 0; anteil = 0; fokus = 0;
            double d = (q - h).Laenge;
            // wegkarten (16.11.): unter dem Rastermass der Tabelle (3 m) und fuer abgestellte Quellen keine Kantenrichtung - auch nicht der
            // zuletzt gemerkte Kantenpunkt (z.SKante), der dann zu einem anderen Ort gehoert (Raid 13.11.: 132-136 Grad bei 1,3 m Abstand)
            if (d < Math.Max(RichtungKern.MinAbstand, Schallweg.WegTabelle.MinAbstand) || Schallweg.WegTabelle.Geparkt(q)) return false;
            V3 u = (q - h) * (1.0 / d);
            V3 mittel; double laenge;
            if (WegTabelleSpiel.Kanten(q, h, kErg) && kErg.PaarN > 0
                && RichtungKern.KantenMittel(h, kErg.PaarKante, kErg.PaarE, kErg.PaarN, out mittel, out fokus))
            { e.Paare = kErg.PaarN; laenge = kErg.Laenge; }
            else if (z.SHatKante)
            {
                kEinzel[0] = z.SKante;
                if (!RichtungKern.KantenMittel(h, kEinzel, kEinzelE, 1, out mittel, out fokus)) return false;
                e.Paare = 0; laenge = z.SLaenge;
            }
            else return false;
            // Energie 1 kHz (Bezugsband der Tabellenrichtung, Annahme: Ortung breitbandiger Klaenge stuetzt sich vor allem auf die mittleren
            // und hohen Baender). Gebeugter Weg S gegen die Wege aus Richtung der Quelle: Durchgang T (nur Wand), Reflexion R des Spiels
            // (Richtung unbekannt -> Quelle) und gehaltene Kante D, soweit sie ueber S liegt (SummeGetrennt nimmt max(S, D)).
            const int b = 1;
            double eS = WegeKern.Energie(z.S[b]);
            double eD = z.HatD ? (z.DDirekt ? 1.0 : WegeKern.Energie(z.D[b])) : 0;
            double rest = (z.HatT ? WegeKern.Energie(z.TWand[b]) : 0) + (z.HatR && !z.RDirekt ? WegeKern.Energie(z.R[b]) : 0) + Math.Max(0, eD - eS);
            e.DtMs = Math.Max(0, laenge - d) / RaumwegKern.Schall * 1000.0;
            anteil = RichtungKern.KantenAnteil(eS, rest, e.DtMs);
            w = anteil * fokus;
            dir = RichtungKern.KantenZiel(u, mittel, w);
            return true;
        }

        /// <summary>Darf das Objekt der Quelle fuer die Richtung verschoben werden? Nur reine Audio-Objekte (dieselbe Regel wie
        /// WandDaempfung.Verschiebbar): im Teilbaum ausser Transform, Audio-Komponenten und Skripten nichts - kein Renderer, Licht,
        /// Collider, Partikel, Rigidbody ... Sonst bewegt sich Sichtbares mit (Leuchtstofflampen: BetterSource sitzt am Lampen-FX).
        /// Einmal je Eintrag geprueft; ein neuer Besitzer/Elternwechsel legt einen neuen Eintrag an bzw. prueft neu (Schieben = 0).</summary>
        static bool Verschiebbar(Eintrag e)
        {
            var t = e.T;
            if (t == null) return false;
            if (e.Schieben != 0 && ReferenceEquals(e.SchiebEltern, t.parent)) return e.Schieben > 0;
            e.SchiebEltern = t.parent; e.Schieben = -1;
            // lampentraeger (17.11.): Grund merken - Log 22:08 meldete Pool-Quellen der Bots an der Szenenwurzel als "visible parts"
            if (t.parent == null) { e.SchiebGrund = "sound object is a scene root"; return false; }   // Szenenwurzel
            e.SchiebGrund = "sound object carries visible parts";
            schiebPuffer.Clear();
            try
            {
                t.GetComponentsInChildren(true, schiebPuffer);
                if (schiebPuffer.Count > 64) { e.SchiebGrund = "sound object carries more than 64 components"; return false; }
                foreach (var c in schiebPuffer)
                {
                    if (c == null || c is Transform || c is MonoBehaviour || c is AudioSource) continue;
                    var ty = c.GetType();
                    if (ty.Namespace == "UnityEngine" && ty.Name.StartsWith("Audio", StringComparison.Ordinal)) continue;   // Audio-Filter
                    if (dNichtVerschiebbarArten < 12) { dNichtVerschiebbarArten++; Plugin.Log.LogInfo("Direction from the opening: '" + t.name + "' stays at its place (" + ty.Name + " on '" + c.gameObject.name + "' would move with it)."); }
                    return false;
                }
            }
            finally { schiebPuffer.Clear(); }
            e.Schieben = 1; e.SchiebGrund = null;
            return true;
        }
        static readonly System.Collections.Generic.List<Component> schiebPuffer = new System.Collections.Generic.List<Component>(16);
        static int dNichtVerschiebbar, dNichtVerschiebbarArten, dGetragen;
        static int dKanteN, dKanteGesperrt; static double dKanteWinkel, dKanteFokus;   // wegrichtung: Bilder mit Kantenrichtung (Diagnose)

        /// <summary>Hat das Spiel die Quelle inzwischen selbst bewegt oder umgehaengt? Dann gilt das als neue echte Position.</summary>
        static void Uebernehmen(Eintrag e)
        {
            var t = e.T;
            if (t.parent != e.Eltern)
            {
                e.Eltern = t.parent; e.BasisLokal = t.localPosition; e.Hat = false; e.Dir = default; dUebernommen++;
            }
            else if (e.Hat && (t.localPosition - e.Geschrieben).sqrMagnitude > WiederverwendungKern.EigenQ)
            {
                e.BasisLokal = t.localPosition; e.Hat = false; dUebernommen++;
            }
            else if (!e.Hat) e.BasisLokal = t.localPosition;
            if (e.HatOffset && e.C != null && (e.C._positionOffset - e.GeschriebenOffset).sqrMagnitude > 1e-10f)
            { e.BasisOffset = e.C._positionOffset; e.HatOffset = false; }
        }

        static Vector3 EchtVon(Eintrag e) => e.Hat ? (e.Eltern != null ? e.Eltern.TransformPoint(e.BasisLokal) : e.BasisLokal) : e.T.position;

        static void Zurueck(Eintrag e)
        {
            if (e.T != null && WiederverwendungKern.BasisGilt(e.Hat, e.T.parent == e.Eltern, (e.T.localPosition - e.Geschrieben).sqrMagnitude))
            {
                e.T.localPosition = e.BasisLokal; dZurueck++;
            }
            e.Hat = false;
            OffsetZurueck(e);
            if (e.Getragen) { Traeger.Zurueck(e.S); e.Getragen = false; }   // lampenwand: Traeger an den echten Ort (bleibt angelegt)
        }

        static void OffsetZurueck(Eintrag e)
        {
            if (e.HatOffset && e.C != null && (e.C._positionOffset - e.GeschriebenOffset).sqrMagnitude <= 1e-10f) e.C._positionOffset = e.BasisOffset;
            e.HatOffset = false;
        }

        static void Entfernen(int i)
        {
            var e = Liste[i];
            Zurueck(e);
            Traeger.Frei(e.S);   // lampenwand: kein Raumweg mehr -> Original wieder hoerbar, Traeger weg
            if (e.S != null) Tabelle.Remove(e.S);
            else { // zerstoertes Objekt: Schluessel per Referenz entfernen
                foreach (var kv in Tabelle) if (ReferenceEquals(kv.Value, e)) { Tabelle.Remove(kv.Key); break; }
            }
            Liste.RemoveAt(i);
        }

        internal static void AllesZurueck()
        {
            try { for (int i = Liste.Count - 1; i >= 0; i--) Zurueck(Liste[i]); }
            catch (Exception ex) { Statistik.Fehler(ex); }
            try { Traeger.AllesFrei(); } catch (Exception ex) { Statistik.Fehler(ex); }   // lampenwand
            Liste.Clear(); Tabelle.Clear();
        }

        static V3 V(Vector3 v) => new V3(v.x, v.y, v.z);

        // ---------------------------------------------------------------- Klang-Log / Diagnose

        /// <summary>Klang-Log: " | direction: ..." fuer diese Quelle (null = kein Raumweg / aus).</summary>
        internal static string LogText(BetterSource s, SpatialAudioOccluder occ)
        {
            var inv = CultureInfo.InvariantCulture;
            if (Aktiv != null && !Aktiv.Value) return " | direction: source (setting off)";
            if (s == null || !Tabelle.TryGetValue(s, out var e) || e.Art != 0) return null;
            string wo = e.Ersatz ? "over the obstacle" : "from the opening";
            if (e.Gesperrt) return " | direction: source (" + (e.SchiebGrund ?? "sound object carries visible parts") + ", not moved; target " + e.WinkelZiel.ToString("F0", inv) + " deg"
                                   + (Traeger.Ablehnung(s) is string tg ? "; no carrier: " + tg : "") + ")";   // wegrichtung: vorher als "wall path dominates" gemeldet
            // lampenwand: der Traeger spielt an der Zielposition (Objekt mit sichtbaren Teilen bleibt stehen)
            if (e.Getragen) return " | direction: " + wo + " " + e.WinkelJetzt.ToString("F0", inv) + " deg off the source (target " + e.WinkelZiel.ToString("F0", inv)
                                   + " deg, room path " + (e.Anteil * 100).ToString("F0", inv) + " % of the energy, focus " + e.Buendel.ToString("F2", inv) + ", weight " + e.Gewicht.ToString("F2", inv)
                                   + ", carrier (visible object stays)" + (FuehrendAn ? (RichtungKern.PraezedenzOhneRang ? ", leading path (all paths)" : ", leading path") : "") + ")";
            if (!e.Hat) return " | direction: source (wall path dominates, room path " + (e.Anteil * 100).ToString("F0", inv) + " %)";
            return " | direction: " + wo + " " + e.WinkelJetzt.ToString("F0", inv) + " deg off the source (target " + e.WinkelZiel.ToString("F0", inv)
                   + " deg, room path " + (e.Anteil * 100).ToString("F0", inv) + " % of the energy, focus " + e.Buendel.ToString("F2", inv) + ", weight " + e.Gewicht.ToString("F2", inv)
                   + (e.Selbst ? ", occlusion kept at the source" : "") + (e.Einmal ? (e.Gehalten ? ", one-shot, held" : ", one-shot") : "")
                   + (FuehrendAn ? (RichtungKern.PraezedenzOhneRang ? ", leading path (all paths)" : ", leading path") : "") + ")";
        }

        /// <summary>wegrichtung: Klang-Log " | direction: from the edge ..." fuer Hindernis-Zeilen (null = kein Eintrag dieser Art / Schalter aus).</summary>
        internal static string LogTextKante(BetterSource s)
        {
            if (KanteAktiv == null || !KanteAktiv.Value || s == null || !Tabelle.TryGetValue(s, out var e) || e.Art != 1) return null;
            var inv = CultureInfo.InvariantCulture;
            string paare = e.Paare > 0 ? e.Paare + (e.Paare == 1 ? " table pair" : " table pairs") : "last stored edge";
            if (e.Gesperrt) return " | direction: source (" + (e.SchiebGrund ?? "sound object carries visible parts") + ", not moved; edge target " + e.WinkelZiel.ToString("F0", inv) + " deg)";
            if (!e.Hat) return " | direction: source (edge path " + (e.Anteil * 100).ToString("F0", inv) + " % of the energy, focus " + e.Buendel.ToString("F2", inv) + ", " + paare + ")";
            return " | direction: from the edge " + e.WinkelJetzt.ToString("F0", inv) + " deg off the source (target " + e.WinkelZiel.ToString("F0", inv)
                   + " deg, edge path " + (e.Anteil * 100).ToString("F0", inv) + " % of the energy, delay " + e.DtMs.ToString("F1", inv) + " ms, focus " + e.Buendel.ToString("F2", inv)
                   + ", weight " + e.Gewicht.ToString("F2", inv) + ", " + paare + (e.Selbst ? ", occlusion kept at the source" : "") + ")";
        }

        internal static void Loggen()
        {
            if (dWinkelN == 0 && dZurueck == 0) return;
            Plugin.Log.LogInfo($"[Diagnostics] Direction from the opening: {Liste.Count} sources tracked, {dGesetzt} moved, {dZurueck} put back, " +
                               $"{dUebernommen} times the game moved a source itself (taken over), angle to the source on average " +
                               $"{(dWinkelN > 0 ? dSummeWinkel / dWinkelN : 0):F0} deg, at most {dMaxWinkel:F0} deg, {dVoll} of {dWinkelN} frames fully from the opening; " +
                               $"focus of the opening directions on average {(dBuendelN > 0 ? dSummeBuendel / dBuendelN : 0):F2}, {dWeiter} new sounds continued from their figure's last direction, " +
                               $"{dFreigabe} entries dropped when the game released the source to its pool.");
            if (dKanteN > 0 || dKanteGesperrt > 0)
                Plugin.Log.LogInfo($"[Diagnostics] Direction from the edge (obstacle paths): {dKanteN} frames moved, angle to the source on average {dKanteWinkel / Math.Max(1, dKanteN):F0} deg, " +
                                   $"focus on average {dKanteFokus / Math.Max(1, dKanteN):F2}, {dKanteGesperrt} frames not moved (sound object carries visible parts).");
            dKanteN = dKanteGesperrt = 0; dKanteWinkel = dKanteFokus = 0;
            if (dGetragen > 0 || dNichtVerschiebbar > 0)
                Plugin.Log.LogInfo($"[Diagnostics] Direction from the opening, objects with visible parts: {dGetragen} frames played by a carrier, {dNichtVerschiebbar} frames left at the source.");
            dGetragen = dNichtVerschiebbar = 0;
            Traeger.Loggen();   // lampenwand
            dFreigabe = 0;
            dGesetzt = dZurueck = dUebernommen = dVoll = dWinkelN = dWeiter = dBuendelN = 0; dSummeWinkel = dSummeBuendel = 0; dMaxWinkel = 0;
        }
    }
}
