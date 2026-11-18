using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;

namespace Materialklang
{
    // galpegel (10.11.2026) - Fremdverstaerkung je Klanggruppe, Spiel-Teil. Rechnung und Herleitung: FremdPegelKern.cs.
    // Anbieter 1: Guns Are Loud (GAL). Gelesen wird, was WIRKLICH gespielt wird: die exponierten Parameter des Master-Mixers
    // (AudioMixer.GetFloat; GAL schreibt dort Sends, Gewinn, Filter, Passivkurve, Contrast-Fader). Per Reflection nur die Beschriftung
    // (wirksame Route Vanilla/Realistic, Vorlage) aus GunsAreLoud.Client.Runtime.HeadphoneRouteRuntime.Instance.Status - keine harte
    // Abhaengigkeit, keine GAL-Datei. Fehlt GAL oder sein Mixer: Zustand "none", 0 dB, EINE Info-Zeile.
    // Abfrage alle 0,25 s (GAL selbst prueft alle 0,2 s): Headset auf/ab wirkt damit innerhalb von 0,25 s; je Klang nur ein Woerterbuch-
    // Zugriff je Mixergruppe (Ergebnis gemerkt, verworfen bei jeder Aenderung der Mixerwerte).
    // NICHT auslesbar (weggelassen, Bericht): momentane Verstaerkung des Kompressors (nur im nativen Effekt), Hoerschaden-Zustand
    // (GAL fuehrt nur eine Dosis, keine Schwellenverschiebung in dB).
    internal static class FremdPegel
    {
        internal static readonly FremdPegelKern.Zustand Z = new FremdPegelKern.Zustand();
        static readonly double[] BandF = { 250, 1000, 4000 };   // Baender der Hoerbarkeit (Hoerbarkeit.NrBand)

        static float naechste;
        static bool gesucht, infoGemeldet;
        static Type galTyp;
        static PropertyInfo galInstanz, galStatus;
        static FieldInfo fEffektiv, fVorlage;
        static string galVersion = "";
        static string letzteBeschriftung;
        static AudioMixer gebunden;
        static int wechsel;

        sealed class Eintrag { public int Kat; public bool Kontrast; public int Stand = -1; public readonly double[] G = new double[3]; public double A; public string Text; }
        static readonly Dictionary<AudioMixerGroup, Eintrag> gruppen = new Dictionary<AudioMixerGroup, Eintrag>();
        static readonly List<string> kette = new List<string>(8);

        internal static void Tick()
        {
            if (Time.realtimeSinceStartup < naechste) return;
            naechste = Time.realtimeSinceStartup + 0.25f;
            try { Lesen(); }
            catch (Exception e) { Statistik.Fehler(e); Z.Da = false; }
        }

        static void Suchen()
        {
            gesucht = true;
            galTyp = AccessTools.TypeByName("GunsAreLoud.Client.Runtime.HeadphoneRouteRuntime");
            if (galTyp == null) return;
            galVersion = galTyp.Assembly.GetName().Version?.ToString() ?? "";
            galInstanz = AccessTools.Property(galTyp, "Instance");
            galStatus = AccessTools.Property(galTyp, "Status");
            var st = AccessTools.TypeByName("GunsAreLoud.Client.Runtime.HeadphoneRouteStatus");
            if (st != null) { fEffektiv = AccessTools.Field(st, "Effective"); fVorlage = AccessTools.Field(st, "TemplateId"); }
        }

        static float Get(AudioMixer m, string n, float ersatz) => m.GetFloat(n, out float v) && !float.IsNaN(v) && !float.IsInfinity(v) ? v : ersatz;

