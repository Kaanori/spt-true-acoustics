using System;

namespace Materialklang
{
    // blechbass (17.11.2026): Hochpass des Wegs nach der Physik.
    //
    // Befund (belegt, review-20261110/blechschuss-STAND.md Teil 2):
    //  - Jeder Gegnerschuss bekommt hp = 80 Hz (Klang-Log 17.11.: 98,6 % der Gunshots-Zeilen). Der Wert ist der "offene" Hochpass des
    //    Spiel-Presets (Gunshots: hpfSettings.frequencyCurve(0) = 80 Hz, aus resources.assets gelesen), den die Mod bisher behielt
    //    (Bauakustik.HochpassPhysik, Physik.HochpassOffen). Unitys Filter dazu ist 4-polig wie der Tiefpass (Annahme fuer den Hochpass;
    //    der Tiefpass ist gemessen): 80 Hz nehmen bei 63 Hz 8,9 dB, bei 50 Hz 16,4 dB, bei 40 Hz 24,1 dB weg.
    //  - In der Aufnahme 17.11. 18:30 verliert ein Feuerstoss auf freiem Weg (Bot12, Vector, 43 m, Daemmung 0,3 dB, lp offen) gegen
    //    seinen Roh-Clip 27 dB bei 63 Hz, 15 dB bei 100 Hz, 10 dB bei 126 Hz (relativ 1 kHz) - mehr als der 80-Hz-Filter rechnerisch
    //    wegnimmt (8,9 / 0,7 / 0,1 dB); die Form passt zu einem 4-poligen Hochpass um 150 Hz. Woher der Rest kommt (Skalierung von Unitys
    //    Hochpass?), ist offen - er sitzt aber im selben Glied: am Hochpass, den die Mod setzt.
    //  - Physik: Kein Ausbreitungsweg der Mod daempft den Bass staerker als die Mitten - Luft (ISO 9613-1) fast nichts im Bass,
    //    Massengesetz ~6 dB/Okt. MEHR Daemmung zu hohen Frequenzen, Beugung bringt tiefe Frequenzen besser um Kanten. Der Bass wird
    //    hinter Waenden leiser, aber das macht schon die Bandrechnung (Lautstaerke am Bezugsband, Tiefpass darueber). Ein zusaetzlicher
    //    Hochpass ist Klanggestaltung des Spiels, keine Physik.
    //  ANNAHME (nicht gerechnet): Unterhalb der Eigenresonanz leichter Bauteile (Tuerblatt, Blech; Masse-Feder) steigt die Daemmung zu
    //  tiefen Frequenzen wieder (Steifigkeitsbereich). Die Mod rechnet keine Baender unter 250 Hz; dafuer einen pauschalen Hochpass
    //  zu setzen, waere geraten.
    // Regel mit Schalter: Hochpass = neutral (HpNeutral, SEBs FilterBlend-Standard ohne Ziel 10 Hz), nie ueber dem Tiefpass.
    // Ohne Schalter exakt wie bisher (Bauakustik.HochpassPhysik).
    public static class HochpassKern
    {
        /// <summary>Neutraler Hochpass (Hz): SEB FilterBlend.DefaultHighPass ohne Ziele (FilterBlend.cs:11).</summary>
        public const float HpNeutral = 10f;

        /// <summary>Hochpass fuer das Spiel: physikalisch = neutral (kein Bassverlust auf dem Weg), sonst wie Bauakustik.HochpassPhysik.</summary>
        public static float Hochpass(float hpSpiel, float hpOffen, float lp, bool physikalisch)
        {
            if (!physikalisch) return Bauakustik.HochpassPhysik(hpSpiel, hpOffen, lp);
            float hp = Math.Min(HpNeutral, hpSpiel);
            return hp > lp ? lp : hp;
        }

        /// <summary>Daempfung (dB) eines Butterworth-Hochpasses mit 'pole' Polen bei f: 10 lg(1 + (fc/f)^(2 pole)).</summary>
        public static double MehrDb(double fc, double f, int pole) => 10.0 * Math.Log10(1.0 + Math.Pow(Math.Max(fc, 0.0) / Math.Max(f, 1e-6), 2 * pole));
    }
}
