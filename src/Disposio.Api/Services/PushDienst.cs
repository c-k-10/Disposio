using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

namespace Disposio.Api.Services;

/// <summary>
/// v0.7: Push-Benachrichtigungen ueber Firebase Cloud Messaging an Themen (fahrer_&lt;benutzer_id&gt;).
/// Dienstkonto-JSON: Pfad lokal ueber User-Secret Firebase:CredentialsPfad,
/// auf Render Secret File /etc/secrets/firebase.json und Umgebungsvariable Firebase__CredentialsPfad.
/// Ohne Pfad oder mit unlesbarer Datei laeuft die API normal weiter, es wird nur nichts gesendet.
/// </summary>
public sealed class PushDienst
{
    /// <summary>Kanal-ID wie in der App (Android-Kanal "Disposio Benachrichtigungen" mit eigenem Ton).
    /// v0.7.1: neue ID, weil Android den Ton eines bestehenden Kanals nicht aendern kann.</summary>
    public const string KanalId = "disposio_ton";

    /// <summary>Name der Ton-Datei in der App (android/app/src/main/res/raw/ton.wav, ohne Endung).</summary>
    public const string Ton = "ton";

    private readonly FirebaseMessaging? _messaging;
    private readonly ILogger<PushDienst> _log;

    public PushDienst(IConfiguration konfiguration, ILogger<PushDienst> log)
    {
        _log = log;
        var pfad = konfiguration["Firebase:CredentialsPfad"];
        if (string.IsNullOrWhiteSpace(pfad))
        {
            _log.LogWarning("Firebase:CredentialsPfad fehlt - Push-Benachrichtigungen sind aus");
            return;
        }
        if (!File.Exists(pfad))
        {
            _log.LogWarning("Firebase-Dienstkonto nicht gefunden - Push-Benachrichtigungen sind aus");
            return;
        }

        try
        {
            var app = FirebaseApp.DefaultInstance ?? FirebaseApp.Create(new AppOptions
            {
                Credential = CredentialFactory.FromFile<ServiceAccountCredential>(pfad).ToGoogleCredential(),
            });
            _messaging = FirebaseMessaging.GetMessaging(app);
            _log.LogInformation("Push-Benachrichtigungen aktiv");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Firebase-Dienstkonto ungueltig - Push-Benachrichtigungen sind aus");
        }
    }

    public bool Aktiv => _messaging is not null;

    public static string Thema(long benutzerId) => $"fahrer_{benutzerId}";

    /// <summary>Sendet an das Thema des Fahrers. Fehler werden nur geloggt.</summary>
    public async Task<bool> SendenAsync(long benutzerId, string titel, string text,
        IReadOnlyDictionary<string, string> daten, CancellationToken abbruch)
    {
        if (_messaging is null)
        {
            return false;
        }
        try
        {
            await _messaging.SendAsync(new Message
            {
                Topic = Thema(benutzerId),
                Notification = new Notification { Title = titel, Body = text },
                Data = daten,
                Android = new AndroidConfig
                {
                    Priority = Priority.High,
                    Notification = new AndroidNotification
                    {
                        ChannelId = KanalId,
                        Sound = Ton,
                        Priority = NotificationPriority.HIGH,
                    },
                },
            }, abbruch);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Push an Benutzer {BenutzerId} fehlgeschlagen", benutzerId);
            return false;
        }
    }
}
