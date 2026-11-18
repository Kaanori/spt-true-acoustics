using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // hallabsorption (17.11.2026) - wirksame Oberflaeche und flaechengewichteter Absorptionsgrad eines Raums aus Zufallssehnen.
    // Reiner Kern ohne Unity (Offline-Test). Spiel-Teil: HallZonen.cs (Strahlen beim Raid-Start, ueber Bilder verteilt, Cache).
    //
    // Ursache (Meldung Kaan 17.11. 21:16 "da ist was ganz Komisches ... das ist Hall"; tunneldiffus-STAND Teil 2/3):
    //   A eines Raums war Hueflaeche der Raumboxen x alpha aus 64 Strahlen von der Boxmitte (HallZonen.MessenRaum). Beides unterschaetzt
    //   die Absorption grosser, moeblierter Raeume: (1) Maschinen, Kisten, Stege, Rohre haben eigene Flaeche im Raum (Area_2: 3,1-fache
    //   Huellflaeche), (2) Strahlen aus der Mitte treffen fast nur Decke und Aussenwaende (alpha 0,051 statt flaechengewichtet 0,09-0,11).
    //   Ergebnis Hallen T(1 kHz) 6-7 s statt 2,6-4,7 s; seit dem echten Nachhall (nachhallstimme) hoerbar als Dauerhall.
    //
    // Physik (Quellen):
    //  - Mittlere Sehnenlaenge isotroper, gleichverteilter Geraden durch einen Koerper = 4 V / S (Cauchy-Crofton; Kuttruff, Room Acoustics,
    //    5. Aufl., Kap. 4.5 "mean free path" l = 4V/S). Gilt fuer beliebige, auch nicht-konvexe Raeume: Einbauten zaehlen mit ihrer
    //    Oberflaeche (Einbauten als Teil von S bzw. als Streukoerper: EN 12354-6:2003 Gl. 4 mit A_obj; VDI 3760 / ISO 11690-3 Streudichte).
    //  - Schaetzer: isotrope Testlinien durch gleichverteilte Punkte der Vereinigung; je Linie c = Zahl der an LUFT grenzenden Flaechen
    //    (Luft -> fest, fest -> Luft) innerhalb des Raums plus offene Enden, L = Laenge der Linie im Raum. Stereologie: S_V = 2 P_L
    //    (Saltykov 1958; Underwood, Quantitative Stereology 1970); fuer einen leeren konvexen Raum (c = 2) genau Cauchy 4V/S. Die Linien
    //    sind laengengewichtet gezogen -> S = 2 V Mittel(c/L), Flaechenanteile mit Gewicht 1/L. Erste Fassung zog nur die erste Flaeche
    //    je Richtung (l = 1/Mittel(1/L)): schwerer Rand bei sehr kurzen Sehnen, Area_2 offline 15 400-23 900 m2 bei 400 Proben und
    //    systematisch zu klein; Linien: 20 157-20 823 m2 (400), 20 256 m2 (6000) [sonde2, LINIE].
    //  - Endpunkte ohne Flaeche bis zum Rand der Raumboxen: offene Grenze (Portal, Spalt, Kopplung zum Nachbarraum). Sie zaehlen NICHT als
    //    Flaeche mit alpha 1 - die Portale fuehrt die Mod wie bisher mit ihrer echten Flaeche (SOffen), weitere Luecken werden konservativ
    //    nicht als Absorption gerechnet (Annahme, im Bericht beziffert).
    //  - Uebergabe an die bestehende Rechnung (Hueflaeche S_Box, Portale So, Eyring): aequivalenter Wand-Absorptionsgrad der Huelle, der
    //    dieselbe Eyring-Flaeche ergibt wie die echten Flaechen: -S_Box ln(1 - am) = -(S_mat + So) ln(1 - (S_mat a_mat + So)/(S_mat + So)).
    //    So bleiben Hallzonen (Meta-Bezug mit S_Box), Raumweg (A = S alpha + So), Wandweg und Nachhall-Stimme bei EINER A je Raum.
    // ------------------------------------------------------------------------------------------
    internal static class SehnenKern
    {
        /// <summary>Linien je Raum. Streuung von S_wirk bei 400 Linien offline (Area_2, 3 Saaten): +-2 % (sonde2, LINIE).</summary>
        public const int ZielSehnen = 400;
        /// <summary>Hoechstens so viele Ziehungen je Raum und Ziellinie (Punkte ausserhalb der Vereinigung werden verworfen).</summary>
        public const int VersucheJeLinie = 6;
        /// <summary>Linien fuer einen Raum mit Volumen v (m3): 400, in Hallen mehr - offline streute T60(1 kHz) bei 400 Linien in den Hallen um
        /// +-10-18 % (Area_2 3,86-4,47 s, Area_3 4,32-5,11 s; Area_3 4,32 s mit 6000), getrieben von seltenen stark absorbierenden Treffern;
        /// Streuung ~ 1/sqrt(N) -> 400 sqrt(V/2500), hoechstens 1600 (Factory: nur die drei Hallen ueber 400).</summary>
        public static int ZielFuer(double v) => (int)Math.Min(1600, Math.Max(ZielSehnen, ZielSehnen * Math.Sqrt(Math.Max(0, v) / 2500.0)));

        /// <summary>Sammelt Testlinien eines Raums (keine Allokation nach dem Anlegen). Je Linie: LinieBeginnen, je an Luft grenzender
        /// Flaeche Flaeche(alpha) bzw. je offenem Ende Offen(), dann LinieEnde(Laenge der Linie in der Vereinigung).</summary>
        public sealed class Sammler
        {
            public int Sehnen, Innen, Versuche, Ziel = ZielSehnen;
            public double SummeKehr, GewOffen, GewGesamt;   // SummeKehr = Summe c/L
            public readonly double[] GewAlpha;
            readonly double[] linAlpha; int linFlaechen, linOffen;
            public Sammler(int baender) { GewAlpha = new double[baender]; linAlpha = new double[baender]; }
            public void Leeren() { Sehnen = Innen = Versuche = 0; SummeKehr = GewOffen = GewGesamt = 0; Array.Clear(GewAlpha, 0, GewAlpha.Length); LinieBeginnen(); }
            public bool Fertig => Sehnen >= Ziel || Versuche >= VersucheJeLinie * Ziel;
            public void LinieBeginnen() { Array.Clear(linAlpha, 0, linAlpha.Length); linFlaechen = linOffen = 0; }
            public void Flaeche(double[] alpha) { linFlaechen++; for (int b = 0; b < linAlpha.Length && b < alpha.Length; b++) linAlpha[b] += alpha[b]; }
            public void Offen() { linOffen++; }
            /// <summary>Linie abschliessen: Laenge l (m) innerhalb der Vereinigung; Gewicht 1/l (Punkt-Stichprobe ist laengengewichtet).</summary>
            public void LinieEnde(double l)
            {
                l = Math.Max(l, 0.01);
                double w = 1.0 / l;
                int c = linFlaechen + linOffen;
                Sehnen++;
                SummeKehr += c * w;
                GewGesamt += c * w; GewOffen += linOffen * w;
                for (int b = 0; b < GewAlpha.Length; b++) GewAlpha[b] += w * linAlpha[b];
                LinieBeginnen();
            }
        }

        /// <summary>Ergebnis eines Raums. vBox = Volumen der Vereinigung (m3), sBox = ihre Oberflaeche, sOffen = offene Portalflaeche (m2).
        /// sWirk = 2 V Mittel(c/L) (Stereologie S_V = 2 P_L: an Luft grenzende Flaechen + offene Grenze), fOffen = Anteil der offenen Grenze,
        /// alphaMat[b] = flaechengewichteter Absorptionsgrad der Flaechen, alphaBox[b] = aequivalenter Wandwert der Huelle (s. Kopf).
        /// vLuft = vBox (Linien zaehlen auch durch Objekte). false = zu wenig Linien.</summary>
        public static bool Ergebnis(Sammler s, double vBox, double sBox, double sOffen, double[] alphaMat, double[] alphaBox,
                                    out double sWirk, out double fOffen, out double vLuft)
        {
            sWirk = fOffen = vLuft = 0;
            if (s == null || s.Sehnen < 30 || !(s.SummeKehr > 0) || !(vBox > 0) || !(sBox > 0)) return false;
            vLuft = vBox;
            sWirk = 2.0 * vBox * s.SummeKehr / s.Sehnen;
            fOffen = s.GewGesamt > 0 ? s.GewOffen / s.GewGesamt : 0;
            double gMat = s.GewGesamt - s.GewOffen;
            double sMat = sWirk * (1 - fOffen);
            for (int b = 0; b < alphaMat.Length && b < s.GewAlpha.Length; b++)
            {
                alphaMat[b] = gMat > 0 ? s.GewAlpha[b] / gMat : 0;
                if (alphaBox != null && b < alphaBox.Length) alphaBox[b] = AlphaBoxAequivalent(sBox, sOffen, sMat, alphaMat[b]);
            }
            return true;
        }

        /// <summary>Wand-Absorptionsgrad der Huelle (sBox mit Portalen sOffen), der dieselbe Eyring-Flaeche ergibt wie die echten Flaechen
        /// sMat mit alphaMat plus die Portale (RaumGeometrieKern.EyringFlaeche). Begrenzt auf 0,005..0,95.</summary>
        public static double AlphaBoxAequivalent(double sBox, double sOffen, double sMat, double alphaMat)
        {
            double so = Math.Max(0, Math.Min(sOffen, 0.95 * sBox));
            double ziel = RaumGeometrieKern.EyringFlaeche(sMat + so, so, alphaMat);
            double am = 1 - Math.Exp(-ziel / Math.Max(sBox, 1e-9));
            double w = sBox - so;
            double a = w > 1e-9 ? (am * sBox - so) / w : alphaMat;
            return a < 0.005 ? 0.005 : (a > 0.95 ? 0.95 : a);
        }

        // ---------------------------------------------------------------- Stichprobe (gleichverteilt in der Vereinigung, isotrop)

        /// <summary>Kasten nach Volumen waehlen (u gleichverteilt in [0,1)).</summary>
        public static int KastenWaehlen(Kasten[] ks, int n, double u)
        {
            double summe = 0;
            for (int i = 0; i < n; i++) summe += ks[i].Volumen;
            double z = u * summe;
            for (int i = 0; i < n; i++) { z -= ks[i].Volumen; if (z < 0) return i; }
            return n - 1;
        }

        /// <summary>Punkt im Kasten k aus drei Gleichverteilten.</summary>
        public static void PunktIn(in Kasten k, double u1, double u2, double u3, out double x, out double y, out double z)
        {
            double a = (2 * u1 - 1) * k.Hx, b = (2 * u2 - 1) * k.Hy, c = (2 * u3 - 1) * k.Hz;
            x = k.Mx + a * k.Ux + b * k.Vx + c * k.Wx;
            y = k.My + a * k.Uy + b * k.Vy + c * k.Wy;
            z = k.Mz + a * k.Uz + b * k.Vz + c * k.Wz;
        }

        /// <summary>Gleichverteilung ueber der VEREINIGUNG: ein Punkt aus einem nach Volumen gewaehlten Kasten wird mit Wahrscheinlichkeit
        /// 1 / (Zahl der Kaesten, die ihn enthalten) angenommen (Ueberlappungen sonst mehrfach gewichtet).</summary>
        public static bool Annehmen(Kasten[] ks, int n, double x, double y, double z, double u)
        {
            int c = 0;
            for (int i = 0; i < n; i++) if (RaumGeometrieKern.Drin(ks[i], x, y, z)) c++;
            return c > 0 && u * c < 1.0;
        }

        /// <summary>Isotrope Richtung (Einheitsvektor) aus zwei Gleichverteilten.</summary>
        public static void Richtung(double u1, double u2, out double dx, out double dy, out double dz)
        {
            double c = 2 * u1 - 1, s = Math.Sqrt(Math.Max(0, 1 - c * c)), ph = 2 * Math.PI * u2;
            dx = s * Math.Cos(ph); dy = c; dz = s * Math.Sin(ph);
        }

        // ---------------------------------------------------------------- Materialzuordnung der Treffer

        /// <summary>Akustisches Material eines Treffers. ballistik = MaterialType des BallisticCollider (Geschosswirkung), flaeche =
        /// BaseBallistic.GetSurfaceSound am Treffer (das Material, das das Spiel fuer Schritte auf genau dieser Flaeche abspielt,
        /// Player.cs: Schrittflaeche = GetSurfaceSound des getroffenen HighPoly/Terrain-Colliders). Befund Factory (sonde2, schicht2.txt):
        /// die Boeden der Hallen und Keller sind als Geschossmaterial Gravel/Soil gefuehrt ("Factory_area_inside_floor_BALLISTIC_Gravel",
        /// "Factory_basement_BALLISTIC_soil"), dazu Haufen-Decals ("Sand_decal_*", 0,26-0,33 m, 0,6-12 cm ueber einem Boden), waehrend die
        /// Schritte der Bots dort ueberwiegend asphalt/concrete klingen (Klang-Log 17:38, Area_1: 203 asphalt, 81 concrete, 50 gravel).
        /// Regel: Ist das Geschossmaterial poroeser Boden (Soil, Gravel, Mud, Pebbles, SoilForest), die Flaeche fuer Schritte aber hart,
        /// gilt die harte Flaeche (ein poroeser Absorber braucht Schichtdicke ~ lambda/4, Kuttruff Kap. 6 - eine harte Lauffaeche ist keiner).
        /// Sonst das Geschossmaterial. flaeche null/leer = unbekannt.</summary>
        public static string Akustisch(string ballistik, string flaeche)
        {
            if (string.IsNullOrEmpty(ballistik)) return "Stone";
            if (string.IsNullOrEmpty(flaeche) || !Poroes(ballistik)) return ballistik;
            string hart = Hart(flaeche);
            return hart ?? ballistik;
        }

        /// <summary>Poroeser Boden laut Geschossmaterial.</summary>
        public static bool Poroes(string ballistik)
            => ballistik == "Soil" || ballistik == "Gravel" || ballistik == "Mud" || ballistik == "Pebbles" || ballistik == "SoilForest";

        /// <summary>Tabellenmaterial einer harten Schrittflaeche (BaseBallistic.ESurfaceSound-Name), null = nicht hart.</summary>
        public static string Hart(string flaeche)
        {
            switch (flaeche)
            {
                case "Concrete": return "Concrete";
                case "Asphalt": return "Asphalt";
                case "Tile": return "Tile";
                case "Slate": return "Stone";
                case "Metal": return "MetalThick";
                case "MetalThin": return "MetalThin";
                case "Wood": case "WoodThick": return "WoodThick";
                case "WoodThin": return "WoodThin";
                case "Glass": case "WholeGlass": return "Glass";
                case "Plastic": return "Plastic";
                default: return null;   // Soil, Gravel, Grass, Swamp, Puddle, Garbage, GarbageMetal, Snow: Geschossmaterial bleibt
            }
        }

        /// <summary>Pruefsumme der Raumgeometrie (Boxen auf cm gerundet) fuer den Cache je Raum (FNV-1a 64).</summary>
        public static ulong Pruefsumme(Kasten[] ks, int n, string name)
        {
            ulong h = 14695981039346656037UL;
            unchecked
            {
                void M(long v) { h = (h ^ (ulong)v) * 1099511628211UL; }
                if (name != null) foreach (char c in name) M(c);
                for (int i = 0; i < n; i++)
                {
                    var k = ks[i];
                    M(R(k.Mx)); M(R(k.My)); M(R(k.Mz)); M(R(k.Hx)); M(R(k.Hy)); M(R(k.Hz));
                    M(R(k.Ux * 100)); M(R(k.Uy * 100)); M(R(k.Uz * 100)); M(R(k.Wx * 100)); M(R(k.Wy * 100)); M(R(k.Wz * 100));
                }
            }
            return h;
        }
        static long R(double x) => (long)Math.Round(x * 100);
    }
}
