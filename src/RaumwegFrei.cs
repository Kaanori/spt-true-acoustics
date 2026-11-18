using System;
using Audio.SpatialSystem;
using EFT.DataProviding;
using UnityEngine;

namespace Materialklang
{
    // 0.8.6 - Raumweg fuer Quellen ohne Okklusion im Spiel (BTR, Taxi, Lok, Flugzeug, Fackeln, Feuer, Generatoren, Ambient-Spieler;
    // Abschnitte 15/17/18, WandDaempfung.cs). Meldung Streets 09.11.2026: "mitten im Gebaeude, der BTR kommt direkt durch die Waende
    // statt durch Tueren und Fenster". Diese Quellen bekamen nur die Wand (5 Linien) plus einen pauschalen Umweg (15 dB draussen /
    // 25 dB + 5 je Portal zwischen Raeumen), keine Route durch die Oeffnungen und keine Richtung ueber die Oeffnung.
    // Jetzt dieselbe Rechnung wie fuer Occluder-Quellen:
    //  - verschiedene Raeume mit Routen des Spiels: Routendaten des Raumpaars direkt aus SpatialAudioDataContainer (dieselben
    //    Felder, die SoundPropagationJobScheduler.PrepareData in den Job gibt), alle Routen (Qualitaet 1), RaumwegNetz.Rechne mit
    //    Kappung und Wiederverwendung, RoutenMischer (ohne eigene Ueberblendung: der WandFilter blendet in 300 ms).
    //  - keine Route, Quelle oder Hoerer draussen (auch beide draussen): Ersatzweg (Oeffnungen des Innenraums + ISO 9613-2
    //    Abschirmung ueber Daecher/Gelaende bzw. nur die Abschirmung auf der Direktlinie), gleicher Cache wie Raumweg.Ersatzweg.
    //  - beide drinnen ohne Route: dicht (nur die Wand), wie bei den Occluder-Quellen.
    //  - gleicher Innenraum oder Raumdaten fehlen: kein Raumweg -> der Umweg der Wanddaempfung (Annahme) bleibt.
    internal static partial class Raumweg
    {
        internal enum FreiArt { Keine, Route, Ersatz, Dicht }

        /// <summary>Raumweg-Zustand einer Quelle ohne Okklusion (je WandDaempfung-Eintrag).</summary>
        internal sealed class FreiWeg
        {
            internal readonly Zustand Z = new Zustand();
            public FreiArt Art;
            public double D0, D1, D2;          // Daemmung des Raumwegs je Band (Bezug / 1 kHz / 4 kHz)
            public V3 Aus;                     // energiegewichteter Austritt (Richtung vom Hoerer)
            public double Buendel;
            public int Routen;                 // Routen des Raumpaars
            public string Text;                // Klang-Log (nur bei Log an)
            public string Grund;               // warum kein Raumweg (Log)
            public void Vergessen() { Z.Vergessen(); Art = FreiArt.Keine; Aus = default; Text = null; }
        }

        static readonly double[] freiDb = new double[3];
        static int dFreiRoute, dFreiErsatz, dFreiDicht, dFreiKeine;

