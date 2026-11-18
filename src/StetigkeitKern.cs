using System;

namespace Materialklang
{
    // 10.11.2026 (Fachpruefung, Befunde B2/B3/R1a/R1b) - reine Rechnung ohne Unity-Typen, damit der Offline-Test sie mituebersetzt.
    //
    // B2 - Routen-Ueberblendung lief nur im Takt des Spiels:
    //   SpatialAudioSystem.LateUpdate rechnet je Bild EINEN Prozessor (Round-Robin). Der Mischer (RoutenMischer, lineare Gewichte)
    //   wurde nur fortgeschrieben, wenn sein Rechner dran war; dazwischen stand die Wertung still und sprang beim naechsten
    //   Durchlauf um alles, was inzwischen haette passieren sollen (Bot43 f=243972: Gewichte 0,98 -> 0,35 in einem Bild,
    //   Pegelschritte bis 15 dB je Bild). Dazu waren lineare Energiegewichte in dB ungleichmaessig: zwei Routen 20 dB auseinander -
    //   der Pegel faellt fast nur im letzten Zehntel der Blendzeit.
    //   Loesung: der Mischer liefert sofort das Ziel (Summe der Routen mit ihren Rang-Gewichten), die Ueberblendung macht
    //   RoutenGleiter: die Daemmung JE BAND (dB) laeuft exponentiell aufs Ziel, x(t+dt) = Ziel + (x - Ziel) * exp(-dt/tau).
    //   Das ist bildratenunabhaengig (gleiche Endwerte bei 30 und 144 Bildern/s, nur die Abtastung ist feiner) und wird von
    //   Raumweg jedes Bild fortgeschrieben, unabhaengig davon, wann das Spiel den Rechner aufruft. Der Tiefpass wird aus den
    //   geglitteten Baendern gefittet, laeuft also stetig in Oktaven mit.
    //   tau = Blendzeit / 3 (wie WandDaempfung fuer Quellen ohne Okklusion): nach der eingestellten Blendzeit bleiben 5 % der
    //   dB-Differenz uebrig (20 dB -> 1 dB), nach 2 Blendzeiten 0,25 %.
    //
    // R1a/B3/R1b - Wiederverwendung (Pool) von Quellen, Rechnern und Containern: siehe WiederverwendungKern.
    internal sealed class RoutenGleiter
    {
        /// <summary>Numerisches Ende der Annaeherung: Rest unter 0,01 dB je Band (weit unter jeder Hoerschwelle fuer Pegelunterschiede,
        /// ~0,5 dB) -> exakt aufs Ziel, danach braucht der Zustand kein Fortschreiben mehr.</summary>
        public const double FertigDb = 0.01;
        public const int NB = 3;

        public readonly double[] Db = new double[NB];      // aktuell (geglittet), Daemmung je Band >= 0
        public readonly double[] Ziel = new double[NB];
        public bool Hat;                                   // hat einen Wert (sonst: naechstes Ziel gilt sofort)
        public bool Laeuft;                                // noch nicht am Ziel
        double t = double.NaN;

        public void Leeren() { Hat = false; Laeuft = false; t = double.NaN; }

        /// <summary>Bis 'jetzt' (Sekunden, beliebige stetige Uhr) aufs Ziel zulaufen.</summary>
        public void Fortschreiben(double jetzt, double tau)
        {
            if (!Hat) return;
            double dt = double.IsNaN(t) ? 0 : Math.Max(0, jetzt - t);
            t = jetzt;
            if (!Laeuft) return;
            double a = tau > 0 ? Math.Exp(-dt / tau) : 0;
            bool l = false;
            for (int i = 0; i < NB; i++)
            {
                double d = (Db[i] - Ziel[i]) * a;
                if (Math.Abs(d) <= FertigDb) d = 0; else l = true;
                Db[i] = Ziel[i] + d;
            }
            Laeuft = l;
        }

