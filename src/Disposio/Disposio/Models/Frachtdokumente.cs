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

/// <summary>Ein Dokument (public.dokument) – aus der Fahrer-App oder in D11 zugeordnet.</summary>
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
    string? Bemerkung);

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
