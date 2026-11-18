using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // diffushoerbar (17.11.2026) - Diffusanteil bleibt hoerbar. Reine Rechnung (ohne Unity, Offline-Test).
    //
    // Ursache (Befund B1, Messung 17.11. review-20261110/metamessung/metamessung-STAND.md F3; Raid 12.11. raid1419-STAND.md):
    //   "Diffuse sound as reverb" (DiffusKern.Aufteilen) spielt trocken nur den gerichteten Anteil G und schiebt den Diffusanteil D in den
    //   Meta-Reverb-Send (Hall-Eingang bleibt beim Pegel der Summe). Die ANNAHME dahinter (DiffusKern.cs Kopf): Metas Hall macht aus dem
    //   Eingang ein Hallfeld in der Hoehe von D. Gemessen (Factory P4, Footsteps, 4 kHz): Metas Hall liegt bei Send -4 dB rund 50 dB unter
    //   dem Direktschall (Fenster 150-800 ms gegen 0-50 ms), Send wirkt linear (19,4 von 20 dB), T60 2,66 s = Raum der Quelle.
    //   Auf Gesamtenergie des Halls umgerechnet (Fenster 150-800 ms traegt exp(-13,8*0,15/2,66) - exp(-13,8*0,8/2,66) = 44 % -> +3,5 dB)
    //   und auf Send 0 dB bezogen: eta = Hall/Direkt je Einheit Hall-Eingang ~ -43 dB (P4) bzw. ~ -32 dB (P3, naeher an der Quelle).
    //   D ist damit am Ohr praktisch verloren: dem Hoerer fehlen -10 lg(1 - Anteil) dB, am Send-Deckel 20 - Preset = 20...28 dB.
    //
    // Physik: D ist reale Schallenergie am Ohr (Diffusfeld des Hoererraums aus dem Quellraum, Kuttruff, Room Acoustics, Kap. 5: 4P/A;
    //   EN 12354-1 fuer den Wandweg). Was der Hall nicht hoerbar macht, muss trocken gespielt werden, sonst fehlt es.
    //
    // Loesung (Variante b, energetisch geschlossen; Begruendung im Bericht review-20261110/diffushoerbar-STAND.md):
    //   - Hall-Eingang I* = S * 10^((Preset + Regler)/10): genau der Hall-Eingang, den Kaan seit 10.11. hoert (DiffusKern.Aufteilen haelt
    //     Lautstaerke + Send beim Pegel der Summe, danach der Regler "Diffuse reverb level"). Der Hall wird nicht lauter und nicht leiser
    //     ("Hall etwas uebertrieben" / "falscher Raum" reissen nicht wieder auf).
    //   - Der Hall, den Meta daraus am Ohr macht, H = eta * I (eta gemessen, Regler), ist Teil von D (Lesart 2 der Messung: Metas Hall
    //     kommt aus dem Raum der Quelle - das ist das durchgesickerte Hallfeld). Trocken bleibt v = S - H, nie weniger als G.
    //   - Energie am Ohr: v + H = G + D = S (geschlossen). Traegt der Hall mehr als D (nur bei sehr grossem eta), wird der Send so weit
    //     gesenkt, dass H = D bleibt (v = G). Klemmt der Meta-Send (+20 dB), traegt der Hall nur eta * v * 20 dB.
    //   Mit eta ~ -40 dB ist H < 0,1 % von S: trocken = Summe auf < 0,01 dB. Wird der Hall spaeter wirksam (eigener Raumklang, eta -> 0 dB),
    //   wandert die Energie ohne Codeaenderung in den Hall.
    // Rechnung im gespielten Mass (nach Kennlinie), wie DiffusKern.Aufteilen (ANNAHME dort: die Kennlinie staucht beide Teile gleich).
    // ------------------------------------------------------------------------------------------
    internal static class DiffusHoerbarKern
    {
        /// <summary>Standard fuer eta (dB): Hallenergie am Ohr / Direktenergie am Ohr bei Send 0 dB. Messung 17.11.: -43 dB (P4) .. -32 dB
        /// (P3), 4-kHz-Band, Footsteps ueber den Meta-Spatializer. -40 = gerundete Mitte; im Ergebnis wirkt der Wert erst ab etwa -25 dB.</summary>
        public const double EtaStandardDb = -40.0;
        /// <summary>Grenzen des Meta-Send (MetaXRAudioSource.ReverbSendDb: Mathf.Clamp(value, -60f, 20f)).</summary>
        public const double SendMinDb = -60.0, SendMaxDb = 20.0;
        /// <summary>Rechengrenze der Daempfung (wie WegeKern, 200 dB = still).</summary>
        public const double DbMax = 200.0;

        static double Leistung(double db) => Math.Pow(10.0, -db / 10.0);
        static double Db(double p) => p > 0 ? Math.Min(DbMax, -10.0 * Math.Log10(p)) : DbMax;

        /// <summary>
        /// Aufteilung trocken/Hall bei hoerbarem Diffusanteil. Eingaben (dB, gespieltes Mass): dbGesamt = Daempfung der Summe G + D,
        /// dbTrocken = Daempfung nur des gerichteten Anteils G (&gt;= dbGesamt), sendBasis = Reverb-Send des Presets, reglerDb = "Diffuse reverb
        /// level", etaDb = Metas Hall/Direkt bei Send 0 dB. Ergebnis: dbAus = Daempfung des trockenen Signals, sendAus = Reverb-Send (absolut,
        /// in den Meta-Grenzen), diffusTrockenDb = Daempfung des trocken gespielten Diffusanteils v - G (DbMax = keiner), hallDb = Daempfung
        /// des Halls am Ohr (eta * v * s). Diffusanteil 0 (dbTrocken &lt;= dbGesamt): keine Aufteilung, Send = Preset (wie Physik ohne Hallweg).
        /// </summary>
        public static void Aufteilen(double dbGesamt, double dbTrocken, double sendBasis, double reglerDb, double etaDb,
                                     out double dbAus, out double sendAus, out double diffusTrockenDb, out double hallDb)
            => Aufteilen(dbGesamt, dbTrocken, sendBasis, reglerDb, etaDb, double.NaN, out dbAus, out sendAus, out diffusTrockenDb, out hallDb);

        /// <summary>tunneldiffus (17.11.): wie oben, der trockene Teil traegt aber nur die Energie, die im Ereignispegel ankommt
        /// (dbEreignis &gt;= dbGesamt, ZeitKern: Hallenergie, die nach dem Ereignis abklingt, gehoert nicht zum Ereignis). Der Hall-Eingang
        /// I* bleibt beim Pegel der Summe (dbGesamt) - der Nachhall ist reale Energie, nur eben Hall. NaN = wie bisher (Summe).
        /// G (gerichtet) wird hoechstens so laut wie das Ereignis (die Abstrahlung der letzten Oeffnung ist selbst verschmiert).</summary>
        public static void Aufteilen(double dbGesamt, double dbTrocken, double sendBasis, double reglerDb, double etaDb, double dbEreignis,
                                     out double dbAus, out double sendAus, out double diffusTrockenDb, out double hallDb)
        {
            if (double.IsNaN(dbGesamt)) dbGesamt = 0;
            if (double.IsNaN(sendBasis)) sendBasis = 0;
            if (double.IsNaN(reglerDb)) reglerDb = 0;
            double Sh = Leistung(dbGesamt);                                   // Summe: Hall-Eingang
            double S = double.IsNaN(dbEreignis) ? Sh : Math.Min(Sh, Leistung(dbEreignis));   // Ereignis: trocken hoerbar
            double G = double.IsNaN(dbTrocken) ? S : Leistung(dbTrocken);
            if (G > S) G = S;
            double D = S - G;
            double smin = Math.Pow(10, SendMinDb / 10), smax = Math.Pow(10, SendMaxDb / 10);
            if (!(D > 0) || !(S > 0))
            {
                // tunneldiffus: Hall-Eingang beim Pegel der Summe halten (trocken ist um Sh/S leiser -> Send um so viel hoeher)
                double hoch = S > 0 && Sh > S ? 10 * Math.Log10(Sh / S) : 0;
                dbAus = Db(S); sendAus = Math.Max(SendMinDb, Math.Min(SendMaxDb, sendBasis + hoch));
                diffusTrockenDb = DbMax; hallDb = DbMax;
                return;
            }
            double eta = double.IsNaN(etaDb) ? 0 : Math.Pow(10, etaDb / 10);
            double ziel = Sh * Math.Pow(10, (sendBasis + reglerDb) / 10);   // Hall-Eingang I* (wie seit 10.11., Summe)

            // v + eta * min(I*, smax * v) = S (f(v) steigt in v): zuerst ungeklemmt, sonst am Send-Deckel
            double v = S - eta * ziel;
            if (!(v >= ziel / smax)) v = S / (1 + eta * smax);
            double s;
            if (v < G)
            {
                // Hall traegt mehr als D: trocken = G, Send so weit gesenkt, dass der Hall genau D traegt (eta > 0 hier sicher)
                v = G;
                s = G > 0 ? D / (eta * G) : smax;
            }
            else s = ziel / v;
            if (s > smax) s = smax;
            if (s < smin)
            {
                // ANNAHME: am unteren Meta-Grenzwert (-60 dB) bleibt der Rest-Hall (eta * v * 1e-6) unberuecksichtigt, wenn v = G gilt
                s = smin;
                double v2 = S / (1 + eta * smin);
                if (v2 >= G) v = v2;
            }
            dbAus = Db(v);
            sendAus = 10 * Math.Log10(s);
            diffusTrockenDb = v - G > S * 1e-12 ? Db(v - G) : DbMax;
            hallDb = Db(eta * v * s);
        }

        /// <summary>Wie viel lauter das trockene Signal gegenueber dem gerichteten Anteil allein ist (dB, &gt;= 0): dbTrocken - dbAus.</summary>
        public static double PlusDb(double dbTrocken, double dbAus) => double.IsNaN(dbTrocken) || double.IsNaN(dbAus) ? 0 : Math.Max(0, Math.Min(DbMax, dbTrocken) - dbAus);

        /// <summary>Klang-Log: " | diffuse dry -X dB (+Y dB over directed)" - trocken gespielter Diffusanteil und Anhebung gegenueber G allein.
        /// NaN = nicht angewandt (kein Feld).</summary>
        public static string LogFeld(float diffusTrockenDb, float plusDb) => LogFeld(diffusTrockenDb, plusDb, float.NaN, float.NaN, float.NaN);

        /// <summary>tunneldiffus: dazu " | event -X dB (late Y %, signal Z s)" - Abzug des Ereignispegels gegen die stationaere Summe
        /// (ZeitKern), Anteil der verschmierten Energie, Signaldauer des Clips ("steady" = Dauerklang/unbekannt). ereignisDb NaN = aus.</summary>
        public static string LogFeld(float diffusTrockenDb, float plusDb, float ereignisDb, float spaetAnteil, float signalS, bool sprache = false)
        {
            if (float.IsNaN(diffusTrockenDb)) return null;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string d = diffusTrockenDb >= (float)DbMax - 0.01f ? "none" : (-diffusTrockenDb).ToString("+0.0;-0.0", inv) + " dB";
            string t = " | diffuse dry " + d + " (" + plusDb.ToString("+0.0;-0.0", inv) + " dB over directed part)";
            if (!float.IsNaN(ereignisDb) && sprache)   // tunneldiffus Teil 2: Sprache, frueher Anteil (D50)
                t += " | speech early " + (-ereignisDb).ToString("+0.0;-0.0", inv) + " dB (late " + (100f * spaetAnteil).ToString("F0", inv) + " %, D50 window)";
            else if (!float.IsNaN(ereignisDb))
                t += " | event " + (-ereignisDb).ToString("+0.0;-0.0", inv) + " dB (late " + (100f * spaetAnteil).ToString("F0", inv) + " %, signal "
                     + (float.IsPositiveInfinity(signalS) ? "steady" : signalS.ToString("F2", inv) + " s") + ")";
            return t;
        }

        /// <summary>Gemerkte Werte je Quelle (Pool-Objekt) fuer das Klang-Log; angelegt beim ersten Anwenden, keine Allokation je Klang.</summary>
        public sealed class Merk
        {
            public float TrockenDb = float.NaN, PlusDb = float.NaN;
            public float EreignisDb = float.NaN, Spaet = float.NaN, SignalS = float.NaN;   // tunneldiffus (Log)
            public bool Sprache;                                                            // tunneldiffus Teil 2: Logfeld fuer Sprache
            public void Setzen(double trocken, double plus) { TrockenDb = (float)trocken; PlusDb = (float)plus; }
            public void Vergessen() { TrockenDb = PlusDb = EreignisDb = Spaet = SignalS = float.NaN; }
        }
    }
}
