using System;
using System.Collections.Generic;

namespace Materialklang
{
    // 0.8.5 - 15. Ambient emitters: reine Rechnung (ohne Unity, im Offline-Test).
    // Kleine ortsfeste Umgebungsquellen des Spiels (BaseAmbientSoundPlayer: Leuchtreklame "ElectricSign", Lagerfeuer, Wassertropfen ...)
    // haben eine eigene Kurve (Unity CustomRolloff, x = d / MaxDistance), die bei MaxDistance (Customs 3,5-15 m) auf 0 geht.
    // Neue Kurve (gilt fuer alle umgestellten Quellen der Abschnitte 15-18):
    //  - bis zum Bezugsabstand r_ref exakt die Spielkurve (an der Quelle gleich laut),
    //  - danach die Pegel-Linie c * G(d) mit dem Abstandsgesetz G der Quellart (QuellPegelKern.Gesetz: Punktquelle 1/r,
    //    Flaechen-/Linienquelle mit Nahfeld nach Rathe), hoechstens so laut wie die Kurve bis 0.8.8-dev (Herleitung unten),
    //  - neue Reichweite = MaxDistance * Faktor (hoechstens Obergrenze, nie kuerzer als vorher),
    //  - weiches Ausblenden (cos^2) auf den letzten 25 % der neuen Reichweite statt Abschneiden.
    // Luftdaempfung (ISO 9613-1) bleibt weg: bis 30 m unter 0,2 dB bei 1 kHz.
    //
    // 0.8.8 ABSTANDSKURVE (Gutachten Kalibrierung 10.11.2026, Hauptfehler): bis 0.8.8-dev war r_ref = r0 = "erste Entfernung, ab der
    // die Spielkurve steiler faellt als 1/r". Fuer eine lineare Kurve min -> max ist das IMMER max/2 (Steigung in log-log -d/(max-d)),
    // egal wo sie zu fallen beginnt: V-Ex-Taxi linear 2 -> 20 m -> r0 = 10 m, Generator Streets linear 1 -> 12 m -> r0 = 6 m, BTR
    // r0 = 42,7 m. Bis r0 blieb die (fast flache) Spielkurve, der ganze Umkreis klang wie aus naechster Naehe (Taxi in 3,7 m -0,9 dB,
    // echt -5,3 dB gegenueber 2 m; in 10 m -5,1 statt -14 dB). Die Kurve des Spiels ist ein Gestaltungsmittel (lineares Ausblenden
    // bis MaxDistance), kein Abstandsgesetz; nur ihr flacher Anfang sagt etwas ueber die Quelle (Mindestabstand/Groesse).
    // Jetzt (Bezugsabstand r_ref und Pegel-Linie c * G(d), G = Abstandsgesetz der Quellart):
    //   Knie   = Ende des flachen Anfangs der Spielkurve (erste Entfernung, ab der sie um mehr als 0,3 dB je Verdopplung faellt:
    //            linear = minDistance, logarithmisch = minDistance, eigene Kurve = Ende ihres Plateaus),
    //   Nah    = Mindestabstand der Quellart (Karosserie, Gehaeuse; QuellMass),
    //   r_ref  = max(1 m, min(Knie, Nah)), hoechstens r0 (alte Regel). Quellart unbekannt: max(1 m, Knie).
    //   Linie relativ: c_rel = Spiel(r_ref) / G(r_ref) - die Physik haengt am Pegel, den das Spiel nahe der Quelle spielt
    //            (Gutachten: "relativ zum Spiel bleiben, Physik in die Abstandskurve legen").
    //   Linie absolut: c_abs = 10^(ueber/20), ueber = Tabelle(1 m) + K - Clip/Lautstaerke/Gruppe: der echte Pegel der Quelle.
    //            Laute Quellen (Alarm 100, BTR 107, Generator 85 dB(A) in 1 m) kann das Spiel nahe der Quelle nicht so laut spielen
    //            (Vollaussteuerung): ihr flaches oder langsam fallendes Spielplateau ist Kompression. Liegt c_abs mehr als 3 dB ueber
    //            c_rel, gilt die echte Linie - die Kurve folgt dann dem Spiel, bis dessen Pegel unter den echten faellt.
    //   Neue Kurve: bis r_ref die Spielkurve, danach min(Kurve bis 0.8.8-dev, c * G(d)) mit c = max(c_rel, c_abs) - nirgends lauter
    //            als bis 0.8.8-dev, an der Quelle gleich laut, stetig und fallend.
    //   Pegel unbekannt (Clip-Pegel erst beim Abspielen, Skriptquellen, Ambient-Spieler): bei Quellarten, die lauter sein koennen,
    //            als das Spiel typisch spielt (Tabelle am Mindestabstand + K ueber KlipGrenze), c_abs = unendlich (Kurve wie bis
    //            0.8.8-dev); leise Arten (Tropfen, Brummen, Lagerfeuer, PKW) nur relativ. Quellart und Pegel unbekannt (Tueren,
    //            Landung, Leuchtpatrone): wie bis 0.8.8-dev - ihr Nahbereich bleibt SEBs Mischung wie bei Schritten und Stimmen.
    internal static class UmgebungKern
    {
        public const double KnieSteigung = -0.05;     // dln g / dln d: 0,3 dB je Verdopplung = noch flach
        public const double RefMin = 1.0;             // m, kleinster Bezugsabstand (Tabelle "in 1 m"; naeher ist kaum ein Ohr)
        public const int ProOktave = 32;              // Abtastung der Spielkurve je Verdopplung (geometrisch; linear verpasste Knie)
        /// <summary>dBFS(A): typischer gespielter Nahpegel einer Schleife (Taxi suv_idle x 0,536 = -41,6, Wrackfeuer -41,1 LAeq).
        /// Ist der Clip-Pegel unbekannt und liegt die Quellart real darueber (Tabelle am Mindestabstand + K), kann ihr Spielplateau
        /// Kompression sein - dann bleibt die Kurve wie bis 0.8.8-dev.</summary>
        public const double KlipGrenze = -40.0;
        public const double AbsolutSchwelle = 3.0;   // dB: c_abs gilt erst ab 3 dB ueber c_rel (wie die Kalibrierschwelle)

