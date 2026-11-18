using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using EFT.Ballistics;
using UnityEngine;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // hallabsorption (17.11.2026) - Spiel-Teil der Sehnenmessung (Rechnung und Quellen: SehnenKern.cs).
    // Ablauf: HallZonen.Messen legt wie bisher je Raum die Boxen, S_Box, SOffen und das alte alpha (64 Strahlen) an - das ist der
    // Zwischenwert. Danach misst SehnenSchritt je Bild hoechstens BudgetMs Zufallssehnen (Physics.Raycast nur im Hauptfaden), Raum fuer Raum,
    // 400 Sehnen je Raum. Ist ein Raum fertig, gilt sein aequivalenter Wandwert (AlphaSehnen) sofort fuer Raumweg, Wandweg und Nachhall-
    // Stimme (HallZonen.WandAlpha/RaumFuer lesen live); sind alle fertig, werden die Hallzonen einmal neu gelegt. Ergebnis je Raum im Cache
    // BepInEx/cache/TrueAcoustics/room-absorption.tsv (Schluessel: Pruefsumme der Boxen + Name) - ab dem zweiten Raid sofort da.
    // Budget (Annahme, die Diagnosezeile misst): ein Raycast in Factory ~2-5 us -> 1 ms je Bild ~ 200-400 Strahlen. Je Testlinie so viele
    // Strahlen wie Flaechen auf ihr + 2 (offline Factory: siehe Bericht), dazu je Collider einmal die Schrittflaechen-Probe bei poroesem
    // Boden. Nur beim ersten Raid je Karte (danach Cache); bis dahin gilt die alte Messung.
    // ------------------------------------------------------------------------------------------
    internal static partial class HallZonen
    {
        internal static ConfigEntry<bool> SehnenAn;
        /// <summary>Rechenzeit je Bild fuer die Sehnen (ms). Annahme: 1 ms von 16,7 ms (60 Bilder/s) ist im Raid-Start nicht spuerbar;
        /// die Hallmessung selbst kostet dort 0,2-0,5 s am Stueck.</summary>
        const double BudgetMs = 1.0;
        /// <summary>Cache-Version: hochzaehlen, wenn Materialtabelle, Zuordnung (SehnenKern.Akustisch) oder Stichprobe sich aendern.</summary>
        const int CacheVersion = 1;

        static int sehnenRaum = -1;            // Index in daten, -1 = nicht gestartet
        static bool sehnenFertig, cacheGeladen;
        static SehnenKern.Sammler sammler;
        static System.Random sehnenZufall;
        static long sehnenStrahlen, sehnenTicks; static int sehnenBilder, sehnenAusCache, sehnenPoroes, sehnenHart; static double sehnenMaxMs;
        static readonly double[] alphaPuffer = new double[6];
        static readonly double[] matPuffer = new double[6], boxPuffer = new double[6];
        static readonly Dictionary<int, string> akustikCache = new Dictionary<int, string>(1024);
        static Dictionary<ulong, string[]> cacheZeilen;

        static float[] AlphaVon(RaumDaten d) => SehnenAn != null && SehnenAn.Value && d.HatSehnen ? d.AlphaSehnen : d.Alpha;

        static void SehnenBinden(ConfigFile cfg, string sek)
        {
            SehnenAn = cfg.Bind(sek, "Room absorption from random chords", true,
                "On (default since 0.8.6): the absorption of every interior room comes from random chords through its air volume " +
                "(mean free path 4V/S, Kuttruff): machines, crates, pipes and walkways count with their own surface, and each material counts with its " +
                "area. Before, only the outer surface of the room boxes and 64 rays from the room centre counted - big furnished halls came out with " +
                "6-7 s reverb instead of about 2.5-4.5 s. The measurement runs at raid start spread over the frames (1 ms per frame) and is cached " +
                "(BepInEx/cache/TrueAcoustics), so from the second raid on a map it is there at once. Used by the reverb zones, the room paths, the " +
                "wall paths and the reverb voices alike. Off = the old measurement. Applies live.");
            SehnenAn.SettingChanged += (_, __) => { if (daten != null) Anstehend(0.3f); };
        }

        static void SehnenLeeren()
        {
            sehnenRaum = -1; sehnenFertig = false; cacheGeladen = false; sammler = null;
            sehnenStrahlen = sehnenTicks = 0; sehnenBilder = sehnenAusCache = sehnenPoroes = sehnenHart = 0; sehnenMaxMs = 0;
            akustikCache.Clear();
        }

        static string CacheDatei => Path.Combine(Path.Combine(BepInEx.Paths.CachePath, "TrueAcoustics"), "room-absorption.tsv");

        /// <summary>Ein Teilschritt je Bild (aus Tick). Keine Allokation je Sehne.</summary>
        static void SehnenSchritt()
        {
            if (sehnenFertig || daten == null || daten.Count == 0 || SehnenAn == null || !SehnenAn.Value) return;
            if (!SpatialAudioSystem_Initialized()) return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                if (!cacheGeladen) { cacheGeladen = true; CacheLesen(); }
                if (sehnenRaum < 0) { sehnenRaum = 0; sehnenZufall = new System.Random(17112026); sammler = new SehnenKern.Sammler(Baender.Length); }
                int maske = LayersMaskController.HighPolyWithTerrainMask;
                bool rueck = Physics.queriesHitBackfaces;
                Physics.queriesHitBackfaces = true;   // Punkt im Objekt erkennen (erster Treffer = Rueckseite); am Ende zurueck
                try
                {
                    long grenze = t0 + (long)(BudgetMs * Stopwatch.Frequency / 1000.0);
                    while (sehnenRaum < daten.Count && Stopwatch.GetTimestamp() < grenze)
                    {
                        var d = daten[sehnenRaum];
                        if (d.HatSehnen) { sehnenRaum++; sammler.Leeren(); continue; }
                        if (sammler.Versuche == 0) sammler.Ziel = SehnenKern.ZielFuer(d.Volumen);
                        if (sammler.Fertig) { RaumAbschliessen(d); sehnenRaum++; sammler.Leeren(); continue; }
                        EineSehne(d, maske);
                    }
                }
                finally { Physics.queriesHitBackfaces = rueck; }
                if (sehnenRaum >= daten.Count) AllesFertig();
            }
            catch (Exception ex) { Statistik.Fehler(ex); sehnenFertig = true; }
            finally
            {
                long dt = Stopwatch.GetTimestamp() - t0;
                sehnenTicks += dt; sehnenBilder++;
                sehnenMaxMs = Math.Max(sehnenMaxMs, dt * 1000.0 / Stopwatch.Frequency);
            }
        }

        static bool SpatialAudioSystem_Initialized() => Audio.SpatialSystem.SpatialAudioSystem.Initialized;

        static void EineSehne(RaumDaten d, int maske)
        {
            var ks = d.Kaesten; int n = ks.Length;
            sammler.Versuche++;
            var rz = sehnenZufall;
            int i = SehnenKern.KastenWaehlen(ks, n, rz.NextDouble());
            SehnenKern.PunktIn(ks[i], rz.NextDouble(), rz.NextDouble(), rz.NextDouble(), out double x, out double y, out double z);
            if (!SehnenKern.Annehmen(ks, n, x, y, z, rz.NextDouble())) return;
            SehnenKern.Richtung(rz.NextDouble(), rz.NextDouble(), out double dx, out double dy, out double dz);
            var o = new Vector3((float)x, (float)y, (float)z);
            var dir = new Vector3((float)dx, (float)dy, (float)dz);
            sammler.LinieBeginnen();
            float l = Halblinie(ks, o, dir, maske) + Halblinie(ks, o, -dir, maske);
            sammler.LinieEnde(l);
        }

        /// <summary>Halbe Testlinie ab o: zaehlt die an Luft grenzenden Flaechen bis zum Rand der Vereinigung (SehnenKern-Kopf). Luft -> fest =
        /// Vorderseite bei Tiefe 0, fest -> Luft = Rueckseite bei Tiefe 1 (Rueckseiten nur mit Physics.queriesHitBackfaces). Am Rand in Luft:
        /// Wand knapp ausserhalb (bis 0,6 m, Vorderseite) oder offene Grenze; in fest: Ende (die Wand ist schon gezaehlt). Ergebnis: Laenge (m).</summary>
        static float Halblinie(Kasten[] ks, Vector3 o, Vector3 dir, int maske)
        {
            float tEx = (float)RaumGeometrieKern.Austritt(ks, ks.Length, o.x, o.y, o.z, dir.x, dir.y, dir.z);
            float t0 = 0f, ende = tEx; int tiefe = -1;
            for (int k = 0; k < 64; k++)
            {
                sehnenStrahlen++;
                if (!Physics.Raycast(o + dir * t0, dir, out RaycastHit hit, tEx + 0.6f - t0, maske, QueryTriggerInteraction.Ignore))
                {
                    if (tiefe <= 0) sammler.Offen();
                    return ende;
                }
                float tt = t0 + hit.distance;
                bool vorne = Vector3.Dot(hit.normal, dir) <= 0f;
                if (tiefe < 0) tiefe = vorne ? 0 : 1;
                if (tt > tEx)
                {
                    if (tiefe == 0) { if (vorne) { Material(hit, alphaPuffer); sammler.Flaeche(alphaPuffer); ende = tt; } else sammler.Offen(); }
                    return ende;
                }
                if ((vorne && tiefe == 0) || (!vorne && tiefe == 1)) { Material(hit, alphaPuffer); sammler.Flaeche(alphaPuffer); }
                tiefe = Math.Max(0, tiefe + (vorne ? 1 : -1));
                t0 = tt + 0.0005f;
            }
            return ende;
        }

        static void Material(RaycastHit hit, double[] alpha)
        {
            string mat = Akustisch(hit);
            for (int b = 0; b < Baender.Length; b++) alpha[b] = Alpha(mat, Baender[b]);
        }

        /// <summary>Akustisches Material des Treffers (SehnenKern.Akustisch): poroeses Geschossmaterial mit harter Schrittflaeche -> harte
        /// Flaeche. Schrittflaeche wie das Spiel (Player.cs: Linecast mit AudioControllerStepLayerMask, BaseBallistic.GetSurfaceSound am
        /// Treffer) - hier entlang der Flaechennormalen von 0,5 m vor der Flaeche auf sie zu. Nur fuer bodenaehnliche Flaechen (Normale
        /// nach oben, &gt; 45 Grad), denn nur dort gibt es Schritte; je Collider einmal.</summary>
        static string Akustisch(RaycastHit hit)
        {
            var col = hit.collider;
            int id = col != null ? col.GetInstanceID() : 0;
            if (id != 0 && akustikCache.TryGetValue(id, out var m)) return m;
            string ball = Materialname(col, null);
            string r = ball;
            if (SehnenKern.Poroes(ball) && hit.normal.y > 0.7071f)
            {
                sehnenPoroes++;
                string flaeche = null;
                var a = hit.point + hit.normal * 0.5f;
                sehnenStrahlen++;
                if (Physics.Raycast(a, -hit.normal, out RaycastHit h2, 0.6f, LayersMaskController.AudioControllerStepLayerMask, QueryTriggerInteraction.Ignore)
                    && h2.collider != null)
                {
                    var bb = h2.collider.GetComponent<BaseBallistic>();
                    if (bb != null) flaeche = bb.GetSurfaceSound(h2.point).ToString();
                }
                r = SehnenKern.Akustisch(ball, flaeche);
                if (r != ball) sehnenHart++;
            }
            if (id != 0) { if (akustikCache.Count > 20000) akustikCache.Clear(); akustikCache[id] = r; }
            return r;
        }

        static void RaumAbschliessen(RaumDaten d)
        {
            if (!SehnenKern.Ergebnis(sammler, d.Volumen, d.Oberflaeche, d.SOffen, matPuffer, boxPuffer, out double sw, out double fo, out double vl)) return;
            Uebernehmen(d, sw, fo, vl, sammler.Sehnen, sammler.Innen, matPuffer);
        }

        static void Uebernehmen(RaumDaten d, double sw, double fo, double vl, int sehnen, int innen, double[] mat)
        {
            double sMat = sw * (1 - fo);
            for (int b = 0; b < Baender.Length; b++)
            {
                d.AlphaMat[b] = (float)mat[b];
                d.AlphaSehnen[b] = (float)SehnenKern.AlphaBoxAequivalent(d.Oberflaeche, d.SOffen, sMat, mat[b]);
            }
            d.SWirk = (float)sw; d.FOffen = (float)fo; d.VLuft = (float)vl; d.SehnenN = sehnen; d.SehnenInnen = innen;
            d.HatSehnen = true;
        }

        static void AllesFertig()
        {
            sehnenFertig = true;
            if (HallAn) Anstehend(0f);   // Zonen mit der neuen Absorption neu legen
            try { CacheSchreiben(); } catch (Exception ex) { Statistik.Fehler(ex); }
            if (Diagnose != null && Diagnose.Value) SehnenLoggen();
        }

        // ---------------------------------------------------------------- Cache
        // Zeile: pruefsumme(hex) TAB name TAB sehnen TAB innen TAB S_wirk TAB f_offen TAB V_luft TAB alpha_mat 125..4k (6)

        static void CacheLesen()
        {
            cacheZeilen = new Dictionary<ulong, string[]>();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            try
            {
                string f = CacheDatei;
                if (File.Exists(f))
                {
                    var zeilen = File.ReadAllLines(f);
                    if (zeilen.Length > 0 && zeilen[0].Trim() == "# room-absorption " + CacheVersion)
                        foreach (var z in zeilen.Skip(1))
                        {
                            var t = z.Split('\t');
                            if (t.Length < 13) continue;
                            if (ulong.TryParse(t[0], System.Globalization.NumberStyles.HexNumber, inv, out ulong k)) cacheZeilen[k] = t;
                        }
                }
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
            foreach (var d in daten)
            {
                d.Pruef = SehnenKern.Pruefsumme(d.Kaesten, d.Kaesten.Length, d.Name);
                if (!cacheZeilen.TryGetValue(d.Pruef, out var t)) continue;
                try
                {
                    var mat = new double[Baender.Length];
                    for (int b = 0; b < Baender.Length; b++) mat[b] = double.Parse(t[7 + b], inv);
                    Uebernehmen(d, double.Parse(t[4], inv), double.Parse(t[5], inv), double.Parse(t[6], inv), int.Parse(t[2], inv), int.Parse(t[3], inv), mat);
                    sehnenAusCache++;
                }
                catch (Exception) { }
            }
        }

        static void CacheSchreiben()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var alle = cacheZeilen ?? new Dictionary<ulong, string[]>();
            foreach (var d in daten)
            {
                if (!d.HatSehnen) continue;
                var t = new string[13];
                t[0] = d.Pruef.ToString("x16", inv); t[1] = d.Name.Replace('\t', ' '); t[2] = d.SehnenN.ToString(inv); t[3] = d.SehnenInnen.ToString(inv);
                t[4] = d.SWirk.ToString("R", inv); t[5] = d.FOffen.ToString("R", inv); t[6] = d.VLuft.ToString("R", inv);
                for (int b = 0; b < Baender.Length; b++) t[7 + b] = d.AlphaMat[b].ToString("R", inv);
                alle[d.Pruef] = t;
            }
            var sb = new StringBuilder();
            sb.Append("# room-absorption ").Append(CacheVersion).Append('\n');
            foreach (var t in alle.Values) sb.Append(string.Join("\t", t)).Append('\n');
            string text = sb.ToString(), datei = CacheDatei;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(datei)); File.WriteAllText(datei + ".tmp", text); if (File.Exists(datei)) File.Delete(datei); File.Move(datei + ".tmp", datei); }
                catch (Exception) { }
            });
        }

        // ---------------------------------------------------------------- Diagnose
        static void SehnenLoggen()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var gem = daten.Where(d => d.HatSehnen).ToList();
            if (gem.Count == 0) { Plugin.Log.LogInfo("[Reverb] Room absorption: no room measured by random chords."); return; }
            var verh = gem.Select(d => (double)d.SWirk / Math.Max(1, d.Oberflaeche)).OrderBy(x => x).ToList();
            double ms = sehnenTicks * 1000.0 / Stopwatch.Frequency;
            Plugin.Log.LogInfo(string.Format(inv, "[Reverb] Room absorption: {0} of {1} rooms measured by random chords ({2} from cache), {3} rays in {4} frames, " +
                "{5:F0} ms frame time in total (max {6:F2} ms per frame, budget {7:F1} ms), S_eff/S median {8:F2} (min {9:F2}, max {10:F2}); porous floor hits {11}, " +
                "{12} of them hard by the game's step surface.", gem.Count, daten.Count, sehnenAusCache, sehnenStrahlen, sehnenBilder, ms, sehnenMaxMs, BudgetMs,
                verh[verh.Count / 2], verh[0], verh[verh.Count - 1], sehnenPoroes, sehnenHart));
            // die 10 groessten Raeume: T60 alt/neu je Band (125/1k/4k), Hallzonen-Zuschlag alt/neu bei 1 kHz
            var dRt = new float[Baender.Length]; var dP = new float[Baender.Length];
            Plugin.Log.LogInfo("[Reverb] Room absorption, largest rooms (name: V m3, S_eff/S, open share, wall alpha old/new 125/1k/4k, T60 old/new 125/1k/4k s, zone dRT60 1k old/new s): " +
                string.Join(" | ", gem.OrderByDescending(d => d.Volumen).Take(10).Select(d =>
                {
                    double T(float a, int b) => RaumGeometrieKern.T60(d.Volumen, d.Oberflaeche, d.SOffen, a, LuftM[b]);
                    bool alt = d.HatSehnen; d.HatSehnen = false; Korrektur(d, 1f, dRt, dP); float rAlt = dRt[Band1k]; d.HatSehnen = alt;
                    Korrektur(d, 1f, dRt, dP); float rNeu = dRt[Band1k];
                    return string.Format(inv, "{0}: {1:F0}, {2:F2}, {3:F2}, {4:F3}/{5:F3}/{6:F3} -> {7:F3}/{8:F3}/{9:F3}, {10:F2}/{11:F2}/{12:F2} -> {13:F2}/{14:F2}/{15:F2}, {16:+0.00;-0.00} -> {17:+0.00;-0.00}",
                        d.Name, d.Volumen, d.SWirk / Math.Max(1, d.Oberflaeche), d.FOffen, d.Alpha[0], d.Alpha[Band1k], d.Alpha[5], d.AlphaSehnen[0], d.AlphaSehnen[Band1k], d.AlphaSehnen[5],
                        T(d.Alpha[0], 0), T(d.Alpha[Band1k], Band1k), T(d.Alpha[5], 5), T(d.AlphaSehnen[0], 0), T(d.AlphaSehnen[Band1k], Band1k), T(d.AlphaSehnen[5], 5), rAlt, rNeu);
                })));
        }
    }
}
