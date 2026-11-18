using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // nachhallschuss (17.11.2026): Rechnung der Nachhall-Stimme fuer Gegnerschuesse (SEB-Live) und die Vergabe des Hall-Vorrats nach
    // Energie (alle Klassen). Reine Rechnung (Offline-Test NachhallSchussTest). Spiel-Teil: NachhallSchuss.cs, Zweitstimme.cs.
    // Bericht: review-20261110/nachhallstimme-STAND.md, Teil 2.
    //
    // Anregung (belegt, SEB 1.1.1 GunshotEmitterBase.TryStartShot + Spiel SuperSource.PlayScheduled):
    //   source1.volume = balance x Pegel, source2.volume = (1 - balance) x Pegel, PlayScheduled(start) -> PreOcclusionVolume = source1.volume,
    //   _secondPreEffectsVolume = source2.volume, danach UpdateSourceVolume(): source2.volume = zweiterVor x Faktor x Fade.
    //   Im Postfix steht damit: v1 = PreOcclusionVolume, v2 = source2.volume / (Faktor x Fade). Beide Clips spielen GANZ (ScheduleEndBeforeWrap:
    //   Ende = Clip-Laenge - 2 ms) -> die Anregung spielt dieselben zwei Clips mit v1 / v2 (PlayOneShot), Energie gleich der des trockenen
    //   Schusses (zwei verschiedene Aufnahmen addieren sich energetisch - Annahme wie mehrweg).
    // Pegel im gespielten Mass (Kennlinie): der Faktor des Schusses ist die gespielte Lautstaerke der SUMME S (Kennlinie/Stauchung schon
    //   darin). Er wird nach den PHYSIKALISCHEN Anteilen geteilt (trocken G/S, Nachhall D/S - NachhallKern.Teile). Die Stauchung wirkt so
    //   einmal auf das Ereignis, das Verhaeltnis Direkt- zu Hallenergie (der physikalische Hinweis auf Raum und Entfernung) bleibt wie
    //   gerechnet. G und D je fuer sich durch die Kennlinie zu schicken, staucht doppelt: bei Steigung 0,2 waere G = -12 dB gespielt nur
    //   -2,4 dB, der Hall bliebe ein Rest - das Verhaeltnis waere um ~10 dB falsch.
    // Vergabe (alle Klassen): Energie am Ohr bei der Anregung e = Faktor^2 x Nachhall-Anteil x Pegel^2 / d^2 (d >= 1 m); eine laufende Stimme
    // zaehlt mit ihrer JETZIGEN Energie e x 10^(-6 Alter / T60) (Abfall um 60 dB in T60). Ist der Vorrat voll, bekommt der neue Klang die
    // Stimme mit der kleinsten jetzigen Energie, wenn seine Energie groesser ist (sonst Ersatz = alter Abzug). Kostenregel, keine Klangregel.
    // ------------------------------------------------------------------------------------------
    internal static class NachhallSchussKern
    {
        /// <summary>Anregung aus dem Zustand des Schusses nach PlayScheduled: v1 = PreOcclusionVolume, v2 = source2.volume / (Faktor x Fade).
        /// volArg/balance wie SimpleSource.Play (Clip 1 mit volArg x balance, Clip 2 mit volArg x (1 - balance)).</summary>
        public static void Anregung(float preOcc1, float vol2, float faktor, float fade, out float volArg, out float balance)
        {
            float f = faktor * fade;
            float v1 = preOcc1 > 0 ? preOcc1 : 0, v2 = f > 1e-6f && vol2 > 0 ? vol2 / f : 0;
            volArg = v1 + v2;
            balance = volArg > 0 ? v1 / volArg : 1f;
        }

        /// <summary>Energie am Ohr bei der Anregung (relativ): voll^2 x nach x pegel^2 / max(1, d^2).</summary>
        public static double Prio(double voll, double nach, double pegel, double d2) => voll * voll * Math.Max(0, nach) * pegel * pegel / Math.Max(1.0, d2);

        /// <summary>Jetzige Energie einer laufenden Stimme: prio x 10^(-6 alter / T60).</summary>
        public static double Jetzt(double prio, double alterS, double t60)
            => !(t60 > 0) ? 0 : prio * Math.Pow(10, -6.0 * Math.Max(0, alterS) / t60);

        /// <summary>Darf der neue Klang (Energie neu) die Stimme mit jetziger Energie alt nehmen? Nur bei groesserer Energie.</summary>
        public static bool Nehmen(double neu, double alt) => neu > alt;

        /// <summary>Rechenzeit auf dem Audiofaden (ms je Block) fuer n Stimmen bei tBlock ms je Stimme und Block; Anteil am Block (0..1).</summary>
        public static double Budget(int n, double tBlockMs, double blockMs, out double anteil)
        {
            double ms = Math.Max(0, n) * Math.Max(0, tBlockMs);
            anteil = blockMs > 0 ? ms / blockMs : 0;
            return ms;
        }
    }
}
