using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // 0.8.4 (Ausbaustufe 1 nach der Machbarkeitsstudie "True Acoustics") - alle Schallwege in echten dB, je Oktavband.
    // Reine Rechnung ohne Unity, damit der Offline-Test (Materialklang-KlangTest) sie mituebersetzen kann. Spiel-Teil: Wege.cs.
    //
    // Konvention (wie RaumwegKern): Pegel RELATIV zum Freifeld-Direktweg in der Direktlinie (die Entfernungskurve rechnet
    // Unity/SEB): dL_i = 10 lg(I_i / I_0), I_0 = W / (4 pi d^2). dL <= 0 heisst leiser als ohne Hindernis.
    // Baender: 0 = Bezugsfrequenz (Einstellung, Standard 250 Hz), 1 = 1 kHz, 2 = 4 kHz.
    //
    //  D (Beugung um eine Kante, Spiel: EdgeDiffractionCalculator, EIN Kantenpunkt): UTD (Uniform Theory of Diffraction,
    //     Kouyoumjian & Pathak 1974) in der Form von Valve Steam Audio (Utd.cs, Apache 2.0), mit dem echten Abstandsparameter
    //     L = s s'/(s+s') aus Quelle, Kantenpunkt und Hoerer. Keilwinkel: das Spiel kennt keine Flaechen -> Einstellung (90 Grad = Hausecke).
    //     Der Winkelfaktor des Spiels (energyFactor) wird NICHT zusaetzlich angewendet - der Winkel steckt in der UTD.
    //  R (Reflexion, Spiel: ReflectionCalculator, kuerzester Weg Quelle -> Treffer -> ... -> Hoerer):
    //     dL_R(f) = -20 lg(L_R/d) + Sum_k 10 lg(1 - alpha_k(f)) - n * Zusatzverlust
    //     Spiegelquellen-Naeherung (Kuttruff, Room Acoustics, Kap. 4). Ersetzt den Spielverlust 0,7 je Treffer und die lineare
    //     Abstandsdaempfung des Spiels. Zusatzverlust (Einstellung, Annahme): der letzte Abschnitt zum Hoerer ist im Spiel ein
    //     Sichttest vom Treffpunkt aus, kein Spiegelweg - die Flaeche ist endlich und streut (siehe README).
    //  Summe: E_b = Sum_i 10^(dL_i,b / 10) ueber alle Wege, dL_b = 10 lg(min(E_b, 1)). Deckel 0 dB: freie Sicht bleibt 0 dB,
    //     Reflexionen machen einen freien Klang nicht lauter (fruehe Reflexionen rendert Meta XR aus seiner Geometrie).
    // ------------------------------------------------------------------------------------------

    /// <summary>Letzter Wert eines Wegs (D oder R) zum Halten, solange das Spiel ihn nicht neu rechnet.</summary>
    internal sealed class WegSpeicher
    {
        public bool Hat, Direkt;
        public int Fehl;                                           // so viele Rechnungen in Folge gelaufen, aber nicht gefunden
        public readonly double[] Db = new double[WegeKern.N];    // dL je Band (<= 0)
        public V3 Q, H;                                            // Quelle/Hoerer bei der Messung
        public void Setzen(double[] db, V3 q, V3 h, bool direkt = false) { Hat = true; Direkt = direkt; Fehl = 0; Array.Copy(db, Db, WegeKern.N); Q = q; H = h; }
        public void Vergessen() { Hat = false; Direkt = false; Fehl = 0; }
    }

    /// <summary>0.8.5: zeitliche Ueberblendung eines Wegs (D oder R) in Energie je Band: von dem, was gerade zu hoeren ist,
    /// linear in 'dauer' Sekunden zum neuen Ziel. Gleiches Prinzip wie der Raumweg (RoutenMischer): ein Weg, der dazukommt,
    /// blendet ein, einer, der verschwindet, blendet mit seinem letzten Wert aus; springt er (Haltestufe, Hysterese, neue Kante),
    /// blendet der alte Stand aus und der neue ein (in Energie dasselbe wie die lineare Ueberblendung).</summary>
    internal sealed class WegBlende
    {
        public bool Hat;
        public double T0;
        public readonly double[] Alt = new double[WegeKern.N], Ziel = new double[WegeKern.N];
        public double Anteil(double jetzt, double dauer) => !Hat || dauer <= 0 ? 1.0 : Math.Max(0.0, Math.Min(1.0, (jetzt - T0) / dauer));
        public void Wert(double jetzt, double dauer, double[] aus)
        {
            double k = Anteil(jetzt, dauer);
            for (int b = 0; b < WegeKern.N; b++) aus[b] = Alt[b] + (Ziel[b] - Alt[b]) * k;
        }
        public void Setzen(double[] ziel, double jetzt, double dauer)
        {
            if (Hat && dauer > 0) Wert(jetzt, dauer, Alt);
            else Array.Copy(ziel, Alt, WegeKern.N);
            Array.Copy(ziel, Ziel, WegeKern.N);
            T0 = jetzt; Hat = true;
        }
        public bool Laeuft(double jetzt, double dauer)
        {
            if (!Hat || Anteil(jetzt, dauer) >= 1.0) return false;
            for (int b = 0; b < WegeKern.N; b++) if (Math.Abs(Ziel[b] - Alt[b]) > 1e-9) return true;
            return false;
        }
        public void Vergessen() { Hat = false; }
    }

    /// <summary>Ergebnis einer fertigen Rechnung (je Rechner des Spiels), Wege einzeln und als Summe.</summary>
    internal sealed class WegZustand
    {
        public bool Gueltig;            // es gibt ein Ergebnis
        public bool Frei;               // das Spiel hat "frei" gemeldet
        public bool HatT, HatD, HatR;   // Weg traegt bei (D/R auch gehalten)
        public bool DGehalten, RGehalten, DDirekt, RDirekt, RHysterese;
        public bool UmwegUnbekannt;     // 0.8.8: diese Rechnung fand keinen Umweg (kein freier Strahl, keine Kante, keine Reflexion) - Log
        // umwegsuche (13.11.): Ergebnis der eigenen Wegsuche statt der Rueckfallgrenze (nur bei UmwegUnbekannt, UmwegKern/Auswerten)
        public bool HatS;               // angewendet in dieser Rechnung
        public readonly double[] S = new double[WegeKern.N];         // Energiesumme der Kantenwege der Suche je Band (dB, <= 0)
        public readonly WegBlende SBl = new WegBlende();               // Ueberblendung des Suchanteils (wie D/R)
        public int SWege, SKanten;      // Log: Zahl der Kantenwege, Kanten des lautesten Wegs (1 kHz)
        public bool SHatKante; public V3 SKante;   // Richtungsangabe: letzter Kantenpunkt des lautesten Wegs vor dem Hoerer (nur Log/bereitgestellt)
        public double SAlterMs;         // Log: Alter des Suchergebnisses
        public UmwegKern.Grund SGrund;  // Log: warum (nicht) angewendet; KeinNetz = Schalter aus / keine Suche
        // wegtabelle (13.11.): S kommt aus der vorab gerechneten Tabelle (Schallweg/SwTabelle.cs) statt aus der Live-Suche. Richtungsdaten fuer
        // klang-richtung: Gesamtlaenge des lautesten Wegs, erster Kantenpunkt nach der Quelle (SErste), letzter vor dem Hoerer (SKante), Kanten (SKanten).
        public bool STab; public double SLaenge; public V3 SErste; public int STabQ = -1, STabH = -1, STabRaum = -1; public int STabFern, STabStrahlen; public bool STabSicht;   // wegsicht: Fernpaare, Sichtpruefungen
        public int STabGrund = -1;       // WegTabelle.Grund der letzten Abfrage ohne Wert (Log), -1 = keine Abfrage
        public readonly double[] TabGlatt = new double[WegeKern.N];   // Tabellenwert nach der Ratenbegrenzung (TabGleitKern), Baender der Mod
        public bool TabHat; public double TabZeit;                     // TabGlatt gilt / Zeit der letzten Abfrage (s)
        public int Bester;              // 1 = T, 2 = D, 3 = R (lautester Weg im Bezugsband), 0 = keiner / frei
        public readonly double[] T = new double[WegeKern.N], D = new double[WegeKern.N], R = new double[WegeKern.N];
        public readonly double[] TWand = new double[WegeKern.N];      // T nur durch die Wand (freie Strahlen nicht mitgezaehlt)
        public double TDiffus;          // diffus-hall: Diffusanteil (Hoererraum-Hallfeld) der Energie durch die Wand, Bezugsband, 0..1
        // hallsend (B2, 12.11.): dasselbe bei 1 kHz / 4 kHz (NaN = unbekannt -> wie das Bezugsband)
        public double TDiffus1 = double.NaN, TDiffus2 = double.NaN;
        public double TDiffusBand(int b) => b == 1 && !double.IsNaN(TDiffus1) ? TDiffus1 : b == 2 && !double.IsNaN(TDiffus2) ? TDiffus2 : TDiffus;
        // hallsend (A2, 12.11.): Quelle und Hoerer stehen im selben gemessenen Raum (HallZonen.RaumFuer(Hoerer, Quelle).QuelleDrin) -
        // dann schattet das Hindernis nur den Direktschall ab, das Hallfeld des Raums bleibt (DiffusKern.RaumHall).
        public bool GleicherRaum;
        public double RaumHallSend = double.NaN, RaumHallFehl;   // hallsend (A2): zuletzt angewandter Send-Zusatz / fehlender Hall (dB, Klang-Log)
        public double TFrei;            // Anteil der freien T-Strahlen (0..1) = geometrischer Anteil am Rand eines Hindernisses
        public readonly double[] Ergebnis = new double[WegeKern.N];   // Summe (oder bester Weg, wenn die Summe aus ist)
        public double Wertung;          // -Ergebnis[0] / Dynamik, 0..1
        public double LD, LR, Direkt;   // Weglaengen (Log)
        public int RAnzahl;             // Reflexionen des R-Wegs (Log)
        public readonly WegSpeicher DSp = new WegSpeicher(), RSp = new WegSpeicher();
        public int Rechnungen;
        // 0.8.5: zeitliche Ueberblendung von D und R (WegBlende); Jetzt = was gerade gilt (Ergebnis = Ziel der Rechnung)
        public readonly WegBlende DBl = new WegBlende(), RBl = new WegBlende();
        public readonly double[] Jetzt = new double[WegeKern.N];
        public double WertungJetzt;
        public bool Blendet;

        public void Vergessen()
        {
            Gueltig = Frei = HatT = HatD = HatR = DGehalten = RGehalten = DDirekt = RDirekt = RHysterese = UmwegUnbekannt = false;
            HatS = SHatKante = false; SGrund = UmwegKern.Grund.KeinNetz; SBl.Vergessen();
            STab = TabHat = false; STabGrund = -1; STabQ = STabH = STabRaum = -1; SLaenge = 0; STabFern = STabStrahlen = 0; STabSicht = false;   // wegtabelle
            Bester = 0; Wertung = 0; TDiffus = 0; TDiffus1 = TDiffus2 = double.NaN; GleicherRaum = false; RaumHallSend = double.NaN; RaumHallFehl = 0; DSp.Vergessen(); RSp.Vergessen(); DBl.Vergessen(); RBl.Vergessen(); Blendet = false; WertungJetzt = 0;
        }
    }

    internal static class WegeKern
    {
        public const int N = 3;
        public const double C = 343.0;
        public const double KeinWeg = -1000.0;

        // ---------------------------------------------------------------- 10.11.2026 (Klang-Log Factory 18:00:34.603): Deckel nur fuer die Lautstaerke
        // Ursache "leiser, aber heller Schuss": die Banddaemmungen eines Wegs wurden JE BAND bei "Max. insulation" (60 dB) gedeckelt
        // (RaumwegKern.Route, RaumwegNetz.Baender/Schirmen, RoutenMischer.Summe, Wand zwischen Raeumen, Wanddaempfung). Liegt ein Weg in
        // allen Baendern ueber dem Deckel (Log: "rooms 60.0 (4k 60.0)"), war die Mehrdaempfung der Hochbaender 0 und der Tiefpass-Fit
        // offen (22 kHz), obwohl die Daemmung mit der Frequenz stieg. Ausserdem wurden gedeckelte Wege energetisch summiert: drei Routen
        // ueber 60 dB ergaben 60 - 10 lg 3 = 55,2 dB (Log 18:00:36.053) statt mindestens 60.
        // Jetzt: Baender ungedeckelt bis zur Rechengrenze BandGrenzeDb (nur gegen lg 0; weit jenseits jedes hoerbaren Werts), Summen
        // ueber die echten Werte, und erst wo die Lautstaerke entsteht: Bezugsband auf Max. insulation, die Hochbaender um denselben
        // Betrag mit (Deckeln) - die Klangfarbe (Abstand der Baender) bleibt die der echten Daemmung.

        /// <summary>Rechengrenze der Banddaemmung (dB): "keine Energie" (lg 0) und Kappungen ohne physikalische Bedeutung.</summary>
        public const double BandGrenzeDb = 200.0;

        /// <summary>Deckel nur auf die Lautstaerke: Bezugsband db[0] hoechstens maxDb, die anderen Baender um denselben Betrag
        /// verschoben (Abstand zum Bezugsband = Klangfarbe bleibt). Unter dem Deckel unveraendert.</summary>
        public static void Deckeln(double[] db, double maxDb)
        {
            double ueber = db[0] - maxDb;
            if (!(ueber > 0)) return;
            for (int b = 0; b < db.Length; b++) db[b] -= ueber;
        }

        /// <summary>Wie Deckeln fuer drei einzelne Werte.</summary>
        public static void Deckeln(ref double d0, ref double d1, ref double d2, double maxDb)
        {
            double ueber = d0 - maxDb;
            if (!(ueber > 0)) return;
            d0 -= ueber; d1 -= ueber; d2 -= ueber;
        }

        /// <summary>
        /// 0.8.8 (Meldung Streets 09.11.2026 "Schuesse mal lauter, mal leiser"): Grenze der Abschirmung im Hindernis-Zweig.
        /// Den Hindernis-Zweig nimmt das Spiel nur, wenn Quelle und Hoerer im selben Raum der Raumdaten liegen (draussen: beide in
        /// CommonOutdoor) - dann gibt es immer einen Weg ueber und um die Hindernisse. Das Spiel sucht aber nur EINE Kante nahe dem
        /// ersten Treffer der Sichtlinie (EdgeDiffractionCalculator) und misst Durchgang nur durch EIN Objekt; zwischen Haeusern auf
        /// 80-200 m findet es oft nichts (alle 98 T-Strahlen "zwei Objekte"/"zu dick", keine Kante, keine Reflexion). Das galt als
        /// volle Daemmung 60 dB, ein Schuss mit gefundener Kante/Reflexion als -1..-15 dB: derselbe Schuetze sprang je Rechnung um
        /// bis zu 59 dB. ISO 9613-2:1996 Abschn. 7.4: die Abschirmung Dz ist auf 20 dB (Einfach-) bzw. 25 dB (Mehrfachbeugung)
        /// begrenzt. Das gilt hier als Untergrenze des Hindernis-Ergebnisses, egal ob die Spielsuche den Umweg gefunden hat.
        /// </summary>
        public const double SchirmMaxDb = 25.0;

        // ------------------------------------------------------------------------------------------
        // schirmgrenze (12.11.2026, Marke 3 Factory 23:06:59: 91 Schusszeilen "through walls" mit lp 22 kHz; Bot7 57 m, alle 98 T-Strahlen
        // "zwei Objekte", keine Kante, keine Reflexion -> -25/-25/-25 dB): die Grenze war FLACH ueber die Baender. ISO 9613-2:1996 Gl. (14):
        //   Dz = 10 lg(3 + C2/lambda * C3 * z * Kmet),  C2 = 20,  C3 = 3 fuer Mehrfachbeugung mit e >> lambda (Gl. 15), Deckel 25 dB je Band.
        // Dz steigt mit der Frequenz; der Deckel bindet zuerst im hoechsten Band. Ohne gefundenen Umweg ist z unbekannt - die Norm selbst gibt
        // dann nur den Deckel. Begruendete Bandkurve: der KLEINSTE Umweg z*, bei dem der Deckel im hoechsten Band gerade erreicht wird
        //   z* = (10^(25/10) - 3) * lambda_hoch / (C2 * C3)    (4 kHz: 0,45 m),   Grenze_b = min(25, Dz(f_b, z*))   -> 13,5 / 19,1 / 25,0 dB.
        // Das hoechste Band bleibt bei 25 dB (wie bisher), darunter folgt die Grenze der Beugung: ein Tiefpass entsteht von selbst.
        // Kmet = 1 (Raeume; draussen bei z* < 0,5 m und d <= 250 m: Kmet >= 0,6 -> hoechstens 1,5 dB weniger, vernachlaessigt).
        // Gegenprobe mit der eigenen Wegsuche (UTD ueber die echte Geometrie, review-20261110/schirmgrenze/boden.py, alle Logs vom 12.11.):
        // 137 Rechnungen an der Grenze, Kantenweg der Suche Median -8,1/-15,0/-24,8 dB (10-90 %: 250 Hz -21,7..-1,5), Neigung 250->4k Median
        // 13,9 dB. Die flache Grenze lag bei 250 Hz im Median 17 dB zu tief und ohne jede Neigung; die neue liegt 5 dB unter dem Median.
        // Gleiche Formel wie HindernisKern.Dz (RichtungKern.cs), hier ausgeschrieben, weil die Wegsuche-Tests WegeKern ohne RichtungKern binden.
        // ------------------------------------------------------------------------------------------
        public const double SchirmC2 = 20.0, SchirmC3 = 3.0;
        static readonly double[] StandardF = { 250, 1000, 4000 };

        /// <summary>schirmgrenze: kleinster Umweg (m), bei dem die Abschirmung im Band fHoch den Deckel SchirmMaxDb erreicht.</summary>
        public static double SchirmUmweg(double fHoch) => (Math.Pow(10, SchirmMaxDb / 10) - 3) * (C / Math.Max(fHoch, 1)) / (SchirmC2 * SchirmC3);

        /// <summary>schirmgrenze: Grenze der Abschirmung (dB, > 0) im Band f; fHoch = hoechstes Band. Hoechstens SchirmMaxDb und maxDb.</summary>
        public static double SchirmDb(double f, double fHoch, double maxDb)
        {
            double z = SchirmUmweg(fHoch);
            double dz = 10 * Math.Log10(3 + SchirmC2 / (C / Math.Max(f, 1)) * SchirmC3 * z);
            return Math.Max(0, Math.Min(Math.Min(SchirmMaxDb, maxDb), dz));
        }

        /// <summary>
        /// Beugung um EINE Kante nach UTD (Utd.cs, portiert aus Valve Steam Audio): dL (dB, &lt;= 0) gegenueber dem Freifeld in
        /// Direktlinie. q = Quelle, kante = Kantenpunkt (Spiel: DiffractionPathResult.diffractionPoint), h = Hoerer, n = Keilzahl.
        /// UTD (Kouyoumjian-Pathak): u_d/u_0 = |D| / sqrt(L) * d / (s + s'), L = s s' / (s + s'), Ablenkwinkel wie Steam Audio
        /// (Winkel zwischen einfallender und weiterlaufender Richtung). Einfallswinkel alpha_i zur Keilflaeche: seit oeffnung (16.11.)
        /// aus den Flaechen des Keils, wenn bekannt (Ueberladung mit Normalen), sonst Mitte des moeglichen Bereichs (siehe unten).
        /// </summary>
        public static double Beugung(V3 q, V3 kante, V3 h, double f, double n = 1.5)
            => Beugung(q, kante, h, f, n, default, default, default, default);

        // ------------------------------------------------------------------------------------------
        // oeffnung/B (16.11.2026, Befund wellenfeld 4.1, nachgerechnet review-20261110/oeffnung/beugwinkel.py gegen Kouyoumjian & Pathak 1974,
        // Proc. IEEE 62, Gl. 25-27, unabhaengige Rechnung wellenfeld/utd.py): Beugung uebergab alpha_i = 0 (Einfall streifend entlang der
        // Keilflaeche, Steam-Audio-Vorgabe). Dann fallen Einfalls- und Spiegelterm zusammen (Quelle "auf" der Flaeche) und |D| ist bis zu
        // doppelt so gross: Halbebene/90-Grad-Ecke, Quelle 5-60 Grad ueber der Flaeche, Ablenkung 5-80 Grad, 250 Hz-4 kHz: im Mittel
        // +2,2...+3,0 dB, hoechstens +5,5 dB zu laut; mit dem wahren alpha_i +-0,03 dB.
        //  - Flaechen bekannt (Wegenetz: Normalen N1/N2 nach aussen, Winkelhalbierende Aus ins Freie, Kante t = B - A): alpha_i = Winkel der
        //    Quellrichtung (senkrecht zur Kante projiziert) zur Flaeche auf der Quellseite, durch die Luft gemessen.
        //  - Flaechen unbekannt (Hindernis-Zweig: Kantenpunkt des Spiels): die Quelle kann ueberall zwischen Flaeche (0) und der Lage liegen,
        //    bei der der Hoerer auf der Rueckflaeche liegt ((n - 1) pi - Ablenkung); genommen wird die Mitte. Fehler dann im Mittel
        //    -0,5...-1,0 dB, hoechstens -3,5 dB (Quelle dicht an der Flaeche), statt +2,2...+3,0 / +5,5 dB.
        // ------------------------------------------------------------------------------------------

        /// <summary>oeffnung/B: Einfallswinkel (rad) der Quelle zur Keilflaeche auf ihrer Seite, gemessen durch die Luft; NaN = Flaechen unbekannt.
        /// n1/n2 = Flaechennormalen nach aussen, aus = Winkelhalbierende ins Freie, t = Kantenrichtung.</summary>
        public static double EinfallWinkel(V3 q, V3 kante, V3 h, V3 n1, V3 n2, V3 aus, V3 t)
        {
            double tl = t.Laenge;
            if (!(tl > 1e-9) || !(n1.Laenge > 1e-9) || !(n2.Laenge > 1e-9) || !(aus.Laenge > 1e-9)) return double.NaN;
            t = t * (1.0 / tl);
            V3 Kreuz(V3 a, V3 b) => new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
            V3 Flaeche(V3 nn)   // Richtung entlang der Flaeche, weg von der Kante (vom Freien weg)
            {
                V3 e = Kreuz(nn, t); double l = e.Laenge; if (!(l > 1e-9)) return default;
                e = e * (1.0 / l); return V3.Dot(e, aus) > 0 ? e * -1.0 : e;
            }
            V3 e1 = Flaeche(n1), e2 = Flaeche(n2);
            if (e1.IstNull || e2.IstNull) return double.NaN;
            V3 v = Kreuz(t, e1); if (V3.Dot(v, aus) < 0) v = v * -1.0;
            double Phi(V3 x)
            {
                x = x - t * V3.Dot(x, t);
                double w = Math.Atan2(V3.Dot(x, v), V3.Dot(x, e1));
                return w < 0 ? w + 2 * Math.PI : w;
            }
            double ps = Phi(q - kante), pr = Phi(h - kante), spann = Phi(e2);
            if (spann < 1e-3) spann = 2 * Math.PI;   // Platte: beide Flaechen in derselben Richtung
            return ps <= pr ? ps : Math.Max(0, spann - ps);
        }

        /// <summary>oeffnung/B: Breite der Uebergangszone an der Schattengrenze (Fresnelzahl), wie die Sichtzonen-Fortsetzung bei Kurze-Anderson.</summary>
        public const double UebergangN = 0.2;

        /// <summary>Beugung wie oben, mit den Flaechen des Keils (default = unbekannt).</summary>
        public static double Beugung(V3 q, V3 kante, V3 h, double f, double n, V3 n1, V3 n2, V3 frei, V3 t)
        {
            double s1 = Math.Max(V3.Abstand(q, kante), 0.05), s2 = Math.Max(V3.Abstand(kante, h), 0.05);
            double d = Math.Max(V3.Abstand(q, h), 0.05);
            V3 ein = (kante - q) * (1.0 / s1), aus = (h - kante) * (1.0 / s2);
            double cos = Math.Max(-1.0, Math.Min(1.0, V3.Dot(ein, aus)));
            double winkel = Math.Max(1e-6, Math.Acos(cos));
            double l = s1 * s2 / (s1 + s2);
            // Gesamtreview A1: ein Keil mit Aussenwinkel n*pi laesst (Einfall entlang der Flaeche) hoechstens die Ablenkung
            // (n - 1)*pi zu - dahinter laege der Hoerer IM Keil, die UTD-Formel gilt dort nicht mehr (|D| steigt wieder an).
            // Zeigt die Geometrie eine groessere Ablenkung, ist die Kante tatsaechlich spitzer als eingestellt: wirksames n so,
            // dass der Hoerer genau auf der Rueckflaeche liegt (bei 180 Grad n = 2, die duenne Kante wie Steam Audio).
            n = Math.Max(n, Math.Min(2.0, 1.0 + winkel / Math.PI));
            // oeffnung/B: Einfallswinkel; zulaessig 0 .. (n - 1) pi - Ablenkung (sonst laege der Hoerer im Keil)
            double amax = Math.Max(0.0, (n - 1.0) * Math.PI - winkel);
            double ai = EinfallWinkel(q, kante, h, n1, n2, frei, t);
            ai = double.IsNaN(ai) ? 0.5 * amax : Math.Max(0.0, Math.Min(amax, ai));
            // Uebergang an der Schattengrenze: auf der Sichtseite rechnet die Mod den Direktschall allein (0 dB, ohne das gebeugte Feld,
            // das ihn dort bis -6 dB an der Grenze absenkt). Damit der Pegel beim Eintritt in den Schatten nicht springt, waechst alpha_i
            // ueber die Uebergangszone N = 0 .. 0,2 von 0 (Grenze: |D| wie bisher, 0 dB) auf den wahren Wert - spiegelbildlich zur Fortsetzung
            // der Sichtzone bei Kurze-Anderson (N = -0,2 .. 0, RaumwegKern.KurzeAnderson). Ausserhalb der Zone gilt der wahre Einfallswinkel.
            double nFr = 2.0 * Math.Max(0.0, s1 + s2 - d) * f / C;
            if (nFr < UebergangN) ai *= nFr / UebergangN;
            double dk = Utd.Koeffizient(winkel, f, n, ai, l, C);
            double kantenDb = Math.Min(0.0, 20.0 * Math.Log10(Math.Max(dk, 1e-12) / Math.Sqrt(l)));
            return kantenDb - 20.0 * Math.Log10(Math.Max(1.0, (s1 + s2) / d));
        }

        /// <summary>
        /// Gesamtreview (Blendgranate Ground Zero 09.11.2026, MARK 1): hat KEIN Strahl der Durchdringungsrechnung ein Ergebnis -
        /// kein freier, kein gemessener (gleicher Collider von beiden Seiten) - sondern nur Strahlen, die auf einer Seite nichts
        /// treffen (Endpunkt im Collider, einseitiges Mesh) oder auf zwei verschiedene Objekte, ist die Daemmung unbekannt. Das Spiel
        /// schreibt dafuer NoPath (Wertung 1), im Physik-Modus wuerde daraus die volle Dynamik (60 dB). "Zu dick" (&gt;= maxThickness)
        /// ist dagegen gemessen und zaehlt als Ergebnis (voll verdeckt zu Recht).
        /// </summary>
        public static bool TOhneErgebnis(int frei, int gemessen, int einseitig, int zweiObjekte)
            => frei <= 0 && gemessen <= 0 && einseitig + zweiObjekte > 0;

        /// <summary>Keilzahl n aus dem Innenwinkel des Keils (Grad): 0 = duenne Kante (n = 2), 90 = Hausecke (n = 1,5).</summary>
        public static double KeilN(double innenGrad) => (360.0 - Math.Max(0.0, Math.Min(170.0, innenGrad))) / 180.0;

        /// <summary>Vergleich (vor der UTD, nur Test): Kurze-Anderson, d = Direktlinie, l = Weg ueber die Kante.</summary>
        public static double BeugungKA(double d, double l, double f)
        {
            d = Math.Max(d, 0.1);
            l = Math.Max(l, d);
            double umweg = l - d;
            return -20.0 * Math.Log10(l / d) - RaumwegKern.KurzeAnderson(RaumwegKern.Fresnel(umweg, f));
        }

        /// <summary>Maekawa (1968) als Gegenprobe: A = 10 lg(3 + 20 N), N &gt;= 0.</summary>
        public static double Maekawa(double n) => 10.0 * Math.Log10(3.0 + 20.0 * Math.Max(0.0, n));

        /// <summary>Reflexionsweg: dL (dB) bei Weglaenge l, n Reflexionen mit alpha[k] (dieses Band), Zusatzverlust je Reflexion (dB).</summary>
        public static double Reflexion(double d, double l, double[] alpha, int n, double zusatzDb)
        {
            d = Math.Max(d, 0.1);
            l = Math.Max(l, d);
            double db = -20.0 * Math.Log10(l / d);
            for (int k = 0; k < n; k++)
            {
                double a = alpha != null && k < alpha.Length ? alpha[k] : 0.1;
                a = a < 0 ? 0 : (a > 0.999 ? 0.999 : a);
                db += 10.0 * Math.Log10(1.0 - a) - zusatzDb;
            }
            return db;
        }

        public static double Energie(double db) => db <= KeinWeg ? 0.0 : Math.Pow(10.0, db / 10.0);
        public static double DbAus(double e, double minDb) => e <= 0 ? minDb : Math.Max(minDb, 10.0 * Math.Log10(e));

        /// <summary>Energiesumme je Band, gedeckelt bei 0 dB, nach unten bei -maxDb. Keiner da -> -maxDb.</summary>
        public static void Summe(double[] t, bool hatT, double[] d, bool hatD, double[] r, bool hatR, double maxDb, double[] aus)
        {
            for (int b = 0; b < N; b++)
            {
                double e = 0;
                if (hatT) e += Energie(t[b]);
                if (hatD) e += Energie(d[b]);
                if (hatR) e += Energie(r[b]);
                aus[b] = Math.Min(0.0, DbAus(e, -maxDb));
            }
        }

        /// <summary>
        /// Energiesumme mit getrenntem T: E = E(T durch die Wand) + G + E(R), G = max(Anteil freier T-Strahlen, E(D)).
        /// Freie T-Strahlen und der Beugungsweg beschreiben am Rand eines Hindernisses DIESELBE Energie (Schall, der an der Kante
        /// vorbeikommt) - sie werden darum nicht addiert, sondern der groessere zaehlt. Meldet D "Sichtlinie frei", gilt 0 dB
        /// (die UTD hat an der Schattengrenze 0 dB, so geht der Pegel stetig in den Schatten ueber).
        /// </summary>
        public static void SummeGetrennt(double[] tWand, double tFrei, bool hatT, double[] d, bool hatD, bool dDirekt,
                                         double[] r, bool hatR, bool rDirekt, double maxDb, double[] aus, double[] s = null)
        {
            for (int b = 0; b < N; b++)
            {
                double g = hatT ? tFrei : 0;
                if (hatD) g = dDirekt ? 1.0 : Math.Max(g, Energie(d[b]));   // UTD ist an der Schattengrenze 0 dB -> stetig zur Sichtlinie
                // umwegsuche: die Kantenwege der eigenen Suche sind dieselbe Art Energie wie D (Schall um die Kanten der Einbauten) - sie
                // stehen im Maximum, nicht in der Summe: ein (gehaltener) Kantenweg des Spiels und der Suche ueber dieselbe Kante zaehlen einmal
                if (s != null) g = Math.Max(g, Energie(s[b]));
                double e = g;
                if (hatT) e += Energie(tWand[b]);
                if (hatR && !rDirekt) e += Energie(r[b]);
                else if (hatR && !hatT) e = Math.Max(e, 1.0);
                aus[b] = Math.Min(0.0, DbAus(e, -maxDb));
            }
        }

        /// <summary>Haltefaktor eines gemerkten Wegs nach Bewegung (m) von Quelle/Hoerer: 1 bis 'nah', 0 ab 'fern', dazwischen linear.</summary>
        public static double Haltefaktor(double bewegung, double nah, double fern)
        {
            if (bewegung <= nah) return 1.0;
            if (bewegung >= fern || fern <= nah) return 0.0;
            return 1.0 - (bewegung - nah) / (fern - nah);
        }

        /// <summary>
        /// Eine fertige Rechnung auswerten (alle Werte als dL &lt;= 0 je Band).
        /// frei = das Spiel meldet "frei" (Direkttest) -> alles 0 dB.
        /// T: tWand = nur die Energie durch Waende (freie Strahlen = 0), tFrei = Anteil freier Strahlen.
        /// D: ranD/dDirekt (Sichtlinie frei = 0 dB)/dHat (Kante gefunden)/d; R: ranR/rDirekt (Direktweg frei)/rHat/r.
        /// Nicht gelaufene Wege: gemerkter Wert, abgeblendet mit der Bewegung (haltNah..haltFern m). Gelaufen, aber nichts gefunden:
        /// der gemerkte Wert klingt um 'abklingDb' je Rechnung ab (Monte-Carlo-Strahlen finden den Weg nicht jedes Mal).
        /// R-Hysterese: neuer R-Wert wird erst uebernommen, wenn er sich um mehr als 'hystDb' (Bezugsband) aendert.
        /// summe = Energiesumme (sonst: lautester Weg im Bezugsband, wie das Minimum der Wertungen).
        /// </summary>
        public static void Auswerten(WegZustand z, bool frei,
            bool ranT, double[] tWand, double tFrei,
            bool ranD, bool dDirekt, bool dHat, double[] d,
            bool ranR, bool rDirekt, bool rHat, double[] r,
            V3 q, V3 h, double maxDb, bool summe,
            double haltNah = 1.0, double haltFern = 3.0, double abklingDb = 3.0, double hystDb = 1.0, double[] freiT = null, double[] f = null,
            double[] suche = null)
        {
            z.Gueltig = true; z.Rechnungen++;
            z.Frei = frei;
            z.DGehalten = z.RGehalten = z.DDirekt = z.RDirekt = z.RHysterese = z.UmwegUnbekannt = z.HatS = false;
            if (frei)
            {
                // Spiel: Direkttest T <= Freiweg -> "frei". Statt hart 0 dB gilt der (kleine) Wert des Direkttests, sonst springt der Pegel
                // an der Freiweg-Schwelle um bis zu 1,2 dB (Grenzfall-Test). freiT = null (ausser Reichweite) -> 0 dB.
                for (int b = 0; b < N; b++) { z.T[b] = z.Ergebnis[b] = freiT != null ? Math.Max(-maxDb, Math.Min(0, freiT[b])) : 0; z.D[b] = z.R[b] = 0; z.TWand[b] = KeinWeg; }
                z.TFrei = 1; z.HatT = true; z.HatD = z.HatR = false; z.Bester = 0;
                z.Wertung = Math.Max(0, Math.Min(1, -z.Ergebnis[0] / maxDb));
                z.DSp.Vergessen(); z.RSp.Vergessen();
                return;
            }
            z.HatT = ranT;
            z.TFrei = ranT ? Math.Max(0, Math.Min(1, tFrei)) : 0;
            for (int b = 0; b < N; b++)
            {
                z.TWand[b] = ranT ? Math.Min(0, tWand[b]) : KeinWeg;
                z.T[b] = ranT ? Math.Max(-maxDb, Math.Min(0, DbAus(z.TFrei + Energie(z.TWand[b]), -maxDb))) : KeinWeg;
            }

            z.HatD = Weg(z.DSp, z.D, ranD, dDirekt, dHat, d, q, h, maxDb, haltNah, haltFern, abklingDb, 0, out z.DGehalten, out _);
            z.DDirekt = z.HatD && z.DSp.Direkt;
            z.HatR = Weg(z.RSp, z.R, ranR, rDirekt, rHat, r, q, h, maxDb, haltNah, haltFern, abklingDb, hystDb, out z.RGehalten, out z.RHysterese);
            z.RDirekt = z.HatR && z.RSp.Direkt;
            // 0.8.8 UmwegUnbekannt (Log/Diagnose): diese Rechnung hat keinen Umweg gefunden (kein freier Strahl, keine Kante, keine Reflexion).
            z.UmwegUnbekannt = UmwegUnbekanntAus(z.TFrei, ranD, dDirekt, dHat, ranR, rDirekt, rHat);
            // umwegsuche: NUR in diesem Fall ersetzt das Ergebnis der Wegsuche die Rueckfallgrenze (Schranken: unten die Grenze, oben 0 dB)
            if (suche != null && z.UmwegUnbekannt)
            {
                for (int b = 0; b < N; b++) z.S[b] = suche[b] <= KeinWeg ? KeinWeg : Math.Max(-BandGrenzeDb, Math.Min(0.0, suche[b]));
                z.HatS = z.S[0] > KeinWeg || z.S[1] > KeinWeg || z.S[2] > KeinWeg;
            }

            Zusammenfassen(z, summe, maxDb, f);
        }

        /// <summary>umwegsuche: Ende von Auswerten (lautester Weg, Summe, Schirmgrenze, Wertung) aus den Feldern von z - auch fuer ein
        /// Suchergebnis, das zwischen zwei Rechnungen des Spiels ankommt (SucheSetzen). Rechnet genau wie bis 12.11. das Ende von Auswerten.</summary>
        static void Zusammenfassen(WegZustand z, bool summe, double maxDb, double[] f)
        {
            // lautester Weg (Bezugsband) - fuer das Log und fuer "Summe aus"
            z.Bester = 0; double best = double.NegativeInfinity;
            if (z.HatT && z.T[0] > best) { best = z.T[0]; z.Bester = 1; }
            if (z.HatD && z.D[0] > best) { best = z.D[0]; z.Bester = 2; }
            if (z.HatR && z.R[0] > best) { best = z.R[0]; z.Bester = 3; }
            if (z.HatS && z.S[0] > best) { best = z.S[0]; z.Bester = 4; }   // umwegsuche: 4 = Kantenweg der eigenen Suche

            if (summe) SummeGetrennt(z.TWand, z.TFrei, z.HatT, z.D, z.HatD, z.DDirekt, z.R, z.HatR, z.RDirekt, maxDb, z.Ergebnis, z.HatS ? z.S : null);
            else
            {
                double[] src = z.Bester == 1 ? z.T : z.Bester == 2 ? z.D : z.Bester == 3 ? z.R : z.Bester == 4 ? z.S : null;
                for (int b = 0; b < N; b++) z.Ergebnis[b] = src != null ? Math.Max(-maxDb, Math.Min(0, src[b])) : -maxDb;
            }
            // 0.8.8: Schirmgrenze (SchirmMaxDb): im Hindernis-Zweig gibt es immer einen Weg um die Hindernisse - hoechstens 25 dB.
            // schirmgrenze: je Band (SchirmDb), nicht mehr flach
            var ff = f ?? StandardF;
            for (int b = 0; b < N; b++) z.Ergebnis[b] = Math.Max(z.Ergebnis[b], -SchirmDb(ff[b], ff[N - 1], maxDb));
            z.Wertung = Math.Max(0, Math.Min(1, -z.Ergebnis[0] / maxDb));
        }

        /// <summary>
        /// umwegsuche: Ergebnis der Wegsuche fuer die LETZTE Rechnung des Spiels setzen oder zuruecknehmen (suche = null), wenn es zwischen zwei
        /// Rechnungen ankommt oder veraltet. Grund: das Spiel rechnet einen Hindernis-Weg selten neu (LogOutput 12.11. 23:03: 27 fertige
        /// Rechnungen je 30 s fuer alle Quellen), die Suche braucht 0,1-1 s - nur zur Zeit der Rechnung gefragt, kaeme ihr Ergebnis meist erst
        /// zur naechsten an (dann oft schon > 3 s alt). Gilt nur, wenn die letzte Rechnung keinen Umweg fand (sonst false, nichts geaendert).
        /// Mit Summe gleitet der Suchanteil (SBl, wie D/R) ueber 'dauer'; D/R laufen unveraendert weiter. true = etwas geaendert.
        /// </summary>
        public static bool SucheSetzen(WegZustand z, double[] suche, bool summe, double maxDb, double[] f, double jetzt, double dauer)
        {
            if (z == null || !z.Gueltig || z.Frei || !z.UmwegUnbekannt) return false;
            bool hat = false; bool gleich = true;
            for (int b = 0; b < N; b++)
            {
                double v = suche == null || suche[b] <= KeinWeg ? KeinWeg : Math.Max(-BandGrenzeDb, Math.Min(0.0, suche[b]));
                if (v > KeinWeg) hat = true;
                if (v != z.S[b]) gleich = false;
                eS[b] = v;
            }
            if (hat == z.HatS && (gleich || !hat)) return false;
            z.HatS = hat;
            for (int b = 0; b < N; b++) z.S[b] = eS[b];
            Zusammenfassen(z, summe, maxDb, f);
            if (summe && z.DBl.Hat)
            {
                for (int b = 0; b < N; b++) eS[b] = z.HatS ? Energie(z.S[b]) : 0.0;
                z.SBl.Setzen(eS, jetzt, dauer);
            }
            return true;
        }

        /// <summary>0.8.8/umwegsuche: hat diese Rechnung des Spiels keinen Umweg gefunden (kein freier T-Strahl, keine Kante, keine Reflexion,
        /// auch keine freie Sichtlinie in D/R)? Genau dann greift die Rueckfallgrenze - und nur dann fragt der Hindernis-Zweig die Wegsuche.
        /// tFrei = Anteil freier T-Strahlen (0, wenn T nicht lief).</summary>
        public static bool UmwegUnbekanntAus(double tFrei, bool ranD, bool dDirekt, bool dHat, bool ranR, bool rDirekt, bool rHat)
            => tFrei <= 0 && !(ranD && (dDirekt || dHat)) && !(ranR && (rDirekt || rHat));

        static readonly double[] eD = new double[N], eR = new double[N], eS = new double[N], wD = new double[N], wR = new double[N], wS = new double[N];

        /// <summary>0.8.5: Nach Auswerten (nur mit Energiesumme): Ziele der D/R-Ueberblendung setzen. "frei" gilt sofort
        /// (der Uebergang an der Freiweg-Schwelle ist schon stetig, siehe Auswerten).</summary>
        public static void BlendeSetzen(WegZustand z, double jetzt, double dauer)
        {
            if (z.Frei || !z.Gueltig) { z.DBl.Vergessen(); z.RBl.Vergessen(); z.SBl.Vergessen(); return; }
            for (int b = 0; b < N; b++)
            {
                eD[b] = z.HatD ? (z.DDirekt ? 1.0 : Energie(z.D[b])) : 0.0;
                eR[b] = z.HatR && !z.RDirekt ? Energie(z.R[b]) : 0.0;
                eS[b] = z.HatS ? Energie(z.S[b]) : 0.0;   // umwegsuche: Suchanteil gleitet wie D/R (dazu, weg, neues Ergebnis)
            }
            z.DBl.Setzen(eD, jetzt, dauer);
            z.RBl.Setzen(eR, jetzt, dauer);
            z.SBl.Setzen(eS, jetzt, dauer);   // immer (wie D): so blendet ein erstes Suchergebnis von 0 ein statt zu springen
        }

        /// <summary>0.8.5: was zur Zeit 'jetzt' gilt (z.Jetzt, z.WertungJetzt): T sofort, D und R uebergeblendet, sonst wie
        /// SummeGetrennt. Am Ende der Ueberblendung genau z.Ergebnis.</summary>
        public static void Mischen(WegZustand z, double jetzt, double dauer, double maxDb, double[] f = null)
        {
            var ff = f ?? StandardF;
            if (z.Frei || !z.DBl.Hat)
            {
                Array.Copy(z.Ergebnis, z.Jetzt, N); z.WertungJetzt = z.Wertung; z.Blendet = false;
                return;
            }
            z.DBl.Wert(jetzt, dauer, wD); z.RBl.Wert(jetzt, dauer, wR);
            bool mitS = z.SBl.Hat; if (mitS) z.SBl.Wert(jetzt, dauer, wS);
            for (int b = 0; b < N; b++)
            {
                double g = z.HatT ? z.TFrei : 0;
                g = Math.Max(g, wD[b]);
                if (mitS) g = Math.Max(g, wS[b]);   // umwegsuche: wie SummeGetrennt (im Maximum mit D)
                double e = g + wR[b];
                if (z.HatT) e += Energie(z.TWand[b]);
                if (z.HatR && z.RDirekt && !z.HatT) e = Math.Max(e, 1.0);
                e = Math.Max(e, Energie(-SchirmDb(ff[b], ff[N - 1], maxDb)));   // 0.8.8 Schirmgrenze wie Auswerten (schirmgrenze: je Band)
                z.Jetzt[b] = Math.Min(0.0, DbAus(e, -maxDb));
            }
            z.WertungJetzt = Math.Max(0, Math.Min(1, -z.Jetzt[0] / maxDb));
            z.Blendet = z.DBl.Laeuft(jetzt, dauer) || z.RBl.Laeuft(jetzt, dauer) || (mitS && z.SBl.Laeuft(jetzt, dauer));
        }

        static bool Weg(WegSpeicher sp, double[] ziel, bool ran, bool direkt, bool hat, double[] neu, V3 q, V3 h, double maxDb,
            double haltNah, double haltFern, double abklingDb, double hystDb, out bool gehalten, out bool hysterese)
        {
            gehalten = hysterese = false;
            if (ran && direkt)
            {
                for (int b = 0; b < N; b++) ziel[b] = 0;
                sp.Setzen(ziel, q, h, true);
                return true;
            }
            if (ran && hat)
            {
                if (hystDb > 0 && sp.Hat && !sp.Direkt && sp.Fehl == 0 && Math.Abs(neu[0] - sp.Db[0]) <= hystDb)
                {
                    hysterese = true;
                    Array.Copy(sp.Db, ziel, N);
                    return true;
                }
                for (int b = 0; b < N; b++) ziel[b] = Math.Max(-maxDb, Math.Min(0, neu[b]));
                sp.Setzen(ziel, q, h);
                return true;
            }
            if (!sp.Hat) { for (int b = 0; b < N; b++) ziel[b] = KeinWeg; return false; }
            if (sp.Direkt) { sp.Vergessen(); for (int b = 0; b < N; b++) ziel[b] = KeinWeg; return false; }   // "frei" nie halten
            if (ran)
            {
                // gelaufen, nichts gefunden: einmal halten (Monte-Carlo-Strahlen treffen nicht jedes Mal), danach abklingen
                sp.Fehl++;
                if (sp.Fehl > 1) for (int b = 0; b < N; b++) sp.Db[b] -= abklingDb;
                if (sp.Db[0] < -maxDb) { sp.Vergessen(); for (int b = 0; b < N; b++) ziel[b] = KeinWeg; return false; }
                gehalten = true;
                Array.Copy(sp.Db, ziel, N);
                return true;
            }
            // nicht gelaufen (niedrige Prioritaet / Spielschwelle): gemerkter Wert, mit der Bewegung abgeblendet
            double bew = Math.Max(V3.Abstand(q, sp.Q), V3.Abstand(h, sp.H));
            double k = Haltefaktor(bew, haltNah, haltFern);
            if (k <= 0) { sp.Vergessen(); for (int b = 0; b < N; b++) ziel[b] = KeinWeg; return false; }
            gehalten = true;
            double kDb = 10.0 * Math.Log10(k);
            for (int b = 0; b < N; b++) ziel[b] = sp.Db[b] + kDb;
            return true;
        }

        // ---------------------------------------------------------------- Filter aus der Bandkurve

        /// <summary>Mehrdaempfung (dB) eines 2-poligen Tiefpasses mit Grenzfrequenz fc bei f (|H|^2 = 1/(1+(f/fc)^4)).</summary>
        public static double MehrDb(double fc, double f) => 10.0 * Math.Log10(1.0 + Math.Pow(f / Math.Max(fc, 1.0), 4));
        /// <summary>Gesamtreview A8: Mehrdaempfung des Tiefpasses bei f gegenueber dem Bezugsband fRef (dB, &gt;= 0).</summary>
        public static double MehrDbRelativ(double fc, double f, double fRef) => Math.Max(0.0, MehrDb(fc, f) - MehrDb(fc, fRef));

        // ---------------------------------------------------------------- 10.11.2026 (Pruefung Psychoakustik B2): Fit nach Lautheit
        // Ursache: der Tiefpass des Spiels (Unity AudioLowPassFilter ueber AudioBuiltinEQFilter.ApplyLowPass, 2-polig, 12 dB/Okt.)
        // kann eine Daemmung, die mit ~6 dB/Okt. steigt (Massengesetz) oder ~3 dB/Okt. (Kantenbeugung), nicht nachbilden. Der Fit
        // bisher traf nur die zwei Stuetzpunkte 1 kHz / 4 kHz mit gleichem Gewicht; dazwischen und darunter lag der Filter bis 6 dB
        // daneben (Massengesetz: 500 Hz-1 kHz 5-6 dB zu laut, Bericht B2). Ein eigener Filter je Quelle wurde geprueft und nicht
        // gebaut (Kosten/Risiko, siehe Bericht klangfarbe). Jetzt: fc so, dass die Abweichung des Filters von der Bandkurve ueber
        // 250 Hz-4 kHz in Lautheit am kleinsten ist:
        //   - Sollkurve zwischen den Baendern: konstante Steigung je Oktave (Gerade in lg f zwischen Bezug, 1 kHz, 4 kHz) - so
        //     verlaufen Massengesetz (6 dB/Okt.), Kantenbeugung (~3 dB/Okt.) und Fugen (flach); ausserhalb 250 Hz-4 kHz wird nichts
        //     erfunden (keine Baender dort).
        //   - Gewicht je Terz = Breite in Bark (Zwicker/Terhardt 1980, J. Acoust. Soc. Am. 68, 1523: z = 13 atan(0,00076 f) +
        //     3,5 atan((f/7500)^2)). Lautheit = Integral der spezifischen Lautheit ueber Bark; N' ~ E^0,23 (Zwicker/Fastl,
        //     Psychoacoustics, Kap. 8), ein Pegelfehler dL aendert lg N' also proportional zu dL. Summe Gewicht * dL^2 = Lautheitsfehler
        //     bei gleicher spezifischer Lautheit je Bark (Annahme: das Quellspektrum ist je Klanggruppe nicht bekannt).
        //   - Bezugsband bleibt fest (Filter relativ zum Bezugsband, wie MehrDbRelativ): die Lautstaerke des Bezugsbands ist die Rechnung.
        // Ergebnis Massengesetz +12/+24 dB: fc 652 Hz statt 768 Hz (1 kHz -4,0 statt -6,2 dB zu laut, 4 kHz +7,3 statt +4,5 dB zu dumpf,
        // Bark-gewichteter Fehler 3,9 statt 4,4 dB); Beugung +6/+12: 1353 statt 1992 Hz (Fehler 3,6 statt 4,7 dB). Die Untergrenze
        // "Low-pass at full insulation" gilt weiter (ein unimodaler Fehler: Minimum im Intervall = geklemmtes Minimum).

        /// <summary>Kritische Bandrate (Bark) nach Zwicker/Terhardt 1980.</summary>
        public static double Bark(double f) => 13.0 * Math.Atan(0.00076 * f) + 3.5 * Math.Atan((f / 7500.0) * (f / 7500.0));

        sealed class FitStuetzen
        {
            public double FRef, F1, F2;
            public double[] F, W, A, B;   // Terzmitte, Gewicht (Bark), Soll = A m1 + B m2
        }
        static FitStuetzen fitStuetzen;   // unveraenderlich nach dem Anlegen -> Austausch der Referenz ist threadsicher

        static FitStuetzen Stuetzen(double fRef, double f1, double f2)
        {
            var s = fitStuetzen;
            if (s != null && s.FRef == fRef && s.F1 == f1 && s.F2 == f2) return s;
            if (!(fRef > 0 && f1 > fRef && f2 > f1)) return null;
            double o1 = Math.Log(f1 / fRef, 2), o2 = Math.Log(f2 / fRef, 2);
            int n = Math.Max(2, (int)Math.Round(3 * o2));   // Terzen (250 Hz-4 kHz: 12)
            s = new FitStuetzen { FRef = fRef, F1 = f1, F2 = f2, F = new double[n], W = new double[n], A = new double[n], B = new double[n] };
            for (int j = 0; j < n; j++)
            {
                double ua = fRef * Math.Pow(f2 / fRef, (double)j / n), ub = fRef * Math.Pow(f2 / fRef, (double)(j + 1) / n);
                double fm = Math.Sqrt(ua * ub), o = Math.Log(fm / fRef, 2);
                s.F[j] = fm; s.W[j] = Bark(ub) - Bark(ua);
                if (o <= o1) { s.A[j] = o / o1; s.B[j] = 0; }
                else { double t = (o - o1) / (o2 - o1); s.A[j] = 1 - t; s.B[j] = t; }
            }
            fitStuetzen = s;
            return s;
        }

        static double MehrDbSchnell(double fc, double f) { double r = f / fc; r *= r; r *= r; return 10.0 * Math.Log10(1.0 + r); }

        static double FehlerLautheit(FitStuetzen s, double fc, double m1, double m2)
        {
            double r0 = MehrDbSchnell(fc, s.FRef), e = 0;
            for (int j = 0; j < s.F.Length; j++)
            {
                double d = MehrDbSchnell(fc, s.F[j]) - r0 - (s.A[j] * m1 + s.B[j] * m2);
                e += s.W[j] * d * d;
            }
            return e;
        }

        /// <summary>
        /// Tiefpass-Fit aus der Bandkurve (10.11.2026, B2): Mehrdaempfung m1/m2 gegenueber dem Bezugsband fRef bei f1 (1 kHz) und
        /// f2 (4 kHz). Gesucht ist fc des 2-poligen Filters, dessen Abweichung von der Bandkurve ueber fRef..f2 in Lautheit am
        /// kleinsten ist (Begruendung oben). m1/m2 &lt;= 0,05 dB -> offen (fMax). Ergebnis zwischen fMin und fMax.
        /// Ungueltige Frequenzfolge (fRef &gt;= f1) -> der fruehere Zwei-Punkte-Fit.
        /// </summary>
        public static double TiefpassFit(double m1, double m2, double f1, double f2, double fMin, double fMax = 22000, double fRef = 250)
        {
            m1 = Math.Max(0, m1); m2 = Math.Max(0, m2);
            if (m1 <= 0.05 && m2 <= 0.05) return fMax;
            var s = Stuetzen(fRef, f1, f2);
            if (s == null) return TiefpassFitZweiPunkte(m1, m2, f1, f2, fMin, fMax);
            double lo = Math.Log(fMin), hi = Math.Log(fMax);
            if (!(hi > lo)) return Math.Max(fMin, Math.Min(fMax, fMin));
            // grob (16 Punkte in lg fc), dann goldener Schnitt im Nachbarintervall des besten Punkts
            const int Grob = 16;
            int best = 0; double eb = double.MaxValue;
            for (int i = 0; i <= Grob; i++)
            {
                double e = FehlerLautheit(s, Math.Exp(lo + (hi - lo) * i / Grob), m1, m2);
                if (e < eb) { eb = e; best = i; }
            }
            double a = lo + (hi - lo) * Math.Max(0, best - 1) / Grob, b = lo + (hi - lo) * Math.Min(Grob, best + 1) / Grob;
            double g = (Math.Sqrt(5) - 1) / 2;
            double c = b - g * (b - a), dd = a + g * (b - a);
            double fc_ = FehlerLautheit(s, Math.Exp(c), m1, m2), fd = FehlerLautheit(s, Math.Exp(dd), m1, m2);
            for (int i = 0; i < 28; i++)
            {
                if (fc_ < fd) { b = dd; dd = c; fd = fc_; c = b - g * (b - a); fc_ = FehlerLautheit(s, Math.Exp(c), m1, m2); }
                else { a = c; c = dd; fc_ = fd; dd = a + g * (b - a); fd = FehlerLautheit(s, Math.Exp(dd), m1, m2); }
            }
            return Math.Max(fMin, Math.Min(fMax, Math.Exp((a + b) / 2)));
        }

        /// <summary>Bis 10.11.2026: Fit an die zwei Stuetzpunkte f1/f2 mit gleichem Gewicht (nur noch Rueckfall und Vergleich).</summary>
        public static double TiefpassFitZweiPunkte(double m1, double m2, double f1, double f2, double fMin, double fMax = 22000)
        {
            m1 = Math.Max(0, m1); m2 = Math.Max(0, m2);
            if (m1 <= 0.05 && m2 <= 0.05) return fMax;
            double lo = Math.Log(fMin), hi = Math.Log(fMax);
            // Fehlerfunktion ist in log fc unimodal: goldener Schnitt
            double g = (Math.Sqrt(5) - 1) / 2, a = lo, b = hi;
            double c = b - g * (b - a), dd = a + g * (b - a);
            double fc_ = Fehler(Math.Exp(c), m1, m2, f1, f2), fd = Fehler(Math.Exp(dd), m1, m2, f1, f2);
            for (int i = 0; i < 40; i++)
            {
                if (fc_ < fd) { b = dd; dd = c; fd = fc_; c = b - g * (b - a); fc_ = Fehler(Math.Exp(c), m1, m2, f1, f2); }
                else { a = c; c = dd; fc_ = fd; dd = a + g * (b - a); fd = Fehler(Math.Exp(dd), m1, m2, f1, f2); }
            }
            return Math.Max(fMin, Math.Min(fMax, Math.Exp((a + b) / 2)));
        }

        static double Fehler(double fc, double m1, double m2, double f1, double f2)
        {
            double e1 = MehrDb(fc, f1) - m1, e2 = MehrDb(fc, f2) - m2;
            return e1 * e1 + e2 * e2;
        }

        // ---------------------------------------------------------------- Empfangsraum (EN ISO 12354-1, vereinfacht)

        /// <summary>
        /// Korrektur (dB, + = lauter) des Wegs durch eine Wand fuer den Raum, in dem der Hoerer steht, gegenueber "Freifeld durch eine
        /// unendliche Wand" (dL = -R). Quelle als Punktquelle, Wandabstand r1, Hoerer-Wandabstand r2, Wandflaeche s, d = Direktlinie.
        /// Auf die Wand: I = 1/(4 pi r1^2), durch: P = I s tau. Am Hoerer: Direktabstrahlung P/(2 pi r2^2) (Halbraum, nie mehr als die
        /// unendliche Wand) + diffus 4P/A2 (Kuttruff), Diffusanteil mit 'kDiffus' gewichtet (Meta XR hallt evtl. selbst im Hoererraum).
        /// a2 &lt;= 0 = Hoerer im Freien (nur Direktabstrahlung). Ergebnis auf +-maxDb begrenzt.
        /// 10.11. (akustik-wand): im Spiel durch Wandweg ersetzt (ohne Quellraum, Diffusanteil und Deckel pauschal); bleibt fuer den Vergleich.
        /// </summary>
        public static double Empfangsraum(double s, double r1, double r2, double d, double a2, double kDiffus, double maxDb)
        {
            s = Math.Max(s, 0.1); r1 = Math.Max(r1, 0.25); r2 = Math.Max(r2, 0.25); d = Math.Max(d, r1 + r2);
            double direkt = Math.Min(1.0, s * d * d / (r1 * r1 * 2 * Math.PI * r2 * r2));
            double diffus = a2 > 0 ? kDiffus * 4.0 * s * d * d / (r1 * r1 * Math.Max(a2, RaumwegKern.MinFlaecheA)) : 0;
            double db = 10.0 * Math.Log10(direkt + diffus);
            return Math.Max(-maxDb, Math.Min(maxDb, db));
        }

        // ---------------------------------------------------------------- 10.11. (akustik-wand, Befund 1): Wandweg mit beiden Raeumen
        // Bis 10.11. (Empfangsraum oben): auf die Wand traf nur der Direktschall I = W/(4 pi r1^2), ohne Raumwinkel (bei grossen, nahen
        // Waenden mehr Leistung als die Quelle abgibt) und ohne das Diffusfeld des Quellraums; der Diffusanteil im Hoererraum war mit 0,5
        // gewichtet und das Ergebnis auf +-6 dB gedeckelt. EN ISO 12354-1 (L2 = L1 - R + 10 lg(S/A2), L1 = Diffusfeld des Senderaums)
        // lag damit bis 19 dB hoeher. Jetzt dieselbe Physik wie im Raumweg (RaumwegKern.Route, Gesamtreview A3 + Raumwinkel 0.8.5):
        //   Leistung auf die Wand (W = 1):  P_dir = Omega/(4 pi)  (Raumwinkel des Wandrechtecks von der Quelle, Kuttruff, Room Acoustics Kap. 5)
        //                                   P_dif = S/A1' * exp(-0,04 r1/T1)   (Diffusfeld I = W/A1, Barron & Lee 1988; A1' = A1 + 4 m V1)
        //   durch die Wand: tau * P (tau steckt in -R, hier nur die Korrektur gegenueber "Freifeld durch eine unendliche Wand")
        //   am Hoerer: Direktanteil der Wand nie mehr als durch eine unendliche Wand (Kugelwelle bleibt Kugelwelle: Faktor 1),
        //              der diffus auftreffende Teil strahlt als Flaechenquelle: min(1/(2 pi r2^2), 1/S) (dicht an der Wand I = tau * W/A1),
        //              Diffusfeld des Hoererraums 4 P/A2' * exp(-0,04 r2/T2).
        // Annahmen: Quelle auf der Mittelsenkrechten des Wandrechtecks im Abstand r1 (der echte Fusspunkt ist nicht bekannt -> eher zu
        // grosser Raumwinkel, hoechstens Faktor 2 in P_dir); a1/a2 &lt;= 0 = Quelle/Hoerer im Freien (kein Diffusfeld dort).
        // Grenzfall Raum-Raum ohne Direktanteile (r1, r2 gross, V = 0): k = 10 lg(16 pi d^2 S/(A1 A2)) = EN 12354-1 exakt (Test).

        /// <summary>Korrektur (dB, + = lauter) des Wandwegs gegenueber -R im Freifeld ueber die Direktlinie d (siehe oben).
        /// breite x hoehe = Wandrechteck (m), r1/r2 = Abstand Quelle/Hoerer zur Wand, a = aequivalente Absorptionsflaeche (m2, &lt;= 0 = im Freien),
        /// v = Volumen (m3, 0 = ohne Luft und Abklingen), m = Luft-Energiedaempfung (1/m).</summary>
        public static double Wandweg(double breite, double hoehe, double r1, double r2, double d, double a1, double v1, double a2, double v2, double m)
        {
            WandwegTeile(breite, hoehe, r1, r2, d, a1, v1, a2, v2, m, out double g, out double dif);
            return 10 * Math.Log10(g + dif);
        }

        /// <summary>diffus-hall (10.11.): der Wandweg in seinen zwei Anteilen (Faktoren gegenueber -R im Freifeld, Summe = 10^(Wandweg/10)).
        /// gerichtet = was die Wand selbst zum Hoerer abstrahlt (Direkt -> Direkt und der diffus auftreffende Teil als Flaechenquelle):
        /// kommt aus der Richtung der Wand. diffus = Hallfeld des Hoererraums 4 P/A2' (kommt von ueberall, zeitlich verschmiert).</summary>
        public static void WandwegTeile(double breite, double hoehe, double r1, double r2, double d, double a1, double v1, double a2, double v2, double m,
                                        out double gerichtet, out double diffus)
        {
            breite = Math.Max(breite, 0.3); hoehe = Math.Max(hoehe, 0.3);
            double s = breite * hoehe;
            r1 = Math.Max(r1, 0.25); r2 = Math.Max(r2, 0.25); d = Math.Max(d, r1 + r2);
            double hx = breite / 2, hy = hoehe / 2;
            // Raumwinkel eines Rechtecks von einem Punkt auf seiner Mittelsenkrechten (exakt): Omega = 4 atan(a b / (w sqrt(a^2 + b^2 + w^2)))
            double om = 4 * Math.Atan(hx * hy / (r1 * Math.Sqrt(hx * hx + hy * hy + r1 * r1)));
            double pDir = om / (4 * Math.PI);
            double pDif = a1 > 0 ? s / Raum(a1, v1, m, r1) : 0;
            double bezug = 1.0 / (4 * Math.PI * d * d);                       // tau / (4 pi d^2) ohne tau
            gerichtet = Math.Min(1.0, pDir / (2 * Math.PI * r2 * r2) / bezug)   // Direkt -> Direkt (nie mehr als die unendliche Wand)
                        + pDif * Math.Min(1.0 / (2 * Math.PI * r2 * r2), 1.0 / s) / bezug;
            diffus = a2 > 0 ? 4 * (pDir + pDif) / Raum(a2, v2, m, r2) / bezug : 0;
        }

        /// <summary>diffus-hall: Diffusanteil (0..1) der Gesamtenergie des Hindernis-Wegs im Bezugsband - dieselbe Summe wie SummeGetrennt
        /// (freie T-Strahlen bzw. Kante, Wand, Reflexion), davon traegt nur der Wandanteil mit seinem Hoererraum-Hallfeld z.TDiffus bei.
        /// Kante und Reflexion sind gerichtet (ein Weg, eine Richtung). Ohne Summe (bester Weg allein): Anteil des Wandwegs, wenn T gewinnt.</summary>
        public static double DiffusAnteil(WegZustand z, bool summe) => DiffusAnteil(z, summe, 0);

        /// <summary>hallsend (B2, 12.11.): wie DiffusAnteil, im Band b (0 = Bezug, 1 = 1 kHz, 2 = 4 kHz) mit den Energien dieses Bands.</summary>
        public static double DiffusAnteil(WegZustand z, bool summe, int b)
        {
            if (z == null || !z.Gueltig || z.Frei || !z.HatT || !(z.TDiffus > 0)) return 0;
            double f = Math.Min(1.0, Math.Max(0.0, z.TDiffusBand(b)));
            if (!summe) return z.Bester == 1 ? f : 0;
            double g = z.TFrei;
            if (z.HatD) g = z.DDirekt ? 1.0 : Math.Max(g, Energie(z.D[b]));
            if (z.HatS) g = Math.Max(g, Energie(z.S[b]));   // umwegsuche: Kantenwege der Suche sind gerichtet (wie D)
            double ew = Energie(z.TWand[b]);
            double e = g + ew;
            if (z.HatR && !z.RDirekt) e += Energie(z.R[b]);
            else if (z.HatR) e = Math.Max(e, 1.0);
            return e > 0 ? Math.Min(1.0, ew * f / e) : 0;
        }

        /// <summary>hallsend (A2, 12.11.): gilt fuer dieses Hindernis-Ergebnis "Hall des Raums bleibt"? Nur mit gueltiger, verdeckter
        /// Rechnung (frei = nichts abzuschatten) und Quelle + Hoerer im selben gemessenen Raum. Hoerer draussen, Quelle in einem anderen
        /// gemessenen Raum oder kein gemessener Raum: false (Verhalten wie bis 12.11.; draussen gibt es kein Hallfeld des Raums).</summary>
        public static bool RaumHallBleibt(WegZustand z) => z != null && z.Gueltig && !z.Frei && z.GleicherRaum;

        /// <summary>A' = max(A + 4 m V, MinFlaecheA), geteilt durch den Abfall exp(-0,04 r/T) nach Barron (T = 0,161 V/A'), wie RaumwegKern.QuellDiffus.</summary>
        internal static double Raum(double a, double v, double m, double r)   // mehrwand: auch fuer die Wandkette (WandKetteKern)
        {
            v = Math.Max(0, v);
            double aF = Math.Max(a + 4 * m * v, RaumwegKern.MinFlaecheA);
            double abfall = v > 0 ? Math.Exp(-0.04 * r / Math.Max(0.161 * v / aF, 0.05)) : 1.0;
            return aF / abfall;
        }

        // ---------------------------------------------------------------- Flankenuebertragung (EN ISO 12354-1, 4.4 + Anhang E)

        /// <summary>Stossstellendaemm-Mass starrer Kreuzstoss, gerader Weg (K_13). M = lg(m'_senkrecht / m'_i).</summary>
        public static double K13(double m) => 8.7 + 17.1 * m + 5.7 * m * m;
        /// <summary>Starrer Kreuzstoss, Weg um die Ecke (K_12).</summary>
        public static double K12(double m) => 8.7 + 5.7 * m * m;

        /// <summary>
        /// Bau-Schalldaemm-Mass R' aus Direktweg R_D und vier Stossstellen (Boden, Decke, zwei Seitenwaende) mit je drei Flankenwegen
        /// (Ff, Fd, Df). rF/mF je Stossstelle (Flankenbauteil: R in dB, m' in kg/m2), lf = Stossstellenlaenge (m), sS = Trennwandflaeche.
        /// R_ij = (R_i + R_j)/2 + K_ij + 10 lg(S_s / (l0 l_f)), l0 = 1 m. R' = -10 lg(10^(-R_D/10) + Sum 10^(-R_ij/10)).
        /// </summary>
        public static double Flanke(double rD, double mD, double sS, double[] rF, double[] mF, double[] lf, int n)
        {
            mD = Math.Max(mD, 0.1); sS = Math.Max(sS, 0.5);
            double e = Math.Pow(10, -rD / 10);
            for (int j = 0; j < n; j++)
            {
                double mf = Math.Max(mF[j], 0.1);
                double flaeche = 10.0 * Math.Log10(sS / Math.Max(lf[j], 0.3));
                double rff = rF[j] + K13(Math.Log10(mD / mf)) + flaeche;
                double rfd = (rF[j] + rD) / 2 + K12(Math.Log10(mD / mf)) + flaeche;
                e += Math.Pow(10, -rff / 10) + 2 * Math.Pow(10, -rfd / 10);
            }
            return -10.0 * Math.Log10(e);
        }
    }
}
