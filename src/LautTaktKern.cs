using System;
using System.Collections.Generic;

namespace Materialklang
{
    // 10.11.2026 (Review B1, audio-programmierer.md) - die Lautstaerke folgt der Wertung, auch wenn diese langsam wandert.
    // Reine Rechnung ohne Unity, damit der Offline-Test (Materialklang-KlangTest) sie mituebersetzen kann. Spiel-Teil: Physik.cs
    // (GemerktVor = Prefix CombinedSpatialAudioOccluder.ApplyCachedParamsToContainer).
    //
    // Ursache (Spielcode CombinedSpatialAudioOccluder.OnUpdateOcclusionEffects, Dekompilat Z. 147-158):
    //   flag2 = |_smoothedPropagationEffect - _lastPropagationEffect| > effectChangeThreshold (0,05)   (Hindernis-Zweig ebenso)
    //   _lastPropagationEffect = _smoothedPropagationEffect                                           (JEDER Aufruf)
    //   kein Ausloeser -> ApplyCachedParamsToContainer: Lautstaerke aus dem Merker der letzten VOLLEN Rechnung
    //   (CalculateFinalOcclusionParams), der Tiefpass dagegen wird in ApplyOcclusionToSource frisch gerechnet.
    //   Verglichen wird also mit dem VORIGEN AUFRUF, nicht mit dem Wert, zu dem die angewandte Lautstaerke gehoert. Wandert die
    //   Wertung in Schritten < 0,05 (Routen-Gleiter jedes Bild, Spielglaettung 20*dt je Aufruf), loest nie etwas aus: die
    //   Lautstaerke bleibt beliebig weit stehen und springt erst beim naechsten grossen Schritt (Log 16-44-54: Bot47 1,3 s lang
    //   2,9 dB daneben, dann Sprung 2,8 dB; Bot52 4,9 dB zu laut). Dazu ist die Schwelle in Wertungseinheiten fuer die Spielkurve
    //   gemacht (endet bei -4,4 dB: 0,05 ~ 0,2 dB); im Physik-Modus ist 0,05 Wertung = 3 dB (Dynamik 60, Lautheit 1), in der
    //   Verdeckungs-Zone leiser Quellen bis 9 dB (3 dB je dB, Hoerbarkeit.Verdeckung) - und die Lautstaerke haengt dort zusaetzlich
    //   am Abstand (Pegel am Ohr), den die Spielregel gar nicht ansieht.
    //
    // Loesung (an der Stelle, an der das Spiel "gemerkt" waehlt): im gemerkten Weg je Quelle die Soll-Daempfung fuer den
    // AKTUELLEN Stand rechnen (dieselbe Rechnung wie die volle: Physik.SollDb) und mit der zuletzt ANGEWANDTEN vergleichen. Liegt
    // eine Quelle des Behaelters hoerbar daneben (> SchwelleDb), rechnet der Behaelter voll - genau wie der volle Weg des Spiels
    // (CalculateFinalOcclusionParams + ApplyOcclusionToSource je Quelle), Lautstaerke und Tiefpass stammen dann wieder aus
    // derselben Rechnung. Sonst bleibt der gemerkte Weg. Kein Timer, keine Wertungs-Schwelle: entscheidend ist der Abstand in dB.
    //
    // Kosten (LogOutput.log 10.11., 20 Fenster a 30 s, Factory): volle Lautstaerke-Rechnung 5,3 us je Quelle (24715 Aufrufe),
    // Filter + Hoerbarkeit 8,4 us je Anwenden (87591, davon ~63000 im gemerkten Weg = ~105 je s). Die Pruefung kostet je gemerktem
    // Anwenden eine Soll-Rechnung (~5 us) -> ~0,55 ms je Sekunde (~0,01 ms je Bild bei 60 Bildern/s); die zusaetzlichen vollen
    // Rechnungen fallen nur an, solange sich Hoerbares aendert (bei 0,25 dB Schwelle z. B. 12 je 3-dB-Blende).
    internal static class LautTaktKern
    {
        /// <summary>Hoerbarer Abstand (dB) zwischen angewandter und Soll-Lautstaerke, ab dem neu gerechnet wird. Annahme: halbe
        /// kleinste Unterschiedsschwelle fuer Pegel - breitbandiges Rauschen ~0,5 dB (Miller 1947, J. Acoust. Soc. Am. 19, 609),
        /// Sinustoene 0,5-1,5 dB je nach Pegel (Jesteadt/Wier/Green 1977, JASA 61, 169). Halb, damit auch ein Fehler, der sich mit
        /// dem naechsten Schritt addiert, unter der Wahrnehmung bleibt; der Lautstaerke-Gleiter der Quelle (BetterSource, 100 ms)
        /// verschleift die verbleibenden Stufen zusaetzlich.</summary>
        public const double SchwelleDb = 0.25;

