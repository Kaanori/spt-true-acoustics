using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // 0.8.4 - Raumweg physikalisch (Physik-Modus). Reine Rechnung ohne Unity (Offline-Test). Spiel-Teil: Raumweg.cs.
    //
    // Ursache (Lighthouse-Log 08.11.2026 19:12, MARK 7): Granate draussen 40,6 m, Hoerer im Wasserwerk, offenes Garagentor -
    //   "path=via doors/windows (rooms) effect=0.23 -> insulation 13.7 dB". Der Wert 0,23 ist KEINE Daemmung, sondern die
    //   Wegkosten-Heuristik des Spiels (PropagationJob.GetRouteCostAndDistance, beste Route = kleinste Kosten):
    //     je Portal  (Segmentanteil * (Knickwinkel^diffractionExponent + Durchgangskosten traversalMaxCost) + Schliessung)
    //                * WallOcclusion(Raeume)
    //     + Hoehe    Segmentanteil * (|dy| / typicalRoomHeight * absoluteHeightWeight + Steigung^heightExponent * Gewicht hoch/runter)
    //     + Umweg    (1 - Direktlinie / Weglaenge) * distanceWeight * Anteil bis zum letzten Portal
    //   danach mal GetEnvironmentFactor der Klanggruppe (draussen <-> drinnen, bei Figurenklaengen 1,5-2,0).
    //   Ein OFFENES Tor (Schliessung 0) kostet also trotzdem Durchgang, Knick, Hoehe und Umweg. Der Physik-Modus las diese
    //   Kosten bisher linear als dB-Skala (Wertung 0..1 = 0..60 dB): 0,1 Wegkosten = 6 dB, egal ob dort ein Tor offen steht.
    // Loesung: im Physik-Modus wird die Route aus Geometrie und Portalzustand gerechnet (Energie, zwei parallele Wege):
    //   1. Direkt durch die Oeffnungen (geometrisch): Kugelwelle ueber die ganze Weglaenge, an jeder Oeffnung Beugung um die
    //      Kante nach Kurze-Anderson (1971), N = 2*Umweg/lambda, Illuminated-Zone-Fortsetzung bis N = -0,2, je Kante hoechstens
    //      20 dB (Deckel der Einzelbeugung, ISO 9613-2). Nur der offene Teil einer Oeffnung traegt diesen Weg.
    //   2. Ueber die Raeume (diffus, Sabine/Kuttruff): Leistung durch die Oeffnung P = I * S * t (t = offen + zu * tau),
    //      im Innenraum trifft sie als P/A auf die naechste Oeffnung, am Hoerer gilt 4P/A; im Freien strahlt sie als
    //      P/(2 pi r^2) weiter. A = aequivalente Absorptionsflaeche des Raums (Raumoberflaeche * alpha + offene Portalflaechen).
    //      Der Hall des Quellraums zaehlt nicht (den spielt das Spiel selbst ueber Meta XR).
    //   Bezug: Freifeld W/(4 pi d^2) in Direktlinie (die Entfernungskurve rechnet Unity/SEB). Daemmung = 10 lg(Bezug / Summe), >= 0.
    //   Geschlossene Tueren/heile Fenster: 0.8.6 tau je Band aus dem Massengesetz des Blatts (Material + gemessene Dicke,
    //   TuerKern.cs; bis 0.8.5 ein fester Anteil der Hoechstdaemmung je Typ mit Tiefpass-Abbildung). Portale ohne Blattmodell
    //   (Baender = false) behalten tau im Bezugsband und die Tiefpass-Abbildung. Beugung macht den Rest frequenzabhaengig
    //   (Tiefpass aus der Mehrdaempfung bei 4 kHz gegenueber der Bezugsfrequenz, 2-poliger Filter).
    // ------------------------------------------------------------------------------------------

    internal struct V3
    {
        public double X, Y, Z;
        public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static V3 operator +(V3 a, V3 b) => new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator *(V3 a, double s) => new V3(a.X * s, a.Y * s, a.Z * s);
        public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public double Laenge => Math.Sqrt(X * X + Y * Y + Z * Z);
        public static double Abstand(V3 a, V3 b) => (a - b).Laenge;
        public bool IstNull => X == 0 && Y == 0 && Z == 0;
    }

    /// <summary>Eine Oeffnung (Portal) des Raumwegs, wie RoomPair.AudioPortalData plus Zustand.</summary>
    internal struct RaumPortal
    {
        public V3 Mitte, Rechts, Oben, Normale;
        public double HalbX, HalbY;
        public double Offen;      // 0 = zu, 1 = ganz offen (Anteil der freien Flaeche)
        public double Tau;        // Energie-Transmissionsgrad des geschlossenen Teils (Tuerblatt, Glas) im Bezugsband
        // 0.8.6 (Tuer-Massengesetz): Transmissionsgrad bei 1 kHz / 4 kHz aus dem Massengesetz des Blatts (TuerKern.cs).
        // Baender = false: kein Blattmodell, alle Baender mit Tau (Tiefpass-Abbildung des Physik-Modus wie bis 0.8.5).
        public double TauMitte, TauHoch;
        public bool Baender;
        public double Flaeche => 4.0 * HalbX * HalbY;
        /// <summary>Transmissionsgrad im Band b (0 = Bezug, 1 = 1 kHz, 2 = 4 kHz).</summary>
        public double TauBand(int b) => !Baender || b == 0 ? Tau : b == 1 ? TauMitte : TauHoch;
    }

    internal struct RaumwegErgebnis
    {
        public bool Gueltig;
        public double Db, DbHoch;          // Daemmung bei Bezugs- und hoher Frequenz
        public double DbMitte;             // 0.8.5: Daemmung bei 1 kHz (Baender wie Stufe 1: Bezug / 1 kHz / 4 kHz)
        public double DbTuer;              // Anteil der geschlossenen Tueren/Fenster (Bezugsband)
        public double DbTuerMitte, DbTuerHoch;   // 0.8.6: dasselbe bei 1 kHz / 4 kHz
        public bool TuerBaender;           // 0.8.6: die Route hat geschlossene Blaetter, alle mit Baendern aus dem Massengesetz (dann keine Tiefpass-Abbildung)
        public double DbGeo, DbDiffus;     // die beiden Wege einzeln (Bezugsfrequenz)
        public double DbDiffusHoch;        // 0.8.5: Raeume-Weg bei der hohen Frequenz (Log)
        public double Laenge, Direkt;      // Weglaenge ueber die Portale, Direktlinie
        public double DbAussen;            // 0.8.6: Abschirmung der Strecken im Freien (Bezugsband, schon in Db enthalten), 0 = keine/frei
        public int AussenStrecken;         // 0.8.6: Zahl der Strecken im Freien, die abgeschirmt gerechnet wurden
        // raumecke (16.11.): Ecken innerhalb der Raeume (RaumEckeKern), ihre Beugung im Bezugsband / bei 1 kHz (dB, schon in Db), Zahl
        public double DbEcken, DbEckenMitte; public int Ecken;
        // tabellenneu (16.11.): voriger Wegpunkt vor dem Austritt (Ecke oder vorletzter Portalpunkt; IstNull = keiner) - fuer die Nahblende
        // 0,3-1,5 m vor einem Durchgang (klang-richtung). Von RaumwegNetz gesetzt.
        public V3 Vor;
        // diffus-hall (10.11.): Diffusanteil der Energie im Bezugsband, UNGEDECKELT aus den Intensitaeten (Raeume-Weg / (offen + Raeume)).
        // 0 = alles gerichtet (Beugung/Direkt durch die Oeffnungen), 1 = nur Hallfeld. DiffusKern.cs: trocken spielt nur der Rest.
        public double AnteilDiffus;
        // lampenpegel (18.11.): um wie viel dB die Route im Bezugsband UEBER dem Freifeld laege (Db ist bei 0 gekappt), 0 = nicht gekappt.
        // Nur fuer Quellen, deren Abzug ohnehin weit ueber 0 liegt (Lampen, Kalibrierung.LampeDb); alle anderen lesen weiter Db.
        public double UeberFrei;
        // hallsend (B2, 12.11.): derselbe Anteil bei 1 kHz / 4 kHz aus G und D dieses Bands (Kantenbeugung daempft G mit der Frequenz
        // staerker als D). AnteilBaender = false: unbekannt -> wie das Bezugsband (Wegsuche, alte Aufrufer).
        public double AnteilDiffusMitte, AnteilDiffusHoch;
        public bool AnteilBaender;
        public double AnteilBand(int b) => !AnteilBaender || b == 0 ? AnteilDiffus : b == 1 ? AnteilDiffusMitte : AnteilDiffusHoch;
        // tunneldiffus (17.11.): Zeitstruktur der Energie je Band (ZeitKern): langsamer Teil (ueber das Hallfeld des Quellraums) und
        // schneller Teil (Direktanteil der Quelle in die erste Oeffnung), Anteile an der Summe dieser Route. Hat = false: unbekannt (k = 1).
        public ZeitKern.Baender Zeit;
        public ZeitKern.KetteSprache Sprache;   // tunneldiffus Teil 2: Raumkette + Teile bei 1 kHz (frueher Anteil erst beim Mischen)
        /// <summary>diffus-hall: Ergebnis, das nur den Diffusanteil traegt (Wegsuche: Raumfolgen = Hallfeld, Kantenwege = gerichtet).</summary>
        public static RaumwegErgebnis NurAnteil(double anteil) => new RaumwegErgebnis { AnteilDiffus = anteil };
    }

    internal static class RaumwegKern
    {
        public const double Schall = 343.0;
        public const double KanteMaxDb = 20.0;
        public const double EckenMaxDb = 25.0;   // raumecke: Ecken eines Raumstuecks zusammen (ISO 9613-2 Mehrfachbeugung)
        public const double MinFlaecheA = 1.0;
        public const double FMitte = 1000.0;

        /// <summary>Wie PropagationJob.ComputeClosestPointOnPortal: Punkt auf dem Portal-Rechteck zwischen Eintritt und Austritt.</summary>
        public static V3 PortalPunkt(in RaumPortal p, V3 ein, V3 aus)
        {
            double a = V3.Dot(ein - p.Mitte, p.Rechts), b = V3.Dot(ein - p.Mitte, p.Oben);
            double c = V3.Dot(aus - p.Mitte, p.Rechts), d = V3.Dot(aus - p.Mitte, p.Oben);
            double n1 = Math.Abs(V3.Dot(ein - p.Mitte, p.Normale)), n2 = Math.Abs(V3.Dot(aus - p.Mitte, p.Normale));
            double s = n1 + n2;
            double u = s < 1e-6 ? (a + c) * 0.5 : (n2 * a + n1 * c) / s;
            double v = s < 1e-6 ? (b + d) * 0.5 : (n2 * b + n1 * d) / s;
            u = Math.Max(-p.HalbX, Math.Min(p.HalbX, u));
            v = Math.Max(-p.HalbY, Math.Min(p.HalbY, v));
            return p.Mitte + p.Rechts * u + p.Oben * v;
        }

        /// <summary>Kurze-Anderson (1971): Einfuegungsdaempfung einer Kante, N = Fresnel-Zahl (negativ = Sichtlinie frei).
        /// 5 dB an der Schattengrenze, 0 dB ab N = -0,2 in der freien Zone, gedeckelt bei 20 dB.</summary>
        public static double KurzeAnderson(double n)
        {
            double db;
            if (n >= 0)
            {
                double x = Math.Sqrt(2 * Math.PI * n);
                db = x < 1e-6 ? 5.0 : 5.0 + 20.0 * Math.Log10(x / Math.Tanh(x));
            }
            else if (n > -0.2)
            {
                double x = Math.Sqrt(2 * Math.PI * -n);
                db = x < 1e-6 ? 5.0 : 5.0 + 20.0 * Math.Log10(x / Math.Tan(x));
            }
            else db = 0;
            return Math.Max(0, Math.Min(KanteMaxDb, db));
        }

        /// <summary>Umweg (m) um die naechste Kante der Oeffnung fuer den Weg q -> r. positiv = Schatten (Sichtlinie durch die
        /// Oeffnung verdeckt), negativ = Sichtlinie frei (Abstand zur Kante als Umweg, fuer die Illuminated-Zone).</summary>
        public static double Umweg(in RaumPortal p, V3 q, V3 r, V3 spielPunkt)
        {
            double dq = V3.Dot(q - p.Mitte, p.Normale), dr = V3.Dot(r - p.Mitte, p.Normale);
            double direkt = V3.Abstand(q, r);
            bool durch = false; double u = 0, v = 0;
            if (dq * dr < 0)                                     // q und r auf verschiedenen Seiten: Schnittpunkt mit der Ebene
            {
                double t = dq / (dq - dr);
                V3 s = q + (r - q) * t;
                u = V3.Dot(s - p.Mitte, p.Rechts); v = V3.Dot(s - p.Mitte, p.Oben);
                durch = Math.Abs(u) <= p.HalbX && Math.Abs(v) <= p.HalbY;
            }
            if (!durch)
            {
                double um = V3.Abstand(q, spielPunkt) + V3.Abstand(spielPunkt, r) - direkt;
                return Math.Max(um, 0);
            }
            // freie Sicht: naechste Kante
            double ex = p.HalbX - Math.Abs(u), ey = p.HalbY - Math.Abs(v);
            V3 kante = ex < ey
                ? p.Mitte + p.Rechts * (Math.Sign(u == 0 ? 1 : u) * p.HalbX) + p.Oben * v
                : p.Mitte + p.Rechts * u + p.Oben * (Math.Sign(v == 0 ? 1 : v) * p.HalbY);
            return -(V3.Abstand(q, kante) + V3.Abstand(kante, r) - direkt);
        }

        public static double Fresnel(double umweg, double f) => 2.0 * umweg * f / Schall;

        /// <summary>Eine Route von der Quelle zum Hoerer (beide in echter Richtung). punkte[k] = Portalpunkt k (wie im Spiel),
        /// raumA[k] = Absorptionsflaeche des Raums VOR Portal k (k = 0: Quellraum, k = n: Hoererraum), &lt;= 0 = im Freien.
        /// beugungRef/beugungHoch (Laenge n) bekommen die Kanten-dB fuer das Log.</summary>
        public static RaumwegErgebnis Route(V3 quelle, V3 hoerer, RaumPortal[] p, V3[] punkte, int n, double[] raumA,
            double fRef, double fHoch, double maxDb, double[] beugungRef = null, double[] umwegLog = null, double[] raumV = null,
            V3[] ecken = null, int[] eckStart = null)
        {
            var e = new RaumwegErgebnis();
            double d = Math.Max(V3.Abstand(quelle, hoerer), 0.5);
            // raumecke: Ecken je Raumstueck k (k = 0 Quellraum .. n Hoererraum) in ecken[eckStart[k] .. eckStart[k+1]); null = keine
            bool mitEcken = ecken != null && eckStart != null && eckStart.Length >= n + 2 && eckStart[n + 1] > 0;
            double laenge = 0; V3 vor = quelle;
            for (int k = 0; k <= n; k++)
            {
                if (mitEcken) for (int j = eckStart[k]; j < eckStart[k + 1]; j++) { laenge += V3.Abstand(vor, ecken[j]); vor = ecken[j]; }
                V3 nach = k < n ? punkte[k] : hoerer; laenge += V3.Abstand(vor, nach); vor = nach;
            }
            laenge = Math.Max(laenge, d);
            e.Laenge = laenge; e.Direkt = d;

            // Intensitaeten je Watt Quellleistung (W = 1)
            // 1. geometrisch durch die Oeffnungen: Kugelwelle ueber die Weglaenge, offener Anteil, Kantenbeugung
            double geoRef = 1.0 / (4 * Math.PI * laenge * laenge), geoHoch = geoRef, geoMitte = geoRef, tuer = 1.0, tuerM = 1.0, tuerH = 1.0;
            bool mitBlatt = false, ohneBlatt = false;   // geschlossene Teile mit / ohne Baender aus dem Massengesetz
            // raumecke: Beugung an den Ecken in den Raeumen (dieselbe Kantenformel); Quellraum-Ecken daempfen auch die Direktleistung in die
            // erste Oeffnung, Hoererraum-Ecken auch die gerichtete Abstrahlung der letzten Oeffnung
            double fq0 = 1, fq1 = 1, fq2 = 1, fh0 = 1, fh1 = 1, fh2 = 1;
            if (mitEcken)
                for (int k = 0; k <= n; k++)
                {
                    V3 a0 = k == 0 ? quelle : punkte[k - 1], b0 = k < n ? punkte[k] : hoerer;
                    double sR = 0, sM = 0, sH = 0;
                    for (int j = eckStart[k]; j < eckStart[k + 1]; j++)
                    {
                        V3 pv = j == eckStart[k] ? a0 : ecken[j - 1], pn = j == eckStart[k + 1] - 1 ? b0 : ecken[j + 1], c = ecken[j];
                        double um = Math.Max(0, V3.Abstand(pv, c) + V3.Abstand(c, pn) - V3.Abstand(pv, pn));
                        sR += KurzeAnderson(Fresnel(um, fRef)); sM += KurzeAnderson(Fresnel(um, FMitte)); sH += KurzeAnderson(Fresnel(um, fHoch)); e.Ecken++;
                    }
                    // mehrere Ecken in einem Raum: Summe hoechstens EckenMaxDb (wie die Mehrfachbeugung ISO 9613-2: 25 dB)
                    sR = Math.Min(sR, EckenMaxDb); sM = Math.Min(sM, EckenMaxDb); sH = Math.Min(sH, EckenMaxDb);
                    double gR = Math.Pow(10, -sR / 10), gM = Math.Pow(10, -sM / 10), gH = Math.Pow(10, -sH / 10);
                    geoRef *= gR; geoMitte *= gM; geoHoch *= gH; e.DbEcken += sR; e.DbEckenMitte += sM;
                    if (k == 0) { fq0 *= gR; fq1 *= gM; fq2 *= gH; }
                    if (k == n) { fh0 *= gR; fh1 *= gM; fh2 *= gH; }
                }
            for (int k = 0; k < n; k++)
            {
                V3 q = k == 0 ? quelle : punkte[k - 1], r = k == n - 1 ? hoerer : punkte[k + 1];
                if (mitEcken)
                {
                    if (eckStart[k + 1] > eckStart[k]) q = ecken[eckStart[k + 1] - 1];          // letzte Ecke vor Portal k
                    if (eckStart[k + 2] > eckStart[k + 1]) r = ecken[eckStart[k + 1]];          // erste Ecke danach
                }
                double um = Umweg(p[k], q, r, punkte[k]);
                double ilR = KurzeAnderson(Fresnel(um, fRef)), ilH = KurzeAnderson(Fresnel(um, fHoch));
                if (beugungRef != null) beugungRef[k] = ilR;
                if (umwegLog != null) umwegLog[k] = um;
                double o = Clamp01(p[k].Offen);
                geoRef *= o * Math.Pow(10, -ilR / 10);
                geoHoch *= o * Math.Pow(10, -ilH / 10);
                geoMitte *= o * Math.Pow(10, -KurzeAnderson(Fresnel(um, FMitte)) / 10);
                tuer *= o + (1 - o) * Clamp01(p[k].Tau);
                tuerM *= o + (1 - o) * Clamp01(p[k].TauBand(1));
                tuerH *= o + (1 - o) * Clamp01(p[k].TauBand(2));
                if (o < 1 && p[k].Baender) mitBlatt = true;
                else if (o < 1 && p[k].Tau < 1) ohneBlatt = true;
            }

            // 2. diffus ueber die Raeume. 0.8.5 (Meldung Interchange 08.11.2026, Gegner Etagen hoeher klang "direkt neben mir"):
            //    a) Quelle -> erste Oeffnung: Leistung = W * Omega / 4 pi (Raumwinkel des Rechtecks, Kuttruff, Room Acoustics,
            //       Punktquelle vor einer Oeffnung). Vorher I * S = W * S / (4 pi r^2) ohne Schraegeinfall und ohne Grenze - bei r = 2 m und 80 m2
            //       ging mehr Leistung durch die Oeffnung, als die Quelle abgibt (1,6 W von 1 W).
            //    b) A je Raum und Band: A = Sum S alpha + alle offenen Oeffnungen des Raums (Aufrufer) + 4 m V (Luftabsorption,
            //       ISO 9613-1 bei 20 C / 50 %, Kuttruff Gl. Sabine mit 4mV; EN 12354-6 Gl. 4). Ohne V: 4mV = 0.
            //    c) Abstand im Raum: Nachhallenergie faellt mit dem Abstand vom Eintritt, E ~ 4/A * exp(-0,04 r / T)
            //       (Barron & Lee 1988, "revised theory", JASA 84; T = 0,161 V / A nach Sabine). Ohne V: kein Abfall.
            //    Innenraum: auf die naechste Oeffnung trifft P/A (Diffusfeld), am Hoerer 4P/A; im Freien Halbraum P/(2 pi r^2)
            //    mit Luftdaempfung. Je Band (Bezug / 1 kHz / 4 kHz): der Weg ueber viele Raeume wird so auch dumpfer.
            double d0 = 0, d1 = 0, d2 = 0, s0 = 0, s1 = 0, s2 = 0;   // stahltor: s = gerichtete Abstrahlung der letzten Oeffnung
            double bezug = 1.0 / (4 * Math.PI * d * d);
            if (n > 0)
            {
                Luft(fRef, out double m0, out double l0); Luft(FMitte, out double m1, out double l1); Luft(fHoch, out double m2, out double l2);
                double o0 = Clamp01(p[0].Offen);
                double t0 = o0 + (1 - o0) * Clamp01(p[0].Tau), t1 = o0 + (1 - o0) * Clamp01(p[0].TauBand(1)), t2 = o0 + (1 - o0) * Clamp01(p[0].TauBand(2));
                double om = Raumwinkel(p[0], quelle) / (4 * Math.PI);
                // Gesamtreview A3: Quelle in einem Raum -> durch die erste Oeffnung geht ausser dem Direktanteil auch das Diffusfeld
                // des Quellraums, P = S0 * t0 * W / A0 (auf eine Flaeche im Diffusfeld trifft I = W / A; Kuttruff, Room Acoustics,
                // Kap. 5; EN 12354-3 Gl. fuer den Innenpegel), mit Luftabsorption und Abstandsabfall nach Barron wie in den
                // weiteren Raeumen. Vorher fehlte dieser Anteil (raumA[0] wurde nicht benutzt).
                double r0 = Math.Max(V3.Abstand(quelle, punkte[0]), 0.5);
                // 0.8.6: geschlossene Teile je Band mit ihrem Transmissionsgrad (Massengesetz des Blatts)
                double q0 = QuellDiffus(p[0], t0, raumA, raumV, r0, m0), q1 = QuellDiffus(p[0], t1, raumA, raumV, r0, m1), q2 = QuellDiffus(p[0], t2, raumA, raumV, r0, m2);
                // oeffnung (16.11.): Abstrahlung der letzten Oeffnung je Band (Kantenauffuellung haengt von f ab) -> st je Band
                d0 = Kette(om * fq0 * t0 + q0, p, punkte, n, quelle, hoerer, raumA, raumV, m0, l0, 0, fRef, out double pl0, out double st0, out double sb0);
                d1 = Kette(om * fq1 * t1 + q1, p, punkte, n, quelle, hoerer, raumA, raumV, m1, l1, 1, FMitte, out double pl1, out double st1, out double sb1);
                d2 = Kette(om * fq2 * t2 + q2, p, punkte, n, quelle, hoerer, raumA, raumV, m2, l2, 2, fHoch, out double pl2, out double st2, out double sb2);
                // stahltor (12.11.): gerichtete Abstrahlung der letzten Oeffnung (Herleitung bei Abstrahlung); raumecke: um die Ecken im Hoererraum
                s0 = fh0 * Abstrahlung(n, o0, Clamp01(p[0].Tau), om * fq0, q0, pl0, st0, bezug, sb0);
                s1 = fh1 * Abstrahlung(n, o0, Clamp01(p[0].TauBand(1)), om * fq1, q1, pl1, st1, bezug, sb1);
                s2 = fh2 * Abstrahlung(n, o0, Clamp01(p[0].TauBand(2)), om * fq2, q2, pl2, st2, bezug, sb2);
                // tunneldiffus: Zeitstruktur je Band (welcher Teil der Energie durch welche Raumstufen kommt, ZeitKern)
                if (raumV != null && raumA != null && raumA.Length > n && raumV.Length > n)
                {
                    // Laufzeitsummen je Band in einem Durchgang (Leistung: Raumwege-Benchmark)
                    double z0 = 0, z1 = 0, z2 = 0, y0 = 0, y1 = 0, y2 = 0, a00 = 0, a01 = 0, a02 = 0, an0 = 0, an1 = 0, an2 = 0;
                    for (int k = 0; k <= n; k++)
                    {
                        double a = raumA[k], v = raumV[k];
                        if (!(a > 0) || !(v > 0)) continue;
                        double c4 = 4.0 * v / ZeitKern.Schall;
                        double u0 = c4 / Math.Max(a + 4 * m0 * v, MinFlaecheA), u1 = c4 / Math.Max(a + 4 * m1 * v, MinFlaecheA), u2 = c4 / Math.Max(a + 4 * m2 * v, MinFlaecheA);
                        z0 += u0; z1 += u1; z2 += u2; y0 += u0 * u0; y1 += u1 * u1; y2 += u2 * u2;
                        if (k == 0) { a00 = u0; a01 = u1; a02 = u2; }
                        if (k == n) { an0 = u0; an1 = u1; an2 = u2; }
                    }
                    if (z0 > 0)
                    {
                        ZeitSetzen(ref e, 0, geoRef, d0, s0, om * fq0 * t0, q0, st0, fh0, n, z0, y0, a00, an0);
                        ZeitSetzen(ref e, 1, geoMitte, d1, s1, om * fq1 * t1, q1, st1, fh1, n, z1, y1, a01, an1);
                        ZeitSetzen(ref e, 2, geoHoch, d2, s2, om * fq2 * t2, q2, st2, fh2, n, z2, y2, a02, an2);
                        FruehSetzen(ref e, geoMitte, d1, s1, om * fq1 * t1, q1, st1, fh1, n, raumA, raumV, m1);   // Teil 2: Sprache (1 kHz)
                    }
                }
            }
            e.Gueltig = true;
            e.DbGeo = Db(bezug, geoRef, maxDb);
            // stahltor: "rooms" = alles, was ueber die Raeume kommt (Hallfeld 4P/A + Abstrahlung der letzten Oeffnung)
            e.DbDiffus = Db(bezug, d0 + s0, maxDb);
            e.DbDiffusHoch = Db(bezug, d2 + s2, maxDb);
            e.Db = Db(bezug, geoRef + d0 + s0, maxDb);
            // lampenpegel (18.11.): gekappter Teil (Summe ueber Freifeld), ungedeckelt; Db selbst bleibt unveraendert
            double ueberF = geoRef + d0 + s0 > bezug && bezug > 0 ? 10 * Math.Log10((geoRef + d0 + s0) / bezug) : 0;
            e.UeberFrei = double.IsNaN(ueberF) || double.IsInfinity(ueberF) ? 0 : ueberF;
            e.DbHoch = Db(bezug, geoHoch + d2 + s2, maxDb);
            e.DbMitte = Db(bezug, geoMitte + d1 + s1, maxDb);
            // diffus-hall: vor dem Deckel bei 0 dB (Summe kann ueber Freifeld liegen). Diffus am Hoerer ist der Raeume-Weg nur, wenn der Hoerer
            // in einem Raum steht (letzte Stufe 4P/A, Hallfeld von ueberall). Steht er im Freien, strahlt die letzte Oeffnung P/(2 pi r^2) in den
            // Halbraum: zeitlich verschmiert, aber aus der Richtung der Oeffnung -> gerichtet (Anteil 0).
            // stahltor: im Raum ist nur 4P/A diffus; die Abstrahlung der letzten Oeffnung (s) kommt aus ihrer Richtung -> gerichtet,
            // wie im Wandweg (WegeKern.WandwegTeile "gerichtet").
            bool hoererImRaum = n > 0 && raumA != null && raumA.Length > n && raumA[n] > 0;
            e.AnteilDiffus = hoererImRaum ? DiffusKern.Anteil(geoRef + s0, d0) : 0;
            // hallsend (B2): je Band mit G und D desselben Bands (geoMitte/d1, geoHoch/d2 wie Db/DbMitte/DbHoch)
            e.AnteilDiffusMitte = hoererImRaum ? DiffusKern.Anteil(geoMitte + s1, d1) : 0;
            e.AnteilDiffusHoch = hoererImRaum ? DiffusKern.Anteil(geoHoch + s2, d2) : 0;
            e.AnteilBaender = true;
            e.DbTuer = Math.Min(maxDb, -10 * Math.Log10(Math.Max(tuer, 1e-12)));
            e.DbTuerMitte = Math.Min(maxDb, -10 * Math.Log10(Math.Max(tuerM, 1e-12)));
            e.DbTuerHoch = Math.Min(maxDb, -10 * Math.Log10(Math.Max(tuerH, 1e-12)));
            e.TuerBaender = mitBlatt && !ohneBlatt;   // ohne Blatt-Portal genau wie bis 0.8.5 (Tiefpass-Abbildung)
            return e;
        }

        /// <summary>0.8.6 Leistung: untere Schranke der Daemmung Route(...).Db (Bezugsband), ohne teure Funktionen ausser einem Log10.
        /// Jeder Faktor ist eine obere Schranke des Faktors in Route: Kantenbeugung Kurze-Anderson im Schatten (N >= 0)
        /// 5 + 20 lg(x / tanh x) >= 5 + 20 lg max(1, x), x^2 = 2 pi N, gedeckelt bei 20 dB; frei (N &lt; 0) >= 0 dB.
        /// Raumwinkel eines Rechtecks im Abstand w <= Flaeche / w^2 und <= 2 pi. Diffusfeld: aF = max(A + 4 m V, 1) >= max(A, 1),
        /// Abfall exp(-0,04 r / T) <= 1, Luftdaempfung im Freien <= 1. Damit ist UntereDb <= Route(...).Db fuer jede Route.</summary>
        public static double UntereDb(V3 quelle, V3 hoerer, RaumPortal[] p, V3[] punkte, int n, double[] raumA, double fRef, double maxDb, bool umgekehrt = false)
        {
            // umgekehrt: Felder in Job-Richtung (wie vor RaumwegNetz.Umdrehen) - Portal k der echten Richtung = n - 1 - k, Raum k = n - k
            int P(int k) => umgekehrt ? n - 1 - k : k;
            double A(int k) => umgekehrt ? raumA[n - k] : raumA[k];
            double d = Math.Max(V3.Abstand(quelle, hoerer), 0.5);
            double laenge = 0; V3 vor = quelle;
            for (int k = 0; k < n; k++)
            {
                ref RaumPortal pk = ref p[k];
                if (!(pk.HalbX >= 0 && pk.HalbY >= 0)) return 0;   // negative Groesse: keine Schranke
                V3 pu = punkte[P(k)];
                laenge += V3.Abstand(vor, pu); vor = pu;
            }
            laenge += V3.Abstand(vor, hoerer);
            laenge = Math.Max(laenge, d);
            double geo = 1.0 / (4 * Math.PI * laenge * laenge);
            for (int k = 0; k < n && geo > 0; k++)
            {
                int j = P(k);
                V3 q = k == 0 ? quelle : punkte[P(k - 1)], r = k == n - 1 ? hoerer : punkte[P(k + 1)];
                double um = Umweg(p[j], q, r, punkte[j]);
                double f = 1.0;
                if (um >= 0) f = Math.Max(0.01, 0.31623 / Math.Max(1.0, 2 * Math.PI * Fresnel(um, fRef)));
                geo *= Clamp01(p[j].Offen) * f;
            }
            double diffus = 0;
            if (n > 0)
            {
                ref RaumPortal p0 = ref p[P(0)];
                double o0 = Clamp01(p0.Offen);
                double t0 = o0 + (1 - o0) * Clamp01(p0.Tau);
                V3 rel = quelle - p0.Mitte;
                double w = Math.Max(Math.Abs(V3.Dot(rel, p0.Normale)), 1e-3);
                double leistung = Math.Min(0.5, p0.Flaeche / (4 * Math.PI * w * w)) * t0;
                double a0 = A(0);
                if (a0 > 0) leistung += p0.Flaeche * t0 / Math.Max(a0, MinFlaecheA);
                double inzident = 0, einfall = 0;
                for (int k = 0; k < n; k++)
                {
                    int j = P(k);
                    if (k > 0)
                    {
                        double o = Clamp01(p[j].Offen);
                        leistung = einfall * (o + (1 - o) * Clamp01(p[j].Tau));
                    }
                    double a = A(k + 1);
                    bool letzte = k == n - 1;
                    if (a > 0)
                    {
                        inzident = (letzte ? 4.0 : 1.0) * leistung / Math.Max(a, MinFlaecheA);
                        if (!letzte) einfall = inzident * p[P(k + 1)].Flaeche;
                        // stahltor/oeffnung: obere Schranke der Abstrahlung der letzten Oeffnung (Route: <= Leistung * OeffnungStrahl)
                        else inzident += leistung * StrahlOben(p[j], hoerer);
                    }
                    else if (letzte) inzident = leistung * StrahlOben(p[j], hoerer);   // oeffnung: im Freien dieselbe Abstrahlung wie im Raum
                    else einfall = leistung * Math.Min(1.0, RaumwinkelOben(p[P(k + 1)], punkte[j]) / (2 * Math.PI));   // 0.8.6 wie Kette
                }
                diffus = inzident;
            }
            return Db(1.0 / (4 * Math.PI * d * d), geo + diffus, maxDb);
        }

        /// <summary>Gesamtreview A3: Leistung des Quellraum-Diffusfelds durch die erste Oeffnung (W = 1), 0 im Freien.
        /// A wie in Kette (A0 + 4 m V, mindestens MinFlaecheA), Abfall exp(-0,04 r / T) ueber den Abstand Quelle -> Oeffnung.</summary>
        internal static double QuellDiffus(in RaumPortal p0, double t0, double[] raumA, double[] raumV, double r0, double m)
        {
            double a = raumA != null && raumA.Length > 0 ? raumA[0] : 0;
            if (a <= 0) return 0;
            double v = raumV != null && raumV.Length > 0 ? Math.Max(0, raumV[0]) : 0;
            double aF = Math.Max(a + 4 * m * v, MinFlaecheA);
            double abfall = v > 0 ? Math.Exp(-0.04 * r0 / Math.Max(0.161 * v / aF, 0.05)) : 1.0;
            return p0.Flaeche * t0 / aF * abfall;
        }

        /// <summary>tunneldiffus (17.11.): Zeitstruktur der Route im Band b (ZeitKern, Herleitung dort). Teile der Energie (W = 1):
        /// geo = gebeugter Direktschall (unverschmiert, k = 1); d = Hallfeld des Hoererraums 4P/A (bzw. im Freien die Abstrahlung der
        /// letzten Oeffnung); s = gerichtete Abstrahlung der letzten Oeffnung in den Hoererraum. Die Kette ist linear in der Startleistung
        /// om + q (om = Direktanteil der Quelle Omega/4pi in die erste Oeffnung, q = Diffusfeld des Quellraums S/A): d und s teilen sich im
        /// selben Verhaeltnis auf - ausser bei n = 1, wo die Abstrahlung den Blatt-Teil des Direktanteils direkt (ohne Raumstufe) und
        /// q * strahl getrennt fuehrt (Abstrahlung). Laufzeitstufen (tau = 4V/(cA), A wie in der Kette mit 4mV):
        ///   d aus q: Raeume 0..n, d aus om: 1..n, s aus q: 0..n-1, s aus om: 1..n-1 (Raum n = Hoererraum, Raum 0 = Quellraum).
        /// Langsam = Teile aus q, schnell = Teile aus om; Anteile an geo + d + s.
        /// sum/sum2 = Summe tau bzw. tau^2 ueber die Raeume 0..n (tau = ZeitKern.TauRaum mit A + 4mV wie die Kette), tau0/tauN = Quell-/Hoererraum.</summary>
        static void ZeitSetzen(ref RaumwegErgebnis e, int b, double geo, double d, double s, double omP, double q, double st, double fh,
                               int n, double sum, double sum2, double tau0, double tauN)
        {
            double ges = Math.Max(0, geo) + Math.Max(0, d) + Math.Max(0, s);
            if (!(ges > 0) || n <= 0) return;
            double start = Math.Max(0, omP) + Math.Max(0, q);
            double fq = start > 0 ? Math.Max(0, q) / start : 0;
            double dq = Math.Max(0, d) * fq, dom = Math.Max(0, d) - dq;
            double sq, som;
            if (n == 1) { sq = Math.Min(Math.Max(0, s), fh * Math.Max(0, q) * Math.Max(0, st)); som = Math.Max(0, s) - sq; }
            else { sq = Math.Max(0, s) * fq; som = Math.Max(0, s) - sq; }
            var l = new ZeitKern.Summe(); var f = new ZeitKern.Summe();
            ZeitKern.Dazu(ref l, dq, sum, sum2);
            ZeitKern.Dazu(ref l, sq, sum - tauN, sum2 - tauN * tauN);
            ZeitKern.Dazu(ref f, dom, sum - tau0, sum2 - tau0 * tau0);
            ZeitKern.Dazu(ref f, som, sum - tau0 - tauN, sum2 - tau0 * tau0 - tauN * tauN);
            e.Zeit.Setzen(b, ZeitKern.Normiert(l, ges), ZeitKern.Normiert(f, ges));
        }

        /// <summary>tunneldiffus Teil 2 (17.11.): Raumkette und Energieteile der Route bei 1 kHz (Hauptband der Sprachverstaendlichkeit) fuer den
        /// fruehen Anteil im D50-Fenster ablegen (Teile wie ZeitSetzen; ausgewertet in RoutenMischer.FruehMischen mit der EXAKTEN Ketten-CDF -
        /// die Zwei-Stufen-Naeherung ueberschaetzt den fruehen Teil langer Ketten um bis 13 dB, d50.py).</summary>
        static void FruehSetzen(ref RaumwegErgebnis e, double geo, double d, double s, double omP, double q, double st, double fh,
                                int n, double[] raumA, double[] raumV, double m)
        {
            double ges = Math.Max(0, geo) + Math.Max(0, d) + Math.Max(0, s);
            if (!(ges > 0) || n <= 0) return;
            var k = new ZeitKern.KetteSprache();
            double mz = 0;   // Zwischenraeume ueber 2: in M1 zusammengezaehlt
            for (int i = 0; i <= n; i++)
            {
                double a = raumA[i], v = raumV[i];
                float t = (float)(a > 0 && v > 0 ? ZeitKern.TauRaum(v, Math.Max(a + 4 * m * v, MinFlaecheA)) : 0);
                if (i == 0) k.T0 = t;
                else if (i == n) k.TN = t;
                else if (t > 0)
                {
                    if (k.MN == 0) k.M0 = t; else if (k.MN == 1) k.M1 = t; else mz += t;
                    if (k.MN < 2) k.MN++;
                }
            }
            if (mz > 0) k.M1 += (float)mz;
            double start = Math.Max(0, omP) + Math.Max(0, q);
            double fq = start > 0 ? Math.Max(0, q) / start : 0;
            double dq = Math.Max(0, d) * fq, dom = Math.Max(0, d) - dq;
            double sq = n == 1 ? Math.Min(Math.Max(0, s), fh * Math.Max(0, q) * Math.Max(0, st)) : Math.Max(0, s) * fq, som = Math.Max(0, s) - sq;
            k.DQ = (float)(dq / ges); k.DOM = (float)(dom / ges); k.SQ = (float)(sq / ges); k.SOM = (float)(som / ges);
            k.Hat = true;
            e.Sprache = k;
        }

        /// <summary>0.8.6 Wegsuche (Livetest-Fix): nur der Raeume-Weg (diffus) einer Portalfolge im Band 'band' (0 = Bezug, 1 = 1 kHz,
        /// 2 = 4 kHz) - dieselbe Rechnung wie in Route (Raumwinkel der ersten Oeffnung + Diffusfeld des Quellraums, Kette ueber die
        /// Raeume, am Hoerer 4P/A bzw. P/(2 pi r^2) im Freien). m/luftDb aus LuftWerte. Ergebnis: Intensitaet am Hoerer (W = 1).</summary>
        public static double Diffus(V3 quelle, V3 hoerer, RaumPortal[] p, V3[] punkte, int n, double[] raumA, double[] raumV, int band, double m, double luftDb, double f = 0)
        {
            // oeffnung: Frequenz des Bands fuer die Kantenauffuellung der letzten Oeffnung; 0 = Standardbaender 250 / 1000 / 4000 Hz
            if (!(f > 0)) f = band <= 0 ? 250.0 : band == 1 ? FMitte : 4000.0;
            if (n <= 0) return 0;
            double o0 = Clamp01(p[0].Offen);
            double t = o0 + (1 - o0) * Clamp01(p[0].TauBand(band));
            double om = Raumwinkel(p[0], quelle) / (4 * Math.PI);
            double r0 = Math.Max(V3.Abstand(quelle, punkte[0]), 0.5);
            double qd = QuellDiffus(p[0], t, raumA, raumV, r0, m);
            double i = Kette(om * t + qd, p, punkte, n, quelle, hoerer, raumA, raumV, m, luftDb, band, f, out double pl, out double st, out double sb);
            // stahltor: + Abstrahlung der letzten Oeffnung wie in Route. Die Wegsuche fasst die Portale zusammen (Offen = 0) und fuehrt den
            // Direktschall der Quelle als eigene Kantenwege -> bei n = 1 wird der Direktanteil Omega/4pi NICHT abgestrahlt (o0 = 1 gesetzt,
            // sonst doppelt mit dem Kantenweg), nur das Diffusfeld des Quellraums. bezug spielt dann keine Rolle.
            return i + Abstrahlung(n, 1.0, 0.0, om, qd, pl, st, 0.0, sb);
        }

        /// <summary>0.8.6: Luftabsorption wie Luft (20 C / 50 %), ohne den gemeinsamen Zwischenspeicher (fuer andere Faeden).</summary>
        public static void LuftWerte(double f, out double m, out double db) { db = Iso9613.Alpha(f, 20, Iso9613.Bezugsdruck, 50); m = db / 4.3429; }

        /// <summary>Diffuse Kette ab der Leistung startP hinter Oeffnung 0 (W = 1). m = Luft-Energiedaempfung (1/m),
        /// luftDb = dB/m (im Freien). Ergebnis: Intensitaet am Hoerer (Freifeld-Aequivalent).</summary>
        static double Kette(double startP, RaumPortal[] p, V3[] punkte, int n, V3 quelle, V3 hoerer, double[] raumA, double[] raumV, double m, double luftDb, int band, double f)
            => Kette(startP, p, punkte, n, quelle, hoerer, raumA, raumV, m, luftDb, band, f, out _, out _, out _);

        // ------------------------------------------------------------------------------------------
        // stahltor (12.11.2026) - gerichtete Abstrahlung der letzten Oeffnung. Ursache (Klang-Log 14:03:01.542, Hoerer 1,3 m hinter dem
        // geschlossenen Tor #209): die Kette gab am Hoerer im Raum nur das Hallfeld 4P/A; G ist bei geschlossenem Portal 0 -> Diffusanteil
        // 100 %, der trockene Klang fiel auf den Boden. Die Leistung P, die durch die letzte Oeffnung (offen oder Blatt) geht, strahlt aber
        // zuerst von der Oeffnung aus zum Hoerer, als Flaechenquelle in den Halbraum: I = P min(1/(2 pi r^2), 1/S) (dicht davor I = P/S,
        // weit weg Halbraum-Kugelwelle) - dieselbe Rechnung wie der Wandweg fuer den diffus auftreffenden Teil (WegeKern.WandwegTeile,
        // EN ISO 12354-1 Anh. / Kuttruff, Room Acoustics Kap. 5: Abstrahlung einer Flaeche im Diffusfeld). Dazu wie bisher 4P/A2 (Hallfeld
        // des Hoererraums, das dieselbe Leistung aufbaut: Direkt + Hall wie L = Lw + 10 lg(Q/(4 pi r^2) + 4/A), Hopkins, Sound Insulation
        // Kap. 1; im Wandweg genauso gezaehlt). Nichts doppelt:
        //  - G (gebeugter Direktschall der Quelle) traegt bei n = 1 schon den OFFENEN Teil des Direktanteils Omega/4pi -> abgestrahlt wird
        //    nur der Blatt-Teil (1 - o) tau Omega/4pi, wie der Wandweg "Direkt -> Direkt" nie mehr als durch eine unendliche Wand
        //    (min(bezug, ...)), plus das Diffusfeld des Quellraums (in G nie enthalten). Bei n >= 2 ist P nur Diffusleistung des
        //    vorletzten Raums (G = Direktschall der Quelle) -> ganz.
        //  - Stetig: o 0 -> 1 laesst den Blatt-Teil gegen 0 gehen, waehrend G waechst; r -> 0 bleibt bei P/S begrenzt.
        //  - Hoerer im Freien: die Kette rechnet die letzte Stufe schon als gerichtete Abstrahlung (seit oeffnung: OeffnungStrahl) -> kein Zusatz.
        // ------------------------------------------------------------------------------------------

        // ------------------------------------------------------------------------------------------
        // oeffnung (16.11.2026, Meldung Reserve 20:08, review-20261110/bunkerdraussen-STAND.md): Hoerer DRAUSSEN auf dem Rampenmund (0,3 m
        // hinter der Ebene der 46-m2-Oeffnung #1214, 3,5 m ueber ihrer Oberkante) hoerte einen PMC 13 m tiefer im Bunker mit 5 dB Abzug;
        // die letzte Stufe war P/(2 pi r^2) zum naechsten Portalpunkt - ohne Richtwirkung, ohne Flaechenausdehnung, ohne Seitenpruefung
        // (Raumwinkel nimmt |w|) und im Freien ohne die 1/S-Grenze (r >= 0,5 m: +9 dB Sprung beim Hinaustreten). Jetzt fuer den OFFENEN Teil:
        //  - Lambert-Flaechenstrahler: eine Oeffnung, auf die ein Diffusfeld faellt, strahlt mit konstanter Strahldichte (cos-Gesetz);
        //    Integral ueber die Flaeche: I = P / S * Omega_H / pi (Omega_H = Raumwinkel der Oeffnung vom Hoerer, Kuttruff, Room Acoustics,
        //    Kap. 2/5 "Lambert"; auf der Achse fern P/(pi r^2), dicht davor 2P/S). Nur zur Abstrahlseite (weg vom Raum davor).
        //  - Auffuellung durch Beugung am Rand (Wellenanteil, den das geometrische cos-Gesetz streifend/dahinter nicht hat):
        //    P / (2 pi r_e^2) * 10^(-KA(N)/10), r_e = Abstand zum naechsten Randpunkt, N aus dem Umweg Oeffnungsmitte -> Rand -> Hoerer
        //    (Kurze-Anderson wie an den Portalen); vor der Ebene N = 0 (5 dB, Schattengrenze). Gegenprobe OeAL-Richtlinie 28 (1987),
        //    Richtwirkung von Oeffnungen: -4,9 dB bei 90 Grad gegen den Halbraum. Genommen wird das Groessere (nichts doppelt, stetig).
        // Der GESCHLOSSENE Teil (Blatt, tau) strahlt wie bisher als Flaeche in den Halbraum, min(1/(2 pi r^2), 1/S) (WandwegTeile; Platten
        // haben bis nahe 90 Grad keine nennenswerte Richtwirkung, OeAL 28 Ausgabe 2001: 0 dB bis 85 Grad); dahinter nur die Kantenauffuellung.
        // Gewichtet mit dem Leistungsanteil o / (o + (1 - o) tau) des Bands. Zwischenstufen im Freien (Einfall auf die naechste Oeffnung,
        // Omega / 2 pi) bleiben unveraendert - dort ist der Fehler nicht gerechnet.
        // Abnahme: review-20261110/bunkerdraussen/reihe.py (6 Hoererorte), innen.py (im Bunker nichts leiser); OeffnungTest.cs.
        // ------------------------------------------------------------------------------------------

        /// <summary>oeffnung: Abstrahlfaktor (I = P * Faktor) der Oeffnung p zum Hoerer. punkt = Portalpunkt der Route, vorher = Punkt im Raum
        /// VOR der Oeffnung (bestimmt die Abstrahlseite; liegt er in der Ebene, gilt der Hoerer als vorn), offen = Leistungsanteil des offenen
        /// Teils (0..1), f = Frequenz (Hz). blatt = Faktor des geschlossenen Teils allein.</summary>
        public static double OeffnungStrahl(in RaumPortal p, V3 punkt, V3 vorher, V3 hoerer, double offen, double f, out double blatt)
        {
            double s = Math.Max(p.Flaeche, 1e-6);
            double sv = V3.Dot(vorher - p.Mitte, p.Normale), sh = V3.Dot(hoerer - p.Mitte, p.Normale);
            bool vorn = !(sv * sh > 0);
            // naechster Randpunkt
            V3 rel = hoerer - p.Mitte;
            double u = Math.Max(-p.HalbX, Math.Min(p.HalbX, V3.Dot(rel, p.Rechts))), v = Math.Max(-p.HalbY, Math.Min(p.HalbY, V3.Dot(rel, p.Oben)));
            if (Math.Abs(u) < p.HalbX && Math.Abs(v) < p.HalbY)
            {
                if (p.HalbX - Math.Abs(u) < p.HalbY - Math.Abs(v)) u = u < 0 ? -p.HalbX : p.HalbX;
                else v = v < 0 ? -p.HalbY : p.HalbY;
            }
            V3 rand = p.Mitte + p.Rechts * u + p.Oben * v;
            double re = Math.Max(V3.Abstand(rand, hoerer), 0.5);
            double il;
            if (vorn) il = KurzeAnderson(0);
            else
            {
                V3 sq = p.Mitte + p.Normale * (sv > 0 ? -0.01 : 0.01);   // knapp auf der Abstrahlseite
                double um = Math.Max(0, V3.Abstand(sq, rand) + V3.Abstand(rand, hoerer) - V3.Abstand(sq, hoerer));
                il = KurzeAnderson(Fresnel(um, f));
            }
            double kante = Math.Pow(10, -il / 10) / (2 * Math.PI * re * re);
            double lambert = vorn ? Raumwinkel(p, hoerer) / (Math.PI * s) : 0;
            blatt = vorn ? StrahlFaktor(s, V3.Abstand(punkt, hoerer)) : kante;
            double o = Clamp01(offen);
            return o * Math.Max(lambert, kante) + (1 - o) * blatt;
        }

        /// <summary>oeffnung: obere Schranke von OeffnungStrahl (fuer UntereDb): Lambert &lt;= min(2/S, 1/(pi rho^2)), Blatt &lt;= dasselbe,
        /// Kante &lt;= 10^(-0,5)/(2 pi max(rho, 0,5)^2), rho = Abstand des Hoerers zum Rechteck.</summary>
        public static double StrahlOben(in RaumPortal p, V3 hoerer)
        {
            V3 rel = hoerer - p.Mitte;
            double du = Math.Max(0, Math.Abs(V3.Dot(rel, p.Rechts)) - p.HalbX), dv = Math.Max(0, Math.Abs(V3.Dot(rel, p.Oben)) - p.HalbY), w = V3.Dot(rel, p.Normale);
            double rho2 = Math.Max(du * du + dv * dv + w * w, 1e-6), r5 = Math.Max(rho2, 0.25);
            return Math.Max(Math.Min(2.0 / Math.Max(p.Flaeche, 1e-6), 1.0 / (Math.PI * rho2)), 0.31623 / (2 * Math.PI * r5));
        }

        /// <summary>stahltor: Abstrahlfaktor einer Oeffnung der Flaeche s im Abstand r (I = P * Faktor): min(1/(2 pi r^2), 1/s).
        /// r wie die letzte Stufe der Kette (mindestens 0,5 m).</summary>
        public static double StrahlFaktor(double s, double r)
        {
            r = Math.Max(r, 0.5);
            return Math.Min(1.0 / (2 * Math.PI * r * r), 1.0 / Math.Max(s, 1e-6));
        }

        /// <summary>stahltor: gerichtete Intensitaet der letzten Oeffnung am Hoerer (W = 1). n = Zahl der Portale, o0/tau0 = Offen und
        /// Transmissionsgrad des ersten Portals (dieses Bands), om = Omega/4pi der Quelle zum ersten Portal, quellDiffus = Diffusleistung
        /// des Quellraums durch das erste Portal, pLetzt/strahl aus Kette (0 = Hoerer im Freien), bezug = 1/(4 pi d^2).</summary>
        public static double Abstrahlung(int n, double o0, double tau0, double om, double quellDiffus, double pLetzt, double strahl, double bezug, double strahlBlatt = -1)
        {
            if (n <= 0 || !(strahl > 0) || !(pLetzt > 0)) return 0;
            if (n >= 2) return pLetzt * strahl;
            double blatt = (1 - o0) * tau0;
            // oeffnung: der Blatt-Teil des Direktanteils strahlt mit dem Faktor des geschlossenen Teils (strahlBlatt < 0: wie strahl)
            double sb = strahlBlatt >= 0 ? strahlBlatt : strahl;
            return blatt * Math.Min(bezug, om * sb) + quellDiffus * strahl;
        }

        /// <summary>Kette wie oben; pLetzt = Leistung durch die letzte Oeffnung und strahl = StrahlFaktor dorthin, wenn der Hoerer in einem
        /// Raum steht (sonst 0: die letzte Stufe ist schon die gerichtete Abstrahlung ins Freie).</summary>
        static double Kette(double startP, RaumPortal[] p, V3[] punkte, int n, V3 quelle, V3 hoerer, double[] raumA, double[] raumV, double m, double luftDb, int band,
                            double f, out double pLetzt, out double strahl, out double strahlBlatt)
        {
            pLetzt = 0; strahl = 0; strahlBlatt = 0;
            // 0.8.6 (Meldung Streets 09.11.2026, Bot looted 42 m entfernt hinter Raumwegen, in der Tiefgarage hoerbar):
            // einfall = Leistung, die auf die naechste Oeffnung trifft. Im Freien strahlt die Oeffnung k in den Halbraum, auf
            // Oeffnung k+1 trifft der Anteil Omega / 2 pi (Raumwinkel von Portalpunkt k aus, wie die Quelle vor Oeffnung 0 mit
            // Omega / 4 pi). Vorher I * S = P * S / (2 pi r^2): immer senkrechter Einfall und ohne Grenze - bei schraeg liegenden
            // oder nahen Oeffnungen ging mehr Leistung in die naechste Oeffnung als die vorige abstrahlt. Beleg (Log 17:15:54/55):
            // Bot 0,9 m hinter der Friseurtuer -> Route 14,4 dB, derselbe Bot 2 m weiter DRAUSSEN vor dem Garagentor -> 30,6 dB.
            double leistung = startP, inzident = 0, einfall = 0;
            for (int k = 0; k < n; k++)
            {
                if (k > 0)
                {
                    double o = Clamp01(p[k].Offen);
                    leistung = einfall * (o + (1 - o) * Clamp01(p[k].TauBand(band)));
                }
                double a = raumA[k + 1];
                bool letzte = k == n - 1;
                double r = Math.Max(V3.Abstand(punkte[k], letzte ? hoerer : punkte[k + 1]), 0.5);
                // oeffnung: Abstrahlung der letzten Oeffnung (Lambert + Kante fuer den offenen, Halbraum fuer den geschlossenen Teil)
                double faktor = 0, faktorBlatt = 0;
                if (letzte)
                {
                    double ok = Clamp01(p[k].Offen), tk = ok + (1 - ok) * Clamp01(p[k].TauBand(band));
                    faktor = OeffnungStrahl(p[k], punkte[k], k == 0 ? quelle : punkte[k - 1], hoerer, tk > 0 ? ok / tk : 1.0, f, out faktorBlatt);
                }
                if (a > 0)
                {
                    double v = raumV != null && raumV.Length > k + 1 ? Math.Max(0, raumV[k + 1]) : 0;
                    double aF = Math.Max(a + 4 * m * v, MinFlaecheA);
                    double abfall = v > 0 ? Math.Exp(-0.04 * r / Math.Max(0.161 * v / aF, 0.05)) : 1.0;
                    inzident = (letzte ? 4.0 : 1.0) * leistung / aF * abfall;
                    if (!letzte) einfall = inzident * p[k + 1].Flaeche;
                    else { pLetzt = leistung; strahl = faktor; strahlBlatt = faktorBlatt; }   // stahltor/oeffnung: Abstrahlung der letzten Oeffnung
                }
                else if (letzte) inzident = leistung * faktor * Math.Pow(10, -luftDb * r / 10);   // oeffnung: vorher P/(2 pi r^2), r >= 0,5 m
                else einfall = leistung * Raumwinkel(p[k + 1], punkte[k]) / (2 * Math.PI) * Math.Pow(10, -luftDb * r / 10);
            }
            return inzident;
        }

        /// <summary>0.8.6: obere Schranke von Raumwinkel(p, q) ohne Winkelfunktionen: Omega = Int w / rho^3 dS &lt;= S * w / rho_min^3
        /// (rho_min = Abstand von q zum Rechteck), hoechstens 2 pi.</summary>
        public static double RaumwinkelOben(in RaumPortal p, V3 q)
        {
            V3 rel = q - p.Mitte;
            double u = V3.Dot(rel, p.Rechts), v = V3.Dot(rel, p.Oben), w = Math.Max(Math.Abs(V3.Dot(rel, p.Normale)), 1e-3);   // wie Raumwinkel
            double du = Math.Max(0, Math.Abs(u) - p.HalbX), dv = Math.Max(0, Math.Abs(v) - p.HalbY);
            double rho2 = du * du + dv * dv + w * w;
            if (rho2 < 1e-12) return 2 * Math.PI;
            return Math.Min(2 * Math.PI, p.Flaeche * w / (rho2 * Math.Sqrt(rho2)));
        }

        /// <summary>Raumwinkel (sr) des Portal-Rechtecks von Punkt q aus (exakt, 0..2 pi).</summary>
        public static double Raumwinkel(in RaumPortal p, V3 q)
        {
            V3 rel = q - p.Mitte;
            double u = V3.Dot(rel, p.Rechts), v = V3.Dot(rel, p.Oben), w = Math.Max(Math.Abs(V3.Dot(rel, p.Normale)), 1e-3);
            double G(double x, double y) => Math.Atan(x * y / (w * Math.Sqrt(x * x + y * y + w * w)));
            double x1 = -p.HalbX - u, x2 = p.HalbX - u, y1 = -p.HalbY - v, y2 = p.HalbY - v;
            return Math.Max(0, Math.Min(2 * Math.PI, G(x2, y2) - G(x1, y2) - G(x2, y1) + G(x1, y1)));
        }

        // Luftabsorption ISO 9613-1 bei 20 C, 50 % r. F., 101,325 kPa (Raeume: ohne Wetter). m = alpha / (10 lg e).
        static void Luft(double f, out double m, out double db)
        {
            // 0.8.5 Leistung: fester Zustand (20 C / 50 %) -> je Frequenz einmal (Iso9613.Alpha: ~10 Exp/Pow, bisher 3x je Route)
            if (LuftCache)
                for (int i = 0; i < luftN; i++)
                    if (luftF[i] == f) { m = luftM[i]; db = luftDb[i]; return; }
            db = Iso9613.Alpha(f, 20, Iso9613.Bezugsdruck, 50);
            m = db / 4.3429;
            if (LuftCache) { int i = luftN < luftF.Length ? luftN++ : (luftNext++ % luftF.Length); luftF[i] = f; luftM[i] = m; luftDb[i] = db; }
        }

        /// <summary>0.8.5: Luftabsorption je Frequenz merken (false = jedes Mal rechnen, Referenz im Benchmark).</summary>
        internal static bool LuftCache = true;
        static readonly double[] luftF = new double[8], luftM = new double[8], luftDb = new double[8];
        static int luftN, luftNext;

        static double Db(double bezug, double wert, double maxDb)
        {
            if (wert <= 0) return maxDb;
            return Math.Max(0, Math.Min(maxDb, 10 * Math.Log10(bezug / wert)));
        }

        /// <summary>Grenzfrequenz eines 2-poligen Tiefpasses (|H|^2 = 1/(1+(f/fc)^4)), der bei fHoch genau die Mehrdaempfung
        /// mehrDb hat. Keine Mehrdaempfung -> 22 kHz.</summary>
        public static double Tiefpass(double mehrDb, double fHoch, double fMin = 200, double fMax = 22000)
        {
            if (mehrDb <= 0.05) return fMax;
            double fc = fHoch / Math.Pow(Math.Pow(10, mehrDb / 10) - 1, 0.25);
            return Math.Max(fMin, Math.Min(fMax, fc));
        }

        /// <summary>Aequivalente Absorptionsflaeche eines Innenraums (m2): Oberflaeche * alpha + offene Portalflaechen.</summary>
        public static double Absorption(double oberflaeche, double alpha, double offeneFlaeche)
            => Math.Max(MinFlaecheA, oberflaeche * Clamp01(alpha) + Math.Max(0, offeneFlaeche));

        static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);
    }

    // ------------------------------------------------------------------------------------------
    // 0.8.5 - Mehrere Routen statt "die lauteste gewinnt" (Rueckmeldung eines WTT-Entwicklers zur Mehrwege-Demo: an Uebergaengen
    // springt es, wenn die Auswahl hart den Gewinner wechselt oder mehrere Wege gleichzeitig tauschen). Logik wie die Demo
    // (mpGewichte/mpHalten/mpSummen):
    //  - Jede Route hat eine stabile ID aus ihrer Portalfolge (und dem Raumpaar).
    //  - Weiches Rang-Gewicht: Routen nach Pegel sortiert, Bezug r = Mitte zwischen der N-ten und der (N+1)-ten Route,
    //    w = smoothstep(0,5 + (L - r) / Breite); dazu weich raus, was mehr als 46 dB unter der lautesten liegt. Bei hoechstens
    //    N Routen zaehlen alle voll. Breite 0 = harte Top-N-Auswahl.
    //  - Zeitliches Halten: das Gewicht laeuft linear in 'Blendzeit' auf sein Ziel (neu: von 0 herauf; verschwunden: mit dem
    //    letzten Beitrag auf 0 herunter). Springt eine Route mit gleicher ID um mehr als 3 dB in einer Rechnung, wird der alte
    //    Stand ein "Geist", der ausblendet, und der neue blendet ein (in Energie = lineare Ueberblendung).
    //  - Summe: E_b = Sum w * 10^(-Daemmung_b / 10), je Band (Bezug / 1 kHz / 4 kHz). Erste Rechnung eines Rechners: Gewichte
    //    sofort am Ziel (ein neuer Klang blendet nicht ein).
    // Reine Rechnung (Offline-Test), Spiel-Teil: Raumweg.cs.
    // ------------------------------------------------------------------------------------------

    internal sealed class RoutenEintrag
    {
        public ulong Id;
        public bool Geist, Da;
        public double W, Ziel;              // Blendgewicht (aktuell) und Rang-Gewicht (Ziel)
        /// <summary>raid2242richtung (17.11.2026): Anwesenheit der Route OHNE Rang-Gewicht (0..1, blendet wie W: 1 = in der letzten Rechnung
        /// geliefert, 0 = verschwunden/Geist). Das Rang-Gewicht ist eine Auswahl fuer die stetige Pegelsumme (N = 3 lauteste), keine Physik;
        /// fuer die Praezedenz (wer kommt zuerst an) zaehlt die volle Energie jeder gelieferten Route (RichtungKern.Gewicht).</summary>
        public double Anwesend = 1;   // ohne Mischer angelegt (Tests): anwesend
        public int Rang;                     // 1 = lauteste der letzten Rechnung, 0 = nicht mehr geliefert
        public readonly double[] Db = new double[3];   // Daemmung je Band (>= 0)
        public V3 Austritt;                  // letzter Portalpunkt vor dem Hoerer
        public V3 Vor;                       // tabellenneu: Wegpunkt davor (Erg.Vor; IstNull = keiner)
        public RaumwegErgebnis Erg;
        public int N;
        public readonly int[] Portale = new int[16];
        public double Energie0 => Math.Pow(10, -Db[0] / 10);
    }

    internal sealed class RoutenMischer
    {
        struct Kand
        {
            public ulong Id; public double D0, D1, D2; public V3 Aus; public RaumwegErgebnis Erg; public int Start, N; public double Ziel; public int Rang;
        }

        public const double GeistDb = 3.0;
        public const double TiefCut = 46.0, TiefBreite = 6.0;
        public readonly System.Collections.Generic.List<RoutenEintrag> Eintraege = new System.Collections.Generic.List<RoutenEintrag>();
        readonly System.Collections.Generic.Stack<RoutenEintrag> frei = new System.Collections.Generic.Stack<RoutenEintrag>();
        Kand[] kand = new Kand[32];
        int[] kandPortale = new int[256];
        int kandN, kandP;
        readonly System.Collections.Generic.HashSet<ulong> ids = new System.Collections.Generic.HashSet<ulong>();
        double tLetzt = double.NaN;
        bool frisch = true;
        // Ergebnis der letzten Rechnung (Diagnose/Log)
        public double GewinnerDb = double.NaN;     // lauteste Route der letzten Rechnung (alte Auswahl)
        public V3 GewinnerAustritt;
        public int Geister, Neue;                   // seit dem letzten Abholen
        public bool Laeuft;                         // mindestens ein Gewicht ist noch nicht am Ziel
        public bool Hat => Eintraege.Count > 0;

        public void Leeren()
        {
            foreach (var e in Eintraege) frei.Push(e);
            Eintraege.Clear(); kandN = kandP = 0; frisch = true; tLetzt = double.NaN; Laeuft = false; GewinnerDb = double.NaN;
        }

        public void Beginn() { kandN = 0; kandP = 0; ids.Clear(); }

        /// <summary>Eine gerechnete Route melden (Daemmung je Band >= 0). Doppelte IDs zaehlen einmal (die erste).</summary>
        public void Kandidat(ulong id, double db0, double db1, double db2, V3 austritt, in RaumwegErgebnis erg, int[] portale, int n)
            => Kandidat(id, db0, db1, db2, austritt, erg, portale, n, 0);

        /// <summary>Wie oben, Portalfolge ab portale[start] (0.8.5: gespeicherte Kandidaten aus RaumwegNetz).</summary>
        public void Kandidat(ulong id, double db0, double db1, double db2, V3 austritt, in RaumwegErgebnis erg, int[] portale, int n, int start)
        {
            if (!ids.Add(id)) return;
            if (kandN == kand.Length) Array.Resize(ref kand, kand.Length * 2);
            n = Math.Min(n, 16);
            if (kandP + n > kandPortale.Length) Array.Resize(ref kandPortale, Math.Max(kandPortale.Length * 2, kandP + n));
            if (portale != null) Array.Copy(portale, start, kandPortale, kandP, n);
            kand[kandN++] = new Kand { Id = id, D0 = db0, D1 = db1, D2 = db2, Aus = austritt, Erg = erg, Start = kandP, N = n };
            kandP += n;
        }

        static double Glatt(double x) => x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x);

        /// <summary>Weiche Rang-Gewichte der gemeldeten Routen (Demo mpGewichte). Pegel L = -Daemmung.</summary>
        void Gewichte(int rangN, double breiteDb)
        {
            // nach Pegel absteigend (= Daemmung aufsteigend); wenige Routen -> Einfuegesortierung reicht
            for (int i = 1; i < kandN; i++)
            {
                var k = kand[i]; int j = i - 1;
                while (j >= 0 && kand[j].D0 > k.D0) { kand[j + 1] = kand[j]; j--; }
                kand[j + 1] = k;
            }
            if (kandN == 0) return;
            double lmax = -kand[0].D0;
            double r = kandN > rangN && rangN > 0 ? (-kand[rangN - 1].D0 - kand[rangN].D0) / 2 : double.NegativeInfinity;
            for (int i = 0; i < kandN; i++)
            {
                double l = -kand[i].D0, w;
                if (breiteDb <= 0) w = rangN <= 0 || i < rangN ? 1 : 0;
                else w = double.IsNegativeInfinity(r) ? 1 : Glatt(0.5 + (l - r) / breiteDb);
                if (breiteDb > 0) w *= Glatt((l - (lmax - TiefCut)) / TiefBreite);
                kand[i].Ziel = w; kand[i].Rang = i + 1;
            }
        }

        /// <summary>0.8.6: fuehrt der Mischer diese Route gerade (auch beim Ausblenden)? Dann wird sie immer exakt gerechnet.</summary>
        public bool Fuehrt(ulong id) => Finde(id) != null;

        RoutenEintrag Finde(ulong id)
        {
            for (int i = 0; i < Eintraege.Count; i++) { var e = Eintraege[i]; if (!e.Geist && e.Id == id) return e; }
            return null;
        }

        /// <summary>Nach einer Rechnung: Gewichte, Geister, neue Routen; danach bis 'jetzt' fortschreiben.</summary>
        public void Uebernehmen(double jetzt, double blendS, int rangN, double breiteDb)
        {
            // laufende Blenden bis jetzt fertigschreiben; die neuen Ziele blenden erst ab jetzt (auch nach langer Pause ohne Rechnung)
            if (!frisch) Fortschreiben(jetzt, blendS);
            Gewichte(rangN, breiteDb);
            GewinnerDb = kandN > 0 ? kand[0].D0 : double.NaN;
            if (kandN > 0) GewinnerAustritt = kand[0].Aus;
            for (int i = 0; i < Eintraege.Count; i++) Eintraege[i].Da = false;
            bool start = frisch || blendS <= 0;
            for (int i = 0; i < kandN; i++)
            {
                ref Kand k = ref kand[i];
                var e = Finde(k.Id);
                if (e != null && !start && Math.Abs(e.Db[0] - k.D0) > GeistDb)
                {
                    e.Geist = true; e.Ziel = 0; e.Rang = 0; Geister++;
                    e = null;
                }
                if (e == null)
                {
                    if (k.Ziel <= 1e-4) continue;
                    e = frei.Count > 0 ? frei.Pop() : new RoutenEintrag();
                    e.Id = k.Id; e.Geist = false; e.W = start ? k.Ziel : 0; e.Anwesend = start ? 1 : 0;
                    Eintraege.Add(e);
                    if (!start) Neue++;
                }
                e.Da = true; e.Ziel = k.Ziel; e.Rang = k.Rang;
                e.Db[0] = k.D0; e.Db[1] = k.D1; e.Db[2] = k.D2; e.Austritt = k.Aus; e.Erg = k.Erg; e.Vor = k.Erg.Vor;
                e.N = k.N; Array.Copy(kandPortale, k.Start, e.Portale, 0, k.N);
            }
            for (int i = 0; i < Eintraege.Count; i++) { var e = Eintraege[i]; if (!e.Da) { e.Ziel = 0; e.Rang = 0; } }
            tLetzt = jetzt;
            frisch = false;
            Fortschreiben(jetzt, blendS);     // dt = 0: nur Aufraeumen und Laeuft bestimmen (bei Blendzeit 0: sofort am Ziel)
        }

        /// <summary>Gewichte linear in blendS Sekunden aufs Ziel fuehren (jedes Bild). Ausgeblendete Eintraege fallen weg.</summary>
        public void Fortschreiben(double jetzt, double blendS)
        {
            double dt = double.IsNaN(tLetzt) ? 0 : Math.Max(0, jetzt - tLetzt);
            tLetzt = jetzt;
            double schritt = blendS > 0 ? dt / blendS : double.PositiveInfinity;
            bool laeuft = false;
            for (int i = Eintraege.Count - 1; i >= 0; i--)
            {
                var e = Eintraege[i];
                e.W = e.W < e.Ziel ? Math.Min(e.Ziel, e.W + schritt) : Math.Max(e.Ziel, e.W - schritt);
                double pz = e.Da && !e.Geist ? 1.0 : 0.0;   // raid2242richtung: Anwesenheit ohne Rang (gleiche Blende)
                e.Anwesend = e.Anwesend < pz ? Math.Min(pz, e.Anwesend + schritt) : Math.Max(pz, e.Anwesend - schritt);
                if (e.Ziel <= 1e-9 && e.W <= 1e-4) { Eintraege.RemoveAt(i); frei.Push(e); continue; }
                if (Math.Abs(e.W - e.Ziel) > 1e-4) laeuft = true;
            }
            Laeuft = laeuft;
        }

        /// <summary>Energiesumme je Band als Daemmung (>= 0, hoechstens maxDb). richtung = energiegewichteter Mittelwert der
        /// Austrittspunkte (Bezugsband). false = keine Route traegt bei.</summary>
        public bool Summe(double maxDb, double[] db, out V3 austritt, out double gewicht)
        {
            double e0 = 0, e1 = 0, e2 = 0, sw = 0; V3 a = default;
            foreach (var e in Eintraege)
            {
                if (e.W <= 0) continue;
                double x0 = e.W * Math.Pow(10, -e.Db[0] / 10);
                e0 += x0; e1 += e.W * Math.Pow(10, -e.Db[1] / 10); e2 += e.W * Math.Pow(10, -e.Db[2] / 10);
                a = a + e.Austritt * x0; sw += x0;
            }
            gewicht = sw;
            austritt = sw > 0 ? a * (1.0 / sw) : default;
            db[0] = DbAus(e0, maxDb); db[1] = DbAus(e1, maxDb); db[2] = DbAus(e2, maxDb);
            return e0 > 0;
        }

        /// <summary>0.8.6 Richtung: wie Summe, der Austritt aber als energiegewichtetes Mittel der RICHTUNGEN vom Hoerer h zu den
        /// Austrittspunkten (Intensitaetsvektor), nicht der Punkte selbst. Das Mittel zweier Punkte links und rechts liegt sonst
        /// zwischen ihnen - vor oder hinter dem Hoerer, wo keine Oeffnung ist (Log 09.11.: 3 % der Klaenge > 90 Grad neben der
        /// lautesten Oeffnung). austritt = h + mittlere Richtung * mittlerer Abstand; buendel = |Summe w e u| / Summe w e (1 = eine
        /// Richtung, 0 = gleich stark aus entgegengesetzten Richtungen, dann ist die Richtung unbestimmt).</summary>
        public bool Summe(double maxDb, double[] db, V3 h, out V3 austritt, out double buendel)
        {
            double e0 = 0, e1 = 0, e2 = 0, sw = 0, sl = 0; V3 u = default;
            foreach (var e in Eintraege)
            {
                if (e.W <= 0) continue;
                double x0 = e.W * Math.Pow(10, -e.Db[0] / 10);
                e0 += x0; e1 += e.W * Math.Pow(10, -e.Db[1] / 10); e2 += e.W * Math.Pow(10, -e.Db[2] / 10);
                V3 r = e.Austritt - h; double l = r.Laenge;
                if (l > 1e-6) { u = u + r * (x0 / l); sl += x0 * l; sw += x0; }
            }
            double lu = u.Laenge;
            buendel = sw > 0 ? Math.Min(1, lu / sw) : 0;
            austritt = sw > 0 && lu > 1e-12 ? h + u * (sl / sw / lu) : default;
            db[0] = DbAus(e0, maxDb); db[1] = DbAus(e1, maxDb); db[2] = DbAus(e2, maxDb);
            return e0 > 0;
        }

        /// <summary>lampenpegel (18.11.): um wie viel dB die Energiesumme der gemischten Routen (Bezugsband, Gewicht W wie Summe) OHNE die
        /// Kappung bei 0 dB lauter waere als die gespielte Summe (DbAus kappt sie ebenfalls bei 0). Je Route zaehlt Db[0] - Erg.UeberFrei
        /// (eine spaetere Zusatzdaempfung, z. B. Abschirmung im Freien, steckt in Db[0] und wirkt so auf beide gleich). &gt;= 0.</summary>
        public double UeberFreiDb()
        {
            double ec = 0, er = 0;
            foreach (var e in Eintraege)
            {
                if (e.W <= 0) continue;
                double x = e.W * Math.Pow(10, -e.Db[0] / 10);
                ec += x;
                er += x * Math.Pow(10, Math.Max(0, e.Erg.UeberFrei) / 10);
            }
            if (!(ec > 0) || !(er > 0)) return 0;
            // gespielt: DbAus(ec) = max(0, -10 lg ec) (oben nicht relevant: Lampen-Routen liegen nie am Deckel, und dort zaehlt es nicht)
            double gespielt = Math.Max(0, -10 * Math.Log10(ec));
            double u = 10 * Math.Log10(er) + gespielt;
            return u > 0 && !double.IsInfinity(u) ? u : 0;
        }

        /// <summary>diffus-hall: energiegewichteter Diffusanteil (Bezugsband) der aktuell gemischten Routen (Gewicht W wie Summe).
        /// Jede Route bringt ihren Anteil aus der Rechnung mit (RaumwegErgebnis.AnteilDiffus; Wegsuche: Raumfolge 1, Kantenweg 0).</summary>
        public double DiffusAnteil() => DiffusAnteil(0);

        /// <summary>hallsend (B2, 12.11.): wie DiffusAnteil, im Band b mit den Energien dieses Bands (Gewicht W, Db[b]).</summary>
        public double DiffusAnteil(int b)
        {
            double s = 0, sd = 0;
            foreach (var e in Eintraege)
            {
                if (e.W <= 0) continue;
                double x = e.W * Math.Pow(10, -e.Db[b] / 10);
                double a = e.Erg.AnteilBand(b);
                if (!(a > 0)) a = 0; else if (a > 1) a = 1;
                s += x; sd += x * a;
            }
            return s > 0 ? sd / s : 0;
        }

        /// <summary>tunneldiffus (17.11.): Zeitstruktur der gemischten Routen im Band b (Gewicht W x Energie dieses Bands wie DiffusAnteil):
        /// langsamer und schneller Teil als Anteile an der Summe, Momente energiegewichtet (Mischung von Verteilungen: Mittel und zweites
        /// Moment mischen linear). Routen ohne Zeitstruktur (Wegsuche, alte Aufrufer) zaehlen als unverschmiert (Anteil 0).</summary>
        public void ZeitMischen(int b, out ZeitKern.Teil langsam, out ZeitKern.Teil schnell)
        {
            double s = 0;
            var l = new ZeitKern.Summe(); var f = new ZeitKern.Summe();
            foreach (var e in Eintraege)
            {
                if (e.W <= 0) continue;
                double x = e.W * Math.Pow(10, -e.Db[b] / 10);
                if (!(x > 0)) continue;
                s += x;
                if (!e.Erg.Zeit.Hat) continue;
                var tl = e.Erg.Zeit.Langsam(b); var tf = e.Erg.Zeit.Schnell(b);
                if (tl.E > 0) { l.E += x * tl.E; l.M1 += x * tl.E * (double)tl.M1; l.M2 += x * tl.E * (double)tl.M2; }
                if (tf.E > 0) { f.E += x * tf.E; f.M1 += x * tf.E * (double)tf.M1; f.M2 += x * tf.E * (double)tf.M2; }
            }
            langsam = ZeitKern.Normiert(l, s); schnell = ZeitKern.Normiert(f, s);
        }

        /// <summary>tunneldiffus Teil 2: frueher Anteil (Sprache, 1 kHz) der gemischten Routen, energiegewichtet (Gewicht W x Energie bei 1 kHz).
        /// Erst hier je beitragender Route (W &gt; 0) aus Raumkette + Teilen gerechnet (KetteSprache.Frueh). Routen ohne Angabe zaehlen ganz als
        /// frueh. false = keine Route mit Angabe.</summary>
        public bool FruehMischen(out double frueh)
        {
            double s = 0, sf = 0; bool hat = false;
            foreach (var e in Eintraege)
            {
                if (e.W <= 0) continue;
                double x = e.W * Math.Pow(10, -e.Db[1] / 10);
                if (!(x > 0)) continue;
                s += x;
                if (e.Erg.Sprache.Hat) { sf += x * e.Erg.Sprache.Frueh(ZeitKern.SprachFenster); hat = true; } else sf += x;
            }
            frueh = s > 0 ? sf / s : 1.0;
            return hat;
        }

        static double DbAus(double e, double maxDb) => e <= 0 ? maxDb : Math.Max(0, Math.Min(maxDb, -10 * Math.Log10(e)));

        /// <summary>Winkel (Grad) zwischen den Richtungen Hoerer -> a und Hoerer -> b.</summary>
        public static double Winkel(V3 h, V3 a, V3 b)
        {
            V3 u = a - h, v = b - h;
            double lu = u.Laenge, lv = v.Laenge;
            if (lu < 1e-6 || lv < 1e-6) return 0;
            double c = Math.Max(-1, Math.Min(1, V3.Dot(u, v) / (lu * lv)));
            return Math.Acos(c) * 180.0 / Math.PI;
        }

        /// <summary>Stabile Routen-ID: FNV-1a (64 bit) ueber Raumpaar und Portalfolge (Job-Richtung).</summary>
        public static ulong Id(uint paar, int[] portale, int n)
        {
            ulong h = 14695981039346656037UL;
            unchecked
            {
                h = (h ^ paar) * 1099511628211UL;
                for (int i = 0; i < n; i++) h = (h ^ (uint)portale[i]) * 1099511628211UL;
                h = (h ^ (uint)n) * 1099511628211UL;
            }
            return h;
        }
    }

    // ------------------------------------------------------------------------------------------
    // 0.8.5 - Hoerbarkeit leiser Quellen (Meldung Interchange 08.11.2026: "in der Realitaet wuerde man ihn da oben nicht laufen
    // und atmen hoeren"). Der Lautheitsfaktor (Physik-Modus, Standard 0,2, bei Kaan 0,5) staucht JEDE Daemmung: 60 dB -> 30 dB.
    // Begruendung des Faktors: ein Schuss hat real ~150-160 dB, Kopfhoerer spielen ~90 dB - der echte Pegelumfang passt nicht.
    // Fuer einen Schuss stimmt das. Fuer Atem, Schritte, Ausruestung, Stimme ist es umgekehrt: sie sind real leise (60-72 dB(A)
    // in 1 m) und liegen hinter wenigen dB Daemmung schon unter dem Grundgeraeusch. Die Stauchung macht sie hoerbar, obwohl es
    // physikalisch nichts zu hoeren gibt.
    // Loesung: gestaucht wird nur der Teil der Daemmung, bis zu dem die Quelle real noch ueber dem Grundgeraeusch liegt
    // ("Knie"); jedes dB darueber zaehlt voll, und ueber 10 dB unter dem Grundgeraeusch (Mithoerschwelle) blendet sie ganz aus.
    //   Knie = L_Quelle(1 m) - 20 lg(d) - L_Grund (>= 0)
    //   D_Spiel = Lautheit * min(D, Knie) + max(0, D - Knie) + Verdeckung(D - Knie)   (0.8.6; bis 0.8.5: Max. insulation * smoothstep((D - Knie) / 10 dB))
    // Lautes (Schuesse, Explosionen, Einschlaege) hat kein Knie (Tabelle NaN): keine Bandrechnung; die Stauchung macht seit 0.8.6 die
    // Kennlinie (Kennlinie/Gestaucht unten) aus dem realen Pegel, nicht mehr der Lautheitsfaktor allein.
    // Quellpegel (Schaetzwerte, dB(A) in 1 m): Stimme 72 = "laut" nach ISO 9921:2003 Tab. 1 (normal 60, angehoben 66, laut 72,
    // sehr laut 78; Bots rufen meist); Schritte/Ausruestung 65 (Gehen mit Stiefeln auf hartem Boden ~55-70 dB(A), Messwerte der
    // Trittschall-Literatur); Waffenhandhabung 70, Zusammenstoesse 75, Tueren/Schalter 70 (Annahmen). Grundgeraeusch 30 dB(A) =
    // ruhiger Innenraum (Einstellung; Wohnraum nachts 25-30, ruhiges Buero 35-40 dB(A)).
    // ------------------------------------------------------------------------------------------
    internal static class Hoerbarkeit
    {
        // Reihenfolge BetterAudio.AudioSourceGroupType: Gunshots, Weaponry, Impacts, Character, Environment, Collisions, Speech,
        // Distant, NonspatialBypass, Nonspatial, Voip, Grenades, Windows, VehicleInSpeech, Lamp, InteractiveObjects,
        // OutEnvironment, PropagatedExclusive, LightOcclusion
        public static readonly double[] QuellPegel =
        {
            double.NaN, 70, double.NaN, 65, double.NaN, 75, 72,
            double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, 70,
            double.NaN, double.NaN, double.NaN,
        };

        // 10.11. (akustik-wand, Psychoakustik B1): QuellPegel ist der Pegel des BEZUGSKLANGS der Gruppe. Character = Rennen auf hartem
        // Boden, 65 dB(A) LAFmax in 1 m (KalibrierKern.GangSoll[1]). Die Clips der Gruppe streuen um 26 dB (LAFmax-Median aus ClipPegelDaten:
        // Ausruestung -47,5, Gehen -35,5, Rennen -27,6, Sprint -21,1 dBFS(A)); bis 10.11. galt fuer jeden Schritt 65 dB(A). Jetzt:
        //   L_Quelle(1 m) = QuellPegel + (LAFmax_Clip - LAFmax_Bezug)      (Bezug = Median der Renn-Clips, KalibrierKern.BezugRennen)
        // Das setzt voraus, dass das Spiel die Clips einer Gruppe im selben Massstab mischt (gleiche Grundlautstaerke der Quelle) - die
        // Lautstaerke eines einzelnen PlayOneShot (volume * balance) ist im Spiel nicht abgelegt und fehlt (BetterSource/SimpleSource.Play).
        // Andere Gruppen: kein gemessener Anker -> 0 (Gruppenwert wie bisher).

        /// <summary>Pegelkorrektur (dB) des gespielten Clips gegenueber dem Bezugsklang der Gruppe; 0, wenn unbekannt.</summary>
        public static double ClipKorrektur(int typ, double lafmaxClip, double lafmaxBezug)
        {
            if (typ != 3 || double.IsNaN(lafmaxClip) || double.IsNaN(lafmaxBezug)) return 0;
            return lafmaxClip - lafmaxBezug;
        }

        /// <summary>Knie (dB) fuer Gruppe 'typ' in Abstand d bei Grundgeraeusch lGrund; NaN = kein Knie (alles gestaucht).</summary>
        public static double Knie(int typ, double d, double lGrund)
        {
            if (typ < 0 || typ >= QuellPegel.Length || double.IsNaN(QuellPegel[typ])) return double.NaN;
            return Math.Max(0, QuellPegel[typ] - 20 * Math.Log10(Math.Max(d, 1.0)) - lGrund);
        }

        /// <summary>Breite (dB) unter dem Grundgeraeusch, ueber die eine Quelle ganz verdeckt wird: ein Signal 10 dB unter einem
        /// breitbandigen Verdecker ist nicht mehr wahrnehmbar (Mithoerschwelle, Zwicker/Fastl "Psychoacoustics" Kap. 4; Sprache
        /// ab etwa -10 bis -15 dB Stoerabstand unverstaendlich/unhoerbar).</summary>
        public const double MaskBreite = 10.0;

        /// <summary>Daemmung, die das Spiel anwendet: gestaucht bis zum Knie, darueber voll und ueber MaskBreite zusaetzlich bis
        /// 'stumm' (maxDb) ausgeblendet - im Spiel gibt es kein Grundgeraeusch, das die Quelle verdecken wuerde.</summary>
        public static double Daempfung(double d, double lautheit, double knie, double maxDb = 60)
        {
            if (d <= 0) return 0;
            if (double.IsNaN(knie)) return d * lautheit;
            double ueber = Math.Max(0, d - knie);
            return lautheit * Math.Min(d, knie) + ueber + Verdeckung(ueber, maxDb);
        }

        // ------------------------------------------------------------------------------------------
        // 0.8.6 Kennlinie (Psychoakustik-Pruefung 10.11.2026, E1; ersetzt den nie gebauten "Fix 2"). Ursache: Physik.LautstaerkeKern
        // rechnete db = Max. insulation * Wertung * Lautheit - die Daemmung im echten dB-Mass, die Entfernung aber macht SEBs gestauchte
        // Kurve. Ein Schuss liegt real 70-75 dB ueber einem Schritt, im Spiel 20-30 dB. Eine Tuer, die real 23 dB nimmt, nahm dem Schuss
        // im Spiel ebenfalls 23 dB - gemessen am gestauchten Spielpegel viel zu viel ("ferne Gefechte unhoerbar", Kaans Uebergang
        // Lautheit 0,42). Der Lautheitsfaktor staucht dagegen auch Schritte und Stimmen, die real gar nicht laut sind.
        //
        // Herleitung: Die Wiedergabe bildet den realen Pegel am Ohr L auf einen gespielten Pegel K(L) ab (Kennlinie). Real gilt
        //    L_frei = L_Quelle(1 m) - 20 lg d            (Pegel am Hoerer ohne Hindernis, dB(A); d <= 1 m zaehlt als 1 m)
        //    L_Ohr  = L_frei - D                         (D = reale Daemmung des Wegs, Massengesetz/Beugung/Raumweg)
        // Das Spiel (SEB) spielt den Klang ohne Hindernis mit SEINER Abstandskurve, also schon als K(L_frei) - die Mod liefert nur den
        // ZUSAETZLICHEN Abzug, den das Hindernis im gespielten Mass macht:
        //    Abzug = K(L_frei) - K(L_frei - D)
        // K hat Steigung 1 unterhalb des Knies L_K (der Spiel-Mix bildet leise und mittlere Pegel 1:1 ab: Kalibrierung der Schritte
        // K = -100,4 dB, Gutachten Kalibrierung 10.11.: Taxi 59, Feuer 59, Generator 80 dB(A) werden etwa real gespielt) und Steigung s
        // darueber. Liegen L_frei und L_frei - D unter dem Knie, ist der Abzug exakt D (Lautheit 1 = heutiges Verhalten); liegt die
        // Quelle ueber dem Knie, zaehlt der Teil von D, der im gestauchten Bereich liegt, nur mit s:
        //    Abzug = s * min(D, k) + max(0, D - k),   k = max(0, L_frei - L_K)        (hartes Knie)
        // Beispiel (Bericht E1, Gewehr 140 dB(A), 80 m, L_K 85, s 0,25): L_frei 101,9 -> Tuer offen 6 dB -> 1,5; Tuer zu 23 dB -> 10,3;
        // Beton 60 dB -> 47,3 dB. Die Reihenfolge des echten Pegels bleibt erhalten, ein Schuss hinter Beton ist real 42 dB(A) und
        // spielt entsprechend leise.
        //
        // Weiches Knie (Giannoulis/Massberg/Reiss, "Digital Dynamic Range Compressor Design", J. Audio Eng. Soc. 60 (2012) 399, Gl. 4):
        //    K(L) = L                                          fuer L - L_K <= -W/2
        //    K(L) = L + (s - 1) (L - L_K + W/2)^2 / (2 W)      fuer |L - L_K| < W/2
        //    K(L) = L_K + s (L - L_K)                          fuer L - L_K >= W/2
        // Wert und Steigung sind stetig (Steigung waechst linear von s auf 1), K ist streng monoton fuer s > 0. Ein Pegel, der beim
        // Gehen ueber das Knie wandert, aendert seine Stauchung damit um hoechstens (1 - s) / W je dB - kein hoerbarer Knick.
        // W = 10 dB: uebliche Kniebreite von Kompressoren (6-12 dB); ausserhalb des Knies gilt exakt die Formel mit hartem Knie,
        // die Zahlen aus E1 bleiben also gleich, solange beide Pegel mehr als 5 dB vom Knie entfernt sind.
        //
        // Wahl der Werte (Standard L_K 85 dB(A), s 0,2 - Einstellungen "Compression knee" / "Compression above knee"):
        //  - s aus dem Spiel-Mix: die Nah-Schussclips des Spiels (clip-pegel.tsv, LAFmax: svd_fire_close -7,9, toz_fire_close1/2 -7,2/-6,6,
        //    *_fire_indoor_close -4,8/-5,2 dBFS(A)) spielen mit K = -100,4 wie 93-96 dB(A) in 1 m; real hat ein Gewehr ~140 dB(A).
        //    K(140) = 85 + s * 55 = 93,4 ... 95,6  ->  s = 0,15 (draussen) ... 0,19 (drinnen). Gegenprobe Einschlaege (concrete1-5,
        //    LAFmax Median -14,6, laut -10,9 dBFS(A) -> 86-90 dB(A) gespielt) mit real ~100-110 dB(A): s = 0,1-0,25. SEBs eigene Nahkurve
        //    staucht die Entfernung mit 0,15-0,25 (2-20 m, seb_kurven.py). s = 0,2 liegt in allen drei Bereichen (und dort, wo das alte
        //    "Lautheit 0,2 nach Gehoer" lag).
        //  - L_K: bis 80 dB(A) spielt das Spiel nach der Kalibrierung real; und der lauteste Schuss muss unter der Vollaussteuerung bleiben
        //    (0 dBFS(A) = 100,4 dB(A)): L_K + s (140 - L_K) <= ~96 -> L_K <= 86 bei s = 0,2. Also 85.
        //  UNSICHER: die SEB-Bank selbst ist nicht gemessen (nur die Spiel-Clips), Gruppenlautstaerke des Mixers nicht nachgemessen
        //  (+-3 dB auf den gespielten Pegel -> s +-0,05), und der reale Schusspegel ist eine Literaturschaetzung (+-5 dB -> s +-0,02).
        // ------------------------------------------------------------------------------------------

        /// <summary>Breite des weichen Knies der Kennlinie (dB).</summary>
        public const double KennBreite = 10.0;

        /// <summary>Kennlinie: gespielter Pegel zum realen Pegel l (dB(A)) mit Knie 'knie' (dB(A)) und Steigung 'steigung' darueber.
        /// knie NaN oder steigung &gt;= 1: keine Stauchung (K(l) = l). Steigung unter 0,01 zaehlt als 0,01 (streng monoton).</summary>
        public static double Kennlinie(double l, double knie, double steigung)
        {
            if (double.IsNaN(knie) || !(steigung < 1)) return l;
            double s = Math.Max(0.01, steigung), h = KennBreite / 2, x = l - knie;
            if (x <= -h) return l;
            if (x >= h) return knie + s * x;
            double u = x + h;
            return l + (s - 1) * u * u / (2 * KennBreite);
        }

        /// <summary>Abzug (dB) im gespielten Mass, den die reale Daemmung d einer Quelle mit realem Pegel lFrei am Hoerer (ohne Hindernis)
        /// macht: K(lFrei) - K(lFrei - d). Unter dem Knie exakt d. lFrei NaN (Pegel unbekannt): d. d &lt;= 0: 0.</summary>
        public static double Kompression(double lFrei, double d, double knie, double steigung)
        {
            if (!(d > 0)) return 0;
            if (double.IsNaN(lFrei) || double.IsNaN(knie) || !(steigung < 1) || lFrei <= knie - KennBreite / 2) return d;
            return Kennlinie(lFrei, knie, steigung) - Kennlinie(lFrei - d, knie, steigung);
        }

        /// <summary>Realer Pegel am Hoerer ohne Hindernis: l1 (dB(A) in 1 m) - 20 lg d, d &lt;= 1 m (und NaN) zaehlen als 1 m.</summary>
        public static double FreiPegel(double l1, double abstand) => l1 - 20 * Math.Log10(abstand > 1 ? abstand : 1);

        // Pegel der lauten Gruppen (ohne Hoerbarkeitsrechnung) fuer die Kennlinie, dB(A) LAFmax in 1 m, Freifeld. SCHAETZWERTE:
        //  - Schuss 140: Gewehr-Muendungsknall, Spitzenpegel 155-165 dB in 1 m seitlich (NIOSH-Messungen an Schuetzen); die
        //    F-Zeitbewertung (125 ms) eines 1-3 ms langen Knalls nimmt 20-30 dB weg -> 130-140 dB(A). Obere Grenze, weil die Spielwaffen
        //    ueberwiegend Gewehre sind und der Bericht mit 140 rechnet. Pistolen/kleine Kaliber liegen 5-10 dB darunter (Gruppe kennt
        //    die Waffe nicht).
        //  - Schuss mit Schalldaempfer 120: Schalldaempfer senken den Muendungsknall um 20-35 dB (Spitze); 20 dB auf LAFmax (Annahme).
        //  - Einschlag 105: Geschoss trifft Beton/Stahl, Annahme 100-110 dB(A) in 1 m (passt zur Gegenprobe mit s 0,2, siehe oben).
        //  - Explosion 160: Handgranate (~180 g TNT) 4 kPa (166 dB Spitze) in 15 m nach Kingery-Bulmash; F- und A-Bewertung des tieffrequenten
        //    Stosses -25 dB -> ~140 dB(A) in 15 m = 164 als Punktquelle in 1 m, abgerundet. Sehr unsicher (+-10 dB), liegt aber immer weit
        //    im gestauchten Bereich (der Fehler verschiebt nur den Punkt, ab dem D voll zaehlt).
        // Alle anderen lauten/unbekannten Gruppen (Environment, Distant, ...): kein Pegel -> Abzug = D x Lautheit wie bisher.
        public const double PegelSchuss = 140, PegelSchussGedaempft = 120, PegelEinschlag = 105, PegelExplosion = 160;

        /// <summary>Pegel (dB(A) in 1 m) der Gruppe 'typ' fuer die Kennlinie: QuellPegel der leisen Gruppen, sonst die Schaetzwerte der
        /// lauten (Gunshots, Impacts, Grenades). NaN = unbekannt.</summary>
        public static double KennPegel(int typ, bool gedaempft)
        {
            if (typ >= 0 && typ < QuellPegel.Length && !double.IsNaN(QuellPegel[typ])) return QuellPegel[typ];
            switch (typ)
            {
                case 0: return gedaempft ? PegelSchussGedaempft : PegelSchuss;   // Gunshots
                case 2: return PegelEinschlag;                                   // Impacts
                case 11: return PegelExplosion;                                  // Grenades
                default: return double.NaN;
            }
        }

        /// <summary>Daemmung (dB), die das Spiel fuer eine Quelle OHNE Bandrechnung anwendet (laute Gruppen, oder "Quiet sources: real
        /// audibility" aus): lautheit x Kompression(L_frei, d). Pegel unbekannt: d x lautheit (bisheriges Verhalten).</summary>
        public static double Gestaucht(int typ, bool gedaempft, double abstand, double d, double lautheit, double knie, double steigung, double pegelKorr = 0)
        {
            double l1 = KennPegel(typ, gedaempft) + pegelKorr;   // 10.11. (B1): Pegel des gespielten Clips (ClipKorrektur)
            if (double.IsNaN(l1)) return d * lautheit;
            return lautheit * Kompression(FreiPegel(l1, abstand), d, knie, steigung);
        }

        // ------------------------------------------------------------------------------------------
        // 0.8.6 - Verdeckung ohne Klippe (Meldung Streets 09.11.2026 20:02 "Taxi hat einen harten Cut - genau wo ich den Marker setzte,
        // hoert es auf"). Bisher: Zusatz = Max. insulation * smoothstep(unter / 10 dB), also 60 dB Mehrdaempfung auf 10 dB - im Steilsten
        // 9 dB Lautstaerke je dB Pegel. Im Log 20:02:47 -> 20:02:48 (1,5 m gegangen): 1-kHz-Band +2,9 -> +7,8 dB ueber der
        // Mithoerschwelle, Lautstaerke -55,4 -> -10,5 dB. Physikalisch falsch: ein Signal knapp unter dem Grundgeraeusch wird nicht
        // 60 dB leiser, es wird (teil)verdeckt.
        //  - Teilverdeckung (Zwicker/Fastl, "Psychoacoustics", 3. Aufl. 2007, Abschn. 8.7.3; Moore/Glasberg/Baer 1997, J. Audio Eng.
        //    Soc. 45, Partial Loudness): die Lautheit eines Signals im Rauschen ist kleiner als in Ruhe und steigt nahe der
        //    Mithoerschwelle steiler mit dem Pegel (Lautheitsfunktion im Rauschen wie bei Recruitment: rund doppelte Steigung nahe
        //    der Schwelle, weit darueber gleich laut wie ohne Verdecker). In der Spielwiedergabe gibt es den Verdecker nicht (kein
        //    kalibriertes Grundgeraeusch) - die fehlende Teilverdeckung wird als Mehrdaempfung nachgebildet.
        //  - Nachbildung: ab dem Grundgeraeusch (unter = 0) waechst die Steigung stetig von 0 auf MaskSteigung dB je dB
        //    (quadratisch ueber MaskRampe, Steigung stetig), danach linear mit MaskSteigung, gedeckelt bei Max. insulation:
        //      V(u) = k u^2 / (2 R)        fuer 0 < u < R   (R = MaskRampe = 5 dB, k = MaskSteigung = 2)
        //      V(u) = k (u - R/2)          fuer u >= R      (an der Mithoerschwelle u = 10: 15 dB, 10 dB darunter 35 dB, ...)
        //    k = 2: ueber der Mithoerschwelle waechst die Lautheit im Rauschen rund doppelt so schnell wie in Ruhe - ein Signal X dB
        //    ueber der Mithoerschwelle klingt etwa wie eines X dB ueber der Ruhehoerschwelle -, die gleichwertige Lautstaerke also
        //    um 2 dB je dB; darunter (real unhoerbar) wird mit derselben Steigung weiter abgesenkt statt abgeschnitten.
        //    Zusammen mit dem physikalischen Pegel faellt die Lautstaerke dort mit 3 dB je dB Daemmung/Abstand - stetig und monoton,
        //    keine Schwelle; 1 dB Messunterschied aendert die Lautstaerke um hoechstens 3 dB (vorher bis 10 dB).
        // Gilt fuer Occluder-Quellen (Daempfung/Rechnen) und Quellen ohne Okklusion (QuellPegelKern.Rechnen) gleich.
        // ------------------------------------------------------------------------------------------
        /// <summary>Steigung der nachgebildeten Teilverdeckung unter dem Grundgeraeusch (dB Mehrdaempfung je dB), Bereich 1-2:
        /// Lautheitsfunktion im Rauschen nahe der Mithoerschwelle rund doppelt so steil wie in Ruhe (Zwicker/Fastl 8.7.3).</summary>
        public const double MaskSteigung = 2.0;
        /// <summary>Breite (dB), ueber die die Steigung von 0 auf MaskSteigung waechst (Steigung stetig, kein Knick am Grundgeraeusch).</summary>
        public const double MaskRampe = 5.0;

        /// <summary>Mehrdaempfung (dB) fuer ein Band 'unter' dB unter dem Grundgeraeusch (&lt;= 0: keine), gedeckelt bei maxDb.
        /// Stetig, monoton, Steigung stetig und hoechstens MaskSteigung.</summary>
        public static double Verdeckung(double unter, double maxDb)
        {
            if (unter <= 0 || maxDb <= 0) return 0;
            double r = MaskRampe, k = MaskSteigung;
            double v = unter < r ? k * unter * unter / (2 * r) : k * (unter - r / 2);
            return Math.Min(maxDb, v);
        }

        // ------------------------------------------------------------------------------------------
        // 0.8.5 - Hoerbarkeit JE BAND (Einwand Kaan 08.11.2026: "Wenn die hoerbar waeren, soll man's hoeren - auch wenn's nur das
        // dumpfe Tiefe ist"). Bisher wurde EIN Pegel (dB(A)) gegen EIN Grundgeraeusch gestellt, und zwar mit der Daemmung des
        // Bezugsbands. Wand und Grundgeraeusch sind aber frequenzabhaengig: das Massegesetz daemmt Tiefen am wenigsten, ein ruhiger
        // Raum ist dafuer tief am lautesten. Jetzt je Band 250 Hz / 1 kHz / 4 kHz (wie Stufe 1):
        //   L_b(d)  = Quellpegel_b - 20 lg d                          (Quellpegel_b aus dB(A) und Spektrum der Klanggruppe)
        //   N_b     = Grundgeraeusch je Band (NR-Kurve, auf den eingestellten dB(A)-Wert gelegt), mind. Hoerschwelle + 10 dB
        //   Knie_b  = L_b(d) - N_b,  ueber_b = Knie_b + 10 - D_b     (> 0 = Band liegt ueber der Mithoerschwelle)
        //   G_b     = Lautheit * D_0 + Neigung_b + Zusatz_b           (Zusatz_b = Daempfung(D_b, Knie_b) - Lautheit * D_b)
        // Lautstaerke = lautestes Band (min G_b), Tiefpass = Fit an G_b - G_0. Die Energiesumme der hoerbaren Baender entsteht dann im
        // Klang selbst: der Filter nimmt die Baender weg, die Lautstaerke haelt das lauteste. (Eine Energiesumme in der Lautstaerke
        // UND der Tiefpass wuerden die Hoehen doppelt abziehen.) Ohne Zusatz in allen Baendern = exakt das Verhalten davor.
        // Stumm erst, wenn alle Baender 10 dB unter ihrem Grundgeraeusch liegen. Alles ist stetig in D, Abstand und Grundgeraeusch
        // (0.8.6: Verdeckung mit hoechstens 2 dB je dB), es gibt keine Schwelle, die flackern koennte - Hysterese ist nicht noetig.
        // ------------------------------------------------------------------------------------------

        public const int NB = 3;
        /// <summary>A-Bewertung bei 250 Hz / 1 kHz / 4 kHz (IEC 61672-1:2013, Tab. E.1).</summary>
        public static readonly double[] ABew = { -8.6, 0.0, 1.0 };

        /// <summary>Spektrum je Klanggruppe: Oktavpegel 250 Hz / 1 kHz / 4 kHz relativ (dB). SCHAETZWERTE, keine Messung:
        /// Character (Schritte, Ausruestung, Atem) tieflastig - Fersenaufprall auf hartem Boden hat sein Maximum unter 500 Hz, darueber
        /// faellt es ab (Verlauf wie Trittschall-Spektren, EN ISO 10140-3 Normhammerwerk faellt nach oben, Schritte steiler);
        /// Stimme mittig - Verlauf des Langzeit-Sprachspektrums bei angehobener/lauter Stimme (ANSI S3.5-1997 Tab. 3: Maximum
        /// 500 Hz-1 kHz, 4 kHz rund 10 dB darunter), gerundet; Waffenhandhabung (Metallklicken) hochlastig; Zusammenstoesse
        /// breitbandig; Tueren/Schalter mittig.</summary>
        static readonly double[][] Spektrum =
        {
            null, new[] { -10.0, -3.0, 0.0 }, null, new[] { 0.0, -4.0, -10.0 }, null, new[] { 0.0, -2.0, -6.0 }, new[] { -2.0, 0.0, -9.0 },
            null, null, null, null, null, null, null, null, new[] { -3.0, 0.0, -4.0 },
            null, null, null,
        };

        /// <summary>Quellpegel (dB, unbewertet) im Band b in 1 m: Spektrum so verschoben, dass die A-bewertete Summe der drei
        /// Baender den dB(A)-Wert der Tabelle QuellPegel ergibt. NaN = laute Gruppe ohne Hoerbarkeitsrechnung.</summary>
        public static double BandPegel(int typ, int b)
        {
            if (typ < 0 || typ >= QuellPegel.Length || double.IsNaN(QuellPegel[typ]) || Spektrum[typ] == null) return double.NaN;
            var s = Spektrum[typ];
            double e = 0;
            for (int i = 0; i < NB; i++) e += Math.Pow(10, (s[i] + ABew[i]) / 10);
            return QuellPegel[typ] + s[b] - 10 * Math.Log10(e);
        }

        // NR-Kurven (ISO R1996-1971 / BS 8233: L = a + b * NR) fuer 63 Hz ... 8 kHz - Standardspektrum eines Innenraums, tief laut,
        // hoch leise. Die Einstellung "background noise (dB)" bleibt ein dB(A)-Gesamtpegel: gesucht wird das NR, dessen A-bewertete
        // Summe ueber alle 8 Oktaven ihn ergibt (30 dB(A) -> NR 19,8: 250 Hz 30,4 / 1 kHz 19,8 / 4 kHz 14,2 dB).
        static readonly double[] NrA = { 35.5, 22.0, 12.0, 4.8, 0.0, -3.5, -6.1, -8.0 };
        static readonly double[] NrB = { 0.790, 0.870, 0.930, 0.974, 1.000, 1.015, 1.025, 1.030 };
        static readonly double[] NrABew = { -26.2, -16.1, -8.6, -3.2, 0.0, 1.2, 1.0, -1.1 };
        static readonly int[] NrBand = { 2, 4, 6 };   // 250 Hz / 1 kHz / 4 kHz in der Oktavreihe
        /// <summary>Hoerschwelle im freien Schallfeld (ISO 389-7:2005) bei 250 Hz / 1 kHz / 4 kHz.</summary>
        public static readonly double[] Hoerschwelle = { 11.4, 2.4, -5.4 };

        static double NrDbA(double nr)
        {
            double e = 0;
            for (int i = 0; i < 8; i++) e += Math.Pow(10, (NrA[i] + NrB[i] * nr + NrABew[i]) / 10);
            return 10 * Math.Log10(e);
        }

        static double grundFuer = double.NaN;
        static readonly double[] grundBand = new double[NB];

        /// <summary>Grundgeraeusch je Band (dB) fuer den dB(A)-Gesamtpegel lGrund, mindestens Hoerschwelle + MaskBreite (dann ist die
        /// Mithoerschwelle die Hoerschwelle selbst). Ergebnis-Array wird wiederverwendet.</summary>
        public static double[] Grund(double lGrund)
        {
            if (lGrund == grundFuer) return grundBand;
            double lo = -40, hi = 100;
            for (int i = 0; i < 50; i++) { double m = (lo + hi) / 2; if (NrDbA(m) < lGrund) lo = m; else hi = m; }
            double nr = (lo + hi) / 2;
            for (int b = 0; b < NB; b++)
            {
                int o = NrBand[b];
                grundBand[b] = Math.Max(NrA[o] + NrB[o] * nr, Hoerschwelle[b] + MaskBreite);
            }
            grundFuer = lGrund;
            return grundBand;
        }

        static double rohFuer = double.NaN, rohNr;

        /// <summary>galpegel (10.11.): reales Grundgeraeusch im Band b (dB) fuer den dB(A)-Gesamtpegel lGrund OHNE die Untergrenze
        /// Hoerschwelle + MaskBreite (die haengt am Ohr, also an einer Verstaerkung zwischen Feld und Ohr: FremdPegelKern.Verdecker).</summary>
        public static double GrundRoh(double lGrund, int b)
        {
            if (lGrund != rohFuer)
            {
                double lo = -40, hi = 100;
                for (int i = 0; i < 50; i++) { double m = (lo + hi) / 2; if (NrDbA(m) < lGrund) lo = m; else hi = m; }
                rohNr = (lo + hi) / 2; rohFuer = lGrund;
            }
            int o = NrBand[b];
            return NrA[o] + NrB[o] * rohNr;
        }

        /// <summary>Ergebnis der Bandrechnung (wiederverwendbar, Hauptthread).</summary>
        public sealed class Baender
        {
            public bool Gilt;                                   // Gruppe hat eine Hoerbarkeitsrechnung
            public readonly double[] D = new double[NB];        // reale Daemmung je Band (dB)
            public readonly double[] Knie = new double[NB];     // Pegel ueber dem Grundgeraeusch ohne Wand (dB, >= 0)
            public readonly double[] Ueber = new double[NB];    // Pegel hinter der Wand ueber der Mithoerschwelle (dB, > 0 = hoerbar)
            public readonly double[] Zusatz = new double[NB];   // Daempfung ueber die reine Stauchung hinaus (dB, >= 0)
            public readonly double[] G = new double[NB];        // Spiel-Daempfung je Band (dB)
            public double Lautstaerke;                          // dB, die das Spiel als Lautstaerke anwendet (lautestes Band)
            public double ZusatzMax;                            // groesster Zusatz der Hochbaender (fuer die Tiefpass-Untergrenze)
            public bool Hoerbar(int b) => Ueber[b] > 0;
            public bool Stumm => Ueber[0] <= 0 && Ueber[1] <= 0 && Ueber[2] <= 0;
        }

        /// <summary>Tiefste Grenzfrequenz, wenn nur noch das tiefe Band hoerbar ist: bei 300 Hz liegt 1 kHz 21 dB, 4 kHz 45 dB unter
        /// 250 Hz (2-poliger Tiefpass) - dumpfes Wummern. Gewaehlt, keine Literatur.</summary>
        public const double TiefpassDumpf = 300.0;

        /// <summary>
        /// Hoerbarkeit je Band. d0 = reale Daemmung im Bezugsband (dB), m[b] = reale Mehrdaemmung des Bands b gegenueber dem
        /// Bezugsband, neigung[b] = Mehrdaempfung, die das Spiel ohne Hoerbarkeit ueber den Tiefpass anwendet (m[0] = neigung[0] = 0).
        /// false = Gruppe ohne Rechnung (laut) oder keine Daemmung: dann gilt die Stauchung allein.
        /// kennKnie/kennSteigung: Kennlinie (0.8.6) fuer den Bezugsband-Abzug; Standard aus (NaN) = lautheit * d0 wie bis 0.8.6.
        /// </summary>
        public static bool Rechnen(int typ, double abstand, double lGrund, double d0, double[] m, double[] neigung,
                                   double lautheit, double maxDb, Baender r, double kennKnie = double.NaN, double kennSteigung = 1.0, double pegelKorr = 0,
                                   double[] grund = null)
        {
            r.Gilt = false;
            if (double.IsNaN(pegelKorr)) pegelKorr = 0;
            if (double.IsNaN(BandPegel(typ, 0))) return false;
            r.Gilt = true;
            // galpegel (10.11.): grund = Verdecker je Band mit Fremdverstaerkung (FremdPegelKern.Verdecker), null = ohne (wie bisher)
            var n = grund ?? Grund(lGrund);
            double ab = 20 * Math.Log10(Math.Max(abstand, 1.0));
            d0 = Math.Max(0, d0);
            // 0.8.6 Kennlinie: Bezugsband-Abzug im gespielten Mass (unter dem Knie exakt lautheit * d0 wie bisher; leise Gruppen liegen
            // mit hoechstens 75 dB(A) in 1 m immer darunter)
            double a0 = lautheit * Kompression(FreiPegel(QuellPegel[typ] + pegelKorr, abstand), d0, kennKnie, kennSteigung);
            r.Lautstaerke = double.MaxValue; r.ZusatzMax = 0;
            for (int b = 0; b < NB; b++)
            {
                double db = Math.Max(0, d0 + (b == 0 ? 0 : m[b]));
                double frei = BandPegel(typ, b) + pegelKorr - ab - n[b];
                double knie = Math.Max(0, frei);
                r.D[b] = db; r.Knie[b] = knie;
                r.Ueber[b] = frei + MaskBreite - db;
                r.Zusatz[b] = db > 0 ? Math.Max(0, Daempfung(db, lautheit, knie, maxDb) - lautheit * db) : 0;
                r.G[b] = a0 + (b == 0 ? 0 : neigung[b]) + r.Zusatz[b];
                if (r.G[b] < r.Lautstaerke) r.Lautstaerke = r.G[b];
                if (b > 0 && r.Zusatz[b] > r.ZusatzMax) r.ZusatzMax = r.Zusatz[b];
            }
            return true;
        }

        /// <summary>Ein Band, das so weit unter dem lautesten liegt, ist im Klang weg; mehr Abstand muss der Tiefpass nicht herstellen.
        /// Ohne diese Grenze verlangt ein verdecktes 4-kHz-Band (G + 60 dB) vom 2-poligen Fit einen so tiefen Tiefpass, dass das
        /// noch gut hoerbare 1-kHz-Band mit wegfaellt. Gewaehlt, keine Literatur.</summary>
        public const double WegAbstand = 30.0;

        /// <summary>Tiefpass zu den hoerbaren Baendern: Fit an die Mehrdaempfung je Band gegenueber dem Bezugsband (G_b - G_0),
        /// hoechstens WegAbstand bzw. die Neigung ohne Hoerbarkeit, wenn die schon groesser ist.
        /// lpAlt = Tiefpass ohne Hoerbarkeit (exakt zurueck, solange kein Band einen Zusatz hat), tiefpassMin = "Low-pass at full
        /// insulation". Die Untergrenze sinkt stetig mit dem Zusatz des 1-kHz-Bands bis TiefpassDumpf (nur Tiefen hoerbar).</summary>
        public static double Tiefpass(Baender r, double lpAlt, double f1, double f2, double tiefpassMin, double fRef = 250)
        {
            if (!r.Gilt || (r.Zusatz[0] <= 1e-9 && r.ZusatzMax <= 1e-9)) return lpAlt;
            double ziel = Math.Min(tiefpassMin, TiefpassDumpf);
            double x = Math.Min(1.0, r.Zusatz[1] / WegAbstand);
            double fMin = Math.Min(lpAlt, tiefpassMin * Math.Pow(ziel / tiefpassMin, x));
            double t1 = Ziel(r, 1), t2 = Ziel(r, 2);
            // Ein Tiefpass daempft 4 kHz immer staerker als 1 kHz. Verlangen die Baender das Gegenteil (hochlastige Quelle, Tiefen
            // und Mitten schon verdeckt, 4 kHz noch hoerbar), wird 1 kHz nur so weit gesenkt wie 4 kHz - lieber ein hoerbares Band
            // behalten als es mit wegzufiltern (der Fit springt sonst zwischen den beiden unvereinbaren Zielen).
            // Gesenkt wird nur um den Teil, den die Hoerbarkeit (Zusatz 1 kHz > Zusatz 4 kHz) verursacht - ohne Zusatz bleibt es 0.8.4.
            t1 -= Math.Min(Math.Max(0, t1 - t2), Math.Max(0, r.Zusatz[1] - r.Zusatz[2]));
            return WegeKern.TiefpassFit(t1, t2, f1, f2, fMin, 22000, fRef);   // 10.11. (B2): Fit nach Lautheit wie Wege.FitLp
        }

        static double Neigung(Baender r, int b) => r.G[b] - r.Zusatz[b] - (r.G[0] - r.Zusatz[0]);   // Neigung des Spiels ohne Hoerbarkeit
        static double Ziel(Baender r, int b) => Math.Min(r.G[b] - r.G[0], Math.Max(Neigung(r, b), WegAbstand));
    }
}
