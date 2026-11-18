using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using EFT.DataProviding;
using UnityEngine;

namespace Materialklang
{
    // 0.8.6 - Ersatzweg, wenn das Spiel fuer ein Raumpaar keine Route kennt (Ursache und Formeln: RichtungKern.cs, Teil 2).
    // ParallelSoundPropagationCalculator.OnUpdate: TryGetMatchingRoomPair ohne Routen -> _propagationScore = 1, Fruehabbruch
    // (kein Job). Im Physik-Modus heisst 1 = volle Daemmung (60 dB) = stumm, obwohl Quelle oder Hoerer im Freien steht.
    // Ersatz, nur wenn Quelle ODER Hoerer draussen ist:
    //  - drinnen <-> draussen: jede Oeffnung des Innenraums nach draussen (hoechstens 4, kuerzester Weg zuerst) als Route mit
    //    einer Oeffnung (RaumwegKern.Route, Zustand der Oeffnung wie bei jeder Route) plus Abschirmung des Wegs im Freien
    //    (Quelle -> Oeffnung bzw. Oeffnung -> Hoerer) nach ISO 9613-2 aus dem Hoehenprofil (Strahlen von oben).
    //  - draussen <-> draussen: nur die Abschirmung auf der Direktlinie.
    //  - beide drinnen, oder der Innenraum hat keine Oeffnung nach draussen: bleibt dicht (Wert des Spiels).
    // Der Wert laeuft durch denselben RoutenMischer wie jeder Raumweg (Baender, Tiefpass, Ueberblendung, Klang-Log, Richtung).
    internal static partial class Raumweg
    {
        internal static class Ersatzweg
        {
            internal static ConfigEntry<bool> Aktiv, HausAn;   // 0.8.6: HausAn = Bebauung (ISO 9613-2 Anhang A.3)
            const int MaxOeffnungen = 4, MaxJeBild = 6, ProfilN = 48;
            const float CacheS = 1.0f, ObenM = 200f;

            sealed class Kand
            {
                public ulong Id; public double D0, D1, D2; public V3 Aus; public RaumwegErgebnis Erg; public int Pi = -1;
                public Hindernis H; public double I0;
            }
            sealed class Ergebnis { public float Zeit; public readonly List<Kand> K = new List<Kand>(); public string Text; }

            static readonly Dictionary<long, Ergebnis> Cache = new Dictionary<long, Ergebnis>();
            static int bild = -1, jeBild;
            static readonly double[] ps = new double[HindernisKern.MaxProfil], py = new double[HindernisKern.MaxProfil];
            static readonly double[] hs = new double[HindernisKern.MaxProfil + 2], hy = new double[HindernisKern.MaxProfil + 2];
            static readonly RaumPortal[] rp1 = new RaumPortal[1];
            static readonly V3[] pk1 = new V3[1];
            static readonly double[] ra = new double[2], rv = new double[2], bg = new double[1], um = new double[1];
            static readonly int[] idx1 = new int[1], geo1 = new int[1];
            static readonly List<(int pi, double kosten)> wahl = new List<(int, double)>();

            static int dGerechnet, dCache, dDicht, dBudget, dStrahlen;
            static double dSummeDb; static int dWerte;

            internal static void Binden(ConfigFile c)
            {
                Aktiv = c.Bind("0. Physics Mode (real values)", "Room paths: diffraction when the game has no route", true,
                    "When the game knows no route between the source's room and yours (it would be fully insulated = silent) and one of you is outdoors: " +
                    "the sound goes over the obstacles (roofs, terrain; ISO 9613-2 screening, at most 20 dB for one edge and 25 dB for several) and in through the openings of the indoor room. " +
                    "Both indoors without a route stays silent. Off = the game value (silent).");
                Aktiv.SettingChanged += (_, __) => Cache.Clear();
                HausAn = c.Bind("0. Physics Mode (real values)", "Room paths: housing along the path (ISO 9613-2 A.3)", true,
                    "Screening over the obstacles only knows the corners of the rubber band (the highest roof, the roof in front of you); every further row of houses " +
                    "below it did not count - a BTR 220 m away behind several blocks lost only 12-25 dB. On = attenuation by housing after ISO 9613-2 annex A.3: " +
                    "0.1 dB per metre of the line of sight that runs below roofs (buildings at least 2 m above the line; terrain and the buildings whose roof edges " +
                    "are already in the screening do not count), at most 10 dB, all bands. Off = as before.");
                HausAn.SettingChanged += (_, __) => Cache.Clear();
            }

            internal static void SzeneGewechselt() { Cache.Clear(); schirmImpl.Leeren(); }

            /// <summary>0.8.6: Profil einer Strecke im Freien einer Route (RaumwegFrei.AussenSchirm), Frequenzen wie der Ersatzweg.</summary>
            internal static Hindernis ProfilFrei(Vector3 a, Vector3 b, HashSet<int> eigen)
                => Profil(a, b, Physik.Frequenz.Value, Wege.F[1], Wege.F[2], eigen);

