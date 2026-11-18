using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // explosionfern (17.11.2026): Steigung der Kennlinie fuer Explosionen aus der Abstandskurve des Spiels.
    //
    // Befund (Kaan 17.11. 17:55:43, Factory-Tunnel, Marke 1 "Tonspitzen, die weggeknackt haben"): zwei Granaten 43 m / 57 m hinter
    // 3-4 Oeffnungen, reale Daemmung 23,2 / 22,7 dB, gespielt nur -4,6 / -4,5 dB. Ursache in der Mod: Hoerbarkeit.Kennlinie staucht
    // alles ueber dem Knie mit EINER Steigung s = 0,2 - hergeleitet aus den Nah-Schussclips (RaumwegKern.cs, "Wahl der Werte").
    // Fuer Explosionen gab es nie eine eigene Herleitung (PegelExplosion 160 "liegt immer weit im gestauchten Bereich").
    //
    // Prinzip der Kennlinie (RaumwegKern.cs): die Wiedergabe bildet den realen Pegel L auf einen gespielten K(L) ab; den Abstand macht
    // das Spiel selbst, die Mod liefert nur K(L_frei) - K(L_frei - D). Gleicher realer Pegel -> gleicher gespielter Pegel. Ein Hindernis
    // mit D dB macht eine Quelle real so laut wie dieselbe Quelle frei im Ersatzabstand d_e = d * 10^(D/20). Wie viel leiser das Spiel
    // eine Explosion in d_e spielt als in d, sagt seine eigene Abstandskurve der Gruppe "Grenades" -> die Steigung, mit der das Spiel
    // Explosionen staucht, ist die Sekante dieser Kurve zwischen d und d_e (gespielte dB je reale dB). Fuer Schuesse ist das genau
    // die s = 0,2 der Einstellung (aus den Schussclips und SEBs Nahkurve hergeleitet) - deshalb gilt die Kurven-Steigung nur fuer die
    // Gruppe, deren s nie hergeleitet wurde (Grenades, Gilt()).
    //
    // Zahlen (Zweitrechnung review-20261110/explosionfern/sollwerte.py; SPT-Preset "Grenades" aus resources.assets PathID 34097,
    // MaxDistance 1000 m = grenadeBank.Rolloff aus audio/banks/grenade.bundle): d 43 m, D 23,2 -> s 0,312 (Abzug 7,23 statt 4,64 dB);
    // d 57,3 m, D 22,7 -> s 0,340 (7,71 statt 4,54 dB); offene Tuer D 1,2 dB in 43 m -> s 0,215 (0,26 statt 0,24 dB).
    // Gegenprobe Aufnahme 17.11. (20261117-175633-dauergefecht.wav, 17 freie Explosionen 37-98 m): gespielter Pegel faellt mit
    // 0,54 +- 0,13 dB je realem dB Abstand (Kurve + Wechsel Nah-/Fern-Clip) - die Kurve allein ist also die untere Grenze (vorsichtig).
    //
    // Grenzen der Kurve: der letzte Abschnitt faellt auf 0 bei MaxDistance (Hoerweite des Spiels, kein Pegelgesetz). Der Ersatzabstand
    // wird deshalb am letzten Schluessel mit Wert > 0 begrenzt (Nutzende) und die Sekante ueber die verkuerzte Strecke genommen.
    // ANNAHME: die Quelle spielt mit der Preset-Kurve der Gruppe (BetterSource bekommt sie beim Anlegen) und x = d / MaxDistance
    // (Unity Custom Rolloff; MaxDistance = bank.Rolloff * OcclusionRolloffScale, SuperSourceDistant.SetRolloff).
    // ------------------------------------------------------------------------------------------
    internal static class KennGruppe
    {
        /// <summary>Gruppe "Grenades" (BetterAudio.AudioSourceGroupType, Index 11).</summary>
        public const int TypGrenades = 11;

        /// <summary>Gilt die Steigung aus der Abstandskurve fuer Gruppe 'typ'? Nur Explosionen: fuer Schuesse (0) und Einschlaege (2) ist die
        /// Steigung der Einstellung aus den Spielclips hergeleitet (RaumwegKern.cs), leise Gruppen liegen unter dem Knie.</summary>
        public static bool Gilt(int typ) => typ == TypGrenades;

        /// <summary>Unity-AnimationCurve an x: kubisch nach Hermite mit den Tangenten je Schluessel (Keyframe inTangent/outTangent, ohne
        /// Gewichte), ausserhalb geklemmt. k = flach [t, v, in, out] je Schluessel, n = Zahl der Schluessel.</summary>
        public static double Kurve(double[] k, int n, double x)
        {
            if (k == null || n <= 0) return double.NaN;
            if (n == 1 || x <= k[0]) return k[1];
            int l = (n - 1) * 4;
            if (x >= k[l]) return k[l + 1];
            int a = 0, b = n - 1;   // Binaersuche: k[a*4] <= x < k[b*4]
            while (b - a > 1) { int m = (a + b) >> 1; if (k[m * 4] <= x) a = m; else b = m; }
            int i = a * 4, j = b * 4;
            double dt = k[j] - k[i];
            if (!(dt > 0)) return k[j + 1];
            double u = (x - k[i]) / dt, u2 = u * u, u3 = u2 * u;
            return (2 * u3 - 3 * u2 + 1) * k[i + 1] + (u3 - 2 * u2 + u) * k[i + 3] * dt
                 + (-2 * u3 + 3 * u2) * k[j + 1] + (u3 - u2) * k[j + 2] * dt;
        }

        /// <summary>Letztes x (Anteil von MaxDistance) mit Kurvenwert &gt; 0 - dahinter blendet das Spiel nur noch auf Hoerweite aus.</summary>
        public static double Nutzende(double[] k, int n)
        {
            if (k == null) return double.NaN;
            for (int i = n - 1; i >= 0; i--) if (k[i * 4 + 1] > 1e-6) return k[i * 4];
            return double.NaN;
        }

        /// <summary>Steigung (gespielte dB je reale dB) der Abstandskurve zwischen d und dem Ersatzabstand d * 10^(D/20), begrenzt auf
        /// Nutzende * maxDist. NaN, wenn nicht bestimmbar (keine Kurve, D &lt;= 0, d schon hinter dem Nutzende, Kurvenwert &lt;= 0).
        /// Ergebnis auf [0,01; 1] geklemmt (Kennlinie streng monoton, nie lauter als real).</summary>
        public static double Steigung(double[] k, int n, double maxDist, double d, double D)
        {
            if (k == null || n < 2 || !(maxDist > 0) || !(D > 0) || double.IsNaN(d)) return double.NaN;
            d = Math.Max(d, 1.0);
            double ende = Nutzende(k, n) * maxDist;
            if (!(d < ende)) return double.NaN;
            double de = Math.Min(d * Math.Pow(10, D / 20.0), ende);
            double strecke = 20 * Math.Log10(de / d);
            if (!(strecke > 1e-4)) return double.NaN;
            double v0 = Kurve(k, n, d / maxDist), v1 = Kurve(k, n, de / maxDist);
            if (!(v0 > 0) || !(v1 > 0)) return double.NaN;
            double s = 20 * Math.Log10(v0 / v1) / strecke;
            return double.IsNaN(s) ? double.NaN : Math.Min(1.0, Math.Max(0.01, s));
        }

        /// <summary>Steigung fuer die Kennlinie: aus der Kurve, wenn die Gruppe sie nutzt (Gilt) und sie bestimmbar ist, sonst die Einstellung.</summary>
        public static double Waehle(bool an, int typ, double[] k, int n, double maxDist, double d, double D, double sEinstellung)
        {
            if (!an || !Gilt(typ)) return sEinstellung;
            double s = Steigung(k, n, maxDist, d, D);
            return double.IsNaN(s) ? sEinstellung : s;
        }
    }
}
