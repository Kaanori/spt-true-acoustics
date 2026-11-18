using System;
using System.Collections.Generic;

namespace Materialklang
{
    // ---------------------------------------------------------------------------------------------
    //  galpegel (10.11.2026) - Fremdverstaerkung je Klanggruppe. Reine Rechnung (ohne Unity, Offline-Test).
    //
    //  Anlass: Guns Are Loud 1.2.0 (GAL) ersetzt SEBs Mixer durch eine eigene Fassung und aendert die Wiedergabepegel je Gruppe.
    //  Die Mod rechnet an drei Stellen mit ABSOLUTEN Pegeln: Hoerbarkeit gegen das Grundgeraeusch, Kennlinie lauter Klaenge,
    //  Kalibrierung (Bezug K und Gruppenwerte). Hier steht, was ein anderer Mixer an diesen Stellen wirklich aendert - als allgemeine
    //  Stelle (Zustand je Anbieter, Gewinn je Kategorie und Band), GAL ist der erste Anbieter (FremdPegel.cs liest ihn aus).
    //
    //  Signalweg von GAL "Realistic" (Quelle: GAL 1.2.0 MixerRouteGenerator.cs, TransactionalMixerHeadphoneRoute.BuildValues,
    //  HeadphoneElectronicPath.cs = Referenzmodell des nativen Effekts; README build/headphones):
    //    Ausgang = Passiv(x)  +  Elektronik( Summe_k Send_k * x_k )
    //    Passiv:     jeder trockene Zweig von World laeuft durch den Bus "GAL Passive": GAL_PassiveVolume + 9 ParamEQ (Daemmung der
    //                Kapsel, beim RAC 15-29 dB). ParamEQ: A = Steuerwert (Mitte +40 lg A dB), Q = 1 / "Octave range" (GAL README).
    //    Elektronik: Sends je Kategorie (Guns, ClientPlayer, ObservedPlayer, NPC, TechnicalSounds, NatureSounds, CommonSounds,
    //                Ambient, Returns = Werte der EFT-Headset-Vorlage; NonspatialBypass, Voip, Occlusion direkt mit 0 dB) ->
    //                Bandpass HP/LP (Ordnung 1 oder Butterworth 2) -> Rauschen -> Kuhschwanz + Praesenz -> Gewinn (QuietGain unter der
    //                Schwelle, darueber Kompressor Ratio/Knie) -> Begrenzer (Ceiling, Saettigung) -> GAL_ElectronicsVolume.
    //    Contrast:   Quellen in "<Gruppe>/GAL Contrast Input" bekommen GAL_ContrastRouteNN (alle gleich, Standard -8 dB) VOR beiden Wegen.
    //  "Vanilla": Sends und Elektronik stumm, Passiv neutral -> nur Contrast (0 dB, wenn Contrast aus).
    //  Leise Signale (unter der Kompressorschwelle) sehen damit einen festen Gewinn je Kategorie k und Frequenz f:
    //    G_k(f) = C + 10 lg( 10^(P(f)/10) + 10^((S_k + Q + E(f) + V)/10) )     (Energiesumme: 3 ms Versatz -> Kammfilter, im Mittel Energie)
    //  P = Passiv, E = Bandpass + Kuhschwanz + Praesenz, Q = QuietGain, V = Elektronik-Lautstaerke, C = Contrast (falls geroutet).
    //
    //  Was das fuer die Rechnung der Mod heisst (Herleitung, Bericht galpegel):
    //   (a) Hoerbarkeit leiser Quellen gegen das Grundgeraeusch: das Grundgeraeusch ist ein REALES Feld am Ort des Hoerers (kein
    //       gespielter Klang). Ein Gehoerschutz mit Mikrofon verstaerkt Quelle und Umgebung mit DEMSELBEN Gewinn (ein Mikrofon) ->
    //       der Abstand Quelle/Grundgeraeusch je Band bleibt gleich, die Verdeckung aendert sich NICHT. Es aendern sich nur:
    //         - der Abstand zur Ruhehoerschwelle: am Ohr L + G, Schwelle T fest -> Untergrenze des Verdeckers T + 10 - G (statt T + 10);
    //         - das Eigenrauschen der Elektronik (nach dem Bandpass, vor dem Gewinn): zusaetzlicher Verdecker R am Ohr.
    //       Gerechnet in der Pegelskala der Quelle (ohne Headset):  N'_b = max( 10 lg(10^(N_b/10) + 10^(R_b/10)), T_b + 10 - G_b ),
    //       R_b = Rauschen im Band [dBFS] + Q + E_Farbe(f_b) - K - G_b   (K = Spiel-dBFS(A) je dB(A), Kalibrierung).
    //       Ohne Headset (G = 0, kein Rauschen) exakt das bisherige Grund().
    //   (b) Kennlinie lauter Klaenge: sie bildet REALE Pegel auf den Spielmix ab (SEB/Spiel staucht ab ~85 dB(A)). GALs Kompressor
    //       wirkt danach auf den gespielten Klang, wie ein echter Gehoerschutz auf den echten Schall. Ihn in die Kennlinie zu rechnen,
    //       hiesse doppelt stauchen. Keine Aenderung; der Fremdgewinn verschiebt nur den gespielten Pegel, nicht die Abzuege.
    //   (c) Kalibrierung: die Korrektur vergleicht gespielte Pegel zweier Gruppen (Bezugsschritte vs. Zielquelle). Ein Gewinn, der
    //       je Gruppe verschieden ist, gehoert in beide Ist-Werte: Korrektur += G(Bezugsgruppe) - G(Zielgruppe) (Breitband, A-bewertet
    //       ueber die drei Baender). Die gespeicherten K-Werte bleiben ohne Fremdgewinn (Raid-uebergreifend vergleichbar).
    // ---------------------------------------------------------------------------------------------
    internal static class FremdPegelKern
    {
        // Kategorien (Reihenfolge der GAL-Sends, dann die direkten Wege)
        public const int Guns = 0, ClientPlayer = 1, ObservedPlayer = 2, Npc = 3, EnvTechnical = 4, EnvNature = 5, EnvCommon = 6,
                         Ambient = 7, Returns = 8, Bypass = 9, Voip = 10, Occlusion = 11, AnzahlKat = 12, KeineKat = -1;
        public static readonly string[] KatName = { "guns", "player", "observed", "npc", "technical", "nature", "common", "ambient", "returns", "bypass", "voip", "occlusion" };
        /// <summary>Exponierte Send-Parameter des GAL-Mixers je Kategorie.</summary>
        public static readonly string[] SendParameter =
        {
            "GAL_ElectronicsGunsSend", "GAL_ElectronicsClientPlayerSend", "GAL_ElectronicsObservedPlayerSend", "GAL_ElectronicsNpcSend",
            "GAL_ElectronicsEnvTechnicalSend", "GAL_ElectronicsEnvNatureSend", "GAL_ElectronicsEnvCommonSend", "GAL_ElectronicsAmbientSend",
            "GAL_ElectronicsEffectsReturnsSend", "GAL_ElectronicsNonspatialBypassSend", "GAL_ElectronicsVoipSend", "GAL_ElectronicsOcclusionSend",
        };
        /// <summary>Name der Kindgruppe, in die GAL Quellen fuer "Gunshot Contrast" verschiebt.</summary>
        public const string KontrastGruppe = "GAL Contrast Input";
        public const double Stumm = -79.5;   // Send/Lautstaerke darunter = aus (Unity-Mixer -80 dB)

