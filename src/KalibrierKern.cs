using System;
using System.Collections.Generic;

namespace Materialklang
{
    // ---------------------------------------------------------------------------------------------
    //  0.8.6 Kalibrierung der Quellpegel (Kaan 09.11.2026: "Bau die saubere Kalibrierung.")
    //
    //  Problem: die Quellen ohne Spiel-Okklusion (Taxi, BTR, Lok, Flugzeug, Feuer, Generatoren, Maschinen, Tropfen, Alarme,
    //  Ambient-Spieler) klingen so laut, wie ihr Clip gemastert ist (Clip-Pegel x Spiel-volume x Mixer-Gruppe). Die Pegeltabelle
    //  QuellPegelKern sagt, wie laut sie real sind (dB(A) in 1 m) - aber nichts verbindet "dBFS im Spiel" mit "dB(A) real".
    //  Messung (Datei-Analyse /tmp/kal, A-bewertet): suv_idle LAeq -36,2 dBFS(A) x vol 0,536 -> -41,6 dBFS(A) in 1 m;
    //  Schritte walk_asphalt LAFmax -41,9, walk_concrete -44,9, run_asphalt -35,9, sprint -31,0 dBFS(A). Das Taxi im Leerlauf
    //  (real ~68 dB(A) in 1 m) klang damit gleich laut wie ein Schritt eines Bots (real ~65 dB(A) in 1 m).
    //
    //  Bezug: Schritte sind die haeufigsten Klaenge mit bekanntem Realpegel. Je Schritt eines anderen Spielers/Bots (Mixergruppe
    //  ObservedPlayerMovement) der tatsaechlich abgespielte Pegel in 1 m:
    //    Ist_Schritt = LAFmax(Clip) + 20 lg(BaseVolume x volume) + 20 lg(Kurve(1 m)) + Gruppe_dB
    //  K = Median(Ist_Schritt) - 65 dB(A)   ("Spiel-dBFS je dB(A) in 1 m"). Eigene Schritte (ClientPlayerMovement) nur als Ersatz,
    //  solange kein fremder gehoert wurde (Bezug 65 dB(A) in 1 m wie fremde; der Fusspunkt liegt 1,7 m unter dem Ohr, das steckt
    //  in der Kurve des Spiels).
    //  Je Quellart: Ist_Art = Median ueber ihre Quellen (Clip-Pegel + 20 lg volume + Kurve(1 m) + Gruppe), Soll = Tabelle + K,
    //  Korrektur = Soll - Ist, gedeckelt +-15 dB, weich eingeblendet ueber die Zahl der Bezugsschritte (smoothstep bis 12).
    //  Schleifen (Leerlauf, Feuer, Generator) mit LAeq, Einzelklaenge (Tor, Horn, Pfeifen, Schritte) mit LAFmax (125 ms), wie
    //  eine Pegelmessung "fast" (IEC 61672-1).
    //
    //  0.8.6 GUTACHTEN KALIBRIERUNG (10.11.2026, GUTACHTEN-KALIBRIERUNG.md) - umgesetzt:
    //  - Korrektur am Bezugsabstand r der Quelle statt pauschal in 1 m: Soll(r) = Tabelle(1 m) + 20 lg G(r) (Abstandsgesetz der
    //    Quellart, QuellPegelKern.Gesetz), Ist(r) = Clip + 20 lg vol + 20 lg Kurve(r) + Gruppe, Korrektur = Soll(r) + K - Ist(r).
    //  - Grenzen -12 ... +3 dB (Absenken erlaubt, Anheben kaum: ein Schritt-Bezug ist auf +-5 dB genau, +9 ... +15 dB lassen sich
    //    damit nicht begruenden); |Korrektur| unter 3 dB wird nicht angewendet (kleiner als die Unsicherheit).
    //  - Bezug nach Gangart getrennt und nur Asphalt/Beton (Clipname walk_/run_/sprint_ + asphalt/concrete; die Schritt-Clips
    //    streuen je Untergrund und Gangart um 32 dB): Soll Gehen 57, Rennen 65, Sprint 70 dB(A) LAFmax in 1 m (Schaetzung,
    //    Gutachten Frage 1; mit den Clip-Medianen K = -98,8 / -99,6 / -100,5). K = Median(Ist_i - Soll(Gangart_i)).
    //  - Mindestens 30 Bezugsschritte; bis dahin gilt der K-Wert des letzten Raids (gespeichert), sonst KStandard - keine
    //    Einblendung mehr (die Korrektur wuchs waehrend des Raids, das Taxi wurde lauter, je mehr Bots vorbeiliefen).
    //  - Eigene Schritte: Ohr 1,7 m ueber dem Fusspunkt -> Kurve bei 1,7 m + 20 lg 1,7 (bis dahin Kurve(1 m), ~4 dB daneben);
    //    die Kurve wirkt nur mit dem 3D-Anteil (spatialBlend).
    //  - Mixergruppen fuer beide Spiel-Snapshots (BetterAudio.TransitToEnvironment: draussen "Outdoor", sonst "Indoor").
    //  - Stereo: Pegel je Kanal, dann energetisch gemittelt (clip-pegel.py und Pegel()).
    //
    //  A-Bewertung: IEC 61672-1 Anhang E (Pole 20,6 / 107,7 / 737,9 / 12194 Hz), digital per bilinearer Transformation mit
    //  Vorverzerrung der Pole, auf 0 dB bei 1 kHz normiert - drei Biquads.
    // ---------------------------------------------------------------------------------------------
    internal static class KalibrierKern
    {
        public const double SchrittDbA = 65.0;     // Rennen auf Asphalt/Beton, LAFmax in 1 m (Schaetzung, Gutachten Frage 1)
        public const double MaxAnheben = 3.0;      // 0.8.6 Gutachten: hoechstens +3 dB
        public const double MaxAbsenken = 12.0;    // 0.8.6 Gutachten: hoechstens -12 dB
        public const double Schwelle = 3.0;        // 0.8.6 Gutachten: |Korrektur| < 3 dB wird nicht angewendet
        public const int BezugMin = 30;            // 0.8.6 Gutachten: so viele Bezugsschritte, bevor der Raid-eigene Bezug gilt
        public const double KStandard = -100.4;    // dBFS(A) je dB(A) in 1 m: Gutachten, Clip-Mediane aller drei Gangarten (-98,8/-99,6/-100,5)
        public const double OhrHoehe = 1.7;        // m ueber dem Fusspunkt (eigene Schritte)
        public static readonly double[] GangSoll = { 57.0, 65.0, 70.0 };   // Gehen / Rennen / Sprint, dB(A) LAFmax in 1 m (Schaetzung)
        static readonly string[] gangName = { "walk", "run", "sprint" };

