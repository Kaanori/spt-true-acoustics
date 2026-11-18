using System;

namespace Materialklang
{
    // sendsprung (12.11.2026): Reverb-Send einer laufenden Quelle gleitet statt zu springen (Bericht review-20261110/knacken-STAND.md).
    //
    // Befund: Der Send folgt im Normalfall dem geglitteten Diffus-Zusatz der Route (Raumweg Zustand.DGleit, tau = Blende/3) und aendert
    // sich dann stetig. Hart springt er nur bei einem Zweigwechsel in Physik.Trocken: Aufteilung -> keine Aufteilung (SendZurueck, bis
    // -24 dB auf den Preset), Raumhall-Zweig <-> normaler Zweig, oder wenn ein anderer Weg (Wand zwischen Raeumen / Route / Hindernis)
    // den Zusatz liefert. Der Wechsel selbst ist berechtigt (der Weg hat sich geaendert), der Sprung nicht: ein Pegelsprung von 20 dB am
    // Hall-Eingang ist eine Unstetigkeit des Eingangssignals (Knack-Impuls in den Hall). Ob Metas Parameter 11 je Block rampt, ist nicht
    // belegt (ANNAHME: nein) -> hier dieselbe Gleitregel wie fuer die Route (exponentiell, tau = Blende/3, Standard 100 ms).
    //
    // Regeln:
    //  - erster Wert einer Quelle und Quellen, die still/gerade gestartet sind (Stetigkeit.SofortZulassen): sofort (ein neuer Klang
    //    beginnt nicht mit dem Send seines Vorgaengers und blendet seinen Hall nicht ein);
    //  - laufender Klang: Ziel aendern, der geschriebene Wert laeuft exponentiell hin (Rest unter FertigDb -> exakt aufs Ziel);
    //  - Freigabe an den Pool (Release, vor Stop()): hart auf den Preset und vergessen.
    // Gegleitet wird der Zusatz ueber dem Preset (dB), nicht der absolute Send - der Preset haengt am Spatializer (mono/stereo).
    internal sealed class SendGleiter
    {
        /// <summary>Rest unter 0,05 dB -> exakt aufs Ziel (weit unter der Unterscheidbarkeit von Pegeln, ~0,5 dB).</summary>
        public const double FertigDb = 0.05;

        bool hat;
        double ist, ziel, t = double.NaN;
        /// <summary>Welcher Spatializer den Zusatz traegt (true = Meta der Quelle selbst, false = Hall-Zwilling stereo).</summary>
        public bool Raeumlich;

        public bool Hat => hat;
        public double Ist => ist;
        public double Ziel => ziel;
        public bool Laeuft => hat && ist != ziel;
        /// <summary>Weicht der geschriebene Send vom Preset ab (dann muss die Freigabe ihn zuruecksetzen)?</summary>
        public bool Angehoben => hat && (ist != 0 || ziel != 0);

        /// <summary>Neues Ziel (Zusatz in dB). sofort = neuer/stiller Klang oder erster Wert. Rueckgabe: jetzt zu schreibender Zusatz.</summary>
        public double Setzen(double zusatz, bool sofort, double jetzt, double tau)
        {
            if (double.IsNaN(zusatz) || double.IsInfinity(zusatz)) return ist;
            if (!hat || sofort) { hat = true; ist = ziel = zusatz; t = jetzt; return ist; }
            Fortschreiben(jetzt, tau);
            ziel = zusatz;
            return ist;
        }

        /// <summary>Bis 'jetzt' (Sekunden, stetige Uhr) aufs Ziel zulaufen. Rueckgabe: zu schreibender Zusatz.</summary>
        public double Fortschreiben(double jetzt, double tau)
        {
            if (!hat) return 0;
            double dt = double.IsNaN(t) ? 0 : Math.Max(0, jetzt - t);
            t = jetzt;
            if (ist == ziel) return ist;
            double a = tau > 0 ? Math.Exp(-dt / tau) : 0;
            double d = (ist - ziel) * a;
            ist = Math.Abs(d) <= FertigDb ? ziel : ziel + d;
            return ist;
        }

        /// <summary>Freigabe an den Pool: ohne Gleiten auf den Preset, Zustand vergessen.</summary>
        public void Vergessen() { hat = false; ist = ziel = 0; t = double.NaN; }
    }
}