        static void Lesen()
        {
            if (!gesucht) Suchen();
            var ba = MonoBehaviourSingleton<BetterAudio>.Instantiated ? MonoBehaviourSingleton<BetterAudio>.Instance : null;
            AudioMixer m = ba != null ? ba.Master : null;
            bool da = galTyp != null && m != null && m.GetFloat("GAL_ElectronicsVolume", out _);
            if (!da)
            {
                if (!infoGemeldet && (galTyp == null || m != null))
                {
                    infoGemeldet = true;
                    Plugin.Log.LogInfo(galTyp == null ? "External gain: Guns Are Loud not loaded -> 0 dB for all mixer groups."
                                                      : "External gain: Guns Are Loud loaded, but its mixer is not the master mixer -> 0 dB.");
                }
                if (Z.Da) { Z.Da = false; Z.Neutral(); Z.Stand++; Melden(); }
                return;
            }
            // Werte lesen und mit dem letzten Stand vergleichen
            bool neu = !Z.Da || m != gebunden;
            gebunden = m;
            neu |= Setzen(ref Z.Volume, Get(m, "GAL_ElectronicsVolume", -80));
            for (int k = 0; k < FremdPegelKern.AnzahlKat; k++) neu |= Setzen(ref Z.Send[k], Get(m, FremdPegelKern.SendParameter[k], -80));
            neu |= Setzen(ref Z.QuietGain, Get(m, "GAL_ElectronicsQuietGain", 0));
            neu |= Setzen(ref Z.Hp, Get(m, "GAL_ElectronicsMicHP", 200)); neu |= Setzen(ref Z.Lp, Get(m, "GAL_ElectronicsMicLP", 10000));
            double ord = Get(m, "GAL_ElectronicsBandOrder", 1); int o = ord >= 1.5 ? 2 : 1; if (o != Z.Ordnung) { Z.Ordnung = o; neu = true; }
            neu |= Setzen(ref Z.ShelfDb, Get(m, "GAL_ElectronicsLowShelfDb", 0)); neu |= Setzen(ref Z.ShelfHz, Get(m, "GAL_ElectronicsLowShelfHz", 200));
            neu |= Setzen(ref Z.PresDb, Get(m, "GAL_ElectronicsPresenceDb", 0)); neu |= Setzen(ref Z.PresHz, Get(m, "GAL_ElectronicsPresenceHz", 3200));
            neu |= Setzen(ref Z.RauschenDbFs, Get(m, "GAL_ElectronicsNoiseDb", -120));
            neu |= Setzen(ref Z.Threshold, Get(m, "GAL_ElectronicsThreshold", -24)); neu |= Setzen(ref Z.Ratio, Get(m, "GAL_ElectronicsRatio", 10));
            neu |= Setzen(ref Z.Knie, Get(m, "GAL_ElectronicsKnee", 6)); neu |= Setzen(ref Z.Ceiling, Get(m, "GAL_ElectronicsCeiling", 0.5f));
            neu |= Setzen(ref Z.PassivDb, Get(m, "GAL_PassiveVolume", 0));
            int nb = 0;
            for (int i = 0; i < 9; i++)
            {
                string b = "GAL_PassiveBand" + (i + 1);
                if (!m.GetFloat(b + "Gain", out float g)) break;
                neu |= Setzen(ref Z.PGain[i], g); neu |= Setzen(ref Z.PFreq[i], Get(m, b + "Frequency", 1000)); neu |= Setzen(ref Z.PQInv[i], Get(m, b + "Q", 1));
                nb++;
            }
            if (nb != Z.PBaender) { Z.PBaender = nb; neu = true; }
            // Contrast: alle Fader gemeinsam (GunshotContrastMixerRouter); der erste steht fuer alle
            neu |= Setzen(ref Z.KontrastDb, Get(m, "GAL_ContrastRoute00", 0));
            double fs = AudioSettings.outputSampleRate > 8000 ? AudioSettings.outputSampleRate : 48000; neu |= Setzen(ref Z.Fs, fs);
            // aktiv = Elektronikweg hoerbar (mindestens ein Send und die Elektronik-Lautstaerke ueber -80 dB): GAL "Realistic"
            bool aktiv = Z.Volume > FremdPegelKern.Stumm && Array.Exists(Z.Send, s => s > FremdPegelKern.Stumm);
            if (aktiv != Z.Aktiv) { Z.Aktiv = aktiv; neu = true; }
            Z.Da = true; Z.Anbieter = "GAL " + galVersion;
            Beschriften(aktiv);
            if (neu) { Z.Stand++; }
            if (letzteBeschriftung != Z.Modus + "|" + Z.Vorlage + "|" + Z.Aktiv) { letzteBeschriftung = Z.Modus + "|" + Z.Vorlage + "|" + Z.Aktiv; wechsel++; Melden(); }
        }

        static bool Setzen(ref double ziel, double wert)
        {
            if (Math.Abs(ziel - wert) < 1e-4) return false;
            ziel = wert; return true;
        }

