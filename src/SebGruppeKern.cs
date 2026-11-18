namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // occludedmixer (17.11.2026): Welche Mixergruppe bekommt ein SEB-Live-Schuss? Reine Rechnung ohne Unity/SEB.
    //
    // Belegt (Bericht review-20261110/occludedmixer-STAND.md):
    //  - SEB 1.1.1 LiveWeaponSoundEngine.ResolveMixerGroup (Dekompilat Z. 308-321): Guns/Occluded, wenn die Wirkung des
    //    Spieler-Behaelters des Schuetzen (AudioSourceContainer.OcclusionEffect, derselbe Behaelter, in den die Schussquelle per
    //    ProcessSourceOcclusion(IPlayer, ...) kommt) > 0,5 ist, sonst Guns/Gunshots. Aufgerufen je Schuss aus GunshotEmitterBase.TryStartShot.
    //  - Mixer (live_mastermixer.bundle, gleiche Struktur im Spiel-Mixer): Guns/Occluded = Fader 0 dB -> zwei Sends 0 dB VOR dem Filter
    //    in Guns/OccludedTail (Receive + Seitenkette einer Duck Volume, Schwelle -20 dB, Ratio 10) -> Lowpass Simple (FMOD Typ 18).
    //    Dessen Grenzfrequenz ist der freigegebene Parameter "GunsLowPassFilter", den das Spiel bei jedem Raid-Start auf 22 kHz setzt
    //    (SpatialAudioSystem.Initialize -> SetGunshotsLowPassFilterFrequency(true)).
    //  - Folge: Ein verdeckter Schuss klingt in Guns/Occluded als Summe aus dem (offenen) Tiefpass-Zweig und einer ungefilterten
    //    Kopie, die nur ueber -20 dB geduckt wird: leise Schuesse bis +6 dB lauter, laute mit flacherer Spitze. Die Mod rechnet
    //    Pegel und Klangfarbe dieses Schusses im Physik-Modus schon selbst -> die Gruppe zaehlt die Verdeckung ein zweites Mal
    //    (als Pegel-/Dynamik-Sprung an der Schwelle 0,5), nicht als zweiter Hoehenschnitt.
    // Annahme: dass Schuesse mit Wirkung > 0,5 im Physik-Modus von der Mod bearbeitet sind (Ausnahme: Wege.TUnbestimmt - dann
    // Spielwert; zur Zeit der Gruppenwahl nicht bekannt).
    // ------------------------------------------------------------------------------------------
    internal static class SebGruppeKern
    {
        // Was mit dem Schuss geschah (Klang-Log): 0 = unbekannt (kein SEB-Live-Schuss / nicht erfasst)
        internal const byte Unbekannt = 0, Gunshots = 1, OccludedSeb = 2, Umgelegt = 3;

        /// <summary>Soll der Schuss statt in Guns/Occluded in Guns/Gunshots spielen? Nur wenn SEB Occluded gewaehlt hat, der Schalter
        /// an ist, die Mod den Schuss im Physik-Modus rechnet und die SEB-Teile der Mod nicht abgeschaltet sind.</summary>
        internal static bool Umlegen(bool schalter, bool pluginAn, bool physikAn, bool sebGestoert, bool sebWillOccluded, bool gunshotsDa)
            => schalter && pluginAn && physikAn && !sebGestoert && sebWillOccluded && gunshotsDa;

        internal static byte Zustand(bool sebWillOccluded, bool umgelegt)
            => !sebWillOccluded ? Gunshots : (umgelegt ? Umgelegt : OccludedSeb);

        /// <summary>Logfeld (englisch, feste Texte - keine Allokation).</summary>
        internal static string LogFeld(byte z)
        {
            switch (z)
            {
                case Gunshots: return " | mixer Gunshots";
                case OccludedSeb: return " | mixer Occluded (SEB, shooter occluded)";
                case Umgelegt: return " | mixer Gunshots (SEB chose Occluded, kept off: physics)";
                default: return null;
            }
        }
    }
}
