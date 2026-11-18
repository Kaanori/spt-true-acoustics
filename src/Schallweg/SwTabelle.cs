using System;
using System.Collections.Generic;
using System.IO;

namespace Materialklang.Schallweg
{
    // ------------------------------------------------------------------------------------------
    //  wegtabelle (Etappe 2, 13.11.2026): vorab gerechnete Kantenwege je Karte ("Precomputed obstacle paths").
    //  Ursache (review-20261110/wegtabelle-STAND.md): die Live-Suche haengt am 2-ms-Budget in Wandzeit (Ergebnis je nach CPU-Last,
    //  Median -2,2 dB, 27 % der Laeufe > 6 dB zu leise) und an zwei Zwischenspeichern mit Vorgeschichte. Die Tabelle haelt das Ergebnis
    //  derselben Suche OHNE Budget (beide Richtungen, schliessbare Portale gesperrt) je Paar von Stuetzpunkten (Sonden, 3-m-Raster) im selben
    //  Raum. Nachschlagen: Zellverweis je 1-m-Zelle (im Bau gerechnet: 2 von der Zelle sichtbare Sonden + Gewicht 1000/(Abstand + 0,25)),
    //  2 x 2 Paare: 250 Hz/1 kHz Energiemittel nach Gewicht, 4 kHz das leiseste der Paare (Raster groeber als die Fresnel-Zone bei 4 kHz:
    //  sqrt(lambda d) ~ 1,3 m bei 20 m - gemessen sonst 16 % > 6 dB zu hell, so 6 %, Restfehler eher dumpf). Kein Strahl, kein Netz, keine BVH,
    //  keine Speicheranforderung je Nachschlagen. Reiner Kern ohne Unity (Offline-Test, Bauwerkzeug, Plugin).
    //  Datei <karte>-<pruefsumme des Wegenetzes:x16>.wegtabelle, Format "TAWT" Version 4:
    //   Kopf: Magie, Version, Karte, LeserVersion des Wegenetzes, Erstellt, Bauer (Werkzeug), Pruefsumme des Wegenetzes, Spieldateien
    //         (Name, Groesse, Zeit - dieselbe Liste wie im Wegenetz; Spiel-Update = andere Pruefsumme = nicht laden), Raster (m)
    //   Raeume: Anzahl, je Raum Raum-Id (RaumModell), Name, Sondenzahl n, Sonden (3 float), Werte n x n zu 18 Byte
    //         (dB je Band sbyte in 0,5 dB, -127 = kein Weg; Kanten des lautesten Wegs; Laenge ushort 1/32 m; letzter Kantenpunkt relativ zur
    //          Hoerersonde und erster relativ zur Quellsonde, je 3 short in 1/64 m)
    //   Zellverweis: Anzahl, Schluessel (long, aufsteigend), Raum (ushort Index), Sonden (2 ushort, 0xFFFF = keine), Gewichte (2 ushort)
    //   Ende "EDNE".
    //  tabellenverteilen (18.11.): Format 8 = Koerper wie Format 7, neuer Kopf. Bis Format 7 stand je Spieldatei die Aenderungszeit im Kopf
    //  und in der Pruefsumme - die ist auf jedem Rechner der Installationszeitpunkt, eine Tabelle passte also nur auf dem Bau-Rechner.
    //  Format 8 haelt je Spieldatei Name, Groesse und einen Inhalts-Abdruck (SpielAbdruck, XXH64) und darueber eine Gesamtsumme; keine Zeit,
    //  kein Pfad. Format 4 bis 7 bleiben lesbar (Werkzeuge, Tests), Passt lehnt sie ab (Zeitbindung) - Format 8 entsteht aus einer
    //  Format-7-Datei durch Umstempeln des Kopfes (Werkzeug Materialklang-Release-086/tabellen/werkzeug), der Koerper bleibt bytegleich.
    // ------------------------------------------------------------------------------------------
    internal sealed class TabErgebnis
    {
        public readonly double[] Db = new double[3];   // Kantensumme je Band 250 Hz / 1 kHz / 4 kHz (dB, darf ueber 0 liegen)
        public double Laenge; public int Kanten;       // lautester Weg (1 kHz) des Paars mit dem groessten Beitrag
        public V3 Letzter, Erster;                     // letzter Kantenpunkt vor dem Hoerer, erster nach der Quelle (Welt)
        public int Raum, SondeQ = -1, SondeH = -1;     // Raumindex, staerkste Sonden (Index im Raum)
        public WegTabelle.Grund Grund;
        // wegrichtung (13.11.): je beteiligtem Sondenpaar (hoechstens 2 x 2) der letzte Kantenpunkt (Welt) und sein Gewicht im Mittel
        // (Zellgewicht x Energie 1 kHz, dieselbe Groesse wie e1 in Mischen) - fuer die Richtung als Vektormittel (RichtungKern.KantenMittel).
        // wegfein (Format 5): je Paar bis zu ZWEI Wege (lautester und ein deutlich anderer) -> bis 2 x 2 x 2 = 8 Eintraege; Gewicht = Zellgewicht x
        // Energie 1 kHz DIESES Wegs. Der Rest der Paarsumme (weitere Wege) zaehlt im Pegel, hat aber keine Richtung (nicht in den Eintraegen).
        public const int PaarMax = 8;
        public int PaarN;
        public readonly V3[] PaarKante = new V3[PaarMax];
        public readonly double[] PaarE = new double[PaarMax];
        public readonly bool[] PaarOhneKante = new bool[PaarMax];   // Paar ohne Kante: PaarKante = Quellsonde
        public readonly double[] PaarGh = new double[PaarMax];      // Zellgewicht der Hoerersonde (Naehe zum Ohr)
        // wegsicht (Format 7): Paare, die nur als Fernpaar (drei Pegel, ohne Weg) beitrugen; Sichtpruefungen dieses Nachschlagens;
        // Seiten (Quelle/Hoerer), deren Sonden per Sichtpruefung gewaehlt wurden / bei denen keine Kandidat-Sonde sichtbar war.
        public int FernN, Strahlen, SichtGewaehlt, SichtKeine;
    }

    /// <summary>wegsicht: Sichtentscheidung fuer die Wahl der Sonden zur Laufzeit (Plugin: Physics.Linecast mit der Ebenenmaske der Schallstrahlen;
    /// offline: BVH des Wegenetzes). true = freie Linie a -> b.</summary>
    internal interface ISicht { bool Frei(V3 a, V3 b); }

