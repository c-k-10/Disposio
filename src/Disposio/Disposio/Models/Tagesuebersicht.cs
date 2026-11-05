using System;
using System.Collections.Generic;

namespace Disposio.Models;

/// <summary>
/// Alles, was D01 „Tagesübersicht“ beim Schichtbeginn anzeigt. Wird in einem Rutsch geladen
/// (TagesuebersichtRepository) und danach nur noch angezeigt.
/// </summary>
/// <param name="Seit">Beginn des Zeitraums „Seit Ihrer letzten Schicht“ (deutsche Zeit).</param>
/// <param name="SeitAbmeldung">true = Seit ist die letzte Abmeldung; false = Ersatzwert (gestern 18:00 oder 3 Tage).</param>
public sealed record Tagesuebersicht(
    DateTime Seit,
    bool SeitAbmeldung,
    int TourenHeute,
    int SendungenUnverplant,
    int FreigabenOffen,
    int SchaedenOffen,
    IReadOnlyList<SchichtEreignis> Ereignisse,
    IReadOnlyList<TagesHinweis> Hinweise);

/// <summary>Was ist seit der letzten Schicht passiert? Eine Zeile in „Seit Ihrer letzten Schicht“.</summary>
/// <param name="Zeit">Zeitpunkt in deutscher Zeit.</param>
/// <param name="ZeitGenau">false = nur das Datum ist bekannt (Schadensmeldungen haben keine Uhrzeit).</param>
/// <param name="ZielAnsicht">Ansicht, die der Knopf öffnet, zum Beispiel „D07“.</param>
/// <param name="ZielParameter">Startwert für die Ansicht, zum Beispiel die Tour-Id. null = ohne.</param>
public sealed record SchichtEreignis(
    DateTime Zeit,
    bool ZeitGenau,
    string Titel,
    string Detail,
    string KnopfText,
    string ZielAnsicht,
    long? ZielParameter);

/// <summary>Eine Zeile in „Heute priorisieren“: Hinweis, Auswirkung und der nächste Schritt.</summary>
public sealed record TagesHinweis(
    string Hinweis,
    string Auswirkung,
    string KnopfText,
    string ZielAnsicht,
    long? ZielParameter);
