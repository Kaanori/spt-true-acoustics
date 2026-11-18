using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using EFT.Ballistics;
using HarmonyLib;
using Meta.XR.Acoustics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Materialklang
{
    // Feature 9: Hall je Material.
    // Das Spiel nutzt Meta-XR-Audio mit eingebackenen Raumdaten (.xrageo), in denen ALLE Materialien
    // gleich sind (Absorption 0.1). Der Hall kennt also keine Materialien. Wir messen darum beim
    // Raidstart je Innenraum, woraus die Waende bestehen (Strahlen + BallisticCollider), rechnen mit
    // Eyring (tankhall; bis 0.8.8 Sabine) die Abweichung zu 0.1 aus und legen pro Raum eine offizielle Meta-"Control Zone" an,
    // die RT60 (Sekunden, additiv) und Hallpegel (dB, additiv) im Raum verschiebt.
    internal static partial class HallZonen
    {
        // Baender der Control Zone (Hz).
        static readonly float[] Baender = { 125f, 250f, 500f, 1000f, 2000f, 4000f };
        const int Band1k = 3;
        const float GebackenAlpha = 0.1f;
        const int Strahlen = 64;
        const int MinTreffer = 12;          // je Messpunkt (von 64 Strahlen); darunter ist der Raum zu offen fuer eine Aussage
        const float MinVolumen = 6f;        // m3
        // tankhall: keine Obergrenze mehr (bis hier 40000 m3 je Box). Mit der Vereinigung aller Boxen sind die grossen Hallen der Factory
        // (Area_1 43000, Area_3 67000 m3) sonst verworfen; Luftabsorption (4mV) und Eyring gelten auch dort.
        const int MaxMesspunkte = 4;        // Strahlen aus den Mitten der (groessten) Boxen eines Raums, hoechstens 4 (Raidstart-Zeit)

        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<float> Staerke, AlphaMin;
        internal static ConfigEntry<int> MaxZonen, MaxZonenAlt;
        internal static ConfigEntry<bool> PegelAusZeit;
        internal static ConfigEntry<bool> NurGroessteBox;
        internal static ConfigEntry<bool> Diagnose;

        // Absorptionsgrad je Materialname und Frequenz, Paare (Hz, alpha). Quelle: BSGs ungenutzte Zuordnung
        // (Spieldaten, Herkunft von BSG nicht dokumentiert; mehrere Zeilen gleichen klassischen Lehrbuchtabellen)
        // (26 Materialien, Namen = EFT-MaterialType). Alles Unbekannte faellt auf Stone zurueck.
        static readonly Dictionary<string, float[]> Tabelle = new Dictionary<string, float[]>
        {
            ["Asphalt"] = new float[] { 125f, 0.02f, 250f, 0.03f, 500f, 0.05f, 1000f, 0.06f, 2000f, 0.07f, 4000f, 0.08f },
            ["Cardboard"] = new float[] { 400f, 0.41f, 500f, 0.607f, 630f, 0.773f, 800f, 0.669f, 1000f, 0.685f, 1250f, 0.806f, 1600f, 0.579f, 2000f, 0.792f },
            ["Concrete"] = new float[] { 125f, 0.01f, 250f, 0.01f, 500f, 0.02f, 1000f, 0.02f, 2000f, 0.02f, 4000f, 0.02f },
            ["Fabric"] = new float[] { 125f, 0.07f, 250f, 0.31f, 500f, 0.49f, 1000f, 0.75f, 2000f, 0.7f, 4000f, 0.6f },
            ["GarbageMetal"] = new float[] { 1000f, 1.0f },
            ["Glass"] = new float[] { 125f, 0.35f, 250f, 0.25f, 500f, 0.18f, 1000f, 0.12f, 2000f, 0.07f, 4000f, 0.05f },
            ["GrassHigh"] = new float[] { 125f, 0.11f, 250f, 0.26f, 500f, 0.6f, 1000f, 0.69f, 2000f, 0.92f, 4000f, 0.99f },
            ["GrassLow"] = new float[] { 125f, 0.11f, 250f, 0.26f, 500f, 0.6f, 1000f, 0.69f, 2000f, 0.92f, 4000f, 0.99f },
            ["Grate"] = new float[] { 1000f, 1.0f },
            ["Gravel"] = new float[] { 125f, 0.25f, 250f, 0.6f, 500f, 0.65f, 1000f, 0.7f, 2000f, 0.75f, 4000f, 0.8f },
            ["MetalNoDecal"] = new float[] { 125f, 0.05f, 250f, 0.1f, 500f, 0.1f, 1000f, 0.1f, 2000f, 0.07f, 4000f, 0.02f },
            // diffus-hall (10.11., Entscheidung des Besitzers "ja, mach das mit Stahl"): bis hier hatten MetalThick/MetalThin die BSG-Zeile
            // 0,05/0,10/0,10/0,10/0,07/0,02 = in den Mitten genau der Bake-Wert 0,1 -> ein Stahltank hallte in den Mitten nicht laenger
            // als ein beliebiger Raum (Boiler_spawn_room, 100 % MetalThick: T 1 kHz 1,21 s = gebacken). Getrennt nach dem, was die Mod
            // unterscheiden kann:
            //  - Metal thick (Tanks, Zisternen, Tore, Faesser, dickes Blech): schallharte glatte Flaeche wie Stahl, polierter Stein,
            //    gestrichener Beton. Tabellenwerke geben dafuer 0,01-0,02 von 250 Hz bis 4 kHz (Kuttruff, Room Acoustics, 6. Aufl. 2017,
            //    Tabelle der Absorptionsgrade "hard surfaces"; Vorlaender, Auralization, Anhang Materialdaten; DIN 18041:2016 Tab. B.1
            //    Hartflaechen). Nur Spannen -> Mitte 0,015 (500 Hz/1 kHz), 0,02 an den Raendern; bei 125 Hz etwas hoeher (Rest-
            //    Plattenschwingung, Fugen): 0,04 (Annahme innerhalb der Spannen 0,02-0,05 harter Flaechen bei 125 Hz).
            //  - Metal thin (Trapezblech, Container, Karosserie): Plattenschwinger, Tiefenabsorption 0,10-0,25 bei 125-250 Hz (Resonanz
            //    duenner Bleche mit Luftpolster, Kuttruff Kap. 6 "resonance absorbers"; Herstellertabellen Trapezblech), fallend auf
            //    0,03-0,05 ab 1 kHz -> 125 Hz 0,20, 250 Hz 0,15 (Mitte/oberes Drittel der Spanne, tiefere Frequenz naeher an der Resonanz),
            //    500 Hz 0,08 (Uebergang), 1-4 kHz 0,04 (Mitte).
            // Folgen (Rechnung review-20261110/diffus-hall/stahl.py mit den Raumdaten der Sonde, wie HallZonen): Tank 1 kHz 1,21 -> 2,71 s
            // (roh 3,40 s, am Deckel +1,5 s); Container_1 1 kHz 0,26 -> 0,33 s, 125 Hz 0,34 -> 0,20 s; Small_Metal_Room_1 0,52 -> 0,75 s.
            ["MetalThick"] = new float[] { 125f, 0.04f, 250f, 0.02f, 500f, 0.015f, 1000f, 0.015f, 2000f, 0.02f, 4000f, 0.02f },
            ["MetalThin"] = new float[] { 125f, 0.20f, 250f, 0.15f, 500f, 0.08f, 1000f, 0.04f, 2000f, 0.04f, 4000f, 0.04f },
            ["Mud"] = new float[] { 125f, 0.15f, 250f, 0.25f, 500f, 0.3f, 1000f, 0.25f, 2000f, 0.2f, 4000f, 0.15f },
            ["Pebbles"] = new float[] { 125f, 0.25f, 250f, 0.6f, 500f, 0.65f, 1000f, 0.7f, 2000f, 0.75f, 4000f, 0.8f },
            ["Plastic"] = new float[] { 125f, 0.12f, 250f, 0.09f, 500f, 0.07f, 1000f, 0.05f, 2000f, 0.05f, 4000f, 0.04f },
            ["Rubber"] = new float[] { 125f, 0.05f, 250f, 0.05f, 500f, 0.1f, 1000f, 0.1f, 2000f, 0.05f, 4000f, 0.05f },
            ["Soil"] = new float[] { 125f, 0.15f, 250f, 0.25f, 500f, 0.4f, 1000f, 0.55f, 2000f, 0.6f, 4000f, 0.6f },
            ["SoilForest"] = new float[] { 125f, 0.15f, 250f, 0.25f, 500f, 0.4f, 1000f, 0.55f, 2000f, 0.6f, 4000f, 0.6f },
            ["Stone"] = new float[] { 125f, 0.02f, 500f, 0.02f, 2000f, 0.05f, 4000f, 0.05f },
            ["Swamp"] = new float[] { 125f, 0.01f, 250f, 0.01f, 500f, 0.01f, 1000f, 0.02f, 2000f, 0.02f, 4000f, 0.03f },
            ["Tile"] = new float[] { 125f, 0.01f, 250f, 0.01f, 500f, 0.01f, 1000f, 0.01f, 2000f, 0.02f, 4000f, 0.02f },
            ["Water"] = new float[] { 125f, 0.01f, 250f, 0.01f, 500f, 0.01f, 1000f, 0.02f, 2000f, 0.02f, 4000f, 0.03f },
            ["WaterPuddle"] = new float[] { 125f, 0.01f, 250f, 0.01f, 500f, 0.01f, 1000f, 0.02f, 2000f, 0.02f, 4000f, 0.03f },
            ["WoodThick"] = new float[] { 125f, 0.19f, 250f, 0.14f, 500f, 0.09f, 1000f, 0.06f, 2000f, 0.06f, 4000f, 0.05f },
            ["WoodThin"] = new float[] { 125f, 0.42f, 250f, 0.21f, 500f, 0.1f, 1000f, 0.08f, 2000f, 0.06f, 4000f, 0.06f },
        };

        // MaterialType-Namen ohne eigene Zeile -> naechstes Tabellenmaterial.
        static readonly Dictionary<string, string> Aliase = new Dictionary<string, string>
        {
            ["GlassVisor"] = "Glass", ["GlassShattered"] = "Glass", ["GarbagePaper"] = "Cardboard",
            ["Tyre"] = "Rubber", ["GenericSoft"] = "Fabric", ["GenericHard"] = "Concrete",
        };

        sealed class Box
        {
            public Vector3 Mitte, Halb;
            public Quaternion Drehung;
        }

        sealed class RaumDaten
        {
            public string Name, Dominant;
            public Box Box;                       // tankhall: Huelle der Vereinigung im Achsensystem der groessten Box (Zone, Wandseite)
            public Kasten[] Kaesten;              // tankhall: alle Boxen des Raums (Raum = ihre Vereinigung)
            public float Volumen, VzuS;           // Volumen der Vereinigung (m3), V/S
            public float Oberflaeche, SOffen;     // tankhall: Oberflaeche der Vereinigung, offene Portalflaeche beim Messen (m2)
            public SpatialAudioRoom Raum;
            public int Treffer;
            public bool Angelegt;                 // fuer das Klang-Log: es gibt eine Zone fuer diesen Raum
            public float DRt1k, DPegel1k;         // Verschiebung bei 1 kHz (s, dB) laut letzter Berechnung
            public Quaternion Inv;
            public float[] Alpha = new float[Baender.Length];
            // hallabsorption (17.11.): Messung ueber Zufallssehnen (HallZonenSehnen.cs, SehnenKern): aequivalenter Wandwert der Huelle je Band,
            // dazu wirksame Flaeche, offener Anteil, Luftvolumen, Materialwert der echten Flaechen; HatSehnen = Messung liegt vor
            public float[] AlphaSehnen = new float[Baender.Length], AlphaMat = new float[Baender.Length];
            public bool HatSehnen;
            public float SWirk, FOffen, VLuft;
            public int SehnenN, SehnenInnen;
            public ulong Pruef;
            public int GruppeOben = -1, GruppeUnten = -1;   // 0.8.4 Stufe 1: haeufigste Materialgruppe der Decke / des Bodens (Flankenwege)
            // hallzone: Zonen nur ueber dem eigenen Raum (HallZonenKern.Zerlegen + Einruecken), einmal je Raid aus der Geometrie
            public List<Kasten> Zonen;
            public double Fade;
            public bool Gitter;
        }

        /// <summary>0.8.4 Stufe 1 (Empfangsraum, Flankenwege): Daten des Raums, in dem ein Punkt steht.</summary>
        internal struct RaumInfo
        {
            public float Oberflaeche, Volumen;        // Boxoberflaeche (m2), Volumen (m3)
            public float WandFlaeche;                 // Flaeche der Boxseite Richtung Quelle (m2)
            public float WandBreite, WandHoehe;       // Kantenlaengen dieser Seite (waagerecht / senkrecht, m)
            public float Alpha0, Alpha1, Alpha2;      // mittlerer Absorptionsgrad bei Bezugsfrequenz / 1 kHz / 4 kHz, mit Oeffnungen (alpha 1); A = Oberflaeche * Alpha
            public bool QuelleDrin;                   // die Quelle steht in derselben Box (kein Raum dahinter)
            public int GruppeOben, GruppeUnten;
            public string Name;
        }

        static Vector3[] richtungen;
        static List<SpatialAudioRoom> raeume;       // Raeume des laufenden Raids (aus dem Raumspeicher)
        static List<RaumDaten> daten;               // Messergebnis (einmal je Raid)
        static GameObject wurzel;
        static int zonenAktiv;
        static bool anstehend;
        static float fruehestens;

        public static int ZonenAnzahl => zonenAktiv;

        public static void Binden(ConfigFile cfg)
        {
            const string sek = "9. Reverb by Material";
            Aktiv = cfg.Bind(sek, "Enabled", true,
                "Reverb by material on/off. At raid start it measures the wall materials of every interior room and shifts reverb time and reverb level in the room (Meta Control Zones). Off = reverb as in the base game. Takes effect immediately.");
            Staerke = cfg.Bind(sek, "Strength", 1.0f,
                new ConfigDescription("Multiplies the calculated shift of reverb time and reverb level (0 = none, 1 = as calculated, 2 = double).", new AcceptableValueRange<float>(0f, 2f)));
            // hallzone (12.11.): ein Raum bekommt jetzt eine Zone je Teilbox (HallZonenKern), nicht mehr eine Huellbox. Der alte
            // Schluessel zaehlte Raeume und steht bei bestehenden cfg-Dateien auf 400 - darum ein neuer Schluessel. Meta begrenzt die Zahl
            // nicht (ovra::ovrAudio_SceneAddControlZone haengt an eine wachsende Liste an, Dekompilat review-20261110/hallzone/ghidra);
            // was viele Zonen je Hall-Berechnung kosten, ist nicht gemessen. Offline (review-20261110/hallzone/zaehlen): Factory 710 Zonen
            // (114 Raeume), Reserve hoechstens 1806 (514 Raeume) -> Standard 2000, damit auf allen bekannten Karten kein Raum leer ausgeht.
            MaxZonenAlt = cfg.Bind(sek, "Max zones", 400,
                new ConfigDescription("No longer used (since 0.8.8): see 'Max zone boxes'.", new AcceptableValueRange<int>(0, 2000)));
            MaxZonen = cfg.Bind(sek, "Max zone boxes", 2000,
                new ConfigDescription("Maximum number of reverb zones per raid. A room gets one zone per part of its shape (only its own volume, no zone reaches into another room). If the limit is reached, the rooms whose reverb differs most from the base game (times their volume) come first; the log says how many rooms got none. Takes effect immediately.", new AcceptableValueRange<int>(0, 4000)));
            PegelAusZeit = cfg.Bind(sek, "Reverb level follows reverb time", true,
                "On: a room with a longer reverb time also gets a louder reverb (diffuse field level ~ reverb time: +10*lg(T_new/T_base) dB, from the reverb time actually set, i.e. after the cap). Off: only the reverb time is shifted - use this if the game's reverb already gets louder by itself with a longer reverb time (not measured yet). Takes effect immediately.");
            // tankhall: beide Schluessel bleiben (cfg-Kompatibilitaet), wirken aber nicht mehr. Ein Raum ist die Vereinigung ALLER seiner
            // Boxen (eine Zone ueber seine Huelle); die Oeffnungen zaehlen mit ihrer echten Flaeche (alpha 1) statt einer Untergrenze.
            NurGroessteBox = cfg.Bind(sek, "Only largest box per room", true,
                "No longer used (since 0.8.8): a room is the union of all its boxes and gets one zone over its hull.");
            AlphaMin = cfg.Bind(sek, "Min. absorption", 0.08f,
                new ConfigDescription("No longer used (since 0.8.8): open portals of the room now count with their real area (absorption 1), furniture and clutter are seen by the rays; a floor would make hard rooms (steel tanks, bare concrete) too soft.", new AcceptableValueRange<float>(0.01f, 0.1f)));
            SehnenBinden(cfg, sek);   // hallabsorption (17.11.)
            Diagnose = cfg.Bind(sek, "Diagnostics", true,
                "Writes to the BepInEx log at raid start: zone count, measuring time and the 5 most extreme rooms (name, main material, change of reverb time at 1 kHz).");

            // Gesamtreview C3: Live-Aenderungen sammeln (0,3 s), statt bei jedem Schritt eines Schiebereglers bis 400 Zonen neu zu bauen
            const float Sammeln = 0.3f;
            EventHandler schalter = (_, __) =>
            {
                if (!HallAn) ZonenZerstoeren();
                else if (daten != null || raeume != null) Anstehend(Sammeln);
            };
            Aktiv.SettingChanged += schalter;
            if (Plugin.Aktiv != null) Plugin.Aktiv.SettingChanged += schalter;   // Hauptschalter aus = Vanilla (keine Zonen)
            Staerke.SettingChanged += (_, __) => { if (HallAn && daten != null) Anstehend(Sammeln); };
            MaxZonen.SettingChanged += (_, __) => { if (HallAn && daten != null) Anstehend(Sammeln); };
            PegelAusZeit.SettingChanged += (_, __) => { if (HallAn && daten != null) Anstehend(Sammeln); };
        }

        /// <summary>Klang-Log (hallzone, Befund A4): ALLE angelegten Zonen, deren Native-Box den Punkt enthaelt, mit Gewicht nach der
        /// Fade-Annahme (HallZonenKern.Gewicht). Name = Raum (bei mehreren "A 60%+B 40%"), rt/db = Summe Gewicht x Verschiebung bei
        /// 1 kHz. Da sich die Zonen nicht ueberlappen, liegt ein Punkt in genau einer Zone (Gewicht 1) oder im Fade-Band zweier
        /// Zonen; nur dort haengt der wirksame Wert von Metas (unbekannter) Verrechnungsregel ab - die Summe ist dann eine Annahme.</summary>
        internal static bool Finde(Vector3 p, out string name, out float rt, out float db)
        {
            name = null; rt = db = 0f;
            if (zonenAktiv == 0 || angelegt.Count == 0) return false;
            int n = 0;
            for (int i = 0; i < angelegt.Count; i++)
            {
                var z = angelegt[i];
                float dx = p.x - (float)z.K.Mx, dy = p.y - (float)z.K.My, dz = p.z - (float)z.K.Mz;
                if (dx * dx + dy * dy + dz * dz > z.R2) continue;
                double w = HallZonenKern.Gewicht(z.K, z.Fade, p.x, p.y, p.z);
                if (w <= 0) continue;
                int k = 0; while (k < n && !ReferenceEquals(findeRaum[k], z.D)) k++;
                if (k == n) { if (n == findeRaum.Length) continue; findeRaum[n] = z.D; findeW[n] = 0; n++; }
                findeW[k] += w;
            }
            if (n == 0) return false;
            double srt = 0, sdb = 0;
            for (int k = 0; k < n; k++) { srt += findeW[k] * findeRaum[k].DRt1k; sdb += findeW[k] * findeRaum[k].DPegel1k; }
            if (n == 1 && findeW[0] > 0.995) name = findeRaum[0].Name;
            else
            {
                var sb = new System.Text.StringBuilder();
                for (int k = 0; k < n; k++) { if (k > 0) sb.Append('+'); sb.Append(findeRaum[k].Name).Append(' ').Append(Mathf.RoundToInt((float)Math.Min(1, findeW[k]) * 100)).Append('%'); }
                name = sb.ToString();
            }
            rt = (float)srt; db = (float)sdb;
            return true;
        }
        static readonly RaumDaten[] findeRaum = new RaumDaten[8];
        static readonly double[] findeW = new double[8];

        /// <summary>hallzone: eine angelegte Meta-Zone (Teilbox eines Raums, schon eingerueckt).</summary>
        sealed class ZonenEintrag { public RaumDaten D; public Kasten K; public double Fade; public float R2; }
        static readonly List<ZonenEintrag> angelegt = new List<ZonenEintrag>();

        /// <summary>tankhall: liegt p in der Vereinigung der Boxen des Raums? (Huelle als schneller Vortest)</summary>
        static bool ImRaum(RaumDaten d, Vector3 p)
        {
            Vector3 v = p - d.Box.Mitte;
            float r = d.Box.Halb.magnitude;
            if (v.sqrMagnitude > r * r) return false;
            return RaumGeometrieKern.DrinVereinigung(d.Kaesten, d.Kaesten.Length, p.x, p.y, p.z);
        }

        /// <summary>Hallzonen anlegen (Abschnitt 9 und Hauptschalter an).</summary>
        static bool HallAn => Aktiv.Value && (Plugin.Aktiv == null || Plugin.Aktiv.Value);

        static void Anstehend(float verzoegerung)
        {
            anstehend = true;
            fruehestens = Time.realtimeSinceStartup + verzoegerung;
        }

        // Raidstart: Raumspeicher steht, Raeume sind initialisiert.
        internal static void RaeumeMelden(List<SpatialAudioRoom> liste)
        {
            ZonenZerstoeren();
            daten = null;
            jeRaum.Clear();
            raeume = liste != null ? new List<SpatialAudioRoom>(liste) : null;
            SehnenLeeren();   // hallabsorption
            if (raeume != null && raeume.Count > 0) Anstehend(1.5f);
        }

        // Raidende / Szenenwechsel.
        public static void Leeren()
        {
            alphaNamen.Clear();
            jeRaum.Clear();
            SehnenLeeren();   // hallabsorption
            ZonenZerstoeren();
            daten = null;
            raeume = null;
            anstehend = false;
        }

        // Szenenwechsel waehrend das Raumsystem noch lebt (Reihenfolge der Ereignisse unsicher): nichts tun.
        public static void SzeneGewechselt()
        {
            if (!SpatialAudioSystem.Initialized) Leeren();
        }

        // Aus Plugin.Update: fuehrt das Anlegen aus, sobald das Raumsystem fertig ist.
        public static void Tick()
        {
            SehnenSchritt();   // hallabsorption (17.11.): Sehnenmessung ueber die Bilder verteilt (HallZonenSehnen.cs)
            // Gesamtreview C2: gemessen wird auch ohne Hallzonen, wenn Abschnitt 14 die Raeume braucht (Empfangsraum, Flanken)
            if (!anstehend || (!HallAn && !Wege.RaumKorrektur && !Wege.FlankeKorrektur)) return;
            if (!SpatialAudioSystem.Initialized || Time.realtimeSinceStartup < fruehestens) return;
            anstehend = false;
            var sw = Stopwatch.StartNew();
            try
            {
                bool gemessen = false;
                if (daten == null)
                {
                    daten = Messen();
                    gemessen = true;
                }
                double msMessen = sw.Elapsed.TotalMilliseconds;
                ZonenZerstoeren();
                if (HallAn) Erzeugen(msMessen, gemessen, sw);
                else if (Diagnose.Value && gemessen) Plugin.Log.LogInfo($"[Reverb] {daten.Count} rooms measured for sound paths (14.) in {msMessen:F1} ms; reverb zones off.");
            }
            catch (Exception ex)
            {
                Statistik.Fehler(ex);
            }
        }

        // ---------------------------------------------------------------- Messen

        static List<RaumDaten> Messen()
        {
            var ergebnis = new List<RaumDaten>();
            if (raeume == null) return ergebnis;
            if (richtungen == null) richtungen = Fibonacci(Strahlen);
            var typCache = new Dictionary<int, string>(256);
            int maske = LayersMaskController.HighPolyWithTerrainMask;
            var zaehler = new Dictionary<string, int>();
            var summe = new float[Baender.Length];

            jeRaum.Clear();
            foreach (var raum in raeume)
            {
                if (raum == null || !Innenraum(raum) || raum.Colliders == null) continue;
                // tankhall: alle Boxen des Raums (Vereinigung) statt nur der groessten - siehe RaumGeometrieKern (Tank der Factory)
                var ks = Kaesten(raum);
                if (ks == null) continue;
                string name = !string.IsNullOrEmpty(raum.Name) ? raum.Name : raum.gameObject.name;
                var d = MessenRaum(name, raum, ks, maske, typCache, zaehler, summe);
                if (d != null) { ergebnis.Add(d); jeRaum[raum] = d; }
            }
            return ergebnis;
        }

        static readonly Dictionary<SpatialAudioRoom, RaumDaten> jeRaum = new Dictionary<SpatialAudioRoom, RaumDaten>();

        /// <summary>tankhall: Boxen (BoxCollider der AudioTriggerAreas) eines Raums als Kasten; null = keine.</summary>
        internal static Kasten[] KaestenVon(SpatialAudioRoom raum) => Kaesten(raum);   // raumecke
        static Kasten[] Kaesten(SpatialAudioRoom raum)
        {
            var cols = raum != null ? raum.Colliders : null;
            if (cols == null) return null;
            var l = new List<Kasten>(cols.Length);
            foreach (var c in cols)
            {
                if (c == null || !c.enabled) continue;
                var b = AusCollider(c);
                Vector3 u = b.Drehung * Vector3.right, v = b.Drehung * Vector3.up, w = b.Drehung * Vector3.forward;
                l.Add(new Kasten { Mx = b.Mitte.x, My = b.Mitte.y, Mz = b.Mitte.z, Ux = u.x, Uy = u.y, Uz = u.z, Vx = v.x, Vy = v.y, Vz = v.z,
                                   Wx = w.x, Wy = w.y, Wz = w.z, Hx = b.Halb.x, Hy = b.Halb.y, Hz = b.Halb.z });
            }
            return l.Count > 0 ? l.ToArray() : null;
        }

        /// <summary>tankhall: Oberflaeche (m2) und Volumen (m3) der Vereinigung der Boxen eines Raums - fuer den Raumweg
        /// (Raumweg.Netz.Messen), damit Hall, Wandweg und Raumweg denselben Raum rechnen. false = keine Boxen.</summary>
        internal static bool Vereinigung(SpatialAudioRoom raum, out double flaeche, out double vol)
        {
            flaeche = vol = 0;
            if (raum != null && jeRaum.TryGetValue(raum, out var d)) { flaeche = d.Oberflaeche; vol = d.Volumen; return true; }
            var ks = Kaesten(raum);
            if (ks == null) return false;
            flaeche = RaumGeometrieKern.Oberflaeche(ks, ks.Length);
            vol = RaumGeometrieKern.Volumen(ks, ks.Length);
            return flaeche > 0;
        }

        /// <summary>tankhall: gemessener mittlerer Absorptionsgrad der Waende (ohne Oeffnungen) bei hz; NaN = Raum nicht gemessen.</summary>
        internal static double WandAlpha(SpatialAudioRoom raum, float hz)
        {
            if (raum == null || daten == null || !jeRaum.TryGetValue(raum, out var d)) return double.NaN;
            return BandAlpha(d, hz);
        }

        /// <summary>tankhall: offene Flaeche (m2) der Portale des Raums beim Messen (Zustand Open, Groesse wie Raumweg: GetPortalSize x*y).
        /// ANNAHME: Die Zone wird einmal je Raid gelegt; Tueren, die erst spaeter auf- oder zugehen, aendern sie nicht (der Raumweg
        /// rechnet die offenen Flaechen dagegen live).</summary>
        static float OffeneFlaeche(SpatialAudioRoom raum)
        {
            float s = 0;
            var gesehen = new HashSet<BaseSpatialAudioPortal>();
            if (raum.roomConnections == null) return 0;
            foreach (var rc in raum.roomConnections)
            {
                if (rc == null || rc.connectingPortals == null) continue;
                foreach (var p in rc.connectingPortals)
                {
                    if (p == null || !gesehen.Add(p) || p.state != BaseSpatialAudioPortal.PortalState.Open) continue;
                    try { var g = p.GetPortalSize(); s += Mathf.Abs(g.x * g.y); } catch { }
                }
            }
            return s;
        }

        static bool Innenraum(SpatialAudioRoom r)
        {
            if (r.Outdoor) return false;
            var t = r.Type;
            if ((t & EAudioRoomTypeMask.Indoor) == 0) return false;
            if ((t & (EAudioRoomTypeMask.Outdoor | EAudioRoomTypeMask.IndoorVehicle)) != 0) return false;
            return true;
        }

        static Box AusCollider(BoxCollider c)
        {
            var tr = c.transform;
            var s = tr.lossyScale;
            return new Box
            {
                Mitte = tr.TransformPoint(c.center),
                Halb = new Vector3(Mathf.Abs(c.size.x * s.x), Mathf.Abs(c.size.y * s.y), Mathf.Abs(c.size.z * s.z)) * 0.5f,
                Drehung = tr.rotation,
            };
        }

        static RaumDaten MessenRaum(string name, SpatialAudioRoom raum, Kasten[] ks, int maske, Dictionary<int, string> typCache,
                                    Dictionary<string, int> zaehler, float[] summe)
        {
            int n = ks.Length;
            float vol = (float)RaumGeometrieKern.Volumen(ks, n);
            if (vol < MinVolumen) return null;
            float flaeche = (float)RaumGeometrieKern.Oberflaeche(ks, n);
            if (flaeche <= 0f) return null;
            int gross = 0;
            for (int i = 1; i < n; i++) if (ks[i].Volumen > ks[gross].Volumen) gross = i;
            var h = RaumGeometrieKern.HuelleIn(ks, n, gross);
            var box = new Box
            {
                Mitte = new Vector3((float)h.Mx, (float)h.My, (float)h.Mz), Halb = new Vector3((float)h.Hx, (float)h.Hy, (float)h.Hz),
                Drehung = Quaternion.LookRotation(new Vector3((float)h.Wx, (float)h.Wy, (float)h.Wz), new Vector3((float)h.Vx, (float)h.Vy, (float)h.Vz)),
            };

            // Messpunkte: Mitten der groessten Boxen (gleiche Mitten - z. B. die 10 Scheiben des Tanks - zaehlen einmal)
            var mitten = new List<Vector3>(MaxMesspunkte);
            foreach (int i in Enumerable.Range(0, n).OrderByDescending(i => ks[i].Volumen))
            {
                var m = new Vector3((float)ks[i].Mx, (float)ks[i].My, (float)ks[i].Mz);
                bool doppelt = false;
                foreach (var x in mitten) if ((x - m).sqrMagnitude < 0.25f) { doppelt = true; break; }
                if (!doppelt) mitten.Add(m);
                if (mitten.Count >= MaxMesspunkte) break;
            }

            zaehler.Clear();
            Array.Clear(summe, 0, summe.Length);
            int treffer = 0;
            var inv = Quaternion.Inverse(box.Drehung);

            Array.Clear(obenZ, 0, obenZ.Length); Array.Clear(untenZ, 0, untenZ.Length);
            foreach (var mitte in mitten)
                for (int i = 0; i < richtungen.Length; i++)
                {
                    Vector3 dir = richtungen[i];
                    // tankhall: Strecke bis zum Rand der VEREINIGUNG + etwas Spiel (Waende liegen oft knapp ausserhalb der Boxen).
                    float t = (float)RaumGeometrieKern.Austritt(ks, n, mitte.x, mitte.y, mitte.z, dir.x, dir.y, dir.z) + 0.6f;

                    RaycastHit hit;
                    if (!Physics.Raycast(mitte, dir, out hit, t, maske, QueryTriggerInteraction.Ignore)) continue;
                    string mat = Materialname(hit.collider, typCache);
                    treffer++;
                    zaehler[mat] = zaehler.TryGetValue(mat, out int c) ? c + 1 : 1;
                    for (int b = 0; b < Baender.Length; b++) summe[b] += Alpha(mat, Baender[b]);
                    Vector3 lokal = inv * dir;
                    if (lokal.y > 0.7f || lokal.y < -0.7f)
                    {
                        int g = hit.collider != null ? MaterialCache.Bestimmen(hit.collider, false) : (int)Gruppe.Unknown;
                        if (g >= 0 && g < obenZ.Length) { if (lokal.y > 0) obenZ[g]++; else untenZ[g]++; }
                    }
                }
            if (treffer < MinTreffer * mitten.Count) return null;

            var d = new RaumDaten
            {
                Name = name, Box = box, Kaesten = ks, Raum = raum, Volumen = vol, Oberflaeche = flaeche, VzuS = vol / flaeche, Treffer = treffer,
                SOffen = Mathf.Min(OffeneFlaeche(raum), flaeche),
                Dominant = zaehler.OrderByDescending(p => p.Value).First().Key,
            };
            for (int b = 0; b < Baender.Length; b++) d.Alpha[b] = Mathf.Max(0.005f, summe[b] / treffer);
            d.GruppeOben = Haeufigste(obenZ); d.GruppeUnten = Haeufigste(untenZ);
            return d;
        }

        static readonly int[] obenZ = new int[Plugin.AnzahlGruppen], untenZ = new int[Plugin.AnzahlGruppen];
        static int Haeufigste(int[] z)
        {
            int best = -1, n = 0;
            for (int g = 0; g < z.Length; g++) if (z[g] > n) { n = z[g]; best = g; }
            return best;
        }

        // ---------------------------------------------------------------- 0.8.4 Stufe 1: Daten fuer die Wegrechnung

        static readonly Dictionary<int, string> alphaNamen = new Dictionary<int, string>(512);

        /// <summary>Absorptionsgrad (BSG-Tabelle oben) des Colliders bei hz - fuer Reflexionstreffer (Wege.cs).</summary>
        internal static float AlphaCollider(int colliderId, float hz)
        {
            if (!alphaNamen.TryGetValue(colliderId, out string mat))
            {
                Collider col = null;
                try { col = Resources.InstanceIDToObject(colliderId) as Collider; } catch { }
                mat = Materialname(col, null);
                if (alphaNamen.Count > 20000) alphaNamen.Clear();
                alphaNamen[colliderId] = mat;
            }
            return Alpha(mat, hz);
        }

        /// <summary>Raum (gemessene Box, kleinste zuerst), in dem 'hoerer' steht; Wandseite Richtung 'quelle'.
        /// Gibt es keine Messung (Abschnitt 9 aus, noch nicht gemessen, draussen) -> false.</summary>
        internal static bool RaumFuer(Vector3 hoerer, Vector3 quelle, float f0, out RaumInfo info)
        {
            info = default;
            var liste = daten;
            if (liste == null)
            {
                // Gesamtreview C2: Raeume noch nicht gemessen (Hall aus beim Raidstart) -> Messung anfordern
                if (raeume != null && !anstehend) Anstehend(0f);
                return false;
            }
            RaumDaten best = null;
            // Zwischenspeicher: gleiche Liste und Punkt hoechstens 0,5 m weiter -> dieselbe Box (bis zu 2 Punkte: Hoerer und Quelle)
            int slot = -1;
            for (int c = 0; c < cachePos.Length; c++)
                if (ReferenceEquals(cacheListe[c], liste) && (cachePos[c] - hoerer).sqrMagnitude < 0.25f) { slot = c; break; }
            if (slot >= 0) { best = cacheBox[slot]; if (best == null) return false; }
            else
            for (int i = 0; i < liste.Count; i++)
            {
                var d = liste[i];
                if (!ImRaum(d, hoerer)) continue;
                if (best == null || d.Volumen < best.Volumen) best = d;
            }
            if (slot < 0)
            {
                cacheNaechster = (cacheNaechster + 1) % cachePos.Length;
                cachePos[cacheNaechster] = hoerer; cacheListe[cacheNaechster] = liste; cacheBox[cacheNaechster] = best;
            }
            if (best == null) return false;
            var h = best.Box.Halb;
            var inv = Quaternion.Inverse(best.Box.Drehung);
            Vector3 q = inv * (quelle - best.Box.Mitte);
            info.QuelleDrin = ImRaum(best, quelle);   // tankhall: in der Vereinigung (nicht nur in der Huelle)
            // Seite, durch die die Linie Hoerer -> Quelle die Box verlaesst (groesstes |q|/Halb)
            float ax = Mathf.Abs(q.x) / Mathf.Max(h.x, 0.01f), ay = Mathf.Abs(q.y) / Mathf.Max(h.y, 0.01f), az = Mathf.Abs(q.z) / Mathf.Max(h.z, 0.01f);
            if (ay >= ax && ay >= az) { info.WandBreite = 2 * h.x; info.WandHoehe = 2 * h.z; }        // Decke/Boden
            else if (ax >= az) { info.WandBreite = 2 * h.z; info.WandHoehe = 2 * h.y; }
            else { info.WandBreite = 2 * h.x; info.WandHoehe = 2 * h.y; }
            info.WandFlaeche = info.WandBreite * info.WandHoehe;
            info.Volumen = best.Volumen;
            info.Oberflaeche = best.Oberflaeche;
            // tankhall: Waende mit gemessenem alpha, offene Portale mit alpha 1 (echte Flaeche) statt Untergrenze "Min. absorption"
            info.Alpha0 = (float)RaumGeometrieKern.AlphaMitOeffnungen(best.Oberflaeche, best.SOffen, BandAlpha(best, f0));
            info.Alpha1 = (float)RaumGeometrieKern.AlphaMitOeffnungen(best.Oberflaeche, best.SOffen, BandAlpha(best, 1000f));
            info.Alpha2 = (float)RaumGeometrieKern.AlphaMitOeffnungen(best.Oberflaeche, best.SOffen, BandAlpha(best, 4000f));
            info.GruppeOben = best.GruppeOben; info.GruppeUnten = best.GruppeUnten;
            info.Name = best.Name;
            return true;
        }

        static readonly Vector3[] cachePos = new Vector3[4];
        static readonly List<RaumDaten>[] cacheListe = new List<RaumDaten>[4];
        static readonly RaumDaten[] cacheBox = new RaumDaten[4];
        static int cacheNaechster;

        // alpha der Messung bei hz (zwischen den 6 Baendern logarithmisch)
        static float BandAlpha(RaumDaten d, float hz)
        {
            var al = AlphaVon(d);   // hallabsorption: Sehnenmessung, wenn an und vorhanden
            if (hz <= Baender[0]) return al[0];
            for (int b = 0; b < Baender.Length - 1; b++)
                if (hz <= Baender[b + 1])
                    return Mathf.Lerp(al[b], al[b + 1], Mathf.Log(hz / Baender[b]) / Mathf.Log(Baender[b + 1] / Baender[b]));
            return al[Baender.Length - 1];
        }

        // MaterialType-Name des getroffenen Colliders (wie MaterialCache, aber ohne Gruppenbildung).
        static string Materialname(Collider col, Dictionary<int, string> cache)
        {
            if (col == null) return "Stone";
            int id = col.GetInstanceID();
            if (cache != null && cache.TryGetValue(id, out string s)) return s;
            string r = "Stone";
            if (col is TerrainCollider) r = "Soil";
            else
            {
                BaseBallistic bb;
                if (!col.TryGetComponent(out bb) && col.transform.parent != null)
                    col.transform.parent.TryGetComponent(out bb);
                if (bb is BallisticCollider bc) r = bc.TypeOfMaterial.ToString();
                else if (bb is TerrainBallistic) r = "Soil";
            }
            if (cache != null) cache[id] = r;
            return r;
        }

        static float Alpha(string material, float hz)
        {
            float[] p;
            if (!Tabelle.TryGetValue(material, out p))
            {
                string a;
                if (!Aliase.TryGetValue(material, out a) || !Tabelle.TryGetValue(a, out p)) p = Tabelle["Stone"];
            }
            int n = p.Length / 2;
            if (hz <= p[0]) return p[1];
            if (hz >= p[(n - 1) * 2]) return p[(n - 1) * 2 + 1];
            for (int i = 0; i < n - 1; i++)
            {
                float f0 = p[i * 2], f1 = p[i * 2 + 2];
                if (hz > f1) continue;
                float u = Mathf.Log(hz / f0) / Mathf.Log(f1 / f0);
                return Mathf.Lerp(p[i * 2 + 1], p[i * 2 + 3], u);
            }
            return p[(n - 1) * 2 + 1];
        }

        static Vector3[] Fibonacci(int n)
        {
            var r = new Vector3[n];
            float gold = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < n; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / n;
                float rad = Mathf.Sqrt(1f - y * y);
                float phi = gold * i;
                r[i] = new Vector3(Mathf.Cos(phi) * rad, y, Mathf.Sin(phi) * rad);
            }
            return r;
        }

        // ---------------------------------------------------------------- Rechnen + Zonen

        // Sabine-Korrektur gegenueber dem eingebackenen alpha 0.1.
        // Quelle: Sabine T60 = 0.161*V/(S*alpha) (SI-Form; W. C. Sabine, Collected Papers on Acoustics, 1922;
        // H. Kuttruff, Room Acoustics, 6. Aufl., CRC 2017). Fuer stark daempfende Raeume waere Eyring genauer
        // (C. F. Eyring, JASA 1, 217-241, 1930) - hier nicht verwendet.
        // Hallpegel: diffuses Feld ~ 10*log10(4/(S*alpha)) -> Differenz 10*log10(0.1/alpha) (Kuttruff; Bies & Hansen).
        // 0.8.3 (Review-Befund 6): bis 0.8.2 lag fast jeder Betonraum am Deckel +1,5 s (alpha 0,02 -> 0.161*V/S*(50-10)), Raeume
        // verschiedener Groesse klangen gleich. Zwei Ursachen behoben:
        //  - Die Strahlen sehen nur Wandflaechen. Oeffnungen, Moebel, Fugen absorbieren auch: mittleres alpha nie unter "Min. absorption"
        //    (Standard 0,05 = hoechstens doppelte gebackene Nachhallzeit; Annahme, kein Messwert).
        //  - Die Luft absorbiert im Raum mit (4mV-Term, ISO 354), in grossen Hallen ab 2 kHz deutlich. m nach ISO 9613-1 bei 20 C,
        //    50 % r. F., 101,325 kPa (Raumklima wie bei Hallraum-Messungen) - fuer gebackene und neue Zeit gleich.
        // Grenzen -0.8..+1.5 s und +-6 dB bleiben als Sicherung (nach Gehoer).
        const float DeckelRt = 1.5f;
        static readonly double[] LuftM = BerechneLuftM();
        static double[] BerechneLuftM()
        {
            var m = new double[Baender.Length];
            for (int b = 0; b < m.Length; b++) m[b] = Bauakustik.LuftM(Iso9613.Alpha(Baender[b], 20.0, Iso9613.Bezugsdruck, 50.0));
            return m;
        }

        // tankhall: Eyring statt Sabine (harte Raeume, kleines alpha; fuer weiche Raeume genauer), Oeffnungen mit ihrer echten Flaeche
        // in neuer UND gebackener Rechnung (Meta sieht das Loch in der Geometrie auch), keine Untergrenze "Min. absorption" mehr.
        // Der Deckel -0,8..+1,5 s / +-6 dB bleibt als Sicherung (Factory offline 141 Raeume: 5 am Deckel bei 1 kHz, alles kahle
        // Betonraeume mit geschlossener Tuer, gerechnet 2,6 - 4,5 s).
        // hallzone (Befund A3): der Pegel folgt der GESETZTEN (gedeckelten, mit "Strength" gewichteten) Nachhallzeit, nicht mehr der
        // ungedeckelten (Tank 1 kHz bisher +4,5 dB zu +1,5 s, passend +3,5 dB). Der eigene +-6-dB-Deckel entfaellt (HallZonenKern.Gesetzt).
        static void Korrektur(RaumDaten d, float staerke, float[] dRt, float[] dPegel, double[] tGeb = null)
        {
            bool pegel = PegelAusZeit == null || PegelAusZeit.Value;
            for (int b = 0; b < Baender.Length; b++)
            {
                RaumGeometrieKern.HallKorrektur(d.Volumen, d.Oberflaeche, d.SOffen, AlphaVon(d)[b], GebackenAlpha, LuftM[b], out double rt, out double _);
                double tg = RaumGeometrieKern.T60(d.Volumen, d.Oberflaeche, d.SOffen, GebackenAlpha, LuftM[b]);
                HallZonenKern.Gesetzt(tg, rt, staerke, -0.8, DeckelRt, out double r, out double p);
                dRt[b] = (float)r; dPegel[b] = pegel ? (float)p : 0f;
                if (tGeb != null) tGeb[b] = tg;
            }
        }

        /// <summary>hallzone: Zonen je Raum aus der Geometrie (einmal je Messung). Besitz: kleinster Raum (wie Finde/RaumFuer).</summary>
        static void Zerlegen(List<RaumDaten> liste)
        {
            int n = liste.Count;
            var R = new Kasten[n][]; var V = new double[n];
            for (int i = 0; i < n; i++) { R[i] = liste[i].Kaesten; V[i] = liste[i].Volumen; }
            for (int i = 0; i < n; i++)
            {
                var d = liste[i];
                try
                {
                    var t = HallZonenKern.Zerlegen(R, V, i);
                    int ii = i; var kl = t.Kleinere;
                    d.Fade = t.Fade; d.Gitter = t.Gitter;
                    d.Zonen = HallZonenKern.Aufbereiten(t, (x, y, z) => HallZonenKern.Gehoert(R, V, ii, kl, x, y, z));
                }
                catch (Exception ex) { Statistik.Fehler(ex); d.Zonen = new List<Kasten>(); }
            }
        }

        static void Erzeugen(double msMessen, bool gemessen, Stopwatch sw)
        {
            if (daten == null) return;
            float staerke = Staerke.Value;
            int max = MaxZonen.Value;
            var dRt = new float[Baender.Length];
            var dPegel = new float[Baender.Length];
            var tGeb = new double[Baender.Length];
            var dRtD = new double[Baender.Length];
            foreach (var x in daten) x.Angelegt = false;
            double msZerlegen = 0;
            if (daten.Count > 0 && daten[0].Zonen == null)
            {
                var swz = Stopwatch.StartNew();
                Zerlegen(daten);
                msZerlegen = swz.Elapsed.TotalMilliseconds;
            }

            // hallzone: Limit nach hoerbarer Abweichung x Volumen statt "die groessten N" (HallZonenKern.Auswahl)
            var abw = new double[daten.Count]; var vol = new double[daten.Count]; var nz = new int[daten.Count];
            for (int i = 0; i < daten.Count; i++)
            {
                Korrektur(daten[i], staerke, dRt, dPegel, tGeb);
                for (int b = 0; b < dRtD.Length; b++) dRtD[b] = dRt[b];
                abw[i] = HallZonenKern.Abweichung(tGeb, dRtD); vol[i] = daten[i].Volumen; nz[i] = daten[i].Zonen != null ? daten[i].Zonen.Count : 0;
            }
            var wahl = HallZonenKern.Auswahl(abw, vol, nz, max, out int leer, out int belegt);
            var kandidaten = wahl.Select(i => daten[i]).ToList();
            int ohneEigenes = nz.Count(z => z == 0);

            wurzel = new GameObject("Materialklang_Hallzonen");
            UnityEngine.Object.DontDestroyOnLoad(wurzel);
            angelegt.Clear();
            int ok = 0, fehl = 0, raeumeOk = 0;
            var extrem = new List<(string name, string mat, float rt1k, float alpha1k, float vol)>();
            int amDeckel = 0;
            foreach (var d in kandidaten)
            {
                Korrektur(d, staerke, dRt, dPegel);
                if (dRt[Band1k] >= DeckelRt * staerke - 1e-4f && staerke > 0f) amDeckel++;
                extrem.Add((d.Name, d.Dominant, dRt[Band1k], AlphaVon(d)[Band1k], d.Volumen));
                d.DRt1k = dRt[Band1k]; d.DPegel1k = dPegel[Band1k]; d.Inv = Quaternion.Inverse(d.Box.Drehung);
                int vorher = ok;
                foreach (var k in d.Zonen)
                {
                    if (ZoneAnlegen(d, k, dRt, dPegel))
                    {
                        ok++;
                        float r = (float)(Math.Sqrt(k.Hx * k.Hx + k.Hy * k.Hy + k.Hz * k.Hz) + d.Fade);
                        angelegt.Add(new ZonenEintrag { D = d, K = k, Fade = d.Fade, R2 = r * r });
                    }
                    else fehl++;
                }
                d.Angelegt = ok > vorher;
                if (d.Angelegt) raeumeOk++;
            }
            zonenAktiv = ok;
            sw.Stop();

            if (Diagnose.Value)
            {
                string karte = SceneManager.GetActiveScene().name;
                var top = extrem.OrderByDescending(e => Mathf.Abs(e.rt1k)).Take(5)
                    .Select(e => $"{e.name} [{e.mat}, a(1k)={e.alpha1k:F2}, {e.vol:F0} m3] dRT60(1k)={e.rt1k:+0.00;-0.00} s");
                Plugin.Log.LogInfo($"[Reverb] Map {karte}: {ok} zones created ({fehl} failed) for {raeumeOk} of {daten.Count} measured rooms" +
                                   $" ({(gemessen ? $"measuring {msMessen:F1} ms, " : "no new measurement, ")}{(msZerlegen > 0 ? $"zone layout {msZerlegen:F1} ms, " : "")}total {sw.Elapsed.TotalMilliseconds:F1} ms), strength {staerke:F2}, " +
                                   $"rooms = union of their boxes ({daten.Count(x => x.Kaesten.Length > 1)} with several), zones cover only the room's own volume " +
                                   $"({daten.Count(x => x.Gitter)} rooms split around overlaps/smaller rooms inside), Eyring, open portals as absorption 1, " +
                                   $"reverb level {(PegelAusZeit.Value ? "from the set reverb time" : "unchanged")}; " +
                                   $"{amDeckel} of {kandidaten.Count} rooms at the +{DeckelRt:F1} s cap (1 kHz); " +
                                   $"limit 'Max zone boxes' {max}: {nz.Sum()} zones needed, {belegt} used, {leer} rooms without zone (lowest audible shift x volume first)" +
                                   (ohneEigenes > 0 ? $", {ohneEigenes} rooms lie completely inside smaller rooms." : "."));
                if (extrem.Count > 0) Plugin.Log.LogInfo("[Reverb] Most extreme rooms: " + string.Join(" | ", top));
                // tankhall: je Raum A/V wie im Klang-Log des Raumwegs (zum Nachrechnen)
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                Plugin.Log.LogInfo("[Reverb] Rooms (name: boxes, V m3, S m2, open m2, wall alpha 125/1k/4k, A(1k) m2, dRT60 125/1k/4k s, dLevel 125/1k/4k dB, zones, fade m): " + string.Join(" | ", kandidaten.Select(d =>
                {
                    Korrektur(d, staerke, dRt, dPegel);
                    var alv = AlphaVon(d);
                    double a1k = d.Oberflaeche * RaumGeometrieKern.AlphaMitOeffnungen(d.Oberflaeche, d.SOffen, alv[Band1k]);
                    return string.Format(inv, "{0}: {1}, {2:F0}, {3:F0}, {4:F1}, {5:F3}/{6:F3}/{7:F3}, {8:F1}, {9:+0.00;-0.00}/{10:+0.00;-0.00}/{11:+0.00;-0.00}, {12:+0.0;-0.0}/{13:+0.0;-0.0}/{14:+0.0;-0.0}, {15}, {16:F2}",
                        d.Name, d.Kaesten.Length, d.Volumen, d.Oberflaeche, d.SOffen, alv[0], alv[Band1k], alv[5], a1k, dRt[0], dRt[Band1k], dRt[5],
                        dPegel[0], dPegel[Band1k], dPegel[5], d.Zonen.Count, d.Fade);
                })));
                if (leer > 0)
                    Plugin.Log.LogInfo("[Reverb] Rooms without zone (limit 'Max zone boxes'): " + string.Join(", ", Enumerable.Range(0, daten.Count)
                        .Where(i => nz[i] > 0 && !daten[i].Angelegt).OrderByDescending(i => abw[i] * vol[i]).Take(30)
                        .Select(i => string.Format(inv, "{0} ({1:F1} dB, {2:F0} m3, {3} zones)", daten[i].Name, abw[i], vol[i], nz[i]))));
            }
        }

        static bool ZoneAnlegen(RaumDaten d, in Kasten k, float[] dRt, float[] dPegel)
        {
            try
            {
                var mitte = new Vector3((float)k.Mx, (float)k.My, (float)k.Mz);
                var dreh = Quaternion.LookRotation(new Vector3((float)k.Wx, (float)k.Wy, (float)k.Wz), new Vector3((float)k.Vx, (float)k.Vy, (float)k.Vz));
                var halb = new Vector3((float)k.Hx, (float)k.Hy, (float)k.Hz);
                return Zone.Anlegen(wurzel.transform, d.Name, mitte, dreh, halb, (float)d.Fade, Baender, dRt, dPegel);
            }
            catch (Exception ex) { Statistik.Fehler(ex); return false; }
        }

        static void ZonenZerstoeren()
        {
            if (wurzel != null) UnityEngine.Object.Destroy(wurzel);   // Zone.OnDestroy gibt das native Objekt frei
            wurzel = null;
            zonenAktiv = 0;
            angelegt.Clear();
        }

        // Alles, was Meta-Typen anfasst, liegt hier - so bricht ein fehlendes Meta.XR.Audio nichts anderes.
        static class Zone
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            public static bool Anlegen(Transform eltern, string name, Vector3 mitte, Quaternion drehung, Vector3 halb, float fade,
                                       float[] hz, float[] dRt, float[] dPegel)
            {
                var go = new GameObject("Hall_" + name);
                var tr = go.transform;
                tr.SetParent(eltern, false);
                tr.position = mitte;
                tr.rotation = drehung;
                // Die Zone ist 2 Einheiten gross (Native-Box = 2 + Fade/Scale), also Scale = halbe Raumgroesse.
                tr.localScale = new Vector3(Mathf.Max(halb.x, 0.1f), Mathf.Max(halb.y, 0.1f), Mathf.Max(halb.z, 0.1f));

                var z = go.AddComponent<MetaXRAcousticControlZone>();
                z.Rt60.points.Clear();
                z.ReverbLevel.points.Clear();
                for (int i = 0; i < hz.Length; i++)
                {
                    z.Rt60.points.Add(new Spectrum.Point(hz[i], dRt[i]));
                    z.ReverbLevel.points.Add(new Spectrum.Point(hz[i], dPegel[i]));
                }
                // hallzone: Fade je Raum (HallZonenKern.FadeFuer, 0,2-1 m); die Teilbox ist an echten Raumgrenzen schon um Fade/2
                // eingerueckt, die Native-Box (2 + Fade/Scale) endet dort also an der Wand
                z.FadeDistance = fade;
                z.StartInternal();                      // sofort anlegen statt erst im naechsten Frame
                if (z._controlHandle == IntPtr.Zero)
                {
                    UnityEngine.Object.Destroy(go);
                    return false;
                }
                return true;
            }
        }
    }

    // Raidstart: Raumspeicher fertig (Raeume initialisiert, gueltige Raeume in _validSpatialAudioRooms).
    [HarmonyPatch(typeof(AudioRoomStorage), MethodType.Constructor, new Type[0])]
    internal static class HallRaumspeicherPatch
    {
        static void Postfix(AudioRoomStorage __instance)
        {
            try { HallZonen.RaeumeMelden(__instance._validSpatialAudioRooms); KlangLog.RaidStart(); Wege.RaidStart(); }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }
    }

    // Raidende: Raumsystem wird abgebaut.
    [HarmonyPatch(typeof(SpatialAudioSystem), nameof(SpatialAudioSystem.Dispose))]
    internal static class HallDisposePatch
    {
        static void Prefix()
        {
            try { HallZonen.Leeren(); KlangLog.RaidEnde(); }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }
    }
}
