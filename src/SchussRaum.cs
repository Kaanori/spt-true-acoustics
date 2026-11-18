using System;
using System.Runtime.CompilerServices;
using Audio.ReverbSubsystem;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using HarmonyLib;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    //  schusshrtf (12.11.2026): F12 "21. Gunshot spatialization (experimental)" -> "Gunshots: HRTF (experimental)", Standard AUS.
    //  Fremde Schuesse spielen ueber den Meta-Spatializer (HRTF) statt ueber Unitys Stereo-Panning. Eigene Schuesse bleiben Stereo.
    //
    //  Wo das Spiel entscheidet (Dekompilat / Asset gelesen):
    //   - Preset-Asset "Gunshots" (resources.assets, AudioGroupPreset): SteamSpatialize = false, DirectBinaural = true, SpatialBlend = 1,
    //     HrtfIntensity = 0 (Character: SteamSpatialize = true, HrtfIntensity = 1). DirectBinaural setzt LoadGroupPreset immer auf true
    //     (BetterAudio.cs:1006); Gunshots haben DisableBinauralByDist = false -> CheckBinauralAllowed kehrt sofort zurueck (BetterSource.cs:675-677).
    //   - Jeder Schuss (Spiel-Warteschlange SuperAudioSample.PlayOn, SEB GunshotEmitterBase.TryStartShot:117/130) laeuft ueber
    //     EnableStereo(firstPerson) -> ReverbSuperSource.PlayScheduled -> SuperSource.PlayScheduled -> BetterSource.PlayScheduled, das
    //     RefreshSpatialization(SteamSpatialize && DirectBinaural) = false setzt (BetterSource.cs:371). Danach spielt ReverbSuperSource
    //     die Hall-Zwillinge nur bei !EnableSpatialization (ReverbSuperSource.cs:104-118), sonst schaltet es sie selbst ab.
    //  Eingriff: Prefix ReverbSuperSource.PlayScheduled merkt die Quelle (nur Gunshots, nicht _forceStereo, Schalter an), der Prefix
    //  BetterSource.RefreshSpatialization macht NUR fuer diese Quelle und NUR innerhalb dieses Aufrufs aus false ein true. Damit nimmt das
    //  Spiel selbst seinen raeumlichen Weg (Zwillinge aus, Send am eigenen Spatializer). Der Finalizer loescht die Marke und setzt die
    //  HRTF-Staerke beider Spatializer (source1 + source2) auf 1, weil das Gunshots-Asset 0 hat (nur ITD/ILD, keine HRTF-Klangfarbe).
    //  Zurueck: Release setzt spatialize selbst zurueck (BetterSource.cs:339); die HRTF-Staerke gehoert dem Klang -> Postfix Release
    //  stellt den Preset-Wert her (gleiche Besitz-Regel wie Physik.SendFreigeben). Der naechste PlayScheduled rechnet ohnehin neu.
    //  AUS: je PlayScheduled einer Schussquelle ein bool-Vergleich, je RefreshSpatialization ein Referenzvergleich, je Release ein int-Vergleich.
    //  Faden: alles Hauptfaden (PlayScheduled kommt aus Update/SEB-Tick). Fehlt ein Feld/eine Methode (Spiel-Update): kein Haken, Spielverhalten.
    // ------------------------------------------------------------------------------------------
    internal static class SchussRaum
    {
        internal const string Sek = "21. Gunshot spatialization (experimental)";
        // HRTF-Staerke fuer erzwungene Schuesse: 1 = volle HRTF-Klangfarbe (Meta-Standard, AudioGroupPreset-Vorgabe, Character-Asset; SEB
        // traegt fuer Gunshots ebenfalls 1 ein, nutzt es wegen UseSimpleSpatializer aber nicht, LiveGroupPresetTable.cs:477-486).
        const float HrtfVoll = 1f;

        static ConfigEntry<bool> Schalter;
        static bool an;                       // Kopie von Schalter.Value fuer den heissen Pfad
        static BetterSource imAbspielen;      // Quelle, deren PlayScheduled gerade laeuft und erzwungen wird (Hauptfaden)
        static AccessTools.FieldRef<BetterSource, bool> forceStereoRef;
        static AccessTools.FieldRef<SuperSource, BaseSpatialAudioSource> zweiterRef;

        sealed class Besitz { public bool Hrtf; }
        static readonly ConditionalWeakTable<BetterSource, Besitz> BesitzTab = new ConditionalWeakTable<BetterSource, Besitz>();
        static readonly ConditionalWeakTable<BetterSource, Besitz>.CreateValueCallback BesitzNeu = _ => new Besitz();
        static int besitzZahl;                // > 0: mindestens eine Quelle traegt noch die HRTF-Staerke der Mod
        internal static int Erzwungen, Zurueckgestellt;

        internal static void Anwenden(Harmony h, ConfigFile c)
        {
            Schalter = c.Bind(Sek, "Gunshots: HRTF (experimental)", false,
                "On = shots of OTHER players and bots play through the game's HRTF spatializer (Meta XR, like footsteps) instead of plain stereo panning. Your own shots stay stereo. The reverb then comes from the shot source itself (game mono preset) instead of the stereo reverb twin. Applies from the next shot. Experimental - compare by ear.");
            an = Schalter.Value;
            Schalter.SettingChanged += (_, __) => an = Schalter.Value;

            var play = AccessTools.DeclaredMethod(typeof(ReverbSuperSource), "PlayScheduled", new[] { typeof(double) });
            var refresh = AccessTools.DeclaredMethod(typeof(BetterSource), "RefreshSpatialization", new[] { typeof(bool) });
            var rel = AccessTools.DeclaredMethod(typeof(BetterSource), "Release", Type.EmptyTypes);
            bool felder = AccessTools.Field(typeof(BetterSource), "_forceStereo") != null && AccessTools.Field(typeof(SuperSource), "_secondSourceSpatializer") != null;
            if (play == null || refresh == null || rel == null || !felder)
            {
                Plugin.Log.LogWarning("Gunshots HRTF: game methods/fields not found (ReverbSuperSource.PlayScheduled, BetterSource.RefreshSpatialization/Release, _forceStereo, _secondSourceSpatializer) -> switch has no effect, shots stay as the game plays them.");
                return;
            }
            forceStereoRef = AccessTools.FieldRefAccess<BetterSource, bool>("_forceStereo");
            zweiterRef = AccessTools.FieldRefAccess<SuperSource, BaseSpatialAudioSource>("_secondSourceSpatializer");
            h.Patch(play, prefix: new HarmonyMethod(typeof(SchussRaum), nameof(VorPlay)), finalizer: new HarmonyMethod(typeof(SchussRaum), nameof(NachPlay)));
            // Guns Are Loud hat einen Prefix auf dieselbe Methode (LocalGunshotForceStereoPatch: eigene Schuesse -> false, Priority Normal).
            // Unserer laeuft VORHER (HigherThanNormal), GALs false gewinnt also; der Finalizer sieht dann spatialize aus und besitzt nichts.
            h.Patch(refresh, prefix: new HarmonyMethod(typeof(SchussRaum), nameof(VorRefresh)) { priority = Priority.HigherThanNormal });
            h.Patch(rel, postfix: new HarmonyMethod(typeof(SchussRaum), nameof(NachRelease)));
        }

        /// <summary>Prefix ReverbSuperSource.PlayScheduled. EnableStereo(firstPerson) kam schon davor (GunshotEmitterBase.cs:117,
        /// WeaponSoundPlayer.cs:311, SEB WeaponSoundPlayerPatches.cs:242) -> _forceStereo gilt fuer DIESEN Schuss.</summary>
        static void VorPlay(ReverbSuperSource __instance)
        {
            if (!an) return;
            imAbspielen = null;
            try
            {
                var p = __instance.Preset;
                if (p == null) return;
                bool fs = forceStereoRef(__instance);
                bool spiel = SchussRaumKern.SpielRaeumlich(fs, p.DirectBinaural, p.SteamSpatialize);
                if (SchussRaumKern.Erzwingen(true, p.Type == BetterAudio.AudioSourceGroupType.Gunshots, fs, spiel)) imAbspielen = __instance;
            }
            catch (Exception e) { imAbspielen = null; Statistik.Fehler(e); }
        }

        /// <summary>Prefix BetterSource.RefreshSpatialization: nur innerhalb des erzwungenen PlayScheduled und nur fuer diese Quelle.</summary>
        static void VorRefresh(BetterSource __instance, ref bool enabledSpat)
        {
            if (ReferenceEquals(__instance, imAbspielen)) enabledSpat = true;
        }

        /// <summary>Finalizer ReverbSuperSource.PlayScheduled (laeuft auch bei Ausnahme; void = Ausnahme des Spiels geht unveraendert weiter).</summary>
        static void NachPlay(ReverbSuperSource __instance)
        {
            if (ReferenceEquals(imAbspielen, null) || !ReferenceEquals(__instance, imAbspielen)) return;
            imAbspielen = null;
            try
            {
                if (!__instance.EnableSpatialization) return;   // etwas anderes hat es wieder abgeschaltet -> nichts besitzen
                HrtfSetzen(__instance, HrtfVoll);
                var b = BesitzTab.GetValue(__instance, BesitzNeu);   // Pool-Objekt: einmal je Quelle angelegt, nicht je Schuss
                if (!b.Hrtf) { b.Hrtf = true; besitzZahl++; }
                Erzwungen++;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>Postfix BetterSource.Release: spatialize hat das Spiel schon zurueckgesetzt (BetterSource.cs:339), die HRTF-Staerke nicht.</summary>
        static void NachRelease(BetterSource __instance)
        {
            if (besitzZahl == 0) return;
            try
            {
                if (!(__instance is SuperSource s) || !BesitzTab.TryGetValue(__instance, out var b) || !b.Hrtf) return;
                b.Hrtf = false; besitzZahl--;
                if (s.Preset != null) HrtfSetzen(s, s.Preset.HrtfIntensity);
                Zurueckgestellt++;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>HRTF-Staerke an beiden Spatializern (source1 und source2: SuperSource reicht HrtfIntensity nicht an den zweiten weiter,
        /// SuperSource.cs:27-41 nur EnableSpatialization/Reverb/Send). UpdateParameters schreibt sofort (Parameter 9), sonst erst im naechsten Bild.</summary>
        static void HrtfSetzen(SuperSource s, float wert)
        {
            var sp1 = s.Spatializer;
            if (sp1 != null) { sp1.HrtfIntensity = wert; sp1.UpdateParameters(); }
            var sp2 = zweiterRef(s);
            if (sp2 != null) { sp2.HrtfIntensity = wert; sp2.UpdateParameters(); }
        }

        /// <summary>Fuer das Klang-Log: hat der Schalter diese Quelle raeumlich gemacht (und sie spielt noch so)?</summary>
        internal static bool VomSchalter(BetterSource q)
        {
            if (besitzZahl == 0 || q == null) return false;
            return BesitzTab.TryGetValue(q, out var b) && b.Hrtf && q.EnableSpatialization;
        }

        internal static bool An => an;
    }
}