            /// <summary>Postfix-Teil von OnUpdate bei Fruehabbruch.</summary>
            internal static void Pruefen(ParallelSoundPropagationCalculator pc, AudioSourceContainer c, Vector3 hv)
            {
                try
                {
                    if (!Tabelle.TryGetValue(pc, out var z))
                    {
                        if (Aktiv == null || !Aktiv.Value || pc._propagationScore < 1f) return;
                        z = Tabelle.GetValue(pc, Neu);
                    }
                    if (Aktiv == null || !Aktiv.Value || pc._propagationScore < 1f) { Ende(z); return; }
                    var sys = pc._audioSystem;
                    var qr = c.CurrentAudioRoom; var hr = sys != null ? sys.ListenerCurrentRoom : null;
                    if (qr == null || hr == null) { Ende(z); return; }
                    Vector3 qv = c.CurrentPosition;
                    if ((hv - qv).sqrMagnitude > c.SqrMaxDistance) { Ende(z); return; }          // ausser Reichweite: Spiel
                    if (sys.TryGetMatchingRoomPair(qr, out var rpaar, out _) && rpaar.Routes.Length != 0) { Ende(z); return; }
                    bool qd = qr.IsOutdoor, hd = hr.IsOutdoor;
                    if (!qd && !hd) { dDicht++; Ende(z); return; }                               // beide drinnen: dicht

                    long key = Schluessel(qr.ID, hr.ID, qv, hv);
                    float t = Time.realtimeSinceStartup;
                    if (!Cache.TryGetValue(key, out var erg) || t - erg.Zeit > CacheS)
                    {
                        int b = Time.frameCount;
                        if (b != bild) { bild = b; jeBild = 0; }
                        // Gesamtreview A5/A6: aufschieben nur, wenn der Klang schon einen Ersatzwert fuer dieses Raumpaar hat (wie
                        // Raumweg.SpaetNach) - ein Einmal-Klang (MultiSource) ohne Wert wird sofort gerechnet, sonst bliebe er stumm.
                        // Die Strahlen zaehlen zum Zeitbudget "Room paths: max. ms per frame" (ZeitRechnen) wie jede Raumweg-Rechnung.
                        bool hatWert = z.Ersatz && z.Wertung >= 0f && z.RaumA == qr && z.RaumB == hr;
                        float budget = BudgetMs != null ? BudgetMs.Value : 0f;
                        if (hatWert && (jeBild >= MaxJeBild || (budget > 0f && ZeitRechnen.ImBild(b) >= budget)))
                        {
                            dBudget++; NeuAnfordern(pc);                                          // naechstes Bild
                            pc._propagationScore = z.Wertung;                                     // bis dahin gilt der letzte Wert
                            if (z.Mix.Laeuft) Schreiben(pc, z, Time.realtimeSinceStartup, false);
                            return;
                        }
                        jeBild++;
                        if (Cache.Count > 256) Cache.Clear();
                        long tr = Laufzeit.Jetzt;
                        erg = Rechnen(qv, hv, qr, hr, qd, hd, KlangLog.An);
                        ZeitRechnen.Dazu(tr, b);
                        erg.Zeit = t;
                        Cache[key] = erg;
                        dGerechnet++;
                    }
                    else dCache++;
                    if (erg.K.Count == 0) { dDicht++; Ende(z); return; }

                    z.A = qv; z.B = hv; z.Umgekehrt = false; z.RaumA = qr; z.RaumB = hr; z.Paar = 0xFFFFFFFEu; z.Hat = false;
                    z.Behaelter = c; z.Ersatz = true;
                    double jetzt = Time.realtimeSinceStartup;
                    bool weg = WegsucheMischen(z, jetzt);   // 0.8.6 Abschnitt 19 "Use": Wege der Wegsuche statt des Ersatzwegs
                    if (!weg)
                    {
                        z.Mix.Beginn();
                        foreach (var k in erg.K)
                        {
                            idx1[0] = k.Pi;
                            z.Mix.Kandidat(k.Id, k.D0, k.D1, k.D2, k.Aus, k.Erg, k.Pi >= 0 ? idx1 : null, k.Pi >= 0 ? 1 : 0);
                        }
                        z.Mix.Uebernehmen(jetzt, BlendS, RangNJetzt, BreiteJetzt);
                    }
                    z.Spiel = 1f; z.PaarGerechnet = z.Paar; z.Aufschub = 0; z.Nachholen = false;
                    Schreiben(pc, z, jetzt, true);
                    if (!weg) z.Kette = erg.Text;
                    dSummeDb += z.Wertung * Physik.Dynamik.Value; dWerte++;
                }
                catch (Exception e) { Statistik.Fehler(e); }
            }

