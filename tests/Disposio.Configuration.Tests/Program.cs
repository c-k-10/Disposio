using Disposio.Services;
using Npgsql;

// Nur synthetische Testdaten. Keine Datenbankanfragen und keine dauerhafte Speicherung.
if (args.SequenceEqual(new[] { "--check-local" }))
{
    try
    {
        var value = Environment.GetEnvironmentVariable(Database.ConnectionStringEnvironmentVariable, EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(Database.ConnectionStringEnvironmentVariable);
        Database.ParseConnectionString(value!);
        Console.WriteLine("OK: Lokale Verbindung ist lesbar. Zugangsdaten wurden nicht ausgegeben. Datenbankzugriff wurde nicht getestet.");
        return;
    }
    catch (InvalidOperationException exception)
    {
        Console.WriteLine(exception.Message);
        Environment.ExitCode = 1;
        return;
    }
}

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("Prüfung fehlgeschlagen: " + name);
    checks++;
}
void Reject(string? value)
{
    try
    {
        Database.ParseConnectionString(value!);
        throw new Exception("Ungültige Verbindung wurde angenommen.");
    }
    catch (InvalidOperationException exception)
    {
        Check(!exception.Message.Contains("TEST_SECRET") && exception.InnerException is null, "Fehler verrät keine Eingabe");
    }
}
const string host = "ep-test.eu-central-1.aws.neon.tech";
const string secret = "TEST_SECRET;=:@/+ü%3B\"";
var original = new NpgsqlConnectionStringBuilder
{
    Host = host, Database = "test database", Username = "test+user", Password = secret,
    SslMode = SslMode.VerifyFull
};
var baselineUrl = $"postgresql://test:TEST_SECRET@{host}/test?sslmode=require";
try
{
    _ = new NpgsqlConnectionStringBuilder(baselineUrl);
    throw new Exception("Erwartete alte Formatbegrenzung wurde nicht reproduziert.");
}
catch (ArgumentException) { Check(true, "Alter Parser lehnt eine gültige Neon-URL ab"); }
var semicolon = original.ConnectionString.Replace("Username=", "User ID=");
foreach (var text in new[] { semicolon, " " + semicolon + " ", "'" + semicolon + "'" })
{
    var parsed = Database.ParseConnectionString(text);
    Check(parsed.Password == secret && parsed.Username == original.Username, "Npgsql-Aliase und Passwortzeichen");
}
foreach (var scheme in new[] { "postgresql", "postgres", "POSTGRESQL" })
{
    var url = $"{scheme}://{Uri.EscapeDataString(original.Username)}:{Uri.EscapeDataString(secret)}@{host}/{Uri.EscapeDataString(original.Database)}?sslmode=require&channel_binding=require";
    foreach (var text in new[] { url, " " + url + " ", "'" + url + "'", "\"" + url + "\"", "psql '" + url + "'", "psql \"" + url + "\"" })
    {
        var parsed = Database.ParseConnectionString(text);
        Check(parsed.Password == secret && parsed.Username == original.Username && parsed.Database == original.Database,
            "Neon-URL und kopierte psql-Zeile erhalten die Verbindungsdaten");
        Check(parsed.Port == 5432 && parsed.SslMode == SslMode.VerifyFull, "Standardport und TLS");
        Check(parsed.ConnectionString.Contains("Channel Binding=Require", StringComparison.OrdinalIgnoreCase), "Channel Binding");
    }
}
Check(Database.ParseConnectionString("Host=localhost;Database=test;Username=test;Password=TEST_SECRET").Host == "localhost", "Bestehende Npgsql-Hosts bleiben lesbar");
Check(Database.ParseConnectionString($"postgresql://test:TEST_SECRET@{host}:6543/test").Port == 6543, "Expliziter Port");
Check(Database.ParseConnectionString($"postgresql://test:TEST_SECRET+a%253B@{host}/test").Password == "TEST_SECRET+a%3B", "Plus und genau einmal dekodieren");
foreach (var text in new string?[]
{
    null, "", " ", "TEST_SECRET", "Password=TEST_SECRET;Port=invalid",
    $"postgresql://test@{host}/test", $"postgresql://test:TEST_SECRET@{host}/",
    $"postgresql://test:TEST_SECRET@{host}/test#fragment",
    $"postgresql://test:TEST_SECRET@{host}:0/test",
    $"postgresql://test:TEST_SECRET@{host}/test?sslmode=disable",
    $"postgresql://test:TEST_SECRET@{host}/test?sslmode=require&sslmode=verify-full",
    $"postgresql://test:TEST_SECRET@{host}/test?unknown=value",
    $"postgresql://test:TEST_SECRET%ZZ@{host}/test",
    $"psql 'postgresql://test:TEST_SECRET@{host}/test' --other",
    "psql 'TEST_SECRET'", "Host=" + host + ";Database=test;Username=test"
}) Reject(text);

