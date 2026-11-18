using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // vorverzug (17.11.2026): Einsatz der Nachhall-Stimme JE EREIGNIS statt je Stimme. Reine Rechnung (Offline-Test VorverzugTest).
    // Befund (klang-filter, hallglocke-STAND Nebenbefund; belegt in VorverzugTest "alt"): Zweitstimme stellte die Vorverzoegerung des
    // Hallnetzes bei jedem Ereignis auf Einsatz + (Start - jetzt) und im naechsten Bild zurueck auf den Einsatz - waehrend noch Signal in
    // der Verzoegerungsleitung lag. Ein Sprung der Leseposition schneidet bzw. verdoppelt das laufende Signal (Stufe im Ausgang, Teile
    // eines frueheren Ereignisses fehlen oder kommen zweimal). Bei einer Stimme je Gegner/Schuetze ueberlappen die Ereignisse dauernd.
    // Ursache: die Laufzeit gehoert zum Ereignis, die Verzoegerungsleitung aber zur Stimme (ein gemeinsamer Eingang, die Anteile der
    // Ereignisse lassen sich dort nicht mehr trennen).
    // Regel:
    //  - Die Stimme hat EINE feste Vorverzoegerung Dv = Einsatz (Laufzeit des Austrittswegs gegen den fruehesten Weg) des ersten Ereignisses;
    //    sie wechselt nur mit einer neuen Stimme (dann ist das Netz leer, Setzen(leeren: true)) - wie hallglocke die Leitungen der Vorstufe.
    //  - Jedes Ereignis wird zu seinem eigenen Zeitpunkt angeregt: Feuerzeit = Ziel - Dv mit Ziel = Start des Originals + Einsatz des
    //    Ereignisses. Liegt sie in der Zukunft (Schuesse: SEB plant um die Schall-Laufzeit voraus; Testquelle: Vorlauf), wartet die Anregung
    //    in einem festen Puffer der Stimme und wird im Bild gefeuert, dessen Mitte ihr am naechsten liegt (Bild-Raster; Unity startet
    //    PlayOneShot ohnehin erst mit dem naechsten Mischblock, mehrweg-STAND). Liegt sie in der Vergangenheit (Einmal-Klaenge, ein Bild
    //    spaeter geplant), sofort - um die Verspaetung spaeter.
    //  - Abweichung je Ereignis: |Feuer - Feuerzeit| <= halbes Bild (+ ein Mischblock, Unity), Einsatzunterschiede zweier Ereignisse in
    //    derselben Stimme bleiben erhalten, das Netz sieht nie einen Sprung.
    // ------------------------------------------------------------------------------------------
    internal static class VorverzugKern
    {
        /// <summary>Feuerzeit (dsp, s) einer Anregung: Ziel - Dv.</summary>
        public static double Feuerzeit(double startOriginal, double einsatzEreignis, double dv) => startOriginal + Math.Max(0, einsatzEreignis) - Math.Max(0, dv);

        /// <summary>Faellig im Bild jetzt (dsp)? Ja, wenn die Feuerzeit hoechstens ein halbes Bild entfernt ist (naechstes Bild laege weiter weg).</summary>
        public static bool Faellig(double feuerzeit, double jetzt, double bildS) => jetzt >= feuerzeit - 0.5 * Math.Max(0, bildS);

        /// <summary>Feste Vorverzoegerung einer neuen Stimme (s): Einsatz des ersten Ereignisses, begrenzt auf die Leitung des Netzes.</summary>
        public static double Dv(double einsatz, double max) => Math.Max(0, Math.Min(max, einsatz));
    }

    /// <summary>Fester Puffer wartender Anregungen einer Stimme (keine Speicheranforderung nach dem Anlegen; nur Hauptfaden).</summary>
    internal sealed class AnregungsPuffer<T> where T : class
    {
        public readonly T[] C1, C2; public readonly float[] V1, V2, Pitch; public readonly double[] Feuer;
        public int N;
        public AnregungsPuffer(int kap)
        {
            C1 = new T[kap]; C2 = new T[kap]; V1 = new float[kap]; V2 = new float[kap]; Pitch = new float[kap]; Feuer = new double[kap];
        }
        public int Kapazitaet => Feuer.Length;
        /// <summary>false = voll (der Aufrufer feuert dann sofort).</summary>
        public bool Dazu(T c1, T c2, float v1, float v2, float pitch, double feuer)
        {
            if (N >= Feuer.Length) return false;
            C1[N] = c1; C2[N] = c2; V1[N] = v1; V2[N] = v2; Pitch[N] = pitch; Feuer[N] = feuer; N++;
            return true;
        }
        /// <summary>Naechste faellige Anregung (frueheste zuerst) herausnehmen; false = keine.</summary>
        public bool Faellig(double jetzt, double bildS, out T c1, out T c2, out float v1, out float v2, out float pitch, out double feuer)
        {
            int best = -1;
            for (int i = 0; i < N; i++) if (VorverzugKern.Faellig(Feuer[i], jetzt, bildS) && (best < 0 || Feuer[i] < Feuer[best])) best = i;
            if (best < 0) { c1 = c2 = null; v1 = v2 = pitch = 0; feuer = 0; return false; }
            c1 = C1[best]; c2 = C2[best]; v1 = V1[best]; v2 = V2[best]; pitch = Pitch[best]; feuer = Feuer[best];
            int l = N - 1;
            C1[best] = C1[l]; C2[best] = C2[l]; V1[best] = V1[l]; V2[best] = V2[l]; Pitch[best] = Pitch[l]; Feuer[best] = Feuer[l];
            C1[l] = C2[l] = null; N = l;
            return true;
        }
        public void Leeren() { for (int i = 0; i < N; i++) C1[i] = C2[i] = null; N = 0; }
    }
}
