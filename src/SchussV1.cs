using System;
using System.Runtime.CompilerServices;
using Audio.SpatialSystem;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    //  v1waffen (12.11.2026): Schussteile der Mod auch fuer Waffen, die Sound Engine Backport ueber seine "V1 engine" spielt.
    //  F12 "7. Weapon Directivity" -> "Cover SEB legacy (V1) weapons", Standard AN (Wunsch des Besitzers).
    //
    //  Wie V1 abspielt (Dekompilat SEB 1.1.1 SoundEngineBackport/WeaponSoundPlayerPatches.cs + Spiel):
    //   - FireBulletPrefix (:160-192): IsLegacyBank(bank, player) -> FireBulletV1 statt LiveWeaponSoundEngine. Legacy = Bank mit
    //     Loop-Clipnamen oder (neu in 1.1.1) Auto-Waffe mit "SingleVariantSameLength" (je Stufe ein Clip, alle gleich lang, :73-89/108-156).
    //   - FireBulletV1 (:241-354) ist das Spielverfahren (WeaponSoundPlayer.FireBullet, WeaponSoundPlayer.cs:274-354) mit SEB-Schnitten:
    //     Warteschlange BetterAudio.BorrowWeaponAudioQueue(Gunshots) = ZWEI ReverbSuperSource aus dem Gunshots-Pool des Spiels
    //     (BetterAudio.cs:1115-1133), Pose(WeaponPosition) + SetRolloff(SoundBank.Rolloff) + GunshotMixerGroup nur beim Ausleihen,
    //     RegisterSourceForOcclusionProcess -> der Okkluder des Spiels (und damit Physik/Waende/Hall/Kennlinie der Mod) laeuft mit.
    //     Clips: SoundBank.PickClipsByDistance (Spiel), Pegel SoundBank.BaseVolume * LiveMixer.GunshotGain, Start dspTime + d/340,29.
    //     Schleife (V1State.LoopMode): EIN Enqueue mit clipTime 0 -> SuperBetterAudioQueue.Update setzt Loop = true (End == 0); die
    //     Folgeschuesse spielen nichts neu (nur Balance/UpdateMixerGroup/UpdatePitch); Schluss: StopLoopV1 / ScheduleLoopTail plant den
    //     Schweif (Tail-Bank) an einem Schnitt aus LegacyLoopCuts. Ohne Schleife: je Schuss ein Enqueue (Einzelclip).
    //   - Abspielen: SuperBetterAudioQueue.Update -> SuperAudioSample.PlayOn(SuperSource) -> ReverbSuperSource.PlayScheduled
    //     (SuperBetterAudioQueue.cs:117-149, SuperAudioSample.cs:16-25). PlayOn ruft nur diese Warteschlange; BorrowWeaponAudioQueue nur
    //     WeaponSoundPlayer und SEB-V1 -> PlayOn/Enqueue sind genau der Warteschlangen-Weg, der Live-Weg (TryStartShot) nutzt sie nie.
    //   - SEBs FilterBlend/LiveFilterBlend laufen NICHT mit (Registry nur fuer Quellen aus SEBs eigenem Pool, DynamicBetterSourcePool.cs:156)
    //     -> der Tiefpass ist der des Spiel-Okkluders (FilterCutoffCalculator.Compute -> Farbe.ComputeNach der Mod).
    //  Bis paket-1112i griffen auf diesem Weg: Okkluder/Physik/Hall/Kennlinie ja; Richtwirkung nein (nur LiveWeaponSoundEngine.FireBullet);
    //  Luft FALSCH (volle Entfernung zusaetzlich zur Luft der Fernaufnahme); Abschnitt 13 nein (nur TryStartShot); Schalldaempfer-Merker
    //  der Kennlinie FALSCH (nur aus PrepareShot, Pool-Quelle trug den Wert ihres letzten Live-Schusses); Klang-Log "clip=-".
    //
    //  Eingriff (nur Spielmethoden, keine SEB-Namen - eine neue SEB-Fassung kann hier nichts brechen):
    //   - Prefix WeaponSoundPlayer.FireBullet (vor SEB): Richtwirkung je Kugel (SchussRichtKern, dieselbe Rechnung wie SebTeil) und
    //     "Fenster" bis zum Finalizer. Prefix SuperBetterAudioQueue.Enqueue im Fenster: deren zwei Quellen gehoeren diesem Schuetzen.
    //   - Pegel: im Okkluder-Kontext (Farbe.KontextVor/-Ende) wie die Laubdaempfung als Faktor auf OcclusionVolumeFactor; das Spiel
    //     fuehrt source.volume selbst gleitend nach (BetterSource.UpdateSourceVolume, 10/s) -> waehrend einer Schleife gleitet der Pegel,
    //     wenn sich der Schuetze dreht. Beim Start eines Samples (Prefix PlayOn) wird der Faktor genau gesetzt (SchussRichtKern.StartFaktor),
    //     weil SuperSource.PlayScheduled den Pegel sofort uebernimmt (SuperSource.cs:192-205).
    //   - Tiefpass: Farbe.ComputeNach (Spiel-Okkluder) = Min(Okkluder, Richtwirkung, Luft ab der letzten Blendgrenze, Laub) - dieselbe
    //     Min-Bildung wie SebTeil.BlendRechnen; das Spiel-EQ gleitet (AudioEQFilterBase), laufende Schleifen nie "sofort" (Stetigkeit).
    //   - Abschnitt 13: nur beim Start eines Samples (PlayOn: neue Wiedergabe, Stetigkeit.SofortZulassen erlaubt dort einen Sprung) -
    //     waehrend eine Schleife laeuft wird Reichweite/Kurve NIE umgesetzt (sie ist ein Clip ueber viele Schuesse; Aenderungen gelten ab
    //     dem naechsten Sample = Schweif oder naechste Salve). Bezug ist die Reichweite der Bank (SoundBank.Rolloff), nicht SEBs 600 m.
    //   - Abbruch der Schleife (StopLoopV1, LegacyLoopCuts) bleibt unberuehrt: die Mod liest nur, plant nichts um.
    //  Faden: alles Hauptfaden (FireBullet aus dem Spiel-Update, Queue-Update aus BetterAudio, Okkluder aus SpatialAudioSystem).
    // ------------------------------------------------------------------------------------------
    internal static class SchussV1
    {
        internal static ConfigEntry<bool> Aktiv;
        static bool an;
        static SchussRichtKern.V1Teil teile;
        static bool gestoert;

        internal static bool Bereit => teile != SchussRichtKern.V1Teil.Keiner && !gestoert;
        static bool Laeuft => an && teile != SchussRichtKern.V1Teil.Keiner && !gestoert && Plugin.Aktiv.Value;

        // je WeaponSoundPlayer (einmal angelegt): Werte der letzten Kugel
        sealed class Schuss
        {
            public WeaponSoundPlayer Spieler;
            public bool Gueltig, Innen, Gedaempft, EigeneSicht;
            public float W, Db, DirLp = SchussRichtKern.OffenLp;
            public SoundBank BankA, BankB;            // A = Body/Doublet, B = Tail (letzte Clipwahl im Fenster)
            public AudioClip B1, B2;
            public bool MitteA, MitteB;               // Abschnitt 13 "keep mid clip" bei dieser Clipwahl
        }

        // je Pool-Quelle der Warteschlange (einmal angelegt): wem sie gerade gehoert und was ihr Sample traegt
        sealed class Quelle
        {
            public Schuss S;
            public float GIst = 1f, OvfGemerkt;
            public bool Gemerkt;
            public SoundBank Bank;
            public float Abzug, Faktor = 1f;
            public bool Schleife, Mitte;
            public bool Nah;                          // nahclip: Sample traegt die Nah-Aufnahme (Luft ueber die ganze Strecke, Kurve 1/r)
            public bool MaxGesetzt; public float MaxVorher;
        }

        static readonly ConditionalWeakTable<WeaponSoundPlayer, Schuss> schussTab = new ConditionalWeakTable<WeaponSoundPlayer, Schuss>();
        static readonly ConditionalWeakTable<WeaponSoundPlayer, Schuss>.CreateValueCallback schussNeu = _ => new Schuss();
        static readonly ConditionalWeakTable<BetterSource, Quelle> quellTab = new ConditionalWeakTable<BetterSource, Quelle>();
        static readonly ConditionalWeakTable<BetterSource, Quelle>.CreateValueCallback quelleNeu = _ => new Quelle();

        static Schuss fenster;              // FireBullet/StopFiringLoop dieses Schuetzen laeuft gerade (Hauptfaden)
        static Quelle anwendenQ;            // Okkluder-Anwenden der Quelle laeuft gerade
        static float anwendenG = 1f;

        // Diagnose (30 s)
        static int samples, schleifen, schweife, richtN, startKorr, kurve13, quellen, fremdN;
        static double summeW, summeDb, summeAbzug;

        internal static void Binden(ConfigFile c, string sek)
        {
            Aktiv = c.Bind(sek, "Cover SEB legacy (V1) weapons", true,
                "Sound Engine Backport plays some weapons with its older \"V1 engine\" through the game's own gunshot queue instead of its live engine " +
                "(weapons with looped recordings; since SEB 1.1.1 also automatic weapons whose recordings are one equally long clip per distance step). " +
                "On = these shots get the same treatment as the others: weapon directivity (this section), air absorption only beyond the last distance step of the recording (8), " +
                "the section 13 distance curve when that is enabled, the suppressor flag for the loudness curve, and \"SEB V1 engine\" in the sound log. " +
                "Off = V1 shots as before (air absorption over the full distance, no directivity). Applies live (from the next shot).");
            an = Aktiv.Value;
            Aktiv.SettingChanged += (_, __) => an = Aktiv.Value;
        }

        // ---------------------------------------------------------------- Anwenden
        internal static void Anwenden(Harmony h)
        {
            if (!Chainloader.PluginInfos.TryGetValue("red.soundenginebackport", out var pi))
            {
                Plugin.Log.LogInfo("SEB V1 weapons: Sound Engine Backport not found - nothing to cover.");
                return;
            }
            if (!SebSchutz.VersionPasst(pi.Metadata.Version))
            {
                Plugin.Log.LogWarning($"SEB V1 weapons: Sound Engine Backport {pi.Metadata.Version} found, built for 1.0.x/1.1.x -> V1 coverage off (V1 shots play as SEB intends).");
                return;
            }
            var feuer = AccessTools.DeclaredMethod(typeof(WeaponSoundPlayer), "FireBullet");
            var stop = AccessTools.DeclaredMethod(typeof(WeaponSoundPlayer), "StopFiringLoop", Type.EmptyTypes);
            var enq = AccessTools.DeclaredMethod(typeof(SuperBetterAudioQueue), "Enqueue",
                new[] { typeof(AudioClip), typeof(AudioClip), typeof(float), typeof(double), typeof(float), typeof(float), typeof(float) });
            var playOn = AccessTools.DeclaredMethod(typeof(SuperAudioSample), "PlayOn", new[] { typeof(SuperSource) });
            var pick = AccessTools.DeclaredMethod(typeof(SoundBank), "PickClipsByDistance");
            var rel = AccessTools.DeclaredMethod(typeof(BetterSource), "Release", Type.EmptyTypes);
            // Kontext des Okkluders (Farbe.KontextVor/ComputeNach) ist gepatcht, wenn SebAkustik ApplyOcclusionToSource + Compute fand
            teile = SchussRichtKern.Teile(feuer != null, enq != null, rel != null, SebAkustik.LuftOk, playOn != null, pick != null, stop != null);
            if (teile == SchussRichtKern.V1Teil.Keiner)
            {
                Plugin.Log.LogWarning("SEB V1 weapons: game targets not found (WeaponSoundPlayer.FireBullet, SuperBetterAudioQueue.Enqueue, BetterSource.Release or the occlusion context) " +
                                      "-> V1 coverage off, V1 shots play as SEB intends.");
                return;
            }
            // vor SEB (dessen Prefix ueberspringt das Original) und mit hoechster Prioritaet, damit kein anderer Prefix uns auslaesst
            string[] vorSeb = { "red.soundenginebackport" };
            h.Patch(feuer, prefix: new HarmonyMethod(typeof(SchussV1), nameof(FeuerVor)) { priority = Priority.First, before = vorSeb },
                           finalizer: new HarmonyMethod(typeof(SchussV1), nameof(FensterZu)));
            h.Patch(enq, prefix: new HarmonyMethod(typeof(SchussV1), nameof(EnqueueVor)));
            h.Patch(rel, prefix: new HarmonyMethod(typeof(SchussV1), nameof(FreigabeVor)));
            if ((teile & SchussRichtKern.V1Teil.Start) != 0)
                h.Patch(playOn, prefix: new HarmonyMethod(typeof(SchussV1), nameof(PlayOnVor)));
            if ((teile & SchussRichtKern.V1Teil.ClipWahl) != 0)
                h.Patch(pick, postfix: new HarmonyMethod(typeof(SchussV1), nameof(ClipNach)) { priority = Priority.Last });   // nach Fernschuss.ClipNach
            if ((teile & SchussRichtKern.V1Teil.Ende) != 0)
                h.Patch(stop, prefix: new HarmonyMethod(typeof(SchussV1), nameof(StopVor)) { priority = Priority.First, before = vorSeb },
                              finalizer: new HarmonyMethod(typeof(SchussV1), nameof(FensterZu)));
            string fehlt = (playOn == null ? " SuperAudioSample.PlayOn (level from the first sample, section 13, clip name)" : "") +
                           (pick == null ? " SoundBank.PickClipsByDistance (bank per sample, keep mid clip)" : "") +
                           (stop == null ? " WeaponSoundPlayer.StopFiringLoop (tail bank)" : "");
            if (fehlt.Length > 0) Plugin.Log.LogWarning("SEB V1 weapons: not found, these parts are off:" + fehlt + ".");
            Plugin.Log.LogInfo($"SEB V1 weapons: ready ({teile}) - patched WeaponSoundPlayer.FireBullet/StopFiringLoop, SuperBetterAudioQueue.Enqueue, " +
                               $"SuperAudioSample.PlayOn, SoundBank.PickClipsByDistance, BetterSource.Release. Switch is {(an ? "on" : "off")}.");
        }

        static void Fehler(string teil, Exception ex)
        {
            if (!SebSchutz.Strukturfehler(ex)) { Statistik.Fehler(ex); return; }
            if (gestoert) return;
            gestoert = true;
            Plugin.Log.LogError($"SEB V1 weapons: {teil} failed ({ex.GetType().Name}: {ex.Message}) - the game probably changed. V1 coverage is off for this session; V1 shots play as SEB intends.");
        }

        // ---------------------------------------------------------------- Haken: Schuss
        // Prefix WeaponSoundPlayer.FireBullet(Ammo ammo, Vector3 shotPosition, Vector3 shotDirection, float pitchMult, ...)
        static void FeuerVor(WeaponSoundPlayer __instance, Vector3 shotPosition, Vector3 shotDirection)
        {
            fenster = null;
            if (!Laeuft || __instance == null) return;
            try { fenster = Vorbereiten(__instance, shotPosition, shotDirection); }
            catch (Exception ex) { fenster = null; Fehler("shot preparation", ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static Schuss Vorbereiten(WeaponSoundPlayer p, Vector3 pos, Vector3 richtung)
        {
            var br = p.playersBridge;
            if (br == null) return null;
            var s = schussTab.GetValue(p, schussNeu);
            s.Spieler = p;
            s.EigeneSicht = br.PointOfView == EPointOfView.FirstPerson;
            s.Innen = br.Environment == EnvironmentType.Indoor;
            s.Gedaempft = p.IsSilenced;
            s.Gueltig = false; s.W = 0f; s.Db = 0f; s.DirLp = SchussRichtKern.OffenLp;
            if (s.EigeneSicht || !SebAkustik.RichtAktiv.Value) return s;
            var ba = MonoBehaviourSingleton<BetterAudio>.Instance;
            var lt = ba != null ? ba.ListenerTransform : null;
            if (lt == null) return s;
            Vector3 o = lt.position - pos;
            float w = SchussRichtKern.W(richtung.x, richtung.y, richtung.z, o.x, o.y, o.z);
            float g = SchussRichtKern.Gewicht(s.Innen, SebAkustik.InnenFaktor.Value);
            s.W = w;
            s.Db = SchussRichtKern.Db(w, SebAkustik.RueckDb.Value, g);
            s.DirLp = SchussRichtKern.Lp(w, SebAkustik.RueckLpf.Value, g);
            s.Gueltig = true;
            return s;
        }

        // Prefix WeaponSoundPlayer.StopFiringLoop: der Schweif (StopLoopV1) waehlt seine Clips hier
        static void StopVor(WeaponSoundPlayer __instance)
        {
            fenster = null;
            if (!Laeuft || __instance == null) return;
            if (schussTab.TryGetValue(__instance, out var s)) fenster = s;
        }

        // Finalizer FireBullet/StopFiringLoop (laeuft auch bei Ausnahme; void = Ausnahme des Spiels geht unveraendert weiter)
        static void FensterZu() => fenster = null;

        // Prefix SuperBetterAudioQueue.Enqueue: im Fenster gehoeren die zwei Quellen dieser Warteschlange dem Schuetzen
        static void EnqueueVor(SuperBetterAudioQueue __instance)
        {
            var s = fenster;
            if (s == null || __instance == null) return;
            try { Verknuepfen(__instance, s); }
            catch (Exception ex) { Fehler("queue", ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Verknuepfen(SuperBetterAudioQueue w, Schuss s)
        {
            var a = w.AudioSources;
            if (a == null) return;
            for (int i = 0; i < a.Length; i++)
            {
                var src = a[i];
                if (src == null) continue;
                var q = quellTab.GetValue(src, quelleNeu);
                if (!ReferenceEquals(q.S, s))
                {
                    q.S = s; q.Gemerkt = false; q.GIst = 1f; q.MaxGesetzt = false; q.Schleife = false; q.Mitte = false; q.Nah = false;
                    quellen++;
                }
                // bis zum ersten PlayOn: Bank der letzten Clipwahl (sonst die Body-Bank des Schuetzen) fuer den Luft-Versatz
                if (q.Bank == null) BankSetzen(q, s.BankA ?? Koerper(s), s);
                SebAkustik.GedaempftSetzen(src, s.Gedaempft);   // Kennlinie: Schalldaempfer dieses Schuetzen, nicht der des letzten Live-Schusses
            }
        }

        static SoundBank Koerper(Schuss s)
        {
            var p = s.Spieler;
            return p == null ? null : (s.Gedaempft && p.BodySilenced != null ? p.BodySilenced : p.Body);
        }

        static void BankSetzen(Quelle q, SoundBank b, Schuss s)
        {
            q.Bank = b;
            var opt = b != null ? b.BlendOptions : null;
            SchussRichtKern.LuftVersatz(opt != null ? opt.Values : null, SebAkustik.SchussAbzug.Value, out q.Abzug, out q.Faktor);
            // nahclip: die Nah-Aufnahme enthaelt keine Luft der Strecke -> volle Entfernung (Abzug 0, Faktor 1)
            q.Nah = s != null && Fernschuss.NahGilt(b, s.Innen ? (int)EnvironmentType.Indoor : (int)EnvironmentType.Outdoor, s.EigeneSicht);
            if (q.Nah) { q.Abzug = 0f; q.Faktor = 1f; }
        }

        // Postfix SoundBank.PickClipsByDistance (zuletzt, nach Fernschuss.ClipNach): welche Bank waehlte welche Clips
        static void ClipNach(SoundBank __instance, ref AudioClip clip1, ref AudioClip clip2)
        {
            var s = fenster;
            if (s == null || Fernschuss.SchussBank != null) return;   // im Live-Weg (TryStartShot) gehoert die Wahl SEB/Fernschuss
            var p = s.Spieler;
            if (p == null || __instance == null) return;
            bool mitte = Fernschuss.SchussMitte; Fernschuss.SchussMitte = false;
            if (ReferenceEquals(__instance, p.Tail) || ReferenceEquals(__instance, p.TailSilenced))
            { s.BankB = __instance; s.B1 = clip1; s.B2 = clip2; s.MitteB = mitte; }
            else { s.BankA = __instance; s.MitteA = mitte; }
        }

        /// <summary>Fuer Fernschuss.ClipNach: gehoert diese Bank zum laufenden V1-Fenster (Abschnitt 13 "keep mid clip")?</summary>
        internal static bool MitteFenster(SoundBank b)
        {
            if (!Fernschuss.Aktiv.Value || !Fernschuss.MitteBehalten.Value) return false;
            return BankImFenster(b);
        }

        /// <summary>nahclip: Nah-Aufnahme fuer diese Clipwahl im laufenden V1-Fenster (fremder Schuss, Bank des Schuetzen)?</summary>
        internal static bool NahFenster(SoundBank b, int environment)
        {
            var s = fenster;
            return s != null && BankImFenster(b) && Fernschuss.NahGilt(b, environment, s.EigeneSicht);
        }

        static bool BankImFenster(SoundBank b)
        {
            var s = fenster;
            if (s == null || b == null || !Laeuft) return false;
            var p = s.Spieler;
            return p != null && (ReferenceEquals(b, p.Body) || ReferenceEquals(b, p.BodySilenced) || ReferenceEquals(b, p.Doublet)
                              || ReferenceEquals(b, p.DoubletSilenced) || ReferenceEquals(b, p.Tail) || ReferenceEquals(b, p.TailSilenced));
        }

        /// <summary>nahclip: traegt diese V1-Quelle gerade die Nah-Aufnahme (fuer den Luft-Tiefpass in Farbe.ComputeNach)?</summary>
        internal static bool NahQuelle(BetterSource src) =>
            Laeuft && src != null && quellTab.TryGetValue(src, out var q) && q.S != null && !q.S.EigeneSicht && q.Nah;

        // ---------------------------------------------------------------- Haken: Sample-Start
        // Prefix SuperAudioSample.PlayOn(SuperSource source): Clips/Pegel sind gesetzt, PlayScheduled folgt im Original
        static void PlayOnVor(SuperAudioSample __instance, SuperSource source)
        {
            if (!Laeuft || source == null || __instance == null) return;
            if (!quellTab.TryGetValue(source, out var q) || q.S == null) return;
            try { Start(__instance, source, q); }
            catch (Exception ex) { Fehler("sample start", ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Start(SuperAudioSample a, SuperSource src, Quelle q)
        {
            var s = q.S;
            var c1 = a.Clip1; var c2 = a.Clip2;
            bool schweif = s.BankB != null && ((c1 != null && (ReferenceEquals(c1, s.B1) || ReferenceEquals(c1, s.B2)))
                                            || (c2 != null && (ReferenceEquals(c2, s.B1) || ReferenceEquals(c2, s.B2))));
            BankSetzen(q, schweif ? s.BankB : (s.BankA ?? Koerper(s)), s);
            q.Schleife = a.End == 0.0;   // SuperBetterAudioQueue.Update: Loop = End == 0
            q.Mitte = schweif ? s.MitteB : s.MitteA;
            SebAkustik.GedaempftSetzen(src, s.Gedaempft);
            samples++; if (q.Schleife) schleifen++; if (schweif) schweife++;
            if (s.EigeneSicht) return;
            fremdN++; summeAbzug += q.Abzug;

            // Pegel genau ab diesem Sample (PlayScheduled uebernimmt OcclusionVolumeFactor sofort)
            if (SebAkustik.RichtAktiv.Value && s.Gueltig && src.IncludedInOcclusionProcess && !SebAkustik.IstSebQuelle(src))
            {
                float gSoll = SchussRichtKern.Gain(s.Db);
                float f = SchussRichtKern.StartFaktor(src.OcclusionVolumeFactor, q.OvfGemerkt, q.Gemerkt, q.GIst, gSoll);
                if (Math.Abs(f - 1f) > 1e-4f) { src.SetOcclusionVolumeFactor(src.OcclusionVolumeFactor * f); startKorr++; }
                q.GIst = gSoll; q.OvfGemerkt = src.OcclusionVolumeFactor; q.Gemerkt = true;
                richtN++; summeW += s.W; summeDb += s.Db;
            }
            Fern(src, q, s);
        }

        /// <summary>Abschnitt 13 fuer dieses Sample (nur am Start - siehe Kopf).</summary>
        static void Fern(SuperSource src, Quelle q, Schuss s)
        {
            if (!Fernschuss.Bereit || Fernschuss.Gestoert) return;
            var bank = q.Bank;
            bool an13 = Fernschuss.Aktiv.Value && bank != null && SchussRichtKern.FernGilt(s.Innen, s.Gedaempft, s.EigeneSicht);
            if (q.Mitte) { Fernschuss.MitteGenutzt++; Fernschuss.BankMerken(Fernschuss.BanksMitte, bank); }
            FernschussSeb.KurveFertig k = null;
            float bankMax = q.MaxGesetzt ? q.MaxVorher : src.MaxDistance;   // Reichweite, die das Spiel/SEB-V1 gesetzt hat
            float maxD = bankMax;
            if (q.Nah)
            {
                // nahclip: Bezug ist die Gruppenkurve bei der Reichweite der Bank (wie Abschnitt 13 auf diesem Weg); Reichweite draussen
                // nur verlaengert, wenn Abschnitt 13 an ist
                float nahMax = an13 ? SchussRichtKern.FernMax(bankMax, Fernschuss.MaxDraussen.Value) : bankMax;
                if (Math.Abs(src.MaxDistance - nahMax) > 0.01f)
                {
                    if (!q.MaxGesetzt) { q.MaxVorher = src.MaxDistance; q.MaxGesetzt = true; }
                    src.SetRolloff(nahMax);
                }
                if (FernschussSeb.NahAuflegen(src, bank, nahMax, bankMax, Farbe.Entfernung(src), s.Gedaempft)) return;   // nahkennlinie: Schalldaempfer fuer den Quellpegel
                if (q.MaxGesetzt) { src.SetRolloff(q.MaxVorher); q.MaxGesetzt = false; maxD = bankMax; }
            }
            if (an13)
            {
                maxD = SchussRichtKern.FernMax(bankMax, Fernschuss.MaxDraussen.Value);
                k = FernschussSeb.KurveFuer(src, bank, s.Innen ? EnvironmentType.Indoor : EnvironmentType.Outdoor, maxD, bankMax);
            }
            if (k == null)
            {
                if (FernschussSeb.HatMarke(src)) FernschussSeb.Zuruecksetzen(src);
                if (q.MaxGesetzt) { src.SetRolloff(q.MaxVorher); q.MaxGesetzt = false; }
                Fernschuss.Merken(src, float.NaN, q.Mitte);
                return;
            }
            if (Math.Abs(src.MaxDistance - maxD) > 0.01f)
            {
                if (!q.MaxGesetzt) { q.MaxVorher = src.MaxDistance; q.MaxGesetzt = true; }
                src.SetRolloff(maxD);
            }
            FernschussSeb.KurveAuflegen(src, k);
            kurve13++;
            Fernschuss.Verstaerkt++;
            Fernschuss.BankMerken(Fernschuss.BanksVerstaerkt, bank);
            if (KlangLog.An) Fernschuss.Merken(src, FernschussSeb.UnterschiedDb(k, maxD, bankMax, Farbe.Entfernung(src)), q.Mitte);
        }

        // Prefix BetterSource.Release: die Quelle geht an den Pool zurueck -> Zuordnung loesen (Kurve setzt Fernschuss.FreigabeVor zurueck,
        // OcclusionVolumeFactor setzt das Spiel selbst auf 1: BetterSource.Release -> ResetOcclusion, BetterSource.cs:329-336)
        static void FreigabeVor(BetterSource __instance)
        {
            if (__instance == null || !quellTab.TryGetValue(__instance, out var q) || q.S == null) return;
            try
            {
                q.S = null; q.Gemerkt = false; q.GIst = 1f; q.Bank = null; q.MaxGesetzt = false; q.Schleife = false; q.Mitte = false; q.Nah = false;
                SebAkustik.GedaempftSetzen(__instance, false);
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        // ---------------------------------------------------------------- aus dem Okkluder-Kontext (SebAkustik.Farbe)
        /// <summary>Aus Farbe.KontextVor (Prefix ApplyOcclusionToSource): Richtwirkung als Faktor auf die Lautstaerke dieses Anwendens
        /// (wie die Laubdaempfung; KontextEnde stellt die Rechenwerte des Okkluders wieder her, die Quelle behaelt den Wert).</summary>
        internal static void AnwendenVor(SpatialAudioOccluder occ, BetterSource source)
        {
            anwendenQ = null;
            if (!Laeuft || source == null || occ == null) return;
            if (!quellTab.TryGetValue(source, out var q) || q.S == null) return;
            try
            {
                var s = q.S;
                if (s.EigeneSicht || !s.Gueltig || !SebAkustik.RichtAktiv.Value || SebAkustik.IstSebQuelle(source)) return;
                float g = SchussRichtKern.Gain(s.Db);
                anwendenQ = q; anwendenG = g;
                if (g > 0.99999f) return;
                var cp = occ.CurrentParameters;
                if (!Kontext.VolGeaendert) { Kontext.Occ = occ; Kontext.VolOrig = cp.OcclusionVolumeFactor; Kontext.VolGeaendert = true; }
                cp.OcclusionVolumeFactor *= g;
            }
            catch (Exception ex) { anwendenQ = null; Statistik.Fehler(ex); }
        }

        /// <summary>Aus Farbe.KontextEnde: welchen Faktor traegt die Quelle jetzt (fuer den genauen Pegel beim naechsten Sample-Start).</summary>
        internal static void AnwendenNach(BetterSource source)
        {
            var q = anwendenQ;
            anwendenQ = null;
            if (q == null || source == null) return;
            if (source.IncludedInOcclusionProcess) { q.OvfGemerkt = source.OcclusionVolumeFactor; q.GIst = anwendenG; q.Gemerkt = true; }
        }

        /// <summary>Aus Farbe.ComputeNach/WaldVor: Entfernung fuer die Luftdaempfung eines V1-Schusses (ohne die eingebackene Luft der
        /// Fernaufnahme, wie SebTeil.BlendRechnen). false = keine V1-Quelle (Entfernung unveraendert).</summary>
        internal static bool LuftMeterAnpassen(BetterSource src, ref float d)
        {
            if (!Laeuft || src == null || !quellTab.TryGetValue(src, out var q) || q.S == null || q.S.EigeneSicht || q.Bank == null) return false;
            d = SchussRichtKern.LuftMeter(d, q.Faktor, q.Abzug);
            return true;
        }

        /// <summary>Aus Farbe.ComputeNach (nach Luft und Laub): Tiefpass der Richtwirkung + SEB-Felder des Klang-Logs.</summary>
        internal static void TiefpassNach(BetterSource src, ref float lp, ref float hp, float luftHz, float waldM, float waldHz)
        {
            if (!Laeuft || src == null || !quellTab.TryGetValue(src, out var q) || q.S == null) return;
            var s = q.S;
            if (s.EigeneSicht || SebAkustik.IstSebQuelle(src)) return;
            float dirLp = SebAkustik.RichtAktiv.Value && s.Gueltig ? s.DirLp : float.MaxValue;
            if (dirLp < lp) { lp = dirLp; if (hp > lp) hp = lp; }
            if (KlangLog.An)
                KlangLog.SebMerken(src, s.W, s.Gueltig ? s.Db : 0f, dirLp, luftHz, lp, waldM, waldHz, s.Gedaempft, true, q.Schleife);
        }

        // ---------------------------------------------------------------- Diagnose
        internal static void Loggen()
        {
            if (samples == 0) return;
            string r = richtN > 0 ? $", directivity {richtN}x (mean w {summeW / richtN:F2}, {summeDb / richtN:F1} dB, level set at start {startKorr}x)" : "";
            string luft = fremdN > 0 ? $", {fremdN} of others (air offset mean {summeAbzug / fremdN:F0} m)" : "";
            Plugin.Log.LogInfo($"[Diagnostics] SEB V1 weapons: {samples} samples ({schleifen} loop clips, {schweife} tails) on {quellen} newly owned queue sources" +
                               r + luft + (kurve13 > 0 ? $", section 13 curve {kurve13}x" : ""));
            samples = schleifen = schweife = richtN = startKorr = kurve13 = quellen = fremdN = 0;
            summeW = summeDb = summeAbzug = 0;
        }
    }
}
