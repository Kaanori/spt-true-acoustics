using System;
using UnityEngine;

namespace Materialklang
{
    // explosionfern (17.11.2026) - Spiel-Teil zu KennGruppeKern.cs: Steigung der Kennlinie fuer Explosionen aus der Abstandskurve,
    // mit der das Spiel die Quelle wirklich spielt (Preset.SoundRolloff der Gruppe, x = d / MaxDistance der Quelle).
    // Keine Allokation je Klang: die Schluessel einer Kurve werden einmal je Kurvenobjekt in ein Feld kopiert (AnimationCurve.keys
    // legt eine Kopie an) und nur neu gelesen, wenn das Preset ein anderes Kurvenobjekt traegt (Fernschuss setzt die SPT-Kurve ab dem
    // naechsten Raid ein, SEB ersetzt sie sonst).
    internal static class KennGruppeLauf
    {
        static AnimationCurve kurve;          // zuletzt gelesenes Kurvenobjekt (Referenzvergleich)
        static double[] schluessel = new double[4 * 128];
        static int anzahl;

        // Fuer das Klang-Log: zuletzt angewandte Steigung je Quelle/Bild (wie Physik.FarbeLog)
        static int logQuelle, logBild = -1;
        static float logS = float.NaN;

        /// <summary>Steigung der Kennlinie fuer Quelle q (Gruppe typ) im Abstand d bei realer Daemmung D; die Einstellung, wenn der
        /// Schalter aus ist, die Gruppe die Kurve nicht nutzt oder die Kurve fehlt. merken = fuer das Klang-Log vormerken.</summary>
        internal static double Steigung(BetterSource q, int typ, double d, double D, double sEinstellung, bool merken)
        {
            bool an = Physik.KennKurveAn != null && Physik.KennKurveAn.Value;
            if (!an || !KennGruppe.Gilt(typ) || q == null || q.Preset == null) return sEinstellung;
            var c = q.Preset.SoundRolloff;
            if (c == null) return sEinstellung;
            if (!ReferenceEquals(c, kurve))
            {
                var keys = c.keys;   // eine Kopie je Kurvenobjekt, nicht je Klang
                int n = Math.Min(keys.Length, schluessel.Length / 4);
                for (int i = 0; i < n; i++)
                {
                    schluessel[i * 4] = keys[i].time; schluessel[i * 4 + 1] = keys[i].value;
                    schluessel[i * 4 + 2] = keys[i].inTangent; schluessel[i * 4 + 3] = keys[i].outTangent;
                }
                anzahl = n; kurve = c;
            }
            double maxDist = q.MaxDistance * q.OcclusionRolloffScale;
            double s = KennGruppe.Waehle(true, typ, schluessel, anzahl, maxDist, d, D, sEinstellung);
            if (merken) { logQuelle = q.GetInstanceID(); logBild = Time.frameCount; logS = (float)s; }
            return s;
        }

        /// <summary>Klang-Log: in diesem Bild fuer q angewandte Kurven-Steigung, sonst NaN.</summary>
        internal static float Log(BetterSource q)
            => q != null && logBild == Time.frameCount && logQuelle == q.GetInstanceID() ? logS : float.NaN;
    }
}
