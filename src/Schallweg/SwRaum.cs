using System;
using System.Collections.Generic;

namespace Materialklang.Schallweg
{
    // ------------------------------------------------------------------------------------------
    //  0.8.8 Wegsuche, Livetest-Fix (Factory 10.11.2026): Raumanteil (Diffusfeld) entlang eines gefundenen Wegs.
    //  Befund: Die Suche kannte nur Beugung um Kanten. In Hallen und Fluren mit harten Waenden traegt aber das Nachhallfeld den
    //  Schall in die Nachbarraeume (Sabine/Kuttruff: hinter einer Oeffnung S trifft die Leistung W S / A auf den naechsten Raum,
    //  dort herrscht 4 P / A). Die Suche war "um Ecken / ueber Raeume" im Median 14 dB (250 Hz) bis 38 dB (4 kHz) leiser als die
    //  Mod, deren Raumweg genau diesen Anteil rechnet.
    //  Loesung: Die Raumfolge kommt aus der GEOMETRIE des gefundenen Wegs (nicht aus den Portalrouten des Spiels): der Weg wird in
    //  Oeffnungen, die er durchquert, sind die Spielportale, deren Rechteck er kreuzt (alle Portale des Spiels, auch die grossen Tore
    //  ohne Netzknoten); ihre beiden Raeume geben die Raumfolge. Startraum = kleinster Kasten (SpatialAudioRoom-Area) um die Quelle,
    //  Endraum der um den Hoerer. Zwischen zwei Raeumen fliesst die Nachhallenergie durch ALLE offenen Oeffnungen, die sie verbinden
    //  (P = I S je Oeffnung, Kuttruff): ein Uebergang zaehlt darum mit der Summe aller Spielportale zwischen den beiden Raeumen (der
    //  gekreuzte gibt die Lage), nicht nur mit dem, durch den der direkte Weg geht. Liegt die Seite vor einer Oeffnung nicht im bisherigen
    //  Raum und verbindet auch kein Spielportal die beiden (das Spiel kennt die Oeffnung nicht, z. B. Treppe Untergeschoss -> Halle),
    //  ist die Kopplung (Flaeche der unbekannten Oeffnung) nicht bestimmt und der Weg bekommt keinen Raumanteil (nur den geometrischen
    //  Teil; Raid-2-Fix: auch nicht mehr "ein Luftraum, weil sich die Kaesten beruehren" - Nachbarraeume beruehren sich immer an der
    //  gemeinsamen Wand; den Anteil ueber die bekannten Oeffnungen rechnet Graph() ueber den Raumgraphen) - Nachspiel: Keller ->
    //  Eingangshalle als ein Luftraum gerechnet gab 0 dB ueber 42 m, die Treppe dazwischen ist aber eine kleine Oeffnung. Die Rechnung selbst ist die der Mod
    //  (RaumwegKern.Diffus: Raumwinkel der ersten Oeffnung + Diffusfeld des Quellraums, A = S alpha + offene Oeffnungen + 4 m V,
    //  Abfall nach Barron, am Hoerer 4 P / A). Gleicher Raum ohne Uebergang: Raid-3-Fix - Nachhallfeld des eigenen Raums (Eigen),
    //  sonst blieb hinter Deckung in einer Halle nur der Beugungsweg.
    //  Nur der Arbeitsfaden benutzt eine Instanz (Zustaende der Tueren schreibt er selbst aus seinem Eingang).
    // ------------------------------------------------------------------------------------------
    internal sealed class RaumKette
    {
        public int[] Portale;            // Raumportal-Indizes (WegDaten.Q*) in Wegrichtung Quelle -> Hoerer
        public string[] Raeume;          // Raumnamen (Gruppen mit '+')
        public double[] Db = new double[3];   // Pegel je Band gegenueber dem Freifeld in Direktlinie (<= 0)
        public V Austritt;               // Punkt auf der letzten Oeffnung (Richtung fuer den Mischer)
        public double Schirm;            // Raid-2-Fix: Abschirmung der Strecken im Freien bei 1 kHz (dB, in Db enthalten; Log)
        public ulong Id
        {
            get { ulong h = 1469598103934665603ul; foreach (int k in Portale) { h ^= (uint)k; h *= 1099511628211ul; } return (h & ~3ul) | 3ul; }
        }
    }

    internal sealed class RaumModell
    {
        public readonly int RN, QN;
        readonly bool[] draussen; readonly double[] flaeche, vol; readonly string[] name;
        readonly int[] bxRaum; readonly V[] bxM, bxX, bxY, bxZ; readonly double[] bxVol, bxL1, bxL2, bxL3, bxLo, bxHi; readonly int[][] raumBx;
        readonly Dictionary<long, int[]> gitter = new Dictionary<long, int[]>(); const double G = 10.0;
        readonly RaumPortal[] q; readonly int[] qA, qB, qNetz; readonly int[][] raumQ;
        readonly V[] qM, qU, qW, qN; readonly Dictionary<long, int[]> qGitter = new Dictionary<long, int[]>(); const double QG = 4.0;
        static long QZelle(V x) => Wegenetz.Schluessel((int)Math.Floor(x.X / QG), (int)Math.Floor(x.Y / QG), (int)Math.Floor(x.Z / QG));
        public readonly bool[] QZu;          // Zustand der Raumportale ohne Netzportal (live aus dem Spiel; Start offen)
        readonly double[] qTau;              // 3 je Raumportal: Transmissionsgrad des geschlossenen Teils je Band
        readonly double[] aRaum;             // Absorptionsflaeche je Raum beim aktuellen Tuerstand
        int aStand = int.MinValue;
        const int MaxPortale = 16;            // wie RaumwegNetz.MaxPortale
        public double Alpha = 0.1;           // wie Raumweg "Room paths: room absorption" (Abschnitt 0)
        readonly double[] luftM = new double[3], luftDb = new double[3];