        /// <summary>Gangart eines Schritt-Clips auf Asphalt oder Beton (0 Gehen, 1 Rennen, 2 Sprint), sonst -1
        /// (andere Untergruende, Springen, Landen, Drehen ... zaehlen nicht).</summary>
        public static int Gangart(string clip)
        {
            if (string.IsNullOrEmpty(clip)) return -1;
            for (int g = 0; g < 3; g++)
                if (clip.StartsWith(gangName[g] + "_asphalt", StringComparison.OrdinalIgnoreCase) || clip.StartsWith(gangName[g] + "_concrete", StringComparison.OrdinalIgnoreCase))
                    return g;
            return -1;
        }

        public static string GangText(int g) => g >= 0 && g < 3 ? gangName[g] : "?";

        /// <summary>Kurve mit dem 3D-Anteil: Unity daempft nur den raeumlichen Anteil (spatialBlend 0 = 2D, keine Entfernung).</summary>
        public static double KurveWirksam(double kurve, double raumAnteil)
        {
            double b = Math.Max(0, Math.Min(1, raumAnteil));
            return (1 - b) + b * Math.Max(0, kurve);
        }

        /// <summary>Gespielter Pegel eines EIGENEN Schritts auf 1 m umgerechnet (dBFS(A)): am Ohr (1,7 m ueber dem Fuss) gemessen,
        /// dann wie eine Punktquelle auf 1 m zurueck (+20 lg 1,7) - so ist er mit fremden Schritten in 1 m vergleichbar.</summary>
        public static double EigenerSchritt(double clipDb, double lautstaerke, double kurveOhr, double gruppeDb)
            => Gespielt(clipDb, lautstaerke, kurveOhr, gruppeDb) + 20 * Math.Log10(OhrHoehe);

        /// <summary>A-Bewertung als drei Biquads (b0 b1 b2 a1 a2 je Stufe) fuer die Abtastrate fs.</summary>
        public static double[] AFilter(double fs)
        {
            double[] w = { 20.598997, 107.65265, 737.86223, 12194.217 };
            var p = new double[4];
            for (int i = 0; i < 4; i++)
            {
                double wa = 2 * fs * Math.Tan(Math.PI * Math.Min(w[i], 0.45 * fs) / fs);   // vorverzerrt
                p[i] = (1 - wa / (2 * fs)) / (1 + wa / (2 * fs));                            // s = -wa -> z
            }
            // Stufe 1: Nullstellen 1,1 / Pole p0,p0; Stufe 2: 1,1 / p1,p2; Stufe 3: -1,-1 / p3,p3
            var c = new double[15];
            Stufe(c, 0, 1, 1, p[0], p[0]);
            Stufe(c, 5, 1, 1, p[1], p[2]);
            Stufe(c, 10, -1, -1, p[3], p[3]);
            // auf 0 dB bei 1 kHz normieren
            double g = Betrag(c, 1000, fs);
            c[0] /= g; c[1] /= g; c[2] /= g;
            return c;
        }

