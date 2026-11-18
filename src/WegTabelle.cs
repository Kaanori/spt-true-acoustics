using System;
using System.IO;
using System.Threading.Tasks;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using Materialklang.Schallweg;
using UnityEngine;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    //  wegtabelle (Etappe 2, 13.11.2026) - Abschnitt 19 "Precomputed obstacle paths" (Standard AN).
    //  Im Hindernis-Zweig ohne gefundenen Umweg (WegeKern.UmwegUnbekanntAus, Wege.cs) fragt Wegsuche.Umweg ZUERST hier: Kantenwege aus der
    //  vorab gerechneten Tabelle der Karte (Schallweg/SwTabelle.cs; Bericht review-20261110/wegtabelle-STAND.md). Hauptfaden, ~0,3 us, keine
    //  Speicheranforderung, kein Strahl, kein Wegenetz (~13 MB statt ~700 MB), deterministisch. Ohne Wert (Zelle ohne Sonde, anderer Raum,
    //  Paar ohne Weg) oder ohne Tabelle: bisheriges Verhalten (Live-Suche, wenn der Hauptschalter an ist, sonst Schirmgrenze).
    //  Schranken wie umwegsuche (WegeKern.Auswerten): nie lauter als freie Sicht (0 dB), nie leiser als die Schirmgrenze (ISO 9613-2) -
    //  die untere bleibt, weil sie die Physik des Hindernisses (hoechstens 20/25 dB Beugungsverlust) beschreibt und nicht die Suche.
    //  Glaettung: Tabellenwert folgt mit begrenzter Rate (TabGleitKern, Regler lauter/leiser), danach gleitet S wie bisher (SucheSetzen/SBl).
    //  Datei: BepInEx/cache/TrueAcoustics/<karte>-<pruefsumme des Wegenetzes>.wegtabelle; Pruefsumme ueber dieselben Spieldateien wie das
    //  Wegenetz (Spiel-Update -> passt nicht -> nicht geladen, eine Warnzeile).
    // ------------------------------------------------------------------------------------------
    internal static class WegTabelleSpiel
    {
        internal static ConfigEntry<bool> An;
        internal static ConfigEntry<float> Rauf, Runter;
        internal static ConfigEntry<bool> SichtAn;
        static readonly LinecastSicht sicht = new LinecastSicht();

        static WegTabelle tab; static Task<WegTabelle> laden; static string ladeKarte, ladeText, datei; static double ladeS;
        static float naechsteDiag;
        static int dAbfragen, dWert, dLive; static readonly int[] dGrund = new int[8];
        static readonly TabErgebnis erg = new TabErgebnis();
        static readonly double[] band = new double[WegeKern.N];

        internal static bool Bereit => tab != null;

        internal static void Binden(ConfigFile c, string sek)
        {
            An = c.Bind(sek, "Precomputed obstacle paths", true,
                "If source and listener are in the same room and the game finds no way around the obstacles between them (sound log: 'no detour found'), " +
                "the paths around the edges come from a table that was calculated in advance for the map (file <map>-<checksum>.wegtabelle in " +
                "BepInEx/cache/TrueAcoustics). Works without 'Path search (experimental)' and without its 700 MB network (about 13 MB on Factory, no " +
                "background thread). Never quieter than the screening limit, never louder than line of sight. No table or no value for a place = as before. " +
                "Applies live (the table is loaded at the next raid start).");
            Rauf = c.Bind(sek, "Precomputed paths: max. rise (dB/s)", 60f,
                new ConfigDescription("How fast the table value may get louder (assumption, not measured: a bot at 5 m/s crosses a ~1 m wide shadow edge with ~12 dB in 0.2 s). Applies live.",
                    new AcceptableValueRange<float>(5f, 500f)));
            Runter = c.Bind(sek, "Precomputed paths: max. fall (dB/s)", 15f,
                new ConfigDescription("How fast the table value may get quieter (assumption: hides small steps when a source walks from one table cell to the next). Applies live.",
                    new AcceptableValueRange<float>(1f, 500f)));
            SichtAn = c.Bind(sek, "Precomputed paths: line-of-sight check", true,
                "Pick the table probes that are really visible from the source and from the listener (a few Physics.Linecast per lookup, same " +
                "layers as the game's sound rays, cached, at most 16 per frame). Off = the probes chosen when the table was built (cell centre). " +
                "Needs a table in format 7. Applies live.");
        }

        internal static void SzeneGewechselt() { tab = null; laden = null; ladeKarte = null; }

        /// <summary>Aus Wegsuche.Tick (jedes Bild, auch bei ausgeschaltetem Hauptschalter): Tabelle der Karte laden, Diagnose je 30 s.</summary>
        internal static void Tick()
        {
            if (An == null || !An.Value) { if (tab != null || laden != null) SzeneGewechselt(); return; }
            if (!Singleton<GameWorld>.Instantiated || Singleton<GameWorld>.Instance.MainPlayer == null) { if (tab != null || laden != null || ladeKarte != null) SzeneGewechselt(); return; }
            string karte = Wegsuche.KarteVon(Singleton<GameWorld>.Instance.LocationId);
            if (karte == null) return;
            if (laden == null && tab == null && ladeKarte != karte) StartLaden(karte);
            if (laden != null && laden.IsCompleted)
            {
                var t = laden; laden = null;
                if (t.Status == TaskStatus.RanToCompletion && t.Result != null) { tab = t.Result; Plugin.Log.LogInfo("Precomputed obstacle paths: " + ladeText); KlangLog.Text("[PathTable] " + ladeText); }
                else if (t.Exception != null) Plugin.Log.LogWarning("Precomputed obstacle paths: loading failed: " + t.Exception.GetBaseException().Message);
                else Plugin.Log.LogInfo("Precomputed obstacle paths: " + ladeText);   // fehlende/unpassende Datei ist kein Fehler (optionale Daten)
            }
            float jetzt = Time.realtimeSinceStartup;
            if (tab != null && jetzt >= naechsteDiag) { naechsteDiag = jetzt + 30f; Diagnose(); }
        }

        static void StartLaden(string karte)
        {
            ladeKarte = karte; ladeText = null;
            string dir = Path.Combine(BepInEx.Paths.CachePath, "TrueAcoustics");
            string daten = Application.dataPath;   // Hauptfaden
            laden = Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string grund = "no file " + karte + "-*.wegtabelle";
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, karte + "-*.wegtabelle"))
                    {
                        var k = WegTabelle.Kopf(f, out string g0);
                        if (k == null) { grund = Path.GetFileName(f) + ": " + g0; continue; }
                        if (!k.Passt(daten, out string g)) { grund = Path.GetFileName(f) + ": " + g + " (made for other game files)"; continue; }
                        var t = WegTabelle.Lesen(f);
                        ladeS = sw.Elapsed.TotalSeconds; datei = Path.GetFileName(f);
                        ladeText = $"map {karte}: {datei} loaded in {ladeS:F2} s - {t.RN} rooms, {t.SOff[t.RN]} probes, {t.ZKey.Length} cells, about {t.Bytes / 1000000} MB (built {t.Erstellt}).";
                        return t;
                    }
                ladeText = $"map {karte}: no precomputed obstacle paths for this map ({grund}) - optional data, not part of the public release; the screening limit applies instead.";
                return (WegTabelle)null;
            });
        }

        /// <summary>wegkarten (16.11.): obere Schranke je Band = Pegel desselben Wegs OHNE Hindernis (dB gegen Freifeld gleichen Abstands). Heute 0 dB,
        /// weil die Mod bei freier Sicht 0 dB spielt (WegeKern.Auswerten, Zweig frei) - Gang-Fuehrung (Rohr.Gewinn3) gibt es nur in der Suche.
        /// Spielt die Mod spaeter bei freier Sicht einen Gangpegel (Auftrag klang-akustik), gehoert derselbe Kernaufruf HIER hin, damit ein Hindernis
        /// nie lauter macht als kein Hindernis und der Gang-Gewinn trotzdem bleibt.</summary>
        static double Obergrenze(Vector3 q, Vector3 h, int band) => 0.0;

        /// <summary>Hindernis-Zweig ohne Umweg (aus Wegsuche.Umweg, Hauptfaden): Tabellenwert je Band der Mod nach aus (ratenbegrenzt) und
        /// Richtungsdaten nach z. false = kein Wert (Grund in z.STabGrund) oder keine Tabelle. Keine Speicheranforderung.</summary>
        internal static bool Umweg(Vector3 q, Vector3 h, double[] f, WegZustand z, double[] aus)
        {
            z.STab = false;
            if (tab == null || An == null || !An.Value) { z.STabGrund = (int)WegTabelle.Grund.KeineTabelle; z.TabHat = false; return false; }
            dAbfragen++;
            if (!tab.Holen(new V3(q.x, q.y, q.z), new V3(h.x, h.y, h.z), erg, SichtJetzt(true)))
            {
                dGrund[(int)erg.Grund]++; z.STabGrund = (int)erg.Grund; z.TabHat = false; return false;
            }
            dWert++;
            UmwegKern.Baender(erg.Db, Suche.F, f, band);
            for (int b = 0; b < band.Length; b++) band[b] = Math.Min(Obergrenze(q, h, b), band[b]);   // Summe mehrerer Wege: nie lauter als unverdeckt
            double jetzt = Time.realtimeSinceStartup;
            TabGleitKern.Folgen(z.TabGlatt, band, jetzt - z.TabZeit, Rauf.Value, Runter.Value, !z.TabHat);
            z.TabHat = true; z.TabZeit = jetzt;
            for (int b = 0; b < aus.Length && b < band.Length; b++) aus[b] = z.TabGlatt[b];
            z.STab = true; z.STabGrund = (int)WegTabelle.Grund.Wert; z.SGrund = UmwegKern.Grund.Angewendet;
            z.SWege = 0; z.SKanten = erg.Kanten; z.SLaenge = erg.Laenge; z.SAlterMs = 0;
            z.SHatKante = erg.Kanten > 0; z.SKante = erg.Letzter; z.SErste = erg.Erster;
            z.STabQ = erg.SondeQ; z.STabH = erg.SondeH; z.STabRaum = erg.Raum;
            z.STabFern = erg.FernN; z.STabStrahlen = erg.Strahlen; z.STabSicht = erg.SichtGewaehlt > 0;
            return true;
        }

        /// <summary>wegrichtung (13.11.): Tabelle am ECHTEN Ort nachschlagen (Richtung.cs, je Bild fuer jede Quelle mit Richtung "from the edge"):
        /// die 2 x 2 Sondenpaare mit ihren letzten Kantenpunkten in r. Hauptfaden, ~0,5 us, keine Speicheranforderung, kein Zaehler.</summary>
        internal static bool Kanten(V3 q, V3 h, TabErgebnis r)
        {
            if (tab == null || An == null || !An.Value) { r.PaarN = 0; return false; }
            return tab.Holen(q, h, r, SichtJetzt(false));
        }

        /// <summary>wegsicht: Sichtpruefung fuer dieses Nachschlagen, oder null (Schalter aus, Format &lt; 7, Bildbudget erschoepft -> Wahl aus dem Bau).
        /// zaehlen = Abfrage des Pegels (Diagnose), sonst Richtung je Bild.</summary>
        static ISicht SichtJetzt(bool zaehlen)
        {
            if (SichtAn == null || !SichtAn.Value || tab.Version < 7) return null;
            if (!sicht.Frei_Budget(2 * WegTabelle.KandMax)) { if (zaehlen) dSichtBudget++; return null; }
            if (zaehlen) dSicht++;
            return sicht;
        }
        static int dSicht, dSichtBudget;

        /// <summary>Live-Suche als Rueckfall fuer einen Ort ohne Tabellenwert angewendet (Diagnose).</summary>
        internal static void LiveRueckfall() => dLive++;

        internal static string GrundText(int g)
        {
            switch ((WegTabelle.Grund)g)
            {
                case WegTabelle.Grund.Wert: return "value";
                case WegTabelle.Grund.KeineTabelle: return "no table for this map";
                case WegTabelle.Grund.ZelleOhneSonde: return "no probe visible from this place";
                case WegTabelle.Grund.AndererRaum: return "source and listener in different table rooms";
                case WegTabelle.Grund.KeinWertPaar: return "no edge path between the probes";
                case WegTabelle.Grund.ZuNah: return "source closer than 3 m (finer than the table grid)";
                case WegTabelle.Grund.Geparkt: return "parked source";
                case WegTabelle.Grund.UeberPaargrenze: return "probe pair not stored (beyond the pair limit of the table)";
                default: return "-";
            }
        }

        static void Diagnose()
        {
            int ohne = dAbfragen - dWert;
            if (tab.Version >= 7)
            {
                string ts = $"[Diagnostics] Precomputed paths, line-of-sight check: {(SichtAn != null && SichtAn.Value ? "on" : "off")}, {dSicht} lookups checked, {dSichtBudget} over the frame budget; " +
                            $"{sicht.N} linecasts + {sicht.Treffer} cached, {(sicht.N > 0 ? 100.0 * sicht.FreiN / sicht.N : 0):F0} % free (expected from the path network: {tab.SichtFreiErwartet:F0} % - far below = the game's colliders differ from the table's geometry), " +
                            $"{sicht.Zeit * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F2} ms in 30 s, max {sicht.MaxBildZeit * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F3} ms and {sicht.MaxBild} linecasts in one frame.";
                Plugin.Log.LogInfo(ts); KlangLog.Text(ts); sicht.Zuruecksetzen(); dSicht = dSichtBudget = 0;
            }
            string t = $"[Diagnostics] Precomputed obstacle paths ({datei}): {dAbfragen} lookups, {dWert} with a value" +
                       (dAbfragen > 0 ? $" ({100.0 * dWert / dAbfragen:F0} %)" : "") + $", {ohne} without ({dGrund[2]} no probe visible, {dGrund[3]} different rooms, {dGrund[4]} no edge path between the probes, {dGrund[5]} closer than 3 m, {dGrund[6]} parked, {dGrund[7]} beyond the pair limit)" +
                       $", of those {dLive} used the live path search instead, the rest kept the screening limit.";
            Plugin.Log.LogInfo(t); KlangLog.Text(t);
            dAbfragen = dWert = dLive = 0; Array.Clear(dGrund, 0, dGrund.Length);
        }
    }

    /// <summary>wegsicht: Sichtpruefung fuer WegTabelle.Holen mit Physics.Linecast (Hauptfaden), Ebenen wie die Schallstrahlen des Spiels
    /// (TransmissionCalculator._queryParams.layerMask = Terrain + HighPolyCollider, Statistik.AudioMaske; dieselben Ebenen liest der Export des
    /// Wegenetzes). Tuerblaetter liegen im Spiel auf diesen Ebenen, im Wegenetz nicht: eine geschlossene Tuer sperrt die Linie - gewollt, die
    /// Tabelle ist mit gesperrten schliessbaren Portalen gerechnet. Ergebnis je (Punkt auf 0,5 m, Sonde) 1 s gemerkt (offene Tuer: neu nach 1 s).
    /// Hoechstens Budget Linecasts je Bild (sonst Wahl aus dem Bau). Keine Speicheranforderung.</summary>
    internal sealed class LinecastSicht : ISicht
    {
        const int C = 4096; const float Halt = 1f; const int Budget = 16;   // ~5 us je Linecast (Annahme, im Log gemessen) -> <= 0,1 ms je Bild
        readonly long[] key = new long[C]; readonly float[] zeit = new float[C]; readonly bool[] wert = new bool[C];
        public int N, FreiN, Treffer, MaxBild; public long Zeit, MaxBildZeit;
        int bild = -1, imBild; long bildZeit;
        public void Zuruecksetzen() { N = FreiN = Treffer = MaxBild = 0; Zeit = MaxBildZeit = 0; }
        void Bild() { int f = Time.frameCount; if (f != bild) { bild = f; imBild = 0; bildZeit = 0; } }
        /// <summary>Darf dieses Nachschlagen noch pruefen? Weiche Grenze: unter Budget Linecasts in diesem Bild (gemerkte Ergebnisse zaehlen nicht);
        /// ein Nachschlagen braucht hoechstens n weitere - schlimmstenfalls Budget + n je Bild.</summary>
        public bool Frei_Budget(int n) { Bild(); return imBild < Budget; }
        static long Q(float x) => (long)Math.Floor(x * 2f) & 0xFFFF;   // Punkt auf 0,5 m
        public bool Frei(V3 a, V3 b)
        {
            Bild();
            long k = (Q((float)a.X) << 48) ^ (Q((float)a.Y) << 32) ^ (Q((float)a.Z) << 16) ^ (long)((uint)((float)b.X * 64f) * 73856093u ^ (uint)((float)b.Y * 64f) * 19349663u ^ (uint)((float)b.Z * 64f) * 83492791u) * 0x9E3779B1L;
            int i = (int)((ulong)(k ^ (k >> 29)) % C); float jetzt = Time.realtimeSinceStartup;
            if (key[i] == k && jetzt - zeit[i] < Halt) { Treffer++; return wert[i]; }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int maske = Statistik.AudioMaske != 0 ? Statistik.AudioMaske : (int)LayersMaskController.HighPolyWithTerrainMask;
            bool frei = !Physics.Linecast(new Vector3((float)a.X, (float)a.Y, (float)a.Z), new Vector3((float)b.X, (float)b.Y, (float)b.Z), maske, QueryTriggerInteraction.Ignore);
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            key[i] = k; zeit[i] = jetzt; wert[i] = frei;
            N++; if (frei) FreiN++; imBild++; Zeit += dt; bildZeit += dt;
            if (imBild > MaxBild) MaxBild = imBild; if (bildZeit > MaxBildZeit) MaxBildZeit = bildZeit;
            return frei;
        }
    }
}
