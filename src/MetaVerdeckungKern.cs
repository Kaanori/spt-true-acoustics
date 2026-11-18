using System;
using System.Globalization;
using System.Text;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    //  metaschalter (17.11.2026): reine Regeln fuer F12 "22. Meta occlusion (experimental)" (ohne Unity).
    //  Meta-Parameter 17 = MetaXRAudioSourceExperimentalFeatures.OcclusionIntensity, Wertebereich 0..1
    //  (verwaltet: Mathf.Clamp(value, 0, 1); nativ: ovrAudio_SetSourceOcclusionIntensity lehnt < 0, > 1 und NaN ab und schreibt
    //  ovrAudioSourceProperty 0xe der Ausbreitungs-Quelle, dort nochmals auf [0, 1] begrenzt - Ghidra alle.c:29487-29512, 91368-91377).
    //  Besitz-Regel wie beim Send (DiffusKern.SendBesitz): Die Mod merkt sich beim ersten Ueberschreiben den Wert, den die Komponente
    //  vorher hatte (= Prefab-Wert, das Spiel schreibt den Parameter nie), und gibt ihn beim Release bzw. beim Ausschalten zurueck.
    //  Ein Pool-Objekt traegt danach wieder genau den Prefab-Wert; die naechste Verwendung uebernimmt nichts von der Mod.
    // ------------------------------------------------------------------------------------------
    public static class MetaVerdeckungKern
    {
        public const float Min = 0f, Max = 1f;

        /// <summary>Wertebereich der Meta-API. NaN/Unendlich (kein gueltiger Wert) -> 1 = Meta-Vorgabe (Feld-Vorgabe 1f, MetaXRAudioSourceExperimentalFeatures).</summary>
        public static float Begrenzen(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return Max;
            return v < Min ? Min : (v > Max ? Max : v);
        }

        /// <summary>Ein Schritt je Update einer Spatializer-Komponente.
        /// an/soll: Schalter und Sollwert; hat/vorher: Besitz der Mod an dieser Komponente; aktuell: Wert des Feldes jetzt.
        /// Rueckgabe true = Feld auf <paramref name="neu"/> schreiben.</summary>
        public static bool Schritt(bool an, float soll, ref bool hat, ref float vorher, float aktuell, out float neu)
        {
            neu = aktuell;
            if (an)
            {
                float s = Begrenzen(soll);
                if (!hat) { vorher = aktuell; hat = true; }
                if (aktuell == s) return false;
                neu = s; return true;
            }
            if (!hat) return false;           // Schalter aus und nichts im Besitz: Spielwert bleibt unberuehrt
            hat = false;
            if (aktuell == vorher) return false;
            neu = vorher; return true;
        }

        /// <summary>Release der Quelle: Besitz aufgeben, vorherigen Wert zurueck. true = schreiben.</summary>
        public static bool Freigeben(ref bool hat, float vorher, float aktuell, out float neu)
        {
            neu = aktuell;
            if (!hat) return false;
            hat = false;
            if (aktuell == vorher) return false;
            neu = vorher; return true;
        }

        /// <summary>Logfeld je Klangzeile. feld = Wert der Komponente, nativ = Rueckgelesener Wert am Spatializer (NaN = nicht lesbar),
        /// mod = von der Mod gesetzt. null = keine Meta-Quelle.</summary>
        public static string LogFeld(float feld, float nativ, bool mod)
        {
            if (float.IsNaN(feld)) return null;
            var sb = new StringBuilder(" | meta occl ");
            sb.Append(feld.ToString("F2", CultureInfo.InvariantCulture));
            if (!float.IsNaN(nativ) && Math.Abs(nativ - feld) > 0.005f) sb.Append(" (native ").Append(nativ.ToString("F2", CultureInfo.InvariantCulture)).Append(')');
            if (mod) sb.Append(" (switch)");
            return sb.ToString();
        }

        /// <summary>Wert auf 0,01 gerundet als Schluessel fuer die Verteilung (gleiche Werte zusammenfassen).</summary>
        public static int Fach(float v) => float.IsNaN(v) ? -1 : (int)Math.Round(Begrenzen(v) * 100f);
    }
}
