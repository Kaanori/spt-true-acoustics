using System;

namespace Materialklang
{
    // blechschuss (17.11.2026): Modell des Spiel-Tiefpasses und Fit dazu.
    //
    // Befund (gemessen): Der Tiefpass, den das Spiel je Klang setzt (Unity AudioLowPassFilter ueber AudioBuiltinEQFilter.ApplyLowPass,
    // Dekompilat Audio.Effects/AudioBuiltinEQFilter.cs:32-36), ist 4-polig (24 dB/Okt.), nicht 2-polig, wie WegeKern.MehrDb annimmt.
    // Messung: Metamessung 17.11. (Aufnahme tools/messung/aufnahmen/20261117-160126-meta-factory.wav), weisser 10-ms-Klick der Testquelle
    // am selben Platz P4, M13 (Mod aus: lp 14822 Hz, hp 120 Hz, vol -2,5 dB) gegen M16 (Mod an: lp 3407 Hz, hp 80 Hz, vol -15,0 dB),
    // Differenzspektrum in 1/6 Okt. 500 Hz-7 kHz (24 Baender > 10 dB ueber Grund): 4-Pol-Butterworth |H|^2 = 1/(1+(f/fc)^8) weicht
    // 0,94 dB RMS ab, Versatz -12,6 dB (Log -12,5 dB); 2-Pol-Butterworth (MehrDb) 4,16 dB RMS, z. B. 6,3 kHz gemessen -35,5 dB gegen
    // -25,2 dB (2-Pol) / -34,7 dB (4-Pol). Skript und Tabelle: review-20261110/blechschuss/filterform.py, filterform.png.
    // Die Guete Q = 1 des Spiels (Gunshots-Preset: resonanceCurve konstant 1, aus resources.assets gelesen, blechschuss/preset/) ergibt
    // in der Messung KEINE Ueberhoehung (2 x 2-Pol mit Q 1 passt mit 1,52 dB RMS schlechter als Butterworth).
    //
    // Folge: Der Fit (WegeKern.TiefpassFit, Stuetzen 250 Hz-4 kHz, 2-Pol-Modell) waehlt die Grenzfrequenz fuer einen Filter, den es
    // nicht gibt. Gespielt werden unterhalb fc fast keine, oberhalb fc 24 dB/Okt. - bei typischen Schuessen durch Tueren (Log 17.11.:
    // lp 4-6 kHz, Sollneigung 4 kHz nur ~1 dB) fehlen bei 8 kHz ~18 dB und bei 12,5 kHz ~33 dB mehr als die Bandkurve verlangt
    // (review-20261110/blechschuss/fitvergleich.txt). Oberhalb 4 kHz prueft der Fit nichts, deshalb fiel die Kante nie auf.
    //
    // Hier: dasselbe Fit-Verfahren (Lautheitsfehler, Gewicht Bark-Breite, Zwicker/Terhardt 1980) mit
    //  - waehlbarer Polzahl (4 = Spielfilter, 2 = eigener WandFilter, WandFilterKern.cs:10),
    //  - Stuetzen bis fOben (Standard 8 kHz). Oberhalb des obersten Bands f2 laeuft die Sollkurve mit der Steigung f1->f2 weiter, nie
    //    fallend (ANNAHME: Massengesetz/Beugung steigen oberhalb 4 kHz weiter, Fugen bleiben flach; eine fallende Daemmung ueber 4 kHz
    //    wird nicht erfunden). Eine Oktave ueber dem obersten Band - weiter nicht, weil dort keine Rechnung mehr steht.
    // Den Mangel selbst (24 dB/Okt. gegen ~6 dB/Okt. des Massengesetzes) behebt das nicht; das kann nur ein eigener Filter (Etappe 0).
    public static class SpielfilterKern
    {
        /// <summary>Polzahl des Spiel-Tiefpasses (gemessen 17.11.2026, s. oben).</summary>
        public const int PoleSpiel = 4;
        /// <summary>Polzahl des eigenen WandFilter (RBJ-Butterworth 2. Ordnung, WandFilterKern.cs).</summary>
        public const int PoleEigen = 2;
        /// <summary>Obere Grenze der Fit-Stuetzen (eine Oktave ueber dem 4-kHz-Band).</summary>
        public const double FObenStandard = 8000;

        /// <summary>Mehrdaempfung (dB) eines Butterworth-Tiefpasses mit 'pole' Polen bei f: 10 lg(1 + (f/fc)^(2 pole)).</summary>
        public static double MehrDb(double fc, double f, int pole)
        {
            double r = f / Math.Max(fc, 1.0);
            return 10.0 * Math.Log10(1.0 + Math.Pow(r, 2 * pole));
        }

        /// <summary>Mehrdaempfung bei f gegenueber dem Bezugsband fRef (dB, &gt;= 0).</summary>
        public static double MehrDbRelativ(double fc, double f, double fRef, int pole) => Math.Max(0.0, MehrDb(fc, f, pole) - MehrDb(fc, fRef, pole));

