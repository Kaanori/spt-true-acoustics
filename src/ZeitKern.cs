using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // tunneldiffus (17.11.2026) - Zeitliche Verschmierung des Raumanteils. Reine Rechnung (ohne Unity, Offline-Test).
    //
    // Ursache (Meldung Kaan 17.11. ~17:47, Factory, Hoerer im Tunnel Underground_New_3_Room_5: "jeder Schritt ueber mir, jedes Wort";
    //   Klang-Log 2026-11-17_17-38-18, Handrechnung review-20261110/tunneldiffus/hand.py):
    //   Der Raumweg (RaumwegKern.Route/Kette) rechnet die STATIONAERE Energie am Ohr: Diffusfeld des Quellraums S/A, je Raumstufe P S/A,
    //   am Hoerer 4P/A, dazu die Abstrahlung der letzten Oeffnung (Kuttruff, Room Acoustics, Kap. 5). Das stimmt mit der Handrechnung
    //   auf 1-4 dB (die Mod liegt eher darunter). Seit diffushoerbar (17.11. 17:30) spielt die Mod diese Energie als EIN trockenes Ereignis.
    //   Fuer Dauerklaenge richtig - fuer kurze Klaenge (Schritt, Klick, Schuss) nicht: die Energie aus dem Hallfeld einer Halle mit
    //   T = 7 s kommt ueber Sekunden verteilt an, das Gehoer (und das Mass, in dem die Mod kalibriert: LAFmax) bewertet nur ~125 ms.
    //   Gespielt als kompakter Schritt war der Raumanteil 5-10 dB zu laut (250 Hz/1 kHz), 3-7 dB bei 4 kHz.
    //
    // Physik (Quellen):
    //  - Jede Raumstufe ist fuer die Energie ein Tiefpass 1. Ordnung: V dw/dt = P_ein - c A w / 4 (Kuttruff, Room Acoustics, 5. Aufl.,
    //    Kap. 5.1 Gl. 5.4/5.5: Abklingen w(t) = w0 exp(-c A t / 4V), Zeitkonstante tau = 4V / (c A) = T60 / 13,8). Gekoppelte Raeume
    //    ohne Rueckfluss (Kap. 5.7; dieselbe Naeherung wie die stationaere Kette): Hintereinanderschaltung dieser Tiefpaesse. Die
    //    Energie-Impulsantwort einer Route ist die Faltung der Exponentialkerne (Hypoexponential-Verteilung), Flaeche 1.
    //  - Ereignispegel = LAFmax: Zeitbewertung F, exponentiell mit tau_F = 0,125 s (IEC 61672-1:2013, 5.8) - genau das Mass der
    //    Quellpegel der Mod (QuellPegelKern/KalibrierKern: LAFmax in 1 m). Das F-Glied ist eine weitere Stufe 1. Ordnung.
    //  - Signal: Rechteck der Dauer tau_s mit derselben Energie und demselben LAFmax wie der Clip. Aus der Clip-Tabelle (LAeq ueber die
    //    Laenge L, LAFmax): tau_eff = L 10^((LAeq - LAFmax)/10) = tau_s / (1 - exp(-tau_s / tau_F)) -> tau_s (SignalDauer).
    //  - Faktor k = max_t (rect * h_Kette * f_F)(t) / max_t (rect * f_F)(t): Anteil der stationaeren Energie eines Wegs, der im
    //    Ereignispegel ankommt. Dauerklang (tau_s -> unendlich): k = 1; Impuls in einem Raum mit tau >> tau_F: k ~ tau_F / (e tau).
    //    Gegenprobe der bisherigen Faustformel DiffusKern.ImFenster (1 - exp(-13,8 * 0,1 / T)): ein Raum T = 8 s -> 0,16 gegen k = 0,14.
    //  - Mehrere Raumstufen werden fuer die Rechnung auf ZWEI Stufen mit gleichem Mittel und gleicher Varianz der Laufzeitverteilung
    //    gebracht (Mittel = Summe tau_i, Varianz = Summe tau_i^2 - additiv fuer Faltungen, darum ueber Routen und Teilwege mischbar);
    //    das F-Glied bleibt exakt die dritte Stufe. Fehler gegen die numerische Faltung (naeherung.py, Variante B): hoechstens 0,5 dB
    //    fuer die Ketten der Meldung, 0,7 dB fuer 4 gleiche Stufen; immer zu LEISE (Vorzeichen bekannt).
    //  - Der gerichtete Anteil (Kantenbeugung durch die Oeffnungen) ist nicht verschmiert (k = 1). Die Abstrahlung der letzten Oeffnung
    //    ist es: sie strahlt das Hallfeld des Raums davor ab (Kette ohne die Hoererraum-Stufe).
    // Was fehlt dann? Der Rest (1 - k) der Energie ist der Nachhall, der nach dem Ereignis abklingt. Ihn kann nur ein hoerbarer Hall
    // tragen; Metas Hall liegt 30-50 dB darunter (metamessung F3) - Grenze der Technik (EigenerRaumklang/MACHBARKEIT.md, Etappe 5).
    // Ihn als Teil des Ereignisses trocken zu spielen, macht das Ereignis um 1/k zu laut und zu klar (Sprache verstaendlich statt verhallt).
    // ------------------------------------------------------------------------------------------
    internal static class ZeitKern
    {
        /// <summary>Zeitkonstante der Zeitbewertung F (s), IEC 61672-1.</summary>
        public const double TauF = 0.125;
        public const double Schall = 343.0;
        /// <summary>Mindestabstand der Zeitkonstanten (relativ) in der geschlossenen Formel (Hypoexponential mit verschiedenen Raten).
        /// Gleiche tau werden um diesen Anteil getrennt (Fehler &lt; 0,1 dB, review-20261110/tunneldiffus/naeherung.py).</summary>
        public const double Trennung = 0.05;

        /// <summary>Energie-Zeitkonstante eines Raums (s): tau = 4 V / (c A) (Kuttruff Kap. 5.1). 0 = keine Raumstufe (im Freien, unbekannt).</summary>
        public static double TauRaum(double v, double a) => v > 0 && a > 0 ? 4.0 * v / (Schall * a) : 0.0;

        /// <summary>Energie-aequivalente Dauer (s) eines Clips aus Laenge (s), LAeq und LAFmax (dB): L 10^((LAeq - LAFmax)/10).
        /// NaN, wenn etwas fehlt.</summary>
        public static double TauEff(double laengeS, double laeq, double lafmax)
        {
            if (!(laengeS > 0) || double.IsNaN(laeq) || double.IsNaN(lafmax)) return double.NaN;
            return laengeS * Math.Pow(10, (laeq - lafmax) / 10.0);
        }

        /// <summary>Dauer tau_s (s) des Rechtecks mit gleichem LAFmax und gleicher Energie: Loesung von tau_s / (1 - exp(-tau_s/tau_F)) = tauEff.
        /// tauEff &lt;= tau_F: 0 (Impuls); NaN/unendlich: unendlich (Dauerklang).</summary>
        public static double SignalDauer(double tauEff)
        {
            if (double.IsNaN(tauEff) || double.IsPositiveInfinity(tauEff)) return double.PositiveInfinity;
            if (tauEff <= TauF) return 0.0;
            if (tauEff > 50 * TauF) return tauEff;              // 1 - exp(-50) = 1 auf 1e-21
            double lo = 0, hi = tauEff;                          // f(x) = x / (1 - e^(-x/tauF)) steigt, f(tauEff) > tauEff
            for (int i = 0; i < 60; i++)
            {
                double m = 0.5 * (lo + hi);
                double f = m > 0 ? m / (1 - Math.Exp(-m / TauF)) : TauF;
                if (f < tauEff) lo = m; else hi = m;
            }
            return 0.5 * (lo + hi);
        }

        /// <summary>Teil der Energie mit gemeinsamer Zeitstruktur: E = Energieanteil (an der Summe der Route bzw. Mischung),
        /// M1 = mittlere Laufzeit der Raumstufen (s), M2 = zweites Moment (s^2, roh: Varianz + M1^2).
        /// (float: liegt je Route und Band im RaumwegErgebnis - halbe Groesse fuer die Kopien im Raumwege-Benchmark; 7 Stellen reichen.)</summary>
        public struct Teil
        {
            public float E, M1, M2;
            public bool Leer => !(E > 0);
        }

        /// <summary>Summe fuer Teil (double): Energie, Energie x Mittel, Energie x zweites Moment.</summary>
        public struct Summe
        {
            public double E, M1, M2;
        }

        /// <summary>Summiert Energie e mit Laufzeitmittel mu und Varianz var (beide aus einer Kette: mu = Summe tau, var = Summe tau^2)
        /// ungewichtet auf (Normiert teilt danach).</summary>
        public static void Dazu(ref Summe t, double e, double mu, double var)
        {
            if (!(e > 0)) return;
            t.E += e; t.M1 += e * mu; t.M2 += e * (var + mu * mu);
        }

        /// <summary>Aus der Summe (Dazu) den Teil mit Anteil E = Summe / gesamt und Momenten je Energie machen.</summary>
        public static Teil Normiert(in Summe summe, double gesamt)
        {
            var t = new Teil();
            if (!(summe.E > 0) || !(gesamt > 0)) return t;
            t.E = (float)Math.Min(1.0, summe.E / gesamt); t.M1 = (float)(summe.M1 / summe.E); t.M2 = (float)(summe.M2 / summe.E);
            return t;
        }

        /// <summary>Faktor k (0..1): Anteil der stationaeren Energie eines Teils mit Laufzeitmomenten m1/m2 (s, s^2), der im LAFmax eines
        /// Signals der Dauer tauS (s, 0 = Impuls, unendlich = Dauerklang) ankommt (siehe Kopf). m1 &lt;= 0: keine Raumstufe, k = 1.</summary>
        public static double Faktor(double m1, double m2, double tauS)
        {
            if (!(m1 > 1e-6) || double.IsNaN(m2)) return 1.0;
            if (double.IsPositiveInfinity(tauS) || double.IsNaN(tauS)) return 1.0;
            double var = Math.Max(0, m2 - m1 * m1);
            // zwei Stufen mit gleichem Mittel und gleicher Varianz: a + b = m1, a^2 + b^2 = var
            double a, b;
            double disk = 2 * var - m1 * m1;
            if (disk >= 0)
            {
                double r = Math.Sqrt(disk);
                a = 0.5 * (m1 + r); b = 0.5 * (m1 - r);
                // Varianz groesser als die eines Exponentials (nur bei Mischungen moeglich): eine Stufe mit dem Mittel (Varianz geht verloren)
                if (b < 1e-6 * m1) { a = m1; b = 0; }
            }
            else { a = b = 0.5 * m1; }                           // weniger Varianz als zwei gleiche Stufen: zwei gleiche Stufen
            if (tauS > 50 * (m1 + TauF) + 1) return 1.0;         // stationaer (Rest < 1e-20)
            // Stufen absteigend, Abstand >= Trennung
            double t1 = a, t2 = b, t3 = TauF;
            Sortieren(ref t1, ref t2, ref t3);
            int n = t3 > 1e-9 ? 3 : (t2 > 1e-9 ? 2 : 1);
            if (n >= 2 && t2 > t1 * (1 - Trennung)) t2 = t1 * (1 - Trennung);
            if (n >= 3 && t3 > t2 * (1 - Trennung)) t3 = t2 * (1 - Trennung);
            // Koeffizienten der CDF: H(t) = 1 - Summe c_i exp(-t / tau_i), c_i = Prod_{j != i} tau_i / (tau_i - tau_j)
            double c1, c2 = 0, c3 = 0;
            if (n == 1) c1 = 1;
            else if (n == 2) { c1 = t1 / (t1 - t2); c2 = t2 / (t2 - t1); }
            else
            {
                c1 = t1 / (t1 - t2) * t1 / (t1 - t3);
                c2 = t2 / (t2 - t1) * t2 / (t2 - t3);
                c3 = t3 / (t3 - t1) * t3 / (t3 - t2);
            }
            double d = Math.Max(tauS, 1e-4);
            double lo = 0, hi = d + 6 * (t1 + t2 + t3);
            const double g = 0.6180339887498949;
            double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo);
            double f1 = Rechteck(x1, d, n, t1, t2, t3, c1, c2, c3), f2 = Rechteck(x2, d, n, t1, t2, t3, c1, c2, c3);
            for (int i = 0; i < 48; i++)
            {
                // Rechteck * log-konkave Dichte ist log-konkav -> eingipflig, Goldener Schnitt findet das Maximum
                if (f1 < f2) { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = Rechteck(x2, d, n, t1, t2, t3, c1, c2, c3); }
                else { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = Rechteck(x1, d, n, t1, t2, t3, c1, c2, c3); }
            }
            double max = Math.Max(f1, f2);
            double direkt = 1 - Math.Exp(-d / TauF);             // LAFmax des Rechtecks selbst (nur F-Glied)
            double k = direkt > 0 ? max / direkt : 1.0;
            return k < 0 ? 0 : (k > 1 ? 1 : k);
        }

        static double H(double t, int n, double t1, double t2, double t3, double c1, double c2, double c3)
        {
            if (t <= 0) return 0;
            double s = c1 * Math.Exp(-t / t1);
            if (n >= 2) s += c2 * Math.Exp(-t / t2);
            if (n >= 3) s += c3 * Math.Exp(-t / t3);
            return 1 - s;
        }

        static double Rechteck(double t, double d, int n, double t1, double t2, double t3, double c1, double c2, double c3)
            => H(t, n, t1, t2, t3, c1, c2, c3) - H(t - d, n, t1, t2, t3, c1, c2, c3);

        static void Sortieren(ref double a, ref double b, ref double c)
        {
            if (a < b) { var x = a; a = b; b = x; }
            if (b < c) { var x = b; b = c; c = x; }
            if (a < b) { var x = a; a = b; b = x; }
        }

        /// <summary>Ereignis-Faktor r (0..1) der Energie: 1 - E_l (1 - k_l) - E_s (1 - k_s) fuer den langsamen Teil (ueber das Hallfeld des
        /// Quellraums) und den schnellen Teil (Direktanteil der Quelle in die erste Oeffnung, nur Raeume danach). Der Rest der Energie
        /// (gerichtet: Beugung durch die Oeffnungen) zaehlt ganz. Ereignis-Mehrdaempfung = -10 lg r.</summary>
        public static double Ereignis(in Teil langsam, in Teil schnell, double tauS, out double kL, out double kS)
        {
            kL = langsam.Leer ? 1.0 : Faktor(langsam.M1, langsam.M2, tauS);
            kS = schnell.Leer ? 1.0 : Faktor(schnell.M1, schnell.M2, tauS);
            double eL = langsam.Leer ? 0 : Math.Min(1, langsam.E), eS = schnell.Leer ? 0 : Math.Min(1, schnell.E);
            if (eL + eS > 1) { double s = eL + eS; eL /= s; eS /= s; }
            double r = 1 - eL * (1 - kL) - eS * (1 - kS);
            return r < 1e-6 ? 1e-6 : (r > 1 ? 1 : r);
        }

        // ------------------------------------------------------------------------------------------
        // tunneldiffus Teil 2 (17.11.2026) - Sprache: nuetzlicher (frueher) Anteil statt Ereignispegel.
        // Lautheit eines kurzen Ereignisses und Verstaendlichkeit von Sprache sind verschiedene Masse derselben Impulsantwort:
        //  - Lautheit/LAFmax: das Gehoer integriert die Energie ueber ~100-200 ms (Zeitbewertung F 125 ms, IEC 61672-1; Zwicker/Fastl
        //    Kap. 8.7) - spaete Energie macht ein Ereignis LAUTER, solange sie im Fenster liegt (Faktor oben).
        //  - Verstaendlichkeit: nur Energie bis ~50 ms nach der ersten Wellenfront verschmilzt mit dem Direktschall zu einer klaren Silbe;
        //    spaetere Energie verdeckt die folgenden Silben und wirkt als Stoerschall (Thiele 1953, Deutlichkeit; Lochner & Burger 1958,
        //    Nutz-/Stoerenergie; Bradley 1986 U50; ISO 3382-1:2009 Anh. A: D50 = E(0..50 ms) / E(0..unendlich)).
        //    Fuer einen exponentiellen Abfall D50 = 1 - exp(-13,8 * 0,05 / T) (Kuttruff Kap. 9).
        // Bei Sprache traegt das trockene Signal darum nur den NUETZLICHEN Anteil: Kette der Raumstufen (ohne F-Glied), Anteil der Energie
        // in den ersten 50 ms ab dem Einsatz = H_Kette(0,05 s). Der gerichtete Anteil (Beugung) ist die erste Wellenfront selbst (nuetzlich).
        // Der spaete Anteil ist reale Energie und gehoert in den Hall-Eingang (bleibt bei der Summe, DiffusHoerbarKern). Dieselben Momente,
        // dieselbe Zwei-Stufen-Naeherung wie Faktor - EIN Kern, zwei Masse (Fenster-CDF statt Maximum der F-bewerteten Antwort).
        // ------------------------------------------------------------------------------------------

        /// <summary>Fenster fuer die Deutlichkeit (s): D50 nach ISO 3382-1 Anh. A.</summary>
        public const double SprachFenster = 0.050;

        /// <summary>Frueher Anteil (0..1) der Energie eines Teils mit Laufzeitmomenten m1/m2: Energie in [0, fenster] / gesamt
        /// (Zwei-Stufen-Naeherung der Raumkette, ohne F-Glied). m1 &lt;= 0: keine Raumstufe, 1.</summary>
        public static double Frueh(double m1, double m2, double fenster)
        {
            if (!(m1 > 1e-6) || double.IsNaN(m2)) return 1.0;
            if (!(fenster > 0)) return 0.0;
            double var = Math.Max(0, m2 - m1 * m1);
            double a, b;
            double disk = 2 * var - m1 * m1;
            if (disk >= 0)
            {
                double r = Math.Sqrt(disk);
                a = 0.5 * (m1 + r); b = 0.5 * (m1 - r);
                if (b < 1e-6 * m1) { a = m1; b = 0; }
            }
            else { a = b = 0.5 * m1; }
            double t = fenster;
            if (b <= 0) return 1 - Math.Exp(-t / a);
            if (Math.Abs(a - b) < 1e-9 * a) return 1 - Math.Exp(-t / a) * (1 + t / a);   // Erlang-2
            return 1 - (a * Math.Exp(-t / a) - b * Math.Exp(-t / b)) / (a - b);
        }

        [ThreadStatic] static double[] kettePuffer;

        /// <summary>Exakte CDF einer Kette von Tiefpaessen (Energie-Impulsantwort, Hypoexponential-Verteilung) an der Stelle x (s):
        /// Stufen tau[i0..i1] (inklusive), Stufen &lt;= 0 (im Freien) zaehlen nicht. Gleiche/nahe tau werden um Trennung (5 %) getrennt
        /// (Fehler gegen die numerische Faltung: d50.py). Keine Stufe: 1. Keine Speicheranforderung nach dem ersten Aufruf je Faden.</summary>
        public static double KetteCdf(double[] tau, int i0, int i1, double x)
        {
            if (!(x > 0)) return 0;
            var b = kettePuffer ?? (kettePuffer = new double[24]);
            int n = 0;
            for (int i = i0; i <= i1 && i < tau.Length; i++) if (tau[i] > 1e-9 && n < b.Length) b[n++] = tau[i];
            if (n == 0) return 1.0;
            // absteigend sortieren (Einfuegen) und trennen
            for (int i = 1; i < n; i++) { double v = b[i]; int j = i - 1; while (j >= 0 && b[j] < v) { b[j + 1] = b[j]; j--; } b[j + 1] = v; }
            // nahe Werte (Abstand < Trennung) als Gruppe symmetrisch um ihr Mittel spreizen (Mittel der Laufzeit bleibt; nur nach unten
            // trennen machte die Kette schneller: 4 gleiche Stufen +1,1 dB im D50-Fenster)
            for (int i = 0; i < n;)
            {
                int j = i + 1;
                while (j < n && b[j] > b[j - 1] * (1 - Trennung)) j++;
                int L = j - i;
                if (L > 1)
                {
                    double mu = 0; for (int k = i; k < j; k++) mu += b[k]; mu /= L;
                    double c0 = 0.5 * (L - 1);
                    for (int k = 0; k < L; k++) b[i + k] = mu * Math.Pow(1 - Trennung, k - c0);
                }
                i = j;
            }
            for (int i = 1; i < n; i++) if (b[i] > b[i - 1] * (1 - 1e-3)) b[i] = b[i - 1] * (1 - 1e-3);   // Gruppen beruehren sich: nie gleich
            double s = 0;
            for (int i = 0; i < n; i++)
            {
                double c = 1;
                for (int j = 0; j < n; j++) if (j != i) c *= b[i] / (b[i] - b[j]);
                s += c * Math.Exp(-x / b[i]);
            }
            double h = 1 - s;
            return h < 0 ? 0 : (h > 1 ? 1 : h);
        }

        /// <summary>Teil 2: Raumkette und Energieteile einer Route bei 1 kHz fuer den fruehen Anteil (Sprache). Wird je Route nur abgelegt
        /// (billig) und erst beim Mischen fuer die wenigen beitragenden Routen ausgewertet (Frueh) - Raumwege-Benchmark.
        /// T0 = Quellraum, TN = Hoererraum, M0/M1 = Zwischenraeume (mehr als 2: die weiteren in M1 zusammengezaehlt - Mittel bleibt, die
        /// Kette wird im D50-Fenster etwas zu frueh, also eher zu laut; Annahme, Fehler bei 3 Zwischenraeumen siehe Test),
        /// G/DQ/DOM/SQ/SOM = Anteile der Routenenergie: gebeugt, Hallfeld aus Quellraum-Diffusfeld / aus Direktanteil, Abstrahlung ebenso.</summary>
        public struct KetteSprache
        {
            public float T0, TN, M0, M1;
            public byte MN;
            public bool Hat;
            public float DQ, DOM, SQ, SOM;
            public float G => Math.Max(0f, 1f - DQ - DOM - SQ - SOM);   // gebeugter Rest (Summe der Anteile = 1)

            /// <summary>Frueher Anteil (0..1): G + DQ H(0..n) + DOM H(1..n) + SQ H(0..n-1) + SOM H(1..n-1), H = exakte Ketten-CDF bei fenster.</summary>
            public double Frueh(double fenster)
            {
                if (!Hat) return 1.0;
                var t = fruehPuffer ?? (fruehPuffer = new double[8]);
                int k = 0;
                t[k++] = T0;
                if (MN > 0) t[k++] = M0; if (MN > 1) t[k++] = M1;
                t[k++] = TN;
                int n = k - 1;
                double f = G + DQ * KetteCdf(t, 0, n, fenster) + DOM * KetteCdf(t, 1, n, fenster)
                         + SQ * KetteCdf(t, 0, n - 1, fenster) + SOM * (n >= 2 ? KetteCdf(t, 1, n - 1, fenster) : 1.0);
                return f < 0 ? 0 : (f > 1 ? 1 : f);
            }
        }
        [ThreadStatic] static double[] fruehPuffer;

        /// <summary>Nuetzlicher Anteil r (0..1) der Energie fuer Sprache: 1 - E_l (1 - U_l) - E_s (1 - U_s), U = Frueh(.., fenster).</summary>
        public static double Nutz(in Teil langsam, in Teil schnell, double fenster, out double uL, out double uS)
        {
            uL = langsam.Leer ? 1.0 : Frueh(langsam.M1, langsam.M2, fenster);
            uS = schnell.Leer ? 1.0 : Frueh(schnell.M1, schnell.M2, fenster);
            double eL = langsam.Leer ? 0 : Math.Min(1, langsam.E), eS = schnell.Leer ? 0 : Math.Min(1, schnell.E);
            if (eL + eS > 1) { double s = eL + eS; eL /= s; eS /= s; }
            double r = 1 - eL * (1 - uL) - eS * (1 - uS);
            return r < 1e-6 ? 1e-6 : (r > 1 ? 1 : r);
        }

        /// <summary>Mehrdaempfung (dB, &gt;= 0) aus dem Ereignis-Faktor r.</summary>
        public static double Db(double r) => r > 0 && r < 1 ? -10.0 * Math.Log10(r) : 0.0;

        /// <summary>Zeitstruktur einer Route je Band: langsamer und schneller Teil (Anteile an der Routenenergie).</summary>
        public struct Baender
        {
            public Teil L0, L1, L2, S0, S1, S2;
            public bool Hat;
            // tunneldiffus Teil 2: frueher Anteil (D50-Fenster, 1 kHz) an der Energie der Route bzw. Mischung, exakt aus der Raumkette;
            // HatFrueh = false: unbekannt (alles nuetzlich)
            public float Frueh;
            public bool HatFrueh;
            public Teil Langsam(int b) => b <= 0 ? L0 : b == 1 ? L1 : L2;
            public Teil Schnell(int b) => b <= 0 ? S0 : b == 1 ? S1 : S2;
            public void Setzen(int b, Teil l, Teil s) { if (b <= 0) { L0 = l; S0 = s; } else if (b == 1) { L1 = l; S1 = s; } else { L2 = l; S2 = s; } Hat = true; }
        }
    }
}