        static void Beschriften(bool aktiv)
        {
            string modus = aktiv ? "electronics active" : "electronics off", vorlage = "";
            try
            {
                object inst = galInstanz?.GetValue(null);
                object st = inst != null ? galStatus?.GetValue(inst) : null;
                if (st != null)
                {
                    modus = fEffektiv?.GetValue(st)?.ToString() ?? modus;
                    vorlage = fVorlage?.GetValue(st) as string ?? "";
                }
            }
            catch (Exception) { }
            Z.Modus = modus; Z.Vorlage = vorlage;
        }

        // ---------------------------------------------------------------- je Mixergruppe

        static Eintrag Fuer(AudioMixerGroup g)
        {
            if (g == null || !Z.Da) return null;
            if (!gruppen.TryGetValue(g, out var e))
            {
                e = new Eintrag();
                Vorfahren(g, kette);
                e.Kat = FremdPegelKern.Kategorie(kette); e.Kontrast = FremdPegelKern.ImKontrast(kette);
                if (gruppen.Count > 512) gruppen.Clear();
                gruppen[g] = e;
            }
            if (e.Stand != Z.Stand)
            {
                // (a) nur das Geraet (Passiv + Elektronik): Contrast ist ein Mischregler NACH der realen Welt (aendert weder den realen
                // Pegel noch das Eigenrauschen des Geraets relativ zur Quelle) -> nicht in die Hoerbarkeit. Log/Kalibrierung mit Contrast.
                for (int b = 0; b < 3; b++) e.G[b] = FremdPegelKern.GewinnDb(Z, e.Kat, BandF[b], false);
                e.A = FremdPegelKern.GewinnDbA(Z, e.Kat, e.Kontrast, BandF, Hoerbarkeit.ABew);
                e.Text = FremdPegelKern.Text(Z, e.Kat, BandF, e.Kontrast);
                e.Stand = Z.Stand;
            }
            return e;
        }

        // Namen der Gruppe und ihrer Vorfahren von innen nach aussen (wie Kalibrierung.GruppenName: Unity gibt den Elternteil nicht
        // heraus, FindMatchingGroups("p/<Pfadrest>") findet die Gruppe nur, wenn p ihr Vorfahr ist). Einmal je Gruppe.
        static void Vorfahren(AudioMixerGroup g, List<string> aus)
        {
            aus.Clear();
            aus.Add(g.name);
            var mixer = g.audioMixer;
            if (mixer == null || FremdPegelKern.Kategorie(aus) != FremdPegelKern.KeineKat) return;
            var alle = mixer.FindMatchingGroups(string.Empty);
            string rest = g.name;
            for (int tiefe = 0; tiefe < 10; tiefe++)
            {
                string eltern = null;
                foreach (var p in alle)
                {
                    if (p == null || p == g || aus.Contains(p.name)) continue;
                    if (Array.IndexOf(mixer.FindMatchingGroups(p.name + "/" + rest), g) >= 0) { eltern = p.name; break; }
                }
                if (eltern == null) break;
                aus.Add(eltern);
                if (FremdPegelKern.Kategorie(aus) != FremdPegelKern.KeineKat) break;   // naechster Send gefunden: weiter oben aendert nichts
                rest = eltern + "/" + rest;
            }
        }

        /// <summary>Gewinn des Geraets (ohne Contrast) fuer die Gruppe je Hoerbarkeits-Band (250 Hz / 1 kHz / 4 kHz, dB, leise Signale).
        /// false = keiner (0 dB).</summary>
        internal static bool Band(AudioSource s, out double[] g)
        {
            g = null;
            var e = s != null ? Fuer(s.outputAudioMixerGroup) : null;
            if (e == null) return false;
            g = e.G;
            return e.G[0] != 0 || e.G[1] != 0 || e.G[2] != 0;
        }

        /// <summary>(c) Breitband-Fremdgewinn (dB(A)) der Mixergruppe g fuer die Kalibrierung; 0 = keiner.</summary>
        internal static double DbA(AudioMixerGroup g)
        {
            try { var e = Fuer(g); return e != null ? e.A : 0; }
            catch (Exception) { return 0; }
        }

        /// <summary>(c) Fremdgewinn der Bezugsschritte (fremde Spieler/Bots: ObservedPlayerMovement unter ObservedPlayer, von GAL in den
        /// Contrast-Eingang verschoben, GunshotContrastModel). 0 ohne Anbieter.</summary>
        internal static double BezugDbA()
            => Z.Da ? FremdPegelKern.GewinnDbA(Z, FremdPegelKern.ObservedPlayer, true, BandF, Hoerbarkeit.ABew) : 0;

