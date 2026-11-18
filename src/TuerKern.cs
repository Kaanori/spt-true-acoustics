// ---------------------------------------------------------------
//  0.8.6: Tuerblatt und Fensterscheibe nach dem Massengesetz der Mod. torfluegel (12.11.2026): feste Torfluegel (unten). Reine Rechnung ohne Unity (Offline-Test,
//  Demo-Seite). Spiel-Teil (Material, Dickenmessung am Collider, Portal): TuerenFenster.cs.
//
//  Kaan 09.11.2026: "Wir koennen nicht pauschal Werte fuer Tueren geben. Wenn das Material es durchlaesst, hoert man es
//  entsprechend gedaempft." Bis 0.8.5 bekam jede geschlossene Tuer/jedes heile Fenster einen festen Anteil der
//  Hoechstdaemmung je Typ (duennes Holz 0,35 / dickes Holz 0,45 / duennes Metall 0,6 / dickes Metall 0,8 / Fenster 0,55
//  x 60 dB = 21/27/36/48/33 dB), gleich bei 250 Hz, 1 kHz und 4 kHz (die Hoehen kamen nur aus einer Tiefpass-Abbildung).
//
//  Jetzt (dieselben Formeln wie die Waende, Bauakustik.cs):
//    m   = Dichte * Dicke                      (massiv)    bzw.  Dichte * 2 * Blech   (Hohlkoerper: zwei Bleche, Luft dazwischen)
//    TL  = 20 lg(m f) - 47 dB                  (Feldeinfall-Massengesetz)
//    fc  = c0^2 / (2 pi h) * sqrt(12 rho (1 - nu^2) / E),  h = Dicke der einzelnen Platte (massiv: Dicke, hohl: Blech)
//    oberhalb fc/2: Koinzidenz nach Sharp/Cremer (Bauakustik.Koinzidenz, Verlustfaktor eta = "Coincidence loss factor")
//    Ergebnis je Band 0 .. Max. insulation.
//  Hohlkoerper-Regel: eine Metalltuer, die dicker gemessen wird als "Solid up to" ihrer Gruppe (Abschnitt 0), ist ein Kasten
//  aus zwei Blechen (Stahltuer/Stahlzargentuer: Bleche um 1-1,5 mm, Fuellung ohne nennenswerte Masse). Die Fuellung und die
//  Masse-Luft-Masse-Resonanz werden nicht gerechnet (die beiden Bleche zaehlen als eine Masse: eher zu wenig Daemmung).
//  0.8.6 Livetest-Fix (Wegsuche Factory 10.11.2026: Wege durch geschlossene Tueren -19,6/-34,4/-52,5 dB fuer EIN Blatt, bis
//  -105 dB @4 kHz fuer zwei): Fugen/Spalte um das Blatt sind ein paralleler Durchlass. Eine Tuer ist ein zusammengesetztes Bauteil:
//    tau = (1 - s) * tau_Blatt + s * tau_Spalt,  R = -10 lg tau      (Flaechenanteile, EN 12354-1 / Bies & Hansen, "Engineering
//  Noise Control", Abschn. 7.2.5 "composite transmission loss": 1 % offene Flaeche begrenzt R auf 20 dB, 0,1 % auf 30 dB).
//    tau_Spalt = 1 (Flaechenanteils-Naeherung): ein Spalt, schmal gegen die Wellenlaenge, laesst je Flaeche im Mittel etwa die
//  einfallende Leistung durch (Gomperts & Kihlman 1967, Acustica 18: tau des Schlitzes um 1, an der Tiefenresonanz darueber, tief
//  darunter weniger - im Oktavmittel ~1).
//    s: Innentuer ohne Dichtung, 0,9 x 2,0 m, Luft 3 mm an Seiten/oben und 6-10 mm am Boden = 0,02 m2 = 1,2 %; der Falz der Zarge
//  verdeckt einen Teil -> wirksam 0,5 % (FugeTuer, R hoechstens 23 dB; gemessen: ungedichtete Innentueren R'w 15-20 dB, gedichtete
//  25-30 dB, DIN 4109 Bbl. 1 / Fasold & Veres "Schallschutz und Raumakustik in der Praxis"). Fenster mit Rahmen und Dichtung:
//  0,1 % (FugeFenster, hoechstens 30 dB; Fenster R'w 25-35 dB). Gitter/Dichte 0 bleibt 0 dB.
//
//  Plattenwerte (E-Modul, Querkontraktion), typische Tabellenwerte: Holz laengs der Faser 9 GPa / 0,3 (wie Physik.cs),
//  Stahl 200 GPa / 0,3, Kalk-Natron-Glas 70 GPa / 0,2 (EN 572-1). Weich (Kunststoff/Stoff) und Unbekannt: nur Massengesetz.
// ---------------------------------------------------------------
using System;

