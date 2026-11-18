using System;
using System.Text;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // mehrwand (10.11.2026) - mehrere getrennte Trennwaende auf der Linie Quelle -> Hoerer als KETTE (EN ISO 12354-1 je Raumpaar).
    // Reine Rechnung ohne Unity (Offline-Test Materialklang-KlangTest). Spiel-Teil: Wege.KorrigierenKette.
    //
    // Ursache (Klang-Log Factory 10.11. 22:27:55.711, @Bot18, d = 38,8 m, Metall 0,04 m + 2 x Beton 0,15 m): WandZwischenRaeumen.Linie
    // bzw. WandDaempfungKern.Linie summierten ALLE Schichten der Linie zu einer Wand und Wege.Korrigieren rechnete darauf EINMAL die
    // Flanken (Stossstellen des Hoererraums, R' = 65 statt 111 dB) und EINMAL den Wandweg (Quelle 1. Flaeche -> letzte Flaeche Hoerer,
    // Diffusfeld des Hoererraums ueber die ganze Linie: +36,4 dB). Eine Flanke umgeht aber nur die Trennwand, an deren Stossstellen sie
    // haengt, und der Raumterm 10 lg(S/A) gilt je Raumpaar. Bei zwei Trennwaenden mit einem Raum dazwischen ist der Zwischenraum
    // Empfangsraum der ersten und Senderaum der zweiten Uebertragung:
    //    L2 = L1 - R'1 + 10 lg(S1/A_zw),  L3 = L2 - R'2 + 10 lg(S2/A3)    (EN ISO 12354-1:2000, Gl. 2 / 4.4, je Uebergang)
    // die Daemmungen addieren sich, jede mit eigenen Flanken und eigenem Raumterm.
    //
    // Zerlegung (Getrennt): zwei aufeinanderfolgende Schichten gehoeren zu EINER Konstruktion (Doppelschale, Bauakustik.Doppelschale),
    // solange ihr Luftabstand senkrecht zur Flaeche hoechstens HohlraumMax = c/(2 pi f0) ist (f0 = Bezugsband, 250 Hz -> 0,218 m): das ist
    // Sharps Grenzfrequenz f_l = c/(2 pi d) (Sharp 1978; Bies, Hansen & Howard Kap. 7.2.6) - oberhalb davon wirkt die Luftschicht in KEINEM
    // gerechneten Band mehr als Feder zwischen den Schalen, die Schalen sind akustisch entkoppelt und die Doppelschale ist schon ab
    // c/(4 pi f0) (0,109 m) gleich R_a + R_b. Groessere Abstaende sind ein eigener Raum (Zwischenraum, Messung HallZonen.RaumFuer) oder,
    // ohne gemessenen Raum, eine Freifeldstrecke (ANNAHME, konservativ: kein Diffusfeld = keine Verstaerkung).
    //
    // Raeume auf der Linie: Raum[0] = Quellraum, Raum[j] = Raum zwischen Block j-1 und j (Mitte des Abstands), Raum[n] = Hoererraum.
    // Ein Block mit DEMSELBEN gemessenen Raum auf beiden Seiten ist keine Trennwand, sondern ein Hindernis IN diesem Raum (Gasflasche vor
    // der Wand): es schattet nur den gerichteten Anteil ab (Strahl der Quelle bzw. die Abstrahlung der Wand), nicht das Diffusfeld des
    // Raums, das an ihm vorbei auf die naechste Wand trifft. Hindernisse bekommen keine Flanken (sie haengen an keiner Stossstelle).
    //
    // Leistung (W = 1) auf Trennwand p, getrennt in gerichtet G (Strahl der Punktquelle, Raumwinkel Omega/4 pi wie WegeKern.WandwegTeile)
    // und N (diffus auftreffend + Flaechenabstrahlung der vorigen Wand):
    //   1. Wand:      G = tau_Hindernisse * Omega_1/(4 pi),  N = S_1/A_0'  (Diffusfeld des Quellraums, Barron-Abfall ueber r1, WegeKern.Raum)
    //   Wand p -> q:  T = tau'_p (G + N)
    //                 G_q = tau'_p tau_H G * min(1, Omega_q/Omega_p)                       (Strahl, Kugelwelle bleibt Kugelwelle)
    //                 N_q = tau'_p tau_H N * min(1, S_q/(2 pi e^2), S_q/S_p)              (Flaechenquelle S_p im Abstand e, wie zum Hoerer)
    //                     + T * S_q / A_zw'                                               (Diffusfeld des Zwischenraums; Freifeld: 0)
    //   Hoerer (r2):  I/I0 = tau'_n tau_H [ G_dir * min(1, (Omega/4pi)/(2 pi r2^2)/I0) + N min(1/(2 pi r2^2), 1/S_n)/I0 ] + 4 T/A_n' / I0
    //   I0 = 1/(4 pi d^2). Bei EINER Trennwand ohne Hindernis ist das Zeile fuer Zeile WegeKern.WandwegTeile (Test: auf 0,01 dB gleich).
    //   Grenzfall weit weg von allen Waenden (keine Direktanteile, V = 0): 10 lg(16 pi d^2 S_1 S_2 / (A_0 A_zw A_3)) = die Kette oben (Test).
    // Flanken je Trennwand: WegeKern.Flanke mit den Stossstellen (Boden/Decke, Laenge = Wandbreite) des EMPFANGSRAUMS dieser Wand, wie
    // bisher beim Hoererraum; ohne gemessenen Empfangsraum keine Flanken (wie bisher "Hoerer draussen"). Flaeche S je Wand wie bisher:
    // Seite des Empfangsraums Richtung Quelle, hoechstens "max. wall area", nicht groesser als die Seite des Senderaums Richtung Hoerer.
    // Stetigkeit wie Wege.Korrigieren: Raumkorrektur K * min(1, Summe R'/10) (offene/duenne Teile bekommen keine Raumkorrektur).
    // ------------------------------------------------------------------------------------------

    /// <summary>Raum auf der Linie (Quell-, Zwischen- oder Hoererraum) bzw. Freifeldstrecke (Bekannt = false).</summary>
    internal sealed class KettenRaum
    {
        public bool Bekannt;                                       // A/V gemessen (HallZonen)
        public string Name;                                        // gleicher Name = derselbe Raum (Hindernis darin)
        public readonly double[] A = new double[WegeKern.N];       // aequivalente Absorptionsflaeche je Band (m2, mit Oeffnungen)
        public double V;                                           // Volumen (m3)
        public double VorB, VorH;                                  // Raumseite Richtung Quelle (waagerecht/senkrecht, m), 0 = unbekannt
        public double NachB, NachH;                                // Raumseite Richtung Hoerer
        public bool Flanke;                                        // Boden/Decke bekannt und schwer genug (Flankenwege)
        public readonly double[] RF = new double[2 * WegeKern.N];  // Daemmung Boden je Band, dann Decke je Band (dB)
        public readonly double[] MF = new double[2];               // Flaechenmasse Boden, Decke (kg/m2)

        public void Leeren()
        {
            Bekannt = Flanke = false; Name = null; V = VorB = VorH = NachB = NachH = 0;
            Array.Clear(A, 0, A.Length); Array.Clear(RF, 0, RF.Length); Array.Clear(MF, 0, MF.Length);
        }

        /// <summary>Derselbe gemessene Raum (dann ist der Block dazwischen ein Hindernis in ihm).</summary>
        public bool Gleich(KettenRaum o) => o != null && Bekannt && o.Bekannt && Name != null && string.Equals(Name, o.Name, StringComparison.Ordinal);
    }

    /// <summary>Eine Konstruktion auf der Linie (Schichten mit Kontakt oder Hohlraum bis HohlraumMax).</summary>
    internal struct KettenBlock
    {
        public float A, B;                 // Lage auf der Linie (m ab Quelle): erste Flaeche, letzte Flaeche
        public double R0, R1, R2;          // Daemmung je Band (dB) nach Massengesetz/Doppelschale, ohne Flanken und Raeume
        public double Masse;               // Flaechenmasse (kg/m2) fuer die Flanken
        public int Schichten;
        // tordoppelt (12.11.2026): alle Schichten des Blocks sind Blatt/Scheibe EINES Audio-Portals des Spiels (Tuer, Tor, Fenster;
        // Zuordnung im Spiel-Teil TuerenFenster.IstPortalBlatt). BlattB/BlattH = Rechteck des Portals (GetPortalSize, m), 0 = unbekannt.
        public bool Blatt;
        public double BlattB, BlattH;
        public double R(int b) => b == 0 ? R0 : b == 1 ? R1 : R2;
    }

    internal sealed class KettenWahl
    {
        public bool Raum = true, Flanke = true;                    // Wege.RaumKorrektur / FlankeKorrektur
        public double MaxFlaeche = 20;                             // "Receiving room: max. wall area (m2)"
        public readonly double[] LuftM = new double[WegeKern.N];   // Luft-Energiedaempfung je Band (1/m)
    }

    internal sealed class KettenErgebnis
    {
        public readonly double[] Db = new double[WegeKern.N];      // Daemmung je Band (dB, >= 0) mit Flanken, Raeumen, Zusatz
        public readonly double[] RStrich = new double[16];         // R' je Block im Bezugsband (Hindernis: R), fuer das Log
        public readonly bool[] Trennwand = new bool[16];
        public double DiffusAnteil0;                               // Hallfeld des Hoererraums / Gesamt im Bezugsband (mit Stetigkeit)
        public readonly double[] DiffusAnteilB = new double[WegeKern.N];   // hallsend (B2, 12.11.): dasselbe je Band (B[0] = DiffusAnteil0)
        // Flanke0 = Summe (R' - R) aller Trennwaende. Raum0 = Summe R' der Trennwaende - Ergebnis (ohne Gelaende), Bezugsband: alles, was
        // Raeume und Hindernisse darin gegenueber den Trennwaenden allein ausmachen (+ = lauter). kettenraum (12.11.): bis dahin gegen die
        // Summe ALLER Bloecke (mit Hindernissen) gerechnet - die Hindernisse schatten nur den Strahl, das Diffusfeld umgeht sie (siehe
        // Kopf), ihr volles R stand daher im Raumterm (Log 12.11. 13:28:30.257: "room +205.6" = 217,4 dB Hindernisse - 11,8 dB).
        public double Flanke0 = double.NaN, Raum0 = double.NaN;
        public double Hindernis0;                                  // Summe R der Hindernisse im Bezugsband (Log)
        public int Bloecke, Trennwaende, Hindernisse, FreieStrecken, Zwischenraeume;
        public double Gerichtet0, Diffus0;                         // Teile (Faktor gegenueber Freifeld, Bezugsband, vor Stetigkeit/Zusatz)
        // tordoppelt: jede Trennwand der Linie ist ein Portal-Blatt -> die Linie folgt einer Route des Raumwegs, der Wandweg traegt
        // nichts Eigenes bei (Db = Rechengrenze, der Aufrufer laesst die Linie weg). Siehe NurBlatt.
        public bool NurBlatt;
    }

    internal static class WandKetteKern
    {
        /// <summary>Groesster Luftabstand (m, senkrecht) zweier Schichten EINER Konstruktion: c/(2 pi f0) (Sharp f_l, siehe oben).</summary>
        public static double HohlraumMax(double f0) => Bauakustik.C0 / (2 * Math.PI * Math.Max(f0, 20.0));

        /// <summary>Sind zwei Schichten mit Luftabstand 'luft' (m, senkrecht zur Flaeche) getrennte Konstruktionen?</summary>
        public static bool Getrennt(double luft, double f0) => luft > HohlraumMax(f0);

        // ------------------------------------------------------------------------------------------
        // tordoppelt (12.11.2026) - ein Bauteil, eine Energie. Ursache (Klang-Log 14:03:43.348 f12076, Bot10 17,4 m hinter dem
        // geschlossenen Tor #209): das Torblatt zaehlte zweimal - als Portal #209 im Raumweg (TuerKern: Blatt + Fuge, Portal-Flaeche
        // 2,1 m2) UND als Trennwand im Wandweg, dort mit der Raumseite des Hoererraums (20 m2) statt des Blatts. Beide Wege gehen in
        // die Energiesumme (WandZwischenRaeumen.ScorePostfix): Wand 0,0 dB + Route -13,7 dB -> Summe -0,2 statt ~ -9,5 dB (Hand,
        // review-20261110/tordoppelt/marke4.py).
        // Regel: Eine Trennwand, die Blatt eines Portals ist, ist ein Teil des zusammengesetzten Bauteils "Wand mit Tuer"
        // (Flaechenanteile tau = Summe S_i tau_i / S, Bies & Hansen "Engineering Noise Control" Abschn. 7.2.5 "composite transmission
        // loss"; EN ISO 12354-1 je Element). Den Teil "Tuer" rechnet der Raumweg (Portal, TuerKern, Oeffnungs-
        // flaeche, Abstrahlung seit stahltor) - er gehoert nicht noch einmal in den Wandweg.
        //  1. Sind ALLE Trennwaende der Linie Portal-Blaetter, verlaeuft die Linie Raum fuer Raum durch Portale = eine Route des
        //     Raumwegs. Die Linie macht keine Aussage (NurBlatt); WandLinie.Mitteln verteilt ihr Gewicht auf die Linien, die die
        //     Wand NEBEN dem Blatt treffen (die stehen fuer den Rest der Trennflaeche). Treffen alle Linien Blaetter, gibt es keinen
        //     Wandweg (Vanilla-Route = Raumweg allein). Weglassen statt "Energie 0": mit 0 waere der Wandanteil davon abhaengig,
        //     wie viele der 5 Linien (0,25 m Abstand) gerade das Blatt treffen - ein Sprung je Linie (40 % Gewicht der Mitte).
        //     Die Raumseite (bis 20 m2) bleibt fuer die Wand daneben; das Blatt in ihr zaehlt dort mit (hoechstens 10 lg(S/(S-S_Blatt)),
        //     Tor: 0,5 dB zu laut, ANNAHME vertretbar gegen den Sprung).
        //  2. Sonst (Blatt + echte Wand auf derselben Linie, z. B. Tuer A-B, dann Wand B-C): ein eigener Weg (durch die Tuer nach B,
        //     dann durch die Wand) - bleibt, aber das Blatt mit SEINER Flaeche (Portal-Rechteck) und ohne Flankenwege (ANNAHME: die
        //     Stossstellen gehoeren zur Wand, in der die Tuer sitzt, nicht zum Blatt; TuerKern im Raumweg rechnet ebenfalls nur direkt).
        // Hindernisse (Block mit demselben Raum auf beiden Seiten) zaehlen fuer die Regel nicht: sie schatten nur den Direktanteil.
        // ------------------------------------------------------------------------------------------

        /// <summary>tordoppelt: ohne Raumdaten (jeder Block gilt als Trennwand) - sind alle Bloecke Portal-Blaetter?</summary>
        public static bool NurBlatt(KettenBlock[] bl, int n)
        {
            if (n <= 0) return false;
            for (int j = 0; j < n; j++) if (!bl[j].Blatt) return false;
            return true;
        }

        static bool BlattRechteck(in KettenBlock k, out double b, out double h)
        {
            b = k.BlattB; h = k.BlattH;
            return k.Blatt && b > 0 && h > 0;
        }

        /// <summary>Rechteck einer Trennwand zwischen 'send' und 'recv' (wie Wege.TBeginn/QuellRaum): Seite des Empfangsraums Richtung
        /// Quelle, auf maxFlaeche verkleinert, nicht groesser als die Seite des Senderaums Richtung Hoerer; ohne Empfangsraum die Seite des
        /// Senderaums (hoechstens maxFlaeche). false = keiner der beiden Raeume bekannt.</summary>
        public static bool Rechteck(KettenRaum send, KettenRaum recv, double maxFlaeche, out double b, out double h)
        {
            b = h = 0;
            if (recv != null && recv.Bekannt && recv.VorB * recv.VorH > 0)
            {
                Verkleinern(recv.VorB, recv.VorH, maxFlaeche, out b, out h);
                double sq = send != null && send.Bekannt ? send.NachB * send.NachH : 0;
                if (sq > 0 && sq < b * h) Verkleinern(b, h, sq, out b, out h);
                return true;
            }
            if (send != null && send.Bekannt && send.NachB * send.NachH > 0)
            {
                Verkleinern(send.NachB, send.NachH, Math.Min(send.NachB * send.NachH, maxFlaeche), out b, out h);
                return true;
            }
            return false;
        }

        static void Verkleinern(double b0, double h0, double flaeche, out double b, out double h)
        {
            // wie Wege.Rechteck (float-Eingang dort; gleiche Formel)
            double s = Math.Max(0.01, b0 * h0), f = s > flaeche ? Math.Sqrt(flaeche / s) : 1.0;
            b = b0 * f; h = h0 * f;
        }

        static double Omega(double b, double h, double r)
        {
            b = Math.Max(b, 0.3); h = Math.Max(h, 0.3); r = Math.Max(r, 0.25);
            double hx = b / 2, hy = h / 2;
            return 4 * Math.Atan(hx * hy / (r * Math.Sqrt(hx * hx + hy * hy + r * r)));
        }

        static readonly int[] teil = new int[16];
        static readonly double[] rs = new double[16 * WegeKern.N];
        static readonly double[] wb = new double[16], wh = new double[16];
        static readonly bool[] wr = new bool[16];
        static readonly double[] fR = new double[4], fM = new double[4], fL = new double[4];

        /// <summary>
        /// Kette rechnen. bl[0..n) = Bloecke in Linienrichtung, raum[0..n] = Quellraum, Zwischenraeume, Hoererraum (Laenge n+1).
        /// len = Linienlaenge (m), d = Abstand Quelle-Hoerer (Bezug Freifeld). zusatz (optional) = Daemmung je Band ausserhalb der
        /// Bloecke (Gelaende), wirkt auf alle Anteile. Ergebnis in aus (Db je Band, Log-Werte).
        /// </summary>
        public static void Rechnen(KettenBlock[] bl, int n, KettenRaum[] raum, double len, double d, KettenWahl w, double[] zusatz, KettenErgebnis aus)
        {
            if (n > teil.Length) n = teil.Length;
            aus.Bloecke = n; aus.Trennwaende = aus.Hindernisse = aus.FreieStrecken = aus.Zwischenraeume = 0;
            aus.Flanke0 = aus.Raum0 = double.NaN; aus.Hindernis0 = 0; aus.DiffusAnteil0 = 0; aus.Gerichtet0 = aus.Diffus0 = 0; Array.Clear(aus.DiffusAnteilB, 0, WegeKern.N);
            aus.NurBlatt = false;
            double grenze = WegeKern.BandGrenzeDb;
            int np = 0;
            for (int j = 0; j < n; j++)
            {
                bool tw = !raum[j].Gleich(raum[j + 1]);
                aus.Trennwand[j] = tw;
                if (tw) teil[np++] = j; else aus.Hindernisse++;
                if (j > 0) { if (!raum[j].Bekannt) aus.FreieStrecken++; else if (!raum[j].Gleich(raum[0]) && !raum[j].Gleich(raum[n])) aus.Zwischenraeume++; }
            }
            aus.Trennwaende = np;
            // tordoppelt (Regel 1): alle Trennwaende sind Portal-Blaetter -> Route des Raumwegs, keine eigene Aussage
            bool nurBlatt = np > 0;
            for (int k = 0; k < np && nurBlatt; k++) if (!bl[teil[k]].Blatt) nurBlatt = false;
            if (nurBlatt)
            {
                aus.NurBlatt = true;
                for (int b = 0; b < WegeKern.N; b++) { aus.Db[b] = grenze; aus.DiffusAnteilB[b] = 0; }
                for (int j = 0; j < n && j < aus.RStrich.Length; j++) aus.RStrich[j] = bl[j].R0;
                return;
            }

            // R' je Trennwand (Flanken des Empfangsraums), R je Hindernis; Rechteck je Trennwand
            bool flankeIrgendwo = false; double flankeSumme = 0;
            for (int j = 0; j < n; j++)
            {
                var send = raum[j]; var recv = raum[j + 1];
                bool tw = aus.Trennwand[j];
                // tordoppelt (Regel 2): ein Portal-Blatt mit seinem Rechteck (Flaeche des Portals wie im Raumweg), nicht der Raumseite
                wr[j] = tw && (BlattRechteck(bl[j], out wb[j], out wh[j]) || Rechteck(send, recv, w.MaxFlaeche, out wb[j], out wh[j]));
                for (int b = 0; b < WegeKern.N; b++)
                {
                    double r = bl[j].R(b);
                    if (tw && !bl[j].Blatt && w.Flanke && recv.Bekannt && recv.Flanke && r > 1.0 && bl[j].Masse > 0.5)
                    {
                        // wie Wege.Korrigieren: Boden, Decke (Laenge = Wandbreite), zwei Seitenwaende wie die Trennwand (Laenge = Hoehe)
                        double sFl = w.Raum && wr[j] ? wb[j] * wh[j] : Math.Min(recv.VorB * recv.VorH, w.MaxFlaeche);
                        fR[0] = recv.RF[b]; fM[0] = recv.MF[0]; fL[0] = recv.VorB;
                        fR[1] = recv.RF[WegeKern.N + b]; fM[1] = recv.MF[1]; fL[1] = recv.VorB;
                        fR[2] = fR[3] = r; fM[2] = fM[3] = bl[j].Masse; fL[2] = fL[3] = recv.VorH;
                        double rf = WegeKern.Flanke(r, bl[j].Masse, sFl, fR, fM, fL, 4);
                        if (b == 0) { flankeIrgendwo = true; flankeSumme += rf - r; }
                        r = rf;
                    }
                    rs[j * WegeKern.N + b] = r;
                }
                if (j < aus.RStrich.Length) aus.RStrich[j] = rs[j * WegeKern.N];
            }
            if (flankeIrgendwo) aus.Flanke0 = flankeSumme;

            for (int b = 0; b < WegeKern.N; b++)
            {
                double rSumme = 0;
                for (int j = 0; j < n; j++) rSumme += rs[j * WegeKern.N + b];
                double z = zusatz != null ? Math.Max(0, zusatz[b]) : 0;
                if (np == 0 || !w.Raum || !(rSumme > 0.5))
                {
                    // keine Trennwand (alles Hindernisse in einem Raum) oder ohne Raumkorrektur: nur die Daemmungen
                    aus.Db[b] = Math.Max(0, Math.Min(grenze, rSumme + z));
                    continue;
                }
                double m = w.LuftM[b];
                int j0 = teil[0], jn = teil[np - 1];
                double r1 = Math.Max(bl[j0].A, 0.25), r2 = Math.Max(len - bl[jn].B, 0.25);
                double dd = Math.Max(d, r1 + r2);
                double bezug = 1.0 / (4 * Math.PI * dd * dd);
                // alle Energien relativ zum Produkt der tau (tau' der Trennwaende, tau der Hindernisse) -> keine Unterlaeufe
                // gerichtet (Strahl) auf der 1. Trennwand: Hindernisse davor schatten ihn ab
                double tauH = 1;
                for (int j = 0; j < j0; j++) tauH *= Math.Pow(10, -rs[j * WegeKern.N + b] / 10);
                double strahl = wr[j0] ? Omega(wb[j0], wh[j0], r1) / (4 * Math.PI) : 0.5;   // ohne Rechteck: Halbraum (unendliche Wand)
                double s = wr[j0] ? Math.Max(wb[j0], 0.3) * Math.Max(wh[j0], 0.3) : 0;
                double geo = tauH * strahl;
                var q0 = raum[j0];
                double nicht = q0.Bekannt && q0.A[b] > 0 && s > 0 ? s / WegeKern.Raum(q0.A[b], q0.V, m, r1) : 0;
                double tauKette = tauH;      // Produkt der tau bis vor die aktuelle Trennwand
                double ger = 0, dif = 0;
                for (int k = 0; k < np; k++)
                {
                    int j = teil[k];
                    double tau = Math.Pow(10, -rs[j * WegeKern.N + b] / 10);
                    double T = tau * (geo + nicht);
                    if (k == np - 1)
                    {
                        double tauA = 1;
                        for (int i = j + 1; i < n; i++) tauA *= Math.Pow(10, -rs[i * WegeKern.N + b] / 10);
                        double flaecheQ = s > 0 ? Math.Min(1.0 / (2 * Math.PI * r2 * r2), 1.0 / s) : 1.0 / (2 * Math.PI * r2 * r2);
                        ger = tauKette * tau * tauA * Math.Min(1.0, strahl / (2 * Math.PI * r2 * r2) / bezug)
                              + tau * tauA * nicht * flaecheQ / bezug;
                        var hr = raum[j + 1];
                        dif = hr.Bekannt && hr.A[b] > 0 ? 4 * T / WegeKern.Raum(hr.A[b], hr.V, m, r2) / bezug : 0;
                        break;
                    }
                    int jq = teil[k + 1];
                    double tauO = 1;
                    for (int i = j + 1; i < jq; i++) tauO *= Math.Pow(10, -rs[i * WegeKern.N + b] / 10);
                    double e = Math.Max(bl[jq].A - bl[j].B, 0.25);
                    double sq = wr[jq] ? Math.Max(wb[jq], 0.3) * Math.Max(wh[jq], 0.3) : 0;
                    double fl = 1.0;
                    if (sq > 0) fl = Math.Min(fl, sq / (2 * Math.PI * e * e));
                    if (sq > 0 && s > 0) fl = Math.Min(fl, sq / s);
                    var zr = raum[j + 1];
                    double neuNicht = tau * tauO * nicht * fl
                                      + (zr.Bekannt && zr.A[b] > 0 && sq > 0 ? T * sq / WegeKern.Raum(zr.A[b], zr.V, m, e) : 0);
                    if (wr[jq]) strahl = Math.Min(strahl, Omega(wb[jq], wh[jq], Math.Max(bl[jq].A, 0.25)) / (4 * Math.PI));
                    tauKette *= tau * tauO;
                    geo = tauKette * strahl;
                    nicht = neuNicht;
                    s = sq > 0 ? sq : s;
                }
                double ges = ger + dif;
                double tauAlle = Math.Pow(10, -rSumme / 10);
                double kKorr = ges > 0 && tauAlle > 0 ? 10 * Math.Log10(ges / tauAlle) : 0;
                double stetig = Math.Min(1.0, rSumme / 10.0);
                aus.Db[b] = Math.Max(0, Math.Min(grenze, rSumme - kKorr * stetig + z));
                aus.DiffusAnteilB[b] = DiffusKern.Anteil(ger, dif) * stetig;   // hallsend (B2): je Band, gleich stetig wie Band 0
                if (b == 0)
                {
                    // kettenraum: Raumterm gegenueber den Trennwaenden allein (Db = rSumme - kKorr*stetig + z, also
                    // Raum0 = Summe R' - (Db - z) = kKorr*stetig - Summe R Hindernisse); ohne Hindernis unveraendert = kKorr*stetig
                    double rHind = 0;
                    for (int j = 0; j < n; j++) if (!aus.Trennwand[j]) rHind += rs[j * WegeKern.N];
                    aus.Hindernis0 = rHind;
                    aus.Raum0 = kKorr * stetig - rHind;
                    aus.DiffusAnteil0 = DiffusKern.Anteil(ger, dif) * stetig;
                    aus.Gerichtet0 = ger / tauAlle; aus.Diffus0 = dif / tauAlle;
                }
            }
        }

        /// <summary>Klang-Log: Kurzbeschreibung der Kette (Bezugsband).</summary>
        public static string Text(KettenErgebnis e, KettenRaum[] raum, KettenBlock[] bl)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder(160);
            sb.Append(" chain ").Append(e.Trennwaende).Append(e.Trennwaende == 1 ? " partition" : " partitions");
            if (e.Hindernisse > 0) sb.Append(" + ").Append(e.Hindernisse).Append(e.Hindernisse == 1 ? " obstacle" : " obstacles");
            sb.Append(" [");
            for (int j = 0; j < e.Bloecke && j < e.RStrich.Length; j++)
            {
                if (j > 0)
                {
                    var r = raum[j];
                    sb.Append(" | ");
                    if (r.Bekannt) sb.Append(Kurz(r.Name)).Append(' ').Append(r.A[0].ToString("F0", inv)).Append(" m2/").Append(r.V.ToString("F0", inv)).Append(" m3");
                    else sb.Append("free ").Append((bl[j].A - bl[j - 1].B).ToString("F1", inv)).Append(" m");
                    sb.Append(" | ");
                }
                sb.Append(e.Trennwand[j] ? "R' " : "obstacle ").Append(bl[j].R0.ToString("F1", inv));
                if (e.Trennwand[j] && Math.Abs(e.RStrich[j] - bl[j].R0) > 0.05) sb.Append("->").Append(e.RStrich[j].ToString("F1", inv));
                sb.Append(" dB");
            }
            sb.Append(']');
            // kettenraum: Hindernisse schatten nur den gerichteten Anteil; "room" davor gilt gegenueber den Trennwaenden allein
            if (e.Hindernisse > 0) sb.Append(" (obstacles ").Append(e.Hindernis0.ToString("F1", inv)).Append(" dB shade the direct part only, room term vs. partitions)");
            return sb.ToString();
        }

        static string Kurz(string name)
        {
            if (string.IsNullOrEmpty(name)) return "room";
            int i = name.LastIndexOf('/');
            return i >= 0 && i < name.Length - 1 ? name.Substring(i + 1) : name;
        }
    }
}
