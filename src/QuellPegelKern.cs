using System;
using System.Collections.Generic;

namespace Materialklang
{
    /// <summary>0.8.6: Pegelart einer Quelle ohne Okklusion im Spiel (Abschnitte 15/17/18 + Wanddaempfung).</summary>
    internal enum PegelArt
    {
        Unbekannt = -1,
        PkwLeerlauf = 0, BtrMotor, BtrTurm, BtrHorn, LokFahrt, LokLeerlauf, Flugzeug, Fackel, Pfeifen, Tor,
        Lagerfeuer, Grossfeuer, Generator, Maschine, Tropfen, Klein, Alarm,
        Maschinenhalle   // 0.8.6 Abstandskurve: mehrere Aggregate in einem Raum (Labs "sound_generators_loop_stereo")
    }

    /// <summary>0.8.6 Abstandskurve: Abmessungen einer Quellart fuer das Abstandsgesetz (QuellPegelKern.Gesetz).
    /// Nah = kleinster Abstand Ohr - akustischer Mittelpunkt (Karosserie, Gehaeuse, Flammen; naeher kommt man nicht heran),
    /// A x B = abstrahlende Flaeche (A &lt;= B, m); 0 x 0 = Punktquelle. Bekannt = false: Quellart unbekannt.</summary>
    internal struct QuellMass
    {
        public double Nah, A, B;
        public bool Bekannt;
        public QuellMass(double nah, double a, double b) { Nah = nah; A = Math.Min(a, b); B = Math.Max(a, b); Bekannt = true; }
        /// <summary>Ende des flachen Nahfelds einer Flaechenquelle (a/pi) und Beginn des Fernfelds (b/pi), Rathe 1969.</summary>
        public double R1 => A / Math.PI;
        public double R2 => B / Math.PI;
        public static readonly QuellMass Unbekannt = default;
    }

