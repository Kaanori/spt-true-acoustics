using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // 0.8.5 Leistung (Interchange, grosses Bot-Gefecht 08.11.2026: SpatialAudioSystem.LateUpdate 26-28 ms je Bild, ruhig 0,1 ms).
    // Ursache: Raumweg.Rechnen lief je Klang nach jedem PropagationJob ueber ALLE Routen des Raumpaars (Log: 22404 Kandidaten
    // in 140 Rechnungen = 160 je Rechnung, bis 350) und rechnete fuer jedes Portal jeder Route die Absorptionsflaeche der Raeume
    // neu - darin fuer JEDE Oeffnung des Raums GetPortalSize (Collider.size, transform, localScale: drei Unity-Aufrufe),
    // PortalClosureLevel, GetInstanceID und zwei Woerterbuchzugriffe. Grosse Hallen haben Dutzende Oeffnungen: je Rechnung
    // Zehntausende Unity-Aufrufe, mehrere Rechnungen im selben Bild (Schuesse, Einschlaege, Schritte vieler Bots).
    // Loesung (dieselben Formeln, dieselben Werte):
    //  - Raumnetz einmal je Raid: je Portal Flaeche, Transmission, Raeume vorn/hinten; je Raum Oberflaeche, Volumen und seine
    //    Oeffnungen (dichte Indizes statt Woerterbuecher). Je Bild einmal die Schliessung aller Portale, die offene Flaeche und
    //    die Absorptionsflaeche je Raum hoechstens einmal je Bild.
    //  - Die Routenschleife ist reine Rechnung auf diesen Feldern (allokationsfrei, Offline-Test), Spiel-Teil liest nur noch die
    //    Job-Daten ein (Raumweg.cs).
    //  - Ergebnis-Wiederverwendung: gleiche Route (Raumpaar, Richtung, Quelle und Hoerer in derselben 1-cm-Zelle, gleiche
    //    Einstellungen) mit unveraendertem Zustand aller beteiligten Portale und Raeume -> die gespeicherten Kandidaten gehen
    //    unveraendert in den RoutenMischer (der Spielteil selbst rechnet erst nach 1 m Bewegung neu).
    // ------------------------------------------------------------------------------------------
    internal abstract class RaumwegNetz
    {
        public const int MaxPortale = 16;
        public const double ZelleM = 0.01;         // Toleranz der Wiederverwendung (Quelle und Hoerer, 1 cm: < 0,05 dB)
        public const int CacheMax = 512;

        // ---- je Portal (Index wie im Job / _indexMap), statisch je Raid
        public int PortalN;
        public bool[] HatObjekt = new bool[0];      // Portal-Objekt bekannt (sonst gilt die Schliessung aus den Job-Daten)
        public float[] Voll = new float[0];         // Mathf.Lerp(1, 0.1, _transmission)
        public double[] FlaecheP = new double[0];   // |Breite * Hoehe| (GetPortalSize)
        public int[] Vorn = new int[0], Hinten = new int[0];   // Raumindex oder -1
        // je Bild: Schliessung des Spiels (PortalClosureLevel)
        public float[] Schliessung = new float[0];
        // 0.8.6 (Tuer-Massengesetz): Daemmung des geschlossenen Blatts je Band (3 je Portal: Bezug / 1 kHz / 4 kHz, TuerKern.cs),
        // NaN = kein Blattmodell (Wert aus der Schliessung des Jobs wie bis 0.8.5). Spiel: einmal je Bild gefuellt.
        public double[] BlattDb = new double[0];
        /// <summary>Zaehlt jede Aenderung an BlattDb (Wiederverwendung gespeicherter Ergebnisse nur bei gleichem Stand).</summary>
        public int BlattStand;

        // ---- je Portal aus den Job-Daten (je Rechnung geladen)
        public RaumPortal[] JGeo = new RaumPortal[0];
        public float[] JZu = new float[0], JWand = new float[0];
        public int[] JGeoKey = new int[0];
        int[] jStempel = new int[0];
        double[] zZu = new double[0], zTau = new double[0], zTauM = new double[0], zTauH = new double[0]; bool[] zBand = new bool[0];
        int[] zStempel = new int[0];   // 0.8.6: Portalzustand je Rechnung
        int rechnung = 1;

        // ---- je Raum (dichter Index), statisch je Raid
        public int RaumN;
        public short[] RaumId = new short[0];
        public bool[] Draussen = new bool[0];
        public int[] PStart = new int[0], PAnz = new int[0], PListe = new int[0];
        double[] oberflaeche = new double[0], volumen = new double[0];
        bool[] gemessen = new bool[0];
        // je Bild
        double[] offen = new double[0], aWert = new double[0];
        float[] aAlpha = new float[0];
        int[] offenBild = new int[0], aBild = new int[0];
        int bild = int.MinValue;

        // Diagnose
        public int DRechnungen, DTreffer, DFehl, DRouten, DRaumA, DRaumAGespart;

        /// <summary>Oberflaeche (m2) und Volumen (m3) eines Innenraums messen (Spiel: Collider-Boxen). Einmal je Raid und Raum.</summary>
        protected abstract void Messen(int raum, out double flaeche, out double vol);

        /// <summary>tankhall: gemessener mittlerer Absorptionsgrad der Waende des Raums (Spiel: HallZonen-Strahlen, BSG-Tabelle, 1 kHz);
        /// NaN = unbekannt -> der Pauschalwert der Einstellung "Room paths: room absorption".</summary>
        protected virtual double WandAlpha(int raum) => double.NaN;

        /// <summary>raumecke: Kaesten (Boxen) eines Raums (Spiel: BoxCollider der AudioTriggerAreas wie HallZonen); null = unbekannt
        /// (dann gerade wie bisher).</summary>
        protected virtual Kasten[] KaestenVon(int raum) => null;
        /// <summary>raumecke: freie Linie durch die Geometrie (Spiel: Physics.Linecast); null = nicht pruefen.</summary>
        protected virtual ILinie Linie => null;

        // ---- raumecke (16.11.): Wege in mehrteiligen Raeumen ueber die Kastenuebergaenge (RaumEckeKern)
        public static bool EckenAn = true;
        RaumEcken[] eckRaum = new RaumEcken[0]; bool[] eckGebaut = new bool[0];
        readonly V3[] eckPkt = new V3[(MaxPortale + 1) * RaumEckeKern.EckenMax];
        readonly int[] eckStart = new int[MaxPortale + 2], segRaum = new int[MaxPortale + 1];
        readonly V3[] eckTmp = new V3[RaumEckeKern.EckenMax];
        // Merker je (Raum, Anfang, Ende auf 0,25 m): Ecken (gleiche Endpunkte ergeben dieselben Ecken)
        const int EckMerkN = 4096;
        readonly long[] eckKey = new long[EckMerkN]; readonly byte[] eckZahl = new byte[EckMerkN]; readonly V3[] eckWert = new V3[EckMerkN * RaumEckeKern.EckenMax];
        public int DEckenRouten, DEckenStuecke, DEckenTreffer; public long DEckenTicks;
        public long DEckenBauTicks; public int DEckenRaeume;

        RaumEcken EckenVon(int raum)
        {
            if (raum < 0) return null;
            if (raum >= eckRaum.Length) { int k = Math.Max(raum + 1, Math.Max(16, 2 * eckRaum.Length)); Array.Resize(ref eckRaum, k); Array.Resize(ref eckGebaut, k); }
            if (!eckGebaut[raum])
            {
                eckGebaut[raum] = true; long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var ks = raum < RaumN && !Draussen[raum] ? KaestenVon(raum) : null;
                eckRaum[raum] = ks != null && ks.Length > 1 ? RaumEckeKern.Bauen(ks, ks.Length, Linie) : null;
                DEckenBauTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; if (eckRaum[raum] != null) DEckenRaeume++;
            }
            return eckRaum[raum];
        }

        /// <summary>raumecke: Kastengraphen aller bekannten Raeume jetzt bauen (Aufwaermen beim Raid-Start statt beim ersten Klang).</summary>
        public void EckenVorbauen() { for (int r = 0; r < RaumN; r++) EckenVon(r); }

        static long Q4(double x) => (long)Math.Floor(x * 4) & 0xFFF;
        // je (Raum, Portal) ein Baum der kuerzesten Wege von der Portalmitte (einmal je Raid)
        readonly Dictionary<long, EckBaum> eckBaeume = new Dictionary<long, EckBaum>();
        EckBaum BaumVon(int raum, int portal, RaumEcken re)
        {
            long k = ((long)raum << 32) | (uint)portal;
            if (eckBaeume.TryGetValue(k, out var t)) return t;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var p = JGeo[portal]; t = RaumEckeKern.Baum(re, p.Mitte, Linie, p.Normale); eckBaeume[k] = t;   // tabellenneu: Vorpunkt entlang der Normalen
            DEckenBauTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; DEckenBaeume++;
            return t;
        }
        public int DEckenBaeume;
        /// <summary>Stueck zwischen Portal (Index portal, Punkt am Portal) und dem beweglichen Ende (Quelle oder Hoerer); portalZuerst = Portal
        /// liegt am Anfang des Stuecks (Hoererraum), sonst am Ende (Quellraum). Ecken in Wegrichtung nach eckPkt[ziel..].</summary>
        int EckenStueck(int raum, int portal, V3 amPortal, V3 ende, bool portalZuerst, int ziel)
        {
            var re = EckenVon(raum); if (re == null || portal < 0) return 0;
            V3 a = portalZuerst ? amPortal : ende, b = portalZuerst ? ende : amPortal;
            ulong key = (ulong)(raum & 0x3FF) * 0x9E3779B97F4A7C15UL;
            key ^= (ulong)(Q4(a.X) | Q4(a.Y) << 12 | Q4(a.Z) << 24 | Q4(b.X) << 36 | Q4(b.Y) << 48) ^ ((ulong)Q4(b.Z) << 52) * 0xC2B2AE3D27D4EB4FUL;
            long k = (long)key; int h = (int)((key ^ (key >> 29)) % EckMerkN);
            if (eckKey[h] == k && eckZahl[h] != 255)
            {
                int z = eckZahl[h]; for (int i = 0; i < z; i++) eckPkt[ziel + i] = eckWert[h * RaumEckeKern.EckenMax + i]; DEckenTreffer++; return z;
            }
            var baum = BaumVon(raum, portal, re);
            int n = RaumEckeKern.WegBaum(re, baum, amPortal, ende, eckTmp, RaumEckeKern.EckenMax, Linie);   // Ecken Portal -> Ende
            if (!portalZuerst) Array.Reverse(eckTmp, 0, n);
            eckKey[h] = k; eckZahl[h] = (byte)n;
            for (int i = 0; i < n; i++) { eckWert[h * RaumEckeKern.EckenMax + i] = eckTmp[i]; eckPkt[ziel + i] = eckTmp[i]; }
            return n;
        }

        /// <summary>raumecke: Ecken aller Raumstuecke der Route (echte Richtung, pkt geladen) nach eckPkt/eckStart; Rueckgabe Gesamtzahl.</summary>
        int EckenRechnen(in Eingabe ein, int m, V3 quelle, V3 hoerer)
        {
            if (!EckenAn) { for (int k = 0; k <= m + 1; k++) eckStart[k] = 0; return 0; }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            // Raum je Stueck in Job-Richtung, dann in echte Richtung
            segRaum[0] = ein.RaumA; for (int i = 1; i < m; i++) segRaum[i] = raumIdx[i]; segRaum[m] = ein.RaumB;
            if (ein.Umgekehrt) Array.Reverse(segRaum, 0, m + 1);
            // nur Quellraum (Quelle -> erste Oeffnung) und Hoererraum (letzte Oeffnung -> Hoerer): dort haengen Austritt/Richtung und die
            // Direktleistung in die erste Oeffnung daran; in den Raeumen dazwischen traegt fast nur das Hallfeld (Diffusanteil ~ 99 %)
            int z = 0;
            for (int k = 0; k <= m; k++)
            {
                eckStart[k] = z;
                // tabellenneu (Messung Factory 16.11. 19:37): Quellraum-Ecken (z. B. Area_1, Kaesten mit Luecke unter Buehnen) kippten den gerichteten
                // Anteil zu anderen Routen (+900 grobe Fehlgriffe) -> nur noch der Hoererraum (dort haengt der Austritt = Richtung dran)
                if (raumDraussen[k] || k != m) continue;
                int n = k == 0 ? EckenStueck(segRaum[0], idx[0], pkt[0], quelle, false, z) : EckenStueck(segRaum[m], idx[m - 1], pkt[m - 1], hoerer, true, z);
                z += n; if (n > 0) DEckenStuecke++;
            }
            eckStart[m + 1] = z;
            if (z > 0) DEckenRouten++;
            DEckenTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            return z;
        }
        /// <summary>raumecke: letzter Wegpunkt vor dem Hoerer (Austritt fuer die Richtung) und der davor (Punkt 2: fuer die Nahblende vor
        /// Durchgaengen; default = keiner).</summary>
        V3 AustrittPunkt(int m, out V3 vorher)
        {
            int z = eckStart[m + 1], s = eckStart[m];
            if (z > s) { vorher = z - s >= 2 ? eckPkt[z - 2] : pkt[m - 1]; return eckPkt[z - 1]; }
            vorher = eckStart[m] > eckStart[m - 1] ? eckPkt[eckStart[m] - 1] : (m >= 2 ? pkt[m - 2] : default);
            return pkt[m - 1];
        }
        public V3 BestAus, BestVorAus; public int BestEcken; public double BestEckDb;

        // ---------------------------------------------------------------- Aufbau

        public void PortaleAnlegen(int n)
        {
            PortalN = n;
            Array.Resize(ref HatObjekt, n); Array.Resize(ref Voll, n); Array.Resize(ref FlaecheP, n);
            Array.Resize(ref Vorn, n); Array.Resize(ref Hinten, n); Array.Resize(ref Schliessung, n); Array.Resize(ref BlattDb, 3 * n);
            for (int i = 0; i < n; i++) { HatObjekt[i] = false; Voll[i] = 1f; FlaecheP[i] = 0; Vorn[i] = Hinten[i] = -1; Schliessung[i] = 0f; }
            for (int i = 0; i < BlattDb.Length; i++) BlattDb[i] = double.NaN;
            BlattStand++;
            JobAnlegen(n);
            bild = int.MinValue;
            Leeren(); StatikLeeren();
        }

        void JobAnlegen(int n)
        {
            if (JGeo.Length >= n) return;
            int k = Math.Max(n, 16);
            Array.Resize(ref JGeo, k); Array.Resize(ref JZu, k); Array.Resize(ref JWand, k); Array.Resize(ref JGeoKey, k); Array.Resize(ref jStempel, k);
            Array.Resize(ref zZu, k); Array.Resize(ref zTau, k); Array.Resize(ref zTauM, k); Array.Resize(ref zTauH, k); Array.Resize(ref zBand, k);
            Array.Resize(ref zStempel, k);
        }

        public void RaeumeAnlegen(int n, int portalEintraege)
        {
            RaumN = n;
            Array.Resize(ref RaumId, n); Array.Resize(ref Draussen, n); Array.Resize(ref PStart, n); Array.Resize(ref PAnz, n);
            Array.Resize(ref PListe, Math.Max(portalEintraege, 1));
            Array.Resize(ref oberflaeche, n); Array.Resize(ref volumen, n); Array.Resize(ref gemessen, n);
            Array.Resize(ref offen, n); Array.Resize(ref aWert, n); Array.Resize(ref aAlpha, n); Array.Resize(ref offenBild, n); Array.Resize(ref aBild, n);
            for (int i = 0; i < n; i++) { gemessen[i] = false; offenBild[i] = aBild[i] = int.MinValue; PAnz[i] = 0; PStart[i] = 0; }
            bild = int.MinValue;
            Leeren(); StatikLeeren();
        }

        /// <summary>Raum ohne eigene Oeffnungen nachtragen (Quell-/Hoererraum, der an keinem Portal haengt). Rueckgabe: Index.</summary>
        public int RaumDazu(short id, bool draussen)
        {
            int n = RaumN + 1;
            if (n > RaumId.Length)
            {
                int k = Math.Max(16, RaumId.Length * 2);
                Array.Resize(ref RaumId, k); Array.Resize(ref Draussen, k); Array.Resize(ref PStart, k); Array.Resize(ref PAnz, k);
                Array.Resize(ref oberflaeche, k); Array.Resize(ref volumen, k); Array.Resize(ref gemessen, k);
                Array.Resize(ref offen, k); Array.Resize(ref aWert, k); Array.Resize(ref aAlpha, k); Array.Resize(ref offenBild, k); Array.Resize(ref aBild, k);
            }
            int r = RaumN; RaumN = n;
            RaumId[r] = id; Draussen[r] = draussen; PStart[r] = 0; PAnz[r] = 0; gemessen[r] = false; offenBild[r] = aBild[r] = int.MinValue;
            return r;
        }

        /// <summary>Neues Bild: Werte "je Bild" verfallen (Spiel: danach Schliessung[] fuer alle Portale fuellen).</summary>
        public bool BildSetzen(int b)
        {
            if (b == bild) return false;
            bild = b;
            return true;
        }

        /// <summary>Vor dem Einlesen der Job-Daten einer Rechnung.</summary>
        public void RechnungBeginn(int portalAnzahlJob)
        {
            JobAnlegen(portalAnzahlJob);
            if (++rechnung == int.MaxValue) { rechnung = 1; Array.Clear(jStempel, 0, jStempel.Length); Array.Clear(zStempel, 0, zStempel.Length); }
        }

        /// <summary>Portal-Objekt zum Job-Index bekannt (wie 'pi &lt; portalJeIndex.Length ? portalJeIndex[pi] : null' != null).</summary>
        public bool Obj(int pi) => pi < PortalN && HatObjekt[pi];

        public bool JobGeladen(int pi) => pi < jStempel.Length && jStempel[pi] == rechnung;

        /// <summary>0.8.6: Blatt-Daemmung eines Portals je Band setzen (NaN = kein Blattmodell); zaehlt BlattStand nur bei Aenderung.</summary>
        public void BlattSetzen(int pi, double d0, double d1, double d2)
        {
            if (pi < 0 || 3 * pi + 2 >= BlattDb.Length) return;
            int k = 3 * pi;
            if (Gleich(BlattDb[k], d0) && Gleich(BlattDb[k + 1], d1) && Gleich(BlattDb[k + 2], d2)) return;
            BlattDb[k] = d0; BlattDb[k + 1] = d1; BlattDb[k + 2] = d2;
            BlattStand++;
        }

        static bool Gleich(double a, double b) => a == b || (double.IsNaN(a) && double.IsNaN(b));

        /// <summary>0.8.6: Daemmung des geschlossenen Blatts im Band b (NaN = kein Blattmodell).</summary>
        public double Blatt(int pi, int b) => Obj(pi) && 3 * pi + b < BlattDb.Length ? BlattDb[3 * pi + b] : double.NaN;

        /// <summary>
        /// 0.8.6: Baender einer Route (Bezug / 1 kHz / 4 kHz). Beugung und Raeume je Band physikalisch (Route). Geschlossene Tueren/
        /// Fenster: mit Blattmodell stecken ihre Baender schon in DbMitte/DbHoch (Massengesetz je Band); ohne (TuerBaender = false)
        /// zusaetzlich die Tiefpass-Abbildung des Physik-Modus wie bis 0.8.5.
        /// </summary>
        public static void Baender(in RaumwegErgebnis e, float dyn, float tiefpassMin, double fRef, double f1, double f2, out double d0, out double d1, out double d2)
        {
            double x1 = e.DbMitte - e.Db, x2 = e.DbHoch - e.Db;
            if (!e.TuerBaender)
            {
                float lpTuer = Abbildung(Clamp01f((float)(e.DbTuer / dyn)), tiefpassMin);
                double t0 = WegeKern.MehrDb(lpTuer, fRef);
                x1 = Math.Max(x1, WegeKern.MehrDb(lpTuer, f1) - t0);
                x2 = Math.Max(x2, WegeKern.MehrDb(lpTuer, f2) - t0);
            }
            // 10.11. (klangfarbe): nicht je Band bei Max. insulation kappen (Route liefert echte Werte bis WegeKern.BandGrenzeDb); der
            // Deckel gilt erst fuer die Lautstaerke (Raumweg.Schreiben, WegeKern.Deckeln). dyn nur noch als Skala der Abbildung.
            d0 = e.Db; d1 = e.Db + Math.Max(0, x1); d2 = e.Db + Math.Max(0, x2);
        }

        /// <summary>Daten eines Portals aus dem Job (center/right/up/normal/halfSize, closureLevel, Wandfaktor, Geometrie-Schluessel).</summary>
        public void JobSetzen(int pi, in RaumPortal geo, float zu, float wand, int geoKey)
        {
            JGeo[pi] = geo; JZu[pi] = zu; JWand[pi] = wand; JGeoKey[pi] = geoKey; jStempel[pi] = rechnung;
        }

        // ---------------------------------------------------------------- Raeume

        static float Clamp01f(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        /// <summary>Geschlossener Anteil eines Portals aus der Schliessung des Spiels (wie 0.8.4/0.8.5: closure / Lerp(1, 0.1, transmission)).</summary>
        public double ZuSpiel(int pi)
        {
            float voll = Voll[pi];
            return voll > 0.001f ? Clamp01f(Schliessung[pi] / voll) : 0f;
        }

        /// <summary>Offene Flaeche (m2) aller Oeffnungen eines Raums (einmal je Bild).</summary>
        public double OffeneFlaeche(int raum)
        {
            if (offenBild[raum] == bild) return offen[raum];
            double summe = 0;
            int s = PStart[raum], e = s + PAnz[raum];
            for (int i = s; i < e; i++)
            {
                int pi = PListe[i];
                summe += FlaecheP[pi] * (1.0 - ZuSpiel(pi));
            }
            offen[raum] = summe; offenBild[raum] = bild;
            return summe;
        }

        bool Gemessen(int raum)
        {
            if (!gemessen[raum])
            {
                Messen(raum, out oberflaeche[raum], out volumen[raum]);
                gemessen[raum] = true;
            }
            return oberflaeche[raum] > 0;
        }

        /// <summary>Absorptionsflaeche eines Innenraums (wie Raumweg.RaumFlaeche bis 0.8.5): -1 = im Freien/unbekannt.</summary>
        public double RaumFlaeche(int raum, float alpha, out double vol)
        {
            vol = 0;
            if (raum < 0 || Draussen[raum]) return -1;
            if (!Gemessen(raum)) return -1;
            vol = volumen[raum];
            DRaumA++;
            if (aBild[raum] == bild && aAlpha[raum] == alpha) { DRaumAGespart++; return aWert[raum]; }
            double aw = WandAlpha(raum);
            double a = RaumwegKern.Absorption(oberflaeche[raum], double.IsNaN(aw) ? alpha : aw, OffeneFlaeche(raum));
            aWert[raum] = a; aAlpha[raum] = alpha; aBild[raum] = bild;
            return a;
        }

        // Raum hinter dem Portal (wie Raumweg.RaumNach): der andere als der davor; passt keiner, der mit dem anderen
        // Drinnen/Draussen-Zustand.
        double RaumNach(int pi, ref short vorRaum, ref bool vorDraussen, float alpha, out double vol, out int nach)
        {
            vol = 0; nach = -1;
            if (!Obj(pi)) return -1;
            int f = Vorn[pi], bk = Hinten[pi];
            if (f >= 0 && RaumId[f] == vorRaum) nach = bk;
            else if (bk >= 0 && RaumId[bk] == vorRaum) nach = f;
            else if (f >= 0 && bk >= 0) nach = Draussen[f] != vorDraussen ? f : bk;
            else nach = bk >= 0 ? bk : f;
            if (nach < 0) return -1;
            vorRaum = RaumId[nach]; vorDraussen = Draussen[nach];
            return RaumFlaeche(nach, alpha, out vol);
        }

        // ---------------------------------------------------------------- Routen

        /// <summary>0.8.6 (Meldung Ground Zero 09.11.2026 21:34 MARK 6/7, V-Ex-Taxi 155 m): Abschirmung einer Strecke im Freien
        /// (ISO 9613-2 ueber das Hoehenprofil, Spielteil: Ersatzweg.Profil). a/b = Endpunkte, vonPortal/bisPortal = Portalindex des
        /// Endpunkts oder -1 (Quelle/Hoerer) - Schluessel fuer den Cache des Spielteils.</summary>
        public interface IAussenSchirm { bool Strecke(V3 a, V3 b, int vonPortal, int bisPortal, out Hindernis h); }

        /// <summary>Eingabe einer Rechnung (Job-Richtung: A = Emitter des Raumpaars).</summary>
        public struct Eingabe
        {
            public V3 A, B;
            public bool Umgekehrt;
            public uint Paar;
            public int RaumA, RaumB;          // Raumindex oder -1
            public short RaumAId;             // ID des Raums A oder -1
            public bool RaumADraussen;
            public float Dyn, Alpha, TiefpassMin;
            public double FRef, FHoch;
            public float Fit1, Fit2;
            public int RoutenGesamt;          // Laenge der Routenliste (Schluessel)
            public int RangN;                 // 0.8.6: Mischer-Einstellung (weicher Rang N, 0 = alle voll) fuer das Kappen
            public double Breite;             // 0.8.6: Rang-Breite (dB), 0 = harte Auswahl
            public IAussenSchirm Schirm;      // 0.8.6: Strecken im Freien abschirmen (null = wie bisher: Sichtlinie im Freien angenommen)
        }

        // Routen in Job-Reihenfolge: Start in RIdx, Anzahl (-1 = ungueltig, wie 'continue' im Spielteil)
        public int[] RStart = new int[64], RN = new int[64], RIdx = new int[256];

        // Arbeitsfelder (allokationsfrei)
        readonly RaumPortal[] rp = new RaumPortal[MaxPortale];
        readonly V3[] pkt = new V3[MaxPortale], pktJob = new V3[MaxPortale];
        readonly double[] raumA = new double[MaxPortale + 1], raumV = new double[MaxPortale + 1], beug = new double[MaxPortale], umweg = new double[MaxPortale];
        readonly int[] idx = new int[MaxPortale], geo = new int[MaxPortale], raumIdx = new int[MaxPortale + 1];
        readonly bool[] raumDraussen = new bool[MaxPortale + 1];   // 0.8.6: Raum k (vor Portal k) im Freien

        // Lauteste Route (Klang-Log)
        public RaumwegErgebnis Best;
        public int BestN, BestRoute = -1;
        public readonly int[] BestIdx = new int[MaxPortale];
        public readonly double[] BestA = new double[MaxPortale + 1], BestV = new double[MaxPortale + 1];
        public double BestOmega, BestR1;
        public readonly double[] BestBeug = new double[MaxPortale], BestUm = new double[MaxPortale], BestOffen = new double[MaxPortale], BestTauDb = new double[MaxPortale];

        /// <summary>Alle Routen [0, anzahl) rechnen und als Kandidaten in den Mischer geben (mix.Beginn() inklusive).
        /// logDetails = lauteste Route fuer das Klang-Log merken (0.8.6: auch dann mit Wiederverwendung, die Details liegen im
        /// Eintrag). routen = exakt gerechnete Kandidaten. Rueckgabe: mindestens eine gueltige Route.</summary>
        public bool Rechne(in Eingabe ein, int anzahl, RoutenMischer mix, bool logDetails, out int routen, out bool treffer)
        {
            routen = 0; treffer = false;
            DRechnungen++;
            if (ein.Schirm != null) AussenKuerzen(anzahl);
            ulong schluessel = Schluessel(in ein, anzahl);
            if (tabelle.TryGetValue(schluessel, out var alt) && alt.Passt(in ein, anzahl, this) && (!logDetails || alt.MitBest))
            {
                treffer = true; DTreffer++;
                mix.Beginn();
                for (int i = 0; i < alt.KN; i++)
                {
                    ref var k = ref alt.K[i];
                    mix.Kandidat(k.Id, k.D0, k.D1, k.D2, k.Aus, k.Erg, alt.KPortale, k.N, k.Start);
                }
                if (logDetails) alt.BestLaden(this);
                routen = alt.Routen;
                DRouten += routen;
                return alt.Hat;
            }
            DFehl++;
            if (alt == null)
            {
                if (tabelle.Count >= CacheMax) Leeren();
                alt = frei.Count > 0 ? frei.Pop() : new Eintrag();
                tabelle[schluessel] = alt;
            }
            alt.Beginn(in ein, anzahl);
            alt.BlattStand = BlattStand;
            if (pMerk.Length < JGeo.Length) Array.Resize(ref pMerk, JGeo.Length);
            if (++merkNr == int.MaxValue) { merkNr = 1; Array.Clear(pMerk, 0, pMerk.Length); Array.Clear(rMerk, 0, rMerk.Length); }

            float dyn = ein.Dyn;
            double fRef = ein.FRef;
            V3 a = ein.A, b = ein.B;
            V3 quelle = ein.Umgekehrt ? b : a, hoerer = ein.Umgekehrt ? a : b;
            bool hat = false; double bestDb = 0;
            BestRoute = -1;
            mix.Beginn();
            float fit1 = ein.Fit1, fit2 = ein.Fit2;

            // 0.8.6 Leistung (Ground Zero 09.11. 15:13-15:16: Raumpaare mit bis zu 2539 Routen, je Rechnung bis 20 ms): der Mischer
            // gibt einer Route nur Gewicht, wenn sie unter den N+1 lautesten liegt oder hoechstens Breite/2 unter der Mitte r zwischen
            // der N-ten und (N+1)-ten (RoutenMischer.Gewichte; harte Auswahl: unter den N lautesten). Jede andere Route hat Gewicht 0
            // und aendert weder r noch die Raenge der gewichteten. Eine Route, deren untere Schranke der Daemmung schon schlechter ist
            // als die (N+1)-te bisher gerechnete und als r + Breite/2, wird daher nicht exakt gerechnet - Pegel, Gewichte und Summe des
            // Mischers bleiben dieselben (RechneGekappt).
            int rangN = ein.RangN;
            bool kappen = rangN > 0 && rangN <= MaxRangN && anzahl > 2 * (rangN + 1);
            if (kappen) hat = RechneGekappt(in ein, anzahl, mix, alt, logDetails, quelle, hoerer, ref routen);
            else
            for (int r = 0; r < anzahl; r++)
            {
                int m = Vorbereiten(in ein, r, alt);
                if (m == 0) continue;
                // Stabile Routen-ID aus der Portalfolge, ueber die Geometrie der Portale (doppelt eingetragene Portale = eine Route)
                ulong id = RoutenMischer.Id(ein.Paar, geo, m);
                // in echte Richtung (Quelle -> Hoerer) bringen
                if (ein.Umgekehrt) Umdrehen(m);
                for (int i = 0; i < m; i++) pkt[i] = pktJob[i];
                int ez = EckenRechnen(in ein, m, quelle, hoerer);
                var e = RaumwegKern.Route(quelle, hoerer, rp, pkt, m, raumA, fRef, ein.FHoch, WegeKern.BandGrenzeDb, beug, umweg, raumV, ez > 0 ? eckPkt : null, eckStart);   // 10.11.: Baender ungekappt
                V3 aus = AustrittPunkt(m, out V3 vorAus); e.Vor = vorAus;   // tabellenneu: Wegpunkt vor dem Austritt fuer die Nahblende
                routen++;
                if (!e.Gueltig) continue;
                // Baender (Bezug / 1 kHz / 4 kHz): Beugung physikalisch je Band, geschlossene Tueren/Fenster nach ihrem Massengesetz
                Baender(in e, dyn, ein.TiefpassMin, fRef, fit1, fit2, out double d0, out double d1, out double d2);
                Schirmen(in ein, m, quelle, hoerer, ref e, ref d0, ref d1, ref d2);
                mix.Kandidat(id, d0, d1, d2, aus, e, idx, m);
                alt.Kandidat(id, d0, d1, d2, aus, in e, idx, m);
                if (!hat || e.Db < bestDb)
                {
                    hat = true; bestDb = e.Db; Best = e; BestN = m; BestRoute = r;
                    if (logDetails)
                    {
                        for (int i = 0; i < m; i++) { BestIdx[i] = idx[i]; BestBeug[i] = beug[i]; BestUm[i] = umweg[i]; BestOffen[i] = rp[i].Offen; BestTauDb[i] = -10 * Math.Log10(Math.Max(rp[i].Tau, 1e-12)); }
                        for (int i = 0; i <= m; i++) { BestA[i] = raumA[i]; BestV[i] = raumV[i]; }
                        BestR1 = V3.Abstand(quelle, pkt[0]); BestOmega = RaumwegKern.Raumwinkel(rp[0], quelle) / (4 * Math.PI) + RaumwegKern.QuellDiffus(rp[0], 1.0, raumA, raumV, Math.Max(BestR1, 0.5), 0);   // Gesamtreview A3: mit Diffusfeld
                        BestAus = aus; BestVorAus = vorAus; BestEcken = e.Ecken; BestEckDb = e.DbEckenMitte;
                    }
                }
            }
            DRouten += routen;
            alt.Hat = hat; alt.Routen = routen; alt.Fertig = true;
            if (logDetails) alt.BestMerken(this); else alt.MitBest = false;
            return hat;
        }

        // 0.8.6: Portaldaten einer Route in Job-Richtung laden (rp, pktJob, idx, geo, raumA, raumV, raumIdx); merken = Zustand fuer
        // die Wiederverwendung eintragen. Rueckgabe: Zahl der Portale (0 = Route ungueltig).
        int Vorbereiten(in Eingabe ein, int r, Eintrag merken)
        {
            int n = RN[r];
            if (n <= 0) return 0;
            int s0 = RStart[r];
            float dyn = ein.Dyn, alpha = ein.Alpha;
            V3 b = ein.B;
            // Portalpunkte genau wie der Job (Richtung A -> B, Austritt immer B)
            V3 eintritt = ein.A; int m = 0;
            short vorRaum = ein.RaumAId; bool vorDraussen = ein.RaumADraussen;
            raumDraussen[0] = ein.RaumA >= 0 && ein.RaumA < RaumN ? Draussen[ein.RaumA] : ein.RaumADraussen;
            for (int i = 0; i < n; i++)
            {
                int pi = RIdx[s0 + i];
                var p = JGeo[pi];
                V3 punkt = RaumwegKern.PortalPunkt(p, eintritt, b);
                if (punkt.IstNull) continue;
                // Zustand: offener Anteil aus der Schliessung des Spiels, geschlossener Teil mit dem Wert der Mod (Abschnitt 4).
                // 0.8.6: je Portal einmal je Rechnung (haengt nur vom Portal ab, nicht von der Route)
                float jz = JZu[pi];
                ZustandLaden(pi, dyn);
                double zu = zZu[pi];
                p.Offen = 1.0 - zu;
                p.Tau = zTau[pi]; p.TauMitte = zTauM[pi]; p.TauHoch = zTauH[pi]; p.Baender = zBand[pi];
                rp[m] = p; pktJob[m] = punkt; idx[m] = pi; geo[m] = JGeoKey[pi];
                // Raum hinter dem Portal (in Job-Richtung)
                raumA[m + 1] = RaumNach(pi, ref vorRaum, ref vorDraussen, alpha, out raumV[m + 1], out raumIdx[m + 1]);
                raumDraussen[m + 1] = vorDraussen;
                // 0.8.6: je Portal nur einmal je Rechnung (bisher lineare Suche im Eintrag je Portal jeder Route: bei 2500 Routen
                // ueber Hunderte Portale quadratisch)
                if (merken != null && pMerk[pi] != merkNr) { pMerk[pi] = merkNr; merken.PortalMerken(pi, zu, jz); }
                eintritt = punkt; m++;
            }
            if (m == 0) return 0;
            raumA[0] = RaumFlaeche(ein.RaumA, alpha, out raumV[0]);
            raumA[m] = RaumFlaeche(ein.RaumB, alpha, out raumV[m]);
            if (ein.RaumB >= 0 && ein.RaumB < RaumN) raumDraussen[m] = Draussen[ein.RaumB];
            if (merken != null)
            {
                for (int i = 1; i < m; i++) RaumMerken(merken, raumIdx[i], raumA[i]);
                RaumMerken(merken, ein.RaumA, raumA[0]); RaumMerken(merken, ein.RaumB, raumA[m]);
            }
            return m;
        }

        void RaumMerken(Eintrag merken, int raum, double a)
        {
            if (raum < 0) return;
            if (raum >= rMerk.Length) Array.Resize(ref rMerk, Math.Max(raum + 1, 2 * rMerk.Length));
            if (rMerk[raum] == merkNr) return;
            rMerk[raum] = merkNr; merken.RaumMerken(raum, a);
        }

        // ---------------------------------------------------------------- 0.8.6 Kappen

        // Ergebnisse je Route (Index wie im Job), damit der Mischer sie in Job-Reihenfolge bekommt
        double[] kD0 = new double[0], kD1 = new double[0], kD2 = new double[0];
        V3[] kAus = new V3[0]; RaumwegErgebnis[] kErg = new RaumwegErgebnis[0];
        int[] kStart = new int[0], kN = new int[0], kPortale = new int[256];
        int[] eimerStart = new int[0], reihe = new int[0];

        /// <summary>Wie die Routenschleife in Rechne, aber Routen ohne moegliches Gewicht werden nicht exakt gerechnet:
        /// 1. Statik je Raumpaar, Zustand aller Portale/Raeume fuer die Wiederverwendung merken, grobe Schranke (GrobDb) je Route.
        /// 2. Zuerst exakt, was der Mischer gerade fuehrt (immer), dann aufsteigend nach grober Schranke (Eimer zu 0,25 dB):
        ///    Schranke > Schwelle -> weg (ab dem ersten Eimer ueber der Schwelle alle folgenden); sonst Portalpunkte laden, feine
        ///    Schranke (RaumwegKern.UntereDb) > Schwelle -> weg; sonst exakt. Die besten zuerst = engste Schwelle am schnellsten.
        /// 3. Die exakt gerechneten in Job-Reihenfolge in den Mischer (gleiche Reihenfolge, gleiche lauteste Route wie bisher).</summary>
        bool RechneGekappt(in Eingabe ein, int anzahl, RoutenMischer mix, Eintrag alt, bool logDetails, V3 quelle, V3 hoerer, ref int routen)
        {
            int rangN = ein.RangN; double breite = ein.Breite;
            float dyn = ein.Dyn;
            var st = StatikHolen(in ein, anzahl);
            if (untenDb.Length < anzahl)
            {
                int k = Math.Max(anzahl, 2 * untenDb.Length);
                Array.Resize(ref untenDb, k); Array.Resize(ref rArt, k); Array.Resize(ref reihe, k);
                Array.Resize(ref kD0, k); Array.Resize(ref kD1, k); Array.Resize(ref kD2, k); Array.Resize(ref kAus, k); Array.Resize(ref kErg, k);
                Array.Resize(ref kStart, k); Array.Resize(ref kN, k);
            }
            // 1. Zustand merken (je Portal/Raum einmal), grobe Schranken
            for (int i = 0; i < st.PortaleN; i++) { int pi = st.Portale[i]; ZustandLaden(pi, dyn); alt.PortalMerken(pi, zZu[pi], JZu[pi]); }
            for (int i = 0; i < st.RaeumeN; i++) { int ri = st.Raeume[i]; alt.RaumMerken(ri, RaumFlaeche(ri, ein.Alpha, out _)); }
            double aQ = AStill(ein.RaumA, ein.Alpha), aH = AStill(ein.RaumB, ein.Alpha);
            // 10.11. (klangfarbe): Routen sind bis WegeKern.BandGrenzeDb unterscheidbar (nicht mehr alle bei Max. insulation gleich) ->
            // Eimer und Schranken bis dorthin, sonst kappt nichts mehr, sobald alle Routen ueber dem Deckel liegen
            float grenze = (float)WegeKern.BandGrenzeDb;
            int eimerN = (int)(grenze * 4) + 2;
            if (eimerStart.Length < eimerN + 1) eimerStart = new int[eimerN + 1];
            Array.Clear(eimerStart, 0, eimerN + 1);
            for (int r = 0; r < anzahl; r++)
            {
                rArt[r] = st.Art[r];
                if (rArt[r] != 1) continue;
                double u = GrobDb(st, r, in ein, quelle, hoerer, aQ, aH, grenze);
                untenDb[r] = u;
                eimerStart[Eimer(u, eimerN) + 1]++;
            }
            for (int i = 0; i < eimerN; i++) eimerStart[i + 1] += eimerStart[i];
            for (int r = 0; r < anzahl; r++) if (rArt[r] == 1) reihe[eimerStart[Eimer(untenDb[r], eimerN)]++] = r;   // danach: eimerStart[i] = Ende von Eimer i

            // 2. exakt rechnen
            topN = 0; kp = 0; bestHat = false;
            foreach (var en in mix.Eintraege)
                if (!en.Geist && st.IdIndex.TryGetValue(en.Id, out int ri) && ri < anzahl && rArt[ri] == 1) { Exakt(in ein, st, ri, quelle, hoerer, logDetails); routen++; }
            int gekappt = 0;
            for (int pos = 0, eimer = 0; pos < eimerStart[eimerN - 1]; pos++)
            {
                while (eimer < eimerN - 1 && pos >= eimerStart[eimer]) eimer++;
                int r = reihe[pos];
                if (rArt[r] != 1) continue;                              // schon gerechnet (vom Mischer gefuehrt)
                if (topN > rangN)
                {
                    double schwelle = Schwelle(rangN, breite) + 1e-6;
                    if (eimer / 4.0 > schwelle)                          // alle weiteren Eimer liegen hoeher
                    {
                        for (int q = pos; q < eimerStart[eimerN - 1]; q++) if (rArt[reihe[q]] == 1) gekappt++;
                        break;
                    }
                    if (untenDb[r] > schwelle) { gekappt++; continue; }
                    int m = Vorbereiten(in ein, r, null);
                    if (m == 0) { rArt[r] = 0; continue; }
                    if (RaumwegKern.UntereDb(quelle, hoerer, rp, pktJob, m, raumA, ein.FRef, grenze, ein.Umgekehrt) > schwelle) { gekappt++; continue; }
                    ExaktGeladen(in ein, st, r, m, quelle, hoerer, logDetails);
                }
                else Exakt(in ein, st, r, quelle, hoerer, logDetails);
                routen++;
            }
            DGekappt += gekappt;

            // 3. in Job-Reihenfolge in den Mischer
            bool hat = false;
            for (int r = 0; r < anzahl; r++)
            {
                if (rArt[r] != 2) continue;
                hat = true;
                mix.Kandidat(st.Id[r], kD0[r], kD1[r], kD2[r], kAus[r], kErg[r], kPortale, kN[r], kStart[r]);
                alt.Kandidat(st.Id[r], kD0[r], kD1[r], kD2[r], kAus[r], in kErg[r], kPortale, kN[r], kStart[r]);
            }
            return hat;
        }

        static int Eimer(double db, int eimerN) { int e = (int)(db * 4); return e < 0 ? 0 : (e >= eimerN - 1 ? eimerN - 2 : e); }

        bool bestHat;
        int kp;

        /// <summary>0.8.6 (Meldung Ground Zero 09.11.2026 21:34 MARK 6/7): Strecken einer Route im Freien abschirmen.
        /// Ursache: RaumwegKern.Route rechnet jeden Abschnitt innerhalb eines Raums als freie Sichtlinie - in einem Innenraum stimmt
        /// das, "draussen" ist aber ein Raum ueber die ganze Karte mit Gebaeuden darin. Beleg (Log 21:34:06.814): V-Ex-Taxi 155 m,
        /// Hoerer im Innenraum (163 m2/883 m3), Route Quelle -> AudioPortal#82 (Static Open, 3622 m2, draussen -> draussen) ->
        /// Fenster #74 (24,2 m2) -> Hoerer, Weg 155,0/155,0 m (= Direktlinie), "clear" an beiden Portalen -> 2,4 dB, gemischt 0,8 dB.
        /// Dieselbe Linie hatte 2 m daneben (beide draussen, keine Route) 20-23/25/25 dB Abschirmung ueber die Hindernisse.
        /// Jetzt: zusammenhaengende Raeume im Freien entlang der Route = eine Strecke (Quelle bzw. 0,3 m vor dem Portal auf der
        /// Aussenseite bis 0,3 m vor dem naechsten Portal bzw. Hoerer), deren Abschirmung (ISO 9613-2, wie Ersatzweg) kommt je
        /// Band auf die Route (geometrischer und diffuser Anteil gehen beide ueber diese Strecke). Freie Strecke = 0 dB.</summary>
        void Schirmen(in Eingabe ein, int m, V3 quelle, V3 hoerer, ref RaumwegErgebnis e, ref double d0, ref double d1, ref double d2)
        {
            var schirm = ein.Schirm;
            if (schirm == null || m <= 0) return;
            double a0 = 0, a1 = 0, a2 = 0; int strecken = 0;
            for (int k = 0; k <= m; k++)
            {
                if (!raumDraussen[k]) continue;
                int k0 = k;
                while (k < m && raumDraussen[k + 1]) k++;
                int k1 = k;   // Raeume k0..k1 im Freien: von (k0 == 0 ? Quelle : Portal k0-1) bis (k1 == m ? Hoerer : Portal k1)
                V3 roh0 = k0 == 0 ? quelle : pkt[k0 - 1], roh1 = k1 == m ? hoerer : pkt[k1];
                V3 a = k0 == 0 ? quelle : Aussen(in rp[k0 - 1], pkt[k0 - 1], roh1);
                V3 b = k1 == m ? hoerer : Aussen(in rp[k1], pkt[k1], roh0);
                if (!schirm.Strecke(a, b, k0 == 0 ? -1 : idx[k0 - 1], k1 == m ? -1 : idx[k1], out Hindernis h) || !h.Gueltig) continue;
                a0 += h.Db0; a1 += h.Db1; a2 += h.Db2; strecken++;
            }
            if (strecken == 0) return;
            double g = WegeKern.BandGrenzeDb;   // 10.11. (klangfarbe): nicht je Band bei Max. insulation kappen
            d0 = Math.Max(0, Math.Min(g, d0 + a0));
            d1 = Math.Max(d0, Math.Min(g, d1 + a1));
            d2 = Math.Max(d0, Math.Min(g, d2 + a2));
            e.Db = Math.Min(g, e.Db + a0); e.DbMitte = Math.Min(g, e.DbMitte + a1); e.DbHoch = Math.Min(g, e.DbHoch + a2);
            e.DbAussen = a0; e.AussenStrecken = strecken;
        }

        /// <summary>0.8.6: mit Abschirmung der Strecken im Freien zaehlen Portale zwischen zwei Raeumen im Freien nicht mehr. Das Spiel
        /// teilt "draussen" in Zonen (Static-Open-Portale bis 3622 m2, Ground Zero); eine Route, die nur andere solche Zonengrenzen
        /// kreuzt, ist derselbe Schallweg (Quelle -> ueber die Hindernisse -> Oeffnung des Innenraums). Ohne Kuerzen zaehlte jede
        /// Zonenfolge als eigener Weg mit fast gleichem Pegel und der Mischer summierte sie (Offline-Test: 11 Zonengrenzen x 2 offene
        /// Fenster -> 14,9 statt 19 dB). Gekuerzt in RIdx/RN vor der Rechnung: die Routen-ID entsteht aus den verbleibenden Portalen,
        /// gleiche Folgen zaehlen einmal (Statik: erste ihrer ID), Schranken und Kappen gelten fuer die gekuerzte Route.</summary>
        void AussenKuerzen(int anzahl)
        {
            for (int r = 0; r < anzahl; r++)
            {
                int n = RN[r];
                if (n <= 0) continue;
                int s0 = RStart[r], m = 0;
                for (int i = 0; i < n; i++)
                {
                    int pi = RIdx[s0 + i];
                    if (Obj(pi))
                    {
                        int f = Vorn[pi], b = Hinten[pi];
                        if (f >= 0 && b >= 0 && Draussen[f] && Draussen[b]) continue;
                    }
                    RIdx[s0 + m++] = pi;
                }
                RN[r] = m > 0 ? m : -1;
            }
        }

        /// <summary>Punkt 0,3 m vor dem Portal auf der Seite von 'seite' (wie Ersatzweg: Strahlen von oben treffen sonst den Sturz).</summary>
        static V3 Aussen(in RaumPortal p, V3 punkt, V3 seite)
        {
            double s = V3.Dot(seite - punkt, p.Normale);
            if (Math.Abs(s) < 1e-6) return punkt;
            return punkt + p.Normale * (s > 0 ? 0.3 : -0.3);
        }

        void Exakt(in Eingabe ein, Statik st, int r, V3 quelle, V3 hoerer, bool logDetails)
        {
            int m = Vorbereiten(in ein, r, null);
            if (m == 0) { rArt[r] = 0; return; }
            ExaktGeladen(in ein, st, r, m, quelle, hoerer, logDetails);
        }

        /// <summary>Route r (Portaldaten schon geladen) exakt rechnen, Ergebnis je Route ablegen, Schwelle und lauteste Route
        /// fortschreiben (lauteste wie bisher: kleinste Daemmung, bei Gleichstand die fruehere im Job).</summary>
        void ExaktGeladen(in Eingabe ein, Statik st, int r, int m, V3 quelle, V3 hoerer, bool logDetails)
        {
            float dyn = ein.Dyn; double fRef = ein.FRef;
            if (ein.Umgekehrt) Umdrehen(m);
            for (int i = 0; i < m; i++) pkt[i] = pktJob[i];
            int ez = EckenRechnen(in ein, m, quelle, hoerer);
            var e = RaumwegKern.Route(quelle, hoerer, rp, pkt, m, raumA, fRef, ein.FHoch, WegeKern.BandGrenzeDb, beug, umweg, raumV, ez > 0 ? eckPkt : null, eckStart);   // 10.11.: Baender ungekappt
            V3 aus = AustrittPunkt(m, out V3 vorAus); e.Vor = vorAus;   // tabellenneu: Wegpunkt vor dem Austritt fuer die Nahblende
            rArt[r] = 2;
            if (!e.Gueltig) { rArt[r] = 0; return; }
            Baender(in e, dyn, ein.TiefpassMin, fRef, ein.Fit1, ein.Fit2, out kD0[r], out kD1[r], out kD2[r]);
            Schirmen(in ein, m, quelle, hoerer, ref e, ref kD0[r], ref kD1[r], ref kD2[r]);
            TopDazu(e.Db, ein.RangN);   // 0.8.6: mit Abschirmung (die Schranken UntereDb/GrobDb bleiben untere Schranken)
            kAus[r] = aus; kErg[r] = e;
            int n = Math.Min(m, 16);
            if (kp + n > kPortale.Length) Array.Resize(ref kPortale, Math.Max(kPortale.Length * 2, kp + n));
            Array.Copy(idx, 0, kPortale, kp, n);
            kStart[r] = kp; kN[r] = m; kp += n;
            if (!bestHat || e.Db < Best.Db || (e.Db == Best.Db && r < BestRoute))
            {
                bestHat = true; Best = e; BestN = m; BestRoute = r;
                if (logDetails)
                {
                    for (int i = 0; i < m; i++) { BestIdx[i] = idx[i]; BestBeug[i] = beug[i]; BestUm[i] = umweg[i]; BestOffen[i] = rp[i].Offen; BestTauDb[i] = -10 * Math.Log10(Math.Max(rp[i].Tau, 1e-12)); }
                    for (int i = 0; i <= m; i++) { BestA[i] = raumA[i]; BestV[i] = raumV[i]; }
                    BestR1 = V3.Abstand(quelle, pkt[0]); BestOmega = RaumwegKern.Raumwinkel(rp[0], quelle) / (4 * Math.PI) + RaumwegKern.QuellDiffus(rp[0], 1.0, raumA, raumV, Math.Max(BestR1, 0.5), 0);
                    BestAus = aus; BestVorAus = vorAus; BestEcken = e.Ecken; BestEckDb = e.DbEckenMitte;
                }
            }
        }

        // ---------------------------------------------------------------- 0.8.6 Kappen: Daten je Raumpaar

        /// <summary>Was an den Routen eines Raumpaars fest ist (Portalfolgen, Raeume dahinter, Routen-IDs, Mindestabstaende der
        /// Portalmitten). Gilt, solange Routenliste und Raumnetz gleich sind (Pruefsumme ueber die Portalfolgen).</summary>
        sealed class Statik
        {
            public ulong Summe;
            public int Anzahl;
            public byte[] Art = new byte[0];          // 1 = gueltig und erste ihrer ID, 0 = ungueltig/doppelt
            public ulong[] Id = new ulong[0];
            public int[] Start = new int[0], N = new int[0];
            public int[] Pi = new int[0], Nach = new int[0];   // je Portal (Job-Reihenfolge): Index, Raum dahinter (-1 = keiner)
            public double[] Seg = new double[0];      // Mindestabstand Portal j -> j+1 (Mitten minus Radien, >= 0)
            public double[] LInnen = new double[0];   // Summe der Seg einer Route
            public int[] Portale = new int[0], Raeume = new int[0];
            public int PortaleN, RaeumeN;
            public readonly Dictionary<ulong, int> IdIndex = new Dictionary<ulong, int>();
        }

        readonly Dictionary<ulong, Statik> statik = new Dictionary<ulong, Statik>();
        double[] radius = new double[0]; int[] radiusStempel = new int[0];
        int statikNr = 1;
        public int DStatikNeu;

        /// <summary>Statik verwerfen (neues Raumnetz).</summary>
        public void StatikLeeren() { statik.Clear(); statikNr++; }

        Statik StatikHolen(in Eingabe ein, int anzahl)
        {
            ulong h = 14695981039346656037UL;
            unchecked
            {
                void M(ulong v) { h = (h ^ v) * 1099511628211UL; }
                M(ein.Paar); M((ulong)anzahl); M((ulong)(ushort)ein.RaumAId); M(ein.RaumADraussen ? 1UL : 0UL); M((ulong)ein.RaumA); M((ulong)ein.RaumB);
                for (int r = 0; r < anzahl; r++)
                {
                    int n = RN[r]; M((ulong)n);
                    for (int i = 0, s0 = RStart[r]; i < n; i++) M((ulong)RIdx[s0 + i]);
                }
            }
            if (statik.TryGetValue(h, out var st) && st.Anzahl == anzahl) return st;
            if (statik.Count >= 256) statik.Clear();
            DStatikNeu++;
            st = new Statik { Summe = h, Anzahl = anzahl };
            int gesamt = 0;
            for (int r = 0; r < anzahl; r++) if (RN[r] > 0) gesamt += RN[r];
            st.Art = new byte[anzahl]; st.Id = new ulong[anzahl]; st.Start = new int[anzahl]; st.N = new int[anzahl]; st.LInnen = new double[anzahl];
            st.Pi = new int[gesamt]; st.Nach = new int[gesamt]; st.Seg = new double[gesamt];
            if (radius.Length < JGeo.Length) { Array.Resize(ref radius, JGeo.Length); Array.Resize(ref radiusStempel, JGeo.Length); }
            var pListe = new List<int>(); var rListe = new List<int>();
            if (pMerk.Length < JGeo.Length) Array.Resize(ref pMerk, JGeo.Length);
            if (++merkNr == int.MaxValue) { merkNr = 1; Array.Clear(pMerk, 0, pMerk.Length); Array.Clear(rMerk, 0, rMerk.Length); }
            void RaumNeu(int ri)
            {
                if (ri < 0) return;
                if (ri >= rMerk.Length) Array.Resize(ref rMerk, Math.Max(ri + 1, 2 * rMerk.Length));
                if (rMerk[ri] != merkNr) { rMerk[ri] = merkNr; rListe.Add(ri); }
            }
            RaumNeu(ein.RaumA); RaumNeu(ein.RaumB);
            gesehen.Clear();
            int f = 0;
            for (int r = 0; r < anzahl; r++)
            {
                int n = RN[r];
                st.Start[r] = f; st.N[r] = Math.Max(n, 0);
                if (n <= 0) continue;
                short vorRaum = ein.RaumAId; bool vorDraussen = ein.RaumADraussen;
                double lin = 0;
                for (int i = 0; i < n; i++)
                {
                    int pi = RIdx[RStart[r] + i];
                    st.Pi[f + i] = pi; geo[Math.Min(i, MaxPortale - 1)] = JGeoKey[pi];
                    int nach = NachStatisch(pi, ref vorRaum, ref vorDraussen);
                    st.Nach[f + i] = nach;
                    if (i < n - 1) RaumNeu(nach);
                    if (pMerk[pi] != merkNr) { pMerk[pi] = merkNr; pListe.Add(pi); }
                    if (i > 0)
                    {
                        int pj = st.Pi[f + i - 1];
                        double sg = Math.Max(0, V3.Abstand(JGeo[pj].Mitte, JGeo[pi].Mitte) - Radius(pj) - Radius(pi));
                        st.Seg[f + i - 1] = sg; lin += sg;
                    }
                }
                st.LInnen[r] = lin;
                ulong id = RoutenMischer.Id(ein.Paar, geo, n);
                st.Id[r] = id;
                if (gesehen.Add(id)) { st.Art[r] = 1; st.IdIndex[id] = r; }
                f += n;
            }
            st.Portale = pListe.ToArray(); st.PortaleN = st.Portale.Length;
            st.Raeume = rListe.ToArray(); st.RaeumeN = st.Raeume.Length;
            statik[h] = st;
            return st;
        }

        // Wie RaumNach, ohne Absorptionsflaeche (Raumfolge ist fest je Raid)
        int NachStatisch(int pi, ref short vorRaum, ref bool vorDraussen)
        {
            if (!Obj(pi)) return -1;
            int f = Vorn[pi], bk = Hinten[pi], nach;
            if (f >= 0 && RaumId[f] == vorRaum) nach = bk;
            else if (bk >= 0 && RaumId[bk] == vorRaum) nach = f;
            else if (f >= 0 && bk >= 0) nach = Draussen[f] != vorDraussen ? f : bk;
            else nach = bk >= 0 ? bk : f;
            if (nach < 0) return -1;
            vorRaum = RaumId[nach]; vorDraussen = Draussen[nach];
            return nach;
        }

        /// <summary>Groesster Abstand eines Portalpunkts von der Mitte: |Rechts| HalbX + |Oben| HalbY (ohne Annahme orthonormal).</summary>
        double Radius(int pi)
        {
            if (radiusStempel[pi] != statikNr)
            {
                var g = JGeo[pi];
                radius[pi] = g.Rechts.Laenge * Math.Abs(g.HalbX) + g.Oben.Laenge * Math.Abs(g.HalbY);
                radiusStempel[pi] = statikNr;
            }
            return radius[pi];
        }

        /// <summary>Portalzustand je Rechnung (geschlossener Anteil, Transmissionsgrad des geschlossenen Teils).</summary>
        void ZustandLaden(int pi, float dyn)
        {
            if (zStempel[pi] == rechnung) return;
            float jz = JZu[pi];
            double z0 = Obj(pi) ? ZuSpiel(pi) : (jz > 0.001f ? 1.0 : 0.0);
            zZu[pi] = z0; zStempel[pi] = rechnung;
            double b0 = Blatt(pi, 0);
            if (!double.IsNaN(b0))
            {
                // 0.8.6: geschlossener Teil = Tuerblatt/Scheibe nach dem Massengesetz je Band (TuerKern.cs). Der offene Anteil kommt
                // weiter aus der Schliessung des Spiels (Oeffnungswinkel, zerbrochene Scheiben).
                // 10.11. (klangfarbe): Blatt je Band ungekappt (die Fuge begrenzt es ohnehin, TuerKern.MitFuge)
                double g = WegeKern.BandGrenzeDb;
                double t0 = z0 > 1e-3 ? Math.Min(g, b0) : 0.0, t1 = z0 > 1e-3 ? Math.Min(g, Blatt(pi, 1)) : 0.0, t2 = z0 > 1e-3 ? Math.Min(g, Blatt(pi, 2)) : 0.0;
                zTau[pi] = Math.Pow(10, -t0 / 10); zTauM[pi] = Math.Pow(10, -t1 / 10); zTauH[pi] = Math.Pow(10, -t2 / 10); zBand[pi] = true;
                return;
            }
            // 0.8.6 Tuerfuge-Fix: ohne Blattmodell der Wert der Schliessung als Blatt, aber mit der Tuerfuge parallel (TuerKern.OhneBlattDb) -
            // vorher fugenlos bis Max. insulation (Factory Tor #209: 45 dB statt hoechstens 23 dB)
            double tlDb = z0 > 1e-3 ? TuerKern.OhneBlattDb(jz / z0 * JWand[pi] * dyn, dyn) : 0.0;
            zTau[pi] = zTauM[pi] = zTauH[pi] = Math.Pow(10, -tlDb / 10); zBand[pi] = false;
        }

        /// <summary>Wie RaumFlaeche, ohne Diagnosezaehler.</summary>
        double AStill(int raum, float alpha)
        {
            if (raum < 0 || Draussen[raum] || !Gemessen(raum)) return -1;
            if (aBild[raum] == bild && aAlpha[raum] == alpha) return aWert[raum];
            double a = RaumwegKern.Absorption(oberflaeche[raum], alpha, OffeneFlaeche(raum));
            aWert[raum] = a; aAlpha[raum] = alpha; aBild[raum] = bild;
            return a;
        }

        /// <summary>Grobe untere Schranke der Daemmung einer Route (Bezugsband) ohne Portalpunkte: Weglaenge >= Abstaende der
        /// Portalmitten minus ihre Radien, keine Kantenbeugung (>= 0 dB), Diffusfeld wie RaumwegKern.UntereDb mit Mindestabstaenden.
        /// Jeder Faktor ist >= dem in RaumwegKern.Route, also GrobDb <= UntereDb <= Route(...).Db.</summary>
        double GrobDb(Statik st, int r, in Eingabe ein, V3 q, V3 h, double aQ, double aH, float dyn)
        {
            int s = st.Start[r], n = st.N[r];
            bool umg = ein.Umgekehrt;
            float alpha = ein.Alpha;
            int J(int k) => umg ? n - 1 - k : k;                     // Portal k der echten Richtung -> Job-Index
            double AJob(int j) => j == 0 ? (umg ? aH : aQ) : j == n ? (umg ? aQ : aH) : AStill(st.Nach[s + j - 1], alpha);
            double ATrue(int k) => umg ? AJob(n - k) : AJob(k);
            int pf = st.Pi[s + J(0)], pl = st.Pi[s + J(n - 1)];
            ref RaumPortal gf = ref JGeo[pf];
            ref RaumPortal gl = ref JGeo[pl];
            if (!(gf.HalbX >= 0 && gf.HalbY >= 0)) return 0;
            double d = Math.Max(V3.Abstand(q, h), 0.5);
            double lEnde = Math.Max(0, V3.Abstand(gl.Mitte, h) - Radius(pl));
            double laenge = Math.Max(d, Math.Max(0, V3.Abstand(q, gf.Mitte) - Radius(pf)) + st.LInnen[r] + lEnde);
            double offen = 1;
            for (int k = 0; k < n && offen > 0; k++) { double o = 1.0 - zZu[st.Pi[s + k]]; offen *= o < 0 ? 0 : (o > 1 ? 1 : o); }
            double geoE = offen / (4 * Math.PI * laenge * laenge);
            // Diffusfeld
            double o0 = Clamp01d(1.0 - zZu[pf]);
            double t0 = o0 + (1 - o0) * Clamp01d(zTau[pf]);
            double w = Math.Max(Math.Abs(V3.Dot(q - gf.Mitte, gf.Normale)), 1e-3);
            double fl0 = 4.0 * gf.HalbX * gf.HalbY;
            double leistung = Math.Min(0.5, fl0 / (4 * Math.PI * w * w)) * t0;
            double a0 = ATrue(0);
            if (a0 > 0) leistung += fl0 * t0 / Math.Max(a0, RaumwegKern.MinFlaecheA);
            double inzident = 0;
            bool draussenVor = false;   // 0.8.6: inzident ist die Leistung auf die naechste Oeffnung (Freiraum-Sprung)
            for (int k = 0; k < n; k++)
            {
                int jk = J(k), pi = st.Pi[s + jk];
                if (k > 0)
                {
                    ref RaumPortal g = ref JGeo[pi];
                    if (!(g.HalbX >= 0 && g.HalbY >= 0)) return 0;
                    double o = Clamp01d(1.0 - zZu[pi]);
                    leistung = (draussenVor ? inzident : inzident * (4.0 * g.HalbX * g.HalbY)) * (o + (1 - o) * Clamp01d(zTau[pi]));
                }
                double a = ATrue(k + 1);
                bool letzte = k == n - 1;
                draussenVor = false;
                if (a > 0) inzident = (letzte ? 4.0 : 1.0) * leistung / Math.Max(a, RaumwegKern.MinFlaecheA);
                else if (letzte)
                {
                    double rr = Math.Max(lEnde, 0.5);
                    inzident = leistung / (2 * Math.PI * rr * rr);
                }
                else
                {
                    // 0.8.6 wie RaumwegKern.Kette: Anteil Omega / 2 pi <= S / (2 pi Seg^2), hoechstens 1 (hier schon als Leistungsanteil)
                    ref RaumPortal gn = ref JGeo[st.Pi[s + J(k + 1)]];
                    double sg = st.Seg[s + Math.Min(jk, J(k + 1))];
                    double fl = 4.0 * gn.HalbX * gn.HalbY;
                    inzident = leistung * (sg * sg * 2 * Math.PI <= fl ? 1.0 : fl / (2 * Math.PI * sg * sg));
                    draussenVor = true;
                }
            }
            double wert = geoE + inzident, bezug = 1.0 / (4 * Math.PI * d * d);
            if (wert <= 0) return dyn;
            return Math.Max(0, Math.Min(dyn, 10 * Math.Log10(bezug / wert)));
        }

        static double Clamp01d(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);

        // 0.8.6 Kappen: die N+1 kleinsten Daemmungen der exakt gerechneten Routen (aufsteigend)
        double[] untenDb = new double[0]; byte[] rArt = new byte[0];   // 0 = ungueltig/doppelt, 1 = offen, 2 = Saat (gerechnet)
        readonly int[] saatIdx = new int[4 * (MaxRangN + 1) + 64];
        readonly double[] top = new double[MaxRangN + 1];
        int topN;
        readonly HashSet<ulong> gesehen = new HashSet<ulong>();
        public const int MaxRangN = 16;
        public int DGekappt;
        int[] pMerk = new int[0], rMerk = new int[0];
        int merkNr;

        void TopDazu(double db, int rangN)
        {
            int k = rangN + 1;
            if (topN == k && db >= top[k - 1]) return;
            int j = topN < k ? topN++ : k - 1;
            while (j > 0 && top[j - 1] > db) { top[j] = top[j - 1]; j--; }
            top[j] = db;
        }

        /// <summary>Daemmung, ab der eine Route sicher Gewicht 0 hat und keinen Rang veraendert (topN == N+1).</summary>
        double Schwelle(int rangN, double breite)
        {
            double dN1 = top[rangN];                                  // (N+1)-te
            if (breite <= 0) return top[rangN - 1];                   // harte Auswahl: nur die N lautesten zaehlen
            return Math.Max(dN1, (top[rangN - 1] + dN1) / 2 + breite / 2);
        }

        void Umdrehen(int m)
        {
            Array.Reverse(rp, 0, m); Array.Reverse(pktJob, 0, m); Array.Reverse(idx, 0, m);
            Array.Reverse(raumA, 0, m + 1); Array.Reverse(raumV, 0, m + 1); Array.Reverse(geo, 0, m); Array.Reverse(raumDraussen, 0, m + 1);
        }

        /// <summary>Wie Physik.Abbildung (Mathf.Pow = (float)Math.Pow): 22 kHz * (min/22 kHz)^w.</summary>
        public static float Abbildung(float w, float tiefpassMin) => 22000f * (float)Math.Pow(tiefpassMin / 22000f, Clamp01f(w));

        // ---------------------------------------------------------------- Wiederverwendung

        sealed class Eintrag
        {
            public struct Kand { public ulong Id; public double D0, D1, D2; public V3 Aus; public RaumwegErgebnis Erg; public int Start, N; }
            public Kand[] K = new Kand[16];
            public int KN;
            public int[] KPortale = new int[64];
            int kp;
            // beteiligte Portale (Zustand) und Raeume (Absorptionsflaeche)
            public int[] PPi = new int[32]; public double[] PZu = new double[32]; public float[] PJob = new float[32];
            public int PN;
            public int[] RIdx = new int[16]; public double[] RA = new double[16];
            public int RN;
            public bool Hat, Fertig;
            public int Routen;
            // 0.8.6: lauteste Route fuers Klang-Log (Wiederverwendung auch bei eingeschaltetem Klang-Log)
            public bool MitBest;
            public int BlattStand;   // 0.8.6: Stand der Blatt-Daemmungen bei der Rechnung
            RaumwegErgebnis bErg; int bN, bRoute; double bOmega, bR1; V3 bAus, bVorAus; int bEcken; double bEckDb;
            readonly int[] bIdx = new int[MaxPortale];
            readonly double[] bBeug = new double[MaxPortale], bUm = new double[MaxPortale], bOffen = new double[MaxPortale], bTau = new double[MaxPortale];
            readonly double[] bA = new double[MaxPortale + 1], bV = new double[MaxPortale + 1];

            public void BestMerken(RaumwegNetz n)
            {
                MitBest = true; bErg = n.Best; bN = n.BestN; bRoute = n.BestRoute; bOmega = n.BestOmega; bR1 = n.BestR1;
                bAus = n.BestAus; bVorAus = n.BestVorAus; bEcken = n.BestEcken; bEckDb = n.BestEckDb;
                int m = Math.Min(n.BestN, MaxPortale);
                Array.Copy(n.BestIdx, bIdx, m); Array.Copy(n.BestBeug, bBeug, m); Array.Copy(n.BestUm, bUm, m);
                Array.Copy(n.BestOffen, bOffen, m); Array.Copy(n.BestTauDb, bTau, m);
                Array.Copy(n.BestA, bA, m + 1); Array.Copy(n.BestV, bV, m + 1);
            }

            public void BestLaden(RaumwegNetz n)
            {
                n.Best = bErg; n.BestN = bN; n.BestRoute = bRoute; n.BestOmega = bOmega; n.BestR1 = bR1;
                n.BestAus = bAus; n.BestVorAus = bVorAus; n.BestEcken = bEcken; n.BestEckDb = bEckDb;
                int m = Math.Min(bN, MaxPortale);
                Array.Copy(bIdx, n.BestIdx, m); Array.Copy(bBeug, n.BestBeug, m); Array.Copy(bUm, n.BestUm, m);
                Array.Copy(bOffen, n.BestOffen, m); Array.Copy(bTau, n.BestTauDb, m);
                Array.Copy(bA, n.BestA, m + 1); Array.Copy(bV, n.BestV, m + 1);
            }
            // Schluesselfelder
            uint paar; bool umg; long ax, ay, az, bx, by, bz; int anzahl, gesamt, raumA, raumB;
            float dyn, alpha, tpMin, fit1, fit2; double fRef, fHoch, breite; int rangN; bool schirm;

            public void Beginn(in Eingabe e, int anz)
            {
                KN = kp = PN = RN = 0; Hat = Fertig = MitBest = false; Routen = 0; rangN = e.RangN; breite = e.Breite; schirm = e.Schirm != null;
                paar = e.Paar; umg = e.Umgekehrt; anzahl = anz; gesamt = e.RoutenGesamt; raumA = e.RaumA; raumB = e.RaumB;
                ax = Z(e.A.X); ay = Z(e.A.Y); az = Z(e.A.Z); bx = Z(e.B.X); by = Z(e.B.Y); bz = Z(e.B.Z);
                dyn = e.Dyn; alpha = e.Alpha; tpMin = e.TiefpassMin; fit1 = e.Fit1; fit2 = e.Fit2; fRef = e.FRef; fHoch = e.FHoch;
            }

            public void Kandidat(ulong id, double d0, double d1, double d2, V3 aus, in RaumwegErgebnis erg, int[] portale, int n)
                => Kandidat(id, d0, d1, d2, aus, in erg, portale, n, 0);

            public void Kandidat(ulong id, double d0, double d1, double d2, V3 aus, in RaumwegErgebnis erg, int[] portale, int n, int start)
            {
                if (KN == K.Length) Array.Resize(ref K, K.Length * 2);
                if (kp + n > KPortale.Length) Array.Resize(ref KPortale, Math.Max(KPortale.Length * 2, kp + n));
                Array.Copy(portale, start, KPortale, kp, n);
                K[KN++] = new Kand { Id = id, D0 = d0, D1 = d1, D2 = d2, Aus = aus, Erg = erg, Start = kp, N = n };
                kp += n;
            }

            public void PortalMerken(int pi, double zu, float job)   // doppelt: verhindert der Stempel pMerk (0.8.6)
            {
                if (PN == PPi.Length) { Array.Resize(ref PPi, PN * 2); Array.Resize(ref PZu, PN * 2); Array.Resize(ref PJob, PN * 2); }
                PPi[PN] = pi; PZu[PN] = zu; PJob[PN] = job; PN++;
            }

            public void RaumMerken(int raum, double a)
            {
                if (raum < 0) return;   // doppelt: verhindert der Stempel in RaumwegNetz.RaumMerken (0.8.6)
                if (RN == RIdx.Length) { Array.Resize(ref RIdx, RN * 2); Array.Resize(ref RA, RN * 2); }
                RIdx[RN] = raum; RA[RN] = a; RN++;
            }

            public bool Passt(in Eingabe e, int anz, RaumwegNetz netz)
            {
                if (!Fertig || BlattStand != netz.BlattStand || paar != e.Paar || umg != e.Umgekehrt || anzahl != anz || gesamt != e.RoutenGesamt || raumA != e.RaumA || raumB != e.RaumB) return false;
                if (ax != Z(e.A.X) || ay != Z(e.A.Y) || az != Z(e.A.Z) || bx != Z(e.B.X) || by != Z(e.B.Y) || bz != Z(e.B.Z)) return false;
                if (dyn != e.Dyn || alpha != e.Alpha || tpMin != e.TiefpassMin || fit1 != e.Fit1 || fit2 != e.Fit2 || fRef != e.FRef || fHoch != e.FHoch
                    || rangN != e.RangN || breite != e.Breite || schirm != (e.Schirm != null)) return false;
                // Zustand aller beteiligten Portale (Spiel und Job) und die Absorptionsflaeche aller beteiligten Raeume
                for (int i = 0; i < PN; i++)
                {
                    int pi = PPi[i];
                    if (!netz.JobGeladen(pi) || netz.JZu[pi] != PJob[i]) return false;
                    double zu = netz.Obj(pi) ? netz.ZuSpiel(pi) : (netz.JZu[pi] > 0.001f ? 1.0 : 0.0);
                    if (zu != PZu[i]) return false;
                }
                for (int i = 0; i < RN; i++)
                    if (netz.RaumFlaeche(RIdx[i], alpha, out _) != RA[i]) return false;
                return true;
            }
        }

        static long Z(double x) => (long)Math.Floor(x / ZelleM);

        readonly Dictionary<ulong, Eintrag> tabelle = new Dictionary<ulong, Eintrag>(CacheMax);
        readonly Stack<Eintrag> frei = new Stack<Eintrag>();

        public int CacheAnzahl => tabelle.Count;

        public void Leeren()
        {
            foreach (var kv in tabelle) frei.Push(kv.Value);
            tabelle.Clear();
        }

        static ulong Schluessel(in Eingabe e, int anzahl)
        {
            ulong h = 14695981039346656037UL;
            unchecked
            {
                void M(ulong v) { h = (h ^ v) * 1099511628211UL; }
                M(e.Paar); M(e.Umgekehrt ? 1UL : 0UL); M((ulong)anzahl); M((ulong)e.RoutenGesamt); M(e.Schirm != null ? 1UL : 0UL);
                M((ulong)Z(e.A.X)); M((ulong)Z(e.A.Y)); M((ulong)Z(e.A.Z));
                M((ulong)Z(e.B.X)); M((ulong)Z(e.B.Y)); M((ulong)Z(e.B.Z));
            }
            return h;
        }
    }

    /// <summary>0.8.5: Laufzeitmessung je Teil (Hauptfaden). Summe und groesster Wert je Bild, Zaehler je 30 s.</summary>
    internal sealed class Laufzeit
    {
        public readonly string Name;
        long summe, bildSumme, bildMax, anzahl;
        int bild = int.MinValue, bilder;
        public Laufzeit(string name) { Name = name; }
        public static long Jetzt => Stopwatch.GetTimestamp();
        public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        /// <summary>Zeit seit 'start' diesem Teil im Bild 'b' zuschlagen.</summary>
        public void Dazu(long start, int b)
        {
            long dt = Stopwatch.GetTimestamp() - start;
            if (b != bild) { Abschliessen(); bild = b; bilder++; }
            bildSumme += dt; summe += dt; anzahl++;
        }

        void Abschliessen() { if (bildSumme > bildMax) bildMax = bildSumme; bildSumme = 0; }

        /// <summary>Was in diesem Bild schon verbraucht ist (ms).</summary>
        public double ImBild(int b) => b == bild ? Ms(bildSumme) : 0;

        public bool Leer => anzahl == 0;

        /// <summary>"name x ms in n calls / k frames, max y ms/frame" und zuruecksetzen.</summary>
        public string Text()
        {
            Abschliessen();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string s = Name + " " + Ms(summe).ToString("F1", inv) + " ms in " + anzahl + " calls / " + bilder + " frames (max " + Ms(bildMax).ToString("F2", inv) + " ms per frame)";
            summe = bildMax = anzahl = 0; bilder = 0; bild = int.MinValue;
            return s;
        }
    }
}
