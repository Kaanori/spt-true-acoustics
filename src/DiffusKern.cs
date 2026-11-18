using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // diffus-hall (10.11.2026) - Diffusanteil als Hall statt als trockener Pegel. Reine Rechnung (ohne Unity, Offline-Test).
    //
    // Ursache (Meldungen "klingt, als gaebe es keine Waende", "im Metalltank klang es nicht wie in einem Fass"; Klang-Log
    // 18:40 MARK 1: Schuss 62 m in der Nachbarhalle "open 20.5, rooms 0.0" -> vol -0.1 dB, lp 22 kHz):
    //   Die Mod rechnet fuer verdeckte Quellen die GESAMTE Energie am Hoerer: gerichteter Anteil G (Direkt/Beugung durch die Oeffnungen,
    //   durch die Wand abgestrahlt) + Diffusanteil D (Hallfeld des Quellraums, das durch Oeffnung/Wand in den Hoererraum strahlt und dort
    //   wieder ein Hallfeld aufbaut, 4P/A). Diese Summe ging als Lautstaerke des TROCKENEN, gerichteten Signals ans Spiel
    //   (BetterSource.OcclusionVolumeFactor -> source1.volume). Unity wendet AudioSource.volume VOR dem Spatializer an, also auf Direkt-
    //   UND Hallweg gleich (Unity-Handbuch AudioSource: "volume ... applied to the signal first and therefore affect both the direct and
    //   reverberated signals"; der Hall-Zwilling ReverbSimpleSource kopiert source1.volume, Dekompilat ReverbSimpleSource.UpdateSourceVolume).
    //   Folge: D klang trocken, gerichtet und nah, und Meta XR hallte die Summe noch einmal.
    //
    // Physik/Psychoakustik: D kommt zeitlich verschmiert und aus allen Richtungen an. Es bestimmt weder Richtung noch "Naehe" noch
    //   Klarheit (Klarheitsmass C50/C80 = Verhaeltnis frueher zu spaeter Energie, ISO 3382-1:2009 Anh. A; Kuttruff, Room Acoustics,
    //   Kap. 9). Die Lautheit eines Impulses integriert das Gehoer ueber ~100 ms (zeitliche Integration: Zwicker/Fastl, Psychoacoustics,
    //   Kap. 8.7 und 4.4; Moore, An Introduction to the Psychology of Hearing, Kap. 4 "temporal integration"); von einem exponentiell
    //   abklingenden Hallfeld mit Nachhallzeit T liegt im Fenster t der Anteil 1 - exp(-13,8 t / T) (Abklingen um 60 dB in T,
    //   13,8 = 6 ln 10). Bei T = 3 s sind das in 100 ms 37 %. Fuer Dauerquellen zaehlt die volle Diffusenergie (stationaer).
    //
    // Loesung mit den Mitteln des Spiels: der trockene Pegel traegt nur G; der Hall bekommt per Quelle (Meta "Reverb Send", +-dB vor
    //   dem spaeten Hall, Direktschall unberuehrt, Bereich -60..+20 dB: MetaXRAudioSource.ReverbSendDb Clamp) genau so viel mehr, dass sein
    //   Eingang beim heutigen Gesamtpegel bleibt: Send-Zusatz = trocken-Zusatz = 10 lg((G + D) / G). Der Hall wird damit nicht lauter
    //   und nicht leiser als bis jetzt; D steckt nur noch im Hall (nicht trocken UND im Hall). Die zeitliche Struktur (wie viel davon in
    //   die Lautheit eines Schusses eingeht) liefert der Hall selbst - darum wird der Fensteranteil NICHT zusaetzlich trocken gespielt
    //   (das waere dieselbe Energie zweimal). Reicht der Send-Bereich nicht (Zusatz > 20 dB - Grundsend), bleibt der Rest im trockenen
    //   Pegel: nie Energie, die gar nicht hoerbar ist.
    //   Quellen ohne Hallweg (Spatializer ohne Raumklang, Raumklang aus) behalten die Summe trocken (sonst waere D verschwunden).
    // ANNAHME (nicht belegt, im Spiel messen): das Verhaeltnis Hall/Direkt, das Meta fuer eine Quelle bei gleichem Eingang erzeugt, ist
    //   die Hallenergie des Hoererraums (eingebackene Geometrie + HallZonen). Ob dessen absoluter Pegel D trifft, kann erst der eigene
    //   Raumklang-Baustein (eigener Hall je Weg) sicherstellen - heute bleibt er wie bisher.
    // ------------------------------------------------------------------------------------------
    internal static class DiffusKern
    {
        /// <summary>Obergrenze des Meta-Reverb-Send je Quelle (MetaXRAudioSource.ReverbSendDb: Mathf.Clamp(value, -60f, 20f)).</summary>
        public const double SendMaxDb = 20.0;
        /// <summary>Hoechster Diffusanteil fuer die Rechnung (1 - 1e-3 -> trocken hoechstens +30 dB; "Max. insulation" deckelt ohnehin).</summary>
        public const double AnteilMax = 0.999;
        /// <summary>6 ln 10: Energie eines Hallfelds faellt in T60 um 60 dB -> E(t) ~ exp(-13,8 t / T).</summary>
        public const double Abkling = 13.815510557964274;

        /// <summary>Diffusanteil (0..1) der Energie aus gerichtetem Anteil g und Diffusanteil d (beide Intensitaet, >= 0).</summary>
        public static double Anteil(double g, double d)
        {
            if (!(g >= 0)) g = 0;
            if (!(d >= 0)) d = 0;
            double s = g + d;
            return s > 0 ? d / s : 0;
        }

        /// <summary>Wie viel dB der trockene Anteil leiser ist als die Summe: -10 lg(1 - anteil), anteil auf 0..AnteilMax.</summary>
        public static double TrockenZusatzDb(double anteil)
        {
            if (!(anteil > 0)) return 0;
            if (anteil > AnteilMax) anteil = AnteilMax;
            return -10.0 * Math.Log10(1.0 - anteil);
        }

        /// <summary>Aufteilung im gespielten Mass. dbGesamt = Daempfung der Summe (dB, wie bisher angewandt), dbTrocken = Daempfung nur des
        /// gerichteten Anteils (>= dbGesamt), sendBasis = Reverb-Send des Spiels (Preset, dB). Ergebnis: angewandte Daempfung des trockenen
        /// Signals und neuer Send. Der Hall-Eingang (Lautstaerke + Send) bleibt beim Gesamtpegel: -dbAus + sendAus = -dbGesamt + sendBasis.
        /// Ist der Send-Bereich zu klein, bleibt der Rest trocken (dbAus kleiner als dbTrocken).</summary>
        public static void Aufteilen(double dbGesamt, double dbTrocken, double sendBasis, out double dbAus, out double sendAus)
        {
            double zusatz = dbTrocken - dbGesamt;
            if (!(zusatz > 0)) zusatz = 0;                 // auch NaN -> 0 (wie bisher)
            double platz = SendMaxDb - sendBasis;
            if (!(platz > 0)) platz = 0;
            if (zusatz > platz) zusatz = platz;
            dbAus = dbGesamt + zusatz;
            sendAus = sendBasis + zusatz;
        }

        /// <summary>Anteil der Energie eines exponentiell abklingenden Hallfelds (Nachhallzeit t60, s), der in den ersten 'fenster'
        /// Sekunden nach seinem Einsatz ankommt: 1 - exp(-13,8 fenster / t60).</summary>
        public static double ImFenster(double t60, double fenster)
        {
            if (!(t60 > 0)) return 1;
            if (!(fenster > 0)) return 0;
            return 1.0 - Math.Exp(-Abkling * fenster / t60);
        }

        /// <summary>Empfundener Ereignispegel (dB relativ zum Freifeld-Direktschall) aus gerichtetem Anteil g und Diffusanteil d
        /// (Intensitaeten). Impuls: vom Diffusanteil zaehlt nur der Teil im Integrationsfenster (Standard 100 ms); Dauerton: alles.</summary>
        public static double EreignisDb(double g, double d, double t60, bool impuls, double fenster = 0.1)
        {
            double k = impuls ? ImFenster(t60, fenster) : 1.0;
            double e = Math.Max(0, g) + k * Math.Max(0, d);
            return e > 0 ? 10.0 * Math.Log10(e) : double.NegativeInfinity;
        }

        /// <summary>galpegel (10.11.): Diffusanteil des Raumwegs, wenn die Wand zwischen Raeumen mitzaehlt. Route (Tueren/Fenster) und Wand
        /// sind parallele Wege mit eigener Daempfung (dB, Bezugsband) und eigenem Diffusanteil (0..1). summe = Energiesumme beider Wege
        /// (Wege.Summe, ScorePostfix): Anteil energiegewichtet (Mischen); sonst gilt nur der leisere Weg (kleinere Daempfung; gleich -> Route,
        /// wie ScorePostfix min(Route, Wand)).</summary>
        public static double ZweiWege(double routeDb, double routeAnteil, double wandDb, double wandAnteil, bool summe)
        {
            if (double.IsNaN(routeDb)) routeDb = double.PositiveInfinity;
            if (double.IsNaN(wandDb)) wandDb = double.PositiveInfinity;
            double fr = routeAnteil > 0 ? (routeAnteil < 1 ? routeAnteil : 1) : 0, fw = wandAnteil > 0 ? (wandAnteil < 1 ? wandAnteil : 1) : 0;
            if (!summe) return wandDb < routeDb ? fw : fr;
            double er = double.IsPositiveInfinity(routeDb) ? 0 : Math.Pow(10, -routeDb / 10), ew = double.IsPositiveInfinity(wandDb) ? 0 : Math.Pow(10, -wandDb / 10);
            double s = er + ew;
            return s > 0 ? (er * fr + ew * fw) / s : 0;
        }

        // ---------------------------------------------------------------- hallsend (12.11.2026)

        /// <summary>
        /// hallsend (Befund A2, Durchgang Akustik 12.11.): Hindernis im SELBEN Raum. Ein Hindernis zwischen Quelle und Hoerer im selben Raum
        /// schattet nur den Direktschall (und die fruehen Wege um das Hindernis) ab; das Hallfeld des Raums speist die Quelle mit ihrer
        /// ganzen Leistung, es haengt nicht vom Hindernis ab (Kuttruff, Room Acoustics, Kap. 5: I_diffus = 4 W / A, unabhaengig vom
        /// Quellort; ISO 17624 / VDI 2720-1 Abschirmung im Raum wirkt nur auf den Direktanteil). Die Mod rechnet so schon die Kette
        /// (WandKetteKern: Hindernisse schatten nur den gerichteten Teil). Bis 12.11. senkte die Lautstaerke der Quelle (wirkt vor dem
        /// Spatializer auf Direkt- UND Hallweg) den Hall um die volle Hindernisdaempfung (Log 23:45:53.901: Summe -25 dB, Hall -25 dB).
        /// Stellgroessen: trocken = Hindernisdaempfung dbTrocken (unveraendert), Hall-Eingang (Lautstaerke + Send) = unverdeckter Pegel
        /// dbHall (Daempfung ohne Hindernis, normal 0): Send = Basis + (dbTrocken - dbHall). Der Meta-Send reicht hoechstens bis SendMaxDb;
        /// was darueber laege, fehlt dem Hall (fehlDb, im Log sichtbar). Der trockene Teil wird dafuer NICHT lauter gemacht: er traegt den
        /// Direktschall, dessen Pegel das Hindernis bestimmt (anders als in Aufteilen, wo der Rest Diffusenergie desselben Wegs ist).
        /// </summary>
        public static void RaumHall(double dbTrocken, double dbHall, double sendBasis, out double sendAus, out double fehlDb)
        {
            double zusatz = dbTrocken - dbHall;
            if (!(zusatz > 0)) zusatz = 0;                 // auch NaN -> 0
            double platz = SendMaxDb - sendBasis;
            if (!(platz > 0)) platz = 0;
            fehlDb = zusatz > platz ? zusatz - platz : 0;
            sendAus = sendBasis + Math.Min(zusatz, platz);
        }

        /// <summary>
        /// hallsend (Befund B2): Klangfarbe des trockenen Signals. a0..a2 = Diffusanteil je Band (Bezug/1 kHz/4 kHz) der Summe, zApp =
        /// angewandter Trocken-Zusatz im Bezugsband (dB, &lt;= -10 lg(1 - a0); kleiner, wenn der Send-Deckel einen Teil des Diffusanteils trocken
        /// laesst). Modell: trocken = G + kappa D (kappa = Teil von D, der nicht in den Hall ging; der Send ist breitbandig, kappa also in allen
        /// Baendern gleich): 1 - a0 + kappa a0 = 10^(-zApp/10). Trocken je Band relativ zur Summe: r_b = 1 - a_b + kappa a_b.
        /// Ergebnis k1/k2 = Mehrdaempfung des trockenen Signals bei 1 kHz / 4 kHz gegenueber dem Bezugsband, ZUSAETZLICH zur Bandkurve der
        /// Summe (dB, + = dumpfer): k_b = -10 lg(r_b / r_0). Fall d (Durchgang): G faellt mit der Frequenz (Kantenbeugung), D kaum -> trocken
        /// dumpfer als die Summe.
        /// </summary>
        public static void TrockenBaender(double a0, double a1, double a2, double zApp, out double k1, out double k2)
        {
            k1 = k2 = 0;
            a0 = Klemme(a0); a1 = Klemme(a1); a2 = Klemme(a2);
            if (!(a0 > 0) || !(zApp > 0)) return;
            double r0 = Math.Pow(10, -zApp / 10);
            double kappa = (r0 - (1 - a0)) / a0;
            kappa = kappa < 0 ? 0 : (kappa > 1 ? 1 : kappa);
            r0 = 1 - a0 + kappa * a0;
            double r1 = 1 - a1 + kappa * a1, r2 = 1 - a2 + kappa * a2;
            const double klein = 1e-6;
            k1 = -10 * Math.Log10(Math.Max(r1, klein) / Math.Max(r0, klein));
            k2 = -10 * Math.Log10(Math.Max(r2, klein) / Math.Max(r0, klein));
        }

        static double Klemme(double a) => a > 0 ? (a < AnteilMax ? a : AnteilMax) : 0;

        /// <summary>Diffusanteil (0..1) aus dem Trocken-Zusatz zusatzDb = -10 lg(1 - a) (Umkehrung von TrockenZusatzDb).</summary>
        public static double AnteilAusZusatz(double zusatzDb) => zusatzDb > 0 ? 1 - Math.Pow(10, -zusatzDb / 10) : 0;

        /// <summary>
        /// hallsend (Spielcode-Fund 1): wem gehoert ein angehobener Reverb-Send? Das Spiel setzt ReverbSendDB nur in SetPreset (beim Anlegen
        /// der Pool-Quelle); BorrowSource und Release lassen ihn stehen (BetterAudio.cs:246-267, BetterSource.Release Z. 329-345). Der Send
        /// gehoert aber zum KLANG, nicht zum Pool-Objekt. Regel: wer anhebt, merkt es (Erhoeht); jede Stelle, an der der Klang endet
        /// (Freigabe) oder an der fuer ihn keine Aufteilung gilt (Zurueck), setzt auf den Preset-Wert zurueck, wenn angehoben war.
        /// Rueckgabe true = jetzt zuruecksetzen (genau einmal je Anhebung).
        /// </summary>
        public sealed class SendBesitz
        {
            public bool Angehoben;
            public void Erhoeht() { Angehoben = true; }
            public bool Zurueck() { if (!Angehoben) return false; Angehoben = false; return true; }
        }

        /// <summary>Energiegewichteter Diffusanteil mehrerer paralleler Wege: Summe e_i f_i / Summe e_i (e = Energie, f = Anteil).</summary>
        public static double Mischen(double[] e, double[] f, int n)
        {
            double s = 0, sd = 0;
            for (int i = 0; i < n; i++)
            {
                double x = e[i] > 0 ? e[i] : 0;
                double a = f[i] > 0 ? (f[i] < 1 ? f[i] : 1) : 0;
                s += x; sd += x * a;
            }
            return s > 0 ? sd / s : 0;
        }
    }
}
