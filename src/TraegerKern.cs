using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // lampenwand (17.11.2026) - Klangtraeger fuer feste Quellen mit sichtbaren Teilen (Leuchtstofflampen). Reine Regeln ohne Unity.
    // Spiel-Teil: Traeger.cs. Befund und Zahlen: review-20261110/lampenwand-STAND.md.
    //
    // Ursache (Klang-Log 2026-11-17_20-06-44, 2477 von 2477 hoerbaren Raumweg-Zeilen der Lampen-Schleifen "direction: source (sound object
    // carries visible parts, not moved)"): LoopLampSoundController legt die BetterSource (SimpleSource) mit
    // BetterAudio.CreateBetterSourceWithParentSettings auf das GameObject der AudioSource, und das ist das Lampen-FX mit LODGroup
    // (LogOutput: "LODGroup on 'Fluorescent_lamp_02_on_FX (1)' would move with it"). SimpleSource.Init: source1 = GetComponent<AudioSource>()
    // (SimpleSource.cs). Eine Unity-Komponente laesst sich nicht an ein anderes GameObject haengen, und Unity spielt eine AudioSource
    // an der Position IHRES Transforms. Die Richtung (Richtung.cs) durfte das Objekt daher nicht verschieben.
    //
    // Loesung: ein unsichtbares Kind-Objekt der Lampe mit einer eigenen AudioSource spielt dieselbe Schleife (gleicher Clip, gleiche
    // Abspielstelle timeSamples) und folgt Bild fuer Bild allen Einstellungen der Original-Quelle (Lautstaerke - also auch der Daempfung
    // der Mod -, Tonhoehe, Abstandskurve, Mischpult-Gruppe, Tief-/Hochpass). Das Original laeuft stumm (mute) weiter: alle Spiel- und
    // Mod-Logik (Okklusion, Lautstaerke, Filter, AudioSourceCulling, Lampe an/aus) arbeitet unveraendert an ihm, nur gehoert wird der Traeger.
    // Als Kind der Lampe wird der Traeger mit ihr inaktiv und mit ihr zerstoert. Die Richtung verschiebt nur den Traeger (gleicher Abstand).
    // Nur fuer Schleifen: einen One-Shot (PlayOneShot) kann man nicht spiegeln, timeSamples kennt ihn nicht (mehrweg-STAND.md).
    // Nur ohne Spatializer (Meta-Parameter haengen an einer Komponente des Originals; Lampen: Logfeld "spatial off") und ohne andere
    // Unity-Audiofilter als Tief-/Hochpass (die werden mitgefuehrt).
    // ------------------------------------------------------------------------------------------
    internal static class TraegerKern
    {
        internal enum Grund { Ja = 0, KeineSchleife, KeinClip, Raeumlich, FremderFilter, Aus }

        /// <summary>Darf ein Traeger die Quelle spielen? loop/clip = AudioSource.loop/clip, raeumlich = AudioSource.spatialize,
        /// fremderFilter = andere Unity-Audiofilter als Tief-/Hochpass am Original.</summary>
        public static Grund Zulassen(bool schalter, bool loop, bool hatClip, bool raeumlich, bool fremderFilter)
        {
            if (!schalter) return Grund.Aus;
            if (!hatClip) return Grund.KeinClip;
            if (!loop) return Grund.KeineSchleife;
            if (raeumlich) return Grund.Raeumlich;
            if (fremderFilter) return Grund.FremderFilter;
            return Grund.Ja;
        }

        public static string Text(Grund g)
        {
            switch (g)
            {
                case Grund.Ja: return "carrier";
                case Grund.KeineSchleife: return "not a loop (one-shots cannot be mirrored)";
                case Grund.KeinClip: return "no clip";
                case Grund.Raeumlich: return "spatializer on (its settings belong to the original)";
                case Grund.FremderFilter: return "other audio filter on the original";
                default: return "carrier switch off";
            }
        }

        /// <summary>Versatz (Samples) der Abspielstelle 'neu' gegen 'alt' in einer Schleife der Laenge 'laenge': kuerzester Weg auf dem
        /// Kreis, Bereich (-laenge/2, laenge/2]. Ohne Laenge: einfache Differenz.</summary>
        public static int Versatz(int alt, int neu, int laenge)
        {
            long d = (long)neu - alt;
            if (laenge <= 0) return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, d));
            long l = laenge;
            d %= l; if (d < 0) d += l;            // 0 .. l-1
            if (d > l / 2) d -= l;                // (-l/2, l/2]
            return (int)d;
        }

        /// <summary>Muss die Abspielstelle des Traegers neu gesetzt werden? Ja, wenn sie mehr als 'grenze' Samples (Betrag) daneben liegt.
        /// Grenze = ein DSP-Puffer (Spiel-Log: 1024 Samples): so genau startet ein Play() ohnehin (naechster Audio-Block, mehrweg-STAND.md);
        /// darunter ist ein Nachsetzen selbst der groessere Sprung.</summary>
        public static bool Nachsetzen(int versatz, int grenze) => Math.Abs((long)versatz) > Math.Max(0, grenze);
    }
}