var previous = Environment.GetEnvironmentVariable(Database.ConnectionStringEnvironmentVariable);
try
{
    Environment.SetEnvironmentVariable(Database.ConnectionStringEnvironmentVariable,
        $"postgresql://test:TEST_SECRET@{host}/test?sslmode=require");
    Check(Database.GetSettings().Host == host, "Umgebungsvariable darf URL enthalten");
    _ = new NeonSqlClient();
    Check(true, "HTTPS-Client akzeptiert die URL ohne Netzwerkzugriff");
    Environment.SetEnvironmentVariable(Database.ConnectionStringEnvironmentVariable,
        "Host=x.evil.test/path.neon.tech;Database=test;Username=test;Password=TEST_SECRET");
    try { _ = new NeonSqlClient(); throw new Exception("Unsicherer Host angenommen"); }
    catch (InvalidOperationException exception) { Check(!exception.Message.Contains("TEST_SECRET"), "HTTPS-Hostschutz bleibt aktiv"); }
}
finally { Environment.SetEnvironmentVariable(Database.ConnectionStringEnvironmentVariable, previous); }

// US-05 (#90, #92): Ampel für Wartungstermine. Reine Rechenregeln, ohne Datenbank.
{
    var heute = new DateOnly(2026, 11, 5);
    Disposio.Models.WartungsAmpel A(DateOnly termin, int? faelligKm = null, int aktuellKm = 0) =>
        Disposio.Models.WartungsRegeln.Bewerten(termin, faelligKm, aktuellKm, heute);
    Check(A(heute.AddDays(-1)) == Disposio.Models.WartungsAmpel.Ueberfaellig, "Wartung: gestern ist überfällig");
    Check(A(heute) == Disposio.Models.WartungsAmpel.BaldFaellig, "Wartung: heute ist bald fällig, nicht überfällig");
    Check(A(heute.AddDays(14)) == Disposio.Models.WartungsAmpel.BaldFaellig, "Wartung: in 14 Tagen ist bald fällig");
    Check(A(heute.AddDays(15)) == Disposio.Models.WartungsAmpel.Geplant, "Wartung: in 15 Tagen ist geplant");
    Check(A(heute.AddDays(60), 100_000, 100_000) == Disposio.Models.WartungsAmpel.Ueberfaellig, "Wartung: Kilometerstand erreicht");
    Check(A(heute.AddDays(60), 100_000, 99_000) == Disposio.Models.WartungsAmpel.BaldFaellig, "Wartung: 1.000 km vorher bald fällig");
    Check(A(heute.AddDays(60), 100_000, 98_999) == Disposio.Models.WartungsAmpel.Geplant, "Wartung: mehr als 1.000 km vorher geplant");

    var frueh = new Disposio.Models.Wartungstermin(1, 7, "N-DS 101", "Inspektion", heute.AddDays(30), null, 50_000, "");
    var kmFaellig = new Disposio.Models.Wartungstermin(2, 7, "N-DS 101", "Ölwechsel", heute.AddDays(90), 50_000, 50_000, "");
    Check(Disposio.Models.WartungsRegeln.DringendsterTermin(new[] { frueh, kmFaellig }, heute) == kmFaellig,
        "Wartung: Fahrzeugstatus nimmt den dringendsten, nicht den frühesten Termin");
    Check(Disposio.Models.WartungsRegeln.DringendsterTermin(Array.Empty<Disposio.Models.Wartungstermin>(), heute) is null,
        "Wartung: ohne Termin kein Status");
}

Console.WriteLine($"PASS: {checks} Konfigurations- und Wartungsprüfungen ohne Netzwerk oder Speicherung.");