        /// <summary>Sollkurve der Bandrechnung (dB gegenueber fRef): Gerade in lg f durch (fRef,0), (f1,m1), (f2,m2); oberhalb f2 mit der
        /// Steigung f1->f2 weiter, aber nicht fallend.</summary>
        public static double Soll(double f, double m1, double m2, double fRef, double f1, double f2)
        {
            double o = Math.Log(f / fRef, 2), o1 = Math.Log(f1 / fRef, 2), o2 = Math.Log(f2 / fRef, 2);
            if (o <= o1) return m1 * o / o1;
            if (o <= o2) return m1 + (m2 - m1) * (o - o1) / (o2 - o1);
            return m2 + Math.Max(0.0, (m2 - m1) / (o2 - o1)) * (o - o2);
        }

        sealed class Stuetzen
        {
            public double FRef, F1, F2, FOben; public int Pole;
            public double[] F, W;   // Terzmitte, Gewicht (Bark-Breite)
        }
        static Stuetzen cache;   // unveraenderlich nach dem Anlegen -> Austausch der Referenz ist threadsicher

        static Stuetzen Holen(double fRef, double f1, double f2, double fOben, int pole)
        {
            var s = cache;
            if (s != null && s.FRef == fRef && s.F1 == f1 && s.F2 == f2 && s.FOben == fOben && s.Pole == pole) return s;
            if (!(fRef > 0 && f1 > fRef && f2 > f1 && fOben >= f2 && pole >= 1 && pole <= 8)) return null;
            int n = Math.Max(2, (int)Math.Round(3 * Math.Log(fOben / fRef, 2)));   // Terzen (250 Hz-8 kHz: 15)
            s = new Stuetzen { FRef = fRef, F1 = f1, F2 = f2, FOben = fOben, Pole = pole, F = new double[n], W = new double[n] };
            for (int j = 0; j < n; j++)
            {
                double ua = fRef * Math.Pow(fOben / fRef, (double)j / n), ub = fRef * Math.Pow(fOben / fRef, (double)(j + 1) / n);
                s.F[j] = Math.Sqrt(ua * ub);
                s.W[j] = WegeKern.Bark(ub) - WegeKern.Bark(ua);
            }
            cache = s;
            return s;
        }

        static double Db(double fc, double f, int pole)
        {
            double r = f / fc, q = r * r, p = 1;
            for (int i = 0; i < pole; i++) p *= q;   // r^(2 pole) ohne Math.Pow
            return 10.0 * Math.Log10(1.0 + p);
        }

        static double Fehler(Stuetzen s, double fc, double m1, double m2)
        {
            double r0 = Db(fc, s.FRef, s.Pole), e = 0;
            for (int j = 0; j < s.F.Length; j++)
            {
                double d = Db(fc, s.F[j], s.Pole) - r0 - Soll(s.F[j], m1, m2, s.FRef, s.F1, s.F2);
                e += s.W[j] * d * d;
            }
            return e;
        }

        /// <summary>
        /// Tiefpass-Fit fuer einen Butterworth-Filter mit 'pole' Polen: fc, dessen Abweichung von der Bandkurve (m1 bei f1, m2 bei f2,
        /// gegenueber fRef) ueber fRef..fOben im Lautheitsmass am kleinsten ist. m1/m2 &lt;= 0,05 dB -> offen (fMax). Ergebnis in [fMin, fMax].
        /// Mit pole = 2 und fOben = f2 dasselbe Verfahren wie WegeKern.TiefpassFit (feineres Grobraster, Abweichung &lt; 1 %). Keine Allokation ausser beim ersten Aufruf je Parametersatz.
        /// </summary>
        public static double TiefpassFit(double m1, double m2, double f1, double f2, double fMin, double fMax, double fRef, int pole, double fOben)
        {
            m1 = Math.Max(0, m1); m2 = Math.Max(0, m2);
            if (m1 <= 0.05 && m2 <= 0.05) return fMax;
            var s = Holen(fRef, f1, f2, Math.Max(fOben, f2), pole);
            if (s == null) return WegeKern.TiefpassFitZweiPunkte(m1, m2, f1, f2, fMin, fMax);
            double lo = Math.Log(fMin), hi = Math.Log(fMax);
            if (!(hi > lo)) return Math.Max(fMin, Math.Min(fMax, fMin));
            const int Grob = 24;
            int best = 0; double eb = double.MaxValue;
            for (int i = 0; i <= Grob; i++)
            {
                double e = Fehler(s, Math.Exp(lo + (hi - lo) * i / Grob), m1, m2);
                if (e < eb) { eb = e; best = i; }
            }
            double a = lo + (hi - lo) * Math.Max(0, best - 1) / Grob, b = lo + (hi - lo) * Math.Min(Grob, best + 1) / Grob;
            double g = (Math.Sqrt(5) - 1) / 2;
            double c = b - g * (b - a), dd = a + g * (b - a);
            double fc_ = Fehler(s, Math.Exp(c), m1, m2), fd = Fehler(s, Math.Exp(dd), m1, m2);
            for (int i = 0; i < 28; i++)
            {
                if (fc_ < fd) { b = dd; dd = c; fd = fc_; c = b - g * (b - a); fc_ = Fehler(s, Math.Exp(c), m1, m2); }
                else { a = c; c = dd; fc_ = fd; dd = a + g * (b - a); fd = Fehler(s, Math.Exp(dd), m1, m2); }
            }
            return Math.Max(fMin, Math.Min(fMax, Math.Exp((a + b) / 2)));
        }
    }
}