        /// <summary>Kategorie einer Mixergruppe aus ihren Namen von innen nach aussen (Gruppe, Eltern, ...): der naechste Vorfahr mit
        /// eigenem Send (wie im Mixer: ein Send sitzt auf der Kategorie-Gruppe, alles darunter fliesst durch). KeineKat = nur Passiv.</summary>
        public static int Kategorie(IList<string> vorfahren)
        {
            if (vorfahren == null) return KeineKat;
            for (int i = 0; i < vorfahren.Count; i++)
            {
                switch (vorfahren[i])
                {
                    case "Guns": return Guns;
                    case "ClientPlayer": return ClientPlayer;
                    case "ObservedPlayer": return ObservedPlayer;
                    case "NPC": return Npc;
                    case "TechnicalSounds": return EnvTechnical;
                    case "NatureSounds": return EnvNature;
                    case "CommonSounds": return EnvCommon;
                    case "Ambient": return Ambient;
                    case "Returns": return Returns;
                    case "Bypass": case "NonspatialBypass": return Bypass;
                    case "Voip": return Voip;
                    case "Occlusion": return Occlusion;
                }
            }
            return KeineKat;
        }

        /// <summary>Steht die Quelle hinter GALs Contrast-Fader (innerste Gruppe "GAL Contrast Input")?</summary>
        public static bool ImKontrast(IList<string> vorfahren) => vorfahren != null && vorfahren.Count > 0 && vorfahren[0] == KontrastGruppe;

