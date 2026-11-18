using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // raid2242richtung (17.11.2026) - zwei Startfehler des Spiels bei Einmal-Klaengen. Reine Rechnung (ohne Unity, Offline-Test).
    //
    // C. Reichweite des Okklusions-Behaelters (Granataufschlag, Klang-Log 22:42 Marke 7):
    //   Grenade.Init -> InitAudioSource meldet die Aufschlag-Quelle (Pool, Gruppe Collisions) als ContinuousPropagated an
    //   (Grenade.cs:121-135). AudioSourceContainer.AddSource liest dabei MaxDistance der Quelle (method_2, AudioSourceContainer.cs:279-292,
    //   344-355) - das ist der Wert ihres VORIGEN Einsatzes im Pool (Huelsen, Gewehr-Fallgeraeusch). Die eigene Reichweite setzt erst
    //   PlayCollisionSound -> SetRolloff (Grenade.cs:164-176); der Behaelter rechnet sie nie nach (method_2 nur bei Add/Remove).
    //   SoundOcclusionProcessor.UpdatePriority: OutOfRange, sobald d^2 > MaxDistance^2 + 25 (SoundOcclusionProcessor.cs, GetPriority mit
    //   SqrMaxDistance + 25) -> der Prozessor rechnet nie (ManualUpdate nur ausserhalb OutOfRange, SpatialAudioSystem.method_25/35), der
    //   Aufschlag spielt mit Wirkung 0 = ungedaempft ueber Raumgrenzen (Log: path=direct, effect 0.00, hp 80 = Spielwert).
    //   Regel: die Reichweite eines Behaelters ist (wie im Spiel) die groesste MaxDistance seiner Quellen - aber mit dem Wert, mit dem
    //   die Quelle JETZT spielt. Nachgerechnet beim Start eines Klangs (SoundOcclusionProcessor.OnSourcePlayed), nur wachsend
    //   (Schrumpfen kann nur zu viel rechnen lassen, nie zu wenig).
    //
    // D. Einmal-Klaenge starten einen Puffer lang ungedaempft (Messung klang-messung, schussfehlt-STAND.md: 30 Rechteck-Spitzen,
    //   +17...+52 dB, 12-25 ms flach):
    //   BetterAudio.PlayAtPoint (BetterAudio.cs:1344-1377): ResetOcclusion() (Faktor 1) -> Play() (SimpleSource.Play -> UpdateSourceVolume
    //   mit speed 1: volume = voll, dann PlayOneShot) -> ERST DANACH ProcessSourceOcclusion -> MultiSourceSpatialAudioOccluder.
    //   SetAudioSourceContainer: Faktor 0, UpdateSourceVolume, Tiefpass 20 Hz (MultiSourceSpatialAudioOccluder.cs:35-50) bis der Job
    //   rechnet. Zwischen PlayOneShot und dem Stummschalten liegen einige zehn Mikrosekunden Hauptfaden (Container, Raum, Haken) -
    //   faellt die Grenze eines Mischblocks hinein, mischt der Audiofaden EINEN Block (1024 Samples = 21,3 ms) mit voller Lautstaerke
    //   [Annahme zum Mechanismus des Mischfadens; belegt sind Reihenfolge und Spitzenform]. Bei stark gedaempften Klaengen (-40 dB)
    //   ist dieser Block 40 dB lauter als der Rest des Klangs.
    //   Regel: Ein Einmal-Klang mit Einmal-Okklusion (OneShotPropagation/OneShotFullOcclusion) beginnt still (Faktor 0 VOR Play) - genau
    //   der Zustand, in den das Spiel ihn ohnehin sofort danach setzt. Nimmt der MultiSource-Occluder ihn nicht (gleicher Raum, Ziel
    //   None), gibt die Mod ihn im selben Aufruf frei (Faktor 1, Lautstaerke sofort). Keine Verzoegerung gegen das Spiel: der
    //   Klang war dort ab dem Stummschalten ebenfalls still, bis seine Rechnung kommt.
    // ------------------------------------------------------------------------------------------
    internal static class ReichweiteKern
    {
        /// <summary>Wie SoundOcclusionProcessor.UpdatePriority / AudioSourcePriorityCalculator.GetPriority: ausser Reichweite, wenn
        /// das Abstandsquadrat zum Hoerer groesser ist als MaxDistance^2 + 25 (5 m Zuschlag des Spiels).</summary>
        public static bool AusserReichweite(double abstandQuadrat, double maxDistance)
            => abstandQuadrat > maxDistance * maxDistance + 25.0;

        /// <summary>Neue Reichweite des Behaelters: groesste MaxDistance der Quellen (quellenMax), nur wachsend. false = keine Aenderung.</summary>
        public static bool Wachsen(double behaelterMax, double quellenMax, out double neu)
        {
            neu = behaelterMax;
            if (double.IsNaN(quellenMax) || !(quellenMax > behaelterMax + 1e-3)) return false;
            neu = quellenMax;
            return true;
        }

        /// <summary>D: Okklusionstests, deren Klaenge das Spiel nach Play stumm schaltet (MultiSource-Occluder).</summary>
        public static bool StillStarten(bool schalter, int occlusionTest, int oneShotPropagation, int oneShotFullOcclusion)
            => schalter && (occlusionTest == oneShotPropagation || occlusionTest == oneShotFullOcclusion);

        // tuerstart (18.11.2026): Tueren/Kisten/Schalter und Fenster spielen PlayAtPoint mit None und melden ERST NACH dem Play mit
        //   OneShotPropagation an (WorldInteractiveObject.PlaySoundAtPoint -> ProcessOcclusion -> InteractiveObjectOccluder.CheckOcclusion,
        //   WorldInteractiveObject.cs:1230-1255; WindowBreaker.PlayHitSound/PlayPieceSound -> LimitedPlay(None) ->
        //   ProcessInteractiveObjectOcclusion(OneShotPropagation), WindowBreaker.cs:924-948). Massgeblich fuer den stillen Start ist dann
        //   der Test der spaeteren Anmeldung, nicht der des PlayAtPoint-Aufrufs; die Freigabe entscheidet sich erst nach der Anmeldung.

        /// <summary>Okklusionstest, der ueber den stillen Start entscheidet: bei spaeterer Anmeldung durch den Aufrufer deren Test,
        /// sonst der des PlayAtPoint-Aufrufs.</summary>
        public static int WirksamerTest(int playTest, bool spaeterAngemeldet, int spaetererTest)
            => spaeterAngemeldet ? spaetererTest : playTest;
    }
}
