import 'dart:convert';

import 'package:http/http.dart' as http;

/// Adresse der Disposio.Api.
/// Beim Starten mitgeben, z. B.:
///   flutter run --dart-define=API_URL=https://disposio-api.onrender.com
/// Ohne Angabe wird die lokale API genommen (10.0.2.2 = der PC aus Sicht des Emulators).
const apiUrl = String.fromEnvironment(
  'API_URL',
  defaultValue: 'http://10.0.2.2:5080',
);

class ApiClient {
  ApiClient({http.Client? client}) : _client = client ?? http.Client();

  final http.Client _client;

  /// Phase 0: prueft, ob API und Datenbank erreichbar sind.
  /// Gibt einen lesbaren Text fuer die Anzeige zurueck.
  Future<String> verbindungTesten() async {
    try {
      final antwort = await _client
          .get(Uri.parse('$apiUrl/health'))
          .timeout(const Duration(seconds: 60)); // Render braucht beim Aufwachen bis zu ~1 Minute
      final daten = jsonDecode(antwort.body) as Map<String, dynamic>;
      if (antwort.statusCode == 200) {
        return 'API und Datenbank erreichbar';
      }
      return 'API erreichbar, Datenbank: ${daten['datenbank']}';
    } catch (e) {
      return 'API nicht erreichbar ($e)';
    }
  }
}