        /// <summary>Zustand eines Anbieters (gelesene Mixerwerte). Aktiv = Elektronikweg spielt (GAL Realistic).</summary>
        public sealed class Zustand
        {
            public string Anbieter = "none", Modus = "none", Vorlage = "";
            public bool Da;                                         // Anbieter geladen und Mixer gebunden
            public bool Aktiv;                                      // Zwei-Wege-Route (Passiv + Elektronik) spielt
            public readonly double[] Send = new double[AnzahlKat];  // dB
            public double Volume, QuietGain, Hp = 200, Lp = 10000, ShelfDb, ShelfHz = 200, PresDb, PresHz = 3200, RauschenDbFs = -120;
            public int Ordnung = 1;
            public double Threshold = -24, Ratio = 10, Knie = 6, Ceiling = 0.5;
            public double PassivDb;                                 // GAL_PassiveVolume
            public readonly double[] PGain = new double[9], PFreq = new double[9], PQInv = new double[9];
            public int PBaender;                                    // gelesene ParamEQ-Baender
            public double KontrastDb;                               // Wert der Contrast-Fader (<= 0)
            public double Fs = 48000;
            public int Stand;                                       // zaehlt jede Aenderung (Caches)

            public void Neutral()
            {
                Aktiv = false; Volume = QuietGain = ShelfDb = PresDb = PassivDb = KontrastDb = 0; RauschenDbFs = -120;
                for (int i = 0; i < AnzahlKat; i++) Send[i] = -80;
                PBaender = 0;
            }
        }

        // ---------------------------------------------------------------- Frequenzgaenge (digital, wie der Effekt rechnet)

        /// <summary>|H| in dB eines Biquads (b0 b1 b2 / a0 a1 a2) bei f (Abtastrate fs).</summary>
        public static double BiquadDb(double b0, double b1, double b2, double a0, double a1, double a2, double f, double fs)
        {
            double w = 2 * Math.PI * f / fs, c1 = Math.Cos(w), s1 = -Math.Sin(w), c2 = Math.Cos(2 * w), s2 = -Math.Sin(2 * w);
            double nr = b0 + b1 * c1 + b2 * c2, ni = b1 * s1 + b2 * s2, dr = a0 + a1 * c1 + a2 * c2, di = a1 * s1 + a2 * s2;
            return 10 * Math.Log10((nr * nr + ni * ni) / Math.Max(1e-300, dr * dr + di * di));
        }

        /// <summary>RBJ-Glocke mit Amplitudenkoeffizient a (Mitte +40 lg a dB) und Guete q bei f.</summary>
        public static double GlockeDb(double a, double f0, double q, double f, double fs)
        {
            if (!(a > 0) || !(f0 > 0) || !(q > 0) || f0 >= fs / 2) return 0;
            double w0 = 2 * Math.PI * f0 / fs, al = Math.Sin(w0) / (2 * q), c = Math.Cos(w0);
            return BiquadDb(1 + al * a, -2 * c, 1 - al * a, 1 + al / a, -2 * c, 1 - al / a, f, fs);
        }

