namespace Materialklang
{
    // knacken (12.11.2026): Bandkorrektur des trockenen Teils JE QUELLE merken (Kaan, Raid 13:25: "laute Knack-Toene und roboterhafte
    // Geraeusche"; Bericht review-20261110/knacken-STAND.md).
    //
    // Ursache: hallsend (B2) setzt die Bandkurve des trockenen Teils nur fuer das Filter des Bilds, in dem die Lautstaerke voll gerechnet
    // wurde (Physik.Trocken -> filterOcc/filterBild == Time.frameCount). Das Spiel rechnet den Tiefpass aber AUCH im gemerkten Zweig
    // (CombinedSpatialAudioOccluder.OnUpdateOcclusionEffects -> ApplyCachedParamsToContainer -> ApplyOcclusionToSource ->
    // FilterCutoffCalculator.Compute, Dekompilat CombinedSpatialAudioOccluder.cs:155/190-207, SpatialAudioOccluder.cs:167), dort ohne
    // Lautstaerke-Rechnung. Dann galt die Kurve der SUMME, im naechsten vollen Bild wieder die des trockenen Teils: der Zielwert des
    // Tiefpasses wechselte Bild fuer Bild (Klang-Log 13:28:30.257/.305/.344 Bot13 walk_concrete3: 1374 -> 2089 -> 1370 Hz, frisch/gemerkt/
    // frisch). Der Gleiter (Stetigkeit.FrequenzSchritt) folgt einem solchen Wechsel zur Haelfte -> Tiefpass schwingt mit halber Bildrate.
    //
    // Regel: Die Lautstaerke des gemerkten Bilds ist die trockene des letzten vollen Bilds (OcclusionVolumeFactor aus dem Merker des
    // Spiels). Das Filter muss dieselbe Aufteilung tragen -> die Korrektur gehoert zur Quelle und gilt, bis ein volles Bild sie neu setzt
    // oder ohne Aufteilung loescht (oder die Quelle an den Pool zurueckgeht).
    internal sealed class TrockenFilter
    {
        bool gilt;
        double k1, k2;

        /// <summary>Volles Bild mit Aufteilung: Mehrdaempfung des trockenen Teils bei 1 kHz/4 kHz gegenueber der Summe (dB).</summary>
        public void Setzen(double mehr1k, double mehr4k) { k1 = mehr1k; k2 = mehr4k; gilt = true; }

        /// <summary>Volles Bild ohne Aufteilung, Raumhall-Zweig, Physik aus oder Freigabe an den Pool: das Filter folgt wieder der Summe.</summary>
        public void Loeschen() { gilt = false; k1 = k2 = 0; }

        /// <summary>Korrektur fuer das Filter dieser Quelle (volles UND gemerktes Bild). false = Kurve der Summe.</summary>
        public bool Holen(out double mehr1k, out double mehr4k)
        {
            mehr1k = gilt ? k1 : 0; mehr4k = gilt ? k2 : 0;
            return gilt;
        }
    }
}