            /// <summary>0.8.6: Ersatzweg fuer eine Quelle ohne Okklusion im Spiel (WandDaempfung): gleiche Rechnung und gleicher Cache wie
            /// Pruefen, Kandidaten in den Mischer von z. false = bleibt dicht (beide drinnen / keine Oeffnung nach draussen / aus).</summary>
            internal static bool Fuellen(Zustand z, Vector3 qv, Vector3 hv, ISpatialAudioRoom qr, ISpatialAudioRoom hr, double jetzt, out string text,
                                         HashSet<int> eigen = null)
            {
                text = null;
                if (Aktiv == null || !Aktiv.Value || qr == null || hr == null) return false;
                bool qd = qr.IsOutdoor, hd = hr.IsOutdoor;
                if (!qd && !hd) return false;
                long key = Schluessel(qr.ID, hr.ID, qv, hv);
                if (eigen != null) key = unchecked(key * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(eigen));   // eigenes Fahrzeug zaehlt nicht
                float t = Time.realtimeSinceStartup;
                if (!Cache.TryGetValue(key, out var erg) || t - erg.Zeit > CacheS || (KlangLog.An && erg.Text == null))
                {
                    if (Cache.Count > 256) Cache.Clear();
                    erg = Rechnen(qv, hv, qr, hr, qd, hd, KlangLog.An, eigen);
                    erg.Zeit = t;
                    Cache[key] = erg;
                    dGerechnet++;
                }
                else dCache++;
                text = erg.Text;
                if (erg.K.Count == 0) { dDicht++; return false; }
                z.Mix.Beginn();
                foreach (var k in erg.K)
                {
                    idx1[0] = k.Pi;
                    z.Mix.Kandidat(k.Id, k.D0, k.D1, k.D2, k.Aus, k.Erg, k.Pi >= 0 ? idx1 : null, k.Pi >= 0 ? 1 : 0);
                }
                z.Mix.Uebernehmen(jetzt, 0, RangNJetzt, BreiteJetzt);
                return true;
            }

            static void Ende(Zustand z)
            {
                if (z != null && z.Ersatz) z.Vergessen();
            }

            static long Schluessel(short a, short b, Vector3 q, Vector3 h)
            {
                unchecked
                {
                    long k = a * 65599L + b;
                    k = k * 31 + Mathf.FloorToInt(q.x / 2f); k = k * 31 + Mathf.FloorToInt(q.y / 2f); k = k * 31 + Mathf.FloorToInt(q.z / 2f);
                    k = k * 31 + Mathf.FloorToInt(h.x); k = k * 31 + Mathf.FloorToInt(h.y); k = k * 31 + Mathf.FloorToInt(h.z);
                    return k;
                }
            }

