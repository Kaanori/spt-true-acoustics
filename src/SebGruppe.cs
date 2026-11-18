using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // occludedmixer (17.11.2026): SEB-Live-Schuesse eines verdeckten Schuetzen bleiben im Physik-Modus in Guns/Gunshots
    // (Schalter in "7. Weapon Directivity", Standard AUS). Rechnung und Belege: SebGruppeKern.cs.
    // Eingriff: Postfix auf SEB LiveWeaponSoundEngine.ResolveMixerGroup (privat, 1.0.x/1.1.x gleich) - nur der RUECKGABEWERT wird
    // ersetzt. Das Feld WeaponSoundPlayer._currentMixerGroup, das SEB dort ebenfalls setzt, bleibt Occluded: daran haengt
    // IsOccludedToListener (Spiel: BulletSoundPlayersController.TryShot spielt dann kein Ueberschall-Knallen, FireSonicSound) -
    // das bleibt Spielverhalten. Die Quelle (auch die Hall-Zwillinge, ReverbSuperSource.SetMixerGroup) bekommt die Gruppe je Schuss
    // neu (GunshotEmitterBase.TryStartShot) -> Schalter wirkt ab dem naechsten Schuss, kein Zustand bleibt haengen.
    // Nicht erfasst: SEBs V1-Weg und Spiel ohne SEB (WeaponSoundPlayer.UpdateMixerGroup, Schwelle 0,55) - dort bleibt Occluded.
    // ------------------------------------------------------------------------------------------
    internal static class SebGruppe
    {
        internal static ConfigEntry<bool> Aktiv;
        static bool an;
        internal static bool Bereit;

        // Hauptfaden, synchron in TryStartShot: Ergebnis der letzten Gruppenwahl
        static byte letzte;
        // Klang-Log: Zustand je Quelle (Instanz-ID), nur geschrieben, wenn das Klang-Log laeuft
        static readonly Dictionary<int, byte> jeQuelle = new Dictionary<int, byte>(128);

        // Diagnose (30 s)
        internal static int Gewaehlt, Occluded, Umgelegt;

        internal static void Binden(ConfigFile c, string sek)
        {
            Aktiv = c.Bind(sek, "Occluded shots: no extra occluded mix (physics)", true,
                "Sound Engine Backport sends shots of an occluded shooter (the game's occlusion value above 0.5) to the mixer group Guns/Occluded. " +
                "That group plays an unfiltered copy of the shot in parallel (Guns/OccludedTail, ducked only above -20 dB): quiet shots get up to 6 dB louder, " +
                "loud ones a flatter peak, and the shot jumps by that much when the occlusion value crosses 0.5. Physics Mode already calculates level and tone of these shots. " +
                "On = while Physics Mode is on, these shots stay in Guns/Gunshots like all others (supersonic cracks of occluded shooters stay off, as in the game). " +
                "Off = as Sound Engine Backport intends. Applies live (from the next shot). \"mixer ...\" in the sound log shows the group of each shot.");
            an = Aktiv.Value;
            Aktiv.SettingChanged += (_, __) => an = Aktiv.Value;
        }

        // Aufruf aus SebAkustik.SebAnwenden (Version 1.0.x/1.1.x schon geprueft)
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Anwenden(Harmony h)
        {
            var eng = AccessTools.TypeByName("SoundEngineBackport.LiveEngine.LiveWeaponSoundEngine");
            var emi = AccessTools.TypeByName("SoundEngineBackport.LiveEngine.GunshotEmitterBase");
            MethodBase wahl = eng != null ? AccessTools.DeclaredMethod(eng, "ResolveMixerGroup") : null;
            MethodBase start = emi != null ? AccessTools.DeclaredMethod(emi, "TryStartShot") : null;
            if (wahl == null || start == null || ((MethodInfo)wahl).ReturnType != typeof(AudioMixerGroup))
            {
                Plugin.Log.LogWarning("SEB add-on: LiveWeaponSoundEngine.ResolveMixerGroup / TryStartShot not found - occluded shots keep SEB's mixer group.");
                return;
            }
            h.Patch(wahl, postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(SebGruppe), nameof(WahlNach))));
            h.Patch(start, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(SebGruppe), nameof(StartVor))),
                           postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(SebGruppe), nameof(StartNach))));
            Bereit = true;
            // nachhallschuss (17.11.): Nachhall-Stimme der Gegnerschuesse (eigener Postfix, eigenes try - die Gruppenwahl bleibt unberuehrt)
            try { NachhallSchuss.Anwenden(h, start); }
            catch (Exception ex) { Statistik.Fehler(ex); Plugin.Log.LogWarning($"SEB add-on: reverb voice for gunshots not hooked ({ex.Message}) - shots keep their dry diffuse part."); }
        }

        static void StartVor() => letzte = SebGruppeKern.Unbekannt;

        // Postfix LiveWeaponSoundEngine.ResolveMixerGroup(): AudioMixerGroup
        static void WahlNach(ref AudioMixerGroup __result)
        {
            try
            {
                var ba = MonoBehaviourSingleton<BetterAudio>.Instance;
                if (ba == null || __result == null) return;
                bool willOcc = ReferenceEquals(__result, ba.GunshotOccludedMixerGroup);
                var gun = ba.GunshotMixerGroup;
                bool um = SebGruppeKern.Umlegen(an, Plugin.Aktiv.Value, Physik.An, SebAkustik.SebGestoert, willOcc, gun != null);
                if (um) __result = gun;
                letzte = SebGruppeKern.Zustand(willOcc, um);
                Gewaehlt++;
                if (willOcc) { if (um) Umgelegt++; else Occluded++; }
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        // Postfix GunshotEmitterBase.TryStartShot(context, scheduled, out SuperSource source, out float length): Zustand je Quelle fuers Klang-Log
        static void StartNach(bool __result, ref SuperSource source)
        {
            try
            {
                if (!KlangLog.An || !__result || source == null || letzte == SebGruppeKern.Unbekannt) return;
                if (jeQuelle.Count > 4096) jeQuelle.Clear();
                jeQuelle[source.GetInstanceID()] = letzte;
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
            finally { letzte = SebGruppeKern.Unbekannt; }
        }

        /// <summary>Klang-Log: Gruppe des letzten Schusses dieser Quelle (0 = kein SEB-Live-Schuss bekannt).</summary>
        internal static byte Zustand(int quelleId) => jeQuelle.TryGetValue(quelleId, out var z) ? z : SebGruppeKern.Unbekannt;

        internal static void Leeren() => jeQuelle.Clear();

        internal static string Diagnose()
        {
            if (Gewaehlt == 0) return null;
            string s = $"mixer {Gewaehlt} shots, Occluded {Occluded} kept (SEB), {Umgelegt} kept in Gunshots (physics)";
            Gewaehlt = Occluded = Umgelegt = 0;
            return s;
        }
    }
}