    // ---------------------------------------------------------------------------------------------
    //  0.8.6 - Hoerbarkeit der Quellen OHNE Okklusion im Spiel (Meldung Streets 09.11.2026 18:46 MARK 1: V-Ex-Taxi 135 m entfernt
    //  "gut hoerbar"; 18:53 BTR 364 m zwischen Haeuserschluchten hoerbar).
    //
    //  Ursache: Occluder-Quellen (Schritte, Ausruestung, Stimme ...) werden seit 0.8.5 je Band gegen das Grundgeraeusch geprueft
    //  (Hoerbarkeit.Rechnen in Physik.LautstaerkeKern). Die Quellen der Wanddaempfung (WandDaempfung.Auswerten) bekamen nur
    //  Lautstaerke = L0 x Lautheit und den Tiefpass der Bandkurve - keine Pegelannahme und keine Verdeckung. Fuer sie hatten
    //  die Abschnitte 15/17/18 die Reichweite x4 ... x20 verlaengert; die 1/r-Kurve ist an der Quelle so laut wie im Spiel und
    //  faellt richtig (-6 dB je Verdopplung), aber im Spiel gibt es kein Grundgeraeusch, das ferne Quellen verdeckt. Das Taxi
    //  (75 dB(A) in 1 m) liegt in 135 m real bei ~32 dB(A), hinter der Abschirmung bei ~27 dB(A) - unter dem Stadthintergrund
    //  (draussen 35-45 dB(A)); im Spiel war es -33 dB unter dem Nahpegel und damit deutlich hoerbar.
    //
    //  Rechnung je Band (250 Hz / 1 kHz / 4 kHz, wie Hoerbarkeit fuer Occluder-Quellen):
    //    frei_b  = P_b(1 m) - 20 lg d - Luft_b(d) - N_b       (Pegel ohne Wand ueber dem Grundgeraeusch; Luft ISO 9613-1, 20 C/70 %/101,325 kPa)
    //    D_b     = reale Daemmung Wand + Raumweg/Abschirmung (WandDaempfungKern.SummeWege/Summe, schon je Band)
    //    unter_b = D_b - frei_b                                 (dB unter dem Grundgeraeusch hinter der Wand; ueber_b = 10 - unter_b)
    //    Zusatz_b = (1 - Lautheit) * max(0, D_b - max(0, frei_b)) + Hoerbarkeit.Verdeckung(unter_b)   (0.8.6: stetig, hoechstens 2 dB je dB,
    //               bis 0.8.6-dev "Max. insulation * smoothstep(unter_b / 10 dB)" = 60 dB auf 10 dB, siehe RaumwegKern.cs)
    //    G_b     = Lautheit * D_0 + max(0, D_b - D_0) + Zusatz_b,   Lautstaerke = min G_b, Tiefpass = Hoerbarkeit.Tiefpass
    //              (0.8.6 Kennlinie: Lautheit * K(L_frei) - K(L_frei - D_0) statt Lautheit * D_0, L_frei = realer dB(A)-Pegel ohne Wand;
    //               unter dem Knie dasselbe - laut sind hier BTR, Lok, Flugzeug, Horn, Alarm)
    //  Ist frei_b >= 0, ist das genau die Formel der Occluder-Quellen (Hoerbarkeit.Daempfung). Neu ist nur, dass die Verdeckung
    //  auch ohne Wand greift (frei_b < 0): die Occluder-Quellen enden mit der Reichweite des Spiels, bevor sie unter das
    //  Grundgeraeusch fallen; diese Quellen tragen durch Abschnitt 15/17/18 bis 2000 m und fallen dort real darunter.
    //
    //  Keine doppelte Zaehlung: die Entfernung daempft weiter NUR die Kurve (0.8.6: Spielkurve bis zum Bezugsabstand, dann das
    //  Abstandsgesetz der Quellart, siehe UmgebungKern.Bezugsabstand). 20 lg d steckt hier
    //  allein in der Entscheidung, wo die Quelle gegen den Hintergrund steht (wie bei den Occluder-Quellen, deren Spielkurve
    //  ebenfalls die Entfernung macht). Zusatz_b ist keine zweite Entfernungsdaempfung: bei Lautheit 1 ist er 0, solange das Band
    //  ueber dem Grundgeraeusch liegt, und blendet danach ueber 10 dB (Mithoerschwelle, Zwicker/Fastl) weich aus.
    //  Ohne Zusatz in allen Baendern = exakt das Verhalten davor (Lautstaerke L0 x Lautheit, Tiefpass der Bandkurve).
    //
    //  Grundgeraeusch: drinnen "Quiet sources: background noise (dB)" (Abschnitt 0, 30 dB(A) ruhiger Raum), draussen (Hoerer in
    //  einem Aussenraum der Raumdaten) neu "Wall damping: background noise outdoors (dB)" (40 dB(A)). Spektrum beide Male die
    //  NR-Kurve (tief laut, hoch leise) - Wind und ferne Stadt sind ebenfalls tieflastig.
    //
    //  PEGEL JE QUELLART (dB(A) in 1 m, Freifeld; Spektrum 250 Hz / 1 kHz / 4 kHz relativ, Schaetzung nach typischem Verlauf):
    //   Art              dB(A)@1m  Spektrum         Quelle / Herleitung
    //   PKW-Leerlauf       62      0 / -5 / -12     NHTSA 2012: 56 dB(A) in 2 m (SAE J2889-1), Median 17 Fahrzeuge (0.8.6 Gutachten; vorher 68)
    //   BTR-Motor/Ketten  107      0 / -4 / -12     BTR-80-Diesel ~90 dB(A) in 7,5 m (+17,5 dB auf 1 m; Lkw-Diesel 85-90 dB(A) in 7,5 m, ISO 362)
    //   BTR-Turm           80     -4 /  0 /  -6     Elektro-/Hydraulik-Stellmotor ~75-85 dB(A) in 1 m (Annahme)
    //   BTR-Lufthorn      117     -3 /  0 /  -5     Druckluft-Horn 105-118 dB(A) in 2 m (Mitte 111 + 6 dB auf 1 m)
    //   Lok Horn/Fahrt    126      0 / -2 /  -8     Horn mind. 96 dB(A) in 30 m (49 CFR 229.129; +29,5 dB auf 1 m); Fahrt 90 dB(A) in 30 m liegt 6 dB darunter
    //   Lok-Leerlauf      100      0 / -4 / -12     Diesel-Leerlauf 70 dB(A) in 30 m (EPA 40 CFR 201) + 29,5 dB
    //   Flugzeug          140      0 / -3 / -10     Transportflugzeug-Ueberflug 85-100 dB(A) am Boden; 90 dB(A) in 300 m Hoehe + 49,5 dB (Annahme Hoehe)
    //   Signalfackel       70    -10 / -3 /   0     Strassenfackel-Zischen 65-75 dB(A) in 1 m (Annahme, 0.8.5 SkriptQuellenKern)
    //   Moerser-Pfeifen    90    -10 /  0 /  -2     Pfeifen 70-90 dB am Flugweg (Annahme, obere Grenze, da Abstand unbekannt)
    //   Ausgangs-Tor       85     -2 /  0 /  -4     Metalltor/Rolltor ~80-90 dB(A) in 1 m (Annahme)
    //   Lagerfeuer         60     -4 /  0 /  -3     Knistern eines Lager-/Kaminfeuers ~55-65 dB(A) in 1 m (Annahme; 0.8.6: auch "fireplace")
    //   Grossfeuer         60     -2 /  0 /  -4     Restbrand am Wrack wie ein grosses Lagerfeuer (0.8.6 Gutachten: 80 -> 70 -> 60; siehe unten)
    //   Generator          85      0 / -3 / -10     Baustellen-Stromerzeuger 65-75 dB(A) in 7 m (Herstellerangaben, 2000/14/EG) + 17 dB
    //   Maschine           80      0 / -2 /  -8     Pumpen, Turbinen, Kompressoren, Motoren (Annahme, unter dem Generator)
    //   Tropfen            45    -15 / -5 /   0     Wassertropfen auf hartem Boden ~40-50 dB(A) in 1 m (Annahme)
    //   klein (Brummen)    50      0 / -3 / -10     Netzbrummen, Leuchtstoffroehren, Fliegen, Server 40-55 dB(A) (Annahme)
    //   Alarm/Signal      100     -6 /  0 /  -3     Alarmgeber 65-120 dB(A) in 1 m nach EN 54-3, typisch ~100 dB(A)
    //   Maschinenhalle     85      0 / -3 / -10     mehrere Aggregate (wie Generator), Flaeche 6 x 12 m (0.8.6)
    //  Unbekannt -> keine Hoerbarkeitsrechnung (Verhalten wie 0.8.5).
    // ---------------------------------------------------------------------------------------------
    internal static class QuellPegelKern
    {
        // 0.8.6 Kalibrierung (Kaan 09.11.2026): PKW-Leerlauf 75 -> 68 dB(A) in 1 m. Standgeraeusch-Messungen nach ISO 5130 / UN R51
        // (0,5 m hinter dem Auspuff, erhoehte Drehzahl) liegen bei 70-80 dB(A); im Leerlauf und rundum in 1 m eher 62-72 dB(A)
        // (Benziner-SUV ~65, Diesel ~70) - Mitte 68.
        // 0.8.6 (Meldung Streets 09.11.2026 23:50 "Feuer etwas zu laut"): Grossfeuer 80 -> 70 dB(A) in 1 m. Fuer das Geraeusch
        // brennender Gegenstaende gibt es kaum Messungen (Abbasi/Wilson/Ezekoye, ASA-Tagungsbeitrag JASA 136 (4), 2014, doi 10.1121/1.4899840:
        // "few acoustic measurements have been made"; Quelle ist die Pyrolyse des Materials). 80 dB(A) in 1 m waere so laut wie
        // ein Rasenmaeher daneben; ein Fahrzeugbrand ohne Explosionen faucht und knistert eher wie ein grosses Lagerfeuer plus
        // Flammenrauschen (~65-75 dB(A)) - Mitte 70, 10 dB ueber dem Lagerfeuer (60). Kein Messwert, Annahme.
        // 0.8.6 Gutachten Kalibrierung (10.11.2026): PKW-Leerlauf 68 -> 62 dB(A) in 1 m. NHTSA 2012 (Docket NHTSA-2011-0148), Tabelle 29:
        // stehend, Motor an, Mikrofon nach SAE J2889-1 (2 m neben der Fahrzeugmitte, 1,2 m hoch) 50,7-63,1 dB(A), Median 56 dB(A),
        // 17 Fahrzeuge; Tabelle 1: 45,1-48,5 dB(A) in 12 ft (3,7 m). 56 dB(A) in 2 m = 62 dB(A) Punktquellen-Pegel in 1 m.
        // Grossfeuer 70 -> 60 dB(A): die Flammen an den Wracks (Streets, Ground Zero) sind kleine Restbraende; das Spiel spielt sie
        // umgerechnet mit 59 dB(A) in 1 m (wie ein Lagerfeuer, Tabelle 60). Fuer 70 gibt es keinen Beleg (Abbasi et al. 2014:
        // "few acoustic measurements").
        // Maschinenhalle (neu, 0.8.6 Abstandskurve) wie ein Generator 85 dB(A) als Punktquellen-Pegel in 1 m; die Flaeche (QuellMass)
        // macht das Nahfeld flacher.
        public static readonly double[] Pegel = { 62, 107, 80, 117, 126, 100, 140, 70, 90, 85, 60, 60, 85, 80, 45, 50, 100, 85 };

