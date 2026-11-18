using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Materialklang.Schallweg
{
    // ------------------------------------------------------------------------------------------
    //  0.8.6 Wegsuche (Etappe 3): Cache-Datei eines Wegenetzes je Karte bzw. Kachel.
    //  Name: BepInEx/cache/TrueAcoustics/<karte>-<pruefsumme:x16>.wegenetz
    //  Inhalt: alles, was die Wegsuche im Spiel braucht (Knoten, Nachbarlisten, BVH der Schall-Dreiecke, Koerper, Portale), als
    //  reine Felder. Die Datei wird aus den Spieldateien des Rechners gebaut, auf dem sie liegt (Etappe-2-Leser bzw. spaeter der
    //  Leser im Plugin) - nichts davon gehoert in ein Mod-Paket.
    //  Pruefsumme: FNV-1a 64 ueber die Leser-Version und je gelesener Spieldatei Name, Groesse und Zeitstempel (ganze Sekunden UTC).
    //  Die Liste der Dateien steht im Kopf; das Plugin rechnet sie gegen seine eigenen Spieldateien nach (Spiel-Update = andere
    //  Groesse/Zeit = Datei wird nicht benutzt). Gleiche Datei unabhaengig davon, wer sie baut (Werkzeug oder Plugin).
    //  Diese Datei hat keine Unity- und keine anderen Abhaengigkeiten: das Etappe-2-Werkzeug (net9) uebersetzt sie mit.
    // ------------------------------------------------------------------------------------------
    internal sealed class WegDaten
    {
        public const uint Magie = 0x4E574154u;   // "TAWN"
        public const int FormatVersion = 2;   // 2 (0.8.6 Livetest-Fix): + Raeume (SpatialAudioRoom-Kaesten) und alle Spielportale mit ihren Raeumen
        public int Version = FormatVersion;    // Version der gelesenen Datei (1 = ohne Raeume)

        // Kopf
        public string Karte = "", Kachel = "", LeserVersion = "", Erstellt = "";
        public ulong Pruefsumme;
        public List<SpielDatei> Dateien = new List<SpielDatei>();
        public double[] Kern = new double[0];          // x0, z0, x1, z1 des Kachelkerns (leer = ganze Karte)

        // Knoten (N): je 3 Werte x, y, z
        public int N;
        public double[] KP, KA, KB, KU, KW, KN1, KN2, KAus, KT, KInnen;
        public int[] KMutter, KPortal, KGruppe;
        public byte[] KFlags;                           // 1 = Platte, 2 = Portal
        // Netz
        public bool Straff; public double R, Tol, RGelaende;
        public byte[] Daten; public int[] Offs;         // Nachbarlisten kompakt (Varint, beide Richtungen, Bit 0 = kreuzt Tuer/Oeffnung)
        public ulong[] Ok; public double[] E1, E2;      // Sonde: biegefaehige Faecher je Knoten (null = ohne Sonde)
        public bool[] Gel;                              // Gelaendeknoten (null = keine)
        // BVH der Schall-Dreiecke (umsortiert): 9 Werte je Platz, Koerper je Platz, Knoten
        public float[] TriP; public int[] TriK;
        public float[] BMin, BMax; public int[] BLinks, BAnz;
        // Koerper
        public byte[] KoGruppe, KoTerrain; public float[] KoDicke;
        // Portale (PN)
        public int PN;
        public double[] PMitte, PAx, PAy, PAz;
        public string[] PSzene, PTuerId, PTuerKlasse;
        public short[] PId; public byte[] PTyp, PZustand; public bool[] PSchliessbar, PZu;
        public int[] PGruppe; public double[] PDicke;
        // Raeume (Format 2; Format 1: RN = 0). Wie das Spiel sie kennt (SpatialAudioRoom, Kaesten = BoxCollider der AudioTriggerAreas):
        // Oberflaeche und Volumen als Summe der Kaesten (wie RaumwegNetz.Messen der Mod), draussen-Flag.
        public int RN; public string[] RName = new string[0]; public bool[] RDraussen = new bool[0]; public double[] RFlaeche = new double[0], RVol = new double[0];
        public int BxN; public int[] BxRaum = new int[0]; public double[] BxM = new double[0], BxX = new double[0], BxY = new double[0], BxZ = new double[0];   // Mitte + Halbachsen (Welt)
        // Raumportale: ALLE Portale des Spiels (auch die grossen ohne Netzknoten), Raeume vorn/hinten (-1 = keiner), Netzportal (-1 = keins)
        public int QN; public double[] QMitte = new double[0], QAx = new double[0], QAy = new double[0], QAz = new double[0];
        public string[] QSzene = new string[0]; public int[] QId = new int[0], QRaumA = new int[0], QRaumB = new int[0], QNetz = new int[0];
        public bool HatRaeume => RN > 0 && QN > 0;

        public struct SpielDatei { public string Name; public long Groesse, ZeitS; }

        public long Bytes
        {
            get
            {
                long b = 0;
                b += (long)N * (10 * 24 + 8 + 12 + 1);
                b += (Daten?.LongLength ?? 0) + (Offs?.LongLength ?? 0) * 4 + (Ok?.LongLength ?? 0) * 8 + (E1?.LongLength ?? 0) * 16;
                b += (TriP?.LongLength ?? 0) * 4 + (TriK?.LongLength ?? 0) * 4 + (BMin?.LongLength ?? 0) * 8 + (BLinks?.LongLength ?? 0) * 8;
                b += (KoDicke?.LongLength ?? 0) * 6;
                return b;
            }
        }

        // ------------------------------------------------------------------ Pruefsumme

        public static ulong Summe(string leser, IList<SpielDatei> dateien, int version = FormatVersion)
        {
            ulong h = 14695981039346656037ul;
            void Dazu(string s) { foreach (byte b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 1099511628211ul; } }
            Dazu("TAWN" + version + "|" + leser + "|");
            foreach (var d in dateien) Dazu(d.Name + ":" + d.Groesse + ":" + d.ZeitS + "|");
            return h;
        }

        static readonly DateTime Epoche = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        /// <summary>Groesse und Zeitstempel (ganze Sekunden UTC) einer Spieldatei; false = fehlt.</summary>
        public static bool Stand(string datenOrdner, string name, out SpielDatei d)
        {
            d = new SpielDatei { Name = name };
            try
            {
                var fi = new FileInfo(Path.Combine(datenOrdner, name));
                if (!fi.Exists) return false;
                d.Groesse = fi.Length;
                d.ZeitS = (long)Math.Floor((fi.LastWriteTimeUtc - Epoche).TotalSeconds);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Pruefsumme der Dateien aus dem Kopf gegen den eigenen Datenordner nachrechnen. grund = was nicht passt.</summary>
        public bool Passt(string datenOrdner, out string grund)
        {
            grund = null;
            var jetzt = new List<SpielDatei>(Dateien.Count);
            foreach (var d in Dateien)
            {
                if (!Stand(datenOrdner, d.Name, out var s)) { grund = "game file missing: " + d.Name; return false; }
                if (s.Groesse != d.Groesse || s.ZeitS != d.ZeitS) { grund = $"game file changed: {d.Name} (size {s.Groesse} vs {d.Groesse}, time {s.ZeitS} vs {d.ZeitS})"; return false; }
                jetzt.Add(s);
            }
            ulong p = Summe(LeserVersion, jetzt, Version);
            if (p != Pruefsumme) { grund = $"checksum {p:x16} vs {Pruefsumme:x16}"; return false; }
            return true;
        }

        public static string Dateiname(string karte, string kachel, ulong pruef) => karte + (string.IsNullOrEmpty(kachel) ? "" : "-" + kachel) + "-" + pruef.ToString("x16") + ".wegenetz";

        // ------------------------------------------------------------------ Schreiben

        public long Schreiben(string pfad)
        {
            string tmp = pfad + ".tmp";
            using (var f = new BinaryWriter(new BufferedStream(File.Create(tmp), 1 << 20)))
            {
                f.Write(Magie); f.Write(FormatVersion); Version = FormatVersion;
                KopfSchreiben(f);
                f.Write(N);
                foreach (var a in new[] { KP, KA, KB, KU, KW, KN1, KN2, KAus, KT, KInnen }) Feld(f, a);
                Feld(f, KMutter); Feld(f, KPortal); Feld(f, KGruppe); Feld(f, KFlags);
                f.Write(Straff); f.Write(R); f.Write(Tol); f.Write(RGelaende);
                Feld(f, Daten); Feld(f, Offs);
                f.Write(Ok != null); if (Ok != null) { Feld(f, Ok); Feld(f, E1); Feld(f, E2); }
                f.Write(Gel != null); if (Gel != null) Feld(f, Gel);
                Feld(f, TriP); Feld(f, TriK); Feld(f, BMin); Feld(f, BMax); Feld(f, BLinks); Feld(f, BAnz);
                Feld(f, KoGruppe); Feld(f, KoTerrain); Feld(f, KoDicke);
                f.Write(PN);
                Feld(f, PMitte); Feld(f, PAx); Feld(f, PAy); Feld(f, PAz);
                for (int i = 0; i < PN; i++) { f.Write(PSzene[i] ?? ""); f.Write(PTuerId[i] ?? ""); f.Write(PTuerKlasse[i] ?? ""); }
                var ids = new int[PN]; for (int i = 0; i < PN; i++) ids[i] = PId[i]; Feld(f, ids);
                Feld(f, PTyp); Feld(f, PZustand); Feld(f, PSchliessbar); Feld(f, PZu); Feld(f, PGruppe); Feld(f, PDicke);
                // Format 2: Raeume, Kaesten, Raumportale
                f.Write(RN); for (int i = 0; i < RN; i++) f.Write(RName[i] ?? ""); Feld(f, RDraussen); Feld(f, RFlaeche); Feld(f, RVol);
                f.Write(BxN); Feld(f, BxRaum); Feld(f, BxM); Feld(f, BxX); Feld(f, BxY); Feld(f, BxZ);
                f.Write(QN); Feld(f, QMitte); Feld(f, QAx); Feld(f, QAy); Feld(f, QAz);
                for (int i = 0; i < QN; i++) f.Write(QSzene[i] ?? "");
                Feld(f, QId); Feld(f, QRaumA); Feld(f, QRaumB); Feld(f, QNetz);
                f.Write(0x454E4445u);   // "EDNE" Ende
                f.Flush();
            }
            if (File.Exists(pfad)) File.Delete(pfad);
            File.Move(tmp, pfad);
            return new FileInfo(pfad).Length;
        }

        void KopfSchreiben(BinaryWriter f)
        {
            f.Write(Karte ?? ""); f.Write(Kachel ?? ""); f.Write(LeserVersion ?? ""); f.Write(Erstellt ?? ""); f.Write(Pruefsumme);
            f.Write(Dateien.Count); foreach (var d in Dateien) { f.Write(d.Name); f.Write(d.Groesse); f.Write(d.ZeitS); }
            Feld(f, Kern);
        }

        // ------------------------------------------------------------------ Lesen

        /// <summary>Nur den Kopf lesen (Karte, Pruefsumme, Dateiliste). null = keine Wegenetz-Datei dieses Formats.</summary>
        public static WegDaten Kopf(string pfad)
        {
            try
            {
                using (var f = new BinaryReader(File.OpenRead(pfad)))
                {
                    if (f.ReadUInt32() != Magie) return null;
                    int v = f.ReadInt32(); if (v < 1 || v > FormatVersion) return null;
                    var d = new WegDaten { Version = v }; d.KopfLesen(f); return d;
                }
            }
            catch { return null; }
        }

        void KopfLesen(BinaryReader f)
        {
            Karte = f.ReadString(); Kachel = f.ReadString(); LeserVersion = f.ReadString(); Erstellt = f.ReadString(); Pruefsumme = f.ReadUInt64();
            int n = f.ReadInt32(); Dateien = new List<SpielDatei>(n);
            for (int i = 0; i < n; i++) Dateien.Add(new SpielDatei { Name = f.ReadString(), Groesse = f.ReadInt64(), ZeitS = f.ReadInt64() });
            Kern = LiesD(f);
        }

        public static WegDaten Lesen(string pfad)
        {
            using (var f = new BinaryReader(new BufferedStream(File.OpenRead(pfad), 1 << 20)))
            {
                if (f.ReadUInt32() != Magie) throw new InvalidDataException("not a path network file");
                int v = f.ReadInt32(); if (v < 1 || v > FormatVersion) throw new InvalidDataException("other format version " + v);
                var d = new WegDaten { Version = v }; d.KopfLesen(f);
                d.N = f.ReadInt32();
                d.KP = LiesD(f); d.KA = LiesD(f); d.KB = LiesD(f); d.KU = LiesD(f); d.KW = LiesD(f); d.KN1 = LiesD(f); d.KN2 = LiesD(f); d.KAus = LiesD(f); d.KT = LiesD(f); d.KInnen = LiesD(f);
                d.KMutter = LiesI(f); d.KPortal = LiesI(f); d.KGruppe = LiesI(f); d.KFlags = LiesB(f);
                d.Straff = f.ReadBoolean(); d.R = f.ReadDouble(); d.Tol = f.ReadDouble(); d.RGelaende = f.ReadDouble();
                d.Daten = LiesB(f); d.Offs = LiesI(f);
                if (f.ReadBoolean()) { d.Ok = LiesU(f); d.E1 = LiesD(f); d.E2 = LiesD(f); }
                if (f.ReadBoolean()) d.Gel = LiesBool(f);
                d.TriP = LiesF(f); d.TriK = LiesI(f); d.BMin = LiesF(f); d.BMax = LiesF(f); d.BLinks = LiesI(f); d.BAnz = LiesI(f);
                d.KoGruppe = LiesB(f); d.KoTerrain = LiesB(f); d.KoDicke = LiesF(f);
                d.PN = f.ReadInt32();
                d.PMitte = LiesD(f); d.PAx = LiesD(f); d.PAy = LiesD(f); d.PAz = LiesD(f);
                d.PSzene = new string[d.PN]; d.PTuerId = new string[d.PN]; d.PTuerKlasse = new string[d.PN];
                for (int i = 0; i < d.PN; i++) { d.PSzene[i] = f.ReadString(); d.PTuerId[i] = f.ReadString(); d.PTuerKlasse[i] = f.ReadString(); }
                var ids = LiesI(f); d.PId = new short[d.PN]; for (int i = 0; i < d.PN; i++) d.PId[i] = (short)ids[i];
                d.PTyp = LiesB(f); d.PZustand = LiesB(f); d.PSchliessbar = LiesBool(f); d.PZu = LiesBool(f); d.PGruppe = LiesI(f); d.PDicke = LiesD(f);
                if (v >= 2)
                {
                    d.RN = f.ReadInt32(); d.RName = new string[d.RN]; for (int i = 0; i < d.RN; i++) d.RName[i] = f.ReadString();
                    d.RDraussen = LiesBool(f); d.RFlaeche = LiesD(f); d.RVol = LiesD(f);
                    d.BxN = f.ReadInt32(); d.BxRaum = LiesI(f); d.BxM = LiesD(f); d.BxX = LiesD(f); d.BxY = LiesD(f); d.BxZ = LiesD(f);
                    d.QN = f.ReadInt32(); d.QMitte = LiesD(f); d.QAx = LiesD(f); d.QAy = LiesD(f); d.QAz = LiesD(f);
                    d.QSzene = new string[d.QN]; for (int i = 0; i < d.QN; i++) d.QSzene[i] = f.ReadString();
                    d.QId = LiesI(f); d.QRaumA = LiesI(f); d.QRaumB = LiesI(f); d.QNetz = LiesI(f);
                    if (d.RDraussen.Length != d.RN || d.BxRaum.Length != d.BxN || d.QMitte.Length != 3 * d.QN || d.QNetz.Length != d.QN) throw new InvalidDataException("inconsistent room arrays");
                }
                if (f.ReadUInt32() != 0x454E4445u) throw new InvalidDataException("file end marker missing (truncated?)");
                if (d.Offs.Length != d.N + 1 || d.KP.Length != 3 * d.N || d.TriK.Length * 9 != d.TriP.Length || d.BLinks.Length != d.BAnz.Length)
                    throw new InvalidDataException("inconsistent array sizes");
                return d;
            }
        }

        // ------------------------------------------------------------------ Felder (Laenge + Rohdaten, in Bloecken)

        const int Block = 1 << 16;
        [ThreadStatic] static byte[] puffer;
        static byte[] Puffer => puffer ?? (puffer = new byte[Block * 8]);

        static void Roh(BinaryWriter f, Array a, int elem)
        {
            int n = a.Length; f.Write(n);
            var p = Puffer; long gesamt = (long)n * elem, pos = 0;
            while (pos < gesamt)
            {
                int k = (int)Math.Min(p.Length, gesamt - pos);
                Buffer.BlockCopy(a, (int)pos, p, 0, k);
                f.Write(p, 0, k); pos += k;
            }
        }
        static void RohLesen(BinaryReader f, Array a, int elem)
        {
            var p = Puffer; long gesamt = (long)a.Length * elem, pos = 0;
            while (pos < gesamt)
            {
                int k = (int)Math.Min(p.Length, gesamt - pos);
                int got = 0; while (got < k) { int r = f.Read(p, got, k - got); if (r <= 0) throw new EndOfStreamException(); got += r; }
                Buffer.BlockCopy(p, 0, a, (int)pos, k); pos += k;
            }
        }
        static void Feld(BinaryWriter f, double[] a) => Roh(f, a ?? new double[0], 8);
        static void Feld(BinaryWriter f, float[] a) => Roh(f, a ?? new float[0], 4);
        static void Feld(BinaryWriter f, int[] a) => Roh(f, a ?? new int[0], 4);
        static void Feld(BinaryWriter f, ulong[] a) => Roh(f, a ?? new ulong[0], 8);
        static void Feld(BinaryWriter f, byte[] a) => Roh(f, a ?? new byte[0], 1);
        static void Feld(BinaryWriter f, bool[] a) { a = a ?? new bool[0]; var b = new byte[a.Length]; for (int i = 0; i < a.Length; i++) b[i] = a[i] ? (byte)1 : (byte)0; Roh(f, b, 1); }
        static double[] LiesD(BinaryReader f) { var a = new double[f.ReadInt32()]; RohLesen(f, a, 8); return a; }
        static float[] LiesF(BinaryReader f) { var a = new float[f.ReadInt32()]; RohLesen(f, a, 4); return a; }
        static int[] LiesI(BinaryReader f) { var a = new int[f.ReadInt32()]; RohLesen(f, a, 4); return a; }
        static ulong[] LiesU(BinaryReader f) { var a = new ulong[f.ReadInt32()]; RohLesen(f, a, 8); return a; }
        static byte[] LiesB(BinaryReader f) { var a = new byte[f.ReadInt32()]; RohLesen(f, a, 1); return a; }
        static bool[] LiesBool(BinaryReader f) { var b = LiesB(f); var a = new bool[b.Length]; for (int i = 0; i < b.Length; i++) a[i] = b[i] != 0; return a; }
    }
}
