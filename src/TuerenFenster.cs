using System;
using System.Collections.Generic;
using System.Globalization;
using Audio.Extensions;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.DataProviding;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    // Tueren und Fenster daempfen nach ihrem Blatt: Material des Tuerblatts bzw. der Scheibe und gemessene Dicke, Massengesetz
    // je Band (TuerKern.cs, dieselben Formeln wie die Waende). 0.8.6: bis 0.8.5 bekam jede geschlossene Tuer einen festen Anteil
    // der Hoechstdaemmung je Typ (Holz duenn/dick, Metall duenn/dick, Fenster) - Kaan: "Wir koennen nicht pauschal Werte fuer
    // Tueren geben. Wenn das Material es durchlaesst, hoert man es entsprechend gedaempft."
    //
    // Wo das Ergebnis wirkt:
    //  - Raumweg (Physik-Modus): RaumwegNetz/Ersatzweg rechnen den geschlossenen Teil eines Portals mit tau je Band aus dem Blatt
    //    (BlattBaender). Der offene Anteil (Oeffnungswinkel, zerbrochene Scheiben) kommt weiter aus der Schliessung des Spiels.
    //  - Wegkosten des Spiel-Jobs (beide Modi): Schliessung = Blatt-dB(Bezugsband) / Max. insulation / WallOcclusion(Raeume),
    //    die Kosten im Job sind (Abstandsanteil + Schliessung) * WallOcclusion -> genau der Blattwert auf der dB-Skala der Mod.
    //    Eingriffspunkt: die Portaldaten, die der (Burst-)Job spaeter liest - nicht der Job selbst.
    //  - Strahlen durch ein Tuerblatt (Hindernis, Wand zwischen Raeumen, Bot-Hoeren): Stueck eines Tuer-Colliders = dasselbe Blatt
    //    (StueckDb/StueckMasse) statt "Gruppe Tuer x Strahllaenge" (die Strahllaenge durch ein schraeg getroffenes Blatt ist laenger
    //    als das Blatt dick ist; Log 09.11.: Holztuer Wood_19 0,06-0,10 m, Wood_22 0,41 m).
    //
    // Collider im Spiel (EFT 4.1.6, Dekompilat + Klang-Log/Kartenexport 09.11.2026):
    //  - Tuer = EFT.Interactive.Door (WorldInteractiveObject). Das Blatt traegt einen oder mehrere BallisticCollider (Material laut
    //    Spiel; Ground Zero, Kartenexport: WoodThin 37, MetalThick 13, MetalThin 1, Soft 5 - Namen wie
    //    "Door_Wood_19_R_210-100_Door_BALLISTIC_woodthin", "Door_Metal_16_220-210_door_R_BALLISTIC_MetalThick"). Blatt = groesster
    //    BallisticCollider (wie bisher fuer das Material). Dicke: BoxCollider -> kleinste Kante (size x lossyScale);
    //    MeshCollider -> Strahl Ein-/Austritt entlang der duennsten Achse des Meshs (zwei Collider.Raycast von beiden Seiten, wie die
    //    Wandmessung), sonst die Mesh-Bounds. Gemessen in der Achse des Blatts, also unabhaengig vom Oeffnungswinkel.
    //  - Fenster = EFT.Interactive.WindowBreaker (GameWorld.Windows, Schluessel DoorID.GetHashCode() wie SpatialAudioPortal.SyncState),
    //    Scheibe = MeshCollider mit GlassBallisticCollider. Das Spiel selbst fuehrt die Scheibendicke in ZSurfs (lokale Flaechen der
    //    Scheibe entlang AxisZ, aus den Mesh-Grenzen x ThicknessMultyplier, mindestens 2 x MinThickness = 4 mm; WindowBreaker.method_5)
    //    -> Dicke = (ZSurfs.y - ZSurfs.x) x Skalierung der Renderer-Achse. Fehlt ZSurfs: Messung am MeshCollider wie bei Tueren.
    //  - MultiWindowPortal (Fensterreihen): die Scheiben der Reihe (windowID je Scheibe), Blatt = erste gefundene Scheibe.
    internal static class TuerenFenster
    {
        internal static ConfigEntry<bool> Aktiv, Messen;
        internal static ConfigEntry<bool> FluegelAn;   // torfluegel (12.11.): feste Tor-/Tuerfluegel zaehlen mit der Oeffnung
        static int hauptFaden;
        internal static ConfigEntry<float> ErsatzHolz, ErsatzGlas, Blech;

        /// <summary>Ein Blatt mit seinem Ergebnis (je Tuer/Scheibe einmal gemessen, dB je Einstellungsstand).</summary>
        internal sealed class Info
        {
            public string Name, Art;          // Collidername, Messart (box / mesh rays / mesh bounds / game glass model / none)
            public bool Fenster;
            public int Gruppe;                 // Materialgruppe des Blatts
            public double Gemessen = double.NaN;
            public TuerBlatt B;
            public int Stand = -1;
            public double D0, D1, D2;          // dB je Band (Bezug / 1 kHz / 4 kHz), gedeckelt bei Max. insulation
            // torfluegel (12.11.): feste Fluegel der Tor-/Tuerbaugruppe (TuerKern.TauMitFluegeln). E = Portal mit Fluegeln (Raumweg,
            // Schliessung des Spiels); D bleibt das Blatt allein (Strahlen durch das Blatt).
            public bool FluegelGesucht;
            public int FluegelN;
            public readonly int[] FluegelGruppe = new int[MaxFluegel], FluegelId = new int[MaxFluegel];
            public readonly double[] FluegelFlaeche = new double[MaxFluegel], FluegelGemessen = new double[MaxFluegel];
            public readonly TuerBlatt[] FluegelBlatt = new TuerBlatt[MaxFluegel];
            public double PortalFlaeche, FluegelSumme;
            public double E0 = double.NaN, E1 = double.NaN, E2 = double.NaN;
            public WorldInteractiveObject Tuer; public SpatialAudioPortal Portal;   // fuer die Suche (nur Hauptfaden)
        }
        const int MaxFluegel = 8;

        // Portal-ID -> Blatt (null = kein Blattmodell -> Vanilla). Nicht gefundene Tueren werden NICHT gemerkt (Welt noch nicht fertig).
        static readonly Dictionary<short, Info> PortalBlatt = new Dictionary<short, Info>();
        static readonly Dictionary<int, Info> ObjektBlatt = new Dictionary<int, Info>();     // Door/WindowBreaker-InstanceID -> Blatt
        static readonly Dictionary<int, Info> ColliderBlatt = new Dictionary<int, Info>();   // Collider-ID -> Blatt der Tuer (null = keine)
        static readonly Dictionary<short, float> Ziele = new Dictionary<short, float>();     // Portal-ID -> Schliessung "ganz zu" (-1 = Vanilla)
        static readonly HashSet<string> Geloggt = new HashSet<string>();
        static int stand;                       // jede Einstellungsaenderung -> dB neu, Zielwerte neu

        static int angepasst, tuerGeaendert, fensterGeaendert, unbekannteTuer;
        static double summeAlt, summeNeu;
        const int LogMax = 40;

        internal static void Binden(ConfigFile c)
        {
            const string s = "4. Doors and Windows";
            Aktiv = c.Bind(s, "Enabled", true,
                "Closed doors and intact windows attenuate by the mass law of their leaf: material of the door leaf / glass pane (the game's ballistic material) and its thickness, per band 250 Hz / 1 kHz / 4 kHz - the same formulas as walls (section 0: density, 'Solid up to', coincidence). Partly open doors and broken windows let the open part through. Off = vanilla: a closed door is always the same. Changed values apply live.");
            Messen = c.Bind(s, "Door leaf: use measured thickness", true,
                "Measure the thickness of each door leaf / window pane on its collider (box: thinnest edge; mesh: ray in and out along its thinnest axis; windows: the game's own pane thickness). Off = always the fallback thicknesses below. A metal leaf thicker than 'Solid up to' of its group (section 0) is a hollow door of two sheets.");
            ErsatzHolz = c.Bind(s, "Fallback thickness: wood/plastic leaf (m)", 0.04f,
                new ConfigDescription("Leaf thickness when nothing could be measured or the measurement is implausible (over 8 cm: frame, handle). 0.04 = interior door leaf. Marked 'fallback' in the log.", new AcceptableValueRange<float>(0.01f, 0.1f)));
            ErsatzGlas = c.Bind(s, "Fallback thickness: glass pane (m)", 0.006f,
                new ConfigDescription("Pane thickness when nothing could be measured or the measurement is not a single pane (over 12 mm: frame or box mesh). 0.006 = float glass.", new AcceptableValueRange<float>(0.003f, 0.012f)));
            Blech = c.Bind(s, "Metal door: sheet thickness (mm)", 1.5f,
                new ConfigDescription("A metal door leaf thicker than 'Solid up to' of its group is hollow: two steel sheets of this thickness (the filling has no relevant mass). 1.5 = typical steel door.", new AcceptableValueRange<float>(0.5f, 5f)));
            // torfluegel (12.11.2026): Ursache und Regel in TuerKern.cs (Abschnitt torfluegel)
            FluegelAn = c.Bind(s, "Gate wings: count with the opening", true,
                "A gate whose game opening is only the small pass door (e.g. the big steel gates on Factory) also lets sound through its fixed wings: " +
                "they are counted as leaves of the same build (steel sheet on a frame with unsealed gaps, like the door leaf) together with the opening, " +
                "instead of not at all (room path) or as solid steel (through-wall path). Found automatically: flat plates in the same plane that belong " +
                "to the same gate object. Off = only the pass door (behaviour up to 0.8.5). Applies live.");
            FluegelAn.SettingChanged += (_, __) => WandZwischenRaeumen.Neu();
            hauptFaden = System.Threading.Thread.CurrentThread.ManagedThreadId;
            c.SettingChanged += (_, __) => { stand++; Ziele.Clear(); };
        }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var upd = AccessTools.DeclaredMethod(typeof(SpatialAudioDataContainer), "UpdatePortalDataInternal");
                var neu = AccessTools.DeclaredMethod(typeof(SpatialAudioDataContainer), "CreatePortalData");
                if (upd == null || neu == null)
                {
                    Plugin.Log.LogWarning("Doors/windows: target in SpatialAudioDataContainer not found (game update?) -> vanilla.");
                    return;
                }
                h.Patch(upd, prefix: new HarmonyMethod(typeof(TuerenFenster), nameof(UpdatePrefix)));
                h.Patch(neu, postfix: new HarmonyMethod(typeof(TuerenFenster), nameof(CreatePostfix)));
                Plugin.Log.LogInfo("Doors/windows: hooked.");
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        internal static void Leeren()
        {
            PortalBlatt.Clear(); ObjektBlatt.Clear(); ColliderBlatt.Clear(); Ziele.Clear(); Geloggt.Clear(); ColliderPortal.Clear(); FluegelPortal.Clear();
            gueltig.Clear();
            TuerJePortal.Clear(); AbweichungGeloggt.Clear();   // lampenpegel (18.11.)
        }

        // ---- lampenpegel (18.11.): Tuerzustand gegen Portalzustand (nur fuer das Klang-Log) ----
        // Befund lampen1118: die Mod liest nur den Portalzustand (BaseSpatialAudioPortal.state). Das Spiel gleicht Tuer und Portal nur ueber
        // SpatialAudioPortal.SyncState (nur mit ClientWorld, die der lokale Raid nicht hat) und OnDoorInteracted ab. Ob beide im Raid
        // auseinanderlaufen, war nicht belegbar. Dieses Feld zeigt es je Portal mit Tuer in jeder Raumweg-Zeile; die erste Abweichung je
        // Portal steht einmal im BepInEx-Log. Aendert am Klang nichts.
        static readonly Dictionary<short, WorldInteractiveObject> TuerJePortal = new Dictionary<short, WorldInteractiveObject>();
        static readonly HashSet<short> AbweichungGeloggt = new HashSet<short>();

        /// <summary>"door Shut" / "door Open (differs from portal Closed)" / "door moving" fuer ein Portal mit Tuer (Typ Opening mit DoorID),
        /// sonst null. Nur beim Schreiben des Klang-Logs (Allokation erlaubt).</summary>
        internal static string TuerText(BaseSpatialAudioPortal p)
        {
            try
            {
                var sp = p as SpatialAudioPortal;
                if (sp == null || sp.portalType != BaseSpatialAudioPortal.PortalType.Opening || string.IsNullOrEmpty(sp.DoorID)) return null;
                if (!TuerJePortal.TryGetValue(p.ID, out var d))
                {
                    if (!FindeTuer(sp.DoorID, out d)) return "door ?";   // Verzeichnis noch nicht bereit
                    TuerJePortal[p.ID] = d;
                }
                if (d == null) return "door not found";
                int tz = (int)d.DoorState, pz = (int)p.state;
                string t = TuerZustandKern.Text(tz, d.DoorState.ToString(), pz, p.state.ToString());
                if (TuerZustandKern.WeichtAb(tz, pz) && AbweichungGeloggt.Add(p.ID))
                    Plugin.Log.LogInfo("[Doors] door state differs from the audio portal state: portal " + (string.IsNullOrEmpty(p.portalName) ? p.name : p.portalName) +
                                       " #" + p.ID + " is " + p.state + ", door " + sp.DoorID + " is " + d.DoorState + " (first time this raid)");
                return t;
            }
            catch (Exception e) { Statistik.Fehler(e); return null; }
        }

        internal static void DiagnoseLoggen()
        {
            if (angepasst == 0) return;
            Plugin.Log.LogInfo($"[Diagnostics] Doors/windows: {angepasst} portal values adjusted (doors {tuerGeaendert}, windows {fensterGeaendert}), " +
                               $"closure on average {summeAlt / angepasst:F2} -> {summeNeu / angepasst:F2}, doors without leaf {unbekannteTuer}, " +
                               $"fixed gate wings found so far {fluegelGefunden}{(FluegelAn.Value ? "" : " (not counted: switch off)")}.");
            angepasst = tuerGeaendert = fensterGeaendert = unbekannteTuer = 0; summeAlt = summeNeu = 0;
        }

        static bool An => Plugin.Aktiv != null && Plugin.Aktiv.Value && Aktiv != null && Aktiv.Value;

        // ---- Harmony ----

        static void UpdatePrefix(SpatialAudioDataContainer __instance, short portalID, ref float closureLevel)
        {
            if (!An) return;   // Gesamtreview A7: Hauptschalter aus = Vanilla
            try
            {
                if (__instance.PortalsByID != null && __instance.PortalsByID.TryGetValue(portalID, out var portal))
                    closureLevel = Anpassen(portal, closureLevel);
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void CreatePostfix(BaseSpatialAudioPortal portal, ref RoomPair.AudioPortalData __result)
        {
            if (!An) return;   // Gesamtreview A7
            try { __result.closureLevel = Anpassen(portal, __result.closureLevel); }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // ---- Schnittstelle fuer Raumweg / Ersatzweg / Strahlen ----

        /// <summary>Daemmung des geschlossenen Blatts dieses Portals je Band (dB, gedeckelt bei Max. insulation).
        /// false = Abschnitt 4 aus oder kein Blattmodell (Trigger-/statische Portale, Tuer nicht gefunden).</summary>
        internal static bool BlattBaender(BaseSpatialAudioPortal p, out double d0, out double d1, out double d2)
        {
            d0 = d1 = d2 = double.NaN;
            if (p == null || !An) return false;
            try
            {
                var info = PortalInfo(p);
                if (info == null) return false;
                Rechnen(info);
                PortalWert(info, out d0, out d1, out d2);   // torfluegel: mit festen Fluegeln
                return true;
            }
            catch (Exception e) { Statistik.Fehler(e); return false; }
        }

        /// <summary>Logtext des Blatts: "door WoodThin 0.040 m measured (box) 21.5/31.4/49.5 dB", null = kein Blattmodell.</summary>
        internal static string BlattText(BaseSpatialAudioPortal p)
        {
            if (p == null || !An) return null;
            try
            {
                var info = PortalInfo(p);
                if (info == null) return null;
                Rechnen(info);
                return Text(info);
            }
            catch (Exception e) { Statistik.Fehler(e); return null; }
        }

        /// <summary>Gesamtreview A4: Schliessung des Portals im ganz geschlossenen Zustand, wie sie der Job bekommt (Abschnitt 4 an:
        /// Blattwert, sonst/kein Blatt: Vanilla Lerp(1, 0.1, transmission)). Fuer den Ersatzweg ohne Blattmodell.</summary>
        internal static float ZuWert(BaseSpatialAudioPortal p)
        {
            if (p == null) return 1f;
            float voll = Mathf.Lerp(1f, 0.1f, p._transmission);
            if (!An) return voll;
            try { return Anpassen(p, voll, false); }
            catch (Exception e) { Statistik.Fehler(e); return voll; }
        }

        /// <summary>Ein Strahl trifft einen Collider der Gruppe Tuer: Daemmung (dB) des Blatts dieser Tuer bei der Frequenz f
        /// (deckel = bei Max. insulation begrenzen). false = kein Blatt -> Gruppe "Door leaf" mit der Strahllaenge.</summary>
        internal static bool StueckDb(int colliderId, float f, bool deckel, out float db)
        {
            db = 0f;
            var info = ColliderInfo(colliderId);
            if (info == null) return false;
            // tordoppelt (Nebenbefund): ohne Rechnen ist info.B leer (Masse 0 -> TuerKern.Db = 0 dB), solange das Blatt noch nicht ueber
            // sein Portal gerechnet wurde (ANNAHME fuer Klang-Log 14:19-Raid 14:21:32-14:22:17: "Door 0.10 m 0.0 dB" am Tor-Blatt von #209, sonst 21,9)
            Rechnen(info);
            db = (float)TuerKern.Db(info.B, f, Physik.Koinzidenz.Value, Physik.VerlustFaktor.Value, deckel ? Physik.Dynamik.Value : 1e9);
            return true;
        }

        /// <summary>Flaechenmasse (kg/m2) des Blatts der Tuer dieses Colliders (Flanken-/Empfangsraum-Korrektur).</summary>
        internal static bool StueckMasse(int colliderId, out float masse)
        {
            masse = 0f;
            var info = ColliderInfo(colliderId);
            if (info == null) return false;
            Rechnen(info);   // tordoppelt (Nebenbefund): wie StueckDb
            masse = (float)info.B.Masse;
            return true;
        }

        // ---- fuer MaterialCache und die Wandmessung ----

        internal static bool IstTuer(Collider col)
        {
            if (!Aktiv.Value) return false;
            try { return col.GetComponentInParent<Door>() != null; }
            catch (Exception e) { Statistik.Fehler(e); return false; }
        }

        /// <summary>Spielwerte-Modus (Physik aus): Grunddaempfung S eines Tuer-Colliders so, dass das Stueck die Blatt-dB im Bezugsband
        /// ergibt (dB = S x Gesamtstaerke x 4,343). -1 = kein Blatt -> Werte der Gruppe "Doors".</summary>
        internal static float TuerS(int colliderId)
        {
            var info = ColliderInfo(colliderId);
            if (info == null) return -1f;
            Rechnen(info);
            float g = Plugin.Gesamtstaerke.Value;
            return g > 1e-4f ? (float)(info.D0 / (4.3429 * g)) : -1f;
        }

        // ---- Rechnung ----

        static float Anpassen(BaseSpatialAudioPortal p, float closure, bool zaehlen = true)
        {
            if (p == null) return closure;
            var mw = p as MultiWindowPortal;
            var sp = p as SpatialAudioPortal;
            if (mw == null && (sp == null || (sp.portalType != BaseSpatialAudioPortal.PortalType.Opening
                                              && sp.portalType != BaseSpatialAudioPortal.PortalType.Window)))
                return closure;                                   // Trigger/statische Portale: Vanilla

            float ziel = Ziel(p, sp, mw);                         // Schliessung im geschlossenen Zustand
            if (ziel < 0f) return closure;

            float zu = Mathf.Lerp(1f, 0.1f, p._transmission);     // Vanilla-Wert "ganz zu"
            float r = zu > 0.001f ? Mathf.Clamp01(closure / zu) : 0f;   // 0 = offen, 1 = zu (auch beim Auf-/Zugehen)
            // 0.8.3 (Review-Punkt 10): ein offenes Mehrfachfenster (Scheiben zerschossen) bekommt vom Spiel selbst schon
            // traversalMaxCost und Depth der freien Flaeche - der Rest der Scheiben steckt damit in der Weg-Rechnung (kein "Restglas").
            float neu = Mathf.Clamp01(ziel * r);

            if (!zaehlen) return neu;
            angepasst++; summeAlt += closure; summeNeu += neu;
            if (mw != null || sp.portalType == BaseSpatialAudioPortal.PortalType.Window) fensterGeaendert++; else tuerGeaendert++;
            return neu;
        }

        static float Ziel(BaseSpatialAudioPortal p, SpatialAudioPortal sp, MultiWindowPortal mw)
        {
            if (Ziele.TryGetValue(p.ID, out float z)) return z;
            float occ = Verdeckung(p);
            if (occ < 0f) return Ziele[p.ID] = -1f;
            var info = PortalInfo(p);
            if (info == null)
            {
                if (!PortalBlatt.ContainsKey(p.ID)) return -1f;    // Tuer noch nicht in der Welt -> spaeter nochmal
                unbekannteTuer++; return Ziele[p.ID] = -1f;
            }
            Rechnen(info);
            // Blatt-dB im Bezugsband auf der Skala der Mod (Wertung 0..1 = 0..Max. insulation), geteilt durch den Raumfaktor des Jobs
            // torfluegel: Portal mit festen Fluegeln (dieselbe Oeffnung fuer die Wegkosten des Spiels)
            PortalWert(info, out double e0, out _, out _);
            float effekt = (float)(e0 / Physik.Dynamik.Value);
            float zw = Mathf.Clamp01(effekt / Mathf.Max(occ, 0.05f));
            if (info.Portal != null && !info.FluegelGesucht) return zw;   // torfluegel: Suche noch offen (Nebenfaden) -> nicht merken
            return Ziele[p.ID] = zw;
        }

        // Genau die Mittelung aus dem Propagation-Job (nur ein Raum draussen -> Wert des Innenraums).
        static float Verdeckung(BaseSpatialAudioPortal p)
        {
            var f = p.FrontRoom; var b = p.BackRoom;
            if (f == null || b == null) return -1f;
            bool fo = f.Type.IsOutdoor(), bo = b.Type.IsOutdoor();
            if (fo != bo) return fo ? b.WallOcclusion : f.WallOcclusion;
            return (f.WallOcclusion + b.WallOcclusion) * 0.5f;
        }

        /// <summary>
        /// Tuer zur DoorID. false = Verzeichnis noch nicht da (spaeter nochmal), true = gesucht (d = null: keine Tuer mit dieser ID).
        /// 0.8.6 Tuerfuge-Fix: bis hierher nur GameWorld.World._interactiveObjectsDictionary. Das World-Objekt (ClientWorld) legt aber
        /// nur das Netzspiel an (NetworkGame -> ClientWorld.Create -> World.Create, Dekompilat EFT 4.1.6); im lokalen Raid (SPT,
        /// LocalGame : BaseLocalGame) gibt es keins -> GameWorld.World ist null, JEDE Tuer blieb ohne Blattmodell (Factory: alle 59
        /// Tuer-/Torportale, z. B. Tor #209 mit 45 dB fugenlos). Das lokale Spiel fuehrt dasselbe Verzeichnis im
        /// LocalClientTriggersModule (Awake: LocationScene.GetAllObjects&lt;WorldInteractiveObject&gt;() je Id, GameWorld.TriggersModule,
        /// von LocalGame angelegt) -> TryFindInteractiveObject. Das Spiel selbst merkt den Fehlschlag nicht: SpatialAudioPortal.SyncState
        /// fragt nur, wenn Singleton&lt;ClientWorld&gt; existiert.
        /// </summary>
        static bool FindeTuer(string id, out WorldInteractiveObject d)
        {
            d = null;
            if (!Singleton<GameWorld>.Instantiated) return false;
            var gw = Singleton<GameWorld>.Instance;
            if (gw == null) return false;
            var w = gw.World;                                   // Netzspiel (FindDoor() wuerde bei Fehlschlag ins Log schreiben)
            if (w != null && w._interactiveObjectsDictionary != null)
            {
                if (!string.IsNullOrEmpty(id)) w._interactiveObjectsDictionary.TryGetValue(id, out d);
                return true;
            }
            var tm = gw.TriggersModule;                         // lokales Spiel
            if (tm == null) return false;
            try { if (!string.IsNullOrEmpty(id)) tm.TryFindInteractiveObject(id, out d); }
            catch (NullReferenceException) { d = null; return false; }   // Verzeichnis noch nicht gefuellt
            return true;
        }

        static WindowBreaker FindeFenster(string id)
        {
            if (string.IsNullOrEmpty(id) || !Singleton<GameWorld>.Instantiated) return null;
            var ws = Singleton<GameWorld>.Instance.Windows;
            if (ws == null) return null;
            return ws.TryGetByKey(id.GetHashCode(), out var wb) ? wb : null;
        }

        /// <summary>Blatt eines Portals (gemerkt je Portal-ID); null = kein Blattmodell. Tuer noch nicht in der Welt -> null, nicht gemerkt.</summary>
        static Info PortalInfo(BaseSpatialAudioPortal p)
        {
            if (PortalBlatt.TryGetValue(p.ID, out var info)) return info;
            var mw = p as MultiWindowPortal;
            var sp = p as SpatialAudioPortal;
            if (mw != null)
            {
                foreach (var wd in mw._serializedWindowsData)
                {
                    var wb = wd != null ? FindeFenster(wd.windowID) : null;
                    if (wb != null) { info = FensterInfo(wb); break; }
                }
                if (info == null) info = ErsatzFenster(p.name);   // Reihe ohne gefundene Scheibe: Ersatz-Scheibe
            }
            else if (sp != null && sp.portalType == BaseSpatialAudioPortal.PortalType.Window)
            {
                var wb = FindeFenster(sp.DoorID);
                info = wb != null ? FensterInfo(wb) : ErsatzFenster(p.name);
            }
            else if (sp != null && sp.portalType == BaseSpatialAudioPortal.PortalType.Opening)
            {
                if (!FindeTuer(sp.DoorID, out var tuer)) return null;   // Welt noch nicht bereit
                // 0.8.6 Tuerfuge-Fix: Tuer nicht im Verzeichnis / zu weit weg (wie SpatialAudioPortal.IsPositionIdentical) -> Ersatz-Tuer
                // nach dem Massengesetz MIT Fuge statt "kein Blattmodell" (dort galt die Schliessung fugenlos)
                if (tuer == null) info = ErsatzTuer(p.name, "door object not found");
                else if (!sp.IsPositionIdentical(tuer.transform.position)) info = ErsatzTuer(p.name, "door object too far from the portal");
                else
                {
                    info = TuerInfo(tuer);
                    if (info.Portal == null) { info.Tuer = tuer; info.Portal = sp; }   // torfluegel: Fluegel dieser Oeffnung suchen
                }
            }
            PortalBlatt[p.ID] = info;
            return info;
        }

        static Info ErsatzFenster(string name)
        {
            var info = new Info { Name = name, Art = "none", Fenster = true, Gruppe = (int)Gruppe.Glass };
            return info;
        }

        /// <summary>
        /// 0.8.6 Tuerfuge-Fix: Tuerportal, dessen Tuerobjekt nicht zu finden ist. Annahmen: Blatt der Gruppe "Doors (game object, base
        /// value)" (Abschnitt 0: Dichte, Plattenwerte wie Holz) mit der Ersatzdicke Holz (Innentuerblatt 40 mm, Abschnitt 4), Fuge der
        /// Innentuer (TuerKern.FugeTuer) - wie jede gemessene Tuer durch TuerKern.Waehlen/Db, nur ohne Material und Messung.
        /// </summary>
        static Info ErsatzTuer(string name, string art) => new Info { Name = name, Art = art, Fenster = false, Gruppe = (int)Gruppe.Door };

        /// <summary>Blatt einer Tuer: groesster BallisticCollider (Material laut Spiel), Dicke am Collider gemessen.</summary>
        static Info TuerInfo(WorldInteractiveObject d)
        {
            int key = d.GetInstanceID();
            if (ObjektBlatt.TryGetValue(key, out var info)) return info;
            BallisticCollider best = null; Collider bestCol = null; float groesse = -1f;
            foreach (var bc in d.GetComponentsInChildren<BallisticCollider>(true))
            {
                var col = bc.GetComponent<Collider>();
                if (col == null) continue;
                float g = col.bounds.size.sqrMagnitude;
                if (g > groesse) { groesse = g; best = bc; bestCol = col; }
            }
            int gruppe = best != null ? (int)MaterialCache.Zuordnen(best.TypeOfMaterial) : (int)Gruppe.Unknown;
            if (gruppe == (int)Gruppe.Unknown)
            {
                string n = d.name.ToLowerInvariant();
                gruppe = n.Contains("steel") ? (int)Gruppe.MetalThick : n.Contains("metal") ? (int)Gruppe.MetalThin : (int)Gruppe.Door;
            }
            if (bestCol == null) bestCol = d.Collider;
            info = new Info { Name = bestCol != null ? bestCol.name : d.name, Gruppe = gruppe, Fenster = false };
            info.Gemessen = DickeMessen(bestCol, out info.Art);
            ObjektBlatt[key] = info;
            return info;
        }

        /// <summary>Blatt eines Fensters: Scheibe aus Glas, Dicke aus dem Scheibenmodell des Spiels (ZSurfs) bzw. am MeshCollider.</summary>
        static Info FensterInfo(WindowBreaker wb)
        {
            int key = wb.GetInstanceID();
            if (ObjektBlatt.TryGetValue(key, out var info)) return info;
            info = new Info { Name = wb.name, Gruppe = (int)Gruppe.Glass, Fenster = true, Art = "none" };
            double t = double.NaN;
            try
            {
                float dz = wb.ZSurfs.y - wb.ZSurfs.x;
                var rt = wb.Renderer != null ? wb.Renderer.transform : wb.transform;
                if (dz > 1e-5f && wb.AxisZ >= 0 && wb.AxisZ < 3)
                {
                    t = dz * Mathf.Abs(rt.lossyScale[wb.AxisZ]);
                    info.Art = "game glass model";
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
            if (double.IsNaN(t) || t <= 0)
            {
                Collider col = wb.GetComponent<MeshCollider>();
                if (col == null && wb.GlassBallisticCollider != null) col = wb.GlassBallisticCollider.GetComponent<Collider>();
                t = DickeMessen(col, out info.Art);
            }
            info.Gemessen = t;
            ObjektBlatt[key] = info;
            return info;
        }

        /// <summary>
        /// Dicke (m) eines Blatt-Colliders quer zu seiner Flaeche. BoxCollider: kleinste Kante (size x lossyScale). MeshCollider:
        /// duennste Achse der Mesh-Grenzen, dort zwei Strahlen von beiden Seiten (Collider.Raycast: Eintritt + Austritt wie die
        /// Wandmessung), Mitte und vier Versaetze (Loch/Glaseinsatz in der Mitte); kein Treffer -> Mesh-Grenzen. NaN = nicht messbar.
        /// </summary>
        static double DickeMessen(Collider col, out string art)
        {
            art = "none";
            if (col == null) return double.NaN;
            try
            {
                var t = col.transform;
                if (col is BoxCollider box)
                {
                    Vector3 g = Vector3.Scale(box.size, t.lossyScale);
                    art = "box";
                    return Mathf.Min(Mathf.Abs(g.x), Mathf.Min(Mathf.Abs(g.y), Mathf.Abs(g.z)));
                }
                if (col is MeshCollider mc && mc.sharedMesh != null)
                {
                    var bb = mc.sharedMesh.bounds;
                    Vector3 sc = t.lossyScale;
                    int k = 0; float best = float.MaxValue;
                    for (int i = 0; i < 3; i++)
                    {
                        float w = Mathf.Abs(bb.extents[i] * sc[i]);
                        if (w < best) { best = w; k = i; }
                    }
                    Vector3 ach = Vector3.zero; ach[k] = 1f;
                    Vector3 dir = t.TransformDirection(ach).normalized;
                    Vector3 u = Vector3.zero, v = Vector3.zero;
                    u[(k + 1) % 3] = bb.extents[(k + 1) % 3] * 0.3f; v[(k + 2) % 3] = bb.extents[(k + 2) % 3] * 0.3f;
                    float R = col.bounds.extents.magnitude + 0.5f;
                    for (int i = 0; i < 5; i++)
                    {
                        Vector3 lok = bb.center + (i == 1 ? u : i == 2 ? -u : i == 3 ? v : i == 4 ? -v : Vector3.zero);
                        Vector3 o = t.TransformPoint(lok);
                        if (col.Raycast(new Ray(o + dir * R, -dir), out RaycastHit h1, 2f * R)
                            && col.Raycast(new Ray(o - dir * R, dir), out RaycastHit h2, 2f * R))
                        {
                            float d = 2f * R - h1.distance - h2.distance;
                            if (d > 0.0005f) { art = "mesh rays"; return d; }
                        }
                    }
                    art = "mesh bounds";
                    return 2f * best;
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
            return double.NaN;
        }

        // ---- tordoppelt (12.11.2026): gehoert ein Collider zum Blatt/zur Scheibe eines Audio-Portals? ----
        // Zuordnung wie das Spiel selbst (Dekompilat EFT 4.1.6), nicht ueber Namen einzelner Tore:
        //  - SpatialAudioDataLoader.LoadRoomsAndPortals fuellt SpatialAudioDataContainer.InteractivePortalsByID: Id des interaktiven
        //    Objekts -> Portal (SpatialAudioPortal: DoorID; MultiWindowPortal: windowID je Scheibe; IIdentifiable.Ids).
        //  - Das Objekt eines Colliders: EFT.Interactive.Door (WorldInteractiveObject.Id) bzw. WindowBreaker (Id) per
        //    GetComponentInParent - wie MaterialCache/IstTuer und ColliderInfo die Tuer finden.
        //  - Gueltig nur, wenn das Objekt beim Portal steht: BaseSpatialAudioPortal.IsPositionIdentical (das Spiel prueft das in
        //    SyncState/OnDoorInteracted genauso, Abstand < 15 m).
        // Beispiel Factory, Tor #209 (Portal 2,1 m2): das Door-Objekt ist "Enterance_Gate_01_Door", ein Kind des Torfluegels
        // (LogOutput 12.11. 14:19-Raid, ORBIT-Trefferpfad: .../Enterance_Gate_01/Enterance_Gate_01_R/Enterance_Gate_01_Door/
        // Enterance_Gate_01_Door_BALLISTIC_Metalthick neben .../Enterance_Gate_01_R/Enterance_Gate_01_R_BALLISTIC_Metalthick).
        // "Door/Enterance_Gate_01_Door_BALLISTIC_Metalthick" -> Blatt von #209. "Enterance_Gate_01_R/_L_BALLISTIC" sind die festen
        // Torfluegel (kein Door darueber, Klang-Log: Gruppe MetalThick statt Door) -> Wand neben dem Blatt, KEIN Portal-Blatt.
        static readonly Dictionary<int, BaseSpatialAudioPortal> ColliderPortal = new Dictionary<int, BaseSpatialAudioPortal>();

        /// <summary>tordoppelt: true = der Collider ist Blatt (Tuer/Tor) bzw. Scheibe eines Audio-Portals; breite/hoehe = Rechteck des
        /// Portals (GetPortalSize, wie Raumweg/RaumwegNetz FlaecheP). Nur Hauptfaden (Resources.InstanceIDToObject).</summary>
        internal static bool IstPortalBlatt(int colliderId, out float breite, out float hoehe)
        {
            breite = hoehe = 0f;
            if (colliderId == 0) return false;
            try
            {
                // torfluegel: ein fester Fluegel gehoert zur Oeffnung seiner Baugruppe (der Raumweg rechnet ihn mit) -> wie ein Blatt
                if (FluegelAn.Value && FluegelPortal.TryGetValue(colliderId, out var fp) && fp != null)
                {
                    var fg = fp.GetPortalSize();
                    breite = Mathf.Abs(fg.x); hoehe = Mathf.Abs(fg.y);
                    return true;
                }
                if (!ColliderPortal.TryGetValue(colliderId, out var p))
                {
                    if (!PortalSuchen(colliderId, out p)) return false;   // Portaldaten noch nicht geladen: nicht merken
                    ColliderPortal[colliderId] = p;
                }
                if (p == null) return false;
                var g = p.GetPortalSize();
                breite = Mathf.Abs(g.x); hoehe = Mathf.Abs(g.y);
                return true;
            }
            catch (Exception e) { Statistik.Fehler(e); return false; }
        }

        static bool PortalSuchen(int colliderId, out BaseSpatialAudioPortal p)
        {
            p = null;
            if (!DataProvider.TryGetData<SpatialAudioDataContainer>(out var data) || data == null || data.InteractivePortalsByID == null
                || data.InteractivePortalsByID.Count == 0) return false;
            var col = Resources.InstanceIDToObject(colliderId) as Collider;
            if (col == null) return true;
            string id = null; Vector3 pos = default;
            var tuer = col.GetComponentInParent<Door>();
            if (tuer != null) { id = tuer.Id; pos = tuer.transform.position; }
            else
            {
                var wb = col.GetComponentInParent<WindowBreaker>();
                if (wb != null) { id = wb.Id; pos = wb.transform.position; }
            }
            if (string.IsNullOrEmpty(id) || !data.InteractivePortalsByID.TryGetValue(id, out var q) || q == null) return true;
            var sp = q as SpatialAudioPortal;
            bool art = q is MultiWindowPortal || (sp != null && (sp.portalType == BaseSpatialAudioPortal.PortalType.Opening
                                                                || sp.portalType == BaseSpatialAudioPortal.PortalType.Window));
            if (art && q.IsPositionIdentical(pos)) p = q;
            return true;
        }

        /// <summary>Blatt der Tuer, zu der ein getroffener Collider gehoert (null = keine Tuer / Abschnitt 4 aus).</summary>
        static Info ColliderInfo(int colliderId)
        {
            if (!An) return null;
            if (ColliderBlatt.TryGetValue(colliderId, out var info)) return info;
            info = null;
            try
            {
                var door = (Resources.InstanceIDToObject(colliderId) as Collider)?.GetComponentInParent<Door>();
                if (door != null) info = TuerInfo(door);
            }
            catch (Exception e) { Statistik.Fehler(e); }
            ColliderBlatt[colliderId] = info;
            return info;
        }

        static TuerVorgaben Vorgaben() => new TuerVorgaben
        {
            Messen = Messen.Value, ErsatzHolz = ErsatzHolz.Value, ErsatzGlas = ErsatzGlas.Value, Blech = Blech.Value * 0.001,
            FugeTuer = TuerKern.FugeTuer, FugeFenster = TuerKern.FugeFenster   // 0.8.6 Livetest-Fix: Fugen als paralleler Durchlass (TuerKern)
        };

        /// <summary>Blatt waehlen und dB je Band rechnen (nur wenn sich Einstellungen geaendert haben).</summary>
        static void Rechnen(Info info)
        {
            if (info.Portal != null && !info.FluegelGesucht && System.Threading.Thread.CurrentThread.ManagedThreadId == hauptFaden)
            {
                FluegelSuchen(info);   // torfluegel: einmal je Oeffnung, nur Hauptfaden (Collider.Raycast)
                info.Stand = -1;
            }
            if (info.Stand == stand) return;
            int g = info.Gruppe;
            var v = Vorgaben();
            info.B = TuerKern.Waehlen(g, Physik.Dichte[g].Value, Physik.Massiv[g].Value, info.Gemessen, info.Fenster, v);
            var f = Wege.Frequenzen();
            TuerKern.Baender(info.B, f[0], f[1], f[2], Physik.Koinzidenz.Value, Physik.VerlustFaktor.Value, Physik.Dynamik.Value,
                             out info.D0, out info.D1, out info.D2);
            // torfluegel: jeder Fluegel ist ein Blatt seines Materials und seiner gemessenen Dicke (TuerKern.Waehlen, mit Fuge wie die Tuer);
            // Sum S_i tau_i je Band, bezogen auf das Portal-Rechteck (TuerKern.TauMitFluegeln). Die Baender ungekappt wie RaumwegNetz.
            double s0 = 0, s1 = 0, s2 = 0;
            for (int i = 0; i < info.FluegelN; i++)
            {
                int gi = info.FluegelGruppe[i];
                var b = TuerKern.Waehlen(gi, Physik.Dichte[gi].Value, Physik.Massiv[gi].Value, info.FluegelGemessen[i], false, v);
                info.FluegelBlatt[i] = b;
                TuerKern.Baender(b, f[0], f[1], f[2], Physik.Koinzidenz.Value, Physik.VerlustFaktor.Value, WegeKern.BandGrenzeDb, out double a0, out double a1, out double a2);
                double fl = info.FluegelFlaeche[i];
                s0 += fl * Math.Pow(10, -a0 / 10); s1 += fl * Math.Pow(10, -a1 / 10); s2 += fl * Math.Pow(10, -a2 / 10);
            }
            double dyn = Physik.Dynamik.Value;
            info.E0 = TuerKern.DbMitFluegeln(info.D0, info.PortalFlaeche, s0, dyn);
            info.E1 = TuerKern.DbMitFluegeln(info.D1, info.PortalFlaeche, s1, dyn);
            info.E2 = TuerKern.DbMitFluegeln(info.D2, info.PortalFlaeche, s2, dyn);
            info.Stand = stand;
            // einmal je Raid und Blattart ins BepInEx-Log (Material, Dicke gemessen/Ersatz, dB je Band)
            string schl = Kurzname(info.Name) + "|" + g + "|" + info.B.Dicke.ToString("F3", CultureInfo.InvariantCulture) + "|" + info.B.Quelle;
            if (Geloggt.Count < LogMax && Geloggt.Add(schl))
                Plugin.Log.LogInfo("Doors/windows: " + Text(info) + " '" + info.Name + "'" + (Geloggt.Count == LogMax ? " (further leaves not listed)" : ""));
        }

        static string Kurzname(string n)
        {
            if (string.IsNullOrEmpty(n)) return "";
            int i = n.IndexOf(" (", StringComparison.Ordinal);
            return i > 0 ? n.Substring(0, i) : n;
        }

        static string Text(Info info)
        {
            var inv = CultureInfo.InvariantCulture;
            var b = info.B;
            string dicke = b.Hohl
                ? "hollow 2 x " + (b.Platte * 1000).ToString("F1", inv) + " mm" + (double.IsNaN(info.Gemessen) ? "" : " (leaf " + info.Gemessen.ToString("F3", inv) + " m)")
                : b.Dicke.ToString("F3", inv) + " m";
            string quelle = TuerKern.QuelleText(b.Quelle) + (b.Quelle == DickeQuelle.Ersatz && !double.IsNaN(info.Gemessen) && !Messen.Value ? " (measuring off)" : "")
                            + (b.Quelle == DickeQuelle.Unplausibel ? " (measured " + info.Gemessen.ToString("F3", inv) + " m)" : "")
                            + (info.Art != null && info.Art != "none" ? ", " + info.Art : "");
            double fc = TuerKern.Fc(b);
            return (info.Fenster ? "window " : "door ") + ((Gruppe)info.Gruppe) + " " + dicke + " " + quelle + ", " + b.Masse.ToString("F1", inv) + " kg/m2"
                   + (fc > 0 ? ", fc " + fc.ToString("F0", inv) + " Hz" : "")
                   + " -> " + info.D0.ToString("F1", inv) + "/" + info.D1.ToString("F1", inv) + "/" + info.D2.ToString("F1", inv) + " dB"
                   + FluegelText(info);
        }

        // torfluegel: " + 2 fixed wings 20.0 m2 (MetalThick hollow 2 x 1.5 mm) -> opening 2.1 m2 11.7/12.7/12.8 dB"
        static string FluegelText(Info info)
        {
            if (info.FluegelN <= 0) return "";
            var inv = CultureInfo.InvariantCulture;
            var b = info.FluegelBlatt[0];
            string art = ((Gruppe)info.FluegelGruppe[0]) + (b.Hohl ? " hollow 2 x " + (b.Platte * 1000).ToString("F1", inv) + " mm" : " " + b.Dicke.ToString("F3", inv) + " m");
            return " + " + info.FluegelN + " fixed wing(s) " + info.FluegelSumme.ToString("F1", inv) + " m2 (" + art + ")"
                   + (FluegelAn.Value ? " -> opening " + info.PortalFlaeche.ToString("F1", inv) + " m2 " + info.E0.ToString("F1", inv) + "/" + info.E1.ToString("F1", inv) + "/" + info.E2.ToString("F1", inv) + " dB" : " (not counted: switch off)");
        }

        /// <summary>torfluegel: Daemmung des Portals je Band (Blatt + feste Fluegel; Schalter aus oder keine Fluegel: das Blatt).</summary>
        static void PortalWert(Info info, out double d0, out double d1, out double d2)
        {
            bool mit = FluegelAn != null && FluegelAn.Value && info.FluegelN > 0 && !double.IsNaN(info.E0);
            d0 = mit ? info.E0 : info.D0; d1 = mit ? info.E1 : info.D1; d2 = mit ? info.E2 : info.D2;
        }

        // ---- torfluegel (12.11.2026): feste Fluegel einer Tor-/Tuerbaugruppe suchen (Regel und Quellen: TuerKern.cs) ----
        // Baugruppe = der groesste Teilbaum ueber dem Door-Objekt, der keine andere Tuer/kein anderes Fenster enthaelt und nur
        // Blattwerkstoffe (Metall, Holz, Tuerblatt): Factory-Tor "Enterance_Gate_01" (Fluegel L, Fluegel R mit Schlupftuer, Rahmen);
        // eine Ebene hoeher ("Enterance_Gate Group") liegen die anderen Tore -> Schluss. Eine Wand (Beton, Stein) beendet die Suche immer.
        // Teil = BallisticCollider der Baugruppe ausserhalb des Door-Objekts. Strahlenraster 24 x 24 senkrecht zur Portalebene ueber sein
        // Rechteck in der Ebene: Treffer innerhalb der Hohlraumtiefe (WandKetteKern.HohlraumMax, 0,22 m bei 250 Hz: naeher ist es eine
        // Konstruktion) zaehlen als Flaeche, das Portal-Rechteck selbst nicht (es traegt das Blatt). Fluegel = Platte (TuerKern.IstFluegel);
        // ein geoeffneter Fluegel liegt nicht in der Ebene und zaehlt nicht. Offline-Nachbau fuer alle Factory-Tuerportale:
        // review-20261110/torfluegel/sonde/regel.tsv (11 Torportale je 19-20 m2, alle anderen Tueren 0).
        const int Raster = 24;
        static readonly Dictionary<int, int> gueltig = new Dictionary<int, int>();   // Transform-ID -> einzige Tuer darin (0 = ungueltig)
        static readonly Dictionary<int, BaseSpatialAudioPortal> FluegelPortal = new Dictionary<int, BaseSpatialAudioPortal>();
        static int fluegelGefunden;

        static bool Blattwerkstoff(int g) => g == (int)Gruppe.MetalThick || g == (int)Gruppe.MetalThin || g == (int)Gruppe.WoodThick
                                           || g == (int)Gruppe.WoodThin || g == (int)Gruppe.Door;

        /// <summary>Gehoert der Teilbaum t nur zu dieser einen Tuer und besteht nur aus Blattwerkstoffen?</summary>
        static bool Baugruppe(Transform t, WorldInteractiveObject tuer)
        {
            int key = t.GetInstanceID();
            if (!gueltig.TryGetValue(key, out int einzig))
            {
                einzig = 0;
                var tueren = t.GetComponentsInChildren<WorldInteractiveObject>(true);
                var fenster = t.GetComponentsInChildren<WindowBreaker>(true);
                if (tueren.Length == 1 && fenster.Length == 0)
                {
                    einzig = tueren[0].GetInstanceID();
                    foreach (var bc in t.GetComponentsInChildren<BallisticCollider>(true))
                        if (!Blattwerkstoff((int)MaterialCache.Zuordnen(bc.TypeOfMaterial))) { einzig = 0; break; }
                }
                gueltig[key] = einzig;
            }
            return einzig != 0 && einzig == tuer.GetInstanceID();
        }

        static void FluegelSuchen(Info info)
        {
            info.FluegelGesucht = true; info.FluegelN = 0; info.FluegelSumme = 0;
            var sp = info.Portal; var tuer = info.Tuer;
            if (sp == null || tuer == null) return;
            try
            {
                var pc = sp.portalCollider;
                if (pc == null) return;
                var tr = sp.transform;
                Vector3 m = pc.bounds.center, u = tr.right.normalized, v = tr.up.normalized, n = tr.forward.normalized;
                var hs = sp.GetPortalHalfSize();
                float hx = Mathf.Abs(hs.x), hy = Mathf.Abs(hs.y);
                info.PortalFlaeche = 4.0 * hx * hy;
                if (!(info.PortalFlaeche > 1e-4)) return;
                Transform a = null;
                for (var t = tuer.transform.parent; t != null; t = t.parent) { if (!Baugruppe(t, tuer)) break; a = t; }
                if (a == null) return;
                float tol = (float)WandKetteKern.HohlraumMax(Wege.Frequenzen()[0]);
                foreach (var bc in a.GetComponentsInChildren<BallisticCollider>(true))
                {
                    if (info.FluegelN >= MaxFluegel) break;
                    var col = bc.GetComponent<Collider>();
                    if (col == null || !col.enabled || col.isTrigger || !col.gameObject.activeInHierarchy) continue;
                    if (col.transform.IsChildOf(tuer.transform)) continue;   // das Blatt selbst
                    var bb = col.bounds;
                    float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
                    for (int k = 0; k < 8; k++)
                    {
                        Vector3 e = bb.center + Vector3.Scale(bb.extents, new Vector3((k & 1) != 0 ? 1 : -1, (k & 2) != 0 ? 1 : -1, (k & 4) != 0 ? 1 : -1)) - m;
                        float uu = Vector3.Dot(e, u), vv = Vector3.Dot(e, v);
                        u0 = Mathf.Min(u0, uu); u1 = Mathf.Max(u1, uu); v0 = Mathf.Min(v0, vv); v1 = Mathf.Max(v1, vv);
                    }
                    float du = (u1 - u0) / Raster, dv = (v1 - v0) / Raster;
                    if (!(du > 1e-4f) || !(dv > 1e-4f)) continue;
                    float L = Mathf.Abs(Vector3.Dot(bb.center - m, n)) + bb.extents.magnitude + 0.5f;
                    int treffer = 0;
                    for (int i = 0; i < Raster; i++)
                        for (int j = 0; j < Raster; j++)
                        {
                            float uu = u0 + (i + 0.5f) * du, vv = v0 + (j + 0.5f) * dv;
                            if (Mathf.Abs(uu) <= hx && Mathf.Abs(vv) <= hy) continue;   // Portal-Rechteck: traegt das Blatt
                            Vector3 o = m + u * uu + v * vv;
                            if ((col.Raycast(new Ray(o + n * L, -n), out RaycastHit h, 2f * L) && Mathf.Abs(Vector3.Dot(h.point - m, n)) <= tol)
                                || (col.Raycast(new Ray(o - n * L, n), out h, 2f * L) && Mathf.Abs(Vector3.Dot(h.point - m, n)) <= tol))
                                treffer++;
                        }
                    double flaeche = treffer * (double)du * dv, huelle = (u1 - u0) * (double)(v1 - v0);
                    if (!TuerKern.IstFluegel(flaeche, huelle)) continue;
                    int k2 = info.FluegelN++;
                    info.FluegelGruppe[k2] = (int)MaterialCache.Zuordnen(bc.TypeOfMaterial);
                    info.FluegelFlaeche[k2] = flaeche;
                    info.FluegelGemessen[k2] = DickeMessen(col, out _);
                    info.FluegelId[k2] = col.GetInstanceID();
                    info.FluegelSumme += flaeche;
                    FluegelPortal[col.GetInstanceID()] = sp;
                    fluegelGefunden++;
                }
                if (info.FluegelN > 0)
                    Plugin.Log.LogInfo($"Doors/windows: gate '{tuer.name}' (portal #{sp.ID}, {info.PortalFlaeche:F1} m2): {info.FluegelN} fixed wing(s), " +
                                       $"{info.FluegelSumme:F1} m2 in the same plane - counted with the opening (Gate wings: count with the opening).");
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }
    }
}