        static void Stufe(double[] c, int o, double z1, double z2, double p1, double p2)
        {
            c[o] = 1; c[o + 1] = -(z1 + z2); c[o + 2] = z1 * z2;
            c[o + 3] = -(p1 + p2); c[o + 4] = p1 * p2;
        }

        /// <summary>|H(f)| des Filters.</summary>
        public static double Betrag(double[] c, double f, double fs)
        {
            double w = 2 * Math.PI * f / fs, cr = Math.Cos(w), ci = -Math.Sin(w), c2r = Math.Cos(2 * w), c2i = -Math.Sin(2 * w);
            double m = 1;
            for (int s = 0; s < 3; s++)
            {
                int o = 5 * s;
                double nr = c[o] + c[o + 1] * cr + c[o + 2] * c2r, ni = c[o + 1] * ci + c[o + 2] * c2i;
                double dr = 1 + c[o + 3] * cr + c[o + 4] * c2r, di = c[o + 3] * ci + c[o + 4] * c2i;
                m *= Math.Sqrt((nr * nr + ni * ni) / (dr * dr + di * di));
            }
            return m;
        }

        /// <summary>LAeq und LAFmax (125 ms gleitend) in dBFS(A) aus verschachtelten Abtastwerten. 0.8.6: je Kanal A-bewertet, die
        /// Energie danach ueber die Kanaele gemittelt (bis dahin wurden die Abtastwerte vorher gemittelt: unkorreliertes Stereo
        /// bis 3 dB zu leise). n = Zahl der Frames (hoechstens data.Length / kanaele). false = zu kurz / leer.</summary>
        public static bool Pegel(float[] data, int kanaele, int n, double fs, out double laeq, out double lafmax)
        {
            laeq = lafmax = double.NaN;
            if (data == null || kanaele <= 0 || fs < 1000) return false;
            n = Math.Min(n, data.Length / kanaele);
            if (n < 16) return false;
            var c = AFilter(fs);
            var z = new double[kanaele * 12];   // je Kanal: x1, x2, y1, y2 je Biquad
            int fen = Math.Max(1, (int)(0.125 * fs));
            var ring = new double[fen]; int ri = 0; double lauf = 0, summe = 0, max = 0; int gefuellt = 0;
            for (int i = 0; i < n; i++)
            {
                double e = 0;
                for (int k = 0; k < kanaele; k++)
                {
                    double x = data[i * kanaele + k];
                    int o = 12 * k;
                    for (int st = 0; st < 3; st++)
                    {
                        int q = 5 * st, w = o + 4 * st;
                        double y = c[q] * x + c[q + 1] * z[w] + c[q + 2] * z[w + 1] - c[q + 3] * z[w + 2] - c[q + 4] * z[w + 3];
                        z[w + 1] = z[w]; z[w] = x; z[w + 3] = z[w + 2]; z[w + 2] = y; x = y;
                    }
                    e += x * x;
                }
                e /= kanaele;
                summe += e;
                lauf += e - ring[ri]; ring[ri] = e; ri = (ri + 1) % fen; if (gefuellt < fen) gefuellt++;
                if (gefuellt == fen && lauf / fen > max) max = lauf / fen;
            }
            if (gefuellt < fen) max = lauf / Math.Max(1, gefuellt);
            laeq = 10 * Math.Log10(summe / n + 1e-20);
            lafmax = 10 * Math.Log10(max + 1e-20);
            return true;
        }

        /// <summary>Abgespielter Pegel in 1 m (dBFS(A)): Clip-Pegel + Lautstaerke + Kurve + Mixergruppe.</summary>
        public static double Gespielt(double clipDb, double lautstaerke, double kurve1m, double gruppeDb)
            => clipDb + 20 * Math.Log10(Math.Max(lautstaerke, 1e-6)) + 20 * Math.Log10(Math.Max(kurve1m, 1e-6)) + gruppeDb;

        public static double Median(List<double> l)
        {
            if (l == null || l.Count == 0) return double.NaN;
            var a = l.ToArray(); Array.Sort(a);
            int m = a.Length / 2;
            return a.Length % 2 == 1 ? a[m] : 0.5 * (a[m - 1] + a[m]);
        }

