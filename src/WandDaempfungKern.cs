using System;

namespace Materialklang
{
    // ---------------------------------------------------------------------------------------------
    //  0.8.5 Wanddaempfung fuer Quellen ohne Okklusion im Spiel (Abschnitt 17, WandDaempfung.cs) - reine Rechnung ohne Unity,
    //  damit der Offline-Test sie mituebersetzt.
    //
    //  Weg: dieselbe Messung wie "Wand zwischen Raeumen" (5 Linien Quelle -> Ohr, je Linie zwei Strahlen, WandLinie.Auswerten +
    //  Schichten + Massengesetz je Band), dazu ein Umweg (Beugung um das Hindernis / durch Oeffnungen), energetisch summiert:
    //    L_b = -10 lg(10^(-W_b/10) + 10^(-U_b/10)),  U_b = U_250 + 3 dB je Oktave ueber dem Bezugsband (Maekawa, grosse
    //    Fresnelzahl: 10 lg N, N ~ f) bis zur Hoechstdaemmung.
    //  U_250 (ANNAHMEN, als Einstellung): draussen / gleicher Raum 15 dB (ISO 9613-2: Abschirmung einer Kante hoechstens 20 dB,
    //  typisch Haus/Auto 10-20 dB bei 250 Hz), zwischen verschiedenen Raeumen mit Raumweg 25 dB (ISO 9613-2: Mehrfachbeugung
    //  hoechstens 25 dB) + 5 dB je weiterem Portal, Raeume ohne Verbindung im Raumnetz: kein Umweg (nur die Wand).
    //  Freie Linie -> 0 dB (der Umweg zaehlt nur, wenn die Wand daempft: L <= min(W, U)).
    //  Lautstaerke wie bei Okklusions-Quellen: dB x "Loudness factor" (Physik.LautstaerkeKern), Tiefpass: 2-poliger Fit der
    //  Bandkurve (WegeKern.TiefpassFit, wie Wege.FitLp).
    // ---------------------------------------------------------------------------------------------
    internal enum WandSituation { Unbekannt, GleicherRaum, Draussen, ZwischenRaeumen, OhneVerbindung }

    internal struct WandLogStueck { public int G, Id, Teile; public float Dicke, Db; }

    /// <summary>Ergebnis einer Linie (alles im Bezugsband [0] und 1 kHz / 4 kHz).</summary>
    internal struct WandLinienWert
    {
        public int Befund;          // 0 gemessen, 1 keine Aussage (Luecke / Ende in Geometrie), 2 dicht (Huegel / Luecke schon am Deckel)
        public float Db0, Db1, Db2; // Daemmung je Band (gedeckelt), vor Empfangsraum/Flanken
        public double Masse;        // Flaechenmasse der Wand (kg/m2), fuer die Flankenkorrektur
        public float R1, R2;        // Abstand Quelle -> erste Flaeche, letzte Flaeche -> Hoerer (m)
        public int Stuecke;         // Wandstuecke/-schichten auf der Linie
        public int Bloecke;         // mehrwand: getrennte Konstruktionen (WandDaempfungKern.Bloecke[0..Bloecke)), > 1 -> Wege.KorrigierenKette
    }

    /// <summary>Materialdaten fuer die Linienrechnung (im Spiel aus Physik/Bauakustik, im Test aus festen Werten).</summary>
    internal sealed class WandMaterial
    {
        public Func<int, int> Gruppe;                  // Collider-Id -> Gruppe
        public Func<int, bool> Gelaende;               // Collider-Id -> TerrainCollider?
        public Func<int, float, float, bool, float> Daemmung;   // (Gruppe, Dicke, Frequenz, Deckel) -> dB
        public Func<int, float, float> Masse;          // (Gruppe, Dicke) -> kg/m2
        public float[] Dichte, Einzel, Massiv;
        public bool[] Schale;
        public float SchaleM = 0.15f, Dyn = 60f;
        public int Koerper = 10;                       // Gruppe Body: zaehlt nie als Wand
        public double[] F = { 250, 1000, 4000 };
    }

    internal static class WandDaempfungKern
    {
        public const int Linien = 5, MaxTreffer = 8, LogMax = 6;
        /// <summary>0.8.8: Restenergie, ab der eine Linie als frei gilt (Daemmung unter 0,05 dB).</summary>
        public const float FreiEnergie = 0.9886f;
        public static readonly float[] Gewicht = { 0.4f, 0.15f, 0.15f, 0.15f, 0.15f };
        public const float Kreis = 0.25f;
        public const double ProOktave = 3.0;           // Maekawa: +3 dB je Frequenzverdopplung (10 lg N)
        public const double JePortal = 5.0;            // jedes weitere Portal der kuerzesten Route (Annahme)

