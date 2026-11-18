using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    //  v1waffen (12.11.2026): gemeinsamer Kern fuer die Schussteile der Mod - EINE Rechnung fuer beide Abspielwege von SEB:
    //   - Live-Weg (SEB LiveWeaponSoundEngine -> GunshotEmitterBase.TryStartShot, SebAkustik.SebTeil) und
    //   - V1-Weg (SEB 1.1.1 WeaponSoundPlayerPatches.FireBulletV1 -> Spiel-Warteschlange SuperBetterAudioQueue, SchussV1.cs).
    //  Reine Rechnung ohne Unity (im Offline-Test). Formeln unveraendert aus SebTeil.SchussRechnen/FeuerVor (Stand paket-1112i)
    //  herausgezogen; die Kurve (w^1.3, Rueckseite -4 dB / 6 kHz, Innenfaktor) ist nach Gehoer eingestellt (Messgrundlage der
    //  Richtwirkung: Beck, Nakasone & Marr, JASA 129, 1748-1759, 2011), nicht daraus abgeleitet.
    // ------------------------------------------------------------------------------------------
    internal static class SchussRichtKern
    {
        public const float RichtExponent = 1.3f;
        public const float OffenLp = 22000f;   // "kein Tiefpass" (SEB FilterBlend.DefaultLowPass)

        /// <summary>Richtwirkung w: 0 = Muendung zeigt zum Hoerer, 1 = genau weg. s = Schussrichtung, o = Muendung -> Ohr
        /// (beide beliebig lang). Entartet (Laenge ~0) -> 0 (keine Wirkung).</summary>
        public static float W(float sx, float sy, float sz, float ox, float oy, float oz)
        {
            double ls = sx * (double)sx + sy * (double)sy + sz * (double)sz;
            double lo = ox * (double)ox + oy * (double)oy + oz * (double)oz;
            if (ls <= 1e-6 || lo <= 1e-6) return 0f;
            double dot = (sx * (double)ox + sy * (double)oy + sz * (double)oz) / Math.Sqrt(ls * lo);
            return Klemme01((float)((1.0 - dot) * 0.5));
        }

        /// <summary>Gewicht der Richtwirkung: drinnen werfen die Waende den Schall zurueck (Regler "Indoor factor").</summary>
        public static float Gewicht(bool innen, float innenFaktor) => innen ? innenFaktor : 1f;

        /// <summary>Pegel der Richtwirkung in dB (<= 0): rueckDb * w^1.3 * Gewicht.</summary>
        public static float Db(float w, float rueckDb, float gewicht) => rueckDb * (float)Math.Pow(Klemme01(w), RichtExponent) * gewicht;

        /// <summary>Tiefpass der Richtwirkung (Hz): 22 kHz * (rueckLpf / 22 kHz)^(w^2 * Gewicht).</summary>
        public static float Lp(float w, float rueckLpf, float gewicht)
        {
            w = Klemme01(w);
            return OffenLp * (float)Math.Pow(rueckLpf / OffenLp, w * w * gewicht);
        }

        /// <summary>Amplitudenfaktor aus dB.</summary>
        public static float Gain(float db) => (float)Math.Pow(10.0, db / 20.0);

        /// <summary>Luftdaempfung bei Schuessen: die Fernaufnahmen der Bank enthalten die Luft bis zu ihrer letzten Entfernungsgrenze schon
        /// (SoundBank.BlendOptions.Values). Abzug = letzte Grenze * "Shot offset"; ohne Grenzen halbe Entfernung als Naeherung (Faktor 0,5).</summary>
        public static void LuftVersatz(float[] grenzen, float schussAbzug, out float abzug, out float faktor)
        {
            if (grenzen != null && grenzen.Length > 0)
            {
                float letzte = 0f;
                for (int i = 0; i < grenzen.Length; i++) letzte = Math.Max(letzte, grenzen[i]);
                abzug = letzte * schussAbzug;
                faktor = 1f;
            }
            else { abzug = 0f; faktor = 0.5f; }
        }

        /// <summary>Entfernung, ueber die die Luft noch zusaetzlich daempft (m).</summary>
        public static float LuftMeter(float d, float faktor, float abzug) => Math.Max(0f, d * faktor - abzug);

        /// <summary>V1: Faktor auf die Verdeckungs-Lautstaerke der Quelle beim Start eines Samples, damit sie genau gSoll (Richtwirkung)
        /// traegt. Steht an der Quelle noch der Wert, den das letzte Anwenden mit gIst gesetzt hat, wird nur getauscht (gSoll/gIst);
        /// hat etwas anderes ihn gesetzt (Release -> 1, Spiel ohne Mod-Zutat), traegt er noch keine Richtwirkung (-> gSoll).</summary>
        public static float StartFaktor(float ovfJetzt, float ovfGemerkt, bool gemerkt, float gIst, float gSoll)
        {
            if (gemerkt && Math.Abs(ovfJetzt - ovfGemerkt) <= 1e-5f && gIst > 1e-6f) return gSoll / gIst;
            return gSoll;
        }

        /// <summary>Abschnitt 13 auf dem V1-Weg: gilt wie beim Live-Weg nur draussen, ungedaempft, fremde Schuesse.</summary>
        public static bool FernGilt(bool innen, bool gedaempft, bool eigeneSicht) => !innen && !gedaempft && !eigeneSicht;

        /// <summary>Abschnitt 13 auf dem V1-Weg: neue MaxDistance. Das Spiel/SEB-V1 setzt die Reichweite der Bank (SoundBank.Rolloff,
        /// WeaponSoundPlayer.cs:304 / SEB FireBulletV1); der Regler darf sie verlaengern, nie kuerzen.</summary>
        public static float FernMax(float bankRolloff, float maxDraussen) => Math.Max(bankRolloff, maxDraussen);

        [Flags]
        public enum V1Teil { Keiner = 0, Erkennung = 1, Start = 2, ClipWahl = 4, Ende = 8 }

        /// <summary>Welche Teile der V1-Abdeckung laufen, je nach gefundenen Spielzielen. Pflicht (Erkennung): FireBullet, Enqueue,
        /// Release und der Kontext des Verdeckungsfilters - sonst alles aus. Start (Pegel genau ab dem ersten Sample, Abschnitt 13,
        /// Clipname): SuperAudioSample.PlayOn. ClipWahl (Bank je Sample, "keep mid clip"): SoundBank.PickClipsByDistance.
        /// Ende (Schweif von StopFiringLoop im Fenster): StopFiringLoop.</summary>
        public static V1Teil Teile(bool feuer, bool enqueue, bool release, bool kontext, bool playOn, bool pick, bool stop)
        {
            if (!feuer || !enqueue || !release || !kontext) return V1Teil.Keiner;
            var t = V1Teil.Erkennung;
            if (playOn) t |= V1Teil.Start;
            if (pick) t |= V1Teil.ClipWahl;
            if (stop) t |= V1Teil.Ende;
            return t;
        }

        /// <summary>Logfeld je Klangzeile (nach dem SEB-Block): V1-Schuss, Schleife (ein Clip ueber viele Schuesse) oder Einzelclip.</summary>
        public static string LogFeld(bool v1, bool schleife) => !v1 ? null : schleife ? " | SEB V1 engine (loop)" : " | SEB V1 engine";

        static float Klemme01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
    }
}