namespace Materialklang
{
    /// <summary>Woher die Blattdicke kommt (Log).</summary>
    internal enum DickeQuelle : byte { Gemessen, Ersatz, Unplausibel }

    /// <summary>Ein geschlossenes Tuerblatt oder eine Scheibe: was das Massengesetz braucht.</summary>
    internal struct TuerBlatt
    {
        public int Gruppe;          // enum Gruppe
        public double Dichte;       // kg/m3 (Abschnitt 0, Density der Gruppe)
        public double Dicke;        // m, wirksame Gesamtdicke (massiv) bzw. Aussenmass (hohl, nur Log)
        public bool Hohl;           // zwei Bleche
        public double Platte;       // m, Dicke der einzelnen Platte (massiv = Dicke, hohl = Blech)
        public double EModul, Nu;   // Pa; 0 = kein Plattenmodell (nur Massengesetz)
        public DickeQuelle Quelle;
        public double Gemessen;     // m, Messwert (NaN = keine Messung)
        public double Fuge;         // 0.8.6: offener Flaechenanteil der Fugen/Spalte (0 = dicht, nur das Blatt)

        /// <summary>Flaechenmasse kg/m2.</summary>
        public double Masse => Dichte * (Hohl ? 2.0 * Platte : Math.Max(Dicke, 0.002));
    }

    /// <summary>Vorgaben aus Abschnitt 4 (und 0) fuer die Blattwahl.</summary>
    internal struct TuerVorgaben
    {
        public bool Messen;          // "Door leaf: use measured thickness"
        public double ErsatzHolz;    // m, Tuerblatt aus Holz/Kunststoff/unbekannt ohne Messung (Innentuerblatt 40 mm)
        public double ErsatzGlas;    // m, Scheibe ohne Messung (Floatglas 6 mm)
        public double Blech;         // m, Blech einer Metalltuer (Hohlkoerper)
        public double FugeTuer, FugeFenster;   // 0.8.6: offener Flaechenanteil der Fugen (Tuer / Fenster), 0 = dicht

        public static TuerVorgaben Standard => new TuerVorgaben { Messen = true, ErsatzHolz = 0.04, ErsatzGlas = 0.006, Blech = 0.0015, FugeTuer = TuerKern.FugeTuer, FugeFenster = TuerKern.FugeFenster };
    }

    internal static class TuerKern
    {
        // Gruppen (wie enum Gruppe in Plugin.cs; hier als Zahl, damit der Kern ohne Plugin.cs uebersetzt)
        public const int MetallDick = 3, MetallDuenn = 4, HolzDick = 5, HolzDuenn = 6, Glas = 7, Weich = 8, Gitter = 9, Tuer = 13;

        // Plausibilitaet der Messung (m). Glas: eine Scheibe 3-12 mm (Floatglas 3-12 mm EN 572-2, VSG 10 mm); dicker = Rahmen/Kasten
        // des Fenster-Meshs -> Ersatz-Scheibe. Holz/Kunststoff: Tuerblatt 10-80 mm (Innentuer 40 mm, Haus-/Wohnungstuer 50-70 mm);
        // dicker = Zarge/Griff/Schraegmessung -> Ersatzdicke. Die Grenzen sind ANNAHMEN, keine Normwerte.
        public const double GlasMin = 0.003, GlasMax = 0.012, BlattMin = 0.01, BlattMax = 0.08;

        /// <summary>0.8.6: wirksamer offener Flaechenanteil der Fugen (siehe Kopf): Tuer 0,5 %, Fenster 0,1 %.</summary>
        public const double FugeTuer = 0.005, FugeFenster = 0.001;

