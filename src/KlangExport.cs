using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BepInEx.Configuration;
using UnityEngine;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    //  "Save heard sounds for the demo" (Abschnitt 11, 10.11.2026, Standard AUS): jeder im Raid gehoerte Clip wird EINMAL als
    //  WAV nach BepInEx/Materialklang-Logs/sounds/<clip>.wav geschrieben, dazu index.json (Format des Raid-Abspielers der Demo).
    //  So kann jeder Spieler seinen Raid mit den Original-Klaengen SEINER Spielkopie in der Demo abspielen; die Demo liest den
    //  Ordner nur lokal im Browser. Keine Klaenge von BSG in der Demo selbst.
    //   - nur wenn das Klang-Log an ist (gleiche Stelle wie die Klangzeile: Radius, Rate-Grenze)
    //   - AudioClip.GetData nach dem Laden; noch nicht geladene Clips (Schritte: preloadAudioData aus) beim naechsten Abspielen
    //     erneut (wie die Kalibrierung); gestreamte Clips (Vorbis-Umgebungsschleifen) liefern mit GetData nichts -> uebersprungen
    //   - Grenzen: Anzahl, Gesamtgroesse, Laenge je Clip (laengere werden gekuerzt), ueber 30 s (Musik) nie
    //   - hoechstens ein Clip je Bild auf dem Hauptfaden (GetData); WAV-Kodierung und Schreiben im Faden "Materialklang-Export"
    // ------------------------------------------------------------------------------------------
    internal static class KlangExport
    {
        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<int> MaxClips, MaxMB;
        internal static ConfigEntry<float> MaxSek;
        internal static bool An;
        const float MaxGanz = 30f;

        static readonly HashSet<string> fertig = new HashSet<string>(StringComparer.Ordinal);
        static readonly HashSet<string> aus = new HashSet<string>(StringComparer.Ordinal);
        static readonly HashSet<string> dateien = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly List<ExportEintrag> eintraege = new List<ExportEintrag>();
        static long bytes;
        static string dir;
        static bool geladen;
        static int letztesBild = -1, nGestreamt, nGeschrieben, nFehler;

        struct Auftrag { public ExportEintrag E; public float[] D; public int Frames; }
        static readonly Queue<Auftrag> schlange = new Queue<Auftrag>();
        static readonly object sperre = new object();
        static Thread faden;
        static readonly AutoResetEvent wecken = new AutoResetEvent(false);
        static volatile bool indexFaellig, ende;

        internal static void Binden(ConfigFile c)
        {
            Aktiv = c.Bind(KlangLog.Sek, "Save heard sounds for the demo", false,
                "Needs the sound log. Saves every sound clip you hear in a raid once as a WAV file to BepInEx/Materialklang-Logs/sounds (plus index.json), so the raid player of the demo page can replay your raid with the original sounds of your own game copy. The demo reads the folder only in your browser, nothing is uploaded. The sounds belong to Battlestate Games - do not share the folder. Streamed clips (long ambient loops) cannot be read and are skipped; music is never saved. Off = no cost. Applies live.");
            MaxClips = c.Bind(KlangLog.Sek, "Saved sounds: max. number", 1500,
                new ConfigDescription("Stops saving new clips when the sounds folder holds this many.", new AcceptableValueRange<int>(50, 5000)));
            MaxMB = c.Bind(KlangLog.Sek, "Saved sounds: max. size (MB)", 300,
                new ConfigDescription("Stops saving new clips when the sounds folder reaches this size.", new AcceptableValueRange<int>(20, 2000)));
            MaxSek = c.Bind(KlangLog.Sek, "Saved sounds: max. length per clip (s)", 6f,
                new ConfigDescription("Longer clips are cut to this length (with a short fade). Clips over 30 s are never saved.", new AcceptableValueRange<float>(1f, 30f)));
            An = Aktiv.Value;
            Aktiv.SettingChanged += (_, __) => { An = Aktiv.Value; };
        }

        /// <summary>Hauptfaden, aus KlangLog.Aufnehmen (nur bei aktivem Klang-Log und innerhalb von Radius/Rate-Grenze).</summary>
        internal static void Merken(BetterSource quelle)
        {
            if (!An || quelle == null) return;
            try
            {
                var s = quelle.source1;
                var clip = s != null ? s.clip : null;
                if (clip == null) return;
                string name = clip.name;
                if (string.IsNullOrEmpty(name) || fertig.Contains(name) || aus.Contains(name)) return;
                if (clip.loadType == AudioClipLoadType.Streaming) { aus.Add(name); nGestreamt++; return; }
                if (clip.loadState != AudioDataLoadState.Loaded) return;            // beim naechsten Abspielen wieder
                if (Time.frameCount == letztesBild) return;                         // hoechstens ein GetData je Bild
                if (!Laden()) return;
                int ch = clip.channels, rate = clip.frequency;
                float laenge = rate > 0 ? clip.samples / (float)rate : 0;
                float l = KlangExportKern.Erlaubt(eintraege.Count + schlange.Count, bytes, MaxClips.Value, (long)MaxMB.Value * 1024 * 1024, laenge, MaxSek.Value, MaxGanz, ch, rate);
                if (l <= 0) { aus.Add(name); return; }
                letztesBild = Time.frameCount;
                int frames = Math.Min(clip.samples, (int)(l * rate));
                var d = new float[frames * ch];
                if (!clip.GetData(d, 0)) { aus.Add(name); nFehler++; return; }
                fertig.Add(name);
                string datei = KlangExportKern.Dateiname(name, dateien) + ".wav";
                dateien.Add(datei.Substring(0, datei.Length - 4));
                var e = new ExportEintrag { Name = name, Datei = datei, Laenge = frames / (float)rate, Kanaele = ch, Rate = rate, Gekuerzt = frames < clip.samples };
                bytes += 44 + (long)frames * ch * 2;
                lock (sperre) schlange.Enqueue(new Auftrag { E = e, D = d, Frames = frames });
                Starten(); wecken.Set();
            }
            catch (Exception ex) { nFehler++; Statistik.Fehler(ex); }
        }

        /// <summary>Bestehenden Ordner einmal einlesen (ueber mehrere Raids fortsetzen, nichts doppelt).</summary>
        static bool Laden()
        {
            if (geladen) return dir != null;
            geladen = true;
            try
            {
                dir = Path.Combine(Path.Combine(BepInEx.Paths.BepInExRootPath, "Materialklang-Logs"), KlangExportKern.Ordner);
                Directory.CreateDirectory(dir);
                string ip = Path.Combine(dir, "index.json");
                if (File.Exists(ip))
                    foreach (var e in KlangExportKern.IndexLesen(File.ReadAllText(ip)))
                    {
                        string p = Path.Combine(dir, e.Datei);
                        if (!File.Exists(p) || fertig.Contains(e.Name)) continue;
                        fertig.Add(e.Name); dateien.Add(Path.GetFileNameWithoutExtension(e.Datei)); eintraege.Add(e); bytes += new FileInfo(p).Length;
                    }
                Plugin.Log.LogInfo("Saved sounds: " + dir + " (" + eintraege.Count + " clips, " + (bytes / 1048576.0).ToString("F1") + " MB already there)");
                return true;
            }
            catch (Exception ex) { dir = null; Statistik.Fehler(ex); return false; }
        }

        static void Starten()
        {
            if (faden != null) return;
            ende = false;
            faden = new Thread(Schleife) { IsBackground = true, Name = "Materialklang-Export", Priority = System.Threading.ThreadPriority.BelowNormal };
            faden.Start();
        }

        static void Schleife()
        {
            while (!ende)
            {
                wecken.WaitOne(2000);
                while (true)
                {
                    Auftrag a;
                    lock (sperre) { if (schlange.Count == 0) break; a = schlange.Dequeue(); }
                    try
                    {
                        var wav = KlangExportKern.Wav(a.D, a.E.Kanaele, a.Frames, a.E.Rate, a.E.Gekuerzt);
                        string p = Path.Combine(dir, a.E.Datei), tmp = p + ".tmp";
                        File.WriteAllBytes(tmp, wav);
                        if (File.Exists(p)) File.Delete(p);
                        File.Move(tmp, p);
                        lock (sperre) eintraege.Add(a.E);
                        nGeschrieben++; indexFaellig = true;
                    }
                    catch (Exception) { nFehler++; }
                }
                if (indexFaellig) IndexSchreiben();
            }
            if (indexFaellig) IndexSchreiben();
        }

        static void IndexSchreiben()
        {
            try
            {
                string text;
                lock (sperre) text = KlangExportKern.IndexJson(eintraege, DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                string ip = Path.Combine(dir, "index.json"), tmp = ip + ".tmp";
                File.WriteAllText(tmp, text);
                if (File.Exists(ip)) File.Delete(ip);
                File.Move(tmp, ip);
                indexFaellig = false;
            }
            catch (Exception) { nFehler++; }
        }

        /// <summary>Raid-Ende: Zwischenstand melden (der Faden schreibt index.json, sobald die Schlange leer ist).</summary>
        internal static void RaidEnde()
        {
            if (!geladen || dir == null) return;
            wecken.Set();
            Plugin.Log.LogInfo("Saved sounds: " + eintraege.Count + " clips in " + dir + " (" + nGeschrieben + " new this session, " + nGestreamt + " streamed skipped, " + nFehler + " errors)");
        }

        internal static void Beenden()
        {
            ende = true; wecken.Set();
            try { faden?.Join(3000); } catch (Exception) { }
            faden = null;
        }
    }
}
