using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SEB = SoundEngineBackport.LiveEngine;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // nachhallschuss (17.11.2026): Nachhall-Stimme fuer Gegnerschuesse ueber SEBs Live-Engine. Rechnung: NachhallSchussKern, NachhallKern;
    // Vergabe/Teilen: Zweitstimme (Klasse "gunshots"). Bericht: review-20261110/nachhallstimme-STAND.md, Teil 2.
    // Haken: Postfix GunshotEmitterBase.TryStartShot(context, scheduled, out source, out length) (SEB 1.1.1, belegt im Dekompilat):
    //  - der Schuss ist dann komplett eingerichtet: Clips/Pegel (source1/source2), Tonhoehe, Mischergruppe, PlayScheduled(scheduled) - also
    //    ist der Start exakt bekannt (dsp, um die Schall-Laufzeit vorausgeplant: context.Delay);
    //  - die Okklusion lief schon synchron (Pool.ProcessOcclusion -> SpatialAudioSystem.ProcessSourceOcclusion -> UpdateOcclusionEffects
    //    (updateImmediately: true), SpatialAudioSystem.cs:308-346) -> Faktor und Raumweg der Quelle sind meist schon da; sonst plant
    //    Zweitstimme beim naechsten Anwenden (der Start liegt in der Zukunft, die Vorverzoegerung nimmt die Verspaetung auf).
    //  - __instance (der Emitter einer Waffe) ist der Schluessel der Stimme: alle Schuesse dieses Schuetzen speisen EINE Nachhall-Stimme.
    //  - eigene Schuesse (context.IsFirstPerson) nie: das waere der Hall des eigenen Raums (eigener Auftrag).
    // Faden: Hauptfaden (SEB plant aus FireBullet/Tick). Keine Speicheranforderung je Schuss (Ring in Zweitstimme).
    // ------------------------------------------------------------------------------------------
    internal static class NachhallSchuss
    {
        internal static bool Bereit;
        internal static int Schuesse, Eigene;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Anwenden(Harmony h, MethodBase start)
        {
            if (start == null) return;
            h.Patch(start, postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(NachhallSchuss), nameof(StartNach))) { priority = Priority.Low });
            Bereit = true;
        }

        static void StartNach(object __instance, bool __result, ref SuperSource source, double scheduled, ref SEB.ShotPlaybackContext context)
        {
            if (!__result || source == null || __instance == null || SebAkustik.SebGestoert || !Nachhall.An || !Plugin.Aktiv.Value) return;
            try
            {
                if (Eigen(ref context)) { Eigene++; return; }
                Schuesse++;
                Zweitstimme.SchussEreignis(source, scheduled, __instance, out _);
            }
            catch (Exception ex) { if (SebSchutz.Strukturfehler(ex)) Bereit = false; Statistik.Fehler(ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool Eigen(ref SEB.ShotPlaybackContext c) => c.IsFirstPerson;
    }
}
