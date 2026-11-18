using System;
using System.Globalization;
using System.Text;

namespace Materialklang
{
    /// <summary>testquelle (12.11.2026): Signal der Testquelle (F12 Abschnitt 20). testschuss (17.11.): Gunshot = fester Gewehrschuss aus
    /// einer Spiel-Soundbank, sonst (gekennzeichnet) der synthetische Ersatzschuss aus <see cref="TestquelleKern.Schuss"/>.
    /// Neue Werte nur hinten anfuegen (BepInEx speichert den Namen, die Reihenfolge haelt alte Zahlenwerte stabil).</summary>
    public enum TestSignal { Click, PinkNoise, GameClip, Gunshot }

    /// <summary>testquelle: Klanggruppe der Testquelle = Pool des Spiels, aus dem sie die Quelle leiht (BetterAudio.method_5:
    /// Character = ReverbSimpleSource wie fremde Schritte, Gunshots = ReverbSuperSource wie Schuesse).</summary>
    public enum TestGruppe { Footsteps, Gunshots }

    // ------------------------------------------------------------------------------------------
    //  testquelle (12.11.2026): reiner Teil der Testquelle - Messsignale, Send-Ziel, Logzeilen (ohne Unity, im KlangTest geprueft).
    //  Wozu: Messung B1 (hallsend-STAND.md Abschnitt 9) braucht eine reproduzierbare Quelle an fester Stelle, die denselben Weg
    //  nimmt wie ein Gegnerklang. Die Signale werden einmal erzeugt (fester Startwert des Zufalls -> jedes Mal dieselben Abtastwerte).
    //   - Click: 10 ms weisses Rauschen unter einem Hann-Fenster, Spitze 0,95. Kurz genug fuer die Abklingkurve (Fenster 150-800 ms
    //     liegen weit hinter dem Ende), breitbandig (Erwartung: Energie je Oktave +3 dB je Oktave, weil weiss = gleiche Energie je Hz).
    //   - PinkNoise: 1 s rosa Rauschen (Filter nach Paul Kellet, "refined", Fehler laut Autor +-0,05 dB ueber 9,2 Hz..Nyquist bei
    //     44,1 kHz; bei 48 kHz verschieben sich die Pole um 9 % - geprueft im KlangTest), 10 ms Kosinus-Rampen, Effektivwert -15 dBFS.
    //     Erwartung: gleiche Energie je Oktave.
    //     testschuss (17.11.): Laenge einstellbar (F12 "Noise length (s)", Standard 3 s, 0,2-10 s).
    //   - Gunshot (testschuss, 17.11.): Spiel-Clip aus einer Soundbank (Testquelle.cs); Ersatz nur gekennzeichnet: Schuss() unten.
    //   - Beide mit 3 s Stille dahinter: die Quelle spielt so lange weiter, damit der Hall des Spiels nicht mit dem Ende des Clips
    //     abreisst (ANNAHME: der Meta-Hall wird nur gerechnet, solange die AudioSource spielt - nicht belegt, deshalb die Stille).
    // ------------------------------------------------------------------------------------------
    public static class TestquelleKern
    {
        public const double KlickMs = 10.0;
        public const double RosaS = 1.0;          // Laenge des Rosa-Bursts ohne Angabe (Stand 12.11., KlangTest)
        /// <summary>testschuss (17.11.): F12 "Noise length (s)" - Standard und Grenzen.</summary>
        public const double RosaStandardS = 3.0, RosaMinS = 0.2, RosaMaxS = 10.0;
        public const double RampeMs = 10.0;
        public const double NachlaufS = 3.0;
        public const double RosaRmsDbfs = -15.0;
        public const float Spitze = 0.95f;
        public const uint StartWert = 0x5EED1234u;
        /// <summary>Grenzen von MetaXRAudioSource.ReverbSendDb (Meta.XR.Audio.dll: Setter klemmt auf -60..20 dB).</summary>
        public const float SendMin = -60f, SendMax = 20f;

        /// <summary>Gleichverteilt in [-1, 1) aus xorshift32 (Marsaglia 2003) - fest und plattformunabhaengig.</summary>
        static float Weiss(ref uint s)
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            return (float)(s / 2147483648.0 - 1.0);
        }

