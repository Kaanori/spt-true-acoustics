using System;
using System.Diagnostics;
using System.Threading;
using BepInEx.Configuration;
using UnityEngine;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // nachhallstimme (17.11.2026): Spiel-Teil der Nachhall-Stimme - Schalter, Hallfilter auf der Vorrats-Quelle, stiller Traeger-Clip.
    // Rechnung: NachhallKern.cs, Vergabe/Teilen: Zweitstimme.cs. Bericht: review-20261110/nachhallstimme-STAND.md.
    //
    // Signalweg (belegt im Dekompilat bzw. Unity-Doku, Rest Annahme):
    //  - Vorrats-Quelle = SimpleSource aus dem Pool Character (BetterAudio.GetSource); SimpleSource.Init: source1 = GetComponent<AudioSource>()
    //    (SimpleSource.cs) -> ein AudioSource je Objekt, das Filter haengt mit AddComponent am selben Objekt HINTER den Filtern des Prefabs.
    //  - Das Spiel stellt spatializePostEffects = false (BetterAudio.cs:343, beim Anlegen und bei RefreshSourceSettings): Filter liefen dann
    //    NACH Metas Spatializer auf dem Zwei-Ohren-Signal. Fuer die Nachhall-Stimme stellt die Mod true (Unity-Doku AudioSource.
    //    spatializePostEffects: "the spatializer effect is inserted after the effect filters") -> der Nachhall wird erzeugt und DANACH
    //    von Meta aus der Richtung der Oeffnung gerendert; je Bild neu gesetzt (RefreshSourceSettings setzt false zurueck).
    //  - Anregung: der stille Traeger-Clip laeuft als Schleife (haelt die Filterkette am Leben - endet ein AudioSource, endet sein Filter
    //    und der Nachhall wuerde abgeschnitten, Unity-Verhalten, Annahme); jedes Ereignis wird mit PlayOneShot auf DIESELBE Quelle gelegt
    //    (gleiche Filterkette). Ende per SetScheduledEndTime nach dem letzten Ereignis + Schwanz.
    //  - Ob OnAudioFilterRead im Spiel laeuft und wie viele Kanaele das Filter sieht, steht in der Diagnosezeile (Selbsttest: laeuft das
    //    Filter 0,5 s nach dem Start nicht, schaltet die Mod die Nachhall-Stimmen fuer diesen Raid ab - der Klang faellt dann auf den
    //    Ereignis-/Sprachabzug zurueck, nie auf eine trockene Kopie).
    // ------------------------------------------------------------------------------------------
    internal static class Nachhall
    {
        internal static ConfigEntry<bool> Aktiv, Test, Schritte, Stimmen, Einmal, Schuesse;   // nachhallschuss: Schuesse
        internal static ConfigEntry<float> PegelDb, MaxAbkling;
        internal static ConfigEntry<int> Vorrat;
        internal static volatile bool Kaputt;          // Selbsttest gescheitert (Filter laeuft nicht) -> bis Raid-Ende aus
        internal static bool Bewiesen;                 // mindestens eine Stimme hat im Raid nachweislich gerechnet
        internal static int Fehlversuche;
        internal static string KaputtGrund;

        internal static bool An => Richtung.An && Aktiv != null && Aktiv.Value && !Kaputt;
        internal static double MaxT => MaxAbkling != null ? Mathf.Clamp(MaxAbkling.Value, 0.3f, 12f) : 4.0;
        internal static double PegelLinear => PegelDb != null ? Math.Pow(10, Mathf.Clamp(PegelDb.Value, -20f, 20f) / 20.0) : 1.0;
        internal static int Groesse => Vorrat != null ? Mathf.Clamp(Vorrat.Value, 0, 16) : 8;

        /// <summary>Klasse an (Zweitstimme.KTest/KSchritt/KStimme/KEinmal).</summary>
        internal static bool KlasseAn(int k)
        {
            if (!An || Groesse <= 0) return false;
            var e = k == Zweitstimme.KTest ? Test : k == Zweitstimme.KSchritt ? Schritte : k == Zweitstimme.KStimme ? Stimmen : k == Zweitstimme.KEinmal ? Einmal
                  : k == Zweitstimme.KSchuss ? Schuesse : null;
            return e != null && e.Value;
        }

        internal static void Binden(ConfigFile c, string s)
        {
            Aktiv = c.Bind(s, "Room paths: diffuse part as real reverb", false,
                "The part of a sound that reaches you from another room as reverberation (the source room's reverberant field, which " +
                "pours through the openings - in big connected halls 80-99 % of the energy) is played as REAL reverb: smeared over time with the decay " +
                "of the room chain, starting with the travel time of the way, coming from the opening it pours out of. The dry copy then carries only " +
                "the directed part (straight/bent through the openings). Energy: dry + reverb = exactly what the mod computed before. For every sound " +
                "that gets a reverb voice, the old stand-ins 'only what arrives with the event' and 'speech: only the intelligible early part' are NOT " +
                "applied - your ear judges the reverb itself. Off = dry copy as before (with those stand-ins). Applies to the next sound.");
            Aktiv.SettingChanged += (_, __) => { if (!Aktiv.Value) Zweitstimme.HallAllesFrei(); };
            Test = c.Bind(s, "Room paths: real reverb for the test source", true,
                "The test source (section 20, group Footsteps, scheduled start: Reference tick or Repeat) gets the reverb voice - for measuring decay and level.");
            Schritte = c.Bind(s, "Room paths: real reverb for footsteps and gear", true,
                "Enemy footsteps and gear sounds in other rooms get the reverb voice (one per enemy, all steps feed it).");
            Stimmen = c.Bind(s, "Room paths: real reverb for voices", true,
                "Enemy voices in other rooms get the reverb voice (replaces 'speech: only the intelligible early part' for them).");
            Einmal = c.Bind(s, "Room paths: real reverb for one-shot sounds", true,
                "Impacts, casings, doors, windows and other one-shot sounds of the world in other rooms get the reverb voice " +
                "(planned one frame after the sound starts; the delay is taken off the reverb's onset).");
            // nachhallschuss (17.11.)
            Schuesse = c.Bind(s, "Room paths: real reverb for gunshots", true,
                "Enemy gunshots in other rooms (Sound Engine Backport live engine) get the reverb voice: the shot itself then carries only " +
                "the part that comes straight/bent through the openings, the hall of the shooter's room comes as real reverb from the opening it pours " +
                "out of, starting with the shot (sample-exact) plus the extra travel time. One reverb voice per shooter, all shots of a burst feed it. " +
                "Not for your own shots, not (yet) for SEB legacy (V1) weapons. The test source in group Gunshots uses the same path. Applies to the next shot.");
            PegelDb = c.Bind(s, "Room paths: reverb level correction (dB)", 0f,
                new ConfigDescription("Correction of the reverb voice level. 0 = physical (reverb energy = diffuse part computed by the mod; " +
                    "the reverb network is calibrated to energy 1). Only for A/B listening. Applies live.", new AcceptableValueRange<float>(-20f, 20f)));
            MaxAbkling = c.Bind(s, "Room paths: reverb max decay (s)", 4f,
                new ConfigDescription("Upper limit of the reverb voice decay time T60. The mod's room model counts only the hull of big halls " +
                    "and no machines/crates inside, so it computes them far too reverberant (Factory Area_2 7.1 s instead of about 2.6 s). " +
                    "The limit only changes how long the energy is spread, not how much. Applies to the next sound.", new AcceptableValueRange<float>(0.3f, 12f)));
            Vorrat = c.Bind(s, "Room paths: reverb voice pool size", 8,
                new ConfigDescription("How many playback sources the mod borrows for reverb voices (one per sounding enemy/object at a time, " +
                    "each runs a reverb network on the audio thread: about 0.38 ms per audio block per voice (estimate for the current network), so 8 voices " +
                    "take about 3 ms of each 21.3 ms block, 16 about 6 ms). When all are busy, the voice whose reverb has faded most is given to a louder new " +
                    "sound; if none is fainter, the new sound keeps the old stand-in (event/speech cut). 0 = no reverb voices. Applies to the next sound.",
                    new AcceptableValueRange<int>(0, 16)));
        }

        // ---------------------------------------------------------------- stiller Traeger-Clip (einmal je Raid)
        static AudioClip stille;
        static int stilleFs;
        internal static AudioClip Stille()
        {
            int fs = AudioSettings.outputSampleRate > 1000 ? AudioSettings.outputSampleRate : 48000;
            if (stille != null && stilleFs == fs) return stille;
            stille = AudioClip.Create("Materialklang reverb carrier", fs, 1, fs, false);
            stille.SetData(new float[fs], 0);   // einmal je Raid (Hauptfaden)
            stilleFs = fs;
            return stille;
        }

        internal static void SzeneEnde() { Kaputt = false; KaputtGrund = null; Bewiesen = false; Fehlversuche = 0; }

        // ---------------------------------------------------------------- Diagnose Audiofaden
        internal static long FilterTicks, FilterMaxTicks, FilterBloecke;
        internal static int FilterKanaele;
        internal static void FilterZeit(long dt, int kanaele)
        {
            Interlocked.Add(ref FilterTicks, dt);
            Interlocked.Increment(ref FilterBloecke);
            long m = Interlocked.Read(ref FilterMaxTicks);
            if (dt > m) Interlocked.Exchange(ref FilterMaxTicks, dt);
            FilterKanaele = kanaele;
        }
        internal static string FilterText(out bool leer)
        {
            long t = Interlocked.Exchange(ref FilterTicks, 0), m = Interlocked.Exchange(ref FilterMaxTicks, 0), b = Interlocked.Exchange(ref FilterBloecke, 0);
            leer = b == 0;
            double ms = 1000.0 / Stopwatch.Frequency;
            return $"audio thread {b} blocks, {(b > 0 ? t * ms / b : 0):F3} ms per block on average, at most {m * ms:F3} ms, {FilterKanaele} channels";
        }
    }

    /// <summary>Hallfilter einer Nachhall-Stimme (Audiofaden). Ersetzt das Signal der Quelle durch den Nachhall (nur nass). Aktiv nur,
    /// solange die Stimme einem Klang gehoert; sonst Durchlass (die Quelle spielt dann ohnehin nicht).</summary>
    internal sealed class NachhallFilter : MonoBehaviour
    {
        internal NachhallNetz Netz;
        internal volatile bool Aktiv;
        void OnAudioFilterRead(float[] data, int channels)
        {
            var n = Netz;
            if (!Aktiv || n == null) return;
            long t0 = Stopwatch.GetTimestamp();
            try { n.Verarbeiten(data, channels); }
            catch { Array.Clear(data, 0, data.Length); }   // nie die trockene Kopie durchlassen
            Nachhall.FilterZeit(Stopwatch.GetTimestamp() - t0, channels);
        }
    }
}