        /// <summary>Kuhschwanz tief (wie HeadphoneElectronicPath.Biquad.LowShelf, Steilheit S = 1) bei f.</summary>
        public static double KuhschwanzDb(double db, double f0, double f, double fs)
        {
            if (db == 0 || !(f0 > 0)) return 0;
            f0 = Math.Min(f0, fs * 0.45);
            double a = Math.Pow(10, db / 40), w = 2 * Math.PI * f0 / fs, c = Math.Cos(w), al = 2 * Math.Sqrt(a) * (Math.Sin(w) / 2 * Math.Sqrt(2));
            return BiquadDb(a * (a + 1 - (a - 1) * c + al), 2 * a * (a - 1 - (a + 1) * c), a * (a + 1 - (a - 1) * c - al),
                            a + 1 + (a - 1) * c + al, -2 * (a - 1 + (a + 1) * c), a + 1 + (a - 1) * c - al, f, fs);
        }

        /// <summary>Butterworth 2. Ordnung (Q = 1/sqrt 2, wie Biquad.Butterworth) Hoch- oder Tiefpass bei f.</summary>
        public static double Butterworth2Db(bool hoch, double fc, double f, double fs)
        {
            double w = 2 * Math.PI * fc / fs, c = Math.Cos(w), al = Math.Sin(w) / Math.Sqrt(2), b = hoch ? (1 + c) / 2 : (1 - c) / 2;
            return BiquadDb(b, hoch ? -2 * b : 2 * b, b, 1 + al, -2 * c, 1 - al, f, fs);
        }

        /// <summary>Einpolige Filter wie HeadphoneElectronicPath (Ordnung 1): HP y = p (y1 + x - x1), TP y = (1 - p) x + p y1.</summary>
        public static double EinpolDb(bool hoch, double fc, double f, double fs)
        {
            double p = Math.Exp(-2 * Math.PI * fc / fs);
            return hoch ? BiquadDb(p, -p, 0, 1, -p, 0, f, fs) : BiquadDb(1 - p, 0, 0, 1, -p, 0, f, fs);
        }

        /// <summary>Bandpass der Elektronik (ohne Gewinn) bei f.</summary>
        public static double BandpassDb(Zustand z, double f)
        {
            double hp = Math.Max(1, z.Hp), lp = Math.Min(z.Fs * 0.45, Math.Max(hp, z.Lp));
            return z.Ordnung >= 2 ? Butterworth2Db(true, hp, f, z.Fs) + Butterworth2Db(false, lp, f, z.Fs)
                                  : EinpolDb(true, hp, f, z.Fs) + EinpolDb(false, lp, f, z.Fs);
        }

        /// <summary>Klangfarbe der Elektronik (Kuhschwanz + Praesenz, Q 1,2) bei f.</summary>
        public static double FarbeDb(Zustand z, double f)
            => KuhschwanzDb(z.ShelfDb, z.ShelfHz, f, z.Fs) + (z.PresDb != 0 ? GlockeDb(Math.Pow(10, z.PresDb / 40), Math.Min(z.PresHz, z.Fs * 0.45), 1.2, f, z.Fs) : 0);

        /// <summary>Passivweg (Kapsel) bei f: GAL_PassiveVolume + ParamEQ-Baender.</summary>
        public static double PassivDb(Zustand z, double f)
        {
            double d = z.PassivDb;
            for (int i = 0; i < z.PBaender && i < 9; i++)
                if (z.PGain[i] > 0 && Math.Abs(z.PGain[i] - 1) > 1e-6 && z.PQInv[i] > 0) d += GlockeDb(z.PGain[i], z.PFreq[i], 1.0 / z.PQInv[i], f, z.Fs);
            return d;
        }

        /// <summary>Gewinn (dB) leiser Signale der Kategorie kat bei f (unter der Kompressorschwelle). kontrast = Quelle hinter dem
        /// Contrast-Fader. Nicht aktiv: nur Contrast.</summary>
        public static double GewinnDb(Zustand z, int kat, double f, bool kontrast)
        {
            if (z == null || !z.Da) return 0;
            double c = kontrast ? z.KontrastDb : 0;
            if (!z.Aktiv) return c;
            double ep = Math.Pow(10, PassivDb(z, f) / 10);
            if (kat >= 0 && kat < AnzahlKat && z.Send[kat] > Stumm && z.Volume > Stumm)
                ep += Math.Pow(10, (z.Send[kat] + z.QuietGain + BandpassDb(z, f) + FarbeDb(z, f) + z.Volume) / 10);
            return c + 10 * Math.Log10(ep);
        }

