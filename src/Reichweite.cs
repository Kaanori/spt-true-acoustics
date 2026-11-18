using System;
using System.Reflection;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    // raid2242richtung (17.11.2026): Spielteil zu ReichweiteKern (Begruendung und Belege dort).
    //  C. Prefix SoundOcclusionProcessor.OnSourcePlayed: Reichweite des Behaelters auf die MaxDistance, mit der seine Quellen JETZT spielen
    //     (nur wachsend), dann UpdatePriority - kommt der Prozessor dadurch aus OutOfRange, rechnet Raumweg.PrioNach -> Neubeginn sofort
    //     (Combined und, seit heute, ContinuousPropagated). Das Spiel wendet danach in OnSourcePlayed sofort an, BEVOR SimpleSource.Play
    //     die Lautstaerke setzt und den Clip startet (BetterSource.Play ruft OnPlayed vor dem PlayOneShot, SimpleSource.cs:72-100).
    //  D. BetterAudio.PlayAtPoint mit Einmal-Okklusion: Postfix BetterSource.ResetOcclusion (im PlayAtPoint unmittelbar vor Play) setzt
    //     den Faktor auf 0 -> Play startet still. Postfix MultiSourceSpatialAudioOccluder.SetAudioSourceContainer merkt, ob das Spiel den
    //     Klang uebernommen hat; sonst (gleicher Raum) gibt der Postfix von PlayAtPoint ihn im selben Aufruf frei.
    //  tuerstart (18.11.2026): Tueren/Kisten/Schalter (WorldInteractiveObject.PlaySoundAtPoint) und Fenster (WindowBreaker.PlayHitSound/
    //     PlayPieceSound) spielen PlayAtPoint mit None und melden ERST DANACH mit OneShotPropagation an (WorldInteractiveObject.cs:1235-1253,
    //     WindowBreaker.cs:930-945) - derselbe Wettlauf wie D. Prefix/Finalizer dieser Aufrufer: still beginnen wie D, die Freigabe wartet bis
    //     nach der Anmeldung (Finalizer des Aufrufers statt des PlayAtPoint). Hoerer im Raum der Tuer, Tuer ohne Portal und ohne
    //     Raumpaar (InteractiveObjectOccluder.CheckOcclusion) oder Ziel None: das Spiel daempft nicht -> sofort frei wie im Spiel.
    //  Keine Allokation je Klang; Fehler -> Statistik.Fehler und Spielverhalten.
    internal static class Reichweite
    {
        internal static ConfigEntry<bool> BehaelterAn, StillAn;
        static int tiefe;                       // PlayAtPoint-Verschachtelung (Bank-Ueberladungen rufen die Clip-Ueberladung)
        static bool stillWunsch;                // laufendes PlayAtPoint hat Einmal-Okklusion
        static BetterSource stillQuelle;        // still gestartete Quelle dieses Aufrufs
        static bool uebernommen;                // MultiSource-Occluder hat sie angemeldet (und selbst stumm geschaltet)
        static int aussenArt;                   // tuerstart: 0 = kein Aufrufer mit spaeterer Anmeldung, sonst ART_*
        const int ART_TUER = 1, ART_OBJEKT = 2, ART_FENSTER = 3;
        // Diagnose (alle 30 s); Index ART_* fuer Tueren / andere Objekte (Kisten, Schalter, Kofferraeume) / Fenster
        static int dWachsen, dAusReichweite, dStill, dUebernommen, dFrei;
        static readonly int[] aStill = new int[4], aUeber = new int[4], aFrei = new int[4];
        static float naechsteDiag;

        internal static void Binden(ConfigFile c)
        {
            const string s = "0. Physics Mode (real values)";
            BehaelterAn = c.Bind(s, "Occlusion range follows the playing sound", true,
                "On (default since 0.8.8): when a pooled sound source plays with a longer range than it had when it was registered " +
                "(grenade bounces, objects that register early), the game kept the old, shorter range and never calculated walls and rooms for it - " +
                "grenade bounces behind walls played at full volume. On = the range is updated when the sound starts and the first value is " +
                "calculated at once. Off = game behaviour. Applies live.");
            StillAn = c.Bind(s, "One-shot sounds start only after their occlusion", true,
                "On (default since 0.8.8): bullet impacts, ricochets, doors, containers, breaking windows and other one-shot sounds in other rooms are started silent; " +
                "the game mutes them a moment later anyway until their path is calculated. Before, the first audio block (about 1/50 s) could " +
                "slip out at full volume - a short loud click even when the sound is 40 dB down. Sounds in your own room start normally. " +
                "Off = game behaviour. Applies live.");
        }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var gespielt = AccessTools.DeclaredMethod(typeof(SoundOcclusionProcessor), "OnSourcePlayed");
                if (gespielt != null) h.Patch(gespielt, prefix: new HarmonyMethod(typeof(Reichweite), nameof(GespieltVor)));
                else Plugin.Log.LogWarning("Occlusion range: SoundOcclusionProcessor.OnSourcePlayed not found -> game behaviour.");
                var reset = AccessTools.DeclaredMethod(typeof(BetterSource), "ResetOcclusion");
                var setc = AccessTools.DeclaredMethod(typeof(MultiSourceSpatialAudioOccluder), "SetAudioSourceContainer");
                int n = 0;
                if (reset != null && setc != null)
                {
                    foreach (var m in typeof(BetterAudio).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    {
                        if ((m.Name != "PlayAtPoint" && m.Name != "PlayAtPointDistant") || m.ReturnType != typeof(BetterSource)) continue;
                        if (Index(m) < 0) continue;
                        h.Patch(m, prefix: new HarmonyMethod(typeof(Reichweite), nameof(PunktVor)) { priority = Priority.First },
                            finalizer: new HarmonyMethod(typeof(Reichweite), nameof(PunktEnde)) { priority = Priority.Last });
                        n++;
                    }
                    h.Patch(reset, postfix: new HarmonyMethod(typeof(Reichweite), nameof(ResetNach)));
                    h.Patch(setc, postfix: new HarmonyMethod(typeof(Reichweite), nameof(SetNach)));
                }
                if (n == 0) Plugin.Log.LogWarning("One-shot start: BetterAudio.PlayAtPoint/ResetOcclusion not found -> game behaviour (first block may be unattenuated).");
                else Plugin.Log.LogInfo($"One-shot start: {n} PlayAtPoint overloads patched (start silent until occlusion).");
                if (n > 0) AussenAnwenden(h);
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>tuerstart: Aufrufer, die nach PlayAtPoint(None) selbst mit OneShotPropagation anmelden.</summary>
        static void AussenAnwenden(Harmony h)
        {
            int n = 0;
            var tuer = AccessTools.DeclaredMethod(typeof(WorldInteractiveObject), "PlaySoundAtPoint");
            if (tuer != null)
            {
                h.Patch(tuer, prefix: new HarmonyMethod(typeof(Reichweite), nameof(TuerVor)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(Reichweite), nameof(AussenEnde)) { priority = Priority.Last });
                n++;
            }
            foreach (var name in new[] { "PlayHitSound", "PlayPieceSound" })
            {
                var m = AccessTools.DeclaredMethod(typeof(WindowBreaker), name);
                if (m == null) continue;
                h.Patch(m, prefix: new HarmonyMethod(typeof(Reichweite), nameof(FensterVor)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(Reichweite), nameof(AussenEnde)) { priority = Priority.Last });
                n++;
            }
            if (n < 3) Plugin.Log.LogWarning($"One-shot start: only {n} of 3 door/window sound methods found -> missing ones keep game behaviour.");
            else Plugin.Log.LogInfo("One-shot start: door and window sounds patched (start silent until occlusion).");
        }

        static void TuerVor(WorldInteractiveObject __instance)
        {
            if (tiefe > 0 || aussenArt != 0) return;
            aussenArt = __instance is Door ? ART_TUER : ART_OBJEKT;
            stillQuelle = null; uebernommen = false;
        }

        static void FensterVor()
        {
            if (tiefe > 0 || aussenArt != 0) return;
            aussenArt = ART_FENSTER;
            stillQuelle = null; uebernommen = false;
        }

        /// <summary>Finalizer des Aufrufers: die Anmeldung (ProcessOcclusion/ProcessInteractiveObjectOcclusion) ist gelaufen -> wie D
        /// entscheiden. Laeuft auch bei Ausnahme (dann frei wie im Spiel).</summary>
        static Exception AussenEnde(Exception __exception)
        {
            int art = aussenArt;
            if (art == 0 || tiefe > 0) return __exception;
            aussenArt = 0;
            var q = stillQuelle;
            stillQuelle = null; stillWunsch = false;
            bool ueber = uebernommen; uebernommen = false;
            if (q == null) return __exception;
            try
            {
                aStill[art]++;
                if (ueber) aUeber[art]++;
                else
                {
                    q.SetOcclusionVolumeFactor(1f);
                    q.UpdateSourceVolume();
                    aFrei[art]++;
                }
                Diagnose();
            }
            catch (Exception e) { Statistik.Fehler(e); }
            return __exception;
        }

        /// <summary>Position des Parameters "occlusionTest" (EOcclusionTest); -1 = keiner.</summary>
        static int Index(MethodInfo m)
        {
            var p = m.GetParameters();
            for (int i = 0; i < p.Length; i++) if (p[i].ParameterType == typeof(EOcclusionTest) && p[i].Name == "occlusionTest") return i;
            return -1;
        }

        /// <summary>Prefix aller PlayAtPoint-Ueberladungen mit occlusionTest (Harmony bindet den Parameter nach Namen).</summary>
        static void PunktVor(EOcclusionTest occlusionTest)
        {
            var t = occlusionTest;
            if (tiefe++ > 0) return;
            stillQuelle = null; uebernommen = false;
            // tuerstart: Tuer/Fenster melden nach dem Play mit OneShotPropagation an -> deren Test entscheidet
            int wt = ReichweiteKern.WirksamerTest((int)t, aussenArt != 0, (int)EOcclusionTest.OneShotPropagation);
            stillWunsch = StillAn != null && Plugin.Aktiv.Value
                          && ReichweiteKern.StillStarten(StillAn.Value, wt, (int)EOcclusionTest.OneShotPropagation, (int)EOcclusionTest.OneShotFullOcclusion);
        }

        /// <summary>Postfix ResetOcclusion: im PlayAtPoint unmittelbar vor Play -> still beginnen (das Spiel stellt den Faktor gleich danach
        /// selbst auf 0). IncludedInOcclusionProcess ist bei Pool-Quellen an (Release setzt es, BetterSource.cs:335).</summary>
        static void ResetNach(BetterSource __instance)
        {
            if (!stillWunsch || tiefe <= 0 || stillQuelle != null || __instance == null) return;
            try { __instance.SetOcclusionVolumeFactor(0f); stillQuelle = __instance; }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void SetNach(MultiSourceSpatialAudioOccluder __instance, AudioSourceContainer sourceContainer)
        {
            if (stillQuelle == null || sourceContainer == null) return;
            try
            {
                if (sourceContainer.Count > 0 && ReferenceEquals(sourceContainer[0], stillQuelle) && __instance._activeContainersByID.ContainsKey(sourceContainer.ID))
                    uebernommen = true;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static Exception PunktEnde(Exception __exception)
        {
            if (--tiefe > 0) return __exception;
            tiefe = 0;
            if (aussenArt != 0) { stillWunsch = false; return __exception; }   // tuerstart: Freigabe erst nach der Anmeldung (AussenEnde)
            var q = stillQuelle;
            stillQuelle = null; stillWunsch = false;
            if (q == null) return __exception;
            try
            {
                dStill++;
                if (uebernommen) dUebernommen++;
                else
                {
                    // gleicher Raum (MultiSource-Ziel None), kein Audiosystem oder Ausnahme: wie im Spiel voll, sofort (speed 1, kein Lerp)
                    q.SetOcclusionVolumeFactor(1f);
                    q.UpdateSourceVolume();
                    dFrei++;
                }
                Diagnose();
            }
            catch (Exception e) { Statistik.Fehler(e); }
            uebernommen = false;
            return __exception;
        }

        /// <summary>C: Prefix SoundOcclusionProcessor.OnSourcePlayed.</summary>
        static void GespieltVor(SoundOcclusionProcessor __instance)
        {
            if (BehaelterAn == null || !BehaelterAn.Value || !Plugin.Aktiv.Value || __instance == null) return;
            try
            {
                var c = __instance._audioSourceContainer;
                if (c == null || !c.IsValid) return;
                double m = 0;
                for (int i = 0; i < c.Count; i++) { var s = c[i]; if (s != null && s.MaxDistance > m) m = s.MaxDistance; }
                if (!ReichweiteKern.Wachsen(c.MaxDistance, m, out double neu)) return;
                bool vorher = __instance.Priority == Audio.AuxiliaryAudioUtils.EAudioSourcePriority.OutOfRange;
                c.MaxDistance = (float)neu;
                c._sqrMaxDistance = (float)(neu * neu);
                dWachsen++;
                __instance.UpdatePriority();   // Postfix Raumweg.PrioNach: Combined aus OutOfRange -> Neubeginn (erster Wert sofort)
                if (vorher && __instance.Priority != Audio.AuxiliaryAudioUtils.EAudioSourcePriority.OutOfRange)
                {
                    dAusReichweite++;
                    Raumweg.NeubeginnBeimStart(__instance);   // ContinuousPropagated: erster Wert jetzt (Combined: schon in PrioNach)
                }
                Diagnose();
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void Diagnose()
        {
            float t = Time.realtimeSinceStartup;
            if (t < naechsteDiag) return;
            naechsteDiag = t + 30f;
            if (dStill + dWachsen + aStill[ART_TUER] + aStill[ART_OBJEKT] + aStill[ART_FENSTER] == 0) return;
            Plugin.Log.LogInfo($"[Diagnostics] One-shot start: first block already attenuated: {dUebernommen} (started silent {dStill}, " +
                               $"same room released at once {dFrei}). Occlusion range grown at play: {dWachsen}, of these out of range before: {dAusReichweite}. " +
                               $"Doors: attenuated {aUeber[ART_TUER]} (started silent {aStill[ART_TUER]}, released at once {aFrei[ART_TUER]}). " +
                               $"Other objects: attenuated {aUeber[ART_OBJEKT]} (started silent {aStill[ART_OBJEKT]}, released at once {aFrei[ART_OBJEKT]}). " +
                               $"Windows: attenuated {aUeber[ART_FENSTER]} (started silent {aStill[ART_FENSTER]}, released at once {aFrei[ART_FENSTER]}).");
        }
    }
}