        // ---------------------------------------------------------------- Umweg und Summe

        /// <summary>Umweg im Bezugsband (dB), -1 = kein Umweg (nur die Wand zaehlt).</summary>
        public static double Umweg250(WandSituation lage, int portale, double draussen, double zwischen, double dyn)
        {
            double u;
            switch (lage)
            {
                case WandSituation.OhneVerbindung: return -1;
                case WandSituation.ZwischenRaeumen: u = zwischen + JePortal * Math.Max(0, portale - 1); break;
                default: u = draussen; break;
            }
            return Math.Max(0, Math.Min(dyn, u));
        }

        /// <summary>Umweg bei Frequenz f (Bezug f0): +3 dB je Oktave, hoechstens dyn.</summary>
        public static double UmwegBand(double u250, double f, double f0, double dyn)
        {
            if (u250 < 0) return -1;
            double u = u250 + ProOktave * Math.Log(Math.Max(f, 1) / Math.Max(f0, 1), 2);
            return Math.Max(0, Math.Min(dyn, u));
        }

        /// <summary>Wand und Umweg je Band energetisch summiert (nie mehr als die Wand, nie unter 0 dB).</summary>
        public static void Summe(double[] wand, double u250, double[] f, double dyn, double[] aus)
        {
            for (int b = 0; b < aus.Length; b++)
            {
                double w = Math.Max(0, Math.Min(dyn, wand[b]));
                double u = UmwegBand(u250, f[b], f[0], dyn);
                if (u < 0 || w <= 0) { aus[b] = w; continue; }
                double e = Math.Pow(10, -w / 10) + Math.Pow(10, -u / 10);
                aus[b] = Math.Max(0, Math.Min(w, -10 * Math.Log10(e)));
            }
        }

        /// <summary>0.8.8: Wand und echter Raumweg (Routen des Spiels durch Tueren/Fenster bzw. Beugung ueber die Hindernisse) je Band
        /// energetisch summiert - zwei parallele Wege wie bei Occluder-Quellen (WandZwischenRaeumen, "Stufe 1"). Freie Linie -> 0 dB,
        /// nie mehr als die Wand oder der Raumweg allein. route[b] &lt; 0 = kein Raumweg (dicht): nur die Wand.</summary>
        public static void SummeWege(double[] wand, double[] route, double dyn, double[] aus)
        {
            for (int b = 0; b < aus.Length; b++)
            {
                double w = Math.Max(0, Math.Min(dyn, wand[b]));
                double r = route[b];
                if (r < 0 || w <= 0) { aus[b] = w; continue; }
                r = Math.Min(dyn, r);
                double e = Math.Pow(10, -w / 10) + Math.Pow(10, -r / 10);
                aus[b] = Math.Max(0, Math.Min(Math.Min(w, r), -10 * Math.Log10(e)));
            }
        }

        /// <summary>Amplitudenfaktor aus dB (wie Physik.LautstaerkeKern: dB x Lautheit, 10^(-dB/20)).</summary>
        public static double Gain(double db, double lautheit) => Math.Pow(10, -Math.Max(0, db) * lautheit / 20);

        /// <summary>Muss die Quelle neu gemessen werden? Erste Messung sofort; sonst nach Bewegung (Quelle/Hoerer, mind. 0,1 s)
        /// oder nach dem Takt (ferne Quellen seltener).</summary>
        public static bool Faellig(bool hat, double alter, double bewegtQuelle, double bewegtHoerer, double d, double takt, double fernAb, double fernFaktor)
        {
            if (!hat) return true;
            if (alter < 0.1) return false;
            if (bewegtQuelle > 0.5 || bewegtHoerer > 0.5) return true;
            return alter >= (d > fernAb ? takt * fernFaktor : takt);
        }

        /// <summary>Lage aus den Raumdaten des Spiels: gleiche Raum-Id, beide draussen, verschiedene Raeume mit/ohne Raumweg.</summary>
        public static WandSituation Lage(bool gueltig, int raumQ, int raumH, bool draussenQ, bool draussenH, bool hatPaar)
        {
            if (!gueltig) return WandSituation.Unbekannt;
            if (raumQ == raumH) return WandSituation.GleicherRaum;
            if (draussenQ && draussenH) return WandSituation.Draussen;
            return hatPaar ? WandSituation.ZwischenRaeumen : WandSituation.OhneVerbindung;
        }