        static readonly double[] verdecker = new double[3];

        /// <summary>(a) Verdecker je Band (dB, Skala der Quelle) fuer eine Quelle in Gruppe s.outputAudioMixerGroup beim Grundgeraeusch
        /// lGrund (dB(A)) - null = ohne Fremdgewinn (Hoerbarkeit.Grund gilt). k = Bezug K (dBFS(A) je dB(A)) fuer das Eigenrauschen.</summary>
        internal static double[] Verdecker(AudioSource s, double lGrund, double k)
        {
            if (!Z.Da || !Z.Aktiv || !Band(s, out var g)) return null;
            for (int b = 0; b < 3; b++)
                verdecker[b] = FremdPegelKern.Verdecker(Hoerbarkeit.GrundRoh(lGrund, b), Hoerbarkeit.Hoerschwelle[b], Hoerbarkeit.MaskBreite, g[b],
                                                        FremdPegelKern.RauschenReal(Z, BandF[b], k, g[b]));
            return verdecker;
        }

        /// <summary>Klang-Log-Feld (Zeilenende), null ohne Anbieter.</summary>
        internal static string LogText(AudioSource s)
        {
            if (!Z.Da || s == null) return null;
            try { var e = Fuer(s.outputAudioMixerGroup); return e?.Text; }
            catch (Exception) { return null; }
        }

        static string Zusammenfassung()
        {
            var inv = CultureInfo.InvariantCulture;
            if (!Z.Da) return "none (0 dB)";
            var sb = new System.Text.StringBuilder(400);
            sb.Append(Z.Anbieter).Append(" route ").Append(Z.Modus);
            if (!string.IsNullOrEmpty(Z.Vorlage)) sb.Append(" template ").Append(Z.Vorlage);
            sb.Append(", contrast ").Append(Z.KontrastDb.ToString("0.#", inv)).Append(" dB");
            if (Z.Aktiv)
            {
                sb.Append(", sends");
                for (int k = 0; k < FremdPegelKern.AnzahlKat; k++) sb.Append(' ').Append(FremdPegelKern.KatName[k]).Append('=').Append(Z.Send[k].ToString("0.#", inv));
                sb.Append(", electronics vol ").Append(Z.Volume.ToString("0.#", inv)).Append(" quiet gain ").Append(Z.QuietGain.ToString("0.#", inv))
                  .Append(" band ").Append(Z.Hp.ToString("0", inv)).Append('-').Append(Z.Lp.ToString("0", inv)).Append(" order ").Append(Z.Ordnung)
                  .Append(" threshold ").Append(Z.Threshold.ToString("0.#", inv)).Append(" ratio ").Append(Z.Ratio.ToString("0.#", inv))
                  .Append(" noise ").Append(Z.RauschenDbFs.ToString("0", inv)).Append(" dBFS; passive@250/1k/4k ");
                for (int b = 0; b < 3; b++) sb.Append(b == 0 ? "" : "/").Append(FremdPegelKern.PassivDb(Z, BandF[b]).ToString("0.0", inv));
                sb.Append(" dB; small-signal gain@250/1k/4k (contrast input):");
                foreach (int k in new[] { FremdPegelKern.ObservedPlayer, FremdPegelKern.Npc, FremdPegelKern.Guns, FremdPegelKern.Occlusion, FremdPegelKern.Ambient })
                {
                    sb.Append(' ').Append(FremdPegelKern.KatName[k]);
                    for (int b = 0; b < 3; b++) sb.Append(b == 0 ? " " : "/").Append(FremdPegelKern.GewinnDb(Z, k, BandF[b], k != FremdPegelKern.Guns).ToString("+0.0;-0.0", inv));
                }
            }
            sb.Append("; calibration reference (observed steps) ").Append(BezugDbA().ToString("+0.0;-0.0", inv)).Append(" dB(A)");
            return sb.ToString();
        }

        static void Melden()
        {
            try { Plugin.Log.LogInfo("External gain changed: " + Zusammenfassung()); } catch (Exception) { }
        }

        internal static void Loggen()
        {
            if (!Z.Da && !infoGemeldet) return;
            Plugin.Log.LogInfo("[Diagnostics] External gain: " + Zusammenfassung() + $"; {wechsel} state changes, {gruppen.Count} mixer groups resolved.");
            wechsel = 0;
        }
    }
}