        /// <summary>Neues Ziel aus einer Rechnung. Erster Wert (Hat = false): sofort - ein neuer Klang blendet nicht ein.
        /// Sonst wird erst bis 'jetzt' mit dem alten Ziel fortgeschrieben, dann laeuft der Wert ab hier zum neuen.</summary>
        public bool ZielSetzen(double[] ziel, double jetzt, double tau)
        {
            // 10.11. (spitzen, N2): ein nicht endliches Ziel (NaN/Infinity aus einer Rechnung) wird nie uebernommen - sonst bliebe der
            // Gleiter fuer immer NaN (exp-Schritt) und das Spiel bekaeme NaN als Lautstaerke/Filter. Zustand bleibt unveraendert.
            if (!Endlich(ziel)) return false;
            if (!Hat)
            {
                for (int i = 0; i < NB; i++) Db[i] = Ziel[i] = ziel[i];
                Hat = true; Laeuft = false; t = jetzt;
                return true;
            }
            Fortschreiben(jetzt, tau);
            bool l = false;
            for (int i = 0; i < NB; i++)
            {
                Ziel[i] = ziel[i];
                if (Math.Abs(Db[i] - Ziel[i]) > FertigDb) l = true;
            }
            if (!l || tau <= 0) for (int i = 0; i < NB; i++) Db[i] = Ziel[i];
            Laeuft = l && tau > 0;
            return true;
        }

        /// <summary>10.11. (spitzen, Punkt 5): Startwert eines LAUFENDEN Klangs = der Wert, den das Spiel zuletzt angewandt hat (dB, alle
        /// Baender gleich - der Fruehabbruch des Spiels ist flach: Wertung 1 = Max. insulation, 0 = kein Raumweg). Das naechste Ziel
        /// gleitet dann von dort aus, statt als Sofortwert zu springen. Nicht endlich -> nichts (der erste Wert gilt dann sofort).</summary>
        public bool Vorbelegen(double db, double jetzt)
        {
            if (double.IsNaN(db) || double.IsInfinity(db)) return false;
            for (int i = 0; i < NB; i++) Db[i] = Ziel[i] = db;
            Hat = true; Laeuft = false; t = jetzt;
            return true;
        }

        /// <summary>N2: alle Baender endlich.</summary>
        public static bool Endlich(double[] db)
        {
            if (db == null || db.Length < NB) return false;
            for (int i = 0; i < NB; i++) if (double.IsNaN(db[i]) || double.IsInfinity(db[i])) return false;
            return true;
        }

        /// <summary>Zeitkonstante zur eingestellten Blendzeit (Sekunden): 95 % der Aenderung nach der Blendzeit.</summary>
        public static double Tau(double blendS) => blendS > 0 ? blendS / 3.0 : 0.0;
    }

    // Wiederverwendung (Pool) im Spiel - die Ursachen, aus dem Spielcode (Assembly-CSharp 4.1.6):
    //  R1a: SEB gibt ruhende Pool-Quellen nach 2 s mit BetterSource.Release() zurueck, ein anderer Schuetze kann dieselbe Quelle im
    //       selben Update wieder holen. Richtung merkt die echte Position je BetterSource; der Eintrag wurde erst im naechsten
    //       LateUpdate entfernt, und Echt() pruefte nicht, ob das Spiel die Quelle inzwischen selbst bewegt hat -> der neue Besitzer
    //       bekam die Position des alten (16:47:22.082: 105,7 m statt 88,6 m; Luft/Wald/Abstand/Hoerbarkeit rechneten falsch).
    //  R1b: ParallelSoundPropagationCalculator.OnUpdate bricht ab, wenn der Hoerer weiter weg ist als die Reichweite des Containers
    //       (_propagationScore = 1 = stumm). Kommt ein Schuss in den Container, waechst die Reichweite, das Spiel wendet aber sofort
    //       (OnSourceAdded, ProcessSourceOcclusion) den alten Abbruchwert 1 an, und seine Glaettung laeuft danach von "stumm" her
    //       auf ("Schuss beginnt -21...-24 dB dumpf und oeffnet sich"). Dasselbe, wenn ein Prozessor aus "OutOfRange" zurueckkommt
    //       (er rechnete dort nicht) oder neu entsteht (Rechner aus dem Pool traegt den Wert seiner vorigen Quelle).
    internal static class WiederverwendungKern
    {
        /// <summary>Gleiche Grenze wie Richtung.Uebernehmen/Zurueck: Abweichung (Quadrat, m^2) der lokalen Position vom zuletzt
        /// geschriebenen Wert, ab der die Position als "vom Spiel gesetzt" gilt (float-Rundung einer unveraenderten Position = 0).</summary>
        public const double EigenQ = 1e-10;

