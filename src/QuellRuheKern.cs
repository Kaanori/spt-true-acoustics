using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // diffusflattern (17.11.2026) - ruhende Quelle behaelt ihre Lautstaerke. Reine Rechnung (ohne Unity, Offline-Test).
    //
    // Befund (Klang-Log 2026-11-17_19-54-50 Factory, Marken 19:58:02/04/07; review-20261110/diffusflattern/spruenge.txt):
    //   Dieselbe Pool-Quelle springt zwischen zwei Zeilen um 25-37 dB, z. B. src#-1196490 @Bot16: walk_concrete3 -47,5 dB (f 17641)
    //   -> "Character clip=-" -11,1 dB (f 17644) -> walk_concrete4 -47,7 dB (f 17646). Beide Zustaende kommen aus DERSELBEN Rechnung
    //   (Physik.Trocken -> DiffusHoerbarKern, Logfeld "diffuse dry" in beiden). Verschieden ist nur die Eingabe: "clip=-" heisst
    //   _lastPlayingClipName leer, und den leert das Spiel genau dann, wenn die Quelle NICHT spielt (BetterSource.cs:637-640, Release :343).
    //   Ohne Klang ist die Signaldauer unbekannt (ZeitKern.SignalDauer -> "steady", kein Ereignis-Abzug), der Clip-Pegel 0
    //   (Kalibrierung.ClipKorrektur("")), und die Hoerbarkeitsrechnung (Maskierung bis 2 dB je dB) verstaerkt den Unterschied auf 25-37 dB.
    //   Der Wert der ruhenden Quelle ist kein Klang - er wirkt aber auf den NAECHSTEN: das Spiel fuehrt source1.volume nur gleitend nach
    //   (BetterSource.UpdateSourceVolume: Lerp(volume, PreOcclusion x OcclusionVolumeFactor, 10 dt), BetterSource.cs:508-512, nur solange
    //   die Quelle spielt :633-636), und BetterSource.Play setzt die Lautstaerke nicht (:435-444). Startet der naechste Schritt, laeuft
    //   die Lautstaerke im ersten Bild auf das Ziel der RUHENDEN Quelle zu (bei 40 Bildern/s um 25 % der Amplitude: -47,7 -> -22,7 dB)
    //   und erst danach auf das eigene Ziel zurueck (Zeitkonstante 0,1 s, laenger als ein Schritt-Clip von 0,14 s).
    //
    // Regel: Eine Quelle, die nicht spielt, bekommt kein neues Ziel; es bleibt das zuletzt beim Spielen angewandte desselben Behaelters
    //   (derselbe Bot/Gegenstand). So beginnt der naechste Klang dort, wo der letzte aufgehoert hat, und gleitet (Lerp des Spiels) auf
    //   sein eigenes Ziel. Kein Timer, keine Schwelle: Kriterium ist der Spielzustand des Spiels.
    //   Wechselt die Quelle den Behaelter (Pool: 67 von 83 Bot-Quellen im Log hatten mehrere Besitzer), gilt das gemerkte Ziel nicht
    //   (anderer Ort, anderer Weg) -> normal rechnen wie bisher.
    // ------------------------------------------------------------------------------------------
    internal static class QuellRuheKern
    {
        /// <summary>Spielt die Quelle? Zustand des Spiels (BetterSource.PlayBackState == Playing) ODER AudioSource.isPlaying (Schleifen und
        /// Lampen, die AudioSource direkt starten, sowie geplante Klaenge).</summary>
        public static bool Spielt(bool zustandSpielt, bool audioSpielt) => zustandSpielt || audioSpielt;

        /// <summary>Gemerktes Ziel je Quelle (Pool-Objekt): Faktor (Amplitude 0..1), dB, Behaelter beim letzten Anwenden im Spielen.</summary>
        public sealed class Merk
        {
            public float Faktor = float.NaN, Db = float.NaN;
            public int Behaelter;
            public int GehaltenBild = -1;
            public void Gespielt(float faktor, float db, int behaelter) { Faktor = faktor; Db = db; Behaelter = behaelter; }
        }

        /// <summary>Halten statt rechnen? Nur wenn die Quelle nicht spielt UND ein Ziel aus dem Spielen im selben Behaelter vorliegt.</summary>
        public static bool Halten(bool spielt, Merk m, int behaelter)
            => !spielt && m != null && !float.IsNaN(m.Faktor) && m.Behaelter == behaelter;

        /// <summary>Lautstaerke-Nachfuehrung des Spiels je Bild (BetterSource.UpdateSourceVolume, speed = 10 dt): Lerp, auf 0..1 geklemmt.</summary>
        public static double Nachfuehren(double volume, double ziel, double dt)
        {
            double t = Math.Max(0, Math.Min(1, 10.0 * dt));
            double v = volume + (ziel - volume) * t;
            return v < 0 ? 0 : (v > 1 ? 1 : v);
        }

        /// <summary>Klang-Log: Feld, wenn in diesem Bild gehalten wurde (sonst null).</summary>
        public static string LogFeld(Merk m, int bild)
            => m != null && m.GehaltenBild == bild ? " | level held (source not playing, last played " + Db10(m.Db) + " dB)" : null;

        static string Db10(float db) => (-db).ToString("+0.0;-0.0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
