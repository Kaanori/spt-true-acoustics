using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace Materialklang
{
    // lampenwand (17.11.2026) - Klangtraeger fuer feste Quellen mit sichtbaren Teilen. Regel, Ursache und Beleg: TraegerKern.cs.
    // Aufrufer: Richtung.cs (Setzen/Zurueck/Freigabe, je Bild Spiegeln). Keine Harmony-Haken. Keine Allokation je Bild (Abstandskurve nur
    // beim Anlegen kopiert; aendert das Spiel rolloffMode, wird neu kopiert).
    internal static class Traeger
    {
        internal static ConfigEntry<bool> Aktiv;
        internal static bool An => Aktiv != null && Aktiv.Value;

        sealed class Eintrag
        {
            public BetterSource S;
            public AudioSource Alt, Neu;
            public GameObject Go;
            public AudioLowPassFilter LpA, LpN;
            public AudioHighPassFilter HpA, HpN;
            public AudioRolloffMode Rolloff;
            public bool VorMute;
        }

        static readonly Dictionary<BetterSource, Eintrag> Tabelle = new Dictionary<BetterSource, Eintrag>();
        static readonly List<Eintrag> Liste = new List<Eintrag>();
        static readonly Dictionary<BetterSource, TraegerKern.Grund> Abgelehnt = new Dictionary<BetterSource, TraegerKern.Grund>();
        static readonly Dictionary<BetterSource, int> AbgelehntBild = new Dictionary<BetterSource, int>();
        const int NeuPruefenBilder = 120;   // abgelehnte Quelle nach ~2 s neu pruefen (Schleife/Clip setzt das Spiel erst beim Abspielen)
        static readonly List<Component> filterPuffer = new List<Component>(8);
        const int MaxTraeger = 32;
        static int dAngelegt, dFrei, dNachgesetzt, dAbgelehnt, dBilder;

        internal static void Binden(ConfigFile c, string s)
        {
            Aktiv = c.Bind(s, "Room paths: carrier for fixed sources with visible parts", true,
                "Fluorescent lamps carry their hum on the visible lamp object, so the sound could not be moved to the opening it comes " +
                "through and was heard from the lamp itself, through walls and ceilings. On = an invisible carrier inside the lamp plays the same " +
                "loop at the same position in the clip and follows every setting of the original (volume incl. the insulation, pitch, distance " +
                "curve, mixer, low/high-pass); the original keeps running muted, the lamp model stays where it is, only the carrier is moved to " +
                "the opening. Only for looping sounds without spatializer. Off = from the lamp (before). Applies live.");
            Aktiv.SettingChanged += (_, __) => { if (!Aktiv.Value) AllesFrei(); };
        }

        /// <summary>Quelle ueber den Traeger an die Weltposition 'ziel' setzen (anlegen, wenn noetig). false = nicht moeglich (Grund im Log).</summary>
        internal static bool Setzen(BetterSource s, Vector3 ziel)
        {
            if (s == null) return false;
            try
            {
                if (!Tabelle.TryGetValue(s, out var e))
                {
                    if (!An || Liste.Count >= MaxTraeger) return false;
                    if (AbgelehntBild.TryGetValue(s, out int ab) && Time.frameCount - ab < NeuPruefenBilder) return false;
                    e = Anlegen(s);
                    if (e == null) return false;
                }
                if (e.Go == null || e.Alt == null) { Frei(s); return false; }
                e.Go.transform.position = ziel;
                return true;
            }
            catch (Exception ex) { Statistik.Fehler(ex); Frei(s); return false; }
        }

        /// <summary>Traeger zurueck an den echten Ort der Quelle (bleibt angelegt: kein Umschalten Original/Traeger, kein Knacken).</summary>
        internal static void Zurueck(BetterSource s)
        {
            if (s == null || Tabelle.Count == 0 || !Tabelle.TryGetValue(s, out var e) || e.Go == null) return;
            try { e.Go.transform.localPosition = Vector3.zero; }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        internal static bool Hat(BetterSource s) => s != null && Tabelle.Count > 0 && Tabelle.ContainsKey(s);

        /// <summary>Grund, warum diese Quelle keinen Traeger bekam (null = keiner bekannt), fuers Klang-Log.</summary>
        internal static string Ablehnung(BetterSource s)
            => s != null && Abgelehnt.TryGetValue(s, out var g) ? TraegerKern.Text(g) : null;

        static Eintrag Anlegen(BetterSource s)
        {
            var alt = s.source1;
            if (alt == null) return null;
            bool fremd = false; AudioLowPassFilter lp = null; AudioHighPassFilter hp = null;
            filterPuffer.Clear();
            try
            {
                alt.GetComponents(filterPuffer);
                foreach (var c in filterPuffer)
                {
                    if (c == null || c is AudioSource || c is Transform) continue;
                    if (c is AudioLowPassFilter l) { lp = l; continue; }
                    if (c is AudioHighPassFilter h) { hp = h; continue; }
                    if (c is Behaviour b && b.GetType().Namespace == "UnityEngine" && b.GetType().Name.StartsWith("Audio", StringComparison.Ordinal)) fremd = true;
                }
            }
            finally { filterPuffer.Clear(); }
            var g = TraegerKern.Zulassen(An, alt.loop, alt.clip != null, alt.spatialize, fremd);
            if (g != TraegerKern.Grund.Ja)
            {
                if (Abgelehnt.Count > 256) { Abgelehnt.Clear(); AbgelehntBild.Clear(); }
                if (!Abgelehnt.TryGetValue(s, out var alt0) || alt0 != g) dAbgelehnt++;
                Abgelehnt[s] = g; AbgelehntBild[s] = Time.frameCount;
                if (dAbgelehnt <= 12) Plugin.Log.LogInfo("Carrier: '" + alt.gameObject.name + "' stays at its place (" + TraegerKern.Text(g) + ").");
                return null;
            }
            var go = new GameObject("TrueAcoustics carrier") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(alt.transform, false);
            var neu = go.AddComponent<AudioSource>();
            neu.playOnAwake = false;
            var e = new Eintrag { S = s, Alt = alt, Neu = neu, Go = go, LpA = lp, HpA = hp, VorMute = alt.mute };
            if (lp != null) e.LpN = go.AddComponent<AudioLowPassFilter>();
            if (hp != null) e.HpN = go.AddComponent<AudioHighPassFilter>();
            Kurven(e);
            Abgelehnt.Remove(s); AbgelehntBild.Remove(s);
            Tabelle[s] = e; Liste.Add(e); dAngelegt++;
            Spiegeln(e);
            return e;
        }

        /// <summary>Abstandskurve(n) kopieren (Allokation: nur beim Anlegen oder wenn das Spiel den rolloffMode aendert).</summary>
        static void Kurven(Eintrag e)
        {
            var a = e.Alt; var n = e.Neu;
            n.rolloffMode = a.rolloffMode; e.Rolloff = a.rolloffMode;
            if (a.rolloffMode == AudioRolloffMode.Custom) n.SetCustomCurve(AudioSourceCurveType.CustomRolloff, a.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
            n.SetCustomCurve(AudioSourceCurveType.SpatialBlend, a.GetCustomCurve(AudioSourceCurveType.SpatialBlend));
            n.SetCustomCurve(AudioSourceCurveType.Spread, a.GetCustomCurve(AudioSourceCurveType.Spread));
            n.SetCustomCurve(AudioSourceCurveType.ReverbZoneMix, a.GetCustomCurve(AudioSourceCurveType.ReverbZoneMix));
        }

        /// <summary>Jedes Bild (Richtung.SystemSpaetNach, nach allen Okklusionen): Traeger auf den Stand des Originals bringen.</summary>
        internal static void Bild()
        {
            if (Liste.Count == 0) return;
            dBilder++;
            for (int i = Liste.Count - 1; i >= 0; i--)
            {
                var e = Liste[i];
                try
                {
                    if (e.Alt == null || e.Neu == null || e.Go == null || e.S == null || e.S.PlayBackState == BetterSource.EPlayBackState.Released) { Frei(e.S, i); continue; }
                    Spiegeln(e);
                }
                catch (Exception ex) { Statistik.Fehler(ex); Frei(e.S, i); }
            }
        }

        static void Spiegeln(Eintrag e)
        {
            var a = e.Alt; var n = e.Neu;
            if (e.Rolloff != a.rolloffMode) Kurven(e);
            if (!ReferenceEquals(n.outputAudioMixerGroup, a.outputAudioMixerGroup)) n.outputAudioMixerGroup = a.outputAudioMixerGroup;
            n.volume = a.volume; n.pitch = a.pitch; n.loop = a.loop;
            n.spatialBlend = a.spatialBlend; n.panStereo = a.panStereo; n.spread = a.spread; n.dopplerLevel = a.dopplerLevel;
            n.priority = a.priority; n.minDistance = a.minDistance; n.maxDistance = a.maxDistance; n.reverbZoneMix = a.reverbZoneMix;
            n.bypassEffects = a.bypassEffects; n.bypassListenerEffects = a.bypassListenerEffects; n.bypassReverbZones = a.bypassReverbZones;
            n.ignoreListenerPause = a.ignoreListenerPause; n.ignoreListenerVolume = a.ignoreListenerVolume;
            if (e.LpA != null && e.LpN != null) { e.LpN.enabled = e.LpA.enabled; e.LpN.cutoffFrequency = e.LpA.cutoffFrequency; e.LpN.lowpassResonanceQ = e.LpA.lowpassResonanceQ; }
            if (e.HpA != null && e.HpN != null) { e.HpN.enabled = e.HpA.enabled; e.HpN.cutoffFrequency = e.HpA.cutoffFrequency; e.HpN.highpassResonanceQ = e.HpA.highpassResonanceQ; }
            // Original stumm: hat es jemand wieder laut geschaltet, gilt das als neuer Grundzustand (Lampen: kein Schreiber von mute, belegt im Dekompilat)
            if (!a.mute) { e.VorMute = false; a.mute = true; }
            n.mute = e.VorMute;
            n.enabled = a.enabled;
            if (!ReferenceEquals(n.clip, a.clip)) { if (n.isPlaying) n.Stop(); n.clip = a.clip; }
            if (a.isPlaying && a.enabled && n.clip != null)
            {
                int laenge = n.clip.samples;
                if (!n.isPlaying) { n.Play(); n.timeSamples = Mathf.Clamp(a.timeSamples, 0, Math.Max(0, laenge - 1)); }
                else
                {
                    int v = TraegerKern.Versatz(a.timeSamples, n.timeSamples, laenge);
                    if (TraegerKern.Nachsetzen(v, Puffer())) { n.timeSamples = Mathf.Clamp(a.timeSamples, 0, Math.Max(0, laenge - 1)); dNachgesetzt++; }
                }
            }
            else if (n.isPlaying) n.Stop();
        }

        static int puffer = -1;
        static int Puffer()
        {
            if (puffer < 0) { try { AudioSettings.GetDSPBufferSize(out int len, out _); puffer = len > 0 ? len : 1024; } catch { puffer = 1024; } }
            return puffer;
        }

        internal static void Frei(BetterSource s) { if (s != null) Frei(s, -1); }

        static void Frei(BetterSource s, int index)
        {
            Eintrag e = null;
            if (index >= 0 && index < Liste.Count) e = Liste[index];
            else if (s != null && Tabelle.TryGetValue(s, out var x)) e = x;
            if (s != null) { Abgelehnt.Remove(s); AbgelehntBild.Remove(s); }
            if (e == null) return;
            try
            {
                if (e.Alt != null) e.Alt.mute = e.VorMute;
                if (e.Go != null) UnityEngine.Object.Destroy(e.Go);
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
            if (e.S != null) Tabelle.Remove(e.S);
            else { foreach (var kv in Tabelle) if (ReferenceEquals(kv.Value, e)) { Tabelle.Remove(kv.Key); break; } }
            int i = index >= 0 ? index : Liste.IndexOf(e);
            if (i >= 0 && i < Liste.Count && ReferenceEquals(Liste[i], e)) Liste.RemoveAt(i);
            dFrei++;
        }

        internal static void AllesFrei()
        {
            for (int i = Liste.Count - 1; i >= 0; i--) Frei(Liste[i].S, i);
            Liste.Clear(); Tabelle.Clear(); Abgelehnt.Clear(); AbgelehntBild.Clear();
        }

        internal static void Loggen()
        {
            if (dAngelegt == 0 && dFrei == 0 && Liste.Count == 0 && dAbgelehnt == 0) return;
            Plugin.Log.LogInfo($"[Diagnostics] Carriers for fixed sources with visible parts: {Liste.Count} active, {dAngelegt} created, {dFrei} released, " +
                               $"{dAbgelehnt} refused, {dNachgesetzt} times re-synced to the original's clip position, {dBilder} frames mirrored.");
            dAngelegt = dFrei = dNachgesetzt = dAbgelehnt = dBilder = 0;
        }
    }
}