        /// <summary>Bezugsabstand einer Quelle (m) und woraus er stammt (fuer Log und Tests).</summary>
        public struct Bezug
        {
            public double Ref, Knie, R0Alt, Komp;
            public double CRel, CAbs, C;   // Pegel-Linie c * G(d): relativ (Spiel an r_ref), absolut (echter Pegel), benutzt
            public bool Absolut;           // echte Linie gilt (Kompression) - die Kalibrierung laesst diese Quelle aus
            public bool WieBisher;         // Pegel unbekannt, laute Art: Kurve wie bis 0.8.8-dev
            public QuellMass M;
            public string Grund;           // "curve knee", "source size", "1 m minimum", "old r0" + Linie
        }

        public const double Obergrenze = 30.0;        // m, groesste neue Reichweite
        public const double AusblendAnteil = 0.25;    // letzte 25 % der neuen Reichweite
        public const double MinBezug = 0.25;          // m, kleinster Bezugsradius (1/r darf nicht gegen 0 laufen)

        /// <summary>Neue Reichweite: MaxDistance * Faktor, hoechstens Obergrenze, nie kuerzer als die alte.</summary>
        public static double NeueReichweite(double alt, double faktor, double grenze = Obergrenze)
            => Math.Max(alt, Math.Min(alt * Math.Max(1.0, faktor), grenze));

        /// <summary>Alte Regel (bis 0.8.8-dev, nur noch Obergrenze und Vergleich): erste Entfernung, ab der die Spielkurve steiler
        /// faellt als 1/r. alt(x) = Spielkurve mit x = d / maxAlt.</summary>
        public static double Bezugsradius(Func<double, double> alt, double maxAlt) => ErsteSteigung(alt, maxAlt, -1.0);

        /// <summary>Ende des flachen Anfangs der Spielkurve (m): letzte Abtaststelle vor der ersten, an der die Kurve um mehr als
        /// 0,3 dB je Verdopplung faellt.</summary>
        public static double Knie(Func<double, double> alt, double maxAlt) => ErsteSteigung(alt, maxAlt, KnieSteigung);

        static double ErsteSteigung(Func<double, double> alt, double maxAlt, double grenze)
        {
            if (maxAlt <= MinBezug) return maxAlt;
            double q = Math.Pow(2, 1.0 / ProOktave);
            double dVor = MinBezug, gVor = alt(dVor / maxAlt);
            if (gVor <= 1e-6) return MinBezug;
            for (double d = MinBezug * q; ; d *= q)
            {
                if (d >= maxAlt) d = maxAlt;
                double g = d >= maxAlt ? 0.0 : alt(d / maxAlt);
                if (g <= 1e-6) return dVor;
                double s = Math.Log(g / gVor) / Math.Log(d / dVor);
                if (s < grenze || (grenze <= -1.0 && s <= grenze))
                {
                    if (grenze <= -1.0) return dVor;
                    // Knie genau: wo die Kurve den Wert der letzten flachen Stelle verlaesst (lineare Kurve: genau minDistance)
                    double a = dVor, bb = d;
                    for (int i = 0; i < 30; i++) { double c = 0.5 * (a + bb); if (alt(c / maxAlt) >= gVor * (1 - 1e-4)) a = c; else bb = c; }
                    return a;
                }
                if (d >= maxAlt) return maxAlt;
                dVor = d; gVor = g;
            }
        }

