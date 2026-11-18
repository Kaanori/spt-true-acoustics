using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Audio.AmbientSubsystem;
using Audio.AudioCulling;
using Audio.RadioSystem;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.MovingPlatforms;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    // =========================================================================================
    //  0.8.6: Spielfehler in AudioSourceCulling an der Ursache behoben (Beleg + Regel in CullingKern)
    //
    //  Wer das Culling nutzt (EFT 4.1.6):
    //   - GameWorld.AudioSourceCulling: CameraManager.method_7 (aus SetCamera, einmal je Kamera = Raidstart) leert die Liste und
    //     meldet jede Schleife aus LocationScene.GetAllObjects<AudioSource>() an (nur isActiveAndEnabled, loop, spatialBlend > 0,
    //     ohne ISoundPlayer), NeedRestart = !playOnAwake; dazu InteractiveSubscriber (Schliessgeraeusche mit EnableAudioCulling,
    //     Register beim Abspielen, Remove beim Freigeben) und LoopLampSoundController (EnableRestart false bei Stop).
    //   - AmbientAudioSystem (eigene Komponente, eigene Liste): BaseAmbientSoundPlayer (3D; Play -> Register, Stop -> Remove)
    //     und SourceOccluder.SetupSources (_includeInAudioCulling; alle Kinder, auch abgeschaltete), NeedRestart = loop.
    //  Beide liefen mit derselben Work()-Schleife: nur die letzten 10 Eintraege je geschaltet.
    //
    //  Fix: Prefix auf Work() gibt eine gleichwertige Coroutine zurueck, die reihum ueber die ganze Liste geht; je Eintrag
    //  1:1 das Original (UpdateData, Abstand^2, priority, enabled = inReichweite, Play bei NeedRestart && !isPlaying &&
    //  activeInHierarchy, Null-Eintraege entfernen). Remove wird ersetzt (gleiche Logik + Zeiger nachfuehren),
    //  CleanRegisteredAudioSources setzt den Zeiger zurueck. Schalter aus = dieselbe Coroutine mit der Original-Reihenfolge.
    //  Einzige Abweichung je Eintrag: eine Quelle, die ein Skript nachweislich abgeschaltet hat, schaltet das Culling in
    //  Reichweite nicht ein (das Original ueberschrieb das jedes Mal; reihum traefe es jede solche Quelle). Erkannt an den
    //  steuernden Komponenten (CullingKern.Steuerung): ToggleAudioSourceByEvent, ClientBroadcastPlayer, Locomotive.
    //
    //  Dazu der zweite Teil derselben Ursache: LocalGame ruft beim Raidstart AudioUtils.ResetAudioBuffer -> AudioSettings.Reset,
    //  das alle Quellen anhaelt. Eine playOnAwake-Schleife, die dabei eingeschaltet und in Reichweite ist, startet das Culling
    //  nie neu (NeedRestart = false, enabled bleibt true - es gibt keinen Aus/Ein-Wechsel). Prefix merkt sich, welche
    //  Szenen-Schleifen vor dem Reset spielten, Postfix startet genau die wieder, die der Reset angehalten hat. Dasselbe
    //  bei einem Audio-Neustart durch Unity selbst (AudioSettings.OnAudioConfigurationChanged, Geraetewechsel): da gibt es
    //  kein "vorher", darum merkt sich das Culling je Besuch, ob die Quelle spielte.
    // =========================================================================================
    internal static class CullingFix
    {
        internal static ConfigEntry<bool> Aktiv;
        internal static bool An => Aktiv == null || Aktiv.Value;

        sealed class Zustand : IReihumListe
        {
            public AudioSourceCulling C;
            public string Name;
            public int Zeiger, Runden;
            public float Start = -1f;
            public bool Gemeldet;
            public int Bearbeitet;                                          // Diagnose
            public readonly HashSet<int> Fremd = new HashSet<int>();        // in Reichweite, aber von einem Skript ausgeschaltet -> bleibt aus (Diagnose)
            public readonly HashSet<int> Spielt = new HashSet<int>();       // spielte beim letzten Besuch (fuer den Audio-Neustart durch Unity)

            public int Anzahl => C._audioSources.Count;

            public bool Gueltig(int i)
            {
                var v = C._audioSources[i];
                return !(v.AudioSource == null) && !(v.Transform == null);
            }

            public void Bearbeiten(int i) => Eintrag(this, i, false);

            public void Entfernen(int i) => Weg(this, i);
        }

        static readonly ConditionalWeakTable<AudioSourceCulling, Zustand> zustaende = new ConditionalWeakTable<AudioSourceCulling, Zustand>();
        static readonly List<Zustand> alle = new List<Zustand>();          // fuer den Audio-Neustart (zerstoerte werden ausgeraeumt)

        static Zustand Z(AudioSourceCulling c)
        {
            if (!zustaende.TryGetValue(c, out var z))
            {
                z = new Zustand { C = c, Name = c.GetComponent<GameWorld>() != null ? "GameWorld" : c.GetComponent<AmbientAudioSystem>() != null ? "AmbientAudioSystem" : c.gameObject.name };
                zustaende.Add(c, z);
                alle.RemoveAll(x => x.C == null);
                alle.Add(z);
            }
            return z;
        }

        /// <summary>Eintrag i aus der Liste nehmen wie das Spiel (Null-Eintrag in Work, Remove): Liste, InstanceID-Menge, int_1.</summary>
        static void Weg(Zustand z, int i)
        {
            var c = z.C;
            int id = c._audioSources[i].InstanceID;
            c._audioSources.RemoveAt(i);
            c._audioSourcesInstanceID.Remove(id);
            c.int_1 = Math.Min(c._audioSources.Count, CullingKern.SpielBudget);
            z.Spielt.Remove(id);
        }

        internal static void Binden(ConfigFile c)
        {
            Aktiv = c.Bind("1. General", "Fix game audio culling (all looping sources)", true,
                "Fixes a bug in the game's AudioSourceCulling: every frame it starts again at the end of its list and handles at most 10 entries, " +
                "so only the last 10 registered looping sources were ever switched on/off by distance (fires, V-Ex taxi, machines, lamps, ambient loops ...). " +
                "All others kept the state they had when registered - e.g. silent for the whole raid after the audio reset at raid start. " +
                "On: the same per-source logic, but in turn over the whole list (every source at least every 0.5 s); loops stopped by that audio reset " +
                "(or by an audio device change) are started again. Works without Physics Mode and without 'Enabled'. Applies live.");
        }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var t = typeof(AudioSourceCulling);
                var work = AccessTools.DeclaredMethod(t, nameof(AudioSourceCulling.Work));
                var remove = AccessTools.DeclaredMethod(t, nameof(AudioSourceCulling.Remove));
                var clean = AccessTools.DeclaredMethod(t, nameof(AudioSourceCulling.CleanRegisteredAudioSources));
                bool felder = AccessTools.DeclaredField(t, "_audioSources") != null && AccessTools.DeclaredField(t, "int_1") != null
                    && AccessTools.DeclaredField(t, "_priorityCalculator") != null && AccessTools.DeclaredField(t, "_transform") != null
                    && AccessTools.DeclaredField(t, "_isWorking") != null;
                if (work == null || remove == null || clean == null || !felder)
                {
                    Plugin.Log.LogWarning("Audio culling fix: AudioSourceCulling.Work/Remove/fields not found (game update?) -> game culling unchanged.");
                }
                else
                {
                    h.Patch(work, prefix: new HarmonyMethod(typeof(CullingFix), nameof(VorWork)));
                    h.Patch(remove, prefix: new HarmonyMethod(typeof(CullingFix), nameof(VorRemove)));
                    h.Patch(clean, prefix: new HarmonyMethod(typeof(CullingFix), nameof(VorClean)), postfix: new HarmonyMethod(typeof(CullingFix), nameof(NachClean)));
                    SteuerungAnwenden(h);
                }
                var reset = AccessTools.DeclaredMethod(typeof(AudioUtils), nameof(AudioUtils.ResetAudioBuffer));
                if (reset == null) Plugin.Log.LogWarning("Audio culling fix: AudioUtils.ResetAudioBuffer not found (game update?) -> loops stopped by the audio reset are not restarted.");
                else h.Patch(reset, prefix: new HarmonyMethod(typeof(CullingFix), nameof(VorReset)), postfix: new HarmonyMethod(typeof(CullingFix), nameof(NachReset)));
                AudioSettings.OnAudioConfigurationChanged -= NachAudioKonfig;
                AudioSettings.OnAudioConfigurationChanged += NachAudioKonfig;
            }
            catch (Exception e) { Statistik.Fehler(e); Plugin.Log.LogError($"Audio culling fix: applying failed: {e.Message}"); }
        }

        // ---- Skript-Steuerung erkennen (Beleg in CullingKern.Steuerung) ----
        static readonly Dictionary<int, bool> wunsch = new Dictionary<int, bool>();   // InstanceID -> letzter Skriptbefehl "an"
        static readonly HashSet<int> lok = new HashSet<int>();                       // Locomotive.Sources

        static void SteuerungAnwenden(Harmony h)
        {
            void P(Type typ, string name, string postfix)
            {
                try
                {
                    var m = AccessTools.DeclaredMethod(typ, name);
                    if (m == null) { Plugin.Log.LogWarning($"Audio culling fix: {typ.Name}.{name} not found (game update?) -> its sources are switched like any other."); return; }
                    h.Patch(m, postfix: new HarmonyMethod(typeof(CullingFix), postfix));
                }
                catch (Exception ex) { Statistik.Fehler(ex); Plugin.Log.LogWarning($"Audio culling fix: patch {typ.Name}.{name} failed: {ex.Message}"); }
            }
            P(typeof(ToggleAudioSourceByEvent), nameof(ToggleAudioSourceByEvent.Awake), nameof(NachToggle));
            P(typeof(ToggleAudioSourceByEvent), nameof(ToggleAudioSourceByEvent.OnNeedToEnable), nameof(NachToggle));
            P(typeof(ToggleAudioSourceByEvent), nameof(ToggleAudioSourceByEvent.OnNeedToDisable), nameof(NachToggle));
            P(typeof(ClientBroadcastPlayer), nameof(ClientBroadcastPlayer.ChangeAudibleState), nameof(NachRadio));
            P(typeof(Locomotive), nameof(Locomotive.Init), nameof(NachLokInit));
        }

        static void Wunsch(AudioSource s, bool an) { if (s != null) wunsch[s.GetInstanceID()] = an; }

        /// <summary>Awake: Startzustand aus der Szene; OnNeedToEnable/Disable: der Befehl des Skripts (enabled danach).</summary>
        static void NachToggle(ToggleAudioSourceByEvent __instance)
        {
            try { if (__instance != null && __instance._audioSource != null) Wunsch(__instance._audioSource, __instance._audioSource.enabled); }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        static void NachRadio(ClientBroadcastPlayer __instance)
        {
            try
            {
                if (__instance == null) return;
                bool an = __instance.CurrentAudibleState != EAudibleState.Mute;
                Wunsch(__instance._sourceA, an);
                Wunsch(__instance._sourceB, an);
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        static void NachLokInit(Locomotive __instance)
        {
            try
            {
                if (__instance == null || __instance.Sources == null) return;
                foreach (var s in __instance.Sources) if (s != null) lok.Add(s.GetInstanceID());
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        /// <summary>Hat ein Skript diese Quelle bewusst abgeschaltet? (auch fuer die Stumm-Diagnose in FesteQuellen)</summary>
        internal static bool SkriptAus(AudioSource s) => s != null && SkriptAus(s.GetInstanceID(), s);

        static bool SkriptAus(int id, AudioSource s)
        {
            if (wunsch.TryGetValue(id, out bool w)) return CullingKern.SkriptAus(CullingKern.Steuerung.Wunsch, w, s.enabled, s.volume);
            if (lok.Contains(id)) return CullingKern.SkriptAus(CullingKern.Steuerung.Lok, false, s.enabled, s.volume);
            return false;
        }

        // ---- Work: eigene, gleichwertige Coroutine ----
        static bool VorWork(AudioSourceCulling __instance, ref IEnumerator __result)
        {
            __result = Arbeit(__instance);
            return false;
        }

        static IEnumerator Arbeit(AudioSourceCulling c)
        {
            var z = Z(c);
            if (z.Start < 0) z.Start = Time.realtimeSinceStartup;
            while (c._audioSources.Count > 0)
            {
                if (c._transform == null) { yield return null; continue; }
                try
                {
                    if (An) Reihum(z);
                    else Original(z);
                }
                catch (Exception ex) { Statistik.Fehler(ex); }
                yield return null;
            }
            c._isWorking = false;
        }

        /// <summary>Wie das Spiel, aber reihum: Zeiger wandert ueber die ganze Liste.</summary>
        static void Reihum(Zustand z)
        {
            var c = z.C;
            int runden = z.Runden;
            int budget = CullingKern.Budget(c._audioSources.Count, c.int_1, Time.unscaledDeltaTime);
            z.Bearbeitet += CullingKern.Bild(z, ref z.Zeiger, budget, ref z.Runden);
            if (!z.Gemeldet && z.Runden > runden && Time.realtimeSinceStartup - z.Start >= 20f && c._audioSources.Count > 0) Melden(z);
        }

        static void Melden(Zustand z)
        {
            z.Gemeldet = true;
            int n = z.C._audioSources.Count, nie = 0;
            for (int i = 0; i < n; i++) if (!CullingKern.VomSpielErreicht(i, n)) nie++;
            string fremd = z.Fremd.Count > 0 ? $"; {z.Fremd.Count} within range kept off because a game script switched them off (ToggleAudioSourceByEvent, radio mute, Locomotive fade-out)" : "";
            z.Fremd.Clear();
            string zeile = $"AudioSourceCulling [{z.Name}]: {n} sources, {nie} of them never reached by the game (all but its last {CullingKern.SpielBudget}; fixed){fremd}";
            Plugin.Log.LogInfo(zeile);
            if (KlangLog.An) KlangLog.Text("[Culling] " + zeile);
        }

        /// <summary>Original-Work 1:1 (Schalter aus): ab Listenende, hoechstens int_1 Eintraege.</summary>
        static void Original(Zustand z)
        {
            var c = z.C;
            int num = 0;
            int num2 = c._audioSources.Count - 1;
            while (num2 >= 0 && num < c.int_1)
            {
                if (z.Gueltig(num2)) Eintrag(z, num2, true);
                else Weg(z, num2);
                num++;
                num2--;
            }
        }

        /// <summary>Ein Eintrag wie im Spiel (AudioSourceCulling.Work, Zweig 'gueltig'): UpdateData, Abstand^2, priority,
        /// enabled = inReichweite, Play bei NeedRestart. Einziger Unterschied ausser im Original-Modus: eine von einem Skript
        /// bewusst abgeschaltete Quelle (CullingKern.SkriptAus) bleibt aus.</summary>
        static void Eintrag(Zustand z, int i, bool original)
        {
            var c = z.C;
            var value = c._audioSources[i];
            value.UpdateData();
            c._audioSources[i] = value;
            float d2 = Vector3.SqrMagnitude(value.Transform.position - c._transform.position);
            bool an = d2 <= value.SqrCullDistance;
            var s = value.AudioSource;
            s.priority = c._priorityCalculator.CalculatePriority(d2, value.SqrCullDistance);
            bool ein = s.enabled;
            bool neu = original ? an : CullingKern.Schalter(an, an && SkriptAus(value.InstanceID, s));
            if (an && !neu && !z.Gemeldet) z.Fremd.Add(value.InstanceID);
            if (neu != ein) s.enabled = neu;
            if (neu && s.gameObject.activeInHierarchy && value.NeedRestart && !s.isPlaying) s.Play();
            if (original) return;
            if (s.isPlaying) z.Spielt.Add(value.InstanceID);
            else z.Spielt.Remove(value.InstanceID);
        }

        // ---- Remove: gleiche Logik wie im Spiel, Zeiger nachfuehren ----
        static bool VorRemove(AudioSourceCulling __instance, AudioSource audioSource)
        {
            try
            {
                if (audioSource == null) return false;
                int id = audioSource.GetInstanceID();
                var z = Z(__instance);
                var l = __instance._audioSources;
                for (int i = 0; i < l.Count; i++)
                {
                    if (l[i].InstanceID != id) continue;
                    Weg(z, i);
                    CullingKern.Entfernt(ref z.Zeiger, i);
                    return false;
                }
                return false;
            }
            catch (Exception ex) { Statistik.Fehler(ex); return true; }
        }

        // ---- CleanRegisteredAudioSources: vor erneutem method_7 ausgeschaltete Quellen einschalten (sonst nicht mehr angemeldet) ----
        static void VorClean(AudioSourceCulling __instance)
        {
            if (!An) return;
            try
            {
                int n = 0;
                foreach (var v in __instance._audioSources)
                {
                    var s = v.AudioSource;
                    if (s == null) continue;
                    if (!CullingKern.VorLeerenEinschalten(__instance._isWorking, s.enabled, SkriptAus(v.InstanceID, s))) continue;
                    s.enabled = true;
                    n++;
                }
                if (n > 0) Plugin.Log.LogInfo($"AudioSourceCulling [{Z(__instance).Name}]: list cleared for re-registration - {n} sources switched off by the culling switched on again so they are registered again (audio culling fix).");
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        static void NachClean(AudioSourceCulling __instance)
        {
            if (zustaende.TryGetValue(__instance, out var z)) { z.Zeiger = 0; z.Spielt.Clear(); }
        }

        // ---- Audio-Reset (AudioUtils.ResetAudioBuffer): was spielte, wieder starten ----
        // Aufrufer (4.1.6): LocalGame beim Raidstart (GameWorld da), TarkovApplication.OnGameEnd (Raidende), CommonClientApplication.Awake
        // (Spielstart), NetworkGame. Neustart nur mit geladener GameWorld (Raid/Versteck) - im Menue gibt es keine Szenen-Schleifen.
        static readonly List<AudioSource> vorReset = new List<AudioSource>(256);
        static bool imReset;

        static void VorReset()
        {
            vorReset.Clear();
            if (!An || !Singleton<GameWorld>.Instantiated) return;
            try
            {
                imReset = true;
                foreach (var s in LocationScene.GetAllObjects<AudioSource>())
                    if (s != null && s.loop && s.isPlaying) vorReset.Add(s);
            }
            catch (Exception ex) { Statistik.Fehler(ex); vorReset.Clear(); }
        }

        static void NachReset()
        {
            imReset = false;
            if (vorReset.Count == 0) return;
            try
            {
                int angehalten = 0;
                foreach (var s in vorReset)
                {
                    if (s == null || !s.isActiveAndEnabled || s.isPlaying) continue;
                    s.Play();
                    angehalten++;
                }
                Plugin.Log.LogInfo($"Audio reset (AudioUtils.ResetAudioBuffer, game world loaded): {vorReset.Count} looping scene sources were playing, {angehalten} stopped by the reset -> started again (audio culling fix).");
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
            finally { vorReset.Clear(); }
        }

        /// <summary>Unity hat das Audiosystem neu gestartet (Geraetewechsel HDMI/Headset, AudioSettings.Reset). Bei unserem eigenen
        /// Reset-Hook erledigt das NachReset. Sonst: jede angemeldete Quelle, die beim letzten Besuch spielte und jetzt steht,
        /// wieder starten (NeedRestart-Quellen startet das Culling selbst).</summary>
        static void NachAudioKonfig(bool geraetGewechselt)
        {
            if (!An || imReset) return;
            try
            {
                int n = 0, gesamt = 0;
                alle.RemoveAll(x => x.C == null);
                foreach (var z in alle)
                {
                    var c = z.C;
                    if (c._transform == null) continue;
                    var p = c._transform.position;
                    foreach (var v in c._audioSources)
                    {
                        var s = v.AudioSource;
                        if (s == null || v.Transform == null) continue;
                        gesamt++;
                        bool nah = Vector3.SqrMagnitude(v.Transform.position - p) <= v.SqrCullDistance;
                        if (!CullingKern.NeustartNachKonfig(z.Spielt.Contains(v.InstanceID), s.isPlaying, s.enabled, s.gameObject.activeInHierarchy, nah, v.NeedRestart)) continue;
                        s.Play();
                        n++;
                    }
                }
                if (gesamt > 0)
                    Plugin.Log.LogInfo($"Audio configuration changed ({(geraetGewechselt ? "output device changed" : "audio reset")}): {n} of {gesamt} culled looping sources were stopped -> started again (audio culling fix).");
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }
    }
}
