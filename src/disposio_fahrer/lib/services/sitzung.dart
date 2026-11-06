import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Haelt das Anmelde-Token sicher auf dem Geraet (Android Keystore)
/// und merkt sich die letzte Kennung, E-Mail und Sprache fuer den Login.
class Sitzung {
  static const _speicher = FlutterSecureStorage();

  /// true = angemeldet, false = Login zeigen, null = wird noch geladen.
  static final angemeldet = ValueNotifier<bool?>(null);

  static String? _token;
  static String? get token => _token;

  static Future<void> laden() async {
    _token = await _speicher.read(key: 'token');
    angemeldet.value = _token != null;
  }

  static Future<void> anmelden(String token) async {
    _token = token;
    await _speicher.write(key: 'token', value: token);
    angemeldet.value = true;
  }

  /// Loescht das Token. Kennung, E-Mail und Sprache bleiben fuer den naechsten Login.
  static Future<void> abmelden() async {
    _token = null;
    await _speicher.delete(key: 'token');
    angemeldet.value = false;
  }

  static Future<void> letzteEingabenMerken(String kennung, String email, String sprache) async {
    await _speicher.write(key: 'kennung', value: kennung);
    await _speicher.write(key: 'email', value: email);
    await spracheMerken(sprache);
  }

  static Future<void> spracheMerken(String sprache) => _speicher.write(key: 'sprache', value: sprache);

  static Future<String?> lesen(String schluessel) => _speicher.read(key: schluessel);
}