        /// <summary>Abstand (m), ab dem eine Quelle, die real um ueberDb lauter ist, als das Spiel sie im Plateau spielt
        /// (Tabelle(1 m) + K - Clip/Lautstaerke/Gruppe), mit ihrem Abstandsgesetz unter den Spielpegel faellt. 0 = nie lauter.</summary>
        public static double KompAbstand(QuellMass m, double ueberDb)
        {
            if (double.IsNaN(ueberDb)) return double.NaN;
            double ziel = Math.Pow(10, -ueberDb / 20);
            if (QuellPegelKern.Gesetz(m, 0) <= ziel) return 0;
            double a = MinBezug, b = 1e5;
            if (QuellPegelKern.Gesetz(m, b) > ziel) return b;
            for (int i = 0; i < 80; i++)
            {
                double c = Math.Sqrt(a * b);
                if (QuellPegelKern.Gesetz(m, c) > ziel) a = c; else b = c;
            }
            return b;
        }

        /// <summary>0.8.8 Bezugsabstand und Pegel-Linie (Herleitung oben). ueberDb = Tabelle(1 m) + K - Pegel des Clips in 1 m (dB,
        /// NaN = unbekannt), tabelleDbA = Tabellenpegel der Quellart (NaN = unbekannt), k = Bezug K.</summary>
        public static Bezug Bezugsabstand(Func<double, double> alt, double maxAlt, QuellMass m, double ueberDb, double tabelleDbA = double.NaN, double k = double.NaN)
        {
            var b = new Bezug { Knie = Knie(alt, maxAlt), R0Alt = Bezugsradius(alt, maxAlt), Komp = double.NaN, M = m };
            double r;
            if (!m.Bekannt) { r = b.Knie; b.Grund = "curve knee"; }
            else if (b.Knie <= m.Nah) { r = b.Knie; b.Grund = "curve knee"; }
            else { r = m.Nah; b.Grund = "source size"; }
            if (r < RefMin) { r = RefMin; b.Grund = "1 m minimum"; }
            if (r > b.R0Alt) { r = b.R0Alt; b.Grund = "old r0"; }
            if (r > maxAlt) r = maxAlt;
            b.Ref = r;
            double gRef = alt(r / maxAlt);
            b.CRel = gRef / QuellPegelKern.Gesetz(m, r);
            if (!double.IsNaN(ueberDb)) { b.CAbs = Math.Pow(10, ueberDb / 20); b.Komp = KompAbstand(m, ueberDb); }
            else
            {
                bool laut = !m.Bekannt || double.IsNaN(tabelleDbA) || double.IsNaN(k) || tabelleDbA + QuellPegelKern.GesetzDb(m, m.Nah) + k > KlipGrenze;
                b.CAbs = laut ? double.PositiveInfinity : 0;
            }
            b.WieBisher = double.IsPositiveInfinity(b.CAbs);
            b.Absolut = !b.WieBisher && b.CAbs > b.CRel * Math.Pow(10, AbsolutSchwelle / 20);
            b.C = b.WieBisher ? double.PositiveInfinity : b.Absolut ? b.CAbs : b.CRel;
            b.Grund += b.WieBisher ? ", level unknown: 0.8.8-dev curve" : b.Absolut ? ", real level line (game compresses)" : ", relative to the game";
            return b;
        }

        /// <summary>Kurve bis 0.8.8-dev (ohne Ausblenden): Spielkurve bis r0, dann 1/r.</summary>
        public static double AltRegel(Func<double, double> alt, double maxAlt, double r0, double d)
            => d <= r0 ? (d >= maxAlt ? 0 : alt(d / maxAlt)) : alt(Math.Min(r0, maxAlt) / maxAlt) * r0 / d;

        /// <summary>Zielpegel (linear, 0..1) in Entfernung d - alte Form (r0 = Bezugsradius, Punktquelle), fuer Vergleiche.</summary>
        public static double Ziel(Func<double, double> alt, double maxAlt, double r0, double maxNeu, double trimGain, double d)
            => Ziel(alt, maxAlt, new Bezug { Ref = r0, R0Alt = r0, C = double.PositiveInfinity, M = QuellMass.Unbekannt }, maxNeu, trimGain, d);

