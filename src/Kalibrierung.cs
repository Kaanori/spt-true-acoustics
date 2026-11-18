using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    // 0.8.8 - Kalibrierung der Quellpegel (Rechnung und Herleitung: KalibrierKern.cs). Spielteil:
    //  - Bezugsschritte: Player.PlayStepSound (EFT 4.1.6) -> soundBank.Play(NestedStepSoundSource, ..., num4, ...) ->
    //    BetterSource.Play(clip1, clip2, balance, volume) (SimpleSource ruft base.Play, dann PlayOneShot(clip1, volume) bei
    //    source1.volume = BaseVolume x Okklusion x Blende). Abgespielt ohne Okklusion: BaseVolume x volume. Prefix auf
    //    PlayStepSound merkt den Spieler, Prefix auf BetterSource.Play nimmt genau dessen NestedStepSoundSource.
    //  - Clip-Pegel: Offline-Tabelle ClipPegelDaten (alle Clips aus EscapeFromTarkov_Data + sounds.bundle, auch gestreamte
    //    Vorbis-Clips; Herleitung KalibrierKern.TabelleLesen). Ersatz AudioClip.GetData (hoechstens 30 s) fuer Clips, die nicht in
    //    der Tabelle stehen; ein noch nicht geladener Clip wird spaeter erneut versucht.
    //  - Kurve in 1 m aus der AudioSource (eigene Kurve, logarithmisch oder linear), Mixergruppe ueber ihren Namen (SEB-Bundle).
    //  - Je Quellart der Wanddaempfung: Median von Soll(r) - Ist(r) am Bezugsabstand r ihrer Quellen (je Clip, Lautstaerke und r
    //    einmal), Korrektur = Median + K, begrenzt -12 ... +3 dB, unter 3 dB keine (0.8.8 Gutachten), WandFilter-Lautstaerke x
    //    10^(Korrektur/20), 300 ms Ueberblendung des Filters.
    //  - 0.8.8 Gutachten: Bezug nach Gangart (nur Asphalt/Beton), ab 30 Schritten; bis dahin der gespeicherte K-Wert des letzten
    //    Raids (BepInEx/cache/TrueAcoustics/calibration-reference.txt), sonst KalibrierKern.KStandard. Keine Einblendung.
    internal static class Kalibrierung
    {
        internal static ConfigEntry<bool> An;

        sealed class ClipWert { public bool Ok; public double Laeq, Lafmax; }
        static readonly Dictionary<int, ClipWert> clips = new Dictionary<int, ClipWert>(512);
        static readonly List<double> kWerte = new List<double>(512);           // 0.8.8: K-Beitraege (gespielt - Soll der Gangart), fremde + eigene
        static readonly int[] jeGang = new int[3], jeGangEigen = new int[3];
        static double kGespeichert = double.NaN; static int nGespeichert; static bool geladen;
        static double kAktuell = double.NaN; static string kQuelle = "default"; static bool kAlt = true;

        sealed class Art
        {
            public readonly Dictionary<long, double> Ist = new Dictionary<long, double>();
            public readonly List<double> Werte = new List<double>();   // Soll(r) - Ist(r) je Quelle (dB)
            public double Median = double.NaN, Korrektur, Geloggt = double.NaN;
            public string Beispiel;   // Clip, Pegel, vol, Gruppe, Kurve, Bezugsabstand der ersten Quelle (Log)
        }
        static readonly Art[] arten = new Art[32];

        static Player imSchritt;
        static readonly AccessTools.FieldRef<Player, BetterSource> schrittQuelle = AccessTools.FieldRefAccess<Player, BetterSource>("NestedStepSoundSource");
        static int dSchritte, dOhneClip, dGestreamt, dAndereSchritte;

        internal static void Binden(ConfigFile c)
        {
            An = c.Bind(FesteQuellen.Sek, "Calibrate source levels", true,
                "Sources without game occlusion (V-Ex taxi, BTR, trains, plane, fires, generators, machines, dripping water, alarms, ambient players) play as loud as " +
                "their clip is mastered. On = True Acoustics compares what the game really plays (clip level A-weighted x volume x curve x mixer group) with the real " +
                "level of the source (table: idling car 62 dB(A) at 1 m = 56 dB(A) at 2 m, generator 85, alarm 100, dripping water 45 ...), at the source's reference " +
                "distance (e.g. 2 m for a car). Reference: footsteps of bots/players on asphalt/concrete, separately per gait (walk 57, run 65, sprint 70 dB(A) at 1 m); " +
                "the raid's own reference from 30 footsteps on, until then the value of the last raid. Corrections are limited to -12 ... +3 dB (lowering allowed, " +
                "raising hardly), corrections below 3 dB are not applied. Applied in the wall-damping filter of the source. Sound log: 'calibration' in the '[Walls]' " +
                "lines, BepInEx log once per kind. Off = clip level as in the game.");
            An.SettingChanged += (_, __) => { foreach (var a in arten) if (a != null) a.Geloggt = double.NaN; };
            // lampenpegel (18.11.2026)
            LampeAn = c.Bind(FesteQuellen.Sek, "Lamps: real hum level (direct sound + room field)", true,
                "Fluorescent lamps were the only continuous sources without level calibration: True Acoustics took the level the game plays " +
                "(clip x volume x the lamp's own distance curve x mixer) as the real level and only subtracted walls and doors, so lamps were heard " +
                "through rooms where a real lamp is inaudible. On = the lamp plays its real level: the hum level at 1 m below, direct sound with " +
                "1/r instead of the game's curve, plus the reverberant field of the room the lamp hangs in (4/A, decaying with distance, Barron), " +
                "the same room model the room paths use for neighbouring rooms - so walking through a door gives no jump. In other rooms the room " +
                "path applies as before, with this level as the source. Lowering is not limited; raising is limited to +3 dB (said in the log). " +
                "Sound log: 'lamp level' at the end of lamp lines; BepInEx log once per lamp ('[Calibration] Lamp ...': clip, volume, curve, " +
                "how much too loud the game plays it). Off = game level as before. Applies live.");
            LampeSoll1m = c.Bind(FesteQuellen.Sek, "Lamps: hum level at 1 m (dB(A))", (float)KalibrierKern.LampeSollStandard,
                new ConfigDescription("Real level of a lamp's hum at 1 m, A-weighted. Both anchors are ESTIMATES: an intact fluorescent lamp hums at " +
                    "about 35 dB(A); a defective or badly maintained magnetic ballast (flickering tubes, like the Factory lamps) at about 45-50 dB(A). " +
                    "Manufacturers' ballast sound classes A-F span about 20 to over 49 dB; there is no standard value. Default 45 = worn ballast. " +
                    "Used only when 'Lamps: real hum level' is on. Applies live.", new AcceptableValueRange<float>(25f, 60f)));
        }

        // ---------------------------------------------------------------- lampenpegel (18.11.2026)
        // Rechnung und Herleitung: KalibrierKern (LampeIst/LampeSoll/LampeRaumfeld/LampeRoutenZusatz/LampeGesamt). Spielteil:
        //  - Lampe = BetterSource mit Preset-Typ Lamp (LoopLampSoundController: SimpleSource auf der Lampen-AudioSource, Mixer
        //    EnvTechnicalSoundsGroup, maxDistance = min(20, Rolloff), eigene Kurve aus dem Prefab - Dekompilat EFT 4.1.6). Einmal-Klaenge der
        //    Lampe (PlayAtPoint, Gruppe Impacts) sind nicht gemeint.
        //  - Spielpegel Ist(d): Clip-LAeq (Offline-Tabelle/GetData wie die Wandquellen) + PreOcclusionVolume (das Spiel setzt
        //    source1.volume = PreOcclusionVolume x OcclusionVolumeFactor x Fade, BetterSource.UpdateSourceVolume) + Kurve bei d / maxDistance
        //    (live gelesen, die Kurve selbst einmal je Lampe kopiert - GetCustomCurve legt Speicher an) + Mixergruppe.
        //  - Wegdaemmung D = was Physik.SollDb ohne diese Korrektur anwenden wuerde (Raumweg, Wand, Hindernis). Raumfeld: Hoerer im Raum der
        //    Lampe -> LampeRaumfeld mit A/V aus Raumweg.RaumAV; Raumweg aktiv -> gekappter Teil der Routen (Raumweg.LampeUeber).
        //  - Ergebnis ersetzt D in SollDb (gleiche Stelle fuer den vollen Weg und den gemerkten Weg GemerktVor). Keine Allokation je Bild:
        //    der Eintrag je Lampe wird einmal angelegt, Texte nur beim Log.
        internal static ConfigEntry<bool> LampeAn;
        internal static ConfigEntry<float> LampeSoll1m;

        sealed class Lampe
        {
            public AudioClip Clip; public ClipWert W;
            public AnimationCurve Kurve; public AudioRolloffMode Modus = (AudioRolloffMode)(-1); public bool HatModus;
            public bool Gemeldet, GrenzeGemeldet, Begrenzt, EigenerRaum, ImRaumweg;
            public float D;
            public double Ist = double.NaN, Soll = double.NaN, Roh = double.NaN, Gesamt, Weg, Gewinn, Zusatz, Ueber, K = double.NaN, A = double.NaN, V;
            public double Ist1m = double.NaN;
            public string Grund, Gruppe; public double GruppeDb;
        }
        static readonly Dictionary<BetterSource, Lampe> lampen = new Dictionary<BetterSource, Lampe>(64);
        static double luftF = double.NaN, luftM;
        static int dLampen, dLampenBegrenzt;

        static bool IstLampe(BetterSource q) => q != null && q.Preset != null && q.Preset.Type == BetterAudio.AudioSourceGroupType.Lamp;

        /// <summary>Kurvenwert der Lampe im Abstand d (eigene Kurve aus der Kopie, sonst wie Unity log./linear).</summary>
        static float LampeKurve(Lampe l, AudioSource s, float d)
        {
            if (s.rolloffMode == AudioRolloffMode.Custom)
                return l.Kurve != null ? Mathf.Clamp01(l.Kurve.Evaluate(d / Mathf.Max(s.maxDistance, 0.01f))) : 1f;
            return Kurve(s, d);
        }

        /// <summary>Daempfung (dB) der Lampe q im Abstand d anstelle der Wegdaemmung dbWeg (Physik.SollDb). Keine Lampe / aus / Clip-Pegel
        /// unbekannt -> dbWeg unveraendert (Grund im Log-Feld).</summary>
        internal static float LampeDb(SpatialAudioOccluder occ, BetterSource q, float d, float dbWeg)
        {
            if (LampeAn == null || !LampeAn.Value || !IstLampe(q)) return dbWeg;
            try
            {
                var s = q.source1;
                if (s == null) return dbWeg;
                if (!lampen.TryGetValue(q, out var l))
                {
                    if (lampen.Count >= 1024) lampen.Clear();
                    l = new Lampe(); lampen[q] = l; dLampen++;
                }
                if (!ReferenceEquals(l.Clip, s.clip)) { l.Clip = s.clip; l.W = null; l.Gemeldet = false; }
                if (l.W == null || !l.W.Ok) l.W = Clip(s.clip);   // nachgeladene Clips: Clip() merkt "nicht geladen" nicht
                if (!l.HatModus || l.Modus != s.rolloffMode)
                {
                    l.Modus = s.rolloffMode; l.HatModus = true; l.Gemeldet = false;
                    l.Kurve = s.rolloffMode == AudioRolloffMode.Custom ? s.GetCustomCurve(AudioSourceCurveType.CustomRolloff) : null;
                }
                l.D = d; l.Weg = dbWeg;
                if (l.W == null || !l.W.Ok)
                {
                    l.Grund = s.clip == null ? "no clip" : s.clip.loadType == AudioClipLoadType.Streaming ? "streamed clip, not in the offline table" : "clip level unknown";
                    l.Gesamt = dbWeg; l.Ist = double.NaN;
                    return dbWeg;
                }
                l.Grund = null;
                l.Gruppe = GruppenName(s.outputAudioMixerGroup);
                l.GruppeDb = KalibrierKern.GruppeDb(l.Gruppe, Draussen());
                l.K = K();
                float vol = q.PreOcclusionVolume;
                l.Ist = KalibrierKern.LampeIst(l.W.Laeq, vol, KalibrierKern.KurveWirksam(LampeKurve(l, s, d), s.spatialBlend), l.GruppeDb);
                l.Ist1m = KalibrierKern.LampeIst(l.W.Laeq, vol, KalibrierKern.KurveWirksam(LampeKurve(l, s, 1f), s.spatialBlend), l.GruppeDb);
                l.Soll = KalibrierKern.LampeSoll(LampeSoll1m.Value, l.K, d);
                // Raumfeld: Nachbarraum ueber den Raumweg (gekappter Teil), sonst eigener Raum (Diffusfeld 4/A mit Abfall)
                l.Zusatz = 0; l.Ueber = 0; l.EigenerRaum = false; l.ImRaumweg = false; l.A = double.NaN; l.V = 0;
                if (Raumweg.LampeUeber(occ, out double ueber, out double routeDb))
                {
                    l.ImRaumweg = true; l.Ueber = ueber;
                    l.Zusatz = KalibrierKern.LampeRoutenZusatz(routeDb, ueber);
                }
                else
                {
                    var cont = occ != null ? occ.SourceContainer : null;
                    var qr = cont != null ? cont.CurrentAudioRoom : null;
                    var hr = occ != null && occ.AudioSystem != null ? occ.AudioSystem.ListenerCurrentRoom : null;
                    if (qr != null && hr != null && qr.ID == hr.ID && Raumweg.RaumAV(qr, out double a, out double v))
                    {
                        float f = Physik.Frequenz.Value;
                        if (f != luftF) { RaumwegKern.LuftWerte(f, out luftM, out _); luftF = f; }
                        l.EigenerRaum = true; l.A = a; l.V = v;
                        l.Zusatz = KalibrierKern.LampeRaumfeld(d, a, v, luftM);
                    }
                }
                l.Gesamt = KalibrierKern.LampeGesamt(l.Ist, l.Soll, dbWeg, l.Zusatz, out l.Roh, out l.Gewinn, out l.Begrenzt);
                if (!l.Gemeldet) { l.Gemeldet = true; LampeMelden(q, s, l); }
                if (l.Begrenzt)
                {
                    dLampenBegrenzt++;
                    if (!l.GrenzeGemeldet)
                    {
                        l.GrenzeGemeldet = true;
                        var inv = CultureInfo.InvariantCulture;
                        Plugin.Log.LogInfo("[Calibration] Lamp '" + q.name + "': raising limited to +3 dB at " + d.ToString("F1", inv) + " m (needed " +
                                           (-l.Roh).ToString("F1", inv) + " dB; the game plays " + l.Ist.ToString("F1", inv) + " dBFS(A) there" +
                                           (l.Ist < -110 ? ", i.e. nothing - beyond the game's range (maxDistance " + s.maxDistance.ToString("F0", inv) + " m)" : "") + ")");
                    }
                }
                return (float)l.Gesamt;
            }
            catch (Exception ex) { Statistik.Fehler(ex); return dbWeg; }
        }

        static void LampeMelden(BetterSource q, AudioSource s, Lampe l)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(400);
            sb.Append("[Calibration] Lamp '").Append(q.name).Append("': clip ").Append(s.clip != null ? s.clip.name : "-").Append(" LAeq ")
              .Append(l.W.Laeq.ToString("F1", inv)).Append(" dBFS(A), volume ").Append(q.PreOcclusionVolume.ToString("F3", inv))
              .Append(", maxDistance ").Append(s.maxDistance.ToString("F1", inv)).Append(" m, rolloff ").Append(s.rolloffMode)
              .Append(", 3D share ").Append(s.spatialBlend.ToString("F2", inv)).Append(", curve at 1/2/5/10/20 m:");
            float[] ab = { 1f, 2f, 5f, 10f, 20f };   // nur einmal je Lampe
            for (int i = 0; i < ab.Length; i++)
            {
                double k = KalibrierKern.KurveWirksam(LampeKurve(l, s, ab[i]), s.spatialBlend);
                sb.Append(i == 0 ? " " : " / ").Append((20 * Math.Log10(Math.Max(k, 1e-6))).ToString("F1", inv));
            }
            sb.Append(" dB, mixer group ").Append(l.Gruppe ?? "-").Append(' ').Append(l.GruppeDb.ToString("+0.0;-0.0", inv)).Append(" dB")
              .Append(KalibrierKern.Kennt(l.Gruppe) ? "" : " (group not in the table: 0 dB assumed)")
              .Append(": game plays ").Append(l.Ist1m.ToString("F1", inv)).Append(" dBFS(A) at 1 m = ").Append((l.Ist1m - l.K).ToString("F1", inv))
              .Append(" dB(A) with reference K ").Append(l.K.ToString("F1", inv)).Append(" (").Append(kQuelle).Append("); target ")
              .Append(LampeSoll1m.Value.ToString("F0", inv)).Append(" dB(A) at 1 m (estimate) -> ")
              .Append((l.Ist1m - l.K - LampeSoll1m.Value).ToString("F1", inv)).Append(" dB too loud at 1 m");
            Plugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>Klang-Log-Feld einer Lampe (" | lamp level: ..."), null = keine Lampe / aus / noch nicht gerechnet. Nur beim Log.</summary>
        internal static string LampeFeld(BetterSource q)
        {
            if (LampeAn == null || !LampeAn.Value || !IstLampe(q) || !lampen.TryGetValue(q, out var l)) return null;
            var inv = CultureInfo.InvariantCulture;
            if (l.Grund != null) return " | lamp level: not calibrated (" + l.Grund + ")";
            var sb = new StringBuilder(260);
            sb.Append(" | lamp level: game plays ").Append(l.Ist.ToString("F1", inv)).Append(" dBFS(A) at ").Append(l.D.ToString("F1", inv)).Append(" m (= ")
              .Append((l.Ist - l.K).ToString("F1", inv)).Append(" dB(A)), free field target ").Append((l.Soll - l.K).ToString("F1", inv)).Append(" dB(A) (")
              .Append(LampeSoll1m.Value.ToString("F0", inv)).Append(" at 1 m, 1/r)");
            if (l.EigenerRaum)
                sb.Append(", own room A ").Append(l.A.ToString("F0", inv)).Append(" m2/V ").Append(l.V.ToString("F0", inv)).Append(" m3 field ")
                  .Append((10 * Math.Log10(1 + l.Zusatz)).ToString("+0.0;-0.0", inv)).Append(" dB");
            else if (l.ImRaumweg)
                sb.Append(", room path above free field (uncapped) ").Append(l.Ueber.ToString("+0.0;-0.0", inv)).Append(" dB");
            else sb.Append(", no room field (other room without room path / room not measured)");
            sb.Append("; at the ear ").Append((l.Soll - l.K + l.Gewinn).ToString("F1", inv)).Append(" dB(A) (path ").Append(l.Weg.ToString("F1", inv))
              .Append(" dB -> ").Append(l.Gewinn.ToString("+0.0;-0.0", inv)).Append(" dB re free) -> applied ").Append(l.Gesamt.ToString("F1", inv))
              .Append(" dB (correction ").Append((l.Gesamt - l.Weg).ToString("+0.0;-0.0", inv)).Append(" dB)");
            if (l.Begrenzt) sb.Append(", raising limited to +3 dB (raw ").Append((-l.Roh).ToString("+0.0;-0.0", inv)).Append(" dB)");
            return sb.ToString();
        }

        static string BezugDatei => System.IO.Path.Combine(System.IO.Path.Combine(BepInEx.Paths.CachePath, "TrueAcoustics"), "calibration-reference.txt");

        static void Laden()
        {
            if (geladen) return;
            geladen = true;
            try { if (System.IO.File.Exists(BezugDatei)) kGespeichert = KalibrierKern.BezugLesen(System.IO.File.ReadAllText(BezugDatei), out nGespeichert); }
            catch (Exception ex) { Plugin.Log.LogWarning("[Calibration] could not read " + BezugDatei + ": " + ex.Message); }
        }

        /// <summary>Raidende/Szenenwechsel: den Bezug dieses Raids speichern (ab 30 Schritten), dann alles zuruecksetzen.</summary>
        internal static void SzeneGewechselt()
        {
            try
            {
                if (kWerte.Count >= KalibrierKern.BezugMin)
                {
                    double k = KalibrierKern.Median(kWerte);
                    var dir = System.IO.Path.GetDirectoryName(BezugDatei);
                    if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(BezugDatei, KalibrierKern.BezugText(k, kWerte.Count));
                    kGespeichert = k; nGespeichert = kWerte.Count;
                    Plugin.Log.LogInfo("[Calibration] reference of this raid saved: K " + k.ToString("F1", CultureInfo.InvariantCulture) + " dBFS(A) per dB(A) from " + kWerte.Count + " footsteps");
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Calibration] could not save the reference: " + ex.Message); }
            kWerte.Clear(); for (int i = 0; i < 3; i++) { jeGang[i] = 0; jeGangEigen[i] = 0; }
            kAlt = true; kAktuell = double.NaN;
            for (int i = 0; i < arten.Length; i++) arten[i] = null;
            imSchritt = null;
            lampen.Clear();   // lampenpegel (18.11.)
        }

        /// <summary>Bezug K fuer diesen Moment: ab 30 Schritten der des Raids, sonst der des letzten Raids, sonst der Standard.</summary>
        internal static double K()
        {
            Laden();
            if (kAlt || double.IsNaN(kAktuell)) { kAktuell = KalibrierKern.BezugK(kWerte, kGespeichert, out kQuelle); kAlt = false; }
            return kAktuell;
        }

        /// <summary>0.8.8 Abstandskurve: um wie viel dB die Quelle real (Tabelle in 1 m + K) lauter ist, als das Spiel ihren Clip im
        /// Plateau spielt (Clip + Lautstaerke + Gruppe). NaN = Clip oder Quellart unbekannt. Fuer UmgebungKern.Bezugsabstand.</summary>
        internal static double UeberDb(PegelArt a, AudioSource s)
        {
            if (s == null || !QuellPegelKern.Gilt(a)) return double.NaN;
            try
            {
                var w = Clip(s.clip);
                if (w == null || !w.Ok) return double.NaN;
                double clipDb = KalibrierKern.Einzelklang(a) ? w.Lafmax : w.Laeq;
                string gruppe = GruppenName(s.outputAudioMixerGroup);
                double ist = KalibrierKern.Gespielt(clipDb, s.volume, 1.0, KalibrierKern.GruppeDb(gruppe, Draussen()));
                return QuellPegelKern.DbA(a) + K() - ist + Fremd(s);
            }
            catch (Exception) { return double.NaN; }
        }

        // ---------------------------------------------------------------- Mixergruppe -> Tabellenname
        // Die Gruppenwerte (KalibrierKern.GruppeDb) gehoeren zu den Gruppen des SEB-Mixers und werden ueber den NAMEN gefunden.
        // Haengt eine andere Mod eine eigene Kindgruppe dazwischen (Beispiel Guns Are Loud 1.2.0, GunshotContrastMixerRouter:
        // jede Quelle der Umgebung/Schritte/Ambience spielt in "<Gruppe>/GAL Contrast Input", 0 dB, Eltern-Gruppe unveraendert),
        // traegt die Quelle einen unbekannten Namen und bekam bis hier 0 dB statt des Werts der Eltern-Gruppe (EventRadio innen
        // -80 dB, RadioLessVerb -27, AmbFirefight -10/-15, ClientPlayerMovement -3,5 -> Bezug K der Schritte um 3,5 dB daneben).
        // Unity gibt den Elternteil einer AudioMixerGroup zur Laufzeit nicht heraus; FindMatchingGroups("A/B") findet aber genau
        // die Gruppen, deren Pfad ".../A/B" enthaelt. Gesucht wird der naechste Vorfahr mit einem Tabellenwert (je Gruppe einmal).
        static readonly Dictionary<UnityEngine.Audio.AudioMixerGroup, string> gruppenNamen =
            new Dictionary<UnityEngine.Audio.AudioMixerGroup, string>();

        internal static string GruppenName(UnityEngine.Audio.AudioMixerGroup g)
        {
            if (g == null) return null;
            string name = g.name;
            if (KalibrierKern.Kennt(name)) return name;
            if (gruppenNamen.TryGetValue(g, out var fertig)) return fertig;
            string ergebnis = name;
            try
            {
                var mixer = g.audioMixer;
                if (mixer != null)
                {
                    // Vorfahren von innen nach aussen: Kandidat p ist Elternteil, wenn "p/<Pfadrest>" die Gruppe g findet.
                    string rest = name;
                    for (int tiefe = 0; tiefe < 8; tiefe++)
                    {
                        string eltern = null;
                        foreach (var p in mixer.FindMatchingGroups(string.Empty))
                        {
                            if (p == null || p == g) continue;
                            if (Array.IndexOf(mixer.FindMatchingGroups(p.name + "/" + rest), g) >= 0) { eltern = p.name; break; }
                        }
                        if (eltern == null) break;
                        if (KalibrierKern.Kennt(eltern)) { ergebnis = eltern; break; }
                        rest = eltern + "/" + rest;
                    }
                }
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
            if (gruppenNamen.Count > 512) gruppenNamen.Clear();
            gruppenNamen[g] = ergebnis;
            return ergebnis;
        }

        // galpegel (10.11.): Fremdverstaerkung je Mixergruppe (FremdPegelKern (c)). Bezug K kommt von Schritten (ObservedPlayerMovement),
        // die Zielquelle spielt in ihrer eigenen Gruppe; ein Mixer, der beide verschieden verstaerkt (Guns Are Loud: Sends je Kategorie,
        // Elektronik, Contrast), verschiebt beide Ist-Werte: Korrektur += G(Bezug) - G(Ziel). Die gespeicherten K-Werte bleiben ohne
        // Fremdgewinn (gemessen mit den Tabellenwerten der Gruppen), damit ein Headset-Wechsel sie nicht verfaelscht.
        //
        // 10.11. spaet (Entscheidung Kaan): die Kalibrierung gleicht die Gruppen-Balance einer fremden Abmischung NICHT aus. Senkt
        // Guns Are Loud im Headset-Modus Technik/Umgebung staerker ab als Schritte, ist das dessen gewollte Abmischung - die
        // Kalibrierung wuerde sie sonst (bis zum Deckel +3 dB) wieder anheben und dagegen arbeiten. Der Abgleich bleibt in der
        // Hoerbarkeit (FremdPegelKern (a): Hoerschwelle, Eigenrauschen), wo er physikalisch noetig ist.
        static double Fremd(AudioSource s) => 0;

        static bool Draussen()
        {
            try { return WandDaempfung.HoererDraussen(); } catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------- Clip-Pegel
        static float[] puffer = new float[0];

        static Dictionary<string, List<KalibrierKern.ClipEintrag>> tabelle;
        static int dTabelle, dGetData, dNichtGeladen;

        /// <summary>Clip-Pegel: zuerst die Offline-Tabelle (ClipPegelDaten, alle Clips des Spiels, auch gestreamte), sonst
        /// GetData. Ein noch nicht geladener Clip (Schritte: preloadAudioData aus, laden beim ersten Play im Hintergrund) wird
        /// NICHT als "ohne Pegel" gemerkt - bis 0.8.8-dev blieb so jeder Schritt-Clip fuer den ganzen Raid ohne Pegel.</summary>
        static ClipWert Clip(AudioClip c)
        {
            if (c == null) return null;
            int id = c.GetInstanceID();
            if (clips.TryGetValue(id, out var w)) return w;
            w = new ClipWert();
            bool merken = true;
            try
            {
                if (tabelle == null) tabelle = KalibrierKern.TabelleLesen(ClipPegelDaten.Tabelle);
                if (KalibrierKern.TabelleSuchen(tabelle, c.name, c.samples, out var e))
                { w.Ok = true; w.Laeq = e.Laeq; w.Lafmax = e.Lafmax; dTabelle++; }
                else if (c.loadType == AudioClipLoadType.Streaming) { dGestreamt++; }
                else if (c.loadState != AudioDataLoadState.Loaded) { merken = false; dNichtGeladen++; }
                else if (c.channels > 0 && c.frequency > 1000)
                {
                    int frames = Math.Min(c.samples, 30 * c.frequency);
                    int n = frames * c.channels;
                    if (puffer.Length < n) puffer = new float[n];
                    if (c.GetData(puffer, 0))
                        w.Ok = KalibrierKern.Pegel(puffer, c.channels, frames, c.frequency, out w.Laeq, out w.Lafmax);
                    if (w.Ok) dGetData++;
                }
            }
            catch (Exception) { w.Ok = false; }
            if (!merken) return w;
            if (!w.Ok) dOhneClip++;
            if (clips.Count > 4096) clips.Clear();
            clips[id] = w;
            return w;
        }

        // 10.11. (akustik-wand, B1): Pegel des gespielten Clips fuer die Hoerbarkeit (Hoerbarkeit.ClipKorrektur). Der Name kommt aus
        // BetterSource._lastPlayingClipName: BetterSource.Play setzt ihn vor dem Abspielen, Update leert ihn, sobald die Quelle nicht
        // mehr spielt (Dekompilat BetterSource.Play / UpdateSource). "clip=-" im Klang-Log heisst also: diese Quelle des Behaelters
        // spielt gerade nicht (ihre Lautstaerke ist dann ohne Wirkung) oder spielt eine geplante Schleife (PlayScheduled, ohne Namen).
        // Die Laenge des Clips ist bei PlayOneShot nicht abgelegt -> Suche nur bei eindeutigem Namen (TabelleSuchen samples = 0).
        static double bezugRennen = double.NaN;
        static readonly Dictionary<string, double> korrName = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>Pegelkorrektur (dB) des Clips 'name' der Gruppe 'typ' gegenueber ihrem Bezugsklang; 0 = unbekannt/kein Anker.</summary>
        internal static double ClipKorrektur(int typ, string name)
        {
            if (typ != 3 || string.IsNullOrEmpty(name)) return 0;
            if (korrName.TryGetValue(name, out double k)) return k;
            k = 0;
            try
            {
                if (tabelle == null) tabelle = KalibrierKern.TabelleLesen(ClipPegelDaten.Tabelle);
                if (double.IsNaN(bezugRennen)) bezugRennen = KalibrierKern.BezugRennen(tabelle);
                if (KalibrierKern.TabelleSuchen(tabelle, name, 0, out var e)) k = Hoerbarkeit.ClipKorrektur(typ, e.Lafmax, bezugRennen);
            }
            catch (Exception) { k = 0; }
            if (korrName.Count > 4096) korrName.Clear();
            korrName[name] = k;
            return k;
        }

        /// <summary>0.8.8: wirksame Kurve im Abstand d - die Entfernung daempft nur den 3D-Anteil (spatialBlend; Gutachten Punkt 3).</summary>
        static double KurveWirksam(AudioSource s, float d) => KalibrierKern.KurveWirksam(Kurve(s, d), s.spatialBlend);

        /// <summary>Kurvenwert der Quelle im Abstand d (wie Unity: eigene Kurve ueber d / maxDistance, sonst log./linear).</summary>
        static float Kurve(AudioSource s, float d)
        {
            switch (s.rolloffMode)
            {
                case AudioRolloffMode.Custom:
                    var k = s.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
                    return k != null ? Mathf.Clamp01(k.Evaluate(d / Mathf.Max(s.maxDistance, 0.01f))) : 1f;
                case AudioRolloffMode.Linear:
                    return Mathf.Clamp01(1f - (d - s.minDistance) / Mathf.Max(s.maxDistance - s.minDistance, 0.01f));
                default:
                    return d <= s.minDistance ? 1f : s.minDistance / d;
            }
        }

        // ---------------------------------------------------------------- Bezugsschritte
        [HarmonyPatch(typeof(Player), nameof(Player.PlayStepSound))]
        static class SchrittPatch
        {
            static void Prefix(Player __instance) { imSchritt = __instance; }
            static Exception Finalizer(Exception __exception) { imSchritt = null; return __exception; }
        }

        [HarmonyPatch(typeof(BetterSource), nameof(BetterSource.Play), new[] { typeof(AudioClip), typeof(AudioClip), typeof(float), typeof(float), typeof(bool), typeof(bool) })]
        static class PlayPatch
        {
            static void Prefix(BetterSource __instance, AudioClip clip1, AudioClip clip2, float balance, float volume)
            {
                var p = imSchritt;
                if (p == null || An == null || !An.Value) return;
                try
                {
                    if (!ReferenceEquals(schrittQuelle(p), __instance)) return;
                    var s = __instance.source1;
                    if (s == null) return;
                    // Energie beider Clips (PlayOneShot je mit volume x balance bzw. x (1 - balance))
                    double e = 0; bool ok = false;
                    var w1 = Clip(clip1); var w2 = Clip(clip2);
                    double v = Math.Max(1e-6, (double)__instance.BaseVolume * volume);
                    if (clip1 != null && w1 != null && w1.Ok) { double g = clip2 != null ? balance : 1.0; e += Math.Pow(10, w1.Lafmax / 10) * g * g; ok = true; }
                    if (clip2 != null && w2 != null && w2.Ok) { double g = clip1 != null ? 1 - balance : 1.0; e += Math.Pow(10, w2.Lafmax / 10) * g * g; ok = true; }
                    if (!ok || e <= 0) return;
                    // 0.8.8 Gutachten: nur Asphalt/Beton, nach Gangart (Clipname walk_/run_/sprint_)
                    int gang = KalibrierKern.Gangart(clip1 != null ? clip1.name : null);
                    if (gang < 0) gang = KalibrierKern.Gangart(clip2 != null ? clip2.name : null);
                    if (gang < 0) { dAndereSchritte++; return; }
                    string gruppe = GruppenName(s.outputAudioMixerGroup);
                    double grDb = KalibrierKern.GruppeDb(gruppe, Draussen());
                    // fremde Schritte in 1 m; eigene am Ohr (1,7 m ueber dem Fuss, Gutachten Punkt 3) und auf 1 m zurueckgerechnet
                    double db = p.IsYourPlayer
                        ? KalibrierKern.EigenerSchritt(10 * Math.Log10(e), v, KurveWirksam(s, (float)KalibrierKern.OhrHoehe), grDb)
                        : KalibrierKern.Gespielt(10 * Math.Log10(e), v, KurveWirksam(s, 1f), grDb);
                    if (kWerte.Count >= 400) kWerte.RemoveAt(0);
                    kWerte.Add(KalibrierKern.SchrittK(db, gang));
                    if (p.IsYourPlayer) jeGangEigen[gang]++; else jeGang[gang]++;
                    kAlt = true; dSchritte++;
                }
                catch (Exception ex) { Statistik.Fehler(ex); }
            }
        }

        // ---------------------------------------------------------------- Korrektur je Quellart
        /// <summary>Korrektur (dB) fuer die Quelle s der Art a am Bezugsabstand bezug (m, NaN = Mindestabstand der Quellart);
        /// text = Log-Teil (nur wenn log). 0 = aus / unbekannt / unter der Schwelle.</summary>
        internal static double Db(PegelArt a, AudioSource s, double bezug, bool log, out string text)
        {
            text = null;
            if (An == null || !An.Value || s == null || !QuellPegelKern.Gilt(a)) return 0;
            int ai = (int)a;
            if (ai >= arten.Length) return 0;
            var art = arten[ai] ?? (arten[ai] = new Art());
            var inv = CultureInfo.InvariantCulture;
            bool einzel = KalibrierKern.Einzelklang(a);
            var mass = QuellPegelKern.Mass(a);
            if (double.IsPositiveInfinity(bezug))
            {
                // 0.8.8 Abstandskurve: Kurve folgt schon der echten Pegel-Linie (laute Quelle, das Spiel komprimiert sie in der Naehe)
                if (log) text = " | calibration: none (curve follows the real level line of a " + QuellPegelKern.Name(a) + ")";
                return 0;
            }
            double r = !double.IsNaN(bezug) && bezug > 0 ? Math.Max(bezug, UmgebungKern.RefMin) : Math.Max(mass.Nah, UmgebungKern.RefMin);
            // Ist dieser Quelle am Bezugsabstand (je Clip, Lautstaerke und Abstand einmal)
            var clip = s.clip;
            var w = Clip(clip);
            string gruppe = GruppenName(s.outputAudioMixerGroup);
            if (w != null && w.Ok)
            {
                long key = ((long)clip.GetInstanceID() << 24) ^ ((long)Mathf.RoundToInt(s.volume * 1000f) << 10) ^ Mathf.RoundToInt((float)r * 10f);
                if (!art.Ist.ContainsKey(key) && art.Ist.Count < 256)
                {
                    double clipDb = einzel ? w.Lafmax : w.Laeq;
                    double kurve = KurveWirksam(s, (float)r);
                    double grDb = KalibrierKern.GruppeDb(gruppe, Draussen());
                    double ist = KalibrierKern.Gespielt(clipDb, s.volume, kurve, grDb);
                    double sollR = QuellPegelKern.DbA(a) + QuellPegelKern.GesetzDb(mass, r);
                    art.Ist[key] = ist; art.Werte.Add(sollR - ist);
                    art.Median = KalibrierKern.Median(art.Werte);
                    if (art.Beispiel == null)
                        art.Beispiel = "clip " + clip.name + " " + clipDb.ToString("F1", inv) + " dBFS(A) " + (einzel ? "LAFmax" : "LAeq") + ", vol " + s.volume.ToString("F3", inv) +
                                       ", group " + (gruppe ?? "-") + " " + grDb.ToString("F1", inv) + " dB, reference distance " + r.ToString("F1", inv) + " m, curve there " +
                                       kurve.ToString("F2", inv) + " (3D share " + s.spatialBlend.ToString("F2", inv) + "), played " + ist.ToString("F1", inv) +
                                       " dBFS(A), target " + sollR.ToString("F1", inv) + " dB(A) at " + r.ToString("F1", inv) + " m";
                }
            }
            if (double.IsNaN(art.Median)) { if (log) text = " | calibration: clip level unknown (" + (clip == null ? "no clip" : clip.loadType == AudioClipLoadType.Streaming ? "streamed clip, not in the offline table" : clip.loadState != AudioDataLoadState.Loaded ? "clip not loaded yet" : "no data") + ")"; return 0; }
            double k = K();
            double fremd = Fremd(s);   // galpegel (10.11.)
            double roh = art.Median + k + fremd;
            art.Korrektur = KalibrierKern.Begrenzen(roh);
            if (log || double.IsNaN(art.Geloggt) || Math.Abs(art.Geloggt - art.Korrektur) > 0.5)
            {
                var sb = new StringBuilder(260);
                sb.Append("calibration ").Append(art.Korrektur >= 0 ? "+" : "").Append(art.Korrektur.ToString("F1", inv)).Append(" dB (")
                  .Append(QuellPegelKern.Name(a)).Append(": game plays ").Append((-art.Median).ToString("+0.0;-0.0", inv)).Append(" dB vs. target at the reference distance, from ")
                  .Append(art.Werte.Count).Append(art.Werte.Count == 1 ? " source" : " sources").Append(", target ").Append(QuellPegelKern.DbA(a).ToString("0", inv))
                  .Append(" dB(A)@1 m; raw ").Append(roh.ToString("+0.0;-0.0", inv)).Append(" dB");
                if (Math.Abs(roh) < KalibrierKern.Schwelle) sb.Append(" below 3 dB - not applied");
                else if (roh > KalibrierKern.MaxAnheben || roh < -KalibrierKern.MaxAbsenken) sb.Append(" limited to -12...+3");
                sb.Append("; reference K ").Append(k.ToString("F1", inv)).Append(" dBFS(A) per dB(A) (").Append(kQuelle)
                  .Append(kQuelle == "raid" ? ", " + kWerte.Count + " footsteps" : kQuelle == "last raid" ? ", " + nGespeichert + " footsteps" : "")
                  .Append(kQuelle != "raid" ? "; this raid " + kWerte.Count + "/" + KalibrierKern.BezugMin : "").Append("))");
                if (fremd != 0) sb.Append("; external gain reference - this group ").Append(fremd.ToString("+0.0;-0.0", inv)).Append(" dB included");   // galpegel
                if (log) text = " | " + sb;
                if (double.IsNaN(art.Geloggt) || Math.Abs(art.Geloggt - art.Korrektur) > 0.5)
                {
                    art.Geloggt = art.Korrektur;
                    Plugin.Log.LogInfo("[Calibration] " + sb + (art.Beispiel != null ? " - first source: " + art.Beispiel : ""));
                }
            }
            return art.Korrektur;
        }

        internal static void Loggen()
        {
            if (dSchritte == 0 && kWerte.Count == 0) return;
            var inv = CultureInfo.InvariantCulture;
            double k = K();
            var sb = new StringBuilder(260);
            sb.Append("[Diagnostics] Calibration: ").Append(dSchritte).Append(" footsteps measured on asphalt/concrete (other players walk/run/sprint ")
              .Append(jeGang[0]).Append('/').Append(jeGang[1]).Append('/').Append(jeGang[2]).Append(", own ").Append(jeGangEigen[0]).Append('/').Append(jeGangEigen[1]).Append('/').Append(jeGangEigen[2])
              .Append(", other ground/moves skipped ").Append(dAndereSchritte).Append("), reference K ").Append(k.ToString("F1", inv)).Append(" (").Append(kQuelle).Append(", this raid ")
              .Append(kWerte.Count).Append('/').Append(KalibrierKern.BezugMin).Append(kWerte.Count > 0 ? ", median " + KalibrierKern.Median(kWerte).ToString("F1", inv) : "")
              .Append("), clips without data ").Append(dOhneClip)
              .Append(" (streamed ").Append(dGestreamt).Append("), clip levels from the offline table ").Append(dTabelle).Append(", via GetData ").Append(dGetData)
              .Append(", not loaded yet (retried) ").Append(dNichtGeladen).Append(';');
            for (int i = 0; i < arten.Length; i++)
            {
                var a = arten[i];
                if (a == null || double.IsNaN(a.Median)) continue;
                sb.Append(' ').Append(QuellPegelKern.Name((PegelArt)i)).Append(' ').Append(a.Korrektur >= 0 ? "+" : "").Append(a.Korrektur.ToString("F1", inv)).Append(" dB,");
            }
            if (LampeAn != null && LampeAn.Value) sb.Append(" lamps calibrated ").Append(lampen.Count).Append(" (raising limited in ").Append(dLampenBegrenzt).Append(" lamp frames),");   // lampenpegel
            Plugin.Log.LogInfo(sb.ToString().TrimEnd(','));
            dSchritte = 0; dLampenBegrenzt = 0;
        }
    }
}
