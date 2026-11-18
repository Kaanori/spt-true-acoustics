using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using EFT.DataProviding;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;

namespace Materialklang
{
    // 0.8.4 - Raumweg im Physik-Modus aus Geometrie und Portalzustand statt aus der Wegkosten-Heuristik (Ursache und Formel:
    // RaumwegKern.cs). Spiel-Teil: liest nach dem Job dieselben Routen/Portaldaten wie PropagationJob, rechnet jede Route
    // physikalisch (dieselben Portalpunkte wie das Spiel), nimmt die lauteste und schreibt deren Wertung (dB / Max. insulation)
    // in ParallelSoundPropagationCalculator._propagationScore. Alles danach (Wand zwischen Raeumen als Minimum, Lautstaerke,
    // Lautheit, Klang-Log) bleibt wie es ist. Zusaetzlich im Physik-Modus fuer solche Raumwege:
    //  - Der Faktor draussen<->drinnen der Klanggruppe (GetEnvironmentFactor, Heuristik auf die Wegkosten) entfaellt;
    //    der Uebergang steckt jetzt in der Oeffnung selbst.
    //  - Tiefpass = min(Abbildung des Physik-Modus fuer den Tuer-/Fensteranteil, Beugungs-Tiefpass).
    internal static partial class Raumweg
    {
        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<float> Alpha;

        const double FHoch = 4000.0;
        const float MinWertung = 0.005f;   // Propagation muss "aktiv" bleiben (Effekt > 0), sonst schaltet das Spiel auf Hindernis

        internal sealed class Zustand
        {
            public Vector3 A, B;               // Job-Richtung (A = Emitter des Raumpaars)
            public bool Umgekehrt, Hat;
            public uint Paar;
            public ISpatialAudioRoom RaumA, RaumB;
            public float Wertung = -1f, Spiel = -1f, TiefpassBeugung = 22000f, WertungTuer, Umwelt = float.NaN;
            public string Kette;               // fuers Klang-Log
            // 0.8.5: alle Routen mit weichem Rang-Gewicht und zeitlicher Ueberblendung (RaumwegKern.RoutenMischer)
            public readonly RoutenMischer Mix = new RoutenMischer();
            public int Bild = -1;              // letztes Bild mit geschriebenem Wert (Diagnose Pegelsprung)
            public float LetztDb, LetztAlt;
            public V3 Austritt;                // energiegewichteter Austrittspunkt (nur Log, siehe Richtung unten)
            // 0.8.5 Leistung: Bild-Budget (aufgeschobene Rechnung) und Tiefpass-Fit nur bei geaenderter Bandkurve
            public int Aufschub;               // Bilder in Folge aufgeschoben
            public bool Nachholen;             // Rechnung steht aus -> Occluder neu rechnen lassen
            public uint PaarGerechnet;         // Raumpaar der geltenden Wertung
            public double FitM1 = double.NaN, FitM2 = double.NaN; public float FitK, FitTp, FitLp;
            // 0.8.8: Container der Rechnung (Richtung: gehoert der Wert noch zu dieser Quelle?) und Ersatzweg ohne Spielroute
            public AudioSourceContainer Behaelter;
            public bool Ersatz;
            public double Buendel = 1;         // 0.8.8: Buendelung der Austrittsrichtungen (RoutenMischer.Summe mit Hoerer)
            // 10.11. (B2): Ueberblendung als Gleiten der Band-Daemmung in dB (StetigkeitKern.cs), jedes Bild fortgeschrieben (BildTakt)
            public readonly RoutenGleiter Gleit = new RoutenGleiter();
            public ParallelSoundPropagationCalculator Pc;   // Rechner, solange der Zustand im Bild-Takt steht
            public bool ImTakt;
            public int NeuBild = -1;           // 10.11. (R1b): Bild des letzten Neubeginns (nur einmal je Bild)
            public double Ueber;               // 10.11. (klangfarbe): echte Daemmung des Bezugsbands ueber Max. insulation (Ziel), dB
            // 10.11. (spitzen): Continuous-Occluder dieses Rechners (wendet nur in seinem Round-Robin-Takt an) und Dauer der letzten Rechnung
            public SpatialAudioOccluder Occ;
            public AudioSourceContainer OccBehaelter;   // Container, den Occ schon in einem frueheren Takt bedient hat (ab OccSeitBild)
            public int OccSeitBild = int.MaxValue;
            public double LetzteMs;
            // diffus-hall (10.11.): um wie viel dB der GERICHTETE Anteil leiser ist als die Summe (Bezugsband, DiffusKern.TrockenZusatzDb),
            // mit derselben Zeitkonstante wie die Band-Daemmung geglitten (stetig; alle Baender gleich, nur [0] wird gelesen)
            public readonly RoutenGleiter DGleit = new RoutenGleiter();
            // lampenpegel (18.11.): um wie viel dB die gemischten Routen ungekappt ueber der gespielten Summe laegen (RoutenMischer.UeberFreiDb),
            // gleitet wie die Band-Daemmung (alle Baender gleich, nur [0] wird gelesen). Liest nur Kalibrierung.LampeDb.
            public readonly RoutenGleiter UGleit = new RoutenGleiter();
            public double DAnteil;             // Diffusanteil der letzten Rechnung (Log)
            public float DTrocken = float.NaN, DSend = float.NaN;   // zuletzt angewandt: Zusatz trocken / Reverb-Send (dB, Log)
            public bool Gleitet => Gleit.Laeuft || DGleit.Laeuft || UGleit.Laeuft;
            // tunneldiffus (17.11.): Zeitstruktur der gemischten Routen je Band (ZeitKern; Ziel der letzten Rechnung, ungeglitten:
            // ein Verhaeltnis, das mit der geglitteten Summe angewandt wird)
            public ZeitKern.Baender Zeit;
            public void Vergessen() { Mix.Leeren(); Gleit.Leeren(); DGleit.Leeren(); UGleit.Leeren(); DAnteil = 0; DTrocken = DSend = float.NaN; Wertung = -1f; Bild = -1; Aufschub = 0; Nachholen = false; FitM1 = double.NaN; Ersatz = false; Ueber = 0; Zeit = default; }
        }

        static readonly ConditionalWeakTable<ParallelSoundPropagationCalculator, Zustand> Tabelle =
            new ConditionalWeakTable<ParallelSoundPropagationCalculator, Zustand>();
        static readonly ConditionalWeakTable<ParallelSoundPropagationCalculator, Zustand>.CreateValueCallback Neu = _ => new Zustand();

        // Kontext: welcher Rechner gehoert zum Klang, der gerade angewendet wird
        internal static ParallelSoundPropagationCalculator Multi;     // MultiSource: OnUpdateOcclusionEffects
        static SpatialAudioOccluder applyOcc;

        // 0.8.5: Routen je Rang voll (Demo: 3), Rest weich ueber 'Soft rank width'
        internal const int RangN = 3;
        // 10.11. (B2): der Mischer uebernimmt neue Gewichte SOFORT (Blendzeit 0 fuer RoutenMischer.Uebernehmen, auch in Ersatzweg/
        // Wegsuche); die eingestellte Blendzeit wirkt im RoutenGleiter (Band-Daemmung in dB, exp(-dt/tau), jedes Bild).
        // Bis 10.11.: lineare Gewichte, nur fortgeschrieben, wenn das Spiel den Rechner aufrief -> Spruenge im Takt des Round-Robin.
        static double BlendS => 0.0;
        static double BlendEinstellungS => Wege.Summe && Wege.BlendMs != null ? Wege.BlendMs.Value / 1000.0 : 0.0;
        static double GleitTau => RoutenGleiter.Tau(BlendEinstellungS);
        /// <summary>Uhr des Gleiters: Beginn des Bilds (unskaliert) - alle Instanzen eines Klangs bekommen im selben Bild denselben Wert.</summary>
        static double GleitJetzt => Time.unscaledTimeAsDouble;
        static int RangNJetzt => Wege.Summe ? RangN : 1;
        static double BreiteJetzt => Wege.Summe && Wege.RangBreite != null ? Wege.RangBreite.Value : 0.0;
        static int hauptThread = -1;

        // Diagnose
        static int gerechnet, routen, ohneDaten;
        static int dMisch, dMischRouten, dGeister, dNeue, dZwang, dBilder, dSpruengeNeu, dSpruengeAlt;
        static double dMaxNeu, dMaxAlt;
        static double summeSpiel, summeNeu;
        static float letzteUmwelt = float.NaN;

        internal static bool An => Physik.An && Aktiv != null && Aktiv.Value;

        // 0.8.5 Leistung: Bild-Budget (Interchange-Gefecht: SpatialAudioSystem.LateUpdate bis 28 ms, Ursache RaumwegNetz.cs)
        internal static ConfigEntry<float> BudgetMs;
        const int MaxAufschub = 4;
        internal static readonly Laufzeit ZeitGesamt = new Laufzeit("room paths (all)"), ZeitRechnen = new Laufzeit("route calculation"),
            ZeitSchreiben = new Laufzeit("route mix/low-pass"), ZeitWege = new Laufzeit("sound paths snapshot (T/D/R)"),
            ZeitLaut = new Laufzeit("volume + audibility"), ZeitFilter = new Laufzeit("low-/high-pass + audibility"),
            ZeitNeu = new Laufzeit("first values at once (whole, incl. game job)"), ZeitTaktAnwenden = new Laufzeit("glide applied per frame (continuous sounds)");
        static int dVomAngewandt, dTaktAngewandt, dNaN, dVorhersage, dNeuMaxBild, dNeuImBild, neuBildNr = -1;
        static int dAufgeschoben, dUeberBudget, dNachgeholt, dBudgetBilder, letztesUeberBild = -1;

        internal static void Binden(ConfigFile c)
        {
            const string s = "0. Physics Mode (real values)";
            Richtung.Binden(c);   // 0.8.8
            Reichweite.Binden(c);   // raid2242richtung
            Ersatzweg.Binden(c);  // 0.8.8
            Aktiv = c.Bind(s, "Room paths: physical openings", true,
                "Sound between rooms (via doors/windows): computed from the path geometry and the portal state instead of the game's route cost. " +
                "Open openings: edge diffraction (Kurze-Anderson) and the sound field of the rooms in between; closed doors/windows keep the values of section 4. " +
                "Off = the game's route cost read as insulation (0.8.3).");
            Alpha = c.Bind(s, "Room paths: room absorption", 0.1f,
                new ConfigDescription("Mean absorption coefficient of the rooms on a room path (how much sound a room swallows). 0.1 = the game's baked reverb data. " +
                    "Lower = rooms pass more sound on.", new AcceptableValueRange<float>(0.02f, 0.5f)));
            BudgetMs = c.Bind(s, "Room paths: max. ms per frame", 1.0f,
                new ConfigDescription("CPU budget of the room-path calculation per frame (main thread). New sounds (shots, impacts) are always calculated at once; " +
                    "sounds that already have a value wait for the next frame when the budget is used up (at most 4 frames). The values stay the same, only later. " +
                    "0 = no limit. The diagnostic line every 30 s shows the time used.", new AcceptableValueRange<float>(0f, 10f)));
            // raumecke (16.11.): Standard AUS - die Richtung der Raumrouten mischt heute nach der ganzen Energie (fast nur Hallfeld), dann
            // zieht ein Eckweg die Richtung zur Ecke statt zur lauteren direkten Tuer (Messung review-20261110/raumecke-STAND.md)
            Ecken = c.Bind(s, "Room paths: corners in multi-box rooms", false,
                "Experimental: in rooms made of several boxes (L-shaped corridors), the last stretch opening -> listener (and source -> first opening) " +
                "goes around the corner between the boxes when the straight line leaves the room and is blocked (Physics.Linecast). The corner " +
                "diffracts the direct part (Kurze-Anderson) and becomes the exit point for the direction. Applies to new calculations.");
            RaumwegNetz.EckenAn = Ecken.Value; Ecken.SettingChanged += (o, e) => { RaumwegNetz.EckenAn = Ecken.Value; Netz?.Leeren(); };
        }
        internal static ConfigEntry<bool> Ecken;

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var t = typeof(ParallelSoundPropagationCalculator);
                var upd = AccessTools.DeclaredMethod(t, "OnUpdate");
                var spaet = AccessTools.DeclaredMethod(t, "OnLateUpdate");
                var multi = AccessTools.DeclaredMethod(typeof(MultiSourceSpatialAudioOccluder), "OnUpdateOcclusionEffects");
                var apply = AccessTools.DeclaredMethod(typeof(SpatialAudioOccluder), "ApplyOcclusionToSource", new[] { typeof(BetterSource), typeof(bool) });
                if (upd == null || spaet == null || multi == null || apply == null)
                {
                    Plugin.Log.LogWarning("Room paths (physics): targets not found (game update?) -> the game's route cost is used.");
                    return;
                }
                h.Patch(upd, postfix: new HarmonyMethod(typeof(Raumweg), nameof(UpdateNach)));
                h.Patch(spaet, prefix: new HarmonyMethod(typeof(Raumweg), nameof(SpaetVor)), postfix: new HarmonyMethod(typeof(Raumweg), nameof(SpaetNach)));
                h.Patch(multi, prefix: new HarmonyMethod(typeof(Raumweg), nameof(MultiVor)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(Raumweg), nameof(MultiEnde)));
                h.Patch(apply, prefix: new HarmonyMethod(typeof(Raumweg), nameof(ApplyVor)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(Raumweg), nameof(ApplyEnde)));
                // 0.8.5: Pool-Reset vergisst die Routen; laufende Ueberblendung haelt Continuous-Occluder in Bewegung
                hauptThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                Richtung.Anwenden(h);   // 0.8.8
                Reichweite.Anwenden(h);   // raid2242richtung
                // 10.11. (B2): Gleiter jedes Bild fortschreiben - am Anfang des Audio-Bilds, vor jeder Rechnung/Anwendung des Spiels
                var sysUpd = AccessTools.DeclaredMethod(typeof(SpatialAudioSystem), "Update");
                if (sysUpd != null) h.Patch(sysUpd, prefix: new HarmonyMethod(typeof(Raumweg), nameof(BildTakt)));
                else Plugin.Log.LogWarning("Room paths (physics): SpatialAudioSystem.Update not found -> route blends only advance when the game updates the sound.");
                // 10.11. (R1b/B3): neuer Klang / zurueck in Reichweite -> Rechner sofort fuer DIESEN Stand rechnen, Spielglaettung darauf setzen
                var prio = AccessTools.DeclaredMethod(typeof(SoundOcclusionProcessor), "UpdatePriority");
                var hinzu = AccessTools.DeclaredMethod(typeof(SoundOcclusionProcessor), "OnSourceAdded");
                if (prio != null) h.Patch(prio, prefix: new HarmonyMethod(typeof(Raumweg), nameof(PrioVor)), postfix: new HarmonyMethod(typeof(Raumweg), nameof(PrioNach)));
                if (hinzu != null) h.Patch(hinzu, prefix: new HarmonyMethod(typeof(Raumweg), nameof(HinzuVor)));
                if (prio == null || hinzu == null)
                    Plugin.Log.LogWarning("Room paths (physics): SoundOcclusionProcessor.UpdatePriority/OnSourceAdded not found -> a new sound may start with the stale value of its container (game behaviour).");
                var reset = AccessTools.DeclaredMethod(t, "OnReset");
                if (reset != null) h.Patch(reset, postfix: new HarmonyMethod(typeof(Raumweg), nameof(ResetNach)));
                foreach (var ot in new[] { typeof(ContinuousPropagatedSpatialAudioOccluder), typeof(ContinuousSpatialAudioOccluder) })
                {
                    var ou = AccessTools.DeclaredMethod(ot, "OnUpdate");
                    if (ou != null) h.Patch(ou, prefix: new HarmonyMethod(typeof(Raumweg), nameof(OccUpdateVor)));
                    else Plugin.Log.LogWarning($"Room paths (physics): {ot.Name}.OnUpdate not found -> route blends of continuous sounds only advance when they move.");
                }
                JitAufwaermen();   // 10.11. (spitzen)
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // ---------------------------------------------------------------- 10.11. (spitzen) Aufwaermen
        // Beleg (LogOutput 18:40): die erste Routenrechnung des Raids dauerte 48,1 ms (darin Raumnetz 9,5 ms), danach 1-3 ms. Mono
        // uebersetzt jede Methode beim ersten Aufruf (JIT); dazu kommen Raumnetz, Vermessung der Raeume (Collider) und Blattmodelle.
        // 1. JIT: beim Pluginstart (Hauptfaden, Spielstart - niemand spielt) alle Methoden der Raumweg-Rechnung uebersetzen lassen
        //    (MethodHandle.GetFunctionPointer -> mono_compile_method; fuehrt keinen Code aus). Hauptfaden, weil Mono beim Uebersetzen
        //    Klassenkonstruktoren ausfuehren kann (Unity-Aufrufe nur dort erlaubt). Generische Methoden (z. B. NativeArray<T>) gehen
        //    nicht vorab - die deckt die Probe-Rechnung in 2. ab.
        // 2. Je Raid im ersten SpatialAudioSystem.Update, sobald die Portaldaten da sind: Raumnetz bauen, alle Innenraeume vermessen, eine
        //    echte Routenrechnung auf dem Raumpaar mit den meisten Routen (Mischer, Gleiter, Tiefpass-Fit, Klang-Log-Kette), ohne Folgen
        //    fuer einen Klang (eigener Zustand) und ohne die Diagnosezaehler zu verfaelschen.
        static SpatialAudioSystem aufgewaermtSystem;