        /// <summary>0.8.8: Zielpegel (linear, 0..1) in Entfernung d: bis r_ref Spielkurve, dann min(Kurve bis 0.8.8-dev, c * G(d)).</summary>
        public static double Ziel(Func<double, double> alt, double maxAlt, Bezug b, double maxNeu, double trimGain, double d)
        {
            if (d >= maxNeu) return 0.0;
            double g;
            if (d <= b.Ref) g = alt(d / maxAlt);
            else
            {
                g = AltRegel(alt, maxAlt, b.R0Alt, d);
                if (!double.IsInfinity(b.C)) g = Math.Min(g, b.C * QuellPegelKern.Gesetz(b.M, d));
            }
            double start = Math.Max(b.Ref, maxNeu * (1.0 - AusblendAnteil));
            if (d > start)
            {
                double t = (d - start) / (maxNeu - start);
                double c = Math.Cos(t * Math.PI * 0.5);
                g *= c * c;
            }
            g *= trimGain;
            if (g > 1.0) g = 1.0;
            return g < 0 ? 0 : g;
        }

        /// <summary>Baut die Kurve (x = d / maxNeu) als Hermite-Stuetzstellen mit Steigungen. Quellart und Pegel unbekannt: Kurve wie
        /// bis 0.8.8-dev (r0 = alte Regel).</summary>
        public static KurvenPunkt[] Bauen(Func<double, double> alt, double maxAlt, double maxNeu, double trimDb, out double r0)
        {
            var k = Bauen(alt, maxAlt, maxNeu, trimDb, QuellMass.Unbekannt, double.NaN, out Bezug b);
            r0 = b.R0Alt;
            return k;
        }

        /// <summary>0.8.8: Kurve mit Bezugsabstand und Abstandsgesetz der Quellart (m, ueberDb siehe Bezugsabstand).</summary>
        public static KurvenPunkt[] Bauen(Func<double, double> alt, double maxAlt, double maxNeu, double trimDb, QuellMass m, double ueberDb, out Bezug bez,
            double tabelleDbA = double.NaN, double kBezug = double.NaN)
        {
            bez = Bezugsabstand(alt, maxAlt, m, ueberDb, tabelleDbA, kBezug);
            var bz = bez;
            double r0 = bez.Ref;
            double trim = FernschussKern.GainAusDb(trimDb);
            var d = new List<double>();
            // Nahbereich: Spielkurve dicht abtasten (0..r_ref)
            int nNah = 10;
            for (int i = 0; i <= nNah; i++) d.Add(r0 * i / nNah);
            // Gesetz-Teil geometrisch bis zum Ausblendbeginn (mit den Knicken der Flaechenquelle), dann linear im Ausblendbereich
            double start = Math.Max(r0, maxNeu * (1.0 - AusblendAnteil));
            if (start > r0 * 1.0001)
            {
                int nLog = Math.Max(4, (int)Math.Ceiling(Math.Log(start / Math.Max(r0, MinBezug), 2) * 4));
                double q = Math.Pow(start / r0, 1.0 / nLog);
                double v = r0;
                for (int i = 1; i <= nLog; i++) { v *= q; d.Add(i == nLog ? start : v); }
                foreach (double knick in new[] { m.Bekannt ? m.Nah : 0, m.Bekannt ? m.R1 : 0, m.Bekannt ? m.R2 : 0, bez.R0Alt, KnickLinie(alt, maxAlt, bez) })
                    if (knick > r0 * 1.0001 && knick < start * 0.9999) d.Add(knick);
                d.Sort();
            }
            int nAus = 8;
            for (int i = 1; i <= nAus; i++) d.Add(start + (maxNeu - start) * i / nAus);
            // Gesamtreview (Kleinigkeit): r0 = maxNeu (Spielkurve bis zum Ende flach, Faktor 1) gab maxNeu neunmal als Zeitpunkt -
            // doppelte Schluessel in der AnimationCurve. Nur streng steigende Zeitpunkte, hoechstens bis maxNeu.
            for (int i = d.Count - 1; i >= 0; i--) if (d[i] > maxNeu) d.RemoveAt(i);
            for (int i = d.Count - 1; i >= 1; i--) if (d[i] <= d[i - 1] + maxNeu * 1e-6) d.RemoveAt(i);
            if (d.Count == 0 || d[d.Count - 1] < maxNeu - maxNeu * 1e-6) d.Add(maxNeu);

            var k = new KurvenPunkt[d.Count];
            double h = maxNeu * 1e-4;
            for (int i = 0; i < d.Count; i++)
            {
                double di = d[i], xi = di / maxNeu;
                double v = Ziel(alt, maxAlt, bz, maxNeu, trim, di);
                // einseitige Ableitungen (Knick bei r_ref, an den Knicken der Flaechenquelle und am Ausblendbeginn sauber halten)
                double links = di - h <= 0 ? double.NaN : (v - Ziel(alt, maxAlt, bz, maxNeu, trim, di - h)) / h * maxNeu;
                double rechts = di + h >= maxNeu ? double.NaN : (Ziel(alt, maxAlt, bz, maxNeu, trim, di + h) - v) / h * maxNeu;
                if (double.IsNaN(links)) links = double.IsNaN(rechts) ? 0 : rechts;
                if (double.IsNaN(rechts)) rechts = links;
                if (i == d.Count - 1) { v = 0; rechts = 0; }
                k[i] = new KurvenPunkt((float)xi, (float)v, (float)links, (float)rechts);
            }
            Monoton(k);
            return k;
        }