        public static string LageName(WandSituation l)
        {
            switch (l)
            {
                case WandSituation.GleicherRaum: return "same room";
                case WandSituation.Draussen: return "both outdoors";
                case WandSituation.ZwischenRaeumen: return "different rooms";
                case WandSituation.OhneVerbindung: return "rooms without connection";
                default: return "rooms unknown";
            }
        }

        // ---------------------------------------------------------------- eine Linie (wie WandZwischenRaeumen.Linie im Physik-Modus)

        static readonly WandStueck[] stuecke = new WandStueck[2 * MaxTreffer];
        static readonly WandSchicht[] schichten = new WandSchicht[4 * MaxTreffer];
        static readonly WandLage[] schichtLagen = new WandLage[4 * MaxTreffer];   // Lage eines Stuecks (WandLinie.cs)
        /// <summary>mehrwand: Konstruktionen der letzten Linie (WandKetteKern.Getrennt), gueltig bis zum naechsten Aufruf von Linie.</summary>
        public static readonly KettenBlock[] Bloecke = new KettenBlock[4 * MaxTreffer];


        /// <summary>
        /// Wertet eine Linie aus: Treffer vor (A -> B) und rueck (B -> A, T schon ab A). A = Quelle, B = Hoerer. Gibt die Daemmung je
        /// Band zurueck; log (optional) bekommt die Schichten der Linie. Gelaende dicker als 3 m = dicht (Huegel), Koerper zaehlen nicht.
        /// </summary>
        public static WandLinienWert Linie(WandTreffer[] vor, int nVor, bool vorVoll, WandTreffer[] rueck, int nRueck, bool rueckVoll, float len,
            bool versetzt, bool mitteNahA, bool mitteNahB, WandMaterial m, WandLogStueck[] log, ref int nLog)
        {
            var r = new WandLinienWert();
            var befund = WandLinie.Auswerten(vor, nVor, vorVoll, rueck, nRueck, rueckVoll, len, versetzt, mitteNahA, mitteNahB, stuecke, out int n);
            if (befund == LinienBefund.EndeVerdeckt) { r.Befund = 1; return r; }
            float s0 = 0f, s1 = 0f, s2 = 0f; double masse = 0;
            int nl = 0;
            float f0 = (float)m.F[0], f1 = (float)m.F[1], f2 = (float)m.F[2];
            for (int i = 0; i < n; i++)
            {
                var st = stuecke[i];
                int g = m.Gruppe(st.Id);
                if (g == m.Koerper) continue;
                if (m.Gelaende(st.Id))
                {
                    float d = st.Einzeln ? m.SchaleM : st.Dicke;
                    if (d > 3f) { r.Befund = 2; r.Db0 = r.Db1 = r.Db2 = m.Dyn; r.Stuecke = n; return r; }
                    float dd = st.Einzeln ? d : Math.Max(d, 0.02f);
                    float db = m.Daemmung(g, dd, f0, true);
                    s0 += db; s1 += m.Daemmung(g, dd, f1, true); s2 += m.Daemmung(g, dd, f2, true); masse += m.Masse(g, dd);
                    Log(log, ref nLog, st.Id, g, d, db, 1);
                    continue;
                }
                if (nl + 2 > schichtLagen.Length) break;
                // 10.11. (akustik-wand, Befund 2): Dicke senkrecht zur Flaeche (WandLinie.Senkrecht), die Lage bleibt auf der Linie
                if (!st.Einzeln && st.Dicke * WandLinie.Senkrecht(st.Cos) > 1.5f)
                {
                    schichtLagen[nl++] = new WandLage { A = st.Start, B = st.Start, Einzeln = true, G = g, Id = st.Id, Cos = st.Cos };
                    schichtLagen[nl++] = new WandLage { A = st.Start + st.Dicke, B = st.Start + st.Dicke, Einzeln = true, G = g, Id = st.Id, Cos = st.Cos };
                    continue;
                }
                float a = st.Start, b = st.Einzeln ? st.Start : st.Start + Math.Max(st.Dicke, 0.02f / WandLinie.Senkrecht(st.Cos));
                schichtLagen[nl++] = new WandLage { A = a, B = b, Einzeln = st.Einzeln, G = g, Id = st.Id, Cos = st.Cos };
            }
            int ns = WandLinie.Schichten(schichtLagen, nl, m.Dichte, m.Einzel, m.Massiv, m.Schale, schichten);
            float v0 = 0f, v1 = 0f, v2 = 0f, vm = 0f, vB = 0f;
            int nb = 0;
            for (int i = 0; i < ns; i++)
            {
                var sc = schichten[i];
                float db = m.Daemmung(sc.G, sc.Dicke, f0, true), b1 = m.Daemmung(sc.G, sc.Dicke, f1, true), b2 = m.Daemmung(sc.G, sc.Dicke, f2, true);
                float mi = m.Masse(sc.G, sc.Dicke);
                s0 += db; s1 += b1; s2 += b2;
                masse += mi;
                Log(log, ref nLog, sc.Id, sc.G, sc.Dicke, db, sc.Teile);
                // 10.11. (akustik-wand, Befund 6): getrennte Schalen mit Luft dazwischen (Bauakustik.Doppelschale), je Band
                float k0 = 0f, k1 = 0f, k2 = 0f;
                double luft = i > 0 ? (sc.A - vB) * WandLinie.Senkrecht(sc.Cos) : 0;
                if (i > 0)
                {
                    k0 = (float)(Bauakustik.Doppelschale(v0, db, vm, mi, luft, f0) - (v0 + db));
                    k1 = (float)(Bauakustik.Doppelschale(v1, b1, vm, mi, luft, f1) - (v1 + b1));
                    k2 = (float)(Bauakustik.Doppelschale(v2, b2, vm, mi, luft, f2) - (v2 + b2));
                    s0 += k0; s1 += k1; s2 += k2;
                }
                // mehrwand: Abstand ueber der Hohlraumtiefe -> eigene Konstruktion (Kette, WandKetteKern); darunter Doppelschale wie bisher
                if ((i == 0 || WandKetteKern.Getrennt(luft, f0)) && nb < Bloecke.Length) Bloecke[nb++] = new KettenBlock { A = sc.A, B = sc.B };
                else { Bloecke[nb - 1].R0 += k0; Bloecke[nb - 1].R1 += k1; Bloecke[nb - 1].R2 += k2; }
                Bloecke[nb - 1].R0 += db; Bloecke[nb - 1].R1 += b1; Bloecke[nb - 1].R2 += b2; Bloecke[nb - 1].Masse += mi;
                Bloecke[nb - 1].B = Math.Max(Bloecke[nb - 1].B, sc.B); Bloecke[nb - 1].Schichten++;
                v0 = db; v1 = b1; v2 = b2; vm = mi; vB = sc.B;
            }
            if (befund == LinienBefund.Luecke)
            {
                // Untergrenze: erreicht schon das Gesehene die Hoechstdaemmung, ist die Linie sicher dicht, sonst ohne Aussage
                if (s0 >= m.Dyn) { r.Befund = 2; r.Db0 = r.Db1 = r.Db2 = m.Dyn; r.Stuecke = n; return r; }
                r.Befund = 1; return r;
            }
            float r1 = len, r2 = len;
            for (int i = 0; i < n; i++) { r1 = Math.Min(r1, stuecke[i].Start); r2 = Math.Min(r2, len - stuecke[i].Start - stuecke[i].Dicke); }
            // 10.11. (klangfarbe): je Band nur bis zur Rechengrenze, nicht bis Max. insulation - sonst ist eine dicke Wand in allen
            // Baendern gleich und der Tiefpass offen; der Deckel gilt erst fuer die Lautstaerke (WandDaempfung, WegeKern.Deckeln)
            float grenze = (float)WegeKern.BandGrenzeDb;
            r.Db0 = Math.Min(s0, grenze); r.Db1 = Math.Min(s1, grenze); r.Db2 = Math.Min(s2, grenze);
            r.Masse = masse; r.R1 = Math.Max(0f, r1); r.R2 = Math.Max(0f, r2); r.Stuecke = n; r.Bloecke = nb;
            return r;
        }


