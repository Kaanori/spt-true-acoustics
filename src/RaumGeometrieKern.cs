using System;

namespace Materialklang
{
    // tankhall (10.11.2026, Meldung Factory "Boiler_spawn_room" 18:49 MARK 4: "klang nicht wie in einem Metallfass").
    // Ursache: ein Audio-Raum des Spiels besteht oft aus MEHREREN, sich ueberlappenden BoxCollidern (der Tank: 10 gleich
    // grosse Scheiben 1,0 x 6,4 x 6,1 m, je 18 Grad gedreht - zusammen ein Zylinder r ~ 3,1 m). Bis hierher
    //   - nahm HallZonen nur die "groesste" Box (eine 1 m duenne Scheibe, 39 m3): 53 der 64 Messstrahlen endeten nach 1,1 m
    //     im Leeren, < 12 Treffer -> Raum verworfen, KEINE Hallzone, der Hoerer hoerte den gebackenen Meta-Hall;
    //   - summierte der Raumweg (Raumweg.Netz.Messen) Flaechen und Volumen aller Boxen: S 1030 statt ~185 m2, V 392 statt ~190 m3
    //     -> A = S * 0,1 + 5,4 = 109 m2 statt ~ 24 m2 ("alpha 0,34").
    // Hier: Raum = VEREINIGUNG seiner Boxen (so definiert das Spiel den Raum, AudioTriggerArea). Reiner Kern ohne Unity-Typen.
    // Dazu die Nachhallzeit nach Eyring mit Luft (fuer harte Raeume genauer als Sabine) und Oeffnungen mit ihrer echten Flaeche.
    internal struct Kasten
    {
        public double Mx, My, Mz;          // Mitte (Welt)
        public double Ux, Uy, Uz;          // Einheitsachse x (Welt)
        public double Vx, Vy, Vz;          // Einheitsachse y
        public double Wx, Wy, Wz;          // Einheitsachse z
        public double Hx, Hy, Hz;          // halbe Kantenlaengen (m)

        public static Kasten Achsparallel(double mx, double my, double mz, double hx, double hy, double hz)
            => new Kasten { Mx = mx, My = my, Mz = mz, Ux = 1, Vy = 1, Wz = 1, Hx = hx, Hy = hy, Hz = hz };

        /// <summary>Um die senkrechte Achse (y) um 'grad' gedreht (Unity: Drehung um +y, x-Achse -> (cos, 0, -sin)).</summary>
        public static Kasten UmY(double mx, double my, double mz, double hx, double hy, double hz, double grad)
        {
            double r = grad * Math.PI / 180.0, c = Math.Cos(r), s = Math.Sin(r);
            return new Kasten { Mx = mx, My = my, Mz = mz, Ux = c, Uy = 0, Uz = -s, Vx = 0, Vy = 1, Vz = 0, Wx = s, Wy = 0, Wz = c, Hx = hx, Hy = hy, Hz = hz };
        }

        public double Volumen => 8.0 * Hx * Hy * Hz;
        public double Oberflaeche => 8.0 * (Hx * Hy + Hy * Hz + Hx * Hz);
    }

    internal static class RaumGeometrieKern
    {
        public static bool Drin(in Kasten k, double x, double y, double z, double eps = 1e-6)
        {
            double dx = x - k.Mx, dy = y - k.My, dz = z - k.Mz;
            return Math.Abs(dx * k.Ux + dy * k.Uy + dz * k.Uz) <= k.Hx + eps
                && Math.Abs(dx * k.Vx + dy * k.Vy + dz * k.Vz) <= k.Hy + eps
                && Math.Abs(dx * k.Wx + dy * k.Wy + dz * k.Wz) <= k.Hz + eps;
        }

        public static bool DrinVereinigung(Kasten[] ks, int n, double x, double y, double z, double eps = 1e-6)
        {
            for (int i = 0; i < n; i++) if (Drin(ks[i], x, y, z, eps)) return true;
            return false;
        }

