// ---------------------------------------------------------------
//  Kern von "Save heard sounds for the demo" (10.11.2026): reine Logik ohne Unity/BepInEx, damit der Offline-Test
//  (Materialklang-KlangTest Abschnitt 42) sie pruefen kann.
//   - Dateiname     : Clipname -> sicherer Dateiname (nur A-Z a-z 0-9 _ . -, hoechstens 100 Zeichen, Kollision -> ~2, ~3 ...)
//   - Wav           : float-Abtastwerte (verschachtelt) -> 16-Bit-PCM-WAV (RIFF), mit kurzem Ausblenden am Schnitt
//   - IndexJson     : Liste -> index.json im Format des Raid-Abspielers der Demo {"clips":{name:{file,len,ch,sr}}}
//   - IndexLesen    : index.json -> Eintraege (zum Fortsetzen ueber mehrere Raids)
//   - Grenzen       : ob ein Clip noch gespeichert werden darf (Anzahl, Groesse, Laenge)
//  Rechtlich: die Klaenge gehoeren BSG; sie landen nur im eigenen BepInEx-Ordner des Spielers (die Demo liest sie lokal).
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Materialklang
{
    internal sealed class ExportEintrag
    {
        public string Name, Datei;
        public float Laenge;   // gespeicherte Laenge (s)
        public int Kanaele, Rate;
        public bool Gekuerzt;
    }

    internal static class KlangExportKern
    {
        internal const string Ordner = "sounds";
        internal const string Hinweis = "Saved by True Acoustics from your own copy of Escape from Tarkov (sounds (c) Battlestate Games). For the raid player of the demo page only: load this folder there, it is read in your browser and never uploaded. Do not share these files.";

        /// <summary>Sicherer Dateiname (ohne Endung) fuer einen Clipnamen; vergeben = schon benutzte Namen (Gross/klein egal).</summary>
        internal static string Dateiname(string clip, ICollection<string> vergeben)
        {
            var sb = new StringBuilder(Math.Min(100, clip?.Length ?? 0));
            if (!string.IsNullOrEmpty(clip))
                foreach (char ch in clip)
                {
                    if (sb.Length >= 100) break;
                    sb.Append((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '-' || ch == '.' ? ch : '_');
                }
            string b = sb.ToString().Trim('.');
            if (b.Length == 0) b = "clip";
            string n = b; int k = 1;
            while (vergeben != null && Enthaelt(vergeben, n)) n = b + "~" + (++k);
            return n;
        }

        static bool Enthaelt(ICollection<string> c, string n)
        {
            foreach (var x in c) if (string.Equals(x, n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>16-Bit-PCM-WAV aus verschachtelten float-Abtastwerten (frames x kanaele). gekuerzt = am Ende 20 ms ausblenden.</summary>
        internal static byte[] Wav(float[] d, int kanaele, int frames, int rate, bool gekuerzt)
        {
            if (kanaele < 1 || rate < 1 || frames < 0 || d == null || d.Length < frames * kanaele) throw new ArgumentException("WAV: Daten passen nicht");
            int datenBytes = frames * kanaele * 2;
            var ms = new MemoryStream(44 + datenBytes);
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + datenBytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)kanaele); w.Write(rate);
            w.Write(rate * kanaele * 2); w.Write((short)(kanaele * 2)); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(datenBytes);
            int aus = gekuerzt ? Math.Min(frames, rate / 50) : 0;
            for (int i = 0; i < frames; i++)
            {
                float g = aus > 0 && i >= frames - aus ? (frames - 1 - i) / (float)aus : 1f;
                for (int c = 0; c < kanaele; c++)
                {
                    float x = d[i * kanaele + c] * g;
                    if (float.IsNaN(x)) x = 0;
                    if (x > 1f) x = 1f; else if (x < -1f) x = -1f;
                    w.Write((short)Math.Round(x * 32767f));
                }
            }
            w.Flush();
            return ms.ToArray();
        }

        static string Esc(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            foreach (char ch in s)
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        internal static string IndexJson(IEnumerable<ExportEintrag> liste, string erzeugt)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(4096);
            sb.Append("{\"hinweis\":\"").Append(Esc(Hinweis)).Append("\",\"erzeugt\":\"").Append(Esc(erzeugt ?? "")).Append("\",\"quelle\":\"True Acoustics\",\"clips\":{");
            bool erst = true;
            foreach (var e in liste)
            {
                if (!erst) sb.Append(','); erst = false;
                sb.Append("\n\"").Append(Esc(e.Name)).Append("\":{\"file\":\"").Append(Esc(e.Datei)).Append("\",\"len\":").Append(e.Laenge.ToString("0.###", inv))
                  .Append(",\"ch\":").Append(e.Kanaele).Append(",\"sr\":").Append(e.Rate);
                if (e.Gekuerzt) sb.Append(",\"cut\":true");
                sb.Append('}');
            }
            sb.Append("\n}}\n");
            return sb.ToString();
        }

        /// <summary>Liest eine von IndexJson geschriebene Datei (nur dieses Format: je Clip eine Zeile). Unbekanntes wird uebersprungen.</summary>
        internal static List<ExportEintrag> IndexLesen(string text)
        {
            var r = new List<ExportEintrag>();
            if (string.IsNullOrEmpty(text)) return r;
            var inv = CultureInfo.InvariantCulture;
            foreach (var roh in text.Split('\n'))
            {
                string z = roh.Trim().TrimEnd(',');
                if (!z.StartsWith("\"") || !z.Contains("\"file\":\"")) continue;
                try
                {
                    int e1 = EndeString(z, 1); if (e1 < 0) continue;
                    string name = Unesc(z.Substring(1, e1 - 1));
                    int fp = z.IndexOf("\"file\":\"", e1, StringComparison.Ordinal) + 8; int fe = EndeString(z, fp); if (fe < 0) continue;
                    var e = new ExportEintrag { Name = name, Datei = Unesc(z.Substring(fp, fe - fp)) };
                    e.Laenge = (float)Zahl(z, "\"len\":", inv); e.Kanaele = (int)Zahl(z, "\"ch\":", inv); e.Rate = (int)Zahl(z, "\"sr\":", inv); e.Gekuerzt = z.Contains("\"cut\":true");
                    r.Add(e);
                }
                catch (Exception) { }
            }
            return r;
        }

        static int EndeString(string z, int ab)
        {
            for (int i = ab; i < z.Length; i++) { if (z[i] == '\\') { i++; continue; } if (z[i] == '"') return i; }
            return -1;
        }

        static string Unesc(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    if (n == 'u' && i + 4 < s.Length) { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; }
                    else sb.Append(n);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        static double Zahl(string z, string schluessel, CultureInfo inv)
        {
            int p = z.IndexOf(schluessel, StringComparison.Ordinal); if (p < 0) return 0;
            p += schluessel.Length; int e = p;
            while (e < z.Length && (char.IsDigit(z[e]) || z[e] == '.' || z[e] == '-')) e++;
            return double.TryParse(z.Substring(p, e - p), NumberStyles.Float, inv, out double v) ? v : 0;
        }

        /// <summary>Darf ein weiterer Clip gespeichert werden? laenge = volle Clip-Laenge (s). Musik/sehr lange Clips (ueber maxGanz s)
        /// werden nie gespeichert; sonst wird auf maxSek gekuerzt. Gibt die zu speichernde Laenge zurueck oder 0 = nein.</summary>
        internal static float Erlaubt(int anzahl, long bytes, int maxAnzahl, long maxBytes, float laenge, float maxSek, float maxGanz, int kanaele, int rate)
        {
            if (anzahl >= maxAnzahl || laenge <= 0 || kanaele < 1 || rate < 1000) return 0;
            if (laenge > maxGanz) return 0;
            float l = Math.Min(laenge, maxSek);
            long b = 44 + (long)(l * rate) * kanaele * 2;
            if (bytes + b > maxBytes) return 0;
            return l;
        }
    }
}
