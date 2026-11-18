using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // 0.8.8 - Richtung ueber die Oeffnung und Beugung ueber Hindernisse, wenn das Spiel keinen Raumweg kennt.
    // Reine Rechnung ohne Unity (Offline-Test). Spiel-Teil: Richtung.cs (Richtung), Raumweg.cs (Ersatzweg).
    //
    // 1. Richtung (Meldung Ground Zero 09.11.2026, MARK 3, "die Geraeusche durch die Waende sind lauter als durch die moeglichen
    //    Wege"): Bot 23 m entfernt, 8 m hoeher draussen im Turm, Hoerer in der Olive-Halle. Klang-Log: Wand 6 Schichten
    //    (Beton 0,15 + Decke 0,27 + 0,15 + 0,34 m, Vorhang, Sofa) -60 dB, Raumweg ueber zwei Oeffnungen -12,9 dB -> Summe -9,3 dB,
    //    vol -9,8 dB. Die LAUTSTAERKE stimmt also mit dem Oeffnungsweg. Die Richtung nicht: die AudioSource haengt am Bot
    //    (BetterSource-Transform, Kind eines Knochens), Unity und der Meta-XR-Spatializer orten den Klang dort - quer durch Decke
    //    und Waende. Ein Klang, der so laut ist wie ein offener Weg und aus der Wand kommt, klingt "durch die Wand".
    //    Das Spiel hat fuer Raumwege keine virtuelle Quelle: PropagationJob liefert nur eine Zahl (Kosten), die Portalpunkte
    //    bleiben im Job. Die Okklusion rechnet aber NICHT mit der Position der AudioSource, sondern mit AudioSourceContainer.
    //    CurrentPosition = _parentTransform.position + _positionOffset; bei Figuren ist _parentTransform der Knochen Spine1
    //    (SpatialAudioSystem.ProcessSourceOcclusion(IPlayer ...)), die Quellen selbst sind eigene Objekte. Die AudioSource laesst
    //    sich also verschieben, ohne dass sich die Rechnung (Raumpaar, Routen, Wand, Bot-Hoeren ueber SAIN) aendert. Wo der
    //    Container an der Quelle selbst haengt (Einmal-Klaenge der Welt), gleicht _positionOffset die Verschiebung aus.
    //    Regel: Richtung = vom Hoerer zum energiegewichteten Austrittspunkt der Routen (letzter Portalpunkt vor dem Hoerer,
    //    RoutenMischer.Summe), Abstand = der bisherige Abstand Hoerer-Quelle (die Entfernungskurve von Unity/SEB und die
    //    Daemmung des Raumwegs beziehen sich auf die Direktlinie, RaumwegKern: "Bezug Freifeld in Direktlinie").
    //    Gewicht = Energieanteil des Raumwegs an der Summe mit der Wand (Wand dominiert -> Richtung bleibt an der Quelle),
    //    weich ausgeblendet, wenn der Hoerer im Austritt steht (dort ist "Richtung zum Austritt" unbestimmt).
    //
    // 2. Beugung ueber Hindernisse (MARK 1, 13:01:03): Gefecht 230-260 m entfernt, draussen (Area_05), Hoerer in der Capital-Halle.
    //    Das Spiel kennt fuer dieses Raumpaar keine Route (ParallelSoundPropagationCalculator.OnUpdate: TryGetMatchingRoomPair
    //    ohne Routen -> _propagationScore = 1, Fruehabbruch) -> volle Daemmung 60 dB = stumm. Im selben Moment kam ein Bot aus
    //    der Winery (230 m) ueber fuenf Oeffnungen mit -12 dB an - der Hoererraum hat offene Oeffnungen nach draussen.
    //    Ersatzweg (nur wenn Quelle oder Hoerer draussen ist): draussen -> Oeffnung des Innenraums nach draussen -> Hoerer
    //    (RaumwegKern.Route wie jede Route) plus Abschirmung auf dem Weg im Freien. Abschirmung nach ISO 9613-2:1996, 7.4,
    //    Gl. (14): Dz = 10 lg(3 + (C2/lambda) C3 z Kmet), C2 = 20, C3 = 1 (eine Kante) bzw. (1+(5 lambda/e)^2)/(1/3+(5 lambda/e)^2)
    //    (zwei und mehr Kanten, e = Abstand erste-letzte Kante), z = Umweg ueber die Kanten, Kmet Gl. (18); Deckel 20 dB (eine
    //    Kante) bzw. 25 dB (mehrere). Kanten = obere konvexe Huelle ("Gummiband") des Hoehenprofils zwischen den Endpunkten
    //    (Spiel: Strahlen von oben auf Dach/Gelaende). Seitliche Beugung um Gebaeudeecken ist nicht enthalten (eher zu leise).
    //    0.8.8 (Streets 10.11.): dazu die Bebauung unter dem Gummiband nach ISO 9613-2 Anhang A.3 (HindernisKern.Haus, bis 10 dB).
    //    Beide Raeume drinnen ohne Route: bleibt dicht.
    // ------------------------------------------------------------------------------------------

    internal static class RichtungKern
    {
        /// <summary>Unter diesem Abstand Hoerer-Austritt (m) zaehlt die Austrittsrichtung nicht, ab NahVoll voll.</summary>
        public const double NahM = 0.3, NahVoll = 1.5;
        /// <summary>Unter diesem Abstand Hoerer-Quelle (m) wird nichts verschoben.</summary>
        public const double MinAbstand = 0.5;

        static double Glatt(double x) => x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x);
        static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);

        public static V3 Einheit(V3 v)
        {
            double l = v.Laenge;
            return l < 1e-9 ? new V3(0, 0, 0) : v * (1.0 / l);
        }

        /// <summary>Energieanteil des Raumwegs an der Summe (Raumweg routeDb, Ergebnis gesamtDb, beide als Daemmung >= 0).
        /// 1 = alles kommt ueber die Oeffnungen, 0 = alles durch die Wand.</summary>
        public static double Anteil(double routeDb, double gesamtDb)
        {
            if (double.IsNaN(routeDb) || double.IsNaN(gesamtDb)) return 0;
            return Clamp01(Math.Pow(10, (gesamtDb - routeDb) / 10.0));
        }

        /// <summary>Winkel (Grad) zwischen zwei Richtungen.</summary>
        public static double Winkel(V3 a, V3 b)
        {
            double la = a.Laenge, lb = b.Laenge;
            if (la < 1e-9 || lb < 1e-9) return 0;
            double c = Math.Max(-1, Math.Min(1, V3.Dot(a, b) / (la * lb)));
            return Math.Acos(c) * 180.0 / Math.PI;
        }

        /// <summary>Kugelige Interpolation zweier Einheitsvektoren (t = 0: a, 1: b). Gegenlaeufig: ueber eine waagerechte Senkrechte.</summary>
        public static V3 Slerp(V3 a, V3 b, double t)
        {
            t = Clamp01(t);
            if (t <= 0) return a;
            if (t >= 1) return b;
            double c = Math.Max(-1, Math.Min(1, V3.Dot(a, b)));
            if (c > 0.9999) return Einheit(a * (1 - t) + b * t);
            double w = Math.Acos(c);
            if (c < -0.9999)
            {
                // a und b gegenlaeufig: Drehachse senkrecht zu a (bevorzugt waagerecht), halber Weg = Senkrechte
                V3 senk = Einheit(new V3(-a.Z, 0, a.X));
                if (senk.IstNull) senk = new V3(1, 0, 0);
                double phi = Math.PI * t;
                return Einheit(a * Math.Cos(phi) + senk * Math.Sin(phi));
            }
            double s = Math.Sin(w);
            return Einheit(a * (Math.Sin((1 - t) * w) / s) + b * (Math.Sin(t * w) / s));
        }

        /// <summary>Ziel der Ortung: Hoerer h, echte Quelle q, Austrittspunkt aus (Weltkoordinaten), Raumweg-Anteil.
        /// richtung = Einheitsvektor vom Hoerer, gewicht = wie weit die Richtung zum Austritt gedreht ist (0..1).
        /// false = nichts zu tun (Quelle am Hoerer, kein Austritt).</summary>
        public static bool Ziel(V3 h, V3 q, V3 aus, double anteil, out V3 richtung, out double gewicht)
        {
            richtung = default; gewicht = 0;
            V3 dq = q - h;
            double d = dq.Laenge;
            if (d < MinAbstand || aus.IstNull) return false;
            V3 u = dq * (1.0 / d);
            V3 da = aus - h;
            double la = da.Laenge;
            if (la < 1e-6) { richtung = u; return true; }
            double nah = Glatt((la - NahM) / (NahVoll - NahM));
            gewicht = Clamp01(anteil) * nah;
            richtung = Slerp(u, da * (1.0 / la), gewicht);
            return true;
        }

        /// <summary>Zeitliche Nachfuehrung der Richtung (exponentiell, Zeitkonstante tauS). tauS &lt;= 0 oder alt = 0: sofort.</summary>
        public static V3 Nachfuehren(V3 alt, V3 ziel, double dt, double tauS)
        {
            if (alt.IstNull || tauS <= 0 || dt <= 0) return ziel;
            return Slerp(alt, ziel, 1 - Math.Exp(-dt / tauS));
        }

        /// <summary>Virtuelle Position: Hoerer + Richtung * Abstand.</summary>
        public static V3 Position(V3 h, V3 richtung, double abstand) => h + richtung * abstand;

        // ------------------------------------------------------------------------------------------
        // wegrichtung (13.11.2026): Richtung im Hindernis-Fall (gleicher Raum, das Spiel fand keinen Umweg, der Pegel kommt aus der
        // vorab gerechneten Wegtabelle, Schallweg/SwTabelle.cs). Der gebeugte Schall kommt aus der Richtung der letzten Kante vor dem
        // Hoerer. Befund wegtabelle Etappe 2: der letzte Kantenpunkt des staerksten Sondenpaars weicht vom Bezug (Suche ohne Budget am
        // echten Ort) im Median 25 Grad ab, weil benachbarte Paare verschiedene lauteste Wege haben. Regel (Messung: review-20261110/
        // wegrichtung-STAND.md, Messplatz wegtabelle/mess Modus "richtung"):
        //  1. Richtung = Vektormittel der Einheitsvektoren vom ECHTEN Ohr zu den letzten Kantenpunkten aller 2 x 2 Paare, Gewicht =
        //     Zellgewicht x Energie (dasselbe Gewicht wie der Pegel der Tabelle). Kein harter Wechsel zwischen Paaren; zwei gleich
        //     starke Kanten links und rechts geben die Mitte - mit kurzem Mittelvektor.
        //  2. Fokus = Laenge des Mittelvektors (1 = alle Paare zeigen in dieselbe Richtung, 0 = sie heben sich auf). Die Richtung wird
        //     nur um Anteil x Fokus von der echten Quelle zur Kante gedreht (wie die Buendelung beim Raumweg): je uneiniger die Paare,
        //     desto mehr bleibt sie bei der Quelle.
        //  3. Anteil = Gewicht des gebeugten Wegs gegen die Wege, die aus Richtung der Quelle kommen (Durchgang T, Reflexion R, gehaltene
        //     Kante D des Spiels) mit Praezedenz (KantenAnteil).
        // Abstand bleibt der echte (wie beim Raumweg, Richtung.cs): die Wegpegel der Tabelle enthalten die Kugelwelle ueber die ganze
        // Weglaenge relativ zur Direktlinie - eine Quelle in Weglaenge-Abstand bekaeme die Abstandskurve des Spiels noch einmal.
        // ------------------------------------------------------------------------------------------

        /// <summary>wegrichtung: Vektormittel der Richtungen vom Hoerer h zu den Punkten punkt[0..n) mit Gewichten e (&gt;= 0).
        /// Punkte naeher als NahM am Hoerer zaehlen nicht (Richtung unbestimmt), bis NahVoll weich. richtung = Einheitsvektor
        /// (0, wenn sich alles aufhebt), fokus = Laenge des Mittelvektors 0..1. false = kein Gewicht.</summary>
        public static bool KantenMittel(V3 h, V3[] punkt, double[] e, int n, out V3 richtung, out double fokus)
        {
            richtung = default; fokus = 0;
            double es = 0; V3 m = default;
            for (int i = 0; i < n; i++)
            {
                double ei = e[i];
                if (!(ei > 0)) continue;
                es += ei;
                V3 d = punkt[i] - h;
                double la = d.Laenge;
                if (la < 1e-6) continue;
                double nah = Glatt((la - NahM) / (NahVoll - NahM));
                m = m + d * (ei * nah / la);
            }
            if (!(es > 0)) return false;
            m = m * (1.0 / es);
            fokus = Math.Min(1.0, m.Laenge);
            if (fokus > 1e-9) richtung = m * (1.0 / m.Laenge);
            return true;
        }

        /// <summary>Praezedenz: Zuschlag (dB) fuer den frueher ankommenden Weg, wenn der spaetere dt (ms) nach ihm kommt. Unter ~1 ms
        /// Summenlokalisation (Energie, 0 dB), ab 1 ms Praezedenzeffekt: der spaetere Weg bestimmt die Richtung erst, wenn er etwa 10 dB
        /// lauter ist (Haas 1951, Acustica 1, "Ueber den Einfluss eines Einfachechos auf die Hoersamkeit von Sprache": gleich laut bei
        /// ~10 dB Vorsprung des Echos fuer 5-35 ms; Blauert, Spatial Hearing 1997, Kap. 3.1; Psychoakustik-Befund B5).
        /// richtungvorrang (16.11.): ueber der Echoschwelle (~40 ms) bleibt der Zuschlag. Dort hoert man zwei Ereignisse, das erste bleibt
        /// in der Richtung der ersten Wellenfront, das Echo erscheint getrennt an seinem eigenen Ort (Blauert 1997, Kap. 3.1). Wir spielen
        /// nur EINE Richtung: ein Energiemittel zweier getrennter Ereignisse zeigt auf keines von beiden (bis 16.11. fiel der Zuschlag
        /// 35-50 ms auf 0 - Marke 2 vom 16.11.: Wege 71-88 ms spaeter und 13 dB leiser zogen die Richtung 6-10 Grad weg). Dass ein
        /// spaeteres Ereignis die Richtung ab +10 dB uebernimmt, ist auch jenseits von 40 ms eine Annahme (Haas misst nur bis 35 ms
        /// gleiche Lautheit). Die weiche Rampe 0,5-1,5 ms ist eine Annahme (stetig).</summary>
        public static double Vorrang(double dtMs)
        {
            if (!(dtMs > 0)) return 0;
            return VorrangDb * Glatt((dtMs - 0.5) / 1.0);
        }
        public const double VorrangDb = 10.0;

        /// <summary>Anteil der Kantenrichtung (0..1): Energie des gebeugten Wegs eKante gegen die Energie der Wege aus Richtung der Quelle
        /// eRest; der gebeugte Weg ist um dtMs spaeter (Umweg / c) - der fruehere bekommt den Praezedenz-Zuschlag.</summary>
        public static double KantenAnteil(double eKante, double eRest, double dtMs)
        {
            if (!(eKante > 0)) return 0;
            double rest = Math.Max(0, eRest) * Math.Pow(10, Vorrang(dtMs) / 10.0);
            return eKante / (eKante + rest);
        }

        /// <summary>richtungvorrang (16.11.2026): Richtung der Raumrouten (Austritt) mit Praezedenz. Energiegewichtetes Mittel der RICHTUNGEN
        /// vom Hoerer h zu den Austrittspunkten (Intensitaetsvektor, Bezugsband), jede Route mit Energie x 10^(-Vorrang(t - t0)/10):
        /// t = Laufzeit ihrer Weglaenge (Erg.Laenge / c), t0 = die frueheste Route. Der zuerst eintreffende Weg bekommt gegen jeden
        /// spaeteren denselben Zuschlag wie bei der Kante (KantenAnteil); ein schwacher frueher Weg gewinnt nur, wenn er weniger als 10 dB
        /// unter dem spaeteren liegt. Grund (Marke 2, 16.11., Bot16): Sicht durch #132; drei Wege ueber #140/#274 (-13 bis -15 dB,
        /// 71-88 ms spaeter) zogen die Richtung im reinen Energiemittel 6-10 Grad zur Wand.
        /// richtunggerichtet (16.11.): Richtung traegt nur der GERICHTETE Anteil einer Route, Energie x (1 - Erg.AnteilDiffus). Der
        /// Diffusanteil ist das Hallfeld 4P/A des Hoererraums (RaumwegKern.Route: die Abstrahlung der letzten Oeffnung und die Beugung
        /// durch die Oeffnungen zaehlen dort schon als gerichtet); ein Diffusfeld faellt aus allen Richtungen gleich ein und hat keine
        /// Richtung (Kuttruff, Room Acoustics, Kap. 5: Diffusfeld = isotrope Einfallsverteilung). Auch das Spiel trennt so: der
        /// Diffusanteil geht als Reverb-Send, trocken (an der virtuellen Position) spielt nur der Rest (DiffusKern). Die Laufzeit t0
        /// nimmt ebenfalls nur Routen mit gerichtetem Anteil (das Hallfeld hat keine erste Wellenfront).
        /// Untergrenze: keine. Der trockene, gerichtete Teil ist genau das, was an der Position gespielt wird - ist er klein, ist er auch
        /// leise; seine Richtung ist Geometrie (kein Messrauschen). Nur wenn KEINE Route gerichtete Energie hat (Anteil 1, z. B. alle Wege
        /// nur ueber Raumfolgen der Wegsuche), gilt wie bis 16.11. die ganze Energie - dann zeigt die Richtung dorthin, wo das Hallfeld
        /// einstroemt, statt auf nichts. Marke 2 Bot10 mit Ecken (raumecke-STAND): ganze Energie -117...-139 Grad, gerichtet -16...-21 Grad.
        /// austritt = h + mittlere Richtung x mittlerer Abstand, buendel = Laenge des Mittelvektors (0..1). Pegel und Baender bleiben bei
        /// RoutenMischer.Summe. false = keine Route traegt bei. Keine Speicheranforderung.</summary>
        public static bool MischRichtung(System.Collections.Generic.List<RoutenEintrag> eintraege, V3 h, out V3 austritt, out double buendel)
            => MischRichtung(eintraege, h, 0.0, out austritt, out buendel);

        /// <summary>tunneldiffus (17.11.2026): wie oben, mit kappa = Teil des Diffusanteils, der TROCKEN an der virtuellen Position gespielt wird
        /// (0 = Diffusanteil im Hall wie 10.-17.11., 1 = diffushoerbar: alles trocken). Grund (Meldung Tunnel 17.11. 17:47): seit diffushoerbar
        /// spielt der Diffusanteil (90 % der Energie) wieder trocken an der Position - die Richtung wurde aber nur aus dem gerichteten Rest
        /// (10 %) gebildet. Bei Routen mit verschiedenen Austritten zog der schwache gerichtete Rest der Nebenrouten die Mitte weg vom
        /// lautesten Austritt (Log Bot32: 74 Grad zwischen Mitte und lautester Tuer, Buendel 0,44), und Ziel() drehte die Richtung nur zu 44 %
        /// von der Quelle weg: 90 % der Energie kamen 9 Grad neben der Quelle durch die Decke. Der Diffusanteil kommt physikalisch durch die
        /// Oeffnungen herein (Leistung P der letzten Oeffnung, Kuttruff Kap. 5); seine erste Wellenfront und damit die Ortung (Praezedenz,
        /// Blauert 1997 Kap. 3.1) liegt an der Oeffnung, nie am Ort der Quelle hinter der Wand. Gewicht je Route = Energie x (1 - a + kappa a).</summary>
        public static bool MischRichtung(System.Collections.Generic.List<RoutenEintrag> eintraege, V3 h, double kappa, out V3 austritt, out double buendel)
            => MischRichtung(eintraege, h, kappa, false, out austritt, out buendel);

        /// <summary>wegrichtung2 (17.11.2026): wie oben; fuehrend = true: nur die Seite des fuehrenden Wegs (VorrangSeite) zieht die Richtung.
        /// buendel bleibt die Laenge des Mittelvektors ALLER Routen (Breite, nur Log).</summary>
        public static bool MischRichtung(System.Collections.Generic.List<RoutenEintrag> eintraege, V3 h, double kappa, bool fuehrend, out V3 austritt, out double buendel)
        {
            austritt = default; buendel = 0;
            if (eintraege == null) return false;
            if (Mischen(eintraege, h, true, kappa, fuehrend, out austritt, out buendel)) return true;
            return Mischen(eintraege, h, false, 0.0, fuehrend, out austritt, out buendel);
        }

        // ------------------------------------------------------------------------------------------
        // wegrichtung2 (17.11.2026): fuehrender Weg statt Mitte zwischen den Seiten.
        // Befund (Klang-Log 17.11. 18:37-18:49, paket-1117d, review-20261110/wegrichtung2-STAND.md): bei 10 % der hoerbaren Raumweg-Zeilen
        // ist das Buendel < 0,8 - die Austritte liegen auf verschiedenen Seiten. Seit tunneldiffus wird die Richtung VOLL zur Mitte gedreht
        // (RaumGewicht ohne Buendel), z. B. 18:39:54 Bot12: Mitte 84 Grad neben der lautesten Tuer, Buendel 0,39 - die Mitte zeigt auf die
        // Wand zwischen den Oeffnungen, aus der sicher kein Schall kommt.
        // Physik: Zwei Wege mit mehr als ~1 ms Laufzeitunterschied ortet das Gehoer nach der ersten Wellenfront (Praezedenz, Blauert 1997
        // Kap. 3.1; Litovsky et al. 1999, JASA 106); das Energiemittel (Summenlokalisation) gilt nur fuer nahezu gleichzeitige, kohaerente
        // Wege - und auch dort nur bei kleinem Winkel zwischen ihnen: die Phantomquelle zweier gleich lauter Quellen ist bis zu einem
        // Oeffnungswinkel von etwa 60 Grad stabil (Stereo-Basis +-30 Grad), bei seitlichen/gegenueberliegenden Paaren zerfaellt sie
        // (Theile & Plenge 1977, JAES 25; Pulkki 2001 zur Phantomquelle seitlicher Lautsprecherpaare). Regel:
        //  1. Gewicht je Route wie bisher: Energie x 10^(-Vorrang(t - t0)/10) (der fruehe Weg bekommt 10 dB Vorsprung).
        //  2. Fuehrende Route = groesstes Gewicht. Jede Route zieht die Richtung nur mit VorrangSeite(cos Winkel zur fuehrenden): voll bis
        //     60 Grad Abstand, weich (smoothstep) auf 0 bei 90 Grad, darueber gar nicht (eigenes, getrenntes Hoerereignis bzw. unterdrueckt).
        //  Stetig in allen Routenwerten, ausser beim Wechsel der fuehrenden Route zwischen zwei Seiten - das ist der physikalische
        //  Wechsel der ersten Wellenfront; die Nachfuehrung (Richtung.cs, Zeitkonstante) fuehrt dann ueber die Zwischenwinkel.
        //  Die Grenzen 60/90 Grad sind eine Annahme aus der Stereo-Literatur (Lautsprecher-Phantomquellen), nicht fuer Raumwege gemessen.
        // ------------------------------------------------------------------------------------------
        public const double SeiteVollCos = 0.5;   // cos 60 Grad

        // ------------------------------------------------------------------------------------------
        // raid2242richtung (17.11.2026): Praezedenz mit der vollen Energie jeder Route, nicht mit dem Rang-Gewicht des Mischers.
        // Befund (Klang-Log 22:42, Marke 2, Hoerer im Lab_Metal_Tube_Room_3, Schuetzen in Area_1 57-67 m; review-20261110/raid2242/
        // richtung-STAND.md): vier Routen, die frueheste (#109 > #2, 66,9 m, 14 Grad neben der Quelle, -14,4 dB) liegt auf Rang 4 und hat
        // im Mischer das weiche Rang-Gewicht w 0,11 (soft rank N 3, RoutenMischer.Gewichte - eine Auswahl der 3 lautesten fuer eine stetige
        // Pegelsumme, ein Kostenmittel aus der Demo, keine Physik). Gewicht() rechnete Energie x w x Vorrang: die erste Wellenfront verlor
        // dadurch 9,6 dB und die Treppenhaus-Route von HINTEN (#163 > #183 > #208, 82,6 m, 45 ms spaeter, -8,5 dB) wurde "fuehrend": Schuesse
        // 149-155 Grad neben der Quelle (Kaan: "kommt mehr von hinten als aus der Richtung mit Oeffnungen"). Physik: wer zuerst ankommt,
        // haengt an Laufzeit und Energie der Route, nicht daran, ob sie unter die 3 lautesten faellt (Blauert 1997 Kap. 3.1). Darum zaehlt
        // fuer die Richtung die Anwesenheit (RoutenEintrag.Anwesend: geliefert = 1, blendet wie W). Pegel und Energieverteilung der
        // Stimmen (ZweitwegKern.Energie) bleiben beim Rang-Gewicht. Grenze: Routen, deren Rang-Gewicht schon bei der Uebernahme 0 ist
        // (RoutenMischer.Uebernehmen legt sie nicht an), fehlen weiter. false = Verhalten bis 17.11. (Schalter in Richtung.cs).
        // ------------------------------------------------------------------------------------------
        internal static bool PraezedenzOhneRang;

        /// <summary>wegrichtung2: Zuggewicht einer Route mit cos c ihres Winkels zur fuehrenden Route: 1 bis 60 Grad, 0 ab 90 Grad.</summary>
        public static double VorrangSeite(double c) => Glatt(c / SeiteVollCos);

        /// <summary>wegrichtung2: Energieanteil des Raumwegs gegen die Wand zwischen den Raeumen aus den UNGEGLAETTETEN Daemmungen (dB &gt;= 0).
        /// Bis 17.11. kam der Anteil aus Route gegen die angewandte Wirkung des Spiels (Anteil(routeDb, gesamtDb)); die ist geglaettet
        /// (Lerp je Aufruf) und hinkte jeder Aenderung nach - ohne jede Wand ergab 0,01 Score-Unterschied (0,6 dB) 87 % "Raumweg",
        /// die Richtung blieb zu 13 % bei der Quelle (Log 18:37-18:49: 3 % der hoerbaren Zeilen, bis 0,15 Score = 9 dB). Dieselbe Regel
        /// wie Raumweg.Tiefpass (Wand aus dem ungeglaetteten Wert). wandDb NaN/unendlich = keine Wandmessung -&gt; 1.</summary>
        public static double AnteilWand(double routeDb, double wandDb)
        {
            if (double.IsNaN(routeDb)) return 0;
            if (double.IsNaN(wandDb) || double.IsInfinity(wandDb)) return 1;
            double er = Math.Pow(10, -routeDb / 10), ew = Math.Pow(10, -wandDb / 10);
            return er + ew > 0 ? er / (er + ew) : 0;
        }

        /// <summary>tunneldiffus: Gewicht der Austrittsrichtung in Ziel(). Bisher Anteil x Buendel: zeigten die Austritte auseinander, blieb die
        /// Richtung zum Teil bei der Quelle - die einzige Richtung, aus der der Raumweg sicher NICHT kommt (Anteil = Energie ueber die Oeffnungen;
        /// was durch die Wand kommt, haelt Anteil &lt; 1 fest). ausOeffnungen = true: nur der Anteil; die Richtung ist die energiegewichtete
        /// Mitte der Austritte. Die Breite (Buendel klein = Klang von mehreren Seiten) kann eine einzelne Position nicht darstellen - das ist eine
        /// Frage an den Spielcode (Metas Volumetric radius je Quelle), nicht an die Richtung.</summary>
        public static double RaumGewicht(double anteil, double buendel, bool ausOeffnungen)
            => ausOeffnungen ? Clamp01(anteil) : Clamp01(anteil) * Clamp01(buendel);

        /// <summary>Gerichteter Anteil einer Route (0..1) im Bezugsband: 1 - AnteilDiffus.</summary>
        public static double Gerichtet(RoutenEintrag e)
        {
            double a = e.Erg.AnteilBand(0);
            return !(a > 0) ? 1 : a >= 1 ? 0 : 1 - a;
        }

        static bool Mischen(System.Collections.Generic.List<RoutenEintrag> eintraege, V3 h, bool gerichtet, double kappa, bool fuehrend, out V3 austritt, out double buendel)
        {
            kappa = Clamp01(kappa);
            austritt = default; buendel = 0;
            double t0 = double.PositiveInfinity;
            for (int i = 0; i < eintraege.Count; i++)
            {
                var e = eintraege[i];
                if (e.W <= 0 || (gerichtet && !(Gerichtet(e) > 0))) continue;
                double l = e.Erg.Laenge;
                if (l > 0 && l < t0) t0 = l;
            }
            // wegrichtung2: fuehrende Route (groesstes Gewicht nach Vorrang) - nur ihre Seite zieht die Richtung
            V3 an = default; double xMax = 0;
            if (fuehrend)
                for (int i = 0; i < eintraege.Count; i++)
                {
                    double x = Gewicht(eintraege[i], h, gerichtet, kappa, t0, out V3 r, out double lr);
                    if (x > xMax && lr > 1e-6) { xMax = x; an = r * (1.0 / lr); }
                }
            double sw = 0, sl = 0, sa = 0; V3 u = default, ua = default;
            for (int i = 0; i < eintraege.Count; i++)
            {
                double x0 = Gewicht(eintraege[i], h, gerichtet, kappa, t0, out V3 r, out double lr);
                if (!(x0 > 0) || lr <= 1e-6) continue;
                ua = ua + r * (x0 / lr); sa += x0;   // alle Routen: Breite (Buendel)
                if (xMax > 0) x0 *= VorrangSeite(V3.Dot(r, an) / lr);
                if (!(x0 > 0)) continue;
                u = u + r * (x0 / lr); sl += x0 * lr; sw += x0;
            }
            double lu = u.Laenge;
            if (!(sw > 0) || lu < 1e-12) return false;
            buendel = Math.Min(1, (fuehrend ? ua.Laenge / Math.Max(sa, 1e-300) : lu / sw));
            austritt = h + u * (sl / sw / lu);
            return true;
        }

        /// <summary>Gewicht einer Route fuer die Richtung: Energie (Bezugsband) x gerichteter/trocken gespielter Anteil x Vorrang; r = Austritt - h.</summary>
        internal static double Gewicht(RoutenEintrag e, V3 h, bool gerichtet, double kappa, double t0, out V3 r, out double lr)
        {
            r = e.Austritt - h; lr = r.Laenge;
            if (e.W <= 0) return 0;
            double x0 = (PraezedenzOhneRang ? Clamp01(e.Anwesend) : e.W) * Math.Pow(10, -e.Db[0] / 10);
            if (gerichtet) x0 *= kappa > 0 ? Gerichtet(e) + kappa * (1 - Gerichtet(e)) : Gerichtet(e);
            if (!(x0 > 0)) return 0;
            double l = e.Erg.Laenge;
            double dtMs = l > 0 && !double.IsInfinity(t0) ? (l - t0) / RaumwegKern.Schall * 1000.0 : 0;
            return x0 * Math.Pow(10, -Vorrang(dtMs) / 10.0);
        }

        /// <summary>wegrichtung: Nachfuehren wie Nachfuehren (Zeitkonstante tauS), dazu hoechstens maxGradS Grad je Sekunde. Grund: das Ziel
        /// springt, wenn Quelle oder Hoerer in eine andere Zelle der Wegtabelle treten (andere Sondenpaare, andere Kanten; Messung: bis
        /// 112 Grad in einem Bild) - die Zeitkonstante allein laesst davon im ersten Bild 1 - e^(-dt/tau) durch (bei 120 ms und 60 Bildern/s
        /// 13 %). maxGradS &lt;= 0: ohne Begrenzung.</summary>
        public static V3 NachfuehrenBegrenzt(V3 alt, V3 ziel, double dt, double tauS, double maxGradS)
        {
            V3 neu = Nachfuehren(alt, ziel, dt, tauS);
            if (alt.IstNull || !(maxGradS > 0) || !(dt > 0)) return neu;
            double w = Winkel(alt, neu), max = maxGradS * dt;
            return w <= max || w < 1e-9 ? neu : Slerp(alt, neu, max / w);
        }

        /// <summary>wegrichtung: Zielrichtung im Hindernis-Fall. u = Einheitsvektor Hoerer -> echte Quelle, kante = Mittelrichtung
        /// (KantenMittel), gewicht = Anteil x Fokus (0..1). Ergebnis: von u um gewicht zur Kante gedreht.</summary>
        public static V3 KantenZiel(V3 u, V3 kante, double gewicht)
            => kante.IstNull || !(gewicht > 0) ? u : Slerp(u, kante, Clamp01(gewicht));
    }

    /// <summary>Abschirmung eines Wegs im Freien (ISO 9613-2) aus einem Hoehenprofil.</summary>
    internal struct Hindernis
    {
        public bool Gueltig;                 // gerechnet
        public bool Frei;                    // Gummiband ohne Kante ueber der Sichtlinie
        public int Kanten;                   // Ecken der Huelle zwischen den Endpunkten
        public double Umweg;                 // z (m)
        public double Dss, Dsr, E, Direkt, Laenge;
        public V3 ErsteKante, LetzteKante;
        public double Db0, Db1, Db2;         // Dz je Band (Bezug / 1 kHz / 4 kHz), gedeckelt
        public double HoechsteKante;         // hoechster Huellpunkt ueber der Sichtlinie (m), Log
        public double Streif;                // 0.8.8: freie Linie knapp ueber einer Kante: Wegdifferenz (m, > 0), 0 = keine; Db0..2 dann > 0
        // 0.8.8 Klang-Log (Ground Zero 09.11. MARK 8-14): massgebliche Kante (hoechster Profilpunkt ueber der Sichtlinie)
        public string KanteName;             // Collider (null = keiner / unbekannt)
        public double KanteUeber, KanteS;    // Hoehe ueber der Sichtlinie (m), waagerechter Abstand von a (m)
        public double AUeberBoden, BUeberBoden;   // Hoehe der Endpunkte ueber dem Boden (m, NaN = unbekannt)
        public int Ueberhang;                // Profilpunkte, an denen ein Collider nur ueber der Linie hing (verworfen)
        // 0.8.8 (Streets 10.11.2026 "BTR durch Gebaeude"): Richtungskante (letzte Huellecke = erste Kante vom Hoerer aus) und Bebauung
        public double LetzteS;               // waagerechter Abstand der letzten Kante von a (m)
        public string LetzteName;            // Collider der letzten Kante (Log)
        public double Bebaut, Haus;          // bebaute Strecke auf der Linie ausser den Kanten-Gebaeuden (m), A_house (dB, in Db0..2 enthalten)
    }

    internal static class HindernisKern
    {
        public const double C2 = 20.0;
        public const double DeckelEinfach = 20.0, DeckelMehrfach = 25.0;
        public const int MaxProfil = 128;

        /// <summary>ISO 9613-2:1996 Gl. (14)/(18): Abschirmmass Dz (dB) bei Frequenz f. z = Umweg (m), dss/dsr = Quelle-erste Kante /
        /// letzte Kante-Empfaenger, d = Direktabstand, e = Abstand erste-letzte Kante (mehrfach). Gedeckelt 20 / 25 dB.</summary>
        public static double Dz(double f, double z, double dss, double dsr, double d, double e, bool mehrfach)
        {
            double lambda = RaumwegKern.Schall / Math.Max(f, 1.0);
            double c3 = 1.0;
            if (mehrfach)
            {
                double x = 5 * lambda / Math.Max(e, 1e-3);
                x *= x;
                c3 = (1 + x) / (1.0 / 3.0 + x);
            }
            // 0.8.8 (Meldung Streets 09.11.2026 "harter Cut am Taxi"): Gl. (14) springt an der Schattengrenze von 0 auf 10 lg 3 = 4,8 dB
            // (Kante genau auf der Sichtlinie), obwohl sich das Schallfeld dort stetig aendert. Wie CNOSSOS-EU (Richtlinie (EU) 2015/996,
            // Anhang 2.5.6, Beugung: Delta = 10 lg(3 + 40/lambda * C'' * delta) fuer 40/lambda * C'' * delta >= -2, sonst 0) zaehlt eine
            // Kante knapp UNTER der Sichtlinie mit negativer Wegdifferenz weiter, bis das Argument 1 ist (0 dB): mit C2 = 20 bei
            // z = -lambda/10 (250 Hz: 14 cm, 1 kHz: 3 cm). Stetig in z, an z = 0 genau 4,8 dB von beiden Seiten.
            if (z < 0)
            {
                double arg = 3 + C2 / lambda * c3 * z;
                return arg <= 1 ? 0 : Math.Min(mehrfach ? DeckelMehrfach : DeckelEinfach, 10 * Math.Log10(arg));
            }
            double kmet = z > 1e-9 ? Math.Exp(-(1.0 / 2000.0) * Math.Sqrt(dss * dsr * d / (2 * z))) : 1.0;
            double dz = 10 * Math.Log10(3 + C2 / lambda * c3 * z * kmet);
            return Math.Max(0, Math.Min(mehrfach ? DeckelMehrfach : DeckelEinfach, dz));
        }

        /// <summary>Gummiband von a nach b ueber das Profil: s[i] = waagerechter Abstand von a (0 &lt; s &lt; D), y[i] = Oberkante
        /// (Weltkoordinate y, NaN = nichts). Ergebnis mit Dz je Band (f0/f1/f2).</summary>
        /// <remarks>0.8.8: bodenA/bodenB = Boden unter a und b (Weltkoordinate y, NaN = unbekannt). Ohne Kante ueber der Sichtlinie
        /// zaehlt die Kante, an der die Linie am knappsten vorbeigeht, mit negativer Wegdifferenz (Dz, CNOSSOS-EU) - aber nur, wenn sie
        /// aus dem Boden herausragt: Punkte bis eine Wellenlaenge (Bezugsband) ueber der Bodenlinie a-b sind Bodenrauigkeit, kein
        /// beugendes Hindernis (ISO 9613-2 rechnet den Boden in A_gr, nicht in D_z). Gewicht smoothstep(Ueberstand / lambda), damit
        /// auch dieses Kriterium stetig ist.</remarks>
        public static Hindernis Rechnen(V3 a, V3 b, double[] s, double[] y, int n, double f0, double f1, double f2,
            double[] hs = null, double[] hy = null, double bodenA = double.NaN, double bodenB = double.NaN)
        {
            var r = new Hindernis { Gueltig = true };
            double dx = b.X - a.X, dz = b.Z - a.Z;
            double D = Math.Sqrt(dx * dx + dz * dz);
            r.Direkt = V3.Abstand(a, b);
            r.Laenge = r.Direkt;
            if (D < 1e-3 || r.Direkt < 0.5) { r.Frei = true; return r; }
            V3 hor = new V3(dx / D, 0, dz / D);
            // Huelle (obere, monoton von links nach rechts); Profilpunkte nach s sortiert erwartet
            hs = hs ?? new double[MaxProfil + 2]; hy = hy ?? new double[MaxProfil + 2];
            int k = 0;
            hs[k] = 0; hy[k] = a.Y; k++;
            n = Math.Min(n, MaxProfil);
            double vorS = 0;
            for (int i = 0; i <= n; i++)
            {
                double ps, py;
                if (i < n)
                {
                    ps = s[i]; py = y[i];
                    if (double.IsNaN(py) || ps <= 1e-6 || ps >= D - 1e-6 || ps < vorS) continue;
                    // nur Punkte ueber der Sichtlinie koennen Ecken werden (darunter zaehlt das Gelaende nicht)
                    double linie = a.Y + (b.Y - a.Y) * ps / D;
                    if (py <= linie) continue;
                    vorS = ps;
                }
                else { ps = D; py = b.Y; }
                while (k >= 2 && Kreuz(hs[k - 2], hy[k - 2], hs[k - 1], hy[k - 1], ps, py) >= 0) k--;
                hs[k] = ps; hy[k] = py; k++;
            }
            r.Kanten = k - 2;
            if (r.Kanten <= 0) { r.Frei = true; Streifen(ref r, a, b, D, s, y, n, f0, f1, f2, bodenA, bodenB); return r; }
            double laenge = 0;
            for (int i = 1; i < k; i++) laenge += Strecke(hs[i - 1], hy[i - 1], hs[i], hy[i]);
            r.Laenge = laenge;
            r.Umweg = Math.Max(0, laenge - r.Direkt);
            r.Dss = Strecke(hs[0], hy[0], hs[1], hy[1]);
            r.Dsr = Strecke(hs[k - 2], hy[k - 2], hs[k - 1], hy[k - 1]);
            r.E = Math.Max(0, laenge - r.Dss - r.Dsr);
            r.ErsteKante = a + hor * hs[1]; r.ErsteKante.Y = hy[1];
            r.LetzteKante = a + hor * hs[k - 2]; r.LetzteKante.Y = hy[k - 2]; r.LetzteS = hs[k - 2];
            double hoch = 0;
            for (int i = 1; i < k - 1; i++) hoch = Math.Max(hoch, hy[i] - (a.Y + (b.Y - a.Y) * hs[i] / D));
            r.HoechsteKante = hoch;
            bool mehr = r.Kanten >= 2;
            r.Db0 = Dz(f0, r.Umweg, r.Dss, r.Dsr, r.Direkt, r.E, mehr);
            r.Db1 = Dz(f1, r.Umweg, r.Dss, r.Dsr, r.Direkt, r.E, mehr);
            r.Db2 = Dz(f2, r.Umweg, r.Dss, r.Dsr, r.Direkt, r.E, mehr);
            return r;
        }

        /// <summary>0.8.8: freie Sichtlinie knapp ueber einer herausragenden Kante -> Dz mit negativer Wegdifferenz (siehe Rechnen).</summary>
        static void Streifen(ref Hindernis r, V3 a, V3 b, double D, double[] s, double[] y, int n, double f0, double f1, double f2,
                             double bodenA, double bodenB)
        {
            if (double.IsNaN(bodenA) || double.IsNaN(bodenB)) return;
            double lam0 = RaumwegKern.Schall / Math.Max(f0, 1.0);
            double direkt = Strecke(0, a.Y, D, b.Y), besterDb = 0, besterZ = 0;
            for (int i = 0; i < n; i++)
            {
                double ps = s[i], py = y[i];
                if (double.IsNaN(py) || ps <= 1e-6 || ps >= D - 1e-6) continue;
                double boden = bodenA + (bodenB - bodenA) * ps / D;
                double x = Math.Max(0, Math.Min(1, (py - boden) / lam0));
                if (x <= 0) continue;
                double z = Strecke(0, a.Y, ps, py) + Strecke(ps, py, D, b.Y) - direkt;   // > 0: Weg ueber die Kante ist laenger
                double db = Dz(f0, -z, 0, 0, direkt, 0, false) * x * x * (3 - 2 * x);
                if (db > besterDb) { besterDb = db; besterZ = z; }
            }
            if (besterDb <= 0) return;
            double w = besterDb / Math.Max(1e-9, Dz(f0, -besterZ, 0, 0, direkt, 0, false));
            r.Streif = besterZ;
            r.Db0 = besterDb;
            r.Db1 = Dz(f1, -besterZ, 0, 0, direkt, 0, false) * w;
            r.Db2 = Dz(f2, -besterZ, 0, 0, direkt, 0, false) * w;
        }

        static double Kreuz(double os, double oy, double as_, double ay, double bs, double by)
            => (as_ - os) * (by - oy) - (ay - oy) * (bs - os);

        static double Strecke(double s1, double y1, double s2, double y2)
        {
            double a = s2 - s1, b = y2 - y1;
            return Math.Sqrt(a * a + b * b);
        }

        /// <summary>Stuetzstellen des Profils zwischen a und b (waagerecht): Anzahl und Abstand. Schritt 2 m, hoechstens maxN.</summary>
        public static int Stuetzstellen(double waagerecht, int maxN, out double schritt)
        {
            int n = (int)Math.Floor(waagerecht / 2.0);
            n = Math.Max(0, Math.Min(maxN, n));
            schritt = n > 0 ? waagerecht / (n + 1) : 0;
            return n;
        }

        static readonly double[] FresnelT = { 0, 0.03, 0.1, 0.25, 0.5, 0.75, 0.9, 0.97, 1 };
        public const int FresnelN = 9;
        public const double FresnelZugabe = 1.3;

        /// <summary>0.8.8: Polylinie unter der Sichtlinie a-b, so tief, wie eine Kante noch zaehlt (Streifen): z = lambda/10 im Bezugsband,
        /// also h(s) = sqrt(lambda/5 * s1 * s2 / D), mit Zugabe 1,3 (die Sehnen zwischen den Punkten liegen sonst ueber der Grenze).
        /// Strahlen entlang dieser Linie finden duenne Hindernisse knapp UNTER der Sichtlinie, die das Raster nicht trifft - sonst
        /// waere die Schattengrenze nur fuer Hindernisse stetig, die zufaellig auf einem Rasterpunkt liegen. Gibt FresnelN Punkte.</summary>
        /// <remarks>bodenA/bodenB = Boden unter a/b: die Linie bleibt 0,1 m ueber der Bodenlinie (darunter liegt nichts, das als Kante
        /// zaehlt - Streifen verlangt einen Ueberstand ueber die Bodenlinie -, und ein Strahl im Boden traefe die Mauer darauf nicht).</remarks>
        public static int FresnelUnten(V3 a, V3 b, double f0, V3[] aus, double bodenA = double.NaN, double bodenB = double.NaN)
        {
            double lam = RaumwegKern.Schall / Math.Max(f0, 1.0), d = V3.Abstand(a, b);
            for (int i = 0; i < FresnelN; i++)
            {
                double t = FresnelT[i];
                V3 p = a + (b - a) * t;
                p.Y -= FresnelZugabe * Math.Sqrt(Math.Max(0, lam / 5 * t * (1 - t) * d));
                if (!double.IsNaN(bodenA) && !double.IsNaN(bodenB))
                    p.Y = Math.Max(p.Y, Math.Min(a.Y + (b.Y - a.Y) * t, bodenA + (bodenB - bodenA) * t + 0.1));
                aus[i] = p;
            }
            return FresnelN;
        }

        /// <summary>0.8.8: Rasterpunkte des Hoehenprofils FEST IN DER WELT (Vielfache des Schritts auf der Hauptachse x oder z der Linie),
        /// statt gleichmaessig zwischen den Endpunkten. Vorher (Stuetzstellen) wanderten alle Punkte mit jedem Schritt des Hoerers mit
        /// (Abstand = Laenge / (n + 1)) und trafen ein schmales Hindernis mal und mal nicht. Schritt 2 m, verdoppelt, bis hoechstens
        /// maxN Punkte noetig sind (beim Verdoppeln bleibt jeder zweite Punkt, wo er war). Ergebnis s aufsteigend (Abstand von a).</summary>
        public static int Raster(double ax, double az, double ux, double uz, double wa, int maxN, double[] sAus, out double schritt)
        {
            schritt = 2.0;
            while (wa / schritt > maxN) schritt *= 2.0;
            bool xa = Math.Abs(ux) >= Math.Abs(uz);
            double a0 = xa ? ax : az, ua = xa ? ux : uz;
            if (Math.Abs(ua) < 1e-9 || wa <= 0) return 0;
            double c2 = a0 + ua * wa, lo = Math.Min(a0, c2), hi = Math.Max(a0, c2);
            int n = 0;
            for (long k = (long)Math.Floor(lo / schritt) + 1; k * schritt < hi && n < sAus.Length; k++)
            {
                double sv = (k * schritt - a0) / ua;
                if (sv <= 0.01 || sv >= wa - 0.01) continue;
                sAus[n++] = sv;
            }
            if (ua < 0) Array.Reverse(sAus, 0, n);
            return n;
        }

        // 0.8.8 (Meldung Streets 10.11.2026 00:03, "BTR ist durch die Waende zu hoeren, durch Gebaeude"): BTR 207-225 m entfernt hinter
        // mehreren Haeuserreihen, Abschirmung nur 12-25 dB (eine bzw. zwei Kanten des Gummibands: das hoechste Dach und das Dach vor dem
        // Hoerer), hoerbar +10..+19 dB ueber der Verdeckung. Gl. (14) kennt nur die Ecken der Huelle; jede weitere Haeuserreihe darunter
        // zaehlt nicht. ISO 9613-2:1996 Anhang A.3 (2024: A.4) "Housing": A_house,1 = 0,1 B d_b (dB), B = Bebauungsdichte (Grundflaeche
        // der Haeuser / Gesamtflaeche), d_b = Weglaenge durch das bebaute Gebiet, hoechstens 10 dB; gleicher Wert fuer alle Baender.
        // B d_b auf der Sichtlinie = bebaute Strecke: Rasterpunkte (fester Abstand), an denen ein Gebaeude-Collider (kein Gelaende)
        // mindestens HausMinUeberM ueber die Sichtlinie ragt. Die Gebaeude der ersten und der letzten Kante (die beiden Flanken des
        // Schirms in Gl. 14, d_ss und d_sr) zaehlen nicht noch einmal; Gebaeude dazwischen gehen in D_z nur ueber den Umweg z ein und
        // sind Bebauung (eigene Festlegung gegen Doppelzaehlung; ISO addiert A_house zu den anderen A-Termen ohne gemeinsamen Deckel).
        // A_house,2 (Fassadenluecken einer Strassenreihe) ist nicht enthalten.
        public const double HausJeM = 0.1, HausDeckel = 10.0, HausMinUeberM = 2.0;

        /// <summary>A_house (dB) aus der bebauten Strecke (m): 0,1 dB/m, hoechstens 10 dB.</summary>
        public static double Haus(double bebautM) => Math.Max(0, Math.Min(HausDeckel, HausJeM * bebautM));

        /// <summary>Bebaute Strecke (m): Summe der Rasterabstaende ds der Punkte i mit zaehlt[i], deren Oberkante y[i] mindestens
        /// HausMinUeberM ueber der Sichtlinie a-b liegt (s[i] = waagerechter Abstand von a).</summary>
        public static double Bebaut(V3 a, V3 b, double[] s, double[] y, bool[] zaehlt, int n, double ds)
        {
            double dx = b.X - a.X, dz = b.Z - a.Z, D = Math.Sqrt(dx * dx + dz * dz);
            if (D < 1e-3 || ds <= 0) return 0;
            double summe = 0;
            for (int i = 0; i < n; i++)
            {
                if (!zaehlt[i] || double.IsNaN(y[i]) || s[i] <= 0 || s[i] >= D) continue;
                double linie = a.Y + (b.Y - a.Y) * s[i] / D;
                if (y[i] - linie >= HausMinUeberM) summe += ds;
            }
            return Math.Min(summe, D);
        }

        /// <summary>A_house zur Abschirmung dazu (nur mit Kante ueber der Sichtlinie; freie Linie: kein Gebaeude im Weg).</summary>
        public static void HausDazu(ref Hindernis r, double bebautM)
        {
            r.Bebaut = bebautM; r.Haus = 0;
            if (!r.Gueltig || r.Frei || r.Kanten <= 0) return;
            r.Haus = Haus(bebautM);
            r.Db0 += r.Haus; r.Db1 += r.Haus; r.Db2 += r.Haus;
        }

        /// <summary>Ersatzweg: Raumweg-Daemmung je Band (innen, &gt;= 0) plus Abschirmung im Freien, gedeckelt bei maxDb.</summary>
        public static void Summe(double i0, double i1, double i2, in Hindernis h, double maxDb, out double d0, out double d1, out double d2)
        {
            double a0 = h.Gueltig ? h.Db0 : 0, a1 = h.Gueltig ? h.Db1 : 0, a2 = h.Gueltig ? h.Db2 : 0;   // freie Linie: 0, ausser knapp ueber einer Kante
            d0 = Math.Max(0, Math.Min(maxDb, i0 + a0));
            d1 = Math.Max(d0, Math.Min(maxDb, i1 + a1));
            d2 = Math.Max(d0, Math.Min(maxDb, i2 + a2));
        }
    }
}
