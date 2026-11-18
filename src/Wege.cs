using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Audio.SpatialSystem;
using Audio.SpatialSystem.SpatialAudioCalculator;
using BepInEx.Configuration;
using HarmonyLib;
using Unity.Mathematics;
using UnityEngine;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // 0.8.4 - Ausbaustufe 1 der Machbarkeitsstudie "True Acoustics" (Rechnung: WegeKern.cs). Spiel-Teil, nur im Physik-Modus.
    //  1. D und R in echten dB (UTD-Kantenbeugung aus Valve Steam Audio am Kantenpunkt des Spiels, Spiegelquelle aus Weglaenge + Absorption der
    //     getroffenen Flaechen), je Band 250 Hz (Bezug) / 1 kHz / 4 kHz.
    //  2. T je Band (Massegesetz + Koinzidenz bei jeder Frequenz, TransmissionPatch) und EIN Tiefpass aus der Bandkurve
    //     (2-poliger Fit, RaumwegKern/WegeKern) statt der freien Kurve "22 kHz -> 800 Hz ueber die Wertung". Hochpass wie seit 0.8.4.
    //  3. Energiesumme aller Wege statt Minimum (T/D/R, und Raumweg + Wand zwischen Raeumen in WandZwischenRaeumen). Die Spiel-
    //     Schwelle fuer R (T > 0,55 und D > 0,5) wird im Physik-Modus gesenkt, damit R nicht ploetzlich dazukommt.
    //  4. Empfangsraum (EN ISO 12354-1: L2 = L1 - R + 10 lg(S/A)) und 5. Flankenwege (EN ISO 12354-1 4.4, Anhang E) fuer den Weg
    //     durch eine Wand, mit den Raumdaten aus HallZonen.
    // Jeder Teil hat einen Schalter (Abschnitt 14), aus = Verhalten davor.
    // ------------------------------------------------------------------------------------------
    internal static class Wege
    {
        internal const string Sek = "14. Sound Paths (physics)";
        internal static ConfigEntry<bool> DbAn, BaenderAn, SummeAn, RImmer, RaumAn, FlankeAn;
        internal static ConfigEntry<float> RMaxDist;
        internal static ConfigEntry<float> RVerlust, RaumDiffus, RaumMax, RaumMaxFlaeche, BandStaerke, Keil;
        internal static ConfigEntry<float> BlendMs, RangBreite;   // 0.8.5
        internal static ConfigEntry<bool> Spiel24;   // blechschuss (17.11.): Fit fuer den gemessenen 4-poligen Spiel-Tiefpass
        internal static bool Spiel24An => Spiel24 != null && Spiel24.Value;
        internal static double BlendSek => BlendMs != null ? BlendMs.Value / 1000.0 : 0.3;
        static double Zeit => Time.realtimeSinceStartup;

        internal static bool An => Physik.An && DbAn != null && DbAn.Value;
        internal static bool Baender => Physik.An && BaenderAn != null && BaenderAn.Value;
        internal static bool Summe => An && SummeAn.Value;
        internal static bool RImmerAn => An && RImmer.Value;
        internal static bool RaumKorrektur => Physik.An && RaumAn != null && RaumAn.Value;
        internal static bool FlankeKorrektur => Physik.An && FlankeAn != null && FlankeAn.Value;

        internal static readonly double[] F = { 250, 1000, 4000 };
        internal static double[] Frequenzen() { F[0] = Physik.Frequenz != null ? Physik.Frequenz.Value : 250; return F; }

        internal static void Binden(ConfigFile c)
        {
            DbAn = c.Bind(Sek, "Diffraction and reflection in dB", true,
                "Physics mode: the path around an edge (diffraction) and the path over a reflecting wall get real decibels - UTD for the edge (from Valve's Steam Audio), " +
                "image source with the absorption of the hit surfaces for the reflection - per band 250 Hz / 1 kHz / 4 kHz. " +
                "Off = the game's 0..1 values (path length / range), as in 0.8.3. Applies live.");
            Keil = c.Bind(Sek, "Edge diffraction: wedge angle (deg)", 90f,
                new ConfigDescription("Edge diffraction uses the UTD implementation from Valve's Steam Audio (Apache 2.0). The game only knows the edge point, not the " +
                    "two faces, so the wedge is assumed: 90 = house corner (default), 0 = thin edge (wall top, door frame; Steam Audio's value).", new AcceptableValueRange<float>(0f, 150f)));
            BaenderAn = c.Bind(Sek, "Octave bands for the low-pass", true,
                "Physics mode: wall insulation is calculated at 250 Hz, 1 kHz and 4 kHz (mass law + coincidence) and the low-pass is fitted to that curve " +
                "(the game has one 2-pole low-pass per sound). Off = the old curve 22 kHz -> 800 Hz over the insulation. Applies live.");
            BandStaerke = c.Bind(Sek, "Band low-pass strength", 0.6f,
                new ConfigDescription("Multiplies the extra loss of the high bands before the low-pass fit. 1 = physical; lower = brighter. Tuning by ear.",
                    new AcceptableValueRange<float>(0.2f, 1.5f)));
            Spiel24 = c.Bind(Sek, "Low-pass fit for the 24 dB/oct game filter", true,
                "Measured: the game's low-pass per sound falls 24 dB per octave above the cut-off, not 12 dB as the fit assumed. " +
                "With the old fit, shots through doors (cut-off 4-6 kHz) lose 15-35 dB too much at 8-12 kHz - a sharp edge above the mids. " +
                "On = the cut-off is fitted for the real filter and checked up to 8 kHz: usually a higher cut-off, brighter, no edge in the 5-8 kHz range. " +
                "Off = fit as before (for A/B comparison). Only the game's filter; the mod's own wall filter (fixed sources) stays 12 dB/oct. Applies live.");
            SummeAn = c.Bind(Sek, "Energy sum of all paths", true,
                "Physics mode: through the wall, around the edge and over a reflection - all paths add up (energy), as in reality, instead of only the " +
                "loudest one counting. Removes level jumps where the loudest path changes. Also sums the door/window route and the wall between rooms. " +
                "Off = the loudest path wins (minimum). Applies live.");
            RImmer = c.Bind(Sek, "Reflections for every occluded sound", true,
                "The game only looks for a reflection when the wall AND the edge path are both very bad (T > 0.55 and D > 0.5); with the energy sum the " +
                "reflection would then suddenly add up. On = look for it for every occluded sound (costs extra reflection rays, see the diagnostic line). Applies live.");
            RMaxDist = c.Bind(Sek, "Reflections for every occluded sound: max. distance (m)", 50f,
                new ConfigDescription("Cost limit: farther sounds keep the game's rule. The diagnostic line shows the extra casts.", new AcceptableValueRange<float>(0f, 250f)));
            RVerlust = c.Bind(Sek, "Reflection: extra loss per bounce (dB)", 3f,
                new ConfigDescription("On top of the material absorption. The game's last leg to you is a visibility test from the hit point, not a mirror path; " +
                    "finite, scattering surfaces send less than an infinite mirror (assumption, see README).", new AcceptableValueRange<float>(0f, 10f)));
            RaumAn = c.Bind(Sek, "Receiving room", true,
                "Physics mode: sound through a wall fills the room you are in (EN ISO 12354-1: L2 = L1 - R + 10 lg(S/A)) - a small hard room behind the wall " +
                "gets a bit louder, a large or soft one quieter. Uses the rooms measured by section 9 (Reverb by Material). Applies live.");
            // 10.11. (akustik-wand, Befund 1): beide Schluessel bleiben (cfg-Kompatibilitaet), wirken aber nicht mehr - der Wandweg rechnet
            // Quell- und Hoererraum vollstaendig (WegeKern.Wandweg), ohne Gewicht und ohne Deckel.
            RaumDiffus = c.Bind(Sek, "Receiving room: diffuse share", 0.5f,
                new ConfigDescription("No effect since 0.8.8 (kept for old configs): the wall path now computes the diffuse fields of the source room and of your room in full (EN ISO 12354-1).", new AcceptableValueRange<float>(0f, 1f)));
            RaumMax = c.Bind(Sek, "Receiving room: max. correction (dB)", 6f,
                new ConfigDescription("No effect since 0.8.8 (kept for old configs): the room correction is no longer capped, it follows from both rooms (EN ISO 12354-1).", new AcceptableValueRange<float>(0f, 12f)));
            RaumMaxFlaeche = c.Bind(Sek, "Receiving room: max. wall area (m2)", 20f,
                new ConfigDescription("Largest wall area that radiates into your room (the room box side towards the sound, capped).", new AcceptableValueRange<float>(2f, 100f)));
            FlankeAn = c.Bind(Sek, "Flanking transmission", true,
                "Physics mode: sound also travels around a wall through the floor, ceiling and side walls (EN ISO 12354-1, junction values of a rigid cross junction). " +
                "Replaces the fixed 60 dB cap where the room is known: heavy walls between light floors let less through than the mass law says. Applies live.");
            BlendMs = c.Bind(Sek, "Path blend time (ms)", 300f,
                new ConfigDescription("With the energy sum: a path that appears fades in, one that disappears keeps its last value and fades out over this time, " +
                    "and a path that jumps is cross-faded (room routes via doors/windows and the edge/reflection paths). 0 = switch at once (as in 0.8.4). Applies live.",
                    new AcceptableValueRange<float>(0f, 1000f)));
            RangBreite = c.Bind(Sek, "Soft rank width (dB)", 6f,
                new ConfigDescription("Room routes: the 3 loudest routes count fully, weaker ones fade out over this level range below them instead of a hard cut " +
                    "(and everything more than 46 dB below the loudest). 0 = hard top 3. Applies live.", new AcceptableValueRange<float>(0f, 20f)));
            SettingsLive(DbAn); SettingsLive(SummeAn); SettingsLive(RImmer);
        }

        static void SettingsLive(ConfigEntry<bool> e) { e.SettingChanged += (_, __) => { try { FreiwegPatch.AlleNeu(); } catch { } }; }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var t = typeof(SpatialAudioOcclusionCombinedCalculator);
                var spaet = AccessTools.DeclaredMethod(t, "OnLateUpdate");
                var start = AccessTools.DeclaredMethod(t, "StartCalculation", new[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(float) });
                var rStart = AccessTools.DeclaredMethod(typeof(ReflectionCalculator), "StartCalculation");
                var rSchritt = AccessTools.DeclaredMethod(typeof(ReflectionCalculator), "UpdateCalculationStep");
                if (spaet == null || start == null || rStart == null || rSchritt == null)
                {
                    Plugin.Log.LogWarning("Sound paths (14.): targets not found (game update?) -> diffraction/reflection keep the game values.");
                    return;
                }
                h.Patch(spaet, prefix: new HarmonyMethod(typeof(Wege), nameof(SpaetVor)), postfix: new HarmonyMethod(typeof(Wege), nameof(SpaetNach)));
                h.Patch(start, postfix: new HarmonyMethod(typeof(Wege), nameof(StartNach)));
                h.Patch(rStart, postfix: new HarmonyMethod(typeof(Wege), nameof(RStartNach)));
                h.Patch(rSchritt, prefix: new HarmonyMethod(typeof(Wege), nameof(RSchrittVor)) { priority = Priority.Low });
                // Gesamtreview A2: gepoolte TransmissionCalculator - bricht die naechste Rechnung frueh ab (Abstand > maxDistance
                // oder 0), laeuft der TransmissionPatch-Postfix nicht und die Bandkurve des vorigen Klangs bliebe gueltig.
                var tStart = AccessTools.DeclaredMethod(typeof(TransmissionCalculator), "StartCalculation");
                if (tStart != null) h.Patch(tStart, postfix: new HarmonyMethod(typeof(Wege), nameof(TStartNach)));
                else Plugin.Log.LogWarning("Sound paths (14.): TransmissionCalculator.StartCalculation not found - band curve may lag one calculation.");
                Bereit = true;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        internal static bool Bereit;

        // ================================================================ T je Band (aus TransmissionPatch)

        internal sealed class TBand
        {
            public bool Gueltig;
            public bool Unbestimmt;   // Gesamtreview: kein einziger Strahl mit Ergebnis (WegeKern.TOhneErgebnis)
            public readonly float[] Db = new float[WegeKern.N];   // Daemmung je Band (dB, >= 0), Energie-Mittel ueber alle Strahlen
            public readonly float[] DbWand = new float[WegeKern.N]; // nur Energie durch Waende (freie Strahlen = 0); 1000 = keine
            public float Frei;                                     // Anteil freier Strahlen
            public float RaumDb = float.NaN, FlankeDb = float.NaN;  // Korrektur im Bezugsband (Log)
            public float Diffus;                                     // diffus-hall: Diffusanteil der Wand-Energie (Hoererraum-Hallfeld), Bezugsband
            public float Diffus1 = float.NaN, Diffus2 = float.NaN;   // hallsend (B2): dasselbe bei 1 kHz / 4 kHz (NaN = wie Bezugsband)
            public string Raum;
        }
        static readonly ConditionalWeakTable<TransmissionCalculator, TBand> TTab = new ConditionalWeakTable<TransmissionCalculator, TBand>();
        internal static TBand THole(TransmissionCalculator c) => TTab.GetValue(c, _ => new TBand());
        internal static bool TFinde(TransmissionCalculator c, out TBand t) { t = null; return c != null && TTab.TryGetValue(c, out t) && t.Gueltig; }
        /// <summary>Gesamtreview: Klang eines reinen Durchdringungs-Occluders (MultiSource, Hindernis-Zweig), dessen letzte Rechnung
        /// keinen einzigen Strahl mit Ergebnis hatte - der Wert 1 (NoPath) ist dann keine Messung. Physik.cs laesst dafuer den
        /// Spielwert stehen (Lautstaerke- und Filterkurve des Spiels) statt die volle Dynamik anzusetzen. Kombinierte Occluder
        /// brauchen das nicht: dort rechnet das Spiel bei T = 1 Beugung und Reflexion.</summary>
        internal static bool TUnbestimmt(SpatialAudioOccluder occ)
        {
            if (!(occ is MultiSourceSpatialAudioOccluder) || occ.IsPropagationActive) return false;
            var c = Kontext.MultiCalc;
            return c != null && TTab.TryGetValue(c, out var t) && t.Unbestimmt;
        }
        internal static int TUnbestimmtZahl;

        // Postfix TransmissionCalculator.StartCalculation: bricht die Rechnung sofort ab (Abstand > maxDistance -> NoPath, Abstand 0
        // -> ClearPath, Puffer fehlen), schreibt das Spiel sein Ergebnis ohne Strahlen und der Postfix des TransmissionPatch laeuft
        // nicht -> die Bandkurve des vorigen Klangs gilt nicht mehr. Laeuft die Rechnung an, bleibt der alte Wert bis zum neuen
        // Ergebnis stehen (wie das Spielergebnis selbst; sonst wuerde jede Neuberechnung kurz hell).
        static void TStartNach(TransmissionCalculator __instance)
        {
            if (__instance == null || __instance._isCalculationInProgress) return;
            if (TTab.TryGetValue(__instance, out var t)) { t.Gueltig = t.Unbestimmt = false; t.RaumDb = t.FlankeDb = float.NaN; t.Raum = null; t.Diffus = 0f; t.Diffus1 = t.Diffus2 = float.NaN; }
        }

        // ---- Kontext einer Durchdringungsrechnung (Hauptfaden, eine Rechnung zur Zeit) ----
        static bool kRaum, kFlanke;
        static HallZonen.RaumInfo kInfo;
        static double kD, kS;
        static readonly double[] kA2 = new double[WegeKern.N];
        // 10.11. (akustik-wand, Befund 1): Quellraum (A1 je Band, V1), Volumen des Hoererraums, Wandrechteck, Luft je Band (WegeKern.Wandweg)
        static readonly double[] kA1 = new double[WegeKern.N], kLuftM = new double[WegeKern.N];
        static double kV1, kV2, kBreite, kHoehe;

        /// <summary>Wandrechteck auf hoechstens 'flaeche' verkleinert (gleiches Seitenverhaeltnis).</summary>
        static void Rechteck(float breite, float hoehe, double flaeche)
        {
            double s = Math.Max(0.01, (double)breite * hoehe), f = s > flaeche ? Math.Sqrt(flaeche / s) : 1.0;
            kBreite = breite * f; kHoehe = hoehe * f;
        }

        /// <summary>Quellraum fuer den Wandweg (die Box, in der die Quelle steht, ohne den Hoerer): A1 je Band, V1, Wandflaeche begrenzen.</summary>
        static void QuellRaum(Vector3 quelle, Vector3 hoerer)
        {
            kA1[0] = kA1[1] = kA1[2] = -1; kV1 = 0;
            if (!HallZonen.RaumFuer(quelle, hoerer, (float)F[0], out var qi) || qi.QuelleDrin) return;
            kA1[0] = qi.Oberflaeche * qi.Alpha0; kA1[1] = qi.Oberflaeche * qi.Alpha1; kA1[2] = qi.Oberflaeche * qi.Alpha2;
            kV1 = qi.Volumen;
            // gemeinsame Wand: nie groesser als die Seite des Quellraums Richtung Hoerer
            if (qi.WandFlaeche > 0 && qi.WandFlaeche < kBreite * kHoehe) Rechteck((float)kBreite, (float)kHoehe, qi.WandFlaeche);
        }
        static readonly double[] kRF = new double[2 * WegeKern.N], kMF = new double[2];
        static readonly double[] fR = new double[4], fM = new double[4], fL = new double[4];
        // Merker der letzten Korrektur (Strahlen treffen fast immer dieselbe Wand)
        static int mG = -1; static float mTh, mM, mR1, mR2; static readonly float[] mAus = new float[WegeKern.N]; static float mRaum0, mFlanke0;
        static readonly float[] mDiffusB = new float[WegeKern.N];
        internal static float LetzteRaumDb, LetzteFlankeDb;
        /// <summary>diffus-hall: Diffusanteil (0..1) der Energie des letzten Wandstuecks im Bezugsband (Hoererraum-Hallfeld / Wandweg gesamt).</summary>
        internal static float LetzteDiffus;
        /// <summary>hallsend (B2, 12.11.): Diffusanteil des letzten Wandstuecks je Band (LetzteDiffusB[0] = LetzteDiffus).</summary>
        internal static readonly float[] LetzteDiffusB = new float[WegeKern.N];
        static void DiffusLeeren() { LetzteDiffus = 0f; LetzteDiffusB[0] = LetzteDiffusB[1] = LetzteDiffusB[2] = 0f; }

        /// <summary>Vor den Treffern einer Rechnung: Raum des Hoerers (Empfangsraum, Flanken) bestimmen.</summary>
        internal static void TBeginn(Vector3 hoerer, Vector3 quelle)
        {
            Frequenzen();
            mG = -1; LetzteRaumDb = LetzteFlankeDb = float.NaN; DiffusLeeren(); LetzteKette = null;
            kQ = quelle; kH = hoerer; kEndenGefuellt = false;   // mehrwand: Endraeume der Kette erst bei Bedarf (KorrigierenKette)
            kD = Mathf.Max(Vector3.Distance(hoerer, quelle), 0.1f);
            kRaum = kFlanke = false;
            if (!RaumKorrektur && !FlankeKorrektur) return;
            for (int b = 0; b < WegeKern.N; b++) { RaumwegKern.LuftWerte(F[b], out kLuftM[b], out _); }
            kV2 = 0;
            if (!HallZonen.RaumFuer(hoerer, quelle, (float)F[0], out kInfo) || kInfo.QuelleDrin)
            {
                // Hoerer draussen (keine Box), Quelle in einem gemessenen Raum: nur die Direktabstrahlung der Wand (Halbraum),
                // Wandflaeche = Seite des Quellraums Richtung Hoerer. Keine Flanken (kein Empfangsraum).
                if (RaumKorrektur && kInfo.Name == null && HallZonen.RaumFuer(quelle, hoerer, (float)F[0], out var qi) && !qi.QuelleDrin)
                {
                    kInfo = qi; kInfo.Name = qi.Name + " (source room, you outside)";
                    kS = Math.Min(qi.WandFlaeche, RaumMaxFlaeche.Value);
                    kA2[0] = kA2[1] = kA2[2] = -1;
                    // 10.11. (akustik-wand): Quellraum = diese Box (Diffusfeld S/A1 auf die Wand)
                    Rechteck(qi.WandBreite, qi.WandHoehe, kS);
                    kA1[0] = qi.Oberflaeche * qi.Alpha0; kA1[1] = qi.Oberflaeche * qi.Alpha1; kA1[2] = qi.Oberflaeche * qi.Alpha2;
                    kV1 = qi.Volumen;
                    kRaum = true;
                }
                return;
            }
            kS = Math.Min(kInfo.WandFlaeche, RaumMaxFlaeche.Value);
            kA2[0] = kInfo.Oberflaeche * kInfo.Alpha0; kA2[1] = kInfo.Oberflaeche * kInfo.Alpha1; kA2[2] = kInfo.Oberflaeche * kInfo.Alpha2;
            kV2 = kInfo.Volumen;
            kRaum = RaumKorrektur;
            if (kRaum) { Rechteck(kInfo.WandBreite, kInfo.WandHoehe, kS); QuellRaum(quelle, hoerer); kS = kBreite * kHoehe; }
            if (FlankeKorrektur)
            {
                // Boden und Decke: Gruppe aus der Raummessung, Dicke wie eine Einzelflaeche (Schale bzw. Gruppendicke)
                float schale = WandZwischenRaeumen.Schale != null ? WandZwischenRaeumen.Schale.Value : 0.15f;
                kFlanke = true;
                for (int j = 0; j < 2; j++)
                {
                    int g = j == 0 ? kInfo.GruppeUnten : kInfo.GruppeOben;
                    if (g < 0) g = (int)Gruppe.ConcreteStone;
                    float th = Bauakustik.EinzelDicke(g, schale);
                    kMF[j] = Physik.Masse(g, th);
                    for (int b = 0; b < WegeKern.N; b++) kRF[j * WegeKern.N + b] = Physik.Daemmung(g, th, (float)F[b], false);
                    if (kMF[j] <= 0.5) kFlanke = false;    // offener Boden (Gitter) -> keine Aussage
                }
            }
        }

        /// <summary>Ein Wandstueck (Gruppe g, Dicke th, Abstand Quelle r1 / Hoerer r2): Daemmung je Band mit Flanken und Empfangsraum.
        /// db = Massegesetz je Band (Eingang, ungedeckelt), Ausgang gedeckelt bei der Hoechstdaemmung.</summary>
        internal static void TStueck(int g, float th, float masse, float r1, float r2, float[] db)
        {
            if (!kRaum && !kFlanke) { float dyn = Physik.Dynamik.Value; for (int b = 0; b < WegeKern.N; b++) db[b] = Mathf.Clamp(db[b], 0f, dyn); return; }
            // 0.8.8: Flaechenmasse vom Aufrufer (Tuerblatt: Masse des Blatts, sonst Physik.Masse(g, th)); gleiche Masse = gleiches Stueck
            if (g == mG && Mathf.Abs(th - mTh) < 0.01f && masse == mM && Mathf.Abs(r1 - mR1) < 0.25f && Mathf.Abs(r2 - mR2) < 0.25f)
            { Array.Copy(mAus, db, WegeKern.N); LetzteRaumDb = mRaum0; LetzteFlankeDb = mFlanke0; Array.Copy(mDiffusB, LetzteDiffusB, WegeKern.N); LetzteDiffus = mDiffusB[0]; return; }
            Korrigieren(masse, r1, r2, db);
            mG = g; mTh = th; mM = masse; mR1 = r1; mR2 = r2; Array.Copy(db, mAus, WegeKern.N); mRaum0 = LetzteRaumDb; mFlanke0 = LetzteFlankeDb; Array.Copy(LetzteDiffusB, mDiffusB, WegeKern.N);
        }

        /// <summary>Wie TStueck, fuer eine ganze Wand (mehrere Schichten, Flaechenmasse mD gesamt) - Wand zwischen Raeumen.
        /// Vorher TBeginn mit Hoerer/Quelle rufen.</summary>
        internal static void Korrigieren(double mD, float r1, float r2, float[] db)
        {
            // 10.11. (klangfarbe): Baender nicht mehr je Band bei Max. insulation kappen - das flachte die Klangfarbe dicker Waende ab
            // (WegeKern.Deckeln); der Deckel gilt erst fuer die Lautstaerke. Rechengrenze wie WegeKern.BandGrenzeDb.
            float dyn = (float)WegeKern.BandGrenzeDb;
            LetzteRaumDb = LetzteFlankeDb = float.NaN; DiffusLeeren();
            if (!kRaum && !kFlanke) { for (int b = 0; b < WegeKern.N; b++) db[b] = Mathf.Clamp(db[b], 0f, dyn); return; }
            float raum0 = float.NaN, flanke0 = float.NaN;
            for (int b = 0; b < WegeKern.N; b++)
            {
                double r = db[b];
                if (kFlanke && r > 1.0 && mD > 0.5)
                {
                    // Boden, Decke (Stossstellenlaenge = Wandbreite), zwei Seitenwaende wie die Trennwand (Laenge = Hoehe)
                    fR[0] = kRF[b]; fM[0] = kMF[0]; fL[0] = kInfo.WandBreite;
                    fR[1] = kRF[WegeKern.N + b]; fM[1] = kMF[1]; fL[1] = kInfo.WandBreite;
                    fR[2] = fR[3] = r; fM[2] = fM[3] = mD; fL[2] = fL[3] = kInfo.WandHoehe;
                    double rs = WegeKern.Flanke(r, mD, kS, fR, fM, fL, 4);
                    if (b == 0) flanke0 = (float)(rs - r);
                    r = rs;
                }
                if (kRaum && r > 0.5)
                {
                    // 10.11. (akustik-wand, Befund 1): beide Raeume nach EN 12354-1 / Kuttruff (WegeKern.Wandweg) statt Empfangsraum mit
                    // Diffusanteil 0,5 und Deckel +-6 dB (die beiden Einstellungen wirken nicht mehr)
                    WegeKern.WandwegTeile(kBreite, kHoehe, r1, r2, kD, kA1[b], kV1, kA2[b], kV2, kLuftM[b], out double kg, out double kdif);
                    double k = 10 * Math.Log10(kg + kdif);   // = WegeKern.Wandweg
                    double stetig = Math.Min(1.0, r / 10.0);
                    k *= stetig;      // stetig: duenne/offene Teile (R -> 0) bekommen keine Raumkorrektur
                    LetzteDiffusB[b] = (float)(DiffusKern.Anteil(kg, kdif) * stetig);   // diffus-hall: gleich stetig gegen 0; hallsend (B2): je Band
                    if (b == 0) raum0 = (float)k;
                    r -= k;
                }
                db[b] = Mathf.Clamp((float)r, 0f, dyn);
            }
            LetzteRaumDb = raum0; LetzteFlankeDb = flanke0; LetzteDiffus = LetzteDiffusB[0];
        }

        internal static string TRaumName => kRaum || kFlanke ? kInfo.Name : null;

        // ---------------------------------------------------------------- mehrwand (10.11.2026): mehrere Trennwaende als Kette
        // Ursache und Physik: WandKetteKern.cs. Korrigieren (oben) rechnete Flanken und Wandweg EINMAL fuer alle Schichten der Linie;
        // bei getrennten Konstruktionen (Abstand > WandKetteKern.HohlraumMax) rechnet KorrigierenKette je Trennwand ihre Flanken
        // (Empfangsraum dieser Wand) und je Uebergang den Raum mit seinen A/V (HallZonen.RaumFuer am Mittelpunkt des Abstands).
        // Eine Konstruktion (ein Block) bleibt bei Korrigieren - unveraendert.
        static Vector3 kQ, kH;
        static bool kEndenGefuellt;
        static readonly KettenRaum kEndeQ = new KettenRaum(), kEndeH = new KettenRaum();
        static readonly KettenRaum[] kKette = NeueRaeume(17);
        static readonly KettenWahl kWahl = new KettenWahl();
        static readonly KettenErgebnis kErg = new KettenErgebnis();
        static readonly double[] kZusatz = new double[WegeKern.N];
        /// <summary>mehrwand: Kurzbeschreibung der letzten Kette (nur bei laufendem Klang-Log), null = keine Kette.</summary>
        internal static string LetzteKette;
        /// <summary>tordoppelt (12.11.): die letzte Kette bestand nur aus Portal-Blaettern (WandKetteKern.NurBlatt) - db ist dann NICHT
        /// gerechnet, der Aufrufer laesst die Linie weg (der Raumweg traegt sie).</summary>
        internal static bool LetzteNurBlatt;

        static KettenRaum[] NeueRaeume(int n) { var a = new KettenRaum[n]; for (int i = 0; i < n; i++) a[i] = new KettenRaum(); return a; }

        /// <summary>mehrwand: Raumdaten an Punkt p fuer die Kette (Seite Richtung Quelle 'vor', Richtung Hoerer 'nach'). Wie TBeginn: A je Band
        /// = Oberflaeche x Alpha (mit Oeffnungen), Boden/Decke fuer die Flanken aus der Raummessung. false = kein gemessener Raum (Freifeld).</summary>
        static void RaumFuellen(Vector3 p, bool vor, bool nach, KettenRaum r)
        {
            r.Leeren();
            if (!HallZonen.RaumFuer(p, nach ? kH : kQ, (float)F[0], out var ri)) return;
            r.Bekannt = true; r.Name = ri.Name;
            r.A[0] = ri.Oberflaeche * ri.Alpha0; r.A[1] = ri.Oberflaeche * ri.Alpha1; r.A[2] = ri.Oberflaeche * ri.Alpha2;
            r.V = ri.Volumen;
            if (nach) { r.NachB = ri.WandBreite; r.NachH = ri.WandHoehe; }
            if (vor)
            {
                if (nach) { if (!HallZonen.RaumFuer(p, kQ, (float)F[0], out ri)) ri = default; }
                r.VorB = ri.WandBreite; r.VorH = ri.WandHoehe;
            }
            float schale = WandZwischenRaeumen.Schale != null ? WandZwischenRaeumen.Schale.Value : 0.15f;
            r.Flanke = true;
            for (int j = 0; j < 2; j++)
            {
                int g = j == 0 ? ri.GruppeUnten : ri.GruppeOben;
                if (g < 0) g = (int)Gruppe.ConcreteStone;
                float th = Bauakustik.EinzelDicke(g, schale);
                r.MF[j] = Physik.Masse(g, th);
                for (int b = 0; b < WegeKern.N; b++) r.RF[j * WegeKern.N + b] = Physik.Daemmung(g, th, (float)F[b], false);
                if (r.MF[j] <= 0.5) r.Flanke = false;    // offener Boden (Gitter) -> keine Aussage (wie TBeginn)
            }
        }

        /// <summary>mehrwand: wie Korrigieren, fuer nb &gt;= 2 getrennte Konstruktionen (Bloecke in Linienrichtung ab der Quelle, Lage in m
        /// auf der Linie a -> b der Laenge len). db = Summe aller Schichten je Band (Eingang, inkl. Gelaende), Ausgang wie Korrigieren.
        /// Vorher TBeginn mit Hoerer/Quelle rufen.</summary>
        internal static void KorrigierenKette(KettenBlock[] bl, int nb, float len, Vector3 a, Vector3 b, float[] db)
        {
            double masse = 0;
            for (int j = 0; j < nb; j++) masse += bl[j].Masse;
            LetzteNurBlatt = false;
            if (nb <= 1 || (!kRaum && !kFlanke) || len <= 1e-3f)
            {
                // tordoppelt: ohne Raumdaten gilt jeder Block als Trennwand -> nur Portal-Blaetter = Route des Raumwegs (WandKetteKern.NurBlatt)
                if (WandKetteKern.NurBlatt(bl, nb)) { LetzteNurBlatt = true; LetzteRaumDb = LetzteFlankeDb = float.NaN; DiffusLeeren(); LetzteKette = null; return; }
                Korrigieren(masse, nb > 0 ? Mathf.Max(bl[0].A, 0f) : 0f, nb > 0 ? Mathf.Max(len - bl[nb - 1].B, 0f) : 0f, db); return;
            }
            if (nb + 1 > kKette.Length) nb = kKette.Length - 1;
            if (!kEndenGefuellt)
            {
                // Quellraum: Box der Quelle ohne den Hoerer (wie QuellRaum), Hoererraum: Box des Hoerers ohne die Quelle (wie TBeginn)
                RaumFuellen(kQ, false, true, kEndeQ);
                if (kEndeQ.Bekannt && HallZonen.RaumFuer(kQ, kH, (float)F[0], out var qi) && qi.QuelleDrin) kEndeQ.Leeren();
                RaumFuellen(kH, true, false, kEndeH);
                if (kEndeH.Bekannt && HallZonen.RaumFuer(kH, kQ, (float)F[0], out var hi) && hi.QuelleDrin) kEndeH.Leeren();
                kEndenGefuellt = true;
            }
            Kopie(kEndeQ, kKette[0]); Kopie(kEndeH, kKette[nb]);
            Vector3 dir = (b - a) / len;
            for (int j = 1; j < nb; j++)
            {
                // Mittelpunkt des Abstands zwischen Block j-1 und j (Mittellinie; die vier versetzten Linien liegen 0,25 m daneben)
                Vector3 p = a + dir * (0.5f * (bl[j - 1].B + bl[j].A));
                RaumFuellen(p, true, true, kKette[j]);
            }
            kWahl.Raum = RaumKorrektur; kWahl.Flanke = FlankeKorrektur;
            kWahl.MaxFlaeche = RaumMaxFlaeche != null ? RaumMaxFlaeche.Value : 20f;
            for (int i = 0; i < WegeKern.N; i++) kWahl.LuftM[i] = kLuftM[i];
            // Gelaende (Huegel < 3 m) steckt in db, aber in keinem Block: wirkt auf alle Anteile
            for (int i = 0; i < WegeKern.N; i++)
            {
                double s = 0;
                for (int j = 0; j < nb; j++) s += bl[j].R(i);
                kZusatz[i] = Math.Max(0, db[i] - s);
            }
            WandKetteKern.Rechnen(bl, nb, kKette, len, kD, kWahl, kZusatz, kErg);
            if (kErg.NurBlatt) { LetzteNurBlatt = true; LetzteRaumDb = LetzteFlankeDb = float.NaN; DiffusLeeren(); LetzteKette = null; return; }   // tordoppelt
            for (int i = 0; i < WegeKern.N; i++) db[i] = (float)kErg.Db[i];
            LetzteRaumDb = double.IsNaN(kErg.Raum0) ? float.NaN : (float)kErg.Raum0;
            LetzteFlankeDb = double.IsNaN(kErg.Flanke0) ? float.NaN : (float)kErg.Flanke0;
            LetzteDiffus = (float)kErg.DiffusAnteil0;
            for (int i = 0; i < WegeKern.N; i++) LetzteDiffusB[i] = (float)kErg.DiffusAnteilB[i];   // hallsend (B2)
            LetzteKette = KlangLog.An ? WandKetteKern.Text(kErg, kKette, bl) : null;
        }

        static void Kopie(KettenRaum q, KettenRaum z)
        {
            z.Bekannt = q.Bekannt; z.Name = q.Name; z.V = q.V; z.VorB = q.VorB; z.VorH = q.VorH; z.NachB = q.NachB; z.NachH = q.NachH; z.Flanke = q.Flanke;
            Array.Copy(q.A, z.A, z.A.Length); Array.Copy(q.RF, z.RF, z.RF.Length); Array.Copy(q.MF, z.MF, z.MF.Length);
        }

        // ================================================================ R: getroffene Flaechen je Strahl mitschreiben

        sealed class RSpur
        {
            public int[] Anzahl = new int[0], Ids = new int[0];
            public int2[] Karte = new int2[0];
            public bool Aktiv;
            public int Stufen, Befehle;
        }
        static readonly ConditionalWeakTable<ReflectionCalculator, RSpur> RTab = new ConditionalWeakTable<ReflectionCalculator, RSpur>();
        const int MaxRefl = 4;

        // Postfix ReflectionCalculator.StartCalculation: Befehle der Stufe 1 = [Direktstrahl], dann ein Strahl je Startrichtung.
        static void RStartNach(ReflectionCalculator __instance)
        {
            if (!An) return;
            try
            {
                var c = __instance;
                var s = RTab.GetValue(c, _ => new RSpur());
                s.Aktiv = false;
                if (!c._isCalculationInProgress) return;
                int n = c._initialRayCount, cur = c._currentInitialRayCount;
                if (s.Anzahl.Length != n) { s.Anzahl = new int[n]; s.Ids = new int[n * MaxRefl]; s.Karte = new int2[n]; }
                int off = c._directPathCommandIndex >= 0 ? 1 : 0;
                for (int i = 0; i < n; i++)
                {
                    s.Anzahl[i] = 0;
                    s.Karte[i] = new int2(i < cur ? i + off : -1, -1);
                }
                s.Aktiv = true; s.Stufen = 1; s.Befehle = cur + off;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // Prefix ReflectionCalculator.UpdateCalculationStep: genau wenn das Spiel gleich die naechste Stufe plant, liegen in
        // _spherecastHits die Treffer der fertigen Stufe. Treffer x eines Strahls, dessen reflectionCount gestiegen ist = Reflexion.
        static void RSchrittVor(ReflectionCalculator __instance)
        {
            try
            {
                var c = __instance;
                if (c.bool_1 || !c.bool_2 || !c._isCalculationInProgress || !c._lastJobHandle.IsCompleted) return;
                if (c._currentReflectionLevel >= c._maxReflections) return;
                if (!RTab.TryGetValue(c, out var s) || !s.Aktiv) return;
                c._lastJobHandle.Complete();
                var hits = c._spherecastHits; var zust = c._rayStates; var karte = c._commandIndicesMap;
                if (!hits.IsCreated || !zust.IsCreated || !karte.IsCreated) { s.Aktiv = false; return; }
                int n = Math.Min(s.Anzahl.Length, Math.Min(zust.Length, karte.Length));
                for (int i = 0; i < n; i++)
                {
                    int x = s.Karte[i].x;
                    if (x < 0 || x >= hits.Length) continue;
                    int rc = zust[i].reflectionCount;
                    if (rc > s.Anzahl[i] && s.Anzahl[i] < MaxRefl)
                    {
                        s.Ids[i * MaxRefl + s.Anzahl[i]] = hits[x].colliderInstanceID;
                        s.Anzahl[i] = rc;
                    }
                }
                for (int i = 0; i < n; i++) s.Karte[i] = karte[i];       // Befehle der naechsten Stufe
                s.Stufen++; s.Befehle += c._maxCommandsPerBatch;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // ================================================================ Ergebnis je Combined-Rechner

        sealed class Info
        {
            public readonly WegZustand Z = new WegZustand();
            public SpatialAudioOcclusionCombinedCalculator.ECombinedStep Vor;
            public bool RExtra;              // R lief nur wegen der gesenkten Schwelle
            public string RMat;              // Materialien des R-Wegs (Log)
            public float DEnergie;           // Winkelfaktor des Spiels (nur Log)
            public double SNaechste;         // umwegsuche: naechste Abfrage der Wegsuche zwischen zwei Rechnungen (Zeit, 10 Hz wie der Suchfaden)
        }
        static readonly ConditionalWeakTable<SpatialAudioOcclusionCombinedCalculator, Info> Tab =
            new ConditionalWeakTable<SpatialAudioOcclusionCombinedCalculator, Info>();
        static readonly ConditionalWeakTable<SpatialAudioOcclusionCombinedCalculator, Info>.CreateValueCallback Neu = _ => new Info();

        // Diagnose
        static int dBlenden;
        static int dUmwegUnbekannt, dUmwegSuche, dUmwegNach;
        static int dSchnapp, dFrei, dBestT, dBestD, dBestR, dDGehalten, dRGehalten, dRHyst, dRLauf, dRExtra, dTStrahlen, dDStrahlen, dRBefehle, dRBefehleExtra;
        static double dSumMin, dSumSumme; static int dSumN;
        internal static int TStrahlenZaehlen { set { dTStrahlen += value; } }

        static void SpaetVor(SpatialAudioOcclusionCombinedCalculator __instance)
        {
            if (!An) return;
            try
            {
                var c = __instance;
                Tab.GetValue(c, Neu).Vor = c._combinedStep;
                if (c._combinedStep == SpatialAudioOcclusionCombinedCalculator.ECombinedStep.TDScheduled)
                {
                    // Kostenbremse: gesenkte R-Schwelle nur bis "max. distance" (die Schwelle wird genau hier vom Spiel gelesen)
                    FreiwegPatch.Originale(c, out float ot, out float od);
                    float max = RMaxDist.Value;
                    bool extra = RImmer.Value && (c._sourcePosition - c._listenerPosition).sqrMagnitude <= max * max;
                    c._transmissionThreshold = extra ? c._minClearPathScore : ot;
                    c._diffractionThreshold = extra ? 0f : od;
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void SpaetNach(SpatialAudioOcclusionCombinedCalculator __instance)
        {
            if (!An) return;
            try
            {
                if (!Tab.TryGetValue(__instance, out var inf)) return;
                var s = __instance._combinedStep;
                if (inf.Vor == SpatialAudioOcclusionCombinedCalculator.ECombinedStep.TDScheduled && s == SpatialAudioOcclusionCombinedCalculator.ECombinedStep.RScheduled)
                {
                    // haette das Spiel mit seinen Schwellen R gestartet?
                    float t = __instance._lastTScore, d = __instance._lastDScore;
                    FreiwegPatch.Originale(__instance, out float ot, out float od);
                    inf.RExtra = !(t > ot && d > od);
                }
                else if (inf.Vor != SpatialAudioOcclusionCombinedCalculator.ECombinedStep.RScheduled) inf.RExtra = false;
                if (s == SpatialAudioOcclusionCombinedCalculator.ECombinedStep.Complete && inf.Vor != SpatialAudioOcclusionCombinedCalculator.ECombinedStep.Complete)
                    Schnappschuss(__instance, inf);
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // StartCalculation kann sofort fertig sein (Quelle ausser Reichweite -> frei)
        static void StartNach(SpatialAudioOcclusionCombinedCalculator __instance)
        {
            if (!An) return;
            try
            {
                if (__instance._combinedStep != SpatialAudioOcclusionCombinedCalculator.ECombinedStep.Complete) return;
                var inf = Tab.GetValue(__instance, Neu);
                Schnappschuss(__instance, inf);
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>Aus Physik.ResetNach: Rechner wurde zurueckgesetzt (Pool, Wechsel Hindernis/Raumweg).</summary>
        internal static void Vergessen(SpatialAudioOcclusionCombinedCalculator c)
        {
            if (Tab.TryGetValue(c, out var inf)) { inf.Z.Vergessen(); inf.RMat = null; }
        }

        static readonly double[] bT = new double[WegeKern.N], bD = new double[WegeKern.N], bR = new double[WegeKern.N], bS = new double[WegeKern.N];
        static readonly double[] alphaB = new double[MaxRefl];
        static readonly int[] rIds = new int[MaxRefl];

        static void Schnappschuss(SpatialAudioOcclusionCombinedCalculator c, Info inf)
        {
            long t0 = Laufzeit.Jetzt;     // 0.8.5: Zeitmessung (Diagnosezeile der Raumwege)
            try { SchnappschussKern(c, inf); }
            finally { Raumweg.ZeitWege.Dazu(t0, Time.frameCount); Raumweg.SchnappschussMerken(t0, c._ranTransmission, c._ranReflection); }   // 10.11. (spitzen)
        }

        static void SchnappschussKern(SpatialAudioOcclusionCombinedCalculator c, Info inf)
        {
            Frequenzen();
            float dyn = Physik.Dynamik.Value;
            var res = c.LastAggregateResult;
            V3 q = V(c._sourcePosition), h = V(c._listenerPosition);
            double d = Math.Max(V3.Abstand(q, h), 0.1);
            var z = inf.Z;
            z.Direkt = d;
            // hallsend (A2, 12.11.): Quelle im gemessenen Raum des Hoerers (Vereinigung seiner Boxen, HallZonen.RaumFuer)? Einmal je fertiger
            // Rechnung (Kosten wie TBeginn: 4-Punkt-Zwischenspeicher). Kein gemessener Raum (draussen, Hall aus) -> false.
            z.GleicherRaum = HallZonen.RaumFuer(c._listenerPosition, c._sourcePosition, (float)F[0], out var gr) && gr.QuelleDrin;
            z.RaumHallSend = double.NaN; z.RaumHallFehl = 0;   // gilt erst wieder nach dem naechsten Anwenden (Physik.Trocken)
            bool frei = !c._ranTransmission || (!c._ranDiffraction && !c._ranReflection && res.transmissionScore <= c._minClearPathScore);
            dSchnapp++;
            if (frei)
            {
                // Direkttest des Spiels unter der Freiweg-Schwelle: dessen kleiner T-Wert gilt (stetig an der Schwelle)
                double[] freiT = null;
                if (c._ranTransmission && TFinde(c._transmissionCalculator, out var tf) && Math.Abs(tf.Db[0] - c._lastTScore * dyn) < 0.5)
                {
                    for (int b = 0; b < WegeKern.N; b++) bT[b] = -tf.Db[b];
                    freiT = bT;
                }
                WegeKern.Auswerten(z, true, false, null, 0, false, false, false, null, false, false, false, null, q, h, dyn, Summe, freiT: freiT, f: F);
                Blende(z);
                dFrei++;
                return;
            }

            // ---- T ----
            double tDb = res.transmissionScore * dyn, tFrei = 0;
            if (TFinde(c._transmissionCalculator, out var tb) && Math.Abs(tDb - tb.Db[0]) < 0.5)
            {
                tFrei = tb.Frei;
                for (int b = 0; b < WegeKern.N; b++) bT[b] = tb.DbWand[b] >= 999f ? WegeKern.KeinWeg : -tb.DbWand[b];
                z.TDiffus = tb.Diffus;   // diffus-hall
                z.TDiffus1 = tb.Diffus1; z.TDiffus2 = tb.Diffus2;   // hallsend (B2)
            }
            else { for (int b = 0; b < WegeKern.N; b++) bT[b] = -tDb; z.TDiffus = 0; z.TDiffus1 = z.TDiffus2 = double.NaN; }   // ohne Bandmessung: Spielwertung, flach

            // ---- D ----
            bool dDirekt = false, dHat = false;
            if (c._ranDiffraction && c._diffractionCalculator is EdgeDiffractionCalculator dc && dc._buffersAllocated && dc._resultBuffer.IsCreated && dc._resultBuffer.Length > 0)
            {
                var dr = dc._resultBuffer[0];
                if (dr.isDirectPath) dDirekt = true;
                else if (dr.FoundPath)
                {
                    dHat = true;
                    z.LD = dr.pathLength; inf.DEnergie = dr.energyFactor;
                    V3 kante = V(dr.diffractionPoint);
                    double n = WegeKern.KeilN(Keil.Value);
                    for (int b = 0; b < WegeKern.N; b++) bD[b] = WegeKern.Beugung(q, kante, h, F[b], n);
                }
                dDStrahlen += 2 + 4 * dc._dynamicEdgeSearchRayCount;
            }

            // ---- R ----
            bool rDirekt = false, rHat = false;
            if (c._ranReflection && c._reflectionCalculator is ReflectionCalculator rc)
            {
                dRLauf++;
                if (RTab.TryGetValue(rc, out var spur)) { dRBefehle += spur.Befehle; if (inf.RExtra) { dRBefehleExtra += spur.Befehle; dRExtra++; } }
                if (rc.IsDirectPathClear) rDirekt = true;
                else
                {
                    var best = rc._currentBestOverallData;
                    if (best.found && best.pathLength < float.MaxValue)
                    {
                        rHat = true;
                        int n = 1;
                        int ray = best.rayIndex;
                        if (spur != null && spur.Aktiv && ray >= 0 && ray < spur.Anzahl.Length) n = Math.Max(1, Math.Min(MaxRefl, spur.Anzahl[ray]));
                        else if (rc._rayStates.IsCreated && ray >= 0 && ray < rc._rayStates.Length) n = Math.Max(1, Math.Min(MaxRefl, rc._rayStates[ray].reflectionCount));
                        for (int k = 0; k < n; k++) rIds[k] = spur != null && spur.Aktiv && ray >= 0 && ray < spur.Anzahl.Length ? spur.Ids[ray * MaxRefl + k] : 0;
                        for (int b = 0; b < WegeKern.N; b++)
                        {
                            for (int k = 0; k < n; k++) alphaB[k] = rIds[k] != 0 ? HallZonen.AlphaCollider(rIds[k], (float)F[b]) : 0.1;
                            bR[b] = WegeKern.Reflexion(d, best.pathLength, alphaB, n, RVerlust.Value);
                        }
                        z.LR = best.pathLength; z.RAnzahl = n;
                        inf.RMat = KlangLog.An ? RMaterialien(n) : null;
                    }
                }
            }

            // umwegsuche (13.11.): kein Umweg gefunden -> die eigene Wegsuche fragen (Anfrage nur fuer genau diese Rechner, Ergebnis nur, wenn
            // frisch und plausibel; sonst null = Rueckfallgrenze wie bisher). Alle anderen Faelle laufen unveraendert.
            double[] suche = null;
            z.SGrund = UmwegKern.Grund.KeinNetz;
            if (WegeKern.UmwegUnbekanntAus(c._ranTransmission ? tFrei : 0, c._ranDiffraction, dDirekt, dHat, c._ranReflection, rDirekt, rHat)
                && Wegsuche.Umweg(c, QuelleJetzt(c), HoererJetzt(c), z.GleicherRaum, F, z, bS)) suche = bS;
            WegeKern.Auswerten(z, false, c._ranTransmission, bT, tFrei, c._ranDiffraction, dDirekt, dHat, bD, c._ranReflection, rDirekt, rHat, bR, q, h, dyn, Summe, f: F, suche: suche);
            if (z.UmwegUnbekannt) dUmwegUnbekannt++;
            if (z.HatS) dUmwegSuche++;
            inf.SNaechste = Zeit + 0.1;
            Blende(z);
            if (z.Bester == 1) dBestT++; else if (z.Bester == 2) dBestD++; else if (z.Bester == 3) dBestR++;
            if (z.DGehalten) dDGehalten++;
            if (z.RGehalten) dRGehalten++;
            if (z.RHysterese) dRHyst++;
            // Vergleich Minimum (lautester Weg) gegen Summe im Bezugsband
            double minDb = z.Bester == 1 ? z.T[0] : z.Bester == 2 ? z.D[0] : z.Bester == 3 ? z.R[0] : -dyn;
            dSumMin += -minDb; dSumSumme += -z.Ergebnis[0]; dSumN++;
        }

        // 0.8.5: D/R zeitlich ueberblenden (nur mit Energiesumme; sonst gilt das Ergebnis sofort)
        static void Blende(WegZustand z)
        {
            if (!Summe) { z.DBl.Vergessen(); z.RBl.Vergessen(); return; }
            bool lief = z.Blendet;
            WegeKern.BlendeSetzen(z, Zeit, BlendSek);
            WegeKern.Mischen(z, Zeit, BlendSek, Physik.Dynamik.Value, F);
            if (z.Blendet && !lief) dBlenden++;
        }

        /// <summary>Was jetzt gilt (Ueberblendung fortgeschrieben). false = kein Ergebnis.</summary>
        static bool Jetzt(WegZustand z, out double[] db, out double w)
        {
            db = z.Ergebnis; w = z.Wertung;
            if (!z.Gueltig) return false;
            if (Summe && z.DBl.Hat)
            {
                WegeKern.Mischen(z, Zeit, BlendSek, Physik.Dynamik.Value, F);
                db = z.Jetzt; w = z.WertungJetzt;
            }
            return true;
        }

        static string RMaterialien(int n)
        {
            var sb = new StringBuilder(32);
            for (int k = 0; k < n; k++)
            {
                if (k > 0) sb.Append('/');
                sb.Append(rIds[k] != 0 ? ((Gruppe)MaterialCache.Get(rIds[k])).ToString() : "?");
            }
            return sb.ToString();
        }

        /// <summary>Aus Physik.WertungNach: Wertung der letzten fertigen Rechnung (gehalten bis zur naechsten).</summary>
        internal static bool Wertung(SpatialAudioOcclusionCombinedCalculator c, out float w)
        {
            w = 0f;
            if (!An || !Tab.TryGetValue(c, out var inf)) return false;
            // umwegsuche: letzte Rechnung ohne gefundenen Umweg -> hoechstens 10x je Sekunde nachsehen, ob die Wegsuche (inzwischen) ein frisches
            // Ergebnis hat oder ob es veraltet ist (haelt zugleich die Anfrage am Leben). Kostet je Abfrage < 1 us, keine Allokation (Messplatz).
            var zz = inf.Z;
            if (zz.Gueltig && zz.UmwegUnbekannt && Zeit >= inf.SNaechste)
            {
                inf.SNaechste = Zeit + 0.1;
                bool ok = Wegsuche.Umweg(c, QuelleJetzt(c), HoererJetzt(c), zz.GleicherRaum, F, zz, bS);
                if (WegeKern.SucheSetzen(zz, ok ? bS : null, Summe, Physik.Dynamik.Value, F, Zeit, BlendSek)) dUmwegNach++;
            }
            if (!Jetzt(inf.Z, out _, out double wj)) return false;
            w = (float)wj;
            return true;
        }

        /// <summary>diffus-hall: um wie viel dB der gerichtete Anteil des Hindernis-Wegs (Combined) leiser ist als die Summe (Wand mit
        /// Hoererraum-Hallfeld). anteil = Diffusanteil. false = kein Ergebnis / kein Diffusanteil.</summary>
        internal static bool DiffusZusatz(SpatialAudioOccluder occ, out double zusatz, out double anteil, double[] zusatzB = null)
        {
            zusatz = 0; anteil = 0;
            if (!An || occ == null || occ.IsPropagationActive) return false;
            if (!(occ is CombinedSpatialAudioOccluder comb) || !(comb._obstructionCalculator is SpatialAudioOcclusionCombinedCalculator c)) return false;
            if (!Tab.TryGetValue(c, out var inf) || !inf.Z.Gueltig) return false;
            anteil = WegeKern.DiffusAnteil(inf.Z, Summe);
            zusatz = DiffusKern.TrockenZusatzDb(anteil);
            // hallsend (B2): je Band mit den Energien dieses Bands
            if (zusatzB != null)
                for (int b = 0; b < WegeKern.N; b++) zusatzB[b] = b == 0 ? zusatz : DiffusKern.TrockenZusatzDb(WegeKern.DiffusAnteil(inf.Z, Summe, b));
            return anteil > 0;
        }

        /// <summary>wegkarten (16.11.): aktuelle Orte fuer Tabelle und Wegsuche. _sourcePosition/_listenerPosition sind die Orte der letzten
        /// gestarteten Rechnung des Spiels und koennen weit zurueckliegen (Raid 13.11. 22:08: 48 von 92 "different rooms" und 4 von 5 Drehungen
        /// &gt; 90 Grad mit Rechnungsabstand 67 m bei echtem Abstand 1,3 m). Der PositionChangeChecker des Rechners merkt in OnUpdate die Orte,
        /// sobald sie sich um mehr als seine Schwelle (0,3 m je nach Qualitaet) bewegt haben - also hoechstens diese Schwelle neben dem Jetzt.
        /// Leer (noch kein OnUpdate) -> Ort der Rechnung.</summary>
        static Vector3 QuelleJetzt(SpatialAudioOcclusionCombinedCalculator c)
        {
            var p = c._positionChangeChecker; return p != null && p._previousSourcePos != Vector3.zero ? p._previousSourcePos : c._sourcePosition;
        }
        static Vector3 HoererJetzt(SpatialAudioOcclusionCombinedCalculator c)
        {
            var p = c._positionChangeChecker; return p != null && p._previousListenerPos != Vector3.zero ? p._previousListenerPos : c._listenerPosition;
        }

        /// <summary>wegrichtung (13.11.): Zustand des Hindernis-Wegs dieses Occluders, wenn sein Pegel gerade aus der Wegtabelle kommt (das Spiel
        /// fand keinen Umweg, Kantenwege der Tabelle angewendet) - Quelle der Richtung "from the edge" (Richtung.cs). Nur lesen.</summary>
        internal static bool KantenZustand(SpatialAudioOccluder occ, out WegZustand zustand)
        {
            zustand = null;
            if (!An || occ == null || occ.IsPropagationActive) return false;
            if (!(occ is CombinedSpatialAudioOccluder comb) || !(comb._obstructionCalculator is SpatialAudioOcclusionCombinedCalculator c)) return false;
            if (!Tab.TryGetValue(c, out var inf)) return false;
            var z = inf.Z;
            if (!z.Gueltig || z.Frei || !z.UmwegUnbekannt || !z.HatS || !z.STab) return false;
            zustand = z;
            return true;
        }

        /// <summary>hallsend (A2, 12.11.): Hindernis-Weg (Combined, Quelle und Hoerer im selben Raum des Spiels) mit Quelle und Hoerer im
        /// selben GEMESSENEN Raum - der Hall des Raums bleibt beim unverdeckten Pegel (DiffusKern.RaumHall), nur der trockene Teil traegt
        /// die Hindernisdaempfung. false = kein Ergebnis, frei, verschiedene/keine gemessenen Raeume (dann wie bisher).</summary>
        internal static bool RaumHallBleibt(SpatialAudioOccluder occ, out WegZustand zustand)
        {
            zustand = null;
            if (!An || occ == null || occ.IsPropagationActive) return false;
            if (!(occ is CombinedSpatialAudioOccluder comb) || !(comb._obstructionCalculator is SpatialAudioOcclusionCombinedCalculator c)) return false;
            if (!Tab.TryGetValue(c, out var inf) || !WegeKern.RaumHallBleibt(inf.Z)) return false;
            zustand = inf.Z;
            return true;
        }

        // ================================================================ Filter

        /// <summary>Tiefpass aus der Bandkurve des Hindernis-Wegs der Quelle, die gerade angewendet wird (Combined: Wege-Ergebnis,
        /// MultiSource: T-Baender). false = keine Bandkurve -> alte Abbildung.</summary>
        internal static bool Tiefpass(SpatialAudioOccluder occ, out float lp)
        {
            lp = 22000f;
            if (!BandKurve(occ, out double m1, out double m2)) return false;
            lp = FitLp(m1, m2);
            BandFilter++;
            return true;
        }

        /// <summary>0.8.5: reale Mehrdaemmung 1 kHz / 4 kHz gegenueber dem Bezugsband (dB) des Hindernis-Wegs, ohne Zaehler
        /// (auch fuer die Hoerbarkeit je Band in Physik.LautstaerkeNach und das Klang-Log). false = keine Bandkurve.</summary>
        internal static bool BandKurve(SpatialAudioOccluder occ, out double m1, out double m2)
        {
            m1 = m2 = 0;
            if (!Baender || occ == null || occ.IsPropagationActive) return false;
            if (occ is CombinedSpatialAudioOccluder comb && comb._obstructionCalculator is SpatialAudioOcclusionCombinedCalculator c)
            {
                if (An && Tab.TryGetValue(c, out var inf) && Jetzt(inf.Z, out var jd, out _))
                {
                    m1 = jd[0] - jd[1]; m2 = jd[0] - jd[2];
                }
                else if (TFinde(c._transmissionCalculator, out var tb) && TBestimmt(c)) { m1 = tb.Db[1] - tb.Db[0]; m2 = tb.Db[2] - tb.Db[0]; }
                else return false;
            }
            else if (occ is MultiSourceSpatialAudioOccluder && TFinde(Kontext.MultiCalc, out var tm)) { m1 = tm.Db[1] - tm.Db[0]; m2 = tm.Db[2] - tm.Db[0]; }
            else return false;
            return true;
        }

        // Ohne "D/R in dB" (Minimum der Spielwertungen): Bandkurve von T nur, wenn T der leichteste Weg ist.
        static bool TBestimmt(SpatialAudioOcclusionCombinedCalculator c)
        {
            var r = c.LastAggregateResult;
            if (!c._ranTransmission) return false;
            return (!c._ranDiffraction || r.transmissionScore <= r.diffractionScore) && (!c._ranReflection || r.transmissionScore <= r.reflectionScore);
        }

        /// <summary>Tiefpass-Fit aus der Bandkurve (Mehrdaempfung 1 kHz / 4 kHz gegenueber dem Bezugsband, vor BandStaerke).
        /// eigenerFilter = true: der Tiefpass geht an den eigenen WandFilter (2-polig) statt an den Filter des Spiels.</summary>
        internal static float FitLp(double m1, double m2, bool eigenerFilter = false)
        {
            double k = BandStaerke.Value, a = m1 * k, b = m2 * k, tp = Physik.TiefpassMin.Value, f0 = Frequenzen()[0];
            // blechschuss (17.11.): Modell je Ziel-Filter (SpielfilterKern.cs) - Spielfilter 4-polig nur mit Schalter, sonst wie bisher
            int modell = !eigenerFilter && Spiel24An ? 4 : 2;
            // 10.11. (B2): Fit nach Lautheit ab dem Bezugsband. Kleiner Merker (Hauptfaden): dieselbe Quelle fragt je Anwenden bis zu dreimal
            // mit derselben Bandkurve (Filter, Lautstaerke, gemerkter Weg) - der Fit kostet ~3x den bisherigen Zwei-Punkte-Fit.
            for (int i = 0; i < FitN; i++)
                if (fitA[i] == a && fitB[i] == b && fitTp[i] == tp && fitF0[i] == f0 && fitModell[i] == modell) return fitLp[i];
            float lp = (float)FitRoh(a, b, tp, f0, modell == 4);
            int j = fitNaechster++ % FitN;
            fitA[j] = a; fitB[j] = b; fitTp[j] = tp; fitF0[j] = f0; fitModell[j] = modell; fitLp[j] = lp;
            return lp;
        }

        /// <summary>blechschuss (17.11.): Fit ohne BandStaerke/Merker fuer den Filter des Spiels - mit Schalter fuer den gemessenen
        /// 4-poligen Filter bis 8 kHz (SpielfilterKern.TiefpassFit), sonst WegeKern.TiefpassFit wie bisher.</summary>
        internal static double FitSpiel(double a, double b, double tp, double f0) => FitRoh(a, b, tp, f0, Spiel24An);

        static double FitRoh(double a, double b, double tp, double f0, bool spiel24)
        {
            if (!spiel24) return WegeKern.TiefpassFit(a, b, F[1], F[2], tp, 22000, f0);
            return SpielfilterKern.TiefpassFit(a, b, F[1], F[2], tp, 22000, f0, SpielfilterKern.PoleSpiel, SpielfilterKern.FObenStandard);
        }
        const int FitN = 8;
        static readonly double[] fitA = { double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN }, fitB = new double[FitN], fitTp = new double[FitN], fitF0 = new double[FitN];
        static readonly float[] fitLp = new float[FitN];
        static readonly int[] fitModell = new int[FitN];
        static int fitNaechster;

        internal static int BandFilter, SummeRaumweg, BandFilterRaumweg;

        // ================================================================ Klang-Log / Diagnose

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Klang-Log: Beitrag jedes Wegs in dB (Bezug/1k/4k) und die Summe (nur Hindernis-Weg, Physik-Modus).</summary>
        internal static string LogText(SpatialAudioOccluder occ)
        {
            if (!An || !(occ is CombinedSpatialAudioOccluder comb) || !(comb._obstructionCalculator is SpatialAudioOcclusionCombinedCalculator c)) return null;
            if (!Tab.TryGetValue(c, out var inf) || !inf.Z.Gueltig) return null;
            var z = inf.Z;
            var sb = new StringBuilder(200);
            sb.Append(" | paths dB@").Append(F[0].ToString("F0", Inv)).Append("/1k/4k:");
            if (z.Frei) { sb.Append(" clear (game), direct test T "); B(sb, z.T); return sb.ToString(); }
            if (z.HatT) { sb.Append(" T "); B(sb, z.T); sb.Append(" (free rays ").Append((z.TFrei * 100).ToString("F0", Inv)).Append(" %)"); if (TFinde(c._transmissionCalculator, out var tb) && (!float.IsNaN(tb.RaumDb) || !float.IsNaN(tb.FlankeDb))) { sb.Append(" ("); if (!float.IsNaN(tb.FlankeDb)) { sb.Append("flank "); V(sb, tb.FlankeDb); sb.Append(' '); } if (!float.IsNaN(tb.RaumDb)) { sb.Append("room "); V(sb, tb.RaumDb); } if (tb.Raum != null) sb.Append(" \"").Append(tb.Raum).Append('"'); sb.Append(')'); } }
            if (z.HatD)
            {
                sb.Append(" D ");
                if (z.DDirekt) sb.Append("line of sight 0");
                else { B(sb, z.D); sb.Append(" (L ").Append(z.LD.ToString("F1", Inv)).Append(" m, detour ").Append((z.LD - z.Direkt).ToString("F2", Inv)).Append(" m"); if (z.DGehalten) sb.Append(", held"); sb.Append(')'); }
            }
            else if (c._ranDiffraction) sb.Append(" D none");
            if (z.HatR)
            {
                sb.Append(" R ");
                if (z.RDirekt) sb.Append("direct 0");
                else { B(sb, z.R); sb.Append(" (L ").Append(z.LR.ToString("F1", Inv)).Append(" m, n=").Append(z.RAnzahl); if (inf.RMat != null) sb.Append(' ').Append(inf.RMat); if (z.RGehalten) sb.Append(", held"); if (z.RHysterese) sb.Append(", kept"); if (inf.RExtra) sb.Append(", extra"); sb.Append(')'); }
            }
            else if (c._ranReflection) sb.Append(" R none");
            if (z.UmwegUnbekannt && z.HatS && z.STab)
            {
                // wegtabelle: eindeutige Kennung "precomputed path", Werte je Band (nach Ratenbegrenzung), Richtungsdaten fuer klang-richtung
                sb.Append(" | no detour found (game) -> precomputed path: "); B(sb, z.S);
                sb.Append(" dB (").Append(z.SKanten).Append(z.SKanten == 1 ? " edge" : " edges").Append(", len ").Append(z.SLaenge.ToString("F1", Inv))
                  .Append(" m, probes ").Append(z.STabQ).Append('/').Append(z.STabH).Append(" room ").Append(z.STabRaum);
                if (z.STabSicht) sb.Append(", probes by line of sight (").Append(z.STabStrahlen).Append(" checks)");
                if (z.STabFern > 0) sb.Append(", ").Append(z.STabFern).Append(z.STabFern == 1 ? " far pair" : " far pairs").Append(" (level only, its direction falls back to the real source)");
                if (z.SHatKante)
                {
                    sb.Append(", first edge (").Append(z.SErste.X.ToString("F1", Inv)).Append(", ").Append(z.SErste.Y.ToString("F1", Inv)).Append(", ").Append(z.SErste.Z.ToString("F1", Inv))
                      .Append("), last edge (").Append(z.SKante.X.ToString("F1", Inv)).Append(", ").Append(z.SKante.Y.ToString("F1", Inv)).Append(", ").Append(z.SKante.Z.ToString("F1", Inv)).Append(')');
                }
                sb.Append(") instead of screening limit ").Append(WegeKern.SchirmDb(F[0], F[WegeKern.N - 1], 1e9).ToString("F1", Inv)).Append("/")
                  .Append(WegeKern.SchirmDb(F[1], F[WegeKern.N - 1], 1e9).ToString("F1", Inv)).Append("/").Append(WegeKern.SchirmDb(F[2], F[WegeKern.N - 1], 1e9).ToString("F1", Inv)).Append(" dB");
            }
            else if (z.UmwegUnbekannt && z.HatS)
            {
                // umwegsuche: eindeutige Kennung zum Zaehlen ("path search applied"), Werte der Suche je Band, Grenze, Richtungsangabe
                sb.Append(" | no detour found (game) -> path search applied: ").Append(z.SWege).Append(z.SWege == 1 ? " path, " : " paths, ")
                  .Append(z.SKanten).Append(z.SKanten == 1 ? " edge " : " edges "); B(sb, z.S);
                sb.Append(" dB instead of screening limit ").Append(WegeKern.SchirmDb(F[0], F[WegeKern.N - 1], 1e9).ToString("F1", Inv)).Append("/")
                  .Append(WegeKern.SchirmDb(F[1], F[WegeKern.N - 1], 1e9).ToString("F1", Inv)).Append("/").Append(WegeKern.SchirmDb(F[2], F[WegeKern.N - 1], 1e9).ToString("F1", Inv))
                  .Append(" dB (result age ").Append(z.SAlterMs.ToString("F0", Inv)).Append(" ms");
                if (z.SHatKante) sb.Append(", edge point (").Append(z.SKante.X.ToString("F1", Inv)).Append(", ").Append(z.SKante.Y.ToString("F1", Inv)).Append(", ").Append(z.SKante.Z.ToString("F1", Inv)).Append(')');
                sb.Append(')');
            }
            else if (z.UmwegUnbekannt)
            {
                sb.Append(" | no detour found -> screening limit ").Append(WegeKern.SchirmDb(F[0], F[WegeKern.N - 1], 1e9).ToString("F1", Inv)).Append("/")
                  .Append(WegeKern.SchirmDb(F[1], F[WegeKern.N - 1], 1e9).ToString("F1", Inv)).Append("/").Append(WegeKern.SchirmDb(F[2], F[WegeKern.N - 1], 1e9).ToString("F1", Inv))
                  .Append(" dB (ISO 9613-2 Eq. 14, smallest detour reaching the 25 dB cap in the top band)");
                if (z.SGrund != UmwegKern.Grund.KeinNetz) sb.Append(" (path search: ").Append(UmwegKern.Text(z.SGrund)).Append(')');
                if (z.STabGrund >= 0) sb.Append(" (precomputed path: ").Append(WegTabelleSpiel.GrundText(z.STabGrund)).Append(')');   // wegtabelle: Grund ohne Wert
            }
            sb.Append(Summe ? " => sum " : " => loudest ");
            B(sb, z.Ergebnis);
            if (Summe && Jetzt(z, out var jl, out _) && z.Blendet)
            {
                sb.Append(" (blending D ").Append((z.DBl.Anteil(Zeit, BlendSek) * 100).ToString("F0", Inv)).Append(" % / R ")
                  .Append((z.RBl.Anteil(Zeit, BlendSek) * 100).ToString("F0", Inv)).Append(" %");
                if (z.SBl.Laeuft(Zeit, BlendSek)) sb.Append(" / path search ").Append((z.SBl.Anteil(Zeit, BlendSek) * 100).ToString("F0", Inv)).Append(" %");
                sb.Append(", now ");
                B(sb, jl); sb.Append(')');
            }
            sb.Append(" (d ").Append(z.Direkt.ToString("F1", Inv)).Append(" m)");
            // hallsend (A2): Hindernis im selben gemessenen Raum - Hall bleibt beim unverdeckten Pegel
            if (z.GleicherRaum && !double.IsNaN(z.RaumHallSend))
            {
                sb.Append(" | same room: reverb kept, send +").Append(z.RaumHallSend.ToString("F1", Inv)).Append(" dB");
                if (z.RaumHallFehl > 0.05) sb.Append(" (reverb short ").Append(z.RaumHallFehl.ToString("F1", Inv)).Append(" dB, send limit)");
            }
            return sb.ToString();
        }

        /// <summary>Klang-Log (Raumweg): Route, Wand zwischen Raeumen (mit Raum/Flanke) und ihre Summe im Bezugsband.</summary>
        internal static string RaumLogText(SpatialAudioOccluder occ, ParallelSoundPropagationCalculator pc)
        {
            if (!Physik.An || pc == null) return null;
            if (!WandZwischenRaeumen.WandFuer(pc, out float ws, out float[] wdb, out float raum, out float flanke)) return null;
            float dyn = Physik.Dynamik.Value;
            float route = pc._propagationScore * dyn;
            var sb = new StringBuilder(120);
            sb.Append(" | paths dB@").Append(F[0].ToString("F0", Inv)).Append("/1k/4k: route -").Append(route.ToString("F1", Inv))
              .Append(" wall -").Append(wdb[0].ToString("F1", Inv)).Append("/-").Append(wdb[1].ToString("F1", Inv)).Append("/-").Append(wdb[2].ToString("F1", Inv));
            if (!float.IsNaN(flanke) || !float.IsNaN(raum))
            {
                sb.Append(" (");
                if (!float.IsNaN(flanke)) { sb.Append("flank "); V(sb, flanke); sb.Append(' '); }
                if (!float.IsNaN(raum)) { sb.Append("room "); V(sb, raum); }
                sb.Append(')');
            }
            sb.Append(WandZwischenRaeumen.KetteText(pc));   // mehrwand: getrennte Trennwaende als Kette (mittlere Linie), sonst leer
            double e = WegeKern.Energie(-route) + WegeKern.Energie(-wdb[0]);
            sb.Append(Summe ? " => sum " : " => loudest ").Append((Summe ? WegeKern.DbAus(e, -dyn) : -Math.Min(route, wdb[0])).ToString("F1", Inv));
            sb.Append(WandZwischenRaeumen.DiffusText(pc));   // galpegel (10.11.): Aufteilung gerichtet/diffus des Raumwegs mit Wand
            return sb.ToString();
        }

        static void B(StringBuilder sb, double[] v)
        {
            for (int b = 0; b < WegeKern.N; b++)
            {
                if (b > 0) sb.Append('/');
                sb.Append(v[b] <= WegeKern.KeinWeg ? "-" : v[b].ToString("F1", Inv));
            }
        }
        static void V(StringBuilder sb, float v) { if (v >= 0) sb.Append('+'); sb.Append(v.ToString("F1", Inv)); }

        internal static void Loggen()
        {
            if (dSchnapp > 0)
            {
                int verdeckt = dSchnapp - dFrei;
                string kosten = "";
                int alle = dTStrahlen + dDStrahlen + dRBefehle;
                if (alle > 0)
                    kosten = $" Rays/casts in 30 s: transmission {dTStrahlen}, diffraction {dDStrahlen}, reflection {dRBefehle} ({dRLauf} runs); " +
                             $"{dRExtra} reflection runs only because of 'Reflections for every occluded sound' = {dRBefehleExtra} casts (+{100.0 * dRBefehleExtra / Math.Max(1, alle - dRBefehleExtra):F0} % of the other audio casts).";
                Plugin.Log.LogInfo($"[Diagnostics] Sound paths: {dSchnapp} finished path calculations ({dFrei} clear), loudest path T {dBestT} / D {dBestD} / R {dBestR}; " +
                                   $"held D {dDGehalten}, held R {dRGehalten}, no detour found (screening limit {WegeKern.SchirmMaxDb:F0} dB, ISO 9613-2) {dUmwegUnbekannt} (path search applied in {dUmwegSuche}, {dUmwegNach} search results set or withdrawn between two calculations), R kept (< 1 dB change) {dRHyst}, D/R changes blended over {BlendSek * 1000:F0} ms {dBlenden}; " +
                                   (dSumN > 0 ? $"mean insulation loudest path {dSumMin / dSumN:F1} dB -> {(Summe ? "energy sum" : "result")} {dSumSumme / dSumN:F1} dB ({verdeckt} occluded)." : ".") + kosten);
            }
            if (BandFilter + BandFilterRaumweg + SummeRaumweg > 0)
                Plugin.Log.LogInfo($"[Diagnostics] Sound paths: {BandFilter} low-pass values fitted to the band curve (obstruction), {BandFilterRaumweg} on room paths, {SummeRaumweg} room routes summed with the wall between rooms.");
            dBlenden = 0; dUmwegUnbekannt = 0; dUmwegSuche = 0; dUmwegNach = 0;
            dSchnapp = dFrei = dBestT = dBestD = dBestR = dDGehalten = dRGehalten = dRHyst = dRLauf = dRExtra = dTStrahlen = dDStrahlen = dRBefehle = dRBefehleExtra = 0;
            dSumMin = dSumSumme = 0; dSumN = 0; BandFilter = BandFilterRaumweg = SummeRaumweg = 0;
        }

        static V3 V(Vector3 v) => new V3(v.x, v.y, v.z);

        // ================================================================ Pruefschritt Meta-XR-Beugung (nur lesen, nichts umschalten)

        internal static string MetaText;
        static float metaZeit = -1f;

        /// <summary>Raidstart (Raumspeicher fertig): Meta-Einstellung ein paar Sekunden spaeter lesen (Szene ganz geladen).</summary>
        internal static void RaidStart() { metaZeit = Time.realtimeSinceStartup + 5f; }

        internal static void Tick()
        {
            if (metaZeit < 0f || Time.realtimeSinceStartup < metaZeit) return;
            metaZeit = -1f;
            MetaPruefen();
        }

        internal static void MetaPruefen()
        {
            try { MetaText = MetaLesen(); }
            catch (Exception e) { MetaText = "Meta XR acoustics settings not readable (" + e.GetType().Name + ")"; }
            Plugin.Log.LogInfo("[Diagnostics] " + MetaText);
            KlangLog.Text(MetaText);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string MetaLesen()
        {
            var s = MetaXRAcousticSettings.Instance;
            if (s == null) return "Meta XR acoustics: no settings object";
            int geo = UnityEngine.Object.FindObjectsOfType<MetaXRAcousticGeometry>().Length;
            int karten = UnityEngine.Object.FindObjectsOfType<MetaXRAcousticMap>().Length;
            return $"Meta XR acoustics (read only, not changed): acoustic model = {s.AcousticModel}, diffraction enabled = {s.DiffractionEnabled}; " +
                   $"{geo} acoustic geometry objects, {karten} acoustic maps in the scene. With diffraction on, Meta XR also bends sound around its baked geometry " +
                   "(on top of the game's occlusion volume) - to be compared at a corner with the sound log.";
        }
    }
}
