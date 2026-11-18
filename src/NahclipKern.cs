using System;
using System.Collections.Generic;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    //  nahclip (17.11.2026): "13. Distance (SEB) -> Near recording at every distance (physics does the rest)". Reine Rechnung (Offline-Test).
    //  Wunsch des Besitzers: Gegnerschuesse spielen auf jede Entfernung die Nah-Aufnahme; leiser und dumpfer macht sie nur die Physik.
    //
    //  Belegt (Dekompilat):
    //   - Clipwahl SoundBank.PickClipsByDistance (Spiel, EFT/SoundBank.cs:117-157): Stufe i = Zahl der Grenzen <= d in BlendOptions.Values;
    //     0 = close, 1 = close*p + distant*(1-p) mit p = InverseLerp(B1, B0, d), 2 = distant, 3 = distant*p + far*(1-p), 4 = far.
    //     SEBs Grenzen (blendoptions-Buendel): "Standart" 5/40/60/100 m, "Silenced" 5/40/180/300 m. Die Nah-Aufnahme laeuft also nur bis
    //     B0 = 5 m unvermischt - dort ist ihr Pegel der, den SEB fuer sie vorsieht.
    //   - Abstandskurve: Unity CustomRolloff der Quelle = SEBs Gunshots-Kurve (LiveGroupPresetTable.cs:497-519), x = d / MaxDistance,
    //     MaxDistance 600/200 m (draussen/drinnen, gedaempft 300/120; LiveWeaponAudioSettings.Get). Sie ist kein 1/r: bezogen auf 1 m bei
    //     600 m Reichweite 50 m -6,7 / 100 m -9,5 / 200 m -13,7 / 400 m -22,4 dB; 1/r ergibt -34,0 / -40,0 / -46,0 / -52,0 dB.
    //   - Ein Teil der Entfernungsdaempfung steckt heute in den Aufnahmen: Fern-Aufnahmen sind leiser als die Nah-Aufnahmen
    //     (Energie, akm draussen distant -3,1 / far -8,5 dB, m4a1 -1,9 / -12,3 dB; review-20261110/nahclip/clipwerte.txt).
    //  Daraus folgt das Abstandsgesetz dieses Schalters: bis B0 SEBs Kurve unveraendert (dort spielt SEB dieselbe Aufnahme), dahinter
    //  1/r ab B0 (Punktquelle, Freifeld) mal Luftdaempfung im Bezugsband 1 kHz (ISO 9613-1) - der Rest der Luft ist der Tiefpass (LuftFit).
    //  An der Reichweitengrenze des Spiels (MaxDistance) bleibt das weiche Ausblenden von Abschnitt 13 (FernschussKern.Ausblenden,
    //  letzte 20 %) - das ist die Hoergrenze des Spiels, keine Physik (dahinter haelt Unity den letzten Kurvenwert, also 0).
    //
    //  Breite (ANNAHME Quellbreite w): Ein Stereo-Schuss wird mit Unitys Panner gespielt (Gunshots: SteamSpatialize = 0, SEB
    //  UseSimpleSpatializer; BetterSource.EnableStereo(false) -> spatialBlend 1); dessen Spread-Kurve (Grad/360 ueber x) verteilt die
    //  Kanaele um die Quellrichtung. Physikalisch erscheint eine Quelle der Breite w aus d unter 2 atan(w / 2d) - SEBs Kurve haelt dagegen
    //  18 Grad von 60 bis 300 m und wird zur Reichweitengrenze wieder breiter (bis 125 Grad).
    // ------------------------------------------------------------------------------------------
    internal static class NahclipKern
    {
        public const double FRef = 1000.0, FOben = 16000.0, Deckel = 40.0;   // Fit-Stuetzen 1-16 kHz, Kurven bei 40 dB gedeckelt (darunter ist das Band weg)
        public const double LpMin = 1000.0, LpMax = 22000.0;

        /// <summary>Gilt der Schalter fuer diese Clipwahl? Nur mit Blendgrenzen (ohne Grenzen spielt die Bank ohnehin immer Stufe 0)
        /// und nur, wenn die Nah-Aufnahme dieser Umgebung nicht leer ist (sonst bleibt die Wahl des Spiels).</summary>
        public static bool Gilt(bool schalter, bool eigeneSicht, float[] grenzen, bool nahLeer)
            => schalter && !eigeneSicht && !nahLeer && grenzen != null && grenzen.Length >= 2;

        /// <summary>Ab hier gilt 1/r: die erste Blendgrenze der Bank (bis dort spielt SEB die Nah-Aufnahme unvermischt).</summary>
        public static float Anker(float[] grenzen)
        {
            if (grenzen == null || grenzen.Length == 0) return float.NaN;
            float a = grenzen[0];
            return a > 0.5f ? a : 0.5f;   // Grenze 0 (nie gesehen) -> 0,5 m, damit 1/r einen Bezug hat
        }

        // ------------------------------------------------------------------------------------------
        //  nahkennlinie (18.11.2026): "13. Distance (SEB) -> Near recording: distance through the loudness curve".
        //  Befund (Raid 18.11. 09:16 Factory, Kaan: "Schuesse sind jetzt viel zu leise"): mit der Nah-Aufnahme faellt der Pegel ab 5 m mit
        //  1/r im ECHTEN dB-Mass (Klang-Log: distance boost -11,7 dB bei 50-75 m gegen SEB), die Wand dahinter aber schon im GESPIELTEN
        //  Mass: Physik.SollDb -> Hoerbarkeit.Gestaucht = Lautheit x [K(L_frei) - K(L_frei - D)] mit L_frei = L_Quelle - 20 lg d am
        //  ECHTEN Abstand (RaumwegKern.cs, Kennlinie/Kompression/Gestaucht). Diese Rechnung setzt voraus, dass die Wiedergabe die Entfernung
        //  schon als K(L_frei) spielt (RaumwegKern.cs, Herleitung "Das Spiel (SEB) spielt den Klang ohne Hindernis mit SEINER Abstandskurve,
        //  also schon als K(L_frei)"). Mit 1/r stimmt das nicht mehr: Entfernung ungestaucht, Wand gestaucht.
        //  Loesung (dieselbe Abbildung realer Pegel auf den Spielpegel, kein Zuschlag): ab dem Anker a (erste Blendgrenze, bis dort spielt
        //  SEB die Nah-Aufnahme selbst) faellt der Pegel um
        //     A(d) = Lautheit x [K(L_Quelle - 20 lg a) - K(L_Quelle - 20 lg d)]        (= Hoerbarkeit.Kompression(L_frei(a), 20 lg(d/a)))
        //  Zusammen mit dem Wandabzug der Physik (unveraendert, am echten Abstand d):
        //     A(d) + Lautheit x [K(L_frei(d)) - K(L_frei(d) - D)] = Lautheit x [K(L_frei(a)) - K(L_frei(d) - D)]
        //  - EINE Kennlinie ueber den realen Gesamtpegel am Ohr (Entfernung + Wand), nichts doppelt gestaucht. L_Quelle = Hoerbarkeit.KennPegel
        //  (Schuss 140, mit Schalldaempfer 120 dB(A) in 1 m - die Schaetzwerte, mit denen die Wandrechnung schon arbeitet).
        //  Luft: bleibt physikalisch und ungestaucht (Faktor 10^(-alpha(d-a)/20) bei 1 kHz, Rest im Tiefpass) - wie der Wandabzug rechnet
        //  die Kennlinie mit L_frei ohne Luft (Hoerbarkeit.FreiPegel). ANNAHME: die Luft ist bis 400 m bei 1 kHz hoechstens ~2 dB; sie mit
        //  in die Kennlinie zu nehmen, aenderte das Ergebnis um hoechstens 1,6 dB (2 dB x (1 - 0,2)).
        //  l1 = NaN: Schalter aus -> 1/r wie 1118a.
        // ------------------------------------------------------------------------------------------

        /// <summary>Pegelabfall (dB, &gt;= 0) der Entfernung von 'anker' nach d ohne Luft: 1/r (l1 NaN) oder durch die Kennlinie
        /// (Lautheit x [K(l1 - 20 lg anker) - K(l1 - 20 lg d)]). d &lt;= anker: 0.</summary>
        public static double AbstandDb(double anker, double d, double l1 = double.NaN, double lautheit = 1, double knie = double.NaN, double steigung = 1)
        {
            if (!(d > anker) || !(anker > 0)) return 0;
            double geo = 20.0 * Math.Log10(d / anker);
            if (double.IsNaN(l1)) return geo;
            return lautheit * Hoerbarkeit.Kompression(Hoerbarkeit.FreiPegel(l1, anker), geo, knie, steigung);
        }

        /// <summary>Kurvenhoehe (Unity-Verstaerkung) bei d: bis Anker SEB (g(d) von der Gruppenkurve bei basisMax), dahinter
        /// g(Anker) * 10^(-AbstandDb/20) * 10^(-alphaRef*(d-Anker)/20) * Ausblenden(d, maxD). l1 NaN: AbstandDb = 1/r (Anker/d).</summary>
        public static double Gain(KurvenPunkt[] seb, double basisMax, double maxD, double anker, double alphaRef, double d,
                                  double l1 = double.NaN, double lautheit = 1, double knie = double.NaN, double steigung = 1)
        {
            if (d >= maxD) return 0;
            if (d <= anker) return FernschussKern.Auswerten(seb, d / basisMax);
            double ga = FernschussKern.Auswerten(seb, anker / basisMax);
            double ab = double.IsNaN(l1) ? anker / d : Math.Pow(10.0, -AbstandDb(anker, d, l1, lautheit, knie, steigung) / 20.0);
            return ga * ab * Math.Pow(10.0, -alphaRef * (d - anker) / 20.0) * FernschussKern.Ausblenden(d, maxD);
        }

        /// <summary>Die Unity-Kurve (x = d / maxD). seb = Gruppenkurve der Quelle (bezogen auf basisMax). Bis zum Anker dieselben
        /// Abschnitte (Zeitachse umgerechnet, Teilungspunkt mit Wert/Ableitung aus SEB), dahinter Achtel-Oktaven mit einseitigen Tangenten.
        /// l1..steigung: Kennlinie fuer die Entfernung (nahkennlinie), l1 NaN = 1/r.</summary>
        public static KurvenPunkt[] Kurve(KurvenPunkt[] seb, float basisMax, float maxD, float anker, double alphaRef,
                                          double l1 = double.NaN, double lautheit = 1, double knie = double.NaN, double steigung = 1)
        {
            if (seb == null || seb.Length < 2 || !(basisMax > 0f) || !(maxD > anker * 2f) || !(anker > 0f) || anker >= basisMax) return null;
            double s = maxD / (double)basisMax, xa = anker / (double)basisMax;
            Func<double, double> g = d => Gain(seb, basisMax, maxD, anker, alphaRef, d, l1, lautheit, knie, steigung);
            var l = new List<KurvenPunkt>(96);
            foreach (var p in seb)
            {
                if (p.T >= xa - 1e-7) break;
                l.Add(new KurvenPunkt((float)(p.T / s), p.V, (float)(p.In * s), (float)(p.Out * s)));
            }
            double ga = FernschussKern.Auswerten(seb, xa);
            l.Add(new KurvenPunkt((float)(anker / maxD), (float)ga, (float)(FernschussKern.Ableitung(seb, xa) * s), (float)(Abl(g, anker, +1) * maxD)));
            var dl = new List<double>(96);
            for (double d = anker * Math.Pow(2, 0.125); d < maxD; d *= Math.Pow(2, 0.125)) dl.Add(d);
            double start = maxD * (1 - FernschussKern.AusblendAnteil);
            for (int i = 0; i <= 8; i++) dl.Add(start + (maxD - start) * i / 8.0);
            dl.Sort();
            double letzte = anker;
            foreach (double d in dl)
            {
                if (d <= letzte * 1.01 || d >= maxD - 1e-3) continue;
                letzte = d;
                l.Add(new KurvenPunkt((float)(d / maxD), (float)g(d), (float)(Abl(g, d, -1) * maxD), (float)(Abl(g, d, +1) * maxD)));
            }
            l.Add(new KurvenPunkt(1f, 0f, 0f, 0f));
            return l.ToArray();
        }

        static double Abl(Func<double, double> f, double d, int seite)
        {
            double h = Math.Max(1e-3, d * 1e-4);
            return seite > 0 ? (f(d + h) - f(d)) / h : (f(d) - f(d - h)) / h;
        }

        // ---- Breite ----

        /// <summary>Winkel (Grad), unter dem eine Quelle der Breite w (m) aus d (m) erscheint: 2 atan(w / 2d).</summary>
        public static double BreiteGrad(double d, double w) => 2.0 * Math.Atan(w / (2.0 * Math.Max(d, 1e-3))) * 180.0 / Math.PI;

        /// <summary>Unity-Spread-Kurve (Wert = Grad / 360, x = d / maxD) fuer eine Quelle der Breite w: Stuetzen in halben Oktaven
        /// ab 0,25 m, Tangenten aus der Ableitung. x = 0 -> 180 Grad (0,5).</summary>
        public static KurvenPunkt[] BreiteKurve(float maxD, float w)
        {
            if (!(maxD > 1f) || !(w > 0f)) return null;
            Func<double, double> v = d => d <= 0 ? 0.5 : BreiteGrad(d, w) / 360.0;
            var l = new List<KurvenPunkt>(40) { new KurvenPunkt(0f, 0.5f, 0f, (float)(Abl(v, 1e-3, +1) * maxD)) };
            for (double d = 0.25; d < maxD * 0.999; d *= Math.Sqrt(2))
                l.Add(new KurvenPunkt((float)(d / maxD), (float)v(d), (float)(Abl(v, d, -1) * maxD), (float)(Abl(v, d, +1) * maxD)));
            l.Add(new KurvenPunkt(1f, (float)v(maxD), (float)(Abl(v, maxD, -1) * maxD), 0f));
            return l.ToArray();
        }

        // ---- Luft: 4-poliger Spieltiefpass auf ISO 9613-1 ----

        static double[] stF, stW;
        static void Stuetzen()
        {
            if (stF != null) return;
            int n = (int)Math.Round(3 * Math.Log(FOben / FRef, 2));   // 12 Terzen 1-16 kHz
            var f = new double[n]; var w = new double[n];
            for (int j = 0; j < n; j++)
            {
                double ua = FRef * Math.Pow(FOben / FRef, (double)j / n), ub = FRef * Math.Pow(FOben / FRef, (double)(j + 1) / n);
                f[j] = Math.Sqrt(ua * ub); w[j] = WegeKern.Bark(ub) - WegeKern.Bark(ua);
            }
            stW = w; stF = f;
        }

        /// <summary>Luft-Mehrdaempfung gegenueber 1 kHz (dB) bei f ueber d - die Sollkurve des Fits.</summary>
        public static double LuftRel(Func<double, double> alpha, double f, double d) => (alpha(f) - alpha(FRef)) * Math.Max(0, d);

        static double Fehler(double fc, double[] soll, int pole)
        {
            double r0 = SpielfilterKern.MehrDb(fc, FRef, pole), e = 0;
            for (int j = 0; j < stF.Length; j++)
            {
                double d = Math.Min(Deckel, SpielfilterKern.MehrDb(fc, stF[j], pole) - r0) - soll[j];
                e += stW[j] * d * d;
            }
            return e;
        }

        /// <summary>Grenzfrequenz des 4-poligen Spiel-Tiefpasses, dessen Mehrdaempfung gegenueber 1 kHz der Luftdaempfung
        /// (alpha(f) - alpha(1 kHz)) * d ueber 1-16 kHz (Terzen, Gewicht Bark-Breite, beide bei 40 dB gedeckelt) am naechsten kommt.
        /// Der Anteil bei 1 kHz selbst gehoert in die Lautstaerke (Gain). Verfahren wie SpielfilterKern.TiefpassFit.</summary>
        public static double LuftFit(Func<double, double> alpha, double d, int pole = SpielfilterKern.PoleSpiel)
        {
            Stuetzen();
            var soll = new double[stF.Length];
            double max = 0;
            for (int j = 0; j < soll.Length; j++) { soll[j] = Math.Min(Deckel, LuftRel(alpha, stF[j], d)); max = Math.Max(max, soll[j]); }
            if (max <= 0.05) return LpMax;
            double lo = Math.Log(LpMin), hi = Math.Log(LpMax);
            const int Grob = 48;
            int best = 0; double eb = double.MaxValue;
            for (int i = 0; i <= Grob; i++)
            {
                double e = Fehler(Math.Exp(lo + (hi - lo) * i / Grob), soll, pole);
                if (e < eb) { eb = e; best = i; }
            }
            double a = lo + (hi - lo) * Math.Max(0, best - 1) / Grob, b = lo + (hi - lo) * Math.Min(Grob, best + 1) / Grob;
            for (int i = 0; i < 40; i++)
            {
                double m1 = a + (b - a) / 3, m2 = b - (b - a) / 3;
                if (Fehler(Math.Exp(m1), soll, pole) < Fehler(Math.Exp(m2), soll, pole)) b = m2; else a = m1;
            }
            return Math.Max(LpMin, Math.Min(LpMax, Math.Exp((a + b) / 2)));
        }
    }

    /// <summary>nahclip: Tabelle "Luft-Tiefpass je Entfernung" (LuftFit) fuer ein Wetter - einmal je Wetteraenderung gebaut, Abfrage
    /// ohne Allokation (zwei Tabellenwerte mischen, wie LuftTabelle).</summary>
    internal sealed class NahLuftTabelle
    {
        public const double DMin = 1.0, DMax = 3000.0;
        public const int Stufen = 96;
        readonly float[] fc = new float[Stufen];
        readonly double logMin = Math.Log(DMin), schritt = (Math.Log(DMax) - Math.Log(DMin)) / (Stufen - 1);
        public readonly double AlphaRef;   // dB/m bei 1 kHz (fuer die Lautstaerke)
        public readonly object Quelle;     // wofuer gebaut (Wetter.Baender-Objekt)

        public NahLuftTabelle(Func<double, double> alpha, object quelle)
        {
            Quelle = quelle;
            AlphaRef = alpha(NahclipKern.FRef);
            for (int i = 0; i < Stufen; i++) fc[i] = (float)NahclipKern.LuftFit(alpha, Math.Exp(logMin + i * schritt));
        }

        public float Hole(float d)
        {
            if (!(d > (float)DMin)) return fc[0];
            double x = (Math.Log(d) - logMin) / schritt;
            if (x >= Stufen - 1) return fc[Stufen - 1];
            int i = (int)x;
            float t = (float)(x - i);
            return fc[i] + (fc[i + 1] - fc[i]) * t;
        }
    }
}
