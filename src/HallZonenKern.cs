using System;
using System.Collections.Generic;

namespace Materialklang
{
    // hallzone (12.11.2026, Befund A1 des Durchgangs Akustik): bis hier war die Meta-Control-Zone eines Raums die HUELLBOX der
    // Vereinigung seiner Boxen (RaumGeometrieKern.HuelleIn). Bei den grossen Factory-Hallen ist die Huelle zur Haelfte kein Raum
    // (Area_3: 132603 m3 Huelle, 50 % Raum) und deckt 85 von 114 kleineren Raeumen mit ab -> "Hall des falschen Raums".
    // Hier: je Raum Zonen, die NUR seinen eigenen Raum abdecken, und zwar so, dass sich Zonen nirgends ueberlappen (ausser im
    // Fade-Band). Damit ist das Ergebnis dasselbe, gleich wie Meta mehrere Zonen an einem Punkt verrechnet (Summe, kleinste,
    // zuletzt angelegte, gewichtetes Mittel) - die Regel ist nicht dokumentiert und liess sich offline nicht messen
    // (review-20261110/hallzone/zonentest: im Schuhkarton-Modell wirken Zonen nicht, im Strahlmodell liefert der eigene Kontext
    // keinen Hall).
    //  - Besitz: ein Punkt gehoert dem KLEINSTEN Raum (Volumen der Vereinigung), dessen Vereinigung ihn enthaelt - dieselbe Regel wie
    //    HallZonen.Finde/RaumFuer (Wandweg). Beispiel Factory: der Tank (Boiler_spawn_room) liegt IN der Vereinigung von Area_3.
    //  - Ueberlappen sich weder die Boxen des Raums untereinander noch ein kleinerer Raum mit ihnen: Zonen = die Boxen selbst (exakt).
    //  - Sonst: Gitter im Achsensystem der groessten Box, Schnittebenen = Seiten aller beteiligten Boxen (exakt fuer gleich
    //    ausgerichtete Boxen; gedrehte Boxen zusaetzlich alle 'schritt' Meter), Zellmitte "gehoert dem Raum?", dann gierig zu
    //    moeglichst grossen Quadern zusammengefasst (jede Zelle in genau einem Quader).
    //  - Fade: Meta legt die Native-Box um FadeDistance/2 je Seite groesser als die Zone (MetaXRAcousticControlZone.NativeBoxSize =
    //    2 + Fade/Scale) und blendet ueber FadeDistance aus. ANNAHME (nicht gemessen, Dekompilat zeigt nur, dass Box und Fade
    //    getrennt gespeichert werden): Gewicht 1 bis Fade vor dem Rand der Native-Box, linear auf 0 am Rand -> die Rampe liegt
    //    MITTIG auf der Zonenseite. Darum: Seiten, hinter denen derselbe Raum weitergeht, bleiben, wo sie sind (die Rampen zweier
    //    Nachbarboxen ergaenzen sich zu 1, keine Luecke, kein Doppel); Seiten an der echten Raumgrenze werden um Fade/2 nach innen
    //    gerueckt (die Zone endet an der Wand und ragt nicht in den Nachbarraum; der Uebergang dauert Fade).
    internal static class HallZonenKern
    {
        /// <summary>Gehoert p dem Raum 'raum'? (in seiner Vereinigung und in keiner Vereinigung eines kleineren Raums)</summary>
        public static bool Gehoert(Kasten[][] raeume, double[] volumen, int raum, int[] kleinere, double x, double y, double z)
        {
            var eigene = raeume[raum];
            if (!RaumGeometrieKern.DrinVereinigung(eigene, eigene.Length, x, y, z)) return false;
            if (kleinere != null)
                foreach (int j in kleinere)
                    if (RaumGeometrieKern.DrinVereinigung(raeume[j], raeume[j].Length, x, y, z)) return false;
            return true;
        }