    internal sealed class WegTabelle
    {
        public const uint Magie = 0x54574154u;   // "TAWT"
        public const int FormatVersion = 8;   // tabellenverteilen: 8 = Kopf mit Inhalts-Abdruck der Spieldateien statt Dateizeiten (Koerper wie 7)   // wegsicht: 7 = Kandidat-Sonden je Zelle (Sichtpruefung zur Laufzeit) + Fernpaare (drei Pegel)   // wegfein: 5 = zwei Wege je Paar und Zellverweis mit mehreren Raeumen je Zelle; wegkarten: 6 = duenn besetzt
                                              // (je Quellsonde nur gespeicherte Partner, Paargrenze); 4 und 5 werden weiter gelesen
        public const int WertBytes4 = 18, WertBytes5 = 36;
        public int Version = FormatVersion;
        public int WertBytes => Version >= 5 ? WertBytes5 : WertBytes4;
        // Format 6: je Sonde (global, SOff) die Partner (Hoerersonden, aufsteigend) ab POff[g] bis POff[g+1]; Satz k liegt in W ab k * 36.
        public int[] POff = new int[0], Part = new int[0];
        // Format 7: Fernpaare je Sonde (global) ab FOff[g] bis FOff[g+1]: Partner FPart (ushort, aufsteigend), Pegel FW (3 sbyte je Paar: D0 D1 D2).
        public int[] FOff = new int[0]; public ushort[] FPart = new ushort[0]; public byte[] FW = new byte[0];
        // Format 7: Kandidat-Sonden je Zellverweis-Eintrag i: ZC[ZCOff[i] .. ZCOff[i+1]) (Index im Raum, nach Abstand zur Zellmitte).
        // ZS/ZG bleiben die Wahl ohne Sichtpruefung (Format 6, Mitte-Regel; 0xFFFF = Eintrag nur fuer die Sichtpruefung).
        public int[] ZCOff = new int[1]; public ushort[] ZC = new ushort[0];
        public const int KandMax = 8;
        /// <summary>wegsicht: keine Kandidat-Sonde vom echten Ort sichtbar -> true: Wahl ohne Sichtpruefung (ZS) nehmen; false: kein Wert.</summary>
        public bool SichtLeerMitte = true;
        /// <summary>4 kHz = leisestes der 2 x 2 Paare (Etappe 2, Raster groeber als die Fresnel-Zone); false = Energiemittel wie 250 Hz/1 kHz.</summary>
        public bool Leisest4k = true;
        public const sbyte KeinWeg = -127;
        public enum Grund { Wert, KeineTabelle, ZelleOhneSonde, AndererRaum, KeinWertPaar, ZuNah, Geparkt, UeberPaargrenze }   // wegkarten: UeberPaargrenze = Format 6, keines der 2 x 2 Paare gespeichert
        /// <summary>wegkarten (16.11.): unter diesem Abstand Quelle-Hoerer (m) gilt kein Tabellenwert. Begruendung: Rastermass 3 m - liegen Quelle
        /// und Hoerer naeher als zwei Nachbarsonden, beschreiben die 2 x 2 Sondenpaare nicht die Geometrie zwischen ihnen (Raid 13.11. 22:11:31:
        /// Bot 1,3-1,5 m vor dem Spieler, Tabellenweg 22 m ueber eine Kante 12 m entfernt, Richtung 132-136 Grad daneben).</summary>
        public const double MinAbstand = 3.0;
        /// <summary>Abgestellte Quelle (Stimme zwischen zwei Saetzen im Kartenursprung, (-0,1|0|0); wie Wegsuche.Ruhend): nicht abfragen.</summary>
        public static bool Geparkt(V3 q) => Math.Abs(q.X) < 0.5 && Math.Abs(q.Y) < 0.5 && Math.Abs(q.Z) < 0.5;

        // Kopf
        public string Karte = "", NetzLeser = "", Erstellt = "", Bauer = "";
        public ulong NetzPruefsumme; public List<WegDaten.SpielDatei> Dateien = new List<WegDaten.SpielDatei>();   // Format 4-7 (mit Dateizeit)
        /// <summary>Format 8: Spieldateien mit Inhalts-Abdruck (Reihenfolge wie gespeichert) und die Gesamtsumme darueber (SpielAbdruck.Summe).
        /// NetzPruefsumme bleibt in Format 8 nur Herkunftsangabe (welches Wegenetz), sie wird nicht mehr geprueft.</summary>
        public List<SpielAbdruck.Datei> Abdruck = new List<SpielAbdruck.Datei>();
        public int AbdruckVerfahren = SpielAbdruck.Verfahren; public ulong SpielSumme;
        public double Raster = 3;
        /// <summary>Format 7: Anteil freier Sichtpruefungen (%), den der Bau von den Zellmitten aus erwartet (BVH des Wegenetzes, Pruefreihenfolge
        /// wie Waehlen). Das Plugin vergleicht ihn mit dem Anteil seiner Linecasts: weit darunter = Spiel-Collider passen nicht zum Netz.</summary>
        public double SichtFreiErwartet = -1;
        // Raeume
        public int RN; public int[] RaumId = new int[0]; public string[] RaumName = new string[0];
        public int[] SOff = new int[1]; public float[] SP = new float[0];     // Sonden je Raum: SOff[r]..SOff[r+1], 3 Werte je Sonde
        public long[] WOff = new long[1]; public byte[] W = new byte[0];      // Werte je Raum ab WOff[r] (Bytes), n x n x 18
        // Zellverweis
        public long[] ZKey = new long[0]; public ushort[] ZRaum = new ushort[0], ZS = new ushort[0], ZG = new ushort[0];

        public int Sonden(int r) => SOff[r + 1] - SOff[r];
        public long Bytes => W.LongLength + POff.LongLength * 4 + Part.LongLength * 4 + FOff.LongLength * 4 + FPart.LongLength * 2 + FW.LongLength + ZCOff.LongLength * 4 + ZC.LongLength * 2 + SP.LongLength * 4 + ZKey.LongLength * 8 + ZRaum.LongLength * 2 + ZS.LongLength * 2 + ZG.LongLength * 2;

        public static long Schluessel(int x, int y, int z) => ((long)(x + 1048576) << 42) | ((long)(y + 1048576) << 21) | (long)(z + 1048576);
        public static long Zelle(double x, double y, double z) => Schluessel((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));
        public static string Dateiname(string karte, ulong pruef) => karte + "-" + pruef.ToString("x16") + ".wegtabelle";

        // ---------------------------------------------------------------- Werte lesen (aus W, ohne Speicheranforderung)
        //  Format 4 (18 Byte): D0 D1 D2 | Kanten | Laenge | letzter (3 short) | erster (3 short) - ein Weg.
        //  Format 5 (36 Byte): D0 D1 D2 (Summe aller Wege) | Zahl der Wege (0..2) | je Weg 16 Byte: D1 (sbyte, 1 kHz dieses Wegs) | Kanten |
        //                      Laenge (ushort 1/32 m) | letzter Kantenpunkt rel. Hoerersonde (3 short 1/64 m) | erster rel. Quellsonde (3 short).
        public sbyte D(long o, int b) => unchecked((sbyte)W[o + b]);
        short S16(long o) => (short)(W[o] | (W[o + 1] << 8));
        public int Wege(long o) => Version >= 5 ? W[o + 3] : (D(o, 1) == KeinWeg ? 0 : 1);
        long PO(long o, int k) => o + 4 + 16 * k;
        public sbyte WegD1(long o, int k) => Version >= 5 ? unchecked((sbyte)W[PO(o, k)]) : D(o, 1);
        public int Kantenzahl(long o, int k = 0) => Version >= 5 ? W[PO(o, k) + 1] : W[o + 3];
        public double Laenge(long o, int k = 0) { long p = Version >= 5 ? PO(o, k) + 2 : o + 4; return (ushort)(W[p] | (W[p + 1] << 8)) / 32.0; }
        /// <summary>teil 0 = letzter Kantenpunkt (rel. Hoerersonde), 1 = erster (rel. Quellsonde); Weg k.</summary>
        public V3 Rel(long o, int teil, int k = 0) { long p = (Version >= 5 ? PO(o, k) + 4 : o + 6) + 6 * teil; return new V3(S16(p) / 64.0, S16(p + 2) / 64.0, S16(p + 4) / 64.0); }
        public V3 Sonde(int r, int i) { int k = 3 * (SOff[r] + i); return new V3(SP[k], SP[k + 1], SP[k + 2]); }
        /// <summary>Byte-Offset des Satzes (Quellsonde a, Hoerersonde b) im Raum r; -1 = nicht gespeichert (Format 6: jenseits der Paargrenze
        /// oder freie Sicht zwischen den Sonden).</summary>
        public long Wert(int r, int a, int b)
        {
            if (Version < 6) return WOff[r] + ((long)a * Sonden(r) + b) * WertBytes;
            int g = SOff[r] + a, lo = POff[g], hi = POff[g + 1] - 1;
            while (lo <= hi) { int m = (lo + hi) >> 1; int x = Part[m]; if (x == b) return (long)m * WertBytes5; if (x < b) lo = m + 1; else hi = m - 1; }
            return -1;
        }

