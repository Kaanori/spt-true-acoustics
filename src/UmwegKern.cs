using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // umwegsuche (13.11.2026): eigene Wegsuche SCHARF, aber nur fuer einen Fall - Hindernis-Zweig (Quelle und Hoerer im selben Raum des
    // Spiels) ohne gefundenen Umweg. Dort setzte die Mod bisher die Rueckfallgrenze (WegeKern.SchirmDb, 13,5/19,1/25 dB), weil die
    // Kantensuche des Spiels (EdgeDiffractionCalculator: EINE Kante nahe dem ersten Treffer) zwischen Einbauten nichts findet. Die eigene
    // Wegsuche kennt die Kanten des ganzen Raums (Beleg Factory 12.11. 23:06:57.858, Bot7 57 m: 2 Kanten -1,4/-5,3/-12,0 dB).
    // Reine Rechnung ohne Unity: (1) gilt ein Ergebnis der Wegsuche hier (Frische, Plausibilitaet), (2) Baender der Suche (250/1k/4k fest)
    // auf die Baender der Mod (Bezugsband einstellbar). Wie es in die Summe eingeht, steht in WegeKern.Auswerten (Parameter suche).
    // ------------------------------------------------------------------------------------------
    internal static class UmwegKern
    {
        /// <summary>Warum ein Ergebnis (nicht) angewendet wird - je Grund ein Zaehler in der Diagnose.</summary>
        internal enum Grund { Angewendet, KeinNetz, KeinErgebnis, Sichtlinie, KeinWeg, Aufgegeben, QuelleWeg, HoererWeg, ZuAlt, AndererRaum }

        /// <summary>
        /// Gilt das Ergebnis der Wegsuche fuer diese Rechnung des Spiels? Dieselben Frische-Regeln wie der Modus "Use" (Wegsuche.Fuer):
        /// Quelle hoechstens haltQ (3 m, Arbeiter.QuelleHaltM), Hoerer hoechstens haltH (3 m) vom Ort des Ergebnisses, juenger als maxAlter
        /// (3 s). Dazu die Plausibilitaet fuer diesen Fall:
        ///  - Sichtlinie in der Suche: das Spiel meldet verdeckt (alle Strahlen "zwei Objekte"), die Suche frei -> die beiden Geometrien sind
        ///    uneins (Annahme: Einbau fehlt im Netz oder Punkt verschoben); kein Umweg-Ergebnis, die Grenze bleibt.
        ///  - kein Kantenweg (nur Durchgang/Raumanteil) oder Suche ohne Weg aufgegeben: nichts, was die Grenze ersetzen koennte.
        ///  - Quellpunkt der Suche aus einem Koerper in einen ANDEREN Raum geschoben (Nebenbefund lampeweg 12.11.: Deckenplatte -> Raum
        ///    darueber): imRaum = false -> nicht anwenden (der Weg gehoert zu einer anderen Quelle).
        /// </summary>
        internal static Grund Pruefen(bool hat, bool frei, bool aufgegeben, int kantenWege, double abstandQ, double abstandH, double alterS,
                                      bool imRaum, double haltQ = 3.0, double haltH = 3.0, double maxAlter = 3.0)
        {
            if (!hat) return Grund.KeinErgebnis;
            if (abstandQ > haltQ) return Grund.QuelleWeg;
            if (abstandH > haltH) return Grund.HoererWeg;
            if (alterS > maxAlter) return Grund.ZuAlt;
            if (frei) return Grund.Sichtlinie;
            if (kantenWege <= 0) return aufgegeben ? Grund.Aufgegeben : Grund.KeinWeg;
            if (!imRaum) return Grund.AndererRaum;
            return Grund.Angewendet;
        }

        /// <summary>
        /// Baender der Suche (dbS bei fS, aufsteigend) auf die Frequenzen fMod: linear in lg f zwischen den Stuetzstellen, ausserhalb der
        /// erste/letzte Wert (nichts erfunden). Bei gleichen Frequenzen (Standard 250/1k/4k) genau die Werte der Suche.
        /// KeinWeg (kein Kantenweg in diesem Band) bleibt KeinWeg.
        /// </summary>
        internal static void Baender(double[] dbS, double[] fS, double[] fMod, double[] aus)
        {
            int n = fS.Length;
            for (int b = 0; b < aus.Length && b < fMod.Length; b++)
            {
                double f = fMod[b];
                if (f <= fS[0]) { aus[b] = dbS[0]; continue; }
                if (f >= fS[n - 1]) { aus[b] = dbS[n - 1]; continue; }
                int i = 0; while (i + 2 < n && f > fS[i + 1]) i++;
                if (dbS[i] <= WegeKern.KeinWeg || dbS[i + 1] <= WegeKern.KeinWeg) { aus[b] = Math.Max(dbS[i], dbS[i + 1]); continue; }
                double t = Math.Log(f / fS[i]) / Math.Log(fS[i + 1] / fS[i]);
                aus[b] = dbS[i] + (dbS[i + 1] - dbS[i]) * t;
            }
        }

        /// <summary>Energiesumme (dB) gerichteter Wege eines Bands: 10 lg Sum 10^(L_i/10) (inkohaerent, verschiedene Wege). Keiner -> KeinWeg.</summary>
        internal static double Summe(double e) => e > 0 ? 10.0 * Math.Log10(e) : WegeKern.KeinWeg;

        internal static string Text(Grund g)
        {
            switch (g)
            {
                case Grund.Angewendet: return "applied";
                case Grund.KeinNetz: return "no path network";
                case Grund.KeinErgebnis: return "no result yet";
                case Grund.Sichtlinie: return "search sees line of sight (geometries disagree)";
                case Grund.KeinWeg: return "search found no edge path";
                case Grund.Aufgegeben: return "search gave up without a path";
                case Grund.QuelleWeg: return "source moved > 3 m since the result";
                case Grund.HoererWeg: return "listener moved > 3 m since the result";
                case Grund.ZuAlt: return "result older than 3 s";
                case Grund.AndererRaum: return "search moved the source point into another room";
                default: return g.ToString();
            }
        }
    }
}