        /// <summary>0.8.6: Blatt und Fuge parallel: R = -10 lg((1 - s) 10^(-R_Blatt/10) + s). s = 0 -> R_Blatt.</summary>
        public static double MitFuge(double blattDb, double s)
        {
            if (!(s > 0)) return blattDb;
            s = Math.Min(s, 1.0);
            return -10.0 * Math.Log10((1.0 - s) * Math.Pow(10, -blattDb / 10.0) + s);
        }

        /// <summary>
        /// 0.8.6 Tuerfuge-Fix (Factory 10.11.2026, Tor #209 45 dB statt hoechstens 23 dB): geschlossener Teil eines Portals OHNE
        /// Blattmodell (kein Portalobjekt zum Job-Index, Abschnitt 4 aus). Annahmen: Blatt = der einzige bekannte Wert dieses Portals,
        /// die Schliessung des Spiels auf der dB-Skala der Mod (blattDb = Schliessung x WallOcclusion x Max. insulation); Fuge = die
        /// einer ungedichteten Innentuer (FugeTuer 0,5 %, siehe Kopf): ein schliessbares Portal des Spiels ist eine Tuer/ein Tor in
        /// einer Zarge, Fenster haben immer ein Blattmodell (Ersatz-Scheibe, TuerenFenster.ErsatzFenster). Ohne die Fuge war das ein
        /// dichtes Blatt bis 60 dB - physikalisch gibt es das bei einer Tuer nicht. Statische Portale sind nie zu (z0 = 0, kein Einfluss).
        /// </summary>
        public static double OhneBlattDb(double blattDb, double maxDb) => Math.Max(0, Math.Min(maxDb, MitFuge(Math.Max(0, blattDb), FugeTuer)));

        /// <summary>Plattenwerte je Gruppe (E in Pa, nu); 0 = nur Massengesetz.</summary>
        public static void Platte(int gruppe, out double eModul, out double nu)
        {
            switch (gruppe)
            {
                case MetallDick: case MetallDuenn: eModul = 200e9; nu = 0.3; return;
                case HolzDick: case HolzDuenn: case Tuer: eModul = 9e9; nu = 0.3; return;
                case Glas: eModul = 70e9; nu = 0.2; return;
                default: eModul = 0; nu = 0; return;
            }
        }

        /// <summary>
        /// Blatt aus Material und gemessener Dicke (NaN = keine Messung). massiv = "Solid up to" der Gruppe (Abschnitt 0), fenster =
        /// Fensterportal (Plausibilitaet einer Scheibe; der Aufrufer gibt dort Gruppe Glas mit ihrer Dichte).
        /// </summary>
        public static TuerBlatt Waehlen(int gruppe, double dichte, double massiv, double gemessen, bool fenster, in TuerVorgaben v)
        {
            var b = new TuerBlatt { Gruppe = gruppe, Dichte = Math.Max(0, dichte), Gemessen = gemessen };
            bool hat = v.Messen && !double.IsNaN(gemessen) && gemessen > 0;
            double t = hat ? gemessen : double.NaN;
            b.Quelle = hat ? DickeQuelle.Gemessen : DickeQuelle.Ersatz;
            if (gruppe == MetallDick || gruppe == MetallDuenn)
            {
                // massives Blech bis "Solid up to", sonst Kasten aus zwei Blechen (auch ohne Messung)
                if (hat && t <= massiv) { b.Dicke = b.Platte = t; }
                else { b.Hohl = true; b.Platte = Math.Max(v.Blech, 0.0005); b.Dicke = hat ? t : 2 * b.Platte; }
            }
            else if (gruppe == Glas || fenster)
            {
                if (!hat) t = v.ErsatzGlas;
                else if (t > GlasMax) { t = v.ErsatzGlas; b.Quelle = DickeQuelle.Unplausibel; }
                else if (t < GlasMin) t = GlasMin;
                b.Dicke = b.Platte = t;
            }
            else
            {
                if (!hat) t = v.ErsatzHolz;
                else if (t > BlattMax) { t = v.ErsatzHolz; b.Quelle = DickeQuelle.Unplausibel; }
                else if (t < BlattMin) t = BlattMin;
                b.Dicke = b.Platte = t;
            }
            Platte(gruppe, out b.EModul, out b.Nu);
            b.Fuge = gruppe == Gitter ? 0 : (gruppe == Glas || fenster ? v.FugeFenster : v.FugeTuer);
            return b;
        }

