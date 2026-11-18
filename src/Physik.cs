using System;
using System.Runtime.CompilerServices;
using Audio.SpatialSystem;
using Audio.SpatialSystem.Utils;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    // Physik-Modus (Physics Mode): reale Bauakustik statt Spielwerte, fuer ALLE Geraeusche.
    //  - Daemmung je Wandstueck nach Massengesetz: TL = 20*log10(Dichte*Dicke*f) - 47 dB (f = 250 Hz), gedeckelt (Flankenweg).
    //  - Einheitliche Skala: Wertung 0..1 = Daemmung 0..Dynamik dB (Hindernis, Raumweg, Tueren, Wand zwischen Raeumen).
    //  - Lautstaerke = 10^(-Dynamik*Wertung/20) statt Spielkurve (die endet bei -4.4 dB).
    //  - Tiefpass = 22 kHz -> Grenzfrequenz bei voller Daemmung, ohne die 8-kHz-Untergrenze des Spiels.
    //  - Der leichteste Weg zaehlt: Durchdringung/Beugung/Reflexion -> Minimum statt Mittelwert.
    //    0.8.4 Stufe 1 (Wege.cs, Abschnitt 14): D und R in echten dB, Energiesumme statt Minimum, Tiefpass aus der Bandkurve.
    internal static class Physik
    {
        internal static ConfigEntry<bool> Aktiv, Koinzidenz, LeiseReal;
        internal static ConfigEntry<float> Grundgeraeusch;
        internal static ConfigEntry<float> Dynamik, Frequenz, TiefpassMin, Freiweg, Lautheit, VerlustFaktor;
        internal static ConfigEntry<float> KennKnie, KennSteigung;
        internal static ConfigEntry<bool> KennKurveAn;   // explosionfern (17.11.): Explosionen nach ihrer Abstandskurve (KennGruppeKern.cs)
        internal static ConfigEntry<bool> FarbeKennlinieAn;   // farbekennlinie (12.11.)
        internal static ConfigEntry<bool> HochpassPhysikAn;   // blechbass (17.11.): kein Bassverlust auf dem Weg (HochpassKern.cs)
        internal static ConfigEntry<bool> DiffusAlsHall;
        internal static ConfigEntry<bool> DiffusHoerbar;   // diffushoerbar (17.11.): Diffusanteil bleibt trocken hoerbar (DiffusHoerbarKern)
        internal static ConfigEntry<bool> RuheHalten;      // diffusflattern (17.11.): ruhende Quelle behaelt ihr Ziel (QuellRuheKern)
        internal static ConfigEntry<float> MetaHallDb;     // diffushoerbar (17.11.): gemessene Staerke des Meta-Halls (eta, dB)
        internal static ConfigEntry<bool> DiffusEreignis;  // tunneldiffus (17.11.): trocken nur der Teil des Raumanteils, der im Ereignis ankommt (ZeitKern)
        internal static ConfigEntry<bool> SpracheFrueh;    // tunneldiffus Teil 2 (17.11.): Sprache trocken nur mit dem fruehen, verstaendlichen Anteil (D50)
        internal static ConfigEntry<bool> RaumHallAn;   // hallsend (12.11.): Hindernis im selben Raum laesst den Hall stehen (DiffusKern.RaumHall)
        internal static ConfigEntry<float> DiffusHallPegel;   // 10.11.: Abgleich des Hall-Anteils nach Gehoer   // diffus-hall (10.11.): Diffusanteil als Hall statt trocken (DiffusKern.cs)   // 0.8.6 Kennlinie (RaumwegKern.cs, Hoerbarkeit.Kennlinie)
        internal static readonly ConfigEntry<float>[] Dichte = new ConfigEntry<float>[Plugin.AnzahlGruppen];
        internal static readonly ConfigEntry<float>[] Massiv = new ConfigEntry<float>[Plugin.AnzahlGruppen];

        // kg/m^3 (Gitter/Zaun/Gras = offen -> 0). Reihenfolge wie Gruppe.
        // Quelle: uebliche Tabellenwerte; Beton/Stahl vgl. EN 1991-1-1 Anhang A (24 bzw. 77-78,5 kN/m3), Stahl 7850 = EN 1993-1-1,
        // Glas 2500 = EN 572-1. Weich/Unbekannt/Tuer/Kies sind Mischwerte nach Gehoer (siehe README "Physics and sources").
        // Schnee (eigene Gruppe seit 0.6.0): 300 kg/m3 = Mitte zwischen frischem (~100-300) und gepacktem Schnee (~300-500); Tabellenwerte der Schneekunde.
        static readonly float[] StartDichte = { 2300f, 1700f, 1600f, 7850f, 7850f, 600f, 500f, 2500f, 500f, 0f, 1000f, 1000f, 2000f, 600f, 300f };
        // Groesste plausible massive Dicke (m). Dicker gemessen = hohles Objekt (Auto, Container, Haus) -> 2 Waende dieser Staerke.
        // Keine Literaturwerte: Annahmen ueber die Spielgeometrie.
        // 10.11. (akustik-wand, Befund 7): Metal thin 3 mm -> 2 mm. Die Gruppe sind laut Klang-Log (Factory 10.11.) vor allem ISO-Container
        // ("container_6m/12m_BALLISTIC_metalthin": Trapezblech 1,6 mm Seite / 2,0 mm Stirn, ISO 1496-1 und Herstellerangaben), Blechkabinen,
        // Zaeune, Treppenbleche; Karosserieblech 0,7-0,9 mm (EN 10130). Ein hohler Koerper zaehlt jetzt als 2 x 2 mm statt 2 x 3 mm.
        // Glas: "Solid up to" wird nicht mehr benutzt - jedes Glas ist eine Scheibe (TuerKern.GlasDb, Befund 3).
        static readonly float[] StartMassiv = { 1.0f, 3f, 3f, 0.01f, 0.002f, 0.3f, 0.05f, 0.02f, 0.1f, 0f, 0.5f, 3f, 0.5f, 0.06f, 3f };

        // Plattenmodell fuer die Koinzidenz-Korrektur (nur dicke, schwere Gruppen; 0 = keine Korrektur, das Massengesetz bleibt).
        // E-Modul in GPa und Querkontraktionszahl, typische Tabellenwerte: Beton ~25-30 GPa (nu 0.2), Stahl 200 GPa (nu 0.3),
        // Holz (Biegung laengs der Faser) ~9 GPa (nu 0.3). Bis 10.11. blieben duenne Gruppen ohne Korrektur (siehe unten).
        // 10.11. (akustik-wand, Befund 7): auch duennes Metall (Stahl 200 GPa / 0,3, EN 1993-1-1), duennes Holz und Tuerblatt (Holz 9 GPa / 0,3,
        // wie TuerKern.Platte) mit Plattenmodell - ihre Koinzidenz liegt im hoerbaren Bereich (Sperrholz 12 mm: fc ~ 1,2 kHz, Stahlblech
        // 2 mm: fc ~ 6 kHz -> 4-kHz-Band im Uebergang), das reine Massengesetz ueberschaetzte dort 4-9 dB. Glas: TuerKern.GlasDb (70 GPa).
        static readonly float[] EModulGPa = { 25f, 0f, 0f, 200f, 200f, 9f, 9f, 0f, 0f, 0f, 0f, 0f, 0f, 9f, 0f };
        static readonly float[] Querkontraktion = { 0.2f, 0f, 0f, 0.3f, 0.3f, 0.3f, 0.3f, 0f, 0f, 0f, 0f, 0f, 0f, 0.3f, 0f };

        /// <summary>10.11. (Befund 7): Verlustfaktor der Koinzidenz fuer Gruppe g. Beton/Stein: EN 12354-1 Anhang C (Bauteil mit Stossstellen
        /// an schwere Bauteile, Bauakustik.VerlustEn); alle anderen Platten: die Einstellung "Coincidence loss factor".</summary>
        internal static double Verlust(int g, double m, double f, double eta) => g == (int)Gruppe.ConcreteStone ? Bauakustik.VerlustEn(Bauakustik.EtaInnenStein, m, f) : eta;

        internal static bool An => Aktiv != null && Aktiv.Value && Plugin.Aktiv.Value;

        internal static void Binden(ConfigFile c)
        {
            const string s = "0. Physics Mode (real values)";
            Aktiv = c.Bind(s, "Enabled", true,
                "Real building acoustics (mass law) for all sounds. Thick walls = near silence, like in real life. Off = game values / the per-material sliders in sections 2.xx.");
            Dynamik = c.Bind(s, "Max. insulation (dB)", 60f,
                new ConfigDescription("Highest insulation of one path. In reality side paths (ceilings, pipes) limit it to about 55-65 dB.", new AcceptableValueRange<float>(20f, 90f)));
            // 0.8.6 Kennlinie: Standard 0,2 -> 1. Die Stauchung lauter Klaenge macht jetzt die Kennlinie aus dem realen Pegel (Knie + Steigung
            // unten); der Faktor wirkt zusaetzlich auf alles (Steigung oberhalb des Grundgeraeuschs, siehe Hoerbarkeit.Rechnen) und bleibt
            // aus Kompatibilitaet bestehen. Vorhandene cfg-Werte (z. B. 0.42) bleiben, wie sie sind.
            Lautheit = c.Bind(s, "Loudness factor", 1f,
                new ConfigDescription("Extra factor on the insulation you hear (dB x factor), for every sound. 1.0 = real physics (default since 0.8.6). Loud sounds no longer need it: gunshots, explosions and impacts are compressed by the 'Compression knee' / 'Compression above knee' settings from their real level. Below 1 it compresses on top of that (old behaviour, 0.2 was the old default). Only what you hear - bot hearing uses '3. Bot Hearing (SAIN) / Strength'. Applies live.", new AcceptableValueRange<float>(0.1f, 1f)));
            KennKnie = c.Bind(s, "Compression knee (dB(A))", 85f,
                new ConfigDescription("Real level at your ear (dB(A), without walls) above which a sound is compressed like the game mix does it: a real gunshot is ~140 dB(A), headphones play it far quieter. Below the knee every dB of insulation counts fully (footsteps, voices, cars: real physics); above it, the part of the insulation that lies in the loud range counts only with 'Compression above knee'. So a closed door (23 dB) takes ~9 dB off a rifle shot 80 m away instead of 23 dB, while 60 dB of concrete still takes ~46 dB. Soft knee 10 dB wide (no audible step). 85 = the game plays levels up to ~80 dB(A) about 1:1. Applies live.", new AcceptableValueRange<float>(60f, 130f)));
            KennSteigung = c.Bind(s, "Compression above knee", 0.2f,
                new ConfigDescription("Slope of the compression above the knee: dB played per real dB. 0.2 = measured from the game's own close gunshot clips (they play like 93-96 dB(A) for a real ~140 dB(A)); 1.0 = no compression (real physics for loud sounds too: a shot behind a closed door loses the full insulation). Applies live.", new AcceptableValueRange<float>(0.05f, 1f)));
            // explosionfern (17.11.): Standard an - aendert nur Explosionen hinter Hindernissen (Schuesse, Einschlaege, Schritte unveraendert),
            // offene Tuer +0,02 dB, 3-4 Raeume (23 dB) 2,6-3,2 dB leiser; Herleitung und Zahlen KennGruppeKern.cs
            KennKurveAn = c.Bind(s, "Explosions: compression from the game distance curve", true,
                "Explosions behind walls and rooms are compressed as much as the game itself compresses explosions over distance (its volume curve for grenades), instead of the gunshot slope 'Compression above knee'. A grenade 43 m away behind 3-4 rooms (23 dB real) then plays 7.2 dB quieter instead of 4.6 dB - as quiet as the game plays the same grenade ~600 m away in the open. Gunshots, impacts and quiet sounds are not changed. Off = gunshot slope for explosions too (before 0.8.6). Applies live.");
            // farbekennlinie (12.11.): Neigung des Tiefpasses mit demselben Stauchungsfaktor wie die Lautstaerke (FarbeKennlinieKern.cs)
            FarbeKennlinieAn = c.Bind(s, "Timbre follows loudness curve", true,
                "Loud sounds (gunshots, explosions, impacts) that the compression above keeps loud behind an obstacle also lose only as much treble as their loudness drop says: the muffling is compressed by the same factor as the volume (a shot that loses 16 dB of real insulation but only 4 dB of volume gets a quarter of the muffling). Off = muffling from the full real insulation (loud but very dull). Quiet sounds below the knee (footsteps, voices, suppressed shots far away) are not affected. Applies live.");
            HochpassPhysikAn = c.Bind(s, "No bass cut on the path (high-pass)", true,
                "Every sound so far kept the game's high-pass of 80 Hz, which cuts steeply below it (measured: a shot on a free path lost 15-27 dB at 63-100 Hz). " +
                "No path the mod calculates loses more bass than mids - air, walls (mass law) and edges let low frequencies through best; the bass still gets quieter behind walls " +
                "through the band calculation. On = no high-pass on the path (10 Hz), the shot keeps the bass of its recording. Off = game's high-pass as before. Applies live.");
            Frequenz = c.Bind(s, "Reference frequency (Hz)", 250f,
                new ConfigDescription("Frequency at which loudness is evaluated. 250 Hz = the bass you hear through walls.", new AcceptableValueRange<float>(63f, 1000f)));
            TiefpassMin = c.Bind(s, "Low-pass at full insulation (Hz)", 800f,
                new ConfigDescription("How muffled it sounds at maximum insulation.", new AcceptableValueRange<float>(150f, 4000f)));
            Freiweg = c.Bind(s, "Clear-path threshold", 0.02f,
                new ConfigDescription("Score from which a path counts as occluded (0.02 = about 1 dB). Game: 0.5 = 30 dB. Applies live.", new AcceptableValueRange<float>(0f, 0.5f)));
            Koinzidenz = c.Bind(s, "Coincidence correction", true,
                "Walls and plates (concrete/stone, metal, wood, glass, door leaves) insulate less than the plain mass law around and above their coincidence frequency (Sharp / Cremer, see README); thin plywood and glass already from about 1-2 kHz. Off = pure mass law.");
            VerlustFaktor = c.Bind(s, "Coincidence loss factor", 0.1f,
                new ConfigDescription("Effective total loss factor of the coincidence correction for metal, wood and glass (internal damping + edges + radiation). Higher = shallower dip. Concrete/stone no longer uses it since 0.8.6: there the loss factor of EN ISO 12354-1 annex C applies (0.01 + m''/(485 sqrt f)).", new AcceptableValueRange<float>(0.02f, 0.5f)));
            string[] namen = { "Concrete/Stone", "Soil/Terrain", "Gravel/Pebbles", "Metal thick", "Metal thin", "Wood thick", "Wood thin",
                               "Glass", "Soft", "Grate/Fence/Grass", "Body", "Water", "Unknown", "Door leaf", "Snow" };
            for (int i = 0; i < Plugin.AnzahlGruppen; i++)
            {
                Dichte[i] = c.Bind(s, $"Density {i + 1:00} {namen[i]} (kg/m3)", StartDichte[i],
                    new ConfigDescription("Material density used by the mass law (0 = open, no insulation).", new AcceptableValueRange<float>(0f, 12000f)));
                Massiv[i] = c.Bind(s, $"Solid up to {i + 1:00} {namen[i]} (m)", StartMassiv[i],
                    new ConfigDescription("Measured thicker than this = hollow object: counts as two walls of this thickness.", new AcceptableValueRange<float>(0f, 5f)));
            }
            // 0.8.5: leise Quellen nur so weit stauchen, wie sie real ueber dem Grundgeraeusch liegen (RaumwegKern.cs, Hoerbarkeit)
            LeiseReal = c.Bind(s, "Quiet sources: real audibility", false,
                "Quiet sounds (footsteps, gear, breathing, voices: 60-72 dB at 1 m) are checked against the background noise: below it they are masked, increasingly quieter, up to 2 dB per dB (continuous, no cut): at the masking threshold 15 dB, 10 dB below it 35 dB on top of the real level. " +
                "With a loudness factor below 1, they are only compressed as far as they would really stay above the background; insulation beyond that counts fully. " +
                "Checked per band (250 Hz / 1 kHz / 4 kHz): walls let the lows through best, so a sound whose highs are masked stays audible as a dull thud; silent only when no band is left. Gunshots, explosions and impacts are not masked (they follow the compression knee). Off = no masking, insulation x loudness factor and the compression knee only.");
            Grundgeraeusch = c.Bind(s, "Quiet sources: background noise (dB)", 30f,
                new ConfigDescription("Background noise level (dB(A)) the quiet sources have to be heard over (below it they are masked: increasingly quieter, up to 2 dB per dB). Spread over the bands like a quiet room (NR curve: louder in the lows, quieter in the highs). 30 = quiet room. Higher = quiet sounds disappear sooner behind walls.", new AcceptableValueRange<float>(0f, 60f)));
            DiffusHallPegel = c.Bind(s, "Diffuse reverb level (dB)", -6f,
                new ConfigDescription("Level of the reverb part of 'Diffuse sound as reverb' (the diffuse sound of rooms that is sent to the game's reverb). 0 = as calculated; lower = less reverb from sounds in other rooms/halls (the dry, located part is not changed). How loud the game's reverb really plays per dB cannot be measured from outside, so this is tuned by ear. Applies live.",
                    new AcceptableValueRange<float>(-30f, 6f)));
            DiffusAlsHall = c.Bind(s, "Diffuse sound as reverb", true,
                "On (default since 0.8.6): sound that reaches you through rooms, openings or walls is split. The directed part (straight or around edges through the openings, radiated by the wall) is the dry, located sound; the diffuse part (reverb of the source room leaking into your room) is sent to the game's reverb instead (Meta reverb send of that sound, up to +20 dB), so the reverb you hear stays as loud as before but the dry sound gets quieter: a shot in the next hall sounds distant and reverberant instead of close and clear. Bot hearing is unchanged (bots hear the total energy). Off = the whole energy plays as the dry sound (behaviour up to 0.8.56). Applies live.");
            RaumHallAn = c.Bind(s, "Obstacle in the same room keeps the reverb", true,
                "On (default since 0.8.6): when a source and you are in the same measured room and an obstacle (pillar, machine, container, inner wall piece) blocks the direct line, " +
                "only the direct sound gets quieter - the reverb of the room is fed by the whole sound, as in reality, so it keeps the level it has without the obstacle " +
                "(the reverb send of that sound is raised by the obstacle's insulation, up to the game's +20 dB limit; the rest is counted in the diagnostic line). " +
                "Needs 'Diffuse sound as reverb'. Outside (no measured room) and between different rooms nothing changes. Off = the obstacle also lowers the reverb (behaviour up to 0.8.5). Applies live.");
            // diffushoerbar (17.11.): Messung 17.11. - Metas Hall liegt 30-50 dB unter dem Direktschall, der Diffusanteil im Send war verloren
            DiffusHoerbar = c.Bind(s, "Diffuse sound stays audible", true,
                "On (default since 0.8.6): the diffuse part of a sound from another room (the reverb of the source room leaking through doors, openings and walls) " +
                "is played with the dry sound again, because the game's reverb plays it 30-50 dB too quietly (measured on Factory). " +
                "The reverb you hear stays exactly as loud as before; only the part the reverb really makes audible ('Meta reverb strength') is taken off the dry sound. " +
                "Enemies in the next room, behind a wall or in the cellar get louder by the 'applied dry' value of the sound log (typically 5-24 dB). " +
                "Needs 'Diffuse sound as reverb'. Off = only the directed part plays dry, the diffuse part goes to the reverb (behaviour up to 0.8.5). Applies live.");
            // diffusflattern (17.11.): Quelle, die gerade nichts spielt, bekommt kein neues Ziel (QuellRuheKern.cs)
            RuheHalten = c.Bind(s, "Idle sources keep their level", true,
                "On (default since 0.8.6): a sound source that is not playing right now (between two footsteps of the same bot) keeps the level of its last sound " +
                "instead of getting a new one computed without a sound (unknown length and clip level, up to 37 dB louder). The game slides the volume of the next " +
                "sound from that value, so the start of each footstep no longer flares up. Off = level computed every time (behaviour up to 0.8.5). Applies live.");
            MetaHallDb = c.Bind(s, "Meta reverb strength (dB)", (float)DiffusHoerbarKern.EtaStandardDb,
                new ConfigDescription("How loud the game's (Meta) reverb of a sound arrives at your ear compared with its direct sound, at reverb send 0 dB (energy). " +
                    "Measured on Factory: -43 dB far from the source hall, -32 dB near it (4 kHz). Used by 'Diffuse sound stays audible' to take the part the reverb " +
                    "really carries off the dry sound; at the measured values that is less than 0.1 dB. Only raise it if a measurement shows a louder reverb. Applies live.",
                    new AcceptableValueRange<float>(-80f, 10f)));
            // tunneldiffus (17.11.): Meldung Tunnel Factory - Raumanteil aus Hallen als kompaktes Ereignis 5-10 dB zu laut (ZeitKern)
            DiffusEreignis = c.Bind(s, "Diffuse sound: only what arrives with the event", true,
                "On (default since 0.8.6): of the room part of a sound from another room (the reverb of the source hall leaking through openings), " +
                "only the share that reaches you within the ear's short-term window (LAFmax, 125 ms) is played with the dry sound. A hall with a long reverb " +
                "spreads a footstep's energy over seconds; played as one compact step it was 5-10 dB too loud (Factory tunnels). Long sounds (speech, loops) " +
                "keep almost all of it; the duration comes from the clip's measured level table. The reverb input is unchanged. Needs 'Diffuse sound stays audible'. " +
                "Sound log: '| event -X dB (late Y %, signal Z s)'. Off = the whole steady-state energy plays dry. Applies live.");
            // tunneldiffus Teil 2 (17.11.): Meldung "jedes Wort" - spaeter Nachhall einer Halle ist fuer Sprache Stoerschall, nicht Sprache
            SpracheFrueh = c.Bind(s, "Speech from other rooms: only the intelligible early part", true,
                "On (default since 0.8.6): a voice from another room or hall plays dry only with the part of its energy that arrives within " +
                "50 ms after the first sound from the opening (definition D50, ISO 3382-1) - that part is heard as clear speech. The later reverberant " +
                "part of a big hall smears the syllables; in reality you hear it as a loud but unintelligible murmur. That part belongs to the reverb " +
                "(its input stays at the full level), but the game's reverb plays it 30-50 dB too quietly, so voices from halls become much quieter. " +
                "Needs 'Diffuse sound stays audible'. Sound log: '| speech early -X dB (late Y %)'. Off = voices like other sounds. Applies live.");
            Raumweg.Binden(c);   // 0.8.4: Raumweg aus Geometrie und Portalzustand
        }

        // Daemmung (dB) eines Stuecks Material der Gruppe g mit Dicke d (m).
        internal static float Daemmung(int g, float dicke) => Daemmung(g, dicke, Frequenz.Value, true);

        /// <summary>0.8.4 Stufe 1: Flaechenmasse (kg/m2) wie in Daemmung (hohles Objekt = zwei Schalen).</summary>
        internal static float Masse(int g, float dicke)
        {
            if (g == (int)Gruppe.Glass) return (float)TuerKern.GlasMasse(Dichte[g].Value, dicke);   // 10.11.: eine Scheibe (Befund 3)
            float mx = Massiv[g].Value;
            if (dicke > mx) dicke = 2f * mx;
            return Dichte[g].Value * Mathf.Max(dicke, 0.002f);
        }

        /// <summary>0.8.4 Stufe 1: Daemmung bei beliebiger Frequenz f (Oktavbaender); deckel = auf "Max. insulation" begrenzen.</summary>
        internal static float Daemmung(int g, float dicke, float frequenz, bool deckel)
        {
            // 10.11. (akustik-wand, Befund 3): Glas = eine Scheibe mit Koinzidenz und Rahmenfuge, dieselbe Regel wie Fenster und Wegsuche
            if (g == (int)Gruppe.Glass)
                return (float)TuerKern.GlasDb(Dichte[g].Value, dicke, frequenz, Koinzidenz.Value, VerlustFaktor.Value, deckel ? Dynamik.Value : WegeKern.BandGrenzeDb);
            float mx = Massiv[g].Value;
            float schale = Mathf.Min(dicke, mx);               // Dicke einer einzelnen Schale (fuer die Koinzidenzfrequenz)
            if (dicke > mx) dicke = 2f * mx;                 // hohles Objekt: zwei Schalen
            float m = Dichte[g].Value * Mathf.Max(dicke, 0.002f);
            if (m <= 0f) return 0f;
            // Quelle: Massengesetz bei Feldeinfall (diffuser Schalleinfall), TL = 20*log10(m''*f) - 47 dB, m'' in kg/m2.
            // Senkrechter Einfall waere -42 dB; Feldeinfall ~5 dB weniger. Beranek & Ver, "Noise and Vibration Control
            // Engineering" (Wiley 1992/2006); Bies, Hansen & Howard, "Engineering Noise Control" (5. Aufl., CRC 2017).
            // Gilt unterhalb der Koinzidenzfrequenz. Darueber (dicke, schwere Waende) greift die Korrektur nach Bauakustik.cs
            // (Sharp 1978 / Cremer); duenne Gruppen bleiben unveraendert. Deckel = Flankenwege
            // (EN ISO 12354-1:2017), der Wert 60 dB ist eine Annahme.
            double f = frequenz;
            double r = Bauakustik.Massengesetz(m, f);
            if (Koinzidenz.Value && EModulGPa[g] > 0f)
            {
                double fc = Bauakustik.Koinzidenzfrequenz(Dichte[g].Value, Mathf.Max(schale, 0.002f), EModulGPa[g] * 1e9, Querkontraktion[g]);
                r = Bauakustik.Koinzidenz(r, m, f, fc, Verlust(g, m, f, VerlustFaktor.Value));
            }
            return deckel ? Mathf.Clamp((float)r, 0f, Dynamik.Value) : Mathf.Max((float)r, 0f);
        }

        /// <summary>0.8.6: Daemmung eines getroffenen Stuecks: gehoert der Collider zu einer Tuer (Gruppe "Door leaf"), zaehlt ihr Blatt
        /// (Material + gemessene Blattdicke, TuerKern.cs) statt Gruppe x Strahllaenge - dieselbe Rechnung wie das Portal.</summary>
        internal static float DaemmungStueck(int id, int g, float dicke, float frequenz, bool deckel)
        {
            if (g == (int)Gruppe.Door && TuerenFenster.StueckDb(id, frequenz, deckel, out float db)) return db;
            return Daemmung(g, dicke, frequenz, deckel);
        }

        internal static float DaemmungStueck(int id, int g, float dicke) => DaemmungStueck(id, g, dicke, Frequenz.Value, true);

        /// <summary>0.8.6: Flaechenmasse eines getroffenen Stuecks (Tuer: ihr Blatt).</summary>
        internal static float MasseStueck(int id, int g, float dicke)
        {
            if (g == (int)Gruppe.Door && TuerenFenster.StueckMasse(id, out float m)) return m;
            return Masse(g, dicke);
        }

        internal static float EnergieAusDb(float db) => Mathf.Pow(10f, -db / 10f);
        internal static float DbAusEnergie(float e) => -10f * Mathf.Log10(Mathf.Max(e, 1e-12f));
        internal static float WertungAusDb(float db) => Mathf.Clamp01(db / Dynamik.Value);
        internal static float WertungAusEnergie(float e) => WertungAusDb(DbAusEnergie(e));

        internal static void Anwenden(Harmony h)
        {
            Patch(h, AccessTools.DeclaredMethod(typeof(SpatialAudioOccluder), "CalculateFinalOcclusionParams"), nameof(LautstaerkeNach), Priority.Normal);
            Patch(h, AccessTools.DeclaredMethod(typeof(FilterCutoffCalculator), "Compute"), nameof(FilterNach), Priority.First);
            // sendsprung (12.11.): Send einer laufenden Quelle gleitet je Bild weiter (SendGleitKern.cs)
            Patch(h, AccessTools.DeclaredMethod(typeof(SpatialAudioOccluder), "ApplyOcclusionToSource", new[] { typeof(BetterSource), typeof(bool) }), nameof(SendNach), Priority.Normal);
            Patch(h, AccessTools.DeclaredMethod(typeof(SpatialAudioOcclusionCombinedCalculator), "GetScore"), nameof(WertungNach), Priority.Normal);
            // 0.8.1: nach einem Reset (Pool, Wechsel Hindernis <-> Raumweg) gilt die gemerkte Wertung nicht mehr
            Patch(h, AccessTools.DeclaredMethod(typeof(SpatialAudioOcclusionCombinedCalculator), "OnReset"), nameof(ResetNach), Priority.Normal);
            // 10.11. (Review B1): gemerkter Weg nur, solange die angewandte Lautstaerke hoerbar stimmt (LautTaktKern.cs)
            try
            {
                var gemerkt = AccessTools.DeclaredMethod(typeof(CombinedSpatialAudioOccluder), "ApplyCachedParamsToContainer");
                if (gemerkt == null) Plugin.Log.LogWarning("Physics mode: CombinedSpatialAudioOccluder.ApplyCachedParamsToContainer not found -> volume of slowly changing occlusion stays game behaviour (only recalculated on steps > 0.05).");
                else h.Patch(gemerkt, prefix: new HarmonyMethod(typeof(Physik), nameof(GemerktVor)));
            }
            catch (Exception e) { Statistik.Fehler(e); }
            Raumweg.Anwenden(h);   // 0.8.4
            Wege.Anwenden(h);      // 0.8.4 Stufe 1
            // 0.8.3: ein Rechner aus dem Pool traegt noch das Ergebnis seiner vorigen Quelle (siehe NeuerOccluderNach)
            try
            {
                int n = 0;
                foreach (var ctor in AccessTools.GetDeclaredConstructors(typeof(CombinedSpatialAudioOccluder)))
                {
                    h.Patch(ctor, postfix: new HarmonyMethod(typeof(Physik), nameof(NeuerOccluderNach)));
                    n++;
                }
                if (n == 0) Plugin.Log.LogWarning("Steady occlusion: CombinedSpatialAudioOccluder constructor not found -> pooled calculators keep the previous sound's result (game behaviour).");
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void Patch(Harmony h, System.Reflection.MethodInfo ziel, string methode, int prio)
        {
            if (ziel == null) { Plugin.Log.LogWarning($"Physics mode: target for {methode} not found -> this part stays vanilla."); return; }
            h.Patch(ziel, postfix: new HarmonyMethod(typeof(Physik), methode) { priority = prio });
        }

        internal static int Lautstaerken, Filter;
        internal static double SummeDb;

        // Lautstaerke direkt aus der Wertung (gilt fuer jede Quelle mit Verdeckung).
        internal static int LeiseGestaucht; internal static double LeiseSummeDb;

        static void LautstaerkeNach(SpatialAudioOccluder __instance, BetterSource source, Vector3 sourcePos)
        {
            // hallsend (Fund 1): ohne Physik-Modus gilt keine Aufteilung -> ein angehobener Send dieses Klangs gilt nicht mehr
            if (!An) { SendZurueck(source); return; }
            long t0 = Laufzeit.Jetzt;     // 0.8.5: Zeitmessung (Diagnosezeile der Raumwege)
            try { LautstaerkeKern(__instance, source, sourcePos); }
            finally { Raumweg.ZeitLaut.Dazu(t0, Time.frameCount); }
        }

        static void LautstaerkeKern(SpatialAudioOccluder __instance, BetterSource source, Vector3 sourcePos)
        {
            try
            {
                // diffusflattern (17.11.): ruhende Quelle -> zuletzt beim Spielen angewandtes Ziel desselben Behaelters (QuellRuheKern.cs)
                bool spielt = true; QuellRuheKern.Merk rm = null; int beh = 0;
                if (RuheHalten != null && RuheHalten.Value && source != null)
                {
                    spielt = QuellRuheKern.Spielt(source.PlayBackState == BetterSource.EPlayBackState.Playing, source.source1 != null && source.source1.isPlaying);
                    beh = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(__instance);
                    rm = RuheTab.GetValue(source, RuheNeu);
                    if (QuellRuheKern.Halten(spielt, rm, beh))
                    {
                        __instance.CurrentParameters.OcclusionVolumeFactor = rm.Faktor;
                        rm.GehaltenBild = Time.frameCount; RuheGehalten++;
                        if (__instance is CombinedSpatialAudioOccluder combR) TaktVon(combR).Angewandt(source.GetInstanceID(), rm.Db, Time.frameCount);
                        return;
                    }
                }
                // 0.8.4: physikalischer Raumweg -> Faktor draussen/drinnen der Klanggruppe entfaellt (Raumweg.cs)
                // Gesamtreview: keine Strahlmessung (nur einseitige Treffer / zwei Objekte) -> Spielwert, keine volle Dynamik
                // hallsend (Fund 1): auch hier keine Aufteilung -> Send auf den Preset-Wert (sonst behielte der Klang den Send eines Vorgaengers)
                if (Wege.TUnbestimmt(__instance)) { Wege.TUnbestimmtZahl++; TaktVergessen(__instance, source); SendZurueck(source); return; }
                Raumweg.UmweltEntfernen(__instance);
                float w = __instance.OcclusionEffect;
                // 10.11. (Review B5): Hoerbarkeit am ECHTEN Ort der Quelle (Richtung.Echt), wie der Filter (FilterKern) und das Klang-Log.
                // Bis 10.11. hier sourcePos = Container-Position: Lautstaerke und Tiefpass rechneten mit verschiedenen Abstaenden
                // (Speech "prewarm_sound" bei (0,0,0), Container beim Bot: -12,3 statt -9,5 dB). sourcePos bleibt nur als Patch-Parameter.
                float db = SollDb(__instance, source, w, true);
                // diffus-hall (10.11.): nur der gerichtete Anteil trocken, der Diffusanteil ueber den Reverb-Send der Quelle (DiffusKern.cs)
                db = Trocken(__instance, source, w, db, true);
                // dB -> Amplitudenfaktor 10^(-dB/20) (Schalldruck).
                __instance.CurrentParameters.OcclusionVolumeFactor = Mathf.Pow(10f, -db / 20f);
                if (rm != null && spielt) rm.Gespielt(__instance.CurrentParameters.OcclusionVolumeFactor, db, beh);   // diffusflattern
                Lautstaerken++; SummeDb += db;
                // 10.11. (Review B1): angewandte Daempfung merken - der gemerkte Weg des Spiels wird daran gemessen (GemerktVor)
                if (source != null && __instance is CombinedSpatialAudioOccluder comb)
                    TaktVon(comb).Angewandt(source.GetInstanceID(), db, Time.frameCount);
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>Daempfung (dB), die die Quelle bei Wirkung w (schon ohne Faktor draussen/drinnen) bekommen soll - die eine Rechnung
        /// fuer den vollen Weg (LautstaerkeKern) und die Pruefung des gemerkten Wegs (GemerktVor). Ort: echter Ort der Quelle (B5).
        /// zaehlen = Diagnosezaehler fuehren (nur beim Anwenden, nicht beim Pruefen).</summary>
        static float SollDb(SpatialAudioOccluder occ, BetterSource source, float w, bool zaehlen)
        {
            // 0.8.6 Kennlinie: Daemmung im echten Pegelraum, gestaucht nur, soweit die Quelle real ueber dem Knie liegt
            // (Hoerbarkeit.Gestaucht: Lautheit x [K(L_frei) - K(L_frei - D)]; Pegel unbekannt: D x Lautheit wie bisher).
            var lt0 = occ.ListenerTransform;
            int typ0 = source != null && source.Preset != null ? (int)source.Preset.Type : -1;
            Vector3 ort = source != null ? Richtung.Echt(source) : (occ.SourceContainer != null ? occ.SourceContainer.CurrentPosition : Vector3.zero);
            float ab0 = lt0 != null ? Vector3.Distance(ort, lt0.position) : 1f;
            double korr0 = source != null ? Kalibrierung.ClipKorrektur(typ0, source._lastPlayingClipName) : 0;   // 10.11. (B1): Pegel des Clips
            // explosionfern (17.11.): Explosionen mit der Steigung ihrer eigenen Abstandskurve statt der Schuss-Steigung (KennGruppeKern.cs)
            double s0 = KennGruppeLauf.Steigung(source, typ0, ab0, Dynamik.Value * w, KennSteigung.Value, zaehlen);
            float db = (float)Hoerbarkeit.Gestaucht(typ0, typ0 == 0 && SebAkustik.Gedaempft(source), ab0, Dynamik.Value * w, Lautheit.Value,
                                                    KennKnie.Value, s0, korr0);
            // 0.8.5: Hoerbarkeit je Band (RaumwegKern.cs, Hoerbarkeit.Rechnen) - Lautstaerke = lautestes hoerbares Band
            if (Hoeren(occ, source, ort, w, out _))
            {
                float neu = (float)HoerErg.Lautstaerke;
                if (zaehlen)
                {
                    if (neu > db + 0.05f) { LeiseGestaucht++; LeiseSummeDb += neu - db; }
                    if (HoerErg.Stumm) LeiseStumm++;
                    else if (!HoerErg.Hoerbar(1) && !HoerErg.Hoerbar(2)) LeiseNurTief++;
                }
                db = neu;
            }
            // lampenpegel (18.11.): Lampen spielen ihren realen Pegel (Kalibrierung.LampeDb: Ist(d) - Soll(d) - Raumfeld/Weg) statt des
            // Spielpegels minus Weg. Hier, damit voller Weg (LautstaerkeKern) und gemerkter Weg (GemerktVor) dasselbe Ziel haben.
            db = Kalibrierung.LampeDb(occ, source, ab0, db);
            return db;
        }

        // ---------------------------------------------------------------- diffus-hall (10.11.): Diffusanteil als Hall
        // Ursache und Begruendung: DiffusKern.cs. Hier nur der Spiel-Teil: Stellgroessen sind die Lautstaerke (OcclusionVolumeFactor ->
        // source1.volume, wirkt vor dem Spatializer auf Direkt- UND Hallweg) und der Meta-Reverb-Send der Quelle (BetterSource.ReverbSendDB
        // -> MetaXRAudioSource.ReverbSendDb, Parameter 11, nur vor dem spaeten Hall; Hall-Zwilling ReverbSimple/SuperSource: dessen eigener
        // Spatializer). Der Send wird je Anwenden aus dem Preset-Grundwert + Zusatz gesetzt (das Spiel setzt ihn nur beim SetPreset zurueck).

        internal static int DiffusAngewandt, DiffusOhneHall, DiffusGedeckelt;
        internal static double DiffusSummeDb;

        // hallsend (Fund 1): Besitz des angehobenen Sends je Quelle (DiffusKern.SendBesitz). Angelegt nur beim Anheben (keine Allokation je Klang).
        static readonly ConditionalWeakTable<BetterSource, DiffusKern.SendBesitz> SendTab = new ConditionalWeakTable<BetterSource, DiffusKern.SendBesitz>();
        static readonly ConditionalWeakTable<BetterSource, DiffusKern.SendBesitz>.CreateValueCallback SendNeu = _ => new DiffusKern.SendBesitz();
        internal static int SendFreigegeben, SendZurueckGesetzt;

        /// <summary>hallsend (Fund 1): Send aller Spatializer der Quelle auf den Preset-Wert, wie BetterSource/ReverbSimpleSource/
        /// ReverbSuperSource.SetPreset ihn setzen (mono: Spatializer der Quelle, bei SuperSource auch der zweite; stereo: Hall-Zwillinge).</summary>
        static void SendPreset(BetterSource q)
        {
            var ak = q.Preset.acousticSettings;
            q.ReverbSendDB = ak.mono.reverbSendDb;
            float st = ak.stereo.reverbSendDb;
            if (q is Audio.ReverbSubsystem.ReverbSimpleSource rs && rs._spatializer != null) rs._spatializer.ReverbSendDB = st;
            else if (q is Audio.ReverbSubsystem.ReverbSuperSource ss)
            {
                if (ss._spatializerA != null) ss._spatializerA.ReverbSendDB = st;
                if (ss._spatializerB != null) ss._spatializerB.ReverbSendDB = st;
            }
        }

        /// <summary>hallsend (Fund 1): Klang ohne Aufteilung - war der Send angehoben, zurueck auf den Preset-Wert.</summary>
        static void SendZurueck(BetterSource q)
        {
            try
            {
                FilterVergessen(q);   // knacken: ohne Aufteilung auch keine Bandkurve des trockenen Teils mehr
                if (q != null && q.Preset != null && SendTab.TryGetValue(q, out var b) && b.Zurueck())
                {
                    SendZurueckGesetzt++;
                    // sendsprung (12.11.): laufender Klang -> zum Preset GLEITEN (SendGleitKern.cs), sonst hart wie bisher
                    if (SendGleitTab.TryGetValue(q, out var g) && g.Hat && !SendSofort(q))
                    {
                        SendSchreiben(q, g.Raeumlich, (float)g.Setzen(0, false, SendUhr, SendTau));
                        SendGeglitten++;
                    }
                    else { SendPreset(q); if (g != null) g.Vergessen(); }
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>hallsend (Fund 1): aus dem Prefix BetterSource.Release (Richtung.FreigabeVor) - der Klang endet, die Quelle geht an
        /// den Pool. Das Spiel setzt den Send beim naechsten BorrowSource nicht zurueck (kein SetPreset) -> hier, solange der Preset noch
        /// der des alten Klangs ist.</summary>
        internal static void SendFreigeben(BetterSource q)
        {
            try
            {
                FilterVergessen(q);   // knacken: die Korrektur gehoert dem Klang, nicht dem Pool-Objekt (wie der Send)
                if (q == null || q.Preset == null) return;
                bool besitz = SendTab.TryGetValue(q, out var b) && b.Zurueck();
                // sendsprung (12.11.): auch ein noch gleitender Send (SendZurueck lief schon, Ziel Preset nicht erreicht) gehoert dem Klang.
                // Hart: Release ruft danach Stop() (BetterSource.cs:331) - es kommt kein Signal mehr in den Hall.
                bool gleitet = SendGleitTab.TryGetValue(q, out var g) && g.Angehoben;
                if (g != null) g.Vergessen();
                if (besitz || gleitet) { SendPreset(q); SendFreigegeben++; }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>Hat die Quelle einen Hallweg, ueber den der Diffusanteil hoerbar wird? spatial = Meta-Spatializer der Quelle selbst,
        /// sonst der Hall-Zwilling (das Spiel spielt ihn nur, wenn die Quelle nicht raeumlich ist: ReverbSimpleSource.Play/PlayScheduled).</summary>
        static bool Hallweg(BetterSource q, out bool spatial)
        {
            spatial = false;
            if (q == null || q.Preset == null || !(q.Spatializer is Audio.SpatialSystem.MetaSpatialAudioSource) || !q.EnableReverb) return false;
            spatial = q.EnableSpatialization;
            if (spatial) return true;
            return q is Audio.ReverbSubsystem.ReverbSimpleSource rs ? rs._spatializer != null
                 : q is Audio.ReverbSubsystem.ReverbSuperSource ss && ss._spatializerA != null;
        }

        // sendsprung (12.11.): Gleiter je Quelle (SendGleitKern.cs). Angelegt beim ersten Setzen einer Quelle (Pool-Objekt, keine Allokation je Klang).
        static readonly ConditionalWeakTable<BetterSource, SendGleiter> SendGleitTab = new ConditionalWeakTable<BetterSource, SendGleiter>();
        static readonly ConditionalWeakTable<BetterSource, SendGleiter>.CreateValueCallback SendGleitNeu = _ => new SendGleiter();
        internal static int SendGeglitten;
        // dieselbe Zeitkonstante und Uhr wie der Routen-Gleiter (Raumweg.GleitTau/GleitJetzt: Blende/3, Standard 100 ms)
        static double SendTau => RoutenGleiter.Tau(Wege.Summe && Wege.BlendMs != null ? Wege.BlendMs.Value / 1000.0 : 0.0);
        static double SendUhr => Time.unscaledTimeAsDouble;

        /// <summary>Darf der Send dieser Quelle sofort springen? Dieselbe Regel wie fuer den Filter (Stetigkeit.SofortZulassen): still oder
        /// gerade gestartet; Endlos-Klaenge gelten waehrend des Laufens nie als frisch.</summary>
        static bool SendSofort(BetterSource q)
        {
            var a = q.source1;
            if (a == null) return true;
            return Stetigkeit.SofortZulassen(true, a.isPlaying, a.time, a.loop, a.loop);
        }

        /// <summary>sendsprung: Ziel setzen, geschrieben wird der geglittene Wert (SendNach schreibt ihn je Bild fort).</summary>
        static void SendSetzen(BetterSource q, bool spatial, float zusatz)
        {
            var g = SendGleitTab.GetValue(q, SendGleitNeu);
            // Wechsel des tragenden Spatializers (raeumlich <-> Hall-Zwilling): der andere Weg hat keinen Verlauf -> sofort
            bool sofort = SendSofort(q) || (g.Hat && g.Raeumlich != spatial);
            g.Raeumlich = spatial;
            SendSchreiben(q, spatial, (float)g.Setzen(zusatz, sofort, SendUhr, SendTau));
        }

        /// <summary>sendsprung: Postfix SpatialAudioOccluder.ApplyOcclusionToSource - laeuft im vollen UND im gemerkten Bild
        /// (Dekompilat CombinedSpatialAudioOccluder.cs:162-164/190-207) und schreibt einen gleitenden Send fort.</summary>
        static void SendNach(BetterSource source)
        {
            try
            {
                if (source == null || source.Preset == null || !SendGleitTab.TryGetValue(source, out var g) || !g.Laeuft) return;
                SendSchreiben(source, g.Raeumlich, (float)g.Fortschreiben(SendUhr, SendTau));
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>sendsprung: Send, der gerade am tragenden Spatializer steht (fuer das Klang-Log, je QUELLE). NaN = kein Meta-Hallweg.</summary>
        internal static float SendJetzt(BetterSource q, out float preset)
        {
            preset = float.NaN;
            try
            {
                if (q == null || q.Preset == null || !Hallweg(q, out bool spatial)) return float.NaN;
                var ak = q.Preset.acousticSettings;
                if (spatial) { preset = ak.mono.reverbSendDb; return q.ReverbSendDB; }
                preset = ak.stereo.reverbSendDb;
                if (q is Audio.ReverbSubsystem.ReverbSimpleSource rs && rs._spatializer != null) return rs._spatializer.ReverbSendDB;
                if (q is Audio.ReverbSubsystem.ReverbSuperSource ss && ss._spatializerA != null) return ss._spatializerA.ReverbSendDB;
            }
            catch (Exception e) { Statistik.Fehler(e); }
            return float.NaN;
        }

        static void SendSchreiben(BetterSource q, bool spatial, float zusatz)
        {
            var ak = q.Preset.acousticSettings;
            if (spatial) { q.ReverbSendDB = ak.mono.reverbSendDb + zusatz; return; }
            float st = ak.stereo.reverbSendDb + zusatz;
            if (q is Audio.ReverbSubsystem.ReverbSimpleSource rs && rs._spatializer != null) rs._spatializer.ReverbSendDB = st;
            else if (q is Audio.ReverbSubsystem.ReverbSuperSource ss)
            {
                if (ss._spatializerA != null) ss._spatializerA.ReverbSendDB = st;
                if (ss._spatializerB != null) ss._spatializerB.ReverbSendDB = st;
            }
            q.ReverbSendDB = ak.mono.reverbSendDb;   // Spatializer der Quelle selbst (falls sie wieder raeumlich wird) ohne Zusatz
        }

        // hallsend (A2): Zaehler fuer die Diagnosezeile
        internal static int RaumHallAngewandt, RaumHallGedeckelt;
        internal static double RaumHallSummeDb, RaumHallFehlDb;
        // hallsend (B2): Zusatz je Band (Bezug/1k/4k) aus den DiffusZusatz-Funktionen (Hauptfaden, ein Anwenden zur Zeit)
        static readonly double[] zusatzB = new double[3];
        // hallsend (B2): Bandkorrektur des trockenen Signals (dB zusaetzlich zur Bandkurve der Summe, DiffusKern.TrockenBaender).
        // kurveTrocken = Kurve() rechnet gerade fuer das TROCKENE Signal; filter* = gilt fuer das Filter dieses Anwendens (FilterKern).
        static bool kurveTrocken;
        static double kurveK1, kurveK2;
        // knacken (12.11.): Bandkorrektur je Quelle statt nur fuer das Bild des vollen Anwendens (TrockenFilterKern.cs) - sonst rechnete das
        // gemerkte Bild des Spiels den Tiefpass mit der Kurve der Summe und der Zielwert wechselte Bild fuer Bild. Angelegt beim ersten
        // Aufteilen einer Quelle (Pool-Objekt, keine Allokation je Klang).
        static readonly ConditionalWeakTable<BetterSource, TrockenFilter> FilterTab = new ConditionalWeakTable<BetterSource, TrockenFilter>();
        static readonly ConditionalWeakTable<BetterSource, TrockenFilter>.CreateValueCallback FilterNeu = _ => new TrockenFilter();
        static void FilterVergessen(BetterSource q)
        {
            if (q != null && FilterTab.TryGetValue(q, out var t)) t.Loeschen();
            if (q != null && HoerbarTab.TryGetValue(q, out var h)) h.Vergessen();   // diffushoerbar: Logfeld gilt nur, solange aufgeteilt wird
        }

        // diffusflattern (17.11.): gemerktes Ziel je Quelle (Pool-Objekt, angelegt beim ersten Anwenden, keine Allokation je Klang)
        static readonly ConditionalWeakTable<BetterSource, QuellRuheKern.Merk> RuheTab = new ConditionalWeakTable<BetterSource, QuellRuheKern.Merk>();
        static readonly ConditionalWeakTable<BetterSource, QuellRuheKern.Merk>.CreateValueCallback RuheNeu = _ => new QuellRuheKern.Merk();
        internal static int RuheGehalten;

        /// <summary>diffusflattern: wuerde diese Quelle jetzt gehalten (ruht, Ziel aus dem Spielen im selben Behaelter)?</summary>
        static bool RuhtGehalten(SpatialAudioOccluder occ, BetterSource q)
        {
            if (RuheHalten == null || !RuheHalten.Value || q == null || !RuheTab.TryGetValue(q, out var m)) return false;
            bool spielt = QuellRuheKern.Spielt(q.PlayBackState == BetterSource.EPlayBackState.Playing, q.source1 != null && q.source1.isPlaying);
            return QuellRuheKern.Halten(spielt, m, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(occ));
        }

        /// <summary>diffusflattern: Klang-Log-Feld " | level held (...)", wenn die Quelle in diesem Bild gehalten wurde.</summary>
        internal static string RuheFeld(BetterSource q)
        {
            try { return q != null && RuheTab.TryGetValue(q, out var m) ? QuellRuheKern.LogFeld(m, Time.frameCount) : null; }
            catch (Exception e) { Statistik.Fehler(e); return null; }
        }

        // diffushoerbar (17.11.): trocken gespielter Diffusanteil je Quelle fuer das Klang-Log (Pool-Objekt, keine Allokation je Klang)
        static readonly ConditionalWeakTable<BetterSource, DiffusHoerbarKern.Merk> HoerbarTab = new ConditionalWeakTable<BetterSource, DiffusHoerbarKern.Merk>();
        static readonly ConditionalWeakTable<BetterSource, DiffusHoerbarKern.Merk>.CreateValueCallback HoerbarNeu = _ => new DiffusHoerbarKern.Merk();
        internal static int HoerbarAngewandt; internal static double HoerbarSummeDb;

        /// <summary>diffushoerbar: Klang-Log-Feld der Quelle (" | diffuse dry ..."), null = keine Aufteilung mit hoerbarem Diffusanteil.</summary>
        internal static string DiffusTrockenFeld(BetterSource q)
        {
            try { return q != null && HoerbarTab.TryGetValue(q, out var h) ? DiffusHoerbarKern.LogFeld(h.TrockenDb, h.PlusDb, h.EreignisDb, h.Spaet, h.SignalS, h.Sprache) : null; }
            catch (Exception e) { Statistik.Fehler(e); return null; }
        }

        // ---------------------------------------------------------------- tunneldiffus (17.11.2026): Ereignispegel (ZeitKern)
        /// <summary>Ereignis-Abzug je Quelle (Pool-Objekt, keine Allokation je Klang): rechnet ZeitKern nur neu, wenn sich Zeitstruktur,
        /// Signaldauer oder Routenanteil geaendert haben (Goldener Schnitt ~250 exp je Teil und Band).</summary>
        internal sealed class EreignisMerk
        {
            ZeitKern.Baender alt; double altTau = double.NaN, altRho = double.NaN; bool hat, altSprache;
            public double Db0, Db1, Db2, Spaet, TauS;
            public bool Sprache;
            /// <summary>nachhallstimme: gespielte dB, um die der Ereignis-/Sprachabzug das trockene Signal beim letzten Anwenden leiser gemacht hat
            /// (0 = keiner). Zweitstimme nimmt ihn zurueck, wenn eine Nachhall-Stimme den spaeten Teil traegt.</summary>
            public float AbzugDb;
            public void Rechnen(in ZeitKern.Baender zeit, double tauS, double rho, bool sprache = false)
            {
                if (hat && sprache == altSprache && Gleich(zeit, alt) && zeit.Frueh == alt.Frueh && zeit.HatFrueh == alt.HatFrueh
                    && (tauS == altTau || (double.IsPositiveInfinity(tauS) && double.IsPositiveInfinity(altTau)) || (double.IsNaN(tauS) && double.IsNaN(altTau))) && rho == altRho) return;
                alt = zeit; altTau = tauS; altRho = rho; hat = true; TauS = tauS; altSprache = sprache; Sprache = sprache;
                rho = rho > 0 ? (rho < 1 ? rho : 1) : 0;
                if (sprache)
                {
                    // tunneldiffus Teil 2: nuetzlicher Anteil (D50-Fenster, exakte Kette, 1 kHz) - breitbandig (Annahme: Bandform wie die Summe)
                    double fr = zeit.HatFrueh ? Math.Max(1e-6, Math.Min(1.0, zeit.Frueh)) : 1.0;
                    Db0 = Db1 = Db2 = ZeitKern.Db(1 - rho + rho * fr);
                    Spaet = rho * (1 - fr);
                    return;
                }
                double r0 = ZeitKern.Ereignis(zeit.L0, zeit.S0, tauS, out _, out _);
                double r1 = ZeitKern.Ereignis(zeit.L1, zeit.S1, tauS, out _, out _);
                double r2 = ZeitKern.Ereignis(zeit.L2, zeit.S2, tauS, out _, out _);
                // Wand zwischen Raeumen (ohne Zeitstruktur) zaehlt unverschmiert: r = (1 - rho) + rho r_Route
                Db0 = ZeitKern.Db(1 - rho + rho * r0); Db1 = ZeitKern.Db(1 - rho + rho * r1); Db2 = ZeitKern.Db(1 - rho + rho * r2);
                Spaet = rho * (Math.Max(0, zeit.L0.E) + Math.Max(0, zeit.S0.E));
            }
            static bool Gleich(in ZeitKern.Baender a, in ZeitKern.Baender b)
                => G(a.L0, b.L0) && G(a.L1, b.L1) && G(a.L2, b.L2) && G(a.S0, b.S0) && G(a.S1, b.S1) && G(a.S2, b.S2);
            static bool G(in ZeitKern.Teil a, in ZeitKern.Teil b) => a.E == b.E && a.M1 == b.M1 && a.M2 == b.M2;
        }
        static readonly ConditionalWeakTable<BetterSource, EreignisMerk> EreignisTab = new ConditionalWeakTable<BetterSource, EreignisMerk>();
        static readonly ConditionalWeakTable<BetterSource, EreignisMerk>.CreateValueCallback EreignisNeu = _ => new EreignisMerk();
        internal static int EreignisAngewandt; internal static double EreignisSummeDb;
        internal static int NachhallStattAbzug;   // nachhallstimme: Anwendungen, bei denen die Nachhall-Stimme den Abzug ersetzt hat

        /// <summary>nachhallstimme: Abzug (gespielte dB, &gt;= 0), der im zuletzt angewandten Lautstaerke-Faktor der Quelle steckt.</summary>
        internal static double AbzugDb(BetterSource q)
            => q != null && EreignisTab.TryGetValue(q, out var m) && m.AbzugDb > 0 ? m.AbzugDb : 0.0;

        /// <summary>tunneldiffus Teil 2: Sprach-Gruppen des Spiels (BetterAudio.AudioSourceGroupType Speech, Voip, VehicleInSpeech).</summary>
        static bool Sprache(BetterSource q)
        {
            var p = q != null ? q.Preset : null;
            if (p == null) return false;
            var t = p.Type;
            return t == BetterAudio.AudioSourceGroupType.Speech || t == BetterAudio.AudioSourceGroupType.Voip || t == BetterAudio.AudioSourceGroupType.VehicleInSpeech;
        }

        static System.Collections.Generic.Dictionary<string, double> clipTau;
        static readonly System.Collections.Generic.Dictionary<int, double> clipTauId = new System.Collections.Generic.Dictionary<int, double>(256);

        /// <summary>tunneldiffus: Signaldauer tau_s (s) des gerade gespielten Clips (ZeitKern.SignalDauer aus tau_eff der Clip-Tabelle
        /// ClipDauerDaten). Schleife oder unbekannter Clip: unendlich (Dauerklang -> kein Abzug, wie bisher).</summary>
        static double SignalDauer(BetterSource q)
        {
            var a = q.source1;
            if (a != null && a.loop) return double.PositiveInfinity;
            if (clipTau == null) clipTau = ClipTauLesen();
            double te = double.NaN;
            string n = q._lastPlayingClipName;   // BetterSource.Play setzt ihn (keine Allokation)
            if (!string.IsNullOrEmpty(n)) { if (clipTau.TryGetValue(n, out double t)) te = t; }
            else if (a != null && a.clip != null && a.isPlaying)
            {
                // geplante Klaenge (PlayScheduled, Schuesse) ohne Namen: Clip der Quelle, Name einmal je Clip
                int id = a.clip.GetInstanceID();
                if (!clipTauId.TryGetValue(id, out te))
                {
                    if (!clipTau.TryGetValue(a.clip.name, out te)) te = double.NaN;
                    if (clipTauId.Count > 4096) clipTauId.Clear();
                    clipTauId[id] = te;
                }
            }
            return ZeitKern.SignalDauer(te);
        }

        static System.Collections.Generic.Dictionary<string, double> ClipTauLesen()
        {
            var d = new System.Collections.Generic.Dictionary<string, double>(ClipDauerDaten.Anzahl + 16, StringComparer.Ordinal);
            try
            {
                foreach (var z in ClipDauerDaten.Tabelle.Split('\n'))
                {
                    int t = z.IndexOf('\t');
                    if (t <= 0) continue;
                    if (int.TryParse(z.Substring(t + 1).Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int ms))
                        d[z.Substring(0, t)] = ms / 1000.0;
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
            return d;
        }
        internal static int TrockenFarbe; internal static double TrockenFarbeSumme;

        /// <summary>Daempfung des trockenen Signals (dB). dbGesamt = Soll der Summe (SollDb). anwenden = Send setzen und zaehlen (sonst nur
        /// rechnen: Pruefung des gemerkten Wegs). Ohne Schalter, ohne Diffusanteil oder ohne Hallweg: dbGesamt (wie bis 0.8.6).</summary>
        static float Trocken(SpatialAudioOccluder occ, BetterSource q, float w, float dbGesamt, bool anwenden)
        {
            try
            {
                // nachhallstimme: Abzug gilt nur fuer dieses Anwenden (jeder Rueckweg unten ohne Abzug laesst 0 stehen)
                if (anwenden && q != null && EreignisTab.TryGetValue(q, out var em0)) em0.AbzugDb = 0f;
                bool schalter = DiffusAlsHall != null && DiffusAlsHall.Value && q != null;
                // ---- hallsend (A2, 12.11.): Hindernis im selben gemessenen Raum - trocken = Hindernisdaempfung (dbGesamt, unveraendert),
                // Hall-Eingang = unverdeckter Pegel (DiffusKern.RaumHall). Der Regler "Diffuse reverb level" gilt hier NICHT: er gleicht den Hall
                // aus ANDEREN Raeumen ab; Soll ist hier "Hall wie ohne Hindernis" (sonst laege der Hall um den Regler unter dem freien Fall).
                if (schalter && RaumHallAn != null && RaumHallAn.Value && Wege.RaumHallBleibt(occ, out var wzR) && Hallweg(q, out bool spR))
                {
                    if (anwenden)
                    {
                        float dbHall = w > 0f ? SollDb(occ, q, 0f, false) : dbGesamt;   // Daempfung ohne Hindernis (normal 0 dB)
                        var akR = q.Preset.acousticSettings;
                        double basisR = spR ? akR.mono.reverbSendDb : akR.stereo.reverbSendDb;
                        DiffusKern.RaumHall(dbGesamt, dbHall, basisR, out double sendR, out double fehl);
                        SendSetzen(q, spR, (float)(sendR - basisR));
                        SendTab.GetValue(q, SendNeu).Erhoeht();
                        RaumHallAngewandt++; RaumHallSummeDb += sendR - basisR;
                        wzR.RaumHallSend = sendR - basisR; wzR.RaumHallFehl = fehl;
                        if (fehl > 0.05) { RaumHallGedeckelt++; RaumHallFehlDb += fehl; }
                        FilterVergessen(q);   // knacken: trocken = Summe hinter dem Hindernis -> Filter nach der Kurve der Summe
                    }
                    return dbGesamt;
                }
                Raumweg.Zustand z = null;
                WandZwischenRaeumen.DiffusMerk wm = null;
                double zusatz = 0;
                zusatzB[0] = zusatzB[1] = zusatzB[2] = 0;
                // galpegel (10.11.): zuerst der Raumweg MIT Wand zwischen Raeumen (beide Wege verknuepft), sonst Route allein, sonst Hindernisweg
                bool an = schalter
                          && (WandZwischenRaeumen.DiffusZusatz(occ, out zusatz, out z, out wm, zusatzB) || Raumweg.DiffusZusatz(occ, out zusatz, out z, zusatzB)
                              || Wege.DiffusZusatz(occ, out zusatz, out _, zusatzB));
                bool hall = an && zusatz > 0.01 && Hallweg(q, out bool spatial0);
                if (!hall)
                {
                    if (anwenden && q != null)
                    {
                        if (an && zusatz > 0.01) DiffusOhneHall++;
                        SendZurueck(q);   // hallsend (Fund 1): Besitz-Regel wie bei der Freigabe
                        FilterVergessen(q);   // knacken: ohne Aufteilung gilt die Kurve der Summe, auch in den gemerkten Bildern
                        if (z != null) { z.DTrocken = float.NaN; z.DSend = float.NaN; }
                        if (wm != null) { wm.Trocken = float.NaN; wm.Send = float.NaN; }
                    }
                    return dbGesamt;
                }
                Hallweg(q, out bool spatial);
                float dyn = Dynamik.Value;
                // gerichteter Anteil = Summe um 'zusatz' dB leiser -> gleiche Hoerbarkeits-/Kennlinienrechnung wie die Summe (SollDb).
                // hallsend (B2, 12.11.): Bandform des gerichteten Anteils je Band (bis 12.11. ANNAHME "wie die Summe"): Diffusanteil je Band
                // -> Mehrdaempfung des trockenen Teils bei 1k/4k gegenueber der Summe (DiffusKern.TrockenBaender, kappa = 0: nur G).
                double a0 = DiffusKern.AnteilAusZusatz(zusatzB[0] > 0 ? zusatzB[0] : zusatz), a1 = DiffusKern.AnteilAusZusatz(zusatzB[1]), a2 = DiffusKern.AnteilAusZusatz(zusatzB[2]);
                DiffusKern.TrockenBaender(a0, a1, a2, DiffusKern.TrockenZusatzDb(a0), out kurveK1, out kurveK2);
                float wT = Mathf.Clamp01(w + (float)(zusatz / dyn));
                float dbT;
                kurveTrocken = true;
                try { dbT = SollDb(occ, q, wT, false); }
                finally { kurveTrocken = false; }
                var ak = q.Preset.acousticSettings;
                double basis = spatial ? ak.mono.reverbSendDb : ak.stereo.reverbSendDb;
                double dbAus, send;
                // diffushoerbar (17.11.): Metas Hall macht den Diffusanteil nicht hoerbar (Messung 17.11.) -> trocken bleibt die Summe abzueglich
                // dessen, was der Hall wirklich traegt; Hall-Eingang wie seit 10.11. (DiffusHoerbarKern). send = Wert VOR dem Regler (wie unten).
                bool hoerbar = DiffusHoerbar != null && DiffusHoerbar.Value;
                double dTrocken = double.NaN;
                // tunneldiffus (17.11.): trocken nur, was im Ereignispegel ankommt (ZeitKern; Zeitstruktur der Routen x Dauer des Clips).
                // dbE = gespielte Daempfung des Ereignisses (gleiche Kennlinie wie die Summe), Bandform aus dem Ereignis-Abzug je Band.
                double dbE = double.NaN;
                EreignisMerk em = null;
                // tunneldiffus Teil 2: Sprache (Gruppen Speech/Voip/VehicleInSpeech) -> frueher Anteil (D50) statt Ereignispegel
                bool sprache = Sprache(q);
                bool zeitAn = sprache ? SpracheFrueh != null && SpracheFrueh.Value : DiffusEreignis != null && DiffusEreignis.Value;
                // nachhallstimme: traegt eine Nachhall-Stimme den verschmierten Teil dieser Quelle, gibt es KEINEN Abzug (das Gehoer bewertet den
                // gespielten Nachhall selbst) - die eine Stelle, die das entscheidet, ist Zweitstimme.NachhallWirkt
                bool nachhall = zeitAn && Zweitstimme.NachhallWirkt(q);
                if (nachhall && anwenden) NachhallStattAbzug++;
                if (hoerbar && zeitAn && !nachhall && Raumweg.ZeitDaten(z, out var zeit))
                {
                    em = EreignisTab.GetValue(q, EreignisNeu);
                    em.Rechnen(zeit, sprache ? double.NaN : SignalDauer(q), wm != null ? wm.Route : 1.0, sprache);
                    if (em.Db0 > 0.01)
                    {
                        float wE = Mathf.Clamp01(w + (float)(em.Db0 / dyn));
                        double k1Alt = kurveK1, k2Alt = kurveK2;
                        kurveK1 = em.Db1 - em.Db0; kurveK2 = em.Db2 - em.Db0;
                        kurveTrocken = true;
                        try { dbE = SollDb(occ, q, wE, false); }
                        finally { kurveTrocken = false; kurveK1 = k1Alt; kurveK2 = k2Alt; }
                        if (!(dbE >= dbGesamt)) dbE = dbGesamt;
                    }
                }
                if (hoerbar)
                {
                    DiffusHoerbarKern.Aufteilen(dbGesamt, dbT, basis, DiffusHallPegel.Value, MetaHallDb.Value, dbE, out dbAus, out double sendNeu, out dTrocken, out _);
                    send = sendNeu - DiffusHallPegel.Value;
                    // nachhallstimme: wie viel der Abzug gekostet hat (gespielte dB) - dieselbe Aufteilung ohne Ereignis
                    if (anwenden && em != null && !double.IsNaN(dbE))
                    {
                        DiffusHoerbarKern.Aufteilen(dbGesamt, dbT, basis, DiffusHallPegel.Value, MetaHallDb.Value, double.NaN, out double dbOhne, out _, out _, out _);
                        em.AbzugDb = (float)Math.Max(0, dbAus - dbOhne);
                    }
                }
                else DiffusKern.Aufteilen(dbGesamt, dbT, basis, out dbAus, out send);
                if (anwenden)
                {
                    float plus = (float)(send - basis);
                    // 10.11. (Kaan im Raid: "der Hall ist etwas uebertrieben"): wie laut der Hall des Spiels je dB Send wirklich ist, laesst
                    // sich nicht belegen (DiffusKern-Bericht: absolute Hoehe des Meta-Halls unbekannt) -> Abgleich nach Gehoer als Regler.
                    // Wirkt nur auf den Hall-Anteil (Send), nicht auf den trockenen Klang; Grenzen des Sends -60...+20 dB bleiben.
                    float sendPlus = Mathf.Clamp(plus + DiffusHallPegel.Value, -60f - (float)basis, (float)DiffusKern.SendMaxDb - (float)basis);
                    SendSetzen(q, spatial, sendPlus);
                    SendTab.GetValue(q, SendNeu).Erhoeht();
                    DiffusAngewandt++; DiffusSummeDb += plus;
                    if (!hoerbar && dbAus < dbT - 0.05) DiffusGedeckelt++;
                    if (hoerbar)
                    {
                        double pl = DiffusHoerbarKern.PlusDb(dbT, dbAus);
                        var hm = HoerbarTab.GetValue(q, HoerbarNeu);
                        hm.Setzen(dTrocken, pl); HoerbarAngewandt++; HoerbarSummeDb += pl;
                        // tunneldiffus: Logfeld "| event -X dB (late Y %, signal Z s)"
                        if (em != null) { hm.EreignisDb = (float)em.Db0; hm.Spaet = (float)em.Spaet; hm.SignalS = em.Sprache ? float.NaN : (float)em.TauS; hm.Sprache = em.Sprache; EreignisAngewandt++; EreignisSummeDb += em.Db0; }
                        else { hm.EreignisDb = hm.Spaet = hm.SignalS = float.NaN; }
                    }
                    else if (HoerbarTab.TryGetValue(q, out var hv)) hv.Vergessen();
                    if (z != null) { z.DTrocken = plus; z.DSend = (float)send; }
                    if (wm != null) { wm.Trocken = plus; wm.Send = (float)send; }
                    // hallsend (B2): Bandkorrektur fuer das Filter dieses Anwendens. Deckelt der Send, bleibt ein Teil von D trocken (kappa > 0).
                    // ANNAHME (Naeherung): angewandter Zusatz im physikalischen Mass = Zusatz x (angewandt / gerechnet) im gespielten Mass
                    // (die Kennlinie staucht beide gleich; exakt nur ohne Stauchung).
                    double zApp = dbT - dbGesamt > 1e-6 ? DiffusKern.TrockenZusatzDb(a0) * Math.Min(1.0, (dbAus - dbGesamt) / (dbT - dbGesamt)) : 0;
                    DiffusKern.TrockenBaender(a0, a1, a2, zApp, out double fk1, out double fk2);
                    // tunneldiffus: mit Ereignis-Abzug ist trocken ~ Ereignis (Metas Anteil < 0,01 dB) -> Bandform = Abzug je Band gegen das Bezugsband
                    // (4 kHz verschmiert weniger: kuerzeres T durch Luft -> trocken heller als die stationaere Summe)
                    if (!double.IsNaN(dbE) && em != null) { fk1 = em.Db1 - em.Db0; fk2 = em.Db2 - em.Db0; }
                    // knacken (12.11.): gilt fuer diese Quelle bis zum naechsten vollen Bild - auch in den gemerkten Bildern dazwischen,
                    // deren Lautstaerke ja ebenfalls die trockene dieses Bilds ist (TrockenFilterKern.cs)
                    FilterTab.GetValue(q, FilterNeu).Setzen(fk1, fk2);
                }
                return (float)dbAus;
            }
            catch (Exception e) { kurveTrocken = false; if (anwenden) FilterVergessen(q); Statistik.Fehler(e); return dbGesamt; }
        }

        // ---------------------------------------------------------------- 10.11. (Review B1): Lautstaerke im gemerkten Weg
        // Ursache und Rechnung: LautTaktKern.cs. Hier nur der Spiel-Teil.

        static readonly ConditionalWeakTable<CombinedSpatialAudioOccluder, LautTakt> TaktTab = new ConditionalWeakTable<CombinedSpatialAudioOccluder, LautTakt>();
        static readonly ConditionalWeakTable<CombinedSpatialAudioOccluder, LautTakt>.CreateValueCallback TaktNeu = _ => new LautTakt();
        static LautTakt TaktVon(CombinedSpatialAudioOccluder c) => TaktTab.GetValue(c, TaktNeu);
        static void TaktVergessen(SpatialAudioOccluder occ, BetterSource q)
        {
            if (q != null && occ is CombinedSpatialAudioOccluder c && TaktTab.TryGetValue(c, out var t)) t.Vergessen(q.GetInstanceID());
        }

        internal static int TaktGeprueft, TaktGehalten, TaktNeuGerechnet, TaktUnbekannt;
        internal static double TaktMaxGehalten, TaktMaxAusgeloest;
        internal static readonly Laufzeit ZeitTakt = new Laufzeit("cached-volume check");

        /// <summary>Fuer das Klang-Log: Abstand angewandt - Soll (dB) der Quelle, im Bild jetzt gemessen. frisch = voll gerechnet.</summary>
        internal static bool TaktAbstand(SpatialAudioOccluder occ, BetterSource q, out float abstand, out bool frisch)
        {
            abstand = float.NaN; frisch = false;
            if (q == null || !(occ is CombinedSpatialAudioOccluder c) || !TaktTab.TryGetValue(c, out var t)) return false;
            if (!t.Abstand(q.GetInstanceID(), Time.frameCount, out double a, out frisch)) return false;
            abstand = (float)a;
            return true;
        }

        /// <summary>Prefix CombinedSpatialAudioOccluder.ApplyCachedParamsToContainer - nur aus dem gemerkten Zweig von
        /// OnUpdateOcclusionEffects erreicht (Dekompilat Z. 155; die Wertung PropagationEffect/ObstructionEffect ist schon fuer diesen
        /// Aufruf geglaettet). Je Quelle mit Merker: Soll-Daempfung fuer den aktuellen Stand gegen die angewandte. Liegt eine hoerbar
        /// daneben, rechnet der Behaelter so voll, wie es der volle Zweig des Spiels tut (Z. 159-164, updateImmediately = false,
        /// base.OnUpdateOcclusionEffects ist leer; UpdateInfo ruft das Spiel danach selbst) und der gemerkte Weg entfaellt.</summary>
        static bool GemerktVor(CombinedSpatialAudioOccluder __instance, AudioSourceContainer container)
        {
            if (!An || !Stetigkeit.An || __instance == null || container == null) return true;
            bool neu = false;
            long t0 = Laufzeit.Jetzt;
            int bild = Time.frameCount;
            float merk = __instance.OcclusionEffect;
            try
            {
                if (!container.IsValid || Wege.TUnbestimmt(__instance)) return true;
                var takt = TaktVon(__instance);
                var raum = container.CurrentAudioRoom;
                var system = __instance.AudioSystem;
                float gesamt = __instance.CalculateTotalEffect();
                double maxAbstand = 0;
                for (int i = 0; i < container.Count; i++)
                {
                    BetterSource q = container[i];
                    if (q == null) continue;
                    int id = q.GetInstanceID();
                    if (!__instance._cachedOcclusionParams.ContainsKey(id)) continue;   // Spiel: nur merken, nicht anwenden (Z. 199-202)
                    if (RuhtGehalten(__instance, q)) continue;   // diffusflattern: ruhende Quelle behaelt ihr Ziel, kein Grund zum Neurechnen
                    // Wirkung wie CalculateFinalOcclusionParams (Z. "OcclusionEffect = Clamp01(CalculateTotalEffect() * GetEnvironmentFactor)"),
                    // dann wie LautstaerkeKern ohne Faktor draussen/drinnen, solange der physikalische Raumweg gilt
                    __instance.OcclusionEffect = Mathf.Clamp01(gesamt * (system != null ? system.GetEnvironmentFactor(raum, q.SpatialSettings) : 1f));
                    Raumweg.UmweltEntfernen(__instance);
                    float soll = SollDb(__instance, q, __instance.OcclusionEffect, false);
                    soll = Trocken(__instance, q, __instance.OcclusionEffect, soll, false);   // diffus-hall: angewandt wird der trockene Wert
                    TaktGeprueft++;
                    if (takt.Pruefen(id, soll, LautTaktKern.SchwelleDb, bild, out double ab))
                    {
                        neu = true;
                        if (double.IsNaN(ab)) TaktUnbekannt++;
                        else if (Math.Abs(ab) > maxAbstand) maxAbstand = Math.Abs(ab);
                    }
                    else if (Math.Abs(ab) > TaktMaxGehalten) TaktMaxGehalten = Math.Abs(ab);
                }
                if (maxAbstand > TaktMaxAusgeloest) TaktMaxAusgeloest = maxAbstand;
            }
            catch (Exception e) { Statistik.Fehler(e); neu = false; }
            finally { __instance.OcclusionEffect = merk; ZeitTakt.Dazu(t0, bild); }
            if (!neu) { TaktGehalten++; return true; }
            try
            {
                Vector3 pos = container.CurrentPosition;
                var raum = container.CurrentAudioRoom;
                for (int i = 0; i < container.Count; i++)
                {
                    BetterSource q = container[i];
                    __instance.CalculateFinalOcclusionParams(raum, q, pos);
                    __instance.ApplyOcclusionToSource(q, false);
                }
                TaktNeuGerechnet++;
                return false;
            }
            catch (Exception e) { Statistik.Fehler(e); return true; }
        }

        // Tiefpass ohne Spiel-Untergrenze; laeuft vor Klangfarbe/Luft (die nur noch weiter absenken).
        // Begruendung: Massengesetz steigt um 6 dB je Oktave -> Hoehen werden staerker gedaempft. Die Abbildung
        // (logarithmisch 22 kHz -> 800 Hz) ist eine eigene Naeherung, keine Formel aus der Literatur.
        // 0.8.4 (Meldung "klingt roboterhaft", Labs): Das Spiel hebt mit der Verdeckung auch den HOCHPASS an
        // (hpfSettings.frequencyCurve(occlusion), im Labs-Log 70 Hz frei -> 300-460 Hz verdeckt). Zusammen mit dem Tiefpass
        // bei 800 Hz blieb ein Band von gut einer Oktave (Q 1 an beiden Kanten) - Telefon-/Blechdosenklang. Das widerspricht
        // dem Massengesetz, das hier den Tiefpass bestimmt: die Daemmung steigt um 6 dB je Oktave, Tiefen kommen also am
        // BESTEN durch die Wand. Im Physik-Modus gilt deshalb der Hochpass des Spiels bei offenem Weg (Verdeckung 0, nur
        // Richtung/Lage wie im Spiel, ohne den Isolations-Zug, den der Tiefpass hier ebenfalls nicht mehr hat).
        static void FilterNach(Audio.Effects.AudioOcclusionEQPreset preset, float occlusion, DirectionalWeights influencedWeights,
            ref float lowPassFreq, ref float highPassFreq)
        {
            if (!An || occlusion <= 0.001f) return;
            long t0 = Laufzeit.Jetzt;     // 0.8.5: Zeitmessung (Diagnosezeile der Raumwege)
            try { FilterKern(preset, occlusion, influencedWeights, ref lowPassFreq, ref highPassFreq); }
            finally { Raumweg.ZeitFilter.Dazu(t0, Time.frameCount); }
        }

        static void FilterKern(Audio.Effects.AudioOcclusionEQPreset preset, float occlusion, DirectionalWeights influencedWeights,
            ref float lowPassFreq, ref float highPassFreq)
        {
            var occ = Raumweg.AnwendOcc;
            if (Wege.TUnbestimmt(occ)) return;   // Gesamtreview: Spielfilter wie die Spiel-Lautstaerke (LautstaerkeKern)
            // hallsend (B2, 12.11.): das Filter wirkt auf das TROCKENE Signal (der Hall-Send liegt davor, MACHBARKEIT Abschn. 1) -> Bandkurve
            // des trockenen Anteils, wenn das letzte volle Anwenden dieser Quelle die Aufteilung gesetzt hat, sonst die der Summe wie bisher.
            // knacken (12.11.): bis dahin nur im Bild des vollen Anwendens (filterBild == frameCount) - im gemerkten Bild des Spiels
            // (ApplyCachedParamsToContainer rechnet den Tiefpass neu, die Lautstaerke nicht) galt die Summe -> Ziel wechselte je Bild.
            var src0 = Raumweg.AnwendQuelle;
            kurveTrocken = src0 != null && FilterTab.TryGetValue(src0, out var tf) && tf.Holen(out kurveK1, out kurveK2);
            // farbekennlinie (12.11.): Stauchungsfaktor der Lautstaerke dieser Quelle (1 = unter dem Knie / Schalter aus)
            double farbe = FarbFaktor(occ, src0, occlusion);
            FarbeLogLoeschen();
            int quelle;
            float lp;
            try { quelle = Kurve(occ, occlusion, kM, kN, out lp, farbe); }
            finally { kurveTrocken = false; }
            if (farbe < 0.9999 && src0 != null)
            {
                FarbeQuelle = src0.GetInstanceID(); FarbeBild = Time.frameCount; FarbeG = (float)farbe;
                FarbeReal4k = (float)kM[2]; FarbeSpiel4k = (float)(kM[2] * farbe);
            }
            if (quelle == 1) Wege.BandFilter++; else if (quelle == 2) Wege.BandFilterRaumweg++;
            // 0.8.5: Tiefpass an die hoerbaren Baender angepasst (nur Tiefen hoerbar -> dumpf); ohne Zusatz exakt lp
            var src = Raumweg.AnwendQuelle;
            if (src != null && Hoeren(occ, src, Richtung.Echt(src), occlusion, out _, lp))
            {
                var f = Wege.Frequenzen();
                float lpH = (float)Hoerbarkeit.Tiefpass(HoerErg, lp, f[1], f[2], TiefpassMin.Value, f[0]);
                if (lpH < lp - 0.5f) { HoerTiefpass++; HoerTiefpassHz += lp - lpH; }
                lp = lpH;
            }
            lowPassFreq = lp;
            float offen = highPassFreq;
            try { if (preset != null) offen = HochpassOffen(preset, influencedWeights); }
            catch (Exception e) { Statistik.Fehler(e); }
            float hp = HochpassKern.Hochpass(highPassFreq, offen, lp, HochpassPhysikAn != null && HochpassPhysikAn.Value);   // blechbass (17.11.)
            if (hp < highPassFreq - 0.5f) { HochpassGesenkt++; HochpassSummeHz += highPassFreq - hp; }
            highPassFreq = hp;
            Filter++;
        }

        // ---------------------------------------------------------------- 0.8.5: Hoerbarkeit je Band

        static readonly double[] kM = new double[Hoerbarkeit.NB], kN = new double[Hoerbarkeit.NB];
        /// <summary>Letztes Ergebnis von Hoeren (Hauptthread; das Klang-Log liest es im selben Anwenden).</summary>
        internal static readonly Hoerbarkeit.Baender HoerErg = new Hoerbarkeit.Baender();
        internal static int LeiseStumm, LeiseNurTief, HoerTiefpass;
        /// <summary>10.11. (B1): Clip-Korrektur der letzten Hoeren-Rechnung (dB, fuer das Klang-Log).</summary>
        internal static double HoerKorr;
        internal static double HoerTiefpassHz;

        /// <summary>
        /// Bandkurve des Wegs, die auch der Tiefpass benutzt: m = reale Mehrdaemmung 1 kHz / 4 kHz gegenueber dem Bezugsband,
        /// n = Mehrdaempfung des Spiels ueber den Tiefpass (bei den Fit-Wegen BandStaerke * m, sonst aus dem fertigen Tiefpass
        /// zurueckgerechnet, MehrDb(lp, f)). lp = Tiefpass wie 0.8.4. Rueckgabe: 1 Hindernis-Bandkurve, 2 Raumweg + Wand,
        /// 3 Raumweg (Tuer/Beugung), 0 alte Abbildung.
        /// </summary>
        static int Kurve(SpatialAudioOccluder occ, float occlusion, double[] m, double[] n, out float lp, double farbe = 1.0)
        {
            m[0] = n[0] = 0;
            int quelle;
            double m1, m2;
            // farbekennlinie (12.11.): gespielte Neigung = farbe x reale Bandkurve (FarbeKennlinieKern.cs, M2). m bleibt REAL (Hoerbarkeit
            // je Band rechnet mit der echten Daemmung), n und der Fit bekommen die gestauchte. farbe = 1: alles exakt wie bisher.
            bool gestaucht = farbe < 0.9999;
            // 0.8.4 Stufe 1: Hindernis-Weg mit Bandkurve (T/D/R je Band, Wege.cs) -> 2-poliger Fit statt der freien Kurve
            if (Wege.BandKurve(occ, out m1, out m2)) quelle = 1;
            // 0.8.4 Stufe 1: Raumweg + Wand zwischen Raeumen energetisch summiert -> Fit aus beiden Spektren
            else if (WandZwischenRaeumen.SummenKurve(occ, out m1, out m2)) quelle = 2;
            else quelle = 0;
            var f = Wege.Frequenzen();
            if (quelle != 0)
            {
                TrockenKorr(ref m1, ref m2);
                if (quelle == 2) Zweitstimme.ErsteKorr(Raumweg.AnwendQuelle, ref m1, ref m2);   // mehrweg (17.11.): Klangfarbe der ersten Stimme = ihr Buendel
                double k = Wege.BandStaerke.Value;
                if (!gestaucht) farbe = 1.0;
                m[1] = m1; m[2] = m2; n[1] = k * farbe * m1; n[2] = k * farbe * m2;
                lp = Wege.FitLp(farbe * m1, farbe * m2);
                return quelle;
            }
            // 10.11. (klangfarbe, Review B4): physikalischer Raumweg - die echten Baender der Routen (Tuerblatt je Band nach TuerKern,
            // Beugung, Raeume; Raumweg.BandKurve) wie bei Hindernis und Wand. Bis 10.11. wurden m/n aus dem gefitteten Tiefpass
            // zurueckgerechnet (MehrDbRelativ(lp)) - die Hoerbarkeit je Band sah so nur die 2-polige Naeherung, bei offenem Fit gar
            // keine Mehrdaemmung ("D 60.0 / D 60.0 / D 60.0" im Log 18:00:34.603).
            if (Raumweg.BandKurve(occ, out float lpRoute, out m1, out m2))
            {
                // hallsend (B2): trockenes Signal -> eigener Fit aus seiner Bandkurve (sonst der gemerkte Fit der Route)
                bool korr = TrockenKorr(ref m1, ref m2);
                if (Zweitstimme.ErsteKorr(Raumweg.AnwendQuelle, ref m1, ref m2)) korr = true;   // mehrweg (17.11.): Klangfarbe der ersten Stimme = ihr Buendel
                if (korr) lpRoute = Wege.FitLp(m1, m2);
                double k = Wege.BandStaerke.Value;
                m[1] = m1; m[2] = m2; n[1] = k * m1; n[2] = k * m2;
                if (gestaucht) { n[1] *= farbe; n[2] *= farbe; lpRoute = Wege.FitLp(farbe * m1, farbe * m2); }   // farbekennlinie
                lp = lpRoute;
                return 3;
            }
            lp = Abbildung(occlusion);
            // Gesamtreview A8: Mehrdaempfung GEGENUEBER DEM BEZUGSBAND (wie bei den Bandkurven m1/m2), nicht absolut
            // blechschuss (17.11.): mit Schalter die gemessene Form des Spielfilters (4-polig, SpielfilterKern.cs) statt der 2-poligen Annahme
            int pole = Wege.Spiel24An ? SpielfilterKern.PoleSpiel : 2;
            for (int b = 1; b < Hoerbarkeit.NB; b++) m[b] = n[b] = SpielfilterKern.MehrDbRelativ(lp, f[b], f[0], pole);
            // farbekennlinie: auch die alte Abbildung (kein Bandweg) - ihre Neigung gestaucht und neu angepasst (wie Wege.FitLp ohne BandStaerke,
            // die Abbildung kennt sie nicht)
            if (gestaucht)
            {
                n[1] *= farbe; n[2] *= farbe;
                lp = (float)Wege.FitSpiel(n[1], n[2], TiefpassMin.Value, f[0]);   // blechschuss: Modell je Schalter (sonst wie bisher)
            }
            return quelle;
        }

        /// <summary>hallsend (B2, 12.11.): Bandkurve der Summe -> Bandkurve des trockenen (gerichteten) Anteils, solange kurveTrocken gilt.
        /// true = korrigiert.</summary>
        static bool TrockenKorr(ref double m1, ref double m2)
        {
            if (!kurveTrocken || (Math.Abs(kurveK1) < 1e-9 && Math.Abs(kurveK2) < 1e-9)) return false;
            m1 += kurveK1; m2 += kurveK2;
            TrockenFarbe++; TrockenFarbeSumme += kurveK2;
            return true;
        }

        /// <summary>0.8.5: Hoerbarkeit je Band fuer Quelle 'source' (Ergebnis in HoerErg). false = aus, laute Gruppe, keine Daemmung.
        /// lpFertig: Tiefpass ist schon gerechnet (FilterNach) - dann wird die Kurve nicht doppelt bestimmt.</summary>
        internal static bool Hoeren(SpatialAudioOccluder occ, BetterSource source, Vector3 sourcePos, float w, out float lp, float lpFertig = -1f)
        {
            lp = 22000f;
            HoerErg.Gilt = false;
            if (!An || !LeiseReal.Value || source == null || occ == null || w <= 0f) return false;
            var preset = source.Preset;
            var lt = occ.ListenerTransform;
            if (preset == null || lt == null) return false;
            int typ = (int)preset.Type;
            if (double.IsNaN(Hoerbarkeit.BandPegel(typ, 0))) return false;
            if (lpFertig < 0f) Kurve(occ, w, kM, kN, out lp, FarbFaktor(occ, source, w));   // farbekennlinie: leise Gruppen -> 1
            else lp = lpFertig;   // FilterNach hat kM/kN direkt davor fuer diesen Occluder gefuellt
            // 10.11. (akustik-wand, B1): Quellpegel des gespielten Clips statt des Gruppenwerts (Kalibrierung.ClipKorrektur)
            double korr = Kalibrierung.ClipKorrektur(typ, source._lastPlayingClipName);
            HoerKorr = korr;
            // galpegel (10.11.): ein aktives Headset (Guns Are Loud) verschiebt nur die Ruhehoerschwelle und bringt Eigenrauschen mit
            // (FremdPegelKern (a)); Quelle und Grundgeraeusch hebt es gleich. null = ohne Fremdgewinn (Hoerbarkeit.Grund wie bisher).
            double[] verdecker = FremdPegel.Verdecker(source.source1, Grundgeraeusch.Value, Kalibrierung.K());
            return Hoerbarkeit.Rechnen(typ, Vector3.Distance(sourcePos, lt.position), Grundgeraeusch.Value, Dynamik.Value * w,
                                       kM, kN, Lautheit.Value, Dynamik.Value, HoerErg, KennKnie.Value, KennSteigung.Value, korr, verdecker);
        }

        // ---------------------------------------------------------------- farbekennlinie (12.11.): Klangfarbe folgt der Kennlinie

        /// <summary>Stauchungsfaktor g der Lautstaerke fuer Quelle q bei Wirkung w (FarbeKennlinieKern.cs): dieselben Groessen wie SollDb
        /// (Gruppe, Schalldaempfer, Clip-Korrektur, echter Ort, D = Max. insulation x w), ohne Lautheitsfaktor. 1 = Schalter aus, Pegel
        /// unbekannt oder unter dem Knie. Gleich im vollen und im gemerkten Bild (keine Merker) -> kein Wechsel des Ziels je Bild.
        /// ANNAHME: beim aufgeteilten trockenen Teil (Diffuse sound as reverb) gilt der Faktor der Summe (der trockene Teil liegt um den
        /// Diffus-Zusatz tiefer und waere eher weniger gestaucht).</summary>
        internal static double FarbFaktor(SpatialAudioOccluder occ, BetterSource q, float w)
        {
            if (FarbeKennlinieAn == null || !FarbeKennlinieAn.Value || q == null || q.Preset == null || occ == null) return 1.0;
            var lt = occ.ListenerTransform;
            if (lt == null) return 1.0;
            int typ = (int)q.Preset.Type;
            double l1 = Hoerbarkeit.KennPegel(typ, typ == 0 && SebAkustik.Gedaempft(q));
            if (double.IsNaN(l1)) return 1.0;
            l1 += Kalibrierung.ClipKorrektur(typ, q._lastPlayingClipName);
            float ab = Vector3.Distance(Richtung.Echt(q), lt.position);
            double lFrei = Hoerbarkeit.FreiPegel(l1, ab);
            // explosionfern (17.11.): dieselbe Steigung wie die Lautstaerke (SollDb) -> Klangfarbe bleibt an die Lautstaerke gekoppelt
            double s = KennGruppeLauf.Steigung(q, typ, ab, Dynamik.Value * w, KennSteigung.Value, false);
            return FarbeKennlinie.Faktor(lFrei, Dynamik.Value * w, KennKnie.Value, s);
        }

        // Fuer das Klang-Log: letzte gestauchte Neigung (FilterKern), gilt nur fuer Quelle/Bild.
        static int FarbeQuelle, FarbeBild = -1;
        static float FarbeG = 1f, FarbeReal4k = float.NaN, FarbeSpiel4k = float.NaN;
        static void FarbeLogLoeschen() { FarbeBild = -1; FarbeG = 1f; FarbeReal4k = FarbeSpiel4k = float.NaN; }

        /// <summary>Klang-Log: reale und gespielte Neigung bei 4 kHz (dB) und Faktor, wenn FilterKern sie in diesem Bild fuer q gestaucht hat.</summary>
        internal static bool FarbeLog(BetterSource q, out float real4k, out float spiel4k, out float g)
        {
            real4k = spiel4k = float.NaN; g = 1f;
            if (q == null || FarbeBild != Time.frameCount || FarbeQuelle != q.GetInstanceID()) return false;
            real4k = FarbeReal4k; spiel4k = FarbeSpiel4k; g = FarbeG;
            return true;
        }

        /// <summary>Alte Abbildung Wertung -> Tiefpass (logarithmisch 22 kHz -> "Low-pass at full insulation").</summary>
        internal static float Abbildung(float w) => 22000f * Mathf.Pow(TiefpassMin.Value / 22000f, Mathf.Clamp01(w));

        internal static int HochpassGesenkt;
        internal static double HochpassSummeHz;

        // Wie FilterCutoffCalculator.Compute, aber mit Verdeckung 0 (time2 = 0) und ohne Isolations-Lerp.
        static float HochpassOffen(Audio.Effects.AudioOcclusionEQPreset preset, DirectionalWeights w)
        {
            var hs = preset.hpfSettings;
            float a = hs.frequencyCurve.Evaluate(0f);
            var t = hs.positionEqThresholds;
            float hp = w.Front * Mathf.Min(a, t.levelFreq) + w.Up * Mathf.Min(a, t.aboveFreq)
                     + w.Down * Mathf.Min(a, t.belowFreq) + w.Back * Mathf.Min(a, t.behindFreq);
            return Mathf.Clamp(hp, 20f, 20000f);
        }

        // Wertung der letzten fertigen Rechnung je Rechner (0.8.1, siehe Stetigkeit.cs).
        sealed class Fertig { public bool Gemerkt; public float Letzte; }
        static readonly ConditionalWeakTable<SpatialAudioOcclusionCombinedCalculator, Fertig> FertigTab =
            new ConditionalWeakTable<SpatialAudioOcclusionCombinedCalculator, Fertig>();
        static readonly ConditionalWeakTable<SpatialAudioOcclusionCombinedCalculator, Fertig>.CreateValueCallback FertigNeu = _ => new Fertig();

        // Der Schall nimmt den leichtesten Weg: kleinste Teil-Wertung statt Mittelwert.
        // Quelle: parallele Wege addieren sich energetisch, der schwaechste dominiert (Flankenuebertragung,
        // EN ISO 12354-1:2017). Minimum statt Energiesumme ist eine Vereinfachung.
        // 0.8.1: Waehrend einer Neuberechnung passen die _ran*-Merker (schon die neue Rechnung) nicht zum Ergebnis (noch das alte);
        // das Minimum ueber diese Mischung sprang zwischen 0 und 1. Jetzt gilt bis zum Abschluss die letzte fertige Wertung.
        // Bekannte Grenze (Review 0.8.3, Punkt 7, nur dokumentiert): T ist im Physik-Modus eine dB-Skala (Wertung = dB / Dynamik),
        // D (Beugung) und R (Reflexion) sind Heuristiken des Spiels auf eigener Skala 0..1. Das Minimum vergleicht also Werte
        // unterschiedlicher Herkunft; eine Kalibrierkurve fuer D/R gibt es nicht (keine Messdaten).
        static void WertungNach(SpatialAudioOcclusionCombinedCalculator __instance, ref float __result)
        {
            if (!An) return;
            try
            {
                var r = __instance.LastAggregateResult;
                bool t = __instance._ranTransmission, d = __instance._ranDiffraction, rr = __instance._ranReflection;
                var schritt = __instance._combinedStep;
                bool fertig = schritt == SpatialAudioOcclusionCombinedCalculator.ECombinedStep.Complete
                              || schritt == SpatialAudioOcclusionCombinedCalculator.ECombinedStep.Idle;
                var z = FertigTab.GetValue(__instance, FertigNeu);
                float stetig = Stetigkeit.Waehlen(fertig, t, d, rr, r.transmissionScore, r.diffractionScore, r.reflectionScore,
                                                  ref z.Gemerkt, ref z.Letzte, out bool gehalten);
                // 0.8.4 Stufe 1: alle Wege in dB (und als Energiesumme) - Ergebnis der letzten FERTIGEN Rechnung, gehalten bis zur
                // naechsten (Wege.Schnappschuss), damit gilt die Stetigkeit von selbst.
                if (Wege.Wertung(__instance, out float wege))
                {
                    __result = wege;
                    Stetigkeit.Gerechnet++;
                    if (!fertig) Stetigkeit.Gehalten++;
                    return;
                }
                if (Stetigkeit.An)
                {
                    __result = stetig;
                    Stetigkeit.Gerechnet++;
                    if (gehalten) Stetigkeit.Gehalten++;
                }
                else
                {
                    // 0.8.0 (nur zum Vergleich): Minimum ueber die aktuellen Merker
                    float min = 2f;
                    if (t) min = Mathf.Min(min, r.transmissionScore);
                    if (d) min = Mathf.Min(min, r.diffractionScore);
                    if (rr) min = Mathf.Min(min, r.reflectionScore);
                    if (min <= 1f) __result = Mathf.Clamp01(min);
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void ResetNach(SpatialAudioOcclusionCombinedCalculator __instance)
        {
            try { if (FertigTab.TryGetValue(__instance, out var z)) z.Gemerkt = false; Wege.Vergessen(__instance); }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        internal static int PoolZurueckgesetzt;

        // 0.8.3 (Review-Befund 3): SpatialAudioSystemPoolService.ResetCombinedCalculator ruft beim Zurueckgeben nur
        // FinalizeAndGetAggregateResult, NICHT Reset: LastAggregateResult, _combinedStep und die _ran*-Merker bleiben die der
        // vorigen Quelle. Der naechste Occluder, der den Rechner bekommt, las 1-3 Bilder lang deren Wertung - im Physik-Modus
        // sogar als "fertige" Wertung gemerkt (Stetigkeit). Darum hier, sobald ein Occluder mit seinem Rechner entsteht, der
        // Reset, den das Spiel selbst beim Wechsel Hindernis <-> Raumweg macht (LastAggregateResult = frei, OnReset vergisst die
        // gemerkte Wertung). Bis zur ersten eigenen Rechnung gilt also "frei" wie nach jedem Reset im Spiel.
        static void NeuerOccluderNach(CombinedSpatialAudioOccluder __instance)
        {
            if (!Plugin.Aktiv.Value || !Stetigkeit.An) return;
            try
            {
                if (__instance._obstructionCalculator is SpatialAudioOcclusionCombinedCalculator c && !c.IsCalculating)
                {
                    c.Reset();
                    PoolZurueckgesetzt++;
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        internal static void Loggen()
        {
            ReflexionDirektweg.Loggen();   // 0.8.4
            Wege.Loggen();                 // 0.8.4 Stufe 1
            Raumweg.Loggen();              // 0.8.4
            if (Stetigkeit.Gerechnet + Stetigkeit.Geglaettet + Stetigkeit.SofortGelassen > 0)
            {
                Plugin.Log.LogInfo($"[Diagnostics] Steady occlusion: {Stetigkeit.Gehalten} of {Stetigkeit.Gerechnet} path scores held from the last finished calculation " +
                                   $"(0.8.0 would have mixed old and new there), {Stetigkeit.Geglaettet} filter jumps of playing sounds turned into glides, " +
                                   $"{Stetigkeit.SofortGelassen} set at once (new/silent sounds), {PoolZurueckgesetzt} pooled calculators reset for a new sound.");
                Stetigkeit.Gehalten = Stetigkeit.Gerechnet = Stetigkeit.Geglaettet = Stetigkeit.SofortGelassen = PoolZurueckgesetzt = 0;
            }
            if (An && Wege.TUnbestimmtZahl > 0)
                Plugin.Log.LogInfo($"[Diagnostics] Physics mode: {Wege.TUnbestimmtZahl} volume updates of transmission-only sounds without any valid ray result (one-sided hits / two objects) kept the game value instead of full insulation.");
            Wege.TUnbestimmtZahl = 0;
            if (An && TaktGeprueft > 0)
                Plugin.Log.LogInfo($"[Diagnostics] Physics mode: cached volume - {TaktGeprueft} cached sound updates checked against the volume for the current occlusion, " +
                                   $"{TaktNeuGerechnet} containers recalculated in full (applied volume more than {LautTaktKern.SchwelleDb:F2} dB off, largest {TaktMaxAusgeloest:F2} dB; " +
                                   $"{TaktUnbekannt} sounds never calculated), {TaktGehalten} containers kept the cached values (largest gap kept {TaktMaxGehalten:F2} dB); {ZeitTakt.Text()}.");
            TaktGeprueft = TaktGehalten = TaktNeuGerechnet = TaktUnbekannt = 0; TaktMaxGehalten = TaktMaxAusgeloest = 0;
            if (!An || Lautstaerken == 0) return;
            Plugin.Log.LogInfo($"[Diagnostics] Physics mode: {Lautstaerken} volume updates (mean -{SummeDb / Lautstaerken:F1} dB), {Filter} low-pass updates, " +
                               $"{HochpassGesenkt} high-pass values lowered to the open-path value (mean -{(HochpassGesenkt > 0 ? HochpassSummeHz / HochpassGesenkt : 0):F0} Hz).");
            if (LeiseGestaucht > 0)
                Plugin.Log.LogInfo($"[Diagnostics] Physics mode: {LeiseGestaucht} volume updates of quiet sources below the background noise (mean {LeiseSummeDb / LeiseGestaucht:F1} dB more insulation than the loudness factor alone).");
            if (LeiseStumm + LeiseNurTief + HoerTiefpass > 0)
                Plugin.Log.LogInfo($"[Diagnostics] Physics mode: audibility per band - {LeiseStumm} volume updates of quiet sources silent (no band above its masking threshold), {LeiseNurTief} with only the low band audible, {HoerTiefpass} low-pass values lowered to the audible bands (mean -{(HoerTiefpass > 0 ? HoerTiefpassHz / HoerTiefpass : 0):F0} Hz).");
            LeiseGestaucht = 0; LeiseSummeDb = 0; LeiseStumm = LeiseNurTief = HoerTiefpass = 0; HoerTiefpassHz = 0;
            Lautstaerken = Filter = HochpassGesenkt = 0; SummeDb = 0; HochpassSummeHz = 0;
        }
    }
}
