using System;
using System.Collections.Generic;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    //  raumecke (16.11.2026, Marke 2 Factory 19:41, Bot10): ein Audio-Raum des Spiels ist die VEREINIGUNG seiner Boxen (RaumGeometrieKern).
    //  Der Raumweg (RaumwegKern.Route) rechnete jedes Stueck innerhalb eines Raums als gerade Sichtlinie - im L-foermigen Seitenraum
    //  Side_Room_Entrance_1 (Kaesten B27/B28/B29) ging das letzte Stueck Austritt #140 -> Hoerer durch die Westwand: Richtung -90 statt
    //  ~ -146 Grad, Pegel ohne die Ecke (~20 dB). Hier: verlaesst die Gerade die Vereinigung der Kaesten, fuehrt der Weg ueber die
    //  Uebergaenge zwischen den Kaesten (kuerzester Weg ueber Stuetzpunkte in den Ueberlappungen, je Kasten konvex -> jede Gerade zwischen
    //  zwei Punkten desselben Kastens liegt im Raum). Die Ecken sind Kantenpunkte (Beugung nach RaumwegKern.KurzeAnderson in Route) und die
    //  letzte Ecke ist der Austritt fuer die Richtung. Reiner Kern ohne Unity-Typen; Stuetzpunkte je Raum einmal (RaumEcken.Bauen).
    // ------------------------------------------------------------------------------------------
    internal sealed class RaumEcken
    {
        public Kasten[] K = new Kasten[0]; public int N;
        public V3[] P = new V3[0]; public double[] PYu = new double[0], PYo = new double[0];   // Stuetzpunkte in den Ueberlappungen, ihr Hoehenbereich
        public int[] PA = new int[0], PB = new int[0];                                         // die beiden Kaesten je Stuetzpunkt
        public V3[] BMin = new V3[0], BMax = new V3[0];                                         // Huelle je Kasten (Vorfilter)
        public int S;
        public int[] BStart = new int[1], BList = new int[0];   // Stuetzpunkte je Kasten
        public int[] NStart = new int[1], NList = new int[0];   // Nachbarn je Stuetzpunkt (gleicher Kasten, freie Linie)
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;       // Huelle der Vereinigung
        // Puffer fuer Weg (ein Faden: Hauptfaden)
        internal double[] dist = new double[0]; internal int[] vor = new int[0]; internal bool[] fertig = new bool[0];
        internal bool[] inA = new bool[0], inB = new bool[0];
    }

    /// <summary>raumecke: freie Linie durch die Geometrie (Spiel: Physics.Linecast mit der Maske der Schallstrahlen; offline: BVH des
    /// Wegenetzes). Die Kaesten eines Raums decken eine Halle nicht immer ganz ab (Luecken zwischen den Boxen) - eine Ecke gilt nur, wenn
    /// die gerade Linie auch wirklich verdeckt ist. null = nicht pruefen.</summary>
    internal interface ILinie { bool Frei(V3 a, V3 b); }

    /// <summary>raumecke: kuerzeste Wege von einer Portalmitte zu allen Stuetzpunkten eines Raums (Vor -1 = direkt von der Wurzel, -2 = nicht erreichbar).</summary>
    internal sealed class EckBaum { public V3 Wurzel; public double[] D; public int[] Vor; public V3 Vorpunkt; public bool HatVorpunkt; }

    internal static class RaumEckeKern
    {
        public const double Schritt = 0.25;   // Abtastung der Geraden (m)
        public const double RandEps = 0.3;    // Endpunkte (Portalpunkte liegen auf Raumflaechen) und Stuetzpunkte: Toleranz (m)
        public const int EckenMax = 4;
        public const double AussenMin = 1.0;
        public static double UmwegMax = 1.4;
        public static double HoeheMax = 1.5;  // Ecke hoechstens so weit (m) ueber/unter der Hoehe des geraden Wegs  // Eckweg hoechstens so viel mal so lang wie die Gerade (+ 2 m); L-Raum Marke 2: 1,3  // so lang (m) muss die Gerade ausserhalb der Kaesten laufen, damit ein Eckweg gesucht wird

        /// <summary>Stuetzpunkte je Paar sich ueberlappender Kaesten: 3 x 3 Punkte ueber die Ueberlappung (Mitte und Raender in den beiden
        /// waagrechten Achsen des Kastens, Hoehe Mitte), die in beiden Kaesten liegen. Einmal je Raum.</summary>
        public static RaumEcken Bauen(Kasten[] ks, int n) => Bauen(ks, n, null);
        /// <summary>linie != null: Nachbarschaft zweier Stuetzpunkte im selben Kasten nur bei freier Linie (Spiel: Linecast, einmal je Raid).</summary>
        public static RaumEcken Bauen(Kasten[] ks, int n, ILinie linie)
        {
            var r = new RaumEcken { K = ks ?? new Kasten[0], N = ks == null ? 0 : Math.Min(n, ks.Length) };
            r.MinX = r.MinY = r.MinZ = double.MaxValue; r.MaxX = r.MaxY = r.MaxZ = double.MinValue;
            var bmin = new V3[r.N]; var bmax = new V3[r.N];
            for (int i = 0; i < r.N; i++)
            {
                Huelle(in r.K[i], out bmin[i], out bmax[i]);
                r.MinX = Math.Min(r.MinX, bmin[i].X); r.MinY = Math.Min(r.MinY, bmin[i].Y); r.MinZ = Math.Min(r.MinZ, bmin[i].Z);
                r.MaxX = Math.Max(r.MaxX, bmax[i].X); r.MaxY = Math.Max(r.MaxY, bmax[i].Y); r.MaxZ = Math.Max(r.MaxZ, bmax[i].Z);
            }
            var pts = new List<V3>(); var yu = new List<double>(); var yo = new List<double>(); var je = new List<int>[r.N]; var pa = new List<int>(); var pb = new List<int>();
            for (int i = 0; i < r.N; i++) je[i] = new List<int>();
            if (r.N > 1)
                for (int i = 0; i < r.N; i++)
                    for (int j = i + 1; j < r.N; j++)
                    {
                        if (bmin[i].X > bmax[j].X + 0.05 || bmin[j].X > bmax[i].X + 0.05 || bmin[i].Y > bmax[j].Y + 0.05 || bmin[j].Y > bmax[i].Y + 0.05
                            || bmin[i].Z > bmax[j].Z + 0.05 || bmin[j].Z > bmax[i].Z + 0.05) continue;
                        int a = i, b = j;
                        if (!Ueberlappung(in r.K[a], in r.K[b], out double u0, out double u1, out double v0, out double v1, out double w0, out double w1))
                        {
                            a = j; b = i;
                            if (!Ueberlappung(in r.K[a], in r.K[b], out u0, out u1, out v0, out v1, out w0, out w1)) continue;
                        }
                        ref Kasten k = ref r.K[a];
                        double vm = Math.Min(v1, v0 + 1.5);   // Ohrhoehe ueber dem Boden der Ueberlappung (Hallen: Mitte laege Meter hoch)
                        // Stuetzpunkte alle ~2 m ueber die Ueberlappung (mindestens 3, hoechstens 5 je Achse), 0,3 m von ihren Raendern
                        // (die Innenecke liegt sonst auf der Wand: die Linie streift sie)
                        int nu = Math.Max(3, Math.Min(5, (int)Math.Ceiling((u1 - u0) / 2.0) + 1)), nw = Math.Max(3, Math.Min(5, (int)Math.Ceiling((w1 - w0) / 2.0) + 1));
                        double du = Math.Min(0.3, 0.25 * (u1 - u0)), dw = Math.Min(0.3, 0.25 * (w1 - w0));
                        for (int iu = 0; iu < nu; iu++)
                            for (int iw = 0; iw < nw; iw++)
                            {
                                double u = (u0 + du) + (u1 - u0 - 2 * du) * iu / (nu - 1), w = (w0 + dw) + (w1 - w0 - 2 * dw) * iw / (nw - 1);
                                V3 p = Welt(in k, u, vm, w);
                                if (!RaumGeometrieKern.Drin(in r.K[i], p.X, p.Y, p.Z, 0.05) || !RaumGeometrieKern.Drin(in r.K[j], p.X, p.Y, p.Z, 0.05)) continue;
                                double ya = Welt(in k, u, v0, w).Y, yb = Welt(in k, u, v1, w).Y;
                                je[i].Add(pts.Count); je[j].Add(pts.Count); pts.Add(p); yu.Add(Math.Min(ya, yb)); yo.Add(Math.Max(ya, yb)); pa.Add(i); pb.Add(j);
                            }
                    }
            r.P = pts.ToArray(); r.PYu = yu.ToArray(); r.PYo = yo.ToArray(); r.S = r.P.Length; r.PA = pa.ToArray(); r.PB = pb.ToArray(); r.BMin = bmin; r.BMax = bmax;
            r.BStart = new int[r.N + 1]; var bl = new List<int>();
            for (int i = 0; i < r.N; i++) { r.BStart[i] = bl.Count; bl.AddRange(je[i]); }
            r.BStart[r.N] = bl.Count; r.BList = bl.ToArray();
            r.dist = new double[r.S + 2]; r.vor = new int[r.S + 2]; r.fertig = new bool[r.S + 2]; r.inA = new bool[r.N]; r.inB = new bool[r.N];
            // Nachbarn: Stuetzpunkte, die einen Kasten teilen (und frei sichtbar sind)
            var nb = new List<int>[r.S]; for (int i = 0; i < r.S; i++) nb[i] = new List<int>();
            for (int i = 0; i < r.N; i++)
                for (int x = r.BStart[i]; x < r.BStart[i + 1]; x++)
                    for (int y = x + 1; y < r.BStart[i + 1]; y++)
                    {
                        int p = r.BList[x], q = r.BList[y]; if (nb[p].Contains(q)) continue;
                        if (linie != null && !linie.Frei(r.P[p], r.P[q])) continue;
                        nb[p].Add(q); nb[q].Add(p);
                    }
            r.NStart = new int[r.S + 1]; var nl = new List<int>();
            for (int i = 0; i < r.S; i++) { r.NStart[i] = nl.Count; nl.AddRange(nb[i]); }
            r.NStart[r.S] = nl.Count; r.NList = nl.ToArray();
            return r;
        }

        static V3 Welt(in Kasten k, double u, double v, double w)
            => new V3(k.Mx + k.Ux * u + k.Vx * v + k.Wx * w, k.My + k.Uy * u + k.Vy * v + k.Wy * w, k.Mz + k.Uz * u + k.Vz * v + k.Wz * w);

        static void Huelle(in Kasten k, out V3 min, out V3 max)
        {
            double ex = Math.Abs(k.Ux) * k.Hx + Math.Abs(k.Vx) * k.Hy + Math.Abs(k.Wx) * k.Hz;
            double ey = Math.Abs(k.Uy) * k.Hx + Math.Abs(k.Vy) * k.Hy + Math.Abs(k.Wy) * k.Hz;
            double ez = Math.Abs(k.Uz) * k.Hx + Math.Abs(k.Vz) * k.Hy + Math.Abs(k.Wz) * k.Hz;
            min = new V3(k.Mx - ex, k.My - ey, k.Mz - ez); max = new V3(k.Mx + ex, k.My + ey, k.Mz + ez);
        }

        /// <summary>Ueberlappung von b in den Koordinaten von a (Gitter 9 x 5 x 9 in a, Punkte in b): Grenzen je Achse. false = keine.</summary>
        static bool Ueberlappung(in Kasten a, in Kasten b, out double u0, out double u1, out double v0, out double v1, out double w0, out double w1)
        {
            u0 = v0 = w0 = double.MaxValue; u1 = v1 = w1 = double.MinValue; bool hat = false;
            // gleich ausgerichtet (haeufig): Ueberlappung exakt aus den Intervallen je Achse
            double[] ax = { a.Ux, a.Uy, a.Uz, a.Vx, a.Vy, a.Vz, a.Wx, a.Wy, a.Wz }, bx = { b.Ux, b.Uy, b.Uz, b.Vx, b.Vy, b.Vz, b.Wx, b.Wy, b.Wz };
            double[] bh = { b.Hx, b.Hy, b.Hz }, ah = { a.Hx, a.Hy, a.Hz }, lo = new double[3], hi = new double[3]; bool parallel = true;
            double dx = b.Mx - a.Mx, dy = b.My - a.My, dz = b.Mz - a.Mz;
            for (int i = 0; i < 3 && parallel; i++)
            {
                double c = dx * ax[3 * i] + dy * ax[3 * i + 1] + dz * ax[3 * i + 2], hb = -1;
                for (int j = 0; j < 3; j++) { double d = ax[3 * i] * bx[3 * j] + ax[3 * i + 1] * bx[3 * j + 1] + ax[3 * i + 2] * bx[3 * j + 2]; if (Math.Abs(d) > 0.999) hb = bh[j]; }
                if (hb < 0) { parallel = false; break; }
                lo[i] = Math.Max(-ah[i], c - hb); hi[i] = Math.Min(ah[i], c + hb);
            }
            if (parallel)
            {
                if (lo[0] > hi[0] + 0.02 || lo[1] > hi[1] + 0.02 || lo[2] > hi[2] + 0.02) return false;
                u0 = lo[0]; u1 = Math.Max(lo[0], hi[0]); v0 = lo[1]; v1 = Math.Max(lo[1], hi[1]); w0 = lo[2]; w1 = Math.Max(lo[2], hi[2]); return true;
            }
            for (int iu = 0; iu <= 16; iu++)
                for (int iv = 0; iv <= 4; iv++)
                    for (int iw = 0; iw <= 16; iw++)
                    {
                        double u = -a.Hx + 2 * a.Hx * iu / 16.0, v = -a.Hy + 2 * a.Hy * iv / 4.0, w = -a.Hz + 2 * a.Hz * iw / 16.0;
                        V3 p = Welt(in a, u, v, w);
                        if (!RaumGeometrieKern.Drin(in b, p.X, p.Y, p.Z, 0.02)) continue;
                        hat = true; u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v); w0 = Math.Min(w0, w); w1 = Math.Max(w1, w);
                    }
            return hat;
        }

        static bool InIrgendeinem(RaumEcken r, V3 p, double eps)
        {
            for (int i = 0; i < r.N; i++)
            {
                V3 a = r.BMin[i], b = r.BMax[i];
                if (p.X < a.X - eps || p.X > b.X + eps || p.Y < a.Y - eps || p.Y > b.Y + eps || p.Z < a.Z - eps || p.Z > b.Z + eps) continue;
                if (RaumGeometrieKern.Drin(in r.K[i], p.X, p.Y, p.Z, eps)) return true;
            }
            return false;
        }

        /// <summary>Liegt die Gerade a -> b in der Vereinigung der Kaesten? (Abtastung alle 0,25 m, die ersten/letzten 0,3 m ausgenommen;
        /// ein Raum aus einem Kasten: immer ja.)</summary>
        public static bool Frei(RaumEcken r, V3 a, V3 b)
        {
            if (r == null || r.N <= 1) return true;
            for (int i = 0; i < r.N; i++)   // beide Enden im selben Kasten: der Kasten ist konvex
                if (RaumGeometrieKern.Drin(in r.K[i], a.X, a.Y, a.Z, RandEps) && RaumGeometrieKern.Drin(in r.K[i], b.X, b.Y, b.Z, RandEps)) return true;
            double l = V3.Abstand(a, b); int n = (int)Math.Ceiling(l / Schritt);
            for (int s = 1; s < n; s++)
            {
                double t = (double)s / n; if (t * l < RandEps || (1 - t) * l < RandEps) continue;
                if (!InIrgendeinem(r, a + (b - a) * t, 0.02)) return false;
            }
            return true;
        }

        /// <summary>Wie lang (m) liegt die Gerade a -> b ausserhalb der Kaesten? (Abtastung wie Frei)</summary>
        public static double AussenLaenge(RaumEcken r, V3 a, V3 b)
        {
            if (r == null || r.N <= 1) return 0;
            double l = V3.Abstand(a, b); int n = (int)Math.Ceiling(l / Schritt); int aus = 0;
            for (int s = 1; s < n; s++)
            {
                double t = (double)s / n; if (t * l < RandEps || (1 - t) * l < RandEps) continue;
                if (!InIrgendeinem(r, a + (b - a) * t, 0.02)) aus++;
            }
            return aus * (n > 0 ? l / n : 0);
        }

        /// <summary>Weg a -> b im Raum: verlaesst die Gerade die Kaesten, die Ecken (hoechstens max) des kuerzesten Wegs ueber die
        /// Stuetzpunkte nach ecken; Rueckgabe Zahl der Ecken (0 = gerade frei, oder a/b liegt in keinem Kasten, oder kein Weg).
        /// Hoehe der Ecken: auf der Geraden a.y -> b.y nach der Weglaenge, im Hoehenbereich der Ueberlappung. Keine Speicheranforderung.</summary>
        public static int Weg(RaumEcken r, V3 a, V3 b, V3[] ecken, int max) => Weg(r, a, b, ecken, max, null);
        public static int Weg(RaumEcken r, V3 a, V3 b, V3[] ecken, int max, ILinie linie)
        {
            if (r == null || r.N <= 1 || r.S == 0 || Frei(r, a, b)) return 0;
            if (linie != null && linie.Frei(Ein(a, b), Ein(b, a))) return 0;   // Gerade verlaesst die Kaesten, ist aber frei (Luecke zwischen den Boxen)
            int S = r.S; bool hatA = false, hatB = false;
            for (int i = 0; i < r.N; i++)
            {
                r.inA[i] = RaumGeometrieKern.Drin(in r.K[i], a.X, a.Y, a.Z, RandEps); r.inB[i] = RaumGeometrieKern.Drin(in r.K[i], b.X, b.Y, b.Z, RandEps);
                hatA |= r.inA[i]; hatB |= r.inB[i];
            }
            if (!hatA || !hatB) return 0;
            for (int i = 0; i < S + 2; i++) { r.dist[i] = double.MaxValue; r.vor[i] = -1; r.fertig[i] = false; }
            // Start: Stuetzpunkte der Kaesten, in denen a liegt (mit Linie: von a aus sichtbar; a im Tuerrahmen -> bis 1 m eingerueckt)
            for (int i = 0; i < r.N; i++)
                if (r.inA[i])
                    for (int k = r.BStart[i]; k < r.BStart[i + 1]; k++)
                    {
                        int p = r.BList[k]; double dd = V3.Abstand(a, r.P[p]); if (dd >= r.dist[p]) continue;
                        if (linie != null && !linie.Frei(Ein(a, r.P[p]), r.P[p])) continue;
                        r.dist[p] = dd; r.vor[p] = S;
                    }
            int ziel = S + 1;
            while (true)
            {
                int best = -1; double bd = double.MaxValue;
                for (int i = 0; i < S; i++) if (!r.fertig[i] && r.dist[i] < bd) { bd = r.dist[i]; best = i; }
                if (r.dist[ziel] <= bd) break;   // Ziel naeher als jeder offene Punkt
                if (best < 0) break;
                r.fertig[best] = true; V3 pb = r.P[best];
                if (r.inB[r.PA[best]] || r.inB[r.PB[best]])
                {
                    double dz = bd + V3.Abstand(pb, b);
                    if (dz < r.dist[ziel] && (linie == null || linie.Frei(pb, Ein(b, pb)))) { r.dist[ziel] = dz; r.vor[ziel] = best; }
                }
                for (int k = r.NStart[best]; k < r.NStart[best + 1]; k++)
                {
                    int q = r.NList[k]; if (r.fertig[q]) continue;
                    double dq = bd + V3.Abstand(pb, r.P[q]); if (dq < r.dist[q]) { r.dist[q] = dq; r.vor[q] = best; }
                }
            }
            if (r.vor[ziel] < 0) return 0;
            int n = 0; for (int p = r.vor[ziel]; p >= 0 && p != S; p = r.vor[p]) n++;
            if (n == 0 || n > max) return 0;
            int j = n - 1; for (int p = r.vor[ziel]; p >= 0 && p != S; p = r.vor[p]) ecken[j--] = r.P[p];
            // Hoehe nach der Weglaenge
            double gesamt = r.dist[ziel], s0 = 0; V3 vorP = a;
            for (int i = 0; i < n; i++)
            {
                s0 += V3.Abstand(vorP, ecken[i]); vorP = ecken[i];
                int pi = Index(r, ecken[i]); double y = a.Y + (b.Y - a.Y) * (gesamt > 1e-9 ? s0 / gesamt : 0.5);
                if (pi >= 0) y = Math.Max(r.PYu[pi], Math.Min(r.PYo[pi], y));
                ecken[i] = new V3(ecken[i].X, y, ecken[i].Z);
            }
            return n;
        }

        /// <summary>Kuerzeste Wege von einer Wurzel (Mitte eines Portals des Raums) zu allen Stuetzpunkten - einmal je (Raum, Portal), danach
        /// kostet ein Weg zum beweglichen Ende nur die Stuetzpunkte der Kaesten, in denen es liegt (WegBaum).</summary>
        public static EckBaum Baum(RaumEcken r, V3 wurzel, ILinie linie) => Baum(r, wurzel, linie, default);
        /// <summary>tabellenneu (Tunnel-Ausreisser 19:43:23, Luke #46 in der Bodenrinne): liegt die Portalmitte ausserhalb der Kaesten, gilt als
        /// Wurzel der erste Punkt entlang der Portalnormalen (0,25..2 m, beide Seiten), der in einem Kasten liegt und von der Mitte frei
        /// erreichbar ist ("Vorpunkt" - der Schall verlaesst die Oeffnung quer zu ihr); er ist dann die erste Ecke jedes Wegs.</summary>
        public static EckBaum Baum(RaumEcken r, V3 wurzel, ILinie linie, V3 normale)
        {
            var t = new EckBaum { Wurzel = wurzel, D = new double[r.S], Vor = new int[r.S] };
            if (!InIrgendeinem(r, wurzel, RandEps) && normale.Laenge > 1e-9)
            {
                V3 nn = normale * (1 / normale.Laenge);
                for (double sch = 0.25; sch <= 2.0 + 1e-9 && !t.HatVorpunkt; sch += 0.25)
                    for (int sg = 0; sg < 2 && !t.HatVorpunkt; sg++)
                    {
                        V3 p = wurzel + nn * (sg == 0 ? sch : -sch);
                        if (InIrgendeinem(r, p, 0.02) && (linie == null || linie.Frei(wurzel, p))) { t.Vorpunkt = p; t.HatVorpunkt = true; }
                    }
                if (t.HatVorpunkt) wurzel = t.Vorpunkt;
            }
            int S = r.S; var fertig = new bool[S];
            for (int i = 0; i < S; i++) { t.D[i] = double.MaxValue; t.Vor[i] = -2; }
            // Portalmitten liegen auf der Raumgrenze, manchmal bis ~1 m neben den Kaesten (Reserve) -> Toleranz 1 m
            for (int i = 0; i < r.N; i++)
                if (RaumGeometrieKern.Drin(in r.K[i], wurzel.X, wurzel.Y, wurzel.Z, 1.0))
                    for (int k = r.BStart[i]; k < r.BStart[i + 1]; k++)
                    {
                        int p = r.BList[k]; double dd = V3.Abstand(wurzel, r.P[p]); if (dd >= t.D[p]) continue;
                        if (linie != null && !linie.Frei(Ein(wurzel, r.P[p]), r.P[p])) continue;
                        t.D[p] = dd; t.Vor[p] = -1;
                    }
            while (true)
            {
                int best = -1; double bd = double.MaxValue;
                for (int i = 0; i < S; i++) if (!fertig[i] && t.D[i] < bd) { bd = t.D[i]; best = i; }
                if (best < 0) break;
                fertig[best] = true;
                for (int k = r.NStart[best]; k < r.NStart[best + 1]; k++)
                {
                    int q = r.NList[k]; if (fertig[q]) continue;
                    double dq = bd + V3.Abstand(r.P[best], r.P[q]); if (dq < t.D[q]) { t.D[q] = dq; t.Vor[q] = best; }
                }
            }
            return t;
        }

        /// <summary>Weg vom Portal (Punkt a auf dem Portal, Baum t von seiner Mitte) zum Ende b im Raum: wie Weg, aber ueber den Baum.
        /// Ecken in Reihenfolge a -> b. Keine Speicheranforderung.</summary>
        public static int WegBaum(RaumEcken r, EckBaum t, V3 a, V3 b, V3[] ecken, int max, ILinie linie)
        {
            if (r == null || t == null || r.N <= 1 || r.S == 0) return 0;
            if (t.HatVorpunkt)
            {
                // Portal ausserhalb der Kaesten: erst zum Vorpunkt, von dort gerade, wenn frei; sonst ueber den Baum (Vorpunkt = erste Ecke)
                V3 v = t.Vorpunkt;
                if (AussenLaenge(r, v, b) < AussenMin || (linie == null || linie.Frei(v, Ein(b, v)))) { if (max < 1) return 0; ecken[0] = v; return 1; }
                int nb = WegAusBaum(r, t, v, b, ecken, max - 1, linie, 1);
                if (nb < 0) { if (linie != null && !linie.Frei(Ein(a, b), Ein(b, a))) { ecken[0] = v; return 1; } return 0; }
                ecken[0] = v; return nb + 1;
            }
            if (Frei(r, a, b)) return 0;
            // tabellenneu (Tunnel-Ausreisser 19:43:23,1: Luke #46 in der Bodenrinne, Gerade nur 0,2 m ausserhalb der Kaesten, verdeckt von der
            // Rinnenkante): eine Ecke nur, wenn die Gerade ein gutes Stueck ausserhalb des Raums verlaeuft (L-Raum: 17 m) - sonst ist das
            // Hindernis ein Einbau (Kante, Rinne), kein Raumknick, und die Gerade bleibt
            if (AussenLaenge(r, a, b) < AussenMin) return 0;
            if (linie != null && linie.Frei(Ein(a, b), Ein(b, a))) return 0;
            int n0 = WegAusBaum(r, t, a, b, ecken, max, linie, 0);
            return n0 < 0 ? 0 : n0;
        }

        /// <summary>Ecken ab ecken[off] ueber den Baum von a (Wurzel) nach b; -1 = kein Weg.</summary>
        static int WegAusBaum(RaumEcken r, EckBaum t, V3 a, V3 b, V3[] ecken, int max, ILinie linie, int off)
        {
            int best = -1; double bd = double.MaxValue;
            for (int i = 0; i < r.N; i++)
            {
                if (!RaumGeometrieKern.Drin(in r.K[i], b.X, b.Y, b.Z, RandEps)) continue;
                for (int k = r.BStart[i]; k < r.BStart[i + 1]; k++)
                {
                    int p = r.BList[k]; if (t.D[p] == double.MaxValue) continue;
                    double dd = t.D[p] + V3.Abstand(r.P[p], b); if (dd >= bd) continue;
                    if (linie != null && !linie.Frei(r.P[p], Ein(b, r.P[p]))) continue;
                    bd = dd; best = p;
                }
            }
            if (best < 0) return -1;
            // ein Umweg weit ueber die Gerade hinaus (Hallen: Kaesten decken die Luft nicht ganz, der Weg klettert ueber Buehnen) ist kein Raumknick
            if (bd > UmwegMax * V3.Abstand(a, b) + 2.0) return -1;
            int n = 0; for (int p = best; p >= 0; p = t.Vor[p]) n++;
            if (n == 0 || n > max) return -1;
            int j = off + n - 1; for (int p = best; p >= 0; p = t.Vor[p]) ecken[j--] = r.P[p];
            double s0 = 0; V3 vorP = a;
            for (int i = off; i < off + n; i++)
            {
                s0 += V3.Abstand(vorP, ecken[i]); vorP = ecken[i];
                int pi = Index(r, ecken[i]); double y0 = a.Y + (b.Y - a.Y) * (bd > 1e-9 ? Math.Min(1, s0 / bd) : 0.5), y = y0;
                if (pi >= 0) y = Math.Max(r.PYu[pi], Math.Min(r.PYo[pi], y));
                // tabellenneu: Uebergang nur hoch ueber dem Weg (Hallen: Kaesten ueberlappen erst ueber Buehnen/Galerien) -> kein Raumknick auf Ohrhoehe
                if (Math.Abs(y - y0) > HoeheMax) return -1;
                ecken[i] = new V3(ecken[i].X, y, ecken[i].Z);
            }
            return n;
        }

        /// <summary>Punkt p bis 1 m (hoechstens ein Drittel der Strecke) in Richtung q eingerueckt - Portalpunkte liegen im Tuerrahmen.</summary>
        static V3 Ein(V3 p, V3 q) { double l = V3.Abstand(p, q); return l < 1e-6 ? p : p + (q - p) * (Math.Min(1.0, l / 3) / l); }

        static int Index(RaumEcken r, V3 p) { for (int i = 0; i < r.S; i++) if (r.P[i].X == p.X && r.P[i].Y == p.Y && r.P[i].Z == p.Z) return i; return -1; }
    }
}