            static Ergebnis Rechnen(Vector3 qv, Vector3 hv, ISpatialAudioRoom qr, ISpatialAudioRoom hr, bool qd, bool hd, bool log, HashSet<int> eigen = null)
            {
                var erg = new Ergebnis();
                var inv = CultureInfo.InvariantCulture;
                float dyn = Physik.Dynamik.Value, alpha = Alpha.Value;
                double grenze = WegeKern.BandGrenzeDb;   // 10.11. (klangfarbe): Baender ungekappt, Deckel erst fuer die Lautstaerke (Raumweg.Schreiben)
                double f0 = Physik.Frequenz.Value, f1 = Wege.F[1], f2 = Wege.F[2];
                V3 q = V(qv), h = V(hv);
                StringBuilder sb = log ? new StringBuilder(240) : null;
                sb?.Append(" | room path: the game has no route for this room pair (would be silent) -> substitute over the obstacles:");

                if (qd && hd)
                {
                    var hi = Profil(qv, hv, f0, f1, f2, eigen);
                    HindernisKern.Summe(0, 0, 0, hi, grenze, out double d0, out double d1, out double d2);
                    var k = new Kand { Id = RoutenMischer.Id(0xFFFFFFFEu, geo1, 0), D0 = d0, D1 = d1, D2 = d2, H = hi, I0 = 0,
                                       Aus = hi.Frei ? q : hi.LetzteKante, Erg = new RaumwegErgebnis { Gueltig = true, Db = d0, DbMitte = d1, DbHoch = d2, Laenge = hi.Laenge, Direkt = hi.Direkt } };
                    erg.K.Add(k);
                    if (sb != null) { sb.Append(" outdoors -> outdoors"); HText(sb, hi, inv); sb.Append(" = ").Append(d0.ToString("F1", inv)).Append(" dB"); }
                    erg.Text = sb?.ToString();
                    return erg;
                }

                // Innenraum mit seinen Oeffnungen nach draussen
                if (DataProvider.TryGetData<SpatialAudioDataContainer>(out var data) && data != null) PortalTabelle(data);
                if (Netz.BildSetzen(Time.frameCount)) BildAuffrischen();
                var innenRaum = qd ? hr : qr;
                int innen = Netz.Von(innenRaum);
                if (innen < 0 || innen >= Netz.RaumN) { sb?.Append(" indoor room unknown -> stays silent"); erg.Text = sb?.ToString(); return erg; }
                wahl.Clear();
                int s0 = Netz.PStart[innen], e0 = s0 + Netz.PAnz[innen];
                for (int i = s0; i < e0; i++)
                {
                    int pi = Netz.PListe[i];
                    if (pi >= Netz.SGeoHat.Length || !Netz.SGeoHat[pi] || Netz.AussenN[pi].IstNull) continue;
                    int andere = Netz.Vorn[pi] == innen ? Netz.Hinten[pi] : Netz.Vorn[pi];
                    if (andere < 0 || !Netz.Draussen[andere]) continue;
                    V3 p = RaumwegKern.PortalPunkt(Netz.SGeo[pi], q, h);
                    wahl.Add((pi, V3.Abstand(q, p) + V3.Abstand(p, h)));
                }
                wahl.Sort((a, b) => a.kosten.CompareTo(b.kosten));
                int iq = Netz.Von(qr), ih = Netz.Von(hr);
                int n = Math.Min(MaxOeffnungen, wahl.Count);
                if (n == 0) sb?.Append(" the indoor room has no opening to the outside -> stays silent");
                for (int w = 0; w < n; w++)
                {
                    int pi = wahl[w].pi;
                    var g = Netz.SGeo[pi];
                    double zu = Netz.Obj(pi) ? Netz.ZuSpiel(pi) : 0.0;
                    // Gesamtreview A4 / 0.8.6: geschlossener Teil wie RaumwegNetz.ZustandLaden - Blatt nach dem Massengesetz je Band
                    // (Abschnitt 4, TuerKern.cs); ohne Blattmodell der Wert der Schliessung (TuerenFenster.ZuWert)
                    g.Offen = 1.0 - zu;
                    double b0 = Netz.Blatt(pi, 0);
                    if (!double.IsNaN(b0))
                    {
                        g.Tau = zu > 1e-3 ? Math.Pow(10, -Math.Min(grenze, b0) / 10) : 1.0;
                        g.TauMitte = zu > 1e-3 ? Math.Pow(10, -Math.Min(grenze, Netz.Blatt(pi, 1)) / 10) : 1.0;
                        g.TauHoch = zu > 1e-3 ? Math.Pow(10, -Math.Min(grenze, Netz.Blatt(pi, 2)) / 10) : 1.0;
                        g.Baender = true;
                    }
                    else
                    {
                        double zuWert = pi < portalJeIndex.Length && portalJeIndex[pi] != null ? TuerenFenster.ZuWert(portalJeIndex[pi]) : Netz.Voll[pi];
                        // 0.8.6 Tuerfuge-Fix: wie RaumwegNetz.ZustandLaden mit der Tuerfuge parallel (TuerKern.OhneBlattDb)
                        double tlDb = zu > 1e-3 ? TuerKern.OhneBlattDb(zuWert * Netz.WandP[pi] * dyn, dyn) : 0.0;
                        g.Tau = Math.Pow(10, -tlDb / 10); g.Baender = false;
                    }
                    V3 pkt = RaumwegKern.PortalPunkt(g, q, h);
                    rp1[0] = g; pk1[0] = pkt;
                    ra[0] = qd ? -1 : Netz.RaumFlaeche(iq, alpha, out rv[0]);
                    ra[1] = hd ? -1 : Netz.RaumFlaeche(ih, alpha, out rv[1]);
                    if (qd) rv[0] = 0; if (hd) rv[1] = 0;
                    var e = RaumwegKern.Route(q, h, rp1, pk1, 1, ra, f0, FHoch, grenze, bg, um, rv);
                    if (!e.Gueltig) continue;
                    // Baender wie RaumwegNetz.Rechne (geschlossene Tuer: Massengesetz je Band, ohne Blattmodell die Tiefpass-Abbildung)
                    RaumwegNetz.Baender(in e, dyn, Physik.TiefpassMin.Value, f0, f1, f2, out double i0, out double i1, out double i2);
                    // Weg im Freien: Punkt 0,3 m vor der Oeffnung auf der Aussenseite
                    V3 an = Netz.AussenN[pi];
                    V3 vor = pkt + an * 0.3;
                    Vector3 vv = new Vector3((float)vor.X, (float)vor.Y, (float)vor.Z);
                    var hi = qd ? Profil(qv, vv, f0, f1, f2, eigen) : Profil(vv, hv, f0, f1, f2, eigen);
                    HindernisKern.Summe(i0, i1, i2, hi, grenze, out double d0, out double d1, out double d2);
                    geo1[0] = Netz.GeoKey[pi];
                    var k = new Kand
                    {
                        Id = RoutenMischer.Id(0xFFFFFFFEu, geo1, 1), D0 = d0, D1 = d1, D2 = d2, Erg = e, Pi = pi, H = hi, I0 = i0,
                        // Richtung: Hoerer drinnen -> aus der Oeffnung; Hoerer draussen -> von der letzten Kante (sonst Oeffnung)
                        Aus = qd ? pkt : (hi.Frei || hi.Kanten == 0 ? pkt : hi.LetzteKante),
                    };
                    erg.K.Add(k);
                    if (sb != null)
                    {
                        var p = pi < portalJeIndex.Length ? portalJeIndex[pi] : null;
                        sb.Append(w == 0 ? " " : "; ").Append(p != null ? (string.IsNullOrEmpty(p.portalName) ? p.name : p.portalName) : "").Append('#').Append(pi)
                          .Append(" (open ").Append(g.Offen.ToString("F2", inv)).Append(", ").Append((g.Flaeche).ToString("F1", inv)).Append(" m2) route ")
                          .Append(i0.ToString("F1", inv)).Append(" dB, outdoors ").Append(qd ? "source -> opening" : "opening -> you");
                        HText(sb, hi, inv);
                        sb.Append(" = ").Append(d0.ToString("F1", inv)).Append('/').Append(d1.ToString("F1", inv)).Append('/').Append(d2.ToString("F1", inv)).Append(" dB");
                    }
                }
                erg.Text = sb?.ToString();
                return erg;
            }