        /// <summary>Format 7: Index des Fernpaars (a, b) im Raum r, -1 = keins.</summary>
        public int WertFern(int r, int a, int b)
        {
            if (Version < 7 || FOff.Length == 0) return -1;
            int g = SOff[r] + a, lo = FOff[g], hi = FOff[g + 1] - 1;
            while (lo <= hi) { int m = (lo + hi) >> 1; int x = FPart[m]; if (x == b) return m; if (x < b) lo = m + 1; else hi = m - 1; }
            return -1;
        }

        /// <summary>Erster Index des Zellverweises fuer den Schluessel (binaere Suche, Format 5: mehrere Eintraege je Zelle = mehrere Raeume,
        /// hintereinander), -1 = keiner.</summary>
        public int ZelleIndex(long key)
        {
            int lo = 0, hi = ZKey.Length - 1, gef = -1;
            while (lo <= hi) { int m = (lo + hi) >> 1; long k = ZKey[m]; if (k == key) { gef = m; hi = m - 1; } else if (k < key) lo = m + 1; else hi = m - 1; }
            return gef;
        }

        /// <summary>Kantenwege q -> h. false = kein Wert (Grund in r.Grund). Rechnung siehe Kopf; gleich zum Prototyp (Messplatz Vollbau2, Art 6).</summary>
        public bool Holen(V3 q, V3 h, TabErgebnis r) => Holen(q, h, r, null);
        /// <summary>wegsicht: wie Holen; mit sicht (Format 7) waehlt je Seite eine Sichtpruefung vom ECHTEN Ort die Sonden aus den Kandidaten der
        /// Zelle (die zwei naechsten sichtbaren, Gewicht 1000/(Abstand + 0,25) zum echten Ort). Ohne sicht / Format &lt; 7: Wahl aus dem Bau (ZS).
        /// Nicht fadensicher (Puffer der Instanz) - nur aus einem Faden rufen.</summary>
        public bool Holen(V3 q, V3 h, TabErgebnis r, ISicht sicht)
        {
            r.Db[0] = r.Db[1] = r.Db[2] = -200; r.Laenge = 0; r.Kanten = 0; r.SondeQ = r.SondeH = -1; r.Raum = -1; r.PaarN = 0;
            r.FernN = r.Strahlen = r.SichtGewaehlt = r.SichtKeine = 0;
            if (Version < 7) sicht = null;
            if (Geparkt(q)) { r.Grund = Grund.Geparkt; return false; }
            if (V3.Abstand(q, h) < (Raster > 0 ? Raster : MinAbstand)) { r.Grund = Grund.ZuNah; return false; }   // Rastermass der Tabelle (Standard 3 m)
            long kq = Zelle(q.X, q.Y, q.Z), kh = Zelle(h.X, h.Y, h.Z);
            int iq0 = ZelleIndex(kq), ih0 = ZelleIndex(kh);
            if (iq0 < 0 || ih0 < 0) { r.Grund = Grund.ZelleOhneSonde; return false; }
            // wegfein: liegt eine Zelle an einer Raumgrenze, hat sie einen Eintrag je Raum - genommen wird der erste gemeinsame Raum
            int iq = -1, ih = -1;
            for (int a = iq0; a < ZKey.Length && ZKey[a] == kq && iq < 0; a++)
            {
                if (sicht == null && ZS[2 * a] == 0xFFFF) continue;   // Format 7: Eintrag nur fuer die Sichtpruefung
                for (int b = ih0; b < ZKey.Length && ZKey[b] == kh; b++)
                {
                    if (sicht == null && ZS[2 * b] == 0xFFFF) continue;
                    if (ZRaum[a] == ZRaum[b]) { iq = a; ih = b; break; }
                }
            }
            if (iq < 0) { r.Grund = iq0 >= 0 && ih0 >= 0 && sicht == null && Version >= 7 && !HatWahl(iq0, kq) | !HatWahl(ih0, kh) ? Grund.ZelleOhneSonde : Grund.AndererRaum; return false; }
            int raum = ZRaum[iq]; r.Raum = raum;
            if (sicht == null) return Mischen(raum, ZS[2 * iq], ZG[2 * iq], ZS[2 * iq + 1], ZG[2 * iq + 1], ZS[2 * ih], ZG[2 * ih], ZS[2 * ih + 1], ZG[2 * ih + 1], r);
            bool okq = Waehlen(iq, raum, q, sicht, r, 0), okh = Waehlen(ih, raum, h, sicht, r, 1);
            if (!okq || !okh) { r.Grund = Grund.ZelleOhneSonde; return false; }
            return Mischen(raum, wS[0], wG[0], wS[1], wG[1], wS[2], wG[2], wS[3], wG[3], r);
        }
        bool HatWahl(int i0, long key) { for (int a = i0; a < ZKey.Length && ZKey[a] == key; a++) if (ZS[2 * a] != 0xFFFF) return true; return false; }

        // wegsicht: Puffer der Sondenwahl (je Seite 2 Sonden + Gewichte; Seite 0 = Quelle, 1 = Hoerer)
        readonly int[] wS = new int[4], wG = new int[4]; readonly int[] kI = new int[KandMax]; readonly double[] kD = new double[KandMax];
        /// <summary>Die zwei naechsten vom echten Ort p sichtbaren Kandidaten des Eintrags i (Sichtpruefung in Abstandsreihenfolge, Ende bei 2).
        /// Keiner sichtbar: SichtLeerMitte -> Wahl aus dem Bau (ZS), sonst false.</summary>
        bool Waehlen(int i, int raum, V3 p, ISicht sicht, TabErgebnis r, int seite)
        {
            int k0 = ZCOff[i], n = Math.Min(KandMax, ZCOff[i + 1] - k0);
            for (int k = 0; k < n; k++)   // nach Abstand zum echten Ort sortieren (Einfuegen, hoechstens 8)
            {
                int s = ZC[k0 + k]; double dd = V3.Abstand(Sonde(raum, s), p); int j = k;
                while (j > 0 && kD[j - 1] > dd) { kI[j] = kI[j - 1]; kD[j] = kD[j - 1]; j--; }
                kI[j] = s; kD[j] = dd;
            }
            int c = 0; wS[2 * seite] = wS[2 * seite + 1] = 0xFFFF; wG[2 * seite] = wG[2 * seite + 1] = 0;
            for (int k = 0; k < n && c < 2; k++)
            {
                r.Strahlen++;
                if (!sicht.Frei(p, Sonde(raum, kI[k]))) continue;
                wS[2 * seite + c] = kI[k]; wG[2 * seite + c] = (int)Math.Round(1000 / (kD[k] + 0.25)); c++;
            }
            if (c > 0) { r.SichtGewaehlt++; return true; }
            r.SichtKeine++;
            if (!SichtLeerMitte || ZS[2 * i] == 0xFFFF) return false;
            wS[2 * seite] = ZS[2 * i]; wG[2 * seite] = ZG[2 * i]; wS[2 * seite + 1] = ZS[2 * i + 1]; wG[2 * seite + 1] = ZG[2 * i + 1];
            return true;
        }

