import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:http/http.dart' as http;

import '../texte.dart';
import 'sitzung.dart';

/// Adresse der Disposio.Api.
/// Beim Starten mitgeben, z. B.:
///   flutter run --dart-define=API_URL=https://disposio.onrender.com
/// Ohne Angabe wird die lokale API genommen (10.0.2.2 = der PC aus Sicht des Emulators).
const apiUrl = String.fromEnvironment(
  'API_URL',
  defaultValue: 'http://10.0.2.2:5080',
);

/// Fehler der API. [code] ist ein Schluessel aus texte.dart, z. B. 'anmeldung_fehlgeschlagen'.
class ApiFehler implements Exception {
  ApiFehler(this.code);

  final String code;

  String get text => Texte.t(code);

  @override
  String toString() => code;
}

class LoginErgebnis {
  LoginErgebnis(this.json)
      : vorname = json['vorname'] as String,
        nachname = json['nachname'] as String,
        sprache = json['sprache'] as String;

  final Map<String, dynamic> json;
  final String vorname;
  final String nachname;
  final String sprache;
}

class Profil {
  Profil.ausJson(Map<String, dynamic> json)
      : vorname = json['vorname'] as String,
        nachname = json['nachname'] as String,
        email = json['email'] as String,
        firmenname = json['firmenname'] as String,
        fuehrerscheinklasse = json['fuehrerscheinklasse'] as String,
        verfuegbarkeit = json['verfuegbarkeit'] as String,
        sprache = json['sprache'] as String;

  final String vorname;
  final String nachname;
  final String email;
  final String firmenname;
  final String fuehrerscheinklasse;
  final String verfuegbarkeit;
  final String sprache;
}

class ApiClient {
  ApiClient({http.Client? client}) : _client = client ?? http.Client();

  final http.Client _client;

  // Render braucht beim Aufwachen bis zu ~1 Minute
  static const _zeitlimit = Duration(seconds: 60);

  /// Phase 1 (M01): Anmeldung. Speichert das Token bei Erfolg.
  Future<LoginErgebnis> login(String kennung, String email, String passwort, String sprache) async {
    final antwort = await _senden(() => _client.post(
          Uri.parse('$apiUrl/auth/login'),
          headers: _kopf(mitToken: false),
          body: jsonEncode({'kennung': kennung, 'email': email, 'passwort': passwort, 'sprache': sprache}),
        ));
    final daten = _auswerten(antwort, abmeldenBei401: false) as Map<String, dynamic>;
    await Sitzung.anmelden(daten['token'] as String);
    return LoginErgebnis(daten);
  }

  /// Phase 1 (M09): Profil des angemeldeten Fahrers.
  Future<Profil> me() async {
    final antwort = await _senden(() => _client.get(Uri.parse('$apiUrl/me'), headers: _kopf()));
    return Profil.ausJson(_auswerten(antwort) as Map<String, dynamic>);
  }

  Future<void> spracheSetzen(String sprache) async {
    final antwort = await _senden(() => _client.put(Uri.parse('$apiUrl/me/sprache'),
        headers: _kopf(), body: jsonEncode({'sprache': sprache})));
    _auswerten(antwort);
  }

  Future<void> verfuegbarkeitSetzen(String verfuegbarkeit) async {
    final antwort = await _senden(() => _client.put(Uri.parse('$apiUrl/me/verfuegbarkeit'),
        headers: _kopf(), body: jsonEncode({'verfuegbarkeit': verfuegbarkeit})));
    _auswerten(antwort);
  }

  /// Phase 0: prueft, ob API und Datenbank erreichbar sind.
  /// Gibt einen lesbaren Text fuer die Anzeige zurueck.
  Future<String> verbindungTesten() async {
    try {
      final antwort = await _client.get(Uri.parse('$apiUrl/health')).timeout(_zeitlimit);
      final daten = jsonDecode(antwort.body) as Map<String, dynamic>;
      if (antwort.statusCode == 200) {
        return Texte.t('verbindung_ok');
      }
      return '${Texte.t('verbindung_db')} ${daten['datenbank']}';
    } catch (e) {
      return '${Texte.t('verbindung_fehler')} ($e)';
    }
  }

  Map<String, String> _kopf({bool mitToken = true}) => {
        'Content-Type': 'application/json',
        if (mitToken && Sitzung.token != null) 'Authorization': 'Bearer ${Sitzung.token}',
      };

  Future<http.Response> _senden(Future<http.Response> Function() anfrage) async {
    try {
      return await anfrage().timeout(_zeitlimit);
    } on SocketException {
      throw ApiFehler('server_nicht_erreichbar');
    } on TimeoutException {
      throw ApiFehler('server_nicht_erreichbar');
    } on http.ClientException {
      throw ApiFehler('server_nicht_erreichbar');
    }
  }

  /// Wertet die Antwort aus. Bei 401 (Token abgelaufen oder Konto gesperrt) wird abgemeldet,
  /// die App zeigt dann automatisch den Login.
  Object? _auswerten(http.Response antwort, {bool abmeldenBei401 = true}) {
    if (antwort.statusCode >= 200 && antwort.statusCode < 300) {
      return antwort.body.isEmpty ? null : jsonDecode(antwort.body);
    }
    if (antwort.statusCode == 401 && abmeldenBei401) {
      Sitzung.abmelden();
    }
    String? code;
    try {
      code = (jsonDecode(antwort.body) as Map<String, dynamic>)['fehler'] as String?;
    } catch (_) {
      // Kein JSON, z. B. leere 401-Antwort
    }
    throw ApiFehler(code ?? (antwort.statusCode == 401 ? 'anmeldung_fehlgeschlagen' : 'unbekannter_fehler'));
  }
}