        public RaumModell(WegDaten d)
        {
            RN = d.RN; QN = d.QN; draussen = d.RDraussen; flaeche = d.RFlaeche; vol = d.RVol; name = d.RName;
            int nb = d.BxN; bxRaum = d.BxRaum; bxM = new V[nb]; bxX = new V[nb]; bxY = new V[nb]; bxZ = new V[nb]; bxVol = new double[nb]; bxL1 = new double[nb]; bxL2 = new double[nb]; bxL3 = new double[nb];
            var tmp = new Dictionary<long, List<int>>(); bxLo = new double[3 * nb]; bxHi = new double[3 * nb];
            var proRaumBx = new List<int>[RN]; for (int r = 0; r < RN; r++) proRaumBx[r] = new List<int>();
            for (int i = 0; i < nb; i++)
            {
                bxM[i] = V.Aus(d.BxM, i); bxX[i] = V.Aus(d.BxX, i); bxY[i] = V.Aus(d.BxY, i); bxZ[i] = V.Aus(d.BxZ, i);
                bxL1[i] = bxX[i].Len; bxL2[i] = bxY[i].Len; bxL3[i] = bxZ[i].Len; bxVol[i] = 8 * bxL1[i] * bxL2[i] * bxL3[i];
                double ex = Math.Abs(bxX[i].X) + Math.Abs(bxY[i].X) + Math.Abs(bxZ[i].X), ez = Math.Abs(bxX[i].Z) + Math.Abs(bxY[i].Z) + Math.Abs(bxZ[i].Z);
                double ey = Math.Abs(bxX[i].Y) + Math.Abs(bxY[i].Y) + Math.Abs(bxZ[i].Y);
                bxLo[3 * i] = bxM[i].X - ex; bxLo[3 * i + 1] = bxM[i].Y - ey; bxLo[3 * i + 2] = bxM[i].Z - ez;
                bxHi[3 * i] = bxM[i].X + ex; bxHi[3 * i + 1] = bxM[i].Y + ey; bxHi[3 * i + 2] = bxM[i].Z + ez;
                if (bxRaum[i] >= 0 && bxRaum[i] < RN) proRaumBx[bxRaum[i]].Add(i);
                int x0 = (int)Math.Floor((bxM[i].X - ex) / G), x1 = (int)Math.Floor((bxM[i].X + ex) / G), z0 = (int)Math.Floor((bxM[i].Z - ez) / G), z1 = (int)Math.Floor((bxM[i].Z + ez) / G);
                if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 40000) continue;   // riesige Kaesten (> 2 km) nicht ins Gitter
                for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++)
                { long k = Wegenetz.Schluessel(x, 0, z); if (!tmp.TryGetValue(k, out var l)) tmp[k] = l = new List<int>(); l.Add(i); }
            }
            foreach (var kv in tmp) gitter[kv.Key] = kv.Value.ToArray();
            raumBx = new int[RN][]; for (int r = 0; r < RN; r++) raumBx[r] = proRaumBx[r].ToArray();
            q = new RaumPortal[QN]; qA = (int[])d.QRaumA.Clone(); qB = (int[])d.QRaumB.Clone(); qNetz = d.QNetz; QZu = new bool[QN]; QGeoeffnet = new bool[QN]; Schliessbar = d.PSchliessbar; qTau = new double[3 * QN];
            var proRaum = new List<int>[RN]; for (int r = 0; r < RN; r++) proRaum[r] = new List<int>();
            qM = new V[QN]; qU = new V[QN]; qW = new V[QN]; qN = new V[QN]; var qtmp = new Dictionary<long, List<int>>();
            for (int i = 0; i < QN; i++)
            {
                var ax = new[] { V.Aus(d.QAx, i), V.Aus(d.QAy, i), V.Aus(d.QAz, i) };
                Array.Sort(ax, (a, b) => b.Len.CompareTo(a.Len));
                V u = ax[0].Norm(), w = ax[1].Norm(), nn = V.Cross(ax[0], ax[1]).Norm(), m = V.Aus(d.QMitte, i);
                q[i] = new RaumPortal { Mitte = V3v(m), Rechts = V3v(u), Oben = V3v(w), Normale = V3v(nn), HalbX = ax[0].Len, HalbY = ax[1].Len, Offen = 1, Tau = 0, TauMitte = 0, TauHoch = 0, Baender = true };
                qM[i] = m; qU[i] = u; qW[i] = w; qN[i] = nn;
                double rr = Math.Sqrt(ax[0].Len * ax[0].Len + ax[1].Len * ax[1].Len) + 0.3 + QG / 2;
                int x0 = (int)Math.Floor((m.X - rr) / QG), y0 = (int)Math.Floor((m.Y - rr) / QG), z0 = (int)Math.Floor((m.Z - rr) / QG);
                int x1 = (int)Math.Floor((m.X + rr) / QG), y1 = (int)Math.Floor((m.Y + rr) / QG), z1 = (int)Math.Floor((m.Z + rr) / QG);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) <= 200000)
                    for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++)
                    { long k = Wegenetz.Schluessel(x, y, z); if (!qtmp.TryGetValue(k, out var l)) qtmp[k] = l = new List<int>(); l.Add(i); }
                if (qA[i] >= 0 && qA[i] < RN) proRaum[qA[i]].Add(i);
                if (qB[i] >= 0 && qB[i] < RN && qB[i] != qA[i]) proRaum[qB[i]].Add(i);
            }
            // Raid-2-Fix: Portale mit nur EINEM Raum (91 von 386 auf Factory: Fenster, Dachoeffnungen, Aussentueren) fuehren ins Freie.
            // Das Spiel fuehrt sie so (Mod-Route Area_3 >#166> BigRoom >#39> draussen >#138> admin_rooms_4: #138 = Q#193
            // "admin_rooms_4|-"); der Export kennt die Gegenseite nicht (Raum-Pid 0). Gegenseite = der Raum im Freien, dessen Kasten den
            // Punkt 1 m hinter dem Portal enthaelt, sonst der groesste Raum im Freien.
            {
                int grossDr = -1; for (int r = 0; r < RN; r++) if (draussen[r] && (grossDr < 0 || vol[r] > vol[grossDr])) grossDr = r;
                for (int i = 0; i < QN && grossDr >= 0; i++)
                {
                    if ((qA[i] < 0) == (qB[i] < 0)) continue;
                    int bek = qA[i] >= 0 ? qA[i] : qB[i]; if (bek >= RN || draussen[bek]) continue;
                    V m = qM[i], nn = qN[i]; int dr = -1;
                    foreach (double sg in new[] { 1.0, -1.0 })
                    {
                        int r = RaumVon(m + nn * sg); if (r != bek && r >= 0 && draussen[r]) { dr = r; break; }
                    }
                    if (dr < 0) dr = grossDr;
                    if (qA[i] < 0) qA[i] = dr; else qB[i] = dr;
                    proRaum[dr].Add(i);
                }
            }
            raumQ = new int[RN][]; for (int r = 0; r < RN; r++) raumQ[r] = proRaum[r].ToArray();
            qSeiteA = new sbyte[QN];
            for (int i = 0; i < QN; i++)
            {
                int vorn = RaumVon(qM[i] + qN[i] * 0.5), hinten = RaumVon(qM[i] - qN[i] * 0.5);
                qSeiteA[i] = (sbyte)(vorn == qA[i] && hinten != qA[i] ? 1 : hinten == qA[i] && vorn != qA[i] ? -1 : vorn == qB[i] && hinten != qB[i] ? -1 : hinten == qB[i] && vorn != qB[i] ? 1 : 0);
            }
            foreach (var kv in qtmp) qGitter[kv.Key] = kv.Value.ToArray();
            aRaum = new double[RN];
            for (int b = 0; b < 3; b++) RaumwegKern.LuftWerte(Suche.F[b], out luftM[b], out luftDb[b]);
        }

        static V3 V3v(V v) => new V3(v.X, v.Y, v.Z);

        public string Name(int r) => r >= 0 && r < RN ? name[r] : "?";
        /// <summary>Raid-3-Fix: Lage eines Punkts fuer die Mehrfachbeugung: 1 = in einem Innenraum, -1 = im Freien (Raum draussen oder
        /// ohne Flaeche), 0 = in keinem Kasten. Nur lesend (Kaesten, Gitter) - darf aus der Suche desselben Platzes gerufen werden.</summary>
        public int Lage(V p) { int r = RaumVon(p); return r < 0 ? 0 : draussen[r] || flaeche[r] <= 0 ? -1 : 1; }

        /// <summary>Raum um p: kleinster Kasten, der p enthaelt (5 cm Toleranz). -1 = keiner.</summary>
        public int RaumVon(V p)
        {
            if (!gitter.TryGetValue(Wegenetz.Schluessel((int)Math.Floor(p.X / G), 0, (int)Math.Floor(p.Z / G)), out var l)) return -1;
            // Raid-2-Fix: Raeume mit Spielportalen vor Raeumen ohne (Factory hat Doppel wie admin_rooms_4 R7/R49: derselbe Kasten bis auf
            // 10 cm, nur einer hat das Portal - der kleinere ohne Portal machte den Hoererraum unerreichbar, 257 Zeilen ohne Raumanteil)
            int best = -1, bestO = -1; double bv = double.MaxValue, bvO = double.MaxValue;
            foreach (int i in l)
            {
                if (bxVol[i] >= bv && bxVol[i] >= bvO) continue;
                V r = p - bxM[i];
                if (Math.Abs(V.Dot(r, bxX[i])) > bxL1[i] * (bxL1[i] + 0.05) || Math.Abs(V.Dot(r, bxY[i])) > bxL2[i] * (bxL2[i] + 0.05) || Math.Abs(V.Dot(r, bxZ[i])) > bxL3[i] * (bxL3[i] + 0.05)) continue;
                int ri = bxRaum[i];
                if (bxVol[i] < bv) { best = ri; bv = bxVol[i]; }
                if (raumQ != null && ri >= 0 && ri < RN && raumQ[ri].Length > 0 && bxVol[i] < bvO) { bestO = ri; bvO = bxVol[i]; }
            }
            return bestO >= 0 ? bestO : best;
        }

        /// <summary>Zustand der Raumportale fuer diesen Tuerstand: offen/zu und tau je Band. zuNetz = Suche.Zu, tauNetz(i, b) = Daemmung.</summary>
        public void Zustand(int stand, bool[] zuNetz, Func<int, int, double> daemmungNetz, Func<int, double> daemmungFrei)
        {
            if (stand == aStand) return;
            aStand = stand;
            for (int i = 0; i < QN; i++)
            {
                int ni = qNetz[i]; bool zu = ni >= 0 && ni < zuNetz.Length ? zuNetz[ni] : QZu[i];
                bool geo = offenAnteil != null && (ni >= 0 ? !(Schliessbar != null && ni < Schliessbar.Length && Schliessbar[ni]) : !QGeoeffnet[i]);
                q[i].Offen = zu ? 0 : geo ? offenAnteil[i] : 1;
                for (int b = 0; b < 3; b++) qTau[3 * i + b] = q[i].Offen < 1 ? Math.Pow(10, -(ni >= 0 ? daemmungNetz(ni, b) : daemmungFrei(b)) / 10) : 1;
                q[i].Tau = qTau[3 * i]; q[i].TauMitte = qTau[3 * i + 1]; q[i].TauHoch = qTau[3 * i + 2];
            }
            for (int r = 0; r < RN; r++)
            {
                double offen = 0; foreach (int i in raumQ[r]) offen += q[i].Flaeche * q[i].Offen;
                aRaum[r] = draussen[r] || flaeche[r] <= 0 ? -1 : RaumwegKern.Absorption(flaeche[r], Alpha, offen);
            }
        }

        // ---------------------------------------------------------------- Raumfolge eines Wegs

        readonly List<List<int>> gruppen = new List<List<int>>();   // je Abschnitt die Raeume (mehrere = ein Luftraum)
        readonly List<int> folge = new List<int>();                  // Raumportal zwischen Abschnitt k und k+1
        readonly List<V> uebergang = new List<V>();                  // Kreuzungspunkt des Wegs mit diesem Portal (Lage)
        readonly List<int> vonRaum = new List<int>(), nachRaum = new List<int>();   // die beiden Raeume des Uebergangs

        readonly List<KeyValuePair<double, int>> kreuz = new List<KeyValuePair<double, int>>(); readonly List<V> kreuzX = new List<V>(), kreuzR = new List<V>();
        readonly List<int> kand = new List<int>();

        /// <summary>Kreuzt die Strecke a -> b das Rechteck von Raumportal i (Mittelebene, 0,3 m Rand)? t = Anteil, x = Kreuzungspunkt.</summary>
        bool Kreuzt(int i, V a, V b, out double t, out V x)
        {
            t = 0; x = default;
            const double eps = 0.005, rand = 0.3;
            double da = V.Dot(a - qM[i], qN[i]), db = V.Dot(b - qM[i], qN[i]);
            if (!((da < -eps && db >= -eps) || (da > eps && db <= eps))) return false;
            t = Math.Abs(da - db) < 1e-12 ? 1 : da / (da - db); x = a + (b - a) * t; V r = x - qM[i];
            return Math.Abs(V.Dot(r, qU[i])) <= q[i].HalbX + rand && Math.Abs(V.Dot(r, qW[i])) <= q[i].HalbY + rand;
        }

        /// <summary>Raumfolge entlang der Punkte (Quelle ... Hoerer) aus den gekreuzten Spielportalen. false = kein Raumwechsel.</summary>
        public bool Folge(List<V> pts, List<int> portaleAus)
        {
            unbestimmt = false; gruppen.Clear(); folge.Clear(); uebergang.Clear(); vonRaum.Clear(); nachRaum.Clear(); portaleAus.Clear(); kreuz.Clear(); kreuzX.Clear(); kreuzR.Clear(); kand2.Clear();
            // 1. gekreuzte Portale in Wegrichtung
            double s0 = 0;
            for (int sg = 0; sg + 1 < pts.Count; sg++)
            {
                V a = pts[sg], b = pts[sg + 1]; double len = V.Abstand(a, b); int m = Math.Max(1, (int)Math.Ceiling(len / (QG / 2)));
                kand.Clear(); long vor = long.MinValue;
                for (int j = 0; j <= m; j++)
                {
                    long c = QZelle(a + (b - a) * ((double)j / m)); if (c == vor) continue; vor = c;
                    if (!qGitter.TryGetValue(c, out var l)) continue;
                    foreach (int i in l) if (!kand.Contains(i)) kand.Add(i);
                }
                foreach (int i in kand)
                    if (Kreuzt(i, a, b, out double t, out V x))
                    { kreuz.Add(new KeyValuePair<double, int>(s0 + t * len, kreuz.Count)); kreuzX.Add(x); kreuzR.Add(len > 1e-9 ? (b - a) * (1 / len) : new V(0, 0, 0)); kand2.Add(i); }
                s0 += len;
            }
            kreuz.Sort((x, y) => x.Key.CompareTo(y.Key));
            // 2. Raeume: Quellraum, je Kreuzung der Raum auf der anderen Seite
            gruppen.Add(new List<int>()); int r0 = RaumVon(pts[0]); if (r0 >= 0) gruppen[0].Add(r0);
            int letzte = -1, letzteSeite = 0;
            foreach (var kv in kreuz)
            {
                int k = kv.Value, i = kand2[k]; V x = kreuzX[k], dir = kreuzR[k];
                // dieselbe Oeffnung gleich noch einmal in DERSELBEN Richtung = doppelte Kreuzung (Ecke auf der Ebene); in der Gegenrichtung
                // geht der Weg zurueck (Raid-2-Fix C: A >#302> B >#302> A wurde nicht erkannt und #302 spaeter doppelt gezaehlt)
                int seite = V.Dot(dir, qN[i]) >= 0 ? 1 : -1;
                if (i == letzte && seite == letzteSeite) continue; letzte = i; letzteSeite = seite;
                var g = gruppen[gruppen.Count - 1];
                int a = qA[i], b = qB[i];
                int vorR = a >= 0 && g.Contains(a) ? a : b >= 0 && g.Contains(b) ? b : -1;
                if (vorR < 0)
                {
                    // die Seite vor der Oeffnung gehoert nicht zum bisherigen Raum: ueber Spielportale verbunden -> Uebergang dorthin,
                    // sonst ueber den freien Weg ein Luftraum mit ihm
                    int hinten = RaumVon(x + dir * 0.3);
                    vorR = hinten >= 0 && hinten == a ? b : hinten >= 0 && hinten == b ? a : (a >= 0 ? a : b);
                    if (vorR < 0)
                    {
                        // Portal mit nur einem bekannten Raum, und der liegt hinter ihm: dorthin wie ueber eine unbekannte Oeffnung
                        int bekannt = a >= 0 ? a : b;
                        if (bekannt >= 0 && !g.Contains(bekannt) && !Schleife(bekannt)) Dazu(bekannt, x);
                        continue;
                    }
                    if (!g.Contains(vorR)) Dazu(vorR, x);
                }
                int nach = vorR == a ? b : a;
                if (nach < 0 || nach == vorR) continue;   // Portal ohne zweiten Raum: kein Raumwechsel
                if (Schleife(nach)) continue;
                folge.Add(i); uebergang.Add(x); vonRaum.Add(vorR); nachRaum.Add(nach); gruppen.Add(new List<int> { nach });
            }
            // 3. Hoererraum
            int rh = RaumVon(pts[pts.Count - 1]);
            if (rh >= 0 && !gruppen[gruppen.Count - 1].Contains(rh) && !Schleife(rh)) Dazu(rh, pts[pts.Count - 1]);
            kand2.Clear();
            if (unbestimmt) { folge.Clear(); return false; }
            portaleAus.AddRange(folge);
            return folge.Count > 0;
        }
        readonly List<int> kand2 = new List<int>();
        bool unbestimmt;   // Raumwechsel ueber eine unbekannte Oeffnung zwischen getrennten Raeumen: kein Raumanteil

        /// <summary>Diagnose (Offline-Test): Raeume der Punkte, Portale in der Naehe des Wegs und ihre Kreuzungen.</summary>
        public string Erklaeren(List<V> pts)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in pts) sb.Append($" [{p} {Name(RaumVon(p))}]");
            var nah = new HashSet<int>();
            for (int sg = 0; sg + 1 < pts.Count; sg++)
            {
                V a = pts[sg], b = pts[sg + 1]; double len = V.Abstand(a, b); int m = Math.Max(1, (int)Math.Ceiling(len / (QG / 2)));
                for (int j = 0; j <= m; j++) if (qGitter.TryGetValue(QZelle(a + (b - a) * ((double)j / m)), out var l)) foreach (int i in l) nah.Add(i);
            }
            foreach (int i in nah)
            {
                double bestAbst = double.MaxValue; bool kr = false;
                for (int sg = 0; sg + 1 < pts.Count; sg++)
                {
                    if (Kreuzt(i, pts[sg], pts[sg + 1], out _, out _)) kr = true;
                    V a = pts[sg], b = pts[sg + 1], ab = b - a; double tt = H.Clamp(V.Dot(qM[i] - a, ab) / Math.Max(V.Dot(ab, ab), 1e-9), 0, 1);
                    bestAbst = Math.Min(bestAbst, V.Abstand(a + ab * tt, qM[i]));
                }
                if (bestAbst < 6 || kr) sb.Append($"\n      Q#{i} {2 * q[i].HalbX:0.0}x{2 * q[i].HalbY:0.0} m bei {qM[i]} {Name(qA[i])}|{Name(qB[i])} Abstand {bestAbst:0.0} m{(kr ? " GEKREUZT" : "")}");
            }
            return sb.ToString();
        }

        /// <summary>Raum r an den letzten Abschnitt anhaengen: verbindet ihn ein Spielportal mit einem Raum des Abschnitts, als Uebergang
        /// (Lage: das groesste dieser Portale), sonst als Teil desselben Luftraums.</summary>
        void Dazu(int r, V bei)
        {
            var g = gruppen[gruppen.Count - 1]; int best = -1, von = -1; double bf = -1;
            foreach (int raum in g)
                foreach (int i in raumQ[raum])
                {
                    int anderer = qA[i] == raum ? qB[i] : qA[i];
                    if (anderer != r || q[i].Flaeche <= bf) continue;
                    best = i; bf = q[i].Flaeche; von = raum;
                }
            if (best < 0)
            {
                // Raid-2-Fix (A): KEIN Luftraum mehr, nur weil sich die Kaesten beruehren - Nachbarraeume beruehren sich immer an der
                // gemeinsamen Wand (Livetest 10.11. 03:02: Area_3+admin_rooms_2 >#155> Outdoor, +7,5 dB im Median, die 1,3-m2-Oeffnung
                // #166 fiel weg). Ohne Spielportal ist die Kopplung unbekannt: dieser Weg gibt keinen Raumanteil; den Anteil ueber die
                // bekannten Oeffnungen rechnet Graph() ueber den Raumgraphen.
                if (g.Count == 0) g.Add(r); else unbestimmt = true;
                return;
            }
            if (Schleife(r)) return;   // Raid-2-Fix (C): zurueck in einen frueheren Raum = Schleife abschneiden, nicht dieselbe Oeffnung zweimal
            folge.Add(best); uebergang.Add(qM[best]); vonRaum.Add(von); nachRaum.Add(r); gruppen.Add(new List<int> { r });
        }

        /// <summary>Ist r schon ein frueherer Abschnitt? Dann zurueck zu ihm (Schleife abschneiden), true.</summary>
        bool Schleife(int r)
        {
            for (int t = 0; t + 1 < gruppen.Count; t++)
                if (gruppen[t].Contains(r))
                {
                    gruppen.RemoveRange(t + 1, gruppen.Count - t - 1);
                    folge.RemoveRange(t, folge.Count - t); uebergang.RemoveRange(t, uebergang.Count - t);
                    vonRaum.RemoveRange(t, vonRaum.Count - t); nachRaum.RemoveRange(t, nachRaum.Count - t);
                    return true;
                }
            return false;
        }

        /// <summary>Schluessel der zuletzt bestimmten Raumfolge (Folge oder Graph): die Raeume in Wegrichtung. Gleiche Raumfolge = gleicher
        /// Raumanteil (alle Oeffnungen zwischen zwei Raeumen zaehlen ohnehin zusammen), darum wird jede Folge nur einmal gerechnet.</summary>
        public ulong FolgeSchluessel
        {
            get
            {
                ulong h = 1469598103934665603ul;
                for (int k = 0; k < gruppen.Count; k++)
                {
                    foreach (int r in gruppen[k]) { h ^= (uint)(r + 1); h *= 1099511628211ul; }
                    if (k < folge.Count && Aussen(k)) { h ^= 0x10000ul + (uint)folge[k]; h *= 1099511628211ul; }   // im Freien zaehlt das Portal selbst
                    h ^= 0xFFFFul; h *= 1099511628211ul;
                }
                return h;
            }
        }

        // ---------------------------------------------------------------- Raid-2-Fix (B): Raumanteil ueber den Raumgraphen
        //  Befund Raid 2 (10.11.2026): der Raumanteil entstand nur aus den Raumfolgen der GEFUNDENEN Wege. Ging der einzige Weg (Budget)
        //  durch eine geschlossene Tuer, fehlte der Halle-zu-Halle-Anteil ueber die 125-m2-Oeffnung AudioPortal#119 ganz (Area_3 ->
        //  Area_1: Mod -0,3 dB, Suche -25 bis -60 dB; 869 Zeilen ohne Raumanteil, Median 12,6 dB zu leise). Physik: das Nachhallfeld
        //  fliesst durch ALLE offenen Oeffnungen, unabhaengig davon, welchen Beugungsweg die Kantensuche gefunden hat (Kuttruff, Room
        //  Acoustics Kap. 5: gekoppelte Raeume; P = I S je Oeffnung).
        //  Loesung (wie die Mod ueber die Portalrouten des Spiels, RaumwegNetz/RaumwegKern): Quellraum -> Hoererraum ueber den Raumgraphen
        //  aller Spielportale (Zustand je Portal live: Netzportal = Tuerzustand der Suche, sonst QZu aus dem Spiel). Ein Uebergang zwischen
        //  zwei Raeumen fasst alle Portale zwischen ihnen zusammen (wie in Rechnen). Die Raumfolgen werden best-first nach einer oberen
        //  Schranke ihrer Energie aufgezaehlt (je Uebergang S t / A des Raums davor, an der Quelle Omega / 4 pi + S t / A, im Freien
        //  S / 2 pi r^2; jeder Faktor >= dem in RaumwegKern.Kette ohne Abstandsabfall und Luft), nur einfache Folgen (kein Raum zweimal),
        //  die besten GraphKand exakt mit Rechnen (= RaumwegKern.Diffus wie die Mod) und davon die lautesten GraphWege (Mod: weicher Rang
        //  N = 3) innerhalb GraphSpanneDb als Raumanteile.
        public const int GraphWege = 3; const int GraphKand = 12, GraphPops = 2000, GraphSchirmMax = 60, GraphTiefe = 10; const double GraphSpanneDb = 30;
        sealed class Uebergang { public int Nach, Rep; public double S, ST, RepST; }
        List<Uebergang>[] nachbarn; int nachbarnStand = int.MinValue;
        readonly Dictionary<long, List<int[]>> graphCache = new Dictionary<long, List<int[]>>();
        public int DGraphNeu, DGraphCache;   // Diagnose: Raumfolgen neu aufgezaehlt / aus dem Zwischenspeicher

        void NachbarnBauen()
        {
            if (nachbarn == null) { nachbarn = new List<Uebergang>[RN]; for (int r = 0; r < RN; r++) nachbarn[r] = new List<Uebergang>(); }
            for (int r = 0; r < RN; r++)
            {
                var l = nachbarn[r]; l.Clear();
                foreach (int i in raumQ[r])
                {
                    int o = qA[i] == r ? qB[i] : qA[i]; if (o < 0 || o >= RN || o == r) continue;
                    // ins Freie / aus dem Freien je Portal einzeln: dort zaehlen Lage und Abschirmung (Rechnen), nicht nur die Flaeche
                    bool aussen = aRaum[r] < 0 || aRaum[o] < 0;
                    Uebergang u = null; if (!aussen) foreach (var x in l) if (x.Nach == o) { u = x; break; }
                    if (u == null) { u = new Uebergang { Nach = o, Rep = i, RepST = -1 }; l.Add(u); }
                    double st = q[i].Flaeche * (q[i].Offen + (1 - q[i].Offen) * qTau[3 * i + 1]);
                    u.S += q[i].Flaeche; u.ST += st;
                    if (st > u.RepST || (st == u.RepST && q[i].Flaeche > q[u.Rep].Flaeche)) { u.Rep = i; u.RepST = st; }
                }
            }
            nachbarnStand = aStand; graphCache.Clear();
        }

        V InsRechteck(int i, V p)
        {
            V r = p - qM[i];
            double u = H.Clamp(V.Dot(r, qU[i]), -q[i].HalbX, q[i].HalbX), w = H.Clamp(V.Dot(r, qW[i]), -q[i].HalbY, q[i].HalbY);
            return qM[i] + qU[i] * u + qW[i] * w;
        }

        // best-first ueber Teilfolgen (Knoten im Pool, Vorgaenger-Index)
        struct GKnoten { public int Raum, Vor, Tiefe, Portal, SchirmVon; public V Punkt; public double E; public bool Ende, SchirmOffen; }
        readonly List<GKnoten> gk = new List<GKnoten>();
        double[] gPrio = new double[256]; int[] gIdx = new int[256]; int gN;
        void GRein(double p, int i)
        {
            if (gN == gPrio.Length) { Array.Resize(ref gPrio, 2 * gN); Array.Resize(ref gIdx, 2 * gN); }
            int c = gN++; while (c > 0) { int v = (c - 1) >> 1; if (gPrio[v] >= p) break; gPrio[c] = gPrio[v]; gIdx[c] = gIdx[v]; c = v; }
            gPrio[c] = p; gIdx[c] = i;
        }
        int GRaus()
        {
            int top = gIdx[0]; gN--; if (gN == 0) return top;
            double p = gPrio[gN]; int x = gIdx[gN], c = 0;
            while (true) { int l = 2 * c + 1; if (l >= gN) break; int r = l + 1, m = r < gN && gPrio[r] > gPrio[l] ? r : l; if (gPrio[m] <= p) break; gPrio[c] = gPrio[m]; gIdx[c] = gIdx[m]; c = m; }
            gPrio[c] = p; gIdx[c] = x; return top;
        }

        List<int[]> Folgen(int rs, int rh, V q0, V h0)
        {
            var aus = new List<int[]>(); gk.Clear(); gN = 0;
            gk.Add(new GKnoten { Raum = rs, Vor = -1, Tiefe = 0, Punkt = q0, E = 0 }); GRein(0, 0);
            double endMax = aRaum[rh] > 0 ? 4.0 / Math.Max(aRaum[rh], 1) : 1.0 / (2 * Math.PI * 0.25);
            int pops = 0, schirme = 0; double best = double.NegativeInfinity;
            // Grenze -60 dB gegenueber dem Freifeld in Direktlinie (bezug), mit der groessten moeglichen Endstufe (4/A bzw. Halbraum in 0,5 m)
            double d0 = Math.Max(V.Abstand(q0, h0), 0.5), grenze = Math.Log10(1.0 / (4 * Math.PI * d0 * d0)) - 6 - Math.Log10(endMax);
            while (gN > 0 && pops++ < GraphPops && aus.Count < GraphKand)
            {
                double pr = gPrio[0]; int ni = GRaus(); var n = gk[ni];
                if (pr < best - GraphSpanneDb / 10 - 1) break;   // alle weiteren mehr als ~40 dB unter der lautesten Folge
                // Abschirmung der Strecke im Freien erst, wenn der Knoten oben im Heap ist (Schaetzung ohne sie ist eine obere Schranke):
                // Factory hat ~200 Portale ins Freie - alle Paare vorab zu pruefen kostete bis 0,5 s je Raumpaar
                if (n.SchirmOffen && schirme < GraphSchirmMax)
                {
                    schirme++;
                    // -3: nur die Strecke zum Hoerer; -2: von der Quelle (im Freien) zum Portal; sonst vom vorigen Portal; dazu am Ende im Freien die zum Hoerer
                    double sch = n.SchirmVon == -3 ? 0 : n.SchirmVon == -2 ? Schirm(q0, AussenPunkt(n.Portal, qM[n.Portal], q0))[1] : SchirmPaar(n.SchirmVon, n.Portal, h0)[1];
                    if (n.Ende && aRaum[rh] < 0) sch += SchirmPaar(n.Portal, -1, h0)[1];
                    n.E -= sch / 10; n.SchirmOffen = false; gk[ni] = n;
                    if (n.E >= grenze) GRein(n.E, ni);
                    continue;
                }
                if (n.Ende)
                {
                    var f = new int[2 * n.Tiefe + 1]; for (int x = ni, k = n.Tiefe; x >= 0; x = gk[x].Vor, k--) { f[2 * k] = gk[x].Raum; if (k > 0) f[2 * k - 1] = gk[x].Portal; }
                    aus.Add(f); if (pr > best) best = pr; continue;
                }
                int u = n.Raum; double au = aRaum[u];
                foreach (var ub in nachbarn[u])
                {
                    int v = ub.Nach; if (ub.ST <= 0) continue;
                    bool drin = false; for (int x = ni; x >= 0; x = gk[x].Vor) if (gk[x].Raum == v) { drin = true; break; }
                    if (drin) continue;
                    int i = ub.Rep; V pkt = InsRechteck(i, n.Punkt); double t = ub.ST / ub.S, fak;
                    if (n.Vor < 0) fak = RaumwinkelAnteil(i, q0) * t + (au > 0 ? ub.ST / Math.Max(au, 1) : 0);
                    else if (au > 0) fak = ub.ST / Math.Max(au, 1);
                    else { double r = Math.Max(V.Abstand(n.Punkt, pkt), 0.5); fak = Math.Min(1, ub.S / (2 * Math.PI * r * r)) * t; }
                    double e = n.E + Math.Log10(Math.Max(Math.Min(fak, 1), 1e-30));
                    if (e < grenze) continue;   // unter -60 dB
                    // Strecke im Freien bis zu diesem Portal (SchirmVon: vorheriges Portal, -2 = von der Quelle) bzw. zum Hoerer (-3) offen
                    bool sOffen = au < 0; int sVon = au < 0 ? (n.Vor < 0 ? -2 : n.Portal) : -1;
                    if (v == rh)
                    {
                        double ende = aRaum[rh] > 0 ? endMax : 1.0 / (2 * Math.PI * Math.Pow(Math.Max(V.Abstand(pkt, h0), 0.5), 2));
                        double pe = e + Math.Log10(ende / endMax);
                        if (!sOffen && aRaum[rh] < 0) { sOffen = true; sVon = -3; }
                        gk.Add(new GKnoten { Raum = v, Vor = ni, Tiefe = n.Tiefe + 1, Portal = i, Punkt = pkt, E = pe, Ende = true, SchirmOffen = sOffen, SchirmVon = sVon }); GRein(pe, gk.Count - 1);
                    }
                    else if (n.Tiefe + 1 < GraphTiefe && n.Tiefe + 1 < MaxPortale) { gk.Add(new GKnoten { Raum = v, Vor = ni, Tiefe = n.Tiefe + 1, Portal = i, Punkt = pkt, E = e, SchirmOffen = sOffen, SchirmVon = sVon }); GRein(e, gk.Count - 1); }
                }
            }
            return aus;
        }

                /// <summary>Raumfolge f (Raumindizes Quelle -> Hoerer) als Uebergaenge setzen (wie Folge), Lage je Uebergang = Portalpunkt des
        /// staerksten Portals zwischen den beiden Raeumen (RaumwegKern.PortalPunkt wie die Mod). false = ein Uebergang fehlt.</summary>
        bool FolgeSetzen(int[] f, V q0, V h0)
        {
            gruppen.Clear(); folge.Clear(); uebergang.Clear(); vonRaum.Clear(); nachRaum.Clear();
            int n = (f.Length - 1) / 2; if (n < 1 || n > MaxPortale) return false;
            V vor = q0; gruppen.Add(new List<int> { f[0] });
            for (int k = 0; k < n; k++)
            {
                int i = f[2 * k + 1]; V ziel = k + 1 < n ? qM[f[2 * k + 3]] : h0;
                var pp = RaumwegKern.PortalPunkt(q[i], V3v(vor), V3v(ziel)); V x = new V(pp.X, pp.Y, pp.Z);
                folge.Add(i); uebergang.Add(x); vonRaum.Add(f[2 * k]); nachRaum.Add(f[2 * k + 2]); gruppen.Add(new List<int> { f[2 * k + 2] });
                vor = x;
            }
            return true;
        }

        /// <summary>Diagnose (Offline): Kandidaten der Raumgraph-Aufzaehlung mit Schaetzung und exaktem Wert.</summary>
        public string GraphErklaeren(V q0, V h0, int[] erzwingen = null)
        {
            int rs = RaumVon(q0), rh = RaumVon(h0); var sb = new System.Text.StringBuilder();
            if (rs < 0 || rh < 0 || rs == rh) return "kein Raumwechsel";
            if (nachbarnStand != aStand || nachbarn == null) NachbarnBauen();
            var folgen = Folgen(rs, rh, q0, h0); if (erzwingen != null) folgen.Add(erzwingen);
            foreach (var f in folgen)
            {
                if (!FolgeSetzen(f, q0, h0)) { sb.Append("\n   ungueltig"); continue; }
                var k = Rechnen(q0, h0);
                sb.Append("\n   ").Append(k == null ? "null" : $"{k.Db[1]:F1} dB (Schirm {k.Schirm:F1})").Append(": ");
                for (int j = 0; j < f.Length; j++) sb.Append(j % 2 == 0 ? Name(f[j]) : " #" + f[j] + " ");
            }
            return sb.ToString();
        }

        readonly List<RaumKette> graphTmp = new List<RaumKette>(); readonly List<ulong> graphKeys = new List<ulong>();
        /// <summary>Raumanteile Quellraum -> Hoererraum ueber den Raumgraphen (unabhaengig von den gefundenen Wegen). Zustand() vorher.
        /// gesehen bekommt die Schluessel der gerechneten Folgen (FolgeSchluessel), damit Wege mit derselben Raumfolge sie nicht doppelt zaehlen.</summary>
        public void Graph(V q0, V h0, List<RaumKette> aus, HashSet<ulong> gesehen)
        {
            int rs = RaumVon(q0), rh = RaumVon(h0);
            if (rs < 0 || rh < 0) return;
            if (rs == rh) { if (!EigenAn) return; var eig = Eigen(rs, q0, h0); if (eig != null && gesehen.Add(eig.Id)) aus.Add(eig); return; }
            if (nachbarnStand != aStand || nachbarn == null) NachbarnBauen();
            long key = (long)rs * RN + rh;
            if (!graphCache.TryGetValue(key, out var folgen)) { folgen = Folgen(rs, rh, q0, h0); if (graphCache.Count > 4096) graphCache.Clear(); graphCache[key] = folgen; DGraphNeu++; }
            else DGraphCache++;
            graphTmp.Clear(); graphKeys.Clear();
            foreach (var f in folgen)
            {
                if (!FolgeSetzen(f, q0, h0)) continue;
                var k = Rechnen(q0, h0); if (k == null) continue;
                graphTmp.Add(k); graphKeys.Add(FolgeSchluessel);
            }
            gruppen.Clear(); folge.Clear(); uebergang.Clear(); vonRaum.Clear(); nachRaum.Clear();
            double best = double.NegativeInfinity; foreach (var k in graphTmp) best = Math.Max(best, k.Db[1]);
            for (int z = 0; z < GraphWege; z++)
            {
                int bi = -1; for (int j = 0; j < graphTmp.Count; j++) if (graphTmp[j] != null && (bi < 0 || graphTmp[j].Db[1] > graphTmp[bi].Db[1])) bi = j;
                if (bi < 0 || graphTmp[bi].Db[1] < best - GraphSpanneDb || graphTmp[bi].Db[1] <= -60) break;
                if (gesehen.Add(graphKeys[bi])) aus.Add(graphTmp[bi]);
                graphTmp[bi] = null;
            }
        }

        // ---------------------------------------------------------------- Raid-3-Fix: Nachhallfeld des eigenen Raums
        //  Befund Raid 3 (10.11.2026): Quelle und Hoerer im selben Raum ohne Sicht (Deckung in Halle 1, 217 Paare) - die Suche rechnete
        //  nur den Beugungsweg um die Deckung (bei 4 kHz meist am 25-dB-Deckel), Median -7,2 dB (1 kHz) / -14,3 dB (4 kHz) unter der Mod.
        //  Physik: im geschlossenen Raum fuellt das Diffusfeld den Schatten eines Hindernisses auf. Seine Intensitaet am Hoerer ist
        //  4 W / A (Sabine/Kuttruff, Room Acoustics Kap. 5; EN 12354-6), mit Luftabsorption (A + 4 m V) und dem Abfall mit dem Abstand von
        //  der Quelle nach Barron & Lee 1988 (exp(-0,04 r / T), T = 0,161 V / A) - genau die Endstufe, die RaumwegKern.Kette am Hoerer
        //  einer Raumfolge rechnet (dort ab dem Eintrittsportal, hier ab der Quelle). Im Freien (Raum "draussen" oder ohne Kasten) gibt es
        //  kein Diffusfeld. Die Kette hat keine Portale; ihr Pegel ist wie alle Raumanteile auf das Freifeld in Direktlinie bezogen.
        RaumKette Eigen(int r, V q0, V h0)
        {
            if (r < 0 || r >= RN || aRaum[r] <= 0 || draussen[r]) return null;
            double d = Math.Max(V.Abstand(q0, h0), 0.5), bezug = 1.0 / (4 * Math.PI * d * d), v = Math.Max(0, vol[r]);
            var e = new RaumKette { Portale = new int[0], Raeume = new[] { Name(r) }, Austritt = q0 };
            for (int b = 0; b < 3; b++)
            {
                double aF = Math.Max(aRaum[r] + 4 * luftM[b] * v, RaumwegKern.MinFlaecheA);
                double abfall = v > 0 ? Math.Exp(-0.04 * d / Math.Max(0.161 * v / aF, 0.05)) : 1.0;
                double i = 4.0 / aF * abfall;
                e.Db[b] = Math.Max(-60, Math.Min(0, 10 * Math.Log10(i / bezug)));
            }
            DEigen++;
            return e;
        }
        public int DEigen;   // Diagnose: Nachhallanteile im eigenen Raum
        /// <summary>Diagnose: Oberflaeche, Volumen und Absorptionsflaeche (aktueller Tuerstand) eines Raums.</summary>
        public string Info(int r) => r < 0 || r >= RN ? "?" : $"{name[r]}: S {flaeche[r]:F0} m2, V {vol[r]:F0} m3, A {aRaum[r]:F0} m2, draussen {draussen[r]}, Portale {raumQ[r].Length}";
        public int RaumIndex(string n) { for (int r = 0; r < RN; r++) if (name[r] == n) return r; return -1; }
        public bool EigenAn = true;   // Schalter fuer den Offline-Vergleich (aus = wie vor Raid 3)

        bool Aussen(int k)
        {
            int a = vonRaum[k], b = nachRaum[k];
            return (a >= 0 && a < RN && (draussen[a] || flaeche[a] <= 0)) || (b >= 0 && b < RN && (draussen[b] || flaeche[b] <= 0));
        }

        /// <summary>Punkt 0,3 m vor dem Portal auf der Seite von 'ziel' (Aussenseite der Strecke im Freien).</summary>
        V AussenPunkt(int i, V p, V ziel)
        {
            // Seite des Raums im Freien (aus den Kaesten, Konstruktor); unbekannt: zur anderen Streckenseite hin
            int sA = qSeiteA[i], seite = 0;
            if (sA != 0) { if (qA[i] >= 0 && Frei(qA[i])) seite = sA; else if (qB[i] >= 0 && Frei(qB[i])) seite = -sA; }
            if (seite == 0) seite = V.Dot(ziel - p, qN[i]) >= 0 ? 1 : -1;
            return p + qN[i] * (0.3 * seite);
        }
        bool Frei(int r) => r >= 0 && r < RN && (draussen[r] || flaeche[r] <= 0);
        readonly sbyte[] qSeiteA;   // Seite (Normale +1 / -1) des Raums qA, 0 = unbekannt

        // Abschirmung einer Strecke im Freien (ISO 9613-2 Gl. 14/18 ohne Meteorologie, wie HindernisKern.Dz der Mod): Hoehenprofil aus
        // senkrechten Strahlen (BVH) entlang der Strecke, Gummiband = obere konvexe Huelle von a, Profil und b, Umweg z = Huelle - Direkt.
        // Freie Sichtlinie = 0 dB. Zwischenspeicher je 0,5-m-Zelle beider Enden (Geometrie ist fest).
        Bvh bvh;
        public void BvhSetzen(Bvh b)
        {
            bvh = b; offenAnteil = new double[QN];
            // Raid-2-Fix: offener Anteil je Portal aus der Geometrie (5 x 5 Strahlen je 1 m senkrecht durch das Rechteck). Das Spiel legt
            // Portale auch ueber verglaste/vergitterte Fenster (Factory: die 17-m2-Fenster der Halle 3 an z = 64,4 sind zu 0-3/25 frei,
            // die Dachoeffnungen zu 8-19/25). Der verdeckte Teil zaehlt wie ein geschlossener (Durchlass des Blatts), nicht als Loch.
            for (int i = 0; i < QN; i++)
            {
                int frei = 0;
                for (int u = -2; u <= 2; u++) for (int w = -2; w <= 2; w++)
                {
                    V pm = qM[i] + qU[i] * (0.36 * u * q[i].HalbX) + qW[i] * (0.36 * w * q[i].HalbY);
                    if (b.Frei(pm - qN[i] * 0.5, pm + qN[i] * 0.5, 0.001f)) frei++;
                }
                offenAnteil[i] = frei / 25.0;
            }
            aStand = int.MinValue;
        }
        double[] offenAnteil;               // null = ohne Geometrie (alles offen)
        public double OffenAnteil(int i) => offenAnteil == null ? 1 : offenAnteil[i];
        public int RaumA(int i) => qA[i]; public int RaumB(int i) => qB[i];
        public bool[] Schliessbar;          // Netzportal mit Tuerblatt (der Szenenkasten zeigt dessen Szenenstellung): Geometrie zaehlt nicht
        public readonly bool[] QGeoeffnet;  // Raumportal ohne Netzportal, das live geoeffnet wurde (Szenengeometrie zeigt evtl. das Tor zu)
        readonly Dictionary<long, double[]> schirmCache = new Dictionary<long, double[]>();
        static readonly double[] keinSchirm = new double[3];
        double[] profS = new double[64], profY = new double[64], hullS = new double[66], hullY = new double[66];
        public int DSchirm, DSchirmFrei;   // Diagnose: Strecken im Freien gerechnet / davon frei

        double[] Schirm(V a, V b)
        {
            if (bvh == null) return keinSchirm;
            long ka = Wegenetz.Schluessel((int)Math.Floor(a.X * 2), (int)Math.Floor(a.Y * 2), (int)Math.Floor(a.Z * 2));
            long kb = Wegenetz.Schluessel((int)Math.Floor(b.X * 2), (int)Math.Floor(b.Y * 2), (int)Math.Floor(b.Z * 2));
            long key = ka * 1000003L ^ kb;
            if (schirmCache.TryGetValue(key, out var r)) return r;
            DSchirm++;
            if (bvh.Frei(a, b, 0.05f)) { r = keinSchirm; DSchirmFrei++; }
            else
            {
                // Profil aus den Koerpern, die die Linie wirklich schneidet (wie die Mod: nur, was von der Linie nach oben ragt - kein
                // Vordach ueber der Linie, aber auch kein Gebaeude daneben): je Eintritt in einen Koerper seine Oberkante ueber diesem Punkt
                // (Austritt nach oben aus dem Koerper; steckt der Punkt in einem umschlossenen Innenraum, die oberste Flaeche = Dach).
                double dx = b.X - a.X, dz = b.Z - a.Z, D = Math.Sqrt(dx * dx + dz * dz); int np = 0;
                V dir = b - a; double len = dir.Len; dir = dir * (1 / Math.Max(len, 1e-9)); double tl = 0;
                for (int j = 0; j < 24 && tl < len && np < profS.Length; j++)
                {
                    var hit = bvh.Erster(a + dir * tl, dir, (float)(len - tl), 0.002f); if (hit.T < 0) break;
                    tl += hit.T + 0.01;
                    if (!hit.Vorne) continue;
                    V p = a + dir * (tl - 0.005); double top;
                    var hoch = bvh.Erster(p, new V(0, 1, 0), 80f, 0.001f);
                    if (hoch.T >= 0 && !hoch.Vorne) top = p.Y + hoch.T;   // aus dem Koerper nach oben heraus
                    else
                    {
                        double y0 = Math.Max(a.Y, b.Y) + 60; var ab = bvh.Erster(new V(p.X, y0, p.Z), new V(0, -1, 0), 120f, 0.001f);
                        top = ab.T >= 0 ? y0 - ab.T : p.Y;
                    }
                    double sh = Math.Sqrt(Sq(p.X - a.X) + Sq(p.Z - a.Z));
                    if (top > a.Y + (b.Y - a.Y) * (D > 1e-9 ? sh / D : 0)) { profS[np] = sh; profY[np] = top; np++; }
                }
                // nach s ordnen (Eintritte kommen in Wegrichtung, s waechst mit t)
                // obere Huelle (Monotone Chain) von a ueber das Profil nach b
                int hn = 0;
                void Dazu(double sx, double sy)
                {
                    while (hn >= 2 && (hullS[hn - 1] - hullS[hn - 2]) * (sy - hullY[hn - 2]) - (hullY[hn - 1] - hullY[hn - 2]) * (sx - hullS[hn - 2]) >= 0) hn--;
                    hullS[hn] = sx; hullY[hn] = sy; hn++;
                }
                Dazu(0, a.Y); for (int j = 0; j < np; j++) Dazu(profS[j], profY[j]); Dazu(D, b.Y);
                if (hn <= 2) { r = keinSchirm; DSchirmFrei++; }   // nur seitlich verdeckt (kein Profil ueber der Linie): wie die Mod frei
                else
                {
                    double L = 0; for (int j = 0; j + 1 < hn; j++) L += Math.Sqrt(Sq(hullS[j + 1] - hullS[j]) + Sq(hullY[j + 1] - hullY[j]));
                    double direkt = Math.Sqrt(D * D + Sq(b.Y - a.Y)), z = Math.Max(0, L - direkt);
                    double dss = Math.Sqrt(Sq(hullS[1]) + Sq(hullY[1] - a.Y)), dsr = Math.Sqrt(Sq(D - hullS[hn - 2]) + Sq(b.Y - hullY[hn - 2]));
                    double e = Math.Max(0, L - dss - dsr); bool mehr = hn > 3;
                    r = new double[3];
                    for (int bb = 0; bb < 3; bb++) r[bb] = Dz(Suche.F[bb], z, e, mehr);
                }
            }
            if (schirmCache.Count > 50000) schirmCache.Clear();
            schirmCache[key] = r;
            return r;
        }
        /// <summary>Abschirmung (Schaetzung fuer die Aufzaehlung) der Strecke im Freien von Portal i zu Portal j (j &lt; 0: zum Hoerer h0),
        /// zwischen den Portalmitten (je 0,3 m auf der Aussenseite). Portalpaare sind fest, der Hoerer geht ueber seine 0,5-m-Zelle ein.</summary>
        double[] SchirmPaar(int i, int j, V h0)
        {
            V zi = j >= 0 ? qM[j] : h0, a = AussenPunkt(i, qM[i], zi), b = j >= 0 ? AussenPunkt(j, qM[j], qM[i]) : h0;
            return Schirm(a, b);
        }
        double RaumwinkelAnteil(int i, V p) => RaumwegKern.Raumwinkel(q[i], V3v(p)) / (4 * Math.PI);
        static double Sq(double x) => x * x;

        /// <summary>ISO 9613-2:1996 Gl. (14)/(18): Abschirmmass Dz = 10 lg(3 + C2/lambda C3 z), C2 = 20, gedeckelt 20 dB (einfach) / 25 dB
        /// (mehrfach, C3 aus e). Ohne Meteorologie-Korrektur (Kmet ~ 1 auf Kartenentfernungen).</summary>
        public static double Dz(double f, double z, double e, bool mehrfach)
        {
            if (z <= 0) return 0;
            double lambda = 343.0 / f, c3 = 1;
            if (mehrfach) { double x = 5 * lambda / Math.Max(e, 1e-3); x *= x; c3 = (1 + x) / (1.0 / 3.0 + x); }
            return Math.Max(0, Math.Min(mehrfach ? 25.0 : 20.0, 10 * Math.Log10(3 + 20 / lambda * c3 * z)));
        }

        readonly RaumPortal[] pz = new RaumPortal[MaxPortale];
        readonly V3[] pk = new V3[MaxPortale];
        readonly double[] ra = new double[MaxPortale + 1], rv = new double[MaxPortale + 1];

        /// <summary>Raumanteil der zuletzt mit Folge() bestimmten Raumfolge: Pegel je Band gegenueber dem Freifeld in Direktlinie (q, h).
        /// null = kein Raumwechsel ueber ein Spielportal oder zu viele Oeffnungen.</summary>
        public RaumKette Rechnen(V q0, V h0)
        {
            int n = folge.Count; if (n == 0 || n > MaxPortale) return null;
            if (gruppen.Count != n + 1 || vonRaum.Count != n || nachRaum.Count != n || uebergang.Count != n)
                throw new InvalidOperationException($"Raumfolge inkonsistent: {gruppen.Count} Abschnitte, {n} Portale, {vonRaum.Count}/{nachRaum.Count}/{uebergang.Count}");
            for (int k = 0; k <= n; k++)
            {
                double a = 0, v = 0; bool frei = false;
                if (gruppen[k].Count == 0) frei = true;   // Raum unbekannt (kein Kasten): wie im Freien
                foreach (int r in gruppen[k]) { if (aRaum[r] < 0) { frei = true; break; } a += aRaum[r]; v += vol[r]; }
                ra[k] = frei ? -1 : a; rv[k] = frei ? 0 : v;
            }
            V3 qq = V3v(q0), hh = V3v(h0);
            for (int k = 0; k < n; k++)
            {
                int i = folge[k]; V r = uebergang[k] - qM[i];
                double u = H.Clamp(V.Dot(r, qU[i]), -q[i].HalbX, q[i].HalbX), w = H.Clamp(V.Dot(r, qW[i]), -q[i].HalbY, q[i].HalbY);
                pk[k] = V3v(qM[i] + qU[i] * u + qW[i] * w);   // Kreuzungspunkt, ins Rechteck gezogen
                // alle Spielportale zwischen den beiden Raeumen parallel: Flaeche S = Summe, Durchlass je Band = Summe S_i t_i / S
                // (Offen = 0, tau je Band; Kette rechnet Leistung = Einfall * S * tau, genau Summe ueber die Oeffnungen)
                int von = vonRaum[k], nach = nachRaum[k]; double sGes = 0, t0 = 0, t1 = 0, t2 = 0;
                bool aussen = ra[k] < 0 || ra[k + 1] < 0;   // Raid-2-Fix: ins Freie zaehlt jedes Portal mit seiner Lage (Abschirmung), nicht zusammengefasst
                foreach (int j in raumQ[von])
                {
                    int anderer = qA[j] == von ? qB[j] : qA[j]; if (anderer != nach || (aussen && j != i)) continue;
                    double sj = q[j].Flaeche, o = q[j].Offen; sGes += sj;
                    t0 += sj * (o + (1 - o) * qTau[3 * j]); t1 += sj * (o + (1 - o) * qTau[3 * j + 1]); t2 += sj * (o + (1 - o) * qTau[3 * j + 2]);
                }
                var pp = q[i];
                if (sGes > 0)
                {
                    double f = Math.Sqrt(sGes / Math.Max(pp.Flaeche, 1e-6)); pp.HalbX *= f; pp.HalbY *= f;
                    pp.Offen = 0; pp.Tau = t0 / sGes; pp.TauMitte = t1 / sGes; pp.TauHoch = t2 / sGes; pp.Baender = true;
                }
                pz[k] = pp;
            }
            double d = Math.Max(V.Abstand(q0, h0), 0.5), bezug = 1.0 / (4 * Math.PI * d * d);
            var e = new RaumKette { Portale = folge.ToArray(), Raeume = new string[n + 1], Austritt = new V(pk[n - 1].X, pk[n - 1].Y, pk[n - 1].Z) };
            for (int k = 0; k <= n; k++) e.Raeume[k] = gruppen[k].Count == 0 ? "?" : string.Join("+", gruppen[k].ConvertAll(Name));
            // Raid-2-Fix: Strecken im Freien abschirmen (wie die Mod, RaumwegNetz.Schirmen): "draussen" ist ein Raum ueber die ganze
            // Karte mit den Gebaeuden darin. Ohne Abschirmung strahlten die Dachoeffnungen der Halle 3 (7 Portale, 47-94 m2, y 15-21 m)
            // frei in den Aussenhof (Nachspiel: Halle -> Aussenhof +15 dB ueber der Mod); die Mod schirmt dieselbe Strecke ueber das Dach ab.
            double a0 = 0, a1 = 0, a2 = 0;
            for (int k = 0; k <= n; k++)
            {
                if (ra[k] >= 0) continue;
                int k0 = k; while (k < n && ra[k + 1] < 0) k++;
                V ende0 = k0 == 0 ? q0 : AussenPunkt(folge[k0 - 1], new V(pk[k0 - 1].X, pk[k0 - 1].Y, pk[k0 - 1].Z), k == n ? h0 : new V(pk[k].X, pk[k].Y, pk[k].Z));
                V ende1 = k == n ? h0 : AussenPunkt(folge[k], new V(pk[k].X, pk[k].Y, pk[k].Z), ende0);
                var sch = Schirm(ende0, ende1); a0 += sch[0]; a1 += sch[1]; a2 += sch[2];
            }
            e.Schirm = a1;
            for (int b = 0; b < 3; b++)
            {
                double i = RaumwegKern.Diffus(qq, hh, pz, pk, n, ra, rv, b, luftM[b], luftDb[b]);
                double ab = b == 0 ? a0 : b == 1 ? a1 : a2;
                e.Db[b] = i <= 0 ? -60 : Math.Max(-60, Math.Min(0, 10 * Math.Log10(i / bezug) - ab));
            }
            return e;
        }
    }
}
