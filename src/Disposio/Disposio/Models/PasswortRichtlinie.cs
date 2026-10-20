using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Disposio.Models;

/// <summary>
/// Passwort-Richtlinie eines Unternehmens (K04). Die Mindestlänge steht in unternehmen.passwort_mindestlaenge,
/// die übrigen Werte als JSON-Text in unternehmen.passwortregeln. So braucht K04 keine neue Spalte.
/// </summary>
public sealed record PasswortRichtlinie(
    int Mindestlaenge,
    int Fehlversuche,
    int SperrdauerMinuten,
    int InaktivitaetMinuten,
    bool BekanntePasswoerterSperren)
{
    // Werte aus dem Wireframe K04. Gelten, solange eine Firma noch keine eigene Richtlinie gespeichert hat.
    public static PasswortRichtlinie Standard { get; } = new(8, 5, 15, 30, true);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Regeln(int Fehlversuche, int SperrdauerMinuten, int InaktivitaetMinuten, bool BekanntePasswoerterSperren);

    /// <summary>Text für die Spalte passwortregeln. Die Mindestlänge steht in ihrer eigenen Spalte.</summary>
    public string RegelnAlsText() =>
        JsonSerializer.Serialize(new Regeln(Fehlversuche, SperrdauerMinuten, InaktivitaetMinuten, BekanntePasswoerterSperren), Json);

    /// <summary>
    /// Liest die gespeicherte Richtlinie. Ältere Firmen haben in passwortregeln nur einen Satz wie
    /// „Mindestens 8 Zeichen“; dann gelten für die übrigen Werte die Standardwerte.
    /// </summary>
    public static PasswortRichtlinie AusDatenbank(int mindestlaenge, string? regelnText)
    {
        Regeln? regeln = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(regelnText) && regelnText.TrimStart().StartsWith('{'))
                regeln = JsonSerializer.Deserialize<Regeln>(regelnText, Json);
        }
        catch (JsonException)
        {
            regeln = null;
        }

        return regeln is null
            ? Standard with { Mindestlaenge = mindestlaenge }
            : new PasswortRichtlinie(mindestlaenge, regeln.Fehlversuche, regeln.SperrdauerMinuten,
                regeln.InaktivitaetMinuten, regeln.BekanntePasswoerterSperren);
    }

    /// <summary>Prüft die Eingaben aus K04. Wirft eine verständliche Meldung, wenn ein Wert nicht passt.</summary>
    public PasswortRichtlinie Pruefen()
    {
        // 72 Bytes ist die Grenze von BCrypt; mehr als 72 Zeichen Mindestlänge wären nicht erfüllbar.
        Bereich(Mindestlaenge, 8, 72, "Mindestlänge");
        Bereich(Fehlversuche, 3, 20, "Fehlversuche bis temporäre Sperre");
        Bereich(SperrdauerMinuten, 1, 1440, "Sperrdauer (Minuten)");
        Bereich(InaktivitaetMinuten, 5, 480, "Inaktivität bis Abmeldung (Minuten)");
        return this;
    }

    /// <summary>Prüft ein neues Passwort gegen diese Richtlinie. null = in Ordnung, sonst die Meldung.</summary>
    public string? PasswortFehler(string passwort)
    {
        if (passwort.Length < Mindestlaenge)
            return $"Das Passwort braucht mindestens {Mindestlaenge} Zeichen.";
        if (BekanntePasswoerterSperren && HaeufigePasswoerter.Contains(passwort.Trim().ToLowerInvariant()))
            return "Dieses Passwort ist zu bekannt und deshalb gesperrt. Bitte ein anderes wählen.";
        return null;
    }

    private static void Bereich(int wert, int minimum, int maximum, string feld)
    {
        if (wert < minimum || wert > maximum)
            throw new ArgumentException($"{feld}: Bitte eine Zahl von {minimum} bis {maximum} eingeben.");
    }

    // Kleine Liste sehr häufiger Passwörter. Eine vollständige Prüfung gegen bekannte Datenlecks
    // bräuchte einen externen Dienst und ist für den Prototyp nicht vorgesehen.
    private static readonly HashSet<string> HaeufigePasswoerter = new(StringComparer.Ordinal)
    {
        "12345678", "123456789", "1234567890", "123123123", "11111111", "00000000", "87654321",
        "password", "password1", "passwort", "passwort1", "qwertz123", "qwerty123", "qwertzui",
        "qwertyui", "iloveyou", "abc12345", "abcd1234", "hallo123", "hallo1234", "willkommen",
        "willkommen1", "sommer2026", "winter2026", "geheim123", "admin123", "admin1234",
        "letmein1", "disposio", "disposio1", "disposio123", "fussball", "schalke04", "dortmund09"
    };
}