        static readonly double[][] Spektrum =
        {
            new[] { 0.0, -5.0, -12.0 },  new[] { 0.0, -4.0, -12.0 }, new[] { -4.0, 0.0, -6.0 }, new[] { -3.0, 0.0, -5.0 },
            new[] { 0.0, -2.0, -8.0 },   new[] { 0.0, -4.0, -12.0 }, new[] { 0.0, -3.0, -10.0 }, new[] { -10.0, -3.0, 0.0 },
            new[] { -10.0, 0.0, -2.0 },  new[] { -2.0, 0.0, -4.0 },  new[] { -4.0, 0.0, -3.0 },  new[] { -2.0, 0.0, -4.0 },
            new[] { 0.0, -3.0, -10.0 },  new[] { 0.0, -2.0, -8.0 },  new[] { -15.0, -5.0, 0.0 }, new[] { 0.0, -3.0, -10.0 },
            new[] { -6.0, 0.0, -3.0 },   new[] { 0.0, -3.0, -10.0 },
        };

        static readonly string[] Namen =
        {
            "car idle", "BTR engine", "BTR turret", "BTR air horn", "train horn/travel", "train idle", "airdrop plane", "signal flare",
            "mortar whistle", "extract gate", "campfire", "large fire", "generator", "machine", "dripping water", "small source", "alarm/signal",
            "machine hall"
        };