        /// <summary>Wo die Kurve bis 0.8.8-dev die Pegel-Linie c * G(d) schneidet (Stuetzstelle fuer den Knick), 0 = kein Schnitt.</summary>
        static double KnickLinie(Func<double, double> alt, double maxAlt, Bezug b)
        {
            if (double.IsInfinity(b.C) || b.C <= 0) return 0;
            double q = Math.Pow(2, 1.0 / ProOktave), vor = b.Ref;
            bool unterVor = AltRegel(alt, maxAlt, b.R0Alt, vor) <= b.C * QuellPegelKern.Gesetz(b.M, vor) * 1.000001;
            for (double d = b.Ref * q; d < maxAlt * 64; d *= q)
            {
                bool unter = AltRegel(alt, maxAlt, b.R0Alt, d) <= b.C * QuellPegelKern.Gesetz(b.M, d) * 1.000001;
                if (unter != unterVor)
                {
                    double a = vor, e = d;
                    for (int i = 0; i < 40; i++) { double c = 0.5 * (a + e); bool u = AltRegel(alt, maxAlt, b.R0Alt, c) <= b.C * QuellPegelKern.Gesetz(b.M, c) * 1.000001; if (u == unterVor) a = c; else e = c; }
                    return 0.5 * (a + e);
                }
                vor = d;
            }
            return 0;
        }

        /// <summary>0.8.5 (16. Loud signals): Steigungen je Abschnitt nach Fritsch-Carlson begrenzen, damit das Hermite-Stueck nicht
        /// ueberschwingt. Ursache: an einem Knick der Spielkurve (Ladenalarm: flach bis 1 m, dann steil) zwischen zwei weit
        /// auseinanderliegenden Stuetzstellen stieg die Kurve kurz an (+0,002 dB ueber 1,0).</summary>
        internal static void Monoton(KurvenPunkt[] k)
        {
            for (int i = 0; i + 1 < k.Length; i++)
            {
                double h = k[i + 1].T - k[i].T;
                if (h <= 0) continue;
                double delta = (k[i + 1].V - k[i].V) / h;
                if (Math.Abs(delta) < 1e-12) { k[i].Out = 0f; k[i + 1].In = 0f; continue; }
                double a = k[i].Out / delta, b = k[i + 1].In / delta;
                if (a < 0) { k[i].Out = 0f; a = 0; }
                if (b < 0) { k[i + 1].In = 0f; b = 0; }
                double s = a * a + b * b;
                if (s > 9.0)
                {
                    double tau = 3.0 / Math.Sqrt(s);
                    k[i].Out = (float)(tau * a * delta);
                    k[i + 1].In = (float)(tau * b * delta);
                }
            }
        }

        /// <summary>Soll diese Quelle umgestellt werden? (ohne Unity: Typname, Raumklang-Anteil, Reichweite, Pegel an der Quelle)</summary>
        public static bool Kandidat(string typ, float raumAnteil, float maxDistanz, float maxOriginal, float pegelNull)
        {
            if (raumAnteil < 0.5f) return false;                     // 2D/Stereo-Teppich
            if (maxDistanz <= 0f || maxDistanz > maxOriginal) return false;
            if (pegelNull <= 0f) return false;
            switch (typ)
            {
                case "LoopAmbientSoundPlayer":
                case "OneShotAmbientSoundPlayer":
                case "SeasonAmbientSoundPlayer":
                case "SeasonLoopAmbientSoundPlayer":
                case "AmbientSoundPlayer":
                    return true;
                default:
                    // WeatherRandomAmbientSoundPlayer (Wetter/Wind), EventLoopPlayer/EventRandomPlayer (Ereignis-Musik), Unbekanntes
                    return false;
            }
        }
    }
}