        /// <summary>Strecke vom Punkt p (in der Box) in Richtung d (Einheitsvektor) bis zum Rand der Box.</summary>
        static double AustrittBox(in Kasten k, double px, double py, double pz, double dx, double dy, double dz)
        {
            double qx = px - k.Mx, qy = py - k.My, qz = pz - k.Mz;
            double t = double.MaxValue;
            Achse(qx * k.Ux + qy * k.Uy + qz * k.Uz, dx * k.Ux + dy * k.Uy + dz * k.Uz, k.Hx, ref t);
            Achse(qx * k.Vx + qy * k.Vy + qz * k.Vz, dx * k.Vx + dy * k.Vy + dz * k.Vz, k.Hy, ref t);
            Achse(qx * k.Wx + qy * k.Wy + qz * k.Wz, dx * k.Wx + dy * k.Wy + dz * k.Wz, k.Hz, ref t);
            return t == double.MaxValue ? 0 : Math.Max(0, t);
        }
        static void Achse(double l, double d, double h, ref double t)
        {
            if (Math.Abs(d) < 1e-12) return;
            double s = d > 0 ? (h - l) / d : (-h - l) / d;
            if (s < t) t = s;
        }

        /// <summary>Strecke vom Punkt p in Richtung d (Einheitsvektor) bis zum Austritt aus der Vereinigung der Boxen
        /// (0, wenn p in keiner Box liegt). Laeuft von Box zu Box weiter, solange der Punkt noch in einer liegt.</summary>
        public static double Austritt(Kasten[] ks, int n, double px, double py, double pz, double dx, double dy, double dz)
        {
            double t = 0;
            for (int iter = 0; iter < 64; iter++)
            {
                double x = px + dx * t, y = py + dy * t, z = pz + dz * t, best = -1;
                for (int i = 0; i < n; i++)
                    if (Drin(ks[i], x, y, z, 1e-6)) { double te = AustrittBox(ks[i], x, y, z, dx, dy, dz); if (te > best) best = te; }
                if (best < 0) break;
                if (best < 1e-5) break;
                t += best + 1e-5;
            }
            return t;
        }

        /// <summary>Volumen der Vereinigung (m3). Eine Box: exakt. Sonst Mittelpunktregel auf einem Wuerfelgitter in der
        /// achsparallelen Huelle mit hoechstens 'proben' Zellen (Fehler ~ Rand/Zellgroesse, beim Tank &lt; 2 %).</summary>
        public static double Volumen(Kasten[] ks, int n, int proben = 60000)
        {
            if (n <= 0) return 0;
            if (n == 1) return ks[0].Volumen;
            Huelle(ks, n, out double x0, out double y0, out double z0, out double x1, out double y1, out double z1);
            double vh = (x1 - x0) * (y1 - y0) * (z1 - z0);
            if (vh <= 0) return 0;
            double h = Math.Pow(vh / proben, 1.0 / 3.0);   // net472: kein Math.Cbrt
            int nx = Math.Max(1, (int)Math.Ceiling((x1 - x0) / h)), ny = Math.Max(1, (int)Math.Ceiling((y1 - y0) / h)), nz = Math.Max(1, (int)Math.Ceiling((z1 - z0) / h));
            double hx = (x1 - x0) / nx, hy = (y1 - y0) / ny, hz = (z1 - z0) / nz;
            long drin = 0;
            for (int i = 0; i < nx; i++)
            {
                double x = x0 + (i + 0.5) * hx;
                for (int j = 0; j < ny; j++)
                {
                    double y = y0 + (j + 0.5) * hy;
                    for (int k = 0; k < nz; k++)
                        if (DrinVereinigung(ks, n, x, y, z0 + (k + 0.5) * hz)) drin++;
                }
            }
            return drin * hx * hy * hz;
        }

