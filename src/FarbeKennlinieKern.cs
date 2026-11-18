using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // farbekennlinie (12.11.2026): Klangfarbe folgt der Lautstaerke-Kennlinie.
    //
    // Befund (Messung Reserve 12.11. 14:53:37-43, review-20261110/roboter-reserve-STAND.md): Bunker-Schuesse 58-79 m, Hoerer draussen,
    // Raumweg ueber drei Oeffnungen. Reale Daemmung 16,3 / 18,2 / 28,9 dB (250 Hz / 1 kHz / 4 kHz). Die Kennlinie (Hoerbarkeit.Kompression,
    // Knie 85 dB(A), Steigung 0,2) staucht die Lautstaerke auf -4,0 dB, der Tiefpass folgt der VOLLEN Bandkurve (m = 1,9 / 12,6 dB ueber
    // dem Bezugsband) -> lp 2140 Hz, gespielt 4 kHz rund 11 dB unter 1 kHz: laut und dumpf ("roboterhafte Schuesse").
    //
    // Was die Kennlinie tut: sie bildet den realen Pegel am Ohr ohne Hindernis L_frei auf einen gespielten ab (K) und liefert als
    // Lautstaerke den Abzug A_0 = K(L_frei) - K(L_frei - D_0). Geeicht ist sie nur BREITBANDIG (dB(A) der Spiel-Clips, RaumwegKern.cs
    // "Wahl der Werte"). Ein Knie je Band ist nirgends gemessen.
    //
    // Gepruefte Kopplungen (Zahlen Bunker-Fall, Handrechnung in FarbeKennlinieTest):
    //  M0 (bis heute) Tiefpass aus der realen Bandkurve m_b:                    1k +1,9  4k +12,6 dB
    //  M1 Kennlinie JE BAND: Bandabzug A_b = K(L_frei) - K(L_frei - D_b), d. h. Neigung n_b = K(L_Ohr) - K(L_Ohr - m_b) mit
    //     L_Ohr = L_frei - D_0 (Knie je Band im selben Abstand zum eigenen Freipegel wie die Summe - die einzige Annahme ohne erfundenes
    //     Quellspektrum):                                                       1k +1,2  4k +11,3 dB
    //     -> aendert fast nichts: am Ohr liegt der Bunker-Schuss nur 0,7 dB ueber dem Knie (85,7 dB(A)); das 4-kHz-Band liegt 12,6 dB
    //        darunter (73 dB(A)) und faellt 1:1. Gestaucht wird bei M1 nur der Teil von D, der ueber dem Knie liegt - und der gehoert der
    //        Lautstaerke, nicht der Neigung. M1 behebt den gemessenen Fehler nicht.
    //  M2 (gewaehlt) Neigung mit dem Stauchungsfaktor des Wegs: g = A_0 / D_0, n_b = g * m_b:       1k +0,5  4k +3,1 dB
    //
    // Begruendung M2: Lautstaerke und Neigung sind zwei Merkmale DESSELBEN Hindernisses. Real ist ihr Verhaeltnis eine Eigenschaft des Wegs
    // (hier 12,6 dB Neigung auf 16,3 dB Pegelverlust). M0 spielt 12,6 dB Neigung auf 4,0 dB Pegelverlust - das Verhaeltnis ist um 1/g = 4x
    // verzerrt; genau diese Mischung "laut, aber sehr dumpf" ist der gemessene Befund. M2 staucht beide Merkmale mit demselben Faktor, das
    // Verhaeltnis bleibt das reale. g kommt aus genau der Rechnung, die die Lautstaerke schon macht (kein neuer Parameter, kein Bandknie,
    // kein Quellspektrum). Fuer Quellen unter dem Knie ist g = 1 exakt (Schritte, Stimmen, Schuesse mit Schalldaempfer ab ~100 m:
    // alles wie bisher). ANNAHME: Wahrnehmung - dass ein gleich gestauchtes Verhaeltnis natuerlicher klingt als die reale Neigung bei
    // gestauchtem Pegel, ist nicht gemessen; nur Kaan kann es im Raid beurteilen (F12-Schalter, wirkt sofort).
    //
    // Stetigkeit: K ist stetig differenzierbar (weiches Knie, Steigung von 1 auf s linear) -> g ist stetig in L_frei und D_0; fuer D_0 -> 0
    // geht g gegen die Steigung K'(L_frei) (Grenzwert, hier direkt verwendet). Bereich s <= g <= 1.
    // ------------------------------------------------------------------------------------------
    internal static class FarbeKennlinie
    {
        /// <summary>Steigung dK/dl der Kennlinie (Hoerbarkeit.Kennlinie): 1 unter dem Knie, s darueber, im weichen Knie linear dazwischen.</summary>
        public static double Steigung(double l, double knie, double steigung)
        {
            if (double.IsNaN(knie) || double.IsNaN(l) || !(steigung < 1)) return 1;
            double s = Math.Max(0.01, steigung), h = Hoerbarkeit.KennBreite / 2, x = l - knie;
            if (x <= -h) return 1;
            if (x >= h) return s;
            return 1 + (s - 1) * (x + h) / Hoerbarkeit.KennBreite;
        }

        /// <summary>Stauchungsfaktor g = Abzug im gespielten Mass / reale Daemmung des Bezugsbands, ohne Lautheitsfaktor
        /// (Hoerbarkeit.Kompression(lFrei, d0) / d0). lFrei NaN (Pegel unbekannt), Knie NaN oder Steigung &gt;= 1: 1. d0 sehr klein:
        /// Grenzwert K'(lFrei). Ergebnis in [Steigung, 1].</summary>
        public static double Faktor(double lFrei, double d0, double knie, double steigung)
        {
            if (double.IsNaN(lFrei) || double.IsNaN(knie) || !(steigung < 1)) return 1;
            if (lFrei <= knie - Hoerbarkeit.KennBreite / 2) return 1;   // ganz unter dem Knie: exakt 1 (nicht nur numerisch)
            double g = d0 > 1e-3 ? Hoerbarkeit.Kompression(lFrei, d0, knie, steigung) / d0 : Steigung(lFrei, knie, steigung);
            if (double.IsNaN(g)) return 1;
            return Math.Max(Math.Max(0.01, steigung), Math.Min(1.0, g));
        }

        /// <summary>Vergleich (nicht im Spiel benutzt): Neigung je Band nach M1 (Kennlinie je Band), lOhr = Pegel des Bezugsbands am Ohr
        /// (L_frei - D_0), m = reale Mehrdaemmung des Bands.</summary>
        public static double ProBand(double lOhr, double m, double knie, double steigung)
            => Hoerbarkeit.Kennlinie(lOhr, knie, steigung) - Hoerbarkeit.Kennlinie(lOhr - m, knie, steigung);
    }
}
