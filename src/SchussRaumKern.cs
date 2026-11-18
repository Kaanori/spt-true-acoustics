namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    //  schusshrtf (12.11.2026): reine Regeln fuer den Schalter "Gunshots: HRTF (experimental)" (SchussRaum.cs), ohne Unity.
    //
    //  Spielregel (im Dekompilat gelesen): eine Quelle ist raeumlich (Meta-Spatializer an, AudioSource.spatialize = true), wenn
    //    !_forceStereo && Preset.DirectBinaural && Preset.SteamSpatialize
    //  (BetterSource.EnableStereo, BetterSource.cs:286-293; PlayScheduled setzt danach SteamSpatialize && DirectBinaural, BetterSource.cs:371;
    //  _forceStereo blockt in CheckBinauralAllowed, BetterSource.cs:666-672). Im Preset-Asset "Gunshots" (resources.assets) steht
    //  SteamSpatialize = false, DirectBinaural = true -> Schuesse sind im Spiel nie raeumlich.
    //  Reverb-Send: raeumlich = Send am eigenen Spatializer mit dem mono-Preset (MetaSpatialAudioSource.SetParameters), sonst die
    //  Hall-Zwillinge mit dem stereo-Preset (ReverbSuperSource.method_12, PlayScheduled nur bei !EnableSpatialization, ReverbSuperSource.cs:83-120).
    // ------------------------------------------------------------------------------------------
    public static class SchussRaumKern
    {
        /// <summary>Was das Spiel selbst entscheidet (BetterSource.cs:290/371 zusammen mit dem _forceStereo-Riegel).</summary>
        public static bool SpielRaeumlich(bool forceStereo, bool directBinaural, bool steamSpatialize)
            => !forceStereo && directBinaural && steamSpatialize;

        /// <summary>Schaltet der Schalter diese Quelle raeumlich? Nur Gruppe Gunshots, nur fremde Schuesse (eigene = EnableStereo(true) ->
        /// _forceStereo, GunshotEmitterBase.cs:117 / WeaponSoundPlayer.cs:311) und nur, wenn das Spiel es nicht ohnehin tut.</summary>
        public static bool Erzwingen(bool schalter, bool istGunshots, bool forceStereo, bool spielRaeumlich)
            => schalter && istGunshots && !forceStereo && !spielRaeumlich;

        /// <summary>Preset des Reverb-Sends auf dem Weg, der gerade traegt: raeumlich -> mono, sonst stereo (Hall-Zwilling).</summary>
        public static double SendBasis(bool raeumlich, double mono, double stereo) => raeumlich ? mono : stereo;

        /// <summary>Logfeld je Klangzeile: 1 = raeumlich, 0 = Stereo-Panning, sonst unbekannt (kein Feld). mod = vom Schalter raeumlich.</summary>
        public static string LogFeld(int raeumlich, bool mod)
        {
            if (raeumlich == 1) return mod ? " | spatial on (gunshot HRTF)" : " | spatial on";
            if (raeumlich == 0) return " | spatial off";
            return null;
        }
    }
}