            static void HText(StringBuilder sb, in Hindernis hi, CultureInfo inv)
            {
                if (hi.Frei && hi.Streif > 0)
                {
                    sb.Append(" clear line, grazing an edge (path difference -").Append(hi.Streif.ToString("F3", inv)).Append(" m) ")
                      .Append(hi.Db0.ToString("F1", inv)).Append('/').Append(hi.Db1.ToString("F1", inv)).Append('/').Append(hi.Db2.ToString("F1", inv)).Append(" dB");
                    KText(sb, hi, inv); return;
                }
                if (hi.Frei) { sb.Append(" clear line"); KText(sb, hi, inv); return; }
                sb.Append(" screening ").Append(hi.Db0.ToString("F1", inv)).Append('/').Append(hi.Db1.ToString("F1", inv)).Append('/').Append(hi.Db2.ToString("F1", inv))
                  .Append(" dB (").Append(hi.Kanten).Append(hi.Kanten == 1 ? " edge" : " edges").Append(", detour ").Append(hi.Umweg.ToString("F2", inv))
                  .Append(" m, top ").Append(hi.HoechsteKante.ToString("F1", inv)).Append(" m above the line, cap ").Append(hi.Kanten >= 2 ? "25" : "20").Append(" dB");
                if (hi.Haus > 0) sb.Append(", incl. housing ").Append(hi.Haus.ToString("F1", inv)).Append(" dB for ").Append(hi.Bebaut.ToString("F0", inv)).Append(" m built-up (ISO 9613-2 A.3)");
                sb.Append(')');
                KText(sb, hi, inv);
            }

            /// <summary>0.8.6: massgebliche Kante (Collider, Hoehe ueber der Linie, Abstand zu beiden Enden, Fresnelzahl N bei der
            /// Bezugsfrequenz) und die Hoehe der Endpunkte ueber dem Boden.</summary>
            static void KText(StringBuilder sb, in Hindernis hi, CultureInfo inv)
            {
                double lam = RaumwegKern.Schall / Math.Max(Physik.Frequenz.Value, 1.0);
                double D = hi.Direkt;
                sb.Append(" [edge ");
                if (hi.KanteName != null) sb.Append('\'').Append(hi.KanteName).Append("' ");
                sb.Append(hi.KanteUeber.ToString("F2", inv)).Append(" m above the line, ").Append(hi.KanteS.ToString("F1", inv)).Append(" m from the source side / ")
                  .Append(Math.Max(0, D - hi.KanteS).ToString("F1", inv)).Append(" m from your side, N ").Append((2 * hi.Umweg / lam).ToString("F2", inv));
                if (!double.IsNaN(hi.AUeberBoden)) sb.Append("; source ").Append(hi.AUeberBoden.ToString("F2", inv)).Append(" m above ground");
                if (!double.IsNaN(hi.BUeberBoden)) sb.Append(", you ").Append(hi.BUeberBoden.ToString("F2", inv)).Append(" m");
                if (hi.Ueberhang > 0) sb.Append("; ").Append(hi.Ueberhang).Append(" overhanging points ignored");
                if (!hi.Frei && hi.Kanten > 0)
                {
                    // 0.8.6: Kante, ueber die die Richtung zeigt (letzte Huellecke = erste Kante vom Hoerer aus)
                    double vonDir = Math.Max(0.01, D - hi.LetzteS);
                    sb.Append("; direction edge ");
                    if (hi.LetzteName != null) sb.Append('\'').Append(hi.LetzteName).Append("' ");
                    sb.Append(vonDir.ToString("F1", inv)).Append(" m from you, top y ").Append(hi.LetzteKante.Y.ToString("F1", inv));
                }
                sb.Append(']');
            }