        /// <summary>Bezug K (dBFS je dB(A) in 1 m) aus dem Median der Bezugsschritte (Rennen, 65 dB(A)).</summary>
        public static double Bezug(double medianSchritt) => medianSchritt - SchrittDbA;

        /// <summary>0.8.6: K-Beitrag eines Schritts der Gangart g: gespielt (dBFS(A) in 1 m) - Soll der Gangart. NaN bei g &lt; 0.</summary>
        public static double SchrittK(double gespieltDbFs, int g) => g >= 0 && g < 3 ? gespieltDbFs - GangSoll[g] : double.NaN;

        /// <summary>0.8.6: Bezug K fuer den Raid. Ab BezugMin Schritten der Median ihrer K-Beitraege, sonst der gespeicherte Wert des
        /// letzten Raids, sonst KStandard. quelle: "raid" / "last raid" / "default".</summary>
        public static double BezugK(List<double> kWerte, double kGespeichert, out string quelle)
        {
            if (kWerte != null && kWerte.Count >= BezugMin) { quelle = "raid"; return Median(kWerte); }
            if (!double.IsNaN(kGespeichert) && kGespeichert > -140 && kGespeichert < -60) { quelle = "last raid"; return kGespeichert; }
            quelle = "default"; return KStandard;
        }

        /// <summary>Korrektur (dB) einer Quellart am Bezugsabstand: Soll(r) + K - Ist(r), begrenzt auf -12 ... +3 dB,
        /// |Korrektur| unter 3 dB = 0 (Gutachten 10.11.2026).</summary>
        public static double Korrektur(double sollDbA, double istDbFs, double bezugK)
        {
            if (double.IsNaN(sollDbA) || double.IsNaN(istDbFs) || double.IsNaN(bezugK)) return 0;
            return Begrenzen(sollDbA + bezugK - istDbFs);
        }

        /// <summary>Grenzen der Korrektur: unter 3 dB nichts, sonst -12 ... +3 dB.</summary>
        public static double Begrenzen(double k)
        {
            if (double.IsNaN(k) || Math.Abs(k) < Schwelle) return 0;
            return Math.Max(-MaxAbsenken, Math.Min(MaxAnheben, k));
        }

        /// <summary>Gespeicherter Bezug: "K n" (invariant). NaN bei Fehler.</summary>
        public static double BezugLesen(string text, out int n)
        {
            n = 0;
            if (string.IsNullOrEmpty(text)) return double.NaN;
            var t = text.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (t.Length < 1 || !double.TryParse(t[0], System.Globalization.NumberStyles.Float, inv, out double k)) return double.NaN;
            if (t.Length > 1) int.TryParse(t[1], System.Globalization.NumberStyles.Integer, inv, out n);
            return k;
        }

        public static string BezugText(double k, int n) => k.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " " + n;

        /// <summary>Gruppen-dB im SEB-Mixer (live_mastermixer.bundle, SEB 1.0.3, Summe des Pfads) relativ zu ObservedPlayerMovement,
        /// Snapshot "Indoor" (BetterAudio.TransitToEnvironment: alles ausser draussen). Unity gibt Gruppenlautstaerken zur Laufzeit nicht
        /// heraus (nur exponierte Parameter) - deshalb die Werte aus dem Bundle; unbekannte Gruppe = 0 dB.</summary>
        public static double GruppeDb(string gruppe) => GruppeDb(gruppe, false);

        /// <summary>0.8.6 (Gutachten Punkt 3 "Mixer nur aus dem ersten Snapshot"): beide Snapshots, die das Spiel benutzt
        /// (draussen = "Outdoor", sonst "Indoor"; "Bunker" waehlt BetterAudio nie). Ausgelesen 10.11.2026 aus live_mastermixer.bundle
        /// (AudioMixerController, Gruppen per GUID, Pfadsumme je Snapshot). Im Outdoor-Snapshot steht die Gruppe "Main" auf -80 dB;
        /// alle hier gebrauchten Gruppen haengen unter Main, relativ zu ObservedPlayerMovement (ebenfalls unter Main) gilt der Wert
        /// unten. Die Gruppen der kalibrierten Quellen (Environment, TechnicalSounds, VehicleOut, CommonSounds, AmbientIn/Out ...)
        /// unterscheiden sich zwischen den Snapshots um hoechstens 0,3 dB.</summary>
        public static double GruppeDb(string gruppe, bool draussen)
        {
            double v = GruppeDbBekannt(gruppe, draussen);
            return double.IsNaN(v) ? 0 : v;
        }