        /// <summary>
        /// Daemmung (dB) des geschlossenen Blatts bei der Frequenz f: Massengesetz, darueber (oberhalb fc/2) Koinzidenz nach
        /// Sharp/Cremer, wenn koinzidenz und ein Plattenmodell gilt. 0 .. maxDb.
        /// </summary>
        public static double Db(in TuerBlatt b, double f, bool koinzidenz, double eta, double maxDb)
        {
            double m = b.Masse;
            if (m <= 0 || f <= 0) return 0;
            double r = Bauakustik.Massengesetz(m, f);
            if (koinzidenz && b.EModul > 0)
            {
                double fc = Bauakustik.Koinzidenzfrequenz(b.Dichte, Math.Max(b.Platte, 0.0005), b.EModul, b.Nu);
                r = Bauakustik.Koinzidenz(r, m, f, fc, eta);
            }
            r = MitFuge(Math.Max(0, r), b.Fuge);   // 0.8.6: Fugen als paralleler Durchlass
            return Math.Max(0, Math.Min(maxDb, r));
        }

        static readonly TuerVorgaben GlasOhneFuge = new TuerVorgaben { Messen = true, ErsatzHolz = 0.04, ErsatzGlas = 0.006, Blech = 0.0015 };

        /// <summary>
        /// 10.11. (akustik-wand, Befund 3): EINE Glasregel fuer jedes getroffene Glas (Wand zwischen Raeumen, T-Strahlen, Mod-Hindernis,
        /// Wegsuche) - dieselbe wie das Fensterportal (Waehlen mit fenster = true): gemessen 3-12 mm = eine Scheibe, dicker = Rahmen/Kasten des
        /// Fenster-Meshs -> Ersatzscheibe 6 mm (Floatglas, EN 572-2), duenner -> 3 mm; Plattenwerte 70 GPa / 0,2 (EN 572-1) mit Koinzidenz
        /// (fc = 2,0 kHz bei 6 mm); Rahmenfuge 0,1 % (FugeFenster, R hoechstens 30 dB). Bis 10.11. wurde ein 0,1-m-Fenster-Mesh im Wandweg zu
        /// zwei 20-mm-Scheiben ("Solid up to" 0,02 m, hohl) = 100 kg/m2: 41/53/60 dB bei 250 Hz/1 kHz/4 kHz statt ~24/29/30 dB.
        /// dicke = gemessene Dicke senkrecht zur Flaeche (NaN = keine Messung); fuge = false nur fuer den Vergleich ohne Rahmen.
        /// </summary>
        public static double GlasDb(double dichte, double dicke, double f, bool koinzidenz, double eta, double maxDb, bool fuge = true)
        {
            var b = Waehlen(Glas, dichte, 0, dicke, true, fuge ? TuerVorgaben.Standard : GlasOhneFuge);
            return Db(b, f, koinzidenz, eta, maxDb);
        }

        /// <summary>10.11.: Flaechenmasse (kg/m2) der Scheibe nach derselben Regel wie GlasDb.</summary>
        public static double GlasMasse(double dichte, double dicke) => Waehlen(Glas, dichte, 0, dicke, true, TuerVorgaben.Standard).Masse;