        // ---------------------------------------------------------------- 0.8.8 Boden unter der Linie
        // Meldung Streets 10.11.2026 00:03 (BTR 207-225 m, beide draussen): die Linien aus 0,75 m (Motorraum) zum Ohr (1,5 m) trafen
        // Gehweg, Fahrbahn und Kanaldeckel ('Nikitskaya_A_Sidewalk_02_BALLISTIC_soil' Soil 0.15 m 49 dB, '..._Road_02_BALLISTIC_asphalt',
        // '..._metalthick') - Bordsteine und Fahrbahnwoelbung ragen ueber 200 m um Dezimeter ueber eine so tiefe Linie. Das ist keine Wand,
        // durch die Schall hindurchgeht, sondern der Boden unter dem Weg: ISO 9613-2 rechnet ihn als Bodendaempfung A_gr (Abschn. 7.3),
        // nicht als Durchgang. Mit Abschirmung gilt A_bar = D_z - A_gr (Gl. 12), d. h. A_gr + A_bar = D_z: der Bodeneffekt steckt dann
        // im Weg ueber die Hindernisse (Ersatzweg, Profil mit Bodenrauigkeit bis eine Wellenlaenge, HindernisKern.Streifen).
        // Ohne Abschirmung waere harter Boden (G = 0, Stadt) nach Tabelle 3 A_s = A_r = -1,5 dB, A_m = -3q - eine Verstaerkung durch die
        // Bodenreflexion von 3-6 dB, keine Daempfung; sie wird nicht angesetzt (die Pegel der Quellen sind im Freien ueber Boden bezogen).
        // Regel: beide draussen, Boden unter beiden Messenden hoechstens BodenStufeM verschieden (sonst Bruecke/Ebene: echte Trennung
        // moeglich). Ein Collider zaehlt auf einer Linie als Boden, wenn ALLE seine Treffer flach liegen (|n_y| >= BodenFlachNy) und
        // hoechstens BodenSpielraumM ueber bzw. unter der Bodenlinie (Boden unter A -> Boden unter B) liegen. Gebaeude (senkrechte Waende),
        // Autos (Seitenflaechen) bleiben Waende. Eine Linie mit Bodentreffer ist vom Boden unterbrochen: kein Durchgang (Energie 0), der
        // Schall geht ueber den Boden - Ersatzweg/Raumweg tragen ihn (WandDaempfung.Auswerten).
        public const float BodenSpielraumM = 1.5f, BodenFlachNy = 0.8f, BodenStufeM = 3f;
        static readonly int[] nichtBoden = new int[4 * MaxTreffer];