        /// <summary>Kennt die Tabelle diese Gruppe (Name einer Gruppe des SEB-Mixers mit eigenem Wert)?</summary>
        public static bool Kennt(string gruppe) => !double.IsNaN(GruppeDbBekannt(gruppe, false));

        // NaN = Gruppe nicht in der Tabelle (GruppeDb nimmt dann 0 dB wie bisher).
        static double GruppeDbBekannt(string gruppe, bool draussen)
        {
            if (string.IsNullOrEmpty(gruppe)) return double.NaN;
            switch (gruppe)
            {
                case "ClientPlayerMovement": return -3.5;
                case "ObservedPlayerSpeech": return draussen ? -5.0 : -4.5;
                case "Voip": return draussen ? -5.0 : -4.0;
                case "VehicleInSpeech": return draussen ? -19.9 : 6.8;
                case "Hideout": return draussen ? -2.9 : 0;
                case "AmbientOut": case "CommonAmbOutEffects": case "CommonAmbOutBypass": case "PrecipitationAmbientOut": case "OutEnvironment":
                case "AmbientOutDay": case "AmbientOutDayEffects": case "AmbientOutDayBypass": case "AmbientOutNight": case "AmbientOutNightEffects":
                case "AmbientOutNightBypass": case "RadioOut": return draussen ? 0 : -0.3;
                case "EventRadio": return draussen ? 0.3 : -80.3;
                case "AmbientInRoomTone": return 4.0;
                case "RadioIn": return -4.0;
                case "RadioInLessVerb": return draussen ? -2.5 : 0;
                case "Rain": return draussen ? -3.0 : -3.1;
                case "Radio": return draussen ? -9.5 : -8.0;
                case "RadioLessVerb": return draussen ? 0 : -27.0;
                case "AmbFirefight": return draussen ? -10.0 : -15.0;
                case "Instrumental": case "GunshotsVehicleIn": return -3.0;
                case "Impacts": return -2.0;
                // Gruppen mit 0 dB, die bewusst in der Tabelle stehen (Bezugsebene "Main"): ohne diese Liste wuerde eine in sie
                // eingehaengte Kindgruppe (Kalibrierung.GruppenName) nicht auf sie zurueckgefuehrt.
                case "ObservedPlayerMovement": case "ObservedPlayer": case "ClientPlayer": case "NPC": case "Environment":
                case "TechnicalSounds": case "NatureSounds": case "CommonSounds": case "Vehicles": case "VehicleIn": case "VehicleOut":
                case "Ambient": case "AmbCommonEffects": case "AmbientIn": case "OccludedIn": case "AmbientInDayGroupBypass":
                case "AmbientInNightGroupBypass": case "CommonAmbInEffects": case "CommonAmbInBypass": case "PrecipitationAmbientIn":
                case "Gunshots": case "Occluded":
                    return 0;
                default: return double.NaN;
            }
        }

        // ------------------------------------------------------------------ 0.8.6 Clip-Pegel aus der Offline-Tabelle
        //  Ursache "calibration: waiting for footsteps" ueber einen ganzen Raid (Streets 09.11.2026 23:50, ~1 h): die Schritt-Clips
        //  (sounds.bundle, 1655 Clips) haben preloadAudioData = 0 und loadInBackground = 1. Beim ersten Abspielen (Prefix auf
        //  BetterSource.Play) ist der Clip noch nicht geladen; Kalibrierung.Clip hat das als "kein Pegel" fuer immer gemerkt -
        //  LogOutput 10.11. 00:1x: "clips without data 245 (streamed 4)", 5 Schritte in einer Stunde. Gestreamte Clips
        //  (Vorbis: amb_fireplace, amb_generators_loop, Tropfen, Alarme) gehen mit GetData gar nicht.
        //  Fix: Pegel aller Clips offline aus den Spieldateien (Materialklang-KlangTest/werkzeuge/clip-pegel.py, gleiche
        //  A-Bewertung, LAeq + LAFmax 125 ms, hoechstens 30 s, 0.8.6: Kanaele energetisch gemittelt; Vorbis ueber werkzeuge/fsbvorbis.c) als Daten
        //  im Plugin (ClipPegelDaten.cs). GetData bleibt Ersatz fuer Clips, die nicht in der Tabelle stehen (Mods), und merkt
        //  sich "nicht geladen" nicht mehr.
        public struct ClipEintrag { public int Samples; public double Laeq, Lafmax; }