        /// <summary>2 x 2 Sondenpaare mischen (0xFFFF = Sonde fehlt).</summary>
        public bool Mischen(int raum, int qa, int qga, int qb, int qgb, int ha, int hga, int hb, int hgb, TabErgebnis r)
        {
            double wsum = 0, e0 = 0, e1 = 0, e2 = 0, min2 = double.MaxValue, best = -1;
            r.PaarN = 0;
            for (int i = 0; i < 2; i++)
            {
                int a = i == 0 ? qa : qb; double ga = i == 0 ? qga : qgb; if (a == 0xFFFF) continue;
                for (int j = 0; j < 2; j++)
                {
                    int b = j == 0 ? ha : hb; double gb = j == 0 ? hga : hgb; if (b == 0xFFFF) continue;
                    long o = Wert(raum, a, b);
                    if (o < 0)
                    {
                        // Format 7: Fernpaar (Paargrenze < Abstand <= Ferngrenze) - nur drei Pegel, keine Richtung (Eintraege fehlen -> Plugin faellt
                        // fuer diesen Anteil auf die echte Quelle zurueck)
                        int fi = WertFern(raum, a, b); if (fi < 0) continue;   // Format 6: Paar nicht gespeichert -> zaehlt nicht (auch nicht im Gewicht)
                        double gf = ga * gb; wsum += gf; r.FernN++;
                        sbyte f1 = unchecked((sbyte)FW[3 * (long)fi + 1]); if (f1 == KeinWeg) continue;
                        e0 += gf * Math.Pow(10, unchecked((sbyte)FW[3 * (long)fi]) / 20.0); e1 += gf * Math.Pow(10, f1 / 20.0);
                        double f2 = unchecked((sbyte)FW[3 * (long)fi + 2]) / 2.0; if (f2 < min2) min2 = f2; e2 += gf * Math.Pow(10, f2 / 10.0);
                        if (gf * Math.Pow(10, f1 / 20.0) > best) { best = gf * Math.Pow(10, f1 / 20.0); r.Laenge = 0; r.Kanten = 0; r.SondeQ = a; r.SondeH = b; r.Letzter = Sonde(raum, a); r.Erster = Sonde(raum, a); }
                        continue;
                    }
                    double g = ga * gb; wsum += g;
                    sbyte d1 = D(o, 1);
                    if (d1 == KeinWeg) continue;
                    double x1 = Math.Pow(10, d1 / 20.0);
                    e0 += g * Math.Pow(10, D(o, 0) / 20.0); e1 += g * x1;
                    double d2 = D(o, 2) / 2.0; if (d2 < min2) min2 = d2; e2 += g * Math.Pow(10, D(o, 2) / 20.0);
                    // wegrichtung/wegfein: je gespeichertem Weg ein Eintrag (Gewicht = Zellgewicht x Energie dieses Wegs, 1 kHz); ein Weg ohne Kante
                    // (freie Linie zwischen den Sonden) zeigt auf die Quellsonde. Format 4: ein Weg mit der Paarsumme (wie bisher).
                    int nw = Wege(o);
                    for (int k = 0; k < nw && r.PaarN < TabErgebnis.PaarMax; k++)
                    {
                        sbyte dk = WegD1(o, k); if (dk == KeinWeg) continue; int kk = Kantenzahl(o, k);
                        r.PaarKante[r.PaarN] = kk > 0 ? Sonde(raum, b) + Rel(o, 0, k) : Sonde(raum, a);
                        r.PaarOhneKante[r.PaarN] = kk == 0; r.PaarE[r.PaarN] = g * Math.Pow(10, dk / 20.0); r.PaarGh[r.PaarN] = gb; r.PaarN++;
                    }
                    if (g * x1 > best)
                    {
                        best = g * x1; r.Laenge = Laenge(o); r.Kanten = Kantenzahl(o); r.SondeQ = a; r.SondeH = b;
                        r.Letzter = Sonde(raum, b) + Rel(o, 0); r.Erster = Sonde(raum, a) + Rel(o, 1);
                    }
                }
            }
            if (wsum <= 0 || e1 <= 0) { r.Grund = wsum > 0 ? Grund.KeinWertPaar : Version >= 6 && qa != 0xFFFF && ha != 0xFFFF ? Grund.UeberPaargrenze : Grund.ZelleOhneSonde; return false; }
            r.Db[0] = e0 > 0 ? 10 * Math.Log10(e0 / wsum) : -200; r.Db[1] = 10 * Math.Log10(e1 / wsum); r.Db[2] = Leisest4k ? min2 : (e2 > 0 ? 10 * Math.Log10(e2 / wsum) : -200);
            r.Grund = Grund.Wert; return true;
        }

        // ---------------------------------------------------------------- Pruefung gegen die Spieldateien (tabellenverteilen)
        /// <summary>Passt die Tabelle zu den Spieldateien dieses Rechners? Format 8: je Datei Name, Groesse und Inhalts-Abdruck (SpielAbdruck)
        /// gleich, dann die Gesamtsumme. Erst alle Groessen (nur Dateiliste, ms), dann die Abdruecke - ein anderes Spiel-Update scheitert meist
        /// schon an einer Groesse. merker (optional) spart das Lesen bei unveraenderter Datei (Groesse + Aenderungszeit dieses Rechners; die
        /// Zeit entscheidet nie ueber Annehmen, sie spart nur Rechenzeit). Format 4-7: abgelehnt (Pruefsumme mit Dateizeiten des Bau-Rechners).
        /// Nicht fadengebunden (keine Unity-Aufrufe).</summary>
        public bool Passt(string datenOrdner, out string grund, SpielAbdruck.Merker merker = null)
        {
            grund = null;
            if (Version < 8) { grund = "older table format " + Version + " (tied to the file dates of the PC it was built on) - replace it with the current map data"; return false; }
            if (AbdruckVerfahren != SpielAbdruck.Verfahren) { grund = "game file check method " + AbdruckVerfahren + " unknown (expected " + SpielAbdruck.Verfahren + ")"; return false; }
            if (Abdruck.Count == 0) { grund = "no game files listed in the table"; return false; }
            if (SpielAbdruck.Summe(Abdruck) != SpielSumme) { grund = "table header damaged (checksum of the game file list)"; return false; }
            var fi = new FileInfo[Abdruck.Count];
            for (int i = 0; i < Abdruck.Count; i++)
            {
                var d = Abdruck[i]; string pfad = SpielAbdruck.Pfad(datenOrdner, d.Name);
                if (pfad == null) { grund = "invalid game file name in the table: " + d.Name; return false; }
                try { fi[i] = new FileInfo(pfad); if (!fi[i].Exists) { grund = "game file missing: " + d.Name; return false; } }
                catch (Exception e) { grund = "game file not readable: " + d.Name + " (" + e.GetType().Name + ")"; return false; }
                if (fi[i].Length != d.Groesse) { grund = "game file changed: " + d.Name + " (size " + fi[i].Length + " vs " + d.Groesse + ")"; return false; }
            }
            try
            {
                for (int i = 0; i < Abdruck.Count; i++)
                {
                    var d = Abdruck[i];
                    ulong h = merker != null ? merker.Abdruck(fi[i], d.Name) : SpielAbdruck.Inhalt(fi[i].FullName, d.Name, fi[i].Length);
                    if (h != d.Inhalt) { grund = "game file changed: " + d.Name + " (content)"; return false; }
                }
            }
            catch (Exception e) { grund = "game file not readable: " + e.GetType().Name + ": " + e.Message; return false; }
            finally { merker?.Speichern(); }
            return true;
        }