        // ---------------------------------------------------------------------------------------------
        //  0.8.6 Abstandskurve (Gutachten Kalibrierung, Abschnitt 3 Schritt 2): Abmessungen je Quellart.
        //  Abstandsgesetz einer rechteckigen Flaechenquelle a x b (a <= b) nach Rathe (J. Sound Vib. 10 (1969) 472; VDI 3770):
        //    d < a/pi        flach (0 dB je Verdopplung),
        //    a/pi < d < b/pi -3 dB je Verdopplung (Linienquelle),
        //    d > b/pi        -6 dB je Verdopplung (Punktquelle, 1/r).
        //  Gesetz() ist auf das Fernfeld normiert: G(d) = 1/d fuer d >= b/pi, d. h. "dB(A) in 1 m" der Tabelle ist der Pegel einer
        //  gleich lauten Punktquelle in 1 m (so sind auch die Herstellerangaben LWA - 8 dB umgerechnet). Unter Nah bleibt der Pegel
        //  flach (naeher kommt das Ohr nicht an den Mittelpunkt).
        //   Art            Nah   a x b (m)   Beleg / Annahme
        //   PKW-Leerlauf   2,0   1,5 x 1,8   Motorraum; SAE J2889-1 misst 2 m neben der Fahrzeugmitte (Karosserie dazwischen)
        //   BTR-Motor      2,0   2,9 x 7,6   BTR-82A 7,6 x 2,9 m: Heckmotor + acht Raeder entlang des Rumpfs (Linie 7,6 m)
        //   BTR-Turm       1,5   0,5 x 1,0   Stellmotor im Turm
        //   Lok Fahrt      2,0   3,1 x 20    Lok ~20 m, Rad/Schiene entlang (Linienquelle)
        //   Lok Leerlauf   2,0   3,1 x 8     Maschinenraum der Diesellok
        //   Ausgangs-Tor   1,0   3,0 x 4,0   Tor-/Rolltorblatt
        //   Lagerfeuer     1,0   0,8 x 1,0   Feuerstelle
        //   Grossfeuer     1,5   1,8 x 4,5   brennendes Auto
        //   Generator      1,0   0,8 x 1,5   Baustellen-/Notstromaggregat
        //   Maschine       1,0   1,0 x 2,0   Pumpe, Kompressor, Turbine
        //   Maschinenhalle 2,0   6,0 x 12    mehrere Aggregate in einem Raum
        //   alle anderen   1,0   Punkt       Horn, Flugzeug (immer weit weg), Fackel, Pfeifen, Tropfen, kleine Quellen, Alarmgeber
        // ---------------------------------------------------------------------------------------------
        static readonly QuellMass[] Masse =
        {
            new QuellMass(2.0, 1.5, 1.8), new QuellMass(2.0, 2.9, 7.6), new QuellMass(1.5, 0.5, 1.0), new QuellMass(1.0, 0, 0),
            new QuellMass(2.0, 3.1, 20), new QuellMass(2.0, 3.1, 8), new QuellMass(1.0, 0, 0), new QuellMass(1.0, 0, 0),
            new QuellMass(1.0, 0, 0), new QuellMass(1.0, 3.0, 4.0), new QuellMass(1.0, 0.8, 1.0), new QuellMass(1.5, 1.8, 4.5),
            new QuellMass(1.0, 0.8, 1.5), new QuellMass(1.0, 1.0, 2.0), new QuellMass(1.0, 0, 0), new QuellMass(1.0, 0, 0),
            new QuellMass(1.0, 0, 0), new QuellMass(2.0, 6.0, 12),
        };