        static readonly string[] JitTypen =
        {
            "Materialklang.Raumweg", "Materialklang.RaumwegNetz", "Materialklang.RaumwegKern", "Materialklang.RoutenMischer", "Materialklang.RoutenGleiter",
            "Materialklang.WiederverwendungKern", "Materialklang.WegeKern", "Materialklang.Wege", "Materialklang.Ersatzweg", "Materialklang.Richtung",
            "Materialklang.Laufzeit", "Materialklang.RoutenEintrag", "Materialklang.Wegsuche",
        };

        static void JitAufwaermen()
        {
            long t0 = Laufzeit.Jetzt;
            int n = 0, f = 0, typen = 0;
            var asm = typeof(Raumweg).Assembly;
            foreach (var name in JitTypen)
            {
                var t = asm.GetType(name, false);
                if (t == null) continue;
                typen++;
                JitTyp(t, ref n, ref f);
            }
            Plugin.Log.LogInfo($"[Diagnostics] Room paths: {n} methods of {typen} types compiled ahead (JIT) in {Laufzeit.Ms(Laufzeit.Jetzt - t0):F0} ms at game start" +
                               (f > 0 ? $", {f} skipped" : "") + ".");
        }

        static void JitTyp(Type t, ref int n, ref int f)
        {
            if (t.ContainsGenericParameters) return;
            const System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.DeclaredOnly | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            foreach (var m in t.GetMethods(bf))
            {
                if (m.IsAbstract || m.ContainsGenericParameters) continue;
                try { if (m.GetMethodBody() == null) continue; RuntimeHelpers.PrepareMethod(m.MethodHandle); m.MethodHandle.GetFunctionPointer(); n++; }
                catch { f++; }
            }
            foreach (var c in t.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                try { if (c.GetMethodBody() == null) continue; RuntimeHelpers.PrepareMethod(c.MethodHandle); c.MethodHandle.GetFunctionPointer(); n++; }
                catch { f++; }
            }
            foreach (var nt in t.GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)) JitTyp(nt, ref n, ref f);
        }

        static void Aufwaermen(SpatialAudioSystem sys)
        {
            if (!An || sys == null) return;
            try
            {
                if (!DataProvider.TryGetData<SpatialAudioDataContainer>(out var data) || data == null) return;   // noch nicht da: naechstes Bild
                var map = data._portalDataBufferService?._indexMap;
                if (map == null || map.Count == 0 || !data.StaticPortalDataList.IsCreated) return;
                var st = sys._audioRoomStorage;
                if (st == null) return;
                aufgewaermtSystem = sys;
                long t0 = Laufzeit.Jetzt;
                PortalTabelle(data);
                long t1 = Laufzeit.Jetzt;
                long m0 = messenTicks;
                int gemessen = 0;
                float alpha = Alpha.Value;
                for (int ri = 0; ri < Netz.RaumN; ri++) if (Netz.RaumFlaeche(ri, alpha, out _) > 0) gemessen++;
                long te = Laufzeit.Jetzt; if (RaumwegNetz.EckenAn) Netz.EckenVorbauen(); long tEcken = Laufzeit.Jetzt - te;   // raumecke: Kastengraphen im Ladebildschirm
                long t2 = Laufzeit.Jetzt;
                // Raumpaar mit den meisten Routen (ueber die Raeume beiderseits jedes Portals)
                BaseSpatialAudioPortal bp = null; RoomPair bestRp = null; bool bestRev = false; int bestLen = 0;
                foreach (var p in portalJeIndex)
                {
                    if (p == null || p.FrontRoom == null || p.BackRoom == null || p.FrontRoom.ID == p.BackRoom.ID) continue;
                    if (st.TryGetMatchingRoomPair(p.FrontRoom, p.BackRoom, out var rp, out bool rev) && rp != null && rp.Routes != null && rp.Routes.Length > bestLen)
                    { bp = p; bestRp = rp; bestRev = rev; bestLen = rp.Routes.Length; }
                }
                int routenProbe = 0; double probeMs = 0;
                if (bestRp != null && data.TryGetRoutesData(bestRp.ID, out var routes) && routes.IsCreated && routes.Length > 0
                    && data.TryGetPortalIndicesData(bestRp.ID, out var idx) && idx.IsCreated)
                {
                    long tp = Laufzeit.Jetzt;
                    var w = new Zustand();
                    Vector3 q = bp.FrontRoom.Bounds.center, h = bp.BackRoom.Bounds.center;
                    w.Umgekehrt = bestRev; w.A = bestRev ? h : q; w.B = bestRev ? q : h;
                    w.RaumA = bestRev ? (ISpatialAudioRoom)bp.BackRoom : bp.FrontRoom; w.RaumB = bestRev ? (ISpatialAudioRoom)bp.FrontRoom : bp.BackRoom;
                    w.Paar = bestRp.ID; w.Hat = true;
                    double jetzt = Time.realtimeSinceStartup;
                    if (RoutenRechnen(routes, idx, data.StaticPortalDataList, routes.Length, w, float.NaN, jetzt, 0))
                    {
                        float dyn = Physik.Dynamik.Value;
                        var db = new double[3];
                        if (!w.Mix.Summe(WegeKern.BandGrenzeDb, db, V(h), out _, out _)) db[0] = db[1] = db[2] = dyn;
                        WegeKern.Deckeln(db, dyn);
                        w.Gleit.ZielSetzen(db, GleitJetzt, GleitTau);
                        Wege.FitLp(db[1] - db[0], db[2] - db[0]);
                        WiederverwendungKern.Wertung(db[0], dyn, MinWertung, -1f);
                        if (KlangLog.An) RoutenText(w);
                    }
                    routenProbe = routes.Length; probeMs = Laufzeit.Ms(Laufzeit.Jetzt - tp);
                }
                // Diagnose wie vor dem Aufwaermen (die Probe ist keine Rechnung eines Klangs)
                routen = 0; dTreffer = dFehl = 0; dMisch = dMischRouten = dNeue = dGeister = 0; Netz.DRaumA = Netz.DRaumAGespart = Netz.DGekappt = 0;
                dRechnungen = dUeber1 = dUeber2 = dUeber5 = 0; sMax = 0; ersteMs = double.NaN; rechnungImRaid = 0; schnappNr = 0;
                Plugin.Log.LogInfo($"[Diagnostics] Room paths: warm-up for this raid in {Laufzeit.Ms(Laufzeit.Jetzt - t0):F1} ms at frame {Time.frameCount} " +
                                   $"({Time.timeSinceLevelLoad:F1} s after level load): room network {Laufzeit.Ms(t1 - t0):F1} ms, {gemessen} indoor rooms measured " +
                                   $"{Laufzeit.Ms(messenTicks - m0):F1} ms (all {Laufzeit.Ms(t2 - t1):F1} ms; corner graphs of {Netz.DEckenRaeume} multi-box rooms {Laufzeit.Ms(tEcken):F1} ms), test calculation over {routenProbe} routes {probeMs:F1} ms.");
            }
            catch (Exception e) { aufgewaermtSystem = sys; Statistik.Fehler(e); }
        }

        // ---------------------------------------------------------------- Harmony