        /// <summary>Format 8 stempeln: Abdruck der Spieldateien namen (Reihenfolge bleibt) aus datenOrdner erfassen, Gesamtsumme setzen, Version 8.
        /// Fuer Werkzeuge (Umstempeln, Bau). Fehlt eine Datei: Ausnahme.</summary>
        public void Stempeln(string datenOrdner, IList<string> namen)
        {
            var l = new List<SpielAbdruck.Datei>(namen.Count);
            foreach (var n in namen)
            {
                string pfad = SpielAbdruck.Pfad(datenOrdner, n) ?? throw new ArgumentException("invalid game file name: " + n);
                var fi = new FileInfo(pfad); if (!fi.Exists) throw new FileNotFoundException("game file missing", pfad);
                l.Add(new SpielAbdruck.Datei { Name = n, Groesse = fi.Length, Inhalt = SpielAbdruck.Inhalt(pfad, n, fi.Length) });
            }
            Abdruck = l; AbdruckVerfahren = SpielAbdruck.Verfahren; SpielSumme = SpielAbdruck.Summe(l); Version = 8;
            Dateien = new List<WegDaten.SpielDatei>();   // Zeiten gehoeren nicht in Format 8
        }

        // ---------------------------------------------------------------- Datei
        public long Schreiben(string pfad)
        {
            string tmp = pfad + ".tmp";
            using (var f = new BinaryWriter(new BufferedStream(File.Create(tmp), 1 << 20)))
            {
                KopfSchreiben(f);
                f.Write(RN);
                for (int r = 0; r < RN; r++)
                {
                    f.Write(RaumId[r]); f.Write(RaumName[r] ?? ""); int n = Sonden(r); f.Write(n);
                    for (int k = 3 * SOff[r]; k < 3 * SOff[r + 1]; k++) f.Write(SP[k]);
                    if (Version < 6) { f.Write(W, (int)WOff[r], (int)((long)n * n * WertBytes)); continue; }
                    int g0 = SOff[r], k0 = POff[g0], k1 = POff[g0 + n]; f.Write(k1 - k0);
                    for (int i = 0; i <= n; i++) f.Write(POff[g0 + i] - k0);
                    for (int k = k0; k < k1; k++) f.Write(Part[k]);
                    f.Write(W, (int)((long)k0 * WertBytes5), (int)((long)(k1 - k0) * WertBytes5));
                    if (Version < 7) continue;
                    bool fern = FOff.Length > 0; int f0 = fern ? FOff[g0] : 0, f1 = fern ? FOff[g0 + n] : 0; f.Write(f1 - f0);
                    for (int i = 0; i <= n; i++) f.Write(fern ? FOff[g0 + i] - f0 : 0);
                    for (int k = f0; k < f1; k++) f.Write(FPart[k]);
                    f.Write(FW, 3 * f0, 3 * (f1 - f0));
                }
                f.Write(ZKey.Length);
                foreach (long k in ZKey) f.Write(k);
                foreach (ushort x in ZRaum) f.Write(x); foreach (ushort x in ZS) f.Write(x); foreach (ushort x in ZG) f.Write(x);
                if (Version >= 7) { for (int i = 0; i <= ZKey.Length; i++) f.Write(ZCOff.Length > i ? ZCOff[i] : (ZCOff.Length > 0 ? ZCOff[ZCOff.Length - 1] : 0)); foreach (ushort x in ZC) f.Write(x); }
                f.Write(0x454E4445u);
            }
            if (File.Exists(pfad)) File.Delete(pfad);
            File.Move(tmp, pfad);
            return new FileInfo(pfad).Length;
        }

        /// <summary>Kopf schreiben (Magie bis SichtFreiErwartet). Auch fuers Umstempeln: danach folgt der Koerper unveraendert.</summary>
        public void KopfSchreiben(BinaryWriter f)
        {
            f.Write(Magie); f.Write(Version);
            f.Write(Karte ?? ""); f.Write(NetzLeser ?? ""); f.Write(Erstellt ?? ""); f.Write(Bauer ?? ""); f.Write(NetzPruefsumme);
            if (Version >= 8)
            {
                if (Abdruck.Count == 0) throw new InvalidOperationException("format 8 needs the game file list (Stempeln)");
                f.Write(AbdruckVerfahren); f.Write(Abdruck.Count);
                foreach (var d in Abdruck) { f.Write(d.Name); f.Write(d.Groesse); f.Write(d.Inhalt); }
                f.Write(SpielSumme);
            }
            else { f.Write(Dateien.Count); foreach (var d in Dateien) { f.Write(d.Name); f.Write(d.Groesse); f.Write(d.ZeitS); } }
            f.Write(Raster);
            if (Version >= 7) f.Write(SichtFreiErwartet);
        }

        /// <summary>Nur den Kopf lesen (Pruefsumme pruefen, bevor die Werte geladen werden). null = keine Tabelle dieser Version.</summary>
        public static WegTabelle Kopf(string pfad, out string grund)
        {
            grund = null;
            try
            {
                using (var f = new BinaryReader(File.OpenRead(pfad)))
                {
                    var t = new WegTabelle(); if (!KopfLesen(f, t, out grund)) return null; return t;
                }
            }
            catch (Exception e) { grund = e.GetType().Name + ": " + e.Message; return null; }
        }

        /// <summary>Kopf lesen; f steht danach am Anfang des Koerpers (Umstempeln: Rest bytegleich kopieren).</summary>
        public static bool KopfLesen(BinaryReader f, WegTabelle t, out string grund)
        {
            grund = null;
            if (f.ReadUInt32() != Magie) { grund = "not a path table file"; return false; }
            int v = f.ReadInt32(); if (v < 4 || v > FormatVersion) { grund = "path table format " + v + " (expected 4 to " + FormatVersion + ")"; return false; }
            t.Version = v;
            t.Karte = f.ReadString(); t.NetzLeser = f.ReadString(); t.Erstellt = f.ReadString(); t.Bauer = f.ReadString(); t.NetzPruefsumme = f.ReadUInt64();
            if (v >= 8)
            {
                t.AbdruckVerfahren = f.ReadInt32();
                int m = f.ReadInt32(); if (m < 0 || m > 100000) { grund = "game file list " + m; return false; }
                t.Abdruck = new List<SpielAbdruck.Datei>(m);
                for (int i = 0; i < m; i++) t.Abdruck.Add(new SpielAbdruck.Datei { Name = f.ReadString(), Groesse = f.ReadInt64(), Inhalt = f.ReadUInt64() });
                t.SpielSumme = f.ReadUInt64();
            }
            else
            {
                int n = f.ReadInt32(); t.Dateien = new List<WegDaten.SpielDatei>(n);
                for (int i = 0; i < n; i++) t.Dateien.Add(new WegDaten.SpielDatei { Name = f.ReadString(), Groesse = f.ReadInt64(), ZeitS = f.ReadInt64() });
            }
            t.Raster = f.ReadDouble();
            if (v >= 7) t.SichtFreiErwartet = f.ReadDouble();
            return true;
        }