        /// <summary>Raumweg Quelle q (Raum qr) -> Hoerer h (Raum hr). Ergebnis in w (Baender, Austritt, Buendelung).</summary>
        internal static FreiArt Frei(FreiWeg w, Vector3 q, Vector3 h, ISpatialAudioRoom qr, ISpatialAudioRoom hr,
                                     System.Collections.Generic.HashSet<int> eigen = null)
        {
            w.Art = FreiArt.Keine; w.Text = null; w.Grund = null; w.Aus = default; w.Buendel = 0; w.Routen = 0;
            try
            {
                if (!An) { w.Grund = "room paths (physics) off"; dFreiKeine++; return FreiArt.Keine; }
                if (qr == null || hr == null || !qr.IsValid || !hr.IsValid) { w.Grund = "rooms unknown"; dFreiKeine++; return FreiArt.Keine; }
                double jetzt = Time.realtimeSinceStartup;
                float dyn = Physik.Dynamik.Value;
                var z = w.Z;
                bool log = KlangLog.An;
                bool beideDraussen = qr.IsOutdoor && hr.IsOutdoor;
                if (!beideDraussen && qr.ID == hr.ID) { w.Grund = "same room"; dFreiKeine++; return FreiArt.Keine; }

                if (!beideDraussen && MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated)
                {
                    var st = MonoBehaviourSingleton<SpatialAudioSystem>.Instance._audioRoomStorage;
                    if (st != null && st.TryGetMatchingRoomPair(qr, hr, out var rp, out bool rev) && rp != null && rp.Routes != null && rp.Routes.Length > 0)
                    {
                        w.Routen = rp.Routes.Length;
                        if (DataProvider.TryGetData<SpatialAudioDataContainer>(out var data) && data != null
                            && data.TryGetRoutesData(rp.ID, out var routes) && routes.IsCreated && routes.Length > 0
                            && data.TryGetPortalIndicesData(rp.ID, out var idx) && idx.IsCreated)
                        {
                            var liste = data.StaticPortalDataList;
                            if (liste.IsCreated)
                            {
                                PortalTabelle(data);
                                if (z.Paar != rp.ID || z.Umgekehrt != rev || z.Ersatz) z.Vergessen();
                                z.Umgekehrt = rev; z.A = rev ? h : q; z.B = rev ? q : h;
                                z.RaumA = rev ? hr : qr; z.RaumB = rev ? qr : hr; z.Paar = rp.ID; z.Hat = true; z.Ersatz = false; z.Behaelter = null;
                                w.Routen = routes.Length;
                                long tr = Laufzeit.Jetzt; int bild = Time.frameCount;
                                schirmImpl.Beginn(q, h, eigen);
                                bool ok = RoutenRechnen(routes, idx, liste, routes.Length, z, float.NaN, jetzt, 0, schirmImpl);
                                schirmImpl.Ende();
                                ZeitRechnen.Dazu(tr, bild);
                                if (ok && Ergebnis(w, h, dyn))
                                {
                                    w.Art = FreiArt.Route; dFreiRoute++;
                                    if (log) w.Text = z.Kette + RoutenText(z);
                                    return w.Art;
                                }
                            }
                        }
                        // Raumpaar mit Routen, aber keine Routendaten geladen: Annahme der Wanddaempfung bleibt
                        w.Grund = "route data of the room pair not loaded"; dFreiKeine++; z.Vergessen();
                        return FreiArt.Keine;
                    }
                }

                // keine Route des Spiels
                if (qr.IsOutdoor || hr.IsOutdoor)
                {
                    if (!z.Ersatz) z.Vergessen();
                    z.A = q; z.B = h; z.Umgekehrt = false; z.RaumA = qr; z.RaumB = hr; z.Paar = 0xFFFFFFFEu; z.Ersatz = true; z.Behaelter = null;
                    long tr = Laufzeit.Jetzt; int bild = Time.frameCount;
                    bool ok = Ersatzweg.Fuellen(z, q, h, qr, hr, jetzt, out string text, eigen);   // 0.8.6: eigenes Fahrzeug schirmt nicht ab
                    ZeitRechnen.Dazu(tr, bild);
                    if (ok && Ergebnis(w, h, dyn))
                    {
                        w.Art = FreiArt.Ersatz; dFreiErsatz++;
                        if (log) w.Text = text + RoutenText(z);
                        return w.Art;
                    }
                    if (Ersatzweg.Aktiv == null || !Ersatzweg.Aktiv.Value) { w.Grund = "diffraction without a game route off"; dFreiKeine++; z.Vergessen(); return FreiArt.Keine; }
                    w.Text = log ? text : null;
                }
                // beide drinnen ohne Route, oder der Innenraum hat keine Oeffnung nach draussen: dicht (wie Occluder-Quellen)
                z.Vergessen();
                w.Art = FreiArt.Dicht; dFreiDicht++;
                return w.Art;
            }
            catch (Exception e) { Statistik.Fehler(e); w.Grund = "error"; return FreiArt.Keine; }
        }

        static readonly AussenSchirm schirmImpl = new AussenSchirm();

