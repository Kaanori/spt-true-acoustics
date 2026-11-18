using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Materialklang.Schallweg
{
    // ------------------------------------------------------------------------------------------
    //  0.8.8 Wegsuche (Etappe 3): Hoererbaum-Suche wie Etappe2-Karten/Suche2.cs (WegeBaum, HoererBaumBauen, Verfeinern, Bewerten,
    //  RohrTeilen, Durchgang) mit Rohr (Etappe2/Rohr.cs + RohrKarten.cs), Tueren (Massengesetz des Blatts, TuerKern) und gemessener
    //  Dicke (Etappe2/Dicke.cs LokalAn). Gleiche Formeln und Reihenfolgen - der Offline-Test vergleicht mit den Etappe-2-Werten.
    //  Unterschiede zum Werkzeug: (1) kein Zwischenspeicher fuer Hoererbaeume - der Aufrufer (Arbeitsfaden) haelt den Baum;
    //  (2) Zeitbudget je Quelle (BudgetMs, 0 = aus); (3) Wald und Gelaendeprofil fehlen noch (Factory, Labs, Labyrinth haben beides
    //  nicht); (4) Daemmung mit den Standard-Dichten (nicht den F12-Werten), wie das Werkzeug.
    //  Fadensicherheit: Netz, BVH, Portale werden nur gelesen. Eine Suche-Instanz gehoert genau einem Faden.
    //  0.8.8 Livetest-Fix (Factory 10.11.2026, Bericht Wegsuche/BERICHT-LIVETEST-FACTORY.md), je Schalter abschaltbar (alle aus =
    //  genau Etappe 2, der Offline-Test prueft das gegen die Bezugswerte):
    //   KettenDeckel  Mehrfachbeugung begrenzt (Bewerten): Summe der UTD-Kanten hoechstens wie ISO 9613-2 (dicker Schirm, C3, Deckel
    //                 20/25 dB). Vorher -100...-170 dB fuer Wege mit 8-10 Kanten durch offene Hallen (Weg 14 % ueber der Luftlinie).
    //   FugeAn        Tueren mit Fuge als parallelem Durchlass (TuerKern.MitFuge): vorher -52 dB @4 kHz fuer ein Blatt.
    //   FreistellenAn Quell-/Hoererpunkt im Koerper (Lampengehaeuse, Decke) auf die naechste Aussenseite schieben (Freistellen).
    //   Fortsetzen    Zeitbudget vor dem ersten Weg = Unterbrechung, kein "0 Wege" (Stand, Weiter).
    // ------------------------------------------------------------------------------------------
    //  Weg A (Mono-Leistung, Bericht Wegsuche/BERICHT-MONO-LEISTUNG.md): Hoererbaum auf flachen Kanten mit vorab gerechneter Laenge
    //  inkl. Tuerstrafe (Wegenetz.CW), Heap als ulong-Feld, Zeiger in den Schleifen; Baum zuerst bis StartRadius (40 m) und bei Bedarf
    //  weiter (Erweitern, exakt: Kandidaten mit Gesamtlaenge bis Lmin + RFertig sind vollstaendig und richtig sortiert); Kandidaten aus
    //  flachen Knotenbloecken; Kandidatenliste als Heap statt Sortieren; keine Delegaten/LINQ und kaum Speicheranforderungen im Takt.
    //  Mehrere Suche-Instanzen (Faeden) teilen Netz, BVH, Portale und den Tuerzustand (Gemeinsam); jede hat eigene Puffer.
    // ------------------------------------------------------------------------------------------
    /// <summary>Weg A: was alle Suche-Instanzen (Faeden) eines Arbeiters teilen. Zu wird nur geschrieben, wenn keine Suche laeuft.</summary>
    internal sealed class Gemeinsam
    {
        public bool[] Zu; public int[] TuerGruppe; public double[] TuerDicke; public float[] KoerperDiag;
        public bool KettenDeckel = true, FugeAn = true, FreistellenAn = true, RohrAn = true, TuerenAn = true;
        /// <summary>Raid-3-Fix: der 20/25-dB-Deckel der Mehrfachbeugung (ISO 9613-2) nur fuer Wege, die durchs Freie gehen (siehe MehrfachGrenze).</summary>
        public bool DeckelNurDraussen = true;
        public double BaumMax = 80, StartRadius = 40, TuerStrafe = 1;
        /// <summary>wegsuche-use (N3): so weit (m Netzlaenge) darf eine Suche, die bis BaumMax KEINEN Weg fand, den Baum fuer sich weiterrechnen
        /// (Factory ist ~200 m lang). Vorher endete jede Suche an BaumMax: ab 100-120 m Quellabstand kam nie ein Weg (Use-Raids: 39 von 39),
        /// der Pegel sprang an dieser Grenze vom Weg auf den Raumgraphen. Hintergrund und Wege mit Ergebnis bleiben bei BaumMax.</summary>
        public double BaumFern = 250;
        /// <summary>wegsuche-laut (H1): Kandidaten nach Pegel-Schaetzung statt nach Laenge, Abbruch ueber die dB-Schranke (aus = Etappe 2).</summary>
        public bool PegelOrdnung = true;
        /// <summary>wegsuche-use: Gang mit vollem fruehem Spiegelquellen-Gewinn und ASHRAE-Kruemmer bis 3 dB (aus = Etappe 2: Ordnung 3, 4 dB).</summary>
        public bool RohrFrueh = true;

        public Gemeinsam(WegDaten d)
        {
            Zu = (bool[])d.PZu.Clone(); TuerGruppe = (int[])d.PGruppe.Clone(); TuerDicke = (double[])d.PDicke.Clone();
            // Diagonale der Huelle je Koerper (Freistellen: kleine Koerper wie Lampenglas gehoeren zur Quelle)
            int nk = d.KoGruppe.Length; var mn = new float[3 * nk]; var mx = new float[3 * nk];
            for (int i = 0; i < 3 * nk; i++) { mn[i] = float.MaxValue; mx[i] = float.MinValue; }
            var P = d.TriP; var TK = d.TriK;
            for (int tri = 0; tri < TK.Length; tri++)
            {
                int k = TK[tri]; if (k < 0 || k >= nk) continue;
                for (int j = 0; j < 9; j++) { int a = 3 * k + j % 3; float v = P[9 * tri + j]; if (v < mn[a]) mn[a] = v; if (v > mx[a]) mx[a] = v; }
            }
            KoerperDiag = new float[nk];
            for (int k = 0; k < nk; k++) { float dx = mx[3 * k] - mn[3 * k], dy = mx[3 * k + 1] - mn[3 * k + 1], dz = mx[3 * k + 2] - mn[3 * k + 2]; KoerperDiag[k] = dx < 0 ? 0 : (float)Math.Sqrt(dx * dx + dy * dy + dz * dz); }
        }
    }

    internal sealed unsafe class Suche
    {
        public static readonly double[] F = { 250, 1000, 4000 };
        public readonly Wegenetz n; public readonly Bvh bvh; public readonly PortalTest pt; public readonly Gemeinsam G;
        public bool[] Zu => G.Zu; public int[] TuerGruppe => G.TuerGruppe; public double[] TuerDicke => G.TuerDicke;
        public readonly float[] KoerperDicke; public readonly byte[] KoGruppe, KoTerrain;
        public bool RohrAn { get => G.RohrAn; set => G.RohrAn = value; }
        public bool TuerenAn { get => G.TuerenAn; set => G.TuerenAn = value; }
        public double TuerStrafe { get => G.TuerStrafe; set => G.TuerStrafe = value; }
        public double SchrankeDb = 20;
        public bool KettenDeckel { get => G.KettenDeckel; set => G.KettenDeckel = value; }   // 0.8.8 Livetest-Fix (siehe Kopf)
        public bool DeckelNurDraussen { get => G.DeckelNurDraussen; set => G.DeckelNurDraussen = value; }   // Raid-3-Fix
        /// <summary>Raid-3-Fix: Raeume des Spiels (gleiche Instanz wie der Platz dieser Suche, nur lesend: RaumModell.Lage). null = unbekannt.</summary>
        public RaumModell Raum;
        public int DOhneDeckel;   // Diagnose: Wege drinnen, deren Beugung ueber 25 dB lag (Deckel nicht angewandt)
        public bool FugeAn { get => G.FugeAn; set => G.FugeAn = value; }
        public bool RohrFrueh { get => G.RohrFrueh; set => G.RohrFrueh = value; }
        public bool FreistellenAn { get => G.FreistellenAn; set => G.FreistellenAn = value; }
        public double TempC = 15, Feuchte = 70;
        // wegsuche-use: Luftdaempfung (dB/m) je Band, gemerkt fuer TempC/Feuchte (gleiche Funktion, gleiche Werte - nur nicht je Weg neu)
        readonly double[] luftDb = new double[3]; double luftT = double.NaN, luftF = double.NaN;
        double Luft(int b)
        {
            if (TempC != luftT || Feuchte != luftF) { for (int i = 0; i < 3; i++) luftDb[i] = Iso9613.Alpha(F[i], TempC, Iso9613.Bezugsdruck, Feuchte); luftT = TempC; luftF = Feuchte; }
            return luftDb[b];
        }
        public double BaumMax { get => G.BaumMax; set => G.BaumMax = value; }
        public double BaumAnteil = 0.6; public int MaxPruef = 3000;
        public double BudgetMs;          // je WegeBaum-Aufruf (0 = ohne Grenze)
        // Messwerte der letzten Suche
        public int Strahlen, Gezogen, Doppelt, RohrGeteilt; public double MsSuche, MsVerfeinern, MsBewerten; public bool QuelleKalt, Abgebrochen;
        // Weg A: Zeitanteile (Summen seit dem letzten Abholen, Diagnose)
        public double TKandidaten, TSuche, TVerfeinern, TBewerten, TDurchgang, TErweitern, TFrei;

        public Suche(WegDaten d, Wegenetz netz, Bvh b, PortalTest t) : this(d, netz, b, t, new Gemeinsam(d)) { }
        public Suche(WegDaten d, Wegenetz netz, Bvh b, PortalTest t, Gemeinsam g)
        {
            n = netz; bvh = b; pt = t; G = g;
            KoerperDicke = d.KoDicke; KoGruppe = d.KoGruppe; KoTerrain = d.KoTerrain;
            mark = new ushort[netz.K.Length];
        }
        public float[] KoerperDiag => G.KoerperDiag;

        public void CacheLeeren() => quellSicht.Clear();

        // ================================================================ Kandidaten im Radius (ohne Strahl)
        public struct Kand { public int K; public byte S; public float L; public Kand(int k, byte s, float l) { K = k; S = s; L = l; } }

        Kand[] kandBuf = new Kand[1 << 14]; int kandN;

        /// <summary>Kandidaten (Kompatibilitaet: neue Liste). Intern: KandidatenPuffer.</summary>
        public List<Kand> Kandidaten(V p) { int c = KandidatenPuffer(p); var l = new List<Kand>(c); for (int i = 0; i < c; i++) l.Add(kandBuf[i]); return l; }

        /// <summary>Weg A: sichtbare Seite + Sonde je Netzknoten im Radius aus den flachen Bloecken (gleiche Liste und Reihenfolge wie bisher).
        /// Ergebnis in kandBuf[0..Anzahl).</summary>
        int KandidatenPuffer(V p)
        {
            var sw = Stopwatch.StartNew();
            int c = 0; double tol = n.Tol;
            double rmax = n.Gel != null ? Math.Max(n.R, n.RGelaende) : n.R, rn = n.R;
            int r = (int)Math.Ceiling(rmax / Wegenetz.Zelle); Wegenetz.Z3(p, out int zx, out int zy, out int zz);
            for (int x = zx - r; x <= zx + r; x++) for (int y = zy - r; y <= zy + r; y++) for (int w = zz - r; w <= zz + r; w++)
            {
                if (!n.BlkGitter.TryGetValue(Wegenetz.Schluessel(x, y, w), out long be)) continue;
                int anf = (int)(be >> 32), anz = (int)(uint)be;
                if (c + anz > kandBuf.Length) Array.Resize(ref kandBuf, Math.Max(2 * kandBuf.Length, c + anz));
                fixed (KnotenBlock* K0 = n.Blk) fixed (Kand* aus = kandBuf)
                {
                    KnotenBlock* k = K0 + anf, ke = k + anz;
                    for (; k < ke; k++)
                    {
                        double dx = p.X - k->px, dy = p.Y - k->py, dz = p.Z - k->pz; double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        int fl = k->flags;
                        if (len > ((fl & 4) != 0 ? rmax : rn) || len < 0.05) continue;
                        if ((fl & 3) == 0) { double inv = 1 / len, ex = dx * inv, ey = dy * inv, ez = dz * inv; if (!(ex * k->n1x + ey * k->n1y + ez * k->n1z > 0.02 || ex * k->n2x + ey * k->n2y + ez * k->n2z > 0.02)) continue; }
                        int sd = 2;
                        if ((fl & 2) == 0)
                        {
                            double dt = dx * k->tx + dy * k->ty + dz * k->tz; double qx = dx - k->tx * dt, qy = dy - k->ty * dt, qz = dz - k->tz * dt; double l2 = qx * qx + qy * qy + qz * qz, L2 = dx * dx + dy * dy + dz * dz;
                            if (!(l2 < 1e-4 * L2 || l2 < 1e-10))
                            {
                                double inv = 1 / Math.Sqrt(l2), a = (qx * k->n1x + qy * k->n1y + qz * k->n1z) * inv, b = (qx * k->n2x + qy * k->n2y + qz * k->n2z) * inv;
                                if (a > tol && b > tol) sd = 3; else if (a < -tol && b < -tol) sd = 4; else if (a > tol && b < -tol) sd = 0; else if (b > tol && a < -tol) sd = 1;
                            }
                        }
                        if (n.Straff && sd >= 3 && !n.SondeOk(k->idx, new V(dx, dy, dz))) continue; if (sd > 2) sd = 2;
                        aus[c++] = new Kand(k->idx, (byte)sd, (float)len);
                    }
                }
            }
            kandN = c; TKandidaten += sw.Elapsed.TotalMilliseconds;
            return c;
        }

        /// <summary>Strafe fuer eine Kante mit geschlossener Tuer: Laengen-Aequivalent der Daemmung (Suche2.KantenStrafe). Weg A: steckt
        /// vorab in Wegenetz.CW (StrafeAus fuer eine Portal-Kante aus ihren Tueren).</summary>
        public double StrafeAus(int pkante, double[] tuerDb0)
        {
            if (!TuerenAn) return 0;
            double x = 0; var zu = Zu;
            for (int j = n.PkTOff[pkante]; j < n.PkTOff[pkante + 1]; j++) { int p = n.PkT[j]; if (zu[p]) x += tuerDb0[p]; }
            const double d0 = 10;
            return x <= 0 ? 0 : Math.Min(500, d0 * (Math.Pow(10, x / 20) - 1)) * TuerStrafe;
        }

        // ================================================================ Hoererbaum
        public sealed class Baum
        {
            public float[] D; public int[] Vor; public V H; public double Ms, MsStrahlen, MsDijkstra, MsErweitern; public int Strahlen, Gezogen, Erweiterungen;
            public int Tuerstand;   // Stand der Tuerzustaende beim Bau (Aufrufer)
            public double HWeg;     // Verschiebung des Hoererpunkts aus einem Koerper (H = verschobener Punkt)
            public int Gen;         // zaehlt jeden Neubau in diesen Feldern (Wiederverwendung)
            public long Zelle;      // 1-m-Zelle des Hoerers (Aufrufer)
            public bool Vorgebaut, Benutzt;   // Raid-2-Fix (7): vom Vorbau vorab gebaut / als Hoererbaum benutzt (Diagnose: ungenutzte Vorbauten)
            internal ulong[] Heap = new ulong[1 << 16]; internal int HN;
            /// <summary>Alle Knoten mit D bis hierher sind endgueltig. Voll = bis BaumMax gerechnet (dann gelten auch die vorlaeufigen D wie im Original).</summary>
            public float RFertig; public bool Voll;
            internal readonly object Sperre = new object();
            /// <summary>wegsuche-use (7a): Zahl der laufenden Suchen, die diesen (nicht mehr aktuellen) Baum gerade lesen - solange &gt; 0 kommt er nicht in den Pool.</summary>
            internal int Leser;
        }

        /// <summary>Dijkstra vom Hoerer ueber das Netz bis StartRadius (Suche2.HoererBaumBauen; weiter mit Erweitern). puffer = alter Baum
        /// zum Wiederverwenden der Felder (null = neu).</summary>
        public Baum HoererBaumBauen(V h, Baum puffer = null)
        {
            var sw = Stopwatch.StartNew(); int c = n.K.Length;
            var b = puffer ?? new Baum { D = new float[c], Vor = new int[c] };
            lock (b.Sperre) return BauenUnterSperre(b, h, sw, c);
        }

        Baum BauenUnterSperre(Baum b, V h, Stopwatch sw, int c)
        {
            b.H = h; b.HN = 0; b.RFertig = 0; b.Voll = false; b.Gen++; b.Gezogen = 0; b.Erweiterungen = 0; b.MsErweitern = 0;
            fixed (float* D = b.D) fixed (int* Vor = b.Vor) for (int i = 0; i < c; i++) { D[i] = float.MaxValue; Vor[i] = -2; }
            int kn = KandidatenPuffer(h); int strahlen = 0;
            for (int i = 0; i < kn; i++)
            {
                var kd = kandBuf[i]; strahlen++;
                if (!bvh.Frei(h, n.P[kd.K], 0.01f)) continue;
                if (kd.L < b.D[kd.K]) { b.D[kd.K] = kd.L; b.Vor[kd.K] = -1; HeapRein(b, PHeap.Pack(kd.L, kd.K)); }
            }
            b.MsStrahlen = sw.Elapsed.TotalMilliseconds;
            Dijkstra(b, (float)Math.Min(G.StartRadius, G.BaumMax));
            b.Ms = sw.Elapsed.TotalMilliseconds; b.MsDijkstra = b.Ms - b.MsStrahlen; b.Strahlen = kn;
            return b;
        }

        static void HeapRein(Baum b, ulong e) { if (b.HN == b.Heap.Length) Array.Resize(ref b.Heap, 2 * b.Heap.Length); fixed (ulong* hp = b.Heap) PHeap.Rein(hp, ref b.HN, e); }

        /// <summary>Weg A: Baum weiterrechnen, bis der naechste Heap-Eintrag ueber r liegt (Eintraege darueber bleiben im Heap). Aufrufer
        /// haelt b.Sperre, wenn andere Faeden denselben Baum lesen.</summary>
        public void Erweitern(Baum b, double r, long bisTicks = 0)
        {
            if (b.Voll || r <= b.RFertig) return;
            var sw = Stopwatch.StartNew();
            Dijkstra(b, (float)Math.Min(r, G.BaumMax), bisTicks);
            b.Erweiterungen++; double ms = sw.Elapsed.TotalMilliseconds; b.MsErweitern += ms; b.Ms += ms; TErweitern += ms;
        }

        /// <summary>Raid-2-Fix: bisTicks (Stopwatch.GetTimestamp, 0 = ohne) begrenzt die Rechenzeit - das Weiterrechnen des Baums war ohne
        /// Zeitgrenze (31 s "extended" im Raid, Quellen bis 136 ms, Takte bis 172 ms). Bei Zeitablauf gilt der Baum bis knapp unter den
        /// kleinsten Schluessel im Heap als fertig (alle kleineren D sind entnommen und weitergereicht = endgueltig).</summary>
        void Dijkstra(Baum b, float rho, long bisTicks = 0)
        {
            int gez = 0; bool zeit = false;
            while (true)
            {
                bool voll = false;
                fixed (int* off = n.COff, nb = n.CNb, Vor = b.Vor) fixed (float* W = n.CW, D = b.D) fixed (ulong* hp = b.Heap)
                {
                    int hn = b.HN, cap = b.Heap.Length;
                    while (hn > 0)
                    {
                        ulong e = hp[0]; float du = PHeap.Key(e);
                        if (du > rho) break;
                        if (bisTicks != 0 && (gez & 127) == 127 && Stopwatch.GetTimestamp() > bisTicks) { zeit = true; break; }
                        int u = PHeap.Wert(e);
                        int a = off[u], z = off[u + 1];
                        if (hn + (z - a) > cap) { voll = true; break; }   // Heap vergroessern, dann weiter
                        PHeap.Raus(hp, ref hn);
                        if (du > D[u]) continue;
                        gez++;
                        for (int k = a; k < z; k++)
                        {
                            int j = nb[k] & int.MaxValue;
                            double nd = du + (double)W[k];
                            if (nd >= D[j]) continue;
                            float nf = (float)nd; D[j] = nf; Vor[j] = u; PHeap.Rein(hp, ref hn, PHeap.Pack(nf, j));
                        }
                    }
                    b.HN = hn;
                }
                if (!voll) break;
                Array.Resize(ref b.Heap, 2 * b.Heap.Length);
            }
            b.Gezogen += gez;
            if (zeit)
            {
                float top = b.HN > 0 ? PHeap.Key(b.Heap[0]) : rho;
                float rf = Math.Min(rho, top * (1 - 1e-6f) - 1e-4f);
                if (rf > b.RFertig) System.Threading.Volatile.Write(ref b.RFertig, rf);
                return;
            }
            if (rho >= G.BaumMax) { b.Voll = true; System.Threading.Volatile.Write(ref b.RFertig, rho); }
            else System.Threading.Volatile.Write(ref b.RFertig, Math.Max(b.RFertig, rho));
        }

        /// <summary>Nutzbar: D endgueltig (bis RFertig), bei vollem Baum jedes erreichte D (wie das Original bis BaumMax).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static bool Nutzbar(float d, float rFertig, bool voll) => voll ? d < float.MaxValue : d <= rFertig;

        // ================================================================ Wege ueber den Hoererbaum (Suche2.WegeBaum)
        static double Ankunft(Weg a, Weg b)
        {
            V x = a.Punkte[a.Punkte.Count - 2] - a.Punkte[a.Punkte.Count - 1], y = b.Punkte[b.Punkte.Count - 2] - b.Punkte[b.Punkte.Count - 1];
            return Math.Acos(H.Clamp(V.Dot(x.Norm(), y.Norm()), -1, 1)) * 180 / Math.PI;
        }
        readonly Dictionary<long, Dictionary<int, bool>> quellSicht = new Dictionary<long, Dictionary<int, bool>>();
        static long Zelle1(V p) => Wegenetz.Schluessel((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));

        /// <summary>0.8.8: Stand einer Hoererbaum-Suche (fortsetzbar). Unterbrochen = Zeitbudget vor dem ersten Weg (Arbeiter setzt im
        /// naechsten Takt fort); Fertig = alle Abbruchbedingungen von Suche2.WegeBaum geprueft oder Budget nach dem ersten Weg.
        /// Weg A: Kandidaten als Heap (Gesamtlaenge, Knoten); solche jenseits des fertigen Baums warten in Rest, bis der Baum sie erreicht.</summary>
        public sealed class Stand
        {
            public V Q, H; public Baum B; public int K; public double D0;
            internal ulong[] Heap = new ulong[1024]; internal int HN;
            internal int[] RestK = new int[256]; internal float[] RestL = new float[256]; internal int RestN; internal float LMin;
            internal int[] FernK = new int[64]; internal float[] FernL = new float[64]; internal int FernN; internal bool FernFertig;   // wegsuche-use (N3): Kandidaten jenseits des vollen Baums
            internal Dictionary<int, bool> Memo;
            public readonly double[] E = new double[3];
            public readonly List<Weg> Wege = new List<Weg>(); internal readonly List<int[]> Gewaehlt = new List<int[]>();
            internal readonly List<Weg> GewWeg = new List<Weg>();   // wegsuche-use: der gepruefte Weg je gewaehlter Knotenmenge (null = doppelt, nicht behalten)
            public int Pruef, Strahlen, Gezogen, Doppelt, Teile; public bool Fertig, Abgebrochen, Kalt; public double MsSuche, MsVerfeinern, MsBewerten;
            // wegsuche-laut (H1): sichtbare Kandidaten nach geschaetztem Verlust (1 kHz), Gescannt = aus dem Laengen-Heap geholt
            internal ulong[] BHeap = new ulong[256]; internal int BN; public int Gescannt; public double MsSchaetzen;
            /// <summary>wegsuche-laut (M1): Hoerer H ist nicht der Baumpunkt (Zellraster) - das letzte Wegstueck gilt ab H.</summary>
            internal bool HAnders;
            /// <summary>M1: Kandidaten, deren Kette der echte Hoerer an keinem Knoten sieht (Baum gehoert zu einem anderen Ort der Zelle).</summary>
            public int AmHoererVerworfen;
            /// <summary>wegsuche-use (Nachlauf): Aufrufe nach dem ersten Weg, die am Budget endeten und fortgesetzt werden (Zwischenstand gilt).</summary>
            public int NachTeile; public bool Zwischen;
        }

        public List<Weg> WegeBaum(V q, V h, int k, Weg durchgang, Baum b)
        {
            var s = Beginnen(q, h, k, durchgang, b);
            while (!Weiter(s, BudgetMs)) { }
            return s.Wege;
        }

        static void StandRein(Stand s, ulong e) { if (s.HN == s.Heap.Length) Array.Resize(ref s.Heap, 2 * s.Heap.Length); fixed (ulong* hp = s.Heap) PHeap.Rein(hp, ref s.HN, e); }

        /// <summary>Kandidaten der Quelle in den Heap (Suche2.WegeBaum, erster Teil: Gesamtlaenge Quelle -> Kandidat -> Hoerer).</summary>
        public Stand Beginnen(V q, V h, int k, Weg durchgang, Baum b)
        {
            var sw = Stopwatch.StartNew();
            var s = new Stand { Q = q, H = h, B = b, K = k };
            int c = KandidatenPuffer(q);
            long qkey = Zelle1(q);
            if (!quellSicht.TryGetValue(qkey, out var qmemo)) { if (quellSicht.Count > 256) quellSicht.Clear(); quellSicht[qkey] = qmemo = new Dictionary<int, bool>(); s.Kalt = true; }
            s.Memo = qmemo;
            float rf = System.Threading.Volatile.Read(ref b.RFertig); bool voll = b.Voll; float lmin = float.MaxValue;
            if (s.Heap.Length < c) s.Heap = new ulong[Math.Max(1024, c)];
            fixed (ulong* hp = s.Heap)
            {
                int hn = 0;
                for (int i = 0; i < c; i++)
                {
                    var kd = kandBuf[i]; float dk = b.D[kd.K];
                    if (kd.L < lmin) lmin = kd.L;
                    if (Nutzbar(dk, rf, voll)) PHeap.Rein(hp, ref hn, PHeap.Pack(kd.L + dk, kd.K));
                    else if (!voll)
                    {
                        if (s.RestN == s.RestK.Length) { Array.Resize(ref s.RestK, 2 * s.RestN); Array.Resize(ref s.RestL, 2 * s.RestN); }
                        s.RestK[s.RestN] = kd.K; s.RestL[s.RestN] = kd.L; s.RestN++;
                    }
                    else FernRein(s, kd.K, kd.L);
                }
                s.HN = hn;
            }
            s.LMin = lmin;
            s.HAnders = V.Abstand(h, b.H) > 1e-6;
            s.D0 = Math.Max(V.Abstand(q, h), 0.05);
            for (int bb = 0; bb < 3; bb++) s.E[bb] = durchgang == null ? 0 : Math.Pow(10, durchgang.Db[bb] / 10);
            s.MsSuche = sw.Elapsed.TotalMilliseconds;
            return s;
        }

        /// <summary>Weg A: Baum so weit rechnen, dass alle Kandidaten mit Gesamtlaenge bis t erfasst sind (r = t - LMin), und die Wartenden
        /// mit endgueltigem D in den Heap holen. Mehrere Faeden: unter der Sperre des Baums.</summary>
        void Nachladen(Stand s, float t, long bisTicks)
        {
            var b = s.B;
            double r = Math.Max(System.Threading.Volatile.Read(ref b.RFertig) + 20.0, t - s.LMin + 1.0);
            lock (b.Sperre) Erweitern(b, r, bisTicks);
            float rf = System.Threading.Volatile.Read(ref b.RFertig); bool voll = b.Voll; int w = 0;
            for (int i = 0; i < s.RestN; i++)
            {
                float dk = b.D[s.RestK[i]];
                if (Nutzbar(dk, rf, voll)) StandRein(s, PHeap.Pack(s.RestL[i] + dk, s.RestK[i]));
                else if (!voll) { s.RestK[w] = s.RestK[i]; s.RestL[w] = s.RestL[i]; w++; }
                else FernRein(s, s.RestK[i], s.RestL[i]);
            }
            s.RestN = w;
        }

        static void FernRein(Stand s, int k, float l)
        {
            if (s.FernN == s.FernK.Length) { Array.Resize(ref s.FernK, 2 * s.FernN); Array.Resize(ref s.FernL, 2 * s.FernN); }
            s.FernK[s.FernN] = k; s.FernL[s.FernN] = l; s.FernN++;
        }

        /// <summary>wegsuche-use (N3): Suche ohne Weg, Baum voll (BaumMax), aber Kandidaten, die er nicht erreicht hat: den Baum in Schritten von
        /// 20 m bis BaumFern weiterrechnen (dieselbe Dijkstra-Rechnung, unter der Sperre, bis zur Frist) und die nun erreichten Kandidaten in
        /// den Laengen-Heap holen. false = nichts mehr zu holen.</summary>
        bool FernNachladen(Stand s, long bisTicks)
        {
            var b = s.B; if (s.FernFertig || s.FernN == 0) return false;
            float rf0 = System.Threading.Volatile.Read(ref b.RFertig);
            if (rf0 >= G.BaumFern - 1e-3) { s.FernFertig = true; }
            else lock (b.Sperre) { if (b.RFertig < rf0 + 20) { Dijkstra(b, (float)Math.Min(G.BaumFern, b.RFertig + 20.0), bisTicks); b.Erweiterungen++; } }
            float rf = System.Threading.Volatile.Read(ref b.RFertig); int w = 0, neu = 0;
            for (int i = 0; i < s.FernN; i++)
            {
                float dk = b.D[s.FernK[i]];
                if (dk < float.MaxValue) { StandRein(s, PHeap.Pack(s.FernL[i] + dk, s.FernK[i])); neu++; }
                else { s.FernK[w] = s.FernK[i]; s.FernL[w] = s.FernL[i]; w++; }
            }
            s.FernN = w; DFern += neu;
            if (rf >= G.BaumFern - 1e-3 && neu == 0) s.FernFertig = true;
            return neu > 0 || !s.FernFertig;
        }
        public int DFern;   // Diagnose: Kandidaten, die erst die Fern-Erweiterung erreicht hat

        readonly ushort[] mark; readonly List<int> markiert = new List<int>(), knotenTmp = new List<int>(64);

        /// <summary>Suche fortsetzen (Suche2.WegeBaum, Schleife). budgetMs (0 = ohne Grenze) gilt je Aufruf: vor dem ersten Weg 4x
        /// (danach Unterbrechung, false), nach dem ersten Weg 1x (danach fertig, Abgebrochen). true = fertig (Wege in s.Wege).</summary>
        public bool Weiter(Stand s, double budgetMs)
        {
            if (G.PegelOrdnung) return WeiterPegel(s, budgetMs);
            if (s.Fertig) return true;
            var sw = Stopwatch.StartNew(); s.Teile++;
            Strahlen = s.Strahlen; Gezogen = s.Gezogen; Doppelt = s.Doppelt; MsVerfeinern = MsBewerten = 0; Abgebrochen = false; QuelleKalt = s.Kalt;
            var q = s.Q; var h = s.H; var b = s.B; int k = s.K; double d0 = s.D0; var e = s.E; var wege = s.Wege; var gewaehlt = s.Gewaehlt; var qmemo = s.Memo;
            // gewaehlte Knotenmengen als Bits (Satz i = Bit i), am Ende wieder geloescht
            for (int gi = 0; gi < gewaehlt.Count && gi < 16; gi++) foreach (int x in gewaehlt[gi]) { if (mark[x] == 0) markiert.Add(x); mark[x] |= (ushort)(1 << gi); }
            bool unterbrochen = false;
            try
            {
                long start = Stopwatch.GetTimestamp();
                while (true)
                {
                    // Zeitbudget: nach dem ersten Weg hart; vor dem ersten 4x, dann Unterbrechung (0.8.8: fortsetzen statt "0 Wege").
                    // Raid-2-Fix: VOR dem Weiterrechnen des Baums geprueft, und das Weiterrechnen selbst endet an derselben Frist.
                    long bis = budgetMs > 0 ? start + (long)((wege.Count > 0 ? budgetMs : 4 * budgetMs) * Stopwatch.Frequency / 1000.0) : 0;
                    bool ueber = budgetMs > 0 && Stopwatch.GetTimestamp() > bis;
                    if (s.HN == 0)
                    {
                        if (s.RestN == 0 || b.Voll) break;
                        if (ueber) { if (wege.Count > 0) Abgebrochen = true; else unterbrochen = true; break; }
                        Nachladen(s, float.MaxValue, bis); if (s.HN == 0 && !(budgetMs > 0 && Stopwatch.GetTimestamp() > bis)) break; continue;
                    }
                    ulong top = s.Heap[0]; float t = PHeap.Key(top); int u = PHeap.Wert(top);
                    // Kandidaten jenseits des fertigen Baums koennten kuerzer sein: erst den Baum weiterrechnen
                    if (s.RestN > 0 && !b.Voll && t > s.LMin + System.Threading.Volatile.Read(ref b.RFertig))
                    {
                        if (ueber) { if (wege.Count > 0) Abgebrochen = true; else unterbrochen = true; break; }
                        Nachladen(s, t, bis); continue;
                    }
                    if (wege.Count >= k || s.Pruef > MaxPruef || (wege.Count > 0 && s.Pruef > MaxPruef / 10) || Doppelt > 6) break;
                    if (ueber) { if (wege.Count > 0) Abgebrochen = true; else unterbrochen = true; break; }
                    double sum0 = 10 * Math.Log10(Math.Max(e[0], 1e-12));
                    if (wege.Count > 0 && -20 * Math.Log10(t / d0) < sum0 - SchrankeDb) break;
                    fixed (ulong* hp = s.Heap) PHeap.Raus(hp, ref s.HN);
                    var knoten = knotenTmp; knoten.Clear(); for (int x = u; x >= 0; x = b.Vor[x]) knoten.Add(x);
                    if (s.HAnders && !KetteAmHoerer(knoten, h)) { s.AmHoererVerworfen++; continue; }   // wegsuche-laut (M1)
                    int ng = Math.Min(gewaehlt.Count, 16);
                    if (ng > 0)
                    {
                        double ges = 0; for (int i = 0; i + 1 < knoten.Count; i++) ges += n.Laenge(knoten[i], knoten[i + 1]);
                        double max = 0; bool variante = false;
                        for (int gi = 0; gi < ng; gi++)
                        {
                            int bit = 1 << gi; double gem = 0;
                            for (int i = 0; i + 1 < knoten.Count; i++) if ((mark[knoten[i]] & bit) != 0 && (mark[knoten[i + 1]] & bit) != 0) gem += n.Laenge(knoten[i], knoten[i + 1]);
                            max = Math.Max(max, ges > 0 ? gem / ges : 1);
                            if ((mark[u] & bit) != 0) variante = true;
                        }
                        if (max > BaumAnteil) continue;
                        if (variante) continue;
                    }
                    s.Pruef++; Gezogen++;
                    if (!qmemo.TryGetValue(u, out bool sieht)) { Strahlen++; sieht = bvh.Frei(q, n.P[u], 0.01f); qmemo[u] = sieht; }
                    if (!sieht) continue;
                    var w = new Weg(); w.Knoten.AddRange(knoten);
                    { double r0 = V.Abstand(q, n.P[w.Knoten[0]]) + V.Abstand(n.P[w.Knoten[w.Knoten.Count - 1]], h); for (int i = 0; i + 1 < w.Knoten.Count; i++) r0 += V.Abstand(n.P[w.Knoten[i]], n.P[w.Knoten[i + 1]]); w.Roh = r0; }
                    V an = n.P[w.Knoten[w.Knoten.Count - 1]] - h;
                    bool gleich = false;
                    foreach (var x in wege)
                        if (Math.Abs(x.Roh - w.Roh) < Math.Max(0.5, 0.05 * w.Roh) && Math.Acos(H.Clamp(V.Dot((n.P[x.Knoten[x.Knoten.Count - 1]] - h).Norm(), an.Norm()), -1, 1)) * 180 / Math.PI < 15) { gleich = true; break; }
                    if (gleich) { Doppelt++; Waehlen(s, w.Knoten); continue; }
                    var tv = Stopwatch.StartNew(); Verfeinern(w, q, h); MsVerfeinern += tv.Elapsed.TotalMilliseconds;
                    tv.Reset(); tv.Start(); Bewerten(w, q, h); MsBewerten += tv.Elapsed.TotalMilliseconds;
                    Waehlen(s, w.Knoten);
                    gleich = false;
                    foreach (var x in wege) if (Math.Abs(x.Laenge - w.Laenge) < Math.Max(0.5, 0.05 * w.Laenge) && Ankunft(x, w) < 15) { gleich = true; break; }
                    if (gleich) { Doppelt++; continue; }
                    wege.Add(w);
                    for (int bb = 0; bb < 3; bb++) e[bb] += Math.Pow(10, w.Db[bb] / 10);
                }
            }
            finally { foreach (int x in markiert) mark[x] = 0; markiert.Clear(); }
            s.MsVerfeinern += MsVerfeinern; s.MsBewerten += MsBewerten; double msS = sw.Elapsed.TotalMilliseconds - MsVerfeinern - MsBewerten; s.MsSuche += msS;
            TSuche += msS; TVerfeinern += MsVerfeinern; TBewerten += MsBewerten;
            s.Strahlen = Strahlen; s.Gezogen = Gezogen; s.Doppelt = Doppelt; s.Gescannt = s.Pruef; LetzteGescannt = s.Pruef;
            if (unterbrochen) return false;
            s.Abgebrochen = Abgebrochen; s.Fertig = true;
            MsSuche = s.MsSuche; MsVerfeinern = s.MsVerfeinern; MsBewerten = s.MsBewerten;
            RohrTeilen(wege);
            return true;
        }

        // ================================================================ wegsuche-laut (H1): Suche nach geschaetztem Pegel
        /// <summary>Anteil des Zeitbudgets eines Aufrufs, in dem zuerst Kandidaten gesammelt werden (Sichtstrahl + Schaetzung), bevor
        /// die teure Pruefung (Verfeinern + Bewerten, ~10x so teuer) beginnt. Keine physikalische Groesse, sondern die Aufteilung der
        /// Rechenzeit: ohne Budget wird erst alles gesammelt.</summary>
        public double ScanAnteil = 0.5;
        /// <summary>Abbruch, wenn k Wege behalten sind und die beste Schaetzung unter dem leisesten liegt. Aus = alle Kandidaten ueber der
        /// dB-Schranke pruefen (nur fuer Bezugsrechnungen im Offline-Test).</summary>
        public bool KSchranke = true;
        /// <summary>wegsuche-use: Unsicherheit der Pegel-Schaetzung (dB), 99-%-Quantil von "geprueft minus geschaetzt" ueber 143194 sichtbare
        /// Kandidaten (wegsuche-fix/erg/m-schaetz: 8,5 dB). Eine Variante eines behaltenen Wegs wird geprueft, wenn ihre Schaetzung hoechstens
        /// so viel unter seinem gepruefter Pegel liegt.</summary>
        public double SchaetzUnsicherDb = 8.5;
        public int DErsetzt;   // Diagnose: Varianten, die einen behaltenen Weg ersetzt haben
        Weg ersetzt;
        /// <summary>wegsuche-use (Rechenzeit): nach dem ersten Weg darf eine Suche, die am Budget endet, noch so viele weitere Aufrufe (Takte)
        /// je ein Budget lang weitersuchen; dazwischen gilt ihr Zwischenstand (Arbeiter veroeffentlicht ihn). Vorher endete sie nach EINEM
        /// Budget - mit 2 ms waren 36-40 von 244 Testquellen > 6 dB zu leise, weil der lauteste Weg noch nicht gesammelt war (Messung
        /// wegsuche-use). Die Rechenzeit je Takt bleibt das Budget; die Summe ist hoechstens (1 + NachTakte) Budgets. Keine Physik, sondern die
        /// Aufteilung der Rechenzeit auf Takte (0 = wie bisher).</summary>
        public int NachTakte = 3;
        public double TSchaetzen;   // Diagnose: Zeit der Schaetzungen (Summe seit dem letzten Abholen)
        public int LetzteGescannt;  // Diagnose: Kandidaten der letzten Suche aus dem Laengen-Heap

        static void BRein(Stand s, ulong e) { if (s.BN == s.BHeap.Length) Array.Resize(ref s.BHeap, 2 * s.BHeap.Length); fixed (ulong* hp = s.BHeap) PHeap.Rein(hp, ref s.BN, e); }

        // wegsuche-laut (M1): Sicht echter Hoerer -> letzter Knoten, je Hoererort gemerkt (der Baum sah den Knoten vom Baumpunkt aus)
        V hsH = new V(double.NaN, 0, 0); readonly Dictionary<int, bool> hsMemo = new Dictionary<int, bool>();
        bool HSieht(V h, int x)
        {
            if (!(V.Abstand(h, hsH) < 1e-9)) { hsH = h; hsMemo.Clear(); }
            if (!hsMemo.TryGetValue(x, out bool ja)) { Strahlen++; ja = bvh.Frei(h, n.P[x], 0.01f); hsMemo[x] = ja; }
            return ja;
        }

        /// <summary>wegsuche-laut (M1): Kette (Quelle -> ... -> Baumpunkt) fuer den echten Hoerer h kuerzen. Der Baum sah den letzten Knoten
        /// vom Baumpunkt aus; vom Hoerer aus (bis 0,7 m daneben) gilt das letzte Stueck nur, wenn h den Knoten sieht. Sonst endet die Kette
        /// am letzten Knoten davor, den h sieht (das Stueck dahin ist frei, also ein gueltiger Weg). Sieht h auch dessen Vorgaenger, wird
        /// weiter gekuerzt (ein Baum am Hoerer haette den Knoten direkt erreicht). false = h sieht keinen Knoten der Kette.</summary>
        bool KetteAmHoerer(List<int> knoten, V h)
        {
            int j = knoten.Count - 1;
            while (j >= 0 && !HSieht(h, knoten[j])) j--;
            if (j < 0) return false;
            while (j >= 1 && HSieht(h, knoten[j - 1])) j--;
            if (j < knoten.Count - 1) knoten.RemoveRange(j + 1, knoten.Count - 1 - j);
            return true;
        }

        /// <summary>wegsuche-laut (H1): wie Weiter, aber in zwei Stufen. (1) Sammeln: Kandidaten aus dem Laengen-Heap (wie bisher, samt
        /// Weiterrechnen des Baums), Sichtstrahl Quelle -> Knoten; jeder sichtbare bekommt die Pegel-Schaetzung (SchaetzVerlust, ohne
        /// Strahl) und kommt in den Pegel-Heap. (2) Pruefen: der Kandidat mit dem kleinsten geschaetzten Verlust wird verfeinert und
        /// bewertet - wie bisher, mit denselben Doppel- und Ueberlappungsregeln. Abbruch: k Wege, Doppelt &gt; 6, oder kein gesammelter
        /// Kandidat mehr, dessen Schaetzung die Summe noch um mehr als SchrankeDb unterschreitet (1 kHz) und nichts mehr zu sammeln;
        /// die Zaehlgrenze MaxPruef/10 nach dem ersten Weg (Ursache H1) entfaellt. Mit Budget: je Aufruf zuerst ScanAnteil sammeln,
        /// dann pruefen (sammeln weiter, wenn nichts Lohnendes gesammelt ist); Zeitregeln wie Weiter (vor dem ersten Weg 4x, dann
        /// Unterbrechung; danach 1x, dann fertig).</summary>
        bool WeiterPegel(Stand s, double budgetMs)
        {
            if (s.Fertig) return true;
            var sw = Stopwatch.StartNew(); s.Teile++;
            Strahlen = s.Strahlen; Gezogen = s.Gezogen; Doppelt = s.Doppelt; MsVerfeinern = MsBewerten = 0; Abgebrochen = false; QuelleKalt = s.Kalt;
            var q = s.Q; var h = s.H; var b = s.B; int k = s.K; var e = s.E; var wege = s.Wege; var gewaehlt = s.Gewaehlt; var qmemo = s.Memo;
            for (int gi = 0; gi < gewaehlt.Count && gi < 16; gi++) foreach (int x in gewaehlt[gi]) { if (mark[x] == 0) markiert.Add(x); mark[x] |= (ushort)(1 << gi); }
            bool unterbrochen = false; double msSch = 0;
            try
            {
                // Fristen wie Weiter: ab dem ersten Weg 1x Budget ab Aufrufbeginn, davor bis 4x (dann Unterbrechung). Gesammelt wird zuerst
                // ScanAnteil des einfachen Budgets, dann geprueft - so kostet eine Suche wie bisher etwa ein Budget.
                long start = Stopwatch.GetTimestamp(), tick = (long)(budgetMs * Stopwatch.Frequency / 1000.0);
                long sammelBis = start + (long)(ScanAnteil * tick);
                while (true)
                {
                    long bis = budgetMs > 0 ? start + (wege.Count > 0 ? tick : 4 * tick) : 0;
                    long jetzt = Stopwatch.GetTimestamp();
                    bool ueber = budgetMs > 0 && jetzt > bis;
                    // Keine Zaehlgrenzen mehr fuer das Ende (k Wege, Doppelt > 6, MaxPruef/10): in der Pegel-Reihenfolge kommen nach einem Weg
                    // zuerst seine Nachbarn (gleiche Knoten, fast gleiche Schaetzung) - Doppelte haeufen sich, und Kandidaten mit zu guter
                    // Schaetzung koennten die k Plaetze fuellen. Behalten werden die k lautesten (Behalten), das Ende setzt die dB-Schranke.
                    if (s.Pruef > MaxPruef) break;
                    bool aOffen = s.HN > 0 || (s.RestN > 0 && !b.Voll);
                    // dB-Schranke auf der Schaetzung: ein Kandidat, dessen geschaetzter Pegel mehr als SchrankeDb unter der Summe liegt, hebt sie
                    // um hoechstens 10 lg(1 + 10^(-SchrankeDb/10)) (20 dB: 0,04 dB) - der Pegel-Heap ist nach der Schaetzung geordnet, also gilt
                    // das dann fuer alle gesammelten.
                    // Dazu: sind schon k Wege behalten, kann ein Kandidat, dessen Schaetzung unter dem leisesten behaltenen liegt, keinen
                    // ersetzen (Behalten) - auch dann ist die Suche fertig.
                    double sum1 = 10 * Math.Log10(Math.Max(e[1], 1e-12));
                    double best = s.BN > 0 ? -PHeap.Key(s.BHeap[0]) : double.NegativeInfinity;
                    bool bGut = s.BN > 0 && (wege.Count == 0 || (best >= sum1 - SchrankeDb && (wege.Count < k || !KSchranke || best > Leisester(wege))));
                    if (!aOffen && !bGut)
                    {
                        // wegsuche-use (N3): noch kein Weg - den Baum fuer diese Suche ueber BaumMax hinaus weiterrechnen (bis BaumFern)
                        if (wege.Count == 0 && b.Voll && !(budgetMs > 0 && jetzt > bis) && FernNachladen(s, bis)) continue;
                        break;
                    }
                    if (ueber)
                    {
                        if (wege.Count == 0) unterbrochen = true;
                        else if (s.NachTeile < NachTakte) { s.NachTeile++; s.Zwischen = true; unterbrochen = true; }   // Nachlauf: im naechsten Takt weiter
                        else Abgebrochen = true;
                        break;
                    }
                    bool sammeln = aOffen && (!bGut || budgetMs <= 0 || jetzt < sammelBis);
                    if (sammeln)
                    {
                        if (s.HN == 0) { Nachladen(s, float.MaxValue, bis); continue; }
                        ulong top = s.Heap[0]; float t = PHeap.Key(top); int u = PHeap.Wert(top);
                        if (s.RestN > 0 && !b.Voll && t > s.LMin + System.Threading.Volatile.Read(ref b.RFertig)) { Nachladen(s, t, bis); continue; }
                        fixed (ulong* hp = s.Heap) PHeap.Raus(hp, ref s.HN);
                        s.Gescannt++;
                        if (!qmemo.TryGetValue(u, out bool sieht)) { Strahlen++; sieht = bvh.Frei(q, n.P[u], 0.01f); qmemo[u] = sieht; }
                        if (!sieht) continue;
                        var kn = knotenTmp; kn.Clear(); for (int x = u; x >= 0; x = b.Vor[x]) kn.Add(x);
                        if (s.HAnders && !KetteAmHoerer(kn, h)) { s.AmHoererVerworfen++; continue; }   // M1
                        var ts = Stopwatch.StartNew(); double verlust = SchaetzVerlust(kn, q, h, 1); msSch += ts.Elapsed.TotalMilliseconds;
                        BRein(s, PHeap.Pack((float)verlust, u));
                        continue;
                    }
                    // Pruefen: der gesammelte Kandidat mit dem kleinsten geschaetzten Verlust
                    int v = PHeap.Wert(s.BHeap[0]); double schaetz = -PHeap.Key(s.BHeap[0]);
                    fixed (ulong* hp = s.BHeap) PHeap.Raus(hp, ref s.BN);
                    var knoten = knotenTmp; knoten.Clear(); for (int x = v; x >= 0; x = b.Vor[x]) knoten.Add(x);
                    if (s.HAnders) KetteAmHoerer(knoten, h);   // M1, wie beim Sammeln (Sicht gemerkt)
                    int ng = Math.Min(gewaehlt.Count, 16);
                    if (ng > 0)
                    {
                        double ges = 0; for (int i = 0; i + 1 < knoten.Count; i++) ges += n.Laenge(knoten[i], knoten[i + 1]);
                        double max = 0; bool variante = false;
                        for (int gi = 0; gi < ng; gi++)
                        {
                            int bit = 1 << gi; double gem = 0;
                            for (int i = 0; i + 1 < knoten.Count; i++) if ((mark[knoten[i]] & bit) != 0 && (mark[knoten[i + 1]] & bit) != 0) gem += n.Laenge(knoten[i], knoten[i + 1]);
                            max = Math.Max(max, ges > 0 ? gem / ges : 1);
                            if ((mark[v] & bit) != 0) variante = true;
                        }
                        if (max > BaumAnteil) continue;
                        if (variante) continue;
                    }
                    ersetzt = null;
                    s.Pruef++; Gezogen++;
                    var w = new Weg(); w.Knoten.AddRange(knoten);
                    { double r0 = V.Abstand(q, n.P[w.Knoten[0]]) + V.Abstand(n.P[w.Knoten[w.Knoten.Count - 1]], h); for (int i = 0; i + 1 < w.Knoten.Count; i++) r0 += V.Abstand(n.P[w.Knoten[i]], n.P[w.Knoten[i + 1]]); w.Roh = r0; }
                    V an = n.P[w.Knoten[w.Knoten.Count - 1]] - h;
                    bool gleich = false; Weg gleichW = null;
                    foreach (var x in wege)
                        if (Math.Abs(x.Roh - w.Roh) < Math.Max(0.5, 0.05 * w.Roh) && Math.Acos(H.Clamp(V.Dot((n.P[x.Knoten[x.Knoten.Count - 1]] - h).Norm(), an.Norm()), -1, 1)) * 180 / Math.PI < 15) { gleich = true; gleichW = x; break; }
                    // wegsuche-use: ein "gleicher" Weg (Rohlaenge und Ankunft wie ein behaltener) ist derselbe Weg in anderer Kette. In der
                    // Laengen-Reihenfolge kam die kurze, straffe Kette zuerst, in der Pegel-Reihenfolge oft eine leisere - die lautere wurde dann
                    // als "doppelt" verworfen. Jetzt wird sie geprueft, wenn ihre Schaetzung den behaltenen Weg um weniger als die gemessene
                    // Schaetzunsicherheit unterbietet, und ersetzt ihn, wenn sie lauter ist (sonst verworfen; je Weg weiter nur eine Kette). Die
                    // Ueberlappungs-Regel (BaumAnteil) bleibt: dort lohnte die Pruefung nicht (+70 % Strahlen, kein messbarer Gewinn) (Labyrinth "Kammer ueber Gang": die straffe 3-Kanten-Kette -6,0 dB
                    // galt als gleich mit der zuerst geprueften 5-Kanten-Kette -9,4 dB und wurde verworfen)
                    if (gleich && ersetzt == null && schaetz >= gleichW.Db[1] - SchaetzUnsicherDb) ersetzt = gleichW;
                    else if (gleich && ersetzt == null) { Doppelt++; Waehlen(s, w.Knoten, null); continue; }
                    var tv = Stopwatch.StartNew(); Verfeinern(w, q, h); MsVerfeinern += tv.Elapsed.TotalMilliseconds;
                    tv.Reset(); tv.Start(); Bewerten(w, q, h); MsBewerten += tv.Elapsed.TotalMilliseconds;
                    if (ersetzt != null)
                    {
                        Waehlen(s, w.Knoten, null);
                        if (w.Db[1] > ersetzt.Db[1]) { int ix = wege.IndexOf(ersetzt); for (int bb = 0; bb < 3; bb++) e[bb] += Math.Pow(10, w.Db[bb] / 10) - Math.Pow(10, ersetzt.Db[bb] / 10); wege[ix] = w; DErsetzt++; }
                        continue;
                    }
                    Waehlen(s, w.Knoten, w);
                    gleich = false;
                    foreach (var x in wege) if (Math.Abs(x.Laenge - w.Laenge) < Math.Max(0.5, 0.05 * w.Laenge) && Ankunft(x, w) < 15) { gleich = true; break; }
                    if (gleich) { Doppelt++; continue; }
                    Behalten(wege, w, k, e);
                }
            }
            finally { foreach (int x in markiert) mark[x] = 0; markiert.Clear(); }
            s.MsVerfeinern += MsVerfeinern; s.MsBewerten += MsBewerten; s.MsSchaetzen += msSch; double msS = sw.Elapsed.TotalMilliseconds - MsVerfeinern - MsBewerten - msSch; s.MsSuche += msS;
            TSuche += msS; TVerfeinern += MsVerfeinern; TBewerten += MsBewerten; TSchaetzen += msSch;
            s.Strahlen = Strahlen; s.Gezogen = Gezogen; s.Doppelt = Doppelt; LetzteGescannt = s.Gescannt;
            if (unterbrochen) return false;
            s.Abgebrochen = Abgebrochen; s.Fertig = true; s.Zwischen = false;
            MsSuche = s.MsSuche; MsVerfeinern = s.MsVerfeinern; MsBewerten = s.MsBewerten;
            RohrTeilen(wege);
            return true;
        }

        /// <summary>wegsuche-use (Nachlauf): Kopie der bisher behaltenen Wege einer unterbrochenen Suche mit geteiltem Rohr (RohrTeilen aendert
        /// die Wege, darum auf Kopien) - der Zwischenstand, solange die Suche in den naechsten Takten weiterlaeuft.</summary>
        public List<Weg> Zwischenstand(Stand s)
        {
            var l = new List<Weg>(s.Wege.Count); foreach (var w in s.Wege) l.Add(w.Kopie());
            RohrTeilen(l); return l;
        }

        /// <summary>wegsuche-laut: Weg w aufnehmen; bei k Wegen ersetzt er den leisesten (1 kHz), wenn er lauter ist. e = Energiesumme je Band
        /// (Durchgang + behaltene Wege), wird mitgefuehrt.</summary>
        static double Leisester(List<Weg> wege) { double m = double.PositiveInfinity; foreach (var x in wege) if (x.Db[1] < m) m = x.Db[1]; return m; }
        static void Behalten(List<Weg> wege, Weg w, int k, double[] e)
        {
            if (wege.Count < Math.Max(1, k)) { wege.Add(w); for (int bb = 0; bb < 3; bb++) e[bb] += Math.Pow(10, w.Db[bb] / 10); return; }
            int leis = 0; for (int i = 1; i < wege.Count; i++) if (wege[i].Db[1] < wege[leis].Db[1]) leis = i;
            if (w.Db[1] <= wege[leis].Db[1]) return;
            for (int bb = 0; bb < 3; bb++) e[bb] += Math.Pow(10, w.Db[bb] / 10) - Math.Pow(10, wege[leis].Db[bb] / 10);
            wege[leis] = w;
        }

        /// <summary>Knotenmenge eines Wegs als gewaehlt merken (Satz i = Bit i in mark, solange Weiter laeuft).</summary>
        void Waehlen(Stand s, List<int> knoten, Weg weg = null)
        {
            int gi = s.Gewaehlt.Count; s.Gewaehlt.Add(knoten.ToArray()); s.GewWeg.Add(weg);
            if (gi >= 16) return;
            foreach (int x in knoten) { if (mark[x] == 0) markiert.Add(x); mark[x] |= (ushort)(1 << gi); }
        }

        /// <summary>Suche beenden, ohne weiterzusuchen (Arbeiter: zu viele Unterbrechungen). Die bisher gefundenen Wege gelten.</summary>
        public void Aufgeben(Stand s) { if (s.Fertig) return; s.Fertig = true; s.Abgebrochen = true; RohrTeilen(s.Wege); }

        // ================================================================ 0.8.8 Freistellen (Quelle/Hoerer im Koerper)
        // Befund Livetest: 816 Lampen-Zeilen (Median -42 dB) - der Quellpunkt der Neonlampen steckt im Gehaeuse; von dort sieht er keinen
        // Netzknoten (0 Wege) und der Durchgang zaehlt das Gehaeuse als "2 Waende" (-60 dB). Ein Punkt IN einem geschlossenen Koerper
        // trifft in fast allen Richtungen zuerst eine Rueckseite (Normale zeigt nach aussen, Treffer.Vorne = false). Dann: entlang der
        // Richtung mit dem laengsten freien Lauf hinter der Austrittsflaeche (bis 3 m; Gleichstand: kuerzester Austritt), bis 0,15 m vor
        // die Aussenseite (der naechste freie Punkt im offensten angrenzenden Raum - nicht der Spalt zwischen Lampe und Decke oder die
        // Decke selbst, sonst landet die Lampe im Stockwerk darueber). Kleine Koerper (Huelle unter 2 m: das Glas der Lampe, Halter)
        // gehoeren zur Quelle: der Lauf geht durch sie hindurch und der Punkt landet dahinter. Beleg: Lampe Lab 2. OG (28,0|3,4|-35,1)
        // steckt 9 cm ueber der Raumdecke im Hohlraum zwischen zwei Raumschalen, darunter 2 cm Luft und das Lampenglas (1,9 x 0,1 m).
        // Nur kleine Einschluesse (Austritt unter 1 m), damit ein Hoerer/eine Quelle in einem normalen Raum nie verschoben wird.
        static readonly V[] Richtungen = MachRichtungen();
        static V[] MachRichtungen()
        {
            var l = new List<V> { new V(1, 0, 0), new V(-1, 0, 0), new V(0, 1, 0), new V(0, -1, 0), new V(0, 0, 1), new V(0, 0, -1) };
            for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2) l.Add(new V(x, y, z).Norm());
            return l.ToArray();
        }
        public const double FreiMaxM = 1.0, FreiAbstand = 0.15, KleinM = 2.0;

        /// <summary>Freier Lauf (bis 3 m) ab p in Richtung r; Treffer auf kleinen Koerpern (Huelle unter KleinM: Lampenglas, Halter)
        /// zaehlen nicht - sie gehoeren zur Quelle. hinterKlein = Abstand hinter dem letzten kleinen Koerper (0 = keiner).</summary>
        double Lauf(V p, V r, out double hinterKlein)
        {
            hinterKlein = 0; double weg = 0;
            for (int i = 0; i < 16 && weg < 3; i++)
            {
                var h = bvh.Erster(p + r * weg, r, (float)(3 - weg), 0.0005f);
                if (h.T < 0) return 3;
                int k = bvh.TK[h.Platz];
                if (k >= 0 && k < KoerperDiag.Length && KoerperDiag[k] < KleinM) { weg += h.T + 0.002; hinterKlein = weg; continue; }
                return weg + h.T;
            }
            return Math.Min(weg, 3);
        }

        /// <summary>Punkt p aus einem Koerper schieben (siehe oben). weg = Verschiebung (0 = p war frei).</summary>
        public V Freistellen(V p, out double weg)
        {
            weg = 0; if (!FreistellenAn) return p;
            V akt = p;
            for (int runde = 0; runde < 2; runde++)
            {
                int innen = 0, aussen = 0; double best = double.MaxValue, bestLauf = -1, bestKlein = 0; V bd = default;
                foreach (var r in Richtungen)
                {
                    var hit = bvh.Erster(akt, r, 3f, 0.0005f); if (hit.T < 0) continue;
                    if (hit.Vorne) { aussen++; continue; }
                    innen++;
                    if (hit.T > FreiMaxM) continue;
                    // freier Lauf hinter der Austrittsflaeche (bis 3 m, kleine Koerper zaehlen nicht)
                    double lauf = Lauf(akt + r * (hit.T + 0.002), r, out double klein);
                    // der offenste Nachbarraum gewinnt (laengster freier Lauf, bei Gleichstand der kuerzere Austritt): eine Lampe strahlt in
                    // den Raum unter ihr, nicht in den Spalt zur Decke (Livetest-Nachspiel: kuerzester Austritt = nach oben, -41 dB)
                    if (lauf > bestLauf + 0.05 || (Math.Abs(lauf - bestLauf) <= 0.05 && hit.T < best)) { best = hit.T; bestLauf = lauf; bestKlein = klein; bd = r; }
                }
                if (innen < 6 || aussen > 1 || bestLauf < 0.3) break;
                double frei = bestLauf - bestKlein;   // Raum hinter dem letzten kleinen Koerper
                akt = akt + bd * (best + 0.002 + bestKlein + Math.Min(FreiAbstand, 0.5 * frei));
            }
            weg = V.Abstand(akt, p);
            if (weg == 0) akt = VonFlaeche(p, out weg);
            return akt;
        }

        // Raid-2-Fix (6): Quellpunkt AUF einer Flaeche (Einschlaege: der Treffpunkt liegt genau auf dem Collider; Livetest 03:05:55
        // f=13628 Einschlag Halle 3 -> 0 Wege, -60 dB, der Punkt sah keinen Netzknoten). Ein Einschlag strahlt in den Halbraum vor der
        // Flaeche - der Punkt kommt FlaecheAbstand entlang der Flaechennormale davor (wie das Lampen-Freistellen: an die Stelle, von der
        // der Klang wirklich in den Raum geht). Nur, wenn eine Flaeche naeher als FlaecheNah liegt und der Weg dorthin frei ist.
        public const double FlaecheNah = 0.05, FlaecheAbstand = 0.25;
        V VonFlaeche(V p, out double weg)
        {
            weg = 0; double best = FlaecheNah; Bvh.Hit bh = default; V bd = default;
            V nn = default;
            foreach (var r in Richtungen)
            {
                var hit = bvh.Erster(p, r, (float)FlaecheNah, 0.0f); if (hit.T < 0 || hit.T >= best) continue;
                V e0 = bvh.Ecke(hit.Platz, 0), e1 = bvh.Ecke(hit.Platz, 1), e2 = bvh.Ecke(hit.Platz, 2);
                V n0 = V.Cross(e1 - e0, e2 - e0); if (n0.Len < 1e-12) continue; n0 = n0.Norm();
                if (Math.Abs(V.Dot(n0, r)) < 0.3) continue;   // streifender Strahl in der Flaechenebene: unzuverlaessig
                best = hit.T; bh = hit; bd = r; nn = n0;
            }
            if (best >= FlaecheNah) return p;
            // Vorderseite getroffen: die Normale zeigt zur Quelle hin (gegen den Strahl); Rueckseite (Punkt knapp hinter der Flaeche,
            // im Koerper): hinaus durch diese Flaeche, also in Strahlrichtung
            if (bh.Vorne ? V.Dot(nn, bd) > 0 : V.Dot(nn, bd) < 0) nn = -nn;
            V ziel = bh.Vorne ? p + nn * FlaecheAbstand : p + bd * best + nn * FlaecheAbstand;
            if (!bvh.Frei(bh.Vorne ? p + nn * 0.002 : p + bd * (best + 0.002), ziel, 0.001f)) return p;
            weg = V.Abstand(p, ziel);
            return ziel;
        }

        // ================================================================ Verfeinern (Suche2.Verfeinern)
        // Weg A: Goldener Schnitt ohne Delegat (Mono: 4,9 -> 0,7 us je Optimum). f(x) = |p - X(x)| + |X(x) - nx|, X(x) = A + (B - A) x.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static double Fs(V p, V A, V B, V nx, double x) { V X = A + (B - A) * x; return V.Abstand(p, X) + V.Abstand(X, nx); }
        static double GoldenStrecke(V p, V A, V B, V nx, int schritte = 30)
        {
            if (schritte <= 0) return Geschlossen(p, A, B, nx);
            double a = 0, b = 1, g = 0.6180339887; double c = b - g * (b - a), d = a + g * (b - a), fc = Fs(p, A, B, nx, c), fd = Fs(p, A, B, nx, d);
            for (int i = 0; i < schritte; i++) { if (fc < fd) { b = d; d = c; fd = fc; c = b - g * (b - a); fc = Fs(p, A, B, nx, c); } else { a = c; c = d; fc = fd; d = a + g * (b - a); fd = Fs(p, A, B, nx, d); } }
            return (a + b) / 2;
        }
        /// <summary>wegsuche-laut: dasselbe Minimum wie GoldenStrecke, geschlossen (Abwickeln): |p - X| + |X - nx| fuer X auf der Geraden
        /// A + d t ist konvex in t; dreht man nx um die Gerade in die Ebene von p auf die Gegenseite, liegt das Minimum dort, wo die
        /// Verbindung die Gerade schneidet: t* = (a1 r2 + a2 r1) / (r1 + r2) (a = Lage laengs, r = Abstand zur Geraden), auf [0, 1]
        /// beschraenkt (konvex: das Randminimum ist dann das Minimum auf der Strecke). Ergebnis als Anteil wie GoldenStrecke.</summary>
        internal static double Geschlossen(V p, V A, V B, V nx)
        {
            V ab = B - A; double l2 = V.Dot(ab, ab); if (l2 < 1e-18) return 0.5;
            double a1 = V.Dot(p - A, ab) / l2, a2 = V.Dot(nx - A, ab) / l2;   // Anteile laengs
            double r1 = (p - A - ab * a1).Len, r2 = (nx - A - ab * a2).Len;
            double t = r1 + r2 > 1e-12 ? (a1 * r2 + a2 * r1) / (r1 + r2) : a1;
            return t < 0 ? 0 : t > 1 ? 1 : t;
        }
        static V Optimum(Knoten k, V p, V nx, out bool durch, int schritte = 30)
        {
            durch = false;
            if (!k.IstPortal) { V A = k.A, B = k.B; double t = GoldenStrecke(p, A, B, nx, schritte); return A + (B - A) * t; }
            V c = (k.A + k.B) * 0.5, U = k.U, W = k.W, nn = V.Cross(U, W).Norm();
            V d = nx - p; double dn = V.Dot(d, nn);
            if (Math.Abs(dn) > 1e-9)
            {
                double s = V.Dot(c - p, nn) / dn;
                if (s > 0 && s < 1) { V x = p + d * s; double u = V.Dot(x - c, U) / V.Dot(U, U), w = V.Dot(x - c, W) / V.Dot(W, W); if (Math.Abs(u) <= 0.95 && Math.Abs(w) <= 0.95) { durch = true; return x; } }
            }
            V best = c; double bl = double.MaxValue; V U9 = U * 0.95, W9 = W * 0.95;
            V e0 = c - U9 - W9, e1 = c + U9 - W9, e2 = c + U9 + W9, e3 = c - U9 + W9;
            for (int i = 0; i < 4; i++)
            {
                V A = i == 0 ? e0 : i == 1 ? e1 : i == 2 ? e2 : e3, B = i == 0 ? e1 : i == 1 ? e2 : i == 2 ? e3 : e0; double t = GoldenStrecke(p, A, B, nx, schritte);
                V x2 = A + (B - A) * t; double l = V.Abstand(p, x2) + V.Abstand(x2, nx); if (l < bl) { bl = l; best = x2; }
            }
            return best;
        }
        static V FreiPunkt(Knoten k, V x) => k.IstPortal ? x : x + k.Aus * 0.03;

        V[] vfA = new V[16], vfB = new V[16], vfC = new V[16]; bool[] vfOk = new bool[16];
        /// <summary>Diagnose: in Verfeinern uebersprungene Knoten (unveraendert blockiert).</summary>
        public long DVerfSpar;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static bool Gleich(V a, V b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
        void Verfeinern(Weg w, V q, V h)
        {
            int m = w.Knoten.Count;
            var p = new V[m + 2]; p[0] = q; p[m + 1] = h; var dur = new bool[m];
            for (int i = 0; i < m; i++) p[i + 1] = n.K[w.Knoten[i]].P;
            V Pr(int i) => i == 0 || i == m + 1 ? p[i] : FreiPunkt(n.K[w.Knoten[i - 1]], p[i]);
            bool verbessert = false;
            // wegsuche-use (Rechenzeit): Knoten, an denen ALLE 5 Versuche blockiert waren, bleiben stehen (p[i] = alt, dur unveraendert). Haben
            // sich er und seine Nachbarn seither nicht bewegt, rechnet der naechste Durchgang genau dieselben Punkte und Strahlen und scheitert
            // genauso - er wird uebersprungen (gleiches Ergebnis, Profil Mono: 73 % der Versuche blockiert, 79 % der Zeit in Strahlen).
            if (vfA.Length < m) { vfA = new V[2 * m]; vfB = new V[2 * m]; vfC = new V[2 * m]; vfOk = new bool[2 * m]; }
            for (int i = 0; i < m; i++) vfOk[i] = false;
            for (int it = 0; it < 6; it++)
            {
                double schritt = 0;
                for (int i = 1; i <= m; i++)
                {
                    if (vfOk[i - 1] && Gleich(vfA[i - 1], p[i - 1]) && Gleich(vfB[i - 1], p[i]) && Gleich(vfC[i - 1], p[i + 1])) { DVerfSpar++; continue; }
                    vfOk[i - 1] = false;
                    var k = n.K[w.Knoten[i - 1]];
                    V alt = p[i], neu = Optimum(k, p[i - 1], p[i + 1], out bool durch);
                    if (V.Abstand(alt, neu) < 0.005) { dur[i - 1] = durch; continue; }
                    for (int b = 0; b < 5; b++)
                    {
                        p[i] = neu;
                        Strahlen += 2;
                        if (bvh.Frei(Pr(i - 1), Pr(i), 0.01f) && bvh.Frei(Pr(i), Pr(i + 1), 0.01f)) { schritt = Math.Max(schritt, V.Abstand(alt, neu)); dur[i - 1] = durch; verbessert = true; break; }
                        neu = (alt + neu) * 0.5; durch = false; p[i] = alt;
                        if (b == 4) { vfOk[i - 1] = true; vfA[i - 1] = p[i - 1]; vfB[i - 1] = p[i]; vfC[i - 1] = p[i + 1]; }
                    }
                }
                if (schritt < 0.01) break;
            }
            w.Verfeinert = verbessert;
            w.Punkte = new List<V>(p); w.DurchPortal = dur;
        }

        // ================================================================ wegsuche-laut: Pegel-Schaetzung eines Kandidaten ohne Sichtstrahl
        // Befund H1 (review-20261110/performance-mathematik.md): gesucht wurde nach Laenge, bewertet nach Daempfung. Der kuerzeste Weg
        // klebt an den Ecken (starke Beugung), ein laengerer mit flachen Knicken ist oft viel lauter (bis 43 dB). Die Laenge sagt den
        // Pegel kaum voraus: der Beugungsverlust (UTD je Knick, 5-30 dB je Kante) ueberwiegt die Kugelwelle bei Weitem.
        // Die Schaetzung rechnet den Weg so, wie Verfeinern ihn hoechstens straffen kann: Verfeinern schiebt jeden Wegpunkt auf seinem
        // Knoten (Kantenstueck/Portal) zum kuerzesten Durchgang und haelt nur an, wo ein Sichtstrahl blockiert. Ohne die Strahlen ergibt
        // dieselbe Rechnung den straffsten Weg durch dieselben Knoten. Darauf die Bewertung wie in Bewerten (UTD je Knick, Mehrfach-
        // beugungs-Grenze, Kugelwelle, Luft, Tueren der Portalknoten) - ohne Rohr und ohne Tueren, die erst ein Wegstueck quert (beides
        // braucht Strahlen). Messung (wegsuche-fix/mess, 143194 sichtbare Kandidaten Factory): in 97 % nie leiser als der gepruefte Weg,
        // sonst vor allem dort, wo das Rohr (Krummer im Gang) den Knick mildert oder zwei dicht folgende Kanten den Knick anders
        // aufteilen. Eine STRENG zulaessige Grenze gibt es mit diesem Modell nicht billig: die UTD-Summe ist nicht monoton im Straffen
        // (zwei nahe Kanten teilen den Knick je nach Lage verschieden), und Rohrgewinn/Krummer brauchen die Strahlen der Bewertung.
        // Die Knotenpunkte selbst (ohne Straffen) taugen als Schaetzung nicht: sie liegen in der Mitte der Kantenstuecke, die Knick-
        // winkel dort sind falsch (gemessen: sagt den lautesten Weg schlechter voraus als die Laenge).
        internal V[] schP = new V[16]; internal bool[] schDur = new bool[16];
        /// <summary>Straffen nimmt das geschlossene Minimum (Geschlossen, 0 = ohne goldenen Schnitt); Verfeinern behaelt seine 30 Schritte
        /// (Etappe-2-Gleichheit).</summary>
        const int StraffSchritte = 0;

        /// <summary>Straffster Weg q -> knoten -> h (Verfeinern ohne Sichtstrahlen) in schP[0..m+1], schDur[0..m).</summary>
        internal int Straffen(List<int> knoten, V q, V h)
        {
            int m = knoten.Count;
            if (schP.Length < m + 2) { schP = new V[2 * (m + 2)]; schDur = new bool[2 * (m + 2)]; }
            var p = schP; var dur = schDur; p[0] = q; p[m + 1] = h;
            for (int i = 0; i < m; i++) { p[i + 1] = n.P[knoten[i]]; dur[i] = false; }
            for (int it = 0; it < 6; it++)
            {
                double schritt = 0;
                for (int i = 1; i <= m; i++)
                {
                    V neu = Optimum(n.K[knoten[i - 1]], p[i - 1], p[i + 1], out bool durch, StraffSchritte);
                    double d = V.Abstand(p[i], neu); dur[i - 1] = durch;
                    if (d < 0.005) continue;
                    p[i] = neu; if (d > schritt) schritt = d;
                }
                if (schritt < 0.01) break;
            }
            return m;
        }

        /// <summary>Geschaetzter Verlust (dB &gt;= 0) im Band b des Wegs q -> knoten -> h, siehe oben. Keine Sichtstrahlen.</summary>
        public double SchaetzVerlust(List<int> knoten, V q, V h, int b = 1)
        {
            int m = Straffen(knoten, q, h); var p = schP; var dur = schDur;
            double d0 = Math.Max(V.Abstand(q, h), 0.05), L = 0;
            for (int i = 0; i <= m; i++) L += V.Abstand(p[i], p[i + 1]);
            // wegsuche-use: Gang (Rohr) auch in der Schaetzung - seit dem vollen fruehen Spiegelquellen-Gewinn (Rohr.Gewinn3, 20-27 dB in harten
            // Gaengen) lag die Schaetzung ohne Rohr fuer Gangwege so weit unter dem geprueften Pegel, dass die Suche sie nicht mehr zuerst
            // pruefte (Labyrinth "Kammer ueber Gang" 1,1 dB leiser als die Laengen-Reihenfolge). Querschnitt je Netzkantenpaar gemerkt
            // (QuerGemerkt: dieselbe Messung wie Bewerten, auf der Strecke zwischen den beiden Netzknoten bzw. Knoten und Mitte der 1-m-Zelle
            // von Quelle/Hoerer - feste Punkte, damit die Schaetzung nicht davon abhaengt, welche Kette zuerst kam).
            bool rohr = RohrAn && m >= 1;
            if (rohr)
            {
                if (schQ.Length < m + 1) schQ = new RohrQuer[2 * (m + 1)];
                for (int i = 0; i <= m; i++) schQ[i] = i == 0 ? QuerEnde(knoten[0], q) : i == m ? QuerEnde(knoten[m - 1], h) : QuerGemerkt(knoten[i - 1], knoten[i]);
            }
            double kanten = 0; int nb = 0, erste = -1, letzte = -1;
            for (int i = 1; i <= m; i++)
            {
                if (!Knick(p, m, i, knoten, dur, out int iv, out int ina, out double nk, out double winkel)) continue;
                nb++; if (erste < 0) erste = i; letzte = i;
                double s1 = Math.Max(V.Abstand(p[iv], p[i]), 0.05), s2 = Math.Max(V.Abstand(p[i], p[ina]), 0.05), dl = Math.Max(V.Abstand(p[iv], p[ina]), 0.05);
                var kk = n.K[knoten[i - 1]];   // oeffnung/B: Flaechen des Keils fuer den Einfallswinkel (Portale: unbekannt)
                double utd = (kk.IstPortal ? WegeKern.Beugung(new V3(p[iv].X, p[iv].Y, p[iv].Z), new V3(p[i].X, p[i].Y, p[i].Z), new V3(p[ina].X, p[ina].Y, p[ina].Z), F[b], nk)
                    : WegeKern.Beugung(new V3(p[iv].X, p[iv].Y, p[iv].Z), new V3(p[i].X, p[i].Y, p[i].Z), new V3(p[ina].X, p[ina].Y, p[ina].Z), F[b], nk,
                        new V3(kk.N1.X, kk.N1.Y, kk.N1.Z), new V3(kk.N2.X, kk.N2.Y, kk.N2.Z), new V3(kk.Aus.X, kk.Aus.Y, kk.Aus.Z), new V3(kk.B.X - kk.A.X, kk.B.Y - kk.A.Y, kk.B.Z - kk.A.Z)))
                    + 20 * Math.Log10(Math.Max(1.0, (s1 + s2) / dl));
                if (rohr && schQ[iv].Gang && schQ[ina - 1].Gang && Rohr.Biegung(schQ[iv], schQ[ina - 1]))
                { double kr = -Rohr.Kruemmer(F[b], 0.5 * (schQ[iv].W + schQ[ina - 1].W), RohrFrueh) * Math.Min(2.0, winkel / 90.0); if (kr > utd) utd = kr; }
                kanten += utd;
            }
            if (KettenDeckel && nb > 0)
            {
                double e = 0; for (int i = erste; i < letzte; i++) e += V.Abstand(p[i], p[i + 1]);
                kanten = Math.Max(kanten, -MehrfachGrenze(L, d0, e, nb, F[b], !DeckelNurDraussen || !Drinnen(p, erste, letzte)));
            }
            double gewinn = 0;
            if (rohr)
            {
                double cum = 0;
                for (int i = 0; i <= m;)
                {
                    if (!schQ[i].Gang) { cum += V.Abstand(p[i], p[i + 1]); i++; continue; }
                    int j = i; double r = 0, wsum = 0, hsum = 0, asum = 0;
                    while (j <= m && schQ[j].Gang) { double l = V.Abstand(p[j], p[j + 1]); r += l; wsum += schQ[j].W * l; hsum += schQ[j].H * l; asum += schQ[j].A[b] * l; j++; }
                    if (r > 2 * Math.Max(wsum / r, hsum / r))
                    {
                        for (int bb = 0; bb < 3; bb++) { gAlpha[bb] = asum / r; gLuft[bb] = Luft(b); }
                        Rohr.Gewinn3(wsum / r, hsum / r, cum, r, Math.Max(0, L - cum - r), gAlpha, gLuft, gAus, RohrFrueh);
                        gewinn += 10 * Math.Log10(gAus[0]);
                    }
                    cum += r; i = j;
                }
            }
            double tuer = 0;
            if (TuerenAn) for (int i = 0; i < m; i++) { int pi = n.K[knoten[i]].Portal; if (pi >= 0 && Zu[pi]) tuer -= Daemmung(TuerGruppe[pi], TuerDicke[pi], F[b], FugeAn); }
            double db = kanten - 20 * Math.Log10(L / d0) - Luft(b) * (L - d0) + gewinn + Math.Max(tuer, -60);
            return -Math.Min(0, db);
        }

        RohrQuer[] schQ = new RohrQuer[16];
        readonly Dictionary<long, RohrQuer> querMemo = new Dictionary<long, RohrQuer>();
        /// <summary>wegsuche-use: Querschnitt (Rohr.Messen) des Stuecks zwischen den Netzknoten u und v, je Knotenpaar einmal gemessen.</summary>
        RohrQuer QuerGemerkt(int u, int v)
        {
            int a = Math.Min(u, v), b = Math.Max(u, v); long key = ((long)a << 32) | (uint)b;
            if (querMemo.TryGetValue(key, out var r)) return r;
            if (querMemo.Count > 200000) { querMemo.Clear(); querEnde.Clear(); }
            r = Rohr.Messen(bvh, KoGruppe, n.P[a], n.P[b]); querMemo[key] = r; return r;
        }
        readonly Dictionary<(long, int), RohrQuer> querEnde = new Dictionary<(long, int), RohrQuer>();
        RohrQuer QuerEnde(int u, V p)
        {
            int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y), z = (int)Math.Floor(p.Z);
            var key = (Wegenetz.Schluessel(x, y, z), u);
            if (querEnde.TryGetValue(key, out var r)) return r;
            if (querEnde.Count > 200000) querEnde.Clear();
            r = Rohr.Messen(bvh, KoGruppe, n.P[u], new V(x + 0.5, y + 0.5, z + 0.5)); querEnde[key] = r; return r;
        }

        /// <summary>wegsuche-laut (Befund N1): Knick am Wegpunkt i (1..m) von p[0..m+1]? Punkte unter 1 mm Abstand sind EIN Punkt
        /// (Verfeinern legt zwei Knoten, die an derselben Ecke enden, auf dieselbe Stelle; der Nullvektor gab mit V.Norm() = 0 einen
        /// 90-Grad-Knick: gerade Linie mit Doppelpunkt -12,6 statt 0 dB, ein Weg 11 dB zu laut). Ein Haufen zusammenfallender Punkte
        /// zaehlt einmal, an seinem ersten Punkt, der kein durchquertes Portal ist; Nachbarn sind die Punkte vor und nach dem Haufen.
        /// Faellt der Haufen mit Quelle oder Hoerer zusammen, knickt dort nichts. Fuer getrennte Punkte genau die bisherige Rechnung.
        /// iv/ina = Index des Vor-/Nachpunkts, nk = Keilzahl, winkel in Grad.</summary>
        bool Knick(IList<V> p, int m, int i, IList<int> knoten, bool[] dur, out int iv, out int ina, out double nk, out double winkel)
        {
            iv = i - 1; ina = i + 1; nk = 2; winkel = 0;
            const double Eins = 1e-3;   // 1 mm: weit unter jeder Wellenlaenge (4 kHz: 86 mm) und ueber dem Rauschen der Optimierung
            int s = i; while (s >= 1 && V.Abstand(p[s], p[s - 1]) < Eins) s--;
            if (s == 0) return false;   // Haufen an der Quelle
            int e = i; while (e <= m && V.Abstand(p[e + 1], p[e]) < Eins) e++;
            if (e == m + 1) return false;   // Haufen am Hoerer
            int r = -1; for (int x = s; x <= e; x++) { var kx = n.K[knoten[x - 1]]; if (!(kx.IstPortal && dur[x - 1])) { r = x; break; } }
            if (r != i) return false;   // nur durchquerte Portale, oder der Knick zaehlt an einem anderen Punkt des Haufens
            var k = n.K[knoten[i - 1]];
            iv = s - 1; ina = e + 1;
            nk = k.IstPortal || k.Platte ? 2.0 : WegeKern.KeilN(k.InnenGrad);
            winkel = Math.Acos(Math.Max(-1, Math.Min(1, V.Dot((p[i] - p[iv]).Norm(), (p[ina] - p[i]).Norm())))) * 180 / Math.PI;
            return winkel >= 0.5;
        }

        /// <summary>Experiment/Test: Weg q -> knoten -> h wie in der Suche pruefen (Verfeinern mit Strahlen, Bewerten). Ohne Sicht-Pruefung.</summary>
        public Weg Pruefen(List<int> knoten, V q, V h)
        {
            var w = new Weg(); w.Knoten.AddRange(knoten);
            { double r0 = V.Abstand(q, n.P[w.Knoten[0]]) + V.Abstand(n.P[w.Knoten[w.Knoten.Count - 1]], h); for (int i = 0; i + 1 < w.Knoten.Count; i++) r0 += V.Abstand(n.P[w.Knoten[i]], n.P[w.Knoten[i + 1]]); w.Roh = r0; }
            Verfeinern(w, q, h); Bewerten(w, q, h); return w;
        }

        // ================================================================ Bewertung (Suche2.Bewerten)
        readonly List<Weg> rohrMit = new List<Weg>();
        readonly double[] gAlpha = new double[3], gLuft = new double[3], gAus = new double[3];   // wegsuche-use: Puffer fuer Rohr.Gewinn3
        void RohrTeilen(List<Weg> wege)
        {
            if (!RohrAn) return;
            var mit = rohrMit; mit.Clear(); foreach (var x in wege) if (x.GangP.Count > 0) mit.Add(x);
            // stabil absteigend nach Db[1] (wie OrderByDescending)
            for (int i = 1; i < mit.Count; i++) { var x = mit[i]; int j = i - 1; while (j >= 0 && mit[j].Db[1] < x.Db[1]) { mit[j + 1] = mit[j]; j--; } mit[j + 1] = x; }
            for (int i = 1; i < mit.Count; i++)
            {
                var w = mit[i]; int gleich = 0;
                foreach (var gp in w.GangP)
                    for (int j = 0; j < i; j++)
                    {
                        var o = mit[j]; if (o.RohrWeg) continue; bool treffer = false;
                        foreach (var x in o.GangP) if (V.Abstand(x.Key, gp.Key) < Math.Max(gp.Value, x.Value)) { treffer = true; break; }
                        if (treffer) { gleich++; break; }
                    }
                if (gleich * 2 < w.GangP.Count) continue;
                w.RohrWeg = true; RohrGeteilt++;
                for (int b = 0; b < 3; b++) w.Db[b] = w.OhneRohr[b];
            }
        }

        public void Bewerten(Weg w, V q, V h)
        {
            var p = w.Punkte; int m = p.Count - 2;
            double d0 = Math.Max(V.Abstand(q, h), 0.05), L = 0;
            for (int i = 0; i + 1 < p.Count; i++) L += V.Abstand(p[i], p[i + 1]);
            w.Laenge = L; w.Beugungen = 0;
            var quer = new RohrQuer[m + 1];
            if (RohrAn) for (int i = 0; i <= m; i++) { quer[i] = Rohr.Messen(bvh, KoGruppe, p[i], p[i + 1]); Strahlen += 12; }
            for (int b = 0; b < 3; b++) { w.Kanten[b] = 0; w.Rohr[b] = 0; w.Tuer[b] = 0; }
            int ersteKante = -1, letzteKante = -1;
            for (int i = 1; i <= m; i++)
            {
                // wegsuche-laut (N1): Knick() legt Punkte unter 1 mm zusammen (fuer getrennte Punkte genau die bisherige Rechnung)
                if (!Knick(p, m, i, w.Knoten, w.DurchPortal, out int iv, out int ina, out double nk, out double winkel)) continue;
                double s1 = Math.Max(V.Abstand(p[iv], p[i]), 0.05), s2 = Math.Max(V.Abstand(p[i], p[ina]), 0.05), dl = Math.Max(V.Abstand(p[iv], p[ina]), 0.05);
                var a = new V3(p[iv].X, p[iv].Y, p[iv].Z); var c = new V3(p[i].X, p[i].Y, p[i].Z); var e = new V3(p[ina].X, p[ina].Y, p[ina].Z);
                w.Beugungen++; if (ersteKante < 0) ersteKante = i; letzteKante = i;
                bool gang = RohrAn && quer[iv].Gang && quer[ina - 1].Gang && Rohr.Biegung(quer[iv], quer[ina - 1]);
                for (int b = 0; b < 3; b++)
                {
                    var kk = n.K[w.Knoten[i - 1]];   // oeffnung/B: Flaechen des Keils fuer den Einfallswinkel (Portale: unbekannt)
                    double utd = (kk.IstPortal ? WegeKern.Beugung(a, c, e, F[b], nk)
                        : WegeKern.Beugung(a, c, e, F[b], nk, new V3(kk.N1.X, kk.N1.Y, kk.N1.Z), new V3(kk.N2.X, kk.N2.Y, kk.N2.Z),
                            new V3(kk.Aus.X, kk.Aus.Y, kk.Aus.Z), new V3(kk.B.X - kk.A.X, kk.B.Y - kk.A.Y, kk.B.Z - kk.A.Z)))
                        + 20 * Math.Log10(Math.Max(1.0, (s1 + s2) / dl));
                    if (gang)
                    {
                        double kr = -Rohr.Kruemmer(F[b], 0.5 * (quer[iv].W + quer[ina - 1].W), RohrFrueh) * Math.Min(2.0, winkel / 90.0);
                        if (kr > utd) { w.Knick[b] += kr - utd; utd = kr; }
                    }
                    w.Kanten[b] += utd;
                }
            }
            for (int b = 0; b < 3; b++) { w.KantenEP[b] = w.Kanten[b]; w.KantenOhneKnick[b] = w.Kanten[b] - w.Knick[b]; }
            w.Gedeckelt = false;
            if (KettenDeckel && w.Beugungen > 0)
            {
                double e = 0; for (int i = ersteKante; i < letzteKante; i++) e += V.Abstand(p[i], p[i + 1]);
                bool deckel = !DeckelNurDraussen || !Drinnen(p, ersteKante, letzteKante);
                for (int b = 0; b < 3; b++)
                {
                    double grenze = MehrfachGrenze(L, d0, e, w.Beugungen, F[b], deckel);
                    if (!deckel && b == 1 && -w.Kanten[b] > MehrfachGrenze(L, d0, e, w.Beugungen, F[b], true)) DOhneDeckel++;
                    if (-w.Kanten[b] > grenze) { w.Kanten[b] = -grenze; w.Gedeckelt = true; }
                    if (-w.KantenOhneKnick[b] > grenze) w.KantenOhneKnick[b] = -grenze;
                }
            }
            w.GangP.Clear();
            if (RohrAn)
            {
                var cum = new double[m + 2]; for (int i = 0; i <= m; i++) cum[i + 1] = cum[i] + V.Abstand(p[i], p[i + 1]);
                for (int i = 0; i <= m;)
                {
                    if (!quer[i].Gang) { i++; continue; }
                    int j = i; double r = 0, wsum = 0, hsum = 0; double[] asum = new double[3];
                    while (j <= m && quer[j].Gang) { double l = V.Abstand(p[j], p[j + 1]); r += l; wsum += quer[j].W * l; hsum += quer[j].H * l; for (int b = 0; b < 3; b++) asum[b] += quer[j].A[b] * l; j++; }
                    if (r > 2 * Math.Max(wsum / r, hsum / r))
                    {
                        double vor = cum[i], nach = cum[m + 1] - cum[j];
                        for (int b = 0; b < 3; b++) { gAlpha[b] = asum[b] / r; gLuft[b] = Luft(b); }
                        Rohr.Gewinn3(wsum / r, hsum / r, vor, r, nach, gAlpha, gLuft, gAus, RohrFrueh);
                        for (int b = 0; b < 3; b++) w.Rohr[b] += 10 * Math.Log10(gAus[b]);
                        for (int t = i; t < j; t++) { double l = cum[t + 1] - cum[t]; int k2 = Math.Max(1, (int)Math.Ceiling(l)); for (int u = 0; u < k2; u++) w.GangP.Add(new KeyValuePair<V, double>(p[t] + (p[t + 1] - p[t]) * ((u + 0.5) / k2), Math.Max(quer[t].W, quer[t].H))); }
                    }
                    w.GangM += r; i = j;
                }
            }
            if (TuerenAn)
                for (int i = 0; i <= m; i++)
                {
                    int ia = i >= 1 ? n.K[w.Knoten[i - 1]].Portal : -1, ib = i < m ? n.K[w.Knoten[i]].Portal : -1;
                    var l = pt.Alle(p[i], p[i + 1], ia, ib); if (l == null) continue;
                    foreach (int x in l) if (Zu[x]) { w.Tueren++; for (int b = 0; b < 3; b++) w.Tuer[b] -= Daemmung(TuerGruppe[x], TuerDicke[x], F[b], FugeAn); }
                }
            if (TuerenAn)
                for (int i = 1; i <= m; i++) { int pi = n.K[w.Knoten[i - 1]].Portal; if (pi >= 0 && Zu[pi]) { w.Tueren++; for (int b = 0; b < 3; b++) w.Tuer[b] -= Daemmung(TuerGruppe[pi], TuerDicke[pi], F[b], FugeAn); } }
            for (int b = 0; b < 3; b++)
            {
                double luft = Luft(b) * (L - d0);
                w.Db[b] = Math.Min(0, w.Kanten[b] - 20 * Math.Log10(L / d0) - luft + (RohrAn ? w.Rohr[b] : 0) + Math.Max(w.Tuer[b], -60));
                w.OhneRohr[b] = Math.Min(0, w.KantenOhneKnick[b] - 20 * Math.Log10(L / d0) - luft + Math.Max(w.Tuer[b], -60));
            }
            for (int b = 0; b < 3; b++) w.OhneRohr[b] = Math.Max(-60, w.OhneRohr[b]);   // Suche2: nach WaldAnwenden (hier ohne Wald)
            double lambda = WegeKern.C / F[0]; w.Maekawa = -WegeKern.Maekawa(2 * (L - d0) / lambda);
            V rr = (p[p.Count - 2] - h).Norm();
            w.Azimut = Math.Atan2(rr.X, rr.Z) * 180 / Math.PI; w.Hoehe = Math.Asin(Math.Max(-1, Math.Min(1, rr.Y))) * 180 / Math.PI;
        }

        /// <summary>Raid-3-Fix: liegt der gebeugte Teil des Wegs (Kanten erste..letzte und die Mitten dazwischen) ganz in Innenraeumen?
        /// Ein Punkt im Freien (Raum "draussen" oder ohne Flaeche) = nein; Punkte ohne Kasten (in Waenden, Tuerlaibungen) zaehlen nicht,
        /// mindestens ein Punkt muss in einem Innenraum liegen. Ohne Raumdaten: nein (Deckel wie bisher).</summary>
        bool Drinnen(IList<V> p, int a, int b)
        {
            var r = Raum; if (r == null || a < 1 || b < a) return false;
            bool innen = false;
            for (int i = a; i <= b; i++)
            {
                int l = r.Lage(p[i]); if (l < 0) return false; if (l > 0) innen = true;
                if (i < b) { l = r.Lage((p[i] + p[i + 1]) * 0.5); if (l < 0) return false; if (l > 0) innen = true; }
            }
            return innen;
        }

        // ================================================================ 0.8.8 Mehrfachbeugung begrenzen
        /// <summary>
        /// Obergrenze der Beugungsdaempfung (dB, >= 0, ohne die Kugelwelle ueber den Umweg) eines Wegs mit nb Kanten, Laenge L, Direktlinie
        /// d0, Abstand e zwischen erster und letzter Kante. Befund Livetest: die Summe der UTD-Kanten (Epstein-Peterson: jede Kante mit ihren
        /// Nachbarpunkten als Quelle/Empfaenger) gibt fuer Ketten nahezu streifender Kanten je Kante ~6 dB (Schattengrenze) - 8 Kanten auf
        /// einem Weg 14 % ueber der Luftlinie = -64/-104/-150 dB. Das ist der bekannte Fehler der Summenverfahren bei dicht folgenden Kanten
        /// (Deygout 1966, Giovaneli 1984: Ueberschaetzung waechst mit der Kantenzahl). ISO 9613-2:1996 Gl. 14 behandelt alle Kanten als EINEN
        /// dicken Schirm: Dz = 10 lg(3 + (C2/lambda) C3 z), C2 = 20, z = L - d0 (Umweg), C3 = (1 + (5 lambda/e)^2) / (1/3 + (5 lambda/e)^2)
        /// bei zwei und mehr Kanten (1 bei einer), gedeckelt bei 20 dB (Einfach-) bzw. 25 dB (Mehrfachbeugung) - der Deckel beruht auf der
        /// Streuung, die tiefe Schatten fuellt; innen tun das zusaetzlich die Reflexionen (dafuer der Raumanteil RaumModell). Dz ist auf die
        /// Direktlinie bezogen, die Kanten des Wegs auf die Kugelwelle ueber L: Grenze = max(0, Dz - 20 lg(L/d0)). Die Kanten-Summe bleibt,
        /// wo sie kleiner ist (eine einzelne Kante, scharfe gut getrennte Ecken: UTD ist dort genauer).
        /// </summary>
        /// Raid-3-Fix (10.11.2026): deckel = false fuer Wege ganz im Gebaeude. Der 20/25-dB-Deckel der ISO 9613-2 (Abschnitt 7.4) ist eine
        /// Grenze fuer die Schallausbreitung IM FREIEN: dort fuellen Streuung an Turbulenz und Inhomogenitaeten der Atmosphaere den tiefen
        /// Schatten hinter Schirmen auf (Daigle 1979; ISO 9613-2 Anm. zu Gl. 12). Drinnen gibt es diese Streuung nicht - den Schatten fuellen
        /// dort die Reflexionen, und die rechnet der Raumanteil (RaumModell: Diffusfeld des eigenen Raums und der Raumfolgen) als eigenen
        /// Beitrag dazu. Mit Deckel zaehlte ein tuerloser Weg durch 10 Ecken der Bueros (Raid 3, 13 Faelle) wie ein Schirm im Freien
        /// (-25 dB), obwohl der Raumweg derselben Raumfolge -40 dB und die Mod -51 dB ergab. Ohne Deckel bleibt die Formel des dicken
        /// Schirms (Dz waechst mit dem Umweg z) die Grenze der Kanten-Summe - die Ueberschaetzung der Summenverfahren bei streifenden
        /// Kanten bleibt also weiter begrenzt.
        public static double MehrfachGrenze(double L, double d0, double e, int nb, double f, bool deckel = true)
        {
            double lam = WegeKern.C / f, z = Math.Max(0, L - d0);
            double c3 = 1;
            if (nb >= 2 && e > 1e-3) { double x = 5 * lam / e; c3 = (1 + x * x) / (1.0 / 3 + x * x); }
            else if (nb >= 2) c3 = 1;
            double dz = 10 * Math.Log10(3 + 20 / lam * c3 * z);
            if (deckel) dz = Math.Min(dz, nb >= 2 ? 25.0 : 20.0);
            return Math.Max(0, dz - 20 * Math.Log10(Math.Max(L, d0) / d0));
        }

        // ================================================================ Durchgang (Suche2.Durchgang)
        struct Abschnitt { public double A, B; public int G; public double Cos; public int K, Platz; public bool Einzel; }
        readonly List<Bvh.Hit> hitsD = new List<Bvh.Hit>(), hitsL = new List<Bvh.Hit>();
        readonly Dictionary<int, List<KeyValuePair<float, int>>> jeKD = new Dictionary<int, List<KeyValuePair<float, int>>>();
        readonly Stack<List<KeyValuePair<float, int>>> listenPool = new Stack<List<KeyValuePair<float, int>>>();
        readonly List<Abschnitt> abschnitteD = new List<Abschnitt>(); readonly List<KeyValuePair<float, int>> uD = new List<KeyValuePair<float, int>>();
        static readonly Comparison<KeyValuePair<float, int>> NachTP = (x, y) => x.Key.CompareTo(y.Key);
        static readonly Comparison<Abschnitt> NachA = (x, y) => x.A.CompareTo(y.A);

        static readonly string[] Herkunft = { "merged", "in/out", "surface", "body", "assumed" };
        static readonly string[,] WandText = MachWandText();
        static readonly string[] TuerText = Array.ConvertAll(Gruppen.Namen, x => "door " + x);
        static string[,] MachWandText() { var t = new string[Gruppen.Namen.Length, Herkunft.Length]; for (int g = 0; g < Gruppen.Namen.Length; g++) for (int k = 0; k < Herkunft.Length; k++) t[g, k] = Gruppen.Namen[g] + " (" + Herkunft[k] + ")"; return t; }

        public Weg Durchgang(V q, V h)
        {
            var swD = Stopwatch.StartNew();
            var w = new Weg { Durchgang = true, Punkte = new List<V> { q, h }, Laenge = V.Abstand(q, h) };
            var hits = hitsD; bvh.Alle(q, h, hits);
            V dir = (h - q).Norm();
            var jeK = jeKD; foreach (var kv0 in jeK) { kv0.Value.Clear(); listenPool.Push(kv0.Value); } jeK.Clear();
            foreach (var x in hits) { int kk = bvh.TK[x.Platz]; if (!jeK.TryGetValue(kk, out var l)) jeK[kk] = l = listenPool.Count > 0 ? listenPool.Pop() : new List<KeyValuePair<float, int>>(); l.Add(new KeyValuePair<float, int>(x.T, x.Platz)); }
            var abschnitte = abschnitteD; abschnitte.Clear();
            foreach (var kv in jeK)
            {
                int kk = kv.Key; var ts = kv.Value;
                ts.Sort(NachTP); int g = KoGruppe[kk];
                var u = uD; u.Clear(); foreach (var t in ts) if (u.Count == 0 || t.Key - u[u.Count - 1].Key > 0.002f) u.Add(t);
                for (int i = 0; i < u.Count; i += 2)
                {
                    int to = u[i].Value; V nn = bvh.Normale(to); double cos = Math.Max(0.05, Math.Abs(V.Dot(nn, dir)));
                    if (i + 1 < u.Count) abschnitte.Add(new Abschnitt { A = u[i].Key, B = u[i + 1].Key, G = g, Cos = cos, K = kk, Platz = to, Einzel = false });
                    else abschnitte.Add(new Abschnitt { A = u[i].Key, B = u[i].Key, G = g, Cos = cos, K = kk, Platz = to, Einzel = true });
                }
            }
            abschnitte.Sort(NachA);
            double[] sum = new double[3];
            for (int i = 0; i < abschnitte.Count;)
            {
                var s = abschnitte[i]; double ende = s.B; int j = i + 1;
                while (j < abschnitte.Count && !s.Einzel && !abschnitte[j].Einzel && abschnitte[j].A <= ende + 0.005 && abschnitte[j].G == s.G) { ende = Math.Max(ende, abschnitte[j].B); j++; }
                double dicke; int herkunft;
                if (!s.Einzel) { dicke = (ende - s.A) * s.Cos; herkunft = j - i > 1 ? 0 : 1; }
                else
                {
                    V pt0 = q + dir * s.A; V nn = bvh.Normale(s.Platz);
                    double lok = DickeAn(pt0, nn, s.K);
                    if (!double.IsNaN(lok)) { dicke = lok; herkunft = 2; }
                    else if (KoerperDicke != null && !float.IsNaN(KoerperDicke[s.K])) { dicke = KoerperDicke[s.K]; herkunft = 3; }
                    else { dicke = Bauakustik.EinzelDicke(s.G, 0.15f); herkunft = 4; }
                }
                w.Waende.Add(new KeyValuePair<string, double>(WandText[s.G, herkunft], dicke));   // wegsuche-use (M3): Text aus der Tabelle, keine neue Zeichenkette je Wand
                for (int b = 0; b < 3; b++) sum[b] += Daemmung(s.G, dicke, F[b]);
                i = j;
            }
            if (TuerenAn)
            {
                var l = pt.Alle(q, h);
                if (l != null) foreach (int x in l) if (Zu[x]) { w.Tueren++; w.Waende.Add(new KeyValuePair<string, double>(TuerText[TuerGruppe[x]], TuerDicke[x])); for (int b = 0; b < 3; b++) sum[b] += Daemmung(TuerGruppe[x], TuerDicke[x], F[b], FugeAn); }
            }
            for (int b = 0; b < 3; b++) w.Db[b] = -Math.Min(sum[b], 60.0);
            V r = (q - h).Norm(); w.Azimut = Math.Atan2(r.X, r.Z) * 180 / Math.PI; w.Hoehe = Math.Asin(Math.Max(-1, Math.Min(1, r.Y))) * 180 / Math.PI;
            TDurchgang += swD.Elapsed.TotalMilliseconds;
            return w;
        }

        /// <summary>Etappe2/Dicke.cs LokalAn: Dicke am Punkt m einer Flaeche mit Aussennormale nn des Koerpers ki (Gegenflaeche, bis 3 m).</summary>
        double DickeAn(V m, V nn, int ki)
        {
            bvh.Alle(m - nn * 0.0005, m - nn * 3.0, hitsL);
            foreach (var x in hitsL)
            {
                if (bvh.TK[x.Platz] != ki) continue;
                if (x.T < 0.0015f) continue;
                V n2 = bvh.Normale(x.Platz);
                if (V.Dot(nn, n2) > -0.866) continue;
                return x.T + 0.0005;
            }
            return double.NaN;
        }

        /// <summary>Freie Sicht ohne Geometrie und ohne geschlossene Tuer auf der Linie (KartenAblauf.Frei).</summary>
        public bool Frei(V q, V h)
        {
            if (!bvh.Frei(q, h, 0f)) return false;
            var l = pt.Alle(q, h); if (l == null) return true;
            foreach (int i in l) if (Zu[i]) return false;
            return true;
        }

        // ================================================================ Daemmung (Etappe2-Karten/Suche.cs)
        static readonly double[] E = { 25, 0, 0, 200, 0, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, Nu = { 0.2, 0, 0, 0.3, 0, 0.3, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        public const double GlasMax = 0.012, GlasScheibe = 0.006;
        public static double Wirksam(int g, double dicke) => g == 7 && dicke > GlasMax ? GlasScheibe : dicke;
        static readonly TuerVorgaben OhneFuge = new TuerVorgaben { Messen = true, ErsatzHolz = 0.04, ErsatzGlas = 0.006, Blech = 0.0015 };
        public static TuerBlatt Blatt(int g, double dicke, bool fuge = true) => TuerKern.Waehlen(g, Gruppen.Dichte[g], Gruppen.Massiv[g], dicke, g == 7, fuge ? TuerVorgaben.Standard : OhneFuge);
        /// <summary>Daemmung (dB) eines Koerpers bzw. (dicke &lt; 0) eines Tuerblatts; fuge = 0.8.8 Fugen als paralleler Durchlass (TuerKern).</summary>
        public static double Daemmung(int g, double dicke, double f, bool fuge = true)
        {
            if (dicke < 0) return TuerKern.Db(Blatt(g, -dicke, fuge), f, true, 0.1, 60);
            if (g == 7) return TuerKern.GlasDb(Gruppen.Dichte[g], dicke, f, true, 0.1, 60, fuge);   // 10.11. (akustik-wand, Befund 3): eine Glasregel fuer Mod und Wegsuche
            dicke = Wirksam(g, dicke);
            double mx = Gruppen.Massiv[g], schale = Math.Min(dicke, mx);
            if (dicke > mx) dicke = 2 * mx;
            double m = Gruppen.Dichte[g] * Math.Max(dicke, 0.002);
            if (m <= 0) return 0;
            double r = Bauakustik.Massengesetz(m, f);
            if (E[g] > 0) r = Bauakustik.Koinzidenz(r, m, f, Bauakustik.Koinzidenzfrequenz(Gruppen.Dichte[g], Math.Max(schale, 0.002), E[g] * 1e9, Nu[g]), 0.1);
            return H.Clamp(r, 0, 60);
        }
    }

    // ------------------------------------------------------------------------------------------ Rohr (Etappe2/Rohr.cs + Etappe2-Karten/RohrKarten.cs)
    internal struct RohrQuer { public bool Gang; public double W, H; public double[] A; }

    internal static class Rohr
    {
        public static readonly double[][] Alpha =
        {
            new[] { 0.01, 0.02, 0.02 }, new[] { 0.15, 0.25, 0.30 }, new[] { 0.20, 0.40, 0.50 }, new[] { 0.01, 0.01, 0.02 },
            new[] { 0.10, 0.05, 0.05 }, new[] { 0.10, 0.07, 0.07 }, new[] { 0.25, 0.10, 0.10 }, new[] { 0.25, 0.12, 0.07 },
            new[] { 0.30, 0.60, 0.70 }, new[] { 0.05, 0.05, 0.05 }, new[] { 0.30, 0.50, 0.60 }, new[] { 0.01, 0.01, 0.02 },
            new[] { 0.05, 0.05, 0.05 }, new[] { 0.10, 0.07, 0.07 }, new[] { 0.40, 0.60, 0.70 },
        };
        public const double Streuung = 0.05, MaxQuer = 5.0, Gleich = 1.5;
        /// <summary>wegsuche-use: Ende des fruehen Schalls (s) nach dem Direktweg, ISO 3382-1 (C80, Musik/Gehoer: was bis 80 ms nach dem
        /// Direktschall ankommt, verschmilzt mit ihm). Spaetere Spiegelquellen des Gangs sind sein Nachhall - den spielen das Spiel (Hall des
        /// Hoererraums) und der Raumanteil (Diffusfeld der Raumfolge); im Weg gezaehlt waeren sie doppelt.</summary>
        public const double FruehS = 0.080;
        const double C = 343.0;
        static readonly double[] Stellen = { 0.25, 0.5, 0.75 };

        public static RohrQuer Messen(Bvh bvh, byte[] koGruppe, V a, V b)
        {
            var q = new RohrQuer { A = new double[3] };
            V d = b - a; double len = d.Len; if (len < 1.0) return q; d = d * (1 / len);
            V up = Math.Abs(d.Y) > 0.9 ? new V(1, 0, 0) : new V(0, 1, 0);
            V u = V.Cross(d, up).Norm(), v = V.Cross(u, d).Norm();
            // Weg A: ohne Listen/Felder je Aufruf (bis zu 3 Stellen: Median durch Sortieren von 2 bzw. 3 Werten)
            double a0 = 0, a1 = 0, a2 = 0; int na = 0, nw = 0, breit = 0, hoch = 0; double w0 = 0, w1 = 0, w2 = 0, h0 = 0, h1 = 0, h2 = 0;
            V mu = -u, mv = -v;
            for (int st = 0; st < 3; st++)
            {
                double f = Stellen[st];
                V p = a + (b - a) * f; double t0 = 0, t1 = 0, t2 = 0, t3 = 0; bool zu = true;
                for (int i = 0; i < 4 && zu; i++)
                {
                    var h = bvh.Erster(p, i == 0 ? u : i == 1 ? mu : i == 2 ? v : mv, (float)MaxQuer); if (h.T < 0) { zu = false; break; }
                    if (i == 0) t0 = h.T; else if (i == 1) t1 = h.T; else if (i == 2) t2 = h.T; else t3 = h.T;
                    var al = Alpha[koGruppe[bvh.TK[h.Platz]]]; a0 += al[0]; a1 += al[1]; a2 += al[2]; na++;
                }
                // wegsuche-use (Rechenzeit): frueh fertig, wenn das Ergebnis schon feststeht - weniger als 2 geschlossene Stellen sind nicht mehr
                // moeglich, oder zwei Stellen sind breiter/hoeher als MaxQuer (dann liegt auch der Median darueber). Fuer "kein Gang" werden
                // W, H und A nirgends gelesen (Bewerten nutzt sie nur bei Gang), das Ergebnis ist also dasselbe.
                if (!zu) { if (st + 1 - nw >= 2) return q; continue; }
                if (nw == 0) { w0 = t0 + t1; h0 = t2 + t3; } else if (nw == 1) { w1 = t0 + t1; h1 = t2 + t3; } else { w2 = t0 + t1; h2 = t2 + t3; }
                nw++;
                if (t0 + t1 > MaxQuer) breit++; if (t2 + t3 > MaxQuer) hoch++;
                if (breit >= 2 || hoch >= 2) return q;
            }
            if (nw < 2) return q;
            q.W = Median(w0, w1, w2, nw); q.H = Median(h0, h1, h2, nw);
            double[] asum = { a0, a1, a2 };
            if (q.W > MaxQuer || q.H > MaxQuer) return q;
            q.Gang = true;
            for (int k = 0; k < 3; k++) q.A[k] = Math.Min(0.95, asum[k] / Math.Max(1, na) + Streuung);
            return q;
        }

        /// <summary>Wie List.Sort + [Count/2]: bei 2 Werten der groessere, bei 3 der mittlere.</summary>
        static double Median(double x, double y, double z, int n)
        {
            if (n == 2) return Math.Max(x, y);
            return Math.Max(Math.Min(x, y), Math.Min(Math.Max(x, y), z));
        }

        /// <summary>Einfuegungsdaempfung (dB) eines unverkleideten Rechteck-Kruemmers ohne Leitbleche je 90 Grad, ASHRAE Handbook HVAC
        /// Applications (Elbow insertion loss), f w in kHz inch: &lt; 1,9: 0; 1,9-3,8: 1; 3,8-7,5: 5; 7,5-15: 8; 15-30: 4; &gt; 30: 3.
        /// wegsuche-use (Akustiker Befund 8): die letzte Stufe (&gt; 30 kHz inch = 762 kHz mm) fehlte, dort galten 4 statt 3 dB.</summary>
        public static double Kruemmer(double f, double w, bool voll = true)
        {
            double fw = f / 1000 * w * 1000;   // kHz mm
            return fw < 48 ? 0 : fw < 96 ? 1 : fw < 190 ? 5 : fw < 380 ? 8 : fw < 762 || !voll ? 4 : 3;
        }

        /// <summary>Gewinn (Faktor >= 1) eines Gangs w x h gegenueber dem Freifeld auf dem Weg der Laenge L = a + r + x (a vor, r im, x nach
        /// dem Gang): Spiegelquellen des Rechteckkanals (Kuttruff, Room Acoustics: Quaderraum; Spiegelquelle (m, n) bei (m w, n h) quer zur
        /// Achse, |m| + |n| Reflexionen) G = Summe R^(|m|+|n|) L^2 / d^2 10^(-luftDb (d - L) / 10), d^2 = L^2 + (m w)^2 + (n h)^2, R = 1 - alpha
        /// (alpha = gemessene Wandmaterialien + Streuung). Gezaehlt werden nur Spiegelquellen, die durch beide Gangoeffnungen sichtbar sind
        /// (|m|, |n| &lt;= L / 2a bzw. L / 2x, RohrKarten) und deren Schall hoechstens FruehS nach dem Direktweg ankommt (d - L &lt;= c FruehS).
        /// wegsuche-use (Akustiker Befund 8): vorher feste Grenze |m| + |n| &lt;= 3 - ein harter 30-m-Gang bekam 12-13 dB statt der 24-25 dB des
        /// fruehen Schalls (Tabelle review-20261110/wegsuche-use/rohr). Die Reihe laeuft schalenweise (|m| + |n| = k) und endet, wenn auch eine
        /// obere Schranke fuer alle restlichen Schalen den Gewinn um weniger als 0,1 dB hebt (Schale k: hoechstens 4k Glieder, jedes &lt;= R^k L^2 /
        /// (L^2 + k^2 c^2), c = min(w, h) / Wurzel 2). Sie konvergiert fuer alpha > 0 auch ohne Zeitfenster und bleibt unter dem verlustfreien
        /// Rohr 4 pi L^2 / (w h).</summary>
        public static double Gewinn(double w, double h, double a, double r, double x, double alpha, double luftDb = 0)
        {
            var al = new[] { alpha, alpha, alpha }; var lu = new[] { luftDb, luftDb, luftDb }; var g = new double[3];
            Gewinn3(w, h, a, r, x, al, lu, g); return g[0];
        }

        /// <summary>Gewinn fuer alle drei Baender in einem Durchlauf (gleiche Spiegelquellen, je Band eigenes alpha und eigene Luftdaempfung).
        /// Spiegelquellen (m, n) und (-m, n) liegen gleich weit weg und zaehlen gemeinsam.</summary>
        public static void Gewinn3(double w, double h, double a, double r, double x, double[] alpha, double[] luftDb, double[] aus, bool frueh = true)
        {
            double L = a + r + x, L2 = L * L;
            aus[0] = aus[1] = aus[2] = 1;
            if (!frueh) { for (int b = 0; b < 3; b++) aus[b] = GewinnEtappe2(w, h, a, r, x, alpha[b]); return; }
            if (L <= 0 || w <= 0 || h <= 0) return;
            int mMax = Grenze(L, a, x);
            double dMax = L + C * FruehS, rho2Max = dMax * dMax - L2, c2 = 0.5 * Math.Min(w, h) * Math.Min(w, h);
            int kEnde = (int)Math.Min(2.0 * mMax, Math.Sqrt(rho2Max / c2)) + 1;
            double R0 = 1 - Math.Max(alpha[0], 1e-3), R1 = 1 - Math.Max(alpha[1], 1e-3), R2 = 1 - Math.Max(alpha[2], 1e-3);
            double k0 = luftDb[0] * Math.Log(10) / 10, k1 = luftDb[1] * Math.Log(10) / 10, k2 = luftDb[2] * Math.Log(10) / 10;
            double s0 = 0, s1 = 0, s2 = 0, r0 = 1, r1 = 1, r2 = 1;   // r = R^k je Band
            for (int k = 0; k <= kEnde; k++, r0 *= R0, r1 *= R1, r2 *= R2)
            {
                double t0 = 0, t1 = 0, t2 = 0;
                for (int m = 0; m <= k; m++)
                {
                    int an = k - m; if (m > mMax || an > mMax) continue;
                    double mw = m * w, nh = an * h, rho2 = mw * mw + nh * nh; if (rho2 > rho2Max) continue;
                    double d2 = L2 + rho2, dd = Math.Sqrt(d2) - L, g = L2 / d2 * ((m > 0 ? 2 : 1) * (an > 0 ? 2 : 1));
                    t0 += g * Math.Exp(-k0 * dd); t1 += g * Math.Exp(-k1 * dd); t2 += g * Math.Exp(-k2 * dd);
                }
                s0 += r0 * t0; s1 += r1 * t1; s2 += r2 * t2;
                // Rest: obere Schranke der Schalen k+1..kEnde je Band (Glieder ohne Luft, 4j je Schale)
                double e0 = 0, e1 = 0, e2 = 0, q0 = r0 * R0, q1 = r1 * R1, q2 = r2 * R2;
                for (int j = k + 1; j <= kEnde; j++, q0 *= R0, q1 *= R1, q2 *= R2) { double gj = 4.0 * j * L2 / (L2 + j * j * c2); e0 += q0 * gj; e1 += q1 * gj; e2 += q2 * gj; }
                const double Rest = 1.0232929922807541;   // 10^(0,1/10): Restbeitrag unter 0,1 dB
                if (s0 + e0 < Rest * s0 && s1 + e1 < Rest * s1 && s2 + e2 < Rest * s2) break;
            }
            aus[0] = Math.Max(1, s0); aus[1] = Math.Max(1, s1); aus[2] = Math.Max(1, s2);
        }
        /// <summary>Etappe 2 (RohrKarten.Gewinn): Spiegelquellen bis |m| + |n| = 3, ohne Luft - nur fuer den Vergleich (RohrFrueh aus).</summary>
        public static double GewinnEtappe2(double w, double h, double a, double r, double x, double alpha)
        {
            const int ord = 3;
            double L = a + r + x, R = 1 - alpha, L2 = L * L, s = 0;
            int mMax = Math.Min(ord, Grenze(L, a, x));
            for (int m = -mMax; m <= mMax; m++)
                for (int n = -Math.Min(mMax, ord - Math.Abs(m)); n <= Math.Min(mMax, ord - Math.Abs(m)); n++)
                    s += Math.Pow(R, Math.Abs(m) + Math.Abs(n)) * L2 / (L2 + m * w * m * w + n * h * n * h);
            return Math.Max(1, s);
        }
        static int Grenze(double L, double a, double x)
        {
            double g = 1e6;
            if (a > 0.5) g = Math.Min(g, L / (2 * a));
            if (x > 0.5) g = Math.Min(g, L / (2 * x));
            return (int)Math.Floor(g + 1e-9);
        }
        public static bool Biegung(RohrQuer a, RohrQuer b)
            => Math.Max(a.W, b.W) <= Gleich * Math.Max(0.3, Math.Min(a.W, b.W)) && Math.Max(a.H, b.H) <= Gleich * Math.Max(0.3, Math.Min(a.H, b.H));
    }
}