        /// <summary>Breitband-Gewinn fuer Pegel in dB(A) (Kalibrierung): A-bewertetes Energiemittel ueber 250 Hz / 1 kHz / 4 kHz bei
        /// gleicher Bandenergie. ANNAHME: Spektrum der Quelle flach in diesen drei Baendern (Quellen der Kalibrierung sind breitbandig:
        /// Motoren, Feuer, Ambience); Abweichung fuer tief- oder hochlastige Quellen hoechstens die Bandunterschiede von G.</summary>
        public static double GewinnDbA(Zustand z, int kat, bool kontrast, double[] f, double[] aBew)
        {
            if (z == null || !z.Da) return 0;
            if (!z.Aktiv) return kontrast ? z.KontrastDb : 0;
            double s = 0, g = 0;
            for (int b = 0; b < f.Length; b++) { double w = Math.Pow(10, aBew[b] / 10); s += w; g += w * Math.Pow(10, GewinnDb(z, kat, f[b], kontrast) / 10); }
            return 10 * Math.Log10(g / s);
        }

        /// <summary>Oktavbandbreite (Hz) um f: f (2^(1/2) - 2^(-1/2)).</summary>
        public static double Oktavbreite(double f) => f * (Math.Sqrt(2) - 1 / Math.Sqrt(2));

        /// <summary>(a) Eigenrauschen der Elektronik im Oktavband um f, als REALER Pegel am Ohr (dB) in der Skala der Quelle ohne Headset:
        /// weisses Rauschen mit Effektivwert 10^(R/20) (gleichverteilt, Skala sqrt 3, HeadphoneElectronicPath._noiseScale) bis fs/2,
        /// Bandanteil 10 lg(B/(fs/2)), dann Farbe + QuietGain + Lautstaerke (es liegt vor dem Gewinn), minus K (dBFS(A) je dB(A)) und
        /// minus Gewinn der Quelle g. -Unendlich = kein Rauschen.</summary>
        public static double RauschenReal(Zustand z, double f, double k, double g)
        {
            if (z == null || !z.Aktiv || !(z.RauschenDbFs > -120) || z.Volume <= Stumm) return double.NegativeInfinity;
            double band = z.RauschenDbFs + 10 * Math.Log10(Oktavbreite(f) / (z.Fs / 2));
            return band + FarbeDb(z, f) + z.QuietGain + z.Volume - k - g;
        }

        /// <summary>(a) Verdecker je Band in der Skala der Quelle: max(N (+) R, T + maske - G). n = reales Grundgeraeusch je Band (dB,
        /// ohne Untergrenze), t = Ruhehoerschwelle, g = Gewinn der Quelle je Band, r = Eigenrauschen (RauschenReal). Ohne Fremdgewinn
        /// (g = 0, r = -Unendlich) = max(N, T + maske) wie Hoerbarkeit.Grund.</summary>
        public static double Verdecker(double n, double t, double maske, double g, double r)
        {
            double nr = double.IsNegativeInfinity(r) || double.IsNaN(r) ? n : 10 * Math.Log10(Math.Pow(10, n / 10) + Math.Pow(10, r / 10));
            if (double.IsNaN(g)) g = 0;
            return Math.Max(nr, t + maske - g);
        }

        /// <summary>Kurzbeschreibung fuer Log/Diagnose.</summary>
        public static string Text(Zustand z, int kat, double[] f, bool kontrast)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (z == null || !z.Da) return "none";
            string t = z.Anbieter + " " + z.Modus + (kontrast ? " contrast " + z.KontrastDb.ToString("0.#", inv) : "");
            if (!z.Aktiv) return t + " -> " + (kontrast ? z.KontrastDb : 0).ToString("+0.0;-0.0", inv) + " dB";
            t += " " + (kat >= 0 && kat < AnzahlKat ? KatName[kat] + " send " + z.Send[kat].ToString("0.#", inv) : "no send (passive only)") + " ->";
            for (int b = 0; b < f.Length; b++) t += (b == 0 ? " " : "/") + GewinnDb(z, kat, f[b], kontrast).ToString("+0.0;-0.0", inv);
            return t + " dB";
        }
    }
}