        /// <summary>Abmessungen der Quellart (Unbekannt: QuellMass.Unbekannt).</summary>
        public static QuellMass Mass(PegelArt a) => Gilt(a) && (int)a < Masse.Length ? Masse[(int)a] : QuellMass.Unbekannt;

        /// <summary>Skriptquelle: Arten ohne Pegelannahme (Tueren, Landung, Abwurf, Leuchtpatrone) sind Punktquellen (Nah 1 m).</summary>
        public static QuellMass Mass(SkriptArt s)
        {
            var a = Von(s);
            return Gilt(a) ? Mass(a) : new QuellMass(1.0, 0, 0);
        }

        /// <summary>Abstandsgesetz (Verstaerkung, linear) einer Quelle mit den Abmessungen m im Abstand d, normiert auf das Fernfeld
        /// (= 1/d fuer d >= b/pi). Unbekannt: Punktquelle ab UmgebungKern.MinBezug.</summary>
        public static double Gesetz(QuellMass m, double d)
        {
            double nah = m.Bekannt ? Math.Max(m.Nah, UmgebungKern.MinBezug) : UmgebungKern.MinBezug;
            double r = Math.Max(d, nah);
            double r1 = m.Bekannt ? m.R1 : 0, r2 = m.Bekannt ? Math.Max(m.R2, r1) : 0;
            if (r >= r2 || r2 <= 0) return 1.0 / r;
            if (r >= r1 || r1 <= 0) return Math.Sqrt(r2 / r) / r2;
            return Math.Sqrt(r2 / r1) / r2;
        }

        /// <summary>Pegel (dB) relativ zur Punktquelle in 1 m: 20 lg Gesetz(m, d).</summary>
        public static double GesetzDb(QuellMass m, double d) => 20 * Math.Log10(Gesetz(m, d));

        public static bool Gilt(PegelArt a) => a >= 0 && (int)a < Pegel.Length;
        public static string Name(PegelArt a) => Gilt(a) ? Namen[(int)a] : "unknown";
        public static double DbA(PegelArt a) => Gilt(a) ? Pegel[(int)a] : double.NaN;

        /// <summary>Bandpegel (dB, unbewertet) in 1 m: Spektrum so verschoben, dass die A-bewertete Summe der Tabelle entspricht
        /// (wie Hoerbarkeit.BandPegel).</summary>
        public static double BandPegel(PegelArt a, int b)
        {
            if (!Gilt(a)) return double.NaN;
            var s = Spektrum[(int)a];
            double e = 0;
            for (int i = 0; i < Hoerbarkeit.NB; i++) e += Math.Pow(10, (s[i] + Hoerbarkeit.ABew[i]) / 10);
            return Pegel[(int)a] + s[b] - 10 * Math.Log10(e);
        }