        /// <summary>Liegt ein Treffer (Hoehe y, Normale ny, Anteil t der Linie ab A) als Bodenflaeche unter dem Weg?</summary>
        public static bool BodenFlaeche(float y, float ny, float t, float bodenA, float bodenB)
        {
            if (float.IsNaN(bodenA) || float.IsNaN(bodenB) || float.IsNaN(y)) return false;
            if (Math.Abs(ny) < BodenFlachNy) return false;
            float g = bodenA + (bodenB - bodenA) * Math.Max(0f, Math.Min(1f, t));
            return y <= g + BodenSpielraumM && y >= g - BodenSpielraumM;
        }

        /// <summary>Entfernt die Treffer von Boden-Collidern aus beiden Strahlen einer Linie (siehe oben). Rueckgabe = entfernte Treffer.</summary>
        public static int BodenFiltern(WandTreffer[] vor, ref int nVor, WandTreffer[] rueck, ref int nRueck, float len, float bodenA, float bodenB)
        {
            if (float.IsNaN(bodenA) || float.IsNaN(bodenB) || Math.Abs(bodenA - bodenB) > BodenStufeM || len <= 0f) return 0;
            int nn = 0;
            for (int i = 0; i < nVor + nRueck; i++)
            {
                var t = i < nVor ? vor[i] : rueck[i - nVor];
                if (!BodenFlaeche(t.Y, t.Ny, t.T / len, bodenA, bodenB) && nn < nichtBoden.Length) nichtBoden[nn++] = t.Id;
            }
            int weg = 0;
            weg += Ohne(vor, ref nVor, nn); weg += Ohne(rueck, ref nRueck, nn);
            return weg;
        }

        static int Ohne(WandTreffer[] a, ref int n, int nn)
        {
            int k = 0, weg = 0;
            for (int i = 0; i < n; i++)
            {
                bool boden = true;
                for (int j = 0; j < nn; j++) if (nichtBoden[j] == a[i].Id) { boden = false; break; }
                if (boden) { weg++; continue; }
                a[k++] = a[i];
            }
            n = k;
            return weg;
        }

        static void Log(WandLogStueck[] log, ref int nLog, int id, int g, float dicke, float db, int teile)
        {
            if (log == null || nLog >= log.Length) return;
            log[nLog++] = new WandLogStueck { Id = id, G = g, Dicke = dicke, Db = db, Teile = teile };
        }