        /// <summary>0.8.6 (Ground Zero 09.11.2026 21:34 MARK 6/7, Ursache: RaumwegNetz.Schirmen): Abschirmung der Strecken einer Route
        /// im Freien - dasselbe Hoehenprofil wie der Ersatzweg (Ersatzweg.Profil, ISO 9613-2, eigenes Fahrzeug zaehlt nicht).
        /// Cache je Strecke: Endpunkt an einem Portal = Portalindex (alle Routen ueber dasselbe Portal teilen das Profil, das Portal
        /// liegt fest), an Quelle/Hoerer = 2-m- bzw. 1-m-Zelle; 1 s gueltig wie der Ersatzweg.</summary>
        sealed class AussenSchirm : RaumwegNetz.IAussenSchirm
        {
            sealed class Wert { public float Zeit; public Hindernis H; }
            readonly System.Collections.Generic.Dictionary<long, Wert> cache = new System.Collections.Generic.Dictionary<long, Wert>();
            Vector3 q, h; System.Collections.Generic.HashSet<int> eigen;
            internal static int DGerechnet, DCache;

            public void Beginn(Vector3 quelle, Vector3 hoerer, System.Collections.Generic.HashSet<int> eigenFz) { q = quelle; h = hoerer; eigen = eigenFz; }
            public void Ende() { eigen = null; }

            long Ende(int portal, Vector3 p, float zelle)
            {
                unchecked
                {
                    if (portal >= 0) return 1_000_003L + portal;
                    long k = Mathf.FloorToInt(p.x / zelle); k = k * 31 + Mathf.FloorToInt(p.y / zelle); k = k * 31 + Mathf.FloorToInt(p.z / zelle);
                    return k;
                }
            }

            public bool Strecke(V3 a, V3 b, int von, int bis, out Hindernis hi)
            {
                long key;
                unchecked
                {
                    key = Ende(von, q, 2f) * 1_000_000_007L + Ende(bis, h, 1f);
                    if (eigen != null) key = key * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(eigen);
                }
                float t = Time.realtimeSinceStartup;
                if (cache.TryGetValue(key, out var w) && t - w.Zeit <= 1f) { DCache++; hi = w.H; return true; }
                if (cache.Count > 512) cache.Clear();
                hi = Ersatzweg.ProfilFrei(new Vector3((float)a.X, (float)a.Y, (float)a.Z), new Vector3((float)b.X, (float)b.Y, (float)b.Z), eigen);
                cache[key] = new Wert { Zeit = t, H = hi };
                DGerechnet++;
                return true;
            }

            internal void Leeren() => cache.Clear();
        }

        static bool Ergebnis(FreiWeg w, Vector3 h, float dyn)
        {
            var z = w.Z;
            // 10.11. (klangfarbe): Summe ueber die echten Baender der Routen; D0..D2 bleiben ECHT (ungedeckelt) - die Wanddaempfung
            // summiert sie mit der Wand und deckelt erst die Lautstaerke (WandDaempfung, WegeKern.Deckeln). Wertung gedeckelt wie bisher.
            if (!z.Mix.Summe(WegeKern.BandGrenzeDb, freiDb, V(h), out var aus, out double buendel)) return false;
            // richtungvorrang (16.11.): Richtung mit Praezedenz wie im Raumweg (RichtungKern.MischRichtung), Baender unveraendert
            if (!RichtungKern.MischRichtung(z.Mix.Eintraege, V(h), out aus, out buendel)) { aus = default; buendel = 0; }
            w.D0 = freiDb[0]; w.D1 = freiDb[1]; w.D2 = freiDb[2]; w.Aus = aus; w.Buendel = buendel;
            z.Wertung = Mathf.Clamp((float)(freiDb[0] / dyn), MinWertung, 1f); z.Austritt = aus; z.Buendel = buendel;
            return true;
        }

        internal static void FreiLoggen()
        {
            if (dFreiRoute + dFreiErsatz + dFreiDicht + dFreiKeine == 0) return;
            Plugin.Log.LogInfo($"[Diagnostics] Room paths of sources without game occlusion (wall damping): {dFreiRoute} through the game's routes, " +
                               $"{dFreiErsatz} over the obstacles (no route), {dFreiDicht} closed (both indoors without a route: walls only), " +
                               $"{dFreiKeine} without a room path (same indoor room / no room data: detour assumption); " +
                               $"outdoor stretches of the routes screened: {AussenSchirm.DGerechnet} profiles, {AussenSchirm.DCache} from the cache.");
            AussenSchirm.DGerechnet = AussenSchirm.DCache = 0;
            dFreiRoute = dFreiErsatz = dFreiDicht = dFreiKeine = 0;
        }
    }
}
