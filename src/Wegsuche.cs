using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.DataProviding;
using Materialklang.Schallweg;
using UnityEngine;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    //  0.8.6 - Abschnitt 19: eigene Schallweg-Suche (Etappe 3, experimentell, Standard AUS).
    //  Ursache: Die Spielsuche (T/D/R auf der Direktlinie, Raumweg ueber die Portalrouten des Spiels) kennt keine Wege um Kanten
    //  durch den Raum. Die Wegsuche (Etappe 2, eigenes Entwicklungswerkzeug ausserhalb des Plugins) rechnet sie auf einem Netz aus Beugungskanten und Portalen:
    //  Hoererbaum (Dijkstra je 1-m-Zelle des Hoerers) + Sichtstrahlen je Quelle, UTD-Beugung, Rohr, Tuerblaetter, Durchgang.
    //  Daten: Cache-Datei je Karte (BepInEx/cache/TrueAcoustics/<karte>-<pruefsumme>.wegenetz, Format Schallweg/SwDatei.cs), auf
    //  diesem Rechner aus den Spieldateien gebaut; die Pruefsumme ueber die gelesenen Spieldateien muss passen (Spiel-Update = neu bauen).
    //  Laufzeit: Laden im Hintergrund (Task); rechnen im Arbeitsfaden (Schallweg/SwArbeiter.cs, 10 Hz). Hier, im Hauptfaden: nur
    //  Hoererposition, Anfragen (Quellpositionen), Tuerzustaende und das Abholen fertiger Ergebnisse.
    //  Modus "CompareOnly": rechnet und schreibt je Klang eine Zeile "[PathSearch]" ins Klang-Log, der Ton bleibt wie er ist.
    //  Modus "Use": ersetzt im Raumweg/Ersatzweg die Routen durch die gefundenen Wege (RoutenMischer: Baender, Ueberblendung,
    //  Richtung wie bisher). Solange fuer eine Quelle noch kein Ergebnis da ist, gilt der Wert der Mod.
    //  Livetest-Fix (Factory 10.11.2026, Wegsuche/BERICHT-LIVETEST-FACTORY.md):
    //   - Zuordnung je QUELLE (BetterSource) statt je Behaelter: ein Bot-Behaelter traegt Schritte, Stimme, Schuesse an verschiedenen
    //     Orten; je Behaelter ueberschrieben sich ihre Anfragen und Ergebnisse (58 % der Vergleichszeilen verfaelscht) und der Faden
    //     rechnete dieselbe Id bei jedem Ortswechsel neu. "Use" bleibt je Behaelter (der Raumweg der Mod gilt je Behaelter und dessen Ort).
    //   - Ruhende Quellen (kein Clip, prewarm_sound, nicht spielend, im Kartenursprung geparkt) stellen keine Anfrage.
    //   - Raumportale ohne Netzknoten (grosse Tore) folgen dem Spielzustand fuer den Raumanteil.
    // ------------------------------------------------------------------------------------------
    internal static class Wegsuche
    {
        internal enum Modus { CompareOnly, Use }
        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<Modus> Art;
        internal static ConfigEntry<float> BudgetMs;
        internal static ConfigEntry<int> Faeden;
        internal static ConfigEntry<bool> UmwegAn;   // umwegsuche (13.11.): Hindernis-Zweig ohne gefundenen Umweg
        const string Sek = "19. Path search (experimental)";

        static Arbeiter arb;
        static Task<Arbeiter> laden;
        static string ladeKarte, geladenKarte, ladeText, datei;
        static double ladeS; static long ladeMB;
        static bool ladeFehlerGemeldet;
        static string gestopptKarte;   // wegsuche-laut (N5): Karte, auf der der Arbeitsfaden in diesem Raid mit Fehler endete (nicht neu laden)
        static float naechsteDiag, naechsteTuer, alphaGesetzt = float.NaN;

        internal static bool Bereit => arb != null;
        internal static bool Ersetzt => Aktiv != null && Aktiv.Value && Art.Value == Modus.Use && arb != null;
        /// <summary>Raid-3-Fix: in CompareOnly die Use-Schluessel probehalber stellen (nur zaehlen, Arbeiter.Probe).</summary>
        internal static bool ProbeAn => Aktiv != null && Aktiv.Value && Art.Value == Modus.CompareOnly && arb != null;
        internal static void Probe(AudioSourceContainer c, Vector3 q) { var a = arb; if (a != null && c != null) a.Probe(IdBehaelter(c), Sv(q)); }

        internal static void Binden(ConfigFile c)
        {
            Aktiv = c.Bind(Sek, "Path search (experimental)", false,
                "Own sound path search (edges and openings of the map, around corners through the rooms). Needs a path network file for the map in " +
                "BepInEx/cache/TrueAcoustics (optional data file, not part of the public release; maps: Factory, Labs, Labyrinth). Uses about 700 MB of RAM on Factory (about 600 Labs, 110 Labyrinth) and two to four background threads (setting Threads). " +
                "Off = nothing is loaded. Applies at the next raid start (switching off frees it at once).");
            Art = c.Bind(Sek, "Mode", Modus.CompareOnly,
                "CompareOnly = calculate and write one '[PathSearch]' line per sound into the sound log (section 11, 'Sound log' must be on) next to the mod's values; the sound is not changed. " +
                "Use = the room path / fallback path values (sounds between rooms, section 0) come from the path search instead of the game's routes. Sounds without a result yet keep the mod's value.");
            BudgetMs = c.Bind(Sek, "Max. ms per source", 2f,
                new ConfigDescription("Time of the background thread per sound source after the first path is found (further paths are skipped after that). Before the first path a search may take 4x this per tick and then continues in the next tick (not reported as 'no path'). The game thread is not affected.",
                    new AcceptableValueRange<float>(0.5f, 20f)));
            Faeden = c.Bind(Sek, "Threads", 2,
                new ConfigDescription("Background threads that calculate sound sources at the same time (plus one thread that prebuilds the listener tree of the cell you are walking into). " +
                    "Applies at the next raid start.", new AcceptableValueRange<int>(1, 3)));
            // umwegsuche (13.11.2026): eng begrenzter scharfer Einsatz. Haengt am Hauptschalter (Netz geladen, Faeden laufen), gilt in beiden
            // Modi. Begruendung (Bericht review-20261110/umwegsuche-STAND.md): beim Tester steht der Hauptschalter an (cfg Abschnitt 19,
            // "Path search (experimental) = true", Mode CompareOnly) - ein eigener Ladeweg bei ausgeschaltetem Hauptschalter haette 700 MB RAM
            // (Factory) ohne sichtbaren Schalter gekostet.
            UmwegAn = c.Bind(Sek, "Use for obstacles without a detour", true,
                "Only when 'Path search (experimental)' is on (any mode): if source and listener are in the same room and the game finds no way " +
                "around the obstacles between them (sound log: 'no detour found'), the paths around the edges found by the path search replace the " +
                "screening limit (13.5/19.1/25 dB). Never quieter than that limit, never louder than line of sight; changes glide like the other paths. " +
                "No result yet, too old or no network for the map = the screening limit as before. Applies live.");
            Aktiv.SettingChanged += (_, __) => { if (!Aktiv.Value) { gestopptKarte = null; Beenden("switched off"); } };
            WegTabelleSpiel.Binden(c, Sek);   // wegtabelle (13.11.): vorab gerechnete Kantenwege, unabhaengig vom Hauptschalter
        }

        // ================================================================ Laden / Raid

        internal static string KarteVon(string loc)
        {
            if (string.IsNullOrEmpty(loc)) return null;
            string l = loc.ToLowerInvariant();
            if (l.StartsWith("factory")) return "factory";
            if (l == "laboratory") return "labs";
            if (l == "labyrinth") return "labyrinth";
            // umwegsuche: Namen der Exporte (Wegsuche/Etappe2-Karten/lauf.sh <karte> export3) fuer die naechsten Karten - ohne Datei bleibt die Suche aus
            if (l == "bigmap") return "customs";
            if (l == "rezervbase") return "reserve";
            if (l.StartsWith("sandbox")) return "groundzero";
            if (l == "tarkovstreets") return "streets";   // wegkacheln: LocationId TarkovStreets (SPT_Data/database/locations/tarkovstreets/base.json "Id"), Netz/Tabelle "streets"
            return l;   // weitere Karten: Name der Location (shoreline, woods; Kacheln folgen spaeter)
        }

        internal static void SzeneGewechselt() { gestopptKarte = null; Beenden(null); WegTabelleSpiel.SzeneGewechselt(); }

        internal static void Beenden(string grund)
        {
            var a = arb; arb = null;
            if (a != null) { a.Dispose(); if (grund != null) Plugin.Log.LogInfo($"Path search: stopped ({grund}), memory freed."); }
            laden = null; ladeKarte = null; geladenKarte = null; portale.Clear(); raumPortale.Clear(); offeneZeilen.Clear(); namen.Clear(); geschrieben.Clear(); tuerMap = false;
        }

        internal static void Tick()
        {
            WegTabelleSpiel.Tick();   // wegtabelle: laedt die Tabelle der Karte auch bei ausgeschaltetem Hauptschalter
            if (Aktiv == null || !Aktiv.Value) return;
            if (!Singleton<GameWorld>.Instantiated || Singleton<GameWorld>.Instance.MainPlayer == null) { gestopptKarte = null; if (arb != null || laden != null || ladeKarte != null) Beenden("raid ended"); return; }
            string karte = KarteVon(Singleton<GameWorld>.Instance.LocationId);
            if (karte == null) return;
            if (laden == null && arb == null && ladeKarte != karte && gestopptKarte != karte) StartLaden(karte);
            if (laden != null && laden.IsCompleted)
            {
                var t = laden; laden = null;
                if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                {
                    arb = t.Result; geladenKarte = karte;
                    arb.BudgetMs = BudgetMs.Value;
                    arb.StartFrei = false;   // Raid-3-Fix: erster Baum (Ladebildschirm) erst nach dem Tuerabgleich, siehe Tueren()
                    arb.Starten();
                    Plugin.Log.LogInfo($"Path search: {ladeText}");
                    KlangLog.Text("[PathSearch] " + ladeText);
                }
                else if (!ladeFehlerGemeldet)
                {
                    ladeFehlerGemeldet = true;
                    Plugin.Log.LogWarning("Path search: " + (t.Exception != null ? "loading failed: " + t.Exception.GetBaseException().Message : ladeText));
                }
            }
            if (arb == null) return;
            // wegsuche-laut (N5): Fehler im Arbeitsfaden -> deutlich melden und den Arbeiter beenden (Faeden aus, Speicher frei). Vorher lief
            // ein Teil der Faeden weiter und "Use" fiel ohne Hinweis auf den Mod-Wert zurueck. Fuer den Rest des Raids bleibt die Suche aus.
            if (arb.Fehler != null)
            {
                string f = arb.Fehler; gestopptKarte = karte;
                Plugin.Log.LogError("Path search: background thread failed - path search OFF for the rest of this raid" + (Art.Value == Modus.Use ? " (mode Use: all sounds use the mod's values again)" : "") + ": " + f);
                KlangLog.Text("[PathSearch] ERROR: background thread failed, path search off for the rest of this raid: " + f.Split('\n')[0]);
                Beenden("thread error");
                return;
            }
            arb.BudgetMs = BudgetMs.Value;
            if (Raumweg.Alpha != null && Raumweg.Alpha.Value != alphaGesetzt) { alphaGesetzt = Raumweg.Alpha.Value; arb.AlphaSetzen(alphaGesetzt); }
            var ba = MonoBehaviourSingleton<BetterAudio>.Instance;
            var lt = ba != null ? ba.ListenerTransform : null;
            if (lt != null) arb.Hoerer(Sv(lt.position));
            float jetzt = Time.realtimeSinceStartup;
            if (jetzt >= naechsteTuer) { naechsteTuer = jetzt + 0.1f; Tueren(); }
            Zeilen();
            if (jetzt >= naechsteDiag) { naechsteDiag = jetzt + 30f; Diagnose(); }
        }

        static void StartLaden(string karte)
        {
            ladeKarte = karte; ladeFehlerGemeldet = false; ladeText = null;
            string dir = Path.Combine(BepInEx.Paths.CachePath, "TrueAcoustics");
            string daten = Application.dataPath;   // Hauptfaden
            laden = Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (!Directory.Exists(dir)) { ladeText = $"no path network folder {dir} - path search off for this raid."; return (Arbeiter)null; }
                string grund = "no file " + karte + "-*.wegenetz";
                foreach (var f in Directory.GetFiles(dir, karte + "-*.wegenetz"))
                {
                    var k = WegDaten.Kopf(f);
                    if (k == null) { grund = Path.GetFileName(f) + ": not a path network file of this version"; continue; }
                    if (!string.IsNullOrEmpty(k.Kachel)) continue;   // Kacheln (grosse Karten) folgen spaeter
                    if (!k.Passt(daten, out string g)) { grund = Path.GetFileName(f) + ": " + g + " (made for other game files)"; continue; }
                    var d = WegDaten.Lesen(f);
                    var a = new Arbeiter(d, Faeden.Value); a.VarintFreigeben();
                    ladeS = sw.Elapsed.TotalSeconds; ladeMB = a.Bytes / 1000000; datei = Path.GetFileName(f);
                    ladeText = $"map {karte}: {datei} loaded in {ladeS:F1} s (edges with door penalties {a.KantenAufbauMs / 1000:F1} s) - {d.N} nodes, {d.TriK.Length} triangles, {d.PN} openings, {a.Faeden} threads, " +
                               (d.HatRaeume ? $"{d.RN} rooms / {d.QN} room portals" : "no room data (file format 1: rebuild it for the room share)") + $", about {ladeMB} MB; mode {Art.Value}.";
                    return a;
                }
                ladeText = $"map {karte}: no usable path network in {dir} ({grund}) - path search off for this raid.";
                return (Arbeiter)null;
            });
        }

        // ================================================================ Tueren (Szene, Portal-Id) -> Netz-Portal, Zustand live

        struct PortalPaar { public int I; public BaseSpatialAudioPortal P; public bool Zu; }
        static readonly List<PortalPaar> portale = new List<PortalPaar>(), raumPortale = new List<PortalPaar>();
        static bool tuerMap; static int tuerVersuche, tuerGesamt, tuerOhne;

        static void Tueren()
        {
            if (!tuerMap)
            {
                if (tuerVersuche++ % 10 != 0) return;   // einmal je Sekunde versuchen, bis die Portale des Spiels da sind
                if (!DataProvider.TryGetData<SpatialAudioDataContainer>(out var data) || data == null || data.PortalsByID == null || data.PortalsByID.Count == 0) return;
                var d = arb.Daten; portale.Clear(); raumPortale.Clear(); tuerGesamt = 0; tuerOhne = 0;
                foreach (var kv in data.PortalsByID)
                {
                    var p = kv.Value as BaseSpatialAudioPortal;
                    if (p == null) continue;
                    tuerGesamt++;
                    string sz = p.gameObject.scene.name; short id = p.ID; Vector3 m = p.Center();
                    // Raumportal (Raumanteil): nur die ohne Netzportal - die mit Netzportal folgen dessen Zustand
                    {
                        int bq = -1; double bqd = 1.0;
                        for (int i = 0; i < d.QN; i++)
                        {
                            if (d.QId[i] != id || d.QSzene[i] != sz) continue;
                            double dd = Math.Sqrt(Sq(d.QMitte[3 * i] - m.x) + Sq(d.QMitte[3 * i + 1] - m.y) + Sq(d.QMitte[3 * i + 2] - m.z));
                            if (dd < bqd) { bqd = dd; bq = i; }
                        }
                        if (bq >= 0 && d.QNetz[bq] < 0) raumPortale.Add(new PortalPaar { I = bq, P = p, Zu = false });
                    }
                    int best = -1; double bd = 1.0;
                    for (int i = 0; i < d.PN; i++)
                    {
                        if (d.PId[i] != id || d.PSzene[i] != sz) continue;
                        double dd = Math.Sqrt(Sq(d.PMitte[3 * i] - m.x) + Sq(d.PMitte[3 * i + 1] - m.y) + Sq(d.PMitte[3 * i + 2] - m.z));
                        if (dd < bd) { bd = dd; best = i; }
                    }
                    if (best < 0)
                        for (int i = 0; i < d.PN; i++)
                        {
                            double dd = Math.Sqrt(Sq(d.PMitte[3 * i] - m.x) + Sq(d.PMitte[3 * i + 1] - m.y) + Sq(d.PMitte[3 * i + 2] - m.z));
                            if (dd < 0.3 && dd < bd) { bd = dd; best = i; }
                        }
                    if (best < 0) { tuerOhne++; continue; }
                    if (!d.PSchliessbar[best]) continue;   // offene Oeffnung ohne Blatt: Zustand egal
                    portale.Add(new PortalPaar { I = best, P = p, Zu = arb.Zu[best] });
                }
                tuerMap = true; startFreigeben = true;
                Plugin.Log.LogInfo($"Path search: {portale.Count} closable openings follow the game state live ({tuerGesamt} game portals, {tuerOhne} not in the network = large outdoor portals or new; {raumPortale.Count} of those for the room share).");
            }
            for (int k = 0; k < raumPortale.Count; k++)
            {
                var pp = raumPortale[k];
                if (pp.P == null) continue;
                var st = pp.P.State;
                bool zu = st == BaseSpatialAudioPortal.PortalState.Closed || st == BaseSpatialAudioPortal.PortalState.InProgressClose;
                if (zu == pp.Zu) continue;
                pp.Zu = zu; raumPortale[k] = pp;
                arb.RaumTuerSetzen(pp.I, zu); dTueren++;
            }
            for (int k = 0; k < portale.Count; k++)
            {
                var pp = portale[k];
                if (pp.P == null) continue;
                var st = pp.P.State;
                bool zu = st == BaseSpatialAudioPortal.PortalState.Closed || st == BaseSpatialAudioPortal.PortalState.InProgressClose;
                if (zu == pp.Zu) continue;
                pp.Zu = zu; portale[k] = pp;
                arb.TuerSetzen(pp.I, zu); dTueren++;
            }
            // Raid-3-Fix: nach dem ersten Abgleich (die Zustaende stehen jetzt im Eingang) darf der Faden den ersten Hoererbaum am Spawnpunkt
            // bauen und die Suche aufwaermen - noch im Ladebildschirm statt im ersten Gefechtstakt (Raid 3: 122 ms)
            if (startFreigeben) { startFreigeben = false; arb.StartFreigeben(); }
        }
        static bool startFreigeben;
        static double Sq(double x) => x * x;

        // ================================================================ Klang-Log (CompareOnly und Use)

        sealed class Zeile
        {
            public long Id; public int Bild; public double T0; public string Quelle, Klang, Mod; public float Entf;
            public double ModDb = double.NaN, ModAz = double.NaN, RaumDb = double.NaN; public bool Ersatz;
        }
        static readonly List<Zeile> offeneZeilen = new List<Zeile>();
        static readonly Dictionary<long, string> namen = new Dictionary<long, string>();
        static int dZeilen, dOhne, dErsetzt, dRueckfall, dTueren, dRuhend, dVoll, dGleich;
        static int dOhneErg, dQuelleWeg, dHoererWeg, dZuAlt;   // wegsuche-use (7a): Gruende, warum "Use" den Mod-Wert behielt
        // Raid-2-Fix (8): je Quelle das zuletzt geschriebene Ergebnis (Zeit) und sein Bild - Folgeklaenge mit demselben Ergebnis bekommen
        // eine kurze Zeile (13 543 Zeilen fuer 5 317 Ergebnisse brachten das Klang-Log nach 7,5 min an die 50-MB-Grenze)
        static readonly Dictionary<long, KeyValuePair<double, int>> geschrieben = new Dictionary<long, KeyValuePair<double, int>>();

        /// <summary>Schluessel je Quelle (Vergleichszeilen): eigene Id und eigener Ort jeder BetterSource.</summary>
        static long IdQuelle(BetterSource s) => (2L << 32) | (uint)s.GetInstanceID();
        /// <summary>Schluessel je Behaelter ("Use": der Raumweg der Mod gilt je Behaelter und dessen Ort).</summary>
        static long IdBehaelter(AudioSourceContainer c) => (1L << 32) | (uint)RuntimeHelpers.GetHashCode(c);

        /// <summary>Ruhende Quelle: Aufwaermklang, spielt nicht (AudioSource.isPlaying), oder im Kartenursprung geparkt (Bot-Stimme zwischen
        /// zwei Saetzen, Livetest: 4393 Klangzeilen "Speech clip=-" / "prewarm_sound" bei (-0,1|0|0), vol 0). Ein leerer Clipname allein
        /// zaehlt NICHT: die Lampen spielen ihre Schleife mit leerem _lastPlayingClipName.</summary>
        static bool Ruhend(BetterSource s, string klang, float qx, float qy, float qz)
        {
            if (klang == "prewarm_sound") return true;
            if (Math.Abs(qx) < 0.5f && Math.Abs(qy) < 0.5f && Math.Abs(qz) < 0.5f) return true;
            var a = s.source1;
            return a != null && !a.isPlaying;
        }

        /// <summary>Aus KlangLog.Aufnehmen (Hauptfaden, Klang-Log an): Anfrage stellen und die Vergleichszeile vormerken.</summary>
        internal static void Klang(SpatialAudioOccluder occ, BetterSource source, LogEintrag e)
        {
            if (arb == null || source == null || float.IsNaN(e.QX)) return;
            if (offeneZeilen.Count > 64) { dVoll++; return; }   // Raid-2-Fix (8): gezaehlt (Diagnose "skipped, more than 64 open")
            try
            {
                if (Ruhend(source, e.Klang, e.QX, e.QY, e.QZ)) { dRuhend++; return; }
                // Raid-2-Fix (9): im Modus "Use" stellt der Raumweg die Anfrage je Behaelter (Fuer) - die Vergleichszeile nimmt dann dessen
                // Ergebnis statt eine zweite Anfrage je Quelle zu stellen (doppelte Arbeit, Bericht Raid 2 Abschnitt 4).
                bool use = Art.Value == Modus.Use && occ != null && occ.SourceContainer != null;
                long id = use ? IdBehaelter(occ.SourceContainer) : IdQuelle(source);
                var z = new Zeile { Id = id, Bild = e.Bild, T0 = arb.Jetzt, Quelle = e.Quelle, Klang = e.Klang, Entf = e.Entf };
                if (!use) arb.Anfrage(id, new Schallweg.V(e.QX, e.QY, e.QZ));
                namen[id] = e.Quelle;
                if (namen.Count > 512) namen.Clear();
                var inv = CultureInfo.InvariantCulture;
                float dyn = Physik.Dynamik.Value;
                var sb = new StringBuilder(120);
                sb.Append("path=").Append(ZeilenFormat.WegName[Math.Max(0, Math.Min(ZeilenFormat.WegName.Length - 1, e.Weg))])
                  .Append(" total -").Append(e.RohDb.ToString("F1", inv)).Append(" dB");
                if (!float.IsNaN(e.WertT)) sb.Append(" T ").Append(e.WertT.ToString("F2", inv));
                if (!float.IsNaN(e.WertD)) sb.Append(" D ").Append(e.WertD.ToString("F2", inv));
                if (!float.IsNaN(e.WertR)) sb.Append(" R ").Append(e.WertR.ToString("F2", inv));
                if (!float.IsNaN(e.WertRoute)) sb.Append(" route ").Append(e.WertRoute.ToString("F2", inv));
                z.ModDb = -e.RohDb;
                var pc = Raumweg.RechnerVon(occ);
                if (pc != null && Raumweg.RichtungsDaten(pc, occ.SourceContainer, out var aus, out float routeDb, out bool ersatz))
                {
                    z.RaumDb = -routeDb; z.Ersatz = ersatz;
                    var lt = occ.ListenerTransform;
                    if (lt != null) { var h = lt.position; z.ModAz = Math.Atan2(aus.X - h.x, aus.Z - h.z) * 180 / Math.PI; }
                    sb.Append(ersatz ? " | fallback path -" : " | room path -").Append(routeDb.ToString("F1", inv)).Append(" dB");
                    if (!double.IsNaN(z.ModAz)) sb.Append(" exit az ").Append(z.ModAz.ToString("F0", inv));
                }
                z.Mod = sb.ToString();
                offeneZeilen.Add(z);
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        static void Zeilen()
        {
            if (offeneZeilen.Count == 0) return;
            double jetzt = arb.Jetzt;
            for (int i = 0; i < offeneZeilen.Count; i++)
            {
                var z = offeneZeilen[i];
                var r = arb.Holen(z.Id);
                bool fertig = r != null && r.AnfrageZeit >= z.T0 - 1e-6;
                if (!fertig && jetzt - z.T0 < 3.0) continue;
                offeneZeilen.RemoveAt(i--);
                if (!KlangLog.An) continue;
                if (fertig && geschrieben.TryGetValue(z.Id, out var gw) && gw.Key == r.Zeit)
                {
                    // dasselbe Ergebnis wie die letzte Zeile dieser Quelle: nur Klang und Mod-Wert (Vergleich je Klang bleibt erhalten)
                    var inv = CultureInfo.InvariantCulture;
                    KlangLog.Text($"[PathSearch] f={z.Bild} src={z.Quelle} clip={z.Klang} d={z.Entf.ToString("F1", inv)} m | same result as f={gw.Value} | mod: {z.Mod}" +
                                  (double.IsNaN(z.ModDb) ? "" : $" | search minus mod (250 Hz) {(r.Summe[0] - z.ModDb).ToString("+0.0;-0.0", inv)} dB"));
                    dZeilen++; dGleich++; continue;
                }
                KlangLog.Text(fertig ? ZeileText(z, r) : $"[PathSearch] f={z.Bild} src={z.Quelle} clip={z.Klang}: no result after 3 s (listener tree being built or thread busy; {arb.OffenAnzahl} sources waiting, {arb.LaufendAnzahl} searches interrupted by the budget and continuing)");
                if (fertig) { dZeilen++; geschrieben[z.Id] = new KeyValuePair<double, int>(r.Zeit, z.Bild); if (geschrieben.Count > 2048) geschrieben.Clear(); } else dOhne++;
            }
        }

        static string ZeileText(Zeile z, Ergebnis r)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(400);
            sb.Append("[PathSearch] f=").Append(z.Bild).Append(" src=").Append(z.Quelle).Append(" clip=").Append(z.Klang)
              .Append(" d=").Append(z.Entf.ToString("F1", inv)).Append(" m | ").Append(r.Kachel)
              .Append(" | src (").Append(r.Q.X.ToString("F1", inv)).Append(", ").Append(r.Q.Y.ToString("F1", inv)).Append(", ").Append(r.Q.Z.ToString("F1", inv))
              .Append(") listener (").Append(r.H.X.ToString("F1", inv)).Append(", ").Append(r.H.Y.ToString("F1", inv)).Append(", ").Append(r.H.Z.ToString("F1", inv)).Append(')');
            if (r.QWeg > 0) sb.Append(", source point moved ").Append(r.QWeg.ToString("F2", inv)).Append(" m out of a body");
            if (r.HWeg > 0) sb.Append(", listener point moved ").Append(r.HWeg.ToString("F2", inv)).Append(" m out of a body");
            sb.Append(" | ");
            WegeText(sb, r, inv);
            sb.Append(" | mod: ").Append(z.Mod);
            if (!double.IsNaN(z.ModDb)) sb.Append(" | search minus mod (250 Hz) ").Append((r.Summe[0] - z.ModDb).ToString("+0.0;-0.0", inv)).Append(" dB");
            if (!double.IsNaN(z.RaumDb)) sb.Append(", minus ").Append(z.Ersatz ? "fallback" : "room path").Append(' ').Append((r.Summe[0] - z.RaumDb).ToString("+0.0;-0.0", inv)).Append(" dB");
            if (!double.IsNaN(z.ModAz) && !r.Frei) { double az = Az(r); double dz = ((az - z.ModAz) % 360 + 540) % 360 - 180; sb.Append(", arrival vs. exit ").Append(Math.Abs(dz).ToString("F0", inv)).Append(" deg"); }
            sb.Append(" | ").Append(r.Ms.ToString("F1", inv)).Append(" ms")
              .Append(r.Aufgegeben ? $" (no path within {r.Teile} budget slices - search given up, NOT a result 'no path')" : r.Abgebrochen ? " (budget reached, further paths skipped)" : "")
              .Append(r.Teile > 1 && !r.Aufgegeben ? $" (continued over {r.Teile} ticks after the budget)" : "")
              .Append(", ").Append(r.Strahlen).Append(" rays, tree ").Append(arb.LetzterBaumMs.ToString("F0", inv)).Append(" ms, result age ").Append(((arb.Jetzt - r.Zeit) * 1000).ToString("F0", inv)).Append(" ms");
            return sb.ToString();
        }

        static double Az(Ergebnis r) => Math.Atan2(r.Ankunft.X - r.H.X, r.Ankunft.Z - r.H.Z) * 180 / Math.PI;

        static void WegeText(StringBuilder sb, Ergebnis r, CultureInfo inv)
        {
            string Db(double[] d) => d[0].ToString("F1", inv) + "/" + d[1].ToString("F1", inv) + "/" + d[2].ToString("F1", inv);
            if (r.Frei) { sb.Append("line of sight, ").Append(Db(r.Summe)).Append(" dB"); return; }
            sb.Append(r.Wege.Count).Append(r.Wege.Count == 1 ? " path:" : " paths:");
            int k = 0;
            foreach (var w in r.Wege)
            {
                sb.Append(k++ == 0 ? " " : "; ").Append(w.Laenge.ToString("F1", inv)).Append(" m ").Append(Db(w.Db)).Append(" dB az ").Append(w.Azimut.ToString("F0", inv))
                  .Append(" el ").Append(w.Hoehe.ToString("F0", inv)).Append(" (").Append(w.Beugungen).Append(" edges");
                if (w.Gedeckelt) sb.Append(" ").Append(Db(w.Kanten)).Append(" dB, multiple diffraction limit, edge sum ").Append(Db(w.KantenEP));
                if (w.Tueren > 0) sb.Append(", ").Append(w.Tueren).Append(" closed door(s) ").Append(Db(w.Tuer));
                if (w.GangM > 0 && !w.RohrWeg && w.Rohr[1] > 0.05) sb.Append(", corridor +").Append(w.Rohr[1].ToString("F1", inv)).Append(" over ").Append(w.GangM.ToString("F0", inv)).Append(" m");
                sb.Append(')');
            }
            foreach (var rk in r.Raeume)
            {
                sb.Append(" | rooms (diffuse) ").Append(Db(rk.Db)).Append(" dB: ").Append(rk.Raeume[0]);
                for (int i = 0; i < rk.Portale.Length; i++) sb.Append(" >#").Append(rk.Portale[i]).Append("> ").Append(rk.Raeume[i + 1]);
            }
            sb.Append(" | through walls ").Append(Db(r.Durch.Db)).Append(" dB (").Append(r.Durch.Waende.Count).Append(r.Durch.Waende.Count == 1 ? " wall" : " walls");
            if (r.Durch.Tueren > 0) sb.Append(", ").Append(r.Durch.Tueren).Append(" door(s)");
            sb.Append(") | sum ").Append(Db(r.Summe)).Append(" dB, arrival az ").Append(Az(r).ToString("F0", inv));
        }

        /// <summary>Aus KlangLog.Markieren: Wege der Quellen in der Naehe (100 m, Ergebnis juenger als 10 s). Nur spielende Quellen stellen
        /// Anfragen (Ruhend), darum stehen hier nur echte Klaenge (Livetest MARK 1: 40 m waren zu klein, das Gefecht lag 47-76 m weg).</summary>
        const double MarkeM = 100;
        internal static void Marke(Vector3 hoerer)
        {
            if (arb == null || !KlangLog.An) return;
            try
            {
                var inv = CultureInfo.InvariantCulture; var l = new List<Ergebnis>(); arb.Ergebnisse(l);
                double jetzt = arb.Jetzt; var h = Sv(hoerer); int n = 0;
                l.Sort((a, b) => Schallweg.V.Abstand(a.Q, h).CompareTo(Schallweg.V.Abstand(b.Q, h)));
                foreach (var r in l)
                {
                    if (jetzt - r.Zeit > 10 || Schallweg.V.Abstand(r.Q, h) > MarkeM || (r.Id >> 32) != 2) continue;
                    if (n++ >= 12) break;
                    var sb = new StringBuilder(300);
                    sb.Append("[PathSearch] MARK nearby: src=").Append(namen.TryGetValue(r.Id, out var nm) ? nm : "#" + r.Id)
                      .Append(" at (").Append(r.Q.X.ToString("F1", inv)).Append(", ").Append(r.Q.Y.ToString("F1", inv)).Append(", ").Append(r.Q.Z.ToString("F1", inv))
                      .Append(") ").Append(Schallweg.V.Abstand(r.Q, h).ToString("F1", inv)).Append(" m, listener moved ").Append(Schallweg.V.Abstand(r.H, h).ToString("F1", inv))
                      .Append(" m since, age ").Append(((jetzt - r.Zeit) * 1000).ToString("F0", inv)).Append(" ms | ");
                    WegeText(sb, r, inv);
                    foreach (var w in r.Wege)
                    {
                        sb.Append(" | points");
                        for (int i = 1; i + 1 < w.Punkte.Count && i <= 8; i++) sb.Append(" (").Append(w.Punkte[i].X.ToString("F1", inv)).Append(',').Append(w.Punkte[i].Y.ToString("F1", inv)).Append(',').Append(w.Punkte[i].Z.ToString("F1", inv)).Append(')');
                    }
                    KlangLog.Text(sb.ToString());
                }
                if (n == 0) KlangLog.Text($"[PathSearch] MARK nearby: no playing sources with a result in the last 10 s within {MarkeM:F0} m.");
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        // ================================================================ Modus "Use": Ergebnis fuer einen Raumweg/Ersatzweg

        /// <summary>Ergebnis fuer eine Quelle (Container), wenn eines da ist und noch passt (Quelle hoechstens 1 m, Hoerer hoechstens
        /// 3 m daneben, juenger als 3 s - bis der neue Baum fertig ist, gilt das letzte). Stellt immer auch die Anfrage. null = Mod-Wert.</summary>
        internal static Ergebnis Fuer(AudioSourceContainer c, Vector3 q, Vector3 h)
        {
            if (!Ersetzt || c == null) return null;
            long id = IdBehaelter(c);
            var qv = Sv(q);
            // Raid-2-Fix (9): ein Schluessel darf nur EINEN Ort haben (alte Zuordnungsfalle aus Raid 1: je Behaelter mal Fuss-, mal Kopfort ->
            // der Faden rechnete dieselbe Id immer neu, Fuer verwarf das Ergebnis). Hier ist der Ort immer der Behaelterort (alle Aufrufer:
            // Raumweg/Ersatzweg geben sourceContainer.CurrentPosition = parentTransform.position + Offset, ein Punkt je Behaelter und Bild;
            // dekompiliert AudioSourceContainer.GetCurrentPosition). Kaeme dieselbe Id doch an zwei Orten an, zaehlt der Arbeiter das
            // (Arbeiter.DZweiOrte, Diagnose "use: container asked at two places"; Offline-Test 10d).
            arb.Anfrage(id, qv);
            var r = arb.Holen(id);
            // wegsuche-use (7a): die Quelle darf wie der Hoerer bis 3 m (Arbeiter.QuelleHaltM) vom Ort des Ergebnisses weg sein - neu gerechnet
            // wird ab 1 m (QuelleM). Vorher galt das Ergebnis nur bis 1 m, also genau bis zur Neuberechnung: jede riss eine Luecke mit dem
            // Mod-Wert (Use-Raid 18:40, Bot11: 12-16 dB Unterschied, 10 s und mehr ohne Suchwert). Gruende des Rueckfalls einzeln gezaehlt.
            if (r == null) { dRueckfall++; dOhneErg++; return null; }
            if (Schallweg.V.Abstand(r.Q, qv) > arb.QuelleHaltM) { dRueckfall++; dQuelleWeg++; return null; }
            if (Schallweg.V.Abstand(r.H, Sv(h)) > 3.0) { dRueckfall++; dHoererWeg++; return null; }
            if (arb.Jetzt - r.Zeit > 3.0) { dRueckfall++; dZuAlt++; return null; }
            dErsetzt++;
            return r;
        }

        // ================================================================ umwegsuche: Hindernis-Zweig ohne gefundenen Umweg

        static readonly int[] dUmweg = new int[10];   // je UmwegKern.Grund
        static readonly double[] umE = new double[3];

        /// <summary>Schluessel je Rechner des Spiels (Hindernis-Zweig): ein Rechner hat genau einen Quellort (c._sourcePosition).</summary>
        static long IdRechner(object c) => (3L << 32) | (uint)RuntimeHelpers.GetHashCode(c);

        /// <summary>
        /// umwegsuche (Hauptfaden, Wege.SchnappschussKern, nur wenn das Spiel keinen Umweg fand): Anfrage fuer diesen Rechner stellen (der Faden
        /// rechnet nur Quellen, die angefragt sind - also nur die an der Grenze) und, wenn ein frisches, plausibles Ergebnis da ist, die
        /// Energiesumme seiner Kantenwege je Band der Mod nach aus[] schreiben (true). Durchgang (Wand) und Raumanteile (Hallfeld) der Suche
        /// zaehlen NICHT: die Wand rechnet die Mod selbst (T), der Hall des Raums bleibt beim Hindernis im selben Raum (A2, RaumHallBleibt).
        /// gleicherRaum = Quelle im gemessenen Raum des Hoerers (WegZustand.GleicherRaum). Keine Allokation (Puffer statisch, Hauptfaden).
        /// </summary>
        internal static bool Umweg(object c, Vector3 q, Vector3 h, bool gleicherRaum, double[] f, WegZustand z, double[] aus)
        {
            z.SGrund = UmwegKern.Grund.KeinNetz;
            // wegtabelle (13.11.): zuerst die vorab gerechnete Tabelle (Hauptfaden, kein Netz noetig); nur ohne Wert die Live-Suche wie bisher
            if (WegTabelleSpiel.Umweg(q, h, f, z, aus)) return true;
            var a = arb;
            if (a == null || UmwegAn == null || !UmwegAn.Value || Aktiv == null || !Aktiv.Value || c == null) return false;
            long id = IdRechner(c); var qv = Sv(q);
            a.Anfrage(id, qv);
            var r = a.Holen(id);
            int n = 0; Weg laut = null;
            if (r != null && !r.Frei)
                foreach (var w in r.Wege) { if (w.Durchgang) continue; n++; if (laut == null || w.Db[1] > laut.Db[1]) laut = w; }
            bool imRaum = true;
            // Nebenbefund lampeweg: ein aus einem Koerper geschobener Quellpunkt kann im Nachbarraum liegen - dann gilt der Weg nicht hier
            if (r != null && n > 0 && gleicherRaum && r.QWeg > 0.01)
                imRaum = HallZonen.RaumFuer(h, new Vector3((float)r.QFrei.X, (float)r.QFrei.Y, (float)r.QFrei.Z), (float)f[0], out var ri) && ri.QuelleDrin;
            var g = UmwegKern.Pruefen(r != null, r != null && r.Frei, r != null && r.Aufgegeben, n,
                                      r != null ? Schallweg.V.Abstand(r.Q, qv) : 0, r != null ? Schallweg.V.Abstand(r.H, Sv(h)) : 0,
                                      r != null ? a.Jetzt - r.Zeit : 0, imRaum, a.QuelleHaltM, 3.0, 3.0);
            z.SGrund = g; dUmweg[(int)g]++;
            if (g != UmwegKern.Grund.Angewendet) return false;
            for (int b = 0; b < 3; b++) { double e = 0; foreach (var w in r.Wege) if (!w.Durchgang) e += Math.Pow(10, w.Db[b] / 10); umE[b] = UmwegKern.Summe(e); }
            UmwegKern.Baender(umE, Suche.F, f, aus);
            z.SWege = n; z.SKanten = laut.Beugungen; z.SAlterMs = (a.Jetzt - r.Zeit) * 1000;
            // Richtung (Auftrag 4, nur bereitstellen und loggen): letzter Kantenpunkt des lautesten Wegs vor dem Hoerer
            z.SHatKante = laut.Punkte.Count >= 3 && laut.Beugungen > 0;
            if (z.SHatKante) { var p = laut.Punkte[laut.Punkte.Count - 2]; z.SKante = new V3(p.X, p.Y, p.Z); }
            // wegtabelle: Richtungsdaten wie aus der Tabelle (Gesamtlaenge, erster Kantenpunkt); Rueckfall zaehlen, wenn die Tabelle geladen ist
            z.SLaenge = laut.Laenge; if (laut.Punkte.Count >= 3) { var p1 = laut.Punkte[1]; z.SErste = new V3(p1.X, p1.Y, p1.Z); }
            if (WegTabelleSpiel.Bereit) WegTabelleSpiel.LiveRueckfall();
            return true;
        }

        internal static string KetteText(Ergebnis r)
        {
            var sb = new StringBuilder(200); sb.Append(" | path search (use): ");
            WegeText(sb, r, CultureInfo.InvariantCulture);
            return sb.ToString();
        }

        // ================================================================ Diagnose (30 s)

        static void Diagnose()
        {
            var a = arb; if (a == null) return;
            int pre = a.DVorbau, pool = a.DVorratTreffer; double preMs = a.DVorbauMs;   // vor Leistung(): die setzt sie zurueck
            string leistung = a.Leistung();   // vorher: sammelt die Zaehler der Faeden ein
            int s = a.DSuchen, b = a.DBaeume;
            // Raid-2-Fix (8): Baeume klar getrennt (gebaut, weil der Hoerer wartete / vorab gebaut / aus dem Vorrat), Anfragen "done" statt "expired"
            string t = $"[Diagnostics] Path search ({Art.Value}, {a.Kachel}, {datei}, about {ladeMB} MB, loaded in {ladeS:F1} s): {s} sources calculated " +
                       (s > 0 ? $"(mean {a.DSucheMs / s:F1} / max {a.DSucheMax:F1} ms, {a.DUeberBudget} reached the {a.BudgetMs:F1} ms budget, {a.DFrei} line of sight)" : "") +
                       $", listener trees: {b + pre} built ({b} while waiting" + (b > 0 ? $", mean {a.DBaumMs / b:F0} / max {a.DBaumMax:F0} ms" : "") + $"; {pre} prebuilt ahead" + (pre > 0 ? $", mean {preMs / pre:F0} ms" : "") + $"), {pool} taken from the pool" +
                       $", {a.DTakte} ticks (longest {a.DTaktMax:F0} ms, {a.DTakteVoll} full), {a.OffenAnzahl} sources requested, {a.DAbgelaufenFertig} requests done (not renewed for {a.AnfrageS:F0} s), {a.DAbgelaufenOhne} dropped without result, " +
                       $"{a.DFortgesetzt} searches continued after the budget ({a.DAufgegeben} given up without a path), {a.DVerschoben} source points moved out of bodies or off surfaces, {a.DKetten} room shares, {dRuhend} idle sources skipped; " +
                       $"doors {portale.Count} live, {dTueren} changes; log lines {dZeilen} ({dGleich} of them short: same result as the line before), +{dOhne} without result, {dVoll} sounds skipped (more than 64 lines open)" +
                       (Art.Value == Modus.Use ? $"; use: {dErsetzt} values replaced, {dRueckfall} kept the mod value ({dOhneErg} no result yet, {dQuelleWeg} source moved > {a.QuelleHaltM:F0} m, {dHoererWeg} listener moved > 3 m, {dZuAlt} result older than 3 s), {a.DZweiOrte} container asked at two places in one tick"
                                               : $"; keys: {a.DZweiOrte} sources asked at two places in one tick; use dry run (container keys, counted only): {a.DProbeSchluessel} container requests, {a.DProbeZweiOrte} container asked at two places in one tick") +
                       UmwegDiag() +
                       $"; managed memory {GC.GetTotalMemory(false) / 1000000} MB | {leistung}.";
            Plugin.Log.LogInfo(t);
            KlangLog.Text(t);
            a.DSuchen = a.DUeberBudget = a.DFrei = a.DBaeume = a.DTakte = a.DTakteVoll = a.DVerworfen = a.DFortgesetzt = a.DAufgegeben = a.DVerschoben = a.DKetten = 0; dRuhend = 0; a.DSucheMs = a.DSucheMax = a.DBaumMs = a.DBaumMax = a.DTaktMax = 0;
            a.DAbgelaufenFertig = a.DAbgelaufenOhne = 0;
            dZeilen = dOhne = dErsetzt = dRueckfall = dTueren = dVoll = dGleich = 0; dOhneErg = dQuelleWeg = dHoererWeg = dZuAlt = 0; a.DZweiOrte = a.DProbeZweiOrte = a.DProbeSchluessel = 0;
        }

        /// <summary>umwegsuche: Zaehler der Rechnungen ohne gefundenen Umweg (Hindernis-Zweig) seit der letzten Diagnose, dann zurueck auf 0.</summary>
        static string UmwegDiag()
        {
            int alle = 0; foreach (int x in dUmweg) alle += x;
            if (alle == 0 && (UmwegAn == null || !UmwegAn.Value)) return "";
            var sb = new StringBuilder(160);
            sb.Append("; obstacles without a detour: ").Append(dUmweg[(int)UmwegKern.Grund.Angewendet]).Append(" of ").Append(alle).Append(" checks used the path search (checked at each game calculation and up to 10 Hz between)");
            if (UmwegAn != null && !UmwegAn.Value) sb.Append(" (switch 'Use for obstacles without a detour' off)");
            bool erst = true;
            for (int i = 1; i < dUmweg.Length; i++)
            {
                if (dUmweg[i] == 0) continue;
                sb.Append(erst ? ", screening limit kept: " : ", ").Append(dUmweg[i]).Append(' ').Append(UmwegKern.Text((UmwegKern.Grund)i)); erst = false;
            }
            Array.Clear(dUmweg, 0, dUmweg.Length);
            return sb.ToString();
        }

        static Schallweg.V Sv(Vector3 v) => new Schallweg.V(v.x, v.y, v.z);
    }

    // ------------------------------------------------------------------------------------------
    //  Modus "Use": Einhaengepunkt im Raumweg (Raumweg.SpaetNach vor Rechnen, Ersatzweg.Pruefen vor dem Fuellen des Mischers).
    // ------------------------------------------------------------------------------------------
    internal static partial class Raumweg
    {
        /// <summary>true = der Mischer von z ist mit den Wegen der Wegsuche gefuellt (Daemmung je Band, Austrittspunkt = letzter Wegpunkt).</summary>
        static bool WegsucheMischen(Zustand z, double jetzt)
        {
            if (z.Behaelter != null && Wegsuche.ProbeAn) { try { Wegsuche.Probe(z.Behaelter, z.Umgekehrt ? z.B : z.A); } catch (Exception e) { Statistik.Fehler(e); } }   // Raid-3-Fix: Use-Schluessel probehalber
            if (!Wegsuche.Ersetzt || z.Behaelter == null) return false;
            try
            {
                Vector3 q = z.Umgekehrt ? z.B : z.A, h = z.Umgekehrt ? z.A : z.B;
                var r = Wegsuche.Fuer(z.Behaelter, q, h);
                if (r == null) return false;
                double dyn = Physik.Dynamik.Value;
                z.Mix.Beginn();
                var d = r.Durch;
                z.Mix.Kandidat(d.Id, Daempf(d.Db[0], dyn), Daempf(d.Db[1], dyn), Daempf(d.Db[2], dyn), new V3(r.Q.X, r.Q.Y, r.Q.Z), default(RaumwegErgebnis), null, 0);
                if (!r.Frei)
                    foreach (var w in r.Wege)
                    {
                        var p = w.Punkte[w.Punkte.Count - 2];
                        z.Mix.Kandidat(w.Id, Daempf(w.Db[0], dyn), Daempf(w.Db[1], dyn), Daempf(w.Db[2], dyn), new V3(p.X, p.Y, p.Z), default(RaumwegErgebnis), null, 0);
                    }
                // diffus-hall: Hallfeld nur, wenn der Hoerer in einem gemessenen Innenraum steht (sonst strahlt die letzte Oeffnung gerichtet)
                var raumAnteil = HallZonen.RaumFuer(h, q, 250f, out _) ? DiffusVoll : default(RaumwegErgebnis);
                if (!r.Frei)
                    foreach (var k in r.Raeume)   // 0.8.6: Raumanteil (Diffusfeld) der Raumfolgen, Richtung = letzte Oeffnung
                        z.Mix.Kandidat(k.Id, Daempf(k.Db[0], dyn), Daempf(k.Db[1], dyn), Daempf(k.Db[2], dyn), new V3(k.Austritt.X, k.Austritt.Y, k.Austritt.Z), raumAnteil, null, 0);
                z.Mix.Uebernehmen(jetzt, BlendS, RangNJetzt, BreiteJetzt);
                z.Kette = KlangLog.An ? Wegsuche.KetteText(r) : null;
                return true;
            }
            catch (Exception e) { Statistik.Fehler(e); return false; }
        }
        // diffus-hall: Raumfolgen der Wegsuche sind reines Hallfeld (4P/A, RaumwegKern.Diffus), Durchgang und Kantenwege gerichtet (Anteil 0)
        static readonly RaumwegErgebnis DiffusVoll = RaumwegErgebnis.NurAnteil(1.0);
        static double Daempf(double db, double dyn) => Math.Max(0, Math.Min(dyn, -db));
    }
}
