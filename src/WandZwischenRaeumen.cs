using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using HarmonyLib;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Materialklang
{
    // Vanilla: sind Quelle und Hoerer in verschiedenen Audio-Raeumen, entscheiden nur die vorberechneten
    // Portal-Wege (Tueren/Fenster) - die Wand dazwischen zaehlt nicht. Hier wird zusaetzlich die gerade Linie
    // Quelle -> Ohr durch die Waende gemessen (5 Linien, jede in beide Richtungen) und das Ergebnis genommen,
    // das weniger daempft: dort, wo der Schall also wirklich "durch die Wand" leichter durchkommt als ueber die Tuer.
    // Die Strahlen laufen asynchron (RaycastCommand-Batch); nie wird auf einen unfertigen Auftrag gewartet.
    internal static class WandZwischenRaeumen
    {
        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<bool> BlattRegel;   // tordoppelt (12.11.): Portal-Blatt nur im Raumweg (WandKetteKern.NurBlatt)
        internal static ConfigEntry<float> MaxDist, Zuschlag, Schale;

        const int Linien = 5, Strahlen = Linien * 2, MaxTreffer = 8;
        // lampenwand (17.11.): MinWert ist nur die technische Untergrenze "Propagation aktiv" (Effekt > 0, sonst schaltet das Spiel auf
        // Hindernis) - dieselbe wie Raumweg.MinWertung (0,005 = 0,3 dB). Bis 17.11. 0,02 = 1,2 dB: jede Quelle mit Wandmessung in einem
        // anderen Raum bekam mindestens 1,2 dB, ohne Wandmessung 0,3 dB (Log 2026-11-17_20-06-44: 8221 Zeilen "insulation 1.2 dB", alle mit
        // "wall=..", 6196 mit "wall=n/a" bei 0,3 dB). Physikalisch begruendet ist keine der beiden; der Sprung 0,9 dB ist weg.
        const float Kreis = 0.25f, NeuBewegung = 0.3f, NeuAlter = 2f, MinDist = 0.5f, MinWert = 0.005f;
        const int MaxStartsProBild = 8;
        static readonly float[] Gewicht = { 0.4f, 0.15f, 0.15f, 0.15f, 0.15f };

        sealed class WandWeg
        {
            public WeakReference<ParallelSoundPropagationCalculator> Besitzer;
            public NativeArray<RaycastCommand> Befehle;
            public NativeArray<RaycastHit> Treffer;
            public JobHandle Handle;
            public bool Laeuft;
            public bool Hat;               // es gibt eine Messung (gestartet) zum Vergleichen
            public float Score = -1f;      // Wanddaempfung 0..1, -1 = keine Aussage (Vanilla gilt)
            public readonly float[] Db = new float[WegeKern.N];   // 0.8.4 Stufe 1 (Physik-Modus): Daemmung je Band (Bezug/1k/4k)
            public float Echt0 = float.NaN; // 10.11. (klangfarbe): echte Daemmung des Bezugsbands (Db ist gedeckelt: Db[0] <= Max. insulation, Abstand echt)
            public float RaumDb = float.NaN, FlankeDb = float.NaN; // Korrektur Empfangsraum/Flanken im Bezugsband (Log)
            public string Kette;           // mehrwand: Kette getrennter Trennwaende der mittleren Linie (Log), null = eine Konstruktion
            public bool MitteFrei;         // mehrwand: die mittlere Linie hat kein Wandstueck (freie Linie, z. B. durch eine offene Tuer)
            // galpegel (10.11.): Diffusanteil des Wandwegs im Bezugsband (Hallfeld des Hoererraums / Wandweg gesamt, WegeKern.WandwegTeile
            // ueber Wege.Korrigieren, mittlere Linie wie RaumDb); 0 = ohne Raumkorrektur oder freie Linie (dann ist alles gerichtet).
            public float Diffus;
            public float Diffus1, Diffus2;  // hallsend (B2, 12.11.): dasselbe bei 1 kHz / 4 kHz (mittlere Linie)
            public bool Vorrat;             // hallsend (Fund 4): Score/Db/Diffus stammen vorlaeufig aus dem Messvorrat (WandVorrat), eigene Messung laeuft
            public readonly DiffusMerk Merk = new DiffusMerk();
            public Vector3 PA, PB;         // Positionen beim Messen
            public float Laenge, Zeit;
            public short RA = -2, RB = -2;
            public int Schmutz;
            // Klang-Log: Stuecke der mittleren Linie (nur gefuellt, solange das Log an ist)
            public int LogAnzahl;
            public readonly int[] LogGruppe = new int[LogMax];
            public readonly float[] LogDicke = new float[LogMax], LogDb = new float[LogMax];
            public readonly int[] LogId = new int[LogMax];
            public readonly int[] LogTeile = new int[LogMax];
        }
        const int LogMax = 6;

        /// <summary>galpegel (10.11.): zuletzt angewandte Aufteilung des Raumwegs mit Wand (fuer das Klang-Log).</summary>
        internal sealed class DiffusMerk
        {
            public double Anteil, Zusatz;
            public float Trocken = float.NaN, Send = float.NaN;
            // tunneldiffus (17.11.): Energieanteil des Raumwegs an der Summe Route + Wand (Bezugsband; die Zeitstruktur kennt nur die Route)
            public double Route = 1;
        }

        static readonly ConditionalWeakTable<ParallelSoundPropagationCalculator, WandWeg> Tabelle =
            new ConditionalWeakTable<ParallelSoundPropagationCalculator, WandWeg>();
        static readonly List<WandWeg> Alle = new List<WandWeg>();
        static readonly Dictionary<int, bool> Gelaende = new Dictionary<int, bool>();
        static readonly WandTreffer[] vorT = new WandTreffer[MaxTreffer], rueckT = new WandTreffer[MaxTreffer];
        static readonly WandStueck[] stuecke = new WandStueck[2 * MaxTreffer];
        static readonly float[] energie = new float[Linien], energie1 = new float[Linien], energie2 = new float[Linien];
        static readonly float[] bandSum = new float[WegeKern.N], bandTmp = new float[WegeKern.N];
        static double linieMasse;
        static float linieRaum, linieFlanke, linieDiffus, linieDiffus1, linieDiffus2;
        static string linieKette;
        static int linieTeile;
        // mehrwand: getrennte Konstruktionen der Linie (WandKetteKern.Getrennt), je Block Daemmung je Band und Flaechenmasse
        static readonly KettenBlock[] bloecke = new KettenBlock[4 * MaxTreffer];
        static readonly WandLage[] lagen = new WandLage[4 * MaxTreffer];
        static readonly WandSchicht[] schichten = new WandSchicht[4 * MaxTreffer];
        static readonly float[] dichteG = new float[Plugin.AnzahlGruppen], einzelG = new float[Plugin.AnzahlGruppen], massivG = new float[Plugin.AnzahlGruppen];
        static readonly bool[] schaleG = new bool[Plugin.AnzahlGruppen];
        static readonly Vector3[] versatz = new Vector3[Linien];
        static bool mitteNahA, mitteNahB;

        static int hauptThread, schmutz, budgetBild, budgetRest;
        static WandWeg logW; static int logLinie;
        // Diagnose
        static int gestartet, gemessen, vergleiche, wandGewann, gesperrt, voll, ohneBudget, lueckeDicht, endeVerdeckt, einzelZahl, ohneAussage;
        static int durchBlatt, mischBlatt;   // tordoppelt: Linien nur durch Portal-Blaetter (weggelassen) / Blatt + Wand (Blattflaeche)
        static double summeRoute, summeWand;

        /// <summary>Live-Aenderung: alle Wandmessungen gelten als veraltet und werden neu gestartet.</summary>
        internal static void Neu() { schmutz++; }

        /// <summary>Klang-Log: Wertung der Wand und die Stuecke der mittleren Linie (false, wenn es keine Messung gibt).</summary>
        internal static bool LogDaten(ParallelSoundPropagationCalculator c, LogEintrag e, Func<int, string> name)
        {
            if (c == null || !Tabelle.TryGetValue(c, out var w) || w.Score < 0f) return false;
            e.WertWand = w.Score;
            e.WandFrei = w.MitteFrei;   // mehrwand: "free line between rooms" statt "through wall" (keine Wandstuecke auf der mittleren Linie)
            e.WandAnzahl = 0;
            for (int i = 0; i < w.LogAnzahl && i < LogEintrag.MaxStuecke; i++)
            {
                var st = e.Wand[i];
                st.Gruppe = w.LogGruppe[i]; st.Dicke = w.LogDicke[i]; st.Db = w.LogDb[i]; st.Strahlen = 1; st.Teile = w.LogTeile[i]; st.Name = name != null ? name(w.LogId[i]) : null;
                e.WandAnzahl++;
            }
            return true;
        }

        internal static void Binden(ConfigFile c)
        {
            const string s = "5. Walls Between Rooms";
            hauptThread = Thread.CurrentThread.ManagedThreadId;
            Aktiv = c.Bind(s, "Enabled", true,
                "Sound between different rooms can also pass through the wall (vanilla: only via doors/windows). Off = vanilla.");
            MaxDist = c.Bind(s, "Max. distance (m)", 40f,
                new ConfigDescription("Sources farther away are not measured through the wall (the vanilla value applies).", new AcceptableValueRange<float>(5f, 100f)));
            Zuschlag = c.Bind(s, "Surcharge", 0.05f,
                new ConfigDescription("Added to the measured wall attenuation: residual attenuation even with a completely free line. Ignored while Physics Mode is on.", new AcceptableValueRange<float>(0f, 0.5f)));
            // tordoppelt (12.11.2026): Ursache und Regel in WandKetteKern.cs (Abschnitt tordoppelt)
            BlattRegel = c.Bind(s, "Door/window leaf: room path only", true,
                "Physics mode: the leaf of a door, gate or window that is an opening of the room path (via doors/windows) is counted there only - " +
                "a line that crosses from room to room only through such leaves is not counted a second time 'through the wall' (the wall next to " +
                "the leaf still counts). A leaf plus a real wall on the same line counts with the leaf area. Off = as before (the leaf counted twice). Applies live.");
            BlattRegel.SettingChanged += (_, __) => Neu();
            Schale = c.Bind(s, "Shell thickness (m)", 0.15f,
                new ConfigDescription("Assumed thickness of a single surface without a back face for solid groups (concrete/stone, soil, gravel, water, snow, unknown); thin groups (metal, wood, glass, soft, door) use their typical thickness (see README), never more than this. Wall pieces over 1.5 m count as two such shells.", new AcceptableValueRange<float>(0.02f, 1f)));
        }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var t = typeof(ParallelSoundPropagationCalculator);
                var update = AccessTools.DeclaredMethod(t, "OnUpdate");
                var spaet = AccessTools.DeclaredMethod(t, "OnLateUpdate");
                var score = AccessTools.DeclaredMethod(t, "GetScore");
                var reset = AccessTools.DeclaredMethod(t, "OnReset");
                var weg = AccessTools.DeclaredMethod(t, "OnDispose");
                var swap = AccessTools.DeclaredMethod(typeof(SpatialAudioDataContainer), "SwapPortalBuffers");
                if (update == null || spaet == null || score == null || reset == null || weg == null || swap == null)
                {
                    Plugin.Log.LogWarning("Walls between rooms: target in ParallelSoundPropagationCalculator not found (game update?) -> vanilla.");
                    return;
                }
                h.Patch(update, postfix: new HarmonyMethod(typeof(WandZwischenRaeumen), nameof(UpdatePostfix)));
                h.Patch(spaet, postfix: new HarmonyMethod(typeof(WandZwischenRaeumen), nameof(SpaetPostfix)));
                h.Patch(score, postfix: new HarmonyMethod(typeof(WandZwischenRaeumen), nameof(ScorePostfix)));
                h.Patch(reset, postfix: new HarmonyMethod(typeof(WandZwischenRaeumen), nameof(ResetPostfix)));
                h.Patch(weg, postfix: new HarmonyMethod(typeof(WandZwischenRaeumen), nameof(DisposePostfix)));
                h.Patch(swap, postfix: new HarmonyMethod(typeof(WandZwischenRaeumen), nameof(SwapPostfix)));
                Plugin.Log.LogInfo("Walls between rooms: hooked.");
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // Szenenwechsel: alle Auftraege abschliessen, Speicher freigeben, Zustand vergessen.
        internal static void Leeren()
        {
            try
            {
                Gelaende.Clear();
                for (int i = 0; i < vorrat.Length; i++) vorrat[i].Hat = false;   // hallsend (Fund 4): Messvorrat gehoert zur Szene
                for (int i = Alle.Count - 1; i >= 0; i--)
                {
                    var w = Alle[i];
                    Freigeben(w);
                    Zuruecksetzen(w);
                    if (!w.Besitzer.TryGetTarget(out _)) Alle.RemoveAt(i);
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        internal static void DiagnoseLoggen()
        {
            if (gestartet == 0 && vergleiche == 0) return;
            string verg = vergleiche > 0
                ? $"route via doors on average {summeRoute / vergleiche:F2}, wall {summeWand / vergleiche:F2}, wall won {wandGewann}x of {vergleiche}"
                : "no comparison yet";
            Plugin.Log.LogInfo($"[Diagnostics] Walls: {gestartet} measurements started, {gemessen} finished, {verg}; " +
                               $"lines: {voll} with an unseen gap (dropped), {lueckeDicht} with a gap but already at the cap (blocked), {gesperrt} through a hill (blocked), " +
                               $"{endeVerdeckt} offset lines starting inside geometry (dropped), {einzelZahl} single surfaces at group thickness; " +
                               $"{ohneAussage} measurements without any usable line (vanilla), {ohneBudget}x postponed due to the per-frame budget, " +
                               $"{durchBlatt} lines only through the leaf of a door/window portal (left to the room path), {mischBlatt} with a leaf and a wall (leaf area), " +
                               $"{vorratGenommen}x a still valid measurement of the same room pair used until the own one finished (first value of a new sound).");
            vorratGenommen = 0;
            gestartet = gemessen = vergleiche = wandGewann = gesperrt = voll = ohneBudget = lueckeDicht = endeVerdeckt = einzelZahl = ohneAussage = 0;
            durchBlatt = mischBlatt = 0;
            summeRoute = summeWand = 0;
        }

        // ---------------------------------------------------------------- Zustand

        static WandWeg Hole(ParallelSoundPropagationCalculator c)
        {
            return Tabelle.GetValue(c, k =>
            {
                var w = new WandWeg { Besitzer = new WeakReference<ParallelSoundPropagationCalculator>(k) };
                Alle.Add(w);
                return w;
            });
        }

        static void Zuruecksetzen(WandWeg w) { w.Score = -1f; w.Hat = false; w.Laeuft = false; w.Vorrat = false; }

        // Auftrag zu Ende laufen lassen (nur fuer Reset/Dispose/Szenenwechsel erlaubt) und Puffer freigeben.
        static void Freigeben(WandWeg w)
        {
            if (w.Laeuft) { w.Handle.Complete(); w.Laeuft = false; }
            if (w.Befehle.IsCreated) w.Befehle.Dispose();
            if (w.Treffer.IsCreated) w.Treffer.Dispose();
        }

        // ---------------------------------------------------------------- Harmony

        static void SwapPostfix() { schmutz++; }   // Portaldaten wurden getauscht (Tuer auf/zu, Fenster kaputt)

        static void UpdatePostfix(ParallelSoundPropagationCalculator __instance, AudioSourceContainer sourceContainer,
            Vector3 listenerPosition, bool forceUpdate)
        {
            if (!Plugin.Aktiv.Value || !Aktiv.Value) return;   // Gesamtreview A7: Hauptschalter aus = Vanilla
            try
            {
                var w = Hole(__instance);
                if (w.Laeuft)
                {
                    if (w.Handle.IsCompleted) Beenden(__instance, w);
                    if (w.Laeuft) return;
                }

                var sys = __instance._audioSystem;
                var lr = sys?.ListenerCurrentRoom;
                var qr = sourceContainer.CurrentAudioRoom;
                if (lr == null || qr == null) { Zuruecksetzen(w); return; }

                // gleicher Raum / gemeinsames Aussengebiet: Vanilla-Hinderniswerte (Propagation gar nicht aktiv)
                if (sys.IsSourceInListenerRoom(sourceContainer) || sys.IsSourceAndListenerInCommonOutdoor(qr))
                { w.Score = -1f; w.Hat = false; return; }

                Vector3 a = sourceContainer.CurrentPosition, b = listenerPosition;
                float dist2 = (b - a).sqrMagnitude, max = MaxDist.Value;
                if (dist2 > max * max || dist2 < MinDist * MinDist || dist2 > sourceContainer.SqrMaxDistance)
                { w.Score = -1f; w.Hat = false; return; }

                bool neu = !w.Hat || forceUpdate || w.Schmutz != schmutz || w.RA != qr.ID || w.RB != lr.ID
                           || (a - w.PA).sqrMagnitude > NeuBewegung * NeuBewegung
                           || (b - w.PB).sqrMagnitude > NeuBewegung * NeuBewegung
                           || Time.time - w.Zeit > NeuAlter;
                if (!neu) return;

                // hallsend (Fund 4): ohne gueltige eigene Messung bis zum Ende des neuen Auftrags die juengste noch gueltige Messung
                // desselben Raumpaars aus dem Vorrat (WandVorratKern) - sonst hoert ein neuer Klang zuerst nur die Route
                if (!EigeneGilt(w, a, b, qr.ID, lr.ID)) VorratHolen(w, a, b, qr.ID, lr.ID);

                if (Time.frameCount != budgetBild) { budgetBild = Time.frameCount; budgetRest = MaxStartsProBild; }
                if (budgetRest <= 0) { ohneBudget++; return; }
                budgetRest--;

                Starten(w, a, b, qr.ID, lr.ID);
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void SpaetPostfix(ParallelSoundPropagationCalculator __instance)
        {
            if (!Plugin.Aktiv.Value || !Aktiv.Value) return;   // Gesamtreview A7: Hauptschalter aus = Vanilla
            try
            {
                if (Tabelle.TryGetValue(__instance, out var w) && w.Laeuft && w.Handle.IsCompleted) Beenden(__instance, w);
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void ScorePostfix(ParallelSoundPropagationCalculator __instance, ref float __result)
        {
            if (__result <= 0f) return;                 // gleicher Raum -> nichts zu tun
            try
            {
                if (!Plugin.Aktiv.Value || !Aktiv.Value || !Tabelle.TryGetValue(__instance, out var w) || w.Score < 0f) return;
                // Untergrenze, damit "Propagation aktiv" (Effekt > 0) erhalten bleibt
                if (Wege.Summe)
                {
                    // 0.8.4 Stufe 1: Route (Tueren/Fenster) und Wand sind parallele Wege -> Energiesumme statt Minimum
                    float dyn = Physik.Dynamik.Value;
                    // 10.11. (klangfarbe): die ECHTEN Daemmungen summieren, dann deckeln (WertungAusEnergie). Mit den gedeckelten Werten
                    // ergaben Route 80 dB + Wand 90 dB zusammen 57 dB (zwei Wege zu je 60) statt 60 (gedeckelt).
                    float rDb = __result * dyn + Raumweg.UeberDeckel(__instance);
                    float wDb = float.IsNaN(w.Echt0) ? w.Score * dyn : Mathf.Max(w.Score * dyn, w.Echt0);
                    float e = Physik.EnergieAusDb(rDb) + Physik.EnergieAusDb(wDb);
                    __result = Mathf.Max(MinWert, Mathf.Min(Physik.WertungAusEnergie(e), Mathf.Min(__result, w.Score)));
                    Wege.SummeRaumweg++;
                }
                else __result = Mathf.Max(MinWert, Mathf.Min(__result, w.Score));
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void ResetPostfix(ParallelSoundPropagationCalculator __instance)
        {
            try
            {
                if (Thread.CurrentThread.ManagedThreadId != hauptThread) return;
                if (Tabelle.TryGetValue(__instance, out var w))
                {
                    if (w.Laeuft) { w.Handle.Complete(); }
                    Zuruecksetzen(w);
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void DisposePostfix(ParallelSoundPropagationCalculator __instance)
        {
            try
            {
                // Der Finalizer ruft Dispose aus einem Fremd-Thread: dort nichts anfassen, der Szenenwechsel raeumt auf.
                if (Thread.CurrentThread.ManagedThreadId != hauptThread) return;
                if (Tabelle.TryGetValue(__instance, out var w))
                {
                    Freigeben(w);
                    Zuruecksetzen(w);
                    Alle.Remove(w);
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // ---------------------------------------------------------------- Messen

        static void Starten(WandWeg w, Vector3 a, Vector3 b, short ra, short rb)
        {
            if (!w.Befehle.IsCreated)
            {
                w.Befehle = new NativeArray<RaycastCommand>(Strahlen, Allocator.Persistent);
                w.Treffer = new NativeArray<RaycastHit>(Strahlen * MaxTreffer, Allocator.Persistent);
            }
            Vector3 d = b - a;
            float len = d.magnitude;
            d /= len;
            Vector3 u = Vector3.Cross(d, Vector3.up);
            if (u.sqrMagnitude < 0.01f) u = Vector3.Cross(d, Vector3.right);
            u.Normalize();
            Vector3 v = Vector3.Cross(d, u);
            versatz[0] = Vector3.zero;
            versatz[1] = u * Kreis; versatz[2] = -u * Kreis;
            versatz[3] = v * Kreis; versatz[4] = -v * Kreis;

            int maske = (int)LayersMaskController.HighPolyWithTerrainMask | Statistik.AudioMaske;
            var qp = new QueryParameters(maske, true, QueryTriggerInteraction.Ignore, true);
            for (int k = 0; k < Linien; k++)
            {
                w.Befehle[2 * k] = new RaycastCommand(a + versatz[k], d, qp, len);
                w.Befehle[2 * k + 1] = new RaycastCommand(b + versatz[k], -d, qp, len);
            }
            w.Handle = RaycastCommand.ScheduleBatch(w.Befehle, w.Treffer, 1, MaxTreffer);
            JobHandle.ScheduleBatchedJobs();
            w.Laeuft = true; w.Hat = true;
            w.PA = a; w.PB = b; w.Laenge = len; w.Zeit = Time.time; w.RA = ra; w.RB = rb; w.Schmutz = schmutz;
            gestartet++;
        }

        // ---------------------------------------------------------------- hallsend (Fund 4): Messvorrat (Begruendung WandVorratKern.cs)

        sealed class VorratEintrag
        {
            public bool Hat, MitteFrei;
            public short RA, RB;
            public int Schmutz;
            public Vector3 PA, PB;
            public float Zeit, Score, Echt0, RaumDb, FlankeDb, Diffus, Diffus1, Diffus2;
            public readonly float[] Db = new float[WegeKern.N];
        }
        const int VorratGroesse = 32;
        static readonly VorratEintrag[] vorrat = VorratAnlegen();
        static int vorratNaechster, vorratGenommen;
        static VorratEintrag[] VorratAnlegen() { var v = new VorratEintrag[VorratGroesse]; for (int i = 0; i < v.Length; i++) v[i] = new VorratEintrag(); return v; }
        static V3 V(Vector3 p) => new V3(p.x, p.y, p.z);

        static bool EigeneGilt(WandWeg w, Vector3 a, Vector3 b, short ra, short rb)
            => w.Score >= 0f && !w.Vorrat && WandVorratKern.Gilt(w.RA, w.RB, w.Schmutz, V(w.PA), V(w.PB), w.Zeit, ra, rb, schmutz, V(a), V(b), Time.time, NeuBewegung, NeuAlter);

        /// <summary>Juengste gueltige Messung des Raumpaars als vorlaeufiges Ergebnis in w uebernehmen (nur die Ergebnisse, nicht die
        /// Orte - die setzt Starten fuer den eigenen Auftrag). false = keine passende.</summary>
        static bool VorratHolen(WandWeg w, Vector3 a, Vector3 b, short ra, short rb)
        {
            VorratEintrag best = null;
            float jetzt = Time.time;
            V3 va = V(a), vb = V(b);
            for (int i = 0; i < vorrat.Length; i++)
            {
                var v = vorrat[i];
                if (!v.Hat || (best != null && v.Zeit <= best.Zeit)) continue;
                if (WandVorratKern.Gilt(v.RA, v.RB, v.Schmutz, V(v.PA), V(v.PB), v.Zeit, ra, rb, schmutz, va, vb, jetzt, NeuBewegung, NeuAlter)) best = v;
            }
            if (best == null) return false;
            w.Score = best.Score; w.Echt0 = best.Echt0; Array.Copy(best.Db, w.Db, WegeKern.N);
            w.RaumDb = best.RaumDb; w.FlankeDb = best.FlankeDb; w.Diffus = best.Diffus; w.Diffus1 = best.Diffus1; w.Diffus2 = best.Diffus2;
            w.MitteFrei = best.MitteFrei; w.Kette = null; w.LogAnzahl = 0; w.Vorrat = true;
            vorratGenommen++;
            return true;
        }

        static void VorratRein(WandWeg w)
        {
            if (w.Score < 0f) return;
            var v = vorrat[vorratNaechster];
            vorratNaechster = (vorratNaechster + 1) % vorrat.Length;
            v.Hat = true; v.RA = w.RA; v.RB = w.RB; v.Schmutz = w.Schmutz; v.PA = w.PA; v.PB = w.PB; v.Zeit = w.Zeit;
            v.Score = w.Score; v.Echt0 = w.Echt0; Array.Copy(w.Db, v.Db, WegeKern.N);
            v.RaumDb = w.RaumDb; v.FlankeDb = w.FlankeDb; v.Diffus = w.Diffus; v.Diffus1 = w.Diffus1; v.Diffus2 = w.Diffus2; v.MitteFrei = w.MitteFrei;
        }

        static void Beenden(ParallelSoundPropagationCalculator c, WandWeg w)
        {
            w.Handle.Complete();      // fertig laut IsCompleted -> blockiert nicht
            w.Laeuft = false;
            float staerke = Plugin.Gesamtstaerke.Value, schale = Schale.Value, summe = 0f;
            w.LogAnzahl = 0;
            logW = KlangLog.An ? w : null;
            if (Physik.An) Wege.TBeginn(w.PB, w.PA);     // 0.8.4 Stufe 1: Empfangsraum/Flanken = Raum des Hoerers (B)
            w.RaumDb = w.FlankeDb = float.NaN; w.Diffus = w.Diffus1 = w.Diffus2 = 0f; w.Kette = null; w.MitteFrei = false; w.Vorrat = false;
            for (int k = 0; k < Linien; k++)
            {
                logLinie = k; energie1[k] = energie2[k] = -1f;
                linieKette = null; linieTeile = 0;
                energie[k] = Linie(w, k, staerke, schale);
                if (k == 0) { w.RaumDb = linieRaum; w.FlankeDb = linieFlanke; w.Diffus = linieDiffus; w.Diffus1 = linieDiffus1; w.Diffus2 = linieDiffus2; w.Kette = linieKette; w.MitteFrei = linieTeile == 0 && energie[k] >= 0f; }
            }
            logW = null;
            // 0.8.3: Linien ohne Aussage (Luecke, Ende in Geometrie) fallen heraus, die Gewichte der uebrigen werden neu verteilt
            summe = WandLinie.Mitteln(energie, Gewicht, Linien);
            gemessen++;
            if (summe < 0f) { w.Score = -1f; ohneAussage++; return; }     // keine Aussage -> Vanilla-Route gilt (auch ein Vorratswert faellt weg)
            float wand = Physik.An ? Physik.WertungAusEnergie(summe) : Mathf.Clamp01(1f - summe + Zuschlag.Value);
            w.Score = wand;
            w.Echt0 = float.NaN;
            if (Physik.An)
            {
                float s1 = WandLinie.Mitteln(energie1, Gewicht, Linien), s2 = WandLinie.Mitteln(energie2, Gewicht, Linien);
                float dyn = Physik.Dynamik.Value;
                // 10.11. (klangfarbe): echte Baender (nicht je Band bei Max. insulation gekappt, sonst ist eine dicke Wand in allen
                // Baendern 60 dB und der Tiefpass offen); Deckel nur fuer die Lautstaerke (WegeKern.Deckeln), Score bleibt Wertung.
                double d0 = Physik.DbAusEnergie(summe);
                double d1 = s1 < 0f ? d0 : Physik.DbAusEnergie(s1);
                double d2 = s2 < 0f ? d1 : Physik.DbAusEnergie(s2);
                w.Echt0 = (float)d0;
                WegeKern.Deckeln(ref d0, ref d1, ref d2, dyn);
                w.Db[0] = (float)d0; w.Db[1] = (float)d1; w.Db[2] = (float)d2;
            }
            VorratRein(w);   // hallsend (Fund 4)
            float route = c._propagationScore;
            if (route > 0f)
            {
                vergleiche++; summeRoute += route; summeWand += wand;
                if (wand < route) wandGewann++;
            }
        }

        // Restenergie entlang einer Linie (beide Richtungen zusammen), 0 = voellig dicht, -1 = keine Aussage (0.8.3).
        static float Linie(WandWeg w, int k, float staerke, float schale)
        {
            float len = w.Laenge;
            int nVor = Lesen(w, 2 * k, len, false, vorT, out bool vorVoll);
            int nRueck = Lesen(w, 2 * k + 1, len, true, rueckT, out bool rueckVoll);
            if (k == 0) WandLinie.NahAnEnden(vorT, nVor, rueckT, nRueck, len, out mitteNahA, out mitteNahB);
            var befund = WandLinie.Auswerten(vorT, nVor, vorVoll, rueckT, nRueck, rueckVoll, len, k != 0, mitteNahA, mitteNahB, stuecke, out int n);
            if (befund == LinienBefund.EndeVerdeckt) { endeVerdeckt++; return -1f; }

            float sum = 0f;
            int nb = 0;
            bool regel = Physik.An && BlattRegel != null && BlattRegel.Value;   // tordoppelt
            bandSum[1] = bandSum[2] = 0f; linieMasse = 0; linieRaum = linieFlanke = float.NaN; linieDiffus = linieDiffus1 = linieDiffus2 = 0f;
            for (int i = 0; i < n; i++) if (MaterialCache.Get(stuecke[i].Id) != (int)Gruppe.Body) linieTeile++;   // mehrwand: freie Linie (Log)
            if (Physik.An)
            {
                // 0.8.4: im Physik-Modus erst zu Schichten zusammenfassen (WandLinie.Schichten), dann je Schicht Massengesetz
                int nl = 0;
                for (int i = 0; i < n; i++)
                {
                    var st = stuecke[i];
                    int g = MaterialCache.Get(st.Id);
                    if (g == (int)Gruppe.Body) continue;
                    if (IstGelaende(st.Id))
                    {
                        if (!Beitrag(st.Id, st.Dicke, st.Einzeln, schale, ref sum)) { gesperrt++; energie1[k] = energie2[k] = 0f; return 0f; }
                        continue;
                    }
                    if (st.Einzeln) einzelZahl++;
                    if (nl + 2 > lagen.Length) break;
                    // 10.11. (akustik-wand, Befund 2): Dicke senkrecht zur Flaeche (WandLinie.Senkrecht), die Lage bleibt auf der Linie
                    if (!st.Einzeln && st.Dicke * WandLinie.Senkrecht(st.Cos) > 1.5f)
                    {
                        // wohl zwei getrennte Flaechen eines grossen Meshes (wie bisher): zwei Einzelflaechen desselben Colliders
                        lagen[nl++] = new WandLage { A = st.Start, B = st.Start, Einzeln = true, G = g, Id = st.Id, Cos = st.Cos };
                        lagen[nl++] = new WandLage { A = st.Start + st.Dicke, B = st.Start + st.Dicke, Einzeln = true, G = g, Id = st.Id, Cos = st.Cos };
                        continue;
                    }
                    float a = st.Start, b = st.Einzeln ? st.Start : st.Start + Mathf.Max(st.Dicke, 0.02f / WandLinie.Senkrecht(st.Cos));
                    lagen[nl++] = new WandLage { A = a, B = b, Einzeln = st.Einzeln, G = g, Id = st.Id, Cos = st.Cos };
                }
                for (int g = 0; g < Plugin.AnzahlGruppen; g++)
                {
                    dichteG[g] = Physik.Dichte[g].Value; massivG[g] = Physik.Massiv[g].Value;
                    einzelG[g] = Bauakustik.EinzelDicke(g, schale); schaleG[g] = Bauakustik.IstSchalenGruppe(g);
                }
                int ns = WandLinie.Schichten(lagen, nl, dichteG, einzelG, massivG, schaleG, schichten);
                var fb = Wege.F;
                float v0 = 0f, v1 = 0f, v2 = 0f, vm = 0f; float vB = 0f;
                nb = 0;
                for (int i = 0; i < ns; i++)
                {
                    var sc = schichten[i];
                    float db = Physik.DaemmungStueck(sc.Id, sc.G, sc.Dicke);   // 0.8.6: Tuer = ihr Blatt
                    sum += db;
                    BandDazu(sc.Id, sc.G, sc.Dicke);
                    LogStueck(sc.Id, sc.G, sc.Dicke, db, sc.Teile);
                    // 10.11. (akustik-wand, Befund 6): getrennte Schalen mit Luft dazwischen (Bauakustik.Doppelschale), je Band
                    float b1 = Physik.DaemmungStueck(sc.Id, sc.G, sc.Dicke, (float)fb[1], true), b2 = Physik.DaemmungStueck(sc.Id, sc.G, sc.Dicke, (float)fb[2], true);
                    float mi = Physik.MasseStueck(sc.Id, sc.G, sc.Dicke);
                    float k0 = 0f, k1 = 0f, k2 = 0f;
                    double luft = i > 0 ? (sc.A - vB) * WandLinie.Senkrecht(sc.Cos) : 0;
                    if (i > 0)
                    {
                        k0 = (float)(Bauakustik.Doppelschale(v0, db, vm, mi, luft, fb[0]) - (v0 + db));
                        k1 = (float)(Bauakustik.Doppelschale(v1, b1, vm, mi, luft, fb[1]) - (v1 + b1));
                        k2 = (float)(Bauakustik.Doppelschale(v2, b2, vm, mi, luft, fb[2]) - (v2 + b2));
                        sum += k0; bandSum[1] += k1; bandSum[2] += k2;
                    }
                    // tordoppelt: Schicht = Blatt/Scheibe eines Audio-Portals? (Zuordnung wie das Spiel, TuerenFenster.IstPortalBlatt)
                    float pbB = 0f, pbH = 0f;
                    bool blatt = regel && TuerenFenster.IstPortalBlatt(sc.Id, out pbB, out pbH);
                    // mehrwand: Abstand ueber der Hohlraumtiefe -> neue Konstruktion (die Doppelschale ist dort schon R_a + R_b, k = 0)
                    if ((i == 0 || WandKetteKern.Getrennt(luft, fb[0])) && nb < bloecke.Length)
                        bloecke[nb++] = new KettenBlock { A = sc.A, B = sc.B, Blatt = blatt, BlattB = pbB, BlattH = pbH };
                    else { bloecke[nb - 1].R0 += k0; bloecke[nb - 1].R1 += k1; bloecke[nb - 1].R2 += k2; bloecke[nb - 1].Blatt &= blatt; }   // Blatt + Wand in Kontakt = Wand
                    bloecke[nb - 1].R0 += db; bloecke[nb - 1].R1 += b1; bloecke[nb - 1].R2 += b2; bloecke[nb - 1].Masse += mi;
                    bloecke[nb - 1].B = Mathf.Max(bloecke[nb - 1].B, sc.B); bloecke[nb - 1].Schichten++;
                    v0 = db; v1 = b1; v2 = b2; vm = mi; vB = sc.B;
                }
            }
            else
            for (int i = 0; i < n; i++)
            {
                var st = stuecke[i];
                if (!Beitrag(st.Id, st.Dicke, st.Einzeln, schale, ref sum)) { gesperrt++; return 0f; }
            }
            float e = Physik.An ? Physik.EnergieAusDb(Mathf.Min(sum, Physik.Dynamik.Value)) : Mathf.Exp(-sum * staerke);
            if (befund == LinienBefund.Luecke)
            {
                // Untergrenze: erreicht schon das Gesehene die Hoechstdaemmung (bzw. 30 dB ohne Physik-Modus), ist die Linie sicher dicht
                bool dicht = Physik.An ? sum >= Physik.Dynamik.Value : e <= 1e-3f;
                if (dicht) { lueckeDicht++; energie1[k] = energie2[k] = 0f; return 0f; }
                voll++;
                return -1f;
            }
            if (Physik.An)
            {
                // 0.8.4 Stufe 1: je Band, Flanken + Empfangsraum fuer die ganze Wand (Abstaende Quelle/Hoerer -> erste/letzte Flaeche)
                float r1 = len, r2 = len;
                for (int i = 0; i < n; i++) { r1 = Mathf.Min(r1, stuecke[i].Start); r2 = Mathf.Min(r2, len - stuecke[i].Start - stuecke[i].Dicke); }
                bandTmp[0] = sum; bandTmp[1] = bandSum[1]; bandTmp[2] = bandSum[2];
                bool korrigiert = n > 0 && sum > 0f && linieMasse > 0;
                // tordoppelt (WandKetteKern, Regel 1): eine Konstruktion, die Blatt eines Portals ist = Route des Raumwegs -> keine Aussage
                if (korrigiert && nb == 1 && bloecke[0].Blatt) return BlattLinie();
                // mehrwand: getrennte Konstruktionen als Kette (Flanken je Wand, Raum je Uebergang); eine Konstruktion wie bisher
                if (korrigiert && nb > 1)
                {
                    Wege.KorrigierenKette(bloecke, nb, len, w.PA, w.PB, bandTmp);
                    if (Wege.LetzteNurBlatt) return BlattLinie();   // tordoppelt: alle Trennwaende sind Portal-Blaetter
                    linieKette = Wege.LetzteKette;
                    for (int j = 0; j < nb; j++) if (bloecke[j].Blatt) { mischBlatt++; break; }
                }
                else if (korrigiert) Wege.Korrigieren(linieMasse, Mathf.Max(r1, 0f), Mathf.Max(r2, 0f), bandTmp);
                else for (int b = 0; b < WegeKern.N; b++) bandTmp[b] = Mathf.Min(bandTmp[b], (float)WegeKern.BandGrenzeDb);   // 10.11.: nicht je Band bei Max. insulation
                // galpegel: ohne Korrektur gilt kein Wert der vorigen Linie (Wege.Letzte* sind sonst die einer anderen Linie)
                linieRaum = korrigiert ? Wege.LetzteRaumDb : float.NaN; linieFlanke = korrigiert ? Wege.LetzteFlankeDb : float.NaN;
                linieDiffus = korrigiert ? Wege.LetzteDiffus : 0f;
                linieDiffus1 = korrigiert ? Wege.LetzteDiffusB[1] : 0f; linieDiffus2 = korrigiert ? Wege.LetzteDiffusB[2] : 0f;   // hallsend (B2)
                energie1[k] = Physik.EnergieAusDb(bandTmp[1]); energie2[k] = Physik.EnergieAusDb(bandTmp[2]);
                e = Physik.EnergieAusDb(bandTmp[0]);
            }
            return e;
        }

        // tordoppelt: Linie nur durch Portal-Blaetter -> keine Aussage (WandLinie.Mitteln verteilt ihr Gewicht auf die uebrigen Linien,
        // die die Wand neben dem Blatt treffen; treffen alle das Blatt, traegt der Raumweg allein). Begruendung WandKetteKern.cs.
        static float BlattLinie()
        {
            durchBlatt++;
            linieRaum = linieFlanke = float.NaN; linieDiffus = linieDiffus1 = linieDiffus2 = 0f;
            linieKette = KlangLog.An ? " (middle line only through a door/window leaf of the room path: not counted twice)" : null;
            return -1f;
        }

        // Treffer eines Strahls lesen (T vom Linienanfang A), voll = Trefferhoechstzahl erreicht (dahinter unbekannt).
        static int Lesen(WandWeg w, int strahl, float len, bool rueck, WandTreffer[] ziel, out bool voll)
        {
            Vector3 linie = w.Laenge > 1e-4f ? (w.PB - w.PA) / w.Laenge : Vector3.forward;   // Richtung der Linie (alle Strahlen parallel)
            int basis = strahl * MaxTreffer, n = 0;
            for (int j = 0; j < MaxTreffer; j++)
            {
                RaycastHit h = w.Treffer[basis + j];
                int id = h.colliderInstanceID;
                if (id == 0) break;
                ziel[n].Id = id;
                ziel[n].T = rueck ? len - h.distance : h.distance;
                ziel[n].Cos = Mathf.Abs(Vector3.Dot(h.normal, linie));   // 10.11. (akustik-wand, Befund 2)
                n++;
            }
            voll = n >= MaxTreffer;
            return n;
        }

        // false = Linie gilt als dicht (Huegel). Sonst wird die Daempfung des Stuecks auf x addiert.
        // einzeln = Flaeche ohne Gegenstueck (einseitiges Mesh): Dicke je Gruppe (Bauakustik.EinzelDicke, 0.8.3; vorher immer die Schale).
        static bool Beitrag(int id, float dicke, bool einzeln, float schale, ref float x)
        {
            bool gelaende = IstGelaende(id);
            int g = MaterialCache.Get(id);
            if (g == (int)Gruppe.Body) return true;              // Spieler/Leichen zaehlen nicht als Wand
            if (einzeln) { dicke = gelaende ? schale : Bauakustik.EinzelDicke(g, schale); einzelZahl++; }
            if (gelaende && dicke > 3f) return false;
            if (!gelaende && dicke > 1.5f) dicke = 2f * schale;     // wohl zwei getrennte Flaechen eines grossen Meshes
            if (Physik.An)
            {
                // Einzelflaeche: Gruppendicke (Daemmung begrenzt selbst auf 2 mm); 0.8.6: Tuer-Collider = Blatt der Tuer
                float db = Physik.DaemmungStueck(id, g, einzeln ? dicke : Mathf.Max(dicke, 0.02f));
                x += db;
                BandDazu(id, g, einzeln ? dicke : Mathf.Max(dicke, 0.02f));
                LogStueck(id, g, dicke, db);
                return true;
            }
            float s = Plugin.Oberflaeche[g].Value;
            if (g == (int)Gruppe.Door)
            {
                // 0.8.6: Blatt der Tuer (Massengesetz, Bezugsband) als ganzes Stueck, ohne Anteil je Meter Strahllaenge
                float ts = TuerenFenster.TuerS(id);
                if (ts >= 0f)
                {
                    x += ts;
                    LogStueck(id, g, dicke, ts * Plugin.Gesamtstaerke.Value * 4.3429f);
                    return true;
                }
            }
            float dd = einzeln ? dicke : Mathf.Max(dicke, 0.02f);
            x += s + Plugin.Dicke[g].Value * dd;
            LogStueck(id, g, dicke, (s + Plugin.Dicke[g].Value * dd) * Plugin.Gesamtstaerke.Value * 4.3429f);
            return true;
        }

        // 0.8.4 Stufe 1: hohe Baender und Flaechenmasse einer Schicht aufsummieren (Physik-Modus)
        static void BandDazu(int id, int g, float dicke)
        {
            var f = Wege.F;
            bandSum[1] += Physik.DaemmungStueck(id, g, dicke, (float)f[1], true);
            bandSum[2] += Physik.DaemmungStueck(id, g, dicke, (float)f[2], true);
            linieMasse += Physik.MasseStueck(id, g, dicke);
        }

        /// <summary>0.8.4 Stufe 1: Wertung der Wand (fuer die Summe in Physik/Klang-Log), -1 = keine Messung.</summary>
        internal static bool WandFuer(ParallelSoundPropagationCalculator c, out float score, out float[] db, out float raumDb, out float flankeDb)
        {
            score = -1f; db = null; raumDb = flankeDb = float.NaN;
            if (c == null || !Plugin.Aktiv.Value || !Aktiv.Value || !Tabelle.TryGetValue(c, out var w) || w.Score < 0f) return false;
            score = w.Score; db = w.Db; raumDb = w.RaumDb; flankeDb = w.FlankeDb;
            return true;
        }

        /// <summary>galpegel (10.11.): Aufteilung gerichtet/diffus fuer den Raumweg MIT Wand zwischen Raeumen. Bis hier galt sie nur, wenn
        /// die Route gewann (Raumweg.DiffusZusatz); gewann die Wand, spielte die ganze Energie trocken (auch das Hallfeld, das die Wand
        /// in den Hoererraum abstrahlt, WegeKern.WandwegTeile). Jetzt: Diffusanteil beider Wege (Route: ihr geglittener Anteil, Wand:
        /// Hallfeld des Hoererraums / Wandweg gesamt) nach derselben Regel wie ScorePostfix verknuepft (Energiesumme bzw. leiserer Weg).
        /// Freie Linie (keine Wandstuecke) = Direktschall: Anteil 0. false = keine Wandmessung (dann Raumweg/Wege wie bisher).</summary>
        internal static bool DiffusZusatz(SpatialAudioOccluder occ, out double zusatz, out Raumweg.Zustand zustand, out DiffusMerk merk, double[] zusatzB = null)
        {
            zusatz = 0; zustand = null; merk = null;
            try
            {
                if (!Physik.An || occ == null || !occ.IsPropagationActive) return false;
                var pc = Raumweg.RechnerVon(occ);
                if (!WandFuer(pc, out float ws, out _, out _, out _) || !Tabelle.TryGetValue(pc, out var w)) return false;
                float dyn = Physik.Dynamik.Value;
                // dieselben echten Daempfungen wie ScorePostfix (ungedeckelt, Bezugsband)
                double rDb = pc._propagationScore * dyn + Raumweg.UeberDeckel(pc);
                double wDb = float.IsNaN(w.Echt0) ? ws * dyn : Math.Max(ws * dyn, w.Echt0);
                double fr = 0;
                // Route ohne physikalische Rechnung (Spielwert) hat keinen bekannten Diffusanteil -> 0 (wie bisher trocken)
                bool route = Raumweg.RoutenDiffus(occ, out double zr, out zustand, routeZusatzB);
                if (route) fr = 1 - Math.Pow(10, -zr / 10);
                double f = DiffusKern.ZweiWege(rDb, fr, wDb, w.Diffus, Wege.Summe);
                zusatz = DiffusKern.TrockenZusatzDb(f);
                merk = w.Merk; merk.Anteil = f; merk.Zusatz = zusatz;
                {
                    // tunneldiffus: Anteil der Route an der Energie (wie DiffusKern.ZweiWege: Summe energetisch, sonst gilt der leisere Weg)
                    double er = Math.Pow(10, -rDb / 10), ew = Math.Pow(10, -wDb / 10);
                    merk.Route = Wege.Summe ? (er + ew > 0 ? er / (er + ew) : 1) : (wDb < rDb ? 0 : 1);
                    if (!route) merk.Route = 0;   // Spielwert ohne Rechnung: keine Zeitstruktur
                }
                if (zusatzB != null)
                {
                    // hallsend (B2, 12.11.): je Band dieselbe Verknuepfung mit den echten Banddaempfungen beider Wege
                    // (Route: Raumweg.EchteBaender, Wand: Db + abgeschnittener Teil wie SummenKurve); ohne Route-Baender wie das Bezugsband.
                    zusatzB[0] = zusatz;
                    bool rb = Raumweg.EchteBaender(occ, pc, routeTmp);
                    double ueber = float.IsNaN(w.Echt0) ? 0 : Math.Max(0, w.Echt0 - w.Db[0]);
                    for (int b = 1; b < WegeKern.N; b++)
                    {
                        double rDbB = rb ? routeTmp[b] : rDb, wDbB = w.Db[b] + ueber;
                        double frB = route ? DiffusKern.AnteilAusZusatz(routeZusatzB[b]) : 0;
                        double fwB = b == 1 ? w.Diffus1 : w.Diffus2;
                        zusatzB[b] = DiffusKern.TrockenZusatzDb(DiffusKern.ZweiWege(rDbB, frB, wDbB, fwB, Wege.Summe));
                    }
                }
                return true;
            }
            catch (Exception e) { Statistik.Fehler(e); return false; }
        }

        /// <summary>galpegel: Klang-Log-Teil der Aufteilung (Raumweg mit Wand), null = keine.</summary>
        internal static string DiffusText(ParallelSoundPropagationCalculator pc)
        {
            if (pc == null || !Tabelle.TryGetValue(pc, out var w) || w.Score < 0f) return null;
            var m = w.Merk; var inv = System.Globalization.CultureInfo.InvariantCulture;
            string t = " | diffuse share (route+wall) " + (m.Anteil * 100).ToString("F0", inv) + " % (wall alone " + (w.Diffus * 100).ToString("F0", inv) + " %, dry " + m.Zusatz.ToString("+0.0;-0.0", inv) + " dB of the sum";
            if (!float.IsNaN(m.Trocken)) t += ", applied dry " + m.Trocken.ToString("+0.0;-0.0", inv) + " dB, reverb send " + m.Send.ToString("+0.0;-0.0", inv) + " dB)";
            else t += ", not applied)";
            return t;
        }

        /// <summary>mehrwand: Klang-Log-Teil der Kette (mittlere Linie), leer = eine Konstruktion bzw. keine Messung.</summary>
        internal static string KetteText(ParallelSoundPropagationCalculator pc)
        {
            if (pc == null || !Tabelle.TryGetValue(pc, out var w) || w.Score < 0f || w.Kette == null) return "";
            return w.Kette;
        }

        /// <summary>0.8.4 Stufe 1 (Energiesumme, Raumweg + Wand): Tiefpass aus beiden Spektren, energetisch je Band summiert.
        /// Route: Bezugsband = Wertung, Hochbaender aus ihrem Tiefpass (Tuer-Abbildung bzw. Beugung) zurueckgerechnet.</summary>
        internal static bool SummenTiefpass(SpatialAudioOccluder occ, out float lp)
        {
            lp = 22000f;
            if (!SummenKurve(occ, out double m1, out double m2)) return false;
            lp = Wege.FitLp(m1, m2);
            Wege.BandFilterRaumweg++;
            return true;
        }

        /// <summary>0.8.5: Mehrdaemmung 1 kHz / 4 kHz gegenueber dem Bezugsband (dB) von Raumweg + Wand, energetisch je Band summiert,
        /// ohne Zaehler (auch fuer die Hoerbarkeit je Band). false = keine Summe.</summary>
        static readonly double[] routeTmp = new double[WegeKern.N], routeZusatzB = new double[WegeKern.N];

        internal static bool SummenKurve(SpatialAudioOccluder occ, out double m1, out double m2)
        {
            m1 = m2 = 0;
            try
            {
                if (!Wege.Summe || !Wege.Baender || occ == null || !occ.IsPropagationActive) return false;
                var pc = Raumweg.RechnerVon(occ);
                if (!WandFuer(pc, out float ws, out float[] wdb, out _, out _)) return false;
                float wEcht0 = Tabelle.TryGetValue(pc, out var ww) ? ww.Echt0 : float.NaN;
                float dyn = Physik.Dynamik.Value;
                var f = Wege.Frequenzen();
                // 10.11. (klangfarbe): Route mit ihren echten Baendern (Raumweg.EchteBaender: Tuerblatt je Band, Beugung, Raeume) statt
                // aus ihrem gefitteten Tiefpass zurueckgerechnet, Wand ebenso echt (Db + abgeschnittener Teil). Nur die Spielroute ohne
                // physikalische Rechnung behaelt die Abbildung (keine Baender bekannt).
                if (!Raumweg.EchteBaender(occ, pc, routeTmp))
                {
                    float wr = pc._propagationScore, lpr = Physik.Abbildung(wr);
                    double r0 = wr * dyn, m0 = WegeKern.MehrDb(lpr, f[0]);
                    for (int b = 0; b < WegeKern.N; b++) routeTmp[b] = r0 + WegeKern.MehrDb(lpr, f[b]) - m0;
                }
                double wv = float.IsNaN(wEcht0) ? 0 : Math.Max(0, wEcht0 - wdb[0]);
                double l0 = 0, l1 = 0, l2 = 0;
                for (int b = 0; b < WegeKern.N; b++)
                {
                    double er = WegeKern.Energie(-routeTmp[b]);
                    double ew = WegeKern.Energie(-(wdb[b] + wv));
                    double l = WegeKern.DbAus(er + ew, -WegeKern.BandGrenzeDb);
                    if (b == 0) l0 = l; else if (b == 1) l1 = l; else l2 = l;
                }
                m1 = l0 - l1; m2 = l0 - l2;
                return true;
            }
            catch (Exception e) { Statistik.Fehler(e); return false; }
        }

        // Klang-Log: nur die mittlere Linie (Gewicht 0.4) wird mitgeschrieben
        static void LogStueck(int id, int g, float dicke, float db, int teile = 1)
        {
            var w = logW;
            if (w == null || logLinie != 0 || w.LogAnzahl >= LogMax) return;
            int i = w.LogAnzahl++;
            w.LogId[i] = id; w.LogGruppe[i] = g; w.LogDicke[i] = dicke; w.LogDb[i] = db; w.LogTeile[i] = teile;
        }

        static bool IstGelaende(int id)
        {
            if (Gelaende.TryGetValue(id, out bool b)) return b;
            b = Resources.InstanceIDToObject(id) is TerrainCollider;
            Gelaende[id] = b;
            return b;
        }
    }
}