            // 0.8.6 (Meldung Streets 09.11.2026 20:02 "Taxi hat einen harten Cut"): die Abschirmung sprang bei fast gleicher Position
            // zwischen 4,8 dB (Kante 0,1 m ueber der Linie) und 13-20 dB (Kante 6-12 m ueber der Linie, Umweg 1-2 m) - Messung zu Messung.
            // Ursachen im alten Profil:
            //  1. Rasterpunkte gleichmaessig zwischen Quelle und Hoerer (Abstand Laenge/(n+1), bei 200 m alle 4,1 m): mit jedem Schritt
            //     des Hoerers wanderten ALLE Punkte, ein schmales Objekt (Mast, Laterne, Schild, Baum) wurde mal getroffen, mal nicht.
            //  2. Strahl von oben nimmt die OBERSTE Flaeche - auch Objekte, die nur ueber der Linie haengen (Schild, Vordach, Bruecke,
            //     Laternenkopf): der Schall geht darunter durch, das Profil machte daraus eine Wand vom Boden bis dort.
            //  3. Duenne Hindernisse (Zaun, Mauer, Panzersperre) liegen zwischen zwei Rasterpunkten und wurden nur zufaellig getroffen.
            //  4. Schmale Hindernisse zaehlten als Schirm: ISO 9613-2 Abschn. 7.4 verlangt eine waagerechte Ausdehnung quer zur Linie
            //     groesser als die Wellenlaenge - um einen Mast beugt der Schall seitlich herum.
            //  5. Schattengrenze: freie Linie 0 dB, Kante knapp darueber 4,8 dB (HindernisKern.Dz).
            //  6. Das eigene Fahrzeug der Quelle schirmte sich selbst ab (die Wandmessung laesst es schon weg).
            // Jetzt: Rasterpunkte fest in der Welt (HindernisKern.Raster), dazu die Punkte, an denen die Sichtlinie ein Hindernis
            // schneidet (Ein- und Austritt, Oberkante genau dieses Colliders) und knapp unter ihr vorbeigeht (Strahlen entlang der
            // unteren Grenze, bis zu der eine Kante noch zaehlt) - die wandern stetig mit der Linie. Je Punkt zaehlt die
            // oberste Flaeche eines Colliders, der quer zur Linie mindestens eine Wellenlaenge (Bezugsband) breit ist und bis zur
            // Sichtlinie hinunter reicht; das eigene Fahrzeug zaehlt nicht. Die Schattengrenze ist stetig (HindernisKern.Streifen).
            static readonly RaycastHit[] treffer = new RaycastHit[16];
            static readonly double[] rs = new double[HindernisKern.MaxProfil];
            static readonly Collider[] pc = new Collider[HindernisKern.MaxProfil];   // 0.8.6: Collider je Profilpunkt (Klang-Log)
            static readonly bool[] pr = new bool[HindernisKern.MaxProfil], pBau = new bool[HindernisKern.MaxProfil];   // 0.8.6: Rasterpunkt / zaehlt zur Bebauung
            static readonly Collider[] kc = new Collider[8];   // Collider der Huellecken (Kanten-Gebaeude)
            static int ueberhang;
            static readonly V3[] fu = new V3[HindernisKern.FresnelN];
            const float Innen = 0.05f;