        public static WegTabelle Lesen(string pfad)
        {
            using (var f = new BinaryReader(new BufferedStream(File.OpenRead(pfad), 1 << 20)))
            {
                var t = new WegTabelle(); if (!KopfLesen(f, t, out string g)) throw new InvalidDataException(g);
                t.RN = f.ReadInt32(); t.RaumId = new int[t.RN]; t.RaumName = new string[t.RN]; t.SOff = new int[t.RN + 1]; t.WOff = new long[t.RN + 1];
                var sp = new List<float>(); var teile = new List<byte[]>(); var po = new List<int> { 0 }; var pa = new List<int>(); int kBasis = 0;
                var fo = new List<int> { 0 }; var fp = new List<ushort>(); var fw = new List<byte[]>(); int fBasis = 0;
                for (int r = 0; r < t.RN; r++)
                {
                    t.RaumId[r] = f.ReadInt32(); t.RaumName[r] = f.ReadString(); int n = f.ReadInt32();
                    if (n < 0 || n > 0xFFFE) throw new InvalidDataException("probe count " + n);
                    for (int k = 0; k < 3 * n; k++) sp.Add(f.ReadSingle());
                    t.SOff[r + 1] = t.SOff[r] + n;
                    if (t.Version >= 6)
                    {
                        int m = f.ReadInt32(); if (m < 0) throw new InvalidDataException("partner count");
                        int vor = f.ReadInt32(); if (vor != 0) throw new InvalidDataException("partner offsets");
                        for (int i = 1; i <= n; i++) { int x = f.ReadInt32(); if (x < vor || x > m) throw new InvalidDataException("partner offsets"); po.Add(kBasis + x); vor = x; }
                        for (int k = 0; k < m; k++) { int b = f.ReadInt32(); if (b < 0 || b >= n) throw new InvalidDataException("partner index"); pa.Add(b); }
                        var w6 = f.ReadBytes(checked(m * WertBytes5)); if (w6.Length != m * WertBytes5) throw new EndOfStreamException();
                        teile.Add(w6); t.WOff[r + 1] = t.WOff[r] + w6.LongLength; kBasis += m;
                        if (t.Version >= 7)
                        {
                            int fm = f.ReadInt32(); if (fm < 0) throw new InvalidDataException("far pair count");
                            int fv = f.ReadInt32(); if (fv != 0) throw new InvalidDataException("far pair offsets");
                            for (int i = 1; i <= n; i++) { int x = f.ReadInt32(); if (x < fv || x > fm) throw new InvalidDataException("far pair offsets"); fo.Add(fBasis + x); fv = x; }
                            for (int k = 0; k < fm; k++) { ushort b = f.ReadUInt16(); if (b >= n) throw new InvalidDataException("far partner index"); fp.Add(b); }
                            var w7 = f.ReadBytes(checked(3 * fm)); if (w7.Length != 3 * fm) throw new EndOfStreamException();
                            fw.Add(w7); fBasis += fm;
                        }
                        continue;
                    }
                    int rb = t.WertBytes; var w = f.ReadBytes(checked(n * n * rb)); if (w.Length != n * n * rb) throw new EndOfStreamException();
                    teile.Add(w); t.WOff[r + 1] = t.WOff[r] + w.LongLength;
                }
                t.SP = sp.ToArray(); t.W = new byte[t.WOff[t.RN]];
                if (t.Version >= 6) { t.POff = po.ToArray(); t.Part = pa.ToArray(); }
                if (t.Version >= 7)
                {
                    t.FOff = fo.ToArray(); t.FPart = fp.ToArray(); t.FW = new byte[3L * fBasis]; long fx = 0;
                    foreach (var b in fw) { Buffer.BlockCopy(b, 0, t.FW, (int)fx, b.Length); fx += b.Length; }
                }
                for (int r = 0; r < t.RN; r++) Buffer.BlockCopy(teile[r], 0, t.W, (int)t.WOff[r], teile[r].Length);
                int nz = f.ReadInt32(); t.ZKey = new long[nz]; t.ZRaum = new ushort[nz]; t.ZS = new ushort[2 * nz]; t.ZG = new ushort[2 * nz];
                for (int i = 0; i < nz; i++) t.ZKey[i] = f.ReadInt64();
                for (int i = 0; i < nz; i++) t.ZRaum[i] = f.ReadUInt16();
                for (int i = 0; i < 2 * nz; i++) t.ZS[i] = f.ReadUInt16();
                for (int i = 0; i < 2 * nz; i++) t.ZG[i] = f.ReadUInt16();
                if (t.Version >= 7)
                {
                    t.ZCOff = new int[nz + 1]; for (int i = 0; i <= nz; i++) { t.ZCOff[i] = f.ReadInt32(); if (i > 0 && t.ZCOff[i] < t.ZCOff[i - 1]) throw new InvalidDataException("cell candidate offsets"); }
                    if (t.ZCOff[0] != 0) throw new InvalidDataException("cell candidate offsets");
                    t.ZC = new ushort[t.ZCOff[nz]]; for (int i = 0; i < t.ZC.Length; i++) t.ZC[i] = f.ReadUInt16();
                    for (int i = 0; i < nz; i++) for (int k = t.ZCOff[i]; k < t.ZCOff[i + 1]; k++) if (t.ZRaum[i] >= t.RN || t.ZC[k] >= t.Sonden(t.ZRaum[i])) throw new InvalidDataException("cell candidate probe");
                }
                if (f.ReadUInt32() != 0x454E4445u) throw new InvalidDataException("file end marker missing (truncated?)");
                for (int i = 1; i < nz; i++) if (t.ZKey[i] < t.ZKey[i - 1] || (t.Version < 5 && t.ZKey[i] == t.ZKey[i - 1])) throw new InvalidDataException("cell keys not sorted");
                for (int i = 0; i < nz; i++) { if (t.ZRaum[i] >= t.RN) throw new InvalidDataException("cell room"); for (int k = 0; k < 2; k++) { int s = t.ZS[2 * i + k]; if (s != 0xFFFF && s >= t.Sonden(t.ZRaum[i])) throw new InvalidDataException("cell probe"); } }
                return t;
            }
        }
    }

