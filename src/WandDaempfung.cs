using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Materialklang
{
    // ---------------------------------------------------------------------------------------------
    //  17. All fixed sources / Wall damping for non-occluded sources (0.8.5)
    //
    //  Problem: Ambient-Spieler (Abschnitt 15), rohe Szenenquellen und Fahrzeuge (17) sowie die Skriptquellen ohne Okklusion (18:
    //  Lok, Flugzeug, BTR-Turm/-Horn, Moerser-Pfeifen, Ausgangs-Tor) haben im Spiel keinen Occluder. Solange sie nach 3-20 m
    //  verstummten, fiel das kaum auf; mit den verlaengerten Reichweiten hoert man ein Lagerfeuer, das Taxi oder die Lok durch
    //  ganze Haeuser so laut wie im Freien.
    //  Weg (eigene, guenstige Rechnung - kein SourceOccluder/ProcessSourceOcclusion):
    //   - Messung wie "Wand zwischen Raeumen": 5 Linien Quelle -> Ohr (Mitte + 4 um 0,25 m versetzt), je Linie zwei Strahlen
    //     (RaycastCommand-Batch auf den Arbeitsfaeden, hoechstens 8 Treffer je Strahl), Schichten + Massengesetz je Band
    //     (250 Hz / 1 kHz / 4 kHz) mit Empfangsraum und Flanken (Wege.Korrigieren). Rechnung: WandDaempfungKern.Linie.
    //   - Umweg: Raumdaten des Spiels (AudioRoomStorage.FindActualCurrentRoom, RoomPair) -> gleicher Raum/draussen bzw. zwischen
    //     Raeumen, energetisch zur Wand summiert (WandDaempfungKern.Summe, Annahmen dort).
    //   - Wirkung: eigener Filter im Signalweg der Quelle (WandFilter: Lautstaerke x Faktor + 2-poliger Tiefpass, 300 ms
    //     Uebergang), angelegt erst, wenn eine Quelle wirklich gedaempft wird. AudioSource.volume bleibt dem Spiel.
    //   - Takt: hoechstens 'measurements per frame' neue Messungen je Bild (Standard 2), reihum, nur hoerbare Quellen
    //     (spielt, aktiv, innerhalb ihrer Reichweite); neu nach Bewegung > 0,5 m oder nach 'refresh (s)', ferne (> 60 m) 4x seltener.
    //     Nie wird auf einen unfertigen Auftrag gewartet (Ausnahme: Einmal-Klaenge, die sofort gemessen werden). Budget ms/Bild.
    //   - Lebensende: zerstoerte Quellen fallen heraus; Pool-Quellen (BetterSource.Release, Abschnitt 18) und abgeschaltete
    //     Abschnitte melden ab (Filter entfernt). Deaktivierte Objekte (V-Ex-Taxi nach Dispose) werden nicht gemessen.
    //   - Ausnahmen: 2D-Quellen, Objekte mit mehreren AudioSources (ein Skriptfilter waere fuer alle gemeinsam), bypassEffects.
    //     Flugzeug und Moerser-Pfeifen (in der Luft): nur das Stueck bis 40 m vor dem Ohr wird gemessen (Dach/Decke ueber dir).
    //   - Eigene Huelle: Flaechen naeher als 'ignore surfaces near the source' an der Quelle und die Collider des eigenen Fahrzeugs
    //     (V-Ex, BTR, Lok) bzw. einer kleinen Requisite (Elternobjekt < 8 m, <= 64 Collider) zaehlen nicht.
    //  0.8.6 (Streets 09.11.: "mitten im Gebaeude, der BTR kommt direkt durch die Waende statt durch Tueren und Fenster"):
    //   - Umweg = echter Raumweg (Raumweg.Frei, RaumwegFrei.cs): Routen des Spiels fuer das Raumpaar Quelle/Hoerer bzw. der Ersatzweg
    //     ueber die Hindernisse, je Band energetisch zur Wand summiert (WandDaempfungKern.SummeWege). Der pauschale Umweg (15/25 dB)
    //     bleibt nur, wo es keinen Raumweg gibt (gleicher Innenraum, Raumdaten fehlen). Beide drinnen ohne Route: nur die Wand.
    //   - Richtung ueber die Oeffnung wie bei Occluder-Quellen (RichtungKern): das Objekt der Quelle wird um den Hoerer in
    //     Richtung des Austritts gedreht (Abstand bleibt), jedes Bild nachgefuehrt; nur wenn das Objekt nichts als Audio traegt
    //     (Verschiebbar). Messung, Raum und Klang-Log rechnen mit der echten Position (Echt).
    //   - Mehrere Quellen an einem Objekt, die zusammengehoeren (BTR: sechs Motor-/Fahrquellen eines Kontexts), teilen einen
    //     Eintrag und einen Filter (gemeinsam).
    // ---------------------------------------------------------------------------------------------
    internal static class WandDaempfung
    {
        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<float> BudgetMs, Takt, UmwegDraussen, UmwegZwischen, NahQuelle, GrundDraussen;
        internal static ConfigEntry<bool> HoerAn;   // 0.8.6
        internal static ConfigEntry<bool> BodenAn;  // 0.8.6: Boden unter dem Weg ist keine Wand
        internal static ConfigEntry<bool> LuftAn;   // btrfern (16.11.): Luftdaempfung je Band (WandDaempfungKern.Luft)
        internal static ConfigEntry<int> ProBild;

        internal static bool Wirksam => Aktiv != null && Aktiv.Value && Physik.An && !gestoert;

        const int MaxProBild = 8, Strahlen = WandDaempfungKern.Linien * 2, MaxT = WandDaempfungKern.MaxTreffer;
        const float FernAb = 60f, FernFaktor = 4f, HimmelM = 40f, MinD = 0.5f;
        const float RequisiteMax = 8f; const int RequisiteCollider = 64, FahrzeugCollider = 2000;

        sealed class Eintrag
        {
            public AudioSource S; public int Id;
            public string Weg, Name;
            public bool Himmel, Tot, EigenGeprueft, Gesperrt;
            public Transform EigenWurzel;
            public HashSet<int> Eigen;
            public Vector3 MotorQ = new Vector3(float.NaN, 0, 0), MotorA; public float MotorUeberBoden = float.NaN;   // 0.8.6: Messpunkt Motorraum
            public float MotorBoden = float.NaN;   // 0.8.6: Boden unter dem Fahrzeug (Weltkoordinate y)
            public float BodenA = float.NaN, BodenB = float.NaN; public Vector3 BodenQ = new Vector3(float.NaN, 0, 0);   // 0.8.6: Boden unter A/B (Boden unter der Linie)
            public int BodenLinien, BodenTreffer; public bool BodenGilt;
            public double KalDb; public string KalText;   // 0.8.6: Kalibrierung der Quellpegel
            public WandFilter F;
            public string Grund;                          // warum kein Filter (2D, mehrere Quellen, bypass)
            // 0.8.6: zusammengehoerige Quellen an einem Objekt (ein Filter), Raumweg und Richtung
            public bool Gemeinsam; public readonly List<AudioSource> Quellen = new List<AudioSource>(2);
            public int FreieLinien;            // 0.8.6: freie Linien, die mit Ersatzweg nicht zur Wand zaehlen (NurDurch)
            public Raumweg.FreiWeg Frei; public Raumweg.FreiArt Art; public double R0 = -1, R1 = -1, R2 = -1;
            public Transform T, Eltern; public Vector3 BasisLokal, Geschrieben; public bool Verschoben;
            public int Schieben; public string SchiebGrund;   // 0 ungeprueft, 1 darf, -1 nicht (Grund)
            public V3 Aus, Dir; public double Ziel, Anteil, Buendel, WinkelZiel, WinkelJetzt, DirZeit = double.NaN; public bool Richten;
            // Messung
            public bool Hat; public float Zeit = -999f; public Vector3 PosQ, PosH;
            public ISpatialAudioRoom Raum; public Vector3 RaumPos = new Vector3(float.NaN, 0, 0);
            // Ergebnis
            public double W0, W1, W2, U0, L0, L1, L2; public float Gain = 1f, Lp = 22000f; public int Linien;
            public WandSituation Lage; public int Portale; public bool Aussage;
            public int Nah, EigenTreffer;
            public readonly WandLogStueck[] Stuecke = new WandLogStueck[WandDaempfungKern.LogMax]; public int NStuecke;
            public string Kette;   // mehrwand: Kette getrennter Trennwaende der mittleren Linie (Log), null = eine Konstruktion
            // Log
            public float LogZeit = -999f; public double LogL0 = -1; public float LogLp = -1f;
            // 0.8.6: Hoerbarkeit gegen das Grundgeraeusch (QuellPegelKern)
            public PegelArt Pegel = PegelArt.Unbekannt; public float MaxD;
            public double Bezug = double.NaN;   // 0.8.6 Abstandskurve: Bezugsabstand der Kurve (m), fuer die Kalibrierung
            public bool HoerGilt, HoerDraussen; public double HoerDb, HoerGrund, LpOhne = 22000;
            public readonly double[] HoerUeber = new double[3];
            public double A0, A1, A2;   // btrfern: Luftdaempfung je Band in der gespielten Daempfung (dB, Log)
        }

        static readonly Dictionary<int, Eintrag> alle = new Dictionary<int, Eintrag>(256);
        static readonly Dictionary<int, Eintrag> gruppen = new Dictionary<int, Eintrag>(16);   // 0.8.6: gemeinsame Eintraege je Objekt
        static readonly List<Eintrag> liste = new List<Eintrag>(256);
        static int zeiger;
        static bool tote, gestoert, abbauOffen;
        static float abbauNaechste;

        // Strahlen-Auftrag (einer zur Zeit) + Sofort-Auftrag (Einmal-Klaenge)
        static NativeArray<RaycastCommand> cmds, cmdsS;
        static NativeArray<RaycastHit> hits, hitsS;
        static JobHandle handle;
        static bool laeuft;
        static readonly Eintrag[] flug = new Eintrag[MaxProBild];
        static readonly Vector3[] flugA = new Vector3[MaxProBild], flugB = new Vector3[MaxProBild];
        static readonly float[] flugLen = new float[MaxProBild];
        static int nFlug;

        // Rechenpuffer
        static readonly WandTreffer[] vorT = new WandTreffer[MaxT], rueckT = new WandTreffer[MaxT];
        static readonly float[] e0 = new float[WandDaempfungKern.Linien], e1 = new float[WandDaempfungKern.Linien], e2 = new float[WandDaempfungKern.Linien];
        static readonly float[] bandTmp = new float[WegeKern.N];
        static readonly double[] wand = new double[3], gesamt = new double[3], route = new double[3];
        static readonly double[] luftTmp = new double[3];   // btrfern: Luftdaempfung je Band der laufenden Messung (dB)
        static readonly Vector3[] versatz = new Vector3[WandDaempfungKern.Linien];
        static readonly Dictionary<int, bool> gelaende = new Dictionary<int, bool>(256);
        static readonly List<AudioSource> quellenPuffer = new List<AudioSource>(4);
        static readonly List<Collider> colPuffer = new List<Collider>(64);
        static WandMaterial mat;

        // Diagnose
        internal static readonly Laufzeit Zeit = new Laufzeit("wall damping");
        static int dMess, dSofort, dGedaempft, dFrei, dOhne, dBudget, dFilter, dEntfernt;
        static int dGedreht, dZurueck, dUebernommen, dWinkelN; static double dWinkel, dWinkelMax;
        static double dSummeDb, dMaxDb;
        static int dHoer, dHoerStumm, dHoerTief, dHoerLeiser; static double dHoerSummeDb;   // 0.8.6 Hoerbarkeit
        static readonly Hoerbarkeit.Baender hoerErg = new Hoerbarkeit.Baender();

        // =========================================================================================
        //  Einstellungen
        // =========================================================================================
        internal static void Binden(ConfigFile c)
        {
            string s = FesteQuellen.Sek;
            Aktiv = c.Bind(s, "Wall damping for non-occluded sources", true,
                "Ambient players (section 15), raw scene sources and vehicles (this section) and the scripted sources without occlusion (section 18: trains, " +
                "airdrop plane, BTR turret/horn, mortar whistle, extract gates) have no occlusion in the game - with their longer ranges you would hear a campfire, " +
                "the V-Ex taxi or the train through whole buildings as loud as outdoors. On = True Acoustics measures the walls between them and you itself " +
                "(5 lines through the walls, mass law per band like 'Walls between rooms', plus the room path through the game's doors/windows or over the obstacles, " +
                "like occluded sounds, and the direction from the opening) and makes " +
                "them quieter and duller like occluded sources (volume x 'Loudness factor', low-pass from the band curve, 300 ms cross-fade). " +
                "Only extended sources, only while they play and are in range. Sound log: '[Walls]' lines. Works only while Physics Mode is on. Applies live.");
            BudgetMs = c.Bind(s, "Wall damping: max. ms per frame", 0.5f,
                new ConfigDescription("CPU budget on the main thread per frame (the rays themselves run on worker threads). When it is used up, the next sources wait " +
                    "for the next frame. 0 = no limit. The diagnostic line every 30 s shows the time used.", new AcceptableValueRange<float>(0f, 5f)));
            ProBild = c.Bind(s, "Wall damping: measurements per frame", 2,
                new ConfigDescription("New measurements started per frame, in turn over all audible sources (10 rays each). 2 at 60 fps = 120 per second.",
                    new AcceptableValueRange<int>(1, MaxProBild)));
            Takt = c.Bind(s, "Wall damping: refresh (s)", 0.5f,
                new ConfigDescription("A source is measured again after this time (sources farther than 60 m: 4x longer), at once when it or you moved more than 0.5 m.",
                    new AcceptableValueRange<float>(0.1f, 5f)));
            UmwegDraussen = c.Bind(s, "Wall damping: detour outdoors (dB)", 15f,
                new ConfigDescription("Sound also bends around an obstacle (house, car, hill). Loss of that detour at 250 Hz when you and the source are in the same room " +
                    "or both outdoors, +3 dB per octave above (Maekawa). Assumption: ISO 9613-2 caps the screening of one edge at 20 dB; typical 10-20 dB. " +
                    "The wall can only make it quieter down to this detour. Since 0.8.6 only used where there is no room path (same indoor room, room data missing).", new AcceptableValueRange<float>(0f, 40f)));
            UmwegZwischen = c.Bind(s, "Wall damping: detour between rooms (dB)", 25f,
                new ConfigDescription("Detour loss at 250 Hz between different rooms of the game's room network (through doors/windows), +5 dB per further opening of the " +
                    "shortest route, +3 dB per octave. Assumption: ISO 9613-2 caps multiple diffraction at 25 dB. Rooms without any connection: walls only. " +
                    "Since 0.8.6 only a fallback when the game's routes of the room pair are not loaded (otherwise the real routes are calculated).",
                    new AcceptableValueRange<float>(0f, 60f)));
            NahQuelle = c.Bind(s, "Wall damping: ignore surfaces near the source (m)", 1.0f,
                new ConfigDescription("Surfaces closer than this to the source do not count (the barrel of a fire, the housing of a generator). Colliders of the source's " +
                    "own vehicle (V-Ex taxi, BTR, train) and of a small prop it sits in never count.", new AcceptableValueRange<float>(0f, 5f)));
            // 0.8.6: Hoerbarkeit gegen das Grundgeraeusch (QuellPegelKern.cs)
            HoerAn = c.Bind(s, "Wall damping: real audibility", true,
                "These sources carry far (taxi 200 m, BTR 600 m, train 1500 m, plane 2000 m), but the game has no background noise that would mask them. " +
                "On = each source gets a real level (e.g. idling car 75 dB(A) at 1 m, BTR 107, generator 85, campfire 60, alarm 100 - table in the changelog), " +
                "and per band (250 Hz / 1 kHz / 4 kHz) its real level at your ears (distance, air, walls and room path) is checked against the background noise: " +
                "below the background it is masked like quiet occluded sounds (continuously quieter, up to 2 dB per dB, no cut); if only the lows are left it gets dull. " +
                "The volume over distance stays the curve of sections 15/17/18. Off = as 0.8.5. Sound log: 'audible' in the '[Walls]' lines.");
            GrundDraussen = c.Bind(s, "Wall damping: background noise outdoors (dB)", 40f,
                new ConfigDescription("Background noise level (dB(A)) these sources have to be heard over while you are outdoors (wind, distant city, distant fights; " +
                    "quiet urban background is about 35-45 dB(A)). Indoors '0. Physics Mode / Quiet sources: background noise (dB)' applies. Same band shape (NR curve).",
                    new AcceptableValueRange<float>(0f, 70f)));
            BodenAn = c.Bind(s, "Wall damping: ground under the path is no wall", true,
                "Both outdoors: a line from a low source (engine bay 0.75 m) to your ears grazes kerbs, pavements and the road camber over 100-200 m. " +
                "On = flat-lying surfaces near the ground under both ends (pavement, road, terrain, manhole covers) do not count as a wall the sound passes through: " +
                "the ground interrupts the line and the sound goes over it (path over the obstacles; ISO 9613-2: ground attenuation A_gr, not transmission; " +
                "with screening A_gr is part of the barrier term, eq. 12). Walls of buildings and cars still count. Off = as before (ground as a wall layer). " +
                "Sound log: 'ground contact' in the '[Walls]' lines.");
            LuftAn = c.Bind(s, "Wall damping: air absorption (ISO 9613-1)", true,
                "These sources have no occlusion in the game, so they also missed the air low-pass every occluded sound gets: a BTR 400 m away sounded as bright " +
                "as one next to you, only quieter. On = the air absorption of the raid weather (ISO 9613-1, the same weather as the air low-pass) is added per band " +
                "(250 Hz / 1 kHz / 4 kHz) over the straight distance: BTR 420 m at 12 C: 0.5 / 1.6 / 14 dB - duller with distance, barely quieter. " +
                "It does not enter the audibility check twice (that one already subtracts the air from the level at your ears). Off = as before. " +
                "Sound log: 'air' in the '[Walls]' lines. Applies live.");
            EventHandler neu = (_, __) => { foreach (var e in liste) e.Hat = false; abbauOffen = true; };
            LuftAn.SettingChanged += neu;
            HoerAn.SettingChanged += neu; BodenAn.SettingChanged += neu; GrundDraussen.SettingChanged += neu;
            Aktiv.SettingChanged += neu; UmwegDraussen.SettingChanged += neu; UmwegZwischen.SettingChanged += neu; NahQuelle.SettingChanged += neu;
            if (Physik.Aktiv != null) Physik.Aktiv.SettingChanged += neu;
            if (Plugin.Aktiv != null) Plugin.Aktiv.SettingChanged += neu;
        }

        // =========================================================================================
        //  An-/Abmelden (aus den Abschnitten 15/17/18)
        // =========================================================================================

        /// <summary>Stand einer verlaengerten Quelle ohne Okklusion: an = verlaengert (messen), sonst abmelden.
        /// himmel = in der Luft (nur das Stueck vor dem Ohr), sofort = Einmal-Klang (gleich messen), eigen = Wurzel des eigenen Fahrzeugs.</summary>
        internal static void Melden(AudioSource s, bool an, string weg, string name, bool himmel = false, bool sofort = false, Transform eigen = null,
            bool gemeinsam = false, PegelArt pegel = PegelArt.Unbekannt, double bezug = double.NaN)
        {
            if (s == null || gestoert) return;
            try
            {
                if (!an) { Abmelden(s); return; }
                int id = s.GetInstanceID();
                if (!alle.TryGetValue(id, out var e))
                {
                    // 0.8.6 gemeinsam: alle Quellen eines Objekts in einem Eintrag (ein Filter wirkt ohnehin auf alle)
                    int gid = gemeinsam ? s.gameObject.GetInstanceID() : 0;
                    if (gemeinsam && gruppen.TryGetValue(gid, out var g) && !g.Tot) e = g;
                    if (e == null)
                    {
                        e = new Eintrag { S = s, Id = gemeinsam ? gid : id, Gemeinsam = gemeinsam, T = s.transform };
                        liste.Add(e);
                        if (gemeinsam) gruppen[gid] = e;
                    }
                    alle[id] = e;
                    if (!e.Quellen.Contains(s)) e.Quellen.Add(s);
                    if (e.Schieben == 1) e.Schieben = 0;   // neue Quelle am Objekt: Verschiebbarkeit neu pruefen
                }
                e.Weg = weg; e.Name = name ?? s.gameObject.name; e.Himmel = himmel; e.Tot = false;
                if (pegel != PegelArt.Unbekannt || !e.Gemeinsam) e.Pegel = pegel;   // 0.8.6: Pegelannahme fuer die Hoerbarkeit
                if (!double.IsNaN(bezug) || !e.Gemeinsam) e.Bezug = bezug;          // 0.8.6 Abstandskurve: Bezugsabstand der Kurve (Kalibrierung)
                if (eigen != null && e.EigenWurzel != eigen) { e.EigenWurzel = eigen; e.EigenGeprueft = false; }
                if (sofort && Wirksam) Sofort(e);
            }
            catch (Exception ex) { Fehler(ex); }
        }

        internal static void Abmelden(AudioSource s)
        {
            if (s == null) return;
            int id = s.GetInstanceID();
            if (!alle.TryGetValue(id, out var e)) return;
            alle.Remove(id);
            e.Quellen.Remove(s);
            if (e.Quellen.Count > 0) { if (ReferenceEquals(e.S, s)) e.S = e.Quellen[0]; return; }
            Entsorgen(e);
            FilterWeg(e);
            dEntfernt++;
        }

        /// <summary>0.8.6: Eintrag aufgeben - Position zurueck, alle Schluessel (Quellen, Objekt) entfernen.</summary>
        static void Entsorgen(Eintrag e)
        {
            Zurueck(e);
            e.Tot = true; tote = true;
            if (e.Gemeinsam && gruppen.TryGetValue(e.Id, out var g) && ReferenceEquals(g, e)) gruppen.Remove(e.Id);
            foreach (var q in e.Quellen) if (q != null) alle.Remove(q.GetInstanceID());
            if (alle.TryGetValue(e.Id, out var x) && ReferenceEquals(x, e)) alle.Remove(e.Id);
            e.Quellen.Clear();
            e.Frei?.Vergessen();
        }

        static void FilterWeg(Eintrag e)
        {
            // Gesamtreview B6: Destroy wirkt erst am Bildende - bis dahin neutral und aus, und als "geht weg" markiert, damit eine im
            // selben Bild neu ausgeliehene Pool-Quelle sich nicht an den sterbenden Filter haengt (FilterAnlegen).
            try
            {
                if (e.F != null)
                {
                    e.F.Weg = true; e.F.K.ZielGain = 1f; e.F.K.ZielFc = 22000f; e.F.enabled = false;
                    UnityEngine.Object.Destroy(e.F);
                }
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
            e.F = null;
        }

        internal static void SzeneGewechselt()
        {
            try
            {
                if (laeuft) { handle.Complete(); laeuft = false; nFlug = 0; }
                foreach (var e in liste) if (e.S == null) Entsorgen(e);
                // 0.8.6: Schluessel zerstoerter Quellen (gemeinsame Eintraege)
                var weg = new List<int>();
                foreach (var kv in alle) if (kv.Value.Tot) weg.Add(kv.Key);
                foreach (int k in weg) alle.Remove(k);
                tote = true; Aufraeumen();
                gelaende.Clear();
                verschoben.Clear();
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        internal static void Beenden()
        {
            try
            {
                if (laeuft) { handle.Complete(); laeuft = false; }
                if (cmds.IsCreated) cmds.Dispose(); if (hits.IsCreated) hits.Dispose();
                if (cmdsS.IsCreated) cmdsS.Dispose(); if (hitsS.IsCreated) hitsS.Dispose();
            }
            catch { }
        }

        static void Aufraeumen()
        {
            if (!tote) return;
            tote = false;
            liste.RemoveAll(e => e.Tot);
            if (zeiger >= liste.Count) zeiger = 0;
        }

        // =========================================================================================
        //  Takt (Plugin.Update)
        // =========================================================================================
        internal static void Tick()
        {
            if (markeNach >= 0f) MarkeNach();   // 0.8.6 Diagnose bei der Marke
            if (gestoert || (liste.Count == 0 && !laeuft)) return;
            long t0 = Laufzeit.Jetzt; int bild = Time.frameCount;
            try
            {
                if (laeuft && handle.IsCompleted) Abschliessen();
                Aufraeumen();
                if (!Wirksam) { Abbauen(); return; }
                if (abbauOffen) abbauOffen = false;
                var h = FesteQuellen.Hoerer();
                if (h != null) RichtungTakt(h.Value);    // 0.8.6: Richtung ueber die Oeffnung jedes Bild nachfuehren
                if (h == null || laeuft) return;
                Planen(h.Value, t0);
            }
            catch (Exception ex) { Fehler(ex); }
            finally { Zeit.Dazu(t0, bild); }
        }

        static double Budget => BudgetMs != null ? BudgetMs.Value : 0.5;
        static bool UeberBudget(long t0) => Budget > 0 && Laufzeit.Ms(Laufzeit.Jetzt - t0) + Zeit.ImBild(Time.frameCount) > Budget;

        static void Planen(Vector3 hoerer, long t0)
        {
            int max = Math.Min(MaxProBild, ProBild != null ? ProBild.Value : 2);
            float jetzt = Time.time, takt = Takt != null ? Takt.Value : 0.5f;
            int n = liste.Count, gesehen = 0;
            nFlug = 0;
            while (gesehen < n && gesehen < 64 && nFlug < max)
            {
                if (zeiger >= liste.Count) zeiger = 0;
                var e = liste[zeiger++];
                gesehen++;
                if (e.Tot) continue;
                if (!Spielt(e, out float maxD, out float blend)) { if (e.Quellen.Count == 0) Entsorgen(e); else if (e.Verschoben) Zurueck(e); continue; }
                e.MaxD = maxD;
                var s = e.S;
                if (e.Gesperrt && !KlangLog.An) continue;        // kein Filter moeglich: nur fuers Log messen
                Vector3 q = Echt(e);                             // 0.8.6: ohne unsere Verschiebung
                float d = Vector3.Distance(q, hoerer);
                if (d > maxD) continue;                          // unhoerbar: Wert behalten, nicht messen
                if (blend < 0.5f) { Neutral(e, "2D"); continue; }
                if (d < MinD) { Neutral(e, null); e.Hat = true; e.Zeit = jetzt; continue; }
                if (!WandDaempfungKern.Faellig(e.Hat, jetzt - e.Zeit, (q - e.PosQ).magnitude, (hoerer - e.PosH).magnitude, d, takt, FernAb, FernFaktor)) continue;
                if (UeberBudget(t0)) { dBudget++; break; }
                Vorbereiten(e, q, hoerer, d);
                if (Strecke(e, q, hoerer, out var a, out var b, out float len)) { flug[nFlug] = e; flugA[nFlug] = a; flugB[nFlug] = b; flugLen[nFlug] = len; nFlug++; }
            }
            if (nFlug == 0) return;
            if (!cmds.IsCreated)
            {
                cmds = new NativeArray<RaycastCommand>(MaxProBild * Strahlen, Allocator.Persistent);
                hits = new NativeArray<RaycastHit>(MaxProBild * Strahlen * MaxT, Allocator.Persistent);
            }
            for (int i = 0; i < nFlug; i++) Befehle(cmds, i * Strahlen, flugA[i], flugB[i], flugLen[i]);
            handle = RaycastCommand.ScheduleBatch(cmds.GetSubArray(0, nFlug * Strahlen), hits.GetSubArray(0, nFlug * Strahlen * MaxT), 1, MaxT);
            JobHandle.ScheduleBatchedJobs();
            laeuft = true;
        }

        /// <summary>Messstrecke A (Quelle bzw. Punkt in der Luft) -> B (Ohr). false = nichts zu messen.</summary>
        static bool Strecke(Eintrag e, Vector3 q, Vector3 h, out Vector3 a, out Vector3 b, out float len)
        {
            b = h; a = q;
            if (e.EigenWurzel != null && !e.Himmel) { q = Motorpunkt(e, q); a = q; }   // 0.8.6: Fahrzeug strahlt aus dem Motorraum
            Vector3 d = q - h;
            len = d.magnitude;
            if (len < MinD) return false;
            if (e.Himmel && len > HimmelM) { a = h + d * (HimmelM / len); len = HimmelM; }
            // 0.8.6: Boden unter beiden Enden (Bodenlinie fuer WandDaempfungKern.BodenFiltern); Himmelsquellen: keiner
            if (e.Himmel) e.BodenA = float.NaN;
            else if (e.EigenWurzel != null) e.BodenA = e.MotorBoden;
            else if ((a - e.BodenQ).sqrMagnitude > 0.04f || float.IsNaN(e.BodenQ.x)) { e.BodenQ = a; e.BodenA = Boden(a, 4f, e.Eigen); }
            e.BodenB = HoererBoden(b);
            return true;
        }

        /// <summary>0.8.6: Boden unter p (Strahl nach unten bis weit m, ohne eigene Collider), NaN = keiner.</summary>
        static float Boden(Vector3 p, float weit, HashSet<int> eigen)
        {
            int maske = (int)LayersMaskController.HighPolyWithTerrainMask;
            int m = Physics.RaycastNonAlloc(new Ray(p + Vector3.up * 0.05f, Vector3.down), bodenTreffer, weit, maske, QueryTriggerInteraction.Ignore);
            float boden = float.NaN, best = float.MaxValue;
            for (int i = 0; i < m; i++)
            {
                var t = bodenTreffer[i];
                if (t.collider == null || (eigen != null && eigen.Contains(t.collider.GetInstanceID()))) continue;
                if (t.distance < best) { best = t.distance; boden = t.point.y; }
            }
            return boden;
        }

        static float HoererBoden(Vector3 b)
        {
            if (Time.frameCount == hoererBodenBild && (b - hoererBodenPos).sqrMagnitude < 0.01f) return hoererBoden;
            hoererBodenBild = Time.frameCount; hoererBodenPos = b;
            hoererBoden = Boden(b, 3f, null);
            return hoererBoden;
        }
        static int hoererBodenBild = -1; static Vector3 hoererBodenPos; static float hoererBoden = float.NaN;

        // 0.8.6 (Ground Zero 09.11.2026 MARK 10-14, Kaan: "auf freier Luftlinie zum Taxi"): die Quelle des V-Ex-Taxis
        // (WheelDrive.AudioSource "Toyota_Land_Cruiser_close") sitzt am Fahrzeug-Ursprung = auf der Strasse. Beleg: Taxi bei
        // (-20.8, 22.6, 115.0), Hoerer (25.7, 24.3, 108.5), Log d = 47.0 m bei 46.95 m waagerecht -> Hoehenunterschied 1.7 m =
        // Ohrhoehe. Die Messlinie lief damit vom Ohr bis auf den Asphalt und traf auf den letzten Metern Raeder, Karosserien der
        // abgestellten Autos und Muellsaecke ('Cruze_Wheel_Right', 'Subaru_Legacy_door_B_R', 'Garbage_set_02') - die Sichtlinie
        // auf das Taxi war frei. Ein PKW strahlt Motor-/Auspuffgeraeusch aus dem Motorraum ab (Harmonoise/Imagine-Quellmodell:
        // Antrieb 0,3 m leichte bzw. 0,75 m schwere Fahrzeuge; der Motorraum eines Gelaendewagens liegt 0,5-1,1 m hoch).
        // Messpunkt daher MotorHoeheM ueber dem Boden unter der Quelle (Boden ohne die eigenen Collider), nie tiefer als die Quelle.
        const float MotorHoeheM = 0.75f;
        static readonly RaycastHit[] bodenTreffer = new RaycastHit[8];

        static Vector3 Motorpunkt(Eintrag e, Vector3 q)
        {
            if ((q - e.MotorQ).sqrMagnitude < 0.0001f) return e.MotorA;
            e.MotorQ = q; e.MotorA = q;
            int maske = (int)LayersMaskController.HighPolyWithTerrainMask;
            int m = Physics.RaycastNonAlloc(new Ray(q + Vector3.up * 1.5f, Vector3.down), bodenTreffer, 4f, maske, QueryTriggerInteraction.Ignore);
            float boden = float.NaN, best = float.MaxValue;
            for (int i = 0; i < m; i++)
            {
                var t = bodenTreffer[i];
                if (t.collider == null || (e.Eigen != null && e.Eigen.Contains(t.collider.GetInstanceID()))) continue;
                if (t.distance < best) { best = t.distance; boden = t.point.y; }
            }
            if (!float.IsNaN(boden) && q.y < boden + MotorHoeheM) e.MotorA = new Vector3(q.x, boden + MotorHoeheM, q.z);
            e.MotorUeberBoden = float.IsNaN(boden) ? float.NaN : q.y - boden;
            e.MotorBoden = boden;
            return e.MotorA;
        }

        static void Befehle(NativeArray<RaycastCommand> c, int basis, Vector3 a, Vector3 b, float len)
        {
            Vector3 d = (b - a) / len;
            Vector3 u = Vector3.Cross(d, Vector3.up);
            if (u.sqrMagnitude < 0.01f) u = Vector3.Cross(d, Vector3.right);
            u.Normalize();
            Vector3 v = Vector3.Cross(d, u);
            float k = WandDaempfungKern.Kreis;
            versatz[0] = Vector3.zero; versatz[1] = u * k; versatz[2] = -u * k; versatz[3] = v * k; versatz[4] = -v * k;
            int maske = (int)LayersMaskController.HighPolyWithTerrainMask | Statistik.AudioMaske;
            var qp = new QueryParameters(maske, true, QueryTriggerInteraction.Ignore, true);
            for (int l = 0; l < WandDaempfungKern.Linien; l++)
            {
                c[basis + 2 * l] = new RaycastCommand(a + versatz[l], d, qp, len);
                c[basis + 2 * l + 1] = new RaycastCommand(b + versatz[l], -d, qp, len);
            }
        }

        /// <summary>Einmal-Klang (Ausgangs-Tor, Lok-Ereignis): sofort messen (kleiner Auftrag, gleich abgeschlossen) und ohne Uebergang setzen.</summary>
        static void Sofort(Eintrag e)
        {
            var s = e.S;
            var h = FesteQuellen.Hoerer();
            if (s == null || h == null || s.spatialBlend < 0.5f) return;
            Vector3 q = Echt(e);
            float d = Vector3.Distance(q, h.Value);
            if (d > s.maxDistance || d < MinD) return;
            long t0 = Laufzeit.Jetzt; int bild = Time.frameCount;
            try
            {
                Vorbereiten(e, q, h.Value, d);
                if (!Strecke(e, q, h.Value, out var a, out var b, out float len)) return;
                if (!cmdsS.IsCreated)
                {
                    cmdsS = new NativeArray<RaycastCommand>(Strahlen, Allocator.Persistent);
                    hitsS = new NativeArray<RaycastHit>(Strahlen * MaxT, Allocator.Persistent);
                }
                Befehle(cmdsS, 0, a, b, len);
                RaycastCommand.ScheduleBatch(cmdsS, hitsS, 1, MaxT).Complete();
                e.Hat = false;   // erstes Ergebnis ohne Uebergang
                Auswerten(e, hitsS, 0, a, b, len);
                dSofort++;
            }
            finally { Zeit.Dazu(t0, bild); }
        }

        static void Vorbereiten(Eintrag e, Vector3 q, Vector3 h, float d)
        {
            if (!e.EigenGeprueft) { e.EigenGeprueft = true; e.Eigen = EigeneCollider(e); }
            e.PosQ = q; e.PosH = h;
        }

        static void Abschliessen()
        {
            handle.Complete();   // fertig laut IsCompleted -> blockiert nicht
            laeuft = false;
            for (int i = 0; i < nFlug; i++)
            {
                var e = flug[i]; flug[i] = null;
                if (e == null || e.Tot || e.S == null) continue;
                try { Auswerten(e, hits, i * Strahlen * MaxT, flugA[i], flugB[i], flugLen[i]); }
                catch (Exception ex) { Fehler(ex); }
            }
            nFlug = 0;
        }

        // =========================================================================================
        //  Auswerten
        // =========================================================================================
        static WandMaterial Material()
        {
            if (mat == null)
            {
                int n = Plugin.AnzahlGruppen;
                mat = new WandMaterial
                {
                    Gruppe = MaterialCache.Get, Gelaende = IstGelaende,
                    Daemmung = (g, d, f, deckel) => Physik.Daemmung(g, d, f, deckel),
                    Masse = Physik.Masse,
                    Dichte = new float[n], Einzel = new float[n], Massiv = new float[n], Schale = new bool[n],
                    Koerper = (int)Gruppe.Body
                };
            }
            float schale = WandZwischenRaeumen.Schale != null ? WandZwischenRaeumen.Schale.Value : 0.15f;
            for (int g = 0; g < Plugin.AnzahlGruppen; g++)
            {
                mat.Dichte[g] = Physik.Dichte[g].Value; mat.Massiv[g] = Physik.Massiv[g].Value;
                mat.Einzel[g] = Bauakustik.EinzelDicke(g, schale); mat.Schale[g] = Bauakustik.IstSchalenGruppe(g);
            }
            mat.SchaleM = schale; mat.Dyn = Physik.Dynamik.Value;
            var f = Wege.Frequenzen();
            mat.F[0] = f[0]; mat.F[1] = f[1]; mat.F[2] = f[2];
            return mat;
        }

        // 10.11. (akustik-wand, Befund 2): Richtung der Messlinien (A -> B, alle parallel) fuer |cos(Normale, Linie)| der Treffer
        static Vector3 linieDir = Vector3.forward;

        static void Auswerten(Eintrag e, NativeArray<RaycastHit> h, int basis, Vector3 a, Vector3 b, float len)
        {
            linieDir = len > 1e-4f ? (b - a) / len : Vector3.forward;
            var m = Material();
            float dyn = m.Dyn;
            bool log = KlangLog.An;
            e.NStuecke = 0; e.Nah = e.EigenTreffer = 0; e.Kette = null;
            float nah = e.Himmel && len >= HimmelM - 0.01f ? 0f : (NahQuelle != null ? NahQuelle.Value : 1f);
            Wege.TBeginn(b, a);   // Empfangsraum/Flanken = Raum des Hoerers
            bool mitteNahA = false, mitteNahB = false;
            int linien = 0;
            // 0.8.6: Raum der Quelle vor den Linien (Boden unter der Linie nur, wenn beide draussen sind)
            Lage(e, a);
            var hr0 = HoererRaum();
            e.BodenGilt = BodenAn != null && BodenAn.Value && e.Raum != null && e.Raum.IsValid && e.Raum.IsOutdoor && hr0 != null && hr0.IsOutdoor;
            e.BodenLinien = e.BodenTreffer = 0;
            for (int k = 0; k < WandDaempfungKern.Linien; k++)
            {
                int nVor = Lesen(e, h, basis + (2 * k) * MaxT, len, false, nah, vorT, out bool vorVoll);
                int nRueck = Lesen(e, h, basis + (2 * k + 1) * MaxT, len, true, nah, rueckT, out bool rueckVoll);
                // 0.8.6: Boden unter dem Weg ist keine Wand (WandDaempfungKern.BodenFiltern): Treffer raus, Linie vom Boden unterbrochen
                int boden = e.BodenGilt ? WandDaempfungKern.BodenFiltern(vorT, ref nVor, rueckT, ref nRueck, len, e.BodenA, e.BodenB) : 0;
                if (boden > 0) { e.BodenLinien++; e.BodenTreffer += boden; }
                if (k == 0) WandLinie.NahAnEnden(vorT, nVor, rueckT, nRueck, len, out mitteNahA, out mitteNahB);
                int nLog = e.NStuecke;
                var w = WandDaempfungKern.Linie(vorT, nVor, vorVoll, rueckT, nRueck, rueckVoll, len, k != 0, mitteNahA, mitteNahB, m,
                                                log && k == 0 ? e.Stuecke : null, ref nLog);
                if (k == 0) e.NStuecke = nLog;
                if (w.Befund == 1) { e0[k] = e1[k] = e2[k] = -1f; continue; }
                linien++;
                if (w.Befund == 2 || boden > 0) { e0[k] = e1[k] = e2[k] = 0f; continue; }   // dicht bzw. vom Boden unterbrochen
                bandTmp[0] = w.Db0; bandTmp[1] = w.Db1; bandTmp[2] = w.Db2;
                // mehrwand: getrennte Konstruktionen als Kette (WandKetteKern), eine Konstruktion wie bisher
                if (w.Stuecke > 0 && w.Db0 > 0f && w.Masse > 0)
                {
                    if (w.Bloecke > 1) { Wege.KorrigierenKette(WandDaempfungKern.Bloecke, w.Bloecke, len, a, b, bandTmp); if (k == 0) e.Kette = Wege.LetzteKette; }
                    else Wege.Korrigieren(w.Masse, w.R1, w.R2, bandTmp);
                }
                e0[k] = WandDaempfungKern.Energie(bandTmp[0]); e1[k] = WandDaempfungKern.Energie(bandTmp[1]); e2[k] = WandDaempfungKern.Energie(bandTmp[2]);
            }
            dMess++;
            e.Linien = linien;
            bool erst = !e.Hat;
            e.Hat = true; e.Zeit = Time.time;
            // 10.11. (klangfarbe): echte Baender (Rechengrenze statt Max. insulation je Band); gedeckelt wird erst die Lautstaerke unten
            double grenze = WegeKern.BandGrenzeDb;
            if (!WandDaempfungKern.Mitteln(e0, e1, e2, wand, grenze))
            {
                // keine Aussage (alle Linien mit Luecke): letzten Wert behalten
                e.Aussage = false; dOhne++;
                Loggen(e, Vector3.Distance(a, b));
                return;
            }
            e.Aussage = true;
            e.W0 = wand[0]; e.W1 = wand[1]; e.W2 = wand[2];
            // 0.8.6: echter Raumweg (Routen des Spiels / Ersatzweg) statt pauschalem Umweg - nur wenn die Wand ueberhaupt daempft
            // (freie Linie: L = 0 dB, ganz gleich wie laut der Umweg ist)
            e.Art = Raumweg.FreiArt.Keine; e.R0 = e.R1 = e.R2 = -1;
            if (Math.Max(wand[0], Math.Max(wand[1], wand[2])) > 0.05)
            {
                if (e.Frei == null) e.Frei = new Raumweg.FreiWeg();
                e.Art = Raumweg.Frei(e.Frei, a, b, e.Raum, HoererRaum(), e.Eigen);
            }
            e.FreieLinien = 0;
            if (e.Art == Raumweg.FreiArt.Ersatz) NurDurch(e, (float)grenze);
            if (e.Art == Raumweg.FreiArt.Route || e.Art == Raumweg.FreiArt.Ersatz)
            {
                e.R0 = e.Frei.D0; e.R1 = e.Frei.D1; e.R2 = e.Frei.D2; e.U0 = -2;
                route[0] = e.R0; route[1] = e.R1; route[2] = e.R2;
                WandDaempfungKern.SummeWege(wand, route, grenze, gesamt);
            }
            else if (e.Art == Raumweg.FreiArt.Dicht)
            {
                e.U0 = -1;
                WandDaempfungKern.Summe(wand, -1, m.F, grenze, gesamt);
            }
            else
            {
                double u = WandDaempfungKern.Umweg250(e.Lage, e.Portale, UmwegDraussen.Value, UmwegZwischen.Value, dyn);
                e.U0 = u;
                WandDaempfungKern.Summe(wand, u, m.F, grenze, gesamt);
            }
            // e.L0..L2 = echte Daemmung (Log, Anteil des Raumwegs fuer die Richtung); Lautstaerke und Hoerbarkeit mit dem Deckel nur auf
            // dem Bezugsband (WegeKern.Deckeln), der Tiefpass aus dem Abstand der echten Baender
            e.L0 = gesamt[0]; e.L1 = gesamt[1]; e.L2 = gesamt[2];
            float dEcht = Vector3.Distance(Echt(e), b);
            // btrfern (16.11.): Luft je Band zur gespielten Daempfung (Begruendung WandDaempfungKern.Luft); L0..L2 bleiben Wand + Raumweg
            // (Anteil des Raumwegs fuer die Richtung, Verdeckungsrechnung mit eigener Luft)
            Luft(e, dEcht);
            for (int i = 0; i < 3; i++) gesamt[i] += luftTmp[i];
            WegeKern.Deckeln(gesamt, dyn);
            // 0.8.6 Kennlinie (Hoerbarkeit.Kennlinie): laute Quellarten (BTR, Lok, Flugzeug, Alarm) im gespielten Mass gestaucht, sonst L0 x Lautheit
            e.Gain = (float)WandDaempfungKern.Gain(QuellPegelKern.Gestaucht(e.Pegel, dEcht, gesamt[0], Physik.Lautheit.Value, Physik.KennKnie.Value, Physik.KennSteigung.Value), 1.0);
            // farbekennlinie (12.11.): Neigung mit dem Stauchungsfaktor der Lautstaerke (FarbeKennlinieKern.cs; leise Quellarten unter dem Knie: 1)
            double farbe = Physik.FarbeKennlinieAn != null && Physik.FarbeKennlinieAn.Value && QuellPegelKern.Gilt(e.Pegel)
                ? FarbeKennlinie.Faktor(QuellPegelKern.FreiPegelA(e.Pegel, dEcht), gesamt[0], Physik.KennKnie.Value, Physik.KennSteigung.Value) : 1.0;
            e.Lp = Wege.Baender ? Wege.FitLp(farbe * Math.Max(0, e.L1 + e.A1 - e.L0 - e.A0), farbe * Math.Max(0, e.L2 + e.A2 - e.L0 - e.A0), eigenerFilter: true) : Physik.Abbildung((float)(gesamt[0] / dyn));
            if (e.L0 > 0.05) { dGedaempft++; dSummeDb += e.L0; dMaxDb = Math.Max(dMaxDb, e.L0); } else dFrei++;   // btrfern: Diagnose ohne Luft wie bisher
            Hoeren(e, dEcht, dyn);   // 0.8.6: Verdeckung durch das Grundgeraeusch
            // 0.8.6 Kalibrierung (Kalibrierung.cs): fester dB-Wert je Quellart, Bezug = gemessene Schritte
            e.KalDb = Kalibrierung.Db(e.Pegel, e.S, e.Bezug, KlangLog.An, out e.KalText);
            if (e.KalDb != 0) e.Gain = Mathf.Clamp(e.Gain * (float)Math.Pow(10, e.KalDb / 20), WandFilterKern.MinGain, WandFilterKern.MaxGain);
            Setzen(e, erst);
            RichtungZiel(e, b, erst);   // 0.8.6: nach Setzen (Gesperrt bekannt)
            Loggen(e, Vector3.Distance(Echt(e), b));
        }

        /// <summary>btrfern: Luftdaempfung je Band fuer die gerade Entfernung d (Raid-Wetter Wetter.Baender, ISO 9613-1) nach luftTmp und e.A0..A2;
        /// Schalter aus: 0.</summary>
        static void Luft(Eintrag e, float d)
        {
            if (LuftAn != null && LuftAn.Value)
            {
                var lb = Wetter.Baender; var f = Wege.Frequenzen();
                WandDaempfungKern.Luft(d, lb.Alpha(f[0]), lb.Alpha(f[1]), lb.Alpha(f[2]), luftTmp);
            }
            else luftTmp[0] = luftTmp[1] = luftTmp[2] = 0;
            e.A0 = luftTmp[0]; e.A1 = luftTmp[1]; e.A2 = luftTmp[2];
        }

        // 0.8.6 (Meldung Streets 09.11.2026 "harter Cut am Taxi"): die Wand sprang zwischen 60 und 8,2 dB, je nachdem ob eine der vier
        // aeusseren Linien (Kreis 0,25 m) knapp ueber die Kante des Platzes ging: 10 lg(1 / 0,15) = 8,2 dB. Fuenf Linien in 0,25 m sind
        // keine Messung der Fresnelzone (bei 100 m und 250 Hz rund 6 m Radius) - ob eine davon frei ist, ist an der Schattengrenze Zufall.
        // Mit Ersatzweg traegt die Beugung ueber die Hindernisse (ISO 9613-2, stetig an der Schattengrenze) den Schall auf und knapp
        // neben der Sichtlinie; die Wand ist dann nur der Weg DURCH das Hindernis: gemittelt ueber die Linien, die etwas treffen.
        // Freie Linien fallen dort heraus statt mit Energie 1 zu zaehlen (sonst zaehlt derselbe freie Weg doppelt und flackert).
        // Alle Linien frei: Wand 0 dB wie bisher (dann wird kein Raumweg gerechnet).
        static readonly float[] d0T = new float[WandDaempfungKern.Linien], d1T = new float[WandDaempfungKern.Linien], d2T = new float[WandDaempfungKern.Linien];
        static void NurDurch(Eintrag e, float dyn)
        {
            int frei = 0, durch = 0;
            for (int k = 0; k < WandDaempfungKern.Linien; k++)
            {
                bool f = e0[k] >= WandDaempfungKern.FreiEnergie;
                if (f) frei++; else if (e0[k] >= 0f) durch++;
                d0T[k] = f ? -1f : e0[k]; d1T[k] = f ? -1f : e1[k]; d2T[k] = f ? -1f : e2[k];
            }
            if (frei == 0 || durch == 0) return;
            if (!WandDaempfungKern.Mitteln(d0T, d1T, d2T, wand, dyn)) return;
            e.FreieLinien = frei;
            e.W0 = wand[0]; e.W1 = wand[1]; e.W2 = wand[2];
        }

        /// <summary>0.8.6: Hoerbarkeit je Band gegen das Grundgeraeusch (QuellPegelKern.Rechnen): Lautstaerke = lautestes Band, Tiefpass an die
        /// hoerbaren Baender. Ohne Pegelart / aus: Gain und Tiefpass bleiben wie gemessen. d = gerade Entfernung (echter Ort).</summary>
        static void Hoeren(Eintrag e, float d, float dyn)
        {
            e.HoerGilt = false; e.LpOhne = e.Lp;
            if (HoerAn == null || !HoerAn.Value || !QuellPegelKern.Gilt(e.Pegel)) return;
            var lr = HoererRaum();
            e.HoerDraussen = lr != null && lr.IsOutdoor;
            e.HoerGrund = e.HoerDraussen ? GrundDraussen.Value : Physik.Grundgeraeusch.Value;
            gesamt[0] = e.L0; gesamt[1] = e.L1; gesamt[2] = e.L2;
            WegeKern.Deckeln(gesamt, dyn);   // 10.11. (klangfarbe): Deckel nur auf dem Bezugsband, Abstand der Baender echt
            if (!QuellPegelKern.Rechnen(e.Pegel, d, e.HoerGrund, gesamt, Physik.Lautheit.Value, dyn, hoerErg, 0, Physik.KennKnie.Value, Physik.KennSteigung.Value)) return;
            e.HoerGilt = true;
            for (int i = 0; i < 3; i++) e.HoerUeber[i] = hoerErg.Ueber[i];
            luftTmp[0] = e.A0; luftTmp[1] = e.A1; luftTmp[2] = e.A2;
            WandDaempfungKern.LuftInG(hoerErg, luftTmp);   // btrfern: Luft in die gespielte Daempfung je Band, nicht in die Verdeckung
            double alt = QuellPegelKern.Gestaucht(e.Pegel, d, gesamt[0], Physik.Lautheit.Value, Physik.KennKnie.Value, Physik.KennSteigung.Value);   // 0.8.6 Kennlinie
            e.HoerDb = hoerErg.Lautstaerke;
            e.Gain = (float)WandDaempfungKern.Gain(e.HoerDb, 1.0);
            var f = Wege.Frequenzen();
            e.Lp = (float)Hoerbarkeit.Tiefpass(hoerErg, e.Lp, f[1], f[2], Physik.TiefpassMin.Value, f[0]);
            dHoer++;
            if (e.HoerDb > alt + 0.05) { dHoerLeiser++; dHoerSummeDb += e.HoerDb - alt; }
            if (hoerErg.Stumm) dHoerStumm++;
            else if (!hoerErg.Hoerbar(1) && !hoerErg.Hoerbar(2)) dHoerTief++;
        }

        // Treffer eines Strahls (T ab A = Quelle), ohne eigene Huelle und Flaechen nahe der Quelle. voll = Trefferhoechstzahl erreicht.
        static int Lesen(Eintrag e, NativeArray<RaycastHit> h, int basis, float len, bool rueck, float nah, WandTreffer[] ziel, out bool voll)
        {
            int n = 0, roh = 0;
            for (int j = 0; j < MaxT; j++)
            {
                RaycastHit t = h[basis + j];
                int id = t.colliderInstanceID;
                if (id == 0) break;
                roh++;
                float T = rueck ? len - t.distance : t.distance;
                if (e.Eigen != null && e.Eigen.Contains(id)) { e.EigenTreffer++; continue; }
                if (T < nah) { e.Nah++; continue; }
                ziel[n].Id = id; ziel[n].T = T; ziel[n].Y = t.point.y; ziel[n].Ny = t.normal.y; ziel[n].Cos = Mathf.Abs(Vector3.Dot(t.normal, linieDir)); n++;
            }
            voll = roh >= MaxT;
            return n;
        }

        /// <summary>0.8.6: Hoerer in einem Aussenraum der Raumdaten (Kalibrierung: Mixer-Snapshot "Outdoor").</summary>
        internal static bool HoererDraussen()
        {
            var r = HoererRaum();
            return r != null && r.IsOutdoor;
        }

        static ISpatialAudioRoom HoererRaum()
        {
            if (!MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated) return null;
            var lr = MonoBehaviourSingleton<SpatialAudioSystem>.Instance.ListenerCurrentRoom;
            return lr != null && lr.IsValid ? lr : null;
        }

        static void Lage(Eintrag e, Vector3 q)
        {
            e.Lage = WandSituation.Unbekannt; e.Portale = 0;
            if (!MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated) return;
            var sys = MonoBehaviourSingleton<SpatialAudioSystem>.Instance;
            var st = sys._audioRoomStorage;
            var lr = sys.ListenerCurrentRoom;
            if (st == null || lr == null || !lr.IsValid) return;
            if (e.Raum == null || !e.Raum.IsValid || float.IsNaN(e.RaumPos.x) || (q - e.RaumPos).sqrMagnitude > 1f)
            {
                e.Raum = st.FindActualCurrentRoom(e.Raum ?? st._emptyRoom, q);
                e.RaumPos = q;
            }
            var qr = e.Raum;
            bool gueltig = qr != null && qr.IsValid;
            bool paar = false;
            if (gueltig && qr.ID != lr.ID && !(qr.IsOutdoor && lr.IsOutdoor) && st.TryGetMatchingRoomPair(qr, lr, out var rp, out _) && rp != null)
            { paar = true; e.Portale = rp.ShortestRouteLength; }
            e.Lage = WandDaempfungKern.Lage(gueltig, gueltig ? qr.ID : -1, lr.ID, gueltig && qr.IsOutdoor, lr.IsOutdoor, paar);
        }

        // ---- Wirkung ----
        static void Setzen(Eintrag e, bool sofort)
        {
            bool neutral = WandFilterKern.IstNeutral(e.Gain, e.Lp);
            if (e.F == null)
            {
                if (neutral || e.Gesperrt) return;
                if (!FilterAnlegen(e)) return;
                sofort = true;
            }
            var k = e.F.K;
            k.Tau = (float)Math.Max(0.0, Wege.BlendSek / 3.0);
            k.ZielGain = e.Gain; k.ZielFc = e.Lp;
            if (sofort) k.Springen = true;
        }

        static void Neutral(Eintrag e, string grund)
        {
            if (grund != null) e.Grund = grund;
            e.Gain = 1f; e.Lp = 22000f; e.L0 = e.L1 = e.L2 = 0;
            if (e.F != null) { e.F.K.ZielGain = 1f; e.F.K.ZielFc = 22000f; }
            e.Richten = false; e.Ziel = 0;
        }

        static bool FilterAnlegen(Eintrag e)
        {
            var s = e.S;
            if (s.bypassEffects) { e.Gesperrt = true; e.Grund = "bypass effects"; return false; }
            quellenPuffer.Clear();
            s.GetComponents(quellenPuffer);
            int n = quellenPuffer.Count;
            // 0.8.6: zusammengehoerige Quellen (gemeinsam, gleicher Ort) teilen den Filter; eine fremde Quelle am Objekt sperrt weiter
            bool fremd = false, umgehen = false;
            foreach (var x in quellenPuffer) { if (x == null) continue; if (!e.Quellen.Contains(x)) fremd = true; if (x.bypassEffects) umgehen = true; }
            quellenPuffer.Clear();
            if (n > 1 && e.Gemeinsam && fremd) { e.Grund = "several audio sources on one object (not all registered yet)"; return false; }   // naechste Messung
            if (n > 1 && !e.Gemeinsam) { e.Gesperrt = true; e.Grund = "several audio sources on one object"; return false; }
            if (umgehen) { e.Gesperrt = true; e.Grund = "bypass effects"; return false; }
            WandFilter f = null;
            foreach (var x in s.gameObject.GetComponents<WandFilter>()) if (x != null && !x.Weg) { f = x; break; }   // Gesamtreview B6: nie den sterbenden Filter
            if (f == null) f = s.gameObject.AddComponent<WandFilter>();
            f.K.Fs = AudioSettings.outputSampleRate;
            e.F = f; e.Grund = null;
            dFilter++;
            return true;
        }

        /// <summary>Abschnitt aus: Filter auf neutral, nach dem Ausblenden entfernen.</summary>
        static void Abbauen()
        {
            float jetzt = Time.realtimeSinceStartup;
            if (jetzt < abbauNaechste) return;
            abbauNaechste = jetzt + 0.25f;
            foreach (var e in liste)
            {
                if (e.F == null) continue;
                var k = e.F.K;
                k.Tau = (float)Math.Max(0.0, Wege.BlendSek / 3.0); k.ZielGain = 1f; k.ZielFc = 22000f;
                e.Hat = false;
                if (k.Durchgang || e.S == null || !e.S.isPlaying) FilterWeg(e);
            }
            foreach (var e in liste) { e.Richten = false; if (e.Verschoben) Zurueck(e); }   // 0.8.6
        }

        // =========================================================================================
        //  0.8.6: Quellen eines Eintrags, echte Position, Richtung ueber die Oeffnung
        // =========================================================================================

        /// <summary>Spielt eine der Quellen (aktiv)? maxD/blend = groesste Reichweite / groesster Raumanteil der spielenden.</summary>
        static bool Spielt(Eintrag e, out float maxD, out float blend)
        {
            maxD = 0f; blend = 0f; bool an = false;
            for (int i = e.Quellen.Count - 1; i >= 0; i--)
            {
                var q = e.Quellen[i];
                if (q == null) { e.Quellen.RemoveAt(i); continue; }
                if (!q.isActiveAndEnabled || !q.isPlaying) continue;
                an = true; if (q.maxDistance > maxD) maxD = q.maxDistance; if (q.spatialBlend > blend) blend = q.spatialBlend;
            }
            if (e.S == null && e.Quellen.Count > 0) e.S = e.Quellen[0];
            return an && e.S != null;
        }

        /// <summary>Echte Position der Quelle (ohne unsere Verschiebung). Hat das Spiel das Objekt inzwischen selbst bewegt oder
        /// umgehaengt, gilt das als neue echte Position (wie Richtung.Uebernehmen).</summary>
        static Vector3 Echt(Eintrag e)
        {
            var t = e.T != null ? e.T : (e.S != null ? e.S.transform : null);
            if (t == null) return Vector3.zero;
            e.T = t;
            return EchtEigen(e) - Versatz(t);
        }

        // Position ohne die eigene Verschiebung (Verschiebungen der Eltern stecken noch darin)
        static Vector3 EchtEigen(Eintrag e)
        {
            var t = e.T;
            if (!e.Verschoben) return t.position;
            if (t.parent != e.Eltern || (t.localPosition - e.Geschrieben).sqrMagnitude > 1e-10f)
            {
                e.Verschoben = false; e.Dir = default; dUebernommen++;
                if (verschoben.TryGetValue(t, out var x) && ReferenceEquals(x, e)) verschoben.Remove(t);
                return t.position;
            }
            return e.Eltern != null ? e.Eltern.TransformPoint(e.BasisLokal) : e.BasisLokal;
        }

        /// <summary>Verschiebung durch gedrehte Vorfahren (Fahrzeug: z. B. Turm oder Horn unter dem Motor-Objekt). Wir setzen nur
        /// Positionen, keine Drehung: jeder Nachfahre ist um genau dieselbe Strecke mitgewandert.</summary>
        static Vector3 Versatz(Transform t)
        {
            if (verschoben.Count == 0) return Vector3.zero;
            Vector3 v = Vector3.zero;
            var p = t.parent;
            for (int i = 0; i < 16 && p != null; i++, p = p.parent)
                if (verschoben.TryGetValue(p, out var a) && a.Verschoben && !a.Tot) v += p.position - EchtEigen(a);
            return v;
        }
        static readonly Dictionary<Transform, Eintrag> verschoben = new Dictionary<Transform, Eintrag>(16);

        static void Zurueck(Eintrag e)
        {
            if (!e.Verschoben) return;
            e.Verschoben = false;
            if (e.T != null && verschoben.TryGetValue(e.T, out var x) && ReferenceEquals(x, e)) verschoben.Remove(e.T);
            try
            {
                var t = e.T;
                if (t != null && t.parent == e.Eltern && (t.localPosition - e.Geschrieben).sqrMagnitude <= 1e-10f) { t.localPosition = e.BasisLokal; dZurueck++; }
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        /// <summary>Darf das Objekt der Quelle um den Hoerer gedreht werden? Nur reine Audio-Objekte: ausser Transform, den Quellen
        /// dieses Eintrags, Audio-Filtern und Skripten nichts (kein Renderer, Collider, Licht, Partikel, Rigidbody ... im Teilbaum),
        /// nicht die Wurzel der Szene oder des eigenen Fahrzeugs, keine fremde AudioSource darunter.</summary>
        static bool Verschiebbar(Eintrag e)
        {
            if (e.Schieben != 0) return e.Schieben > 0;
            e.Schieben = -1;
            var t = e.T;
            if (t == null) { e.SchiebGrund = "no object"; return false; }
            if (t.parent == null) { e.SchiebGrund = "object is a scene root"; return false; }
            if (e.EigenWurzel != null && t == e.EigenWurzel) { e.SchiebGrund = "object is the vehicle itself"; return false; }
            komPuffer.Clear();
            try
            {
                t.GetComponentsInChildren(true, komPuffer);
                if (komPuffer.Count > 64) { e.SchiebGrund = "object carries " + komPuffer.Count + " components"; return false; }
                var eigeneBs = e.S != null ? e.S.GetComponent<BetterSource>() : null;
                foreach (var c in komPuffer)
                {
                    if (c == null || c is Transform || c is WandFilter) continue;
                    // Quelle mit Okklusion des Spiels darunter: ihr Container rechnet vom Objekt aus -> nicht verschieben
                    if (c is BetterSource bs) { if (!ReferenceEquals(bs, eigeneBs)) { e.SchiebGrund = "occluded game source '" + bs.gameObject.name + "' below the object"; return false; } continue; }
                    if (c is MonoBehaviour) continue;
                    // fremde AudioSource darunter: nur am eigenen Fahrzeug (Turm, Federung - derselbe Ort, ebenfalls ohne Okklusion;
                    // eingetragene Quellen rechnen ueber Versatz() weiter vom echten Ort)
                    if (c is AudioSource a) { if (!e.Quellen.Contains(a) && e.EigenWurzel == null) { e.SchiebGrund = "other audio source '" + a.gameObject.name + "' below the object"; return false; } continue; }
                    var ty = c.GetType();
                    if (ty.Namespace == "UnityEngine" && ty.Name.StartsWith("Audio", StringComparison.Ordinal)) continue;   // Audio-Filter
                    e.SchiebGrund = ty.Name + " on '" + c.gameObject.name + "'";
                    return false;
                }
            }
            finally { komPuffer.Clear(); }
            e.Schieben = 1; e.SchiebGrund = null;
            return true;
        }
        static readonly List<Component> komPuffer = new List<Component>(16);

        /// <summary>Nach einer Messung: Ziel der Ortung (Austritt, Gewicht = Energieanteil des Raumwegs x Buendelung).</summary>
        static void RichtungZiel(Eintrag e, Vector3 hoerer, bool sofort)
        {
            bool weg = (e.Art == Raumweg.FreiArt.Route || e.Art == Raumweg.FreiArt.Ersatz) && e.Frei != null && !e.Frei.Aus.IstNull;
            // Lautstaerke und Richtung kommen aus demselben Weg: ohne Filter (gesperrt) bleibt auch die Richtung beim Spiel
            if (!weg || !Richtung.An || e.Gesperrt) { e.Richten = false; e.Ziel = 0; e.Anteil = 0; return; }
            e.Aus = e.Frei.Aus; e.Buendel = e.Frei.Buendel;
            e.Anteil = RichtungKern.Anteil(e.R0, e.L0);
            e.Ziel = e.Anteil * e.Buendel;
            e.Richten = true;
            if (sofort) e.Dir = default;   // erste Messung: gleich in der Zielrichtung
            Richten(e, hoerer);
        }

        /// <summary>Jedes Bild: Richtung nachfuehren (Hoerer bewegt sich, Fahrzeug faehrt) oder zuruecksetzen.</summary>
        static void RichtungTakt(Vector3 hoerer)
        {
            bool an = Richtung.An;
            for (int i = 0; i < liste.Count; i++)
            {
                var e = liste[i];
                if (e.Tot || (!e.Richten && !e.Verschoben)) continue;
                if (!an || !e.Richten || !Spielt(e, out _, out _)) { Zurueck(e); if (!an) e.Richten = false; continue; }
                try { Richten(e, hoerer); } catch (Exception ex) { Statistik.Fehler(ex); Zurueck(e); e.Richten = false; }
            }
        }

        static void Richten(Eintrag e, Vector3 hv)
        {
            if (e.T == null && e.S != null) e.T = e.S.transform;
            if (e.T == null) return;
            if (!Verschiebbar(e)) { if (e.Verschoben) Zurueck(e); return; }
            Vector3 qv = Echt(e);
            V3 h = new V3(hv.x, hv.y, hv.z), q = new V3(qv.x, qv.y, qv.z);
            if (!RichtungKern.Ziel(h, q, e.Aus, e.Ziel, out V3 dir, out double w)) { Zurueck(e); e.Dir = default; return; }
            double jetzt = Time.realtimeSinceStartup;
            double dt = double.IsNaN(e.DirZeit) ? 0 : Math.Max(0, jetzt - e.DirZeit);
            e.DirZeit = jetzt;
            double tau = Richtung.GlaettungMs != null ? Richtung.GlaettungMs.Value / 1000.0 : 0;
            e.Dir = e.Dir.IstNull ? dir : RichtungKern.Nachfuehren(e.Dir, dir, dt, tau);
            double abstand = (q - h).Laenge;
            V3 u = (q - h) * (1.0 / Math.Max(abstand, 1e-6));
            e.WinkelZiel = RichtungKern.Winkel(u, dir);
            e.WinkelJetzt = RichtungKern.Winkel(u, e.Dir);
            if (e.WinkelJetzt < 0.2 && w < 0.01) { Zurueck(e); return; }   // nichts zu drehen (Wand dominiert)
            if (!e.Verschoben) { e.Eltern = e.T.parent; e.BasisLokal = e.T.localPosition; dGedreht++; verschoben[e.T] = e; }
            V3 ziel = RichtungKern.Position(h, e.Dir, abstand);
            e.T.position = new Vector3((float)ziel.X, (float)ziel.Y, (float)ziel.Z);
            e.Geschrieben = e.T.localPosition;
            e.Verschoben = true;
            dWinkelN++; dWinkel += e.WinkelJetzt; if (e.WinkelJetzt > dWinkelMax) dWinkelMax = e.WinkelJetzt;
        }

        // ---- eigene Huelle ----
        static HashSet<int> EigeneCollider(Eintrag e)
        {
            colPuffer.Clear();
            try
            {
                if (e.EigenWurzel != null)
                {
                    e.EigenWurzel.GetComponentsInChildren(true, colPuffer);
                    if (colPuffer.Count == 0 || colPuffer.Count > FahrzeugCollider) return null;
                    return Menge();
                }
                var p = e.S.transform.parent;
                if (p == null || p.childCount > 32) return null;
                p.GetComponentsInChildren(true, colPuffer);
                if (colPuffer.Count == 0 || colPuffer.Count > RequisiteCollider) return null;
                Bounds bb = default; bool erst = true;
                foreach (var c in colPuffer)
                {
                    if (c == null) continue;
                    if (c is TerrainCollider) return null;
                    if (erst) { bb = c.bounds; erst = false; } else bb.Encapsulate(c.bounds);
                }
                if (erst || bb.size.x > RequisiteMax || bb.size.y > RequisiteMax || bb.size.z > RequisiteMax) return null;
                return Menge();
            }
            finally { colPuffer.Clear(); }
        }

        static HashSet<int> Menge()
        {
            var m = new HashSet<int>();
            foreach (var c in colPuffer) if (c != null) m.Add(c.GetInstanceID());
            return m.Count > 0 ? m : null;
        }

        static bool IstGelaende(int id)
        {
            if (gelaende.TryGetValue(id, out bool b)) return b;
            b = Resources.InstanceIDToObject(id) is TerrainCollider;
            gelaende[id] = b;
            return b;
        }

        static void Fehler(Exception ex)
        {
            Statistik.Fehler(ex);
            if (ex is MissingFieldException || ex is MissingMethodException || ex is TypeLoadException)
            {
                gestoert = true;
                Plugin.Log.LogError($"Wall damping: {ex.GetType().Name}: {ex.Message} - game changed? Wall damping off for this session.");
                foreach (var e in liste) { FilterWeg(e); Zurueck(e); e.Richten = false; }
            }
        }

        // =========================================================================================
        //  Klang-Log und Diagnose
        // =========================================================================================
        static void Loggen(Eintrag e, float d)
        {
            // 0.8.6: Fahrzeuge/Skriptquellen so weit wie ihre [Vehicle]/[Scripted]-Zeile (bis 1,25 x Reichweite) - der BTR in 300-600 m
            // stand dort mit "see [Walls]", die [Walls]-Zeile fiel aber am Log-Radius (300 m) weg
            float radius = KlangLog.Radius.Value;
            if (e.Weg == "vehicle" || e.Weg == "scripted") radius = Math.Max(radius, 1.25f * e.MaxD);
            if (!KlangLog.An || d > radius) return;
            float jetzt = Time.realtimeSinceStartup;
            if (jetzt - e.LogZeit < 1f) return;
            double lw = e.HoerGilt ? e.HoerDb : Math.Min(e.L0, Physik.Dynamik.Value);   // 0.8.6: mit Hoerbarkeit zaehlt die angewandte Lautstaerke
            bool aenderung = Math.Abs(lw - e.LogL0) > 1.0 || (e.LogLp > 0 && Math.Abs(Math.Log(e.Lp / e.LogLp)) > 0.15);
            if (!aenderung && jetzt - e.LogZeit < 5f) return;
            e.LogZeit = jetzt; e.LogL0 = lw; e.LogLp = e.Lp;
            KlangLog.Text(Text(e, d));
        }

        static string Text(Eintrag e, float d)
        {
            var inv = CultureInfo.InvariantCulture;
            var s = e.S;
            var sb = new StringBuilder(320);
            sb.Append("[Walls] ").Append(e.Name);
            // 0.8.6 (Ground Zero 09.11.2026 22:22, sieben 'barrel_fire_sound'): Instanz und echter Ort, damit gleichnamige Quellen unterscheidbar sind
            if (s != null) { sb.Append(" #").Append(s.GetInstanceID()); if (s.gameObject.name != e.Name) sb.Append(" '").Append(s.gameObject.name).Append('\''); }
            sb.Append(" (").Append(e.Weg).Append(", clip ").Append(s != null && s.clip != null ? s.clip.name : "-").Append(')');
            sb.Append(" | d=").Append(d.ToString("F1", inv)).Append(" m at ");
            Pos(sb, Echt(e), inv);
            if (e.Verschoben && e.T != null) Pos(sb.Append(" (object moved to "), e.T.position, inv).Append(" for the direction)");
            if (e.Himmel) sb.Append(" (in the air: last ").Append(HimmelM.ToString("0", inv)).Append(" m measured)");
            if (e.EigenWurzel != null && !float.IsNaN(e.MotorUeberBoden))
                sb.Append(" (source ").Append(e.MotorUeberBoden.ToString("F2", inv)).Append(" m above ground, measured from ")
                  .Append(Math.Max(e.MotorUeberBoden, MotorHoeheM).ToString("F2", inv)).Append(" m = engine bay)");
            sb.Append(" | ").Append(WandDaempfungKern.LageName(e.Lage));
            if (e.Lage == WandSituation.ZwischenRaeumen) sb.Append(" (route ").Append(e.Portale).Append(" openings)");
            if (!e.Aussage) { sb.Append(" | no usable line (gaps) - value kept: ").Append(e.L0.ToString("F1", inv)).Append(" dB"); return sb.ToString(); }
            sb.Append(" | lines ").Append(e.Linien).Append('/').Append(WandDaempfungKern.Linien);
            if (e.FreieLinien > 0) sb.Append(" (").Append(e.FreieLinien).Append(" clear: carried by the path over the obstacles, walls = through the obstacle only)");
            if (e.NStuecke > 0)
            {
                sb.Append(" | wall:");
                for (int i = 0; i < e.NStuecke; i++)
                {
                    var p = e.Stuecke[i];
                    sb.Append(i == 0 ? " " : ", ").Append(p.G >= 0 && p.G < Plugin.AnzahlGruppen ? ((Gruppe)p.G).ToString() : "?").Append(' ')
                      .Append(p.Dicke.ToString("0.00", inv)).Append(" m ").Append(p.Db.ToString("F1", inv)).Append(" dB");
                    if (p.Teile > 1) sb.Append(" (").Append(p.Teile).Append(" parts)");
                    string n = Name(p.Id);
                    if (n != null) sb.Append(" '").Append(n).Append('\'');
                }
            }
            else sb.Append(" | centre line clear");
            sb.Append(" | walls ").Append(e.W0.ToString("F1", inv)).Append('/').Append(e.W1.ToString("F1", inv)).Append('/').Append(e.W2.ToString("F1", inv)).Append(" dB");
            if (e.Kette != null) sb.Append(" |").Append(e.Kette);   // mehrwand
            // 0.8.6: echter Raumweg (Routen des Spiels / ueber die Hindernisse) statt pauschalem Umweg
            if (e.Art == Raumweg.FreiArt.Route || e.Art == Raumweg.FreiArt.Ersatz)
                sb.Append(e.Art == Raumweg.FreiArt.Route ? " + room path through the openings (" + e.Frei.Routen + " game routes) " : " + over the obstacles (no game route) ")
                  .Append(e.R0.ToString("F1", inv)).Append('/').Append(e.R1.ToString("F1", inv)).Append('/').Append(e.R2.ToString("F1", inv)).Append(" dB");
            else if (e.Art == Raumweg.FreiArt.Dicht) sb.Append(" (no route and no opening to the outside: walls only)");
            else if (e.U0 >= 0)
            {
                sb.Append(" + detour ").Append(e.U0.ToString("F1", inv)).Append(" dB @").Append(Wege.F[0].ToString("0", inv)).Append(" Hz (assumption");
                if (e.Frei != null && e.Frei.Grund != null) sb.Append(": ").Append(e.Frei.Grund);
                sb.Append(')');
            }
            else sb.Append(" (no detour)");
            sb.Append(" -> ").Append(e.L0.ToString("F1", inv)).Append('/').Append(e.L1.ToString("F1", inv)).Append('/').Append(e.L2.ToString("F1", inv)).Append(" dB");
            if (e.A0 + e.A1 + e.A2 > 0.005)   // btrfern: Luft je Band (ISO 9613-1, Raid-Wetter), nur im gespielten Klang
                sb.Append(" + air ").Append(e.A0.ToString("F1", inv)).Append('/').Append(e.A1.ToString("F1", inv)).Append('/').Append(e.A2.ToString("F1", inv)).Append(" dB");
            // 0.8.6 Kennlinie: Wand im gespielten Mass (laute Quellarten gestaucht, sonst L0 x Lautheit)
            double vdb = QuellPegelKern.Gestaucht(e.Pegel, d, Math.Min(e.L0 + e.A0, Physik.Dynamik.Value),   // btrfern: mit Luft wie angewandt
                 Physik.Lautheit.Value, Physik.KennKnie.Value, Physik.KennSteigung.Value);   // 10.11.: wie angewandt (Deckel)
            if (e.HoerGilt)
            {
                // 0.8.6: Hoerbarkeit je Band gegen das Grundgeraeusch
                sb.Append(" | ").Append(QuellPegelKern.Name(e.Pegel)).Append(' ').Append(QuellPegelKern.DbA(e.Pegel).ToString("0", inv)).Append(" dB(A) @1 m vs background ")
                  .Append(e.HoerGrund.ToString("0", inv)).Append(" dB(A) ").Append(e.HoerDraussen ? "outdoors" : "indoors");
                sb.Append(" | audible 250/1k/4k:");
                bool eins = false;
                for (int i = 0; i < 3; i++)
                {
                    double u = e.HoerUeber[i]; eins |= u > 0;
                    sb.Append(i == 0 ? " " : " / ").Append(u > 0 ? (u < 10 ? "fading " : "yes ") : "no ").Append(u > 0 ? "+" : "").Append(u.ToString("F1", inv));
                }
                sb.Append(eins ? " dB above masking" : " dB above masking -> silent");
                sb.Append(" | volume ").Append((-e.HoerDb).ToString("F1", inv)).Append(" dB (walls x loudness ").Append(Physik.Lautheit.Value.ToString("0.##", inv))
                  .Append(" = ").Append((-vdb).ToString("F1", inv)).Append(" dB, masking ").Append((-(e.HoerDb - vdb)).ToString("F1", inv)).Append(" dB)")
                  .Append(", low-pass ").Append(e.Lp >= 20000f ? "open" : (e.Lp / 1000f).ToString("0.00", inv) + " kHz");
                if (e.Lp < e.LpOhne - 0.5f) sb.Append(" (walls alone ").Append(e.LpOhne >= 20000f ? "open" : (e.LpOhne / 1000f).ToString("0.00", inv) + " kHz").Append(')');
            }
            if (e.KalText != null) sb.Append(e.KalText);   // 0.8.6 Kalibrierung
            else
            sb.Append(" | volume ").Append((-vdb).ToString("F1", inv)).Append(" dB (x loudness ").Append(Physik.Lautheit.Value.ToString("0.##", inv)).Append(')')
              .Append(", low-pass ").Append(e.Lp >= 20000f ? "open" : (e.Lp / 1000f).ToString("0.00", inv) + " kHz");
            if (e.Gesperrt) sb.Append(" | NOT applied: ").Append(e.Grund);
            // 0.8.6: Richtung
            if (e.Richten)
            {
                if (e.Schieben < 0) sb.Append(" | direction: source (object not movable: ").Append(e.SchiebGrund).Append(')');
                else if (!e.Verschoben) sb.Append(" | direction: source (wall path dominates, room path ").Append((e.Anteil * 100).ToString("F0", inv)).Append(" %)");
                else sb.Append(" | direction: ").Append(e.Art == Raumweg.FreiArt.Ersatz ? "over the obstacle " : "from the opening ").Append(e.WinkelJetzt.ToString("F0", inv))
                       .Append(" deg off the source (target ").Append(e.WinkelZiel.ToString("F0", inv)).Append(" deg, room path ").Append((e.Anteil * 100).ToString("F0", inv))
                       .Append(" % of the energy, focus ").Append(e.Buendel.ToString("F2", inv)).Append(", weight ").Append(e.Ziel.ToString("F2", inv)).Append(')');
            }
            if (e.Frei != null && e.Frei.Text != null && (e.Art == Raumweg.FreiArt.Route || e.Art == Raumweg.FreiArt.Ersatz || e.Art == Raumweg.FreiArt.Dicht)) sb.Append(e.Frei.Text);
            if (e.Nah > 0 || e.EigenTreffer > 0) sb.Append(" | ignored: ").Append(e.Nah).Append(" near-source, ").Append(e.EigenTreffer).Append(" own object");
            if (e.BodenLinien > 0) sb.Append(" | ground contact: ").Append(e.BodenLinien).Append(" lines (").Append(e.BodenTreffer).Append(" ground hits, no wall - over the ground)");
            return sb.ToString();
        }

        static StringBuilder Pos(StringBuilder sb, Vector3 p, IFormatProvider inv)
            => sb.Append('(').Append(p.x.ToString("F1", inv)).Append(", ").Append(p.y.ToString("F1", inv)).Append(", ").Append(p.z.ToString("F1", inv)).Append(')');

        // =========================================================================================
        //  0.8.6 Diagnose bei der Marke (Ground Zero 09.11.2026 22:22:48 MARK 1, Kaan: "direkt neben dem Feuer kein Ton", 12 m weiter
        //  Ton). Die [Walls]-Zeilen zeigen nur, was die Rechnung meint (same room, 0 dB, Tiefpass offen) - nicht, was Unity mit der
        //  Quelle tut. Je eingetragene Quelle in der Naehe eine Zeile mit dem Ist-Zustand: echter Ort und Ort des Objekts, isPlaying,
        //  isVirtual (Stimmenlimit), enabled/aktiv, Wiedergabeposition, volume, priority, spatialize, Mischpult-Gruppe, Kurvenwert
        //  am Ort des Objekts, Filter Ziel/Ist; dazu Unitys Stimmenzahl. 0,5 s spaeter: laeuft die Wiedergabe weiter (Stream steht?).
        // =========================================================================================
        const float MarkeRadius = 40f;
        sealed class MarkeProbe { public AudioSource S; public string Kopf; public int Samples; public float Zeit; }
        static readonly List<MarkeProbe> markeProben = new List<MarkeProbe>(8);
        static float markeNach = -1f;
        static readonly List<MonoBehaviour> mbPuffer = new List<MonoBehaviour>(8);

        internal static void Marke(Vector3 h)
        {
            if (!KlangLog.An) return;
            try
            {
                var inv = CultureInfo.InvariantCulture;
                markeProben.Clear();
                int spielen = 0, virtuell = 0;
                foreach (var a in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                {
                    if (a == null || !a.isPlaying) continue;
                    spielen++; if (a.isVirtual) virtuell++;
                }
                var cfg = AudioSettings.GetConfiguration();
                var kopf = new StringBuilder(200);
                kopf.Append("[Walls state] Unity voices: ").Append(spielen).Append(" sources playing, ").Append(virtuell).Append(" of them virtual (muted by the voice limit: real ")
                    .Append(cfg.numRealVoices).Append(", virtual ").Append(cfg.numVirtualVoices).Append("); ").Append(liste.Count).Append(" wall-damping entries; listener ");
                Pos(kopf, h, inv);
                KlangLog.Text(kopf.ToString());
                foreach (var e in liste)
                {
                    if (e.Tot) continue;
                    Vector3 q = Echt(e);
                    if ((q - h).magnitude > MarkeRadius && (e.T == null || (e.T.position - h).magnitude > MarkeRadius)) continue;
                    foreach (var s in e.Quellen)
                    {
                        if (s == null) continue;
                        KlangLog.Text(Zustand(e, s, q, h, inv));
                        if (s.clip != null && markeProben.Count < 16)
                            markeProben.Add(new MarkeProbe { S = s, Kopf = e.Name + " #" + s.GetInstanceID(), Samples = s.timeSamples, Zeit = Time.realtimeSinceStartup });
                    }
                }
                markeNach = markeProben.Count > 0 ? Time.realtimeSinceStartup + 0.5f : -1f;
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        static string Zustand(Eintrag e, AudioSource s, Vector3 q, Vector3 h, IFormatProvider inv)
        {
            var sb = new StringBuilder(400);
            var t = s.transform;
            float dq = (q - h).magnitude, dt = (t.position - h).magnitude;
            sb.Append("[Walls state] ").Append(e.Name).Append(" #").Append(s.GetInstanceID()).Append(" '").Append(s.gameObject.name).Append("' | real ");
            Pos(sb, q, inv).Append(" d=").Append(dq.ToString("F1", inv)).Append(" m | object at ");
            Pos(sb, t.position, inv).Append(" d=").Append(dt.ToString("F1", inv)).Append(" m");
            sb.Append(e.Verschoben ? " (moved for the direction, " + e.WinkelJetzt.ToString("F0", inv) + " deg)" : (t.position - q).magnitude > 0.01f ? " (DIFFERS from the real place, not moved by us)" : " (at its real place)");
            sb.Append(" | playing ").Append(s.isPlaying ? "yes" : "NO").Append(", virtual ").Append(s.isVirtual ? "YES (voice limit)" : "no")
              .Append(", enabled ").Append(s.enabled ? "yes" : "NO").Append(", object active ").Append(s.gameObject.activeInHierarchy ? "yes" : "NO")
              .Append(", mute ").Append(s.mute ? "YES" : "no").Append(", paused-by-listener ").Append(AudioListener.pause ? "YES" : "no");
            var c = s.clip;
            if (c != null)
                sb.Append(" | clip ").Append(c.name).Append(" (").Append(c.loadType).Append(", ").Append(c.loadState).Append(") at ").Append(s.time.ToString("F2", inv))
                  .Append('/').Append(c.length.ToString("F2", inv)).Append(" s, loop ").Append(s.loop ? "yes" : "no");
            else sb.Append(" | no clip");
            sb.Append(" | volume ").Append(s.volume.ToString("0.###", inv)).Append(", priority ").Append(s.priority).Append(", spatialize ").Append(s.spatialize ? "yes" : "no")
              .Append(", blend ").Append(s.spatialBlend.ToString("0.##", inv)).Append(", spread ").Append(s.spread.ToString("0", inv))
              .Append(", bypass effects/listener/reverb ").Append(s.bypassEffects ? 1 : 0).Append('/').Append(s.bypassListenerEffects ? 1 : 0).Append('/').Append(s.bypassReverbZones ? 1 : 0);
            var g = s.outputAudioMixerGroup;
            sb.Append(", mixer ").Append(g != null ? "'" + g.name + "' (" + (g.audioMixer != null ? g.audioMixer.name : "-") + ")" : "-");
            // Kurve am Ort des Objekts (das rechnet Unity), nicht am echten Ort
            double kurve;
            switch (s.rolloffMode)
            {
                case AudioRolloffMode.Linear: kurve = LautSignalKern.GainLinear(dt, s.minDistance, s.maxDistance); break;
                case AudioRolloffMode.Logarithmic: kurve = LautSignalKern.GainLog(dt, s.minDistance, s.maxDistance); break;
                default:
                    var k = s.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
                    kurve = k == null || k.length == 0 ? double.NaN : k.Evaluate(s.maxDistance > 0 ? dt / s.maxDistance : 0f); break;
            }
            sb.Append(" | rolloff ").Append(s.rolloffMode).Append(" max ").Append(s.maxDistance.ToString("0.#", inv)).Append(" m: at ").Append(dt.ToString("F1", inv)).Append(" m ")
              .Append(double.IsNaN(kurve) ? "?" : kurve <= 1e-6 ? "silent" : (20 * Math.Log10(kurve)).ToString("F1", inv) + " dB");
            if (e.F == null) sb.Append(" | filter: none");
            else
            {
                var fk = e.F.K;
                sb.Append(" | filter: target ").Append((20 * Math.Log10(Math.Max(1e-6, fk.ZielGain))).ToString("F1", inv)).Append(" dB / ").Append((fk.ZielFc / 1000f).ToString("0.0", inv))
                  .Append(" kHz, now ").Append((20 * Math.Log10(Math.Max(1e-6, fk.AktuellGain))).ToString("F1", inv)).Append(" dB / ").Append((fk.AktuellFc / 1000f).ToString("0.0", inv))
                  .Append(" kHz, pass-through ").Append(fk.Durchgang ? "yes" : "no").Append(", component ").Append(e.F.enabled ? "on" : "OFF").Append(e.F.Weg ? " (being removed)" : "");
            }
            sb.Append(" | last measurement ").Append(e.Hat ? (Time.time - e.Zeit).ToString("F1", inv) + " s ago" : "none").Append(": ").Append(WandDaempfungKern.LageName(e.Lage))
              .Append(", gain ").Append((20 * Math.Log10(Math.Max(1e-6, e.Gain))).ToString("F1", inv)).Append(" dB");
            if (e.Gesperrt) sb.Append(", NOT applied: ").Append(e.Grund);
            // Skripte am Objekt und am Elternobjekt (wer koennte die Quelle steuern?)
            sb.Append(" | scripts:");
            int n = 0;
            for (var p = t; p != null && n < 2; p = p.parent, n++)
            {
                mbPuffer.Clear(); p.GetComponents(mbPuffer);
                sb.Append(n == 0 ? " object [" : " parent '" + p.name + "' [");
                for (int i = 0; i < mbPuffer.Count; i++) if (mbPuffer[i] != null) sb.Append(i > 0 ? ", " : "").Append(mbPuffer[i].GetType().Name).Append(mbPuffer[i].enabled ? "" : " (off)");
                sb.Append(']');
            }
            mbPuffer.Clear();
            return sb.ToString();
        }

        /// <summary>0,5 s nach der Marke: ist die Wiedergabe weitergelaufen? (aus Tick)</summary>
        static void MarkeNach()
        {
            if (markeNach < 0f || Time.realtimeSinceStartup < markeNach) return;
            markeNach = -1f;
            try
            {
                var inv = CultureInfo.InvariantCulture;
                foreach (var p in markeProben)
                {
                    var s = p.S;
                    if (s == null || s.clip == null) continue;
                    float dt = Time.realtimeSinceStartup - p.Zeit;
                    int ds = s.timeSamples - p.Samples;
                    if (ds < 0 && s.loop) ds += s.clip.samples;
                    double sek = s.clip.frequency > 0 ? ds / (double)s.clip.frequency : 0;
                    bool laeuft = sek > 0.3 * dt;
                    KlangLog.Text("[Walls state +" + dt.ToString("F2", inv) + " s] " + p.Kopf + ": playback " + (laeuft ? "advanced " : "STALLED - advanced only ") + sek.ToString("F2", inv)
                        + " s, playing " + (s.isPlaying ? "yes" : "NO") + ", virtual " + (s.isVirtual ? "YES" : "no") + ", enabled " + (s.enabled ? "yes" : "NO"));
                }
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
            finally { markeProben.Clear(); }
        }

        static string Name(int id)
        {
            try { return Resources.InstanceIDToObject(id) is Collider c ? c.gameObject.name : null; } catch { return null; }
        }

        internal static void Loggen()
        {
            if (liste.Count == 0 && dMess == 0) return;
            int amb = 0, roh = 0, fz = 0, skr = 0, filter = 0, gesperrt = 0;
            var gruende = new Dictionary<string, int>();
            foreach (var e in liste)
            {
                if (e.Tot) continue;
                switch (e.Weg) { case "ambient": amb++; break; case "raw scene": roh++; break; case "vehicle": fz++; break; default: skr++; break; }
                if (e.F != null) filter++;
                if (e.Grund != null) { gesperrt++; gruende.TryGetValue(e.Grund, out int g); gruende[e.Grund] = g + 1; }
            }
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(300);
            sb.Append("[Diagnostics] Wall damping: ").Append(liste.Count).Append(" sources (ambient ").Append(amb).Append(", raw scene ").Append(roh)
              .Append(", vehicle ").Append(fz).Append(", scripted ").Append(skr).Append("), ").Append(filter).Append(" with filter (").Append(dFilter).Append(" added, ")
              .Append(dEntfernt).Append(" released); ").Append(dMess).Append(" measurements (").Append(dSofort).Append(" one-shot): muffled ").Append(dGedaempft);
            if (dGedaempft > 0) sb.Append(" (avg ").Append((dSummeDb / dGedaempft).ToString("F1", inv)).Append(" dB, max ").Append(dMaxDb.ToString("F1", inv)).Append(" dB)");
            sb.Append(", clear ").Append(dFrei).Append(", no usable line ").Append(dOhne).Append(", ").Append(dBudget).Append("x budget");
            if (dHoer > 0) sb.Append("; audibility vs background: ").Append(dHoer).Append(" checked, ").Append(dHoerLeiser).Append(" quieter (avg ")
                .Append((dHoerLeiser > 0 ? dHoerSummeDb / dHoerLeiser : 0).ToString("F1", inv)).Append(" dB), ").Append(dHoerStumm).Append(" silent, ").Append(dHoerTief).Append(" only lows");
            if (gruende.Count > 0) { sb.Append("; not filtered:"); foreach (var kv in gruende) sb.Append(' ').Append(kv.Key).Append(' ').Append(kv.Value); }
            if (dGedreht + dZurueck + dUebernommen + dWinkelN > 0)
                sb.Append("; direction from the opening: ").Append(dGedreht).Append(" turned, ").Append(dZurueck).Append(" put back, ").Append(dUebernommen)
                  .Append(" moved by the game, angle avg ").Append((dWinkelN > 0 ? dWinkel / dWinkelN : 0).ToString("F0", inv)).Append(" deg / max ").Append(dWinkelMax.ToString("F0", inv)).Append(" deg");
            int nv = 0; foreach (var e in liste) if (!e.Tot && e.Schieben < 0) nv++;
            if (nv > 0) sb.Append(", ").Append(nv).Append(" objects not movable");
            dGedreht = dZurueck = dUebernommen = dWinkelN = 0; dWinkel = dWinkelMax = 0;
            if (!Zeit.Leer) sb.Append("; ").Append(Zeit.Text());
            if (!Wirksam) sb.Append(" (off)");
            Plugin.Log.LogInfo(sb.ToString());
            dMess = dSofort = dGedaempft = dFrei = dOhne = dBudget = dFilter = dEntfernt = 0; dSummeDb = dMaxDb = 0;
            dHoer = dHoerStumm = dHoerTief = dHoerLeiser = 0; dHoerSummeDb = 0;
        }
    }

    /// <summary>Lautstaerke + Tiefpass im Signalweg einer Quelle (Audiofaden). Parameter setzt WandDaempfung (Hauptfaden).</summary>
    internal sealed class WandFilter : MonoBehaviour
    {
        internal readonly WandFilterKern K = new WandFilterKern();
        internal bool Weg;   // Destroy angefordert (wirkt am Bildende)
        void OnAudioFilterRead(float[] data, int channels)
        {
            try { K.Verarbeiten(data, channels); } catch { }
        }
    }
}