        /// <summary>Raum j zaehlt vor Raum i (kleiner; bei Gleichstand der kleinere Index) - Besitzregel.</summary>
        static bool Vorrang(double[] volumen, int j, int i) => volumen[j] < volumen[i] || (volumen[j] == volumen[i] && j < i);

        /// <summary>Raeume, die vor 'raum' zaehlen und sich mit ihm ueberlappen (mehr als 'tol' Meter tief).</summary>
        public static int[] Kleinere(Kasten[][] raeume, double[] volumen, int raum, double tol = 0.02)
        {
            var l = new List<int>();
            var eigene = raeume[raum];
            for (int j = 0; j < raeume.Length; j++)
            {
                if (j == raum || raeume[j] == null || !Vorrang(volumen, j, raum)) continue;
                bool ja = false;
                foreach (var a in eigene) { foreach (var b in raeume[j]) if (Ueberlappen(a, b, tol)) { ja = true; break; } if (ja) break; }
                if (ja) l.Add(j);
            }
            return l.ToArray();
        }

        /// <summary>Ueberlappen sich zwei gedrehte Quader um mehr als 'tol' Meter? (Trennachsen-Satz, 15 Achsen; aneinanderstossende
        /// Quader ueberlappen NICHT.) Quelle: Gottschalk, Lin, Manocha, "OBBTree", SIGGRAPH 1996; Ericson, Real-Time Collision
        /// Detection (2005) Kap. 4.4.</summary>
        public static bool Ueberlappen(in Kasten a, in Kasten b, double tol = 0.02)
        {
            double[][] A = { new[] { a.Ux, a.Uy, a.Uz }, new[] { a.Vx, a.Vy, a.Vz }, new[] { a.Wx, a.Wy, a.Wz } };
            double[][] B = { new[] { b.Ux, b.Uy, b.Uz }, new[] { b.Vx, b.Vy, b.Vz }, new[] { b.Wx, b.Wy, b.Wz } };
            double[] ha = { a.Hx, a.Hy, a.Hz }, hb = { b.Hx, b.Hy, b.Hz };
            double[] t = { b.Mx - a.Mx, b.My - a.My, b.Mz - a.Mz };
            bool Trennt(double ax, double ay, double az)
            {
                double l = Math.Sqrt(ax * ax + ay * ay + az * az);
                if (l < 1e-9) return false;
                ax /= l; ay /= l; az /= l;
                double ra = 0, rb = 0;
                for (int i = 0; i < 3; i++)
                {
                    ra += ha[i] * Math.Abs(A[i][0] * ax + A[i][1] * ay + A[i][2] * az);
                    rb += hb[i] * Math.Abs(B[i][0] * ax + B[i][1] * ay + B[i][2] * az);
                }
                return Math.Abs(t[0] * ax + t[1] * ay + t[2] * az) >= ra + rb - tol;
            }
            for (int i = 0; i < 3; i++) if (Trennt(A[i][0], A[i][1], A[i][2]) || Trennt(B[i][0], B[i][1], B[i][2])) return false;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    double cx = A[i][1] * B[j][2] - A[i][2] * B[j][1], cy = A[i][2] * B[j][0] - A[i][0] * B[j][2], cz = A[i][0] * B[j][1] - A[i][1] * B[j][0];
                    if (Trennt(cx, cy, cz)) return false;
                }
            return true;
        }

        /// <summary>Ergebnis der Zerlegung eines Raums.</summary>
        public sealed class Teile
        {
            public List<Kasten> Boxen = new List<Kasten>();   // ueberlappungsfrei, NICHT eingerueckt
            public bool Gitter;                               // true = ueber das Gitter zerlegt (Boxen ueberlappten oder ein kleinerer Raum liegt darin)
            public int Zellen;                                // Zahl der Gitterzellen (Diagnose)
            public double Fade;                               // Fade dieses Raums (m)
            public int[] Kleinere = new int[0];               // kleinere Raeume, die ihm Volumen wegnehmen (Besitzregel)
        }

