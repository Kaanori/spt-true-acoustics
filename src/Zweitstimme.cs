using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // mehrweg (17.11.2026): weitere Raumwege als eigene Stimmen - Spiel-Teil mit eigenem Stimmenvorrat. Rechnung: ZweitwegKern.
    // mehrwegc (17.11. abends): auch fuer echte Gegner - Schritte/Geraet, Stimmen, Einmal-Klaenge der Welt (@GameWorld). Bericht:
    // review-20261110/mehrweg-STAND.md (Teil C). Schuesse (SEB SuperSource) und Schleifen: nicht (nur Plan).
    //
    // Ereignis (wer spielt was wann):
    //  - Postfix SimpleSource.Play (auch ReverbSimpleSource, ruft base.Play): Clip, Lautstaerke, Tonhoehe, Startversatz (timeSamples bei
    //    Play mit Hauptclip, Sprache: Player.cs:27075-27077) und Startzeit = AudioSettings.dspTime beim Aufruf. Ein One-Shot (Schritte,
    //    Geraet: SoundBank.Play -> SimpleSource.Play -> PlayOneShot, SimpleSource.cs:76-100) startet beim naechsten Mischblock - wann genau
    //    gegen dspTime, ist ungemessen: Unsicherheit U = ein DSP-Puffer (Regler 'start uncertainty', 0 = automatisch aus
    //    AudioSettings.GetDSPBufferSize / outputSampleRate; Spiel-Log: 1024 Samples x 4). Eine weitere Stimme spielt nur, wenn sie
    //    mindestens U nach dem Original kommt (ZweitwegKern.TechnikTor) - sonst kaeme sie womoeglich VOR dem Original, und das Ohr ortete
    //    die falsche Oeffnung. Das ist eine Grenze der Technik bis zur Startmessung, keine Klangregel.
    //  - Eigene Klaenge nie: das Spiel spielt die eigenen Schritte/Stimme nicht raeumlich (Player.cs:27253 EnableSpatialization = !IsYourPlayer;
    //    Stimme 27072 spatialBlend 0 in der Ich-Ansicht) -> nur raeumliche Quellen mit spatialBlend >= 0,5 und gueltigem Raumweg.
    //  - Geplant wird, sobald der Raumweg der Quelle vorliegt: sofort im Postfix (laufende Bots haben ihren Eintrag in Richtung) oder beim
    //    naechsten Anwenden der Quelle (Einmal-Klaenge: PlayAtPoint meldet die Quelle erst NACH Play an, BetterAudio.cs:1344-1377). Eine spaet
    //    geplante Stimme startet mit dem verstrichenen Teil uebersprungen (timeSamples), also im Zeitbezug des Originals.
    // Lautstaerke - EIN Anwender: der Faktor des Originals wird nur hier geteilt, und zwar immer vom zuletzt vom Spiel ANGEWANDTEN Wert aus
    //  (Grundwert). Der einzige Schreiber des Faktors ausser uns ist SpatialAudioOccluder.ApplyOcclusionToSource (SpatialAudioOccluder.cs:168;
    //  die Mod aendert dort nur CurrentParameters, ausser SchussV1 fuer Schuesse - die sind hier nicht dabei). Unser Postfix (Priority.VeryLow,
    //  nach Physik/Testquelle, vor KlangLog) liest den frischen Wert und teilt ihn; zwischen zwei Anwendungen schreibt Bild() dasselbe Produkt
    //  mit den gleitenden Anteilen. Ein zweiter Aufruf im selben Anwenden erkennt den eigenen Wert (Geschrieben) und teilt nicht doppelt.
    //  ManualUpdate gleitet source1.volume dorthin (Lerp 10 dt, BetterSource.cs:508-512) - wie bei jeder Okklusionsaenderung.
    // Ueberlappende Klaenge einer Quelle (Schritte): jedes Ereignis bekommt eigene Vorrats-Stimmen (eine Quelle je Ereignis und Richtung).
    //  Der Faktor des Originals gilt fuer alle seine Klaenge zugleich; die Anteile haengen nur an der Geometrie und sind fuer alle Ereignisse
    //  gleich. Eine Richtung (Slot) zaehlt als "abgedeckt", solange das NEUESTE Ereignis dort eine Stimme hat; bekommt es keine, blendet der
    //  Slot aus (Anteil -> 0, das Original wird wieder voll) - auch die aelteren Stimmen dieses Slots blenden mit.
    // Vorrat: Quellen aus dem Pool Character (BetterAudio.GetSource -> BorrowSource, nie null), nicht bei SpatialAudioSystem angemeldet (keine
    //  Okklusion des Spiels, keine Haken der Mod), EnableReverb = false (kein zweiter Hall-Send), vor der Rueckgabe zurueck; kein
    //  AudioSourceCulling (CullingFix.cs Kopf). Vergabe nach Energie; voll -> die leiseste Stimme wird entzogen (blendet in 0,1 s aus).
    // Keine Speicheranforderung je Klang: Ereignisse in einem festen Ring, Saetze/Stimmen einmal angelegt und wiederverwendet.
    // nachhallstimme (17.11. spaet): je Satz hoechstens EINE Nachhall-Stimme (eigener Vorrat, Hallfilter NachhallFilter): der verschmierte
    //  Teil des Raumwegs als echter Nachhall (NachhallKern). Mit ihr buendeln die weiteren Stimmen nur den GERICHTETEN Teil, das Original
    //  traegt Wand + gerichtetes Buendel 1, und Physik zieht fuer diese Quelle keinen Ereignis-/Sprachabzug ab (NachhallWirkt - EINE Stelle;
    //  ein schon im Grundwert enthaltener Abzug wird hier zurueckgenommen, Physik.AbzugFaktor). Ereignisse einer Quelle speisen dieselbe
    //  Stimme (PlayOneShot auf den stillen Traeger), ihr Nachhall laeuft ueber Satzwechsel/Freigabe hinweg aus.
    // ------------------------------------------------------------------------------------------
    internal static class Zweitstimme
    {
        internal const int KTest = 0, KSchritt = 1, KStimme = 2, KEinmal = 3, KSchuss = 4, KN = 5;   // nachhallschuss: KSchuss (nur Nachhall)
        static readonly string[] KName = { "test source", "footsteps+gear", "voices", "one-shots", "gunshots" };

        sealed class Stimme
        {
            public BetterSource S; public GameObject Traeger; public bool HallVorher;
            public Satz Besitzer; public int Slot; public bool Entzug; public double Prio; public double G = 1;
            public NachhallFilter F;                                   // nachhallstimme: nur Stimmen des Hall-Vorrats
            // nachhallschuss: Stimme eines Schuetzen (Schluessel = SEB-Emitter; alle Schuesse speisen sie), Ende der Fahne, Vergabe nach Energie,
            // Abstandskurve der Schussquelle (gesichert: die des Pools, zurueck fuer andere Klassen und vor der Rueckgabe)
            public object Schuetze; public double Ende, PrioE, TMax; public float PrioZeit;
            public AnimationCurve KurveVorher; public AudioRolloffMode ModusVorher; public bool KurveFremd;
            // vorverzug: feste Vorverzoegerung der Stimme (Einsatz des ersten Ereignisses) + wartende Anregungen (Einsatz je Ereignis)
            public double Vor; public AnregungsPuffer<AudioClip> Warte;
        }
        sealed class Satz
        {
            public BetterSource Q; public int Klasse;
            public readonly MehrAuswahl A = new MehrAuswahl();
            public readonly double[] SJetzt = new double[MehrAuswahl.KMax], SZiel = new double[MehrAuswahl.KMax];
            public readonly int[] SlotBuendel = new int[MehrAuswahl.KMax], SlotN = new int[MehrAuswahl.KMax];
            public readonly bool[] SlotNeu = new bool[MehrAuswahl.KMax];
            public readonly V3[] SlotDir = new V3[MehrAuswahl.KMax];
            public readonly List<Stimme> St = new List<Stimme>(MaxJeSatz);
            public float Basis = float.NaN, Geschrieben = float.NaN, Vol = 1f;
            public double Anteil = 1, Zeit = double.NaN;
            public bool Aktiv;
            // nachhallstimme
            public Stimme Hall; public bool HallAn; public double HallR0, HallR1, HallR2, HallEnde, HallVor; public float HallStart; public long HallBloecke0;
            public readonly NachhallWahl HW = new NachhallWahl();
            public string HallGrund;
            public void Leeren()
            {
                Q = null; Basis = Geschrieben = float.NaN; Anteil = 1; Zeit = double.NaN; Aktiv = false; St.Clear(); A.N = 0; A.Aktiv = 0;
                Hall = null; HallAn = false; HallR0 = HallR1 = HallR2 = HallEnde = HallVor = 0; HallStart = 0; HallBloecke0 = 0; HW.Hat = false; HallGrund = null;
                for (int b = 0; b < MehrAuswahl.KMax; b++) { SJetzt[b] = SZiel[b] = 0; SlotBuendel[b] = SlotN[b] = 0; SlotNeu[b] = false; SlotDir[b] = default; }
            }
        }
        struct Ereignis
        {
            public BetterSource Q; public AudioClip Clip; public float Vol, Pitch; public double Start, Ende; public int Offset; public bool OneShot;
            public AudioClip C1, C2; public float Balance, VolArg;   // nachhallstimme: Anregung wie SimpleSource.Play (PlayOneShot je Clip)
            public int KlasseP1; public object Schuetze;            // nachhallschuss: feste Klasse + 1 (0 = aus dem Klang), Schluessel des Schuetzen
        }

        const int MaxSaetze = 128, MaxJeSatz = 12, RingN = 64;   // nachhallschuss: 128 (ein Satz je Schuss, Dauerfeuer ~15 je Schuetze gleichzeitig)
        static readonly List<Stimme> vorrat = new List<Stimme>(64);
        static readonly List<Stimme> hallVorrat = new List<Stimme>(32);   // nachhallstimme: eigener Vorrat (Schleife + Filter, nie fuer Kopien)
        static readonly Dictionary<object, Stimme> schuetzen = new Dictionary<object, Stimme>(32);   // nachhallschuss: Stimme je Schuetze
        static readonly Dictionary<BetterSource, Satz> saetze = new Dictionary<BetterSource, Satz>(64);
        static readonly List<Satz> aktiv = new List<Satz>(MaxSaetze), freieSaetze = new List<Satz>(MaxSaetze);
        static int angelegt;
        static readonly Ereignis[] ring = new Ereignis[RingN];
        static int ringPos, offen;
        static BetterAudio vorratVon;
        static double uAuto;
        const double GleitS = 0.3, RichtungS = 0.12, EntzugS = 0.1;
        static readonly bool[] genommen = new bool[MehrAuswahl.KMax];
        static readonly int[] buendelSlot = new int[MehrAuswahl.KMax];

        // Diagnose (30 s), je Klasse
        static readonly int[] dKlaenge = new int[KN], dKeine = new int[KN], dVergeben = new int[KN], dAbgelehnt = new int[KN], dTechnik = new int[KN];
        static int dEntzogen, dSpitze, dBilder, dVerpasst;
        static readonly int[] dHall = new int[KN], dHallAbgelehnt = new int[KN], dHallOhne = new int[KN];
        static int dHallSpitze, dHallAnregungen, dHallEntzogen, dHallFern, dHallGewartet, dHallVoll;
        static double dHallFehlerMax;   // vorverzug: groesste Abweichung Feuer gegen Feuerzeit (s, nur gewartete)
        static long dTicks, dMaxTicks;

        static int K => Richtung.StimmenJeKlang != null ? Mathf.Clamp(Richtung.StimmenJeKlang.Value, 1, MehrAuswahl.KMax) : 3;
        static int Groesse => Richtung.VorratGroesse != null ? Mathf.Clamp(Richtung.VorratGroesse.Value, 0, 64) : 16;

        /// <summary>Startunsicherheit (ms) fuer Spielklaenge: Regler, 0 = ein DSP-Puffer (zur Laufzeit gelesen).</summary>
        internal static double UMs()
        {
            float r = Richtung.StartUnsicher != null ? Richtung.StartUnsicher.Value : 0f;
            if (r > 0f) return r;
            if (!(uAuto > 0))
            {
                try
                {
                    AudioSettings.GetDSPBufferSize(out int len, out _);
                    int sr = AudioSettings.outputSampleRate;
                    uAuto = len > 0 && sr > 0 ? 1000.0 * len / sr : 1000.0 * 1024 / 48000;
                }
                catch (Exception) { uAuto = 1000.0 * 1024 / 48000; }
            }
            return uAuto;
        }

        // ---------------------------------------------------------------- Haken

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var play = AccessTools.DeclaredMethod(typeof(SimpleSource), "Play",
                    new[] { typeof(AudioClip), typeof(AudioClip), typeof(float), typeof(float), typeof(bool), typeof(bool) });
                if (play == null) Plugin.Log.LogWarning("Room path voices: SimpleSource.Play not found -> no extra voices for enemies (test source only).");
                else h.Patch(play, prefix: new HarmonyMethod(typeof(Zweitstimme), nameof(PlayVor)), postfix: new HarmonyMethod(typeof(Zweitstimme), nameof(PlayNach)));
                var apply = AccessTools.DeclaredMethod(typeof(Audio.SpatialSystem.SpatialAudioOccluder), "ApplyOcclusionToSource", new[] { typeof(BetterSource), typeof(bool) });
                if (apply == null) Plugin.Log.LogWarning("Room path voices: ApplyOcclusionToSource not found -> no extra voices for enemies.");
                else h.Patch(apply, postfix: new HarmonyMethod(typeof(Zweitstimme), nameof(ApplyNach)) { priority = Priority.VeryLow });
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void PlayVor(SimpleSource __instance, out bool __state) { __state = __instance != null && __instance.CanPlay; }

        static void PlayNach(SimpleSource __instance, AudioClip clip1, AudioClip clip2, float balance, float volume, bool oneShot, bool __state)
        {
            if (!__state || !(MehrAn || Nachhall.An)) return;
            try
            {
                var q = __instance;
                if (q == null || q.source1 == null || !q.EnableSpatialization || q.source1.spatialBlend < 0.5f) return;
                if (!oneShot && q.source1.loop) return;   // Schleifen (Lampen, Generatoren): noch nicht (Plan, mehrweg-STAND Teil C)
                if (IstVorrat(q) || IstHall(q) || (saetze.TryGetValue(q, out var alt) && alt.Klasse == KTest)) return;
                AudioClip c; float scale;
                if (!oneShot) { c = clip1; scale = 1f; }   // Hauptclip: PreOcclusionVolume = BaseVolume x volume (SimpleSource.cs:80-86)
                else if (clip1 != null && clip2 != null)
                {
                    // zwei Clips mit Blende: die Stimme spielt den staerkeren mit der Energie beider (Annahme: zwei verschiedene Aufnahmen
                    // desselben Ereignisses addieren sich energetisch)
                    c = balance >= 0.5f ? clip1 : clip2;
                    scale = volume * Mathf.Sqrt(balance * balance + (1f - balance) * (1f - balance));
                }
                else { c = clip1 != null ? clip1 : clip2; scale = volume; }
                if (c == null || c.frequency <= 0) return;
                // ein neuer Hauptclip schneidet den alten ab (source1.Play) - dessen Stimmen enden mit
                if (!oneShot && alt != null) StimmenStoppen(alt);
                double jetzt = AudioSettings.dspTime;
                float pitch = Mathf.Max(0.05f, Mathf.Abs(q.source1.pitch));
                int off = oneShot ? 0 : Math.Max(0, q.source1.timeSamples);
                int i = ringPos; ringPos = (ringPos + 1) % RingN;
                if (ring[i].Q == null) offen++;
                ring[i] = new Ereignis
                {
                    Q = q, Clip = c, Vol = Mathf.Clamp01(q.PreOcclusionVolume * scale), Pitch = pitch, Start = jetzt, Offset = off, OneShot = oneShot,
                    C1 = clip1, C2 = clip2, Balance = balance, VolArg = volume,
                    Ende = jetzt + Math.Max(0, c.samples - off) / (double)c.frequency / pitch
                };
                Versuchen(i);
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>Postfix ApplyOcclusionToSource: Grundwert merken und teilen; offene Ereignisse dieser Quelle planen (Einmal-Klaenge).</summary>
        static void ApplyNach(BetterSource source)
        {
            if (source == null) return;
            try
            {
                if (saetze.Count > 0 && saetze.TryGetValue(source, out var s) && s.Aktiv)
                {
                    float f = source.OcclusionVolumeFactor;
                    if (float.IsNaN(s.Geschrieben) || f != s.Geschrieben) s.Basis = f;   // frisch vom Spiel (nicht unser eigener Wert)
                    Teilen(s);
                }
                if (offen > 0)
                    for (int i = 0; i < RingN; i++)
                        if (ReferenceEquals(ring[i].Q, source)) Versuchen(i);
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>Aus Testquelle.NachApply - derselbe Weg wie der Postfix (doppelt aufgerufen teilt er nicht doppelt).</summary>
        internal static void NachApply(BetterSource q) => ApplyNach(q);

        /// <summary>Prefix BetterSource.Release (ueber Richtung.FreigabeVor): die Quelle gehoert ab jetzt einem anderen Klang. Sprache endet
        /// mit ihr; Stimmen von One-Shots spielen zu Ende (sie sind um den Umweg spaeter, das Spiel gibt Einmal-Klaenge nach max(Laenge, 1 s) frei).</summary>
        internal static void Freigabe(BetterSource q)
        {
            if (q == null || (saetze.Count == 0 && offen == 0)) return;
            try
            {
                for (int i = 0; i < RingN; i++) if (ReferenceEquals(ring[i].Q, q)) { ring[i].Q = null; offen--; }
                if (!saetze.TryGetValue(q, out var s)) return;
                if (s.Klasse == KStimme || s.Klasse == KTest) StimmenStoppen(s);
                saetze.Remove(q);
                s.Q = null;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // ---------------------------------------------------------------- Planen

        static void Versuchen(int i)
        {
            var ev = ring[i];
            var q = ev.Q;
            if (q == null) return;
            double jetzt = AudioSettings.dspTime;
            if (jetzt >= ev.Ende - 0.02) { ring[i].Q = null; offen--; dVerpasst++; return; }
            if (!Richtung.Routen(q, out var mix, out V3 hJob) || !Richtung.RaumAnteil(q, out double anteil, out bool einmal)) return;   // spaeter
            ring[i].Q = null; offen--;
            int klasse = ev.KlasseP1 > 0 ? ev.KlasseP1 - 1 : einmal ? KEinmal : ev.OneShot ? KSchritt : KStimme;
            bool mehr = klasse != KSchuss && MehrAn && Richtung.KlasseAn(klasse), hall = Nachhall.KlasseAn(klasse);   // Schuesse: keine Kopien (SuperSource)
            if (!mehr && !hall) return;
            var s = SatzFuer(q, klasse);
            if (s == null) { if (mehr) dAbgelehnt[klasse]++; if (hall) dHallAbgelehnt[klasse]++; return; }
            s.Anteil = anteil;
            bool schuss = klasse == KSchuss;
            HallPlanen(s, mix, hall, ev.C1, ev.C2, ev.Balance, ev.VolArg, ev.OneShot, ev.Offset, ev.Pitch, schuss ? 1f : q.PreOcclusionVolume, ev.Start, jetzt, ev.Schuetze);
            Plan(s, mix, hJob, ev.Clip, ev.Vol, ev.Pitch, ev.Start, ev.Offset, UMs(), false, jetzt, mehr, out _);
            Teilen(s);
            // nachhallschuss: der Schuss ist geplant (Start in der Zukunft), SuperSource.PlayScheduled hat volume schon aus dem alten Faktor
            // gesetzt -> sofort nachziehen (BetterSource/SuperSource.UpdateSourceVolume, speed 1); damit spielt er ab dem ersten Abtastwert G
            if (schuss && s.HallAn) q.UpdateSourceVolume();
        }

        /// <summary>nachhallschuss: Gegnerschuss (SEB-Live, Postfix TryStartShot) bzw. Testquelle Gunshots - Ereignis in den Ring, sofort oder beim
        /// naechsten Anwenden planen. q = Schussquelle (nach PlayScheduled), start = geplanter Start (dsp), schuetze = Schluessel der Stimme.
        /// text = Logfeld, wenn sofort geplant (Testquelle).</summary>
        internal static void SchussEreignis(SuperSource q, double start, object schuetze, out string text)
        {
            text = null;
            if (q == null || schuetze == null || !Nachhall.KlasseAn(KSchuss)) return;
            try
            {
                var a1 = q.source1; var a2 = q.source2;
                AudioClip c1 = a1 != null ? a1.clip : null, c2 = a2 != null && a2.enabled ? a2.clip : null;
                if (c1 == null && c2 == null) return;
                NachhallSchussKern.Anregung(q.PreOcclusionVolume, a2 != null && c2 != null ? a2.volume : 0f, q.OcclusionVolumeFactor, q.FadeFactor, out float volArg, out float bal);
                if (!(volArg > 0)) return;
                float pitch = Mathf.Max(0.05f, Mathf.Abs(a1 != null ? a1.pitch : 1f));
                double laenge = Math.Max(c1 != null ? c1.length : 0f, c2 != null ? c2.length : 0f) / pitch;
                int i = ringPos; ringPos = (ringPos + 1) % RingN;
                if (ring[i].Q == null) offen++;
                ring[i] = new Ereignis
                {
                    Q = q, Clip = c1 ?? c2, Vol = volArg, Pitch = pitch, Start = start, Offset = 0, OneShot = true,
                    Ende = Math.Max(start, AudioSettings.dspTime) + laenge,
                    C1 = c1, C2 = c2, Balance = c1 != null && c2 != null ? bal : 1f, VolArg = c1 != null && c2 != null ? volArg : (c1 != null ? volArg * bal : volArg * (1 - bal)),
                    KlasseP1 = KSchuss + 1, Schuetze = schuetze
                };
                Versuchen(i);
                if (ring[i].Q == null && saetze.TryGetValue(q, out var s)) text = HallText(s, true);
                else text = " | reverb voice: planned at the next occlusion update (room path not known yet)";
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>Weitere Stimmen (mehrweg) moeglich: Schalter an, mehr als eine Stimme je Klang, Vorrat &gt; 0.</summary>
        static bool MehrAn => Richtung.ZweitAn && K > 1 && Groesse > 0;

        static Satz SatzFuer(BetterSource q, int klasse)
        {
            if (saetze.TryGetValue(q, out var s)) { s.Klasse = klasse; return s; }
            if (freieSaetze.Count > 0) { s = freieSaetze[freieSaetze.Count - 1]; freieSaetze.RemoveAt(freieSaetze.Count - 1); }
            else if (angelegt < MaxSaetze) { s = new Satz(); angelegt++; }
            else return null;
            s.Leeren();
            s.Q = q; s.Klasse = klasse; s.Aktiv = true; s.Basis = q.OcclusionVolumeFactor;
            saetze[q] = s; aktiv.Add(s);
            return s;
        }

        /// <summary>Buendel, Vergabe, Start der weiteren Stimmen eines Ereignisses. start = Start des Originals (dsp), offset = dessen Startversatz
        /// (Samples), uMs/frueh = Technik-Tor. Gibt false zurueck, wenn keine weitere Stimme spielt; text = Grund/Logfeld (nur Testquelle).</summary>
        static bool Plan(Satz s, List<RoutenEintrag> mix, V3 hJob, AudioClip clip, float vol, float pitch, double start, int offset,
                         double uMs, bool frueh, double jetzt, bool mehr, out string grund)
        {
            grund = null;
            var a = s.A; int kl = s.Klasse;
            if (mehr) dKlaenge[kl]++;
            // nachhallstimme: traegt die Nachhall-Stimme den verschmierten Teil, buendeln die Stimmen nur den gerichteten
            if (!ZweitwegKern.Buendeln(mix, hJob, Richtung.DiffusTrockenKappa(), mehr ? K : 1, a, s.HallAn)) { a.N = 0; SlotsAus(s); grund = "no route carries energy"; return false; }
            ZweitwegKern.Verteilen(a, hJob, uMs, frueh, 0xF);
            int technik = 0;
            for (int b = 1; b < a.N; b++) if (ZweitwegKern.PegelTor(a.PegelDb[b]) > 0 && a.Tor[b] < 1e-9) technik++;
            dTechnik[kl] += technik;
            if (!a.Weitere)
            {
                if (mehr) dKeine[kl]++;
                SlotsAus(s);
                grund = a.N <= 1 ? "all paths from one direction" : technik > 0 ? "later paths less than " + uMs.ToString("F1", CultureInfo.InvariantCulture) + " ms after the first (start uncertainty)" : "further paths below -30 dB of the rest";
                return false;
            }
            // Slot je Buendel (eine Richtung behaelt ihren Slot ueber die Ereignisse), dann Quellen nach Energie
            SlotsZuordnen(s, hJob);
            for (int b = 0; b < MehrAuswahl.KMax; b++) s.SlotNeu[b] = false;
            int maske = 1;
            double d2 = Math.Max(1.0, Abstand2(s.Q));
            for (int b = 1; b < a.N; b++)
            {
                int slot = buendelSlot[b];
                if (slot <= 0 || !(a.S0[b] > 1e-9)) continue;
                if (s.St.Count >= MaxJeSatz) { dAbgelehnt[kl]++; continue; }
                double prio = (double)s.Basis * s.Basis * vol * vol * s.Anteil * a.S0[b] / d2;
                var st = Holen(prio);
                if (st == null) { dAbgelehnt[kl]++; continue; }
                st.Besitzer = s; st.Slot = slot; st.Entzug = false; st.Prio = prio; st.G = 1;
                s.St.Add(st); s.SlotN[slot]++; s.SlotNeu[slot] = true; s.SlotBuendel[slot] = b;
                maske |= 1 << b; dVergeben[kl]++;
            }
            ZweitwegKern.Verteilen(a, hJob, uMs, frueh, maske);
            for (int slot = 1; slot < MehrAuswahl.KMax; slot++)
            {
                int b = s.SlotBuendel[slot];
                s.SZiel[slot] = s.SlotNeu[slot] && b > 0 && b < a.N ? a.S0[b] : 0;
                if (s.SlotNeu[slot]) s.SJetzt[slot] = s.SZiel[slot];   // neues Ereignis: sofort mit seinem Anteil (das Original glitt nie hoch)
            }
            SummeErste(s);
            Teilen(s);   // Original und neue Stimmen auf ihre Anteile, VOR dem Start (PlayScheduled setzt volume = PreOcclusion x Faktor)
            if (maske == 1) { grund = "no free voice"; return false; }
            var q = s.Q;
            double vorlauf = frueh ? 0 : uMs / 1000.0;   // Spielklang: Start mindestens einen Puffer in der Zukunft, der Rest per timeSamples
            for (int i = 0; i < s.St.Count; i++)
            {
                var st = s.St[i];
                if (!s.SlotNeu[st.Slot] || st.S.PlayBackState == BetterSource.EPlayBackState.Playing) continue;
                int b = s.SlotBuendel[st.Slot];
                Einrichten(st, q, pitch);
                st.S.Stop();
                st.S.source1.clip = clip; st.S.source1.volume = vol;
                Stellen(s, st, b, true);
                st.S.SetLowPassFilterParameters(Tiefpass(s, q, b), 1f, true);
                double ziel = start + ZweitwegKern.Versatz(a, b);
                double t = Math.Max(ziel, jetzt + vorlauf);
                long skip = offset + (long)Math.Round((t - ziel) * clip.frequency * pitch);
                if (skip >= clip.samples - 1) { st.S.Stop(); continue; }
                st.S.source1.timeSamples = (int)Math.Max(0, skip);
                st.S.SetOcclusionVolumeFactor((float)(s.Basis * ZweitwegKern.Faktor(s.Anteil * s.SJetzt[st.Slot])));
                st.S.PlayScheduled(t);
                st.S.EnableSpatialization = q.EnableSpatialization;   // PlayScheduled -> RefreshSpatialization(Preset...) (BetterSource.cs:367-373)
                st.S.HrtfIntensity = q.HrtfIntensity;
                st.S.EnableReverb = false;
            }
            s.Vol = vol;
            return true;
        }

        static void SlotsAus(Satz s) { for (int b = 0; b < MehrAuswahl.KMax; b++) { s.SlotNeu[b] = false; s.SZiel[b] = 0; } }

        /// <summary>buendelSlot[b] = Slot des Buendels b: der Slot, dessen Richtung am naechsten liegt (innerhalb der Buendelbreite), sonst ein
        /// freier Slot (ohne laufende Stimme), sonst 0.</summary>
        static void SlotsZuordnen(Satz s, V3 h)
        {
            var a = s.A;
            for (int b = 0; b < MehrAuswahl.KMax; b++) { genommen[b] = false; buendelSlot[b] = 0; }
            double cMin = Math.Cos(ZweitwegKern.BuendelNullGrad * Math.PI / 180);
            for (int b = 1; b < a.N; b++)
            {
                V3 d = RichtungKern.Einheit(a.Aus[b] - h);
                int best = 0; double cBest = cMin;
                for (int slot = 1; slot < MehrAuswahl.KMax; slot++)
                {
                    if (genommen[slot] || s.SlotDir[slot].IstNull || s.SlotN[slot] == 0) continue;
                    double c = V3.Dot(s.SlotDir[slot], d);
                    if (c > cBest) { cBest = c; best = slot; }
                }
                if (best > 0) { genommen[best] = true; buendelSlot[b] = best; }
            }
            for (int b = 1; b < a.N; b++)
            {
                if (buendelSlot[b] > 0) continue;
                for (int slot = 1; slot < MehrAuswahl.KMax; slot++)
                    if (!genommen[slot] && s.SlotN[slot] == 0) { genommen[slot] = true; buendelSlot[b] = slot; s.SlotDir[slot] = RichtungKern.Einheit(a.Aus[b] - h); s.SJetzt[slot] = 0; break; }
            }
            for (int slot = 1; slot < MehrAuswahl.KMax; slot++)
            {
                s.SlotBuendel[slot] = 0;
                for (int b = 1; b < a.N; b++) if (buendelSlot[b] == slot) s.SlotBuendel[slot] = b;
            }
        }

        static void SummeErste(Satz s)
        {
            double rest = 1;
            for (int b = 1; b < MehrAuswahl.KMax; b++) rest -= s.SJetzt[b];
            s.SJetzt[0] = Math.Max(0, rest);
        }

        /// <summary>Testquelle: weitere Stimmen fuer einen geplanten Testklang (Start start, sample-genau, U = 0). Setzt die Lautstaerke der ersten
        /// Stimme schon jetzt (vor q.PlayScheduled). Gibt den Start der ersten Stimme zurueck (spaeter, wenn ein anderer Weg frueher ankommt).</summary>
        internal static double Planen(BetterSource q, AudioClip clip, float vol, double start, out string text)
        {
            text = null;
            // nachhallstimme: der Nachhall des vorigen Testklangs laeuft weiter (dieselbe Stimme wird neu angeregt); nur die Kopien enden
            if (q != null && saetze.TryGetValue(q, out var alt)) { if (alt.Hall != null && alt.Klasse == KTest) StimmenStoppen(alt); else Ende(alt); }
            bool hallAn = Nachhall.KlasseAn(KTest);
            if (!(Richtung.ZweitAn || hallAn) || q == null || clip == null) return start;
            try
            {
                if (!q.EnableSpatialization) { text = " | voices: 1 (more voices only for spatial sources - group Footsteps)"; return start; }
                bool mehr = Richtung.ZweitAn && K > 1;
                if (!Richtung.Routen(q, out var mix, out V3 hJob)) { text = " | voices: 1 (no room path to this source now)"; return start; }
                var s = SatzFuer(q, KTest);
                if (s == null) { text = " | voices: 1 (no free set)"; return start; }
                s.Anteil = Richtung.RaumAnteil(q, out double an, out _) ? an : 1;
                double jetzt = AudioSettings.dspTime;
                // Original: q.PlayScheduled(start [+ Versatz der ersten Stimme]) mit source1.volume = vol (SimpleSource.PlayScheduled) -> Anregung
                // der Nachhall-Stimme mit derselben Lautstaerke, Einsatz auf den geplanten Start bezogen
                HallPlanen(s, mix, hallAn, clip, null, 1f, 1f, true, 0, 1f, vol, start, jetzt);
                bool ok = Plan(s, mix, hJob, clip, vol, 1f, start, 0, 0, true, jetzt, mehr, out string grund);
                Teilen(s);
                text = !mehr ? " | voices: 1 (" + (Richtung.ZweitAn ? "voices per sound = 1" : "extra voices off") + ")" : ok ? Text(s, mix) : " | voices: 1 (" + grund + ")";
                text += HallText(s, true);
                return ok ? start + ZweitwegKern.Versatz(s.A, 0) : start;
            }
            catch (Exception e) { Statistik.Fehler(e); return start; }
        }

        static string Text(Satz s, List<RoutenEintrag> mix)
        {
            var inv = CultureInfo.InvariantCulture; var a = s.A;
            int n = 0; for (int b = 0; b < a.N; b++) if (a.S0[b] > 1e-9) n++;
            var sb = new System.Text.StringBuilder(" | voices: ").Append(n);
            for (int b = 0; b < a.N; b++)
            {
                var e = mix[a.Index[b]];
                sb.Append(b == 0 ? " [" : "; ").Append(b + 1).Append(": opening #").Append(e.N > 0 ? e.Portale[e.N - 1].ToString(inv) : "?")
                  .Append(' ').Append((10 * Math.Log10(Math.Max(1e-12, s.Anteil * a.S0[b] + (b == 0 ? 1 - s.Anteil : 0)))).ToString("F1", inv)).Append(" dB");
                if (b > 0)
                {
                    sb.Append(", ").Append(a.DtMs[b] >= 0 ? "+" : "").Append(a.DtMs[b].ToString("F1", inv)).Append(" ms, ").Append(a.Winkel[b].ToString("F0", inv)).Append(" deg");
                    if (a.S0[b] <= 1e-9) sb.Append(ZweitwegKern.PegelTor(a.PegelDb[b]) > 0 ? ", no free voice" : ", below -30 dB of the rest");
                    else if (a.Tor[b] < 0.999) sb.Append(", gate ").Append(a.Tor[b].ToString("F2", inv));
                }
            }
            sb.Append("] of ").Append(a.N).Append(a.N == 1 ? " bundle" : " bundles").Append(", room path ").Append((s.Anteil * 100).ToString("F0", inv)).Append(" % of the energy, start +")
              .Append((ZweitwegKern.Versatz(a, 0) * 1000).ToString("F1", inv)).Append(" ms");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- je Bild

        /// <summary>Je Bild (aus Testquelle.Tick, laeuft immer): Buendel neu, Anteile gleiten, Lautstaerken, Tiefpass und Ort; Entzug, Freigabe,
        /// Raid-Ende.</summary>
        internal static void Bild()
        {
            if (aktiv.Count == 0 && vorrat.Count == 0 && hallVorrat.Count == 0 && offen == 0) return;
            long t0 = Laufzeit.Jetzt;
            try
            {
                var ba = MonoBehaviourSingleton<BetterAudio>.Instantiated ? MonoBehaviourSingleton<BetterAudio>.Instance : null;
                if ((vorrat.Count > 0 || hallVorrat.Count > 0) && (ba == null || !ReferenceEquals(ba, vorratVon))) { Vergessen(); return; }   // neuer Raid: alte Quellen nicht anfassen
                if (!(Richtung.ZweitAn || Nachhall.An) || !Comfort.Common.Singleton<EFT.GameWorld>.Instantiated) { AllesFrei(); return; }
                if (offen > 0)
                {
                    double jetzt = AudioSettings.dspTime;
                    for (int i = 0; i < RingN; i++) if (ring[i].Q != null && jetzt >= ring[i].Ende - 0.02) { ring[i].Q = null; offen--; dVerpasst++; }
                }
                int stimmen = 0, hallN = 0;
                for (int i = aktiv.Count - 1; i >= 0; i--)
                {
                    var s = aktiv[i];
                    if (!BildSatz(s)) { Ende(s); continue; }
                    stimmen += s.St.Count;
                    if (s.Hall != null) hallN++;
                }
                if (stimmen > dSpitze) dSpitze = stimmen;
                KanaeleBild();
                hallN = 0; for (int j = 0; j < hallVorrat.Count; j++) if (hallVorrat[j].Besitzer != null || hallVorrat[j].Schuetze != null) hallN++;
                if (hallN > dHallSpitze) dHallSpitze = hallN;
                dBilder++;
            }
            catch (Exception e) { Statistik.Fehler(e); }
            finally { long dt = Laufzeit.Jetzt - t0; dTicks += dt; if (dt > dMaxTicks) dMaxTicks = dt; }
        }

        /// <summary>false = Satz zu Ende (keine Stimme mehr).</summary>
        static bool BildSatz(Satz s)
        {
            double jetzt = Time.realtimeSinceStartup, dt = double.IsNaN(s.Zeit) ? 0 : Math.Max(0, jetzt - s.Zeit);
            s.Zeit = jetzt;
            for (int i = s.St.Count - 1; i >= 0; i--)
            {
                var st = s.St[i];
                bool fertig = st.S == null || st.S.PlayBackState != BetterSource.EPlayBackState.Playing
                              || (st.Entzug && st.G < 1e-3 && st.S.source1.volume < 1e-3f);
                if (!fertig) continue;
                s.SlotN[st.Slot]--; if (s.SlotN[st.Slot] <= 0) { s.SlotN[st.Slot] = 0; s.SlotNeu[st.Slot] = false; }
                Frei(st); s.St.RemoveAt(i);
            }
            var q = s.Q;
            bool lebt = q != null;
            HallBild(s, lebt);   // nachhallstimme: Ende, Selbsttest, Ort, Farbe, Zeitstruktur
            if (s.St.Count == 0 && s.Hall == null) return false;
            var a = s.A;
            bool gilt = false; V3 hJob = default;
            if (lebt && s.Klasse != KEinmal)
            {
                // Einmal-Klaenge: das Spiel wendet sie EINMAL an und gibt den Rechner zurueck - Buendel der Planung gelten fuer den ganzen Klang
                // (wie Richtung: Austritt der Anwendung, Richtung vom heutigen Ohr)
                gilt = Richtung.Routen(q, out var mix, out hJob) && ZweitwegKern.Buendeln(mix, hJob, Richtung.DiffusTrockenKappa(), Richtung.ZweitAn ? K : 1, a, s.HallAn);
                if (gilt)
                {
                    SlotsFolgen(s, hJob);
                    int maske = 1;
                    for (int slot = 1; slot < MehrAuswahl.KMax; slot++) if (Abgedeckt(s, slot)) maske |= 1 << s.SlotBuendel[slot];
                    ZweitwegKern.Verteilen(a, hJob, s.Klasse == KTest ? 0 : UMs(), s.Klasse == KTest, maske);
                    if (Richtung.RaumAnteil(q, out double an, out _)) s.Anteil = an;
                }
                for (int slot = 1; slot < MehrAuswahl.KMax; slot++)
                    s.SZiel[slot] = gilt && Abgedeckt(s, slot) ? a.S0[s.SlotBuendel[slot]] : 0;
            }
            else if (lebt) for (int slot = 1; slot < MehrAuswahl.KMax; slot++) if (!Abgedeckt(s, slot)) s.SZiel[slot] = 0;
            double f = 1 - Math.Exp(-dt / GleitS), fe = 1 - Math.Exp(-dt / EntzugS);
            for (int slot = 1; slot < MehrAuswahl.KMax; slot++) s.SJetzt[slot] += (s.SZiel[slot] - s.SJetzt[slot]) * f;
            SummeErste(s);
            for (int i = 0; i < s.St.Count; i++) { var st = s.St[i]; if (st.Entzug) st.G -= st.G * fe; }
            if (lebt) Teilen(s);
            else for (int i = 0; i < s.St.Count; i++) { var st = s.St[i]; if (st.Entzug) st.S.SetOcclusionVolumeFactor((float)(st.S.OcclusionVolumeFactor * (1 - fe))); }
            if (!lebt) return true;   // Original freigegeben: die Stimmen spielen mit dem letzten Wert zu Ende
            double d2 = Math.Max(1.0, Abstand2(q));
            for (int i = 0; i < s.St.Count; i++)
            {
                var st = s.St[i];
                int b = s.SlotBuendel[st.Slot];
                st.Prio = (double)s.Basis * s.Basis * s.Vol * s.Vol * s.Anteil * s.SJetzt[st.Slot] * st.G / d2;
                st.S.source1.maxDistance = q.source1.maxDistance;   // Abstandskurve wie die erste Stimme (das Spiel skaliert sie mit der Verdeckung)
                if (b > 0 && b < a.N && (gilt || s.Klasse == KEinmal))
                {
                    if (gilt) st.S.SetLowPassFilterParameters(Tiefpass(s, q, b), 1f, false);
                    Stellen(s, st, b, false);
                }
            }
            return true;
        }

        static bool Abgedeckt(Satz s, int slot) => s.SlotNeu[slot] && s.SlotN[slot] > 0 && s.SlotBuendel[slot] > 0 && s.SlotBuendel[slot] < s.A.N;

        /// <summary>Je Bild: jeder Slot folgt dem Buendel, das seiner Richtung am naechsten liegt (der Rang zweier Buendel kann wechseln; die
        /// Stimmen bleiben an ihrer Oeffnung). Ohne Buendel innerhalb der Buendelbreite: Slot 0 -> blendet aus.</summary>
        static void SlotsFolgen(Satz s, V3 h)
        {
            var a = s.A;
            for (int b = 0; b < MehrAuswahl.KMax; b++) genommen[b] = false;
            double cMin = Math.Cos(ZweitwegKern.BuendelNullGrad * Math.PI / 180);
            for (int slot = 1; slot < MehrAuswahl.KMax; slot++)
            {
                if (s.SlotN[slot] == 0) { s.SlotBuendel[slot] = 0; continue; }
                int best = 0; double cBest = cMin;
                for (int b = 1; b < a.N; b++)
                {
                    if (genommen[b]) continue;
                    double c = s.SlotDir[slot].IstNull ? -2 : V3.Dot(s.SlotDir[slot], RichtungKern.Einheit(a.Aus[b] - h));
                    if (c > cBest) { cBest = c; best = b; }
                }
                s.SlotBuendel[slot] = best; if (best > 0) { genommen[best] = true; s.SlotDir[slot] = RichtungKern.Einheit(a.Aus[best] - h); }
            }
        }

        /// <summary>Lautstaerken: Original Grundwert x sqrt(1 - Raumanteil x Summe der abgedeckten Slots), Stimme Grundwert x sqrt(Raumanteil x s)
        /// x Entzug-Blende. Der Wandanteil (durch die Wand zwischen den Raeumen) bleibt beim Original.</summary>
        static void Teilen(Satz s)
        {
            var q = s.Q;
            if (q == null || float.IsNaN(s.Basis)) return;
            // nachhallstimme: mit Nachhall-Stimme gilt der Grundwert OHNE Ereignis-/Sprachabzug (schon im Grundwert enthaltener Abzug des letzten
            // Anwendens zurueck; ab dem naechsten Anwenden rechnet Physik ihn gar nicht mehr - NachhallWirkt) und die Slot-Anteile sind Anteile
            // am gerichteten Raumweg. Bilanz: NachhallKern.Teile.
            bool hall = s.HallAn && s.Hall != null && s.Hall.S != null;
            double voll = hall ? Math.Min(1.0, s.Basis * NachhallKern.Zuruecknehmen(Physik.AbzugDb(q))) : s.Basis;
            double weitereS = 0;
            for (int slot = 1; slot < MehrAuswahl.KMax; slot++) if (s.SlotN[slot] > 0) weitereS += s.SJetzt[slot];
            NachhallKern.Teile(s.Anteil, s.HallR0, hall, weitereS, out double erste, out double nach, out double jeSlot);
            q.SetOcclusionVolumeFactor((float)(voll * ZweitwegKern.Faktor(erste)));
            s.Geschrieben = q.OcclusionVolumeFactor;
            for (int i = 0; i < s.St.Count; i++)
            {
                var st = s.St[i];
                if (st.S != null) st.S.SetOcclusionVolumeFactor((float)(voll * ZweitwegKern.Faktor(jeSlot * s.SJetzt[st.Slot]) * st.G));
            }
            // die Nachhall-Stimme nur, solange sie den laufenden Klang traegt; sonst klingt sie mit ihrem letzten Wert aus
            if (hall) s.Hall.S.SetOcclusionVolumeFactor((float)(voll * ZweitwegKern.Faktor(nach)));
        }

        /// <summary>Tiefpass der Stimme (Buendel k): eigene Bandkurve (Wege.FitLp wie die erste Stimme), dazu die Luft wie bei der ersten Stimme
        /// (SebAkustik-Filter: Luft.Grenzfrequenz(echter Abstand), solange dort aktiv). Wald/Hoerbarkeit je Band: nicht nachgebildet.</summary>
        static float Tiefpass(Satz s, BetterSource q, int k)
        {
            var a = s.A;
            if (k <= 0 || k >= a.N || !(a.S0[k] > 0)) return Physik.TiefpassMin.Value;
            double d0 = a.G0 - 10 * Math.Log10(a.S0[k]);
            double m1 = (a.S1[k] > 0 ? a.G1 - 10 * Math.Log10(a.S1[k]) : d0 + 60) - d0, m2 = (a.S2[k] > 0 ? a.G2 - 10 * Math.Log10(a.S2[k]) : d0 + 60) - d0;
            float lp = Wege.FitLp(m1, m2);
            if (float.IsNaN(lp) || float.IsInfinity(lp)) lp = 22000f;
            if (SebAkustik.LuftOk && SebAkustik.LuftAktiv != null && SebAkustik.LuftAktiv.Value && !SebAkustik.IstSebQuelle(q))
                lp = Mathf.Min(lp, Luft.Grenzfrequenz(Farbe.Entfernung(q)));
            return lp;
        }

        static double Abstand2(BetterSource q)
        {
            var ba = MonoBehaviourSingleton<BetterAudio>.Instance;
            var lt = ba != null ? ba.ListenerTransform : null;
            return lt == null || q == null ? 1 : (Richtung.Echt(q) - lt.position).sqrMagnitude;
        }

        static void Stellen(Satz s, Stimme st, int k, bool sofort)
        {
            var ba = MonoBehaviourSingleton<BetterAudio>.Instance;
            var lt = ba != null ? ba.ListenerTransform : null;
            if (lt == null || st.Traeger == null || s.Q == null) return;
            Vector3 hv = lt.position, qv = Richtung.Echt(s.Q);
            V3 h = new V3(hv.x, hv.y, hv.z), qq = new V3(qv.x, qv.y, qv.z);
            if (!RichtungKern.Ziel(h, qq, s.A.Aus[k], 1.0, out V3 ziel, out _)) return;
            // Richtung je Stimme = die des Slots (alle Stimmen eines Slots an derselben Stelle); neu gestartete Stimme sofort
            V3 alt = st.Traeger.transform.position - lt.position is Vector3 v && v.sqrMagnitude > 1e-6f ? RichtungKern.Einheit(new V3(v.x, v.y, v.z)) : default;
            V3 dir = sofort || alt.IstNull ? ziel : RichtungKern.Nachfuehren(alt, ziel, Time.deltaTime, RichtungS);
            V3 p = RichtungKern.Position(h, dir, (qq - h).Laenge);
            st.Traeger.transform.position = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
        }

        // ---------------------------------------------------------------- nachhallstimme (17.11.2026)

        /// <summary>Physik.Trocken: traegt fuer diese Quelle gerade eine Nachhall-Stimme den verschmierten Teil? Dann zieht Physik keinen
        /// Ereignis-/Sprachabzug ab (EINE Stelle entscheidet; Schalter, Klasse, Vorrat und Selbsttest stecken in HallAn).</summary>
        internal static bool NachhallWirkt(BetterSource q)
        {
            if (q == null || saetze.Count == 0 || !Nachhall.An) return false;
            var s = Laufend(q);
            return s != null && s.HallAn && s.Hall != null && s.Hall.S != null;
        }

        static bool IstHall(BetterSource q)
        {
            for (int i = 0; i < hallVorrat.Count; i++) if (ReferenceEquals(hallVorrat[i].S, q)) return true;
            return false;
        }

        /// <summary>Freie Nachhall-Quelle (bei Bedarf geliehen bis zur Vorratsgroesse, mit Hallfilter). Kein Entzug: ist alles belegt, behaelt
        /// der neue Klang den alten Ersatz (Ereignis-/Sprachabzug) - eine laufende Nachhallfahne abzuschneiden waere hoerbar.</summary>
        static Stimme HallHolen(double prio)
        {
            int g = Nachhall.Groesse;
            for (int i = 0; i < hallVorrat.Count && i < g; i++) if (hallVorrat[i].Besitzer == null && hallVorrat[i].Schuetze == null && hallVorrat[i].S != null && hallVorrat[i].F != null) return hallVorrat[i];
            if (hallVorrat.Count >= g)
            {
                // nachhallschuss: Vergabe nach Energie - die Stimme mit der kleinsten JETZIGEN Energie (abgeklungene Fahne) geht an den lauteren Klang
                Stimme schwach = null; double eMin = double.PositiveInfinity; float tJ = Time.realtimeSinceStartup;
                for (int i = 0; i < hallVorrat.Count && i < g; i++)
                {
                    var v = hallVorrat[i];
                    if (v.S == null || v.F == null) continue;
                    double e = NachhallSchussKern.Jetzt(v.PrioE, tJ - v.PrioZeit, v.TMax);
                    if (e < eMin) { eMin = e; schwach = v; }
                }
                if (schwach == null || !NachhallSchussKern.Nehmen(prio, eMin)) return null;
                if (schwach.Schuetze != null)
                {
                    if (schuetzen.TryGetValue(schwach.Schuetze, out var x) && ReferenceEquals(x, schwach)) schuetzen.Remove(schwach.Schuetze);
                    KanalFrei(schwach);
                }
                if (schwach.Besitzer != null) { var b = schwach.Besitzer; schwach.Besitzer = null; b.Hall = null; b.HallAn = false; if (b.Q != null && !float.IsNaN(b.Basis)) Teilen(b); }
                try { if (schwach.S.PlayBackState == BetterSource.EPlayBackState.Playing) schwach.S.Stop(); } catch (Exception e) { Statistik.Fehler(e); }
                schwach.PrioE = 0; if (schwach.Warte != null) schwach.Warte.Leeren();
                dHallEntzogen++;
                return schwach;
            }
            var ba = MonoBehaviourSingleton<BetterAudio>.Instantiated ? MonoBehaviourSingleton<BetterAudio>.Instance : null;
            if (ba == null) return null;
            if ((vorrat.Count > 0 || hallVorrat.Count > 0) && !ReferenceEquals(ba, vorratVon)) return null;
            var src = ba.GetSource(BetterAudio.AudioSourceGroupType.Character);
            if (src == null || src.source1 == null) return null;
            vorratVon = ba;
            var t = new GameObject("Materialklang reverb voice " + hallVorrat.Count);
            UnityEngine.Object.DontDestroyOnLoad(t);
            src.SetParent(t.transform, false);
            src.LocalPosition = Vector3.zero;
            var f = src.gameObject.AddComponent<NachhallFilter>();   // hinter den Filtern des Prefabs (Tiefpass/EQ der Quelle zuerst)
            f.Netz = new NachhallNetz(AudioSettings.outputSampleRate);
            f.Aktiv = true;
            var st = new Stimme { S = src, Traeger = t, HallVorher = src.EnableReverb, F = f };
            src.EnableReverb = false;
            hallVorrat.Add(st);
            return st;
        }

        /// <summary>Nachhall-Stimme fuer ein Ereignis planen bzw. die laufende neu anregen. Setzt s.HallAn (traegt diese Stimme den Klang?).
        /// basisStart = Start des Originals (dsp), jetzt = dspTime jetzt; c1/c2/balance/volArg/oneShot wie SimpleSource.Play; preOcc = Lautstaerke
        /// vor der Verdeckung (PreOcclusionVolume des Originals bzw. die geplante Lautstaerke der Testquelle).</summary>
        static void HallPlanen(Satz s, List<RoutenEintrag> mix, bool klasseAn, AudioClip c1, AudioClip c2, float balance, float volArg, bool oneShot,
                               int offset, float pitch, float preOcc, double basisStart, double jetzt, object schuetze = null)
        {
            s.HallAn = false; s.HallGrund = null;
            if (!klasseAn) return;
            int kl = s.Klasse;
            var q = s.Q;
            if (q == null) return;
            if (!NachhallKern.Waehlen(mix, Nachhall.MaxT, s.HW)) { s.HallGrund = "no time structure on the room path"; dHallOhne[kl]++; return; }
            if (s.Anteil * s.HW.R0 < NachhallKern.MinEnergie) { s.HallGrund = "diffuse part below -30 dB"; dHallOhne[kl]++; return; }
            if (!oneShot && offset > 0) { s.HallGrund = "voice clip resumed mid-way"; dHallOhne[kl]++; return; }   // PlayOneShot kann keinen Versatz
            AudioClip lang = c1 != null && c2 != null ? (c1.length >= c2.length ? c1 : c2) : (c1 ?? c2);
            if (lang == null) return;
            // nachhallschuss/vorverzug: der Start kann (Schuesse: Schall-Laufzeit) weit voraus liegen - die Anregung wartet bis dahin (VorverzugKern);
            // ueber MaxVorausS nicht
            if (basisStart - jetzt > MaxVorausS) { s.HallGrund = "start more than " + MaxVorausS.ToString("F0", CultureInfo.InvariantCulture) + " s ahead"; dHallFern++; return; }
            double prio = NachhallSchussKern.Prio(s.Basis * NachhallKern.Zuruecknehmen(Physik.AbzugDb(q)), s.Anteil * s.HW.R0, preOcc * volArg, Abstand2(q));
            var st = schuetze != null ? (schuetzen.TryGetValue(schuetze, out var sch) ? sch : null) : s.Hall;
            if (st != null && schuetze != null && !ReferenceEquals(st.Schuetze, schuetze)) st = null;
            bool neu = st == null || st.S == null || st.S.PlayBackState != BetterSource.EPlayBackState.Playing;
            if (neu)
            {
                if (s.Hall != null) HallFrei(s);
                if (schuetze != null && st != null) { schuetzen.Remove(schuetze); KanalFrei(st); }
                st = HallHolen(prio);
                if (st == null) { s.HallGrund = "no free reverb voice (all carry louder sounds)"; dHallAbgelehnt[kl]++; return; }
                if (schuetze != null) { st.Schuetze = schuetze; schuetzen[schuetze] = st; } else st.Besitzer = s;
                st.Ende = 0;
            }
            else if (schuetze != null && !ReferenceEquals(s.Hall, st)) { if (s.Hall != null) HallFrei(s); }
            s.Hall = st;
            var src = st.S;
            s.HallR0 = s.HW.R0; s.HallR1 = s.HW.R1; s.HallR2 = s.HW.R2;
            // vorverzug: Vorverzoegerung der Stimme bleibt fest (nur eine neue Stimme - leeres Netz - setzt sie); der Einsatz des Ereignisses
            // steckt in seiner Feuerzeit (VorverzugKern)
            if (neu) { st.Vor = VorverzugKern.Dv(s.HW.EinsatzS, NachhallNetz.MaxVorS); if (st.Warte != null) st.Warte.Leeren(); }
            s.HallVor = st.Vor;
            st.F.Netz.Setzen(s.HW.T0, s.HW.T2, s.HW.TauAuf, st.Vor, Nachhall.PegelLinear, neu);
            if (neu)
            {
                Einrichten(st, q, pitch);
                KurveSetzen(st, q, kl == KSchuss);
                src.Stop();
                src.source1.clip = Nachhall.Stille(); src.source1.loop = true; src.source1.volume = Mathf.Clamp01(preOcc);
                src.source1.spatializePostEffects = true;
                s.HallAn = true;   // fuer Teilen vor dem Start (PlayScheduled setzt volume = PreOcclusion x Faktor)
                HallStellen(s, st, true);
                Teilen(s);
                q.UpdateSourceVolume();   // Original sofort auf seinen Anteil (BetterSource.UpdateSourceVolume, speed 1 = ohne das Lerp 10 dt)
                src.PlayScheduled(jetzt);
                // nachhallschuss: Schuesse spielt das Spiel als Stereo ohne HRTF (Preset Gunshots SteamSpatialize = 0). Der Nachhall kommt aus
                // der Oeffnung -> raeumlich ueber Metas Spatializer der Vorrats-Quelle (dafuer laeuft das Filter VOR ihm; ob es vor Unitys
                // Stereo-Panning laeuft, ist unbelegt - dann waere die Mono-Mitte die Folge). Andere Klassen: wie das Original.
                src.EnableSpatialization = kl == KSchuss ? src.Spatializer is Audio.SpatialSystem.MetaSpatialAudioSource : q.EnableSpatialization;
                if (kl != KSchuss) src.HrtfIntensity = q.HrtfIntensity;
                src.EnableReverb = false;
                src.source1.spatializePostEffects = true;
            }
            if (neu || !ReferenceEquals(st.Besitzer, s)) { s.HallStart = Time.realtimeSinceStartup; s.HallBloecke0 = st.F.Netz.Bloecke; }
            src.SetLowPassFilterParameters(HallTiefpass(s, q), 1f, neu);
            // Anregung wie SimpleSource.Play: One-Shot je Clip mit volume x balance; Hauptclip (Sprache) mit 1 (PreOcclusionVolume traegt volume).
            // vorverzug: zu ihrer eigenen Feuerzeit (Start + Einsatz des Ereignisses - Vorverzoegerung der Stimme)
            AudioClip e1 = oneShot && c1 != null && c2 != null ? c1 : lang, e2 = oneShot && c1 != null && c2 != null ? c2 : null;
            float w1 = !oneShot ? 1f : c1 != null && c2 != null ? volArg * balance : volArg, w2 = e2 != null ? volArg * (1f - balance) : 0f;
            double feuer = VorverzugKern.Feuerzeit(basisStart, s.HW.EinsatzS, st.Vor);
            if (st.Warte == null) st.Warte = new AnregungsPuffer<AudioClip>(16);
            if (VorverzugKern.Faellig(feuer, jetzt, Time.unscaledDeltaTime)) Feuern(st, e1, e2, w1, w2, pitch);
            else if (!st.Warte.Dazu(e1, e2, w1, w2, pitch, feuer)) { dHallVoll++; Feuern(st, e1, e2, w1, w2, pitch); }
            else dHallGewartet++;
            double ende = Math.Max(feuer, jetzt) + st.Vor + lang.length / Math.Max(0.05f, pitch) + NachhallKern.Schwanz(s.HW);
            if (ende > st.Ende) { st.Ende = ende; src.SetScheduledEndTime(ende); }
            s.HallEnde = st.Ende;
            st.PrioE = Math.Max(prio, NachhallSchussKern.Jetzt(st.PrioE, Time.realtimeSinceStartup - st.PrioZeit, st.TMax));   // Anregungen summieren
            st.PrioZeit = Time.realtimeSinceStartup; st.TMax = Math.Max(s.HW.T0, Math.Max(s.HW.T1, s.HW.T2));
            s.HallAn = true;
            dHall[kl]++;
        }

        /// <summary>vorverzug: eine Anregung jetzt auf die Stimme legen (Tonhoehe des Ereignisses, PlayOneShot je Clip).</summary>
        static void Feuern(Stimme st, AudioClip c1, AudioClip c2, float v1, float v2, float pitch)
        {
            var src = st.S;
            if (src == null || src.source1 == null) return;
            src.SetPitch(pitch);
            if (c1 != null && v1 > 0) src.source1.PlayOneShot(c1, v1);
            if (c2 != null && v2 > 0) src.source1.PlayOneShot(c2, v2);
            dHallAnregungen++;
        }

        /// <summary>vorverzug: Schuesse warten bis zu ihrem Start; mehr Vorlauf (SEB: Schall-Laufzeit, 1 km ~ 2,9 s) wird nicht geplant.</summary>
        const double MaxVorausS = 3.0;

        /// <summary>nachhallschuss: Abstandskurve. Schuss: die der Schussquelle (Gunshots-Preset bzw. SEB/Abschnitt 13 - eine andere Kurve gaebe dem
        /// Nachhall eine andere Entfernungsdaempfung als dem trockenen Schuss); sonst die eigene des Pools (gesichert beim Anlegen).</summary>
        static void KurveSetzen(Stimme st, BetterSource q, bool schuss)
        {
            var a = st.S.source1;
            if (st.KurveVorher == null) { st.KurveVorher = a.GetCustomCurve(AudioSourceCurveType.CustomRolloff); st.ModusVorher = a.rolloffMode; }
            if (schuss && q.source1 != null)
            {
                a.rolloffMode = q.source1.rolloffMode;
                if (q.source1.rolloffMode == AudioRolloffMode.Custom) a.SetCustomCurve(AudioSourceCurveType.CustomRolloff, q.source1.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
                a.minDistance = q.source1.minDistance;
                st.KurveFremd = true;
            }
            else KurveZurueck(st);
        }

        static void KurveZurueck(Stimme st)
        {
            if (!st.KurveFremd || st.S == null || st.KurveVorher == null) return;
            st.S.source1.rolloffMode = st.ModusVorher;
            st.S.source1.SetCustomCurve(AudioSourceCurveType.CustomRolloff, st.KurveVorher);
            st.KurveFremd = false;
        }

        /// <summary>nachhallschuss: Stimme eines Schuetzen aufgeben (Fahne zu Ende oder entzogen): Saetze, die sie nutzen, loesen.</summary>
        static void KanalFrei(Stimme st)
        {
            st.Schuetze = null;
            for (int i = 0; i < aktiv.Count; i++)
            {
                var s = aktiv[i];
                if (!ReferenceEquals(s.Hall, st)) continue;
                s.Hall = null; s.HallAn = false;
                if (s.Q != null && !float.IsNaN(s.Basis)) Teilen(s);
            }
        }

        /// <summary>Je Bild: Stimmen der Schuetzen, deren Fahne zu Ende ist, an den Hall-Vorrat zurueck.</summary>
        static void KanaeleBild()
        {
            double jetztD = AudioSettings.dspTime; float bild = Time.unscaledDeltaTime;
            for (int i = 0; i < hallVorrat.Count; i++)
            {
                var st = hallVorrat[i];
                // vorverzug: wartende Anregungen dieser Stimme feuern (nur solange sie spielt; sonst verwerfen)
                if (st.Warte != null && st.Warte.N > 0)
                {
                    if (st.S == null || st.S.PlayBackState != BetterSource.EPlayBackState.Playing || (st.Besitzer == null && st.Schuetze == null)) st.Warte.Leeren();
                    else
                        while (st.Warte.Faellig(jetztD, bild, out var a, out var b, out float v1, out float v2, out float pt, out double fz))
                        {
                            Feuern(st, a, b, v1, v2, pt);
                            double f = Math.Abs(jetztD - fz); if (f > dHallFehlerMax) dHallFehlerMax = f;
                        }
                }
                if (st.Schuetze == null) continue;
                if (st.S != null && st.S.PlayBackState == BetterSource.EPlayBackState.Playing) continue;
                if (schuetzen.TryGetValue(st.Schuetze, out var x) && ReferenceEquals(x, st)) schuetzen.Remove(st.Schuetze);
                KanalFrei(st);
                try { if (st.S != null) st.S.source1.loop = false; } catch (Exception e) { Statistik.Fehler(e); }
            }
        }

        static float HallTiefpass(Satz s, BetterSource q)
        {
            float lp = Wege.FitLp(s.HW.M1, s.HW.M2);
            if (float.IsNaN(lp) || float.IsInfinity(lp)) lp = 22000f;
            if (SebAkustik.LuftOk && SebAkustik.LuftAktiv != null && SebAkustik.LuftAktiv.Value && !SebAkustik.IstSebQuelle(q))
                lp = Mathf.Min(lp, Luft.Grenzfrequenz(Farbe.Entfernung(q)));   // wie die Kopien: Luft der echten Entfernung
            return lp;
        }

        /// <summary>Je Bild: Ende der Stimme, Selbsttest des Filters, Zeitstruktur/Farbe/Anteil nachfuehren (laufende Quelle), Ort.</summary>
        static void HallBild(Satz s, bool lebt)
        {
            var st = s.Hall;
            if (st == null) return;
            if (st.S == null || st.F == null || st.S.PlayBackState != BetterSource.EPlayBackState.Playing || !Nachhall.An) { HallFrei(s); return; }
            // nachhallschuss: der Schuss (eigene Quelle je Schuss) ist zu Ende -> Satz loesen, die Stimme des Schuetzen bleibt
            if (st.Schuetze != null && (!lebt || s.Q == null || s.Q.PlayBackState != BetterSource.EPlayBackState.Playing)) { HallFrei(s); return; }
            // Selbsttest: laeuft das Filter nicht, spielte die Quelle sonst den Clip trocken -> fuer diesen Raid aus (Rueckfall: Abzug).
            // Nur Merker + diese Stimme frei (Bild laeuft gerade ueber 'aktiv'); die anderen Saetze sehen Nachhall.An = false im selben Bild.
            // Eine einzelne stumme Stimme kann auch von Unity virtualisiert sein (zu leise) - darum erst nach 3 Fehlschlaegen ohne einen einzigen
            // Erfolg im Raid; nach einem Erfolg (Filter laeuft nachweislich) keine Abschaltung mehr.
            if (s.HallBloecke0 >= 0 && Time.realtimeSinceStartup - s.HallStart > 0.5f)
            {
                bool lief = st.F.Netz.Bloecke > s.HallBloecke0;
                s.HallBloecke0 = -1;   // je Stimme einmal geprueft
                if (lief) { if (!Nachhall.Bewiesen) Plugin.Log.LogInfo($"[Diagnostics] Reverb voice: audio filter runs ({st.F.Netz.Kanaele} channels, {st.F.Netz.Fs} Hz, spatialize {st.S.source1.spatialize}, post effects {st.S.source1.spatializePostEffects})."); Nachhall.Bewiesen = true; }
                else if (!Nachhall.Bewiesen && ++Nachhall.Fehlversuche >= 3)
                {
                    Nachhall.Kaputt = true;
                    Nachhall.KaputtGrund = "audio filter of the reverb voice did not run (no OnAudioFilterRead block in 0.5 s, 3 voices)";
                    Plugin.Log.LogWarning("[Diagnostics] Reverb voice: " + Nachhall.KaputtGrund + " -> reverb voices off for this raid, sounds fall back to the event/speech cut.");
                    HallFrei(s);
                    return;
                }
            }
            st.S.source1.spatializePostEffects = true;   // RefreshSourceSettings des Spiels setzt false (BetterAudio.cs:343)
            var q = s.Q;
            if (!lebt || q == null) return;   // Original freigegeben: klingt mit dem letzten Wert und Ort aus
            st.S.source1.maxDistance = q.source1.maxDistance;
            if (s.HallAn && s.Klasse != KEinmal && Richtung.Routen(q, out var mix, out _) && NachhallKern.Waehlen(mix, Nachhall.MaxT, s.HW))
            {
                s.HallR0 = s.HW.R0; s.HallR1 = s.HW.R1; s.HallR2 = s.HW.R2;
                st.F.Netz.Setzen(s.HW.T0, s.HW.T2, s.HW.TauAuf, st.Vor, Nachhall.PegelLinear, false);   // vorverzug: Vorverzoegerung der Stimme bleibt fest
                st.S.SetLowPassFilterParameters(HallTiefpass(s, q), 1f, false);
            }
            HallStellen(s, st, false);
        }

        static void HallStellen(Satz s, Stimme st, bool sofort)
        {
            var ba = MonoBehaviourSingleton<BetterAudio>.Instance;
            var lt = ba != null ? ba.ListenerTransform : null;
            if (lt == null || st.Traeger == null || s.Q == null || !s.HW.Hat) return;
            Vector3 hv = lt.position, qv = Richtung.Echt(s.Q);
            V3 h = new V3(hv.x, hv.y, hv.z), qq = new V3(qv.x, qv.y, qv.z);
            if (!RichtungKern.Ziel(h, qq, s.HW.Aus, 1.0, out V3 ziel, out _)) return;
            V3 alt = st.Traeger.transform.position - lt.position is Vector3 v && v.sqrMagnitude > 1e-6f ? RichtungKern.Einheit(new V3(v.x, v.y, v.z)) : default;
            V3 dir = sofort || alt.IstNull ? ziel : RichtungKern.Nachfuehren(alt, ziel, Time.deltaTime, RichtungS);
            V3 p = RichtungKern.Position(h, dir, (qq - h).Laenge);
            st.Traeger.transform.position = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
        }

        static void HallFrei(Satz s)
        {
            var st = s.Hall;
            s.Hall = null; s.HallAn = false; s.HallEnde = 0;
            if (st == null) return;
            // nachhallschuss: Stimme eines Schuetzen gehoert nicht diesem Satz (dem Schuss) - nur loesen; ihre Fahne klingt aus (KanaeleBild)
            if (st.Schuetze != null) { if (s.Q != null && !float.IsNaN(s.Basis)) Teilen(s); return; }
            try
            {
                if (st.S != null)
                {
                    if (st.S.PlayBackState == BetterSource.EPlayBackState.Playing) st.S.Stop();
                    st.S.source1.loop = false;
                    if (st.Warte != null) st.Warte.Leeren();   // vorverzug
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
            st.Besitzer = null;
            if (s.Q != null && !float.IsNaN(s.Basis)) Teilen(s);   // Original wieder mit dem verschmierten Teil (Physik zieht ab dem naechsten Anwenden wieder ab)
        }

        /// <summary>Logfeld " | reverb voice (...): opening #N -X dB, T60 a/b/c s, rise r ms, onset o ms" bzw. Grund, warum keine.</summary>
        static string HallText(Satz s, bool mitGrund)
        {
            var inv = CultureInfo.InvariantCulture;
            if (!(s.HallAn && s.Hall != null))
            {
                if (s.Hall != null) return " | reverb voice: tail of an earlier sound";
                return mitGrund && s.HallGrund != null ? " | reverb voice: none (" + s.HallGrund + ")" : mitGrund && !Nachhall.KlasseAn(s.Klasse) ? " | reverb voice: off" + (Nachhall.Kaputt ? " (" + Nachhall.KaputtGrund + ")" : "") : null;
            }
            var w = s.HW;
            string oeff = "?";
            if (Richtung.Routen(s.Q, out var mix, out _) && w.Index >= 0 && w.Index < mix.Count) { var e = mix[w.Index]; if (e.N > 0) oeff = e.Portale[e.N - 1].ToString(inv); }
            var sb = new System.Text.StringBuilder(" | reverb voice (").Append(KName[s.Klasse]).Append("): opening #").Append(oeff).Append(' ')
                .Append((10 * Math.Log10(Math.Max(1e-12, s.Anteil * s.HallR0))).ToString("F1", inv)).Append(" dB, T60 ")
                .Append(w.T0.ToString("F2", inv)).Append('/').Append(w.T1.ToString("F2", inv)).Append('/').Append(w.T2.ToString("F2", inv)).Append(" s");
            if (w.T0Roh > w.T0 + 1e-3 || w.T1Roh > w.T1 + 1e-3 || w.T2Roh > w.T2 + 1e-3)
                sb.Append(" (capped, room model ").Append(w.T0Roh.ToString("F1", inv)).Append('/').Append(w.T1Roh.ToString("F1", inv)).Append('/').Append(w.T2Roh.ToString("F1", inv)).Append(')');
            sb.Append(", rise ").Append((w.TauAuf * 1000).ToString("F0", inv)).Append(" ms, onset +").Append((s.HallVor * 1000).ToString("F1", inv)).Append(" ms, lp ")
              .Append(Wege.FitLp(w.M1, w.M2).ToString("F0", inv)).Append(" Hz; replaces event/speech cut");
            double ab = Physik.AbzugDb(s.Q);
            if (ab > 0.01) sb.Append(" (") .Append(ab.ToString("F1", inv)).Append(" dB taken back)");
            return sb.ToString();
        }

        static void HallLoggen()
        {
            int summe = 0; for (int k = 0; k < KN; k++) summe += dHall[k] + dHallAbgelehnt[k] + dHallOhne[k];
            string audio = Nachhall.FilterText(out bool leer);
            if (summe == 0 && leer && hallVorrat.Count == 0) return;
            int belegt = 0; foreach (var v in hallVorrat) if (v.Besitzer != null || v.Schuetze != null) belegt++;
            var sb = new System.Text.StringBuilder("[Diagnostics] Reverb voices (sounds carried / refused for lack of voices / without diffuse part):");
            for (int k = 0; k < KN; k++)
                if (dHall[k] + dHallAbgelehnt[k] + dHallOhne[k] > 0)
                    sb.Append(' ').Append(KName[k]).Append(' ').Append(dHall[k]).Append('/').Append(dHallAbgelehnt[k]).Append('/').Append(dHallOhne[k]).Append(';');
            sb.Append($" {dHallAnregungen} excitations, {dHallEntzogen} voices taken from fainter tails, {dHallFern} shots too far ahead, {dHallGewartet} excitations waited for their own onset (at most {dHallFehlerMax * 1000:F0} ms off, {dHallVoll} queue full), {schuetzen.Count} shooters with a voice now, event/speech cut replaced in {Physik.NachhallStattAbzug} applications; pool {hallVorrat.Count}/{Nachhall.Groesse} ({belegt} busy), at most {dHallSpitze} at once; ")
              .Append(audio).Append(Nachhall.Kaputt ? "; OFF: " + Nachhall.KaputtGrund : "").Append('.');
            Plugin.Log.LogInfo(sb.ToString());
            for (int k = 0; k < KN; k++) dHall[k] = dHallAbgelehnt[k] = dHallOhne[k] = 0;
            dHallSpitze = dHallAnregungen = dHallEntzogen = dHallFern = dHallGewartet = dHallVoll = 0; dHallFehlerMax = 0; Physik.NachhallStattAbzug = 0;
        }

        // ---------------------------------------------------------------- Vorrat

        static bool IstVorrat(BetterSource q)
        {
            for (int i = 0; i < vorrat.Count; i++) if (ReferenceEquals(vorrat[i].S, q)) return true;
            return false;
        }

        /// <summary>Freie Quelle aus dem Vorrat (bei Bedarf geliehen, bis zur Vorratsgroesse); sonst Entzug beim schwaechsten Besitzer.</summary>
        static Stimme Holen(double prio)
        {
            int g = Groesse;
            if (g <= 0) return null;
            for (int i = 0; i < vorrat.Count && i < g; i++) if (vorrat[i].Besitzer == null && vorrat[i].S != null) return vorrat[i];
            if (vorrat.Count < g)
            {
                var ba = MonoBehaviourSingleton<BetterAudio>.Instantiated ? MonoBehaviourSingleton<BetterAudio>.Instance : null;
                if (ba == null) return null;
                if (vorrat.Count > 0 && !ReferenceEquals(ba, vorratVon)) return null;
                var src = ba.GetSource(BetterAudio.AudioSourceGroupType.Character);
                if (src == null) return null;
                vorratVon = ba;
                var t = new GameObject("Materialklang voice " + vorrat.Count);
                UnityEngine.Object.DontDestroyOnLoad(t);
                src.SetParent(t.transform, false);
                src.LocalPosition = Vector3.zero;
                var st = new Stimme { S = src, Traeger = t, HallVorher = src.EnableReverb };
                src.EnableReverb = false;
                vorrat.Add(st);
                return st;
            }
            Stimme schwach = null;
            for (int i = 0; i < vorrat.Count; i++)
            {
                var v = vorrat[i];
                if (v.Besitzer == null || v.Entzug) continue;
                if (v.Prio < prio && (schwach == null || v.Prio < schwach.Prio)) schwach = v;
            }
            if (schwach != null)
            {
                schwach.Entzug = true; dEntzogen++;
                // der Slot gilt nur noch als abgedeckt, wenn dort eine andere (nicht entzogene) Stimme spielt
                var bs = schwach.Besitzer; int n = 0;
                foreach (var o in bs.St) if (o.Slot == schwach.Slot && !o.Entzug) n++;
                if (n == 0) bs.SlotNeu[schwach.Slot] = false;
            }
            return null;
        }

        static void Einrichten(Stimme st, BetterSource q, float pitch)
        {
            var s = st.S;
            s.SetMixerGroup(q.source1.outputAudioMixerGroup);
            s.EnabledEQ(true);
            s.SetPitch(pitch);
            s.SetRolloff(q.MaxDistance);
            s.source1.priority = q.source1.priority;
            s.EnableReverb = false;
            if (st.F == null) s.source1.loop = false;   // nachhallstimme: nur die Nachhall-Stimme spielt eine Schleife (Traeger)
        }

        static void Frei(Stimme st)
        {
            try { if (st.S != null && st.S.PlayBackState == BetterSource.EPlayBackState.Playing) st.S.Stop(); }
            catch (Exception e) { Statistik.Fehler(e); }
            st.Besitzer = null; st.Entzug = false; st.Prio = 0; st.G = 1; st.Slot = 0;
        }

        static void StimmenStoppen(Satz s)
        {
            for (int i = 0; i < s.St.Count; i++) Frei(s.St[i]);
            s.St.Clear();
            for (int b = 0; b < MehrAuswahl.KMax; b++) { s.SlotN[b] = 0; s.SlotNeu[b] = false; s.SJetzt[b] = s.SZiel[b] = 0; }
            if (s.Q != null && !float.IsNaN(s.Basis)) { s.Q.SetOcclusionVolumeFactor(s.Basis); s.Geschrieben = s.Q.OcclusionVolumeFactor; }
        }

        /// <summary>Satz zu Ende: Stimmen frei, Original wieder auf den Grundwert, Satz zurueck in den Vorrat.</summary>
        static void Ende(Satz s)
        {
            try { StimmenStoppen(s); }
            catch (Exception e) { Statistik.Fehler(e); }
            try { HallFrei(s); }
            catch (Exception e) { Statistik.Fehler(e); }
            if (s.Q != null && saetze.TryGetValue(s.Q, out var x) && ReferenceEquals(x, s)) saetze.Remove(s.Q);
            int i = aktiv.IndexOf(s);
            if (i >= 0) { int last = aktiv.Count - 1; aktiv[i] = aktiv[last]; aktiv.RemoveAt(last); }
            s.Leeren();
            freieSaetze.Add(s);
        }

        // ---------------------------------------------------------------- Physik / Richtung / Log

        static Satz Laufend(BetterSource src)
        {
            if (src == null || saetze.Count == 0 || !saetze.TryGetValue(src, out var s) || !s.Aktiv || s.Q == null) return null;
            return s;
        }

        /// <summary>Physik.Kurve: Klangfarbe des Originals = die seines eigenen Buendels (plus Wandanteil), nicht die der Summe.
        /// Korrektur der Mehrdaempfung je Band: -10 lg(e1_b / e1_0), e1_b = 1 - Raumanteil x Summe der Slots im Band b (Annahme: der
        /// Wandanteil ist in allen Baendern gleich gross). true = korrigiert.</summary>
        internal static bool ErsteKorr(BetterSource src, ref double m1, ref double m2)
        {
            var s = Laufend(src);
            if (s == null) return false;
            var a = s.A;
            double w0 = 0, w1 = 0, w2 = 0; bool hallK = s.HallAn && s.Hall != null, hat = hallK;
            for (int slot = 1; slot < MehrAuswahl.KMax; slot++)
            {
                int k = s.SlotBuendel[slot];
                if (s.SlotN[slot] == 0 || k <= 0 || k >= a.N || !(s.SJetzt[slot] > 1e-6) || !(a.S0[k] > 0)) continue;
                hat = true;
                double r = s.SJetzt[slot] / a.S0[k];   // jetziger (geglitteter) Anteil, Bandform des Buendels
                w0 += s.SJetzt[slot]; w1 += r * a.S1[k]; w2 += r * a.S2[k];
            }
            if (!hat) return false;
            // nachhallstimme: ohne den verschmierten Teil (Nachhall-Stimme) hat das Original die Farbe von Wand + gerichtetem Buendel 1
            double e0 = NachhallKern.ErsteBand(s.Anteil, s.HallR0, hallK, w0), e1 = NachhallKern.ErsteBand(s.Anteil, s.HallR1, hallK, w1),
                   e2 = NachhallKern.ErsteBand(s.Anteil, s.HallR2, hallK, w2);
            if (!(e0 > 1e-9)) return false;
            m1 += e1 > 1e-9 ? -10 * Math.Log10(e1 / e0) : 60;
            m2 += e2 > 1e-9 ? -10 * Math.Log10(e2 / e0) : 60;
            return true;
        }

        /// <summary>Richtung.Setzen: spielen weitere Stimmen, zeigt das Original auf sein eigenes Buendel (sonst die Seite des fuehrenden Wegs -
        /// der Rueckfall fuer Klaenge ohne weitere Stimme).</summary>
        internal static bool Austritt1(BetterSource src, out V3 aus)
        {
            aus = default;
            var s = Laufend(src);
            if (s == null || s.A.N < 1) return false;
            if (s.HallAn && s.Hall != null) { aus = s.A.Aus[0]; return !aus.IstNull; }   // nachhallstimme: Original = gerichtetes Buendel 1
            for (int slot = 1; slot < MehrAuswahl.KMax; slot++)
                if (s.SlotN[slot] > 0 && s.SJetzt[slot] > 1e-6) { aus = s.A.Aus[0]; return !aus.IstNull; }
            return false;
        }

        /// <summary>Klang-Log: " | voices now: ..." fuer eine Quelle mit weiteren Stimmen (null = keine).</summary>
        internal static string LogText(BetterSource src)
        {
            var s = Laufend(src);
            if (s == null || (s.St.Count == 0 && s.Hall == null)) return null;
            if (s.St.Count == 0) return HallText(s, false);
            var inv = CultureInfo.InvariantCulture; var a = s.A;
            bool hallL = s.HallAn && s.Hall != null;
            double wS = 0; for (int slot = 1; slot < MehrAuswahl.KMax; slot++) if (s.SlotN[slot] > 0) wS += s.SJetzt[slot];
            NachhallKern.Teile(s.Anteil, s.HallR0, hallL, wS, out double ersteL, out _, out double jeL);
            var sb = new System.Text.StringBuilder(" | voices now (").Append(KName[s.Klasse]).Append("): 1 ")
                .Append((10 * Math.Log10(Math.Max(1e-12, ersteL))).ToString("F1", inv)).Append(" dB");
            for (int slot = 1; slot < MehrAuswahl.KMax; slot++)
            {
                if (s.SlotN[slot] == 0) continue;
                int k = s.SlotBuendel[slot];
                sb.Append("; ").Append(slot + 1).Append(' ').Append((10 * Math.Log10(Math.Max(1e-12, jeL * s.SJetzt[slot]))).ToString("F1", inv)).Append(" dB");
                if (k > 0 && k < a.N) sb.Append(" +").Append(a.DtMs[k].ToString("F1", inv)).Append(" ms ").Append(a.Winkel[k].ToString("F0", inv)).Append(" deg");
                else sb.Append(" (direction gone, fading)");
                if (s.SlotN[slot] > 1) sb.Append(" x").Append(s.SlotN[slot]);
                if (!s.SlotNeu[slot]) sb.Append(" (newest sound without voice here, fading)");
            }
            return sb.Append(HallText(s, false)).ToString();
        }

        internal static void Loggen()
        {
            HallLoggen();   // nachhallstimme: eigene Zeile
            int summe = 0; for (int k = 0; k < KN; k++) summe += dKlaenge[k] + dAbgelehnt[k];
            if (summe == 0 && dEntzogen == 0) return;
            int real = -1;
            try { real = AudioSettings.GetConfiguration().numRealVoices; } catch (Exception) { }
            int belegt = 0; foreach (var v in vorrat) if (v.Besitzer != null) belegt++;
            var sb = new System.Text.StringBuilder("[Diagnostics] Room path voices (sounds with several bundles / of them one voice only / voices given / refused for lack of voices / left out for start uncertainty):");
            for (int k = 0; k < KN; k++)
                if (dKlaenge[k] + dAbgelehnt[k] > 0)
                    sb.Append(' ').Append(KName[k]).Append(' ').Append(dKlaenge[k]).Append('/').Append(dKeine[k]).Append('/').Append(dVergeben[k]).Append('/').Append(dAbgelehnt[k]).Append('/').Append(dTechnik[k]).Append(';');
            sb.Append($" {dEntzogen} taken away from quieter sounds, {dVerpasst} sounds ended before their room path was known; pool {vorrat.Count}/{Groesse} ({belegt} busy), " +
                      $"{aktiv.Count} sounds with voices now, at most {dSpitze} voices at once, voices per sound {K}, start uncertainty {UMs():F1} ms; " +
                      $"{Laufzeit.Ms(dTicks) / Math.Max(1, dBilder):F3} ms per frame on average, at most {Laufzeit.Ms(dMaxTicks):F3} ms; Unity real voices {real}.");
            Plugin.Log.LogInfo(sb.ToString());
            for (int k = 0; k < KN; k++) dKlaenge[k] = dKeine[k] = dVergeben[k] = dAbgelehnt[k] = dTechnik[k] = 0;
            dEntzogen = dSpitze = dBilder = dVerpasst = 0; dTicks = dMaxTicks = 0;
            uAuto = 0;   // Puffergroesse neu lesen (das Spiel setzt sie beim Raidstart zurueck, AudioUtils.ResetAudioBuffer)
        }

        // ---------------------------------------------------------------- Aufraeumen

        /// <summary>Neues BetterAudio: die alten Quellen gehoeren zu einem anderen Audiosystem - nur vergessen, Traeger entfernen.</summary>
        static void Vergessen()
        {
            foreach (var s in aktiv) { s.Leeren(); freieSaetze.Add(s); }
            aktiv.Clear(); saetze.Clear();
            for (int i = 0; i < RingN; i++) ring[i].Q = null;
            offen = 0;
            foreach (var v in vorrat)
                try { if (v.Traeger != null) UnityEngine.Object.Destroy(v.Traeger); } catch (Exception e) { Statistik.Fehler(e); }
            foreach (var v in hallVorrat)
                try { if (v.F != null) { v.F.Aktiv = false; UnityEngine.Object.Destroy(v.F); } if (v.Traeger != null) UnityEngine.Object.Destroy(v.Traeger); } catch (Exception e) { Statistik.Fehler(e); }
            vorrat.Clear(); hallVorrat.Clear(); schuetzen.Clear(); vorratVon = null; uAuto = 0;
            Nachhall.SzeneEnde();
        }

        /// <summary>Testquelle entfernt: nur deren Stimmen frei, erste Stimme wieder voll.</summary>
        internal static void Aufraeumen()
        {
            try
            {
                for (int i = aktiv.Count - 1; i >= 0; i--) if (aktiv[i].Klasse == KTest) Ende(aktiv[i]);
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>Raid-Ende / Schalter aus: alle Stimmen an den Pool, alle Originale wieder voll.</summary>
        internal static void AllesFrei()
        {
            for (int i = aktiv.Count - 1; i >= 0; i--) Ende(aktiv[i]);
            for (int i = 0; i < RingN; i++) ring[i].Q = null;
            offen = 0;
            bool gleich = MonoBehaviourSingleton<BetterAudio>.Instantiated && ReferenceEquals(MonoBehaviourSingleton<BetterAudio>.Instance, vorratVon);
            foreach (var v in vorrat)
            {
                try { if (v.S != null && gleich) { v.S.Stop(); v.S.EnableReverb = v.HallVorher; v.S.Release(); } }
                catch (Exception e) { Statistik.Fehler(e); }
                try { if (v.Traeger != null) UnityEngine.Object.Destroy(v.Traeger); }
                catch (Exception e) { Statistik.Fehler(e); }
            }
            vorrat.Clear();
            HallVorratZurueck(gleich);
            vorratVon = null; uAuto = 0;
            if (!Comfort.Common.Singleton<EFT.GameWorld>.Instantiated) Nachhall.SzeneEnde();   // Raid-Ende: Selbsttest neu
        }

        /// <summary>nachhallstimme: Schalter aus - nur die Nachhall-Stimmen an den Pool, Originale wieder ohne Nachhall-Anteil.</summary>
        internal static void HallAllesFrei()
        {
            try
            {
                for (int i = aktiv.Count - 1; i >= 0; i--) { var s = aktiv[i]; HallFrei(s); if (s.St.Count == 0) Ende(s); else Teilen(s); }
                bool gleich = MonoBehaviourSingleton<BetterAudio>.Instantiated && ReferenceEquals(MonoBehaviourSingleton<BetterAudio>.Instance, vorratVon);
                HallVorratZurueck(gleich);
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>Hall-Vorrat an den Pool: Filter entfernen (eine Spielquelle darf nie unser Filter tragen), Schleife/Reihenfolge der Effekte
        /// wie vom Spiel, dann Release.</summary>
        static void HallVorratZurueck(bool gleich)
        {
            schuetzen.Clear();
            foreach (var v in hallVorrat)
            {
                try { if (gleich) KurveZurueck(v); } catch (Exception e) { Statistik.Fehler(e); }
                v.Schuetze = null; v.Besitzer = null; if (v.Warte != null) v.Warte.Leeren();
                try { if (v.F != null) { v.F.Aktiv = false; UnityEngine.Object.Destroy(v.F); v.F = null; } }
                catch (Exception e) { Statistik.Fehler(e); }
                try
                {
                    if (v.S != null && gleich)
                    {
                        v.S.Stop(); v.S.source1.loop = false; v.S.source1.spatializePostEffects = false;
                        v.S.EnableReverb = v.HallVorher; v.S.Release();
                    }
                }
                catch (Exception e) { Statistik.Fehler(e); }
                try { if (v.Traeger != null) UnityEngine.Object.Destroy(v.Traeger); }
                catch (Exception e) { Statistik.Fehler(e); }
            }
            hallVorrat.Clear();
        }
    }
}