            /// <summary>Hoehenprofil zwischen a und b (siehe oben). eigen = Collider des eigenen Fahrzeugs (null = keins).</summary>
            static Hindernis Profil(Vector3 a, Vector3 b, double f0, double f1, double f2, HashSet<int> eigen = null)
            {
                int maske = ((int)LayersMaskController.HighPolyWithTerrainMask | Statistik.AudioMaske) & ~(int)LayersMaskController.PlayerMask;
                ueberhang = 0;
                Vector3 d = b - a; float len = d.magnitude;
                if (len < 0.5f) return new Hindernis { Gueltig = true, Frei = true, Direkt = len, Laenge = len };
                float dx = d.x, dz = d.z;
                float wa = Mathf.Sqrt(dx * dx + dz * dz);
                if (wa < 0.5f) return new Hindernis { Gueltig = true, Frei = true, Direkt = len, Laenge = len };
                float ux = dx / wa, uz = dz / wa, px = -uz, pz = ux;
                float lam = (float)(RaumwegKern.Schall / Math.Max(f0, 1.0));
                float oben = Mathf.Max(a.y, b.y) + ObenM, unten = Mathf.Min(a.y, b.y) - 2f;
                Vector3 dir = d / len;
                int n = 0;
                // 1) Schnittpunkte der Sichtlinie: Eintritt (a -> b) und Austritt (b -> a), je knapp im Hindernis
                for (int seite = 0; seite < 2; seite++)
                {
                    Vector3 o = seite == 0 ? a : b, r = seite == 0 ? dir : -dir;
                    dStrahlen++;
                    int m = Physics.RaycastNonAlloc(o, r, treffer, len, maske, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < m && n < HindernisKern.MaxProfil; i++)
                    {
                        var c = treffer[i].collider;
                        if (c == null || (eigen != null && eigen.Contains(c.GetInstanceID())) || Schmal(c.bounds, px, pz, lam)) continue;
                        Vector3 p = treffer[i].point + r * Innen;
                        float sv = (p.x - a.x) * ux + (p.z - a.z) * uz;
                        if (sv <= 0.01f || sv >= wa - 0.01f) continue;
                        dStrahlen++;
                        if (!c.Raycast(new Ray(new Vector3(p.x, oben, p.z), Vector3.down), out var hc, oben - unten)) continue;
                        ps[n] = sv; py[n] = hc.point.y; pc[n] = c; pr[n] = false; n++;
                    }
                }
                // 1b) knapp unter der Sichtlinie (so tief eine Kante noch zaehlt, HindernisKern.FresnelUnten): duenne Hindernisse, die die
                //     Linie gerade nicht schneiden - fuer die stetige Schattengrenze
                // Boden unter den Endpunkten (Bodenlinie fuer die Kante knapp unter der Sichtlinie)
                double ba = Oberkante(a + Vector3.up * Innen, 60f, float.PositiveInfinity, px, pz, lam, maske, eigen);
                double bb = Oberkante(b + Vector3.up * Innen, 60f, float.PositiveInfinity, px, pz, lam, maske, eigen);
                HindernisKern.FresnelUnten(V(a), V(b), f0, fu, ba, bb);
                for (int seg = 0; seg + 1 < HindernisKern.FresnelN; seg++)
                {
                    Vector3 p0 = new Vector3((float)fu[seg].X, (float)fu[seg].Y, (float)fu[seg].Z), p1 = new Vector3((float)fu[seg + 1].X, (float)fu[seg + 1].Y, (float)fu[seg + 1].Z);
                    Vector3 r = p1 - p0; float rl = r.magnitude;
                    if (rl < 0.05f) continue;
                    r /= rl;
                    dStrahlen++;
                    int m = Physics.RaycastNonAlloc(p0, r, treffer, rl, maske, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < m && n < HindernisKern.MaxProfil - ProfilN; i++)   // Platz fuer das Raster bleibt
                    {
                        var c = treffer[i].collider;
                        if (c == null || (eigen != null && eigen.Contains(c.GetInstanceID())) || Schmal(c.bounds, px, pz, lam)) continue;
                        Vector3 p = treffer[i].point + r * Innen;
                        float sv = (p.x - a.x) * ux + (p.z - a.z) * uz;
                        if (sv <= 0.01f || sv >= wa - 0.01f) continue;
                        dStrahlen++;
                        if (!c.Raycast(new Ray(new Vector3(p.x, oben, p.z), Vector3.down), out var hc, oben - unten)) continue;
                        ps[n] = sv; py[n] = hc.point.y; pc[n] = c; pr[n] = false; n++;
                    }
                }
                // 2) Rasterpunkte fest in der Welt
                int nr = HindernisKern.Raster(a.x, a.z, ux, uz, wa, ProfilN, rs, out double schritt);
                for (int i = 0; i < nr && n < HindernisKern.MaxProfil; i++)
                {
                    float sv = (float)rs[i];
                    float x = a.x + ux * sv, z = a.z + uz * sv, linie = a.y + d.y * sv / wa;
                    double top = Oberkante(new Vector3(x, oben, z), oben - unten, linie, px, pz, lam, maske, eigen, out var oc);
                    if (double.IsNaN(top)) continue;
                    ps[n] = sv; py[n] = top; pc[n] = oc; pr[n] = true; n++;
                }
                // nach s sortieren (HindernisKern.Rechnen erwartet aufsteigend; n <= 128, Einfuegen genuegt)
                for (int i = 1; i < n; i++)
                {
                    double ks = ps[i], ky = py[i]; var kco = pc[i]; bool kr = pr[i]; int j = i - 1;
                    while (j >= 0 && ps[j] > ks) { ps[j + 1] = ps[j]; py[j + 1] = py[j]; pc[j + 1] = pc[j]; pr[j + 1] = pr[j]; j--; }
                    ps[j + 1] = ks; py[j + 1] = ky; pc[j + 1] = kco; pr[j + 1] = kr;
                }
                var erg = HindernisKern.Rechnen(V(a), V(b), ps, py, n, f0, f1, f2, hs, hy, ba, bb);
                // 0.8.6 (Streets 10.11.2026, BTR hinter Haeuserreihen): Bebauung nach ISO 9613-2 Anhang A.3 (HindernisKern.Haus). Die Collider
                // der ersten und letzten Huellecke (hs/hy sind die Ecken aus Rechnen) zaehlen nicht noch einmal; Gelaende ist keine Bebauung.
                int nk = 0;
                for (int v = 1; v <= erg.Kanten; v++)
                    for (int i = 0; i < n; i++)
                        if (ps[i] == hs[v] && py[i] == hy[v])
                        {
                            // nur erste und letzte Kante (die Flanken des Schirms in Gl. 14: d_ss, d_sr); Gebaeude dazwischen sind Bebauung
                            if ((v == 1 || v == erg.Kanten) && pc[i] != null && nk < kc.Length) kc[nk++] = pc[i];
                            if (v == erg.Kanten) erg.LetzteName = pc[i] != null ? pc[i].name : null;
                            break;
                        }
                for (int i = 0; i < n; i++)
                {
                    var c = pc[i];
                    bool z = pr[i] && c != null && !(c is TerrainCollider);
                    for (int j = 0; z && j < nk; j++) if (ReferenceEquals(kc[j], c)) z = false;
                    pBau[i] = z;
                }
                double ds = schritt / Math.Max(1e-3, Math.Max(Math.Abs(ux), Math.Abs(uz)));
                HindernisKern.HausDazu(ref erg, HausAn == null || HausAn.Value ? HindernisKern.Bebaut(V(a), V(b), ps, py, pBau, n, ds) : 0);
                for (int j = 0; j < nk; j++) kc[j] = null;
                // 0.8.6 Klang-Log: massgebliche Kante = hoechster Profilpunkt ueber der Sichtlinie, Endpunkte ueber dem Boden
                double bestU = double.NegativeInfinity; int bi = -1;
                for (int i = 0; i < n; i++)
                {
                    double u = py[i] - (a.y + d.y * ps[i] / wa);
                    if (u > bestU) { bestU = u; bi = i; }
                }
                if (bi >= 0)
                {
                    erg.KanteUeber = bestU; erg.KanteS = ps[bi];
                    var kc = pc[bi];
                    erg.KanteName = kc != null ? kc.name : null;
                }
                erg.AUeberBoden = double.IsNaN(ba) ? double.NaN : a.y - ba;
                erg.BUeberBoden = double.IsNaN(bb) ? double.NaN : b.y - bb;
                erg.Ueberhang = ueberhang;
                for (int i = 0; i < n; i++) pc[i] = null;
                return erg;
            }

