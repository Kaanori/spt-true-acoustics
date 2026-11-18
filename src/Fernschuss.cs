using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using EFT;
using HarmonyLib;
using UnityEngine;
using SEB = SoundEngineBackport.LiveEngine;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // 13. Distance (SEB) - 0.8.4: ferne Schuesse besser hoerbar (bewusst weniger realistisch), nahe Schuesse unveraendert.
    // Belegte Stellen in SEB 1.0.3 / EFT (ilspycmd):
    //  - LiveWeaponAudioSettings.Get: MaxDistance draussen 600 m, drinnen 200, gedaempft 300/120 (Konstanten)
    //    -> wird im Prefix von GunshotEmitterBase.TryStartShot im ShotPlaybackContext ersetzt (nur draussen, ungedaempft, fremde Schuesse).
    //  - GunshotEmitterBase.TryStartShot: Bank.PickClipsByDistance(...) waehlt nah/mittel/fern nach Bank.BlendOptions.Values,
    //    dann SetRolloff(MaxDistance). Die Kurve (CustomRolloff) bekommt jede Quelle beim Anlegen aus Preset.SoundRolloff, das SEB in
    //    LiveGroupPresets.Apply (Postfix BetterAudio.LoadGroupPreset) ersetzt. Eine Kurve je Gruppe gilt fuer ALLE MaxDistance-Werte
    //    (x = d / MaxDistance) - deshalb setzt der Postfix von TryStartShot die neue Kurve je Schuss auf genau diese Quelle
    //    (draussen/ungedaempft), alle anderen Schuesse bekommen wieder die Preset-Kurve; Prefix BetterSource.Release stellt sie zurueck.
    //  - SoundBank.PickClipsByDistance: Stufe 3 = Mitte*p + Fern*(1-p), Stufe 4 = nur Fern. Ist die Fernaufnahme leer (gedaempfte
    //    Banks), blendet das Spiel ab der 3. Grenze ins Leere und ist ab der 4. stumm ("[shot-silent] no clip").
    //    -> Postfix (nur waehrend TryStartShot): mittlere Aufnahme voll bzw. neu waehlen.
    //  - Explosionen: SEB ersetzt die "Grenades"-Kurve durch eine steilere (-15 dB bei 800 m gegenueber SPT). Erster Postfix
    //    (vor SEB) merkt sich die SPT-Kurve, letzter Postfix (nach SEB) setzt sie wieder ein -> ab dem naechsten Raid.
    // Fern-Ausgleich: die Fernaufnahmen sind 3,7-6,5 dB weniger dicht (RMS 50 ms, AK-74M/AKM/TX-15/STM-9). Das sitzt in der Aufnahme,
    // also wird es je Bank an deren eigener Blend-Stufe ausgeglichen (FernschussKern.Ausgleich) - nicht pauschal in Metern.
    // Die Quellenlautstaerke (source.volume) kann es nicht tragen: SEB setzt ~0,75 und Unity begrenzt auf 1 (+2,5 dB).
    // ------------------------------------------------------------------------------------------
    internal static class Fernschuss
    {
        internal const string Sek = "13. Distance (SEB)";
        internal static ConfigEntry<bool> Aktiv, MitteBehalten, ExplosionOriginal;
        internal static ConfigEntry<float> FernDb, MaxDraussen;
        // nahclip (17.11.): Nah-Aufnahme auf jede Entfernung, Entfernung nur ueber Physik (NahclipKern). Unabhaengig von "Enabled".
        internal static ConfigEntry<bool> NahUeberall, NahBreite;
        internal static ConfigEntry<bool> NahKenn;   // nahkennlinie (18.11.): Entfernung der Nah-Aufnahme durch die Kennlinie (NahclipKern.AbstandDb)
        internal static ConfigEntry<float> NahBreiteM;

        internal static bool Bereit, Gestoert;

        // Zustand des laufenden TryStartShot (Hauptfaden, synchron)
        internal static SoundBank SchussBank;
        internal static bool SchussMitte, SchussNah;

        // Diagnose (30 s)
        internal static int Verstaerkt, MitteGenutzt, Zurueck, NahGenutzt;
        internal static int NahKennGenutzt;   // nahkennlinie

        /// <summary>nahkennlinie: Entfernung der Nah-Aufnahme durch die Kennlinie? Nur mit Physik-Modus (dort gilt die Kennlinie auch fuer Waende).</summary>
        internal static bool KennAn => NahKenn != null && NahKenn.Value && Physik.An && Physik.KennKnie != null && Physik.KennSteigung != null && Physik.Lautheit != null;
        internal static readonly HashSet<string> BanksVerstaerkt = new HashSet<string>(), BanksMitte = new HashSet<string>(), BanksNah = new HashSet<string>();
        internal static string ExplosionMeldung;

        // Klang-Log: Pegelaenderung je Quelle
        sealed class Merk { public float Db; public bool Mitte; public float Zeit; public float NahR = float.NaN, NahK = float.NaN; }   // nahkennlinie: Abfall re Anker
        static readonly Dictionary<int, Merk> JeQuelle = new Dictionary<int, Merk>(64);

        internal static void Binden(ConfigFile c)
        {
            Aktiv = c.Bind(Sek, "Enabled", true,
                "Distant gunshots carry further (deliberately less realistic): own volume curve beyond 25 m, longer audible range outdoors, " +
                "suppressed shots no longer fade out at 55-100 m, flatter explosion curve. Nearby shots (up to 25 m) stay exactly as in Sound Engine Backport. " +
                "Needs Sound Engine Backport 1.0.x or 1.1.x. Gunshots: applies to the next shot; explosions: from the next raid.");
            FernDb = c.Bind(Sek, "Far clip compensation (dB)", 4f,
                new ConfigDescription("The far recordings of a weapon are 3.7-6.5 dB less dense than the mid ones (measured: AK-74M, AKM, TX-15, STM-9), " +
                    "so shots got a dip where the game blends to them (usually 55-100 m). This raises the far recording by this much, weighted with its share in the blend. 0 = off.",
                    new AcceptableValueRange<float>(0f, 8f)));
            MaxDraussen = c.Bind(Sek, "Max. distance outdoors (m)", 900f,
                new ConfigDescription("Up to this distance unsuppressed shots are audible outdoors (Sound Engine Backport: 600 m). Indoors and suppressed stay as in SEB.",
                    new AcceptableValueRange<float>(300f, 1500f)));
            MitteBehalten = c.Bind(Sek, "Suppressed: keep mid clip", true,
                "Suppressed weapons have no far recording, so the game blended them out between about 55 and 100 m and was silent beyond. " +
                "On = the mid recording keeps playing up to the max. distance (SEB: 300 m outdoors, 120 m indoors). Applies to every SEB gunshot bank without a far recording.");
            ExplosionOriginal = c.Bind(Sek, "Explosions: SPT curve", true,
                "Use the flatter SPT volume curve for grenades and explosions instead of Sound Engine Backport's steeper one (SEB: about 15 dB quieter at 800 m). From the next raid.");
            NahUeberall = c.Bind(Sek, "Near recording at every distance (physics does the rest)", true,
                "Other people's shots always play the close recording of the weapon (indoor/outdoor and suppressed variant as Sound Engine Backport chooses), " +
                "never the distant or far recording. Distance then only works through physics: up to the bank's first blend limit (5 m) the volume is SEB's, " +
                "beyond that it falls with 1/r (-6 dB per doubling of distance) plus air absorption (ISO 9613-1 over the full distance, also in the highs), " +
                "walls and paths as before. Far shots get clearly quieter than with SEB's curve (SEB: -9.5 dB at 100 m re 1 m, 1/r: -40 dB). " +
                "Replaces the section 13 distance curve and far clip compensation for these shots (max. distance outdoors still applies when section 13 is enabled). " +
                "Also covers SEB legacy (V1) weapons when \"7. Weapon Directivity -> Cover SEB legacy (V1) weapons\" is on. Own shots unchanged. Applies to the next shot.");
            NahBreite = c.Bind(Sek, "Near recording: width shrinks with distance", true,
                "Only with \"Near recording at every distance\": the close recordings are wide stereo; a real source of the width below looks narrower the further away it is " +
                "(angle 2 atan(width / 2 distance): about 2 degrees at 55 m). On = the stereo spread follows that angle; off = Sound Engine Backport's spread curve (18 degrees from 60 to 300 m, wider again towards the max. distance).");
            NahBreiteM = c.Bind(Sek, "Near recording: source width (m)", 2f,
                new ConfigDescription("Assumed width of a firing weapon as a sound source (muzzle, shooter, the ground right next to them). Not measurable in the game; 2 m is an estimate. " +
                    "Larger = wider at the same distance.", new AcceptableValueRange<float>(0.2f, 30f)));
            NahKenn = c.Bind(Sek, "Near recording: distance through the loudness curve", true,
                "Only with \"Near recording at every distance\" and Physics Mode on: beyond the first blend limit (5 m) the drop over distance goes through the same loudness curve " +
                "that walls already use (\"0. Physics Mode -> Compression knee\" / \"Compression above knee\"), starting from the real level of a shot " +
                "(140 dB(A) at 1 m, suppressed 120 dB(A)). Distance and walls then form ONE curve over the total real level at your ear. " +
                "Unsuppressed rifle re 5 m: 55 m -4 dB, 400 m -8 dB (1/r: -21 / -38 dB); suppressed shots fall almost like 1/r beyond ~60 m (real level below the knee). " +
                "Air absorption (treble, plus ~1-2 dB at 1 kHz over 400 m) stays physical. Off = plain 1/r. Applies to the next shot.");
        }

        /// <summary>nahclip: Gilt "Near recording at every distance" fuer diese Bank/Umgebung (fremder Schuss, Bank mit Grenzen und Nah-Aufnahme)?</summary>
        internal static bool NahGilt(SoundBank bank, int environment, bool eigeneSicht)
        {
            if (Gestoert || !Bereit || bank == null || NahUeberall == null || !NahUeberall.Value || eigeneSicht) return false;
            var opt = bank.BlendOptions;
            var env = bank.Environments;
            if (opt == null || env == null) return false;
            int ei = bank.HasEnvironment ? environment : 0;
            bool nahLeer = ei < 0 || ei >= env.Length || env[ei] == null || env[ei].Length < 1 || env[ei][0] == null || env[ei][0].Length == 0;
            return NahclipKern.Gilt(true, false, opt.Values, nahLeer);
        }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue("red.soundenginebackport", out var pi))
                {
                    Plugin.Log.LogInfo("Distance (SEB): Sound Engine Backport not found - section 13 off.");
                    return;
                }
                if (!SebSchutz.VersionPasst(pi.Metadata.Version))
                {
                    Plugin.Log.LogWarning($"Distance (SEB): Sound Engine Backport {pi.Metadata.Version} found, built for 1.0.x/1.1.x -> section 13 off.");
                    return;
                }
                FernschussSeb.Anwenden(h);
            }
            catch (Exception ex)
            {
                Statistik.Fehler(ex);
                Plugin.Log.LogError($"Distance (SEB): applying failed: {ex.Message}");
            }
        }

        internal static void Abschalten(string teil, Exception ex)
        {
            if (Gestoert) return;
            Gestoert = true;
            Plugin.Log.LogError($"Distance (SEB): {teil} failed ({ex.GetType().Name}: {ex.Message}) - Sound Engine Backport probably changed. " +
                                "Section 13 is off for this session; shots play as SEB intends.");
        }

        internal static void Fehler(string teil, Exception ex)
        {
            if (SebSchutz.Strukturfehler(ex)) Abschalten(teil, ex); else Statistik.Fehler(ex);
        }

        // ---- SoundBank.PickClipsByDistance (Postfix): nur waehrend eines SEB-Schusses, nur fuer dessen Bank ----
        static void ClipNach(SoundBank __instance, ref AudioClip clip1, ref AudioClip clip2, ref float proportions, int environment, float distance)
        {
            // v1waffen: auch die Banks eines SEB-V1-Schusses (FireBulletV1/StopLoopV1 waehlen ihre Clips selbst, ohne TryStartShot)
            bool live = SchussBank != null && ReferenceEquals(__instance, SchussBank);
            // nahclip: Nah-Aufnahme statt der Wahl nach Entfernung (Live: SchussNah aus dem Prefix; V1: Bank des laufenden Fensters)
            bool nah = live ? SchussNah : SchussV1.NahFenster(__instance, environment);
            if (!live && !nah && !SchussV1.MitteFenster(__instance)) return;
            try
            {
                if (nah)
                {
                    var e = __instance.Environments;
                    int i0 = __instance.HasEnvironment ? environment : 0;
                    var naheV = e != null && i0 >= 0 && i0 < e.Length && e[i0] != null && e[i0].Length > 0 ? e[i0][0] : null;
                    if (naheV != null && naheV.Length > 0)
                    {
                        AudioClip c = null;
                        __instance.SelectRandomClip(naheV, ref c);
                        if (c != null) { clip1 = c; clip2 = null; proportions = 1f; return; }
                    }
                }
                // bis hier nur die Nah-Wahl; "keep mid clip" wie bisher (Live: Bank nur gemerkt, wenn Abschnitt 13 und der Schalter an sind)
                if (live ? !(MitteBehalten.Value && Aktiv.Value) : !SchussV1.MitteFenster(__instance)) return;
                var opt = __instance.BlendOptions;
                var env = __instance.Environments;
                if (opt == null || env == null) return;
                int ei = __instance.HasEnvironment ? environment : 0;
                if (ei < 0 || ei >= env.Length || env[ei] == null || env[ei].Length < 3) return;
                var mitte = env[ei][1];
                var fern = env[ei][2];
                bool fernLeer = fern == null || fern.Length == 0, mitteLeer = mitte == null || mitte.Length == 0;
                switch (FernschussKern.ClipWahl(opt.Values, distance, fernLeer, mitteLeer))
                {
                    case ClipEntscheid.MitteVoll:
                        clip2 = null; proportions = 1f; SchussMitte = true; break;
                    case ClipEntscheid.MitteNehmen:
                        clip1 = null; __instance.SelectRandomClip(mitte, ref clip1);
                        clip2 = null; proportions = 1f; SchussMitte = clip1 != null; break;
                }
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        // ---- BetterSource.Release (Prefix): eigene Kurve zuruecknehmen, bevor die Quelle in den Pool des Spiels geht ----
        static void FreigabeVor(BetterSource __instance)
        {
            if (!FernschussSeb.HatMarke(__instance)) return;
            try { FernschussSeb.Zuruecksetzen(__instance); Zurueck++; }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        // ---- BetterAudio.LoadGroupPreset: SPT-Kurve fuer Explosionen ----
        static AnimationCurve grenadesSpt;

        static void PresetVorSeb(string groupName, AudioGroupPreset __result)
        {
            if (grenadesSpt != null || groupName != "Grenades" || __result == null || __result.SoundRolloff == null || Gestoert) return;
            try
            {
                if (FernschussSeb.IstSebKurve("Grenades", __result.SoundRolloff)) return;   // schon von SEB ersetzt (zweites Laden)
                grenadesSpt = new AnimationCurve(__result.SoundRolloff.keys);
                Plugin.Log.LogInfo($"Distance (SEB): SPT explosion curve saved ({grenadesSpt.length} keys).");
            }
            catch (Exception ex) { Fehler("explosion curve (save)", ex); }
        }

        static void PresetNachSeb(string groupName, AudioGroupPreset __result)
        {
            if (groupName != "Grenades" || __result == null || Gestoert) return;
            if (!Aktiv.Value || !ExplosionOriginal.Value) { ExplosionMeldung = "SEB curve"; return; }
            if (grenadesSpt == null) { ExplosionMeldung = "SPT curve not available (SEB curve kept)"; return; }
            __result.SoundRolloff = new AnimationCurve(grenadesSpt.keys);
            ExplosionMeldung = "SPT curve";
            Plugin.Log.LogInfo("Distance (SEB): group \"Grenades\" uses the SPT volume curve instead of SEB's (this raid).");
        }

        // ---- Klang-Log ----
        /// <summary>nahR/nahK (nahkennlinie): Pegelabfall der Nah-Aufnahme gegenueber dem Anker nach 1/r und nach der gespielten Regel
        /// (Kennlinie oder 1/r), ohne Luft, dB &lt;= 0; NaN = keine Nah-Aufnahme.</summary>
        internal static void Merken(BetterSource q, float db, bool mitte, float nahR = float.NaN, float nahK = float.NaN)
        {
            if (!KlangLog.An || q == null) return;
            JeQuelle[q.GetInstanceID()] = new Merk { Db = db, Mitte = mitte, Zeit = Time.time, NahR = nahR, NahK = nahK };
        }

        /// <summary>Fuer die Zeile im Klang-Log: Pegelaenderung (dB, NaN = keine) und ob die mittlere Aufnahme weiterlief.</summary>
        internal static float LogDb(BetterSource q, out bool mitte) => LogDb(q, out mitte, out _, out _);

        /// <summary>Wie LogDb, dazu (nahkennlinie) Abfall re Anker nach 1/r und nach der gespielten Regel (NaN = keine Nah-Aufnahme).</summary>
        internal static float LogDb(BetterSource q, out bool mitte, out float nahR, out float nahK)
        {
            mitte = false; nahR = nahK = float.NaN;
            if (q == null || !JeQuelle.TryGetValue(q.GetInstanceID(), out var m)) return float.NaN;
            if (Time.time - m.Zeit > 10f) { JeQuelle.Remove(q.GetInstanceID()); return float.NaN; }
            mitte = m.Mitte; nahR = m.NahR; nahK = m.NahK;
            return m.Db;
        }

        internal static void SzeneGewechselt() => JeQuelle.Clear();

        // ---- Diagnose: eine Zeile alle 30 s, wenn etwas passiert ist ----
        internal static void Loggen()
        {
            if (!Bereit) return;
            if (Verstaerkt + MitteGenutzt + NahGenutzt == 0 && ExplosionMeldung == null) return;
            var t = new List<string>();
            if (NahGenutzt > 0) t.Add($"{NahGenutzt} shots with the near recording ({(KennAn ? $"distance through the loudness curve (knee {Physik.KennKnie.Value:F0} dB(A), slope {Physik.KennSteigung.Value:F2}; {NahKennGenutzt} shots)" : "1/r")} from the first blend limit, air over the full distance, " +
                                      $"width {(NahBreite.Value ? $"{NahBreiteM.Value:F1} m source" : "SEB curve")}; banks {Liste(BanksNah)})");
            if (Verstaerkt > 0) t.Add($"{Verstaerkt} shots with distance curve (max {MaxDraussen.Value:F0} m, far clip +{FernDb.Value:F1} dB; banks {Liste(BanksVerstaerkt)})");
            if (MitteGenutzt > 0) t.Add($"{MitteGenutzt} shots kept the mid clip (no far clip; banks {Liste(BanksMitte)})");
            if (ExplosionMeldung != null) t.Add($"group Grenades: {ExplosionMeldung}");
            if (Zurueck > 0) t.Add($"{Zurueck} sources reset to the SEB curve");
            Plugin.Log.LogInfo("[Diagnostics] Distance (SEB): " + string.Join(", ", t));
            Verstaerkt = MitteGenutzt = Zurueck = NahGenutzt = NahKennGenutzt = 0; ExplosionMeldung = null;
            BanksVerstaerkt.Clear(); BanksMitte.Clear(); BanksNah.Clear();
        }

        static string Liste(HashSet<string> s) => s.Count == 0 ? "-" : string.Join(", ", s);

        internal static void BankMerken(HashSet<string> s, SoundBank b)
        {
            if (b != null && s.Count < 12) s.Add(b.name);
        }

        // Harmony-Hilfen fuer FernschussSeb (Ziele werden hier gesucht, damit ein fehlendes Ziel nur eine Warnung ist)
        internal static MethodBase Ziel(Type t, string name, Type[] args = null)
        {
            if (t == null) { Plugin.Log.LogWarning($"Distance (SEB): type for {name} not found."); return null; }
            var m = args == null ? AccessTools.DeclaredMethod(t, name) : AccessTools.DeclaredMethod(t, name, args);
            if (m == null) Plugin.Log.LogWarning($"Distance (SEB): target {t.Name}.{name} not found.");
            return m;
        }

        internal static HarmonyMethod HM(Type host, string name, int prioritaet = -1, string[] vor = null, string[] nach = null)
        {
            var m = AccessTools.DeclaredMethod(host, name);
            if (m == null) throw new MissingMethodException(host.Name, name);
            var hm = new HarmonyMethod(m);
            if (prioritaet >= 0) hm.priority = prioritaet;
            if (vor != null) hm.before = vor;
            if (nach != null) hm.after = nach;
            return hm;
        }

        internal static void ClipZielPatchen(Harmony h)
        {
            var pick = Ziel(typeof(SoundBank), "PickClipsByDistance");
            var rel = Ziel(typeof(BetterSource), "Release");
            var load = Ziel(typeof(BetterAudio), "LoadGroupPreset");
            if (pick == null || rel == null || load == null) throw new MissingMethodException("Distance (SEB): game targets missing");
            h.Patch(pick, postfix: HM(typeof(Fernschuss), "ClipNach"));
            h.Patch(rel, prefix: HM(typeof(Fernschuss), "FreigabeVor"));
            h.Patch(load, postfix: HM(typeof(Fernschuss), "PresetVorSeb", Priority.First, vor: new[] { "red.soundenginebackport" }));
            h.Patch(load, postfix: HM(typeof(Fernschuss), "PresetNachSeb", Priority.Last, nach: new[] { "red.soundenginebackport" }));
        }
    }

    // ---------------------------------------------------------------------------------------
    // Alles, was SEB-Typen nennt. Zugriffe in NoInlining-Methoden, deren Aufruf im try liegt (siehe SebSchutz).
    // ---------------------------------------------------------------------------------------
    internal static class FernschussSeb
    {
        sealed class Marke { public AnimationCurve Kurve, Breite; }   // nahclip: Breite = eigene Spread-Kurve
        static readonly ConditionalWeakTable<BetterSource, Marke> Marken = new ConditionalWeakTable<BetterSource, Marke>();

        internal sealed class KurveFertig { public AnimationCurve Kurve; public KurvenPunkt[] Punkte; }
        static readonly Dictionary<string, KurveFertig> Kurven = new Dictionary<string, KurveFertig>();
        static AnimationCurve sebRef;
        static KurvenPunkt[] sebPunkte;
        static float sebMax = -1f;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Anwenden(Harmony h)
        {
            var tss = Fernschuss.Ziel(AccessTools.TypeByName("SoundEngineBackport.LiveEngine.GunshotEmitterBase"), "TryStartShot");
            if (tss == null) { Plugin.Log.LogWarning("Distance (SEB): TryStartShot not found - section 13 off."); return; }
            Fernschuss.ClipZielPatchen(h);
            h.Patch(tss, prefix: Fernschuss.HM(typeof(FernschussSeb), "SchussVor"),
                         postfix: Fernschuss.HM(typeof(FernschussSeb), "SchussNach"),
                         finalizer: Fernschuss.HM(typeof(FernschussSeb), "SchussEnde"));
            Fernschuss.Bereit = true;
            Plugin.Log.LogInfo("Distance (SEB): ready - patched GunshotEmitterBase.TryStartShot (max. distance, curve per shot), " +
                               "SoundBank.PickClipsByDistance (mid clip without far clip), BetterAudio.LoadGroupPreset (explosion curve), BetterSource.Release (reset). " +
                               $"Section 13 is {(Fernschuss.Aktiv.Value ? "on" : "off")}.");
        }

        static bool Gilt(bool indoor, bool silenced, bool fp) => !indoor && !silenced && !fp;

        // Prefix TryStartShot: MaxDistance draussen ersetzen, Bank fuer die Clip-Wahl merken
        static void SchussVor(ref SEB.ShotPlaybackContext context)
        {
            Fernschuss.SchussBank = null; Fernschuss.SchussMitte = false; Fernschuss.SchussNah = false;
            if (Fernschuss.Gestoert || !(Fernschuss.Aktiv.Value || Fernschuss.NahUeberall.Value)) return;
            try { Vor(ref context); }
            catch (Exception ex) { Fernschuss.SchussBank = null; Fernschuss.SchussNah = false; Fernschuss.Fehler("shot preparation", ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Vor(ref SEB.ShotPlaybackContext context)
        {
            // nahclip: Nah-Aufnahme fuer diesen Schuss (Clipwahl in ClipNach, Kurve/Breite in Nach)
            Fernschuss.SchussNah = Fernschuss.NahGilt(context.Bank, (int)context.Environment, context.IsFirstPerson);
            if ((Fernschuss.Aktiv.Value && Fernschuss.MitteBehalten.Value) || Fernschuss.SchussNah) Fernschuss.SchussBank = context.Bank;
            if (!Fernschuss.Aktiv.Value) return;   // Reichweite draussen nur mit Abschnitt 13
            if (!Gilt(context.Environment == EnvironmentType.Indoor, context.IsSilenced, context.IsFirstPerson)) return;
            float neu = Fernschuss.MaxDraussen.Value;
            if (Mathf.Abs(neu - context.MaxDistance) < 0.01f) return;
            context = new SEB.ShotPlaybackContext(
                context.Bank, context.DistanceToListener, neu,
                context.BaseVolume, context.Environment, context.IsFirstPerson,
                context.Delay, context.BeatDuration, context.PitchA, context.PitchB, context.IsSilenced);
        }

        // Postfix TryStartShot: Kurve fuer genau diese Quelle
        static void SchussNach(SEB.ShotPlaybackContext context, ref SuperSource source, bool __result)
        {
            bool mitte = Fernschuss.SchussMitte, nah = Fernschuss.SchussNah;
            Fernschuss.SchussBank = null; Fernschuss.SchussMitte = false; Fernschuss.SchussNah = false;
            if (Fernschuss.Gestoert || !__result || source == null) return;
            try { Nach(context, source, mitte, nah); }
            catch (Exception ex) { Fernschuss.Fehler("shot curve", ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Nach(SEB.ShotPlaybackContext context, SuperSource source, bool mitte, bool nah)
        {
            if (mitte)
            {
                Fernschuss.MitteGenutzt++;
                Fernschuss.BankMerken(Fernschuss.BanksMitte, context.Bank);
            }
            if (nah)
            {
                // nahclip: Bezug ist SEBs Gunshots-Kurve bei SEBs eigener Reichweite fuer diesen Schuss (drinnen/draussen, gedaempft);
                // context.MaxDistance traegt schon die Reichweite von Abschnitt 13 (Prefix), wenn der an ist
                SEB.LiveWeaponAudioSettings.Get(null, context.Environment, context.IsSilenced, out float sebMd, out _);
                if (NahAuflegen(source, context.Bank, context.MaxDistance, sebMd, context.DistanceToListener, context.IsSilenced)) return;
            }
            bool an = Fernschuss.Aktiv.Value && Gilt(context.Environment == EnvironmentType.Indoor, context.IsSilenced, context.IsFirstPerson);
            KurveFertig k = an ? Holen(source, context) : null;
            if (k == null)
            {
                if (HatMarke(source)) Zuruecksetzen(source);
                Fernschuss.Merken(source, float.NaN, mitte);
                return;
            }
            var m = Marken.GetValue(source, _ => new Marke());
            BreiteZurueck(source, m);
            if (!ReferenceEquals(m.Kurve, k.Kurve))
            {
                source.source1.SetCustomCurve(AudioSourceCurveType.CustomRolloff, k.Kurve);
                if (source.source2 != null) source.source2.SetCustomCurve(AudioSourceCurveType.CustomRolloff, k.Kurve);
                m.Kurve = k.Kurve;
            }
            Fernschuss.Verstaerkt++;
            Fernschuss.BankMerken(Fernschuss.BanksVerstaerkt, context.Bank);
            if (KlangLog.An)
                Fernschuss.Merken(source, (float)FernschussKern.Unterschied(k.Punkte, context.MaxDistance, sebPunkte, sebMax, context.DistanceToListener), mitte);
        }

        static Exception SchussEnde(Exception __exception)
        {
            Fernschuss.SchussBank = null; Fernschuss.SchussMitte = false; Fernschuss.SchussNah = false;
            return __exception;
        }

        // ---- nahclip: Abstandsgesetz und Breite fuer die Nah-Aufnahme ----
        // Schluessel als Zahlen (keine Zeichenkette je Schuss): Reichweite, Bezug, Anker in cm, Luft bei 1 kHz in dB/1000 km
        // nahkennlinie: zweiter Teil des Schluessels = Kennlinie (Quellpegel, Knie, Steigung, Lautheit); Struktur mit IEquatable -> keine Allokation
        struct NahSchluessel : IEquatable<NahSchluessel>
        {
            public long A, B;
            public bool Equals(NahSchluessel o) => A == o.A && B == o.B;
            public override bool Equals(object o) => o is NahSchluessel n && Equals(n);
            public override int GetHashCode() => A.GetHashCode() * 397 ^ B.GetHashCode();
        }
        static readonly Dictionary<NahSchluessel, KurveFertig> NahKurven = new Dictionary<NahSchluessel, KurveFertig>();
        static readonly Dictionary<long, AnimationCurve> BreiteKurven = new Dictionary<long, AnimationCurve>();
        static long Schluessel(int a, int b, int c, int d) => ((long)(a & 0xFFFF) << 48) | ((long)(b & 0xFFFF) << 32) | ((long)(c & 0xFFFF) << 16) | (long)(d & 0xFFFF);
        static NahLuftTabelle nahLuft;

        /// <summary>nahclip: Luft-Tiefpass (4-poliger Fit auf ISO 9613-1 fuer das Raid-Wetter) und Luftdaempfung bei 1 kHz (dB/m).
        /// Tabelle je Wetter (Wetter.Baender wird bei jeder Wetteraenderung ersetzt); Bau ~ einige ms, selten.</summary>
        internal static NahLuftTabelle NahLuft
        {
            get
            {
                var b = Wetter.Baender;
                var t = nahLuft;
                if (t == null || !ReferenceEquals(t.Quelle, b)) { t = new NahLuftTabelle(f => b.Alpha(f), b); nahLuft = t; }
                return t;
            }
        }

        /// <summary>Gruppenkurve der Quelle (Preset.SoundRolloff, von SEB ersetzt) als Punkte - gemeinsam mit KurveFuer.</summary>
        static bool SebPunkteHolen(BetterSource source)
        {
            var preset = source.Preset;
            var basis = preset != null ? preset.SoundRolloff : null;
            if (basis == null || basis.length < 2) return false;
            if (!ReferenceEquals(basis, sebRef))
            {
                var keys = basis.keys;
                var p = new KurvenPunkt[keys.Length];
                for (int i = 0; i < keys.Length; i++) p[i] = new KurvenPunkt(keys[i].time, keys[i].value, keys[i].inTangent, keys[i].outTangent);
                sebPunkte = p; sebRef = basis; Kurven.Clear(); NahKurven.Clear();
            }
            return true;
        }

        /// <summary>nahclip: Kurve (1/r ab der ersten Blendgrenze der Bank + Luft bei 1 kHz) und Breite auf genau diese Quelle legen.
        /// maxD = gespielte Reichweite, basisMax = Reichweite, auf die sich die Gruppenkurve bezieht (Live: SEBs Wert, V1: SoundBank.Rolloff).
        /// false = nicht moeglich (dann laeuft der Schuss mit den bisherigen Regeln).</summary>
        internal static bool NahAuflegen(SuperSource source, SoundBank bank, float maxD, float basisMax, float dLog, bool gedaempft)
        {
            if (source == null || bank == null || bank.BlendOptions == null || !SebPunkteHolen(source)) return false;
            float anker = NahclipKern.Anker(bank.BlendOptions.Values);
            var luft = NahLuft;
            double aRef = SebAkustik.LuftAktiv.Value ? luft.AlphaRef : 0.0;
            // nahkennlinie: Entfernung durch die Kennlinie der Physik (gleicher Quellpegel/Knie/Steigung/Lautheit wie der Wandabzug in
            // Physik.SollDb -> Hoerbarkeit.Gestaucht), sonst 1/r. Werte je Schuss gelesen -> live ab dem naechsten Schuss.
            bool kenn = Fernschuss.KennAn;
            double l1 = kenn ? Hoerbarkeit.KennPegel(0, gedaempft) : double.NaN;
            double laut = kenn ? Physik.Lautheit.Value : 1.0, knie = kenn ? Physik.KennKnie.Value : double.NaN, steig = kenn ? Physik.KennSteigung.Value : 1.0;
            var key = new NahSchluessel
            {
                A = Schluessel(Mathf.RoundToInt(maxD), Mathf.RoundToInt(basisMax), Mathf.RoundToInt(anker * 100f), (int)Math.Round(aRef * 1e6)),
                B = kenn ? Schluessel((int)Math.Round(l1 * 10), (int)Math.Round(knie * 10), (int)Math.Round(steig * 1000), (int)Math.Round(laut * 1000)) : -1L
            };
            if (!NahKurven.TryGetValue(key, out var k))
            {
                var punkte = NahclipKern.Kurve(sebPunkte, basisMax, maxD, anker, aRef, l1, laut, knie, steig);
                k = punkte == null ? null : new KurveFertig { Kurve = Unity(punkte), Punkte = punkte };
                if (NahKurven.Count > 64) NahKurven.Clear();   // Schluessel aendern sich nur mit Reichweite/Wetter - wenige
                NahKurven[key] = k;
                if (k != null && Plugin.Diagnose.Value)
                    Plugin.Log.LogInfo($"Distance (SEB): near-recording curve built for max {maxD:F0} m (group curve at {basisMax:F0} m), " +
                        (kenn ? $"loudness curve from {anker:F1} m (source {l1:F0} dB(A) at 1 m, knee {knie:F0}, slope {steig:F2}, loudness {laut:F2}), " : $"1/r from {anker:F1} m, ") +
                        $"air {aRef * 1000:F2} dB/km at 1 kHz: 50 m {Db(punkte, 50f, maxD)}, 100 m {Db(punkte, 100f, maxD)}, 200 m {Db(punkte, 200f, maxD)}, 400 m {Db(punkte, 400f, maxD)} dB re 1 m " +
                        $"(group curve: 50 m {Db(sebPunkte, 50f, basisMax)}, 100 m {Db(sebPunkte, 100f, basisMax)}, 200 m {Db(sebPunkte, 200f, basisMax)}, 400 m {Db(sebPunkte, 400f, basisMax)}).");
            }
            if (k == null) return false;
            var m = Marken.GetValue(source, _ => new Marke());
            if (!ReferenceEquals(m.Kurve, k.Kurve))
            {
                source.source1.SetCustomCurve(AudioSourceCurveType.CustomRolloff, k.Kurve);
                if (source.source2 != null) source.source2.SetCustomCurve(AudioSourceCurveType.CustomRolloff, k.Kurve);
                m.Kurve = k.Kurve;
            }
            if (Fernschuss.NahBreite.Value)
            {
                float w = Fernschuss.NahBreiteM.Value;
                long bk = Schluessel(Mathf.RoundToInt(maxD), Mathf.RoundToInt(w * 100f), 0, 0);
                if (!BreiteKurven.TryGetValue(bk, out var bc))
                {
                    var bp = NahclipKern.BreiteKurve(maxD, w);
                    bc = bp == null ? null : Unity(bp);
                    if (BreiteKurven.Count > 64) BreiteKurven.Clear();
                    BreiteKurven[bk] = bc;
                }
                if (bc != null && !ReferenceEquals(m.Breite, bc))
                {
                    source.source1.SetCustomCurve(AudioSourceCurveType.Spread, bc);
                    if (source.source2 != null) source.source2.SetCustomCurve(AudioSourceCurveType.Spread, bc);
                    m.Breite = bc;
                }
            }
            else BreiteZurueck(source, m);
            Fernschuss.NahGenutzt++;
            if (kenn) Fernschuss.NahKennGenutzt++;
            Fernschuss.BankMerken(Fernschuss.BanksNah, bank);
            if (KlangLog.An)
            {
                // nahkennlinie: Abfall re Anker ohne Luft - nach 1/r und nach der gespielten Regel (gleiche Formel wie die Kurve)
                float nahR = -(float)NahclipKern.AbstandDb(anker, dLog);
                float nahK = kenn ? -(float)NahclipKern.AbstandDb(anker, dLog, l1, laut, knie, steig) : nahR;
                Fernschuss.Merken(source, (float)FernschussKern.Unterschied(k.Punkte, maxD, sebPunkte, basisMax, dLog), false, nahR, nahK);
            }
            return true;
        }

        static AnimationCurve Unity(KurvenPunkt[] p)
        {
            var kf = new Keyframe[p.Length];
            for (int i = 0; i < p.Length; i++) kf[i] = new Keyframe(p[i].T, p[i].V, p[i].In, p[i].Out);
            return new AnimationCurve(kf);
        }

        /// <summary>Spread der Gruppe wieder einsetzen (Preset.SpreadCurve, von SEB ersetzt), wenn diese Quelle die Breitenkurve traegt.</summary>
        static void BreiteZurueck(BetterSource q, Marke m)
        {
            if (m == null || m.Breite == null) return;
            m.Breite = null;
            var basis = q.Preset != null ? q.Preset.SpreadCurve : null;
            if (basis == null) return;
            if (q.source1 != null) q.source1.SetCustomCurve(AudioSourceCurveType.Spread, basis);
            if (q is SuperSource s && s.source2 != null) s.source2.SetCustomCurve(AudioSourceCurveType.Spread, basis);
        }

        /// <summary>Kurve fuer MaxDistance + Blend-Grenzen + Fern-Ausgleich der Bank (zwischengespeichert, wenige Varianten).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static KurveFertig Holen(BetterSource source, SEB.ShotPlaybackContext context)
        {
            if (sebMax < 0f)
            {
                SEB.LiveWeaponAudioSettings.Get(null, EnvironmentType.Outdoor, false, out float md, out _);
                sebMax = md;
            }
            return KurveFuer(source, context.Bank, context.Environment, context.MaxDistance, sebMax);
        }

        /// <summary>v1waffen: die Kurve ohne SEB-Typen - Bezug ist die Gruppenkurve der Quelle (Preset.SoundRolloff, von SEB ersetzt) bei
        /// basisMax: Live-Weg = SEBs MaxDistance draussen (600 m), V1-Weg = Reichweite der Bank (SoundBank.Rolloff, die das Spiel/SEB-V1
        /// beim Ausleihen der Warteschlange setzt). Bis 25 m bleibt die Kurve damit genau die des jeweiligen Wegs (FernschussKern.Bauen).</summary>
        internal static KurveFertig KurveFuer(BetterSource source, SoundBank bank, EnvironmentType umgebung, float maxD, float basisMax)
        {
            if (!SebPunkteHolen(source)) return null;
            float b2 = 55f, b3 = 100f;
            bool fernDa = false;
            float[] bv = bank != null && bank.BlendOptions != null ? bank.BlendOptions.Values : null;
            if (bv != null && bv.Length >= 4)
            {
                b2 = bv[2]; b3 = bv[3];
                var env = bank.Environments;
                int ei = bank.HasEnvironment ? (int)umgebung : 0;
                fernDa = env != null && ei < env.Length && env[ei] != null && env[ei].Length >= 3 && env[ei][2] != null && env[ei][2].Length > 0;
            }
            float fdb = Fernschuss.FernDb.Value;
            if (!(basisMax > 0f)) return null;
            string key = $"{maxD:F0}|{b2:F1}|{b3:F1}|{(fernDa ? fdb : 0f):F2}|{basisMax:F0}";   // v1waffen: Bezug gehoert zum Schluessel
            if (Kurven.TryGetValue(key, out var fertig)) return fertig;
            var punkte = FernschussKern.Bauen(sebPunkte, basisMax, maxD, b2, b3, fdb, fernDa);
            if (punkte == null) { Kurven[key] = null; return null; }
            var kf = new Keyframe[punkte.Length];
            for (int i = 0; i < punkte.Length; i++) kf[i] = new Keyframe(punkte[i].T, punkte[i].V, punkte[i].In, punkte[i].Out);
            fertig = new KurveFertig { Kurve = new AnimationCurve(kf), Punkte = punkte };
            Kurven[key] = fertig;
            if (Plugin.Diagnose.Value)
                Plugin.Log.LogInfo($"Distance (SEB): gunshot curve built for max {maxD:F0} m, blend {b2:F0}/{b3:F0} m, far clip {(fernDa ? $"+{fdb:F1} dB" : "missing (no compensation)")}: " +
                    $"50 m {Db(punkte, 50f, maxD)}, 100 m {Db(punkte, 100f, maxD)}, 200 m {Db(punkte, 200f, maxD)}, 400 m {Db(punkte, 400f, maxD)}, 800 m {Db(punkte, 800f, maxD)} dB re 1 m " +
                    $"(SEB, max {basisMax:F0} m: 50 m {Db(sebPunkte, 50f, basisMax)}, 100 m {Db(sebPunkte, 100f, basisMax)}, 200 m {Db(sebPunkte, 200f, basisMax)}, 400 m {Db(sebPunkte, 400f, basisMax)}).");
            return fertig;
        }

        static string Db(KurvenPunkt[] p, float d, float maxD)
        {
            if (d >= maxD) return "silent";
            double g = FernschussKern.Auswerten(p, d / maxD), g1 = FernschussKern.Auswerten(p, 1.0 / maxD);
            return g <= 1e-9 || g1 <= 1e-9 ? "silent" : FernschussKern.DbAusGain(g / g1).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static bool HatMarke(BetterSource q) => q != null && Marken.TryGetValue(q, out var m) && (m.Kurve != null || m.Breite != null);

        /// <summary>v1waffen: Kurve auf genau diese Quelle (beide AudioSources) legen und markieren - Release setzt sie zurueck (FreigabeVor).</summary>
        internal static void KurveAuflegen(SuperSource source, KurveFertig k)
        {
            var m = Marken.GetValue(source, _ => new Marke());
            BreiteZurueck(source, m);   // nahclip: ein Abschnitt-13-Sample traegt nie die Breite der Nah-Aufnahme
            if (ReferenceEquals(m.Kurve, k.Kurve)) return;
            source.source1.SetCustomCurve(AudioSourceCurveType.CustomRolloff, k.Kurve);
            if (source.source2 != null) source.source2.SetCustomCurve(AudioSourceCurveType.CustomRolloff, k.Kurve);
            m.Kurve = k.Kurve;
        }

        /// <summary>v1waffen: Pegelaenderung gegenueber der Bezugskurve (dB) bei Entfernung d - fuer das Klang-Log.</summary>
        internal static float UnterschiedDb(KurveFertig k, float maxD, float basisMax, float d) =>
            (float)FernschussKern.Unterschied(k.Punkte, maxD, sebPunkte, basisMax, d);

        internal static void Zuruecksetzen(BetterSource q)
        {
            if (!Marken.TryGetValue(q, out var m)) return;
            BreiteZurueck(q, m);   // nahclip
            if (m.Kurve == null) return;
            m.Kurve = null;
            var basis = q.Preset != null ? q.Preset.SoundRolloff : null;
            if (basis == null) return;
            if (q.source1 != null) q.source1.SetCustomCurve(AudioSourceCurveType.CustomRolloff, basis);
            if (q is SuperSource s && s.source2 != null) s.source2.SetCustomCurve(AudioSourceCurveType.CustomRolloff, basis);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool IstSebKurve(string gruppe, AnimationCurve c) =>
            SEB.LiveGroupPresetTable.Groups.TryGetValue(gruppe, out var e) && ReferenceEquals(e.Rolloff, c);
    }
}
