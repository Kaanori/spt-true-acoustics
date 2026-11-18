using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using HarmonyLib;
using Meta.XR.Acoustics;
using UnityEngine;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    //  metaschalter (17.11.2026): F12 "22. Meta occlusion (experimental)" + Diagnose, was Meta XR im Raid selbst rechnet.
    //  Befund metaist (review-20261110/metaist-STAND.md): Im Modell Acoustic Ray Tracing holt Metas Unity-Spatializer je Quelle
    //  das Direkt-IR (ovrAudio_SourceGetDirectIR, 4 Baender) und legt es als Shelf-EQ auf den TROCKENEN Schall (Ghidra alle.c:21992,
    //  22059 -> 37226ff). Bei eingeschalteter Beugung (Enable-Flag 6 -> Konfig-Byte +0x1a, alle.c:24613-24618) ersetzt er ausserdem
    //  den Wiedergabeort durch Hoerer + Richtung des Direkt-IR * Abstand (alle.c:22012-22045). Metas Geometrie: alle Flaechen
    //  Transmission 0 (dicht), keine Tueren. Das kommt zur Daempfung von Spiel und Mod hinzu.
    //  Stellschraube ohne eigene Geometrie: Meta-Parameter 17 "Occlusion Intensity" je Quelle (MetaXRAudioSourceExperimentalFeatures
    //  .OcclusionIntensity). Das Spiel setzt ihn NIE (MetaSpatialAudioSource.SetParameters schreibt ihn nicht), er bleibt beim
    //  Prefab-Wert und wird bei jedem ManualUpdate -> UpdateParameters an den Spatializer geschickt (SetSpatializerFloat(17, ...)).
    //  Nativ landet er als Eigenschaft 0xe der Ausbreitungs-Quelle (alle.c:29505). Wie stark er das Direkt-IR (und damit auch die
    //  Richtung) beeinflusst, ist im Ghidra-Code nicht bis zum Verbraucher verfolgt (virtueller Aufruf) -> genau das misst der Schalter.
    //
    //  Eingriff: Prefix MetaSpatialAudioSource.ManualUpdate (laeuft fuer JEDE aktive Spatializer-Komponente: source1, die zweite
    //  Quelle der SuperSource und die Hall-Zwillinge - BetterSource.ManualUpdate ruft alle IManualUpdated der Kinder, BetterSource.cs:616-627).
    //  Schalter an: Feld auf den Sollwert (Meta schreibt es direkt danach selbst nativ). Besitz je Komponente (vorheriger Wert).
    //  Postfix BetterSource.Release: alle Komponenten dieser Quelle zurueck auf ihren vorherigen Wert, sofort nativ (UpdateParameters).
    //  Schalter aus: beim naechsten Update der Komponente zurueck. AUS und nichts im Besitz: ein bool-Vergleich je Komponente und Bild.
    //  Nicht erfasst: Funk/Radio (ClientSpatialBroadcastPlayer setzt eigene Meta-Komponenten ohne MetaSpatialAudioSource).
    //  Faden: alles Hauptfaden. Fehlt die Methode (Spiel-Update): kein Haken, Spielverhalten.
    // ------------------------------------------------------------------------------------------
    internal static class MetaVerdeckung
    {
        internal const string Sek = "22. Meta occlusion (experimental)";
        const int P_OCCLUSION = 17;   // MetaXRAudioSource.NativeParameterIndex.P_OCCLUSION_INTENSITY

        static ConfigEntry<bool> Schalter;
        static ConfigEntry<float> Wert;
        static bool an; static float soll;

        sealed class Eintrag { public bool Hat; public float Vorher = 1f; public int Gruppe = -2; }
        static readonly ConditionalWeakTable<MetaSpatialAudioSource, Eintrag> Tab = new ConditionalWeakTable<MetaSpatialAudioSource, Eintrag>();
        static readonly ConditionalWeakTable<MetaSpatialAudioSource, Eintrag>.CreateValueCallback Neu = _ => new Eintrag();
        static int besitzZahl;
        static int gesetzt, zurueck;

        // Stichprobe fuer die Diagnose: ein Bild lang zaehlt der Prefix alle aktiven Komponenten
        static bool sammeln;
        const int Gruppen = 32;
        static readonly int[] nAktiv = new int[Gruppen + 1], nRaeumlich = new int[Gruppen + 1];
        static readonly Dictionary<int, int>[] werte = new Dictionary<int, int>[Gruppen + 1];
        static int nativAbweichend, nativUnlesbar;

        internal static void Anwenden(Harmony h, ConfigFile c)
        {
            Schalter = c.Bind(Sek, "Override Meta occlusion intensity", false,
                "Off (default) = the mod does not touch Meta XR's own occlusion; the game's value stays (the game never sets it). " +
                "On = every spatialized game sound gets the value below for Meta's 'Occlusion Intensity' (Meta parameter 17): Meta XR's own " +
                "muffling of the direct sound by its baked geometry (walls count as fully sound-proof there, doors are missing). " +
                "Applies at once to playing and new sounds; released sounds go back to the game's value. For A/B measurements.");
            Wert = c.Bind(Sek, "Meta occlusion intensity", 0f,
                new ConfigDescription("Value used while the switch above is on: 0 = Meta does not muffle the direct sound itself, 1 = Meta default (full).",
                    new AcceptableValueRange<float>(MetaVerdeckungKern.Min, MetaVerdeckungKern.Max)));
            an = Schalter.Value; soll = MetaVerdeckungKern.Begrenzen(Wert.Value);
            Schalter.SettingChanged += (_, __) => { an = Schalter.Value; Plugin.Log.LogInfo($"Meta occlusion: switch {(an ? "on" : "off")}, value {soll:F2}."); };
            Wert.SettingChanged += (_, __) => soll = MetaVerdeckungKern.Begrenzen(Wert.Value);
            for (int i = 0; i <= Gruppen; i++) werte[i] = new Dictionary<int, int>();

            var upd = AccessTools.DeclaredMethod(typeof(MetaSpatialAudioSource), "ManualUpdate", Type.EmptyTypes);
            var rel = AccessTools.DeclaredMethod(typeof(BetterSource), "Release", Type.EmptyTypes);
            if (upd == null || rel == null || AccessTools.Field(typeof(BetterSource), "_updatedComps") == null)
            {
                Plugin.Log.LogWarning("Meta occlusion: game methods not found (MetaSpatialAudioSource.ManualUpdate, BetterSource.Release/_updatedComps) -> switch and its diagnostics have no effect, Meta stays as the game sets it.");
                return;
            }
            h.Patch(upd, prefix: new HarmonyMethod(typeof(MetaVerdeckung), nameof(VorUpdate)));
            h.Patch(rel, postfix: new HarmonyMethod(typeof(MetaVerdeckung), nameof(NachRelease)));
        }

        /// <summary>Prefix MetaSpatialAudioSource.ManualUpdate (Hauptfaden). Meta schickt das Feld direkt danach nativ (UpdateParameters).</summary>
        static void VorUpdate(MetaSpatialAudioSource __instance)
        {
            if (!an && besitzZahl == 0 && !sammeln) return;
            try
            {
                var ex = __instance._metaXRAudioSourceExperimental;
                if (ex == null) return;
                var e = Tab.GetValue(__instance, Neu);   // je Komponente einmal (Pool-Objekte leben den ganzen Raid)
                if (an || e.Hat)
                {
                    bool hatte = e.Hat;
                    if (MetaVerdeckungKern.Schritt(an, soll, ref e.Hat, ref e.Vorher, ex.OcclusionIntensity, out float neu))
                    { ex.OcclusionIntensity = neu; if (e.Hat) gesetzt++; else zurueck++; }
                    if (e.Hat && !hatte) besitzZahl++; else if (!e.Hat && hatte) besitzZahl--;
                }
                if (sammeln) Zaehlen(__instance, ex, e);
            }
            catch (Exception x) { Statistik.Fehler(x); }
        }

        /// <summary>Postfix BetterSource.Release: Besitz an allen Spatializern dieser Quelle aufgeben (Pool-Objekt behaelt nichts).</summary>
        static void NachRelease(BetterSource __instance)
        {
            if (besitzZahl == 0) return;
            try
            {
                var comps = __instance._updatedComps;
                if (comps == null) return;
                for (int i = 0; i < comps.Length; i++)
                {
                    if (!(comps[i] is MetaSpatialAudioSource m) || !Tab.TryGetValue(m, out var e) || !e.Hat) continue;
                    var ex = m._metaXRAudioSourceExperimental;
                    if (ex == null) { e.Hat = false; besitzZahl--; continue; }
                    if (MetaVerdeckungKern.Freigeben(ref e.Hat, e.Vorher, ex.OcclusionIntensity, out float neu)) { ex.OcclusionIntensity = neu; ex.UpdateParameters(); }
                    besitzZahl--; zurueck++;
                }
            }
            catch (Exception x) { Statistik.Fehler(x); }
        }

        static void Zaehlen(MetaSpatialAudioSource m, MetaXRAudioSourceExperimentalFeatures ex, Eintrag e)
        {
            if (e.Gruppe == -2)
            {
                var bs = m.GetComponentInParent<BetterSource>();
                e.Gruppe = bs != null && bs.Preset != null ? (int)bs.Preset.Type : -1;
            }
            int g = e.Gruppe >= 0 && e.Gruppe < Gruppen ? e.Gruppe : Gruppen;
            nAktiv[g]++;
            if (m.EnableSpatialization) nRaeumlich[g]++;
            float f = ex.OcclusionIntensity;
            int k = MetaVerdeckungKern.Fach(e.Hat ? e.Vorher : f);   // Spielwert, nicht der Wert der Mod
            werte[g].TryGetValue(k, out int n); werte[g][k] = n + 1;
            var a = m.ParentSource;
            if (a != null && a.GetSpatializerFloat(P_OCCLUSION, out float nat)) { if (Math.Abs(nat - f) > 0.005f) nativAbweichend++; }
            else nativUnlesbar++;
        }

        // ---------------- Diagnose ----------------
        static object system;            // SpatialAudioSystem des laufenden Raids
        static float startZeit = -1f, naechste;
        static int sammelBild = -1;
        static MetaXRAcousticGeometry[] geos; static MetaXRAcousticMap[] karten;

        internal static void SzeneGewechselt() { system = null; startZeit = -1f; geos = null; karten = null; sammeln = false; sammelBild = -1; }

        internal static void Tick()
        {
            var sys = SpatialAudioSystem.Initialized && MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated ? MonoBehaviourSingleton<SpatialAudioSystem>.Instance : null;
            if (!ReferenceEquals(sys, system))
            {
                system = sys; geos = null; karten = null;
                startZeit = sys != null ? Time.realtimeSinceStartup + 5f : -1f;   // Geometrie laedt asynchron (LoadGeometryAsync)
            }
            if (sys == null) return;
            float t = Time.realtimeSinceStartup;
            if (sammelBild >= 0)
            {
                if (Time.frameCount < sammelBild + 2) return;   // ein ganzes Bild aller Quellen abwarten
                sammeln = false; sammelBild = -1;
                bool erste = startZeit > 0f;
                string z = Zeile(erste);
                if (erste) startZeit = -1f;
                Plugin.Log.LogInfo("[Diagnostics] " + z);
                KlangLog.Text(z);
                return;
            }
            bool faellig = startZeit > 0f ? t >= startZeit : (Plugin.Diagnose.Value && t >= naechste);
            if (!faellig) return;
            naechste = t + 30f;
            for (int i = 0; i <= Gruppen; i++) { nAktiv[i] = nRaeumlich[i] = 0; werte[i].Clear(); }
            nativAbweichend = nativUnlesbar = 0;
            sammeln = true; sammelBild = Time.frameCount;
        }

        static string Zeile(bool raidStart)
        {
            var sb = new StringBuilder(raidStart ? "Meta XR at raid start: " : "Meta XR: ");
            try
            {
                var s = MetaXRAcousticSettings.Instance;
                if (s != null) sb.Append("model ").Append(s.AcousticModel).Append(", diffraction ").Append(s.DiffractionEnabled ? "on" : "off").Append("; ");
            }
            catch (Exception e) { sb.Append("settings not readable (").Append(e.GetType().Name).Append("); "); }
            GeometrieText(sb, raidStart);
            int akt = 0, raeum = 0;
            for (int i = 0; i <= Gruppen; i++) { akt += nAktiv[i]; raeum += nRaeumlich[i]; }
            sb.Append(" | active spatializers ").Append(akt).Append(", spatialized ").Append(raeum).Append("; game occlusion intensity per group:");
            for (int i = 0; i <= Gruppen; i++)
            {
                if (nAktiv[i] == 0) continue;
                sb.Append(' ').Append(i < Gruppen ? ((BetterAudio.AudioSourceGroupType)i).ToString() : "other").Append('[');
                bool erst = true;
                foreach (var kv in werte[i]) { if (!erst) sb.Append(','); erst = false; sb.Append(kv.Key < 0 ? "?" : (kv.Key / 100f).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append('x').Append(kv.Value); }
                sb.Append(" spat ").Append(nRaeumlich[i]).Append('/').Append(nAktiv[i]).Append(']');
            }
            sb.Append("; native read-back differs ").Append(nativAbweichend).Append(", unreadable ").Append(nativUnlesbar);
            if (raidStart) PrefabText(sb);
            sb.Append(" | switch ").Append(an ? "on, value " + soll.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "off (game value)")
              .Append(", owned ").Append(besitzZahl).Append(", set ").Append(gesetzt).Append(", restored ").Append(zurueck);
            return sb.ToString();
        }

        static bool Kaputt<T>(T[] a) where T : UnityEngine.Object
        {
            if (a == null) return true;
            for (int i = 0; i < a.Length; i++) if (a[i] == null) return true;
            return false;
        }

        static void GeometrieText(StringBuilder sb, bool neuSuchen)
        {
            try
            {
                if (neuSuchen || Kaputt(geos)) geos = UnityEngine.Object.FindObjectsOfType<MetaXRAcousticGeometry>();
                if (neuSuchen || Kaputt(karten)) karten = UnityEngine.Object.FindObjectsOfType<MetaXRAcousticMap>();
                int geladen = 0, fertigOhne = 0, wartet = 0, ohneHandle = 0; long ecken = 0;
                foreach (var g in geos)
                {
                    if (g == null) continue;
                    if (g.geometryHandle == IntPtr.Zero) ohneHandle++;
                    if (g.isLoaded) geladen++;
                    else if (g.LoadGeometryProcessCompleted) fertigOhne++;
                    else wartet++;
                    if (g.vertexCount > 0) ecken += g.vertexCount;
                }
                sb.Append("geometry ").Append(geos.Length).Append(" (loaded ").Append(geladen).Append(", failed ").Append(fertigOhne)
                  .Append(", pending ").Append(wartet).Append(", no handle ").Append(ohneHandle).Append(", enabled count ").Append(MetaXRAcousticGeometry.EnabledGeometryCount);
                if (ecken > 0) sb.Append(", vertices ").Append(ecken);
                sb.Append("); maps ").Append(karten.Length);
                foreach (var k in karten)
                {
                    if (k == null) continue;
                    sb.Append(" [").Append(k.name).Append(k.IsLoaded ? " loaded" : " not loaded");
                    if (k.mapHandle != IntPtr.Zero)
                    {
                        var ni = MetaXRAcousticNativeInterface.Interface;
                        if (ni.AudioSceneIRGetStatus(k.mapHandle, out AcousticMapStatus st) == 0) sb.Append(", status ").Append(st);
                        if (ni.AudioSceneIRGetPointCount(k.mapHandle, out UIntPtr pts) == 0) sb.Append(", points ").Append(pts.ToUInt64());
                    }
                    else sb.Append(", no handle");
                    sb.Append(", flags ").Append(k.Flags).Append(']');
                }
            }
            catch (Exception e) { sb.Append("geometry/maps not readable (").Append(e.GetType().Name).Append(')'); }
        }

        /// <summary>Einmal je Raid: Werte ALLER Meta-Komponenten samt inaktiver Pool-Objekte und Prefabs (belegt den Prefab-Wert).</summary>
        static void PrefabText(StringBuilder sb)
        {
            try
            {
                var alle = Resources.FindObjectsOfTypeAll<MetaXRAudioSourceExperimentalFeatures>();
                var v = new Dictionary<int, int>(); int asset = 0;
                foreach (var ex in alle)
                {
                    if (ex == null) continue;
                    float f = ex.OcclusionIntensity;
                    var m = ex.GetComponent<MetaSpatialAudioSource>();
                    if (m != null && Tab.TryGetValue(m, out var e) && e.Hat) f = e.Vorher;
                    int k = MetaVerdeckungKern.Fach(f); v.TryGetValue(k, out int n); v[k] = n + 1;
                    if (!ex.gameObject.scene.IsValid()) asset++;
                }
                sb.Append(" | all Meta source components (incl. pool and prefabs) ").Append(alle.Length).Append(", of them prefab assets ").Append(asset).Append(", occlusion intensity:");
                foreach (var kv in v) sb.Append(' ').Append((kv.Key / 100f).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append('x').Append(kv.Value);
            }
            catch (Exception e) { sb.Append(" | prefab values not readable (").Append(e.GetType().Name).Append(')'); }
        }

        /// <summary>Fuer das Klang-Log: Wert an source1 der Quelle (Feld + nativ zurueckgelesen) und ob er von der Mod kommt.</summary>
        internal static string LogFeld(BetterSource q)
        {
            try
            {
                if (q == null || !(q.Spatializer is MetaSpatialAudioSource m) || m._metaXRAudioSourceExperimental == null) return null;
                float f = m._metaXRAudioSourceExperimental.OcclusionIntensity, nat = float.NaN;
                var a = m.ParentSource;
                if (a != null && a.GetSpatializerFloat(P_OCCLUSION, out float n)) nat = n;
                bool mod = besitzZahl > 0 && Tab.TryGetValue(m, out var e) && e.Hat;
                return MetaVerdeckungKern.LogFeld(f, nat, mod);
            }
            catch (Exception x) { Statistik.Fehler(x); return null; }
        }
    }
}
