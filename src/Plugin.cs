using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Audio.SpatialSystem;
using Audio.SpatialSystem.SpatialAudioCalculator;
using Audio.SpatialSystem.SpatialAudioCalculator.Data;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT.Ballistics;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Materialklang
{
    // Materialgruppen fuer das F12-Menue (39 MaterialType-Werte -> 13 Gruppen).
    public enum Gruppe
    {
        ConcreteStone, Soil, GravelSnow, MetalThick, MetalThin, WoodThick, WoodThin,
        Glass, Soft, GridFenceGrass, Body, Water, Unknown, Door,
        Snow   // ab 0.6.0 eigene Gruppe (vorher bei Kies); steht am Ende, damit die Nummern der F12-Abschnitte gleich bleiben
    }

    [BepInPlugin("kaan.materialklang", "True Acoustics – Echo & Matter", Plugin.Version)]
    [BepInDependency("me.sol.sain", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("red.soundenginebackport", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Version = "0.8.9";
        internal static ManualLogSource Log;
        internal static Plugin Instanz;   // 0.8.4: fuer Coroutinen (Kartenexport)
        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<bool> Diagnose;
        internal static ConfigEntry<float> Gesamtstaerke;
        internal static ConfigEntry<float> FreiwegSchwelle;
        internal const int AnzahlGruppen = 15;
        internal static readonly ConfigEntry<float>[] Dicke = new ConfigEntry<float>[AnzahlGruppen];
        internal static readonly ConfigEntry<float>[] Oberflaeche = new ConfigEntry<float>[AnzahlGruppen];

        // Startwerte: Daempfung je Meter Dicke (K) und fester Anteil je Durchgang (S).
        // Ergebnis je Strahl: Restenergie = exp(-(S + K*Dicke) * Gesamtstaerke). Vanilla: exp(-0.3*Dicke).
        internal static readonly (Gruppe g, string name, float k, float s)[] Startwerte =
        {
            (Gruppe.ConcreteStone,     "Concrete, Stone, Tile, Asphalt",      5.0f, 0.30f),
            (Gruppe.Soil,           "Soil, Mud, Terrain",             3.0f, 0.20f),
            (Gruppe.GravelSnow,     "Gravel, Pebbles",                    2.0f, 0.10f),
            (Gruppe.MetalThick,     "Thick Metal (Container, Steel Door)",1.0f, 0.80f),
            (Gruppe.MetalThin,    "Thin Metal (Car, Corrugated Sheet)",    0.8f, 0.35f),
            (Gruppe.WoodThick,       "Thick Wood (Wall, Beam)",          2.0f, 0.25f),
            (Gruppe.WoodThin,      "Thin Wood (Board, Crate)",         1.2f, 0.12f),
            (Gruppe.Glass,           "Glass",                                1.0f, 0.20f),
            (Gruppe.Soft,          "Soft (Cardboard, Fabric, Plastic, Rubber)",0.8f, 0.08f),
            (Gruppe.GridFenceGrass, "Grate, Chain-link Fence, Grass",          0.05f,0.01f),
            (Gruppe.Body,        "Body, Armor, Helmet",             1.5f, 0.10f),
            (Gruppe.Water,         "Water",                              1.0f, 0.10f),
            (Gruppe.Unknown,      "Unknown (like vanilla)",             0.3f, 0.00f),
            (Gruppe.Door,           "Doors (game object, base value)",     0.0f, 0.60f),
            (Gruppe.Snow,           "Snow",                                2.0f, 0.10f),   // Schieberwerte wie bisher bei Kies/Schnee
        };

        void Awake()
        {
            Log = Logger;
            Instanz = this;
            Einstellungen.Anlegen(Config);
            if (Einstellungen.Migriert > 0)
                Log.LogInfo($"Settings: {Einstellungen.Migriert} values carried over from older versions (0.4.0 German names / 0.5.0 names).");
            LiveStart.Awake(Config);   // Klang-Log + Live-Einstellung (aus, solange sie nicht in F12 eingeschaltet sind)
            SceneManager.activeSceneChanged += (_, __) =>
            {
                // Gesamtreview C1: jeder Teil fuer sich
                Sicher("scene: MaterialCache", MaterialCache.Leeren); Sicher("scene: BotHoeren", BotHoeren.Leeren); Sicher("scene: HallZonen", HallZonen.SzeneGewechselt);
                Sicher("scene: TuerenFenster", TuerenFenster.Leeren); Sicher("scene: WandZwischenRaeumen", WandZwischenRaeumen.Leeren); Sicher("scene: KlangLog", KlangLog.SzeneGewechselt);
                Sicher("scene: Fernschuss", Fernschuss.SzeneGewechselt); Sicher("scene: Umgebung", Umgebung.SzeneGewechselt); Sicher("scene: LautSignal", LautSignal.SzeneGewechselt);
                Sicher("scene: WandDaempfung", WandDaempfung.SzeneGewechselt); Sicher("scene: Kalibrierung", Kalibrierung.SzeneGewechselt);
                Sicher("scene: Richtung", Richtung.SzeneGewechselt); Sicher("scene: Ersatzweg", Raumweg.Ersatzweg.SzeneGewechselt);   // 0.8.8
                Sicher("scene: Wegsuche", Wegsuche.SzeneGewechselt);   // 0.8.8 Abschnitt 19
                Sicher("scene: Testquelle", Testquelle.SzeneGewechselt);   // testquelle (12.11.): Abschnitt 20
                Sicher("scene: MetaVerdeckung", MetaVerdeckung.SzeneGewechselt);   // metaschalter (17.11.): Abschnitt 22
            };
            var h = new Harmony("kaan.materialklang");
            // Gesamtreview C1: jeder Teil fuer sich - ein Fehler in einem Anwenden bricht die folgenden nicht mehr ab
            Sicher("Harmony.PatchAll", () => h.PatchAll(typeof(Plugin).Assembly));
            Sicher("CullingFix", () => CullingFix.Anwenden(h));   // 0.8.8: AudioSourceCulling reihum + Audio-Reset (1. General)
            Sicher("BotHoeren", () => BotHoeren.Anwenden(h));
            Sicher("TuerenFenster", () => TuerenFenster.Anwenden(h));
            Sicher("WandZwischenRaeumen", () => WandZwischenRaeumen.Anwenden(h));
            Sicher("SebAkustik", () => SebAkustik.Anwenden(h));
            Sicher("Fernschuss", () => Fernschuss.Anwenden(h));   // 0.8.4: 13. Distance (SEB)
            Sicher("SchussV1", () => SchussV1.Anwenden(h));       // v1waffen (12.11.): SEB-V1-Waffen (nach SebAkustik: braucht dessen Okkluder-Kontext)
            Sicher("Umgebung", () => Umgebung.Anwenden(h));     // 0.8.5: 15. Ambient emitters
            Sicher("Raumton", () => Raumton.Anwenden(h));      // 0.8.5: 15. Ambient emitters / Indoor room tone volume (%)
            Sicher("LautSignal", () => LautSignal.Anwenden(h));   // 0.8.5: 16. Loud signals
            Sicher("FesteQuellen", () => FesteQuellen.Anwenden(h)); // 0.8.5: 17. All fixed sources (rohe Szenenquellen, Takt ferner Quellen)
            Sicher("SkriptQuellen", () => SkriptQuellen.Anwenden(h)); // 0.8.5: 18. Scripted sources (Lok, Airdrop, BTR-Turm/-Horn, Fackeln, Moerser-Pfeifen, Tueren/Tore)
            Sicher("Physik", () => Physik.Anwenden(h));       // ruft auch Wege.Anwenden (0.8.4 Stufe 1, Abschnitt 14)
            Sicher("FilterGleiten", () => FilterGleiten.Anwenden(h));
            Sicher("MultiQuelle", () => MultiQuelle.Anwenden(h));
            Sicher("QuellWirkung", () => QuellWirkung.Anwenden(h));
            Sicher("Wetter", () => Wetter.Anwenden(h));
            Sicher("KlangLog", () => KlangLog.Anwenden(h));
            Sicher("Testquelle", () => Testquelle.Anwenden(h, Config));   // testquelle (12.11.): 20. Test source (measurement)
            Sicher("SchussRaum", () => SchussRaum.Anwenden(h, Config));   // schusshrtf (12.11.): 21. Gunshot spatialization (experimental)
            Sicher("MetaVerdeckung", () => MetaVerdeckung.Anwenden(h, Config));   // metaschalter (17.11.): 22. Meta occlusion (experimental) + Meta-Diagnose
            Log.LogInfo($"Materialklang {Version} (beta) loaded.");
        }

        // Gesamtreview C1: Fehler eines Teils (Anwenden oder Tick) melden und weitermachen; je Teil einmal ausfuehrlich ins Log.
        static readonly HashSet<string> gemeldet = new HashSet<string>();
        static void Sicher(string teil, Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                Statistik.Fehler(e);
                if (gemeldet.Add(teil)) Log.LogError($"{teil}: {e.GetType().Name}: {e.Message} - this part is skipped, the rest keeps working.\n{e.StackTrace}");
            }
        }

        static readonly Action tWege = Wege.Tick, tHall = HallZonen.Tick, tKarte = KartenExport.Tick, tStat = Statistik.VielleichtLoggen, tLive = LiveStart.Tick,
            tWetter = Wetter.Tick, tWald = Wald.Tick, tUmg = Umgebung.Tick, tLaut = LautSignal.Tick, tFest = FesteQuellen.Tick, tSkript = SkriptQuellen.Tick,
            tWand = WandDaempfung.Tick, tWegsuche = Wegsuche.Tick, tFremd = FremdPegel.Tick, tTest = Testquelle.Tick, tMeta = MetaVerdeckung.Tick;

        void Update()
        {
            Sicher("Wege.Tick", tWege); Sicher("HallZonen.Tick", tHall); Sicher("KartenExport.Tick", tKarte); Sicher("Statistik", tStat);
            Sicher("LiveStart.Tick", tLive); Sicher("Wetter.Tick", tWetter); Sicher("Wald.Tick", tWald); Sicher("Umgebung.Tick", tUmg);
            Sicher("LautSignal.Tick", tLaut); Sicher("FesteQuellen.Tick", tFest); Sicher("SkriptQuellen.Tick", tSkript); Sicher("WandDaempfung.Tick", tWand);
            Sicher("Wegsuche.Tick", tWegsuche);   // 0.8.8 Abschnitt 19
            Sicher("FremdPegel.Tick", tFremd);   // galpegel (10.11.): Fremdverstaerkung je Klanggruppe (Guns Are Loud)
            Sicher("Testquelle.Tick", tTest);   // testquelle (12.11.)
            Sicher("MetaVerdeckung.Tick", tMeta);   // metaschalter (17.11.)
        }

        void OnApplicationQuit() { KlangLog.Beenden(); WandDaempfung.Beenden(); Wegsuche.Beenden(null); }
        void OnDestroy() { KlangLog.Beenden(); WandDaempfung.Beenden(); Wegsuche.Beenden(null); }
    }

    internal static class MaterialCache
    {
        static readonly Dictionary<int, byte> Karte = new Dictionary<int, byte>(4096);

        public static void Leeren() => Karte.Clear();

        public static int Get(int colliderId)
        {
            if (Karte.TryGetValue(colliderId, out byte m)) return m;
            int r = Bestimmen(Resources.InstanceIDToObject(colliderId) as Collider, true);
            Karte[colliderId] = (byte)r;
            return r;
        }

        /// <summary>Gruppe eines Colliders (ohne Zwischenspeicher). statistik = in die Diagnose zaehlen (nur fuer echte Strahltreffer;
        /// der Kartenexport, 0.8.4, nutzt dieselbe Zuordnung ohne Zaehlen).</summary>
        internal static int Bestimmen(Collider col, bool statistik)
        {
            int r = (int)Gruppe.Unknown;
            if (col is TerrainCollider) r = (int)Gruppe.Soil;
            else if (col != null && TuerenFenster.IstTuer(col)) r = (int)Gruppe.Door;
            else if (col != null)
            {
                BaseBallistic bb;
                if (!col.TryGetComponent(out bb) && col.transform.parent != null)
                    col.transform.parent.TryGetComponent(out bb);
                if (bb is BallisticCollider bc) r = (int)Zuordnen(bc.TypeOfMaterial);
                else if (bb is TerrainBallistic) r = (int)Gruppe.Soil;
                if (statistik) Statistik.MaterialGesehen(bb is BallisticCollider bc2 ? bc2.TypeOfMaterial.ToString() : "no material", col.gameObject.layer);
            }
            return r;
        }

        internal static Gruppe Zuordnen(MaterialType t)
        {
            switch (t)
            {
                case MaterialType.Concrete: case MaterialType.Stone: case MaterialType.Tile:
                case MaterialType.Asphalt: case MaterialType.GenericHard:
                    return Gruppe.ConcreteStone;
                case MaterialType.Soil: case MaterialType.SoilForest: case MaterialType.Mud: case MaterialType.Swamp:
                    return Gruppe.Soil;
                case MaterialType.Gravel: case MaterialType.Pebbles:
                    return Gruppe.GravelSnow;
                case MaterialType.Snow:
                    return Gruppe.Snow;
                case MaterialType.MetalThick: case MaterialType.MetalNoDecal: case MaterialType.GarbageMetal:
                    return Gruppe.MetalThick;
                case MaterialType.MetalThin:
                    return Gruppe.MetalThin;
                case MaterialType.WoodThick:
                    return Gruppe.WoodThick;
                case MaterialType.WoodThin:
                    return Gruppe.WoodThin;
                case MaterialType.Glass: case MaterialType.GlassVisor: case MaterialType.GlassShattered:
                    return Gruppe.Glass;
                case MaterialType.Cardboard: case MaterialType.GarbagePaper: case MaterialType.Fabric:
                case MaterialType.GenericSoft: case MaterialType.Plastic: case MaterialType.Rubber: case MaterialType.Tyre:
                    return Gruppe.Soft;
                case MaterialType.Chainfence: case MaterialType.Grate: case MaterialType.GrassHigh: case MaterialType.GrassLow:
                    return Gruppe.GridFenceGrass;
                case MaterialType.Body: case MaterialType.BodyArmor: case MaterialType.Helmet: case MaterialType.HelmetRicochet:
                    return Gruppe.Body;
                case MaterialType.Water: case MaterialType.WaterPuddle:
                    return Gruppe.Water;
                default:
                    return Gruppe.Unknown;
            }
        }
    }

    // Nach dem Abschluss von Strahlen + Burst-Job ist alles im Hauptthread lesbar.
    // Wir rechnen das Ergebnis aus denselben Treffern neu, nur mit Materialwerten statt eines globalen Werts.
    [HarmonyPatch(typeof(TransmissionCalculator), nameof(TransmissionCalculator.CompleteCurrentCalculation))]
    internal static class TransmissionPatch
    {
        static readonly float[] Baender = new float[3];

        static void Prefix(TransmissionCalculator __instance, out bool __state)
        {
            __state = __instance._buffersAllocated && __instance._isCalculationInProgress
                      && __instance._currentStep == TransmissionCalculator.ECalculationStep.Complete;
        }

        static void Postfix(TransmissionCalculator __instance, bool __state)
        {
            if (!__state || !Plugin.Aktiv.Value) return;
            try
            {
                Statistik.MaskeMerken(__instance._queryParams.layerMask);
                int n = __instance._dynamicTotalRayCount;
                var hits = __instance._allHits;
                if (n <= 0 || !hits.IsCreated || hits.Length < 2 * n || !__instance._resultBuffer.IsCreated) return;

                float minT = __instance.float_7, maxT = __instance.float_8, maxD = __instance._maxDistance;
                float staerke = Plugin.Gesamtstaerke.Value;
                bool physik = Physik.An;
                float sumE = 0f, sumET = 0f;
                // 0.8.4 Stufe 1: im Physik-Modus je Band (Bezug/1k/4k), mit Flankenwegen und Empfangsraum (Wege.cs)
                float sumE1 = 0f, sumE2 = 0f, sumW0 = 0f, sumW1 = 0f, sumW2 = 0f, sumWD = 0f, sumWD1 = 0f, sumWD2 = 0f; int frei = 0, wand = 0;   // sumWD: diffus-hall, sumWD1/2: hallsend (B2)
                int einseitig = 0, zweiObjekte = 0, zuDick = 0;   // Gesamtreview: Strahlen ohne Durchgang (Vanilla NoPath)
                var f = Wege.Frequenzen();
                if (physik) Wege.TBeginn(__instance._inputListenerPos, __instance._inputSourcePos);
                float raumLog = float.NaN, flankeLog = float.NaN;
                bool gueltig = false;
                bool farbe = Farbe.Aktiv;
                var fs = new FarbeSumme();
                TransInfo ti = KlangLog.An ? KlangLog.TransHole(__instance) : null;   // nur bei eingeschaltetem Klang-Log
                if (ti != null) ti.Beginn(n);

                for (int i = 0; i < n; i++)
                {
                    RaycastHit a = hits[i], b = hits[i + n];
                    int ia = a.colliderInstanceID, ib = b.colliderInstanceID;
                    float dA = 0f, dA1 = 0f, dA2 = 0f;   // diffus-hall: Diffusanteil dieses Wandstuecks (Wege.LetzteDiffus), hallsend: je Band
                    bool ha = ia != 0 && a.distance < maxD, hb = ib != 0 && b.distance < maxD;
                    float e, th;
                    float e1 = 1f, e2 = 1f;
                    if (!ha && !hb) { e = 1f; th = 0f; if (farbe) fs.Frei(); if (ti != null) ti.FreierStrahl(); }
                    else if (ha != hb || ia != ib)                    // wie Vanilla: kein Durchgang
                    {
                        if (ha != hb) einseitig++; else zweiObjekte++;
                        if (ti != null) ti.KeinDurchgang(ha != hb ? 0 : 1);
                        continue;
                    }
                    else
                    {
                        th = Vector3.Distance(a.point, b.point);
                        if (th >= maxT) { zuDick++; if (ti != null) ti.KeinDurchgang(2); continue; }   // wie Vanilla: zu dick
                        int m = MaterialCache.Get(ia);
                        float x = Plugin.Oberflaeche[m].Value + (th >= minT ? Plugin.Dicke[m].Value * th : 0f);
                        float dbp;
                        if (physik)
                        {
                            var band = Baender;
                            // 10.11. (akustik-wand, Befund 2): Massengesetz mit der Dicke SENKRECHT zur Flaeche (Normale am Eintritt), nicht
                            // mit der Strahllaenge zwischen den beiden Treffern (Untergrenze 0,05 wie die Wegsuche, WandLinie.Senkrecht)
                            Vector3 strahl = b.point - a.point;
                            float thS = th * WandLinie.Senkrecht(th > 1e-4f ? Mathf.Abs(Vector3.Dot(a.normal, strahl / th)) : 1f);
                            // 0.8.8: Tuer-Collider = Blatt der Tuer (Material + gemessene Blattdicke), wie das Portal
                            for (int k = 0; k < 3; k++) band[k] = Physik.DaemmungStueck(ia, m, thS, (float)f[k], false);
                            // r1 = Quelle -> Wand (Strahl von der Quelle), r2 = Hoerer -> Wand (Strahl vom Hoerer)
                            Wege.TStueck(m, thS, Physik.MasseStueck(ia, m, thS), b.distance, a.distance, band);
                            dbp = band[0];
                            e1 = Physik.EnergieAusDb(band[1]); e2 = Physik.EnergieAusDb(band[2]);
                            if (!float.IsNaN(Wege.LetzteRaumDb)) raumLog = Wege.LetzteRaumDb;
                            if (!float.IsNaN(Wege.LetzteFlankeDb)) flankeLog = Wege.LetzteFlankeDb;
                            dA = Wege.LetzteDiffus; dA1 = Wege.LetzteDiffusB[1]; dA2 = Wege.LetzteDiffusB[2];
                        }
                        else dbp = x * staerke * 4.3429f;   // dB dieses Stuecks
                        e = physik ? Physik.EnergieAusDb(dbp) : Mathf.Clamp01(Mathf.Exp(-x * staerke));
                        if (farbe) fs.Add(m, x * staerke);
                        Statistik.Treffer(m);
                        if (ti != null) ti.Treffer(ia, m, th, dbp);
                    }
                    gueltig = true;
                    sumE += e;
                    sumET += e * th;
                    sumE1 += e1; sumE2 += e2;
                    if (th <= 0f && !ha && !hb) frei++;
                    else { wand++; sumW0 += e; sumW1 += e1; sumW2 += e2; sumWD += e * dA; sumWD1 += e1 * dA1; sumWD2 += e2 * dA2; }
                }
                if (physik)
                {
                    var tb = Wege.THole(__instance);
                    float dyn = Physik.Dynamik.Value;
                    tb.Gueltig = gueltig || sumE > 0f;
                    // Gesamtreview: kein Strahl mit Ergebnis (nur einseitige Treffer / zwei Objekte) -> Daemmung unbekannt, nicht 60 dB
                    tb.Unbestimmt = WegeKern.TOhneErgebnis(frei, n - frei - einseitig - zweiObjekte, einseitig, zweiObjekte);
                    // 10.11. (klangfarbe): Deckel nur auf dem Bezugsband, die Hochbaender behalten ihren echten Abstand (WegeKern.Deckeln) -
                    // je Band gekappt war eine dicke Wand in allen Baendern 60 dB und der Tiefpass (Wege.BandKurve) offen
                    double t0 = Physik.DbAusEnergie(sumE / n), t1 = Physik.DbAusEnergie(sumE1 / n), t2 = Physik.DbAusEnergie(sumE2 / n);
                    WegeKern.Deckeln(ref t0, ref t1, ref t2, dyn);
                    tb.Db[0] = (float)t0; tb.Db[1] = (float)t1; tb.Db[2] = (float)t2;
                    tb.Frei = (float)frei / n;
                    tb.DbWand[0] = wand > 0 && sumW0 > 0f ? Physik.DbAusEnergie(sumW0 / n) : 1000f;
                    tb.DbWand[1] = wand > 0 && sumW1 > 0f ? Physik.DbAusEnergie(sumW1 / n) : 1000f;
                    tb.DbWand[2] = wand > 0 && sumW2 > 0f ? Physik.DbAusEnergie(sumW2 / n) : 1000f;
                    tb.RaumDb = raumLog; tb.FlankeDb = flankeLog; tb.Raum = Wege.TRaumName;
                    tb.Diffus = sumW0 > 0f ? Mathf.Clamp01(sumWD / sumW0) : 0f;   // diffus-hall: energiegewichtet ueber die Wandstrahlen
                    tb.Diffus1 = sumW1 > 0f ? Mathf.Clamp01(sumWD1 / sumW1) : tb.Diffus;   // hallsend (B2): je Band mit der Energie dieses Bands
                    tb.Diffus2 = sumW2 > 0f ? Mathf.Clamp01(sumWD2 / sumW2) : tb.Diffus;
                    Wege.TStrahlenZaehlen = 2 * n;
                }

                var alt = __instance._resultBuffer[0];
                var neu = new TransmissionPathResult
                {
                    RemainingEnergy = physik ? 1f - Physik.WertungAusEnergie(sumE / n) : Mathf.Clamp01(sumE / n),
                    TotalThickness = sumE > 0f ? sumET / sumE : float.MaxValue,
                    PathPotentiallyValid = gueltig
                };
                __instance._resultBuffer[0] = neu;
                if (farbe) Farbe.Setze(__instance, fs, gueltig);
                Statistik.Rechnung(alt.RemainingEnergy, neu.RemainingEnergy);
            }
            catch (Exception ex)
            {
                Statistik.Fehler(ex);
            }
        }
    }

    // Optional: Schwelle, ab der ein Weg als "frei" gilt, im Client ueberschreiben.
    // Seit 0.6.0 live: die Rechner werden (schwach) mitgefuehrt und bei jeder Aenderung der Einstellung neu gesetzt.
    [HarmonyPatch]
    internal static class FreiwegPatch
    {
        sealed class Merk { public float Orig, OrigT = 0.55f, OrigD = 0.5f; }
        static readonly ConditionalWeakTable<SpatialAudioOcclusionCombinedCalculator, Merk> Original =
            new ConditionalWeakTable<SpatialAudioOcclusionCombinedCalculator, Merk>();
        static readonly List<WeakReference<SpatialAudioOcclusionCombinedCalculator>> Alle =
            new List<WeakReference<SpatialAudioOcclusionCombinedCalculator>>();

        static IEnumerable<MethodBase> TargetMethods() =>
            AccessTools.GetDeclaredConstructors(typeof(SpatialAudioOcclusionCombinedCalculator)).Cast<MethodBase>();

        static void Postfix(SpatialAudioOcclusionCombinedCalculator __instance)
        {
            if (!Original.TryGetValue(__instance, out _))
            {
                Original.Add(__instance, new Merk { Orig = __instance._minClearPathScore, OrigT = __instance._transmissionThreshold, OrigD = __instance._diffractionThreshold });
                Alle.Add(new WeakReference<SpatialAudioOcclusionCombinedCalculator>(__instance));
            }
            Setzen(__instance);
        }

        static void Setzen(SpatialAudioOcclusionCombinedCalculator c)
        {
            float v = Plugin.FreiwegSchwelle.Value;
            float orig = Original.TryGetValue(c, out var m) ? m.Orig : 0.5f;
            // Gesamtreview C4: Hauptschalter aus = Vanilla-Schwelle
            c._minClearPathScore = !Plugin.Aktiv.Value ? orig : (Physik.An) ? Physik.Freiweg.Value : (v >= 0f ? v : orig);
            // 0.8.4 Stufe 1: R fuer jeden verdeckten Klang (Spiel: nur bei T > 0,55 und D > 0,5) - sonst kaeme R mit der Energiesumme
            // ploetzlich dazu. T > Freiweg (verdeckt), D > 0 (keine Sichtlinie).
            if (Original.TryGetValue(c, out var mm))
            {
                bool immer = Wege.RImmerAn;
                c._transmissionThreshold = immer ? c._minClearPathScore : mm.OrigT;
                c._diffractionThreshold = immer ? 0f : mm.OrigD;
            }
        }

        /// <summary>0.8.4 Stufe 1: Schwellen des Spiels fuer R (fuer die Kostenzaehlung "nur wegen der gesenkten Schwelle").</summary>
        internal static void Originale(SpatialAudioOcclusionCombinedCalculator c, out float t, out float d)
        {
            if (Original.TryGetValue(c, out var m)) { t = m.OrigT; d = m.OrigD; }
            else { t = 0.55f; d = 0.5f; }
        }

        // nach einer Aenderung der Schwellen: alle lebenden Rechner neu setzen
        internal static void AlleNeu()
        {
            for (int i = Alle.Count - 1; i >= 0; i--)
            {
                if (Alle[i].TryGetTarget(out var c)) Setzen(c);
                else Alle.RemoveAt(i);
            }
        }
    }

    internal static class Statistik
    {
        static float naechsterLog;
        static int rechnungen, gedaempft, fehler, fehlerGemeldet;
        static double summeAlt, summeNeu;
        static readonly int[] treffer = new int[Plugin.AnzahlGruppen];
        static readonly Dictionary<string, int> materialien = new Dictionary<string, int>();
        static readonly Dictionary<int, int> layer = new Dictionary<int, int>();
        static int maske = -1;
        internal static int AudioMaske;   // Layer-Maske der Audio-Strahlen (zuletzt gesehen)

        public static void MaskeMerken(int m)
        {
            AudioMaske = m;
            if (m == maske || !Plugin.Diagnose.Value) return;
            maske = m;
            var namen = Enumerable.Range(0, 32).Where(i => (m & (1 << i)) != 0)
                .Select(i => $"{i}:{LayerMask.LayerToName(i)}");
            Plugin.Log.LogInfo($"[Diagnostics] Audio rays hit layers: {string.Join(", ", namen)}");
        }

        public static void MaterialGesehen(string mat, int lay)
        {
            materialien[mat] = materialien.TryGetValue(mat, out int c) ? c + 1 : 1;
            layer[lay] = layer.TryGetValue(lay, out int d) ? d + 1 : 1;
        }

        public static void Treffer(int g) => treffer[g]++;

        public static void Rechnung(float alt, float neu)
        {
            rechnungen++;
            summeAlt += alt;
            summeNeu += neu;
            if (neu < 0.5f) gedaempft++;
        }

        public static void Fehler(Exception ex)
        {
            if (fehler++ < 3) Plugin.Log.LogError($"Materialklang: {ex}");
        }

        // 0.8.3: nur die ersten 3 Fehler stehen ausfuehrlich im Log; danach alle 30 s die Anzahl (auch ohne "Diagnostic log").
        static void FehlerMelden()
        {
            if (fehler == fehlerGemeldet) return;
            Plugin.Log.LogWarning($"Materialklang: {fehler - fehlerGemeldet} errors in the last 30 s ({fehler} since start; the first 3 are logged in full above).");
            fehlerGemeldet = fehler;
        }

        public static void VielleichtLoggen()
        {
            if (Time.realtimeSinceStartup < naechsterLog) return;
            naechsterLog = Time.realtimeSinceStartup + 30f;
            FehlerMelden();
            if (!Plugin.Diagnose.Value) return;
            SebAkustik.Loggen();
            Fernschuss.Loggen();
            SchussV1.Loggen();   // v1waffen (12.11.)
            Wald.Loggen();
            Physik.Loggen();
            MultiQuelle.Loggen();
            QuellWirkung.Loggen();
            TuerenFenster.DiagnoseLoggen();
            WandZwischenRaeumen.DiagnoseLoggen();
            WandDaempfung.Loggen();
            Kalibrierung.Loggen();   // 0.8.8
            FremdPegel.Loggen();     // galpegel (10.11.)
            if (rechnungen == 0) return;
            var tr = string.Join(", ", Enumerable.Range(0, Plugin.AnzahlGruppen).Where(i => treffer[i] > 0)
                .Select(i => $"{(Gruppe)i}={treffer[i]}"));
            var mats = string.Join(", ", materialien.OrderByDescending(p => p.Value).Take(12).Select(p => $"{p.Key}={p.Value}"));
            var lays = string.Join(", ", layer.OrderByDescending(p => p.Value).Select(p => $"{LayerMask.LayerToName(p.Key)}={p.Value}"));
            Plugin.Log.LogInfo($"[Diagnostics] {rechnungen} calculations, remaining energy vanilla {summeAlt / rechnungen:F2} -> new {summeNeu / rechnungen:F2}, " +
                               $"attenuated {gedaempft}. Ray hits: {tr}");
            Plugin.Log.LogInfo($"[Diagnostics] New colliders: {mats} | layers: {lays}");
            if (HallZonen.ZonenAnzahl > 0)
                Plugin.Log.LogInfo($"[Diagnostics] Reverb: {HallZonen.ZonenAnzahl} zones active.");
            if (BotHoeren.Ersetzt + BotHoeren.Fallback > 0)
                Plugin.Log.LogInfo($"[Diagnostics] Bots: {BotHoeren.Ersetzt} values replaced, {BotHoeren.CacheTreffer} from cache, {BotHoeren.Fallback} SAIN value (budget), {BotHoeren.Strahlen} rays.");
            BotHoeren.Ersetzt = BotHoeren.Fallback = BotHoeren.CacheTreffer = BotHoeren.Strahlen = 0;
            rechnungen = gedaempft = 0; summeAlt = summeNeu = 0;
            Array.Clear(treffer, 0, treffer.Length);
            materialien.Clear(); layer.Clear();
        }
    }
}