        /// <summary>Skriptquelle (Abschnitt 18) -> Pegelart. Arten mit Okklusion im Spiel (Landung, Leuchtpatrone, Tueren) laufen ueber
        /// die Occluder-Hoerbarkeit und kommen hier nicht vor.</summary>
        public static PegelArt Von(SkriptArt s)
        {
            switch (s)
            {
                case SkriptArt.LokFahrt: return PegelArt.LokFahrt;
                case SkriptArt.LokLeerlauf: return PegelArt.LokLeerlauf;
                case SkriptArt.Flugzeug: return PegelArt.Flugzeug;
                case SkriptArt.BtrTurm: return PegelArt.BtrTurm;
                case SkriptArt.BtrHorn: return PegelArt.BtrHorn;
                case SkriptArt.Ausgangsfackel: return PegelArt.Fackel;
                case SkriptArt.Pfeifen: return PegelArt.Pfeifen;
                case SkriptArt.Ausgangstor: return PegelArt.Tor;
                default: return PegelArt.Unbekannt;
            }
        }

        public const string FeuerListe = "fire, flame, burn, bonfire, campfire";
        public const string GrossfeuerListe = "fire_car, car_fire, house_fire, big_fire, large_fire";
        public const string KaminListe = "fireplace, campfire, bonfire, stove, furnace";   // 0.8.6: amb_fireplace ist kein Grossfeuer
        public const string GeneratorListe = "generator, genset, diesel";
        public const string TropfenListe = "drip, drop, leak, water_drop";
        public const string HallenListe = "generators";   // 0.8.6: mehrere Aggregate (Labs "sound_generators_loop_stereo", Clip amb_generators_loop)

        static readonly string[] feuer = Liste(FeuerListe), gross = Liste(GrossfeuerListe), kamin = Liste(KaminListe), gen = Liste(GeneratorListe), tropf = Liste(TropfenListe),
            halle = Liste(HallenListe);
        static string[] Liste(string s) { var l = s.Split(','); for (int i = 0; i < l.Length; i++) l[i] = l[i].Trim(); return l; }

        /// <summary>Feste Quelle (Abschnitt 15/17) -> Pegelart aus Klasse und Namen (Quelle, Eltern, Grosseltern, Clip).</summary>
        public static PegelArt AusKlasse(QuellKlasse k, IList<string> namen)
        {
            switch (k)
            {
                case QuellKlasse.Fahrzeug: return PegelArt.PkwLeerlauf;
                case QuellKlasse.Laut: return PegelArt.Alarm;
                case QuellKlasse.Mittel:
                    if (LautSignalKern.Treffer(namen, gross) != null) return PegelArt.Grossfeuer;
                    if (LautSignalKern.Treffer(namen, halle) != null) return PegelArt.Maschinenhalle;
                    if (LautSignalKern.Treffer(namen, gen) != null) return PegelArt.Generator;
                    if (LautSignalKern.Treffer(namen, kamin) != null) return PegelArt.Lagerfeuer;
                    if (LautSignalKern.Treffer(namen, feuer) != null) return PegelArt.Grossfeuer;
                    return PegelArt.Maschine;
                default:
                    if (LautSignalKern.Treffer(namen, tropf) != null) return PegelArt.Tropfen;
                    if (LautSignalKern.Treffer(namen, feuer) != null) return PegelArt.Lagerfeuer;
                    return PegelArt.Klein;
            }
        }

        public static readonly double[] BandF = { 250, 1000, 4000 };

        /// <summary>Luftdaempfung je Band ueber d (ISO 9613-1, 20 C, 70 %, 101,325 kPa).</summary>
        public static void Luft(double d, double[] aus)
        {
            for (int b = 0; b < Hoerbarkeit.NB; b++) aus[b] = Iso9613.Alpha(BandF[b], 20, Iso9613.Bezugsdruck, 70) * Math.Max(0, d);
        }

        static readonly double[] luftTmp = new double[Hoerbarkeit.NB], luftA = new double[Hoerbarkeit.NB];