    /// <summary>tabellenverteilen (18.11.): rechnerunabhaengiger Abdruck einer Spieldatei fuer die Pruefung der Wegtabelle (Format 8).
    /// Eingang je Datei: Name (nur der Dateiname im Datenordner des Spiels, ohne Pfad - gleich unter Windows und Proton), Groesse und ein
    /// XXH64 ueber festgelegte Bereiche des Inhalts; keine Dateizeit, kein Ordner. Bereiche (Verfahren 1):
    ///  - Szenendateien "level&lt;n&gt;" und alle Dateien bis VollGrenze: ganz (dort stehen Lage und Ausrichtung jedes Colliders, die Raum-
    ///    kaesten und Tueren; eine verschobene Wand aendert die Groesse nicht).
    ///  - groessere Dateien (sharedassets*.assets, *.resS, ...): der Kopf - bei Unity-SerializedFiles die ganzen Metadaten bis zum Datenanfang
    ///    (Typen und Objekttabelle mit Lage und Groesse JEDES Objekts: ein Netz mit anderer Eckenzahl aendert sie), sonst KopfStd -, dazu
    ///    ProbeN Stichproben zu ProbeB Byte gleichmaessig ueber den Rest, die letzte endet am Dateiende.
    /// Annahme (begruendet, nicht beweisbar): eine Aenderung, die nur Ecken eines Netzes in einer grossen Datei bei gleicher Groesse und
    /// gleicher Objekttabelle verschiebt und KEINE Szenendatei beruehrt, faellt nicht auf. Ein Spiel-Update baut die Szenen neu.
    /// Rechenzeit: Groessen zuerst (ms), Abdruecke nur bei passenden Groessen; Merker spart das Lesen unveraenderter Dateien.
    /// Bytefolge: XXH64 liest 8-Byte-Woerter little-endian (x86/x64/ARM64 unter Windows und Linux).</summary>
    internal static class SpielAbdruck
    {
        public const int Verfahren = 1;
        public const long VollGrenze = 1L << 20, KopfStd = 1L << 16, KopfMax = 64L << 20;
        public const int ProbeN = 32, ProbeB = 4096;
        public struct Datei { public string Name; public long Groesse; public ulong Inhalt; }

        /// <summary>Nur ein blanker Dateiname des Datenordners: Buchstaben, Ziffern, '.', '_', '-', nicht mit '.' beginnend, hoechstens
        /// 200 Zeichen. Damit gibt es keinen Pfadtrenner ('/' oder '\'), kein Laufwerk (':'), kein ".." und keinen absoluten Pfad - auf
        /// jedem System dieselbe Entscheidung (reine Zeichenpruefung, kein Path.IsPathRooted, das unter Linux "C:\x" anders sieht).</summary>
        public static bool NameGueltig(string n)
        {
            if (string.IsNullOrEmpty(n) || n.Length > 200 || n[0] == '.') return false;
            foreach (char c in n)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')) return false;
            return true;
        }
        public static string Pfad(string datenOrdner, string name) => NameGueltig(name) && !string.IsNullOrEmpty(datenOrdner) ? Path.Combine(datenOrdner, name) : null;

        /// <summary>Szenendatei "level" + Ziffern (nicht levelN.resS).</summary>
        public static bool IstSzene(string n)
        {
            if (n == null || n.Length <= 5 || string.CompareOrdinal(n, 0, "level", 0, 5) != 0) return false;
            for (int i = 5; i < n.Length; i++) if (n[i] < '0' || n[i] > '9') return false;
            return true;
        }

        /// <summary>Ende der Metadaten einer Unity-SerializedFile aus den ersten 48 Byte (Version &gt;= 22: Feld Dateigroesse und Datenanfang
        /// 64 bit ab Byte 24/32, aeltere 32 bit ab Byte 4/12, alles big-endian); -1 = keine SerializedFile (z. B. .resS) oder Kopf passt nicht
        /// zur Groesse.</summary>
        public static long MetaEnde(byte[] k, int n, long groesse)
        {
            if (k == null || n < 48) return -1;
            uint ver = BE32(k, 8);
            if (ver >= 22 && ver < 100) { long fs = (long)BE64(k, 24), dof = (long)BE64(k, 32); return fs == groesse && dof >= 48 && dof <= groesse ? dof : -1; }
            if (ver >= 9 && ver < 22) { long fs = BE32(k, 4), dof = BE32(k, 12); return fs == groesse && dof >= 20 && dof <= groesse ? dof : -1; }
            return -1;
        }
        static uint BE32(byte[] b, int p) => (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]);
        static ulong BE64(byte[] b, int p) => (ulong)BE32(b, p) << 32 | BE32(b, p + 4);

        /// <summary>Gelesene Bereiche (Anfang, Laenge) nach Verfahren 1; Rueckgabe = Anzahl (1 = ganze Datei). metaEnde aus MetaEnde (-1 = keins).
        /// start/laenge brauchen ProbeN + 1 Plaetze. Reine Funktion.</summary>
        public static int Bereiche(string name, long groesse, long metaEnde, long[] start, long[] laenge)
        {
            if (groesse <= VollGrenze || IstSzene(name)) { start[0] = 0; laenge[0] = groesse; return 1; }
            long kopf = metaEnde > 0 ? metaEnde : KopfStd;
            if (kopf < KopfStd) kopf = KopfStd; if (kopf > KopfMax) kopf = KopfMax;
            if (groesse - kopf < (long)ProbeN * ProbeB) { start[0] = 0; laenge[0] = groesse; return 1; }
            start[0] = 0; laenge[0] = kopf;
            long spanne = groesse - kopf - ProbeB;
            for (int i = 0; i < ProbeN; i++) { start[1 + i] = kopf + spanne * i / (ProbeN - 1); laenge[1 + i] = ProbeB; }
            return 1 + ProbeN;
        }

        /// <summary>Abdruck der Datei (XXH64, Saat 0, ueber die Bereiche hintereinander). Liest nur die Bereiche. Ausnahme bei Lesefehler.</summary>
        public static ulong Inhalt(string pfad, string name, long groesse) => Inhalt(pfad, name, groesse, out _);
        public static ulong Inhalt(string pfad, string name, long groesse, out long gelesen)
        {
            var puf = new byte[1 << 20]; var h = new Xxh64(); gelesen = 0;
            using (var fs = new FileStream(pfad, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096))
            {
                if (fs.Length != groesse) throw new IOException("size changed while reading: " + name);
                long meta = -1;
                if (groesse > VollGrenze && !IstSzene(name)) { int k = Lies(fs, 0, puf, 48); meta = MetaEnde(puf, k, groesse); }
                var st = new long[ProbeN + 1]; var la = new long[ProbeN + 1];
                int nb = Bereiche(name, groesse, meta, st, la);
                for (int b = 0; b < nb; b++)
                {
                    long pos = st[b], rest = la[b];
                    while (rest > 0)
                    {
                        int k = Lies(fs, pos, puf, (int)Math.Min(puf.Length, rest));
                        if (k <= 0) throw new EndOfStreamException(name);
                        h.Update(puf, 0, k); pos += k; rest -= k; gelesen += k;
                    }
                }
            }
            return h.Digest();
        }
        static int Lies(FileStream fs, long pos, byte[] puf, int n)
        {
            if (fs.Position != pos) fs.Seek(pos, SeekOrigin.Begin);
            int got = 0; while (got < n) { int r = fs.Read(puf, got, n - got); if (r <= 0) break; got += r; }
            return got;
        }

