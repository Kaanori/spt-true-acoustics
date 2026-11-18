using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Materialklang.Schallweg
{
    // ------------------------------------------------------------------------------------------
    //  0.8.8 Wegsuche (Etappe 3): Datentypen der Wegsuche im Plugin. Portierung aus dem eigenen Entwicklungswerkzeug Wegsuche (Etappe 2, ausserhalb des Plugins)
    //  (Mathe.cs, Netz.cs, Wegenetz.cs, Bvh.cs, Suche.cs) nach net471 - gleiche Rechnung, nur Abfragen (der Bau bleibt im Werkzeug
    //  bzw. spaeter im Leser). Keine Unity-Typen: laeuft im Arbeitsfaden und im Offline-Test.
    // ------------------------------------------------------------------------------------------
    internal struct V
    {
        public double X, Y, Z;
        public V(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static V operator +(V a, V b) => new V(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V operator -(V a, V b) => new V(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V operator *(V a, double s) => new V(a.X * s, a.Y * s, a.Z * s);
        public static V operator *(double s, V a) => new V(a.X * s, a.Y * s, a.Z * s);
        public static V operator -(V a) => new V(-a.X, -a.Y, -a.Z);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static double Dot(V a, V b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static V Cross(V a, V b) => new V(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public double Len => Math.Sqrt(X * X + Y * Y + Z * Z);
        public V Norm() { double l = Len; return l > 1e-12 ? this * (1.0 / l) : new V(0, 0, 0); }
        public static double Abstand(V a, V b) => (a - b).Len;
        public override string ToString() => $"({X:0.00}, {Y:0.00}, {Z:0.00})";
        public static V Aus(double[] a, int i) => new V(a[3 * i], a[3 * i + 1], a[3 * i + 2]);
    }

    internal static class H
    {
        public static double Clamp(double x, double a, double b) => x < a ? a : x > b ? b : x;
        public static void Fill<T>(T[] a, T v) { for (int i = 0; i < a.Length; i++) a[i] = v; }
    }

    /// <summary>Knoten des Wegenetzes: Kantenstueck (Strecke) oder Portal (Rechteck). Wie Etappe2/Netz.cs.</summary>
    internal sealed class Knoten
    {
        public V P, A, B, U, W, N1, N2, Aus, T; public bool Platte, IstPortal; public double InnenGrad; public int Mutter = -1, Portal = -1, Gruppe;
    }

    /// <summary>Ein Schallweg Quelle -> Hoerer (Etappe2/Suche.cs Weg, nur die Felder der Hoererbaum-Suche).</summary>
    internal sealed class Weg
    {
        public List<int> Knoten = new List<int>();
        public List<V> Punkte = new List<V>();
        public bool Verfeinert, Durchgang; public bool[] DurchPortal = new bool[0];
        public double Laenge; public double[] Db = new double[3], Kanten = new double[3]; public double Maekawa;
        public double Azimut, Hoehe; public int Beugungen;
        public List<KeyValuePair<string, double>> Waende = new List<KeyValuePair<string, double>>();
        public double Roh; public double[] Rohr = new double[3], Knick = new double[3], Tuer = new double[3]; public int Tueren; public double GangM;
        public List<KeyValuePair<V, double>> GangP = new List<KeyValuePair<V, double>>(); public double[] OhneRohr = new double[3]; public bool RohrWeg;
        /// <summary>0.8.8: Summe der UTD-Kanten vor der Mehrfachbeugungs-Grenze (Log), dieselbe ohne den Rohr-Knick; Gedeckelt = Grenze griff.</summary>
        public double[] KantenEP = new double[3], KantenOhneKnick = new double[3]; public bool Gedeckelt;
        /// <summary>wegsuche-use: Kopie fuer einen Zwischenstand (Felder, die RohrTeilen aendert, eigen; Punkte/Knoten werden nach Bewerten nicht mehr geaendert).</summary>
        public Weg Kopie()
        {
            var w = (Weg)MemberwiseClone();
            w.Db = (double[])Db.Clone(); w.OhneRohr = (double[])OhneRohr.Clone(); w.RohrWeg = false;
            return w;
        }
        /// <summary>Kennung des Wegs fuer den RoutenMischer (Knotenfolge; Durchgang = 1).</summary>
        public ulong Id
        {
            get
            {
                if (Durchgang) return 1;
                ulong h = 1469598103934665603ul;
                foreach (int k in Knoten) { h ^= (uint)k; h *= 1099511628211ul; }
                return h | 2;
            }
        }
    }

    /// <summary>Materialgruppen: Kopie der Standardwerte (Physik.cs StartDichte/StartMassiv), wie Etappe2-Karten/Szene.cs Gruppen.</summary>
    internal static class Gruppen
    {
        public static readonly string[] Namen = { "Concrete/Stone", "Soil", "Gravel", "Metal thick", "Metal thin", "Wood thick", "Wood thin", "Glass", "Soft", "Grate/Fence", "Body", "Water", "Unknown", "Door leaf", "Snow" };
        public static readonly double[] Dichte = { 2300, 1700, 1600, 7850, 7850, 600, 500, 2500, 500, 0, 1000, 1000, 2000, 600, 300 };
        public static readonly double[] Massiv = { 1.0, 3, 3, 0.01, 0.003, 0.3, 0.05, 0.02, 0.1, 0, 0.5, 3, 0.5, 0.06, 3 };
    }

    // ------------------------------------------------------------------------------------------ Wegenetz (nur Abfragen)
    /// <summary>Weg A (Mono-Leistung): ein Netzknoten als flacher Block fuer die Kandidatensuche (nach Gitterzelle sortiert).
    /// flags: 1 Platte, 2 Portal, 4 Gelaende.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KnotenBlock { public double px, py, pz, n1x, n1y, n1z, n2x, n2y, n2z, tx, ty, tz; public int idx, flags; }

    internal sealed unsafe class Wegenetz
    {
        public Knoten[] K;
        public float[] X, Y, Z;
        public V[] P;                       // Weg A: Knotenorte als Feld (ohne Objektzugriff)
        public KnotenBlock[] Blk; public readonly Dictionary<long, long> BlkGitter = new Dictionary<long, long>();   // Zelle -> (Anfang << 32 | Anzahl)
        // Weg A: Kanten flach (CSR). CNb = Zielknoten | Bit 31 (kreuzt Tuer/Oeffnung), CW = Laenge + Tuerstrafe (vorab, je Tuerwechsel nur
        // die betroffenen Kanten). Tuerverzeichnis: je schliessbarem Portal die Portal-Kanten, deren Strafe von ihm abhaengt.
        public int[] COff, CNb; public float[] CW;
        public int[] PkKante; public float[] PkLaenge; public int[] PkTOff, PkT;   // je Portal-Kante: Kantenindex, reine Laenge, Tueren auf ihr
        public int[] PkVon;                                                       // wegsuche-use: Startknoten je Portal-Kante (Tuerwechsel: hat ein Baum sie erreicht?)
        public int[] TPkOff, TPk;                                                 // je Portal: seine Portal-Kanten
        public bool Straff;
        public ulong[] Ok; public V[] E1, E2;
        public bool[] Gel;
        public byte[] Daten; public int[] Offs;
        public double R = 20.0, Tol = 0.035, RGelaende = 60.0;
        public const double Zelle = 10.0;
        public readonly Dictionary<long, int[]> Gitter = new Dictionary<long, int[]>();
        public const int Faecher = 36;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Schluessel(int x, int y, int z) => ((long)(x + 1048576) << 42) | ((long)(y + 1048576) << 21) | (long)(z + 1048576);
        public static void Z3(V p, out int x, out int y, out int z) { x = (int)Math.Floor(p.X / Zelle); y = (int)Math.Floor(p.Y / Zelle); z = (int)Math.Floor(p.Z / Zelle); }

        public static Wegenetz Aus(WegDaten d)
        {
            var n = new Wegenetz { Straff = d.Straff, R = d.R, Tol = d.Tol, RGelaende = d.RGelaende, Daten = d.Daten, Offs = d.Offs, Gel = d.Gel, Ok = d.Ok };
            int c = d.N; n.K = new Knoten[c]; n.X = new float[c]; n.Y = new float[c]; n.Z = new float[c]; n.P = new V[c];
            for (int i = 0; i < c; i++)
            {
                var k = new Knoten
                {
                    P = V.Aus(d.KP, i), A = V.Aus(d.KA, i), B = V.Aus(d.KB, i), U = V.Aus(d.KU, i), W = V.Aus(d.KW, i), N1 = V.Aus(d.KN1, i), N2 = V.Aus(d.KN2, i),
                    Aus = V.Aus(d.KAus, i), T = V.Aus(d.KT, i), InnenGrad = d.KInnen[i], Mutter = d.KMutter[i], Portal = d.KPortal[i], Gruppe = d.KGruppe[i],
                    Platte = (d.KFlags[i] & 1) != 0, IstPortal = (d.KFlags[i] & 2) != 0,
                };
                n.K[i] = k; n.X[i] = (float)k.P.X; n.Y[i] = (float)k.P.Y; n.Z[i] = (float)k.P.Z; n.P[i] = k.P;
            }
            if (d.Ok != null) { n.E1 = new V[c]; n.E2 = new V[c]; for (int i = 0; i < c; i++) { n.E1[i] = V.Aus(d.E1, i); n.E2[i] = V.Aus(d.E2, i); } }
            var g = new Dictionary<long, List<int>>();
            for (int i = 0; i < c; i++) { Z3(n.K[i].P, out int x, out int y, out int z); long key = Schluessel(x, y, z); if (!g.TryGetValue(key, out var l)) g[key] = l = new List<int>(); l.Add(i); }
            foreach (var kv in g) n.Gitter[kv.Key] = kv.Value.ToArray();
            // Weg A: Kandidaten-Bloecke in Zellenreihenfolge (je Zelle dieselbe Knotenfolge wie Gitter)
            n.Blk = new KnotenBlock[c]; int pos = 0;
            foreach (var kv in n.Gitter)
            {
                n.BlkGitter[kv.Key] = ((long)pos << 32) | (uint)kv.Value.Length;
                foreach (int j in kv.Value)
                {
                    var k = n.K[j];
                    n.Blk[pos++] = new KnotenBlock { px = k.P.X, py = k.P.Y, pz = k.P.Z, n1x = k.N1.X, n1y = k.N1.Y, n1z = k.N1.Z, n2x = k.N2.X, n2y = k.N2.Y, n2z = k.N2.Z, tx = k.T.X, ty = k.T.Y, tz = k.T.Z, idx = j,
                                                     flags = (k.IstPortal ? 2 : 0) | (k.Platte ? 1 : 0) | (n.Gel != null && n.Gel[j] ? 4 : 0) };
                }
            }
            return n;
        }

        /// <summary>Weg A: Kanten flach anlegen (aus der Varint-Liste, die danach freigegeben wird) und je Portal-Kante die schliessbaren
        /// Portale auf ihr (PortalTest.Alle wie Suche.KantenStrafe). Einmal je Karte beim Laden.</summary>
        public void KantenBauen(PortalTest pt)
        {
            int c = K.Length; COff = new int[c + 1]; long e = 0;
            for (int u = 0; u < c; u++) { COff[u] = (int)e; foreach (uint cc in NachbarnVarint(u)) e++; }
            COff[c] = (int)e; CNb = new int[e]; CW = new float[e];
            var pkK = new List<int>(); var pkL = new List<float>(); var pkV = new List<int>(); var pkTOff = new List<int> { 0 }; var pkT = new List<int>(); var buf = new List<int>();
            int k2 = 0;
            for (int u = 0; u < c; u++)
                foreach (uint cc in NachbarnVarint(u))
                {
                    int v = Idx(cc); float len = Laenge(u, v);
                    CNb[k2] = v | (Portal(cc) ? int.MinValue : 0); CW[k2] = len;
                    if (Portal(cc))
                    {
                        pkK.Add(k2); pkL.Add(len); pkV.Add(u);
                        pt.Alle(P[u], P[v], buf, K[u].Portal, K[v].Portal); pkT.AddRange(buf); pkTOff.Add(pkT.Count);
                    }
                    k2++;
                }
            PkKante = pkK.ToArray(); PkLaenge = pkL.ToArray(); PkVon = pkV.ToArray(); PkTOff = pkTOff.ToArray(); PkT = pkT.ToArray();
            int np = 0; foreach (int t in PkT) np = Math.Max(np, t + 1);
            var zahl = new int[np + 1]; foreach (int t in PkT) zahl[t + 1]++;
            TPkOff = new int[np + 1]; for (int i = 0; i < np; i++) TPkOff[i + 1] = TPkOff[i] + zahl[i + 1];
            TPk = new int[PkT.Length]; var fuell = (int[])TPkOff.Clone();
            for (int i = 0; i < PkKante.Length; i++) for (int j = PkTOff[i]; j < PkTOff[i + 1]; j++) TPk[fuell[PkT[j]]++] = i;
            Daten = null; Offs = null;   // die Varint-Liste wird hier nicht mehr gebraucht (WegDaten haelt sie, bis Arbeiter.VarintFreigeben)
        }

        /// <summary>Weg A: Laenge der Portal-Kante i neu setzen: reine Laenge + strafe(Tueren dieser Kante) (Suche.KantenStrafe).</summary>
        public void KanteSetzen(int i, double strafe) { CW[PkKante[i]] = (float)(PkLaenge[i] + strafe); }

        public long Bytes => (Daten != null ? (long)Daten.Length + Offs.Length * 4L : 0) + (CNb != null ? CNb.LongLength * 8 + COff.LongLength * 4 + PkKante.LongLength * 8 + PkT.LongLength * 8 : 0)
                             + K.Length * (9 * 24L + 48 + 12 + 24 + 104) + (Ok != null ? K.Length * 56L : 0);

        public static bool Aussen(Knoten k, V d)
        {
            if (k.IstPortal || k.Platte) return true;
            return V.Dot(d, k.N1) > 0.02 || V.Dot(d, k.N2) > 0.02;
        }

        public int Seite(Knoten k, V d)
        {
            if (k.IstPortal) return 2;
            double dt = V.Dot(d, k.T); V dn = d - k.T * dt; double l2 = V.Dot(dn, dn), L2 = V.Dot(d, d);
            if (l2 < 1e-4 * L2 || l2 < 1e-10) return 2;
            double inv = 1 / Math.Sqrt(l2), a = V.Dot(dn, k.N1) * inv, b = V.Dot(dn, k.N2) * inv;
            double tol = Tol;
            if (a > tol && b > tol) return 3;
            if (a < -tol && b < -tol) return 4;
            if (a > tol && b < -tol) return 0;
            if (b > tol && a < -tol) return 1;
            return 2;
        }
        public static bool Erlaubt(int ankunft, int weiter) => ankunft == 2 || weiter == 2 || ankunft != weiter;

        int Fach(int i, V d)
        {
            V e1 = E1[i], e2 = E2[i]; double x = V.Dot(d, e1), y = V.Dot(d, e2);
            if (x * x + y * y < 1e-4 * V.Dot(d, d)) return -1;
            double w = Math.Atan2(y, x); if (w < 0) w += 2 * Math.PI;
            return Math.Min(Faecher - 1, (int)(w / (2 * Math.PI) * Faecher));
        }
        public bool SondeOk(int i, V d) { if (Ok == null) return false; if (K[i].IstPortal) return true; int f = Fach(i, d); return f < 0 || ((Ok[i] >> f) & 1) != 0; }

        public const uint PortalBit = 1u << 30, IndexMaske = (1u << 26) - 1;
        public static int Idx(uint e) => (int)(e & IndexMaske);
        public static bool Portal(uint e) => (e & PortalBit) != 0;

        /// <summary>Nachbarn von u (kompakte Varint-Liste wie Wegenetz.Kompaktieren): Index | PortalBit. Ohne Seiten (Hoererbaum braucht sie nicht).
        /// Weg A: nach KantenBauen aus der flachen Liste.</summary>
        public IEnumerable<uint> Nachbarn(int u)
        {
            if (CNb == null) { foreach (uint c in NachbarnVarint(u)) yield return c; yield break; }
            for (int k = COff[u]; k < COff[u + 1]; k++) { int v = CNb[k]; yield return (uint)(v & int.MaxValue) | (v < 0 ? PortalBit : 0u); }
        }
        public NachbarIter NachbarnVarint(int u) => new NachbarIter(Daten, Offs[u]);
        public struct NachbarIter
        {
            readonly byte[] d; int p, rest, j; uint cur; public uint Current => cur;
            public NachbarIter(byte[] daten, int off) { d = daten; p = off; j = 0; cur = 0; rest = (int)Lies(daten, ref p); }
            static uint Lies(byte[] d, ref int p) { uint v = 0; int s = 0; while (true) { byte b = d[p++]; v |= (uint)(b & 0x7f) << s; if (b < 0x80) return v; s += 7; } }
            public bool MoveNext()
            {
                if (rest-- <= 0) return false;
                uint v = Lies(d, ref p); j += (int)(v >> 1); cur = (uint)j | ((v & 1) << 30); return true;
            }
            public NachbarIter GetEnumerator() => this;
        }

        public float Laenge(int i, int j) { float dx = X[i] - X[j], dy = Y[i] - Y[j], dz = Z[i] - Z[j]; return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz); }
    }

    // ------------------------------------------------------------------------------------------ BVH (nur Abfragen, Etappe2/Bvh.cs)
    /// <summary>wegsuche-use: ein BVH-Knoten zusammenhaengend (32 Byte: Kiste + Kinder/Dreiecke) statt in vier getrennten Feldern.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BKnoten { public float x0, y0, z0, x1, y1, z1; public int li, an; }

    internal sealed unsafe class Bvh
    {
        public readonly float[] P; public readonly int[] TK;          // 9 Werte je Platz, Koerper je Platz
        float[] nMin, nMax; int[] nLinks, nAnz;   // alte Form, nur fuer den Gleichheitstest (aus kn nachgebaut)
        // wegsuche-use (Rechenzeit): Knoten als BKnoten (eine Kiste + Kinder in 32 Byte) statt in vier getrennten Feldern; Kisten- und
        // Dreieckstest in Frei/Erster ausgeschrieben (Mono bettet die Hilfsfunktionen nicht ein: Strahl 5,0 -> 3,7 us, Erster 8,6 -> 6,1 us).
        // Gleiche Rechnung und Reihenfolge wie bisher (auch Gleichstaende bei Erster); Min/Max als Vergleich mit NaN-Weitergabe wie
        // Math.Min/Max (Strahl parallel zu einer Kistenflaeche). Die alten Felder der Datei werden freigegeben (gleicher Speicher wie vorher);
        // mehrere Arbeiter auf denselben WegDaten (Offline-Test) teilen die Knoten.
        readonly BKnoten[] kn;
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WegDaten, BKnoten[]> knJeDatei = new System.Runtime.CompilerServices.ConditionalWeakTable<WegDaten, BKnoten[]>();

        public Bvh(WegDaten d)
        {
            P = d.TriP; TK = d.TriK;
            lock (knJeDatei)
            {
                if (!knJeDatei.TryGetValue(d, out kn))
                {
                    int nn = d.BLinks.Length; kn = new BKnoten[nn]; var mn = d.BMin; var mx = d.BMax;
                    for (int k = 0; k < nn; k++) kn[k] = new BKnoten { x0 = mn[3 * k], y0 = mn[3 * k + 1], z0 = mn[3 * k + 2], x1 = mx[3 * k], y1 = mx[3 * k + 1], z1 = mx[3 * k + 2], li = d.BLinks[k], an = d.BAnz[k] };
                    knJeDatei.Add(d, kn);
                    d.BMin = d.BMax = null; d.BLinks = d.BAnz = null;
                }
            }
        }
        public int Dreiecke => TK.Length;
        public long Bytes => (long)P.Length * 4 + TK.Length * 4L + (long)kn.Length * 32;

        [MethodImpl(MethodImplOptions.AggressiveInlining)] static float Mn(float a, float b) => (a < b || a != a) ? a : b;   // wie Math.Min (NaN gewinnt)
        [MethodImpl(MethodImplOptions.AggressiveInlining)] static float Mx(float a, float b) => (a > b || a != a) ? a : b;   // wie Math.Max

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool Kiste(BKnoten* k, float ox, float oy, float oz, float ix, float iy, float iz, float tmax)
        {
            float t0 = (k->x0 - ox) * ix, t1 = (k->x1 - ox) * ix;
            float tn = Mn(t0, t1), tf = Mx(t0, t1);
            t0 = (k->y0 - oy) * iy; t1 = (k->y1 - oy) * iy;
            tn = Mx(tn, Mn(t0, t1)); tf = Mn(tf, Mx(t0, t1));
            t0 = (k->z0 - oz) * iz; t1 = (k->z1 - oz) * iz;
            tn = Mx(tn, Mn(t0, t1)); tf = Mn(tf, Mx(t0, t1));
            return tf >= Mx(tn, 0f) && tn <= tmax;
        }

        /// <summary>Moeller-Trumbore, Rechnung wie TrefferZ.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Treffer(float* p, float ox, float oy, float oz, float dx, float dy, float dz, out bool vorne)
        {
            float e1x = p[3] - p[0], e1y = p[4] - p[1], e1z = p[5] - p[2], e2x = p[6] - p[0], e2y = p[7] - p[1], e2z = p[8] - p[2];
            float px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
            float det = e1x * px + e1y * py + e1z * pz;
            vorne = det > 0;
            if (Math.Abs(det) < 1e-12f) return -1;
            float inv = 1f / det;
            float tx = ox - p[0], ty = oy - p[1], tz = oz - p[2];
            float u = (tx * px + ty * py + tz * pz) * inv; if (u < 0 || u > 1) return -1;
            float qx = ty * e1z - tz * e1y, qy = tz * e1x - tx * e1z, qz = tx * e1y - ty * e1x;
            float v = (dx * qx + dy * qy + dz * qz) * inv; if (v < 0 || u + v > 1) return -1;
            return (e2x * qx + e2y * qy + e2z * qz) * inv;
        }

        /// <summary>Kiste wie Kiste(), dazu der Eintrittsparameter tn (fuer die Reihenfolge).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool KisteT(BKnoten* k, float ox, float oy, float oz, float ix, float iy, float iz, float tmax, out float tn)
        {
            float t0 = (k->x0 - ox) * ix, t1 = (k->x1 - ox) * ix;
            tn = Mn(t0, t1); float tf = Mx(t0, t1);
            t0 = (k->y0 - oy) * iy; t1 = (k->y1 - oy) * iy;
            tn = Mx(tn, Mn(t0, t1)); tf = Mn(tf, Mx(t0, t1));
            t0 = (k->z0 - oz) * iz; t1 = (k->z1 - oz) * iz;
            tn = Mx(tn, Mn(t0, t1)); tf = Mn(tf, Mx(t0, t1));
            return tf >= Mx(tn, 0f) && tn <= tmax;
        }

        /// <summary>Sichtstrahl a -> b frei? wegsuche-use: das naehere Kind zuerst (Kisten beim Ablegen geprueft, mit derselben Rechnung und
        /// demselben tmax wie bisher beim Abholen). Die Menge der besuchten Blaetter ist dieselbe, nur die Reihenfolge anders - die Antwort
        /// (irgendein Treffer zwischen tmin und tmax?) haengt davon nicht ab; ein verdeckter Strahl endet aber meist viel frueher.</summary>
        public bool Frei(V a, V b, float eps = 0.02f)
        {
            V d = b - a; double len = d.Len; if (len <= 2 * eps) return true;
            float dx = (float)(d.X / len), dy = (float)(d.Y / len), dz = (float)(d.Z / len);
            float ox = (float)a.X, oy = (float)a.Y, oz = (float)a.Z, tmin = eps, tmax = (float)len - eps;
            float ix = 1f / dx, iy = 1f / dy, iz = 1f / dz;
            int* st = stackalloc int[128]; int sp = 0; st[sp++] = 0;
            // Mono (Spiel) bettet die Hilfsfunktionen nicht ein: Kistentest und Dreieckstest hier ausgeschrieben (Rechnung wie Kiste/Treffer)
            fixed (BKnoten* K = kn) fixed (float* T = P)
                while (sp > 0)
                {
                    BKnoten* k = K + st[--sp];
                    float t0 = (k->x0 - ox) * ix, t1 = (k->x1 - ox) * ix, tn, tf;
                    if (t0 < t1 || t0 != t0) { tn = t0; tf = (t1 > t0 || t1 != t1) ? t1 : t0; } else { tn = t1; tf = t0 > t1 ? t0 : t1; }   // Mn/Mx mit NaN-Weitergabe
                    float u0 = (k->y0 - oy) * iy, u1 = (k->y1 - oy) * iy;
                    float lo = (u0 < u1 || u0 != u0) ? u0 : u1, hi = (u0 > u1 || u0 != u0) ? u0 : u1;
                    tn = (tn > lo || tn != tn) ? tn : lo; tf = (tf < hi || tf != tf) ? tf : hi;
                    u0 = (k->z0 - oz) * iz; u1 = (k->z1 - oz) * iz;
                    lo = (u0 < u1 || u0 != u0) ? u0 : u1; hi = (u0 > u1 || u0 != u0) ? u0 : u1;
                    tn = (tn > lo || tn != tn) ? tn : lo; tf = (tf < hi || tf != tf) ? tf : hi;
                    float tn0 = (tn > 0f || tn != tn) ? tn : 0f;
                    if (!(tf >= tn0 && tn <= tmax)) continue;
                    int anz = k->an;
                    if (anz > 0)
                    {
                        float* p = T + 9 * k->li;
                        for (int i = 0; i < anz; i++, p += 9)
                        {
                            float e1x = p[3] - p[0], e1y = p[4] - p[1], e1z = p[5] - p[2], e2x = p[6] - p[0], e2y = p[7] - p[1], e2z = p[8] - p[2];
                            float px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
                            float det = e1x * px + e1y * py + e1z * pz;
                            if (Math.Abs(det) < 1e-12f) continue;
                            float inv = 1f / det;
                            float tx = ox - p[0], ty = oy - p[1], tz = oz - p[2];
                            float uu = (tx * px + ty * py + tz * pz) * inv; if (uu < 0 || uu > 1) continue;
                            float qx = ty * e1z - tz * e1y, qy = tz * e1x - tx * e1z, qz = tx * e1y - ty * e1x;
                            float vv = (dx * qx + dy * qy + dz * qz) * inv; if (vv < 0 || uu + vv > 1) continue;
                            float t = (e2x * qx + e2y * qy + e2z * qz) * inv;
                            if (t > tmin && t < tmax) return false;
                        }
                    }
                    else { st[sp++] = k->li; st[sp++] = k->li + 1; }
                }
            return true;
        }

        public struct Hit { public float T; public int Platz; public bool Vorne; public Hit(float t, int p, bool v) { T = t; Platz = p; Vorne = v; } }

        public Hit Erster(V a, V dir, float tmax, float tmin = 0.001f)
        {
            float dx = (float)dir.X, dy = (float)dir.Y, dz = (float)dir.Z, ox = (float)a.X, oy = (float)a.Y, oz = (float)a.Z;
            float ix = 1f / dx, iy = 1f / dy, iz = 1f / dz; float best = tmax; int bp = -1; bool bv = false;
            int* st = stackalloc int[128]; int sp = 0; st[sp++] = 0;
            fixed (BKnoten* K = kn) fixed (float* T = P)
                while (sp > 0)
                {
                    BKnoten* k = K + st[--sp];
                    float t0 = (k->x0 - ox) * ix, t1 = (k->x1 - ox) * ix, tn, tf;
                    if (t0 < t1 || t0 != t0) { tn = t0; tf = (t1 > t0 || t1 != t1) ? t1 : t0; } else { tn = t1; tf = t0 > t1 ? t0 : t1; }   // Mn/Mx mit NaN-Weitergabe
                    float u0 = (k->y0 - oy) * iy, u1 = (k->y1 - oy) * iy;
                    float lo = (u0 < u1 || u0 != u0) ? u0 : u1, hi = (u0 > u1 || u0 != u0) ? u0 : u1;
                    tn = (tn > lo || tn != tn) ? tn : lo; tf = (tf < hi || tf != tf) ? tf : hi;
                    u0 = (k->z0 - oz) * iz; u1 = (k->z1 - oz) * iz;
                    lo = (u0 < u1 || u0 != u0) ? u0 : u1; hi = (u0 > u1 || u0 != u0) ? u0 : u1;
                    tn = (tn > lo || tn != tn) ? tn : lo; tf = (tf < hi || tf != tf) ? tf : hi;
                    float tn0 = (tn > 0f || tn != tn) ? tn : 0f;
                    if (!(tf >= tn0 && tn <= best)) continue;
                    int anz = k->an;
                    if (anz > 0)
                    {
                        int i0 = k->li; float* p = T + 9 * i0;
                        for (int i = 0; i < anz; i++, p += 9)
                        {
                            float e1x = p[3] - p[0], e1y = p[4] - p[1], e1z = p[5] - p[2], e2x = p[6] - p[0], e2y = p[7] - p[1], e2z = p[8] - p[2];
                            float px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
                            float det = e1x * px + e1y * py + e1z * pz;
                            if (Math.Abs(det) < 1e-12f) continue;
                            float inv = 1f / det;
                            float tx = ox - p[0], ty = oy - p[1], tz = oz - p[2];
                            float uu = (tx * px + ty * py + tz * pz) * inv; if (uu < 0 || uu > 1) continue;
                            float qx = ty * e1z - tz * e1y, qy = tz * e1x - tx * e1z, qz = tx * e1y - ty * e1x;
                            float vv = (dx * qx + dy * qy + dz * qz) * inv; if (vv < 0 || uu + vv > 1) continue;
                            float t = (e2x * qx + e2y * qy + e2z * qz) * inv;
                            if (t > tmin && t < best) { best = t; bp = i0 + i; bv = det > 0; }
                        }
                    }
                    else { st[sp++] = k->li; st[sp++] = k->li + 1; }
                }
            return bp < 0 ? new Hit(-1f, -1, false) : new Hit(best, bp, bv);
        }

        static readonly Comparison<Hit> NachT = (x, y) => x.T.CompareTo(y.T);
        public void Alle(V a, V b, List<Hit> aus)
        {
            aus.Clear();
            V d = b - a; double len = d.Len; if (len < 1e-6) return;
            float dx = (float)(d.X / len), dy = (float)(d.Y / len), dz = (float)(d.Z / len);
            float ox = (float)a.X, oy = (float)a.Y, oz = (float)a.Z, tmax = (float)len;
            float ix = 1f / dx, iy = 1f / dy, iz = 1f / dz;
            int* st = stackalloc int[128]; int sp = 0; st[sp++] = 0;
            fixed (BKnoten* K = kn) fixed (float* T = P)
                while (sp > 0)
                {
                    BKnoten* k = K + st[--sp];
                    if (!Kiste(k, ox, oy, oz, ix, iy, iz, tmax)) continue;
                    int anz = k->an;
                    if (anz > 0)
                    {
                        int i0 = k->li; float* p = T + 9 * i0;
                        for (int i = 0; i < anz; i++, p += 9) { float t = Treffer(p, ox, oy, oz, dx, dy, dz, out bool v); if (t > 0 && t < tmax) aus.Add(new Hit(t, i0 + i, v)); }
                    }
                    else { st[sp++] = k->li; st[sp++] = k->li + 1; }
                }
            aus.Sort(NachT);
        }

        // ---- Bezug (alte Rechnung, nur fuer den Gleichheitstest im Offline-Test) ----
        void BezugBauen()
        {
            if (nLinks != null) return;
            int nn = kn.Length; var a = new float[3 * nn]; var b = new float[3 * nn]; var l = new int[nn]; var z = new int[nn];
            for (int k = 0; k < nn; k++) { a[3 * k] = kn[k].x0; a[3 * k + 1] = kn[k].y0; a[3 * k + 2] = kn[k].z0; b[3 * k] = kn[k].x1; b[3 * k + 1] = kn[k].y1; b[3 * k + 2] = kn[k].z1; l[k] = kn[k].li; z[k] = kn[k].an; }
            nMin = a; nMax = b; nAnz = z; nLinks = l;
        }
        internal bool FreiBezug(V a, V b, float eps = 0.02f)
        {
            BezugBauen();
            V d = b - a; double len = d.Len; if (len <= 2 * eps) return true;
            float dx = (float)(d.X / len), dy = (float)(d.Y / len), dz = (float)(d.Z / len);
            float ox = (float)a.X, oy = (float)a.Y, oz = (float)a.Z, tmin = eps, tmax = (float)len - eps;
            float ix = 1f / dx, iy = 1f / dy, iz = 1f / dz;
            int* st = stackalloc int[128]; int sp = 0; st[sp++] = 0;
            fixed (float* mn = nMin, mx = nMax, Pp = P) fixed (int* li = nLinks, an = nAnz)
                while (sp > 0)
                {
                    int k = st[--sp];
                    if (!KisteZ(mn, mx, k, ox, oy, oz, ix, iy, iz, tmax)) continue;
                    int anz = an[k];
                    if (anz > 0)
                    {
                        float* p = Pp + 9 * li[k];
                        for (int i = 0; i < anz; i++, p += 9) { float t = TrefferZ(p, ox, oy, oz, dx, dy, dz, out _); if (t > tmin && t < tmax) return false; }
                    }
                    else { st[sp++] = li[k]; st[sp++] = li[k] + 1; }
                }
            return true;
        }
        internal Hit ErsterBezug(V a, V dir, float tmax, float tmin = 0.001f)
        {
            BezugBauen();
            float dx = (float)dir.X, dy = (float)dir.Y, dz = (float)dir.Z, ox = (float)a.X, oy = (float)a.Y, oz = (float)a.Z;
            float ix = 1f / dx, iy = 1f / dy, iz = 1f / dz; float best = tmax; int bp = -1; bool bv = false;
            int* st = stackalloc int[128]; int sp = 0; st[sp++] = 0;
            fixed (float* mn = nMin, mx = nMax, Pp = P) fixed (int* li = nLinks, an = nAnz)
                while (sp > 0)
                {
                    int k = st[--sp];
                    if (!KisteZ(mn, mx, k, ox, oy, oz, ix, iy, iz, best)) continue;
                    int anz = an[k];
                    if (anz > 0)
                    {
                        int i0 = li[k]; float* p = Pp + 9 * i0;
                        for (int i = 0; i < anz; i++, p += 9) { float t = TrefferZ(p, ox, oy, oz, dx, dy, dz, out bool v); if (t > tmin && t < best) { best = t; bp = i0 + i; bv = v; } }
                    }
                    else { st[sp++] = li[k]; st[sp++] = li[k] + 1; }
                }
            return bp < 0 ? new Hit(-1f, -1, false) : new Hit(best, bp, bv);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool KisteZ(float* mn, float* mx, int k, float ox, float oy, float oz, float ix, float iy, float iz, float tmax)
        {
            float t0 = (mn[3 * k] - ox) * ix, t1 = (mx[3 * k] - ox) * ix;
            float tn = Math.Min(t0, t1), tf = Math.Max(t0, t1);
            t0 = (mn[3 * k + 1] - oy) * iy; t1 = (mx[3 * k + 1] - oy) * iy;
            tn = Math.Max(tn, Math.Min(t0, t1)); tf = Math.Min(tf, Math.Max(t0, t1));
            t0 = (mn[3 * k + 2] - oz) * iz; t1 = (mx[3 * k + 2] - oz) * iz;
            tn = Math.Max(tn, Math.Min(t0, t1)); tf = Math.Min(tf, Math.Max(t0, t1));
            return tf >= Math.Max(tn, 0f) && tn <= tmax;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float TrefferZ(float* p, float ox, float oy, float oz, float dx, float dy, float dz, out bool vorne)
        {
            float e1x = p[3] - p[0], e1y = p[4] - p[1], e1z = p[5] - p[2];
            float e2x = p[6] - p[0], e2y = p[7] - p[1], e2z = p[8] - p[2];
            float px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
            float det = e1x * px + e1y * py + e1z * pz;
            vorne = det > 0;
            if (Math.Abs(det) < 1e-12f) return -1;
            float inv = 1f / det;
            float tx = ox - p[0], ty = oy - p[1], tz = oz - p[2];
            float u = (tx * px + ty * py + tz * pz) * inv; if (u < 0 || u > 1) return -1;
            float qx = ty * e1z - tz * e1y, qy = tz * e1x - tx * e1z, qz = tx * e1y - ty * e1x;
            float v = (dx * qx + dy * qy + dz * qz) * inv; if (v < 0 || u + v > 1) return -1;
            return (e2x * qx + e2y * qy + e2z * qz) * inv;
        }

        public V Ecke(int platz, int j) { int o = 9 * platz + 3 * j; return new V(P[o], P[o + 1], P[o + 2]); }
        public V Normale(int platz) { V a = Ecke(platz, 0), b = Ecke(platz, 1), c = Ecke(platz, 2); return V.Cross(b - a, c - a).Norm(); }
    }

    // ------------------------------------------------------------------------------------------ Portale (Etappe2-Karten/Wegenetz.cs PortalTest)
    internal sealed class PortalTest
    {
        readonly V[] mitte, n, u, w; readonly double[] hu, hw;
        readonly Dictionary<long, int[]> gitter = new Dictionary<long, int[]>(); const double G = 4.0;
        static long Zelle(V x) => Wegenetz.Schluessel((int)Math.Floor(x.X / G), (int)Math.Floor(x.Y / G), (int)Math.Floor(x.Z / G));

        public PortalTest(WegDaten d)
        {
            int np = d.PN; mitte = new V[np]; n = new V[np]; u = new V[np]; w = new V[np]; hu = new double[np]; hw = new double[np];
            var tmp = new Dictionary<long, List<int>>();
            for (int i = 0; i < np; i++)
            {
                mitte[i] = V.Aus(d.PMitte, i);
                var ax = new[] { V.Aus(d.PAx, i), V.Aus(d.PAy, i), V.Aus(d.PAz, i) };
                var ord = new int[3];   // wie OrderByDescending(v => v.Len) (stabil)
                StabilNachLaenge(ax, ord);
                V a0 = ax[ord[0]], a1 = ax[ord[1]];
                u[i] = a0.Norm(); w[i] = a1.Norm(); hu[i] = a0.Len; hw[i] = a1.Len; n[i] = V.Cross(a0, a1).Norm();
                if (!d.PSchliessbar[i]) continue;
                double rr = Math.Sqrt(hu[i] * hu[i] + hw[i] * hw[i]) + G / 2;
                V lo = mitte[i] - new V(rr, rr, rr), hi = mitte[i] + new V(rr, rr, rr);
                int x0 = (int)Math.Floor(lo.X / G), y0 = (int)Math.Floor(lo.Y / G), z0 = (int)Math.Floor(lo.Z / G);
                int x1 = (int)Math.Floor(hi.X / G), y1 = (int)Math.Floor(hi.Y / G), z1 = (int)Math.Floor(hi.Z / G);
                for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++)
                {
                    long k = Wegenetz.Schluessel(x, y, z);
                    if (!tmp.TryGetValue(k, out var l)) tmp[k] = l = new List<int>(); l.Add(i);
                }
            }
            foreach (var kv in tmp) gitter[kv.Key] = kv.Value.ToArray();
        }
        /// <summary>Reihenfolge wie LINQ OrderByDescending(Len): absteigend, bei Gleichstand urspruengliche Reihenfolge.</summary>
        static void StabilNachLaenge(V[] ax, int[] ord)
        {
            for (int i = 0; i < 3; i++) ord[i] = i;
            for (int i = 1; i < 3; i++) { int k = ord[i]; int j = i - 1; while (j >= 0 && ax[ord[j]].Len < ax[k].Len) { ord[j + 1] = ord[j]; j--; } ord[j + 1] = k; }
        }
        bool Treffer(int i, V a, V b)
        {
            const double eps = 0.005, rand = 0.05;
            double da = V.Dot(a - mitte[i], n[i]), db = V.Dot(b - mitte[i], n[i]);
            bool quer = (da < -eps && db >= -eps) || (da > eps && db <= eps);
            if (!quer) return false;
            V s = Math.Abs(da - db) < 1e-12 ? b : a + (b - a) * (da / (da - db)); V r = s - mitte[i];
            return Math.Abs(V.Dot(r, u[i])) <= hu[i] + rand && Math.Abs(V.Dot(r, w[i])) <= hw[i] + rand;
        }
        /// <summary>Weg A: wie Alle, in einen vorhandenen Puffer (leer = keine).</summary>
        public void Alle(V a, V b, List<int> aus, int ia = -1, int ib = -1)
        {
            aus.Clear(); double len = V.Abstand(a, b); int m = Math.Max(1, (int)Math.Ceiling(len / (G / 2)));
            long vor = long.MinValue;
            for (int s = 0; s <= m; s++)
            {
                long c = Zelle(a + (b - a) * ((double)s / m)); if (c == vor) continue; vor = c;
                if (!gitter.TryGetValue(c, out var l)) continue;
                foreach (int i in l) if (i != ia && i != ib && !aus.Contains(i) && Treffer(i, a, b)) aus.Add(i);
            }
        }
        public List<int> Alle(V a, V b, int ia = -1, int ib = -1)
        {
            List<int> aus = null; double len = V.Abstand(a, b); int m = Math.Max(1, (int)Math.Ceiling(len / (G / 2)));
            long vor = long.MinValue;
            for (int s = 0; s <= m; s++)
            {
                long c = Zelle(a + (b - a) * ((double)s / m)); if (c == vor) continue; vor = c;
                if (!gitter.TryGetValue(c, out var l)) continue;
                foreach (int i in l) if (i != ia && i != ib && (aus == null || !aus.Contains(i)) && Treffer(i, a, b)) (aus = aus ?? new List<int>()).Add(i);
            }
            return aus;
        }
    }

    /// <summary>Weg A: Min-Heap als ulong-Feld (Schluessel-Bits oben, Wert unten; float >= 0 sortiert wie seine Bits). Gleichstand: kleinerer Wert zuerst.</summary>
    internal static unsafe class PHeap
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong Pack(float key, int val) { return ((ulong)(*(uint*)&key) << 32) | (uint)val; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Key(ulong e) { uint k = (uint)(e >> 32); return *(float*)&k; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static int Wert(ulong e) => (int)(uint)e;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Rein(ulong* h, ref int n, ulong e) { int i = n++; while (i > 0) { int p = (i - 1) >> 1; if (h[p] <= e) break; h[i] = h[p]; i = p; } h[i] = e; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Raus(ulong* h, ref int n)
        {
            ulong top = h[0]; ulong l = h[--n]; int i = 0, m = n;
            while (true) { int c = 2 * i + 1; if (c >= m) break; if (c + 1 < m && h[c + 1] < h[c]) c++; if (h[c] >= l) break; h[i] = h[c]; i = c; }
            if (m > 0) h[i] = l; return top;
        }
    }

    /// <summary>Binaerer Min-Heap (float-Schluessel, int-Wert), Etappe2.</summary>
    internal sealed class Heap
    {
        float[] k = new float[1024]; int[] v = new int[1024]; public int Anzahl;
        public void Leeren() => Anzahl = 0;
        public void Rein(float key, int val)
        {
            if (Anzahl == k.Length) { Array.Resize(ref k, 2 * Anzahl); Array.Resize(ref v, 2 * Anzahl); }
            int i = Anzahl++;
            while (i > 0) { int p = (i - 1) >> 1; if (k[p] <= key) break; k[i] = k[p]; v[i] = v[p]; i = p; }
            k[i] = key; v[i] = val;
        }
        public void Raus(out float key, out int val)
        {
            key = k[0]; val = v[0]; float lk = k[--Anzahl]; int lv = v[Anzahl]; int i = 0;
            while (true)
            {
                int c = 2 * i + 1; if (c >= Anzahl) break;
                if (c + 1 < Anzahl && k[c + 1] < k[c]) c++;
                if (k[c] >= lk) break;
                k[i] = k[c]; v[i] = v[c]; i = c;
            }
            k[i] = lk; v[i] = lv;
        }
    }
}