        /// <summary>Oberflaeche der Vereinigung (m2): Boxseiten, soweit sie nicht im Inneren einer anderen Box liegen.
        /// Je Seite ein Gitter jeSeite x jeSeite; ein Gitterpunkt zaehlt als Aussenflaeche, wenn der Punkt 'luecke' Meter
        /// vor der Seite (nach aussen) in keiner anderen Box liegt - aneinanderstossende Boxen haben so keine Zwischenwand.
        /// Deckungsgleiche Seiten (bis 'luecke' auseinander) zaehlen einmal (bei der Box mit dem kleineren Index).
        /// ANNAHME: Spalten unter 'luecke' (5 cm) zwischen Trigger-Boxen sind Ungenauigkeit beim Bau, keine Wand.</summary>
        public static double Oberflaeche(Kasten[] ks, int n, int jeSeite = 24, double luecke = 0.05)
        {
            if (n <= 0) return 0;
            if (n == 1) return ks[0].Oberflaeche;
            double summe = 0;
            for (int b = 0; b < n; b++)
            {
                var k = ks[b];
                for (int achse = 0; achse < 3; achse++)
                    for (int seite = -1; seite <= 1; seite += 2)
                    {
                        // Normale e, Flaechenachsen a1/a2 mit halben Laengen
                        double ex, ey, ez, h, ax, ay, az, ha, bx, by, bz, hb;
                        if (achse == 0) { ex = k.Ux; ey = k.Uy; ez = k.Uz; h = k.Hx; ax = k.Vx; ay = k.Vy; az = k.Vz; ha = k.Hy; bx = k.Wx; by = k.Wy; bz = k.Wz; hb = k.Hz; }
                        else if (achse == 1) { ex = k.Vx; ey = k.Vy; ez = k.Vz; h = k.Hy; ax = k.Ux; ay = k.Uy; az = k.Uz; ha = k.Hx; bx = k.Wx; by = k.Wy; bz = k.Wz; hb = k.Hz; }
                        else { ex = k.Wx; ey = k.Wy; ez = k.Wz; h = k.Hz; ax = k.Ux; ay = k.Uy; az = k.Uz; ha = k.Hx; bx = k.Vx; by = k.Vy; bz = k.Vz; hb = k.Hy; }
                        double cx = k.Mx + seite * ex * (h + luecke), cy = k.My + seite * ey * (h + luecke), cz = k.Mz + seite * ez * (h + luecke);
                        int aussen = 0;
                        for (int i = 0; i < jeSeite; i++)
                        {
                            double u = (-1 + (2 * i + 1.0) / jeSeite) * ha;
                            for (int j = 0; j < jeSeite; j++)
                            {
                                double v = (-1 + (2 * j + 1.0) / jeSeite) * hb;
                                double x = cx + u * ax + v * bx, y = cy + u * ay + v * by, z = cz + u * az + v * bz;
                                // Gegenpunkt 'luecke' innen vor der Seite: schneidet eine Box mit kleinerem Index dieselbe Stelle
                                // (innen drin, aussen nicht), zaehlt dort deren Seite - deckungsgleiche Seiten nur einmal
                                double xi = x - seite * ex * 2 * luecke, yi = y - seite * ey * 2 * luecke, zi = z - seite * ez * 2 * luecke;
                                bool innen = false;
                                for (int o = 0; o < n && !innen; o++)
                                {
                                    if (o == b) continue;
                                    if (Drin(ks[o], x, y, z, 0)) innen = true;
                                    else if (o < b && Drin(ks[o], xi, yi, zi, 0)) innen = true;
                                }
                                if (!innen) aussen++;
                            }
                        }
                        summe += 4.0 * ha * hb * aussen / (jeSeite * (double)jeSeite);
                    }
            }
            return summe;
        }

        static void Huelle(Kasten[] ks, int n, out double x0, out double y0, out double z0, out double x1, out double y1, out double z1)
        {
            x0 = y0 = z0 = double.MaxValue; x1 = y1 = z1 = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                var k = ks[i];
                double rx = Math.Abs(k.Ux) * k.Hx + Math.Abs(k.Vx) * k.Hy + Math.Abs(k.Wx) * k.Hz;
                double ry = Math.Abs(k.Uy) * k.Hx + Math.Abs(k.Vy) * k.Hy + Math.Abs(k.Wy) * k.Hz;
                double rz = Math.Abs(k.Uz) * k.Hx + Math.Abs(k.Vz) * k.Hy + Math.Abs(k.Wz) * k.Hz;
                x0 = Math.Min(x0, k.Mx - rx); x1 = Math.Max(x1, k.Mx + rx);
                y0 = Math.Min(y0, k.My - ry); y1 = Math.Max(y1, k.My + ry);
                z0 = Math.Min(z0, k.Mz - rz); z1 = Math.Max(z1, k.Mz + rz);
            }
        }