        static int Proben(int sr, double sek) => Math.Max(1, (int)Math.Round(sr * sek));

        /// <summary>Klick-Burst: 10 ms weisses Rauschen x Hann-Fenster, auf Spitze 0,95 gebracht, dann 3 s Stille.</summary>
        public static float[] Klick(int sr, out int burst) => Klick(sr, out burst, StartWert);

        /// <summary>Wie Klick, mit eigenem Startwert (nur fuer den KlangTest: Mittel ueber viele Bursts = Erwartung "weiss").
        /// Ein einzelner 10-ms-Burst weicht je Oktave um einige dB davon ab (Schaetzerstreuung ~1/sqrt(B*T)); er ist aber bei jedem
        /// Erzeugen derselbe - belastbar sind A/B-Differenzen mit demselben Klick (tools/messung/LIESMICH.md, Grenzen).</summary>
        public static float[] Klick(int sr, out int burst, uint startWert)
        {
            burst = Proben(sr, KlickMs / 1000.0);
            var d = new float[burst + Proben(sr, NachlaufS)];
            uint s = startWert == 0 ? StartWert : startWert;
            double max = 0;
            for (int i = 0; i < burst; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / burst);   // Hann, symmetrisch, Raender ~0
                double x = Weiss(ref s) * w;
                d[i] = (float)x;
                if (Math.Abs(x) > max) max = Math.Abs(x);
            }
            Skalieren(d, burst, max > 0 ? Spitze / max : 1.0);
            return d;
        }

        /// <summary>Rosa-Rauschen-Burst: 1 s, Rampen 10 ms, Effektivwert -15 dBFS (Spitze hoechstens 0,95), dann 3 s Stille.</summary>
        public static float[] Rosa(int sr, out int burst) => Rosa(sr, out burst, RosaS);

        /// <summary>testschuss (17.11.): wie Rosa, Laenge frei (auf RosaMinS..RosaMaxS begrenzt). Gleicher Startwert -> die ersten
        /// Abtastwerte jeder Laenge sind dieselben (bis auf Rampe und Normierung).</summary>
        public static float[] Rosa(int sr, out int burst, double laengeS)
        {
            burst = Proben(sr, RosaLaenge(laengeS));
            var d = new float[burst + Proben(sr, NachlaufS)];
            uint s = StartWert ^ 0xA5A5A5A5u;
            double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
            // Einschwingen des Filters (laengste Zeitkonstante 1/(1-0,99886) = 877 Proben) vor dem ersten Wert verwerfen
            for (int i = 0; i < 8 * 877; i++) Kellet(Weiss(ref s), ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6);
            int rampe = Proben(sr, RampeMs / 1000.0);
            for (int i = 0; i < burst; i++)
            {
                double x = Kellet(Weiss(ref s), ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6);
                d[i] = (float)(x * Rampe(i, burst, rampe));
            }
            double rms = Effektivwert(d, 0, burst);
            double k = rms > 0 ? Math.Pow(10, RosaRmsDbfs / 20.0) / rms : 1.0;
            double max = 0;
            for (int i = 0; i < burst; i++) max = Math.Max(max, Math.Abs(d[i] * k));
            if (max > Spitze) k *= Spitze / max;   // Effektivwert dann etwas tiefer (Logzeile nennt den echten Wert)
            Skalieren(d, burst, k);
            return d;
        }

        // Paul Kellet, "refined" pink filter (musicdsp.org, 1999): Summe von 6 Tiefpaessen 1. Ordnung + Direktanteil.
        static double Kellet(double w, ref double b0, ref double b1, ref double b2, ref double b3, ref double b4, ref double b5, ref double b6)
        {
            b0 = 0.99886 * b0 + w * 0.0555179;
            b1 = 0.99332 * b1 + w * 0.0750759;
            b2 = 0.96900 * b2 + w * 0.1538520;
            b3 = 0.86650 * b3 + w * 0.3104856;
            b4 = 0.55000 * b4 + w * 0.5329522;
            b5 = -0.7616 * b5 - w * 0.0168980;
            double p = b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362;
            b6 = w * 0.115926;
            return p;
        }

        /// <summary>Kosinus-Rampe am Anfang und Ende (0..1), damit der Burst nicht selbst knackt.</summary>
        static double Rampe(int i, int n, int r)
        {
            if (r <= 0) return 1;
            if (i < r) return 0.5 - 0.5 * Math.Cos(Math.PI * (i + 0.5) / r);
            int j = n - 1 - i;
            if (j < r) return 0.5 - 0.5 * Math.Cos(Math.PI * (j + 0.5) / r);
            return 1;
        }

        public static double RosaLaenge(double s) => double.IsNaN(s) ? RosaStandardS : s < RosaMinS ? RosaMinS : s > RosaMaxS ? RosaMaxS : s;

        // ---------------------------------------------------------------- testschuss (17.11.2026)
        //  Synthetischer Ersatzschuss (nur wenn kein Spiel-Clip ladbar ist; Logzeile sagt "FALLBACK"):
        //   x(t) = w(t) * (exp(-t/TauKnall) + RestAnteil * exp(-t/TauRest)), w = weisses Rauschen (xorshift32, fester Startwert).
        //   - Knall: TauKnall 0,5 ms -> nach 2 ms schon -35 dB: ein breitbandiger, sofort einsetzender Spitzenwert (Muendungsknall).
        //   - Rest: TauRest = 0,3 s / ln(1000) = 43,4 ms -> faellt um 60 dB in 300 ms (= 0,2 dB/ms; Handrechnung: 20 lg e^(-t/Tau)).
        //     RestAnteil 0,35 (-9 dB unter dem Knall): Ausklang deutlich hoerbar, Knall bleibt die Spitze. Annahme (kein Messwert eines
        //     echten Schusses): reicht zum Hinhoeren und als breitbandiges Messsignal; die Zahlen stehen in der Logzeile.
        //   - Ende: 400 ms (Rest dort -80 dB) mit 10 ms Kosinus-Ausblendung, dann 3 s Stille wie bei den anderen Signalen.
        //  Bezugs-Tick (Zeitbezug fuer Laufzeitmessungen): 2 ms Sinus 6 kHz unter einem Hann-Fenster, Spitze TickSpitze (-26 dBFS).
        //   6 kHz, weil weder Klick (breit) noch Schuss eine schmale Linie dort haben -> im Spektrogramm/Bandpass eindeutig zu finden.
        public const double SchussTauKnallMs = 0.5, SchussRestS = 0.3, SchussRestDb = -60.0, SchussRestAnteil = 0.35, SchussEndeS = 0.4;
        public const double TickMs = 2.0, TickHz = 6000.0;
        public const float TickSpitze = 0.05f;   // -26 dBFS: deutlich ueber dem Grund der Aufnahme, aber leise neben dem Testsignal

        /// <summary>Zeitkonstante des Ausklangs: Abfall um |SchussRestDb| in SchussRestS.</summary>
        public static double SchussTauRestS => SchussRestS / Math.Log(Math.Pow(10, -SchussRestDb / 20.0));

        /// <summary>Synthetischer Ersatzschuss (siehe oben), Spitze 0,95, dann 3 s Stille. laenge = Proben bis zum Ende des Ausklangs.</summary>
        public static float[] Schuss(int sr, out int laenge)
        {
            laenge = Proben(sr, SchussEndeS);
            var d = new float[laenge + Proben(sr, NachlaufS)];
            uint s = StartWert ^ 0x0BADF00Du;
            double tk = SchussTauKnallMs / 1000.0, tr = SchussTauRestS;
            int aus = Proben(sr, RampeMs / 1000.0);
            double max = 0;
            for (int i = 0; i < laenge; i++)
            {
                double t = (double)i / sr;
                double h = Math.Exp(-t / tk) + SchussRestAnteil * Math.Exp(-t / tr);
                int j = laenge - 1 - i;
                if (j < aus) h *= 0.5 - 0.5 * Math.Cos(Math.PI * (j + 0.5) / aus);
                double x = Weiss(ref s) * h;
                d[i] = (float)x;
                if (Math.Abs(x) > max) max = Math.Abs(x);
            }
            Skalieren(d, laenge, max > 0 ? Spitze / max : 1.0);
            return d;
        }

        /// <summary>Bezugs-Tick: TickMs Sinus TickHz x Hann, Spitze TickSpitze, ohne Nachlauf (die Quelle ist 2D und hat keinen Hall).</summary>
        public static float[] Tick(int sr)
        {
            int n = Proben(sr, TickMs / 1000.0);
            var d = new float[n];
            double max = 0;
            for (int i = 0; i < n; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / n);
                double x = Math.Sin(2 * Math.PI * TickHz * (i + 0.5) / sr) * w;
                d[i] = (float)x;
                if (Math.Abs(x) > max) max = Math.Abs(x);
            }
            Skalieren(d, n, max > 0 ? TickSpitze / max : 1.0);
            return d;
        }

        /// <summary>Wie SEB (WeaponSoundPlayerPatches.IsLoopClip): "loop" im Namen ohne "tail" = Feuerschleife fuer Dauerfeuer, kein
        /// Einzelschuss (Vanilla-Baenke wie ak74m: ak74m_outdoor_close_loop + eigene Tail-Bank).</summary>
        public static bool IstSchleife(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("loop", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("tail", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>Wiederholung: faellig, sobald der naechste Start innerhalb von Vorlauf + Reserve liegt (geplant) bzw. erreicht ist (sofort).</summary>
        public static bool Faellig(double jetztDsp, double naechster, bool geplant, double vorlauf, double reserve)
            => jetztDsp + (geplant ? vorlauf + reserve : 0.0) >= naechster;

        /// <summary>Startzeit eines geplanten Schusses: der Wunschtermin, aber nie frueher als jetzt + Vorlauf (sonst spielte Unity sofort).</summary>
        public static double GeplanterStart(double wunsch, double jetztDsp, double vorlauf)
            => double.IsNaN(wunsch) || wunsch < jetztDsp + vorlauf ? jetztDsp + vorlauf : wunsch;

        public static double Intervall(double s) => double.IsNaN(s) ? 4.0 : s < 1.0 ? 1.0 : s > 15.0 ? 15.0 : s;

        static void Skalieren(float[] d, int n, double k)
        {
            for (int i = 0; i < n; i++) d[i] = (float)(d[i] * k);
        }

        public static double Effektivwert(float[] d, int von, int n)
        {
            double e = 0;
            for (int i = von; i < von + n; i++) e += (double)d[i] * d[i];
            return n > 0 ? Math.Sqrt(e / n) : 0;
        }

        public static double Dbfs(double x) => 20 * Math.Log10(Math.Max(x, 1e-12));

        /// <summary>Send der Testquelle bei "Send override": Preset + fester Zusatz, in den Grenzen des Meta-Setters.</summary>
        public static float SendZiel(float preset, float zusatz)
        {
            float z = preset + zusatz;
            return z < SendMin ? SendMin : z > SendMax ? SendMax : z;
        }

        // ---------------------------------------------------------------- Logzeilen (einmal je Tastendruck, kein heisser Pfad)

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string F(double x, string f) => double.IsNaN(x) ? "n/a" : x.ToString(f, Inv);
        static string P(double x, double y, double z) => "(" + F(x, "F2") + ", " + F(y, "F2") + ", " + F(z, "F2") + ")";

        public static string AblageZeile(int nr, double x, double y, double z, string raum, string gruppe, double reichweite, int behaelter)
        {
            return "=== TEST SOURCE placed at " + P(x, y, z) + " room " + (string.IsNullOrEmpty(raum) ? "?" : raum) + " === placement=" + nr
                   + " group=" + gruppe + " max-distance=" + F(reichweite, "F0") + " m container#" + behaelter;
        }

        /// <summary>Signal in der TEST-SHOT-Zeile. PinkNoise: laengeS = Burstlaenge. Gunshot: clipName = Spiel-Clip, herkunft = Bank;
        /// clipName null = synthetischer Ersatz, herkunft = Grund (steht als "FALLBACK" in der Zeile).</summary>
        public static string SignalText(TestSignal s, string clipName, double laengeS, string herkunft = null)
        {
            switch (s)
            {
                case TestSignal.Click: return "click(" + F(KlickMs, "F0") + " ms white burst, peak " + F(Spitze, "F2") + ", +" + F(NachlaufS, "F1") + " s silence)";
                case TestSignal.PinkNoise: return "pink(" + F(double.IsNaN(laengeS) ? RosaS : laengeS, "F1") + " s, rms " + F(RosaRmsDbfs, "F0") + " dBFS, +" + F(NachlaufS, "F1") + " s silence)";
                case TestSignal.Gunshot:
                    if (clipName != null) return "gunshot(game clip " + clipName + " from " + (herkunft ?? "?") + ", " + F(laengeS, "F2") + " s)";
                    return "gunshot(FALLBACK synthetic: " + (herkunft ?? "no game clip") + "; crack tau " + F(SchussTauKnallMs, "F1") + " ms + noise "
                           + F(SchussRestDb, "F0") + " dB in " + F(SchussRestS * 1000, "F0") + " ms, +" + F(NachlaufS, "F1") + " s silence)";
                default: return "game-clip(" + (clipName ?? "?") + ", " + F(laengeS, "F2") + " s)";
            }
        }

        /// <summary>testschuss (17.11.): Anhang der TEST-SHOT-Zeile - Schalterstaende, die die Leitung dem Umschalten im F12-Menue zuordnet,
        /// und der Zeitbezug. metaOccl = KlangLog-Feld (" | meta occl ..."), null = keine Meta-Quelle. tickDsp NaN = Bezugs-Tick aus.</summary>
        public static string ZusatzText(bool modAn, string metaOccl, bool geplant, double startDsp, bool spaet, double tickDsp, int wiederholNr, double intervallS)
        {
            var sb = new StringBuilder(200);
            sb.Append(" | mod=").Append(modAn ? "on" : "off");
            sb.Append(string.IsNullOrEmpty(metaOccl) ? " | meta occl n/a" : metaOccl);
            sb.Append(" | start=").Append(geplant ? "scheduled" : "immediate (next audio block)").Append(" dsp=").Append(F(startDsp, "F6"));
            if (geplant) sb.Append(spaet ? " LATE (audio clock already past it - started late)" : " on time");
            sb.Append(" | ref-tick=");
            if (double.IsNaN(tickDsp)) sb.Append("off");
            else sb.Append("on dsp=").Append(F(tickDsp, "F6")).Append(" (2D ").Append(F(TickMs, "F0")).Append(" ms ").Append(F(TickHz / 1000, "F0"))
                    .Append(" kHz, ").Append(F(Dbfs(TickSpitze), "F0")).Append(" dBFS)");
            sb.Append(" | repeat=");
            if (wiederholNr <= 0) sb.Append("off");
            else sb.Append('#').Append(wiederholNr).Append(" every ").Append(F(intervallS, "F1")).Append(" s");
            return sb.ToString();
        }

        /// <summary>Zeile je Ausloesung. sendZusatz NaN = Override aus. sendJetzt/preset NaN = kein Meta-Hallweg.</summary>
        public static string SchussZeile(int nr, string uhr, double dspStart, string signal, string gruppe,
                                         double qx, double qy, double qz, double hx, double hy, double hz,
                                         double sendZusatz, double sendJetzt, double preset, string trocken, string weg, double vol, int ablage,
                                         string zusatz = null)
        {
            double dx = qx - hx, dy = qy - hy, dz = qz - hz;
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            var sb = new StringBuilder(320);
            sb.Append("=== TEST SHOT ").Append(nr).Append(" === clock=").Append(uhr).Append(" dsp-start=").Append(F(dspStart, "F3"))
              .Append(" signal=").Append(signal).Append(" group=").Append(gruppe)
              .Append(" source=").Append(P(qx, qy, qz)).Append(" listener=").Append(P(hx, hy, hz))
              .Append(" d=").Append(F(d, "F2")).Append(" m")
              .Append(" | send-extra=").Append(double.IsNaN(sendZusatz) ? "off" : (sendZusatz >= 0 ? "+" : "") + F(sendZusatz, "F1") + " dB")
              .Append(" send-now=").Append(F(sendJetzt, "F1")).Append(" dB (preset ").Append(F(preset, "F1")).Append(")")
              .Append(" | dry=").Append(trocken).Append(" | path=").Append(weg)
              .Append(" | vol=").Append(F(vol, "F3")).Append(" (").Append(F(vol > 0 ? 20 * Math.Log10(vol) : double.NaN, "F1")).Append(" dB)")
              .Append(" placement=").Append(ablage);
            if (zusatz != null) sb.Append(zusatz);
            return sb.ToString();
        }
    }
}
