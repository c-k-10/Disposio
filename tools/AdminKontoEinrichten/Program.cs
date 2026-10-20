using System.Text;
using Disposio.AdminEinrichtung;
using Disposio.Services;

// Das Werkzeug wird lokal gestartet. Passwörter gehören nicht in Chat, Startargumente,
// Quellcode oder Protokolle. ReadKey liest sie direkt und zeigt nur Sternchen an.
Console.OutputEncoding = Encoding.UTF8;
int exitCode = 1;
Console.WriteLine("Disposio – neues Plattformkonto (MegakrassenAdmin) einrichten");
Console.WriteLine("Rolle: SuperKrasserAdmin | ohne Kundenfirma | ein Konto je Person");
Console.WriteLine("Ein vorhandenes Konto wird niemals verändert. Escape bricht jederzeit ab.");
try
{
    if (args.Length != 0 || Console.IsInputRedirected)
        throw new InvalidOperationException("Bitte dieses Programm ohne Startargumente in einer lokalen Konsole öffnen.");

    string email = EingabeLesen("E-Mail (z. B. vorname@disposio.com): ", verdeckt: false).Trim().ToLowerInvariant();
    string vorname = EingabeLesen("Vorname: ", verdeckt: false);
    string nachname = EingabeLesen("Nachname: ", verdeckt: false);

    // Das Passwort tippt die Person selbst ein, der das Konto gehört.
    Console.WriteLine("Jetzt das eigene Passwort: mindestens 15 Zeichen (höchstens 72 UTF-8-Bytes).");
    string passwort = EingabeLesen("Neues Passwort: ", verdeckt: true);
    string wiederholung = EingabeLesen("Passwort wiederholen: ", verdeckt: true);
    if (!string.Equals(passwort, wiederholung, StringComparison.Ordinal))
        throw new ArgumentException("Die beiden Passwörter stimmen nicht überein. Bitte erneut starten.");

    Console.WriteLine("Konto wird gespeichert …");
    await PlattformAdminEinrichtung.AnlegenAsync(email, vorname, nachname, passwort);
    // .NET-Strings lassen sich nicht garantiert aus dem Speicher entfernen. Wir behalten
    // sie deshalb nicht als Einstellungen und beenden dieses Einrichtungsprogramm danach.
    passwort = wiederholung = string.Empty;
    Console.WriteLine($"Fertig. Konto für {vorname.Trim()} {nachname.Trim()} angelegt.");
    Console.WriteLine("In Disposio den Kontotyp MegakrassenAdmin auswählen.");
    Console.WriteLine($"E-Mail: {email} | Passwort: das eben selbst gewählte Passwort.");
    exitCode = 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Abgebrochen. Es wurde kein Konto angelegt.");
}
catch (NeonSqlException exception) when (exception.Code == "23505")
{
    Console.WriteLine("Ein Plattformkonto mit dieser E-Mail existiert bereits. Es wurde nicht verändert.");
}
catch (ArgumentException exception)
{
    // Diese Meldungen stammen aus unserer Eingabeprüfung und enthalten keine Passwörter.
    Console.WriteLine(exception.Message);
}
catch (InvalidOperationException exception)
{
    Console.WriteLine(exception.Message);
}
catch (Exception)
{
    // Keine Serverantwort oder Verbindungsdaten ausgeben. Ein Verbindungsabbruch kann
    // nach dem Speichern auftreten: Ein erneuter Start überschreibt das Konto trotzdem nicht.
    Console.WriteLine("Einrichtung nicht bestätigt. Prüfe Verbindung und Datenbankmigration und versuche es erneut.");
}
Console.WriteLine("Zum Schließen eine Taste drücken.");
if (!Console.IsInputRedirected) Console.ReadKey(intercept: true);
return exitCode;

// Liest eine Eingabe Taste für Taste. Verdeckt zeigt nur Sternchen (für Passwörter).
static string EingabeLesen(string hinweis, bool verdeckt)
{
    Console.Write(hinweis);
    var eingabe = new StringBuilder();
    while (true)
    {
        ConsoleKeyInfo taste = Console.ReadKey(intercept: true);
        if (taste.Key == ConsoleKey.Escape) throw new OperationCanceledException();
        if (taste.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return eingabe.ToString();
        }
        if (taste.Key == ConsoleKey.Backspace)
        {
            if (eingabe.Length > 0)
            {
                eingabe.Length--;
                Console.Write("\b \b");
            }
        }
        else if (!char.IsControl(taste.KeyChar) && eingabe.Length < 256)
        {
            eingabe.Append(taste.KeyChar);
            Console.Write(verdeckt ? '*' : taste.KeyChar);
        }
    }
}