        /// <summary>Tabelle "name TAB samples TAB LAeq TAB LAFmax" je Zeile -> Name -> Eintraege (gleicher Name, andere Laenge
        /// moeglich).</summary>
        public static Dictionary<string, List<ClipEintrag>> TabelleLesen(string text)
        {
            var d = new Dictionary<string, List<ClipEintrag>>(4096, StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return d;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var zeile in text.Split('\n'))
            {
                var t = zeile.Split('\t');
                if (t.Length < 4 || t[0].Length == 0) continue;
                if (!int.TryParse(t[1], System.Globalization.NumberStyles.Integer, inv, out int n)) continue;
                if (!double.TryParse(t[2], System.Globalization.NumberStyles.Float, inv, out double eq)) continue;
                if (!double.TryParse(t[3], System.Globalization.NumberStyles.Float, inv, out double fm)) continue;
                if (!d.TryGetValue(t[0], out var l)) d[t[0]] = l = new List<ClipEintrag>(1);
                l.Add(new ClipEintrag { Samples = n, Laeq = eq, Lafmax = fm });
            }
            return d;
        }

        /// <summary>Clip-Pegel aus der Tabelle: Name gleich und Laenge passend (2 % oder 256 Samples - ADPCM-Bloecke; ein
        /// gleichnamiger Clip eines Mods mit anderer Laenge zaehlt nicht). samples &lt;= 0 = Laenge unbekannt, dann nur bei
        /// eindeutigem Namen.</summary>
        public static bool TabelleSuchen(Dictionary<string, List<ClipEintrag>> t, string name, int samples, out ClipEintrag e)
        {
            e = default;
            if (t == null || name == null || !t.TryGetValue(name, out var l) || l.Count == 0) return false;
            if (samples <= 0) { if (l.Count != 1) return false; e = l[0]; return true; }
            int best = -1; long bestAb = long.MaxValue;
            for (int i = 0; i < l.Count; i++)
            {
                long ab = Math.Abs((long)l[i].Samples - samples);
                if (ab < bestAb) { bestAb = ab; best = i; }
            }
            if (best < 0 || bestAb > Math.Max(256, 0.02 * samples)) return false;
            e = l[best]; return true;
        }

        /// <summary>10.11. (akustik-wand, B1): Bezug der Character-Gruppe = Median-LAFmax der Renn-Clips auf Asphalt/Beton (Gangart 1) der
        /// Tabelle; NaN ohne Eintraege. Zu diesem Clip gehoert Hoerbarkeit.QuellPegel[Character] = GangSoll[1] = 65 dB(A).</summary>
        public static double BezugRennen(Dictionary<string, List<ClipEintrag>> t)
        {
            var l = new List<double>();
            if (t != null) foreach (var kv in t) if (Gangart(kv.Key) == 1) foreach (var e in kv.Value) l.Add(e.Lafmax);
            if (l.Count == 0) return double.NaN;
            l.Sort();
            return l.Count % 2 == 1 ? l[l.Count / 2] : 0.5 * (l[l.Count / 2 - 1] + l[l.Count / 2]);
        }

        /// <summary>Einzelklang (LAFmax) oder Schleife (LAeq) je Pegelart.</summary>
        public static bool Einzelklang(PegelArt a) => a == PegelArt.Tor || a == PegelArt.BtrHorn || a == PegelArt.Pfeifen || a == PegelArt.Tropfen;