        /// <summary>Die Regel des Spiels (Dekompilat Z. 148): neu rechnen, wenn sich der geglaettete Wert gegenueber dem VORIGEN
        /// Aufruf um mehr als 'schwelle' geaendert hat. Nur fuer Test und Doku - der Aufrufer setzt 'vorher' danach auf 'jetzt'.</summary>
        public static bool SpielRechnetNeu(float jetzt, float vorher, float schwelle) => Math.Abs(jetzt - vorher) > schwelle;

        /// <summary>Neu rechnen? angewandt/soll = Daempfung in dB; NaN = unbekannt (nie voll gerechnet) -> ja.</summary>
        public static bool NeuRechnen(double angewandtDb, double sollDb, double schwelleDb)
            => double.IsNaN(angewandtDb) || double.IsNaN(sollDb) || !(Math.Abs(sollDb - angewandtDb) <= schwelleDb);
    }

    /// <summary>Je Occluder: zuletzt angewandte Daempfung (dB) jeder Quelle und der zuletzt gepruefte Abstand zum Soll.
    /// stempel = Bildnummer (das Klang-Log zeigt den Abstand nur, wenn er im selben Bild gemessen wurde).</summary>
    internal sealed class LautTakt
    {
        struct Eintrag { public double Angewandt, Abstand; public bool Frisch; public int Stempel; }
        readonly Dictionary<int, Eintrag> werte = new Dictionary<int, Eintrag>(8);

        public int Anzahl => werte.Count;

        /// <summary>Volle Rechnung: diese Daempfung ist jetzt angewandt (Abstand 0).</summary>
        public void Angewandt(int quelle, double db, int stempel)
        {
            werte[quelle] = new Eintrag { Angewandt = db, Abstand = 0, Frisch = true, Stempel = stempel };
        }

        /// <summary>Gemerkter Weg: Soll mit der angewandten Daempfung vergleichen. true = neu rechnen (unbekannte Quelle oder
        /// hoerbar daneben). abstand = angewandt - soll (dB, &gt; 0: zu leise), NaN wenn unbekannt.</summary>
        public bool Pruefen(int quelle, double sollDb, double schwelleDb, int stempel, out double abstand)
        {
            if (!werte.TryGetValue(quelle, out var e)) { abstand = double.NaN; return true; }
            abstand = e.Angewandt - sollDb;
            e.Abstand = abstand; e.Frisch = false; e.Stempel = stempel;
            werte[quelle] = e;
            return LautTaktKern.NeuRechnen(e.Angewandt, sollDb, schwelleDb);
        }

        /// <summary>Zuletzt gemessener Abstand der Quelle (frisch = im vollen Weg gerechnet, Abstand 0), nur wenn im Bild 'stempel'.</summary>
        public bool Abstand(int quelle, int stempel, out double abstand, out bool frisch)
        {
            abstand = double.NaN; frisch = false;
            if (!werte.TryGetValue(quelle, out var e) || e.Stempel != stempel) return false;
            abstand = e.Abstand; frisch = e.Frisch;
            return true;
        }

        public bool Angewandt(int quelle, out double db)
        {
            bool da = werte.TryGetValue(quelle, out var e);
            db = da ? e.Angewandt : double.NaN;
            return da;
        }

        public void Vergessen(int quelle) => werte.Remove(quelle);
        public void Leeren() => werte.Clear();
    }
}
