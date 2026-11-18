using System;
using System.Collections.Generic;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // nachhallstimme (17.11.2026): der verschmierte (diffuse) Teil eines Raumwegs als ECHTER Nachhall statt als trockene Kopie.
    // Reine Rechnung ohne Unity (Offline-Test NachhallTest). Spiel-Teil: Zweitstimme.cs (Stimme, Vergabe), Nachhall.cs (Filter, Schalter).
    // Bericht: review-20261110/nachhallstimme-STAND.md.
    //
    // Grundsatz Kaan 17.11.: "Das, was mein Gehoer / Gehirn selbst aus den Klaengen macht, muss ja nicht schon simuliert werden, das macht
    // es ja selbst." Bisher spielte die Mod die Hallenergie aus dem Nachbarraum als trockene Kopie des Clips (klar, knackig, gleichzeitig)
    // und zog dafuer Wahrnehmungsregeln ab (LAFmax-Ereignis, D50 bei Sprache, ZeitKern). Hier wird stattdessen die Zeitstruktur selbst
    // gespielt; das Gehoer bewertet sie selbst. Die Abzuege entfallen fuer jeden Klang, den diese Stimme traegt (eine Stelle entscheidet:
    // Zweitstimme.NachhallWirkt, gelesen von Physik.Trocken).
    //
    // Physik (Quellen):
    //  - Energie-Impulsantwort einer Raumkette = Faltung der Exponentialkerne je Raumstufe, tau = 4V/(cA) (Kuttruff, Room Acoustics,
    //    5. Aufl., Kap. 5.1/5.7; ZeitKern). ZeitKern legt je Route und Band die Momente (Mittel M1, zweites Moment M2) des verschmierten
    //    Teils ab (langsam: aus dem Quellraum-Diffusfeld, schnell: aus dem Direktanteil in die erste Oeffnung). Die Stimme bildet diese
    //    Verteilung mit ZWEI Stufen gleichen Mittels und gleicher Varianz nach (dieselbe Naeherung wie ZeitKern.Faktor): eine lange Stufe a
    //    (Abklingen, T60 = 6 ln10 a) und eine kurze b (Anstieg). Zwei hintereinander geschaltete Hallnetze mit unabhaengigen
    //    Rausch-Impulsantworten falten im Erwartungswert ihre Energie-Huellkurven - genau die Zwei-Stufen-Kette.
    //  - Energie: Anteil R_b der verschmierten Energie am Raumweg je Band b (250 Hz / 1 kHz / 4 kHz) = Summe ueber die Routen
    //    W x 10^(-Db_b/10) x (E_langsam + E_schnell) / Summe W x 10^(-Db_b/10). Die Nachhall-Stimme traegt Raumanteil x R_0 (Bezugsband) der
    //    Gesamtenergie, das Hallnetz ist auf Energie 1 im Bezugsband geeicht (NachhallNetz, Test gegen die numerisch integrierte
    //    Impulsantwort). Klangfarbe: das Netz selbst gibt je Band Energie ~ T_b (Stufe ~ 1/(1-g^2) ~ T); das Tiefpass der Stimme traegt
    //    nur den Rest: m_b = -10 lg(R_b E_b / (R_0 E_0)) + 10 lg(T_b / T_0) (nie negativ - das Filter kann nicht anheben).
    //  - Einsatz: Laufzeit des Austrittswegs gegen den fruehesten Weg ((L - L_min) / c); die Zeit bis zum ersten Echo des Netzes
    //    (kuerzeste Leitung ~30 ms + Diffusoren) steht fuer die Anfangsluecke der Raumantwort (mittlere freie Weglaenge 4V/S / c, Factory-
    //    Hallen ~17 ms, tunneldiffus Teil 2) - Annahme: gleiche Groessenordnung, nicht je Raum gestellt.
    //  - Abklingzeit begrenzt (Regler "max. decay"): Befund tunneldiffus Teil 2 - die Mod rechnet die Hallen viel zu hallig (nur
    //    Huellflaeche, Area_2 T 7,1 s statt ~2,6 s mit Einbauten). Die Begrenzung aendert nur die Zeitstruktur, nicht die Energie.
    // Energiebilanz je Klang (Anteile der Gesamtenergie S = 1): Wand (1 - Raumanteil) + gerichteter Raumweg Raumanteil (1 - R) [Original +
    //  weitere Stimmen nach Buendeln] + Nachhall Raumanteil R = 1 (Teile, Test gegen Handrechnung).
    // Richtung: EINE Nachhall-Stimme je Klang an der Oeffnung mit der meisten verschmierten Energie (Kostenentscheidung: jede Stimme kostet
    //  eine Wiedergabequelle und ein Hallnetz auf dem Audiofaden; mehrweg-Befund: 2. Buendel im Median -9 dB; der Rest des Hallfelds kaeme
    //  physikalisch aus weiteren Oeffnungen - als Plan je Buendel).
    // ------------------------------------------------------------------------------------------
    internal sealed class NachhallWahl
    {
        public bool Hat;
        public double R0, R1, R2;            // verschmierter Anteil an der Raumweg-Energie je Band (0..1)
        public double V0, V1, V2;            // verschmierte Energie je Band (relativ, Summe W x 10^(-Db/10) x Anteil)
        public double T0, T1, T2;            // Abklingzeit T60 je Band (s), begrenzt
        public double T0Roh, T1Roh, T2Roh;   // ungedeckelt (Log)
        public double TauAuf;                // Anstiegsstufe (s, Band 1 kHz)
        public double EinsatzS;              // Laufzeit des Austritts gegen den fruehesten Weg (s)
        public V3 Aus;                       // Austritt der fuehrenden Oeffnung des verschmierten Teils
        public int Index;                    // Route (in der Mischung) dieses Austritts
        public double M1, M2;                // Tiefpass-Ziel: Mehrdaempfung 1 kHz / 4 kHz gegen 250 Hz, die das Filter noch tragen muss (dB, >= 0)
    }

    internal static class NachhallKern
    {
        /// <summary>T60 = 6 ln 10 x tau (Abfall um 60 dB bei exp(-t/tau)).</summary>
        public const double T60JeTau = 13.815510557964274;
        /// <summary>Energieanteil (an der Gesamtenergie des Klangs), unter dem keine Nachhall-Stimme spielt: -30 dB (Kostentor wie
        /// ZweitwegKern.TorDb - 5 dB unter der niedrigsten Hoerschwelle einer Rueckwurf-Kopie).</summary>
        public const double MinEnergie = 1e-3;
        public const double MinT60 = 0.05;

        static double E(RoutenEintrag e, int b) => e.W * Math.Pow(10, -e.Db[b] / 10);

        /// <summary>Verschmierter Anteil (0..1) der Routenenergie im Band b (ZeitKern: langsam + schnell; ohne Zeitstruktur 0).</summary>
        public static double Verschmiert(in RaumwegErgebnis erg, int b)
        {
            if (!erg.Zeit.Hat) return 0;
            double f = Math.Max(0, (double)erg.Zeit.Langsam(b).E) + Math.Max(0, (double)erg.Zeit.Schnell(b).E);
            return f > 1 ? 1 : f;
        }

        /// <summary>Zwei Stufen a &gt;= b mit a + b = m1 und a^2 + b^2 = m2 - m1^2 (Varianz). Wie ZeitKern.Faktor: Varianz ueber der eines
        /// Exponentials (nur bei Mischungen) -&gt; eine Stufe mit dem Mittel; Varianz unter der zweier gleicher Stufen -&gt; zwei gleiche.</summary>
        public static void ZweiStufen(double m1, double m2, out double a, out double b)
        {
            a = b = 0;
            if (!(m1 > 1e-9) || double.IsNaN(m2)) return;
            double var = Math.Max(0, m2 - m1 * m1);
            double disk = 2 * var - m1 * m1;
            if (disk >= 0)
            {
                double r = Math.Sqrt(disk);
                a = 0.5 * (m1 + r); b = 0.5 * (m1 - r);
                if (b < 1e-6 * m1) { a = m1; b = 0; }
            }
            else { a = b = 0.5 * m1; }
        }

        /// <summary>Nachhall-Stimme eines Klangs aus den gemischten Routen: Anteile je Band, Zeitstruktur, Austritt, Klangfarbe.
        /// maxT60 = Regler (s). false = kein verschmierter Teil (keine Route mit Zeitstruktur).</summary>
        public static bool Waehlen(List<RoutenEintrag> mix, double maxT60, NachhallWahl w)
        {
            w.Hat = false;
            if (mix == null || w == null || mix.Count == 0) return false;
            double s0 = 0, s1 = 0, s2 = 0, v0 = 0, v1 = 0, v2 = 0;
            double a10 = 0, a20 = 0, a11 = 0, a21 = 0, a12 = 0, a22 = 0;
            double best = 0, lmin = double.PositiveInfinity; int ib = -1;
            for (int i = 0; i < mix.Count; i++)
            {
                var e = mix[i];
                if (!(e.W > 0)) continue;
                double x0 = E(e, 0), x1 = E(e, 1), x2 = E(e, 2);
                s0 += x0; s1 += x1; s2 += x2;
                double l = e.Erg.Laenge;
                if (l > 0 && l < lmin) lmin = l;
                if (!e.Erg.Zeit.Hat) continue;
                Dazu(e.Erg.Zeit.Langsam(0), e.Erg.Zeit.Schnell(0), x0, ref v0, ref a10, ref a20);
                Dazu(e.Erg.Zeit.Langsam(1), e.Erg.Zeit.Schnell(1), x1, ref v1, ref a11, ref a21);
                Dazu(e.Erg.Zeit.Langsam(2), e.Erg.Zeit.Schnell(2), x2, ref v2, ref a12, ref a22);
                double f0 = x0 * Verschmiert(e.Erg, 0);
                if (f0 > best) { best = f0; ib = i; }
            }
            if (ib < 0 || !(v0 > 0) || !(s0 > 0)) return false;
            w.V0 = v0; w.V1 = v1; w.V2 = v2;
            w.R0 = Math.Min(1, v0 / s0); w.R1 = s1 > 0 ? Math.Min(1, v1 / s1) : 0; w.R2 = s2 > 0 ? Math.Min(1, v2 / s2) : 0;
            double b1 = 0;
            double t0 = Band(a10, a20, v0, out double b0), t1 = v1 > 0 ? Band(a11, a21, v1, out b1) : t0, t2 = v2 > 0 ? Band(a12, a22, v2, out _) : t1;
            if (!(v1 > 0)) b1 = b0;
            w.T0Roh = t0; w.T1Roh = t1; w.T2Roh = t2;
            double cap = maxT60 > MinT60 ? maxT60 : double.PositiveInfinity;
            w.T0 = Clamp(t0, cap); w.T1 = Clamp(t1, cap); w.T2 = Clamp(t2, cap);
            // Begrenzung staucht die ganze Zeitstruktur gleich (Anstieg mit): sonst stiege der Nachhall zweier zu hallig gerechneter Hallen
            // langsamer an, als er abklingt (Factory Area_1 -> Area_3 aus dem Klang-Log 20:29:45: tau 0,53 / 0,48 s)
            w.TauAuf = t1 > 0 ? b1 * (w.T1 / t1) : b1;
            var r = mix[ib];
            w.Index = ib; w.Aus = r.Austritt;
            w.EinsatzS = r.Erg.Laenge > 0 && lmin < double.PositiveInfinity ? Math.Max(0, r.Erg.Laenge - lmin) / RaumwegKern.Schall : 0;
            Farbe(w.V0, w.V1, w.V2, w.T0, w.T1, w.T2, out w.M1, out w.M2);
            w.Hat = true;
            return true;
        }

        static void Dazu(in ZeitKern.Teil l, in ZeitKern.Teil s, double x, ref double v, ref double m1, ref double m2)
        {
            double el = Math.Max(0, (double)l.E), es = Math.Max(0, (double)s.E);
            double f = el + es; if (f > 1) { el /= f; es /= f; f = 1; }
            if (!(f > 0) || !(x > 0)) return;
            v += x * f;
            m1 += x * (el * l.M1 + es * s.M1);
            m2 += x * (el * l.M2 + es * s.M2);
        }

        /// <summary>T60 (s) der langen Stufe aus den gewichteten Momenten eines Bands; b = Anstiegsstufe (s).</summary>
        static double Band(double sm1, double sm2, double v, out double b)
        {
            ZweiStufen(sm1 / v, sm2 / v, out double a, out b);
            return a * T60JeTau;
        }

        static double Clamp(double t, double cap) => !(t > MinT60) ? MinT60 : t > cap ? cap : t;

        /// <summary>Tiefpass-Ziel der Stimme (dB gegen das Bezugsband, &gt;= 0): Farbe der verschmierten Energie minus der Farbe, die das
        /// Netz mit T_b selbst erzeugt (Energie je Band ~ T_b).</summary>
        public static void Farbe(double v0, double v1, double v2, double t0, double t1, double t2, out double m1, out double m2)
        {
            m1 = m2 = 0;
            if (!(v0 > 0) || !(t0 > 0)) return;
            double r1 = v1 > 0 ? -10 * Math.Log10(v1 / v0) : 60, r2 = v2 > 0 ? -10 * Math.Log10(v2 / v0) : 60;
            double n1 = t1 > 0 ? -10 * Math.Log10(t1 / t0) : 0, n2 = t2 > 0 ? -10 * Math.Log10(t2 / t0) : 0;
            m1 = Math.Max(0, r1 - n1); m2 = Math.Max(0, r2 - n2);
        }

        /// <summary>Energieanteile (an der Gesamtenergie 1): anteil = Raumweg (Rest = Wand), r = verschmierter Anteil des Raumwegs,
        /// hall = Nachhall-Stimme spielt, sSlots = Summe der Anteile der weiteren Stimmen (bei hall: am GERICHTETEN Raumweg, sonst am ganzen).
        /// jeSlot = Faktor, mit dem ein Slot-Anteil in Gesamtenergie umgerechnet wird.</summary>
        public static void Teile(double anteil, double r, bool hall, double sSlots, out double erste, out double nachhall, out double jeSlot)
        {
            anteil = anteil > 0 ? (anteil < 1 ? anteil : 1) : 0;
            r = r > 0 ? (r < 1 ? r : 1) : 0;
            nachhall = hall ? anteil * r : 0;
            jeSlot = hall ? anteil * (1 - r) : anteil;
            erste = Math.Max(0, 1 - nachhall - jeSlot * Math.Max(0, sSlots));
        }

        /// <summary>Energie der ersten Stimme im Band b (Anteil an der Bandsumme): 1 - anteil (r_b + (1 - r_b) w_b) mit hall, sonst
        /// 1 - anteil w_b; w_b = Summe der Slot-Anteile in diesem Band (am gerichteten bzw. ganzen Raumweg).</summary>
        public static double ErsteBand(double anteil, double rb, bool hall, double wb)
        {
            double x = hall ? rb + (1 - rb) * wb : wb;
            return 1 - anteil * x;
        }

        /// <summary>Wie lange die Stimme nach dem Ende der Anregung noch laeuft (s): Abfall um 60 dB der laengsten Band-Abklingzeit plus
        /// Anstieg (5 tau: Rest e^-5) - danach ist der Nachhall unter -60 dB.</summary>
        public static double Schwanz(NachhallWahl w) => Math.Max(w.T0, Math.Max(w.T1, w.T2)) + 5 * Math.Max(0, w.TauAuf);

        /// <summary>Faktor, um den die Lautstaerke des Originals angehoben wird, wenn sein Ereignis-/Sprachabzug (gespielte dB) zurueckgenommen
        /// wird - die Nachhall-Stimme traegt den spaeten Teil selbst.</summary>
        public static double Zuruecknehmen(double abzugDb) => abzugDb > 0 && abzugDb < 120 ? Math.Pow(10, abzugDb / 20.0) : 1.0;
    }

    // ------------------------------------------------------------------------------------------
    // Hallnetz (Feedback-Delay-Netz, Jot & Chaigne 1991, AES Preprint 3030; Schroeder 1962 Allpass-Diffusoren). Laeuft im Audiofaden
    // (OnAudioFilterRead), keine Speicheranforderung nach dem Anlegen. Kette: Vorverzoegerung (Einsatz) -> 4 Allpaesse (Echodichte) ->
    // Vorstufe (8 Leitungen, Hadamard, T60 = 13,8 tau_Anstieg, frequenzunabhaengig) -> Hauptnetz (16 Leitungen, Hadamard-Rueckkopplung,
    // je Leitung Tiefpass 1. Ordnung, der die Abklingzeit bei 250 Hz und 4 kHz exakt trifft). NUR NASS: der Ausgang ersetzt das
    // Eingangssignal ganz (das trockene Signal traegt das Original). Eingang = Mittel der Kanaele, Ausgang auf alle Kanaele (mono -> der
    // Spatializer setzt die Richtung, wenn das Filter VOR ihm laeuft: spatializePostEffects = true).
    //
    // hallglocke (17.11.2026, Befund Kaan 21:16 "Glockenlaeuten/Schwingen", Bericht review-20261110/hallglocke-STAND.md): Die alte
    // Vorstufe hatte 4 Leitungen von 3-9 ms (Summe 22,8 ms), klang aber mit T60 = 13,8 tau = 1,4-4 s nach (Anstieg 0,1-0,3 s, Klang-Log).
    // Ein Netz hat so viele Moden je Hz, wie seine Leitungen zusammen lang sind (Sekunden) - 0,023 Moden/Hz = eine Mode alle 44 Hz, jede
    // ~2-4 s lang: ein Glockenspiel (Linien 25-40 dB ueber dem Umfeld, in der Raid-Aufnahme an genau diesen Frequenzen nachgewiesen).
    // Anforderung fuer einen faerbungsfreien Nachhall: Modendichte >= 0,15 T60 Moden/Hz (Schroeder 1962; Jot & Chaigne 1991: Summe der
    // Leitungslaengen >= 0,15 T60), Echodichte >= 1000/s, fuer Impulse ~10000/s (Smith & Lee, CCRMA 2007, "Artificial Reverberation").
    // Darum: (1) Vorstufe mit 8 Leitungen, deren Laengen mit der Anstiegszeit mitwachsen (Summe = VorDichte x T60_Vorstufe) - so ist die
    // Vorstufe fuer jedes tau gleich dicht und ihre Form skaliert nur (eine Eichkonstante statt Tabelle). Laengen wechseln nur, wenn das
    // Netz leer ist (Schlaf oder neue Stimme) - nie mit Signal in den Leitungen (Knackfrei); dazwischen folgen nur die Verstaerkungen.
    // (2) Hauptnetz 16 statt 8 Leitungen, 19-89 ms, Summe ~0,74 s (>= 0,15 x 4,9 s; das alte hatte 0,36 s). (3) Je Netz (= je Stimme)
    // andere, teilerfremde Leitungslaengen (Variante): 8 gleichzeitige Stimmen mit denselben Moden addierten sich sonst zu denselben
    // Spitzen. (4) Zeitvariante Rueckkopplung (RotHzMittel): zwei dichte Stufen hintereinander multiplizieren ihre Spektren - ohne
    // Drehung blieben Restspitzen 12-15 dB statt ~9-10 dB bei Rauschen (Offline-Messung tools/hallglocke). Gedreht wird die Matrix, nicht
    // die Leitungslaenge: keine Tonhoehenschwankung des Halls.
    // Abtastschleife: Methode Lauf, ERZEUGT von tools/hallglocke/erzeuge_lauf.py (ausgerollt, ~2x Rechenzeit des alten Netzes).
    //
    // Eichung: Energie der Impulsantwort im Bezugsband = 1. Ansatz: orthogonale Rueckkopplung erhaelt die Energie, je Durchlauf geht
    // (1 - g_i^2) verloren; bei gleichmaessig verteilter, unkorrelierter Energie in den Leitungen ist die Ausgangsenergie mit
    // c_i = 1/sqrt(N) gleich 1 / Summe(1 - g_i^2) -> Ausgang x sqrt(Summe(1 - g_i^2)). Gegen die numerisch integrierte Impulsantwort
    // geprueft (NachhallTest); Restfehler als Tabelle Korrektur (Hauptnetz) bzw. Konstante KorrekturVorDb (Vorstufe, skalenfrei).
    // ------------------------------------------------------------------------------------------
    internal sealed class NachhallNetz
    {
        public const int N = 16, NV = 8, NA = 4;
        public const double MaxVorS = 1.0;
        public const double FBezug = 250.0, FHoch = 4000.0;
        /// <summary>Stuetzstelle des hohen Bands fuer den Leitungs-Tiefpass: untere Terzkante von 4 kHz (4000 x 2^-1/6). Das Abklingen eines
        /// Bands wird von seinem langsamsten Teil bestimmt (Schroeder-Integral, ISO 3382-1); mit der Stuetze in der Bandmitte klang das
        /// 4-kHz-Terzband 16 % zu lang (NachhallTest, erster Lauf 17.11.: 1,74 statt 1,5 s).</summary>
        public const double FHochStuetz = 3563.6;
        /// <summary>Summe der Leitungslaengen der Vorstufe je Sekunde ihrer T60 (Soll &gt;= 0,15, Jot &amp; Chaigne; Reserve 1/3).</summary>
        public const double VorDichte = 0.2;
        /// <summary>Groesste Anstiegszeit, fuer die die Vorstufe Platz hat (s); darueber bleiben die Leitungen so lang (Dichte sinkt).</summary>
        public const double MaxTauVor = 0.5;
        /// <summary>Kuerzeste Leitung der Vorstufe (Abtastwerte).</summary>
        const int MinVorLaenge = 11;
        // Hauptnetz: Grundlaengen (ms) geometrisch 19 ... 89 ms (Summe ~0,74 s). Je Variante +-7 % gestreut, auf Primzahlen gerundet.
        static readonly double[] LinieMs = { 19.0, 21.06, 23.34, 25.87, 28.68, 31.79, 35.23, 39.05, 43.29, 47.98, 53.18, 58.95, 65.34, 72.42, 80.27, 88.97 };
        // Vorstufe: Anteile an der mittleren Leitungslaenge M = VorDichte x T60_v / NV, geometrisch 0,55 ... 1,55
        static readonly double[] VorAnteil = { 0.55, 0.638, 0.739, 0.857, 0.994, 1.153, 1.336, 1.55 };
        static readonly double[] AllMs = { 4.3, 3.1, 7.7, 10.1 };
        const double Streuung = 0.07;
        const float AllG = 0.6f;
        // Vorzeichen der Ein-/Auskopplung stehen als Literale in Lauf (erzeuge_lauf.py: SB/SC Hauptnetz, VB/VC Vorstufe; Betrag 1/sqrt(N)).
        /// <summary>Zeitvariante Rueckkopplung (Schlecht &amp; Habets 2015, JASA 138(3) "Time-varying feedback matrices in feedback delay
        /// networks and their application in artificial reverberation"): nach der Hadamard-Matrix drehen die Leitungspaare (j, j+N/2) langsam
        /// und stetig (Givens-Drehung, im Mittel RotHz Umdrehungen je Sekunde, je Paar 0,5-1,5 x, wechselnde Richtung). Die Matrix bleibt
        /// orthogonal -&gt; Energie und Abklingzeit unveraendert; die Moden wandern um ~1 Hz und verschmieren, statt als feste Linien zu
        /// stehen (Ersatz fuer die Dichte eines echten Raums: Ausklang-Spitzen 13-15 dB -&gt; 10-11 dB, Rauschen 9-10 dB; hallglocke).
        /// Keine Leitungsmodulation (die waere eine Tonhoehenschwankung). 0 = aus (nur Test/Vergleich).</summary>
        public static double RotHzMittel = 1.0, RotHzVor = 1.0;
        /// <summary>Drehung wird alle RotSchritt Abtastwerte nachgefuehrt (1 Hz: 0,002 rad je Schritt).</summary>
        const int RotSchritt = 16;
        static int zaehler;

        public readonly int Fs, Variante;
        readonly int[] ln = new int[N], lOff = new int[N], lPos = new int[N];
        readonly float[] mem;
        readonly float[] k = new float[N], q = new float[N], z = new float[N];
        readonly int[] vln = new int[NV], vOff = new int[NV], vPos = new int[NV];
        readonly float[] vmem; readonly int vKap;
        readonly float[] vk = new float[NV];
        readonly double[] vAnteil = new double[NV];
        // Drehung: je Paar cos/sin des Winkels und die Drehung je RotSchritt (Rekursion, nachnormiert)
        readonly float[] rc = new float[N / 2], rs = new float[N / 2], dc = new float[N / 2], ds = new float[N / 2];
        readonly float[] vrc = new float[NV / 2], vrs = new float[NV / 2], vdc = new float[NV / 2], vds = new float[NV / 2];
        int rotZ;
        readonly float[][] ap = new float[NA][]; readonly int[] apn = new int[NA], app = new int[NA];
        readonly float[] vor; int vorPos, vorN;
        float norm = 1f, vnorm = 1f, pegel = 1f; bool vorstufe, schlaeft = true;
        double vTau, vTauLaenge = -1;   // Anstieg (s) jetzt / fuer den die Leitungslaengen der Vorstufe gesetzt sind
        long stille, schwanz;

        // Uebergabe Hauptfaden -> Audiofaden (Sperre nur fuer das Kopieren der Werte; einmal je Ereignis bzw. Aenderung)
        readonly object sperre = new object();
        double qT0 = 1, qT2 = 1, qTau, qVor, qPegel = 1; int qGen, gen; volatile bool qNeu;
        /// <summary>Zahl der verarbeiteten Bloecke (Audiofaden schreibt, Hauptfaden liest - Selbsttest).</summary>
        public long Bloecke;
        /// <summary>Kanalzahl des letzten Blocks (Diagnose: was bekommt das Filter wirklich).</summary>
        public int Kanaele;
        /// <summary>Summe der Leitungslaengen (s) = Modendichte (Moden je Hz) von Hauptnetz bzw. Vorstufe (Test/Diagnose).</summary>
        public double SummeHaupt { get { long s = 0; for (int i = 0; i < N; i++) s += ln[i]; return (double)s / Fs; } }
        public double SummeVor { get { long s = 0; for (int i = 0; i < NV; i++) s += vln[i]; return (double)s / Fs; } }
        public int Laenge(int i) => ln[i];
        public int LaengeVor(int i) => vln[i];

        /// <summary>Neues Netz; jede Instanz bekommt eine eigene Variante (andere Leitungslaengen -&gt; andere Moden je Stimme).</summary>
        public NachhallNetz(int fs) : this(fs, System.Threading.Interlocked.Increment(ref zaehler) - 1) { }

        public NachhallNetz(int fs, int variante)
        {
            Fs = fs > 1000 ? fs : 48000;
            Variante = variante;
            uint zuf = 2166136261u ^ (uint)(variante * 16777619 + 7);
            double Zufall() { zuf ^= zuf << 13; zuf ^= zuf >> 17; zuf ^= zuf << 5; return (zuf & 0xFFFFFF) / (double)0xFFFFFF * 2 - 1; }
            int summe = 0;
            for (int i = 0; i < N; i++)
            {
                int soll = (int)Math.Round(LinieMs[i] * (1 + Streuung * Zufall()) * 1e-3 * Fs);
                ln[i] = Prim(soll, ln, i); lOff[i] = summe; lPos[i] = summe; summe += ln[i];
            }
            mem = new float[summe];
            vKap = (int)Math.Ceiling(VorDichte * NachhallKern.T60JeTau * CTau * MaxTauVor / NV * 1.55 * (1 + Streuung) * Fs) + 64;
            vmem = new float[vKap * NV];
            for (int i = 0; i < NV; i++)
            {
                vAnteil[i] = VorAnteil[i] * (1 + Streuung * Zufall());
                vOff[i] = i * vKap; vln[i] = MinVorLaenge + i; vPos[i] = vOff[i];
            }
            for (int i = 0; i < NA; i++) { apn[i] = Math.Max(2, Prim((int)Math.Round(AllMs[i] * (1 + Streuung * Zufall()) * 1e-3 * Fs), apn, i)); ap[i] = new float[apn[i]]; }
            vor = new float[(int)(MaxVorS * Fs) + 2];
            // Drehzahlen je Paar 0,5 ... 1,5 x Mittel, wechselnde Richtung, zufaelliger Anfangswinkel
            for (int j = 0; j < N / 2; j++) Dreh(RotHzMittel * (1 + 0.5 * Zufall()) * (j % 2 == 0 ? 1 : -1), Math.PI * Zufall(), rc, rs, dc, ds, j);
            for (int j = 0; j < NV / 2; j++) Dreh(RotHzVor * (1 + 0.5 * Zufall()) * (j % 2 == 0 ? -1 : 1), Math.PI * Zufall(), vrc, vrs, vdc, vds, j);
            Uebernehmen(1, 1, 0, 0, 1);
        }

        void Dreh(double hz, double phi, float[] c, float[] s, float[] dcos, float[] dsin, int j)
        {
            c[j] = (float)Math.Cos(phi); s[j] = (float)Math.Sin(phi);
            double w = 2 * Math.PI * hz * RotSchritt / Fs; dcos[j] = (float)Math.Cos(w); dsin[j] = (float)Math.Sin(w);
        }

        /// <summary>Alle Drehwinkel einen Schritt (RotSchritt Abtastwerte) weiter; |(cos, sin)| = 1 nachnormiert.</summary>
        void Drehen()
        {
            for (int j = 0; j < N / 2; j++)
            {
                float c = rc[j], s = rs[j], c2 = c * dc[j] - s * ds[j], s2 = s * dc[j] + c * ds[j];
                float m = 1.5f - 0.5f * (c2 * c2 + s2 * s2);   // Newton-Schritt fuer 1/sqrt nahe 1
                rc[j] = c2 * m; rs[j] = s2 * m;
            }
            for (int j = 0; j < NV / 2; j++)
            {
                float c = vrc[j], s = vrs[j], c2 = c * vdc[j] - s * vds[j], s2 = s * vdc[j] + c * vds[j];
                float m = 1.5f - 0.5f * (c2 * c2 + s2 * s2);
                vrc[j] = c2 * m; vrs[j] = s2 * m;
            }
        }

        /// <summary>Naechste Primzahl &gt;= n, die unter den ersten 'bis' Eintraegen von 'schon' nicht vorkommt (teilerfremde Leitungen).</summary>
        static int Prim(int n, int[] schon, int bis)
        {
            if (n < 2) n = 2;
            for (int c = n; ; c++)
            {
                bool p = c >= 2;
                for (int d = 2; p && d * d <= c; d++) if (c % d == 0) p = false;
                if (!p) continue;
                bool frei = true; for (int j = 0; j < bis; j++) if (schon[j] == c) frei = false;
                if (frei) return c;
            }
        }

        /// <summary>Hauptfaden: neue Werte (T60 bei 250 Hz / 4 kHz, Anstieg tau (s), Vorverzoegerung (s), Pegel linear). leeren = Zustand
        /// loeschen (neue Stimme, kein Rest eines frueheren Besitzers).</summary>
        public void Setzen(double t0, double t2, double tauAuf, double vorS, double pegelLinear, bool leeren)
        {
            lock (sperre)
            {
                qT0 = t0; qT2 = t2; qTau = tauAuf; qVor = vorS; qPegel = pegelLinear;
                if (leeren) qGen++;
                qNeu = true;
            }
        }

        void Uebernehmen(double t0, double t2, double tau, double vorS, double peg)
        {
            if (!(t0 > NachhallKern.MinT60)) t0 = NachhallKern.MinT60;
            if (!(t2 > NachhallKern.MinT60)) t2 = NachhallKern.MinT60;
            double c0 = Math.Cos(2 * Math.PI * FBezug / Fs), c2 = Math.Cos(2 * Math.PI * Math.Min(FHochStuetz, 0.45 * Fs) / Fs);
            double sv = 0;
            for (int i = 0; i < N; i++)
            {
                double g0 = Math.Pow(10, -3.0 * ln[i] / (Fs * t0)), g2 = Math.Pow(10, -3.0 * ln[i] / (Fs * t2));
                double pp = Pol(g0, g2, c0, c2);
                // |H(w0)| = g0 exakt: k = g0 sqrt(1 - 2 p c0 + p^2) / (1 - p)
                double kk = g0 * Math.Sqrt(1 - 2 * pp * c0 + pp * pp) / (1 - pp);
                if (kk > 0.99999) kk = 0.99999;
                q[i] = (float)(1 - pp); k[i] = (float)kk;
                sv += 1 - g0 * g0;
            }
            norm = (float)(Math.Sqrt(Math.Max(1e-12, sv)) * Korrektur(t0));
            bool vorher = vorstufe;
            vorstufe = tau > 0.002;
            vTau = tau;
            // Vorstufe wird bei laufendem Netz zugeschaltet: alten (eingefrorenen) Inhalt ihrer Leitungen nicht wieder abspielen
            if (vorstufe && !vorher && !schlaeft) VorLeeren();
            if (vorstufe) { if (schlaeft || vTauLaenge < 0) VorLaengen(tau); VorGewichte(); }
            vorN = Math.Max(0, Math.Min(vor.Length - 2, (int)Math.Round(vorS * Fs)));
            pegel = (float)(peg > 0 ? peg : 0);
            schwanz = (long)((Math.Max(t0, t2) * 1.2 + 6 * Math.Max(0, tau) + vorS + 0.25) * Fs);
        }

        /// <summary>Leitungslaengen der Vorstufe fuer Anstieg tau: Summe = VorDichte x T60_v (nur bei leerem Netz aufrufen - nie mit
        /// Signal in den Leitungen).</summary>
        void VorLaengen(double tau)
        {
            double t = Math.Min(MaxTauVor, tau);
            double m = VorDichte * NachhallKern.T60JeTau * CTau * t / NV * Fs;
            for (int i = 0; i < NV; i++)
            {
                int n = Prim(Math.Max(MinVorLaenge, (int)Math.Round(vAnteil[i] * m)), vln, i);
                vln[i] = Math.Min(vKap, n); vPos[i] = vOff[i];
            }
            vTauLaenge = tau;
        }

        void VorGewichte()
        {
            double tv = NachhallKern.T60JeTau * CTau * vTau, s2 = 0;
            for (int i = 0; i < NV; i++) { double g = Math.Pow(10, -3.0 * vln[i] / (Fs * tv)); vk[i] = (float)g; s2 += 1 - g * g; }
            vnorm = (float)(Math.Sqrt(Math.Max(1e-12, s2)) * Math.Pow(10, -KorrekturVorDb / 20.0));
        }

        void VorLeeren()
        {
            for (int i = 0; i < NV; i++) { Array.Clear(vmem, vOff[i], vKap); vPos[i] = vOff[i]; }
        }

        /// <summary>Pol p des Tiefpasses 1. Ordnung H = k (1-p)/(1 - p z^-1), so dass |H(w2)|/|H(w0)| = g2/g0 (Loesung der quadratischen
        /// Gleichung, kleinere Wurzel in [0,1)). g2 &gt;= g0: 0 (kein Tiefpass).</summary>
        public static double Pol(double g0, double g2, double c0, double c2)
        {
            double r = g2 / g0; r *= r;
            if (!(r < 1)) return 0;
            double a = 1 - r, b = c0 - r * c2;
            double d = b * b - a * a;
            if (d < 0) d = 0;
            double pp = (b - Math.Sqrt(d)) / a;
            return pp < 0 ? 0 : (pp > 0.999 ? 0.999 : pp);
        }

        // Eichung (hallglocke, 17.11.2026, Messung offline: NachhallTest mit NACHHALL_EICHTABELLE=1, Mittel ueber 8 Varianten).
        //  - Hauptnetz: Restfehler der Energie (dB der Impulsantwort ohne Korrektur) je T60 - mit 16 Leitungen und Drehung fast null (das alte
        //    8er-Netz lag bis -0,8 dB daneben: Energie in den Leitungen ungleich verteilt); je Variante +-0,22 dB.
        //  - Vorstufe: Energie-Zuwachs der Kette (dB) - wegen der mitwachsenden Leitungen fuer jedes tau dieselbe Form -> eine Konstante.
        //  - CTau: Abklingzeit der Vorstufe je Anstiegszeit, so dass die mittlere Laufzeit der Energie um genau tau steigt (die erste Leitung
        //    kommt erst nach ~0,19 tau, das Netz braucht einen Umlauf bis zur vollen Dichte).
        static readonly double[] EichT = { 0.05, 0.1, 0.2, 0.3, 0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8, 12 };
        static readonly double[] EichDb = { 0.000, 0.000, -0.006, -0.012, -0.015, -0.012, -0.008, -0.002, 0.000, 0.002, 0.002, 0.000, -0.001, -0.001 };
        public const double KorrekturVorDb = 0.0;   // gemessen +0,03 dB im Mittel (tau 0,02-0,5 s); je Variante -0,6 ... +0,5 dB (eine Impulsantwort je Variante)
        public const double CTau = 0.866;   // gemessen: mittlere Laufzeit +1,155 tau mit CTau 1 (tau 0,02-0,5 s, Mittel 8 Varianten, T 1 und 4 s)

        /// <summary>Amplitudenfaktor, der den gemessenen Restfehler der Eichung ausgleicht (Tabelle oben, linear in lg T; ausserhalb Randwert).</summary>
        public static double Korrektur(double t60) => Math.Pow(10, -Tabelle(EichT, EichDb, t60) / 20.0);

        static double Tabelle(double[] x, double[] y, double v)
        {
            if (!(v > x[0])) return y[0];
            if (v >= x[x.Length - 1]) return y[y.Length - 1];
            int i = 1; while (x[i] < v) i++;
            double f = Math.Log(v / x[i - 1]) / Math.Log(x[i] / x[i - 1]);
            return y[i - 1] + f * (y[i] - y[i - 1]);
        }

        public void Leeren()
        {
            Array.Clear(mem, 0, mem.Length);
            for (int i = 0; i < N; i++) { z[i] = 0; lPos[i] = lOff[i]; }
            for (int i = 0; i < NV; i++) { Array.Clear(vmem, vOff[i], Math.Min(vKap, vln[i] + 1)); vPos[i] = vOff[i]; }
            for (int i = 0; i < NA; i++) { Array.Clear(ap[i], 0, ap[i].Length); app[i] = 0; }
            Array.Clear(vor, 0, vor.Length); vorPos = 0;
            stille = 0;
        }

        /// <summary>Audiofaden: Block verarbeiten (interleaved, kanaele). Ersetzt das Signal durch den Nachhall (nur nass).</summary>
        public void Verarbeiten(float[] data, int kanaele)
        {
            if (data == null || kanaele <= 0) return;
            Kanaele = kanaele;
            if (qNeu)
            {
                double t0, t2, tau, vs, pg; int g;
                lock (sperre) { t0 = qT0; t2 = qT2; tau = qTau; vs = qVor; pg = qPegel; g = qGen; qNeu = false; }
                if (g != gen) { gen = g; Leeren(); schlaeft = true; }
                Uebernehmen(t0, t2, tau, vs, pg);
            }
            Bloecke++;
            int n = data.Length / kanaele, i = 0;
            while (i < n)
            {
                if (schlaeft)
                {
                    // Stille vor dem Einsatz bzw. nach dem Schwanz: exakt 0, keine Rechnung
                    for (; i < n; i++)
                    {
                        int o = i * kanaele; bool still = true;
                        for (int c = 0; c < kanaele; c++) if (data[o + c] != 0f) { still = false; break; }
                        if (!still) break;
                    }
                    if (i >= n) break;
                    schlaeft = false; stille = 0;
                    // Netz ist leer: jetzt duerfen die Leitungen der Vorstufe auf den aktuellen Anstieg wechseln
                    if (vorstufe && vTauLaenge != vTau) { VorLaengen(vTau); VorGewichte(); }
                }
                i = Lauf(data, kanaele, i, n);
            }
        }

        /// <summary>Audiofaden: Abtastwerte von..bis-1 rechnen (Netz wach). Endet der Schwanz, wird geleert und der Index danach
        /// zurueckgegeben. ERZEUGT von tools/hallglocke/erzeuge_lauf.py (16 + 8 Leitungen ausgerollt) - dort aendern.</summary>
        int Lauf(float[] data, int kanaele, int von, int bis)
        {
            float inv = 1f / kanaele;
            float[] m = mem, vm = vmem, vorB = vor; int vLen = vorB.Length, vp = vorPos, vn = vorN;
            float[] ap0 = ap[0], ap1 = ap[1], ap2 = ap[2], ap3 = ap[3]; int an0 = apn[0], an1 = apn[1], an2 = apn[2], an3 = apn[3], aq0 = app[0], aq1 = app[1], aq2 = app[2], aq3 = app[3];
            const float G = AllG;
            int p0 = lPos[0], o0 = lOff[0], e0 = lOff[0] + ln[0]; float z0 = z[0], q0 = q[0], k0 = k[0];
            int p1 = lPos[1], o1 = lOff[1], e1 = lOff[1] + ln[1]; float z1 = z[1], q1 = q[1], k1 = k[1];
            int p2 = lPos[2], o2 = lOff[2], e2 = lOff[2] + ln[2]; float z2 = z[2], q2 = q[2], k2 = k[2];
            int p3 = lPos[3], o3 = lOff[3], e3 = lOff[3] + ln[3]; float z3 = z[3], q3 = q[3], k3 = k[3];
            int p4 = lPos[4], o4 = lOff[4], e4 = lOff[4] + ln[4]; float z4 = z[4], q4 = q[4], k4 = k[4];
            int p5 = lPos[5], o5 = lOff[5], e5 = lOff[5] + ln[5]; float z5 = z[5], q5 = q[5], k5 = k[5];
            int p6 = lPos[6], o6 = lOff[6], e6 = lOff[6] + ln[6]; float z6 = z[6], q6 = q[6], k6 = k[6];
            int p7 = lPos[7], o7 = lOff[7], e7 = lOff[7] + ln[7]; float z7 = z[7], q7 = q[7], k7 = k[7];
            int p8 = lPos[8], o8 = lOff[8], e8 = lOff[8] + ln[8]; float z8 = z[8], q8 = q[8], k8 = k[8];
            int p9 = lPos[9], o9 = lOff[9], e9 = lOff[9] + ln[9]; float z9 = z[9], q9 = q[9], k9 = k[9];
            int p10 = lPos[10], o10 = lOff[10], e10 = lOff[10] + ln[10]; float z10 = z[10], q10 = q[10], k10 = k[10];
            int p11 = lPos[11], o11 = lOff[11], e11 = lOff[11] + ln[11]; float z11 = z[11], q11 = q[11], k11 = k[11];
            int p12 = lPos[12], o12 = lOff[12], e12 = lOff[12] + ln[12]; float z12 = z[12], q12 = q[12], k12 = k[12];
            int p13 = lPos[13], o13 = lOff[13], e13 = lOff[13] + ln[13]; float z13 = z[13], q13 = q[13], k13 = k[13];
            int p14 = lPos[14], o14 = lOff[14], e14 = lOff[14] + ln[14]; float z14 = z[14], q14 = q[14], k14 = k[14];
            int p15 = lPos[15], o15 = lOff[15], e15 = lOff[15] + ln[15]; float z15 = z[15], q15 = q[15], k15 = k[15];
            int vp0 = vPos[0], vo0 = vOff[0], ve0 = vOff[0] + vln[0]; float vk0 = vk[0];
            int vp1 = vPos[1], vo1 = vOff[1], ve1 = vOff[1] + vln[1]; float vk1 = vk[1];
            int vp2 = vPos[2], vo2 = vOff[2], ve2 = vOff[2] + vln[2]; float vk2 = vk[2];
            int vp3 = vPos[3], vo3 = vOff[3], ve3 = vOff[3] + vln[3]; float vk3 = vk[3];
            int vp4 = vPos[4], vo4 = vOff[4], ve4 = vOff[4] + vln[4]; float vk4 = vk[4];
            int vp5 = vPos[5], vo5 = vOff[5], ve5 = vOff[5] + vln[5]; float vk5 = vk[5];
            int vp6 = vPos[6], vo6 = vOff[6], ve6 = vOff[6] + vln[6]; float vk6 = vk[6];
            int vp7 = vPos[7], vo7 = vOff[7], ve7 = vOff[7] + vln[7]; float vk7 = vk[7];
            bool mitVor = vorstufe; float vnm = vnorm * 0.35355339f, out0 = norm * pegel * 0.25f; long st = stille, sw = schwanz; int rz = rotZ;
            float rc0 = rc[0] * 0.25f, rs0 = rs[0] * 0.25f, rc1 = rc[1] * 0.25f, rs1 = rs[1] * 0.25f, rc2 = rc[2] * 0.25f, rs2 = rs[2] * 0.25f, rc3 = rc[3] * 0.25f, rs3 = rs[3] * 0.25f, rc4 = rc[4] * 0.25f, rs4 = rs[4] * 0.25f, rc5 = rc[5] * 0.25f, rs5 = rs[5] * 0.25f, rc6 = rc[6] * 0.25f, rs6 = rs[6] * 0.25f, rc7 = rc[7] * 0.25f, rs7 = rs[7] * 0.25f;
            float vrc0 = vrc[0] * 0.35355339f, vrs0 = vrs[0] * 0.35355339f, vrc1 = vrc[1] * 0.35355339f, vrs1 = vrs[1] * 0.35355339f, vrc2 = vrc[2] * 0.35355339f, vrs2 = vrs[2] * 0.35355339f, vrc3 = vrc[3] * 0.35355339f, vrs3 = vrs[3] * 0.35355339f;
            int ende = bis;
            for (int i = von; i < bis; i++)
            {
                int o = i * kanaele;
                float u = 0f;
                for (int c = 0; c < kanaele; c++) u += data[o + c];
                u *= inv;
                if (u != 0f) st = 0;
                else if (++st > sw) { for (int c = 0; c < kanaele; c++) data[o + c] = 0f; ende = i + 1; break; }
                // Drehung je RotSchritt Abtastwerte einen Schritt weiter (Matrix bleibt orthogonal)
                if (++rz >= RotSchritt) { rz = 0; Drehen(); rc0 = rc[0] * 0.25f; rs0 = rs[0] * 0.25f; rc1 = rc[1] * 0.25f; rs1 = rs[1] * 0.25f; rc2 = rc[2] * 0.25f; rs2 = rs[2] * 0.25f; rc3 = rc[3] * 0.25f; rs3 = rs[3] * 0.25f; rc4 = rc[4] * 0.25f; rs4 = rs[4] * 0.25f; rc5 = rc[5] * 0.25f; rs5 = rs[5] * 0.25f; rc6 = rc[6] * 0.25f; rs6 = rs[6] * 0.25f; rc7 = rc[7] * 0.25f; rs7 = rs[7] * 0.25f; vrc0 = vrc[0] * 0.35355339f; vrs0 = vrs[0] * 0.35355339f; vrc1 = vrc[1] * 0.35355339f; vrs1 = vrs[1] * 0.35355339f; vrc2 = vrc[2] * 0.35355339f; vrs2 = vrs[2] * 0.35355339f; vrc3 = vrc[3] * 0.35355339f; vrs3 = vrs[3] * 0.35355339f; }
                // Vorverzoegerung
                vorB[vp] = u; int r = vp - vn; if (r < 0) r += vLen; float x = vorB[r]; if (++vp >= vLen) vp = 0;
                // Allpaesse (Schroeder): w = x + g d, y = d - g w
                { float d = ap0[aq0], ww = x + G * d; ap0[aq0] = ww; x = d - G * ww; if (++aq0 >= an0) aq0 = 0; }
                { float d = ap1[aq1], ww = x + G * d; ap1[aq1] = ww; x = d - G * ww; if (++aq1 >= an1) aq1 = 0; }
                { float d = ap2[aq2], ww = x + G * d; ap2[aq2] = ww; x = d - G * ww; if (++aq2 >= an2) aq2 = 0; }
                { float d = ap3[aq3], ww = x + G * d; ap3[aq3] = ww; x = d - G * ww; if (++aq3 >= an3) aq3 = 0; }
                // Vorstufe (Anstieg): 8 Leitungen, Hadamard 8 + Drehung (Paare j, j+4), orthonormal
                if (mitVor)
                {
                    float vs0 = vm[vp0], vs1 = vm[vp1], vs2 = vm[vp2], vs3 = vm[vp3], vs4 = vm[vp4], vs5 = vm[vp5], vs6 = vm[vp6], vs7 = vm[vp7];
                    float yv = (vs0 + vs1 - vs2 + vs3 + vs4 + vs5 - vs6 + vs7) * vnm;
                    float vg0 = vs0 * vk0, vg1 = vs1 * vk1, vg2 = vs2 * vk2, vg3 = vs3 * vk3, vg4 = vs4 * vk4, vg5 = vs5 * vk5, vg6 = vs6 * vk6, vg7 = vs7 * vk7;
                    float va0_0 = vg0 + vg1, va0_1 = vg0 - vg1;
                    float va0_2 = vg2 + vg3, va0_3 = vg2 - vg3;
                    float va0_4 = vg4 + vg5, va0_5 = vg4 - vg5;
                    float va0_6 = vg6 + vg7, va0_7 = vg6 - vg7;
                    float va1_0 = va0_0 + va0_2, va1_2 = va0_0 - va0_2;
                    float va1_1 = va0_1 + va0_3, va1_3 = va0_1 - va0_3;
                    float va1_4 = va0_4 + va0_6, va1_6 = va0_4 - va0_6;
                    float va1_5 = va0_5 + va0_7, va1_7 = va0_5 - va0_7;
                    float va2_0 = va1_0 + va1_4, va2_4 = va1_0 - va1_4;
                    float va2_1 = va1_1 + va1_5, va2_5 = va1_1 - va1_5;
                    float va2_2 = va1_2 + va1_6, va2_6 = va1_2 - va1_6;
                    float va2_3 = va1_3 + va1_7, va2_7 = va1_3 - va1_7;
                    float vw0 = vrc0 * va2_0 - vrs0 * va2_4, vw4 = vrs0 * va2_0 + vrc0 * va2_4;
                    float vw1 = vrc1 * va2_1 - vrs1 * va2_5, vw5 = vrs1 * va2_1 + vrc1 * va2_5;
                    float vw2 = vrc2 * va2_2 - vrs2 * va2_6, vw6 = vrs2 * va2_2 + vrc2 * va2_6;
                    float vw3 = vrc3 * va2_3 - vrs3 * va2_7, vw7 = vrs3 * va2_3 + vrc3 * va2_7;
                    float xv = x * 0.35355339f;
                    vm[vp0] = vw0 + xv; if (++vp0 >= ve0) vp0 = vo0;
                    vm[vp1] = vw1 - xv; if (++vp1 >= ve1) vp1 = vo1;
                    vm[vp2] = vw2 + xv; if (++vp2 >= ve2) vp2 = vo2;
                    vm[vp3] = vw3 + xv; if (++vp3 >= ve3) vp3 = vo3;
                    vm[vp4] = vw4 - xv; if (++vp4 >= ve4) vp4 = vo4;
                    vm[vp5] = vw5 + xv; if (++vp5 >= ve5) vp5 = vo5;
                    vm[vp6] = vw6 + xv; if (++vp6 >= ve6) vp6 = vo6;
                    vm[vp7] = vw7 - xv; if (++vp7 >= ve7) vp7 = vo7;
                    x = yv;
                }
                // Hauptnetz: 16 Leitungen, Tiefpass je Leitung z += (1-p)(s - z), Ausgang k z, Hadamard 16 + Drehung (Paare j, j+8)
                float s0 = m[p0], s1 = m[p1], s2 = m[p2], s3 = m[p3], s4 = m[p4], s5 = m[p5], s6 = m[p6], s7 = m[p7], s8 = m[p8], s9 = m[p9], s10 = m[p10], s11 = m[p11], s12 = m[p12], s13 = m[p13], s14 = m[p14], s15 = m[p15];
                float y = s0 + s1 - s2 + s3 + s4 - s5 - s6 + s7 - s8 + s9 + s10 - s11 + s12 + s13 - s14 - s15;
                z0 += q0 * (s0 - z0);
                z1 += q1 * (s1 - z1);
                z2 += q2 * (s2 - z2);
                z3 += q3 * (s3 - z3);
                z4 += q4 * (s4 - z4);
                z5 += q5 * (s5 - z5);
                z6 += q6 * (s6 - z6);
                z7 += q7 * (s7 - z7);
                z8 += q8 * (s8 - z8);
                z9 += q9 * (s9 - z9);
                z10 += q10 * (s10 - z10);
                z11 += q11 * (s11 - z11);
                z12 += q12 * (s12 - z12);
                z13 += q13 * (s13 - z13);
                z14 += q14 * (s14 - z14);
                z15 += q15 * (s15 - z15);
                float g0 = k0 * z0, g1 = k1 * z1, g2 = k2 * z2, g3 = k3 * z3, g4 = k4 * z4, g5 = k5 * z5, g6 = k6 * z6, g7 = k7 * z7, g8 = k8 * z8, g9 = k9 * z9, g10 = k10 * z10, g11 = k11 * z11, g12 = k12 * z12, g13 = k13 * z13, g14 = k14 * z14, g15 = k15 * z15;
                float h0_0 = g0 + g1, h0_1 = g0 - g1;
                float h0_2 = g2 + g3, h0_3 = g2 - g3;
                float h0_4 = g4 + g5, h0_5 = g4 - g5;
                float h0_6 = g6 + g7, h0_7 = g6 - g7;
                float h0_8 = g8 + g9, h0_9 = g8 - g9;
                float h0_10 = g10 + g11, h0_11 = g10 - g11;
                float h0_12 = g12 + g13, h0_13 = g12 - g13;
                float h0_14 = g14 + g15, h0_15 = g14 - g15;
                float h1_0 = h0_0 + h0_2, h1_2 = h0_0 - h0_2;
                float h1_1 = h0_1 + h0_3, h1_3 = h0_1 - h0_3;
                float h1_4 = h0_4 + h0_6, h1_6 = h0_4 - h0_6;
                float h1_5 = h0_5 + h0_7, h1_7 = h0_5 - h0_7;
                float h1_8 = h0_8 + h0_10, h1_10 = h0_8 - h0_10;
                float h1_9 = h0_9 + h0_11, h1_11 = h0_9 - h0_11;
                float h1_12 = h0_12 + h0_14, h1_14 = h0_12 - h0_14;
                float h1_13 = h0_13 + h0_15, h1_15 = h0_13 - h0_15;
                float h2_0 = h1_0 + h1_4, h2_4 = h1_0 - h1_4;
                float h2_1 = h1_1 + h1_5, h2_5 = h1_1 - h1_5;
                float h2_2 = h1_2 + h1_6, h2_6 = h1_2 - h1_6;
                float h2_3 = h1_3 + h1_7, h2_7 = h1_3 - h1_7;
                float h2_8 = h1_8 + h1_12, h2_12 = h1_8 - h1_12;
                float h2_9 = h1_9 + h1_13, h2_13 = h1_9 - h1_13;
                float h2_10 = h1_10 + h1_14, h2_14 = h1_10 - h1_14;
                float h2_11 = h1_11 + h1_15, h2_15 = h1_11 - h1_15;
                float h3_0 = h2_0 + h2_8, h3_8 = h2_0 - h2_8;
                float h3_1 = h2_1 + h2_9, h3_9 = h2_1 - h2_9;
                float h3_2 = h2_2 + h2_10, h3_10 = h2_2 - h2_10;
                float h3_3 = h2_3 + h2_11, h3_11 = h2_3 - h2_11;
                float h3_4 = h2_4 + h2_12, h3_12 = h2_4 - h2_12;
                float h3_5 = h2_5 + h2_13, h3_13 = h2_5 - h2_13;
                float h3_6 = h2_6 + h2_14, h3_14 = h2_6 - h2_14;
                float h3_7 = h2_7 + h2_15, h3_15 = h2_7 - h2_15;
                float w0 = rc0 * h3_0 - rs0 * h3_8, w8 = rs0 * h3_0 + rc0 * h3_8;
                float w1 = rc1 * h3_1 - rs1 * h3_9, w9 = rs1 * h3_1 + rc1 * h3_9;
                float w2 = rc2 * h3_2 - rs2 * h3_10, w10 = rs2 * h3_2 + rc2 * h3_10;
                float w3 = rc3 * h3_3 - rs3 * h3_11, w11 = rs3 * h3_3 + rc3 * h3_11;
                float w4 = rc4 * h3_4 - rs4 * h3_12, w12 = rs4 * h3_4 + rc4 * h3_12;
                float w5 = rc5 * h3_5 - rs5 * h3_13, w13 = rs5 * h3_5 + rc5 * h3_13;
                float w6 = rc6 * h3_6 - rs6 * h3_14, w14 = rs6 * h3_6 + rc6 * h3_14;
                float w7 = rc7 * h3_7 - rs7 * h3_15, w15 = rs7 * h3_7 + rc7 * h3_15;
                float xb = x * 0.25f;
                m[p0] = w0 + xb; if (++p0 >= e0) p0 = o0;
                m[p1] = w1 - xb; if (++p1 >= e1) p1 = o1;
                m[p2] = w2 + xb; if (++p2 >= e2) p2 = o2;
                m[p3] = w3 + xb; if (++p3 >= e3) p3 = o3;
                m[p4] = w4 - xb; if (++p4 >= e4) p4 = o4;
                m[p5] = w5 + xb; if (++p5 >= e5) p5 = o5;
                m[p6] = w6 - xb; if (++p6 >= e6) p6 = o6;
                m[p7] = w7 - xb; if (++p7 >= e7) p7 = o7;
                m[p8] = w8 + xb; if (++p8 >= e8) p8 = o8;
                m[p9] = w9 + xb; if (++p9 >= e9) p9 = o9;
                m[p10] = w10 - xb; if (++p10 >= e10) p10 = o10;
                m[p11] = w11 + xb; if (++p11 >= e11) p11 = o11;
                m[p12] = w12 + xb; if (++p12 >= e12) p12 = o12;
                m[p13] = w13 - xb; if (++p13 >= e13) p13 = o13;
                m[p14] = w14 - xb; if (++p14 >= e14) p14 = o14;
                m[p15] = w15 + xb; if (++p15 >= e15) p15 = o15;
                float ya = y * out0;
                for (int c = 0; c < kanaele; c++) data[o + c] = ya;
            }
            // Zustand zurueckschreiben
            vorPos = vp; app[0] = aq0; app[1] = aq1; app[2] = aq2; app[3] = aq3; stille = st; rotZ = rz;
            lPos[0] = p0; z[0] = z0;
            lPos[1] = p1; z[1] = z1;
            lPos[2] = p2; z[2] = z2;
            lPos[3] = p3; z[3] = z3;
            lPos[4] = p4; z[4] = z4;
            lPos[5] = p5; z[5] = z5;
            lPos[6] = p6; z[6] = z6;
            lPos[7] = p7; z[7] = z7;
            lPos[8] = p8; z[8] = z8;
            lPos[9] = p9; z[9] = z9;
            lPos[10] = p10; z[10] = z10;
            lPos[11] = p11; z[11] = z11;
            lPos[12] = p12; z[12] = z12;
            lPos[13] = p13; z[13] = z13;
            lPos[14] = p14; z[14] = z14;
            lPos[15] = p15; z[15] = z15;
            vPos[0] = vp0; vPos[1] = vp1; vPos[2] = vp2; vPos[3] = vp3; vPos[4] = vp4; vPos[5] = vp5; vPos[6] = vp6; vPos[7] = vp7;
            if (ende < bis) { Leeren(); schlaeft = true; }
            return ende;
        }
    }
}