        /// <summary>Huelle der Vereinigung im Achsensystem der Box 'bezug': Mitte (Welt) und halbe Kantenlaengen.
        /// Fuer die Meta-Control-Zone (eine Box je Raum).</summary>
        public static Kasten HuelleIn(Kasten[] ks, int n, int bezug)
        {
            var r = ks[bezug];
            double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
            for (int i = 0; i < n; i++)
            {
                var k = ks[i];
                for (int e = 0; e < 8; e++)
                {
                    double sx = (e & 1) != 0 ? k.Hx : -k.Hx, sy = (e & 2) != 0 ? k.Hy : -k.Hy, sz = (e & 4) != 0 ? k.Hz : -k.Hz;
                    double x = k.Mx + sx * k.Ux + sy * k.Vx + sz * k.Wx - r.Mx;
                    double y = k.My + sx * k.Uy + sy * k.Vy + sz * k.Wy - r.My;
                    double z = k.Mz + sx * k.Uz + sy * k.Vz + sz * k.Wz - r.Mz;
                    double l0 = x * r.Ux + y * r.Uy + z * r.Uz, l1 = x * r.Vx + y * r.Vy + z * r.Vz, l2 = x * r.Wx + y * r.Wy + z * r.Wz;
                    lo[0] = Math.Min(lo[0], l0); hi[0] = Math.Max(hi[0], l0);
                    lo[1] = Math.Min(lo[1], l1); hi[1] = Math.Max(hi[1], l1);
                    lo[2] = Math.Min(lo[2], l2); hi[2] = Math.Max(hi[2], l2);
                }
            }
            double m0 = (lo[0] + hi[0]) / 2, m1 = (lo[1] + hi[1]) / 2, m2 = (lo[2] + hi[2]) / 2;
            var o = r;
            o.Mx = r.Mx + m0 * r.Ux + m1 * r.Vx + m2 * r.Wx;
            o.My = r.My + m0 * r.Uy + m1 * r.Vy + m2 * r.Wy;
            o.Mz = r.Mz + m0 * r.Uz + m1 * r.Vz + m2 * r.Wz;
            o.Hx = (hi[0] - lo[0]) / 2; o.Hy = (hi[1] - lo[1]) / 2; o.Hz = (hi[2] - lo[2]) / 2;
            return o;
        }

        // ---------------------------------------------------------------- Nachhall

        /// <summary>Aequivalente Absorptionsflaeche nach Eyring (m2): -S ln(1 - A/S), A = (S - So) alpha + So.
        /// Quelle: C. F. Eyring, JASA 1 (1930) 217-241; Kuttruff, Room Acoustics (6. Aufl. 2017) Kap. 5.
        /// Fuer kleines mittleres alpha gleich der Sabine-Flaeche A; fuer harte Raeume die bessere Naeherung.</summary>
        public static double EyringFlaeche(double s, double sOffen, double alphaWand)
        {
            if (s <= 0) return 0;
            double so = Math.Max(0, Math.Min(sOffen, s));
            double a = (s - so) * Math.Max(0, Math.Min(1, alphaWand)) + so;
            double am = Math.Min(a / s, 0.999);       // ganz offene "Raeume" haben keinen Nachhall (ln -> -inf)
            return -s * Math.Log(1 - am);
        }

        /// <summary>T60 (s) nach Eyring mit Luft: 0,161 V / (-S ln(1 - am) + 4 m V).</summary>
        public static double T60(double v, double s, double sOffen, double alphaWand, double mLuft)
        {
            double a = EyringFlaeche(s, sOffen, alphaWand) + 4.0 * mLuft * v;
            return a > 1e-9 ? 0.161 * v / a : 0;
        }

        /// <summary>Verschiebung von Nachhallzeit (s) und Hallpegel (dB) gegenueber dem Meta-Bake (alle Flaechen alpha 'gebacken').
        /// Die Oeffnungen (sOffen, alpha = 1) sieht auch Meta (Loch in der .xrageo), sie stehen darum in BEIDEN Rechnungen.
        /// Hallpegel ~ 1/A' (diffuses Feld, Kuttruff/Bies-Hansen) mit A' = Eyring-Flaeche + 4mV.</summary>
        public static void HallKorrektur(double v, double s, double sOffen, double alphaWand, double gebacken, double mLuft,
                                         out double dRt, out double dPegel)
        {
            double luft = 4.0 * mLuft * v;
            double aNeu = EyringFlaeche(s, sOffen, alphaWand) + luft, aAlt = EyringFlaeche(s, sOffen, gebacken) + luft;
            dRt = 0.161 * v / Math.Max(aNeu, 1e-9) - 0.161 * v / Math.Max(aAlt, 1e-9);
            dPegel = 10.0 * Math.Log10(Math.Max(aAlt, 1e-9) / Math.Max(aNeu, 1e-9));
        }

        /// <summary>Mittlerer Absorptionsgrad des Raums mit Oeffnungen: ((S - So) alpha + So) / S (Sabine-Sinn, fuer A = S * alpha).</summary>
        public static double AlphaMitOeffnungen(double s, double sOffen, double alphaWand)
        {
            if (s <= 0) return alphaWand;
            double so = Math.Max(0, Math.Min(sOffen, s));
            return ((s - so) * alphaWand + so) / s;
        }
    }
}