        // ------------------------------------------------------------------ lampenpegel (18.11.2026)
        //  Ursache (review-20261110/lampen1118/lampen1118-STAND.md): Leuchtstofflampen (BetterAudio-Gruppe Lamp, LoopLampSoundController ->
        //  SimpleSource mit Spiel-Okklusion) waren die einzige Dauerquelle ohne Pegelabgleich. Die Mod nahm ihren Spielpegel (Clip x volume x
        //  Prefab-Kurve, maxDistance min(20, Rolloff) x Mixer) als realen Freifeldpegel im Abstand d und zog nur die Wegdaemmung ab. Real ist eine
        //  Lampe sehr leise (Handrechnung: hinter den offenen Tueren -1 ... +14 dB(A) am Ohr), im Spiel war sie hinter Waenden hoerbar.
        //  Rechnung je Lampe und Bild, im Abstand d (echter Ort der Lampe):
        //    Ist(d)  = LAeq(Clip) + 20 lg(volume vor der Okklusion) + 20 lg(Kurve_wirksam(d)) + Gruppe_dB         [dBFS(A)]
        //    Soll(d) = L_1m + K - 20 lg(max(d, 1 m))                                                                [dBFS(A)]
        //    roh     = Ist(d) - Soll(d)   (> 0: das Spiel spielt die Lampe zu laut -> so viel mehr Daempfung)
        //  L_1m: Pegel des Brummens in 1 m (Einstellwert, Standard 35 dB(A) = ANNAHME, Mitte von Kaans Angabe 30-40 dB(A); Vorschaltgeraete-
        //  Klassen A-D 20-42 dB laut Herstellertabellen, keine Norm). K: Bezug der Schritte (Spiel-dBFS(A) je dB(A) in 1 m, wie oben).
        //  20 lg d ab 1 m = Punktquelle im Freifeld (Kugelwelle); die Wegdaemmung der Mod (Raumweg, Wand) ist auf genau diesen Freifeldpegel
        //  bezogen (RaumwegKern: bezug = 1/(4 pi d^2)) -> zusammen der physikalische Pegel am Ohr. Die Prefab-Kurve faellt damit heraus.
        //  Grenzen: Absenken unbegrenzt (der Abzug ist je Lampe aus ihrem eigenen Clip, ihrer Lautstaerke und Kurve gemessen, nicht der Median
        //  einer Gruppe wie bei den Wandquellen; die -12 dB des Gutachtens 10.11. galten der Unsicherheit dieses Medians). Anheben hoechstens
        //  +3 dB wie im Gutachten (Spiel-Kurve jenseits maxDistance = 0 laesst sich nicht anheben) - greift die Grenze, sagt es das Log.
        //  Keine 3-dB-Schwelle: der Abzug haengt von d ab, eine Schwelle wuerde beim Gehen in 3-dB-Stufen springen.
        // Leitung 18.11. (Kaan: "die lampen sind kaputt und schlecht gewartet daher das lautere brummen"; Factory-Clips gaslamp_flicker =
        // flackernde Roehren): Standard 45 dB(A) = defekte/brummende Drossel statt intakter Leuchte (~35). ANNAHME: Geraeuschklassen der
        // Vorschaltgeraete A-F laut Herstellertabellen (A 20-24, B 25-30, C 31-36, D 37-42, E 43-48, F ab 49 dB; nicht nachgeprueft, keine Norm).
        public const double LampeSollStandard = 45.0;

        /// <summary>Spielpegel der Lampe im Abstand d (dBFS(A)): Clip-LAeq + Lautstaerke vor der Okklusion + wirksame Kurve + Gruppe.</summary>
        public static double LampeIst(double clipLaeq, double lautstaerke, double kurveWirksam, double gruppeDb)
            => Gespielt(clipLaeq, lautstaerke, kurveWirksam, gruppeDb);

        /// <summary>Sollpegel der Lampe im Abstand d (dBFS(A)): L_1m + K - 20 lg(max(d, 1)).</summary>
        public static double LampeSoll(double soll1m, double bezugK, double d)
            => soll1m + bezugK - 20 * Math.Log10(Math.Max(d, 1.0));

        /// <summary>Angewandter Abzug (dB, &gt; 0 = leiser) aus dem rohen: Absenken unbegrenzt, Anheben hoechstens MaxAnheben.
        /// begrenzt = die Anhebegrenze hat gegriffen. NaN -> 0.</summary>
        public static double LampeAnwenden(double roh, out bool begrenzt)
        {
            begrenzt = false;
            if (double.IsNaN(roh)) return 0;
            if (roh < -MaxAnheben) { begrenzt = true; return -MaxAnheben; }
            return roh;
        }