        /// <summary>Gewichtetes Mittel der Restenergie der Linien je Band (Linien ohne Aussage fallen heraus). false = keine Aussage.</summary>
        public static bool Mitteln(float[] e0, float[] e1, float[] e2, double[] aus, double dyn)
        {
            float m0 = WandLinie.Mitteln(e0, Gewicht, Linien);
            if (m0 < 0f) return false;
            float m1 = WandLinie.Mitteln(e1, Gewicht, Linien), m2 = WandLinie.Mitteln(e2, Gewicht, Linien);
            aus[0] = DbAus(m0, dyn); aus[1] = m1 < 0 ? aus[0] : DbAus(m1, dyn); aus[2] = m2 < 0 ? aus[1] : DbAus(m2, dyn);
            return true;
        }

        public static double DbAus(double e, double dyn) => Math.Min(dyn, -10 * Math.Log10(Math.Max(e, 1e-12)));
        public static float Energie(double db) => (float)Math.Pow(10, -db / 10);

        // -----------------------------------------------------------------------------------------
        // btrfern (Meldung Streets 16.11.2026 20:24 MARK 4: "BTR hoere ich von 500 m, als waere er gleich um die Ecke").
        // Ursache (Teil Klangfarbe): die Quellen dieses Abschnitts haben keine Spiel-Okklusion, also laeuft fuer sie auch der
        // Luft-Tiefpass nicht, den alle Okklusions-Quellen im Filter-Haken bekommen (SebAkustik.cs, Farbe/Luft.Grenzfrequenz -
        // Logfeld "air=" an Schritten/Stimmen, an "[Walls]"-Quellen fehlt es). Die Bandkurve der Wanddaempfung kannte die Luft nur
        // in der Hoerbarkeitspruefung (QuellPegelKern.Rechnen: frei_b - Luft_b), nicht im gespielten Klang. BTR 420 m bei Raid-Wetter
        // 12,5 C / 60 % / 101,6 kPa: Luft 0,47 / 1,63 / 14,4 dB (review-20261110/btrfern/handrechnung.py) - ein ferner BTR klang
        // so hell wie ein naher, nur leiser.
        // Rechnung: A_atm,b = alpha_b * d (ISO 9613-2:1996 Gl. 8; alpha nach ISO 9613-1:1993 Gl. 3-5 aus dem Raid-Wetter, Wetter.Baender),
        // d = gerade Entfernung Quelle - Ohr (ISO 9613-2 rechnet A_atm ueber den Direktabstand, auch mit Schirm). Sie kommt zur
        // GESPIELTEN Daempfung je Band dazu (Lautstaerke = lautestes Band, Tiefpass aus dem Abstand der Baender), NICHT in die
        // Verdeckungsrechnung - die zieht die Luft schon selbst vom Pegel am Ohr ab (sonst doppelt). Die Abstandskurve der Quelle
        // (Abschnitte 15/17/18) enthaelt keine Luft (Log [Fixed] BTR: 200 -> 400 m genau -6,0 dB), also auch hier nichts doppelt.

        /// <summary>btrfern: Luftdaempfung je Band (dB) = alpha_b (dB/m) * d (m), d &lt; 0 zaehlt als 0. aus mit 3 Werten.</summary>
        public static void Luft(double d, double alpha0, double alpha1, double alpha2, double[] aus)
        {
            double m = d > 0 ? d : 0;
            aus[0] = Math.Max(0, alpha0) * m; aus[1] = Math.Max(0, alpha1) * m; aus[2] = Math.Max(0, alpha2) * m;
        }

        /// <summary>btrfern: Luft je Band zur gespielten Daempfung der Hoerbarkeit (G_b) dazu und Lautstaerke neu (lautestes Band).
        /// Die Verdeckung (Zusatz, Ueber) bleibt, wie QuellPegelKern.Rechnen sie mit der Luft am Ohr gerechnet hat.</summary>
        public static void LuftInG(Hoerbarkeit.Baender r, double[] luft)
        {
            if (r == null || !r.Gilt || luft == null) return;
            r.Lautstaerke = double.MaxValue;
            for (int b = 0; b < Hoerbarkeit.NB && b < luft.Length; b++)
            {
                r.G[b] += Math.Max(0, luft[b]);
                if (r.G[b] < r.Lautstaerke) r.Lautstaerke = r.G[b];
            }
        }
    }
}