        /// <summary>Zerlegt den Raum 'raum' in ueberlappungsfreie Quader, die genau das abdecken, was ihm gehoert.
        /// 'schritt' = Gitterabstand fuer gedrehte Boxen (m), 'maxZellen' = Rechengrenze (dann wird 'schritt' verdoppelt).</summary>
        public static Teile Zerlegen(Kasten[][] raeume, double[] volumen, int raum, double schritt = 0.5, int maxZellen = 200000, double tolFremd = double.NaN)
        {
            var eigene = raeume[raum];
            var erg = new Teile();
            // Ein kleinerer Raum zaehlt erst, wenn er tiefer als Fade/2 hineinragt: flachere Ueberlappungen (Trigger-Boxen benachbarter
            // Raeume greifen oft 0,1-0,5 m uebereinander) liegen ganz im Fade-Band; dort ruecken beide Zonen ohnehin um Fade/2 von
            // der Grenze zurueck (Einruecken), so dass sich die Gewichte auch dort zu hoechstens 1 addieren (Pruefung: Gewichtssumme
            // in review-20261110/hallzone/zaehlen). Das spart an der Factory ~20 % der Zonen.
            if (double.IsNaN(tolFremd)) tolFremd = 0.5 * FadeFuer(eigene);
            int[] kleinere = Kleinere(raeume, volumen, raum, tolFremd);
            bool ueber = false;
            for (int i = 0; i < eigene.Length && !ueber; i++)
                for (int j = i + 1; j < eigene.Length; j++)
                    if (Ueberlappen(eigene[i], eigene[j])) { ueber = true; break; }
            erg.Fade = FadeFuer(eigene);
            erg.Kleinere = kleinere;
            if (!ueber && kleinere.Length == 0) { erg.Boxen.AddRange(eigene); return erg; }

            erg.Gitter = true;
            int r = 0;
            for (int i = 1; i < eigene.Length; i++) if (eigene[i].Volumen > eigene[r].Volumen) r = i;
            var bz = eigene[r];
            // Ausdehnung des Raums im Bezugssystem
            Projektion(eigene, bz, out double[] lo, out double[] hi);
            var beteiligt = new List<Kasten>(eigene);
            foreach (int j in kleinere) beteiligt.AddRange(raeume[j]);
            for (int versuch = 0; versuch < 8; versuch++, schritt *= 2)
            {
                var ebenen = new List<double>[3];
                for (int a = 0; a < 3; a++) ebenen[a] = new List<double> { lo[a], hi[a] };
                foreach (var k in beteiligt)
                {
                    Projektion(new[] { k }, bz, out double[] kl, out double[] kh);
                    for (int a = 0; a < 3; a++)
                    {
                        double u = Math.Max(lo[a], kl[a]), o = Math.Min(hi[a], kh[a]);
                        if (o <= u) continue;
                        ebenen[a].Add(u); ebenen[a].Add(o);
                        // Seiten von k sind nur dann Ebenen "a = const", wenn k eine Achse parallel zur Bezugsachse a hat (z. B. um y
                        // gedreht: y bleibt exakt); sonst Zwischenebenen alle 'schritt' Meter
                        if (!AchseParallel(k, bz, a)) for (double s = u + schritt; s < o; s += schritt) ebenen[a].Add(s);
                    }
                }
                double ma = MinEbenenAbstand(erg.Fade);
                var ax = Verdichten(ebenen[0], ma); var ay = Verdichten(ebenen[1], ma); var az = Verdichten(ebenen[2], ma);
                long zellen = (long)(ax.Length - 1) * (ay.Length - 1) * (az.Length - 1);
                if (zellen > maxZellen && versuch < 7) continue;
                erg.Zellen = (int)zellen;
                Gitter(raeume, volumen, raum, kleinere, bz, ax, ay, az, erg.Boxen);
                return erg;
            }
            return erg;
        }