        // ------------------------------------------------------------------ lampenpegel Teil 2 (18.11.2026): Raumfeld
        //  Schwaeche der ersten Fassung (nur Direktschall 1/r): im eigenen Raum wurde die Lampe zu leise (BigRoom 14 m: 12 dB(A) statt real
        //  ~31). Eine Lampe hat im Spiel keinen Hallweg (SimpleSource, kein Meta-Send; lampen1118-STAND.md "Diffusanteil") - was der Raum an
        //  Diffusfeld aufbaut, muss also im trockenen Pegel stehen. Pegel am Ohr, W = Schallleistung der Lampe (Q = 1, wie der Raumweg Omega/4pi):
        //    eigener Raum:  I/W = tau / (4 pi max(d,1)^2) + 4/A' * exp(-0,04 r / T)       (Hopkins, Sound Insulation, Kap. 1: Lp = Lw +
        //                   10 lg(Q/(4 pi r^2) + 4/A); Abfall des Hallfelds mit dem Abstand: Barron & Lee 1988, JASA 84, "revised theory")
        //                   A' = A + 4 m V (Luft, ISO 9613-1), T = 0,161 V / A' (Sabine, mindestens 0,05 s), r = max(d, 0,5 m), tau = Durchgang
        //                   eines Hindernisses im selben Raum (Wegdaemmung der Mod; das Hallfeld des Raums geht daran vorbei).
        //                   GENAU DIESELBE Annahme rechnet der Raumweg fuer das Diffusfeld des Quellraums an der ersten Oeffnung
        //                   (RaumwegKern.QuellDiffus: S/A' * exp(-0,04 r0/T), A' aus RaumwegNetz.RaumFlaeche) -> kein Modellwechsel an der Tuer.
        //    Nachbarraum:   Raumweg wie bisher (Direkt durch die Oeffnungen + Diffusfeld des Quellraums + Lambert-Abstrahlung + 4P/A2), aber
        //                   OHNE die Kappung bei 0 dB (RaumwegKern.Db: eine Route nie lauter als frei). Die Kappung ist fuer Quellen im
        //                   Spielmass gedacht (das Spiel spielt sie schon frei; lauter geht nur bis volume 1). Fuer Lampen liegt der Abzug
        //                   aber 25+ dB hoch, und dicht hinter der Tuer liegt der reale Pegel 11-17 dB ueber frei (Hallfeld des Quellraums):
        //                   mit Kappung sprang der Pegel an der Tuer um -11 / -17 dB (lampenpegel.txt, #105 / #68), ohne um -3 / -5 dB
        //                   (das ist physikalisch: die Oeffnung strahlt in den Halbraum, Lambert 2P/S statt 4P/A im Raum).
        //  Ergebnis je Bild: Gewinn G = 10 lg(10^(-D/10) + Zusatz) (dB ueber dem Freifeld, D = Wegdaemmung der Mod), Zusatz = Raumfeld (eigener
        //  Raum) bzw. der gekappte Teil der Routen (Nachbarraum), Gesamtdaempfung = Ist(d) - Soll_frei(d) - G (Anheben hoechstens 3 dB).

        /// <summary>Hallfeld des eigenen Raums relativ zum Freifeld-Direktschall im Abstand d (Energieverhaeltnis, &gt;= 0):
        /// 4/A' exp(-0,04 r/T) * 4 pi max(d,1)^2. a &lt;= 0 (unbekannt/draussen) -> 0. m = Luft-Energiedaempfung (1/m).</summary>
        public static double LampeRaumfeld(double d, double a, double v, double m)
        {
            if (!(a > 0) || double.IsNaN(d)) return 0;
            double vv = Math.Max(0, double.IsNaN(v) ? 0 : v);
            double aF = Math.Max(a + 4 * Math.Max(0, m) * vv, RaumwegKern.MinFlaecheA);
            double r = Math.Max(d, 0.5);
            double abfall = vv > 0 ? Math.Exp(-0.04 * r / Math.Max(0.161 * vv / aF, 0.05)) : 1.0;
            double dd = Math.Max(d, 1.0);
            return 4.0 / aF * abfall * 4 * Math.PI * dd * dd;
        }

        /// <summary>Gekappter Teil der Routen als Energie relativ zum Freifeld: der Raumweg spielt routeDb (&gt;= 0, gekappt); ungekappt
        /// waere er ueberDb dB lauter -> Zusatz = 10^(-routeDb/10) * (10^(ueberDb/10) - 1). ueberDb &lt;= 0 oder NaN -> 0.</summary>
        public static double LampeRoutenZusatz(double routeDb, double ueberDb)
        {
            if (!(ueberDb > 0) || double.IsNaN(routeDb)) return 0;
            return Math.Pow(10, -routeDb / 10) * (Math.Pow(10, ueberDb / 10) - 1);
        }

        /// <summary>Pegel am Ohr gegen das Freifeld (dB, &gt; 0 = lauter als frei): 10 lg(10^(-dbWeg/10) + zusatz).</summary>
        public static double LampeGewinn(double dbWeg, double zusatz)
        {
            double e = Math.Pow(10, -(double.IsNaN(dbWeg) ? 0 : dbWeg) / 10) + Math.Max(0, double.IsNaN(zusatz) ? 0 : zusatz);
            return 10 * Math.Log10(Math.Max(e, 1e-30));
        }

        /// <summary>Gesamtdaempfung der Lampe (dB, das ersetzt die Wegdaemmung): Ist(d) - Soll_frei(d) - Gewinn, Anheben hoechstens
        /// MaxAnheben (begrenzt). roh = ungekappt. ist/soll NaN -> dbWeg (keine Korrektur).</summary>
        public static double LampeGesamt(double ist, double sollFrei, double dbWeg, double zusatz, out double roh, out double gewinn, out bool begrenzt)
        {
            gewinn = LampeGewinn(dbWeg, zusatz);
            roh = ist - sollFrei - gewinn;
            begrenzt = false;
            if (double.IsNaN(roh) || double.IsInfinity(roh)) { roh = double.NaN; return dbWeg; }
            return LampeAnwenden(roh, out begrenzt);
        }
    }
}
