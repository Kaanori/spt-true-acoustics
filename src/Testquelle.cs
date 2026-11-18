using System;
using System.Collections.Generic;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using Diz.DependencyManager;
using Diz.Resources;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    //  testquelle (12.11.2026): Testquelle fuer die Messung (F12 Abschnitt 20, alles Standard AUS / ohne Taste).
    //  Wozu: Messung B1 (review-20261110/hallsend-STAND.md Abschnitt 9) - eine Quelle an fester Stelle, die DENSELBEN Weg nimmt wie
    //  ein Gegnerklang, waehrend der Hoerer woanders steht.
    //
    //  Spielweg (Dekompilat, im Spielcode gelesen):
    //   - Fremde Schritte: Player.CreateNestedSource (EFT/Player.cs:27240-27256) leiht EINE Quelle aus dem Pool Character
    //     (BetterAudio.method_5, BetterAudio.cs:986: ReverbSimpleSource), setzt den Mixer ObservedPlayerMovement, EQ an, raeumlich,
    //     HRTF 1, haengt sie an den Spieler und meldet sie EINMAL mit SpatialAudioSystem.ProcessSourceOcclusion(player, source) an
    //     (SpatialAudioSystem.cs:308-346: Behaelter am Spine1, EOcclusionTest.Combined -> CombinedSpatialAudioOccluder mit
    //     SpatialAudioOcclusionCombinedCalculator + ParallelSoundPropagationCalculator, SpatialAudioOccluderFactory.cs:22-26).
    //     Jeder Schritt spielt dann ueber SoundBank.Play -> BetterSource.Play auf DIESER Quelle (SoundBank.cs:233-249).
    //   - Die Testquelle macht dasselbe ohne Spieler: Quelle aus dem Pool, ein eigenes leeres GameObject als Traeger (wie Spine1),
    //     Anmeldung ueber die GameObject-Ueberladung ProcessSourceOcclusion(go, source, EOcclusionTest.Combined) (SpatialAudioSystem.cs:
    //     348-397) - gleicher Occluder-Typ, gleicher Prozessor, gleiche Haken der Mod (Raumweg, Wandweg, Diffus-Aufteilung, Send, Klang-Log).
    //     Die Quelle bleibt angemeldet, solange sie liegt (wie die Schrittquelle eines Bots): der Occluder ist beim Ausloesen schon
    //     warm (SEB haelt einen Combined-Occluder bis zu seinem ersten OnLateUpdate stumm, landkarte-spielcode.md Abschnitt 2).
    //   - Gruppe Gunshots: Pool Gunshots (BetterAudio.cs:982, ReverbSuperSource), abgespielt wie SuperAudioSample.PlayOn
    //     (SuperAudioSample.cs:16-25: Clip an source1, PlayScheduled) - nur in PlayScheduled spielt ReverbSuperSource seine Hall-Zwillinge
    //     (ReverbSuperSource.cs:96-120). ANNAHME: Gegnerschuesse laufen bei Kaan ueber SEB, nicht ueber diese Warteschlange; Gruppe,
    //     Pool und Behaelter am Spine1 sind dieselben (Klang-Log: "Gunshots ... src=ReverbSuperSource ... owner=Base HumanSpine1").
    //
    //  Messhilfen:
    //   - "Send override": Send der Testquelle = Preset + fester Zusatz statt des Werts der Mod. Geschrieben in einem Postfix auf
    //     SpatialAudioOccluder.ApplyOcclusionToSource NACH der Mod (Physik.SendNach, Priority.Normal) und VOR dem Klang-Log
    //     (Priority.Last) - damit steht der wirksame Wert als "send now" in der Klangzeile. Ein Haken ist noetig, weil die Mod den Send
    //     bei jedem Anwenden neu schreibt (Physik.cs:420-432); eigener Code ausserhalb dieses Aufrufs wuerde Bild fuer Bild mit ihr
    //     wechseln. Fuer jede andere Quelle: ein Referenzvergleich, sonst nichts.
    //   - "Dry muted": die Meta-Quelle bekommt DirectSoundEnabled = false (MetaXRAudioSourceExperimentalFeatures, Parameter 16, Tooltip
    //     in Meta.XR.Audio.dll: "disable direct sound propagation, so only late reverberations is heard"). Genau so schaltet das Spiel
    //     seine Hall-Zwillinge auf "nur Hall" (ReverbSimpleSource.SetPreset/Prewarm, ReverbSuperSource.method_12). Spielt die Quelle
    //     nicht raeumlich (das Spiel schaltet Binaural je nach Blickwinkel/Abstand ab, BetterSource.CheckBinauralAllowed), traegt der
    //     Zwilling den Hall und source1 den trockenen Klang -> dann wird source1 nach dem Start stumm geschaltet (AudioSource.mute; der
    //     Zwilling ist eine eigene AudioSource und uebernimmt nur volume, ReverbSimpleSource.UpdateSourceVolume).
    //  testschuss (17.11.2026): Signal "Gunshot", Wiederholung, Rauschlaenge, Bezugs-Tick.
    //   - Gunshot: fester ungedaempfter AK-Schuss aus einer Spiel-Soundbank, ohne dass im Raid jemand geschossen haben muss. Weg wie
    //     SEB (LiveBankReplacement.Load, SEB 1.1.1 LiveBankReplacement.cs:131-155): Singleton<IEasyAssets>.System.Retain(Schluessel) ->
    //     LoadingJob abwarten (hier: im Tick abfragen, kein await) -> SoundBank aus Node.Data.Assets. Schluessel und Bankname aus SEBs
    //     Tabelle (LiveBankTable.cs: ak105new/ak12/ak545 = live_embedded, von SEBs Servermod im Bundle-Manifest gemeldet, bundles.json),
    //     zuletzt die Vanilla-Bank akm. Clip = Environments[Outdoor].Clips[nah].Clips[0] (fest statt SelectRandomClip, SoundBank.cs:109-167).
    //     SEBs Live-Engine spielt eine solche Bank je Einzelschuss als ganzen Clip (GunshotEmitterBase.TryStartShot), ohne Tail-Bank
    //     (LiveBankTable: kein Tail-Eintrag) -> der Clip enthaelt den Ausklang. Vanilla-Feuerschleifen ("..._loop", eigene Tail-Bank)
    //     werden verworfen (TestquelleKern.IstSchleife = SEBs IsLoopClip). Das Bundle bleibt wie bei SEB fuer die Sitzung gehalten.
    //     Kein Clip ladbar: Warnung im Log, dann der synthetische Ersatzschuss - in jeder TEST-SHOT-Zeile als "FALLBACK" gekennzeichnet.
    //   - Wiederholung: Abspieltaste startet/stoppt (Schalter "Repeat"); geplant auf der DSP-Uhr (naechster = letzter Start + Intervall).
    //   - Bezugs-Tick: eigene 2D-AudioSource (kein Spatializer, keine Mixergruppe, keine Effekte/Hall), PlayScheduled mit DERSELBEN
    //     dsp-Zeit wie die Testquelle. Ist der Tick an, spielt auch Footsteps geplant (SimpleSource.PlayScheduled statt PlayOneShot).
    //  Aufraeumen: neue Ablage, Raid-Ende (GameWorld oder SpatialAudioSystem gewechselt), Szenenwechsel. Release gibt die Quelle an den
    //  Pool zurueck; der leere Behaelter meldet sich dann selbst ab (AudioSourceContainer.RemoveSource -> ClearGoComponents).
    // ------------------------------------------------------------------------------------------
    internal static class Testquelle
    {
        internal const string Sek = "20. Test source (measurement)";
        const double Vorlauf = 0.05;   // Gunshots: PlayScheduled 50 ms voraus (wie eine Warteschlange; Startzeit steht in der Logzeile)

        static ConfigEntry<KeyboardShortcut> AblegenTaste, AusloesenTaste;
        static ConfigEntry<TestSignal> Signal;
        static ConfigEntry<TestGruppe> KlangGruppe;
        static ConfigEntry<string> SpielClipName;
        static ConfigEntry<float> Lautstaerke, Reichweite, SendZusatz;
        static ConfigEntry<bool> SendUeber, TrockenStumm;
        static ConfigEntry<bool> Wiederholen, BezugsTick;          // testschuss (17.11.)
        static ConfigEntry<float> WiederholS, RauschS;            // testschuss (17.11.)

        static BetterSource quelle;
        static GameObject anker;
        static SpatialAudioSystem system;
        static EFT.GameWorld welt;
        static Vector3 ort;
        static TestGruppe ablageGruppe;
        static int ablageNr, schussNr;
        static AudioClip klick, rosa, spielClip;
        static string spielClipFuer;
        static float rosaFuer = float.NaN;

        // testschuss: Wiederholung (DSP-Uhr) und Bezugs-Tick
        static bool laeuft;
        static int wiederholNr;
        static double naechsterStart;
        static AudioSource tickQuelle;
        static AudioClip tickClip, ersatzSchuss;

        internal static void Anwenden(Harmony h, ConfigFile c)
        {
            AblegenTaste = c.Bind(Sek, "Place test source key", KeyboardShortcut.Empty,
                "Places the test source where you stand (your ear position) and writes '=== TEST SOURCE placed at ... ===' into the sound log. Pressing it again moves the source. Unbound by default. For measurements only.");
            AusloesenTaste = c.Bind(Sek, "Play test sound key", KeyboardShortcut.Empty,
                "Plays the test sound at the placed test source (walk somewhere else first) and writes '=== TEST SHOT n ===' into the sound log. The sound takes the same path as an enemy's footstep or shot (game pool, occlusion, rooms, reverb send). With 'Repeat' on, the key starts and stops the repetition. Unbound by default.");
            Signal = c.Bind(Sek, "Signal", TestSignal.Click,
                "Click = 10 ms broadband burst (decay curve). PinkNoise = pink noise burst ('Noise length (s)'). GameClip = a loaded game sound by name (see 'Game clip name'). Gunshot = a fixed unsuppressed AK rifle shot from the game's sound banks (close, outdoor, with its ring-out), loaded by the mod itself - nobody has to shoot first; if no game shot can be loaded, a synthetic shot is used and every TEST SHOT line says FALLBACK. Each signal is followed by 3 s of silence so the reverb can ring out. Applies live.");
            SpielClipName = c.Bind(Sek, "Game clip name", "",
                "Exact name of a loaded game AudioClip for Signal = GameClip. Empty or not found: the log lists loaded clips whose names look like shots/explosions. Applies live.");
            KlangGruppe = c.Bind(Sek, "Sound group", TestGruppe.Footsteps,
                "Which game pool the test source uses: Footsteps = like enemy footsteps (Character, ReverbSimpleSource), Gunshots = like shots (ReverbSuperSource). Applies at the next placement.");
            Lautstaerke = c.Bind(Sek, "Volume", 1f,
                new ConfigDescription("Volume of the test sound before occlusion (game scale 0..1). Applies live.", new AcceptableValueRange<float>(0.01f, 1f)));
            Reichweite = c.Bind(Sek, "Max distance (m)", 100f,
                new ConfigDescription("Rolloff distance of the test source (game SoundBank default 100 m). Beyond it plus 5 m the game stops updating its occlusion. Applies at the next placement.", new AcceptableValueRange<float>(20f, 300f)));
            SendUeber = c.Bind(Sek, "Send override", false,
                "On = the reverb send of the TEST SOURCE ONLY is fixed at preset + 'Send override (dB)' instead of the mod's value (the dry level stays the mod's). For comparing send 0 / +10 / +20 dB. Normal sounds are not affected. Applies live.");
            SendZusatz = c.Bind(Sek, "Send override (dB)", 0f,
                new ConfigDescription("Fixed send above the preset for 'Send override'. The game's reverb send is limited to +20 dB absolute (Footsteps preset -4 -> at most +24, Gunshots preset 0 -> at most +20). Applies live.", new AcceptableValueRange<float>(0f, 24f)));
            TrockenStumm = c.Bind(Sek, "Dry muted (reverb only)", false,
                "On = the direct (dry) sound of the test source is silent, only the game's reverb plays (Meta 'direct sound' off, the same switch the game uses for its reverb-only twins). Applies from the next test sound.");

            // testschuss (17.11.)
            Wiederholen = c.Bind(Sek, "Repeat", false,
                "On = the 'Play test sound key' starts a repeating test sound (every 'Repeat interval (s)'); press it again to stop. Each repetition writes its own '=== TEST SHOT n ===' line with the current switch states, so switching F12 settings while it repeats can be matched to the log. Stops at raid end. Off = one sound per key press. Applies live.");
            WiederholS = c.Bind(Sek, "Repeat interval (s)", 4f,
                new ConfigDescription("Time between two repeated test sounds (start to start, on the audio clock). Applies live from the next sound.", new AcceptableValueRange<float>(1f, 15f)));
            RauschS = c.Bind(Sek, "Noise length (s)", (float)TestquelleKern.RosaStandardS,
                new ConfigDescription("Length of the PinkNoise burst. Applies from the next test sound.", new AcceptableValueRange<float>((float)TestquelleKern.RosaMinS, (float)TestquelleKern.RosaMaxS)));
            BezugsTick = c.Bind(Sek, "Reference tick", false,
                "On = together with every test sound a short quiet NON-spatial tick plays (2 ms, 6 kHz, -26 dBFS, straight to your ears: no spatializer, no mixer group, no reverb), scheduled on the audio clock with exactly the same start time as the test sound (also Footsteps then start scheduled instead of 'play now'). The start time is written into the TEST SHOT line. For travel-time measurements. Applies from the next test sound.");
            Wiederholen.SettingChanged += (_, __) => { if (!Wiederholen.Value && laeuft) Stoppen("switch 'Repeat' off"); };

            SendUeber.SettingChanged += (_, __) => SendGeaendert();
            SendZusatz.SettingChanged += (_, __) => SendGeaendert();

            var apply = AccessTools.DeclaredMethod(typeof(SpatialAudioOccluder), "ApplyOcclusionToSource", new[] { typeof(BetterSource), typeof(bool) });
            if (apply == null) Plugin.Log.LogWarning("Test source: SpatialAudioOccluder.ApplyOcclusionToSource not found -> 'Send override' unavailable.");
            else h.Patch(apply, postfix: new HarmonyMethod(typeof(Testquelle), nameof(NachApply)) { priority = Priority.Low });
        }

        // ---------------------------------------------------------------- je Bild (Plugin.Update)

        internal static void Tick()
        {
            if (anker != null || !ReferenceEquals(quelle, null))
            {
                string grund = RaidVorbei();
                if (grund != null) Aufraeumen(grund);
            }
            // testschuss: Spiel-Schuss laden (nur solange er gebraucht wird und nicht fertig ist; keine Allokation im Leerlauf)
            if (schussZustand == Lade.Nichts ? (anker != null && Signal.Value == TestSignal.Gunshot) : (schussZustand == Lade.Laedt || schussZustand == Lade.Daten))
                SchussWeiter();
            Zweitstimme.Bild();   // zweitweg (17.11.)
            // testschuss: Wiederholung auf der DSP-Uhr
            if (laeuft)
            {
                if (anker == null || ReferenceEquals(quelle, null)) Stoppen("test source gone");
                else if (TestquelleKern.Faellig(AudioSettings.dspTime, naechsterStart, Geplant(), Vorlauf, BildReserve))
                {
                    wiederholNr++;
                    if (!Ausloesen(naechsterStart)) Stoppen("playing failed");
                }
            }
            bool a = AblegenTaste != null && AblegenTaste.Value.IsDown();
            bool b = AusloesenTaste != null && AusloesenTaste.Value.IsDown();
            if (!a && !b) return;
            if (TipptGerade()) return;
            if (a) Ablegen();
            else if (laeuft) Stoppen("key");
            else if (Wiederholen.Value) Starten();
            else Ausloesen(double.NaN);
        }

        /// <summary>Reserve fuer den Bildtakt: der naechste geplante Schuss wird so frueh angestossen, dass auch ein langsames Bild (bis
        /// 100 ms) den Vorlauf nicht auffrisst. Annahme: Bilder ueber 150 ms (Vorlauf + Reserve) sind selten; dann steht "LATE" in der Zeile.</summary>
        const double BildReserve = 0.1;

        /// <summary>Geplanter Start (PlayScheduled): Gunshots immer (Spielweg), Footsteps nur mit Bezugs-Tick.</summary>
        // zweitweg (17.11.): mit 'second path as its own voice' spielen beide Stimmen geplant (sample-genauer Versatz)
        static bool Geplant() => ablageGruppe == TestGruppe.Gunshots || BezugsTick.Value || Richtung.ZweitAn;

        static void Starten()
        {
            if (anker == null || quelle == null || RaidVorbei() != null)
            {
                Melden("Test source: no test source placed (F12 > " + Sek + " > Place test source key).", true);
                return;
            }
            wiederholNr = 1;
            laeuft = true;
            Melden("=== TEST REPEAT started === every " + TestquelleKern.Intervall(WiederholS.Value).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " s placement=" + ablageNr, false);
            if (!Ausloesen(double.NaN)) Stoppen("playing failed");
        }

        static void Stoppen(string grund)
        {
            if (!laeuft) return;
            laeuft = false;
            Melden("=== TEST REPEAT stopped (" + grund + ") === after " + wiederholNr + " sounds, placement=" + ablageNr, false);
            wiederholNr = 0;
        }

        internal static void SzeneGewechselt()
        {
            if (anker != null || !ReferenceEquals(quelle, null)) Aufraeumen("scene changed");
        }

        static string RaidVorbei()
        {
            if (!SpatialAudioSystem.Initialized || !MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated) return "raid ended (audio system gone)";
            if (!ReferenceEquals(MonoBehaviourSingleton<SpatialAudioSystem>.Instance, system) || system == null) return "raid ended (new audio system)";
            if (!Comfort.Common.Singleton<EFT.GameWorld>.Instantiated || !ReferenceEquals(Comfort.Common.Singleton<EFT.GameWorld>.Instance, welt)) return "raid ended (game world gone)";
            if (quelle == null) return "source destroyed by the game";
            return null;
        }

        // ---------------------------------------------------------------- Ablegen

        static void Ablegen()
        {
            try
            {
                if (anker != null || !ReferenceEquals(quelle, null)) Aufraeumen("moved");
                var ba = MonoBehaviourSingleton<BetterAudio>.Instantiated ? MonoBehaviourSingleton<BetterAudio>.Instance : null;
                var lt = ba != null ? ba.ListenerTransform : null;
                if (lt == null || !SpatialAudioSystem.Initialized || !MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated
                    || !Comfort.Common.Singleton<EFT.GameWorld>.Instantiated)
                {
                    Melden("Test source: not placed - only possible in a raid.", true);
                    return;
                }
                var sys = MonoBehaviourSingleton<SpatialAudioSystem>.Instance;
                ablageGruppe = KlangGruppe.Value;
                var typ = ablageGruppe == TestGruppe.Gunshots ? BetterAudio.AudioSourceGroupType.Gunshots : BetterAudio.AudioSourceGroupType.Character;
                ort = lt.position;

                anker = new GameObject("Materialklang test source");
                anker.transform.position = ort;
                UnityEngine.Object.DontDestroyOnLoad(anker);   // Lebensdauer bestimmt Aufraeumen(), nicht das Entladen der Szene

                var q = ba.GetSource(typ);   // BorrowSource: aktiv, Grundlautstaerke 1, Verdeckung zurueckgesetzt (BetterAudio.cs:246-267)
                if (q == null) { Melden("Test source: the game pool gave no source.", true); Aufraeumen(null); return; }
                // wie Player.CreateNestedSource fuer fremde Spieler (EFT/Player.cs:27245-27256)
                var grp = ba.SourceGroups[(int)typ];
                q.SetMixerGroup(typ == BetterAudio.AudioSourceGroupType.Character ? ba.ObservedPlayerMovementMixer : grp.MixerGroup);
                q.EnabledEQ(true);
                q.SetPitch(1f);   // Pool-Objekt: SoundBank.Play hinterlaesst seine Zufallstonhoehe
                q.SetParent(anker.transform, false);
                q.LocalPosition = Vector3.zero;
                q.SetRolloff(Reichweite.Value);   // vor der Anmeldung: der Behaelter liest MaxDistance beim AddSource (AudioSourceContainer.method_2)
                // schusshrtf (12.11.): nur Schritte sind beim Spiel raeumlich (CreateNestedSource: raeumlich, HRTF 1). Schuesse entscheidet
                // der Spielweg je Schuss (EnableStereo + PlayScheduled, siehe Ausloesen) - vorher war die Gunshots-Quelle hier immer raeumlich
                // und damit nicht wie ein echter Gegnerschuss (monofern-STAND.md, Abschnitt 6).
                if (typ == BetterAudio.AudioSourceGroupType.Character)
                {
                    q.EnableSpatialization = true;
                    q.HrtfIntensity = 1f;
                }
                quelle = q; system = sys; welt = Comfort.Common.Singleton<EFT.GameWorld>.Instance;
                int id = sys.ProcessSourceOcclusion(anker, q, EOcclusionTest.Combined, 30000f, Vector3.zero, false);
                if (id < 0) { Melden("Test source: the game refused the occlusion registration.", true); Aufraeumen(null); return; }

                ablageNr++;
                string zeile = TestquelleKern.AblageZeile(ablageNr, ort.x, ort.y, ort.z, RaumName(sys.ListenerCurrentRoom), GruppenText(ablageGruppe, q),
                                                         Reichweite.Value, id);
                Melden(zeile, false);
                if (!KlangLog.An) Plugin.Log.LogWarning("Test source: the sound log is off (F12 > 11) - TEST lines and sound lines are only in this log.");
                Ton(EFT.UI.EUISoundType.MenuCheckBox);   // nicht der Markenton (QuestSubTrackComplete), damit auswerten.py --marken ihn nicht zaehlt
            }
            catch (Exception e)
            {
                Statistik.Fehler(e);
                Plugin.Log.LogWarning("Test source: placing failed (" + e.GetType().Name + ": " + e.Message + ") - nothing placed.");
                Aufraeumen(null);
            }
        }

        // ---------------------------------------------------------------- Ausloesen

        /// <summary>Ein Testklang. wunsch = geplanter Start auf der DSP-Uhr (Wiederholung), NaN = so bald wie moeglich. false = nichts gespielt.</summary>
        static bool Ausloesen(double wunsch)
        {
            try
            {
                if (anker == null || quelle == null || RaidVorbei() != null)
                {
                    Melden("Test source: no test source placed (F12 > " + Sek + " > Place test source key).", true);
                    return false;
                }
                var clip = ClipHolen(out string signalText);
                if (clip == null) return false;
                var q = quelle;
                bool dry = TrockenStumm.Value;
                bool meta = q.Spatializer is MetaSpatialAudioSource;
                q.Mute(false);   // CanPlay verlangt !mute (BetterSource.cs:155-165); ein vorher stummer source1 wird hier wieder frei
                if (meta) q.EnableDirectSound = !dry;   // wirkt ueber MetaXRAudioSourceExperimentalFeatures.UpdateParameters je Bild
                float vol = Lautstaerke.Value;
                bool tick = BezugsTick.Value;
                bool geplant = Geplant();
                double jetzt = AudioSettings.dspTime;
                double start = geplant ? TestquelleKern.GeplanterStart(wunsch, jetzt, Vorlauf) : jetzt;
                bool zwillingFehlt = false;
                string zweitText = null;   // zweitweg
                if (q is SuperSource ss)
                {
                    // wie SuperAudioSample.PlayOn (SuperAudioSample.cs:16-25)
                    ss.source1.clip = clip; ss.source1.volume = vol;
                    ss.source2.enabled = true; ss.source2.clip = null; ss.source2.volume = 0f;
                    ss.SetPitch(1f);
                    // schusshrtf (12.11.): wie ein FREMDER Schuss (SEB GunshotEmitterBase.cs:117 EnableStereo(IsFirstPerson = false),
                    // Spiel WeaponSoundPlayer.cs:311) - danach entscheidet PlayScheduled: Spiel = Stereo + Hall-Zwilling,
                    // Schalter "Gunshots: HRTF" an = Meta-Spatializer (SchussRaum.cs)
                    ss.EnableStereo(false);
                    ss.PlayScheduled(start);
                    // nachhallschuss (17.11.): derselbe Weg wie ein Gegnerschuss (Schluessel = die Testquelle selbst)
                    Zweitstimme.SchussEreignis(ss, start, ss, out zweitText);
                }
                else if (geplant)
                {
                    // testschuss: Bezugs-Tick an -> geplant wie SuperAudioSample.PlayOn, nur mit der einfachen Quelle (SimpleSource.PlayScheduled,
                    // SimpleSource.cs:52-60: Lautstaerke aus source1.volume, source1.PlayScheduled). Unterschied zu Play: kein OnPlayed (der
                    // Occluder ist ohnehin warm und wendet jedes Bild an) und - nur wenn die Quelle NICHT raeumlich spielt - kein Hall-Zwilling
                    // (ReverbSimpleSource ueberschreibt PlayScheduled nicht); das steht dann in der Zeile.
                    q.Stop();
                    q.source1.clip = clip; q.source1.volume = vol;
                    double startQ = Zweitstimme.Planen(q, clip, vol, start, out zweitText);   // zweitweg (17.11.): zweiter Weg als eigene Stimme
                    q.PlayScheduled(startQ);
                    zwillingFehlt = !q.EnableSpatialization && q.EnableReverb && q is Audio.ReverbSubsystem.ReverbSimpleSource;
                }
                else
                {
                    q.Stop();   // kein Ueberlappen mit dem vorigen Testklang (PlayOneShot)
                    q.Play(clip, null, 1f, vol, false, true);   // wie SoundBank.Play (SoundBank.cs:246); loest OnPlayed -> sofortiges Anwenden aus
                }
                double tickDsp = double.NaN;
                if (tick && geplant)
                {
                    var tq = TickQuelle();
                    if (tq != null) { tq.Stop(); tq.PlayScheduled(start); tickDsp = start; }   // dieselbe double-Zahl wie oben
                }
                bool spaet = geplant && AudioSettings.dspTime >= start;   // Audio-Uhr schon darueber -> Unity startet sofort statt geplant
                if (laeuft) naechsterStart = start + TestquelleKern.Intervall(WiederholS.Value);
                bool raeumlich = q.EnableSpatialization;
                string trocken = "on";
                if (dry)
                {
                    if (raeumlich && meta) trocken = "muted (Meta direct sound off)";
                    else if (!raeumlich) { q.source1.mute = true; trocken = "muted (dry source muted, reverb twin plays)"; }
                    else trocken = "NOT muted (no Meta spatializer)";
                }
                string weg = raeumlich ? (meta && q.EnableReverb ? "meta-source (direct + reverb in one spatializer)" : "meta-source without reverb")
                                       : zwillingFehlt ? "stereo, reverb twin NOT played (scheduled start)" : "stereo + reverb twin";
                if (q is SuperSource)   // schusshrtf (12.11.): wer den Schussweg bestimmt hat
                    weg += raeumlich ? (SchussRaum.VomSchalter(q) ? " [gunshot HRTF switch]" : " [game: spatial]") : " [game gunshot path]";
                if (SendUeber.Value) SendSchreiben(q, SendZusatz.Value);   // Gunshots: kein OnPlayed -> sonst erst beim naechsten Anwenden
                float sendJetzt = Physik.SendJetzt(q, out float preset);

                var lt = MonoBehaviourSingleton<BetterAudio>.Instance.ListenerTransform;
                Vector3 h = lt != null ? lt.position : new Vector3(float.NaN, float.NaN, float.NaN);
                schussNr++;
                string zeile = TestquelleKern.SchussZeile(schussNr, DateTime.Now.ToString("HH:mm:ss.fff"), start, signalText, GruppenText(ablageGruppe, q),
                    ort.x, ort.y, ort.z, h.x, h.y, h.z, SendUeber.Value ? SendZusatz.Value : double.NaN, sendJetzt, preset, trocken, weg,
                    q.OcclusionVolumeFactor, ablageNr,
                    TestquelleKern.ZusatzText(Plugin.Aktiv == null || Plugin.Aktiv.Value, MetaVerdeckung.LogFeld(q), geplant, start, spaet, tickDsp,
                                              laeuft ? wiederholNr : 0, TestquelleKern.Intervall(WiederholS.Value)));
                if (zweitText != null) zeile += zweitText;   // zweitweg: nur je Testklang
                Melden(zeile, false);
                return true;
            }
            catch (Exception e)
            {
                Statistik.Fehler(e);
                Plugin.Log.LogWarning("Test source: playing failed (" + e.GetType().Name + ": " + e.Message + ").");
                return false;
            }
        }

        /// <summary>testschuss: 2D-Quelle fuer den Bezugs-Tick, Kind des Traegers (lebt und stirbt mit ihm). Kein Spatializer, keine
        /// Mixergruppe (direkt zum Hoerer), keine Effekte/Hallzonen, kein Doppler; Prioritaet 0, damit Unity sie nie virtualisiert.</summary>
        static AudioSource TickQuelle()
        {
            if (tickQuelle != null) return tickQuelle;
            if (anker == null) return null;
            int sr = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            if (tickClip == null) tickClip = ClipAus(TestquelleKern.Tick(sr), "materialklang_reference_tick", sr);
            var go = new GameObject("Materialklang reference tick");
            go.transform.SetParent(anker.transform, false);
            var a = go.AddComponent<AudioSource>();
            a.playOnAwake = false; a.loop = false; a.clip = tickClip;
            a.spatialBlend = 0f; a.spatialize = false; a.spatializePostEffects = false;
            a.bypassEffects = true; a.bypassListenerEffects = true; a.bypassReverbZones = true;
            a.dopplerLevel = 0f; a.priority = 0; a.volume = 1f; a.pitch = 1f;
            a.outputAudioMixerGroup = null;
            tickQuelle = a;
            return a;
        }

        static string GruppenText(TestGruppe g, BetterSource q)
        {
            string typ = q != null && q.Preset != null ? q.Preset.Type.ToString() : "?";
            return g + "(" + typ + ")";
        }

        // ---------------------------------------------------------------- Signale (einmal erzeugt)

        static AudioClip ClipHolen(out string text)
        {
            text = null;
            int sr = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            switch (Signal.Value)
            {
                case TestSignal.Click:
                    if (klick == null) klick = ClipAus(TestquelleKern.Klick(sr, out _), "materialklang_test_click", sr);
                    text = TestquelleKern.SignalText(TestSignal.Click, null, klick.length);
                    return klick;
                case TestSignal.PinkNoise:
                {
                    float l = (float)TestquelleKern.RosaLaenge(RauschS.Value);
                    if (rosa == null || rosaFuer != l)
                    {
                        if (rosa != null) UnityEngine.Object.Destroy(rosa);
                        rosa = ClipAus(TestquelleKern.Rosa(sr, out _, l), "materialklang_test_pink", sr); rosaFuer = l;
                    }
                    text = TestquelleKern.SignalText(TestSignal.PinkNoise, null, l);
                    return rosa;
                }
                case TestSignal.Gunshot:
                    if (schussZustand == Lade.Bereit && schussClip != null && schussClip.loadState == AudioDataLoadState.Loaded)
                    {
                        text = TestquelleKern.SignalText(TestSignal.Gunshot, schussClip.name, schussClip.length, schussHerkunft);
                        return schussClip;
                    }
                    if (ersatzSchuss == null) ersatzSchuss = ClipAus(TestquelleKern.Schuss(sr, out _), "materialklang_test_gunshot_fallback", sr);
                    text = TestquelleKern.SignalText(TestSignal.Gunshot, null, ersatzSchuss.length, SchussGrund());
                    return ersatzSchuss;
                default:
                    string name = (SpielClipName.Value ?? "").Trim();
                    if (spielClip == null || spielClipFuer != name) { spielClip = SpielClipSuchen(name); spielClipFuer = name; }
                    if (spielClip == null) return null;
                    if (spielClip.loadState != AudioDataLoadState.Loaded) spielClip.LoadAudioData();
                    text = TestquelleKern.SignalText(TestSignal.GameClip, spielClip.name, spielClip.length);
                    return spielClip;
            }
        }

        static AudioClip ClipAus(float[] d, string name, int sr)
        {
            var c = AudioClip.Create(name, d.Length, 1, sr, false);
            c.SetData(d, 0);
            c.hideFlags = HideFlags.DontUnloadUnusedAsset;   // Resources.UnloadUnusedAssets beim Szenenwechsel soll ihn nicht entladen
            return c;
        }

        /// <summary>Nur auf Tastendruck (FindObjectsOfTypeAll ist teuer - nie je Bild).</summary>
        static AudioClip SpielClipSuchen(string name)
        {
            var alle = Resources.FindObjectsOfTypeAll<AudioClip>();
            if (!string.IsNullOrEmpty(name))
                foreach (var c in alle)
                    if (c != null && string.Equals(c.name, name, StringComparison.OrdinalIgnoreCase)) return c;
            var kandidaten = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in alle)
            {
                if (c == null || string.IsNullOrEmpty(c.name)) continue;
                string n = c.name.ToLowerInvariant();
                if (n.Contains("shot") || n.Contains("fire") || n.Contains("explo") || n.Contains("grenade") || n.Contains("gun") || n.Contains("blast"))
                    kandidaten.Add(c.name);
                if (kandidaten.Count >= 60) break;
            }
            Melden("Test source: game clip '" + name + "' not found among " + alle.Length + " loaded clips. Candidates: " + string.Join(", ", kandidaten), true);
            return null;
        }

        // ---------------------------------------------------------------- testschuss: Spiel-Schuss laden

        enum Lade { Nichts, Laedt, Daten, Bereit, Aus }
        // Reihenfolge: AK-Reihe aus SEBs Live-Baenken (Einzelschuss mit Ausklang), zuletzt Vanilla akm (wird verworfen, wenn Feuerschleife).
        static readonly string[] SchussBundle =
        {
            "assets/content/audio/banks/live_embedded/ak105new.bundle", "assets/content/audio/banks/live_embedded/ak12.bundle",
            "assets/content/audio/banks/live_embedded/ak545.bundle", "assets/content/audio/banks/akm.bundle",
        };
        static readonly string[] SchussBank = { "ak105new_main", "ak12_main", "ak545_main", "akm_main" };
        static Lade schussZustand;
        static int schussIdx;
        static DependencyGraph<IEasyBundle>.Token schussToken;
        static AudioClip schussClip;
        static string schussHerkunft, schussFehler = "";

        static string SchussGrund()
        {
            switch (schussZustand)
            {
                case Lade.Aus: return "no game shot loadable: " + schussFehler.TrimEnd(';', ' ');
                case Lade.Bereit: return "game clip unloaded";
                case Lade.Nichts: return "game shot not requested yet";
                default: return "game shot still loading (" + SchussBank[Math.Min(schussIdx, SchussBank.Length - 1)] + ")";
            }
        }

        /// <summary>Hauptfaden, aus Tick: ein Schritt der Lade-Kette (Retain -> LoadingJob fertig -> Bank/Clip -> Audiodaten).</summary>
        static void SchussWeiter()
        {
            try
            {
                if (schussZustand == Lade.Nichts) { schussZustand = Lade.Laedt; schussIdx = 0; schussFehler = ""; }
                if (schussZustand == Lade.Laedt)
                {
                    if (schussToken == null && !SchussAnfordern()) return;
                    var job = schussToken.LoadingJob;
                    if (!job.IsCompleted) return;
                    var assets = Comfort.Common.Singleton<IEasyAssets>.Instance;
                    var node = assets.System.GetNode(SchussBundle[schussIdx]);
                    if (job.IsFaulted || job.IsCanceled || node.Data.LoadState.Value != ELoadState.Loaded) { SchussFehl("bundle did not load"); return; }
                    EFT.SoundBank bank = null;
                    var alle = node.Data.Assets;
                    if (alle != null)
                        foreach (var o in alle)
                            if (o is EFT.SoundBank b && string.Equals(b.name, SchussBank[schussIdx], StringComparison.OrdinalIgnoreCase)) { bank = b; break; }
                    if (bank == null) { SchussFehl("no sound bank " + SchussBank[schussIdx]); return; }
                    var env = bank.Environments;
                    var dv = env != null && env.Length > 0 && env[0] != null ? env[0][0] : null;   // Outdoor (EnvironmentType 0), nahe Stufe
                    var clip = dv != null && dv.Clips != null && dv.Clips.Length > 0 ? dv.Clips[0] : null;
                    if (clip == null) { SchussFehl("bank has no close outdoor clip"); return; }
                    if (TestquelleKern.IstSchleife(clip.name)) { SchussFehl("clip " + clip.name + " is a firing loop, not a single shot"); return; }
                    schussClip = clip;
                    schussHerkunft = SchussBank[schussIdx] + " (" + SchussBundle[schussIdx] + ")";
                    if (clip.loadState == AudioDataLoadState.Unloaded) clip.LoadAudioData();
                    schussZustand = Lade.Daten;
                }
                if (schussZustand == Lade.Daten)
                {
                    if (schussClip == null) { SchussFehl("clip destroyed"); return; }
                    var ls = schussClip.loadState;
                    if (ls == AudioDataLoadState.Loaded)
                    {
                        schussZustand = Lade.Bereit;
                        Melden("Test source: gunshot = game clip " + schussClip.name + " from " + schussHerkunft + ", " + schussClip.length.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                               + " s, " + schussClip.frequency + " Hz, " + schussClip.channels + " ch" + (schussFehler.Length > 0 ? " (skipped: " + schussFehler.TrimEnd(';', ' ') + ")" : ""), false);
                    }
                    else if (ls == AudioDataLoadState.Failed) SchussFehl("clip audio data failed to load");
                }
            }
            catch (Exception e)
            {
                Statistik.Fehler(e);
                SchussFehl(e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Retain des aktuellen Kandidaten (nur wenn er im Bundle-Manifest steht - GetNode wuerde sonst einen Fehler loggen und
        /// den Standardknoten liefern, DependencyGraph.cs:587-599). false = kein Token (Zustand weitergeschaltet).</summary>
        static bool SchussAnfordern()
        {
            while (schussIdx < SchussBundle.Length)
            {
                if (!Comfort.Common.Singleton<IEasyAssets>.Instantiated) { SchussAlleWeg("game asset system not available"); return false; }
                var sys = Comfort.Common.Singleton<IEasyAssets>.Instance.System;
                string key = SchussBundle[schussIdx];
                if (sys == null || sys.Nodes == null || !sys.Nodes.ContainsKey(key)) { schussFehler += SchussBank[schussIdx] + ": not in the bundle manifest; "; schussIdx++; continue; }
                schussToken = sys.Retain(key, null);
                return true;
            }
            SchussAlleWeg(null);
            return false;
        }

        static void SchussFehl(string grund)
        {
            schussFehler += SchussBank[Math.Min(schussIdx, SchussBank.Length - 1)] + ": " + grund + "; ";
            var t = schussToken; schussToken = null; schussClip = null;
            try { if (t != null && !t._released) t.Release(); } catch (Exception e) { Statistik.Fehler(e); }
            schussIdx++;
            if (schussIdx >= SchussBundle.Length) { SchussAlleWeg(null); return; }
            schussZustand = Lade.Laedt;
        }

        static void SchussAlleWeg(string grund)
        {
            if (grund != null) schussFehler += grund + "; ";
            schussZustand = Lade.Aus;
            Melden("Test source: no game gunshot clip could be loaded (" + schussFehler.TrimEnd(';', ' ') + ") -> Signal 'Gunshot' plays the SYNTHETIC fallback shot (marked FALLBACK in every TEST SHOT line).", true);
        }

        // ---------------------------------------------------------------- Send override

        // Postfix SpatialAudioOccluder.ApplyOcclusionToSource, Priority.Low: nach Physik.SendNach (Normal), vor KlangLog.NachApply (Last)
        static void NachApply(BetterSource source)
        {
            if (ReferenceEquals(quelle, null) || !ReferenceEquals(source, quelle)) return;
            Zweitstimme.NachApply(source);   // zweitweg (17.11.): erste Stimme auf ihren Energieanteil (eigenes try)
            if (!SendUeber.Value) return;
            try { SendSchreiben(source, SendZusatz.Value); }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>Send am tragenden Spatializer = Preset + Zusatz (dieselbe Wahl wie Physik.SendSchreiben: raeumlich = eigener
        /// Meta-Spatializer mit mono-Preset, sonst die Hall-Zwillinge mit stereo-Preset).</summary>
        static void SendSchreiben(BetterSource q, float zusatz)
        {
            if (q == null || q.Preset == null) return;
            var ak = q.Preset.acousticSettings;
            if (q.EnableSpatialization) { q.ReverbSendDB = TestquelleKern.SendZiel(ak.mono.reverbSendDb, zusatz); return; }
            float st = TestquelleKern.SendZiel(ak.stereo.reverbSendDb, zusatz);
            if (q is Audio.ReverbSubsystem.ReverbSimpleSource rs && rs._spatializer != null) rs._spatializer.ReverbSendDB = st;
            else if (q is Audio.ReverbSubsystem.ReverbSuperSource ss)
            {
                if (ss._spatializerA != null) ss._spatializerA.ReverbSendDB = st;
                if (ss._spatializerB != null) ss._spatializerB.ReverbSendDB = st;
            }
        }

        /// <summary>Alle Sends der Quelle auf den Preset (wie BetterSource/ReverbSimpleSource/ReverbSuperSource.SetPreset; dieselbe Regel wie
        /// das private Physik.SendPreset). Die Mod hebt ihn beim naechsten vollen Anwenden wieder an, wo sie aufteilt.</summary>
        static void SendPresetSetzen(BetterSource q)
        {
            if (q == null || q.Preset == null) return;
            var ak = q.Preset.acousticSettings;
            q.ReverbSendDB = ak.mono.reverbSendDb;
            float st = ak.stereo.reverbSendDb;
            if (q is Audio.ReverbSubsystem.ReverbSimpleSource rs && rs._spatializer != null) rs._spatializer.ReverbSendDB = st;
            else if (q is Audio.ReverbSubsystem.ReverbSuperSource ss)
            {
                if (ss._spatializerA != null) ss._spatializerA.ReverbSendDB = st;
                if (ss._spatializerB != null) ss._spatializerB.ReverbSendDB = st;
            }
        }

        static void SendGeaendert()
        {
            try
            {
                if (quelle == null) return;
                if (SendUeber.Value) SendSchreiben(quelle, SendZusatz.Value);
                else SendPresetSetzen(quelle);
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // ---------------------------------------------------------------- Aufraeumen

        static void Aufraeumen(string grund)
        {
            Stoppen(grund ?? "test source removed");   // testschuss: Wiederholung endet mit der Quelle (Raid-Ende, Umsetzen, Szenenwechsel)
            Zweitstimme.Aufraeumen();   // zweitweg (17.11.): zweite Stimme an den Pool, erste Stimme wieder voll
            var q = quelle;
            quelle = null;   // zuerst: der Postfix sieht die Quelle ab hier nicht mehr
            try
            {
                if (q != null)
                {
                    q.Mute(false);
                    if (q.Spatializer is MetaSpatialAudioSource) q.EnableDirectSound = true;   // Pool-Objekt: Release setzt das nicht zurueck
                    SendPresetSetzen(q);   // Release setzt den Send nicht zurueck (BetterSource.cs:329-345), Physik.SendFreigeben nur fuer eigene
                    q.Release();           // -> Pool (SetParent BetterAudio), Behaelter leer -> SpatialAudioSystem.ClearGoComponents
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
            try { if (tickQuelle != null) tickQuelle.Stop(); } catch (Exception e) { Statistik.Fehler(e); }
            try { if (anker != null) UnityEngine.Object.Destroy(anker); }   // Bezugs-Tick haengt als Kind daran
            catch (Exception e) { Statistik.Fehler(e); }
            anker = null; system = null; welt = null; tickQuelle = null;
            if (grund != null) Melden("=== TEST SOURCE removed (" + grund + ") === placement=" + ablageNr, false);
        }

        // ---------------------------------------------------------------- Hilfen

        static void Melden(string zeile, bool fehler)
        {
            if (fehler) { Plugin.Log.LogWarning(zeile); Ton(EFT.UI.EUISoundType.ErrorMessage); }
            else Plugin.Log.LogInfo(zeile);
            KlangLog.Text(zeile);
        }

        static void Ton(EFT.UI.EUISoundType art)
        {
            try { Comfort.Common.Singleton<EFT.UI.GUISounds>.Instance?.PlayUISound(art); }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static string RaumName(ISpatialAudioRoom r)
        {
            if (r == null) return null;
            string n = r.Name;
            if (string.IsNullOrEmpty(n) && r is Component c && c != null) n = c.gameObject.name;   // wie KlangLog.RaumName (Name in Karten leer)
            if (string.IsNullOrEmpty(n)) n = r.IsOutdoor ? "outdoor" : "?";
            return n;
        }

        // Kopie von LiveStart.TipptGerade (dort privat, gemeinsame Datei nicht anfassen): Texteingabe aktiv -> Taste zaehlt nicht.
        static Type esTyp;
        static bool TipptGerade()
        {
            try
            {
                if (esTyp == null) esTyp = Type.GetType("UnityEngine.EventSystems.EventSystem, UnityEngine.UI");
                if (esTyp == null) return false;
                var es = esTyp.GetProperty("current")?.GetValue(null);
                var go = es != null ? esTyp.GetProperty("currentSelectedGameObject")?.GetValue(es) as GameObject : null;
                if (go == null) return false;
                foreach (var c in go.GetComponents<Component>())
                    if (c != null && c.GetType().Name.IndexOf("InputField", StringComparison.Ordinal) >= 0) return true;
            }
            catch { }
            return false;
        }
    }
}
