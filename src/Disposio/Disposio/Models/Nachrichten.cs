using System;

namespace Disposio.Models;

/// <summary>Ein Gespräch in der linken Liste von D09: eine Tour mit ihrem Fahrer.</summary>
public sealed record Gespraech(
    long TourId,
    string Tournummer,
    string Kennzeichen,
    long FahrerBenutzerId,
    string FahrerName,
    string FahrerSprache,
    DateTime? LetzteNachricht,
    int Ungelesen);

/// <summary>Eine Chat-Nachricht (public.nachricht) für die Anzeige in D09.</summary>
/// <param name="VomFahrer">true = der Fahrer hat geschrieben; false = ein Disponent.</param>
/// <param name="VonMir">true = das angemeldete Konto hat geschrieben.</param>
/// <param name="Text">Originaltext in <see cref="Originalsprache"/>.</param>
/// <param name="Uebersetzung">Text in <see cref="Zielsprache"/>, sobald übersetzt.</param>
/// <param name="Status">Siehe <see cref="UebersetzungsStatus"/>.</param>
/// <param name="Uebertragung">Siehe <see cref="UebertragungsStatus"/>. Setzt die App des Empfängers.</param>
/// <param name="Gelesen">Der Empfänger hat die Nachricht geöffnet (nachricht.gelesen).</param>
public sealed record ChatNachricht(
    long Id,
    bool VomFahrer,
    bool VonMir,
    string Absender,
    string Text,
    string? Uebersetzung,
    string Originalsprache,
    string Zielsprache,
    string Status,
    DateTime Zeit,
    string Uebertragung,
    bool Gelesen,
    DateTime? GelesenAm);

/// <summary>
/// Werte von nachricht.uebersetzungsstatus. Gleiche Werte in neon-function/functions/uebersetzen.ts
/// und in der Fahrer-App (docs/04_Architektur/Chat-und-Uebersetzung.md).
/// </summary>
public static class UebersetzungsStatus
{
    public const string Ausstehend = "ausstehend";
    public const string Uebersetzt = "uebersetzt";
    public const string Fehlgeschlagen = "fehlgeschlagen";
    public const string NichtNoetig = "nicht_noetig";
}

/// <summary>
/// Werte von nachricht.uebertragungsstatus. Der Absender speichert „gesendet“; die App des Empfängers setzt
/// „zugestellt“, sobald sie die Nachricht abgeholt hat. „Gelesen“ steht in nachricht.gelesen / gelesen_am.
/// </summary>
public static class UebertragungsStatus
{
    public const string Gesendet = "gesendet";
    public const string Zugestellt = "zugestellt";
}

/// <summary>Sprachnamen für die Oberfläche (benutzer.sprache: de, en, pl).</summary>
public static class Sprachen
{
    // „Sprache Polnisch“, „Aus Polnisch übersetzt“
    public static string Name(string code) => code switch
    {
        "de" => "Deutsch",
        "en" => "Englisch",
        "pl" => "Polnisch",
        _ => code
    };

    // „ins Polnische übersetzt“
    public static string Ziel(string code) => code switch
    {
        "de" => "Deutsche",
        "en" => "Englische",
        "pl" => "Polnische",
        _ => code
    };
}