            /// <summary>Hoechste zaehlende Flaeche unter o (Strahl nach unten): nicht das eigene Fahrzeug, quer zur Linie mindestens lam
            /// breit, und - liegt sie ueber der Sichtlinie (linie) - ein Collider, der bis zur Linie hinunter reicht. NaN = nichts.</summary>
            static double Oberkante(Vector3 o, float weit, float linie, float px, float pz, float lam, int maske, HashSet<int> eigen)
                => Oberkante(o, weit, linie, px, pz, lam, maske, eigen, out _);

            static double Oberkante(Vector3 o, float weit, float linie, float px, float pz, float lam, int maske, HashSet<int> eigen, out Collider wer)
            {
                wer = null;
                dStrahlen++;
                int m = Physics.RaycastNonAlloc(new Ray(o, Vector3.down), treffer, weit, maske, QueryTriggerInteraction.Ignore);
                double best = double.NaN; float bestD = float.MaxValue;
                for (int i = 0; i < m; i++)
                {
                    var t = treffer[i];
                    if (t.distance >= bestD) continue;
                    var c = t.collider;
                    if (c == null || (eigen != null && eigen.Contains(c.GetInstanceID()))) continue;
                    var bnd = c.bounds;
                    if (Schmal(bnd, px, pz, lam)) continue;
                    if (t.point.y > linie && bnd.min.y > linie + Innen) continue;   // haengt ueber der Linie: Schall geht darunter durch
                    // 0.8.6 (Ground Zero 09.11.2026 MARK 8-14, V-Ex-Taxi 47-94 m bei freier Sicht: "top 8.2-9.4 m above the line" ->
                    // 15-17 dB, 1-2 m daneben 5 dB): die Huelle des Colliders reicht bis unter die Linie (Baum mit Stamm + Krone in einem
                    // Collider, Vordach/Bruecke/Werbetafel am Mast, Gebaeude mit Durchgang), an DIESER Stelle ist unter der Oberflaeche
                    // aber Luft. Strahl von der Sichtlinie nach oben gegen genau diesen Collider: trifft er eine Unterseite unterhalb der
                    // Oberkante, haengt das Stueck hier ueber der Linie (der Schall geht darunter durch). Steht die Linie im Material
                    // (massiver Block, Gelaende), trifft der Strahl von innen nichts bzw. dieselbe Oberkante -> zaehlt wie bisher.
                    if (t.point.y > linie + Innen)
                    {
                        dStrahlen++;
                        float hoch = t.point.y - linie;
                        if (c.Raycast(new Ray(new Vector3(o.x, linie, o.z), Vector3.up), out var hu, hoch) && hu.point.y < t.point.y - Innen) { ueberhang++; continue; }
                    }
                    best = t.point.y; bestD = t.distance; wer = c;
                }
                return best;
            }

            /// <summary>Quer zur Linie schmaler als eine Wellenlaenge (ISO 9613-2 Abschn. 7.4): kein Schirm, der Schall beugt seitlich herum.</summary>
            static bool Schmal(Bounds bnd, float px, float pz, float lam)
            {
                var e = bnd.extents;
                return 2f * (Mathf.Abs(e.x * px) + Mathf.Abs(e.z * pz)) < lam;
            }

            internal static void Loggen()
            {
                if (dGerechnet + dCache + dBudget == 0) return;
                Plugin.Log.LogInfo($"[Diagnostics] Room paths without a game route: {dGerechnet} substitutes calculated ({dStrahlen} rays), {dCache} from the cache, " +
                                   $"{dBudget} deferred (frame budget), {dDicht} stayed silent (both indoors / no opening to the outside), " +
                                   $"on average {(dWerte > 0 ? dSummeDb / dWerte : 0):F1} dB instead of {Physik.Dynamik.Value:F0} dB.");
                dGerechnet = dCache = dBudget = dDicht = dStrahlen = dWerte = 0; dSummeDb = 0;
            }
        }
    }
}