        /// <summary>R1a: gilt die gemerkte echte Position noch? Nur wenn die Verschiebung nachweislich noch von uns stammt:
        /// Eintrag hat sie gesetzt, gleicher Elternknoten, und die lokale Position ist exakt die zuletzt geschriebene.</summary>
        public static bool BasisGilt(bool hat, bool elternGleich, double abweichungQ) => hat && elternGleich && abweichungQ <= EigenQ;

        /// <summary>B3: Zustand eines Rechners gehoert zu einem anderen Container -> verwerfen (nie vom Vorgaenger weiterblenden).</summary>
        public static bool BehaelterGewechselt(object alt, object neu) => alt != null && !ReferenceEquals(alt, neu);

        /// <summary>R1b: ist der gespeicherte Wert ein Fruehabbruch "ausser Reichweite" (Wertung 1), der fuer die JETZIGE Reichweite
        /// nicht mehr gilt? Gleiche Bedingung wie im Spiel: Abbruch nur bei d^2 > Reichweite^2.</summary>
        public static bool FruehabbruchVeraltet(bool fruehabbruch, float wertung, double abstandQ, double reichweiteQ)
            => fruehabbruch && wertung >= 1f && abstandQ <= reichweiteQ;

        /// <summary>R1b: Prioritaetswechsel, nach dem der Rechner neu beginnen muss (vorher OutOfRange = hat nicht gerechnet).</summary>
        public static bool ZurueckInReichweite(int alt, int neu, int ausserReichweite) => alt == ausserReichweite && neu != ausserReichweite;

        // 10.11. (spitzen, Punkt 5 - Treppenhaus 18:44:56-18:45:10, Lampen src#-1514004/-1514044): faellt ein laufender Rechner in den
        // Fruehabbruch des Spiels (kein Raumweg/Hoererraum gewechselt, Wertung 1), verwirft SpaetNach seinen Zustand; der naechste
        // echte Wert galt dann SOFORT (Gleiter ohne Wert) -> -60 -> -21,9 dB in einem Bild. "Sofort" ist nur fuer einen Klang richtig,
        // der in diesem Bild beginnt (Neubeginn); ein laufender Klang gleitet vom zuletzt ANGEWANDTEN Wert aus.

        /// <summary>Gleiter eines Rechners ohne Wert vom angewandten Wert aus starten? Nur wenn der Klang nicht gerade neu beginnt und
        /// der angewandte Wert bekannt ist (Occluder dieses Rechners bekannt, Wert endlich).</summary>
        public static bool VomAngewandtenStarten(bool gleiterHat, bool neuerKlang, double angewandtDb)
            => !gleiterHat && !neuerKlang && !double.IsNaN(angewandtDb) && !double.IsInfinity(angewandtDb);

        /// <summary>Angewandte Wertung des Spiels (PropagationEffect, 0..1) als Daemmung in dB; nicht endlich -> NaN (unbekannt).</summary>
        public static double AngewandtDb(double effekt, double dyn)
        {
            if (double.IsNaN(effekt) || double.IsInfinity(effekt) || double.IsNaN(dyn) || double.IsInfinity(dyn) || dyn <= 0) return double.NaN;
            return Math.Max(0, Math.Min(1, effekt)) * dyn;
        }

        /// <summary>N2: Wertung (0..1) aus der Daemmung des Bezugsbands fuer _propagationScore - nie NaN. Nicht endlich -> der bisher
        /// gueltige Wert 'alt' (wenn er gueltig ist), sonst 1 (Max. insulation: lieber zu leise als NaN/zu laut).</summary>
        public static float Wertung(double db0, double dyn, float min, float alt)
        {
            if (!double.IsNaN(db0) && !double.IsInfinity(db0) && !double.IsNaN(dyn) && !double.IsInfinity(dyn) && dyn > 0)
            {
                double w = db0 / dyn;
                return (float)Math.Max(min, Math.Min(1.0, w));
            }
            return !float.IsNaN(alt) && alt >= min && alt <= 1f ? alt : 1f;
        }
    }
}
