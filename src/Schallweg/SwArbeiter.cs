using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Materialklang.Schallweg
{
    // ------------------------------------------------------------------------------------------
    //  0.8.6 Wegsuche (Etappe 3): Arbeitsfaden. Ohne Unity (Offline-Test faehrt ihn genauso).
    //   - Hauptfaden: Hoerer(), Anfrage(), TuerSetzen() schreiben in den Eingang (Sperre, nur Kopien von Zahlen);
    //     Holen() liest das letzte fertige Ergebnis je Quelle (unveraenderliches Objekt, Sperre nur fuer den Woerterbuchzugriff).
    //   - Faden: 10 Hz. Eingang uebernehmen (Puffer tauschen) -> Tuerzustaende anwenden -> Hoererbaum neu, wenn der Hoerer die
    //     1-m-Zelle verlassen hat oder eine Tuer umschaltet (der Baum enthaelt die Tuerstrafen) -> offene Anfragen nach Alter
    //     rechnen (je Quelle hoechstens BudgetMs, je Takt hoechstens TaktMs).
    //   - Waehrend der Baum neu entsteht, gelten die letzten Ergebnisse weiter (Halten). Ein alter Baum fuer einen neuen
    //     Hoererort ist kein Ersatz (Etappe 2: bis 60 dB daneben), darum baut der Faden ihn selbst und rechnet erst danach weiter.
    // ------------------------------------------------------------------------------------------
    internal sealed class Ergebnis
    {
        public long Id; public V Q, H; public double Zeit, AnfrageZeit;   // Zeit = fertig (Arbeiter.Jetzt), AnfrageZeit = die bediente Anfrage; Q/H = angefragt
        public bool Frei, Abgebrochen; public Weg Durch; public List<Weg> Wege = new List<Weg>();
        public double[] Summe = new double[3]; public double Ms; public int Strahlen, Tuerstand; public string Kachel;
        /// <summary>Ankunftsrichtung (Hoerer -> letzter Wegpunkt) des lautesten Wegs (1 kHz); bei freier Sicht die Quelle.</summary>
        public V Ankunft;
        // 0.8.6 Livetest-Fix
        public V QFrei, HFrei; public double QWeg, HWeg;            // gerechnete Punkte (aus Koerpern geschoben) und Verschiebung (m)
        public int Teile = 1; public bool Aufgegeben;               // Takte der Suche (> 1 = nach Budget fortgesetzt); Aufgegeben = ohne Weg nach MaxTeile
        public List<RaumKette> Raeume = new List<RaumKette>();      // Raumanteile (Diffusfeld) der Raumfolgen der Wege, je Folge einmal
        /// <summary>wegsuche-laut (M1): kein Weg, weil der echte Hoerer die Ketten des Baums (gebaut an einem anderen Punkt der Zelle) an
        /// keinem Knoten sieht - der Arbeiter baut dann einen Baum am Hoerer selbst.</summary>
        public bool BaumUnpassend;
        /// <summary>wegsuche-use (Nachlauf): Zwischenstand einer Suche, die in den naechsten Takten weiterlaeuft.</summary>
        public bool Zwischenstand;
    }

    /// <summary>Weg A: CPU-Zeit des aufrufenden Fadens (ms): Windows/Wine GetThreadTimes, Linux clock_gettime(CLOCK_THREAD_CPUTIME_ID). NaN = nicht verfuegbar.</summary>
    internal static class FadenCpu
    {
        [DllImport("kernel32")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32")] static extern bool GetThreadTimes(IntPtr h, out long c, out long e, out long k, out long u);
        [StructLayout(LayoutKind.Sequential)] struct Ts { public long s, ns; }
        [DllImport("libc")] static extern int clock_gettime(int clk, out Ts ts);
        static int art = -1;
        public static double Ms()
        {
            try
            {
                if (art < 0) art = Environment.OSVersion.Platform == PlatformID.Win32NT ? 1 : 2;
                if (art == 1) { if (GetThreadTimes(GetCurrentThread(), out _, out _, out long k, out long u)) return (k + u) / 1e4; return double.NaN; }
                if (art == 2) { if (clock_gettime(3, out var ts) == 0) return ts.s * 1e3 + ts.ns / 1e6; return double.NaN; }
            }
            catch { art = 0; }
            return double.NaN;
        }
    }

    internal sealed class Arbeiter : IDisposable
    {
        public readonly WegDaten Daten; public readonly Wegenetz Netz; public readonly Bvh Bvh; public readonly PortalTest Portale;
        public readonly Gemeinsam G;
        public RaumModell Raum => plaetze[0].R;      // null = Datei ohne Raeume (Format 1)
        public Suche Suche => plaetze[0].S;
        public string Kachel => string.IsNullOrEmpty(Daten.Kachel) ? Daten.Karte : Daten.Karte + "-" + Daten.Kachel;
        public double BudgetMs = 2.0, TaktMs = 30.0, TaktS = 0.1;
        public int WegeK = 5, MaxTeile = 12;   // MaxTeile: so viele Takte darf eine Suche ohne ersten Weg fortgesetzt werden
        public double AnfrageS = 2.0;         // Anfragen, die laenger nicht erneuert wurden, fallen weg
        public double QuelleM = 1.0, HoererM = 0.5;   // ab dieser Bewegung neu rechnen (0.8.6: Quelle 1 m - Schritte wandern 0,3-0,5 m je Klang)
        /// <summary>wegsuche-use (7a): so weit (m) darf sich eine Quelle vom Ort ihres Ergebnisses bzw. ihrer laufenden Suche entfernen, bis es
        /// nicht mehr gilt (Wegsuche.Fuer) bzw. die Suche neu beginnt. Befund Use-Raid 18:40: neu gerechnet wurde ab QuelleM = 1 m, das Ergebnis
        /// galt aber auch nur bis 1 m - jede Neuberechnung riss also eine Luecke (Mod-Wert, 12-16 dB anders), und eine Suche ueber mehrere Takte
        /// begann bei 1 m Bewegung von vorn: ein laufender Bot (1,5-5 m/s) bekam oft 10 s und mehr keinen Wert. Jetzt wie beim Hoerer (neu ab
        /// 0,5 m, gueltig bis 3 m): neu ab QuelleM, gueltig und fortgesetzt bis QuelleHaltM. Die Annahme dahinter ist dieselbe wie beim Hoerer:
        /// das Ergebnis eines Orts ist fuer die Dauer einer Neuberechnung (wenige Takte) der beste bekannte Wert in seiner Naehe.</summary>
        public double QuelleHaltM = 3.0;
        public bool RaumAn = true;            // 0.8.6: Raumanteil (Diffusfeld) entlang der Wege
        public bool RaumGraph = true;         // Raid-2-Fix: Raumanteil zusaetzlich ueber den Raumgraphen (unabhaengig vom gefundenen Weg)
        public bool Vorbau = true;            // Weg A: Baum der naechsten Zelle vorbauen
        public const int VorratMax = 12;
        public readonly int Faeden;

        readonly Stopwatch uhr = Stopwatch.StartNew();
        public double Jetzt => uhr.Elapsed.TotalSeconds;

        // ---- Eingang (Hauptfaden schreibt) ----
        sealed class Eingang
        {
            public bool HatHoerer, Start; public V Hoerer; public double Alpha = double.NaN;
            public readonly Dictionary<long, KeyValuePair<V, double>> Anfragen = new Dictionary<long, KeyValuePair<V, double>>();
            public readonly Dictionary<int, bool> Tueren = new Dictionary<int, bool>(), RaumTueren = new Dictionary<int, bool>();
            public readonly Dictionary<long, V> Probe = new Dictionary<long, V>();   // Raid-3-Fix: Probelauf der Use-Schluessel (nur zaehlen)
            public void Leeren() { HatHoerer = false; Start = false; Alpha = double.NaN; Anfragen.Clear(); Tueren.Clear(); RaumTueren.Clear(); Probe.Clear(); }
        }
        Eingang ein = new Eingang(), lokalEin = new Eingang();
        readonly object einSperre = new object();

        // ---- Ausgang ----
        readonly Dictionary<long, Ergebnis> ergebnisse = new Dictionary<long, Ergebnis>();
        readonly object ausSperre = new object();

        // ---- Plaetze (je Faden eine Suche) ----
        sealed class Auftrag { public long Id; public V Q; public double Ta; public Laufend L; public bool Bearbeitet, Fertig; }
        sealed class Platz
        {
            public int Nr; public Suche S; public RaumModell R;
            public readonly List<int> FolgeTmp = new List<int>(); public readonly HashSet<ulong> Gesehen = new HashSet<ulong>();
            public readonly List<Auftrag> Auftraege = new List<Auftrag>();
            public Thread T; public readonly AutoResetEvent Los = new AutoResetEvent(false); public readonly ManualResetEventSlim Fertig = new ManualResetEventSlim(true);
            public double Bis; public double Cpu; public int Verschoben, Ketten, Aufgegeben, Graph;
            public V HVon = new V(double.NaN, 0, 0), HFrei; public double HWeg;   // wegsuche-laut (M1): letzter Hoererort und sein freigestellter Punkt
        }
        readonly Platz[] plaetze; readonly Platz vorbauPlatz;
        sealed class Laufend { public Ergebnis E; public Suche.Stand S; public Suche.Baum B; public int Gen; public Ergebnis Zwischen; }   // Zwischen: wegsuche-use (Nachlauf)   // Gen: Baumfelder werden wiederverwendet

        // ---- Zustand des Koordinators ----
        readonly Dictionary<long, KeyValuePair<V, double>> offen = new Dictionary<long, KeyValuePair<V, double>>();
        readonly Dictionary<long, Laufend> laufend = new Dictionary<long, Laufend>();
        Suche.Baum baum; V hoerer, hoererVor; double hoererVorT = double.NaN; bool hatHoerer; volatile int tuerstand; int raumstand; long baumZelle = long.MinValue; int baumTuerstand = -1;
        Thread faden; volatile bool laufen;
        public volatile string Fehler;
        // Kanten: Tuerstrafe vorab (Wegenetz.CW); Daemmung je Tuer im Bezugsband beim aktuellen Fugen-Schalter
        double[] tuerDb0; bool kantenFuge, kantenTueren; double kantenFaktor = double.NaN;
        // Baumvorrat
        readonly Dictionary<long, Suche.Baum> vorrat = new Dictionary<long, Suche.Baum>(); readonly List<long> vorratFolge = new List<long>();
        readonly Stack<Suche.Baum> baumPool = new Stack<Suche.Baum>(); readonly object vorratSperre = new object();
        volatile bool vorbauBeschaeftigt, vorbauWunsch; V vorbauZiel; long vorbauZelle; int vorbauStand, vorbauArt, vorbauGen; Suche.Baum vorbauBaum;   // Art: 0 vorab, 1 weiterrechnen, 2 dringend (Hoerer schon in der Zelle), 3 Startbaum (Raid-3-Fix)
        V vGlatt; bool vHat;   // geglaettete Hoerergeschwindigkeit (Vorbau)
        int stetigTakte; double baumMsGlatt = 90;   // Raid-3-Fix: Takte mit stetiger Richtung in Folge, geglaettete Bauzeit eines Baums (ms)
        public double VorbauVorausMin = 1.0, VorbauBauten = 2, VorbauTakte = 1;   // Raid-3-Fix: Vorausweite = v (Bauten x Bauzeit + Takte x Takt), mindestens VorbauVorausMin
        public double VorbauStetigS = 0.1;          // Raid-3-Fix: so lange (s) muss die Richtung stetig sein, bevor vorgebaut wird (Nachspiel Raid 3: 0,5 s halbierte den Abfall, verdreifachte aber das Warten)

        // ---- Diagnose (Koordinator schreibt, Hauptfaden liest grob) ----
        public int DSuchen, DUeberBudget, DFrei, DBaeume, DTuerwechsel, DVerworfen, DTakte, DTakteVoll, DFortgesetzt, DAufgegeben, DVerschoben, DKetten;
        public int DVorratTreffer, DVorbau, DVorbauVerworfen, DKantenNeu;
        public int DBaumAmHoerer;
        public int DZwischen;   // wegsuche-use: veroeffentlichte Zwischenstaende (Nachlauf)   // wegsuche-laut (M1): Baeume, die am Hoerer neu gebaut wurden, weil der Zellbaum nicht passte
        bool baumAmHoerer; long amHoererZelle = long.MinValue;
        public int DAbgelaufenFertig, DAbgelaufenOhne, DWartenAufBaum, DVorbauBenutzt, DVorbauUngenutzt; public double DErweitertHinten;
        public int VorratAnzahl { get { lock (vorratSperre) return vorrat.Count; } }
        public double DSucheMs, DSucheMax, DBaumMs, DBaumMax, DTaktMax, DBaumSaatMs, DBaumDijkstraMs, DVorbauMs, DKantenMs;
        public double DCpuKoord, DCpuVorbau; public double[] DCpuHelfer;
        public int OffenAnzahl => offen.Count;
        public int LaufendAnzahl => laufend.Count;
        public double LetzterBaumMs => baum?.Ms ?? double.NaN;

        public Arbeiter(WegDaten d, int faeden = 2)
        {
            Daten = d; Netz = Wegenetz.Aus(d); Bvh = new Bvh(d); Portale = new PortalTest(d); G = new Gemeinsam(d);
            Faeden = Math.Max(1, Math.Min(4, faeden));
            plaetze = new Platz[Faeden];
            for (int i = 0; i < Faeden; i++) plaetze[i] = NeuerPlatz(i);
            vorbauPlatz = NeuerPlatz(-1);
            DCpuHelfer = new double[Faeden];
            var sw = Stopwatch.StartNew();
            Netz.KantenBauen(Portale);
            KantenAlle();
            DKantenMs = sw.Elapsed.TotalMilliseconds;
            nachStand = (a, b) => { int c = stand[a.Key].CompareTo(stand[b.Key]); return c != 0 ? c : b.Value.Value.CompareTo(a.Value.Value); };
        }
        Platz NeuerPlatz(int nr)
        {
            var p = new Platz { Nr = nr, S = new Suche(Daten, Netz, Bvh, Portale, G) { BudgetMs = BudgetMs }, R = Daten.HatRaeume ? new RaumModell(Daten) : null };
            p.R?.BvhSetzen(Bvh);
            p.S.Raum = p.R;   // Raid-3-Fix: Deckel der Mehrfachbeugung nur im Freien (Suche.Drinnen)
            return p;
        }
        public long Bytes => Netz.Bytes + Bvh.Bytes + (long)Daten.N * 40 + (long)Daten.N * 8 * Math.Min(VorratMax, 4);
        public bool[] Zu => G.Zu;
        /// <summary>Weg A: die Varint-Nachbarliste der Datei freigeben (nach dem Aufbau der flachen Kanten unnoetig). Nur, wenn kein zweiter
        /// Arbeiter dieselben WegDaten noch aufbauen will (im Spiel: genau einer).</summary>
        public void VarintFreigeben() { Daten.Daten = null; Daten.Offs = null; }
        /// <summary>Weg A: Laufzeit fuer den Aufbau der flachen Kanten samt Tuerverzeichnis (beim Laden).</summary>
        public double KantenAufbauMs => DKantenMs;

        // ================================================================ Kanten (Tuerstrafe vorab)
        void TuerDbNeu()
        {
            int np = Daten.PN; if (tuerDb0 == null || tuerDb0.Length != np) tuerDb0 = new double[np];
            for (int p = 0; p < np; p++) tuerDb0[p] = Suche.Daemmung(G.TuerGruppe[p], G.TuerDicke[p], Suche.F[0], G.FugeAn);
            kantenFuge = G.FugeAn; kantenTueren = G.TuerenAn; kantenFaktor = G.TuerStrafe;
        }
        /// <summary>Alle Portal-Kanten neu (Laden, Fugen-/Tuerschalter geaendert).</summary>
        void KantenAlle()
        {
            TuerDbNeu(); var s = plaetze[0].S;
            for (int i = 0; i < Netz.PkKante.Length; i++) Netz.KanteSetzen(i, s.StrafeAus(i, tuerDb0));
        }
        /// <summary>Nur die Kanten der Tuer p (Tuerverzeichnis).</summary>
        void KantenTuer(int p)
        {
            if (p < 0 || p + 1 >= Netz.TPkOff.Length) return;
            var s = plaetze[0].S;
            for (int j = Netz.TPkOff[p]; j < Netz.TPkOff[p + 1]; j++) { int i = Netz.TPk[j]; Netz.KanteSetzen(i, s.StrafeAus(i, tuerDb0)); DKantenNeu++; }
        }
        /// <summary>Schalter geaendert (Offline-Test)? Dann alle Kanten neu und die Baeume ungueltig.</summary>
        bool KantenPruefen()
        {
            if (kantenFuge == G.FugeAn && kantenTueren == G.TuerenAn && kantenFaktor == G.TuerStrafe) return false;
            KantenAlle(); tuerstand++; return true;
        }

        // ================================================================ Hauptfaden
        public void Hoerer(V h) { lock (einSperre) { ein.HatHoerer = true; ein.Hoerer = h; } }
        public void Anfrage(long id, V q)
        {
            double t = Jetzt;
            lock (einSperre)
            {
                // Raid-2-Fix (9): dieselbe Id im selben Takt an zwei Orten (> QuelleM) = Zuordnungsfalle (ein Schluessel fuer mehrere Quellen)
                if (ein.Anfragen.TryGetValue(id, out var alt) && V.Abstand(alt.Key, q) > QuelleM) DZweiOrte++;
                ein.Anfragen[id] = new KeyValuePair<V, double>(q, t);
            }
        }
        public int DZweiOrte;
        /// <summary>Raid-3-Fix: ersten Hoererbaum ohne Anfrage bauen erlaubt (Plugin: erst nach dem Abgleich der Tuerzustaende, sonst
        /// verwirft der erste Tuerwechsel ihn gleich wieder). Offline: immer.</summary>
        public volatile bool StartFrei = true;
        /// <summary>Raid-3-Fix: Tuerzustaende sind abgeglichen - ab dem Takt, der sie uebernimmt, darf der erste Baum entstehen.</summary>
        public void StartFreigeben() { lock (einSperre) ein.Start = true; }
        bool ersterBaumAngestossen; volatile bool aufgewaermt;
        public double DAufwaermMs, DStartBaumMs = double.NaN; public int DStartBaeume;   // Diagnose: Aufwaermsuche und erster Baum (Baumfaden)
        /// <summary>Raid-3-Fix: Probelauf der Use-Schluessel in CompareOnly - dieselbe Pruefung wie Anfrage (eine Id, zwei Orte > QuelleM im
        /// selben Takt), aber ohne Rechnung. So zeigt schon ein CompareOnly-Raid, ob die Behaelter-Schluessel (IdBehaelter) eindeutig sind.</summary>
        public void Probe(long id, V q)
        {
            lock (einSperre)
            {
                if (ein.Probe.TryGetValue(id, out var alt)) { if (V.Abstand(alt, q) > QuelleM) DProbeZweiOrte++; }
                else DProbeSchluessel++;
                ein.Probe[id] = q;
            }
        }
        public int DProbeZweiOrte, DProbeSchluessel;   // Schluessel je Takt gezaehlt (Summe ueber die Takte)
        public void TuerSetzen(int portal, bool zu) { lock (einSperre) ein.Tueren[portal] = zu; }
        /// <summary>0.8.6: Zustand eines Raumportals ohne Netzportal (grosse Tore/Oeffnungen, Index WegDaten.Q*).</summary>
        public void RaumTuerSetzen(int q, bool zu) { lock (einSperre) ein.RaumTueren[q] = zu; }
        /// <summary>0.8.6: Raumabsorption alpha (Raumweg-Einstellung der Mod).</summary>
        public void AlphaSetzen(double a) { lock (einSperre) ein.Alpha = a; }
        public Ergebnis Holen(long id) { lock (ausSperre) return ergebnisse.TryGetValue(id, out var e) ? e : null; }
        public void Ergebnisse(List<Ergebnis> aus) { aus.Clear(); lock (ausSperre) aus.AddRange(ergebnisse.Values); }

        public void Starten()
        {
            if (faden != null) return;
            laufen = true;
            for (int i = 1; i < Faeden; i++)
            {
                var p = plaetze[i];
                p.T = new Thread(() => Helfer(p)) { IsBackground = true, Name = "TrueAcoustics-PathSearch-" + i, Priority = ThreadPriority.Lowest };
                p.T.Start();
            }
            vorbauPlatz.T = new Thread(VorbauLauf) { IsBackground = true, Name = "TrueAcoustics-PathTree", Priority = ThreadPriority.Lowest };
            vorbauPlatz.T.Start();
            faden = new Thread(Lauf) { IsBackground = true, Name = "TrueAcoustics-PathSearch", Priority = ThreadPriority.Lowest };
            faden.Start();
        }
        public void Dispose()
        {
            laufen = false;
            var f = faden; faden = null;
            if (f != null && f.IsAlive) f.Join(2000);
            foreach (var p in plaetze) if (p.T != null) { p.Los.Set(); p.T.Join(1000); }
            if (vorbauPlatz.T != null) { vorbauPlatz.Los.Set(); vorbauPlatz.T.Join(2000); }
        }

        void Lauf()
        {
            while (laufen)
            {
                double t0 = Jetzt, c0 = FadenCpu.Ms();
                try { Schritt(); }
                catch (Exception e) { Fehler = e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace; laufen = false; break; }
                double c1 = FadenCpu.Ms(); if (!double.IsNaN(c1 - c0)) DCpuKoord += c1 - c0;
                double rest = TaktS - (Jetzt - t0);
                if (rest > 0) Thread.Sleep((int)Math.Ceiling(rest * 1000));
            }
        }

        void Helfer(Platz p)
        {
            while (true)
            {
                p.Los.WaitOne();
                if (!laufen) { p.Fertig.Set(); return; }
                double c0 = FadenCpu.Ms();
                try { Arbeiten(p); }
                // wegsuche-laut (N5): ein Fehler im Helfer haelt alles an (vorher rechnete der Koordinator mit altem Hoerer weiter, "Use" fiel still
                // auf den Mod-Wert zurueck). Der Hauptfaden sieht Fehler, meldet ihn und beendet den Arbeiter (Wegsuche.Tick -> Beenden).
                catch (Exception e) { Fehler = e.GetType().Name + " (helper " + p.Nr + "): " + e.Message + "\n" + e.StackTrace; laufen = false; }
                double c1 = FadenCpu.Ms(); if (!double.IsNaN(c1 - c0)) p.Cpu += c1 - c0;
                p.Fertig.Set();
            }
        }

        void VorbauLauf()
        {
            var p = vorbauPlatz;
            while (true)
            {
                p.Los.WaitOne();
                if (!laufen) return;
                double c0 = FadenCpu.Ms();
                try
                {
                    if (vorbauArt == 1) ErweiternImHintergrund(p);
                    else
                    {
                        V h = vorbauZiel; long z = vorbauZelle; int st = vorbauStand; bool dringend = vorbauArt >= 2, start = vorbauArt == 3;
                        var sw = Stopwatch.StartNew();
                        var b = BaumNeu(p.S, h);
                        b.Tuerstand = st; b.Zelle = z; b.Vorgebaut = !dringend; b.Benutzt = false;
                        baumMsGlatt = baumMsGlatt * 0.7 + b.Ms * 0.3;
                        if (dringend && !aufgewaermt) { DStartBaumMs = b.Ms; Aufwaermen(p, b); aufgewaermt = true; }
                        lock (vorratSperre)
                        {
                            if (st == tuerstand && !vorrat.ContainsKey(z))
                            {
                                VorratRein(z, b);
                                if (start) DStartBaeume++;   // Raid-3-Fix: Startbaum (ohne Anfrage, niemand wartet) nicht als "built while waiting"
                                else if (dringend) { DBaeume++; DBaumMs += b.Ms; DBaumSaatMs += b.MsStrahlen; DBaumDijkstraMs += b.MsDijkstra; if (b.Ms > DBaumMax) DBaumMax = b.Ms; }
                                else { DVorbau++; DVorbauMs += sw.Elapsed.TotalMilliseconds; }
                            }
                            else { baumPool.Push(b); DVorbauVerworfen++; }
                        }
                    }
                }
                catch (Exception e) { Fehler = e.GetType().Name + " (tree thread): " + e.Message + "\n" + e.StackTrace; laufen = false; }   // N5 wie Helfer
                double c1 = FadenCpu.Ms(); if (!double.IsNaN(c1 - c0)) DCpuVorbau += c1 - c0;
                vorbauBeschaeftigt = false;
            }
        }

        // Raid-2-Fix (5): der Vorbau-Faden rechnet den aktuellen Baum in Scheiben (je ErweiternScheibeMs unter der Baumsperre) bis
        // BaumMax weiter, wenn er nichts vorzubauen hat. Vorher rechnete der Suchfaden ihn waehrend einer Quelle ohne Zeitgrenze weiter
        // (31 s "extended" im Raid, Quellen bis 136 ms). Bricht ab, sobald ein Bau ansteht oder der Baum ein anderer geworden ist.
        // Raid-3-Fix (Starttakt 122 ms in Raid 3): Ursache war nicht der Baum (der entsteht auf dem Baumfaden), sondern die erste Quelle
        // (107,5 ms): Mono uebersetzt jede Methode beim ersten Aufruf (JIT) - Suche, Durchgang, Raumgraph, UTD -, und die Daten des Netzes
        // werden zum ersten Mal in den Speicher geholt. Darum rechnet der Baumfaden nach dem ersten Baum (im Ladebildschirm am Spawnpunkt)
        // eine Probequelle auf seinem eigenen Platz und verwirft sie. Danach laufen Koordinator und Helfer mit fertig uebersetztem Code.
        void Aufwaermen(Platz p, Suche.Baum b)
        {
            var sw = Stopwatch.StartNew(); int kAlt = p.Ketten, gAlt = p.Graph, vAlt = p.Verschoben;
            try
            {
                var h = b.H;
                foreach (var d in new[] { new V(12, 0, 9), new V(-9, 0.5, 14), new V(25, 0, -20) })
                {
                    var e = new Ergebnis { Id = -1, Q = h + d, H = h, HFrei = h, Tuerstand = tuerstand };
                    e.QFrei = p.S.Freistellen(e.Q, out e.QWeg);
                    e.Frei = p.S.Frei(e.QFrei, e.HFrei); e.Durch = p.S.Durchgang(e.QFrei, e.HFrei);
                    if (!e.Frei)
                    {
                        var st = p.S.Beginnen(e.QFrei, e.HFrei, WegeK, e.Durch, b);
                        int n = 0; while (!p.S.Weiter(st, 20) && ++n < MaxTeile) { }
                        if (!st.Fertig) p.S.Aufgeben(st);
                        e.Wege = st.Wege;
                    }
                    Abschliessen(p, e);
                }
            }
            finally { p.Ketten = kAlt; p.Graph = gAlt; p.Verschoben = vAlt; DAufwaermMs = sw.Elapsed.TotalMilliseconds; }
        }

        public double ErweiternScheibeMs = 4;
        void ErweiternImHintergrund(Platz p)
        {
            var b = vorbauBaum; int gen = vorbauGen; if (b == null) return;
            var sw = Stopwatch.StartNew();
            while (laufen && !vorbauWunsch && !b.Voll)
            {
                lock (b.Sperre)
                {
                    if (b.Gen != gen) break;
                    p.S.Erweitern(b, G.BaumMax, Stopwatch.GetTimestamp() + (long)(ErweiternScheibeMs * Stopwatch.Frequency / 1000.0));
                }
                Thread.Sleep(0);
            }
            DErweitertHinten += sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>Raid-2-Fix (4): Platz (Suchfaden) einer Quelle. Die Ids sind im Spiel gerade (InstanceID, (2 &lt;&lt; 32) | id): id % 2
        /// schickte im Raid alle 9473 Quellen auf Platz 0, der zweite Faden rechnete nie. Gemischt (Fibonacci-Hash), stabil je Id (eine
        /// fortgesetzte Suche haengt am Platz ihrer Suche-Instanz).</summary>
        public static int PlatzVon(long id, int faeden) => faeden <= 1 ? 0 : (int)((((ulong)id * 0x9E3779B97F4A7C15ul) >> 33) % (ulong)faeden);

        /// <summary>Baum fuer h (Hoererpunkt aus Koerpern geschoben) mit Puffern aus dem Vorrat.</summary>
        Suche.Baum BaumNeu(Suche s, V h)
        {
            var hf = s.Freistellen(h, out double hw);
            Suche.Baum puffer = null; lock (vorratSperre) if (baumPool.Count > 0) puffer = baumPool.Pop();
            var b = s.HoererBaumBauen(hf, puffer); b.HWeg = hw;
            return b;
        }

        void VorratRein(long z, Suche.Baum b)
        {
            vorrat[z] = b; vorratFolge.Remove(z); vorratFolge.Add(z);
            while (vorratFolge.Count > VorratMax)
            {
                long alt = vorratFolge[0]; vorratFolge.RemoveAt(0);
                if (vorrat.TryGetValue(alt, out var ab) && ab.Vorgebaut && !ab.Benutzt) DVorbauUngenutzt++;
                if (ab != null && ab != baum && Volatile.Read(ref ab.Leser) == 0) baumPool.Push(ab);   // wegsuche-use: von einer laufenden Suche gelesen -> nicht wiederverwenden
                vorrat.Remove(alt);
            }
        }

        readonly List<int> tuerListe = new List<int>();
        public int DBaumBehalten;   // Diagnose: Baeume, die einen Tuerwechsel ueberstanden (Tuer nicht erreicht)

        /// <summary>wegsuche-use (M3): nach einem Tuerwechsel nur die Baeume verwerfen, die eine Kante dieser Tueren erreicht haben (D eines
        /// Endknotens gesetzt). Die Tuer aendert nur die Laenge ihrer Kanten; ein Baum, der keinen Endknoten erreicht hat, enthaelt keinen Weg
        /// ueber sie und keinen Heap-Eintrag dorthin - seine Abstaende bleiben genau richtig, und rechnet er spaeter weiter, nimmt er die
        /// neue Laenge. Vorher verwarf jeder Tuerwechsel irgendwo alle Baeume (80-140 ms Neubau je Baum unter Mono).</summary>
        void BaeumeNachTuer(List<int> tueren)
        {
            lock (vorratSperre)
            {
                wegListe.Clear();
                foreach (var kv in vorrat) { if (Erreicht(kv.Value, tueren)) wegListe.Add(kv.Key); else { kv.Value.Tuerstand = tuerstand; DBaumBehalten++; } }
                foreach (long z in wegListe)
                {
                    var b = vorrat[z]; vorrat.Remove(z); vorratFolge.Remove(z);
                    if (b != baum && Volatile.Read(ref b.Leser) == 0) baumPool.Push(b);
                }
                wegListe.Clear();
            }
            if (baum != null && baumTuerstand == tuerstand - 1 && !Erreicht(baum, tueren)) { baumTuerstand = tuerstand; baum.Tuerstand = tuerstand; }
        }
        bool Erreicht(Suche.Baum b, List<int> tueren)
        {
            var n = Netz;
            lock (b.Sperre)
                foreach (int p in tueren)
                {
                    if (p < 0 || p + 1 >= n.TPkOff.Length) continue;
                    for (int j = n.TPkOff[p]; j < n.TPkOff[p + 1]; j++)
                    {
                        int i = n.TPk[j], u = n.PkVon[i], v = n.CNb[n.PkKante[i]] & int.MaxValue;
                        if (b.D[u] < float.MaxValue || b.D[v] < float.MaxValue) return true;
                    }
                }
            return false;
        }

        void VorratLeeren()
        {
            lock (vorratSperre) { foreach (var kv in vorrat) if (kv.Value != baum && Volatile.Read(ref kv.Value.Leser) == 0) baumPool.Push(kv.Value); vorrat.Clear(); vorratFolge.Clear(); }
        }

        // ================================================================ ein Takt (Faden; im Offline-Test direkt)
        readonly List<KeyValuePair<long, KeyValuePair<V, double>>> liste = new List<KeyValuePair<long, KeyValuePair<V, double>>>();
        readonly Dictionary<long, double> stand = new Dictionary<long, double>();
        readonly Comparison<KeyValuePair<long, KeyValuePair<V, double>>> nachStand;
        readonly List<long> wegListe = new List<long>();
        readonly List<Auftrag> alleAuftraege = new List<Auftrag>(); readonly Stack<Auftrag> auftragPool = new Stack<Auftrag>();

        static long Zelle(V h) => Wegenetz.Schluessel((int)Math.Floor(h.X), (int)Math.Floor(h.Y), (int)Math.Floor(h.Z));
        readonly List<Suche.Baum> leserListe = new List<Suche.Baum>();

        /// <summary>wegsuche-use (7a): darf die laufende Suche l weiterrechnen? Ihr Baum muss derselbe Bau sein (Gen: die Felder werden nach dem
        /// Verdraengen aus dem Vorrat wiederverwendet) und noch im Vorrat stehen oder der aktuelle sein; solange sie rechnet, haelt Baum.Leser
        /// ihn aus dem Pool (VorratRein/VorratLeeren).</summary>
        /// umwegsuche (13.11., Durchgang Welle 1 Befund 6): Pruefung UND Leser-Zaehlung in derselben Sperre. Vorher zaehlte der Koordinator den
        /// Leser erst nach der Sperre - dazwischen konnte der Baumfaden (VorbauLauf -> VorratRein) den Baum mit Leser == 0 verdraengen und in
        /// den Pool legen, der naechste BaumNeu baute ihn um, waehrend ein Helfer die fortgesetzte Suche darauf rechnete (falscher Weg oder
        /// Ausnahme -> Wegsuche fuer den Raid aus). Jetzt auch fuer den aktuellen Baum gezaehlt (er ist durch "!= baum" geschuetzt, das Feld
        /// liest der Baumfaden aber ohne Sperre des Koordinators). Gezaehlte Baeume stehen in leserListe, Freigabe nach dem Takt.
        bool Fortsetzbar(Laufend l, V q)
        {
            if (l.E.Tuerstand != tuerstand || V.Abstand(l.E.Q, q) >= QuelleHaltM || l.B == null) return false;
            lock (vorratSperre)
            {
                if (l.B.Gen != l.Gen) return false;
                bool ok = l.B == baum || (vorrat.TryGetValue(l.B.Zelle, out var vb) && vb == l.B);
                if (ok) { Interlocked.Increment(ref l.B.Leser); leserListe.Add(l.B); }
                return ok;
            }
        }

        public void Schritt()
        {
            double t0 = Jetzt;
            DTakte++;
            // Eingang uebernehmen (Puffer tauschen)
            lock (einSperre) { var x = ein; ein = lokalEin; lokalEin = x; }
            if (lokalEin.HatHoerer) { hoerer = lokalEin.Hoerer; hatHoerer = true; }
            if (lokalEin.Start) StartFrei = true;
            if (!double.IsNaN(lokalEin.Alpha)) foreach (var p in AllePlaetze()) if (p.R != null && p.R.Alpha != lokalEin.Alpha) { p.R.Alpha = lokalEin.Alpha; raumstand++; }
            foreach (var kv in lokalEin.Anfragen) offen[kv.Key] = kv.Value;
            bool tuerNeu = false; tuerListe.Clear();
            foreach (var kv in lokalEin.Tueren)
                if (kv.Key >= 0 && kv.Key < G.Zu.Length && G.Zu[kv.Key] != kv.Value) { G.Zu[kv.Key] = kv.Value; KantenTuer(kv.Key); tuerNeu = true; tuerListe.Add(kv.Key); }
            foreach (var kv in lokalEin.RaumTueren)
                foreach (var p in AllePlaetze())
                    if (p.R != null && kv.Key >= 0 && kv.Key < p.R.QN && p.R.QZu[kv.Key] != kv.Value) { if (p.R.QZu[kv.Key] && !kv.Value) p.R.QGeoeffnet[kv.Key] = true; p.R.QZu[kv.Key] = kv.Value; raumstand++; }
            lokalEin.Leeren();
            bool alleKanten = KantenPruefen();
            if (alleKanten || tuerNeu) { tuerstand++; raumstand++; DTuerwechsel++; if (alleKanten) VorratLeeren(); else BaeumeNachTuer(tuerListe); }
            if (!hatHoerer) return;

            // Hoererbaum (1-m-Zelle, Tuerstand): aus dem Vorrat oder neu
            long zelle = Zelle(hoerer);
            // wegsuche-laut (M1): der Baum dieser Zelle (Vorbau: Zellmitte) passt nicht zum Hoererort - aus ihm kam fuer eine Quelle kein Weg,
            // weil der Hoerer keinen Knoten ihrer Ketten sieht (Ecke, Tunnel). Dann einmal je Zellbesuch dringend einen Baum am Hoerer bauen.
            if (amHoererZelle != long.MinValue && zelle != amHoererZelle) amHoererZelle = long.MinValue;   // Zelle verlassen
            if (baumAmHoerer && baum != null && zelle == baumZelle && amHoererZelle != zelle && V.Abstand(baum.H, plaetze[0].S.Freistellen(hoerer, out _)) > 1e-3)
            {
                amHoererZelle = zelle; DBaumAmHoerer++;
                lock (vorratSperre) { if (vorrat.TryGetValue(zelle, out var alt0) && alt0 == baum) { vorrat.Remove(zelle); vorratFolge.Remove(zelle); } }
                baumZelle = long.MinValue;   // neu waehlen: der Vorrat hat die Zelle nicht mehr -> dringender Bau am Hoerer, bis dahin Halten
            }
            baumAmHoerer = false;
            if (baum == null || zelle != baumZelle || baumTuerstand != tuerstand)
            {
                // nur bauen, wenn es etwas zu rechnen gibt - ausser dem ersten Baum (Raid-3-Fix: im Ladebildschirm, sobald Hoerer und
                // Tuerzustaende da sind; StartFrei setzt das Plugin nach dem Tuerabgleich)
                bool ersterBaum = baum == null && StartFrei && faden != null && vorbauPlatz.T != null && !ersterBaumAngestossen;
                if (offen.Count == 0 && !ersterBaum) return;
                Suche.Baum b = null;
                lock (vorratSperre) if (vorrat.TryGetValue(zelle, out b) && b.Tuerstand == tuerstand) { vorratFolge.Remove(zelle); vorratFolge.Add(zelle); } else b = null;
                if (b == null)
                {
                    if (faden != null && vorbauPlatz.T != null)
                    {
                        // Raid-2-Fix (5): nicht im Takt bauen (121 ms je Baum unter Mono, Takte bis 172 ms) - der Baum-Faden baut genau diese
                        // Zelle, bis dahin gelten die letzten Ergebnisse weiter (Halten; ein alter Baum ist fuer den neuen Ort kein Ersatz).
                        if (!(vorbauBeschaeftigt && vorbauZelle == zelle && vorbauArt != 1)) { vorbauWunsch = true; if (!vorbauBeschaeftigt) { VorbauStarten(ersterBaum && offen.Count == 0 ? 3 : 2, hoerer, zelle); if (ersterBaum) ersterBaumAngestossen = true; } }   // dringend: am Hoererort selbst (wie bisher der Koordinator)
                        if (offen.Count > 0) DWartenAufBaum++;
                        Abschluss(t0); return;
                    }
                    b = BaumNeu(plaetze[0].S, hoerer); b.Tuerstand = tuerstand; b.Zelle = zelle;
                    lock (vorratSperre) VorratRein(zelle, b);
                    DBaeume++; DBaumMs += b.Ms; DBaumSaatMs += b.MsStrahlen; DBaumDijkstraMs += b.MsDijkstra; if (b.Ms > DBaumMax) DBaumMax = b.Ms;
                }
                else { DVorratTreffer++; if (b.Vorgebaut && !b.Benutzt) DVorbauBenutzt++; }
                b.Benutzt = true;
                baum = b; baumZelle = zelle; baumTuerstand = tuerstand;
            }

            // Anfragen reihum: am laengsten nicht gerechnete zuerst (ohne Ergebnis = sofort), damit Dauerquellen (Schritte) neue nicht verdraengen
            liste.Clear(); foreach (var kv in offen) liste.Add(kv);
            stand.Clear();
            lock (ausSperre) foreach (var kv in liste) stand[kv.Key] = ergebnisse.TryGetValue(kv.Key, out var r) ? r.Zeit : -1;
            liste.Sort(nachStand);
            double jetzt = Jetzt;
            foreach (var p in plaetze) p.Auftraege.Clear();
            alleAuftraege.Clear();
            foreach (var kv in liste)
            {
                long id = kv.Key; V q = kv.Value.Key; double ta = kv.Value.Value;
                if (jetzt - ta > AnfrageS)
                {
                    // Raid-2-Fix (8): "requests expired" aufgeteilt - fertig (Quelle 2 s nicht mehr angefragt, Ergebnis war da) oder ohne Ergebnis
                    Ergebnis fe; lock (ausSperre) ergebnisse.TryGetValue(id, out fe);
                    if (fe != null && fe.AnfrageZeit >= ta - 1e-9) DAbgelaufenFertig++; else DAbgelaufenOhne++;
                    offen.Remove(id); laufend.Remove(id); DVerworfen++; continue;
                }
                Ergebnis alt; lock (ausSperre) ergebnisse.TryGetValue(id, out alt);
                bool imNachlauf = alt != null && alt.Zwischenstand && laufend.ContainsKey(id);   // wegsuche-use: Zwischenstand gilt, Suche laeuft weiter
                if (!imNachlauf && alt != null && alt.AnfrageZeit >= ta - 1e-9 && alt.Tuerstand == tuerstand && V.Abstand(alt.H, hoerer) < HoererM && V.Abstand(alt.Q, q) < QuelleM) continue;   // aktuell
                if (!imNachlauf && alt != null && alt.Tuerstand == tuerstand && V.Abstand(alt.H, hoerer) < HoererM && V.Abstand(alt.Q, q) < QuelleM && jetzt - alt.Zeit < 1.0)
                { var neu = Kopie(alt, ta); lock (ausSperre) ergebnisse[id] = neu; continue; }   // gleicher Ort: Ergebnis gilt, nur die Anfrage bestaetigen
                // 0.8.6: unterbrochene Suche (Budget vor dem ersten Weg) fortsetzen, solange Baum, Tuerstand und Quellort passen
                // wegsuche-use (7a): eine angefangene Suche wird zu Ende gerechnet, solange ihr Baum lebt (Felder nicht wiederverwendet), der
                // Tuerstand gilt und die Quelle hoechstens QuelleHaltM weiter ist - vorher begann sie bei 1 m Quellbewegung oder jedem neuen
                // Hoererbaum (gehender Hoerer: alle 0,3-0,7 s) von vorn und wurde fuer bewegte Quellen nie fertig. Ihr Ergebnis gilt fuer ihren
                // Quell- und Hoererort (E.Q/E.H); ist der Ort inzwischen weiter als QuelleM/HoererM, rechnet der naechste Takt neu.
                if (laufend.TryGetValue(id, out var l) && !Fortsetzbar(l, q)) { laufend.Remove(id); l = null; }   // zaehlt den Leser selbst (umwegsuche)
                var a = auftragPool.Count > 0 ? auftragPool.Pop() : new Auftrag();
                a.Id = id; a.Q = q; a.Ta = ta; a.L = l; a.Bearbeitet = a.Fertig = false;
                plaetze[PlatzVon(id, Faeden)].Auftraege.Add(a); alleAuftraege.Add(a);
            }
            // verteilt rechnen: Helfer starten, selbst Platz 0, dann auf alle warten
            double bis = t0 + TaktMs / 1000.0;
            for (int i = 1; i < Faeden; i++) { var p = plaetze[i]; p.Bis = bis; if (p.Auftraege.Count == 0) continue; p.Fertig.Reset(); p.Los.Set(); }
            plaetze[0].Bis = bis; Arbeiten(plaetze[0]);
            for (int i = 1; i < Faeden; i++) plaetze[i].Fertig.Wait();
            foreach (var lb in leserListe) Interlocked.Decrement(ref lb.Leser); leserListe.Clear();
            bool voll = false;
            foreach (var a in alleAuftraege)
            {
                if (!a.Bearbeitet) { voll = true; auftragPool.Push(a); continue; }
                if (a.Fertig) { Zaehlen(a.L.E); if (a.L.E.BaumUnpassend) baumAmHoerer = true; }
                if (!a.Fertig)
                {
                    laufend[a.Id] = a.L; if (a.L.E.Teile > 0) DFortgesetzt++;
                    if (a.L.Zwischen != null) { lock (ausSperre) ergebnisse[a.Id] = a.L.Zwischen; a.L.Zwischen = null; DZwischen++; }
                }
                else { laufend.Remove(a.Id); lock (ausSperre) ergebnisse[a.Id] = a.L.E; }
                a.L = null; auftragPool.Push(a);
            }
            if (voll) DTakteVoll++;
            // alte Ergebnisse aufraeumen
            if (DTakte % 50 == 0)
            {
                lock (ausSperre)
                {
                    wegListe.Clear();
                    foreach (var kv in ergebnisse) if (jetzt - kv.Value.Zeit > 30) wegListe.Add(kv.Key);
                    foreach (long k in wegListe) ergebnisse.Remove(k);
                }
                wegListe.Clear(); foreach (var kv in laufend) if (!offen.ContainsKey(kv.Key)) wegListe.Add(kv.Key);
                foreach (long k in wegListe) laufend.Remove(k);
            }
            Abschluss(t0);
        }

        /// <summary>Ende eines Takts: Vorbau anstossen (oder den aktuellen Baum weiterrechnen lassen), Taktzeit messen.</summary>
        void Abschluss(double t0)
        {
            // Raid-2-Fix (7): Vorbau gezielter. Bisher: Zelle, in der der Hoerer in 0,7 s waere - bei 1-m-Zellen oft die Nachbarzelle
            // (1173 Vorbauten, 615 Treffer). Jetzt: geglaettete Geschwindigkeit, nur bei gleichbleibender Richtung (< 35 Grad), und die
            // naechste noch fehlende Zelle ENTLANG der Laufrichtung (bis 3 m voraus, in 0,25-m-Schritten) - die, die der Hoerer als
            // naechste betritt.
            double tj = Jetzt; long zelle = Zelle(hoerer);
            if (hatHoerer && !double.IsNaN(hoererVorT) && tj > hoererVorT)
            {
                V v = (hoerer - hoererVor) * (1.0 / Math.Max(tj - hoererVorT, 0.05));
                bool stetig = vHat && v.Len > 0.5 && vGlatt.Len > 0.5 && V.Dot(v.Norm(), vGlatt.Norm()) > Math.Cos(35 * Math.PI / 180);
                vGlatt = vHat ? vGlatt * 0.5 + v * 0.5 : v; vHat = true;
                stetigTakte = stetig ? stetigTakte + 1 : 0;
                // Raid-3-Fix (Raid 3: 509 von 862 Vorbauten = 59 % ungenutzt): nur bei Bedarf (offene Anfragen - ohne Klaenge braucht niemand
                // einen Baum, der erste Klang baut ihn dann dringend), nur bei stetiger Richtung ueber VorbauStetigS (Halten, Umdrehen und
                // Zickzack gaben die meisten Fehlbauten; ein Takt in gleicher Richtung reicht, laenger warten liess den Hoerer oefter auf Baeume warten) und nur so weit voraus, wie es die Bauzeit verlangt: die Zelle, die der Hoerer in der
                // Zeit von zwei Bauten plus einem Takt erreicht (mindestens 1 m, hoechstens 3 m). Bisher baute er bei jedem Takt die naechste
                // fehlende Zelle bis 3 m voraus - beim Stehenbleiben oder Abbiegen waren sie alle umsonst.
                bool bedarf = offen.Count > 0;
                if (Vorbau && !vorbauBeschaeftigt && bedarf && stetigTakte * TaktS >= VorbauStetigS - 1e-9 && vGlatt.Len < 15)
                {
                    V r = vGlatt.Norm(); long zz = long.MinValue; V ziel = hoerer;
                    double voraus = Math.Max(VorbauVorausMin, Math.Min(3.0, vGlatt.Len * (VorbauBauten * baumMsGlatt / 1000 + VorbauTakte * TaktS)));
                    for (double sx = 0.25; sx <= voraus + 1e-9; sx += 0.25)
                    {
                        V p = hoerer + r * sx; long c = Zelle(p); if (c == zelle) continue;
                        bool da; lock (vorratSperre) da = vorrat.ContainsKey(c);
                        if (!da) { zz = c; ziel = p; break; }
                    }
                    if (zz != long.MinValue) VorbauStarten(0, BaumOrt(zz, ziel), zz);
                }
            }
            if (!vorbauBeschaeftigt && baum != null && !baum.Voll && faden != null && vorbauPlatz.T != null)
            { vorbauBaum = baum; vorbauGen = baum.Gen; vorbauWunsch = false; vorbauArt = 1; vorbauBeschaeftigt = true; vorbauPlatz.Los.Set(); }
            hoererVor = hoerer; hoererVorT = tj;
            double ms = (Jetzt - t0) * 1000; if (ms > DTaktMax) DTaktMax = ms;
        }

        static V BaumOrt(long zelle, V p) => new V(Math.Floor(p.X) + 0.5, p.Y, Math.Floor(p.Z) + 0.5);
        void VorbauStarten(int art, V ziel, long zz)
        {
            if (vorbauPlatz.T == null) return;
            vorbauZiel = ziel; vorbauZelle = zz; vorbauStand = tuerstand; vorbauArt = art; vorbauWunsch = false; vorbauBeschaeftigt = true; vorbauPlatz.Los.Set();
        }

        IEnumerable<Platz> AllePlaetze() { foreach (var p in plaetze) yield return p; yield return vorbauPlatz; }

        /// <summary>Auftraege eines Platzes bis zu seiner Frist (eigener Faden oder Koordinator).</summary>
        void Arbeiten(Platz p)
        {
            foreach (var a in p.Auftraege)
            {
                if (Jetzt > p.Bis) break;
                if (a.L == null) a.L = Beginnen(p, a.Id, a.Q, hoerer, a.Ta); else a.L.E.AnfrageZeit = a.Ta;
                a.Fertig = Fortsetzen(p, a.L, BudgetMs); a.Bearbeitet = true;
            }
        }

        void Zaehlen(Ergebnis e)
        {
            DSuchen++; DSucheMs += e.Ms; if (e.Ms > DSucheMax) DSucheMax = e.Ms; if (e.Abgebrochen) DUeberBudget++; if (e.Frei) DFrei++;
        }

        static Ergebnis Kopie(Ergebnis a, double ta) => new Ergebnis
        {
            Id = a.Id, Q = a.Q, H = a.H, Zeit = a.Zeit, AnfrageZeit = ta, Frei = a.Frei, Abgebrochen = a.Abgebrochen, Durch = a.Durch, Wege = a.Wege, Summe = a.Summe,
            Ms = a.Ms, Strahlen = a.Strahlen, Tuerstand = a.Tuerstand, Kachel = a.Kachel, Ankunft = a.Ankunft,
            QFrei = a.QFrei, HFrei = a.HFrei, QWeg = a.QWeg, HWeg = a.HWeg, Teile = a.Teile, Aufgegeben = a.Aufgegeben, Raeume = a.Raeume,
        };

        /// <summary>Eine Quelle beginnen (wie KartenAblauf.Lauf "D Endstand": freie Sicht -> nur Durchgang, sonst Durchgang + Hoererbaum-Wege).
        /// Quellpunkt aus Koerpern geschoben (Suche.Freistellen), Hoerer = Punkt des Baums.</summary>
        Laufend Beginnen(Platz pl, long id, V q, V h, double anfrage)
        {
            var sw = Stopwatch.StartNew(); var suche = pl.S; var b = baum;
            // wegsuche-laut (M1): Sicht, Durchgang, Raumanteil und das letzte Wegstueck gelten am echten Hoerer (aus Koerpern geschoben wie
            // der Baumpunkt), nicht am Punkt, an dem der Baum gebaut wurde (Mitte der 1-m-Zelle beim Vorbau: bis 34 dB daneben, freie Sicht
            // kippte). Der Baum liefert nur die Knotenfolge; Suche.Weiter prueft den letzten Knoten vom Hoerer aus (HSieht).
            V hf = HoererFrei(pl, h, out double hw);
            var e = new Ergebnis { Id = id, Q = q, H = h, AnfrageZeit = anfrage, Tuerstand = tuerstand, Kachel = Kachel, HFrei = hf, HWeg = hw };
            var tf = Stopwatch.StartNew(); e.QFrei = suche.Freistellen(q, out e.QWeg); suche.TFrei += tf.Elapsed.TotalMilliseconds; if (e.QWeg > 0) pl.Verschoben++;
            e.Frei = suche.Frei(e.QFrei, e.HFrei);
            e.Durch = suche.Durchgang(e.QFrei, e.HFrei);
            var l = new Laufend { E = e, B = b, Gen = b != null ? b.Gen : 0 };
            if (!e.Frei && b != null) l.S = suche.Beginnen(e.QFrei, e.HFrei, WegeK, e.Durch, b);
            e.Ms = sw.Elapsed.TotalMilliseconds;
            return l;
        }

        /// <summary>wegsuche-laut (M1): Hoerer aus Koerpern geschoben (Suche.Freistellen), je Platz fuer den letzten Hoererort gemerkt
        /// (ein Takt rechnet alle Quellen am selben Hoerer).</summary>
        V HoererFrei(Platz pl, V h, out double weg)
        {
            if (!(V.Abstand(h, pl.HVon) < 1e-9)) { pl.HVon = h; pl.HFrei = pl.S.Freistellen(h, out pl.HWeg); }
            weg = pl.HWeg; return pl.HFrei;
        }

        /// <summary>Suche weiterfuehren (je Aufruf hoechstens budgetMs, siehe Suche.Weiter). true = fertig (Ergebnis vollstaendig).</summary>
        bool Fortsetzen(Platz pl, Laufend l, double budgetMs)
        {
            var sw = Stopwatch.StartNew(); var e = l.E; var suche = pl.S;
            suche.BudgetMs = budgetMs;
            if (l.S != null && !suche.Weiter(l.S, budgetMs))
            {
                e.Ms += sw.Elapsed.TotalMilliseconds; e.Teile = l.S.Teile;
                if (l.S.Zwischen)
                {
                    // wegsuche-use (Nachlauf): Weg gefunden, Budget des Takts aufgebraucht - der Zwischenstand gilt, die Suche laeuft weiter
                    var z = new Ergebnis { Id = e.Id, Q = e.Q, H = e.H, AnfrageZeit = e.AnfrageZeit, Tuerstand = e.Tuerstand, Kachel = e.Kachel, Frei = e.Frei, Durch = e.Durch,
                                           QFrei = e.QFrei, HFrei = e.HFrei, QWeg = e.QWeg, HWeg = e.HWeg, Teile = l.S.Teile, Strahlen = l.S.Strahlen, Ms = e.Ms, Abgebrochen = true, Zwischenstand = true };
                    z.Wege = suche.Zwischenstand(l.S);
                    Abschliessen(pl, z); z.Zeit = Jetzt; l.Zwischen = z;
                    return false;
                }
                if (l.S.Teile < MaxTeile) return false;
                suche.Aufgeben(l.S); e.Aufgegeben = true; pl.Aufgegeben++;
            }
            if (l.S != null) { e.Wege = l.S.Wege; e.Abgebrochen = l.S.Abgebrochen; e.Strahlen = l.S.Strahlen; e.Teile = l.S.Teile; e.BaumUnpassend = e.Wege.Count == 0 && l.S.AmHoererVerworfen > 0; }
            Abschliessen(pl, e);
            e.Ms += sw.Elapsed.TotalMilliseconds; e.Zeit = Jetzt;
            return true;
        }

        /// <summary>Raumanteil, Summe und Ankunft.</summary>
        void Abschliessen(Platz pl, Ergebnis e)
        {
            var raum = pl.R;
            if (RaumAn && raum != null && !e.Frei)
            {
                raum.Zustand(raumstand, G.Zu, daemmNetz, daemmFrei);
                pl.Gesehen.Clear();
                // Raid-2-Fix (B): Raumanteil ueber den Raumgraphen aller Spielportale, auch ohne (oder mit nur einem gesperrten) Weg
                int vor = e.Raeume.Count;
                if (RaumGraph) raum.Graph(e.QFrei, e.HFrei, e.Raeume, pl.Gesehen);
                pl.Ketten += e.Raeume.Count - vor; pl.Graph += e.Raeume.Count - vor;
                // dazu die Raumfolgen der Wege, die der Graph nicht schon hat (gleiche Raumfolge = gleicher Raumanteil)
                foreach (var w in e.Wege)
                {
                    if (!raum.Folge(w.Punkte, pl.FolgeTmp)) continue;
                    if (!pl.Gesehen.Add(raum.FolgeSchluessel)) continue;
                    var kette = raum.Rechnen(e.QFrei, e.HFrei);
                    if (kette != null) { e.Raeume.Add(kette); pl.Ketten++; }
                }
            }
            for (int b = 0; b < 3; b++)
            {
                double en = Math.Pow(10, e.Durch.Db[b] / 10); foreach (var w in e.Wege) en += Math.Pow(10, w.Db[b] / 10);
                foreach (var k in e.Raeume) en += Math.Pow(10, k.Db[b] / 10);
                e.Summe[b] = e.Frei ? e.Durch.Db[b] : 10 * Math.Log10(Math.Min(en, 1));
            }
            Weg laut = null; foreach (var w in e.Wege) if (laut == null || w.Db[1] > laut.Db[1]) laut = w;
            RaumKette lautR = null; foreach (var k in e.Raeume) if (lautR == null || k.Db[1] > lautR.Db[1]) lautR = k;
            double lw = laut != null ? laut.Db[1] : double.NegativeInfinity, lr = lautR != null ? lautR.Db[1] : double.NegativeInfinity;
            if (e.Frei || Math.Max(lw, lr) <= e.Durch.Db[1]) e.Ankunft = e.QFrei;
            else e.Ankunft = lr > lw ? lautR.Austritt : laut.Punkte[laut.Punkte.Count - 2];
        }
        double daemmNetzF(int ni, int b) => Suche.Daemmung(G.TuerGruppe[ni], G.TuerDicke[ni], Suche.F[b], G.FugeAn);
        double daemmFreiF(int b) => Suche.Daemmung(13, -0.04, Suche.F[b], G.FugeAn);   // Raumportal ohne Netzportal (grosse Tore): Tuerblatt 4 cm
        Func<int, int, double> daemmNetzD; Func<int, double> daemmFreiD;
        Func<int, int, double> daemmNetz => daemmNetzD ?? (daemmNetzD = daemmNetzF);
        Func<int, double> daemmFrei => daemmFreiD ?? (daemmFreiD = daemmFreiF);

        /// <summary>Eine Quelle ganz rechnen (Offline-Test: unterbrochene Suche gleich weiterfuehren, Teile zaehlt die Takte).</summary>
        public Ergebnis Rechnen(long id, V q, V h, double anfrage)
        {
            var pl = plaetze[0];
            var l = Beginnen(pl, id, q, h, anfrage);
            while (!Fortsetzen(pl, l, BudgetMs)) { }
            Zaehlen(l.E);
            return l.E;
        }

        /// <summary>Offline-Test: Baum fuer h bauen und eine Quelle rechnen (ohne Faden).</summary>
        public Ergebnis Direkt(V q, V h)
        {
            hoerer = h; hatHoerer = true; foreach (var p in AllePlaetze()) p.S.CacheLeeren();
            if (KantenPruefen()) raumstand++;
            OfflineBaum(h);
            double alt = BudgetMs; BudgetMs = 0;
            try { return Rechnen(0, q, h, Jetzt); } finally { BudgetMs = alt; }
        }

        void OfflineBaum(V h)
        {
            var alt = baum; baum = null;
            var b = BaumNeu(plaetze[0].S, h); b.Tuerstand = tuerstand; b.Zelle = Zelle(h);
            if (alt != null) lock (vorratSperre) if (!vorrat.ContainsValue(alt)) baumPool.Push(alt);
            baum = b; baumZelle = b.Zelle; baumTuerstand = tuerstand;
        }

        /// <summary>Offline-Test: Hoererbaum fuer h bauen (nur wenn h eine andere 1-m-Zelle hat oder der Tuerstand wechselte), danach Rechnen.</summary>
        public void BaumOffline(V h)
        {
            if (KantenPruefen()) raumstand++;
            long z = Zelle(h);
            if (baum != null && z == baumZelle && baumTuerstand == tuerstand && V.Abstand(hoerer, h) < 1e-9) return;
            hoerer = h; hatHoerer = true; foreach (var p in AllePlaetze()) p.S.CacheLeeren(); OfflineBaum(h);
        }
        public V BaumHoerer => baum != null ? baum.H : hoerer;
        public Suche.Baum AktuellerBaum => baum;
        public void TuerDirekt(int p, bool zu) { if (G.Zu[p] != zu) { G.Zu[p] = zu; KantenTuer(p); tuerstand++; raumstand++; VorratLeeren(); } }
        public void RaumTuerDirekt(int q, bool zu) { foreach (var p in AllePlaetze()) if (p.R != null && p.R.QZu[q] != zu) { if (p.R.QZu[q] && !zu) p.R.QGeoeffnet[q] = true; p.R.QZu[q] = zu; raumstand++; } }

        // ================================================================ Diagnose
        /// <summary>Weg A: Leistungszeile (CPU je Faden, Baum- und Suchanteile) seit dem letzten Aufruf; setzt die Summen zurueck.</summary>
        public string Leistung()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            double kand = 0, such = 0, verf = 0, bew = 0, durch = 0, erw = 0, frei = 0, sch = 0; int versch = 0, ketten = 0, aufg = 0;
            foreach (var p in AllePlaetze())
            {
                var s = p.S; kand += s.TKandidaten; such += s.TSuche; verf += s.TVerfeinern; bew += s.TBewerten; durch += s.TDurchgang; erw += s.TErweitern; frei += s.TFrei; sch += s.TSchaetzen;
                s.TKandidaten = s.TSuche = s.TVerfeinern = s.TBewerten = s.TDurchgang = s.TErweitern = s.TFrei = s.TSchaetzen = 0;
                versch += p.Verschoben; ketten += p.Ketten; aufg += p.Aufgegeben; p.Verschoben = p.Ketten = p.Aufgegeben = 0;
            }
            DVerschoben += versch; DKetten += ketten; DAufgegeben += aufg;
            var sb = new System.Text.StringBuilder();
            sb.Append("CPU ms: coordinator ").Append(DCpuKoord.ToString("F0", inv));
            for (int i = 1; i < Faeden; i++) { sb.Append(", helper ").Append(i).Append(' ').Append(plaetze[i].Cpu.ToString("F0", inv)); plaetze[i].Cpu = 0; }
            sb.Append(", tree thread ").Append(DCpuVorbau.ToString("F0", inv));
            // Raid-2-Fix (8): klare Beschriftung - Baeume, auf die der Hoerer warten musste (sein Ort hatte keinen), vorab gebaute, Vorrat
            sb.Append(" | trees: rays ").Append(DBaumSaatMs.ToString("F0", inv)).Append(" + Dijkstra ").Append(DBaumDijkstraMs.ToString("F0", inv)).Append(" ms (trees built while waiting)")
              .Append(", extended ").Append((erw - DErweitertHinten < 0 ? 0 : erw - DErweitertHinten).ToString("F0", inv)).Append(" ms during searches + ").Append(DErweitertHinten.ToString("F0", inv)).Append(" ms in the background")
              .Append(", ").Append(DVorratTreffer).Append(" taken from the pool, ").Append(DVorbau).Append(" prebuilt ahead (").Append(DVorbauMs.ToString("F0", inv)).Append(" ms; ")
              .Append(DVorbauBenutzt).Append(" used, ").Append(DVorbauUngenutzt).Append(" dropped unused, ").Append(DVorbauVerworfen).Append(" discarded by door changes), pool ").Append(VorratAnzahl).Append('/').Append(VorratMax)
              .Append(", ").Append(DWartenAufBaum).Append(" ticks waited for a tree, ").Append(DBaumAmHoerer).Append(" rebuilt at the listener (cell tree did not fit), ").Append(DKantenNeu).Append(" edges reset by doors");
            sb.Append(" | sources ms: candidates ").Append(kand.ToString("F0", inv)).Append(", search ").Append(such.ToString("F0", inv)).Append(", level estimates ").Append(sch.ToString("F0", inv)).Append(", refine ").Append(verf.ToString("F0", inv))
              .Append(", evaluate ").Append(bew.ToString("F0", inv)).Append(", through walls ").Append(durch.ToString("F0", inv)).Append(", body check ").Append(frei.ToString("F0", inv));
            int graph = 0; foreach (var p in AllePlaetze()) { graph += p.Graph; p.Graph = 0; }
            int eigen = 0, ohneDeckel = 0; foreach (var p in AllePlaetze()) { if (p.R != null) { eigen += p.R.DEigen; p.R.DEigen = 0; } ohneDeckel += p.S.DOhneDeckel; p.S.DOhneDeckel = 0; }
            sb.Append(" | room shares: ").Append(ketten).Append(" (").Append(graph).Append(" from the room graph, ").Append(eigen).Append(" reverberant field of the own room)")
              .Append(", ").Append(ohneDeckel).Append(" paths indoors beyond the 25 dB outdoor limit");
            // wegsuche-use: Nachlauf, Tuerwechsel, Fern-Erweiterung, ersetzte Ketten
            int fern = 0, ers = 0; foreach (var p in AllePlaetze()) { fern += p.S.DFern; ers += p.S.DErsetzt; p.S.DFern = 0; p.S.DErsetzt = 0; }
            sb.Append(" | ").Append(DZwischen).Append(" interim results while a search continued, ").Append(DBaumBehalten).Append(" trees kept over door changes (door not reached), ")
              .Append(fern).Append(" candidates beyond ").Append(G.BaumMax.ToString("F0", inv)).Append(" m for searches without a path, ").Append(ers).Append(" paths replaced by a louder chain of the same path");
            DZwischen = 0; DBaumBehalten = 0;
            if (!double.IsNaN(DStartBaumMs)) { sb.Append(" | start: first tree ").Append(DStartBaumMs.ToString("F0", inv)).Append(" ms + warm-up ").Append(DAufwaermMs.ToString("F0", inv)).Append(" ms on the tree thread"); DStartBaumMs = double.NaN; }
            DCpuKoord = DCpuVorbau = 0; DBaumSaatMs = DBaumDijkstraMs = DVorbauMs = DErweitertHinten = 0; DVorratTreffer = DVorbau = DVorbauVerworfen = DKantenNeu = DVorbauBenutzt = DVorbauUngenutzt = DWartenAufBaum = DBaumAmHoerer = 0;
            return sb.ToString();
        }
    }
}