        /// <summary>Fade eines Raums (m): 1 m (Auftrag: stetiger Uebergang ueber ~1 m), in schmalen Raeumen die halbe kleinste
        /// Kantenlaenge seiner Boxen, damit das Gewicht in der Raummitte noch 1 erreicht (Annahme zur Fade-Form siehe oben);
        /// mindestens 0,2 m. EIN Wert je Raum, damit sich die Rampen an inneren Seiten zu 1 ergaenzen.</summary>
        public static double FadeFuer(Kasten[] eigene)
        {
            double m = double.MaxValue;
            foreach (var k in eigene) m = Math.Min(m, 2 * Math.Min(k.Hx, Math.Min(k.Hy, k.Hz)));
            return Math.Max(0.2, Math.Min(1.0, 0.5 * m));
        }

        // Schnittebenen naeher als Fade/2 werden zusammengelegt: feiner kann die Zone ohnehin nicht aufloesen (die Rampe ist Fade
        // breit), und duenne Teilboxen (duenner als Fade) wuerden ihr Gewicht nach der Fade-Annahme nicht mehr zu 1 ergaenzen
        // (eine 0,2 m duenne Box hat in der Mitte Gewicht 0,6 statt 0,2). Offline Factory (review-20261110/hallzone/zaehlen):
        // 5 cm -> 688 Zonen, Gewichtssumme > 1,05 in 6,0 % des Raumvolumens (bis 3,15); Fade/2 -> 387 Zonen, 1,0 % (bis 1,21).
        // Raumgrenzen verschieben sich dabei um hoechstens Fade/2, also innerhalb des Uebergangs.
        static double MinEbenenAbstand(double fade) => Math.Max(0.05, 0.5 * fade);

        static bool AchseParallel(in Kasten k, in Kasten bz, int a)
        {
            double ex = a == 0 ? bz.Ux : a == 1 ? bz.Vx : bz.Wx, ey = a == 0 ? bz.Uy : a == 1 ? bz.Vy : bz.Wy, ez = a == 0 ? bz.Uz : a == 1 ? bz.Vz : bz.Wz;
            return Math.Abs(k.Ux * ex + k.Uy * ey + k.Uz * ez) > 0.9999 || Math.Abs(k.Vx * ex + k.Vy * ey + k.Vz * ez) > 0.9999
                || Math.Abs(k.Wx * ex + k.Wy * ey + k.Wz * ez) > 0.9999;
        }

        static void Projektion(Kasten[] ks, in Kasten bz, out double[] lo, out double[] hi)
        {
            lo = new[] { double.MaxValue, double.MaxValue, double.MaxValue }; hi = new[] { double.MinValue, double.MinValue, double.MinValue };
            foreach (var k in ks)
                for (int e = 0; e < 8; e++)
                {
                    double sx = (e & 1) != 0 ? k.Hx : -k.Hx, sy = (e & 2) != 0 ? k.Hy : -k.Hy, sz = (e & 4) != 0 ? k.Hz : -k.Hz;
                    double x = k.Mx + sx * k.Ux + sy * k.Vx + sz * k.Wx - bz.Mx;
                    double y = k.My + sx * k.Uy + sy * k.Vy + sz * k.Wy - bz.My;
                    double z = k.Mz + sx * k.Uz + sy * k.Vz + sz * k.Wz - bz.Mz;
                    double l0 = x * bz.Ux + y * bz.Uy + z * bz.Uz, l1 = x * bz.Vx + y * bz.Vy + z * bz.Vz, l2 = x * bz.Wx + y * bz.Wy + z * bz.Wz;
                    lo[0] = Math.Min(lo[0], l0); hi[0] = Math.Max(hi[0], l0);
                    lo[1] = Math.Min(lo[1], l1); hi[1] = Math.Max(hi[1], l1);
                    lo[2] = Math.Min(lo[2], l2); hi[2] = Math.Max(hi[2], l2);
                }
        }