        /// <summary>Gesamtsumme ueber die Liste (Reihenfolge wie gespeichert): XXH64 ueber "TAWT-GAMEFILES|&lt;Verfahren&gt;\n" und je Datei
        /// "name|groesse|abdruck(x16)\n" in UTF-8, Zahlen kulturunabhaengig.</summary>
        public static ulong Summe(IList<Datei> l)
        {
            var sb = new System.Text.StringBuilder("TAWT-GAMEFILES|").Append(Verfahren.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            foreach (var d in l)
                sb.Append(d.Name).Append('|').Append(d.Groesse.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                  .Append(d.Inhalt.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            var b = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            var h = new Xxh64(); h.Update(b, 0, b.Length); return h.Digest();
        }

        /// <summary>Merker: Abdruck je Datei dieses Rechners, gueltig solange Groesse und Aenderungszeit (Ticks UTC) gleich sind. Nur Rechen-
        /// ersparnis (ein Raid-Start liest sonst bis ~600 MB Szenendateien je Karte), keine Pruefung: angenommen wird nur bei gleichem Abdruck.
        /// Datei: eine Zeile je Spieldatei "verfahren TAB name TAB groesse TAB ticks TAB abdruck"; kaputte Zeilen werden uebergangen.
        /// Fadensicher (lock), Unity-frei.</summary>
        public sealed class Merker
        {
            readonly string datei; readonly Dictionary<string, (long g, long t, ulong h)> m = new Dictionary<string, (long, long, ulong)>(StringComparer.Ordinal);
            bool geaendert;
            public long Gelesen; public int Treffer, Gerechnet;
            public Merker(string datei)
            {
                this.datei = datei;
                try
                {
                    if (datei == null || !File.Exists(datei)) return;
                    foreach (var z in File.ReadAllLines(datei))
                    {
                        var t = z.Split('\t');
                        if (t.Length != 5 || t[0] != Verfahren.ToString(System.Globalization.CultureInfo.InvariantCulture) || !NameGueltig(t[1])) continue;
                        if (long.TryParse(t[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long g) &&
                            long.TryParse(t[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long ti) &&
                            ulong.TryParse(t[4], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out ulong h))
                            m[t[1]] = (g, ti, h);
                    }
                }
                catch { m.Clear(); }
            }
            public ulong Abdruck(FileInfo fi, string name)
            {
                long g = fi.Length, ti = fi.LastWriteTimeUtc.Ticks;
                lock (m) if (m.TryGetValue(name, out var e) && e.g == g && e.t == ti) { Treffer++; return e.h; }
                ulong h = Inhalt(fi.FullName, name, g, out long n);
                lock (m) { m[name] = (g, ti, h); geaendert = true; Gerechnet++; Gelesen += n; }
                return h;
            }
            public void Speichern()
            {
                lock (m)
                {
                    if (!geaendert || datei == null) return;
                    try
                    {
                        var sb = new System.Text.StringBuilder();
                        string v = Verfahren.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        foreach (var kv in m)
                            sb.Append(v).Append('\t').Append(kv.Key).Append('\t').Append(kv.Value.g.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                              .Append(kv.Value.t.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\t').Append(kv.Value.h.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                        string tmp = datei + ".tmp"; File.WriteAllText(tmp, sb.ToString());
                        if (File.Exists(datei)) File.Delete(datei);
                        File.Move(tmp, datei); geaendert = false;
                    }
                    catch { }   // Merker ist nur Ersparnis
                }
            }
        }
    }

    /// <summary>XXH64 (Yann Collet, Spezifikation github.com/Cyan4973/xxHash doc/xxhash_spec.md), fortlaufend. Sollwerte im Test aus der
    /// Python-Bibliothek xxhash 4.0.2 (unabhaengige Umsetzung). Woerter per Buffer.BlockCopy in ulong[] (Byte-Reihenfolge der Maschine:
    /// little-endian auf allen Zielen; auf big-endian kaeme ein anderer Wert heraus = Tabelle abgelehnt, nie falsch angenommen).</summary>
    internal sealed class Xxh64
    {
        const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL, P3 = 1609587929392839161UL, P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;
        ulong v1, v2, v3, v4, gesamt; readonly ulong seed;
        readonly byte[] mem = new byte[32]; int memN;
        readonly ulong[] w = new ulong[1 << 15];
        public Xxh64(ulong seed = 0) { this.seed = seed; v1 = seed + P1 + P2; v2 = seed + P2; v3 = seed; v4 = seed - P1; }
        static ulong Rotl(ulong x, int r) => (x << r) | (x >> (64 - r));
        static ulong Runde(ulong acc, ulong x) { acc += x * P2; acc = (acc << 31) | (acc >> 33); return acc * P1; }
        static ulong Mischen(ulong acc, ulong v) { acc ^= Runde(0, v); return acc * P1 + P4; }
        public void Update(byte[] b, int off, int n)
        {
            gesamt += (ulong)n;
            if (memN > 0)
            {
                int k = Math.Min(32 - memN, n); Buffer.BlockCopy(b, off, mem, memN, k); memN += k; off += k; n -= k;
                if (memN < 32) return;
                Buffer.BlockCopy(mem, 0, w, 0, 32); Streifen(1); memN = 0;
            }
            while (n >= 32)
            {
                int s = Math.Min(n / 32, w.Length / 4);
                Buffer.BlockCopy(b, off, w, 0, s * 32); Streifen(s); off += s * 32; n -= s * 32;
            }
            if (n > 0) { Buffer.BlockCopy(b, off, mem, 0, n); memN = n; }
        }
        void Streifen(int s)
        {
            ulong a = v1, c = v2, d = v3, e = v4; var x = w; int end = 4 * s;
            for (int i = 0; i < end; i += 4)
            {
                a += x[i] * P2; a = (a << 31) | (a >> 33); a *= P1;
                c += x[i + 1] * P2; c = (c << 31) | (c >> 33); c *= P1;
                d += x[i + 2] * P2; d = (d << 31) | (d >> 33); d *= P1;
                e += x[i + 3] * P2; e = (e << 31) | (e >> 33); e *= P1;
            }
            v1 = a; v2 = c; v3 = d; v4 = e;
        }
        public ulong Digest()
        {
            ulong h;
            if (gesamt >= 32)
            {
                h = Rotl(v1, 1) + Rotl(v2, 7) + Rotl(v3, 12) + Rotl(v4, 18);
                h = Mischen(h, v1); h = Mischen(h, v2); h = Mischen(h, v3); h = Mischen(h, v4);
            }
            else h = seed + P5;
            h += gesamt;
            int p = 0;
            for (; p + 8 <= memN; p += 8) { h ^= Runde(0, BitConverter.ToUInt64(mem, p)); h = Rotl(h, 27) * P1 + P4; }
            if (p + 4 <= memN) { h ^= BitConverter.ToUInt32(mem, p) * P1; h = Rotl(h, 23) * P2 + P3; p += 4; }
            for (; p < memN; p++) { h ^= mem[p] * P5; h = Rotl(h, 11) * P1; }
            h ^= h >> 33; h *= P2; h ^= h >> 29; h *= P3; h ^= h >> 32;
            return h;
        }
    }

    /// <summary>wegtabelle: Wert, der einem Ziel mit begrenzter Rate folgt (dB/s; "lauter" und "leiser" getrennt). Annahme statt Messung:
    /// lauter 60 dB/s (Bot mit 5 m/s quert eine ~1 m breite Fresnel-Zone mit ~12 dB Schattentiefe in 0,2 s), leiser 15 dB/s (verdeckt
    /// Stufen beim Wechsel der Sondenzelle) - beides Regler fuer den Tester.</summary>
    internal static class TabGleitKern
    {
        /// <summary>wert folgt ziel je Band hoechstens rauf*dt nach oben und runter*dt nach unten. Erster Wert: sofort; dt &lt;= 0 (zweite Abfrage
        /// im selben Bild): unveraendert.</summary>
        public static void Folgen(double[] wert, double[] ziel, double dt, double rauf, double runter, bool erster)
        {
            for (int b = 0; b < wert.Length; b++)
            {
                if (erster || double.IsNaN(wert[b])) { wert[b] = ziel[b]; continue; }
                if (dt <= 0) continue;
                double d = ziel[b] - wert[b];
                if (d > 0) wert[b] += Math.Min(d, rauf * dt); else wert[b] -= Math.Min(-d, runter * dt);
            }
        }
    }
}
