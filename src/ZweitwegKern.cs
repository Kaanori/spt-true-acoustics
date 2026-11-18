using System;
using System.Collections.Generic;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // mehrweg (17.11.2026, Nachfolger von zweitweg): bis zu K Stimmen je Klang, eine je Richtungsbuendel der Raumwege.
    // Reine Rechnung ohne Unity (Offline-Test ZweitwegTest). Bericht: review-20261110/mehrweg-STAND.md.
    // Vorgaben Kaan 17.11.: "Der Klang aus dem laengeren Weg soll nicht einfach weg sein. In der Realitaet wuerde man beide hoeren."
    // "Evtl. gibt es auch einen 3ten, 4ten usw. Weg." Grundsatz (wörtlich): "Das, was mein Gehoer / Gehirn selbst aus den Klaengen macht,
    // muss ja nicht schon simuliert werden, das macht es ja selbst. Es soll deshalb ja so realistisch wie moeglich hoerbar sein."
    // Darum traegt jede Stimme, was physikalisch ueber ihr Wegbuendel ankommt (Richtung, Energie je Band, Laufzeit). KEINE Wahrnehmungsregel
    // entscheidet, wie es klingt; die Tore unten sind reine Kostenentscheidungen (eine Stimme faellt nur weg, wenn sie sicher unhoerbar ist,
    // technisch nicht richtig getroffen werden kann oder der Vorrat erschoepft ist - ihre Energie geht dann an das naechste Buendel).
    // Regel:
    //  1. Buendel 1 = Seite des fuehrenden Wegs (groesstes Gewicht Energie x gerichteter Anteil x Vorrang, wie RichtungKern.Gewicht - dieselbe
    //     Route, die ohne weitere Stimmen die Richtung bestimmt). Buendel k > 1: Anker = groesstes Gewicht x Rest, Vorrang je Buendel ab seinem
    //     fruehesten Weg (der frueheste/energiereichste Weg des Buendels nach derselben Praezedenzregel).
    //  2. Zugehoerigkeit m_k,i = Seite(cos(Route i, Anker k)) x Rest_i, Rest_i -= m_k,i (weich, nie mehr als uebrig). Seite = smoothstep: voll
    //     bis BuendelVollGrad, 0 ab BuendelNullGrad. Buendeln ist eine KOSTENENTSCHEIDUNG: Wege, die praktisch aus derselben Richtung kommen,
    //     teilen sich eine Stimme. "Praktisch dieselbe Richtung" = innerhalb der Lokalisationsunschaerfe: seitlich ~10 Grad, vorn ~4 Grad
    //     (Blauert 1997, Spatial Hearing, Tab. 2.1; minimum audible angle bis ~10 Grad seitlich, Mills 1958, JASA 30). Darum 10 / 20 Grad.
    //     Mehrere Wege durch dieselbe Oeffnung haben ohnehin denselben Austritt (ein Buendel).
    //  3. Was nach K Buendeln uebrig bleibt (Rest_i > 0), geht je Route an das Buendel mit dem naechsten Anker (Winkel).
    //  4. Energie: Anteile der GESAMTenergie je Band; Summe aller Stimmen = heutige Summe (RoutenMischer.Summe) exakt. Stimme k > 1 bekommt
    //     Tor_k x E_k, der Rest (1 - Tor_k) x E_k geht an das naechste FRUEHERE Buendel (Kaskade von K nach 2, stetig in jedem Tor);
    //     Stimme 1 traegt alles Uebrige: S_1,b = 1 - Summe S_k,b.
    //  5. Tore (nur Kosten, stetige Rampen):
    //     - Pegel: E_k gegen die Energie ALLER anderen Wege (sie maskieren). Hoerschwelle einer einzelnen Rueckwurf-Kopie gegen den Direktschall
    //       liegt je nach Laufzeit und Signal bei etwa -15 ... -25 dB (Barron 1971, J. Sound Vib. 15; Olive & Toole 1989, JAES 37) und steigt,
    //       wenn weitere Rueckwuerfe/Nachhall da sind (Olive & Toole 1989: in Raeumen hoeher als reflexionsarm; Blauert 1997 Kap. 3.1). Eine
    //       Stimme unter -30 dB gegen den Rest ist danach sicher unhoerbar: Rampe -30 ... -25 dB (5 dB unter der niedrigsten Literaturschwelle).
    //       Die 3./4. Stimme braucht keine eigene Schwelle: ihr Bezug (alle anderen) enthaelt die frueheren Stimmen schon.
    //     - Technik: kann der Start der Stimme nur mit Unsicherheit U ms getroffen werden (rekonstruierter Start eines Spielklangs), muss die
    //       Stimme mindestens U nach Stimme 1 kommen (sonst spielt sie im falschen Zeitbezug, ggf. vor dem Original): Rampe U ... U + max(0,5; U/2).
    //       Darf eine Stimme nicht vor Stimme 1 starten (Original laeuft schon), ist dt < 0 technisch unmoeglich -> Tor 0. Geplante Starts
    //       (Testquelle): U = 0, frueher erlaubt -> Tor 1.
    //     - Vorrat: Bitmaske der Stimmen, die eine Wiedergabequelle bekommen haben (Stimmenvorrat); ohne Quelle Tor 0.
    //  6. Laufzeit: Stimme k startet (L_k - L_min) / c nach der fruehesten gespielten Stimme (L = Weglaenge des Ankers).
    // Keine Speicheranforderung je Aufruf (Puffer in MehrAuswahl, wachsen nur, wenn ein Mischer mehr Routen hat als je zuvor).
    // ------------------------------------------------------------------------------------------
    internal sealed class MehrAuswahl
    {
        public const int KMax = 4;
        public int N;                                          // Buendel gefunden (0 = keine Route)
        public readonly int[] Index = new int[KMax];           // Anker-Route je Buendel
        public readonly V3[] Aus = new V3[KMax];               // Richtungspunkt je Buendel (Hoerer + mittlere Richtung x mittlerer Abstand)
        public readonly double[] L = new double[KMax];         // Weglaenge des Ankers (m)
        public readonly double[] DtMs = new double[KMax];      // (L_k - L_1) / c in ms (> 0: spaeter als Stimme 1)
        public readonly double[] Winkel = new double[KMax];    // Winkel des Ankers gegen den von Buendel 1 (Grad, vom Hoerer)
        public readonly double[] Roh = new double[KMax];       // Energieanteil (Bezugsband) vor dem Tor, inkl. Rest/Zufluss
        public readonly double[] PegelDb = new double[KMax];   // Pegel der Stimme gegen alle anderen Wege (dB, vor dem Tor)
        public readonly double[] Tor = new double[KMax];       // 0..1
        public readonly double[] S0 = new double[KMax], S1 = new double[KMax], S2 = new double[KMax];   // Energieanteil je Band (nach Tor)
        public double G0, G1, G2;                              // Gesamtdaemmung je Band aller Routen (dB)
        public int Aktiv;                                      // Zahl der Stimmen mit S0 > 0 (Stimme 1 zaehlt immer)
        internal readonly double[,] EB = new double[KMax, 3];  // Buendelenergie je Band (Rechenpuffer)
        internal double[] Rest = new double[8], X = new double[8];
        internal double E0Summe, E1Summe, E2Summe;
        public bool Weitere => Aktiv > 1;
    }

    internal static class ZweitwegKern
    {
        public const double BuendelVollGrad = 10.0, BuendelNullGrad = 20.0;
        public const double TorDb = -25.0, TorRampeDb = 5.0;
        static readonly double cVoll = Math.Cos(BuendelVollGrad * Math.PI / 180), cNull = Math.Cos(BuendelNullGrad * Math.PI / 180);

        static double Glatt(double x) => x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x);

        /// <summary>Zugehoerigkeit einer Route mit cos c zum Anker: 1 bis 10 Grad, 0 ab 20 Grad.</summary>
        public static double Seite(double c) => Glatt((c - cNull) / (cVoll - cNull));

        /// <summary>Pegeltor (0..1): Rampe -30 ... -25 dB gegen alle anderen Wege.</summary>
        public static double PegelTor(double pegelDb) => Glatt((pegelDb - (TorDb - TorRampeDb)) / TorRampeDb);

        /// <summary>Technik-Tor (0..1): Laufzeit dt (ms) gegen Stimme 1 bei Startunsicherheit u (ms); fruehErlaubt = Stimme darf vor Stimme 1 starten.</summary>
        public static double TechnikTor(double dtMs, double uMs, bool fruehErlaubt)
        {
            if (fruehErlaubt && !(uMs > 0)) return 1;
            double u = Math.Max(0, uMs), breite = Math.Max(0.5, u * 0.5);
            return Glatt((dtMs - u) / breite);
        }

        /// <summary>Amplitudenfaktor (Wurzel des Energieanteils).</summary>
        public static double Faktor(double s) => Math.Sqrt(s < 0 ? 0 : s > 1 ? 1 : s);

        static double E(RoutenEintrag e, int b, bool gerichtet)
            => e.W * Math.Pow(10, -e.Db[b] / 10) * (gerichtet ? 1 - NachhallKern.Verschmiert(e.Erg, b) : 1);

        /// <summary>Schritt 1-3: Buendel aus den gemischten Routen (Hoerer h im Job-Bezug, kappa wie RichtungKern.MischRichtung), hoechstens k.
        /// false = keine Route traegt bei.
        /// nachhallstimme (17.11.): gerichtet = true -&gt; nur der gerichtete Teil jeder Route (ohne den verschmierten, den die Nachhall-Stimme
        /// traegt; NachhallKern.Verschmiert); Anteile S/G sind dann Anteile am gerichteten Raumweg.</summary>
        public static bool Buendeln(List<RoutenEintrag> eintraege, V3 h, double kappa, int k, MehrAuswahl a) => Buendeln(eintraege, h, kappa, k, a, false);

        /// <summary>Wie oben; gerichtet siehe dort.</summary>
        public static bool Buendeln(List<RoutenEintrag> eintraege, V3 h, double kappa, int k, MehrAuswahl a, bool gerichtet)
        {
            a.N = 0; a.Aktiv = 0;
            if (eintraege == null || eintraege.Count == 0 || a == null) return false;
            k = Math.Max(1, Math.Min(MehrAuswahl.KMax, k));
            int n = eintraege.Count;
            if (a.Rest.Length < n) { a.Rest = new double[n * 2]; a.X = new double[n * 2]; }
            double g0 = 0, g1 = 0, g2 = 0, t0 = double.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                var e = eintraege[i];
                a.Rest[i] = e.W > 0 ? 1 : 0;
                if (e.W <= 0) continue;
                g0 += E(e, 0, gerichtet); g1 += E(e, 1, gerichtet); g2 += E(e, 2, gerichtet);
                double l = e.Erg.Laenge;
                if (l > 0 && l < t0) t0 = l;
            }
            if (!(g0 > 0)) return false;
            a.E0Summe = g0; a.E1Summe = g1; a.E2Summe = g2;
            a.G0 = -10 * Math.Log10(g0); a.G1 = g1 > 0 ? -10 * Math.Log10(g1) : a.G0 + 60; a.G2 = g2 > 0 ? -10 * Math.Log10(g2) : a.G0 + 60;
            // Gewichte der Richtung (global, wie RichtungKern.Mischen) - Richtungspunkt je Buendel und Anker 1
            for (int i = 0; i < n; i++) a.X[i] = RichtungKern.Gewicht(eintraege[i], h, true, kappa, t0, out _, out _);
            for (int b = 0; b < k; b++)
            {
                // Anker: Buendel 1 = fuehrende Route (globaler Vorrang); danach Vorrang ab dem fruehesten Weg mit Rest
                int ia = -1; double best = 0; V3 an = default;
                double tk = t0;
                if (b > 0)
                {
                    tk = double.PositiveInfinity;
                    for (int i = 0; i < n; i++)
                    {
                        if (a.Rest[i] <= 1e-9) continue;
                        double l = eintraege[i].Erg.Laenge;
                        if (l > 0 && l < tk) tk = l;
                    }
                }
                for (int i = 0; i < n; i++)
                {
                    if (a.Rest[i] <= 1e-9) continue;
                    double x = b == 0 ? a.X[i] : RichtungKern.Gewicht(eintraege[i], h, true, kappa, tk, out _, out _) * a.Rest[i];
                    V3 r = eintraege[i].Austritt - h; double lr = r.Laenge;
                    if (x > best && lr > 1e-6) { best = x; ia = i; an = r * (1.0 / lr); }
                }
                if (ia < 0) break;
                // Zugehoerigkeit, Energie je Band, Richtungspunkt
                double e0 = 0, e1 = 0, e2 = 0, sw = 0, sl = 0; V3 u = default;
                for (int i = 0; i < n; i++)
                {
                    var e = eintraege[i];
                    if (a.Rest[i] <= 0) continue;
                    V3 r = e.Austritt - h; double lr = r.Laenge;
                    double m = (lr > 1e-6 ? Seite(V3.Dot(r, an) / lr) : (i == ia ? 1 : 0)) * a.Rest[i];
                    if (i == ia) m = a.Rest[i];
                    if (!(m > 0)) continue;
                    a.Rest[i] -= m;
                    e0 += m * E(e, 0, gerichtet); e1 += m * E(e, 1, gerichtet); e2 += m * E(e, 2, gerichtet);
                    double x = a.X[i] * m;
                    if (x > 0 && lr > 1e-6) { u = u + r * (x / lr); sl += x * lr; sw += x; }
                }
                a.EB[b, 0] = e0; a.EB[b, 1] = e1; a.EB[b, 2] = e2;
                a.Index[b] = ia;
                double lu = u.Laenge;
                a.Aus[b] = sw > 0 && lu > 1e-12 ? h + u * (sl / sw / lu) : eintraege[ia].Austritt;
                a.L[b] = eintraege[ia].Erg.Laenge;
                a.N = b + 1;
            }
            if (a.N == 0) return false;
            // 3. Rest je Route an den naechsten Anker
            for (int i = 0; i < n; i++)
            {
                if (a.Rest[i] <= 1e-12) continue;
                var e = eintraege[i];
                V3 r = e.Austritt - h;
                int j = Naechster(a, h, r, a.N);
                a.EB[j, 0] += a.Rest[i] * E(e, 0, gerichtet); a.EB[j, 1] += a.Rest[i] * E(e, 1, gerichtet); a.EB[j, 2] += a.Rest[i] * E(e, 2, gerichtet);
                a.Rest[i] = 0;
            }
            V3 r1 = a.Aus[0] - h;
            for (int b = 0; b < a.N; b++)
            {
                a.DtMs[b] = a.L[0] > 0 && a.L[b] > 0 ? (a.L[b] - a.L[0]) / RaumwegKern.Schall * 1000.0 : 0;
                a.Winkel[b] = b == 0 ? 0 : RichtungKern.Winkel(a.Aus[b] - h, r1);
            }
            return true;
        }

        /// <summary>Buendel (unter den ersten bis) mit dem kleinsten Winkel zur Richtung r.</summary>
        static int Naechster(MehrAuswahl a, V3 h, V3 r, int bis)
        {
            int j = 0; double cBest = double.NegativeInfinity, lr = r.Laenge;
            for (int b = 0; b < bis; b++)
            {
                V3 d = a.Aus[b] - h; double ld = d.Laenge;
                double c = lr > 1e-9 && ld > 1e-9 ? V3.Dot(r, d) / (lr * ld) : -2;
                if (c > cBest) { cBest = c; j = b; }
            }
            return j;
        }

        /// <summary>Schritt 4-5: Tore und Kaskade. uMs/fruehErlaubt: Technik (siehe oben); maske: Bit b = Stimme b hat eine Wiedergabequelle
        /// (Bit 0 wird ignoriert, Stimme 1 ist das Original). Danach S0..S2, Tor, PegelDb, Roh, Aktiv.</summary>
        public static void Verteilen(MehrAuswahl a, V3 h, double uMs, bool fruehErlaubt, int maske)
        {
            int n = a.N;
            a.Aktiv = n > 0 ? 1 : 0;
            if (n == 0) return;
            double g0 = a.E0Summe, g1 = a.E1Summe, g2 = a.E2Summe;
            Array.Clear(zu0, 0, zu0.Length); Array.Clear(zu1, 0, zu1.Length); Array.Clear(zu2, 0, zu2.Length);
            // Arbeitskopie der Buendelenergien (Zufluss aus spaeteren Buendeln)
            double v0s = 0, v1s = 0, v2s = 0;
            for (int b = n - 1; b >= 1; b--)
            {
                double e0 = a.EB[b, 0] + zu0[b], e1 = a.EB[b, 1] + zu1[b], e2 = a.EB[b, 2] + zu2[b];
                a.Roh[b] = e0 / g0;
                a.PegelDb[b] = e0 <= 0 ? -99 : e0 >= g0 ? 99 : 10 * Math.Log10(e0 / (g0 - e0));
                double t = (maske & (1 << b)) == 0 ? 0 : PegelTor(a.PegelDb[b]) * TechnikTor(a.DtMs[b], uMs, fruehErlaubt);
                a.Tor[b] = t;
                a.S0[b] = t * e0 / g0; a.S1[b] = g1 > 0 ? t * e1 / g1 : 0; a.S2[b] = g2 > 0 ? t * e2 / g2 : 0;
                v0s += a.S0[b]; v1s += a.S1[b]; v2s += a.S2[b];
                if (a.S0[b] > 1e-9) a.Aktiv++;
                if (t < 1)
                {
                    int j = Naechster(a, h, a.Aus[b] - h, b);
                    zu0[j] += (1 - t) * e0; zu1[j] += (1 - t) * e1; zu2[j] += (1 - t) * e2;
                }
                zu0[b] = zu1[b] = zu2[b] = 0;
            }
            zu0[0] = zu1[0] = zu2[0] = 0;
            a.Tor[0] = 1; a.PegelDb[0] = 0;
            a.S0[0] = Math.Max(0, 1 - v0s); a.S1[0] = Math.Max(0, 1 - v1s); a.S2[0] = Math.Max(0, 1 - v2s);
            a.Roh[0] = a.S0[0];
            for (int b = n; b < MehrAuswahl.KMax; b++) { a.S0[b] = a.S1[b] = a.S2[b] = 0; a.Tor[b] = 0; }
        }
        // Zufluss-Puffer der Kaskade (nur Hauptfaden; je Aufruf geleert)
        static readonly double[] zu0 = new double[MehrAuswahl.KMax], zu1 = new double[MehrAuswahl.KMax], zu2 = new double[MehrAuswahl.KMax];

        /// <summary>Startversatz (s, &gt;= 0) der Stimme b: (L_b - L_min) / c, L_min = frueheste gespielte Stimme (S0 &gt; 0; Stimme 1 immer).</summary>
        public static double Versatz(MehrAuswahl a, int b)
        {
            double min = 0;
            for (int j = 1; j < a.N; j++) if (a.S0[j] > 1e-9 && a.DtMs[j] < min) min = a.DtMs[j];
            return b < a.N ? Math.Max(0, a.DtMs[b] - min) / 1000.0 : 0;
        }

        /// <summary>Tiefpass der Stimme b aus ihrer eigenen Bandkurve: Mehrdaempfung gegen das Bezugsband m_x = (G_x - 10 lg s_x) - (G_0 - 10 lg s_0),
        /// Fit wie WegeKern.TiefpassFit. fMin = untere Grenze.</summary>
        public static double Tiefpass(MehrAuswahl a, int b, double fMin)
            => Tiefpass(a.G0, a.G1, a.G2, a.S0[b], a.S1[b], a.S2[b], fMin);

        public static double Tiefpass(double g0, double g1, double g2, double s0, double s1, double s2, double fMin)
        {
            if (!(s0 > 0)) return fMin;
            double d0 = g0 - 10 * Math.Log10(s0);
            double d1 = s1 > 0 ? g1 - 10 * Math.Log10(s1) : d0 + 60, d2 = s2 > 0 ? g2 - 10 * Math.Log10(s2) : d0 + 60;
            return WegeKern.TiefpassFit(d1 - d0, d2 - d0, 1000, 4000, fMin);
        }

        /// <summary>Klangfarbe der Stimme 1 gegen die der Summe (dB, zu addieren zur Mehrdaempfung m1/m2 der Summe): -10 lg(S_1,x / S_1,0).
        /// Ohne weitere Stimmen 0 (bitgleich).</summary>
        public static void ErsteKorr(MehrAuswahl a, out double k1, out double k2)
        {
            k1 = k2 = 0;
            if (a == null || a.Aktiv <= 1 || !(a.S0[0] > 0)) return;
            k1 = a.S1[0] > 0 ? -10 * Math.Log10(a.S1[0] / a.S0[0]) : 60;
            k2 = a.S2[0] > 0 ? -10 * Math.Log10(a.S2[0] / a.S0[0]) : 60;
        }
    }
}