        // ---------------------------------------------------------------
        //  torfluegel (12.11.2026): feste Fluegel eines Tor-/Tuerportals. Klang-Log Factory 23:05:45 (Marke 2, Hoerer 1,6 m hinter Tor
        //  #209): die Trennflaeche To_Entrance_1 <-> Entrance_1_1 ist ein zweifluegeliges Stahltor 5,3 x 4,4 m mit Schlupftuer. Das
        //  Audio-Portal des Spiels ist nur die Schlupftuer (2,1 m2, Door-Objekt "Enterance_Gate_01_Door"); die beiden Fluegel (zusammen
        //  20,0 m2, Strahlenraster am Mesh, review-20261110/torfluegel/sonde) kamen im Raumweg gar nicht vor und im Wandweg (nur bis
        //  40 m) als 2 x 1 cm Vollstahl ("Solid up to" Metal thick, 157 kg/m2, 44,9 dB). Ein Torfluegel ist ein Torblatt, das sich im
        //  Spiel nicht oeffnet: dieselbe Bauart wie das Blatt (Stahlblech auf Rahmen, Hohlkoerper aus zwei Blechen) mit denselben
        //  ungedichteten Fugen. Herstellerangaben Industrietore Rw 20-26 dB (Teckentrup SLW 40/80 23 dB, SW 40 24 dB, Novoferm Thermo
        //  25 dB, Efaflex 20-25 dB, Stahl-Rolltor 26 dB), Mitte 23 dB = Blattregel der Tuer (21,9/22,9/23,0 dB bei 250/1k/4k).
        //  Zusammengesetztes Bauteil (EN ISO 12354-1 / Bies & Hansen 7.2.5): durch die Oeffnung geht I (S_Portal tau_Blatt + Sum S_i tau_i);
        //  bezogen auf das Portal-Rechteck, mit dem die Raumwege rechnen:
        //    tau_eff = tau_Blatt + Sum S_i tau_i / S_Portal   (hoechstens 1)
        //  Ein Teil der Baugruppe zaehlt als Fluegel, wenn er eine Platte ist: getroffene Flaeche >= FluegelAnteilMin der Huelle in der
        //  Portalebene. Factory (Sonde, alle 92 Tuerportale): Torfluegel 67-96 %, Zargen/Torrahmen 6-17 % -> 0,5 trennt beide
        //  (Annahme; jeder Wert zwischen 0,2 und 0,65 gibt dasselbe Ergebnis).
        // ---------------------------------------------------------------
        public const double FluegelAnteilMin = 0.5;

        /// <summary>torfluegel: ist ein Teil der Tuer-/Torbaugruppe ein Fluegel (Platte) statt Rahmen? treffer = getroffene Flaeche
        /// (m2) in der Portalebene, huelle = Rechteck des Teils in derselben Ebene (m2).</summary>
        public static bool IstFluegel(double treffer, double huelle) => huelle > 0 && treffer > 0 && treffer >= FluegelAnteilMin * huelle;

        /// <summary>torfluegel: Transmissionsgrad des Portals mit festen Fluegeln (siehe oben). sTauFluegel = Sum S_i tau_i (m2).</summary>
        public static double TauMitFluegeln(double tauBlatt, double flaechePortal, double sTauFluegel)
        {
            if (!(flaechePortal > 0) || !(sTauFluegel > 0)) return tauBlatt;
            return Math.Min(1.0, Math.Max(0, tauBlatt) + sTauFluegel / flaechePortal);
        }

        /// <summary>torfluegel: dasselbe in dB (0 .. maxDb).</summary>
        public static double DbMitFluegeln(double blattDb, double flaechePortal, double sTauFluegel, double maxDb)
        {
            double t = TauMitFluegeln(Math.Pow(10, -blattDb / 10.0), flaechePortal, sTauFluegel);
            return t <= 0 ? maxDb : Math.Max(0, Math.Min(maxDb, -10.0 * Math.Log10(t)));
        }

        /// <summary>Koinzidenzfrequenz (Hz) der einzelnen Platte, 0 = kein Plattenmodell (Log).</summary>
        public static double Fc(in TuerBlatt b) => b.EModul > 0 ? Bauakustik.Koinzidenzfrequenz(b.Dichte, Math.Max(b.Platte, 0.0005), b.EModul, b.Nu) : 0;

        /// <summary>Alle drei Baender (Bezug / 1 kHz / 4 kHz) auf einmal.</summary>
        public static void Baender(in TuerBlatt b, double f0, double f1, double f2, bool koinzidenz, double eta, double maxDb, out double d0, out double d1, out double d2)
        {
            d0 = Db(b, f0, koinzidenz, eta, maxDb);
            d1 = Db(b, f1, koinzidenz, eta, maxDb);
            d2 = Db(b, f2, koinzidenz, eta, maxDb);
        }

        /// <summary>Ein Wort fuer das Log ("measured", "fallback", "implausible -> fallback").</summary>
        public static string QuelleText(DickeQuelle q) => q == DickeQuelle.Gemessen ? "measured" : q == DickeQuelle.Ersatz ? "fallback" : "implausible measurement -> fallback";
    }
}
