import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:geolocator/geolocator.dart';
import 'package:permission_handler/permission_handler.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../texte.dart';
import 'api_client.dart';
import 'sitzung.dart';

/// Phase 3: GPS waehrend der Fahrt.
/// Laeuft nur, solange die Tour Unterwegs ist, als Android-Vordergrunddienst mit Benachrichtigung.
/// Alle 30 Sekunden oder nach 100 Metern wird ein Punkt im Puffer gespeichert (shared_preferences).
/// Der Puffer wird alle 60 Sekunden gesammelt gesendet. Offline bleibt er liegen und wird nachgesendet.
/// Antwortet die API mit 409 (Tour pausiert oder abgeschlossen), wird der Puffer der Tour geleert und GPS gestoppt.
class GpsDienst {
  static const _pufferSchluessel = 'gps_puffer';
  static const _abstandZeit = Duration(seconds: 30);
  static const _abstandMeter = 100.0;
  static const _sendeTakt = Duration(seconds: 60);
  static const _maxJeAnfrage = 100;

  /// true, solange GPS fuer [tourId] laeuft.
  static final aktiv = ValueNotifier<bool>(false);

  /// Anzahl der Punkte im Puffer (alle Touren).
  static final ausstehend = ValueNotifier<int>(0);

  static int? _tourId;
  static int? get tourId => _tourId;

  static final _api = ApiClient();
  static StreamSubscription<Position>? _abo;
  static Timer? _takt;
  static List<Map<String, dynamic>> _puffer = [];
  static Position? _letzterPunkt;
  static DateTime? _letzteZeit;
  static Future<bool>? _laufendesSenden;

  /// Beim App-Start: Puffer laden und den Sendetakt starten. Beim Abmelden wird GPS gestoppt.
  static Future<void> init() async {
    final prefs = await SharedPreferences.getInstance();
    final gespeichert = prefs.getString(_pufferSchluessel);
    if (gespeichert != null) {
      try {
        _puffer = [for (final p in jsonDecode(gespeichert) as List) Map<String, dynamic>.from(p as Map)];
      } catch (_) {
        _puffer = [];
      }
    }
    ausstehend.value = _puffer.length;
    _takt ??= Timer.periodic(_sendeTakt, (_) => jetztSenden());
    Sitzung.angemeldet.addListener(() {
      if (Sitzung.angemeldet.value == false) stoppen();
    });
  }

  /// Punkte im Puffer fuer eine Tour.
  static int ausstehendFuer(int tourId) => _puffer.where((p) => p['tourId'] == tourId).length;

  /// Standortberechtigung vorhanden und Standort eingeschaltet?
  static Future<bool> berechtigt() async {
    final erlaubnis = await Geolocator.checkPermission();
    return erlaubnis == LocationPermission.always || erlaubnis == LocationPermission.whileInUse;
  }

  /// Fragt Standort und Benachrichtigungen an. Gibt zurueck, ob der Standort erlaubt ist.
  static Future<bool> berechtigungAnfragen() async {
    final standort = await Permission.location.request();
    // Ohne Benachrichtigung laeuft GPS trotzdem, Android zeigt sie dann nur nicht an
    await Permission.notification.request();
    return standort.isGranted || standort.isLimited;
  }

  /// Startet GPS fuer die Tour. Ohne Berechtigung oder bei ausgeschaltetem Standort passiert nichts (false).
  static Future<bool> starten(int tourId) async {
    if (aktiv.value && _tourId == tourId) return true;
    await stoppen();
    if (!await berechtigt() || !await Geolocator.isLocationServiceEnabled()) return false;

    _tourId = tourId;
    _letzterPunkt = null;
    _letzteZeit = null;
    final einstellungen = AndroidSettings(
      accuracy: LocationAccuracy.high,
      distanceFilter: 0,
      // Haeufig messen, gespeichert wird nur nach 30 s oder 100 m (siehe _merken)
      intervalDuration: const Duration(seconds: 5),
      foregroundNotificationConfig: ForegroundNotificationConfig(
        notificationTitle: 'Disposio',
        notificationText: Texte.t('gps_benachrichtigung'),
        notificationChannelName: Texte.t('gps_kanal'),
        setOngoing: true,
        enableWakeLock: true,
      ),
    );
    _abo = Geolocator.getPositionStream(locationSettings: einstellungen).listen(
      _merken,
      onError: (Object _) => stoppen(),
    );
    aktiv.value = true;
    return true;
  }

  /// Stoppt GPS. Die Benachrichtigung verschwindet. Der Puffer bleibt und wird weiter gesendet.
  static Future<void> stoppen() async {
    final abo = _abo;
    _abo = null;
    _tourId = null;
    aktiv.value = false;
    await abo?.cancel();
  }

  static void _merken(Position p) {
    final tourId = _tourId;
    if (tourId == null) return;
    final jetzt = DateTime.now();
    final letzter = _letzterPunkt;
    if (letzter != null && _letzteZeit != null) {
      final meter = Geolocator.distanceBetween(letzter.latitude, letzter.longitude, p.latitude, p.longitude);
      if (jetzt.difference(_letzteZeit!) < _abstandZeit && meter < _abstandMeter) return;
    }
    _letzterPunkt = p;
    _letzteZeit = jetzt;
    _puffer.add({
      'tourId': tourId,
      'breitengrad': p.latitude,
      'laengengrad': p.longitude,
      'erfasstAm': p.timestamp.toUtc().toIso8601String(),
      'genauigkeitM': p.accuracy,
    });
    _speichern();
  }

  /// Sendet den Puffer sofort. Gibt true zurueck, wenn danach nichts mehr aussteht.
  /// Laeuft schon ein Senden, wird darauf gewartet.
  static Future<bool> jetztSenden() => _laufendesSenden ??= _senden().whenComplete(() => _laufendesSenden = null);

  static Future<bool> _senden() async {
    if (Sitzung.token == null) return _puffer.isEmpty;
    final touren = {for (final p in _puffer) p['tourId'] as int};
    for (final tour in touren) {
      while (true) {
        final teil = _puffer.where((p) => p['tourId'] == tour).take(_maxJeAnfrage).toList();
        if (teil.isEmpty) break;
        try {
          await _api.positionenSenden(tour, [
            for (final p in teil) {for (final e in p.entries) if (e.key != 'tourId') e.key: e.value},
          ]);
          _puffer.removeWhere((p) => teil.any((t) => identical(t, p)));
        } on ApiFehler catch (e) {
          if (e.code == 'server_nicht_erreichbar' || e.code == 'anmeldung_fehlgeschlagen') {
            // Offline oder abgemeldet: liegen lassen und spaeter nachsenden
            _speichern();
            return false;
          }
          if (e.code == 'tour_nicht_unterwegs' || e.code == 'tour_nicht_gefunden') {
            // Tour pausiert oder abgeschlossen: Puffer der Tour verwerfen und GPS stoppen
            _puffer.removeWhere((p) => p['tourId'] == tour);
            if (_tourId == tour) await stoppen();
          } else {
            // Sonst (z. B. 400) die Punkte nicht endlos erneut senden
            _puffer.removeWhere((p) => teil.any((t) => identical(t, p)));
          }
        }
        _speichern();
      }
    }
    return _puffer.isEmpty;
  }

  static Future<void> _speichern() async {
    ausstehend.value = _puffer.length;
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_pufferSchluessel, jsonEncode(_puffer));
  }
}
