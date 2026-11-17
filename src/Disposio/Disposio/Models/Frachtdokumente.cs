using System;
using System.Collections.Generic;

namespace Disposio.Models;

/// <summary>Eine Sendung in D11 mit Zusammenfassung ihrer Dokumente und Abliefernachweise.</summary>
public sealed record DokumentSendung(
    long Id,
    string Auftragsnummer,
    string? Kundenreferenz,
    string Kunde,
    int Anzahl,
    string Verpackungstyp,
    decimal GewichtKg,
    DateOnly Lieferdatum,
    string Lieferadresse,
    int Dokumente,
    bool NachweisVorhanden,
    bool ProblemGemeldet)
{
    /// <summary>Kundenreferenz, sonst Auftragsnummer (so sucht man die Sendung auch in D11).</summary>
    public string Referenz => string.IsNullOrWhiteSpace(Kundenreferenz) ? Auftragsnummer : Kundenreferenz!;

    /// <summary>Siehe <see cref="DokumentStatus"/>.</summary>
    public string Status => ProblemGemeldet ? DokumentStatus.Problem
        : NachweisVorhanden ? DokumentStatus.Abgeliefert
        : Lieferdatum < DateOnly.FromDateTime(DateTime.Today) ? DokumentStatus.NachweisFehlt
        : DokumentStatus.Offen;
}

/// <summary>Ein Dokument (public.dokument) – aus der Fahrer-App, in D11, D03 oder D02 zugeordnet.</summary>
/// <param name="Entfernbar">Im Büro direkt zur Sendung hochgeladen (nicht Fahrer-App, nicht Abliefernachweis) und
/// ohne Verlauf – nur solche Dokumente darf der Disponent wieder entfernen.</param>
/// <param name="Ersetzbar">Aktuelle Version, direkt an der Sendung (nicht Abliefernachweis) – „Ersetzen“ möglich.</param>
/// <param name="ErsetztDurchId">Gesetzt = alte Version (Verlauf, Migration 018); zeigt auf die neuere Version.</param>
public sealed record Frachtdokument(
    long Id,
    string Art,
    string Dateiname,
    string Adresse,
    string MimeTyp,
    long Bytes,
    DateTime HochgeladenAm,
    string HochgeladenVon,
    bool AusFahrerApp,
    string? Bemerkung,
    bool Entfernbar = false,
    bool Ersetzbar = false,
    long? ErsetztDurchId = null,
    DateTime? ErsetztAm = null,
    string? ErsetztVon = null)
{
    /// <summary>Aktuelle Version (nicht ersetzt).</summary>
    public bool IstAktuell => ErsetztDurchId is null;
}

/// <summary>Beim Erfassen einer neuen Sendung (D03) ausgewählte Datei. Wird erst nach dem Speichern hochgeladen.</summary>
public sealed record VorgemerktesDokument(string Art, string Dateiname, byte[] Inhalt);

/// <summary>Ein Abliefernachweis der Fahrer-App (public.abliefernachweis), ggf. mit Problemmeldung.</summary>
public sealed record Abliefernachweis(
    long Id,
    DateTime Zeitpunkt,
    string Fahrer,
    string? Empfaenger,
    string Bestaetigungsart,
    int Abgeliefert,
    int Erwartet,
    string? Ausnahmegrund,
    string? Bemerkung)
{
    public bool IstProblem => !string.IsNullOrWhiteSpace(Ausnahmegrund) || Abgeliefert < Erwartet;
}

public sealed record SendungDokumente(IReadOnlyList<Frachtdokument> Dokumente, IReadOnlyList<Abliefernachweis> Nachweise);

/// <summary>Status-Filter in D11.</summary>
public static class DokumentStatus
{
    public const string Alle = "Alle";
    public const string Abgeliefert = "Abgeliefert";
    public const string Problem = "Problem gemeldet";
    public const string NachweisFehlt = "Nachweis fehlt";
    public const string Offen = "Noch nicht geliefert";

    public static readonly IReadOnlyList<string> Auswahl = new[] { Alle, Problem, NachweisFehlt, Abgeliefert, Offen };

    /// <summary>Dokumentarten beim Zuordnen in D11. „Foto“, „Unterschrift“, „Lieferschein“ nutzt auch die Fahrer-App.</summary>
    public static readonly IReadOnlyList<string> Arten = new[] { "Lieferschein", "Frachtbrief (CMR)", "Foto", "Zolldokument", "Sonstiges" };
}