        /// <summary>0.8.6 Kennlinie: realer Pegel dB(A) am Hoerer ohne Hindernis = A-bewertete Summe der drei Bandpegel nach Abstandsgesetz
        /// der Quellart und Luft (in 1 m ohne Luft genau die Tabelle). NaN = Pegelart unbekannt.</summary>
        public static double FreiPegelA(PegelArt art, double abstand, double pegelVersatz = 0)
        {
            if (!Gilt(art)) return double.NaN;
            Luft(abstand, luftA);
            double ab = -GesetzDb(Mass(art), abstand), e = 0;
            for (int b = 0; b < Hoerbarkeit.NB; b++) e += Math.Pow(10, (BandPegel(art, b) + pegelVersatz - ab - luftA[b] + Hoerbarkeit.ABew[b]) / 10);
            return 10 * Math.Log10(e);
        }

        /// <summary>0.8.6 Kennlinie: Daemmung (dB) im gespielten Mass ohne Bandrechnung ("hearing" aus): lautheit x Kompression; Pegelart
        /// unbekannt: d x lautheit wie bisher.</summary>
        public static double Gestaucht(PegelArt art, double abstand, double d, double lautheit, double kennKnie, double kennSteigung)
        {
            if (!Gilt(art)) return Math.Max(0, d) * lautheit;
            return lautheit * Hoerbarkeit.Kompression(FreiPegelA(art, abstand), d, kennKnie, kennSteigung);
        }

        /// <summary>
        /// Hoerbarkeit je Band fuer eine Quelle ohne Spiel-Okklusion (Formel oben). dReal = reale Daemmung Wand + Raumweg je Band
        /// (L0/L1/L2), abstand = gerade Entfernung Quelle - Ohr, lGrund = Grundgeraeusch dB(A), lautheit = "Loudness factor",
        /// maxDb = "Max. insulation". false = Pegelart unbekannt (dann gilt das Verhalten ohne Hoerbarkeit).
        /// </summary>
        public static bool Rechnen(PegelArt art, double abstand, double lGrund, double[] dReal, double lautheit, double maxDb, Hoerbarkeit.Baender r, double pegelVersatz = 0,
                                   double kennKnie = double.NaN, double kennSteigung = 1.0)
        {
            r.Gilt = false;
            if (!Gilt(art)) return false;
            r.Gilt = true;
            var n = Hoerbarkeit.Grund(lGrund);
            Luft(abstand, luftTmp);
            double ab = -GesetzDb(Mass(art), abstand);   // 0.8.6 Abstandskurve: Nahfeld/Flaeche der Quellart (Punktquelle: 20 lg max(d, Nah))
            double d0 = Math.Max(0, dReal[0]);
            // 0.8.6 Kennlinie (RaumwegKern.cs, Hoerbarkeit.Kennlinie): Bezugsband-Abzug im gespielten Mass aus dem realen Pegel am Hoerer
            double a0 = lautheit * Hoerbarkeit.Kompression(FreiPegelA(art, abstand, pegelVersatz), d0, kennKnie, kennSteigung);
            r.Lautstaerke = double.MaxValue; r.ZusatzMax = 0;
            for (int b = 0; b < Hoerbarkeit.NB; b++)
            {
                double db = Math.Max(0, dReal[b]);
                double frei = BandPegel(art, b) + pegelVersatz - ab - luftTmp[b] - n[b];   // pegelVersatz: Offline-Test alter Logs (Tabelle damals)
                double knie = Math.Max(0, frei);
                double unter = db - frei;
                r.D[b] = db; r.Knie[b] = knie;
                r.Ueber[b] = Hoerbarkeit.MaskBreite - unter;
                r.Zusatz[b] = (1 - lautheit) * Math.Max(0, db - knie) + Hoerbarkeit.Verdeckung(unter, maxDb);   // 0.8.6: ohne 60-dB-Klippe
                r.G[b] = a0 + (b == 0 ? 0 : Math.Max(0, db - d0)) + r.Zusatz[b];
                if (r.G[b] < r.Lautstaerke) r.Lautstaerke = r.G[b];
                if (b > 0 && r.Zusatz[b] > r.ZusatzMax) r.ZusatzMax = r.Zusatz[b];
            }
            return true;
        }
    }
}