        static void UpdateNach(ParallelSoundPropagationCalculator __instance, AudioSourceContainer sourceContainer, Vector3 listenerPosition)
        {
            if (!An || sourceContainer == null) return;
            // 0.8.8: Fruehabbruch ohne Spielroute (Wertung 1 = stumm) -> Ersatzweg ueber die Hindernisse
            if (__instance._earlyOutThisFrame) { Ersatzweg.Pruefen(__instance, sourceContainer, listenerPosition); return; }
            try
            {
                var sys = __instance._audioSystem;
                if (sys == null || !sys.TryGetMatchingRoomPair(sourceContainer.CurrentAudioRoom, out var rp, out bool rev)) return;
                var z = Tabelle.GetValue(__instance, Neu);
                // 10.11. (B3): Zustand eines anderen Containers (Rechner wiederverwendet) nie weiterblenden - verwerfen, der neue
                // Klang beginnt mit seinem eigenen ersten Wert. (Normalfall: Pool-Reset -> ResetNach; das hier haelt es auch ohne.)
                if (WiederverwendungKern.BehaelterGewechselt(z.Behaelter, sourceContainer)) { z.Vergessen(); z.Occ = null; z.OccBehaelter = null; dBehaelter++; }   // spitzen: neuer Klang erbt keinen angewandten Wert
                if (z.Ersatz && rp.Routes.Length == 0) return;          // Ersatzweg haelt seinen Wert, bis sich die Lage aendert
                z.Ersatz = false;
                z.Behaelter = sourceContainer;
                Vector3 q = sourceContainer.CurrentPosition;
                z.Umgekehrt = rev;
                z.A = rev ? listenerPosition : q; z.B = rev ? q : listenerPosition;
                z.RaumA = rev ? sys.ListenerCurrentRoom : sourceContainer.CurrentAudioRoom;
                z.RaumB = rev ? sourceContainer.CurrentAudioRoom : sys.ListenerCurrentRoom;
                z.Paar = rp.ID; z.Hat = true;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // Genau die Bedingung, unter der OnLateUpdate das Job-Ergebnis uebernimmt.
        static void SpaetVor(ParallelSoundPropagationCalculator __instance, out bool __state)
        {
            __state = false;
            try
            {
                __state = An && (__instance._isPositionChanged || __instance._forceUpdate) && !__instance._earlyOutThisFrame
                          && __instance._propagationJobScheduler != null && __instance._propagationJobScheduler.LastHandle.IsCompleted;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // 0.8.5: laeuft jedes Mal (nicht nur, wenn ein Job-Ergebnis kommt): neue Rechnung -> Routen uebernehmen; sonst die
        // Ueberblendung zeitlich fortschreiben, solange der Spielwert noch unserer ist.
        static void SpaetNach(ParallelSoundPropagationCalculator __instance, bool __state)
        {
            if (!An) return;
            long t0 = Laufzeit.Jetzt;
            int bild = Time.frameCount;
            try
            {
                if (!Tabelle.TryGetValue(__instance, out var z)) return;
                double jetzt = Time.realtimeSinceStartup;
                if (__instance._earlyOutThisFrame)
                {
                    // 0.8.8: Ersatzweg (kein Job, Wert aus UpdateNach) - nur weiter ueberblenden
                    if (z.Ersatz) { if (z.Wertung >= 0f && z.Gleit.Laeuft && __instance._propagationScore == z.Wertung) Schreiben(__instance, z, jetzt, false); return; }
                    if (z.Wertung >= 0f || z.Mix.Hat) z.Vergessen();
                    return;
                }
                if (__state && z.Hat)
                {
                    // 0.8.5 Bild-Budget: ein Klang, der schon seinen physikalischen Wert (gleiches Raumpaar) hat, wartet, wenn das
                    // Budget verbraucht ist; neue Klaenge (Einmal-Klaenge) werden immer sofort gerechnet.
                    float budget = BudgetMs != null ? BudgetMs.Value : 0f;
                    // 10.11. (spitzen): Budget auch VOR der Rechnung mit ihrer erwarteten Dauer pruefen (zuletzt gemessene Dauer dieses
                    // Raumpaars) - bisher wurde nur gefragt, ob das Budget schon voll ist, und eine 2-3-ms-Rechnung lief bei 0,9 ms noch an.
                    // Ist im Bild noch nichts verbraucht, rechnet die erste Rechnung immer (Fortschritt; MaxAufschub gilt weiter).
                    double schonMs = ZeitRechnen.ImBild(bild);
                    bool vorhersage = budget > 0f && schonMs > 0 && z.PaarGerechnet == z.Paar && schonMs + z.LetzteMs > budget;
                    if (budget > 0f && (schonMs >= budget || vorhersage))
                    {
                        if (vorhersage && schonMs < budget) dVorhersage++;
                        if (letztesUeberBild != bild) { letztesUeberBild = bild; dBudgetBilder++; }
                        if (z.Wertung >= 0f && z.PaarGerechnet == z.Paar && z.Aufschub < MaxAufschub)
                        {
                            z.Aufschub++; z.Nachholen = true; dAufgeschoben++;
                            __instance._propagationScore = z.Wertung;          // unser letzter Wert gilt weiter (nicht die Wegkosten)
                            NeuAnfordern(__instance);
                            if (z.Gleit.Laeuft) Schreiben(__instance, z, jetzt, false);
                            return;
                        }
                        dUeberBudget++;
                    }
                    float spiel = __instance._propagationScore;
                    long tr = Laufzeit.Jetzt;
                    // 10.11. (spitzen, N2): ein Ergebnis der Wegsuche mit nicht endlichem Wert wird verworfen - dann gilt die Rechnung der Mod
                    bool weg = WegsucheMischen(z, jetzt);
                    if (weg && !MischerEndlich(z)) { weg = false; dNaN++; }
                    bool ok = weg || Rechnen(__instance, z, spiel, jetzt);   // 0.8.8 Abschnitt 19 "Use": Wege der Wegsuche statt der Spielrouten
                    ZeitRechnen.Dazu(tr, bild);
                    z.LetzteMs = Laufzeit.Ms(Laufzeit.Jetzt - tr);
                    if (z.Nachholen) dNachgeholt++;
                    z.Aufschub = 0; z.Nachholen = false;
                    if (!ok) { ohneDaten++; z.Vergessen(); return; }
                    z.Spiel = spiel; z.PaarGerechnet = z.Paar;
                    if (!Schreiben(__instance, z, jetzt, true)) { ohneDaten++; return; }   // N2: kein endlicher Wert -> Spielwert bleibt
                    gerechnet++; summeSpiel += spiel * Physik.Dynamik.Value; summeNeu += z.Wertung * Physik.Dynamik.Value;
                }
                else if (z.Wertung >= 0f && z.Gleit.Laeuft && __instance._propagationScore == z.Wertung) Schreiben(__instance, z, jetzt, false);
            }
            catch (Exception e) { Statistik.Fehler(e); }
            finally { ZeitGesamt.Dazu(t0, bild); }
        }

        /// <summary>0.8.5: aufgeschobene Rechnung im naechsten Bild anfordern - der Rechner haelt die Position fuer "geaendert"
        /// (Combined/MultiSource), Continuous-Occluder bekommen den Zwang in OccUpdateVor (z.Nachholen).</summary>
        static void NeuAnfordern(ParallelSoundPropagationCalculator pc)
        {
            var c = pc._positionChangeChecker;
            if (c != null) c._previousListenerPos = new Vector3(1e9f, 1e9f, 1e9f);
        }

        static readonly double[] mixDb = new double[3];

        /// <summary>Wertung und Tiefpass schreiben (neu = gerade uebernommen: Summe der Routen wird das neue Ziel des Gleiters;
        /// sonst nur den Gleiter bis jetzt fortschreiben). 'jetzt' (Mischer-Uhr) ist seit 10.11. ohne Bedeutung fuer die Blende.</summary>
        static bool Schreiben(ParallelSoundPropagationCalculator pc, Zustand z, double jetzt, bool neu)
        {
            int bild = Time.frameCount;
            long t0 = Laufzeit.Jetzt;
            float dyn = Physik.Dynamik.Value;
            double tg = GleitJetzt, tau = GleitTau;
            if (neu)
            {
                // 0.8.8: Austritt als mittlere Richtung vom Hoerer (nicht Mittel der Punkte), dazu die Buendelung
                // 10.11. (klangfarbe, Log 18:00:34.603): Summe ueber die ECHTEN Banddaemmungen der Routen (nicht je Route und Band bei
                // Max. insulation gekappt), danach der Deckel nur fuer die Lautstaerke (WegeKern.Deckeln): das Bezugsband hoechstens
                // dyn, die Hochbaender behalten ihren Abstand. Der Gleiter laeuft so im gespielten Mass (eine Daemmung ueber dem Deckel
                // verzoegert nichts) und der Tiefpass folgt der echten Klangfarbe stetig durch den Deckel. z.Ueber = abgeschnittener Teil.
                if (!z.Mix.Summe(WegeKern.BandGrenzeDb, mixDb, V(z.Umgekehrt ? z.A : z.B), out var aus, out z.Buendel)) { mixDb[0] = mixDb[1] = mixDb[2] = dyn; }
                // richtungvorrang (16.11.): Richtung mit Praezedenz (der zuerst eintreffende Weg zaehlt +10 dB, RichtungKern.MischRichtung);
                // Pegel/Baender oben unveraendert aus der Energiesumme
                // tunneldiffus (17.11.): der trocken gespielte Diffusanteil zaehlt mit (Richtung.DiffusTrockenKappa)
                // wegrichtung2 (17.11.): nur die Seite des fuehrenden Wegs zieht die Richtung (RichtungKern.VorrangSeite), Schalter Richtung.Fuehrend
                if (!RichtungKern.MischRichtung(z.Mix.Eintraege, V(z.Umgekehrt ? z.A : z.B), Richtung.DiffusTrockenKappa(), Richtung.FuehrendAn, out aus, out z.Buendel)) { aus = default; z.Buendel = 0; }
                // 10.11. (spitzen, N2): nicht endliche Summe -> nie uebernehmen; ohne gueltigen Vorwert gilt der Spielwert (Zustand weg)
                if (!RoutenGleiter.Endlich(mixDb))
                {
                    dNaN++;
                    if (!z.Gleit.Hat) { z.Vergessen(); ZeitSchreiben.Dazu(t0, bild); return false; }
                    mixDb[0] = z.Gleit.Ziel[0]; mixDb[1] = z.Gleit.Ziel[1]; mixDb[2] = z.Gleit.Ziel[2];
                }
                else
                {
                    z.Ueber = Math.Max(0, mixDb[0] - dyn);
                    WegeKern.Deckeln(mixDb, dyn);
                    // lampenpegel (18.11.): gekappter Teil der Summe ueber dem Freifeld (nur Lampen lesen ihn)
                    double u = z.Mix.UeberFreiDb();
                    uZiel[0] = uZiel[1] = uZiel[2] = u;
                    // erster Wert sofort (wie Gleit ohne Vorwert): an der Tuer liegt der Raumweg nahe 0 dB, und der gekappte Teil setzt dort das
                    // Raumfeld des eigenen Raums fort (Kalibrierung.LampeDb) - ein Einblenden von 0 aus waere der Sprung
                    z.UGleit.ZielSetzen(uZiel, tg, tau);
                }
                // 10.11. (spitzen, Punkt 5): laufender Klang ohne Gleiterwert (nach Fruehabbruch/Hoererraumwechsel verworfen) gleitet vom
                // zuletzt ANGEWANDTEN Wert aus; sofort nur, wenn der Klang in diesem Bild neu beginnt (Neubeginn) oder kein Wert bekannt ist
                // (Einmal-Klaenge ohne eigenen Occluder). Annahme: PropagationEffect des Occluders ist der zuletzt angewandte Wert (Continuous:
                // = GetScore beim letzten Anwenden; Combined: die geglaettete Wertung). War der Raumweg aus (gleicher Raum, Wert 0), startet
                // er bei 0 dB - dort galt der Hindernisweg, an der Tuer nahezu frei.
                if (!z.Gleit.Hat)
                {
                    double vorher = AngewandtDb(pc, z, dyn);
                    if (WiederverwendungKern.VomAngewandtenStarten(false, z.NeuBild == bild, vorher) && z.Gleit.Vorbelegen(vorher, tg))
                    {
                        dVomAngewandt++;
                        // diffus-hall: der laufende Klang hatte zuletzt keinen bekannten Zusatz -> von 0 dB aus gleiten statt springen
                        if (!z.DGleit.Hat) z.DGleit.Vorbelegen(0, tg);
                    }
                }
                z.Gleit.ZielSetzen(mixDb, tg, tau);
                // diffus-hall: Diffusanteil der gemischten Routen -> Zusatzdaempfung des trockenen Signals (Hall bekommt sie als Send zurueck)
                z.DAnteil = z.Mix.DiffusAnteil();
                // hallsend (B2, 12.11.): Zusatz je Band aus dem Anteil dieses Bands (bis 12.11. alle drei = Bezugsband)
                dZiel[0] = DiffusKern.TrockenZusatzDb(z.DAnteil);
                dZiel[1] = DiffusKern.TrockenZusatzDb(z.Mix.DiffusAnteil(1));
                dZiel[2] = DiffusKern.TrockenZusatzDb(z.Mix.DiffusAnteil(2));
                z.DGleit.ZielSetzen(dZiel, tg, tau);
                // tunneldiffus: Zeitstruktur der Mischung (Physik.Trocken bewertet sie je Quelle mit der Dauer ihres Clips)
                for (int b = 0; b < 3; b++) { z.Mix.ZeitMischen(b, out var zl, out var zs); z.Zeit.Setzen(b, zl, zs); }
                // tunneldiffus Teil 2: frueher Anteil fuer Sprache (D50-Fenster)
                z.Zeit.HatFrueh = z.Mix.FruehMischen(out double zfr); z.Zeit.Frueh = (float)zfr;
                z.Austritt = aus;
                if (z.Gleitet && !z.ImTakt) { z.ImTakt = true; z.Pc = pc; takt.Add(z); }
            }
            else { z.Gleit.Fortschreiben(tg, tau); z.DGleit.Fortschreiben(tg, tau); z.UGleit.Fortschreiben(tg, tau); }
            var gd = z.Gleit.Db;
            mixDb[0] = gd[0]; mixDb[1] = gd[1]; mixDb[2] = gd[2];
            float w = WiederverwendungKern.Wertung(mixDb[0], dyn, MinWertung, z.Wertung);   // N2: nie NaN ans Spiel
            z.Wertung = w; pc._propagationScore = w;
            z.WertungTuer = 0f;                                        // der Tuer-Tiefpass steckt schon in den Baendern
            // 0.8.5: Fit (Minimumsuche) nur, wenn sich die Bandkurve oder die Einstellungen geaendert haben
            double m1 = mixDb[1] - mixDb[0], m2 = mixDb[2] - mixDb[0];
            float k = Wege.BandStaerke.Value, tp = Physik.TiefpassMin.Value;
            if (m1 != z.FitM1 || m2 != z.FitM2 || k != z.FitK || tp != z.FitTp)
            {
                float lpNeu = Wege.FitLp(m1, m2);
                z.FitLp = float.IsNaN(lpNeu) || float.IsInfinity(lpNeu) ? (float.IsNaN(z.FitLp) || z.FitLp <= 0f ? 22000f : z.FitLp) : lpNeu;   // N2
                z.FitM1 = m1; z.FitM2 = m2; z.FitK = k; z.FitTp = tp;
            }
            z.TiefpassBeugung = z.FitLp;
            ZeitSchreiben.Dazu(t0, bild);
            // Diagnose: groesster Pegelsprung je Bild (Summe) gegen "lauteste Route allein"
            if (bild == z.Bild) { z.LetztDb = w * dyn; return true; }
            float db = w * dyn;
            float alt = double.IsNaN(z.Mix.GewinnerDb) ? db : Mathf.Clamp((float)z.Mix.GewinnerDb, MinWertung * dyn, dyn);
            if (z.Bild >= 0)
            {
                float sn = Mathf.Abs(db - z.LetztDb), sa = Mathf.Abs(alt - z.LetztAlt);
                dBilder++;
                if (sn > dMaxNeu) dMaxNeu = sn;
                if (sa > dMaxAlt) dMaxAlt = sa;
                if (sn > 1f) dSpruengeNeu++;
                if (sa > 1f) dSpruengeAlt++;
                HoererSchritt(bild, sn);   // raumwechsel
            }
            z.Bild = bild; z.LetztDb = db; z.LetztAlt = alt;
            return true;
        }

        /// <summary>10.11. (spitzen): zuletzt angewandter Raumweg-Wert (dB) des Occluders dieses Rechners, NaN = unbekannt.</summary>
        static readonly double[] dZiel = new double[3];
        static readonly double[] uZiel = new double[3];   // lampenpegel (18.11.)

        static double AngewandtDb(ParallelSoundPropagationCalculator pc, Zustand z, float dyn)
        {
            var o = z.Occ;
            if (o == null || o._isDisposed || !ReferenceEquals(RechnerVon(o), pc)) return double.NaN;
            // nur ein LAUFENDER Klang: derselbe Container lief schon in einem frueheren Takt dieses Occluders (ein frischer Occluder hat
            // PropagationEffect 0 = Standardwert, der waere als Startwert falsch: neuer Klang zu laut, der dann leiser gleitet)
            if (!ReferenceEquals(z.OccBehaelter, o.SourceContainer) || z.OccSeitBild >= Time.frameCount) return double.NaN;
            return WiederverwendungKern.AngewandtDb(o.PropagationEffect, dyn);
        }

        /// <summary>N2: alle Eintraege des Mischers endlich.</summary>
        static bool MischerEndlich(Zustand z)
        {
            foreach (var e in z.Mix.Eintraege) if (!RoutenGleiter.Endlich(e.Db)) return false;
            return true;
        }

        static void ResetNach(ParallelSoundPropagationCalculator __instance)
        {
            try
            {
                if (System.Threading.Thread.CurrentThread.ManagedThreadId != hauptThread) return;
                if (Tabelle.TryGetValue(__instance, out var z)) { z.Vergessen(); z.Occ = null; z.OccBehaelter = null; }   // spitzen: Pool -> naechster Klang beginnt neu
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // Continuous(Propagated)SpatialAudioOccluder rechnen nur nach 1 m Bewegung neu (oder auf Zwang) und wenden nur dann an.
        // Solange eine Ueberblendung laeuft, wird der Zwang gesetzt - wie bei einer Tuer, die aufgeht (OnPortalStatusChanged).
        static void OccUpdateVor(SpatialAudioOccluder __instance)
        {
            if (!An) return;
            try
            {
                var pc = RechnerVon(__instance);
                if (pc == null) return;
                var z = Tabelle.GetValue(pc, Neu);
                z.Occ = __instance;   // 10.11. (spitzen): fuer das Anwenden je Gleitbild und den Startwert eines laufenden Klangs
                if (!ReferenceEquals(z.OccBehaelter, __instance.SourceContainer)) { z.OccBehaelter = __instance.SourceContainer; z.OccSeitBild = Time.frameCount; }
                if (z.Wertung < 0f || (!z.Gleit.Laeuft && !z.Nachholen)) return;
                switch (__instance)
                {
                    case ContinuousPropagatedSpatialAudioOccluder p: if (!p._forceUpdate) { p._forceUpdate = true; dZwang++; } break;
                    case ContinuousSpatialAudioOccluder c: if (!c._forceUpdate) { c._forceUpdate = true; dZwang++; } break;
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // ---------------------------------------------------------------- 10.11. (B2) Bild-Takt der Ueberblendung

        static readonly List<Zustand> takt = new List<Zustand>(64);
        static int taktBild = -1, dTakt, dBehaelter, dNeubeginn, dNeuReichweite, dNeuVeraltet;
        static SpatialAudioSystem taktSystem;

        /// <summary>Prefix SpatialAudioSystem.Update: jeder gleitende Raumweg wird JEDES Bild mit echter Zeit fortgeschrieben und
        /// geschrieben - unabhaengig davon, wann das Spiel seinen Rechner im Round-Robin aufruft. Jeder Leser (Anwenden des Spiels,
        /// neue Schuesse ueber OnSourceAdded/MultiSource, Richtung, Klang-Log) sieht also den Wert fuer genau dieses Bild.
        /// Faellt raus, sobald das Ziel erreicht ist, der Zustand vergessen wurde oder das Spiel den Wert selbst ueberschrieben hat
        /// (Fruehabbruch/neuer Raum - dann gilt dessen Wert, nicht unserer).</summary>
        static void BildTakt(SpatialAudioSystem __instance)
        {
            if (!ReferenceEquals(aufgewaermtSystem, __instance)) Aufwaermen(__instance);   // 10.11. (spitzen): einmal je Raid, so frueh wie moeglich
            HoererRaumBeobachten(__instance);   // raumwechsel: Diagnose, aendert nichts am Klang
            if (takt.Count == 0) return;
            int bild = Time.frameCount;
            if (bild == taktBild) return;
            taktBild = bild;
            try
            {
                if (!An || !ReferenceEquals(taktSystem, __instance))
                {
                    // aus, oder neues Audiosystem (neuer Raid): alte Rechner nicht mehr anfassen
                    foreach (var z in takt) { z.ImTakt = false; z.Pc = null; }
                    takt.Clear();
                    taktSystem = __instance;
                    return;
                }
                double jetzt = Time.realtimeSinceStartup;
                for (int i = takt.Count - 1; i >= 0; i--)
                {
                    var z = takt[i];
                    var pc = z.Pc;
                    bool weiter = pc != null && !pc._isDisposed && z.Wertung >= 0f && z.Gleitet && pc._propagationScore == z.Wertung;
                    if (weiter)
                    {
                        Schreiben(pc, z, jetzt, false); dTakt++;
                        // 10.11. (spitzen, Punkt 5): Continuous-Occluder setzen PropagationEffect = GetScore() nur in ihrem Round-Robin-Takt
                        // (SpatialAudioSystem.LateUpdate bedient je Bild EINEN Prozessor). Ohne Anwenden hier sprang der gespielte Pegel bei
                        // jedem Takt um alles, was der Gleiter inzwischen gelaufen war (src#-1514044 18:44:59.932 -> 18:45:00.228: -48,5 ->
                        // -24,4 dB). Darum: jedes Gleitbild die Spiel-Routine des Occluders aufrufen (dieselbe wie in seinem Takt). Nur fuer
                        // Zustaende, die gerade gleiten; Combined-Occluder glaetten selbst (Lerp je Takt) und bleiben, wie sie sind.
                        TaktAnwenden(z, pc);
                        weiter = z.Gleitet;
                    }
                    if (!weiter)
                    {
                        z.ImTakt = false; z.Pc = null;
                        int last = takt.Count - 1;
                        takt[i] = takt[last]; takt.RemoveAt(last);
                    }
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // ---------------------------------------------------------------- raumwechsel (10.11.): Diagnose Hoererraum-Wechsel
        // Befund 23:49 (Log 2026-11-10_23-40-10, MARK 2): der Hoererraum des Spiels ist der zuletzt BETRETENE Trigger-Raum
        // (AudioRoomStorage.AddListenerCurrentRoom: Liste, neuester vorn; Ein-/Austritt der Spielerkapsel in die BoxCollider), kein
        // Flattern: die Boxen von office_stairs_L_f4 (x ab 33,65) und upper_floor_hallway (x bis 33,63) beruehren sich nur, und der
        // Spieler ging fuenfmal ueber diese Linie (aus dem Log rekonstruierte Hoererpositionen x 33,0..34,6). Von 703 Spruengen des Raids
        // (>= 3 dB oder >= 0,7 Oktaven in <= 4 Bildern je Bot+Klangart) fielen 2 auf einen Hoererraum-Wechsel; 522 waren zwei gleichzeitige
        // Klaenge desselben Bots (z. B. Character "-" und gear_stereo26 im selben Bild, -32,1/-53,0 dB bei gleicher Daemmung 11,5 dB).
        // Diese Zaehlung zeigt im naechsten Raid unmittelbar, ob ein Hoererraum-Wechsel den angewandten Raumweg springen laesst:
        // groesster Schritt des angewandten Werts (dB je Bild) in den NachWechselBilder Bildern nach einem Wechsel gegen den Rest.
        const int NachWechselBilder = 6;
        static int hoererRaumId = int.MinValue, hoererWechselBild = int.MinValue / 2, dHoererWechsel, dSchritteNachWechsel, dSpruengeNachWechsel;
        static double dMaxNachWechsel;
        static string dHoererFolge;
        static SpatialAudioSystem hoererSystem;

        static void HoererRaumBeobachten(SpatialAudioSystem sys)
        {
            try
            {
                var r = sys.ListenerCurrentRoom;
                if (r == null) return;
                int id = r.ID;
                if (!ReferenceEquals(hoererSystem, sys)) { hoererSystem = sys; hoererRaumId = id; return; }   // neuer Raid: Startraum ist kein Wechsel
                if (id == hoererRaumId) return;
                hoererRaumId = id;
                hoererWechselBild = Time.frameCount;
                dHoererWechsel++;
                string n = r.Name ?? "?";
                if (dHoererFolge == null) dHoererFolge = n;
                else if (dHoererFolge.Length < 300) dHoererFolge += " > " + n;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void HoererSchritt(int bild, float sn)
        {
            if (bild - hoererWechselBild > NachWechselBilder) return;
            dSchritteNachWechsel++;
            if (sn > dMaxNachWechsel) dMaxNachWechsel = sn;
            if (sn > 1f) dSpruengeNachWechsel++;
        }

        static void TaktAnwenden(Zustand z, ParallelSoundPropagationCalculator pc)
        {
            var o = z.Occ;
            if (o == null || o._isDisposed || !(o is ContinuousPropagatedSpatialAudioOccluder || o is ContinuousSpatialAudioOccluder)) return;
            if (!ReferenceEquals(RechnerVon(o), pc)) { z.Occ = null; return; }
            var c = o.SourceContainer;
            if (c == null || !c.IsValid) return;
            long t0 = Laufzeit.Jetzt;
            try { o.UpdateOcclusionEffects(c, true); dTaktAngewandt++; }
            catch (Exception e) { Statistik.Fehler(e); }
            finally { ZeitTaktAnwenden.Dazu(t0, Time.frameCount); }
        }

        // ---------------------------------------------------------------- 10.11. (R1b/B3) erster Wert eines Klangs

        static void PrioVor(SoundOcclusionProcessor __instance, out Audio.AuxiliaryAudioUtils.EAudioSourcePriority __state) { __state = __instance.Priority; }

        /// <summary>Postfix UpdatePriority: kommt ein Prozessor aus OutOfRange (dort rechnete er nicht; auch beim Entstehen - der
        /// Startwert ist OutOfRange, der Rechner kommt aus dem Pool mit dem Wert seiner vorigen Quelle), gilt sein Wert nicht.</summary>
        static void PrioNach(SoundOcclusionProcessor __instance, Audio.AuxiliaryAudioUtils.EAudioSourcePriority __state)
        {
            if (!WiederverwendungKern.ZurueckInReichweite((int)__state, (int)__instance.Priority, (int)Audio.AuxiliaryAudioUtils.EAudioSourcePriority.OutOfRange)) return;
            if (Neubeginn(__instance)) dNeuReichweite++;
        }

        /// <summary>Prefix OnSourceAdded (die Reichweite des Containers ist schon gewachsen, das Spiel wendet gleich sofort an):
        /// steht im Rechner noch der Fruehabbruch "ausser Reichweite" (Wertung 1), der fuer die neue Reichweite nicht mehr gilt?</summary>
        static void HinzuVor(SoundOcclusionProcessor __instance)
        {
            try
            {
                if (!(__instance._occluder is CombinedSpatialAudioOccluder comb) || !(comb._propagationCalculator is ParallelSoundPropagationCalculator pc)) return;
                var c = __instance._audioSourceContainer;
                if (c == null || !c.IsValid) return;
                double dq = (comb.ListenerPos - c.CurrentPosition).sqrMagnitude;
                if (!WiederverwendungKern.FruehabbruchVeraltet(pc._earlyOutThisFrame, pc._propagationScore, dq, c.SqrMaxDistance)) return;
                if (Neubeginn(__instance)) dNeuVeraltet++;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>Raumweg-Rechner eines Combined-Prozessors JETZT fuer den aktuellen Stand rechnen (wie sein naechster Durchlauf mit
        /// Zwang, nur sofort: OnUpdate, Job abschliessen, OnLateUpdate - die eigenen Postfixe laufen mit) und die Glaettung des
        /// Spiels (_smoothedPropagationEffect, Lerp je Aufruf) auf diesen ersten Wert setzen, statt vom alten her einzublenden.
        /// Der Hindernis-Rechner wird nicht angefasst; ForceUpdate laesst den naechsten regulaeren Durchlauf alles neu rechnen.</summary>
        static bool Neubeginn(SoundOcclusionProcessor p)
        {
            long t0 = 0; int tBild = 0;   // 10.11. (spitzen): ganze Dauer (Job des Spiels, unsere Rechnung, Kette) - bisher von keinem Zaehler erfasst
            try
            {
                if (!Plugin.Aktiv.Value || !Stetigkeit.An) return false;
                if (!(p._occluder is CombinedSpatialAudioOccluder comb) || comb._isDisposed) return false;
                if (!(comb._propagationCalculator is ParallelSoundPropagationCalculator pc) || pc._isDisposed || pc._propagationJobScheduler == null) return false;
                var c = p._audioSourceContainer;
                if (c == null || !c.IsValid || p._listenerTransform == null) return false;
                var raum = c.CurrentAudioRoom;
                if (raum == null || !raum.IsValid) return false;          // Raum des Containers noch nicht bestimmt: das Spiel rechnet spaeter
                if (!pc._earlyOutThisFrame && !pc.LastHandle.IsCompleted) return false;   // Job laeuft: dessen Ergebnis ist frisch
                var z = Tabelle.GetValue(pc, Neu);
                int bild = Time.frameCount;
                if (z.NeuBild == bild) return false;
                t0 = Laufzeit.Jetzt; tBild = bild;
                if (neuBildNr != bild) { neuBildNr = bild; dNeuImBild = 0; }
                if (++dNeuImBild > dNeuMaxBild) dNeuMaxBild = dNeuImBild;
                z.Vergessen();
                z.NeuBild = bild;
                pc.ManualUpdate(c, comb.ListenerPos, true);
                if (!pc._earlyOutThisFrame) pc.LastHandle.Complete();
                pc.ManualLateUpdate();
                float s = comb._propagationCalculator.GetScore();         // ganze Kette (Wand zwischen Raeumen, Physik, SEB)
                comb._smoothedPropagationEffect = Mathf.Clamp01(s);
                comb._lastPropagationEffect = -1f;                         // naechstes Anwenden rechnet voll
                comb.ForceUpdate();
                dNeubeginn++;
                return true;
            }
            catch (Exception e) { Statistik.Fehler(e); return false; }
            finally { if (t0 != 0) ZeitNeu.Dazu(t0, tBild); }
        }

        /// <summary>raid2242richtung (17.11.): Neubeginn fuer ContinuousPropagated (wie Neubeginn fuer Combined), NUR beim Start eines Klangs,
        /// dessen Prozessor gerade durch die nachgerechnete Reichweite aus OutOfRange kam (Reichweite.GespieltVor) - z. B. Granataufschlag.
        /// Bisher rechnete so ein Prozessor erst in seinem Round-Robin-Takt (ein Prozessor je Bild) und der Aufschlag spielte bis dahin mit
        /// Wert 0 (ungedaempft). Nicht in PrioNach: dort kaemen beim Raid-Start alle Szenenquellen auf einmal (Schleifen gleiten ohnehin).
        /// Job jetzt rechnen und abschliessen (eigene Postfixe UpdateNach/SpaetNach laufen mit), PropagationEffect = Wertung, _forceUpdate.</summary>
        internal static bool NeubeginnBeimStart(SoundOcclusionProcessor p)
        {
            long t0 = 0; int tBild = 0;
            try
            {
                if (!An || !Plugin.Aktiv.Value || !(p._occluder is ContinuousPropagatedSpatialAudioOccluder o)) return false;
                return NeubeginnFortlaufend(p, o, ref t0, ref tBild);
            }
            catch (Exception e) { Statistik.Fehler(e); return false; }
            finally { if (t0 != 0) ZeitNeu.Dazu(t0, tBild); }
        }

        static bool NeubeginnFortlaufend(SoundOcclusionProcessor p, ContinuousPropagatedSpatialAudioOccluder o, ref long t0, ref int tBild)
        {
            if (o._isDisposed || !(o._propagationCalculator is ParallelSoundPropagationCalculator pc) || pc._isDisposed || pc._propagationJobScheduler == null) return false;
            var c = p._audioSourceContainer;
            if (c == null || !c.IsValid || p._listenerTransform == null) return false;
            var raum = c.CurrentAudioRoom;
            if (raum == null || !raum.IsValid) return false;
            if (!pc._earlyOutThisFrame && !pc.LastHandle.IsCompleted) return false;
            var z = Tabelle.GetValue(pc, Neu);
            int bild = Time.frameCount;
            if (z.NeuBild == bild) return false;
            t0 = Laufzeit.Jetzt; tBild = bild;
            if (neuBildNr != bild) { neuBildNr = bild; dNeuImBild = 0; }
            if (++dNeuImBild > dNeuMaxBild) dNeuMaxBild = dNeuImBild;
            z.Vergessen();
            z.NeuBild = bild;
            pc.ManualUpdate(c, o.ListenerPos, true);
            if (!pc._earlyOutThisFrame) pc.LastHandle.Complete();
            pc.ManualLateUpdate();
            o.PropagationEffect = Mathf.Clamp01(pc.GetScore());
            o._forceUpdate = true;
            dNeubeginn++; dNeuFortlaufend++;
            return true;
        }
        static int dNeuFortlaufend;

        static void MultiVor(MultiSourceSpatialAudioOccluder __instance, AudioSourceContainer sourceContainer)
        {
            Multi = null; MultiBehaelter = sourceContainer;   // wegrichtung2: Container des gerade angewandten Einmal-Klangs
            try
            {
                if (sourceContainer != null && __instance._activePropagationCalculatorsByContID.TryGetValue(sourceContainer.ID, out var c)) Multi = c;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static Exception MultiEnde(Exception __exception) { Multi = null; MultiBehaelter = null; return __exception; }
        /// <summary>wegrichtung2 (17.11.): Container des Einmal-Klangs, den MultiSourceSpatialAudioOccluder gerade anwendet. occ.SourceContainer
        /// ist dort der zuletzt ANGEMELDETE Einmal-Klang (SetAudioSourceContainer), nicht der angewandte - Richtung.Melden fand deshalb fuer
        /// Einschlaege, Huelsen, Tueren, Fenster und ferne Explosionen nie ihren Raumweg (Log 17.11.: 20 % der hoerbaren Zeilen ohne Richtung).</summary>
        internal static AudioSourceContainer MultiBehaelter;
        static void ApplyVor(SpatialAudioOccluder __instance, BetterSource source)
        {
            applyOcc = __instance; applySrc = source;
            // wegrichtung2: Einmal-Klaenge werden nur EINMAL angewandt (danach gibt das Spiel den Rechner zurueck) - die Richtung schon vor dem
            // Anwenden setzen, damit sie ab dem ersten Bild und in dessen Klang-Log-Zeile gilt (KlangLog.NachApply laeuft vor ApplyEnde).
            // Die Wertung dieses Bilds steht schon fest (OnUpdateOcclusionEffects: CalculateFinalOcclusionParams vor ApplyOcclusionToSource).
            if (__instance is MultiSourceSpatialAudioOccluder && source != null) Richtung.Melden(__instance, source);
        }
        static BetterSource applySrc;
        /// <summary>0.8.5: Quelle, die gerade angewendet wird (Hoerbarkeit je Band in Physik.FilterNach).</summary>
        internal static BetterSource AnwendQuelle => applySrc;
        /// <summary>0.8.4 Stufe 1: Occluder, dessen Quelle gerade angewendet wird (fuer den Band-Tiefpass in Physik.FilterNach).</summary>
        internal static SpatialAudioOccluder AnwendOcc => applyOcc;

        /// <summary>0.8.4 Stufe 1: Wertung und Tiefpass der physikalischen Route (fuer die Summe mit der Wand zwischen Raeumen).
        /// false = Route nicht physikalisch gerechnet (dann gilt die Spielwertung und die alte Abbildung).</summary>
        internal static bool Route(SpatialAudioOccluder occ, ParallelSoundPropagationCalculator pc, out float wertung, out float lp)
        {
            wertung = 0f; lp = 22000f;
            try
            {
                if (!An || pc == null || !Tabelle.TryGetValue(pc, out var z) || z.Wertung < 0f || pc._propagationScore != z.Wertung) return false;
                wertung = z.Wertung;
                lp = Mathf.Min(Physik.Abbildung(z.WertungTuer), z.TiefpassBeugung);
                return true;
            }
            catch (Exception e) { Statistik.Fehler(e); return false; }
        }
        static Exception ApplyEnde(Exception __exception)
        {
            if (__exception == null && applyOcc != null && applySrc != null) Richtung.Melden(applyOcc, applySrc);   // 0.8.8
            applyOcc = null; applySrc = null; return __exception;
        }

        /// <summary>0.8.8 Richtung: energiegewichteter Austrittspunkt und Daemmung (dB) des Raumwegs eines Rechners, solange sein
        /// Wert gilt und zu diesem Container gehoert. ersatz = Ersatzweg ohne Spielroute.</summary>
        internal static bool RichtungsDaten(ParallelSoundPropagationCalculator pc, AudioSourceContainer c, out V3 austritt, out float routeDb, out bool ersatz)
            => RichtungsDaten(pc, c, out austritt, out routeDb, out ersatz, out _);

        internal static bool RichtungsDaten(ParallelSoundPropagationCalculator pc, AudioSourceContainer c, out V3 austritt, out float routeDb, out bool ersatz, out double buendel)
        {
            austritt = default; routeDb = float.NaN; ersatz = false; buendel = 0;
            if (!An || pc == null || !Tabelle.TryGetValue(pc, out var z) || z.Wertung < 0f || pc._propagationScore != z.Wertung) return false;
            if (!ReferenceEquals(z.Behaelter, c) || z.Austritt.IstNull) return false;
            austritt = z.Austritt; routeDb = z.Wertung * Physik.Dynamik.Value; ersatz = z.Ersatz; buendel = z.Buendel;
            return true;
        }

        /// <summary>zweitweg (17.11.): gemischte Routen eines Rechners (nur lesen!) und Hoerer im Job-Bezug, solange sein Wert gilt und zu diesem
        /// Container gehoert (dieselben Bedingungen wie RichtungsDaten).</summary>
        internal static bool Mischung(ParallelSoundPropagationCalculator pc, AudioSourceContainer c, out System.Collections.Generic.List<RoutenEintrag> eintraege, out V3 hoerer)
        {
            eintraege = null; hoerer = default;
            if (!An || pc == null || !Tabelle.TryGetValue(pc, out var z) || z.Wertung < 0f || pc._propagationScore != z.Wertung) return false;
            if (!ReferenceEquals(z.Behaelter, c) || z.Ersatz) return false;
            eintraege = z.Mix.Eintraege; hoerer = V(z.Umgekehrt ? z.A : z.B);
            return eintraege != null && eintraege.Count > 0;
        }

        // ---------------------------------------------------------------- fuer Physik / Klang-Log

        /// <summary>Raumweg-Rechner eines Occluders (MultiSource: der des gerade bearbeiteten Einmal-Klangs).</summary>
        internal static ParallelSoundPropagationCalculator RechnerVon(SpatialAudioOccluder occ)
        {
            switch (occ)
            {
                case MultiSourceSpatialAudioOccluder _: return Multi;
                case CombinedSpatialAudioOccluder c: return c._propagationCalculator as ParallelSoundPropagationCalculator;
                case ContinuousPropagatedSpatialAudioOccluder p: return p._propagationCalculator as ParallelSoundPropagationCalculator;
                case ContinuousSpatialAudioOccluder s: return s._propagationCalculator as ParallelSoundPropagationCalculator;
            }
            return null;
        }

        static Zustand Gilt(SpatialAudioOccluder occ, out ParallelSoundPropagationCalculator pc)
        {
            pc = null;
            if (!An || occ == null || !occ.IsPropagationActive) return null;
            pc = RechnerVon(occ);
            if (pc == null || !Tabelle.TryGetValue(pc, out var z) || z.Wertung < 0f) return null;
            // nur solange der Spielwert noch unserer ist (sonst: Fruehabbruch/neuer Raum)
            return pc._propagationScore == z.Wertung ? z : null;
        }

        /// <summary>Aus Physik.LautstaerkeNach: Faktor draussen/drinnen der Klanggruppe entfernen (Heuristik auf Wegkosten).</summary>
        internal static void UmweltEntfernen(SpatialAudioOccluder occ)
        {
            try
            {
                var z = Gilt(occ, out _);
                if (z == null) return;
                float pe = Mathf.Clamp01(occ.PropagationEffect);
                if (pe > 1e-4f) z.Umwelt = letzteUmwelt = occ.OcclusionEffect / pe;
                occ.OcclusionEffect = pe;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>10.11. (klangfarbe, Review B4): Bandkurve der physikalischen Route (wie Wege.BandKurve): m1/m2 = Mehrdaemmung
        /// 1 kHz / 4 kHz gegenueber dem Bezugsband aus den geglitteten ECHTEN Baendern der Routen (Tuerblatt je Band nach TuerKern,
        /// Beugung, Raeume), lp = Fit daraus. false = Route gilt nicht (oder die Wand zwischen Raeumen gewann).</summary>
        internal static bool BandKurve(SpatialAudioOccluder occ, out float lp, out double m1, out double m2)
        {
            lp = 22000f; m1 = m2 = 0;
            if (!Tiefpass(occ, out lp, out _)) return false;
            var z = Gilt(occ, out _);
            if (z == null || !z.Gleit.Hat) return false;
            var gd = z.Gleit.Db;
            m1 = gd[1] - gd[0]; m2 = gd[2] - gd[0];
            return true;
        }

        /// <summary>10.11. (klangfarbe): echte Banddaemmung der Route (dB je Band, ohne Deckel) fuer die Summe mit der Wand zwischen
        /// Raeumen. Waehrend des Gleitens: geglittete Baender + der beim Ziel abgeschnittene Teil (Naeherung).</summary>
        internal static bool EchteBaender(SpatialAudioOccluder occ, ParallelSoundPropagationCalculator pc, double[] db)
        {
            try
            {
                if (!An || pc == null || !Tabelle.TryGetValue(pc, out var z) || z.Wertung < 0f || pc._propagationScore != z.Wertung || !z.Gleit.Hat) return false;
                var gd = z.Gleit.Db;
                for (int b = 0; b < 3; b++) db[b] = gd[b] + z.Ueber;
                return true;
            }
            catch (Exception e) { Statistik.Fehler(e); return false; }
        }

        /// <summary>diffus-hall: um wie viel dB der gerichtete Anteil der physikalischen Route leiser ist als ihre Summe (geglitten,
        /// >= 0). false = Route gilt nicht oder die Wand zwischen Raeumen gewann (dann Wege/Wand-Anteil, siehe Wege.DiffusZusatz).</summary>
        internal static bool DiffusZusatz(SpatialAudioOccluder occ, out double zusatz, out Zustand zustand, double[] zusatzB = null)
        {
            zusatz = 0; zustand = null;
            try
            {
                if (!Tiefpass(occ, out _, out _)) return false;   // Gilt + "Wand gewann" wie beim Tiefpass
                var z = Gilt(occ, out _);
                if (z == null || !z.DGleit.Hat) return false;
                zusatz = Math.Max(0, z.DGleit.Db[0]);
                zustand = z;
                Baender(z, zusatzB);
                return true;
            }
            catch (Exception e) { Statistik.Fehler(e); return false; }
        }

        /// <summary>tunneldiffus (17.11.): Zeitstruktur der gemischten Routen (ZeitKern) des geltenden Raumwegs; false = keine.</summary>
        internal static bool ZeitDaten(Zustand z, out ZeitKern.Baender zeit)
        {
            zeit = default;
            if (z == null || !z.Zeit.Hat) return false;
            zeit = z.Zeit;
            return true;
        }

        /// <summary>hallsend (B2): Trocken-Zusatz je Band (dB, geglitten) des Zustands nach aus[0..2]; aus = null -> nichts.</summary>
        static void Baender(Zustand z, double[] aus)
        {
            if (aus == null) return;
            for (int b = 0; b < 3; b++) aus[b] = Math.Max(0, z.DGleit.Db[b]);
        }

        /// <summary>galpegel (10.11.): wie DiffusZusatz, aber unabhaengig davon, ob die Wand zwischen Raeumen gewann - die Wand verknuepft
        /// den Anteil der Route mit ihrem eigenen (WandZwischenRaeumen.DiffusZusatz).</summary>
        internal static bool RoutenDiffus(SpatialAudioOccluder occ, out double zusatz, out Zustand zustand, double[] zusatzB = null)
        {
            zusatz = 0; zustand = null;
            try
            {
                var z = Gilt(occ, out _);
                if (z == null || !z.DGleit.Hat) return false;
                zusatz = Math.Max(0, z.DGleit.Db[0]);
                zustand = z;
                Baender(z, zusatzB);
                return true;
            }
            catch (Exception e) { Statistik.Fehler(e); return false; }
        }

        /// <summary>10.11. (klangfarbe): Teil der echten Routendaemmung (Bezugsband) ueber Max. insulation, 0 = keiner / unbekannt.</summary>
        internal static float UeberDeckel(ParallelSoundPropagationCalculator pc)
        {
            if (!An || pc == null || !Tabelle.TryGetValue(pc, out var z) || z.Wertung < 0f || pc._propagationScore != z.Wertung) return 0f;
            return (float)z.Ueber;
        }

        /// <summary>lampenpegel (18.11.): geltender Raumweg dieses Occluders - gekappter Teil ueber dem Freifeld (dB, geglitten, &gt;= 0) und die
        /// gespielte (geglittete, gekappte) Summe der Routen im Bezugsband. false = kein eigener Raumweg (gleicher Raum, Spielwert, aus).</summary>
        internal static bool LampeUeber(SpatialAudioOccluder occ, out double ueberDb, out double routeDb)
        {
            ueberDb = 0; routeDb = double.NaN;
            var z = Gilt(occ, out _);
            if (z == null || !z.UGleit.Hat || !z.Gleit.Hat) return false;
            ueberDb = Math.Max(0, z.UGleit.Db[0]);
            routeDb = z.Gleit.Db[0];
            return true;
        }

        /// <summary>lampenpegel (18.11.): Absorptionsflaeche A (m2) und Volumen V (m3) eines Raums, wie sie der Raumweg fuer diesen Raum
        /// rechnet (RaumwegNetz.RaumFlaeche: Waende mit gemessenem alpha + offene Portale). false = draussen / nicht gemessen.</summary>
        internal static bool RaumAV(ISpatialAudioRoom r, out double a, out double v)
        {
            a = -1; v = 0;
            if (!An || r == null) return false;
            int i = Netz.Von(r);
            if (i < 0) return false;
            a = Netz.RaumFlaeche(i, Alpha.Value, out v);
            return a > 0;
        }

        /// <summary>Aus Physik.FilterNach: Tiefpass fuer den Klang, der gerade angewendet wird (nur wenn die Route gewinnt).</summary>
        internal static bool Tiefpass(out float beugung, out float wertungTuer) => Tiefpass(applyOcc, out beugung, out wertungTuer);

        /// <summary>0.8.5: wie Tiefpass, fuer einen beliebigen Occluder (Physik.LautstaerkeNach / Klang-Log).</summary>
        internal static bool Tiefpass(SpatialAudioOccluder occ, out float beugung, out float wertungTuer)
        {
            beugung = 22000f; wertungTuer = 0f;
            try
            {
                var z = Gilt(occ, out var pc);
                if (z == null) return false;
                // Wand zwischen Raeumen gewann? Aus dem UNGEGLAETTETEN Wand-Wert entscheiden (wie ScorePostfix: min(Route, Wand)).
                // Bis 10.11.: Vergleich mit occ.PropagationEffect - das ist der vom Spiel geglaettete Wert (Lerp); stieg die
                // Wertung minimal, hinkte er hinterher und der Tiefpass sprang bildweise auf die Abbildung (~10 kHz) = Flattern.
                if (WandZwischenRaeumen.WandFuer(pc, out float wand, out _, out _, out _) && wand < z.Wertung - 1e-4f) return false;
                beugung = z.TiefpassBeugung; wertungTuer = z.WertungTuer;
                return true;
            }
            catch (Exception e) { Statistik.Fehler(e); return false; }
        }

        /// <summary>Klang-Log: Portalkette und Spielwert der Route (nur path=rooms).</summary>
        internal static string LogText(SpatialAudioOccluder occ, out ParallelSoundPropagationCalculator pc)
        {
            var z = Gilt(occ, out pc);
            if (z == null) return null;
            var inv = CultureInfo.InvariantCulture;
            return z.Kette + (float.IsNaN(z.Umwelt) ? "" : "; indoor/outdoor factor x" + z.Umwelt.ToString("F2", inv) + " ignored")
                   + "; rolloff x" + occ.CurrentParameters.OcclusionRolloffScale.ToString("F2", inv) + RoutenText(z) + DiffusText(z);
        }

        /// <summary>diffus-hall: Klang-Log "diffuse share 99 % -> dry +20.5 dB, reverb send -3.0 -> +17.5 dB" (zuletzt angewandt).</summary>
        static string DiffusText(Zustand z)
        {
            var inv = CultureInfo.InvariantCulture;
            string t = "; diffuse share " + (z.DAnteil * 100).ToString("F0", inv) + " %";
            if (!z.DGleit.Hat) return t;
            t += " (dry " + z.DGleit.Db[0].ToString("+0.0;-0.0", inv) + " dB of the sum";
            if (!float.IsNaN(z.DTrocken)) t += ", applied dry " + z.DTrocken.ToString("+0.0;-0.0", inv) + " dB, reverb send " + z.DSend.ToString("+0.0;-0.0", inv) + " dB";
            else t += ", not applied (no reverb path or switch off)";
            return t + ")";
        }

        /// <summary>0.8.5 Klang-Log: je Route Pegel, Rang, Gewicht (aktuell -> Ziel) und Blendzustand, dann die Summe und die Richtung.</summary>
        static string RoutenText(Zustand z)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(200);
            float dyn = Physik.Dynamik.Value;
            sb.Append(" | routes (").Append(Wege.Summe ? "energy sum, soft rank N " + RangN + " width " + BreiteJetzt.ToString("F0", inv) + " dB, blend " + (BlendEinstellungS * 1000).ToString("F0", inv) + " ms" : "loudest route").Append("):");
            int k = 0;
            foreach (var e in z.Mix.Eintraege)
            {
                if (k++ >= 6) { sb.Append(" +").Append(z.Mix.Eintraege.Count - 6).Append(" more"); break; }
                sb.Append(k == 1 ? " " : "; ");
                if (e.N == 0) sb.Append("outdoors over the obstacles");
                for (int i = 0; i < e.N; i++)
                {
                    var p = e.Portale[i] < portalJeIndex.Length ? portalJeIndex[e.Portale[i]] : null;
                    if (i > 0) sb.Append('>');
                    sb.Append(p != null ? (string.IsNullOrEmpty(p.portalName) ? p.name : p.portalName) : "").Append('#').Append(e.Portale[i]);
                    if (p != null) sb.Append('@').Append(p.ID);   // tabellenneu: Portal-ID (= QId im Wegenetz; der Index ist die Reihenfolge des Spiels)
                }
                sb.Append(" -").Append(e.Db[0].ToString("F1", inv)).Append(" dB");
                if (e.Rang > 0) sb.Append(" rank ").Append(e.Rang);
                sb.Append(" w ").Append(e.W.ToString("F2", inv));
                if (Math.Abs(e.W - e.Ziel) > 1e-3) sb.Append("->").Append(e.Ziel.ToString("F2", inv));
                sb.Append(e.Geist ? " ghost fading out" : !e.Da ? " gone, fading out" : e.W < e.Ziel - 1e-3 ? " fading in" : e.W > e.Ziel + 1e-3 ? " fading out" : " steady");
            }
            sb.Append(" => -").Append((z.Wertung * dyn).ToString("F1", inv)).Append(" dB");
            // 10.11. (B2): laeuft die Ueberblendung, das Ziel und die Zeitkonstante (dB-Gleiten jedes Bild)
            if (z.Gleit.Hat && z.Gleit.Laeuft)
                sb.Append(" (gliding to -").Append(Math.Min(dyn, z.Gleit.Ziel[0]).ToString("F1", inv)).Append(" dB, tau ").Append((GleitTau * 1000).ToString("F0", inv)).Append(" ms)");
            if (!double.IsNaN(z.Mix.GewinnerDb)) sb.Append(" (loudest alone -").Append(Math.Min(dyn, z.Mix.GewinnerDb).ToString("F1", inv)).Append(')');
            // Richtung nur zur Information: das Spiel hat keine virtuelle Quellposition (siehe CHANGELOG 0.8.5)
            V3 h = V(z.Umgekehrt ? z.A : z.B);
            if (!z.Austritt.IstNull && !double.IsNaN(z.Mix.GewinnerDb))
                sb.Append("; weighted exit vs. loudest door ").Append(RoutenMischer.Winkel(h, z.Austritt, z.Mix.GewinnerAustritt).ToString("F0", inv)).Append(" deg");
            return sb.ToString();
        }

        internal static void Loggen()
        {
            Richtung.Loggen(); Zweitstimme.Loggen(); Ersatzweg.Loggen(); FreiLoggen();   // 0.8.8; mehrweg: Stimmenvorrat
            // 0.8.5 Leistung: Zeit auf dem Hauptfaden je Teil (Summe in 30 s, groesster Wert in einem Bild)
            if (!ZeitGesamt.Leer || !ZeitWege.Leer || !ZeitLaut.Leer)
            {
                string budget = BudgetMs != null && BudgetMs.Value > 0f ? $"budget {BudgetMs.Value:F1} ms/frame" : "no budget";
                Plugin.Log.LogInfo($"[Diagnostics] Timing (main thread, 30 s): {ZeitGesamt.Text()}; of it {ZeitRechnen.Text()} and {ZeitSchreiben.Text()}; " +
                                   $"{dTreffer} route sets reused, {dFehl} calculated ({Netz.DGekappt} routes skipped by the level bound, {Netz.DRaumA} room absorptions, {Netz.DRaumAGespart} from the frame cache, " +
                                   $"{Netz.CacheAnzahl} cached sets); {budget}: {dAufgeschoben} calculations deferred, {dNachgeholt} caught up, " +
                                   $"{dUeberBudget} new sounds over budget, {dBudgetBilder} frames at the budget | {ZeitWege.Text()}; {ZeitLaut.Text()}; {ZeitFilter.Text()}.");
                dTreffer = dFehl = dAufgeschoben = dNachgeholt = dUeberBudget = dBudgetBilder = 0; Netz.DRaumA = Netz.DRaumAGespart = Netz.DGekappt = 0;
            }
            if (dRechnungen > 0 || !ZeitNeu.Leer || !ZeitTaktAnwenden.Leer || schnappMax > 0) Plugin.Log.LogInfo(SpitzenText());   // 10.11. (spitzen)
            if (dMisch > 0 || dNeubeginn > 0)
                Plugin.Log.LogInfo($"[Diagnostics] Room paths (physics): {dMisch} route sets mixed ({(Wege.Summe ? $"soft rank N {RangN}, width {BreiteJetzt:F0} dB, blend {BlendEinstellungS * 1000:F0} ms as dB glide tau {GleitTau * 1000:F0} ms" : "loudest route")}), " +
                                   $"{dTakt} glide steps written per frame outside the game's turn, {dBehaelter} states dropped on a container change, " +
                                   $"{dNeubeginn} first values calculated at once ({dNeuReichweite} back in range/new, {dNeuVeraltet} stale 'out of range' value on a new sound, {dNeuFortlaufend} continuous sounds whose range grew at play), " +
                                   $"on average {(double)dMischRouten / dMisch:F1} routes contributing, {dNeue} faded in, {dGeister} ghosts (same route jumped > {RoutenMischer.GeistDb:F0} dB), " +
                                   $"{dZwang} forced updates of continuous sounds while blending; largest room-path level step per frame {dMaxNeu:F1} dB " +
                                   $"(loudest route alone {dMaxAlt:F1} dB), frames with a step > 1 dB {dSpruengeNeu} (loudest alone {dSpruengeAlt}) of {dBilder}.");
            if (dHoererWechsel > 0)
                Plugin.Log.LogInfo($"[Diagnostics] Listener room changes (30 s): {dHoererWechsel} ({dHoererFolge}); in the {NachWechselBilder} frames after a change " +
                                   $"{dSchritteNachWechsel} room-path values written, largest step {dMaxNachWechsel:F1} dB per frame, {dSpruengeNachWechsel} steps > 1 dB " +
                                   $"(whole 30 s: largest {dMaxNeu:F1} dB, {dSpruengeNeu} steps > 1 dB of {dBilder}).");
            dHoererWechsel = dSchritteNachWechsel = dSpruengeNachWechsel = 0; dMaxNachWechsel = 0; dHoererFolge = null;
            dMisch = dMischRouten = dGeister = dNeue = dZwang = dBilder = dSpruengeNeu = dSpruengeAlt = 0; dMaxNeu = dMaxAlt = 0;
            dTakt = dBehaelter = dNeubeginn = dNeuReichweite = dNeuVeraltet = dNeuFortlaufend = 0;
            // diffus-hall: wie oft der Diffusanteil in den Hall ging (Mittel des Zusatzes = trocken leiser = Reverb-Send hoeher)
            if (Physik.DiffusAngewandt + Physik.DiffusOhneHall > 0)
            {
                Plugin.Log.LogInfo($"[Diagnostics] Diffuse sound as reverb: {Physik.DiffusAngewandt} applications, dry quieter / reverb send higher by " +
                                   $"{(Physik.DiffusAngewandt > 0 ? Physik.DiffusSummeDb / Physik.DiffusAngewandt : 0):F1} dB on average, {Physik.DiffusGedeckelt} at the +20 dB send limit (rest stays dry), " +
                                   $"{Physik.DiffusOhneHall} without a reverb path (sum stays dry).");
                Physik.DiffusAngewandt = Physik.DiffusOhneHall = Physik.DiffusGedeckelt = 0; Physik.DiffusSummeDb = 0;
            }
            // hallsend (12.11.): Hall im selben Raum hinter Hindernissen (A2), Klangfarbe des trockenen Teils (B2), Send-Besitz (Fund 1)
            if (Physik.RaumHallAngewandt + Physik.TrockenFarbe + Physik.SendFreigegeben + Physik.SendZurueckGesetzt > 0)
            {
                Plugin.Log.LogInfo($"[Diagnostics] Reverb send: {Physik.RaumHallAngewandt} obstacle applications in the same room kept the room reverb " +
                                   $"(send +{(Physik.RaumHallAngewandt > 0 ? Physik.RaumHallSummeDb / Physik.RaumHallAngewandt : 0):F1} dB on average, {Physik.RaumHallGedeckelt} at the +20 dB limit, " +
                                   $"reverb short there by {(Physik.RaumHallGedeckelt > 0 ? Physik.RaumHallFehlDb / Physik.RaumHallGedeckelt : 0):F1} dB on average); " +
                                   $"{Physik.TrockenFarbe} band curves of the dry part (mean 4 kHz extra {(Physik.TrockenFarbe > 0 ? Physik.TrockenFarbeSumme / Physik.TrockenFarbe : 0):+0.0;-0.0} dB vs. the sum); " +
                                   $"raised send reset to the preset {Physik.SendFreigegeben}x on release, {Physik.SendZurueckGesetzt}x for a sound without split.");
                Physik.RaumHallAngewandt = Physik.RaumHallGedeckelt = Physik.TrockenFarbe = Physik.SendFreigegeben = Physik.SendZurueckGesetzt = 0;
                Physik.RaumHallSummeDb = Physik.RaumHallFehlDb = Physik.TrockenFarbeSumme = 0;
            }
            if (gerechnet + ohneDaten == 0) return;
            Plugin.Log.LogInfo($"[Diagnostics] Room paths (physics): {gerechnet} routes recalculated ({routen} candidates), game route cost read as insulation on average " +
                               $"{(gerechnet > 0 ? summeSpiel / gerechnet : 0):F1} dB -> physical {(gerechnet > 0 ? summeNeu / gerechnet : 0):F1} dB, {ohneDaten} without data (game value kept)" +
                               (float.IsNaN(letzteUmwelt) ? "." : $", indoor/outdoor factor of the sound group ignored (last x{letzteUmwelt:F2})."));
            gerechnet = routen = ohneDaten = 0; summeSpiel = summeNeu = 0;
        }

        // ---------------------------------------------------------------- Rechnung

        // 0.8.5 Leistung: Raumnetz einmal je Raid, Routenschleife als reine Rechnung (RaumwegNetz.cs). Dieser Teil liest nur noch
        // die Job-Daten ein und haelt die Unity-Objekte.
        sealed class SpielNetz : RaumwegNetz
        {
            public SpatialAudioRoom[] Raeume = new SpatialAudioRoom[0];
            public readonly Dictionary<SpatialAudioRoom, int> Index = new Dictionary<SpatialAudioRoom, int>(Gleich.Ref);
            // 0.8.8 Ersatzweg: Geometrie je Portal aus dem Objekt (wie SpatialAudioDataContainer.CreatePortalData), ohne Job
            public RaumPortal[] SGeo = new RaumPortal[0];
            public bool[] SGeoHat = new bool[0];
            public float[] WandP = new float[0];       // wie WandFaktor(AudioPortalData)
            public V3[] AussenN = new V3[0];           // Normale zur Seite im Freien (nur Portale drinnen <-> draussen)
            public int[] GeoKey = new int[0];

            protected override void Messen(int raum, out double flaeche, out double vol)
            {
                long t0 = Laufzeit.Jetzt;
                try { MessenKern(raum, out flaeche, out vol); }
                finally { messenTicks += Laufzeit.Jetzt - t0; }
            }

            void MessenKern(int raum, out double flaeche, out double vol)
            {
                flaeche = 0; vol = 0;
                var sr = raum < Raeume.Length ? Raeume[raum] : null;
                if (sr == null) return;
                // tankhall: Vereinigung der Boxen (wie HallZonen) statt Summe - ueberlappende Boxen zaehlten mehrfach
                // (Factory-Tank: 10 Scheiben, S 1035 statt 251 m2, V 392 statt 191 m3)
                if (HallZonen.Vereinigung(sr, out flaeche, out vol)) return;
                var cols = sr.Colliders;
                if (cols != null)
                    foreach (var bc in cols)
                    {
                        if (bc == null) continue;
                        Vector3 g = Vector3.Scale(bc.size, bc.transform.lossyScale);
                        g = new Vector3(Mathf.Abs(g.x), Mathf.Abs(g.y), Mathf.Abs(g.z));
                        flaeche += 2.0 * (g.x * g.y + g.y * g.z + g.z * g.x);
                        vol += (double)g.x * g.y * g.z;
                    }
                if (flaeche <= 0)
                {
                    Vector3 g = sr.Bounds.size;
                    flaeche = 2.0 * (g.x * g.y + g.y * g.z + g.z * g.x);
                    vol = (double)g.x * g.y * g.z;
                }
            }

            // raumecke: Kaesten des Raums (wie HallZonen) fuer die Wege um Ecken in mehrteiligen Raeumen
            protected override Kasten[] KaestenVon(int raum) => HallZonen.KaestenVon(raum >= 0 && raum < Raeume.Length ? Raeume[raum] : null);
            readonly EckenLinie linie = new EckenLinie();
            protected override ILinie Linie => linie;

            // tankhall: Waende mit dem gemessenen Material (HallZonen, 1 kHz) statt pauschal 0,1
            protected override double WandAlpha(int raum)
                => HallZonen.WandAlpha(raum >= 0 && raum < Raeume.Length ? Raeume[raum] : null, 1000f);

            /// <summary>Index eines Raums (ISpatialAudioRoom, der kein SpatialAudioRoom ist: -1 wie bisher).</summary>
            public int Von(ISpatialAudioRoom r)
            {
                var sr = r as SpatialAudioRoom;
                if ((object)sr == null) return -1;
                if (Index.TryGetValue(sr, out int i)) return i;
                i = RaumDazu(sr.ID, sr.IsOutdoor);
                if (i >= Raeume.Length) Array.Resize(ref Raeume, Math.Max(16, Raeume.Length * 2));
                Raeume[i] = sr; Index[sr] = i;
                return i;
            }
        }

        sealed class Gleich : IEqualityComparer<SpatialAudioRoom>, IEqualityComparer<BaseSpatialAudioPortal>
        {
            public static readonly Gleich Ref = new Gleich();
            public bool Equals(SpatialAudioRoom x, SpatialAudioRoom y) => ReferenceEquals(x, y);
            public int GetHashCode(SpatialAudioRoom o) => RuntimeHelpers.GetHashCode(o);
            public bool Equals(BaseSpatialAudioPortal x, BaseSpatialAudioPortal y) => ReferenceEquals(x, y);
            public int GetHashCode(BaseSpatialAudioPortal o) => RuntimeHelpers.GetHashCode(o);
        }

        static readonly SpielNetz Netz = new SpielNetz();
        static SpatialAudioDataContainer letzterContainer;
        static BaseSpatialAudioPortal[] portalJeIndex = new BaseSpatialAudioPortal[0];
        static int portalAnzahl = -1;
        static int dTreffer, dFehl;

        static bool Rechnen(ParallelSoundPropagationCalculator c, Zustand z, float spiel, double jetzt)
        {
            var s = c._propagationJobScheduler;
            if (s == null || !s._routesSettingsData.IsCreated || !s._routesPortalIndicesData.IsCreated || !s._staticPortalDataList.IsCreated) return false;
            if (!DataProvider.TryGetData<SpatialAudioDataContainer>(out var data) || data == null) return false;
            PortalTabelle(data);
            var routes = s._routesSettingsData;
            int len = routes.Length;
            if (len == 0) return false;
            int anzahl = Mathf.Min(len, Mathf.Max(Mathf.Min(350, len), Mathf.CeilToInt(len * c._qualityFactor)));   // wie Schedule
            return RoutenRechnen(routes, s._routesPortalIndicesData, s._staticPortalDataList, anzahl, z, spiel, jetzt, BlendS);
        }

        /// <summary>0.8.8: Routenrechnung auf den Routendaten eines Raumpaars (Job des Rechners oder - fuer Quellen ohne Okklusion -
        /// direkt aus SpatialAudioDataContainer, WandDaempfung). z.A/B/RaumA/RaumB/Umgekehrt/Paar muessen gesetzt sein.</summary>
        static bool RoutenRechnen(NativeArray<AudioRouteData> routes, NativeArray<int> indizes, NativeList<RoomPair.AudioPortalData> liste, int anzahl,
            Zustand z, float spiel, double jetzt, double blendS, RaumwegNetz.IAussenSchirm schirm = null)
        {
            int len = routes.Length;
            if (len == 0 || anzahl <= 0) return false;
            long rt0 = Laufzeit.Jetzt;                 // 10.11. (spitzen): Zerlegung der langsamsten Rechnung (Diagnose)
            long messen0 = messenTicks; int statik0 = Netz.DStatikNeu;
            if (Netz.BildSetzen(Time.frameCount)) BildAuffrischen();

            // Job-Daten einlesen (wie die Schleife bis 0.8.5: ungueltige Route -> uebersprungen)
            int listeN = liste.Length, idxN = indizes.Length;
            Netz.RechnungBeginn(listeN);
            if (Netz.RStart.Length < anzahl) { Array.Resize(ref Netz.RStart, anzahl * 2); Array.Resize(ref Netz.RN, anzahl * 2); }
            int f = 0;
            for (int r = 0; r < anzahl; r++)
            {
                var rd = routes[r];
                int n = rd.PortalDataLength;
                Netz.RN[r] = -1;
                if (n <= 0 || n > RaumwegNetz.MaxPortale || rd.StartIndex + n > idxN) continue;
                if (f + n > Netz.RIdx.Length) Array.Resize(ref Netz.RIdx, Math.Max(Netz.RIdx.Length * 2, f + n));
                bool ok = true;
                for (int i = 0; i < n; i++)
                {
                    int pi = indizes[rd.StartIndex + i];
                    if (pi < 0 || pi >= listeN) { ok = false; break; }
                    Netz.RIdx[f + i] = pi;
                    if (!Netz.JobGeladen(pi))
                    {
                        var d = liste[pi];
                        var geo = new RaumPortal
                        {
                            Mitte = new V3(d.center.x, d.center.y, d.center.z),
                            Rechts = new V3(d.portalRight.x, d.portalRight.y, d.portalRight.z),
                            Oben = new V3(d.portalUp.x, d.portalUp.y, d.portalUp.z),
                            Normale = new V3(d.portalNormal.x, d.portalNormal.y, d.portalNormal.z),
                            HalbX = d.portalHalfSize.x, HalbY = d.portalHalfSize.y,
                        };
                        Netz.JobSetzen(pi, geo, d.closureLevel, WandFaktor(d), GeoSchluessel(d));
                    }
                }
                if (!ok) continue;
                Netz.RStart[r] = f; Netz.RN[r] = n; f += n;
            }

            var ein = new RaumwegNetz.Eingabe
            {
                A = V(z.A), B = V(z.B), Umgekehrt = z.Umgekehrt, Paar = z.Paar,
                RaumA = Netz.Von(z.RaumA), RaumB = Netz.Von(z.RaumB),
                RaumAId = z.RaumA != null ? z.RaumA.ID : (short)-1, RaumADraussen = z.RaumA != null && z.RaumA.IsOutdoor,
                Dyn = Physik.Dynamik.Value, Alpha = Alpha.Value, TiefpassMin = Physik.TiefpassMin.Value,
                FRef = Physik.Frequenz.Value, FHoch = FHoch, Fit1 = (float)Wege.F[1], Fit2 = (float)Wege.F[2], RoutenGesamt = len,
                RangN = RangNJetzt, Breite = BreiteJetzt,   // 0.8.8: Kappen der Routen ohne Gewicht (RaumwegNetz.Rechne)
                Schirm = schirm,                            // 0.8.8: Strecken im Freien abschirmen (Quellen ohne Spiel-Okklusion)
            };
            bool log = KlangLog.An;
            long rt1 = Laufzeit.Jetzt;
            bool hat = Netz.Rechne(in ein, anzahl, z.Mix, log, out int n0, out bool treffer);
            long rt2 = Laufzeit.Jetzt;
            routen += n0;
            if (treffer) dTreffer++; else dFehl++;
            if (!hat && !z.Mix.Hat) return false;
            // 0.8.5: alle Routen in den Mischer (weiches Rang-Gewicht, verschwindende blenden aus); Wertung schreibt Schreiben()
            z.Mix.Uebernehmen(jetzt, blendS, RangNJetzt, BreiteJetzt);
            dGeister += z.Mix.Geister; dNeue += z.Mix.Neue; z.Mix.Geister = 0; z.Mix.Neue = 0;
            dMisch++;
            foreach (var en in z.Mix.Eintraege) if (en.W > 0.005) dMischRouten++;
            long rt3 = Laufzeit.Jetzt;
            z.Kette = log && hat ? Kette(spiel, ein.Dyn, Netz.Best, Netz.BestN, len, anzahl) : null;
            RechnungMerken(rt0, rt1, rt2, rt3, Laufzeit.Jetzt, anzahl, len, n0, treffer, Netz.DStatikNeu != statik0, messenTicks - messen0);
            return true;
        }

        // ---------------------------------------------------------------- 10.11. (spitzen) Diagnose der Spitzen
        // Je 30 s: die langsamste Routenrechnung mit Zerlegung (Einlesen der Job-Daten + Bild-Auffrischen / RaumwegNetz.Rechne /
        // Mischer / Klang-Log-Kette), Routenzahl, exakt gerechnete Routen, Wiederverwendung, neue Statik, Raum-Vermessung; dazu die
        // Verteilung (> 1 / 2 / 5 ms) und die ersten Rechnungen des Raids (JIT). Ziel: Ursache jeder Spitze aus EINEM Raid ablesbar.
        internal static long messenTicks;
        static int dRechnungen, dUeber1, dUeber2, dUeber5, rechnungImRaid;
        static double sMax, sEin, sNetz, sMisch, sKette, sMessen; static int sAnz, sLen, sExakt, sNr; static bool sTreffer, sStatik, sNeu;
        static double ersteMs = double.NaN; static int ersteAnz;
        static double schnappMax; static int schnappNr, schnappMaxNr; static bool schnappR, schnappT;

        static void RechnungMerken(long t0, long t1, long t2, long t3, long t4, int anz, int len, int exakt, bool treffer, bool statikNeu, long messen)
        {
            double ms = Laufzeit.Ms(t4 - t0);
            dRechnungen++; rechnungImRaid++;
            if (ms > 1) dUeber1++;
            if (ms > 2) dUeber2++;
            if (ms > 5) dUeber5++;
            if (rechnungImRaid == 1) { ersteMs = ms; ersteAnz = anz; }
            if (ms <= sMax) return;
            sMax = ms; sEin = Laufzeit.Ms(t1 - t0); sNetz = Laufzeit.Ms(t2 - t1); sMisch = Laufzeit.Ms(t3 - t2); sKette = Laufzeit.Ms(t4 - t3);
            sMessen = Laufzeit.Ms(messen); sAnz = anz; sLen = len; sExakt = exakt; sTreffer = treffer; sStatik = statikNeu; sNr = rechnungImRaid;
            sNeu = neuBildNr == Time.frameCount && dNeuImBild > 0;
        }

        /// <summary>Aus Wege.Schnappschuss: Dauer eines Schnappschusses (T/D/R) fuer die Diagnose.</summary>
        internal static void SchnappschussMerken(long t0, bool t, bool r)
        {
            double ms = Laufzeit.Ms(Laufzeit.Jetzt - t0);
            schnappNr++;
            if (ms > schnappMax) { schnappMax = ms; schnappMaxNr = schnappNr; schnappT = t; schnappR = r; }
        }

        static string SpitzenText()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(400);
            sb.Append("[Diagnostics] Room-path peaks (30 s): ").Append(dRechnungen).Append(" route calculations, > 1 ms ").Append(dUeber1)
              .Append(", > 2 ms ").Append(dUeber2).Append(", > 5 ms ").Append(dUeber5);
            if (dRechnungen > 0)
                sb.Append("; slowest ").Append(sMax.ToString("F2", inv)).Append(" ms (#").Append(sNr).Append(" of the raid): read job data ")
                  .Append(sEin.ToString("F2", inv)).Append(" ms, RaumwegNetz.Rechne ").Append(sNetz.ToString("F2", inv)).Append(" ms (")
                  .Append(sAnz).Append('/').Append(sLen).Append(" routes, ").Append(sExakt).Append(" exact, ").Append(sTreffer ? "reused" : "calculated")
                  .Append(sStatik ? ", new static data" : "").Append(", rooms measured ").Append(sMessen.ToString("F2", inv)).Append(" ms), mixer ")
                  .Append(sMisch.ToString("F2", inv)).Append(" ms, log chain ").Append(sKette.ToString("F2", inv)).Append(" ms")
                  .Append(sNeu ? ", in a frame with a first value at once" : "");
            if (!double.IsNaN(ersteMs)) sb.Append("; first calculation of the raid ").Append(ersteMs.ToString("F2", inv)).Append(" ms (").Append(ersteAnz).Append(" routes)");
            sb.Append(" | first values at once: at most ").Append(dNeuMaxBild).Append(" in one frame; ").Append(ZeitNeu.Text())
              .Append(" | ").Append(ZeitTaktAnwenden.Text()).Append(", ").Append(dTaktAngewandt).Append(" applied")
              .Append(" | running sounds started from the applied value ").Append(dVomAngewandt).Append(", calculations deferred by their expected time ").Append(dVorhersage)
              .Append(", non-finite results dropped ").Append(dNaN);
            // raumecke: Wege um Ecken in mehrteiligen Raeumen
            sb.Append(" | corners in multi-box rooms: ").Append(Netz.DEckenRouten).Append(" routes, ").Append(Netz.DEckenStuecke).Append(" room stretches, ")
              .Append(Netz.DEckenTreffer).Append(" reused, ").Append((Netz.DEckenTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1", inv)).Append(" ms");
            Netz.DEckenRouten = Netz.DEckenStuecke = Netz.DEckenTreffer = 0; Netz.DEckenTicks = 0;
            if (schnappNr > 0) sb.Append(" | slowest snapshot ").Append(schnappMax.ToString("F2", inv)).Append(" ms (#").Append(schnappMaxNr).Append(" of the raid")
                                 .Append(schnappT ? ", T" : "").Append(schnappR ? ", R" : "").Append(')');
            sb.Append('.');
            dRechnungen = dUeber1 = dUeber2 = dUeber5 = 0; sMax = 0; ersteMs = double.NaN; dNeuMaxBild = 0; dTaktAngewandt = dVomAngewandt = dVorhersage = dNaN = 0; schnappMax = 0;
            return sb.ToString();
        }

        /// <summary>Einmal je Bild: Schliessung und Transmission aller Portale (Felder der Objekte, keine Unity-Aufrufe).</summary>
        static void BildAuffrischen()
        {
            int n = Math.Min(Netz.PortalN, portalJeIndex.Length);
            for (int pi = 0; pi < n; pi++)
            {
                if (!Netz.HatObjekt[pi]) continue;
                var p = portalJeIndex[pi];
                Netz.Schliessung[pi] = p.PortalClosureLevel;
                Netz.Voll[pi] = Mathf.Lerp(1f, 0.1f, p._transmission);
                BlattLaden(pi, p);
            }
        }

        /// <summary>0.8.8: Blatt-Daemmung je Band (Massengesetz, TuerenFenster/TuerKern) ins Netz; NaN = kein Blattmodell.
        /// TuerenFenster haelt das Ergebnis je Portal, hier nur ein Woerterbuchzugriff je Portal und Bild.</summary>
        static void BlattLaden(int pi, BaseSpatialAudioPortal p)
        {
            if (TuerenFenster.BlattBaender(p, out double d0, out double d1, out double d2)) Netz.BlattSetzen(pi, d0, d1, d2);
            else Netz.BlattSetzen(pi, double.NaN, double.NaN, double.NaN);
        }

        // Wie PropagationJob: ein Raum draussen -> Wert des Innenraums, sonst Mittel.
        static float WandFaktor(in RoomPair.AudioPortalData d)
        {
            if (d.isFrontRoomOutdoor != d.isBackRoomOutdoor) return d.isFrontRoomOutdoor ? d.backRoomWallOcclusion : d.frontRoomWallOcclusion;
            return (d.frontRoomWallOcclusion + d.backRoomWallOcclusion) * 0.5f;
        }

        /// <summary>Schluessel eines Portals aus seiner Geometrie (Mitte auf 5 cm, Halbgroessen auf 5 cm).</summary>
        static int GeoSchluessel(in RoomPair.AudioPortalData d)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + Mathf.RoundToInt(d.center.x * 20f); h = h * 31 + Mathf.RoundToInt(d.center.y * 20f);
                h = h * 31 + Mathf.RoundToInt(d.center.z * 20f);
                h = h * 31 + Mathf.RoundToInt(d.portalHalfSize.x * 20f); h = h * 31 + Mathf.RoundToInt(d.portalHalfSize.y * 20f);
                return h;
            }
        }

        /// <summary>Portal-/Raumtabelle (einmal je Raid bzw. wenn sich die Portalzahl aendert): je Portal Flaeche, Raeume vorn/hinten;
        /// je Raum seine Oeffnungen (eindeutig, Reihenfolge wie bis 0.8.5).</summary>
        static void PortalTabelle(SpatialAudioDataContainer data)
        {
            var map = data._portalDataBufferService?._indexMap;
            if (map == null) return;
            if (ReferenceEquals(data, letzterContainer) && map.Count == portalAnzahl) return;
            long t0 = Laufzeit.Jetzt;
            letzterContainer = data; portalAnzahl = map.Count;
            int max = 0;
            foreach (var kv in map) max = Math.Max(max, kv.Value + 1);
            portalJeIndex = new BaseSpatialAudioPortal[max];
            foreach (var kv in map)
                if (data.PortalsByID.TryGetValue(kv.Key, out var p)) portalJeIndex[kv.Value] = p;
            // Raeume (dicht) und Oeffnungen je Raum
            var raeume = new List<SpatialAudioRoom>();
            var raumIdx = new Dictionary<SpatialAudioRoom, int>(Gleich.Ref);
            var listen = new List<List<int>>();
            var gesehen = new List<HashSet<BaseSpatialAudioPortal>>();
            Netz.PortaleAnlegen(max);
            int eintraege = 0;
            for (int pi = 0; pi < max; pi++)
            {
                var p = portalJeIndex[pi];
                if (p == null) { portalJeIndex[pi] = null; continue; }
                Netz.HatObjekt[pi] = true;
                var g = p.GetPortalSize();
                Netz.FlaecheP[pi] = Math.Abs((double)g.x * g.y);
                Netz.Voll[pi] = Mathf.Lerp(1f, 0.1f, p._transmission);
                Netz.Schliessung[pi] = p.PortalClosureLevel;
                BlattLaden(pi, p);
                for (int j = 0; j < 2; j++)
                {
                    var raum = j == 0 ? p.FrontRoom : p.BackRoom;
                    if (raum == null) continue;
                    if (!raumIdx.TryGetValue(raum, out int ri))
                    {
                        ri = raeume.Count; raeume.Add(raum); raumIdx[raum] = ri;
                        listen.Add(new List<int>(8)); gesehen.Add(new HashSet<BaseSpatialAudioPortal>(Gleich.Ref));
                    }
                    if (j == 0) Netz.Vorn[pi] = ri; else Netz.Hinten[pi] = ri;
                    if (gesehen[ri].Add(p)) { listen[ri].Add(pi); eintraege++; }
                }
            }
            Netz.RaeumeAnlegen(raeume.Count, eintraege);
            Netz.Raeume = raeume.ToArray();
            ErsatzGeometrie(max);
            Netz.Index.Clear();
            int k = 0;
            for (int ri = 0; ri < raeume.Count; ri++)
            {
                var raum = raeume[ri];
                Netz.Index[raum] = ri;
                Netz.RaumId[ri] = raum.ID; Netz.Draussen[ri] = raum.IsOutdoor;
                Netz.PStart[ri] = k; Netz.PAnz[ri] = listen[ri].Count;
                foreach (int pi in listen[ri]) Netz.PListe[k++] = pi;
            }
            Plugin.Log.LogInfo($"[Diagnostics] Room paths (physics): room network built - {max} portals, {raeume.Count} rooms, {eintraege} openings, " +
                               $"{Laufzeit.Ms(Laufzeit.Jetzt - t0):F1} ms (once per raid).");
        }

        static V3 V(Vector3 v) => new V3(v.x, v.y, v.z);

        /// <summary>0.8.8 Ersatzweg: Portalgeometrie aus den Objekten (einmal je Raid, nach dem Raumnetz).</summary>
        static void ErsatzGeometrie(int max)
        {
            Array.Resize(ref Netz.SGeo, max); Array.Resize(ref Netz.SGeoHat, max); Array.Resize(ref Netz.WandP, max);
            Array.Resize(ref Netz.AussenN, max); Array.Resize(ref Netz.GeoKey, max);
            for (int pi = 0; pi < max; pi++)
            {
                Netz.SGeoHat[pi] = false; Netz.AussenN[pi] = default;
                var p = portalJeIndex[pi];
                if (p == null) continue;
                try
                {
                    var col = p.portalCollider;
                    if (col == null || p.FrontRoom == null || p.BackRoom == null) continue;
                    var tr = p.transform;
                    var hs = p.GetPortalHalfSize();
                    Vector3 m = col.bounds.center;
                    var g = new RaumPortal
                    {
                        Mitte = V(m), Rechts = V(tr.right), Oben = V(tr.up), Normale = V(tr.forward),
                        HalbX = Mathf.Abs(hs.x), HalbY = Mathf.Abs(hs.y),
                    };
                    Netz.SGeo[pi] = g; Netz.SGeoHat[pi] = true;
                    bool fo = p.FrontRoom.IsOutdoor, bo = p.BackRoom.IsOutdoor;
                    Netz.WandP[pi] = fo != bo ? (fo ? p.BackRoom.WallOcclusion : p.FrontRoom.WallOcclusion) : (p.FrontRoom.WallOcclusion + p.BackRoom.WallOcclusion) * 0.5f;
                    if (fo != bo)
                    {
                        Vector3 innen = (fo ? p.BackRoom : p.FrontRoom).Bounds.center;
                        Vector3 n = tr.forward;
                        Netz.AussenN[pi] = Vector3.Dot(innen - m, n) > 0 ? V(-n) : V(n);
                    }
                    unchecked
                    {
                        int k = 17;
                        k = k * 31 + Mathf.RoundToInt(m.x * 20f); k = k * 31 + Mathf.RoundToInt(m.y * 20f); k = k * 31 + Mathf.RoundToInt(m.z * 20f);
                        k = k * 31 + Mathf.RoundToInt(hs.x * 20f); k = k * 31 + Mathf.RoundToInt(hs.y * 20f);
                        Netz.GeoKey[pi] = k;
                    }
                }
                catch (Exception e) { Statistik.Fehler(e); }
            }
        }

        static string Kette(float spiel, float dyn, RaumwegErgebnis e, int n, int routenGesamt, int routenGerechnet)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(160);
            if (float.IsNaN(spiel)) sb.Append(" | room path (no game occlusion for this source - own calculation over the game's routes): loudest route ");   // 0.8.8
            else sb.Append(" | room path: game cost ").Append(spiel.ToString("F2", inv)).Append(" (=").Append((spiel * dyn).ToString("F1", inv))
              .Append(" dB) -> loudest route ");
            sb.Append(e.Db.ToString("F1", inv)).Append(" dB [open ").Append(e.DbGeo.ToString("F1", inv))
              .Append(", rooms ").Append(e.DbDiffus.ToString("F1", inv)).Append(" (4k ").Append(e.DbDiffusHoch.ToString("F1", inv)).Append(")").Append(", doors ").Append(e.DbTuer.ToString("F1", inv))
              .Append(e.TuerBaender ? "/" + e.DbTuerMitte.ToString("F1", inv) + "/" + e.DbTuerHoch.ToString("F1", inv) : "")
              .Append(e.AussenStrecken > 0 ? ", outdoors screening " + e.DbAussen.ToString("F1", inv) + " (" + e.AussenStrecken + (e.AussenStrecken == 1 ? " stretch)" : " stretches)") : "")
              .Append(e.Ecken > 0 ? ", corners in rooms " + e.DbEcken.ToString("F1", inv) + "/" + e.DbEckenMitte.ToString("F1", inv) + " dB (" + e.Ecken + (e.Ecken == 1 ? " corner" : " corners") + ", exit ("
                      + Netz.BestAus.X.ToString("F1", inv) + ", " + Netz.BestAus.Y.ToString("F1", inv) + ", " + Netz.BestAus.Z.ToString("F1", inv) + "), point before ("
                      + Netz.BestVorAus.X.ToString("F1", inv) + ", " + Netz.BestVorAus.Y.ToString("F1", inv) + ", " + Netz.BestVorAus.Z.ToString("F1", inv) + "))" : "")
              .Append("] length ").Append(e.Laenge.ToString("F1", inv)).Append('/').Append(e.Direkt.ToString("F1", inv)).Append(" m, ")
              .Append(routenGerechnet).Append('/').Append(routenGesamt).Append(" routes; source ").Append(Netz.BestR1.ToString("F1", inv))
              .Append(" m from 1st opening (").Append((Netz.BestOmega * 100).ToString("F1", inv)).Append(" % of its power); rooms A/V:");
            // Gesamtreview A3: Quellraum (Diffusfeld an der ersten Oeffnung) mit ausgeben
            sb.Append(" source ").Append(Netz.BestA[0] > 0 ? Netz.BestA[0].ToString("F0", inv) + " m2/" + Netz.BestV[0].ToString("F0", inv) + " m3" : "outdoors").Append(" |");
            for (int i = 1; i <= n; i++)
                sb.Append(i == 1 ? " " : ", ").Append(Netz.BestA[i] > 0 ? Netz.BestA[i].ToString("F0", inv) + " m2/" + Netz.BestV[i].ToString("F0", inv) + " m3" : "outdoors");
            sb.Append("; portals:");
            for (int i = 0; i < n; i++)
            {
                var p = Netz.BestIdx[i] < portalJeIndex.Length ? portalJeIndex[Netz.BestIdx[i]] : null;
                sb.Append(i == 0 ? " " : " > ").Append(p != null ? (string.IsNullOrEmpty(p.portalName) ? p.name : p.portalName) : "").Append('#').Append(Netz.BestIdx[i]);
                if (p != null) sb.Append('@').Append(p.ID);   // tabellenneu: Portal-ID
                sb.Append('(');
                if (p != null) sb.Append(p.portalType).Append(' ').Append(p.state).Append(' ');
                // lampenpegel (18.11.): Zustand der Tuer selbst gegen den Portalzustand (die Mod liest sonst nur den Portalzustand)
                string tz = p != null ? TuerenFenster.TuerText(p) : null;
                if (tz != null) sb.Append(tz).Append(' ');
                sb.Append("open ").Append(Netz.BestOffen[i].ToString("F2", inv));
                if (Netz.BestOffen[i] < 0.999)
                {
                    // 0.8.8: Blatt mit Material, Dicke (gemessen/Ersatz) und dB je Band; ohne Blattmodell wie bisher der Bezugswert
                    string bt = p != null ? TuerenFenster.BlattText(p) : null;
                    if (bt != null) sb.Append(' ').Append(bt);
                    else sb.Append(" door ").Append(Netz.BestTauDb[i].ToString("F0", inv)).Append(" dB");
                }
                if (p != null) { var g = p.GetPortalSize(); sb.Append(' ').Append((g.x * g.y).ToString("F1", inv)).Append(" m2"); }
                sb.Append(Netz.BestUm[i] >= 0 ? " detour " : " clear ").Append(Math.Abs(Netz.BestUm[i]).ToString("F2", inv)).Append(" m ")
                  .Append(Netz.BestBeug[i].ToString("F1", inv)).Append(" dB)");
            }
            return sb.ToString();
        }
    }

    /// <summary>raumecke: freie Linie fuer RaumEckeKern - Physics.Linecast mit der Maske der Schallstrahlen (Statistik.AudioMaske: Terrain +
    /// HighPolyCollider, dieselben Ebenen wie das Wegenetz), Enden 0,3 m eingerueckt. Hauptfaden, keine Speicheranforderung.</summary>
    internal sealed class EckenLinie : ILinie
    {
        public bool Frei(V3 a, V3 b)
        {
            V3 d = b - a; double l = d.Laenge; if (l < 0.7) return true;
            V3 u = d * (1 / l), a2 = a + u * 0.3, b2 = b - u * 0.3;
            int maske = Statistik.AudioMaske != 0 ? Statistik.AudioMaske : (int)global::LayersMaskController.HighPolyWithTerrainMask;
            return !UnityEngine.Physics.Linecast(new UnityEngine.Vector3((float)a2.X, (float)a2.Y, (float)a2.Z), new UnityEngine.Vector3((float)b2.X, (float)b2.Y, (float)b2.Z),
                                                  maske, UnityEngine.QueryTriggerInteraction.Ignore);
        }
    }
}