        // Schnittebenen sortieren, Ebenen naeher als 'minAbstand' zusammenlegen (siehe MinEbenenAbstand).
        static double[] Verdichten(List<double> l, double minAbstand)
        {
            l.Sort();
            var r = new List<double>(l.Count) { l[0] };
            for (int i = 1; i < l.Count; i++) if (l[i] - r[r.Count - 1] > minAbstand) r.Add(l[i]);
            if (r.Count == 1) r.Add(l[l.Count - 1]); else r[r.Count - 1] = l[l.Count - 1];   // die aeusseren Grenzen bleiben exakt
            return r.ToArray();
        }

        static void Gitter(Kasten[][] raeume, double[] volumen, int raum, int[] kleinere, in Kasten bz, double[] ax, double[] ay, double[] az, List<Kasten> aus)
        {
            int nx = ax.Length - 1, ny = ay.Length - 1, nz = az.Length - 1;
            var drin = new bool[nx * ny * nz];
            var frei = new bool[nx * ny * nz];
            int Idx(int i, int j, int k) => (k * ny + j) * nx + i;
            for (int k = 0; k < nz; k++)
            {
                double cz = 0.5 * (az[k] + az[k + 1]);
                for (int j = 0; j < ny; j++)
                {
                    double cy = 0.5 * (ay[j] + ay[j + 1]);
                    for (int i = 0; i < nx; i++)
                    {
                        double cx = 0.5 * (ax[i] + ax[i + 1]);
                        double x = bz.Mx + cx * bz.Ux + cy * bz.Vx + cz * bz.Wx, y = bz.My + cx * bz.Uy + cy * bz.Vy + cz * bz.Wy, z = bz.Mz + cx * bz.Uz + cy * bz.Vz + cz * bz.Wz;
                        bool g = Gehoert(raeume, volumen, raum, kleinere, x, y, z);
                        drin[Idx(i, j, k)] = g; frei[Idx(i, j, k)] = g;
                    }
                }
            }
            // gierig: von jeder freien Zelle erst in x, dann y, dann z so weit wie alle Zellen frei sind
            for (int k = 0; k < nz; k++)
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        if (!frei[Idx(i, j, k)]) continue;
                        int i2 = i + 1; while (i2 < nx && frei[Idx(i2, j, k)]) i2++;
                        int j2 = j + 1;
                        for (; j2 < ny; j2++) { bool ok = true; for (int a = i; a < i2 && ok; a++) ok = frei[Idx(a, j2, k)]; if (!ok) break; }
                        int k2 = k + 1;
                        for (; k2 < nz; k2++) { bool ok = true; for (int b = j; b < j2 && ok; b++) for (int a = i; a < i2 && ok; a++) ok = frei[Idx(a, b, k2)]; if (!ok) break; }
                        for (int c = k; c < k2; c++) for (int b = j; b < j2; b++) for (int a = i; a < i2; a++) frei[Idx(a, b, c)] = false;
                        double mx = 0.5 * (ax[i] + ax[i2]), my = 0.5 * (ay[j] + ay[j2]), mz = 0.5 * (az[k] + az[k2]);
                        var o = bz;
                        o.Mx = bz.Mx + mx * bz.Ux + my * bz.Vx + mz * bz.Wx;
                        o.My = bz.My + mx * bz.Uy + my * bz.Vy + mz * bz.Wy;
                        o.Mz = bz.Mz + mx * bz.Uz + my * bz.Vz + mz * bz.Wz;
                        o.Hx = 0.5 * (ax[i2] - ax[i]); o.Hy = 0.5 * (ay[j2] - ay[j]); o.Hz = 0.5 * (az[k2] - az[k]);
                        aus.Add(o);
                    }
        }

        /// <summary>Fertige Zonen eines Raums: Teilboxen an gemischten Seiten teilen (Spalten), dann Einruecken.</summary>
        public static List<Kasten> Aufbereiten(Teile t, Func<double, double, double, bool> gehoert)
        {
            var geteilt = new List<Kasten>(t.Boxen.Count);
            foreach (var b in t.Boxen) Spalten(b, Math.Max(0.25, t.Fade), gehoert, geteilt, 0);
            var r = new List<Kasten>(geteilt.Count);
            foreach (var b in geteilt) r.Add(Einruecken(b, t.Fade, gehoert));
            return r;
        }

        /// <summary>Eine Seite ist "gemischt", wenn dahinter teils derselbe Raum, teils etwas anderes liegt (L-foermige Halle: die
        /// Seite der einen Box grenzt halb an die andere Box, halb an ein Buero). Eine Box kann nur ganz oder gar nicht einruecken -
        /// darum wird sie an der Stelle des Wechsels geteilt (Ebene quer zur Seite), bis jede Seite einheitlich ist (hoechstens 6 Stufen).
        /// Teile nicht duenner als Fade: duennere Teile ergaenzen ihre Rampen nicht mehr zu 1 (siehe MinEbenenAbstand); ein Wechsel
        /// naeher als Fade an der Ecke bleibt darum ungeteilt (Mehrheit entscheidet in Einruecken).</summary>
        static void Spalten(in Kasten k, double minTeil, Func<double, double, double, bool> gehoert, List<Kasten> aus, int tiefe)
        {
            const double Vor = 0.1; const int N = 16, M = 5;
            double[] h = { k.Hx, k.Hy, k.Hz };
            double[][] e = { new[] { k.Ux, k.Uy, k.Uz }, new[] { k.Vx, k.Vy, k.Vz }, new[] { k.Wx, k.Wy, k.Wz } };
            if (tiefe < 6)
                for (int a = 0; a < 3; a++)
                    for (int s = -1; s <= 1; s += 2)
                        for (int t = 1; t <= 2; t++)
                        {
                            int b = (a + t) % 3, c = (a + 3 - t) % 3;
                            if (2 * h[b] < 2 * minTeil) continue;
                            int vorher = -1, wechsel = -1;
                            for (int i = 0; i < N && wechsel < 0; i++)
                            {
                                int ja = 0;
                                for (int j = 0; j < M; j++)
                                {
                                    double fb = (-1 + (2 * i + 1.0) / N) * h[b], fc = (-1 + (2 * j + 1.0) / M) * h[c], d = s * (h[a] + Vor);
                                    double x = k.Mx + d * e[a][0] + fb * e[b][0] + fc * e[c][0];
                                    double y = k.My + d * e[a][1] + fb * e[b][1] + fc * e[c][1];
                                    double z = k.Mz + d * e[a][2] + fb * e[b][2] + fc * e[c][2];
                                    if (gehoert(x, y, z)) ja++;
                                }
                                int bit = 2 * ja >= M ? 1 : 0;
                                if (vorher >= 0 && bit != vorher) wechsel = i;
                                vorher = bit;
                            }
                            if (wechsel <= 0) continue;
                            double schnitt = -h[b] + 2 * h[b] * wechsel / N;   // lokale Koordinate der Teilung auf Achse b
                            double l1 = schnitt + h[b], l2 = h[b] - schnitt;
                            if (l1 < minTeil || l2 < minTeil) continue;
                            var k1 = k; var k2 = k;
                            double m1 = -h[b] + l1 / 2, m2 = schnitt + l2 / 2;
                            Setze(ref k1, b, l1 / 2, m1, e); Setze(ref k2, b, l2 / 2, m2, e);
                            Spalten(k1, minTeil, gehoert, aus, tiefe + 1); Spalten(k2, minTeil, gehoert, aus, tiefe + 1);
                            return;
                        }
            aus.Add(k);
        }

        static void Setze(ref Kasten k, int achse, double halb, double mitteLokal, double[][] e)
        {
            k.Mx += mitteLokal * e[achse][0]; k.My += mitteLokal * e[achse][1]; k.Mz += mitteLokal * e[achse][2];
            if (achse == 0) k.Hx = halb; else if (achse == 1) k.Hy = halb; else k.Hz = halb;
        }

        /// <summary>Rueckt die Seiten der Teilbox, hinter denen NICHT derselbe Raum weitergeht, um fade/2 nach innen (siehe Kopf).
        /// Eine Seite gilt als innen, wenn mindestens die Haelfte von 5 x 5 Punkten 0,1 m vor ihr dem Raum gehoert.
        /// Untergrenze der halben Kante 0,05 m (Zone.Anlegen: 0,1 m).</summary>
        public static Kasten Einruecken(in Kasten k, double fade, Func<double, double, double, bool> gehoert)
        {
            const double Vor = 0.1; const int N = 5;
            double[] h = { k.Hx, k.Hy, k.Hz };
            double[][] e = { new[] { k.Ux, k.Uy, k.Uz }, new[] { k.Vx, k.Vy, k.Vz }, new[] { k.Wx, k.Wy, k.Wz } };
            double[] verschiebung = new double[3], neuH = (double[])h.Clone();
            for (int a = 0; a < 3; a++)
            {
                int b = (a + 1) % 3, c = (a + 2) % 3;
                double rein = 0;
                for (int s = -1; s <= 1; s += 2)
                {
                    int ja = 0;
                    for (int u = 0; u < N; u++)
                        for (int v = 0; v < N; v++)
                        {
                            double fu = (-1 + (2 * u + 1.0) / N) * h[b], fv = (-1 + (2 * v + 1.0) / N) * h[c], d = s * (h[a] + Vor);
                            double x = k.Mx + d * e[a][0] + fu * e[b][0] + fv * e[c][0];
                            double y = k.My + d * e[a][1] + fu * e[b][1] + fv * e[c][1];
                            double z = k.Mz + d * e[a][2] + fu * e[b][2] + fv * e[c][2];
                            if (gehoert(x, y, z)) ja++;
                        }
                    bool innen = 2 * ja >= N * N;
                    double ein = innen ? 0 : 0.5 * fade;
                    // Seite s ruckt um 'ein' nach innen: Mitte wandert um -s*ein/2, Halbkante schrumpft um ein/2
                    verschiebung[a] += -s * ein / 2; rein += ein / 2;
                }
                neuH[a] = Math.Max(0.05, h[a] - rein);
            }
            var o = k;
            o.Mx = k.Mx + verschiebung[0] * e[0][0] + verschiebung[1] * e[1][0] + verschiebung[2] * e[2][0];
            o.My = k.My + verschiebung[0] * e[0][1] + verschiebung[1] * e[1][1] + verschiebung[2] * e[2][1];
            o.Mz = k.Mz + verschiebung[0] * e[0][2] + verschiebung[1] * e[1][2] + verschiebung[2] * e[2][2];
            o.Hx = neuH[0]; o.Hy = neuH[1]; o.Hz = neuH[2];
            return o;
        }

        /// <summary>Gewicht der Zone (Zonenquader z, Fade f) am Punkt nach der ANNAHME oben: je Achse 1 bis f/2 innerhalb der
        /// Zonenseite, linear auf 0 bei f/2 ausserhalb; die Achsen multipliziert (Annahme). Nur fuer das Klang-Log.</summary>
        public static double Gewicht(in Kasten z, double f, double x, double y, double zz)
        {
            double dx = x - z.Mx, dy = y - z.My, dz = zz - z.Mz;
            double a = Math.Abs(dx * z.Ux + dy * z.Uy + dz * z.Uz), b = Math.Abs(dx * z.Vx + dy * z.Vy + dz * z.Vz), c = Math.Abs(dx * z.Wx + dy * z.Wy + dz * z.Wz);
            double w = Rampe(z.Hx, a, f); if (w <= 0) return 0;
            w *= Rampe(z.Hy, b, f); if (w <= 0) return 0;
            return w * Rampe(z.Hz, c, f);
        }
        static double Rampe(double h, double l, double f)
        {
            if (f <= 1e-9) return l <= h ? 1 : 0;
            return Math.Max(0, Math.Min(1, (h + 0.5 * f - l) / f));
        }

        // ---------------------------------------------------------------- Werte einer Zone

        /// <summary>A3: gesetzte Nachhallzeit-Verschiebung (s) und der dazu passende Pegel (dB). dRT wird auf [min, max] begrenzt und mit
        /// 'staerke' multipliziert; der Pegel folgt der GESETZTEN Zeit: Energiedichte des Diffusfelds w = 4P/(cA) mit A = 0,161 V/T
        /// (Kuttruff, Room Acoustics 6. Aufl. 2017, Kap. 5), also w ~ T/V und dPegel = 10 lg((T_geb + dRT)/T_geb). Fuer die ungedeckelte Zeit ist das genau
        /// 10 lg(A'_alt/A'_neu) wie bisher (RaumGeometrieKern.HallKorrektur). Kein eigener dB-Deckel mehr (er war nur eine zweite
        /// Sicherung neben dem Zeitdeckel und machte Pegel und Zeit wieder unstimmig).</summary>
        public static void Gesetzt(double tGebacken, double dRtRoh, double staerke, double min, double max, out double dRt, out double dPegel)
        {
            dRt = Math.Max(min, Math.Min(max, dRtRoh)) * staerke;
            double t = tGebacken + dRt;
            dPegel = tGebacken > 1e-6 && t > 1e-6 ? 10.0 * Math.Log10(t / tGebacken) : 0;
        }

        /// <summary>Hoerbare Abweichung vom gebackenen Standard (dB): groesstes |10 lg(T_gesetzt/T_geb)| ueber die Baender.
        /// Zeit und Pegel des Diffusfelds haengen ueber T zusammen (siehe Gesetzt), darum ein Mass fuer beide.</summary>
        public static double Abweichung(double[] tGebacken, double[] dRt)
        {
            double m = 0;
            for (int b = 0; b < dRt.Length; b++)
            {
                double t = tGebacken[b] + dRt[b];
                if (tGebacken[b] > 1e-6 && t > 1e-6) m = Math.Max(m, Math.Abs(10.0 * Math.Log10(t / tGebacken[b])));
            }
            return m;
        }

        /// <summary>Limit "Max zones": Raeume nach hoerbarer Abweichung x Volumen (Auftrag) absteigend; ein Raum bekommt alle seine
        /// Zonen oder keine (eine halbe Zonenmenge wuerde ihn nur teilweise abdecken). Rueckgabe: gewaehlte Raeume (Index),
        /// 'leer' = Zahl der Raeume ohne Zone, 'belegt' = Zonen insgesamt.</summary>
        public static List<int> Auswahl(double[] abweichung, double[] volumen, int[] zonen, int budget, out int leer, out int belegt)
        {
            int n = abweichung.Length;
            var idx = new int[n];
            var score = new double[n];
            for (int i = 0; i < n; i++) { idx[i] = i; score[i] = abweichung[i] * volumen[i]; }
            Array.Sort(idx, (a, b) => { int c = score[b].CompareTo(score[a]); return c != 0 ? c : a.CompareTo(b); });
            var r = new List<int>(n); belegt = 0; leer = 0;
            foreach (int i in idx)
            {
                if (zonen[i] <= 0) continue;
                if (belegt + zonen[i] <= budget) { r.Add(i); belegt += zonen[i]; }
                else leer++;
            }
            return r;
        }
    }
}
