namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // hallsend (12.11.2026, Spielcode-Fund 4) - Messvorrat der Wand zwischen Raeumen. Reine Rechnung (ohne Unity, Offline-Test).
    //
    // Ursache: Raumweg.Neubeginn (erster Wert eines neuen / wieder hoerbaren Klangs) ruft den Raumweg-Rechner sofort durch
    //   (ManualUpdate -> Job fertig -> ManualLateUpdate -> GetScore). Der Wand-Strahlauftrag (WandZwischenRaeumen.Starten,
    //   RaycastCommand) wird in ManualUpdate erst GEPLANT und ist beim GetScore direkt danach praktisch nie fertig. Ein Rechner aus dem
    //   Pool ist zurueckgesetzt (Score -1) -> der erste Wert war nur die Route ueber Tueren/Fenster (zu leise, wenn die Wand leichter
    //   durchlaesst), einen Takt spaeter kam die Wand dazu und das Spiel blendete hoch. Warten auf den Auftrag ist verboten (Hauptfaden,
    //   Regel "nie auf unfertige Strahl-Auftraege warten").
    // Loesung: die Wandmessung gehoert zur GEOMETRIE (Quellort, Hoererort, Raumpaar, Tuer-/Fensterzustand), nicht zum Rechner. Fertige
    //   Messungen kommen in einen kleinen Vorrat; ein Rechner ohne gueltige eigene Messung uebernimmt vorlaeufig die juengste Messung
    //   desselben Raumpaars, deren Orte und Zustand nach DERSELBEN Regel noch gelten, nach der ein Rechner seine eigene Messung behaelt
    //   (WandZwischenRaeumen.UpdatePostfix: Bewegung beider Enden &lt;= 0,3 m, Alter &lt;= 2 s, gleicher Portalzustand). Die eigene Messung
    //   laeuft trotzdem an und ersetzt den Vorratswert beim naechsten Takt. Kein neuer Schwellwert: dieselben Grenzen wie bisher.
    // ------------------------------------------------------------------------------------------
    internal static class WandVorratKern
    {
        /// <summary>Gilt eine Messung (Raumpaar ra/rb, Portalzustand schmutz, Orte pa/pb, Zeit zeit) fuer den Stand jetzt (Raumpaar,
        /// Zustand, Orte a/b, Zeit jetzt)? Dieselbe Regel wie "nicht neu messen" im WandZwischenRaeumen.UpdatePostfix.</summary>
        public static bool Gilt(short ra, short rb, int schmutz, V3 pa, V3 pb, double zeit,
                                short raJetzt, short rbJetzt, int schmutzJetzt, V3 a, V3 b, double jetzt, double bewegung, double alter)
        {
            if (ra != raJetzt || rb != rbJetzt || schmutz != schmutzJetzt) return false;
            if (jetzt - zeit > alter || jetzt < zeit) return false;
            double b2 = bewegung * bewegung;
            V3 da = a - pa, db = b - pb;
            return V3.Dot(da, da) <= b2 && V3.Dot(db, db) <= b2;
        }
    }
}
