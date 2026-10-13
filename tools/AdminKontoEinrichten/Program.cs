using System.Text;
using Disposio.AdminEinrichtung;
using Disposio.Services;

// Das Werkzeug wird lokal gestartet. Passwörter gehören nicht in Chat, Startargumente,
// Quellcode oder Protokolle. ReadKey liest sie direkt und zeigt nur Sternchen an.
Console.OutputEncoding = Encoding.UTF8;
int exitCode = 1;
Console.WriteLine("Disposio – neues Plattformkonto einrichten");
Console.WriteLine("Konto: admin@disposio.com | Rolle: SuperKrasserAdmin | ohne Kundenfirma");
Console.WriteLine("Ein vorhandenes Konto wird niemals verändert.");
try
{
    if (args.Length != 0 || Console.IsInputRedirected)
        throw new InvalidOperationException("Bitte dieses Programm ohne Startargumente in einer lokalen Konsole öffnen.");

    Console.WriteLine("Wähle mindestens 15 Zeichen (höchstens 72 UTF-8-Bytes). Escape bricht ab.");
    string passwort = PasswortLesen("Neues Passwort: ");
    string wiederholung = PasswortLesen("Passwort wiederholen: ");
    if (!string.Equals(passwort, wiederholung, StringComparison.Ordinal))
        throw new ArgumentException("Die beiden Passwörter stimmen nicht überein. Bitte erneut starten.");

    Console.WriteLine("Konto wird gespeichert …");
    await PlattformAdminEinrichtung.AnlegenAsync(PlattformAdminEinrichtung.StandardEmail, passwort);
    // .NET-Strings lassen sich nicht garantiert aus dem Speicher entfernen. Wir behalten
    // sie deshalb nicht als Einstellungen und beenden dieses Einrichtungsprogramm danach.
    passwort = wiederholung = string.Empty;
    Console.WriteLine("Fertig. In Disposio den Kontotyp MegakrassenAdmin auswählen.");
    Console.WriteLine("E-Mail: admin@disposio.com | Passwort: das eben selbst gewählte Passwort.");
    exitCode = 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Abgebrochen. Es wurde kein Konto angelegt.");
}
catch (NeonSqlException exception) when (exception.Code == "23505")
{
    Console.WriteLine("Das Plattformkonto existiert bereits. Es wurde nicht verändert.");
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

static string PasswortLesen(string hinweis)
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
            Console.Write('*');
        }
    }
}
