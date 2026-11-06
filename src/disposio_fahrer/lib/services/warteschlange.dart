import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:path_provider/path_provider.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:uuid/uuid.dart';

import 'api_client.dart';
import 'sitzung.dart';

/// Ein Eintrag der Warteschlange: Nachweis, Dokument, Tankvorgang oder Schaden.
/// [id] ist zugleich die clientId fuer die API (doppelt gesendet = nur einmal gespeichert).
class WarteEintrag {
  WarteEintrag({
    required this.id,
    required this.art,
    required this.titel,
    required this.daten,
    this.datei,
    this.eltern,
    required this.erstellt,
  });

  WarteEintrag.ausJson(Map<String, dynamic> json)
      : id = json['id'] as String,
        art = json['art'] as String,
        titel = json['titel'] as String,
        daten = Map<String, dynamic>.from(json['daten'] as Map),
        datei = json['datei'] as String?,
        eltern = json['eltern'] as String?,
        erstellt = DateTime.parse(json['erstellt'] as String),
        status = json['status'] as String? ?? Warteschlange.wartet,
        fehler = json['fehler'] as String?;

  final String id;

  /// nachweis, dokument, tank oder schaden
  final String art;

  /// Kurzer Text fuer M11, z. B. "Nachweis A-NILS-21" (schon uebersetzt beim Anlegen).
  final String titel;

  /// Daten fuer die API. Bei Dokumenten: art, eigentuemer (nachweisId/tankvorgangId/schadenId)
  /// und eigentuemerId, sobald der Eltern-Eintrag gesendet ist.
  final Map<String, dynamic> daten;

  /// Bilddatei im App-Ordner (nur Dokumente).
  final String? datei;

  /// Dokument wartet auf diesen Eintrag (Nachweis, Tankvorgang oder Schaden) und bekommt dessen ID.
  final String? eltern;
  final DateTime erstellt;

  String status = Warteschlange.wartet;

  /// Letzte Fehlermeldung (Schluessel aus texte.dart), auch bei „wartet“ nach einem Serverfehler.
  String? fehler;

  Map<String, dynamic> zuJson() => {
        'id': id,
        'art': art,
        'titel': titel,
        'daten': daten,
        'datei': datei,
        'eltern': eltern,
        'erstellt': erstellt.toIso8601String(),
        // „wird gesendet“ wird nach einem Neustart wieder „wartet“
        'status': status == Warteschlange.sendet ? Warteschlange.wartet : status,
        'fehler': fehler,
      };
}

/// Phase 5 (M11): eine gemeinsame Warteschlange fuer Nachweise, Dokumente, Tankvorgaenge und Schaeden.
/// Alles wird zuerst lokal gespeichert (shared_preferences, Bilder im App-Ordner) und dann gesendet:
/// sofort nach dem Anlegen, alle 20 Sekunden und auf Knopfdruck. Reihenfolge wie angelegt; ein Dokument
/// wartet, bis sein Nachweis/Tankvorgang/Schaden gesendet ist, und bekommt dann dessen ID.
/// Offline oder Serverfehler (5xx): bleibt „wartet“ und wird automatisch erneut gesendet.
/// Andere Fehler (z. B. 400/404): „Fehler“, nur „Erneut senden“ in M11 versucht es noch einmal.
class Warteschlange {
  static const wartet = 'wartet';
  static const sendet = 'sendet';
  static const fehlerStatus = 'fehler';

  static const _schluessel = 'warteschlange';
  static const _takt = Duration(seconds: 20);
  static const _uuid = Uuid();

  /// Alle offenen Eintraege (fuer M11), wird bei jeder Aenderung neu gesetzt.
  static final eintraege = ValueNotifier<List<WarteEintrag>>([]);

  static final _api = ApiClient();
  static List<WarteEintrag> _liste = [];
  static Timer? _timer;
  static Future<void>? _laufendesSenden;

  static String neueId() => _uuid.v4();

  static Future<void> init() async {
    final prefs = await SharedPreferences.getInstance();
    final gespeichert = prefs.getString(_schluessel);
    if (gespeichert != null) {
      try {
        _liste = [
          for (final e in jsonDecode(gespeichert) as List) WarteEintrag.ausJson(Map<String, dynamic>.from(e as Map)),
        ];
      } catch (_) {
        _liste = [];
      }
    }
    eintraege.value = List.of(_liste);
    _timer ??= Timer.periodic(_takt, (_) => senden());
    Sitzung.angemeldet.addListener(() {
      if (Sitzung.angemeldet.value == false) _leeren();
    });
  }

  /// Ordner fuer Bilder, die noch gesendet werden muessen.
  static Future<Directory> bildOrdner() async {
    final ordner = Directory('${(await getApplicationDocumentsDirectory()).path}/warteschlange');
    if (!await ordner.exists()) await ordner.create(recursive: true);
    return ordner;
  }

  /// Kopiert ein Bild (Kamera-Cache, Unterschrift) in den App-Ordner, damit es bis zum Senden erhalten bleibt.
  static Future<String> bildSichern(List<int> inhalt, {String endung = 'jpg'}) async {
    final datei = File('${(await bildOrdner()).path}/${neueId()}.$endung');
    await datei.writeAsBytes(inhalt, flush: true);
    return datei.path;
  }

  /// Legt Eintraege an (zuerst der Eltern-Eintrag, dann seine Dokumente) und sendet sofort, wenn Netz da ist.
  static Future<void> hinzufuegen(List<WarteEintrag> neue) async {
    _liste.addAll(neue);
    await _speichern();
    unawaited(senden());
  }

  /// Fehler-Eintrag (und seine wartenden Dokumente) erneut versuchen.
  static Future<void> erneut(String id) async {
    for (final e in _liste) {
      if (e.id == id) {
        e.status = wartet;
        e.fehler = null;
      }
    }
    await _speichern();
    await senden();
  }

  /// Eintrag verwerfen (Fehler oder Tippfehler, nicht waehrend er gesendet wird). Seine Dokumente werden mit verworfen.
  static Future<void> verwerfen(String id) async {
    final weg = {id, for (final e in _liste) if (e.eltern == id) e.id};
    for (final e in _liste.where((e) => weg.contains(e.id))) {
      await _bildLoeschen(e);
    }
    _liste.removeWhere((e) => weg.contains(e.id));
    await _speichern();
  }

  /// Einmal alles senden, was gesendet werden kann. Laeuft schon ein Senden, wird darauf gewartet.
  static Future<void> senden() => _laufendesSenden ??= _senden().whenComplete(() => _laufendesSenden = null);

  static Future<void> _senden() async {
    // Jede Runde neu suchen: ein gesendeter Eltern-Eintrag gibt seine Dokumente frei
    while (Sitzung.token != null) {
      final offene = {for (final e in _liste) e.id};
      final naechster = _liste.where((e) => e.status == wartet && !offene.contains(e.eltern)).firstOrNull;
      if (naechster == null) return;

      naechster.status = sendet;
      eintraege.value = List.of(_liste);
      try {
        final ergebnisId = await _eintragSenden(naechster);
        // Dokumente bekommen die ID ihres Eltern-Eintrags
        for (final kind in _liste.where((e) => e.eltern == naechster.id)) {
          kind.daten['eigentuemerId'] = ergebnisId;
        }
        _liste.remove(naechster);
        await _bildLoeschen(naechster);
      } on ApiFehler catch (e) {
        naechster.fehler = e.code;
        if (e.code == 'anmeldung_fehlgeschlagen' || e.status == 0) {
          // Offline oder abgemeldet: spaeter weiter
          naechster.status = wartet;
          await _speichern();
          return;
        }
        // Serverfehler: spaeter automatisch erneut; sonst Fehler bis „Erneut senden“
        naechster.status = e.voruebergehend ? wartet : fehlerStatus;
        await _speichern();
        if (e.voruebergehend) return;
        continue;
      } on FileSystemException {
        // Bild nicht mehr vorhanden: nicht endlos versuchen
        naechster.status = fehlerStatus;
        naechster.fehler = 'datei_fehlt';
      }
      await _speichern();
    }
  }

  /// Sendet einen Eintrag und gibt die neue ID aus der Antwort zurueck.
  static Future<int?> _eintragSenden(WarteEintrag e) async {
    final daten = {...e.daten, 'clientId': e.id};
    switch (e.art) {
      case 'nachweis':
        final stoppId = daten.remove('stoppId');
        return (await _api.jsonSenden('/stopps/$stoppId/nachweise', daten))['nachweisId'] as int?;
      case 'tank':
        return (await _api.jsonSenden('/tankvorgaenge', daten))['tankvorgangId'] as int?;
      case 'schaden':
        return (await _api.jsonSenden('/schaeden', daten))['schadenId'] as int?;
      default:
        final antwort = await _api.dokumentSenden(e.datei!, {
          'art': daten['art'] as String,
          (daten['eigentuemer'] as String): '${daten['eigentuemerId']}',
          'clientId': e.id,
        });
        return antwort['dokumentId'] as int?;
    }
  }

  /// Bild loeschen, wenn kein anderer Eintrag es noch braucht (z. B. gleiche Unterschrift fuer mehrere Auftraege).
  static Future<void> _bildLoeschen(WarteEintrag e) async {
    final pfad = e.datei;
    if (pfad == null || _liste.any((x) => x != e && x.datei == pfad)) return;
    try {
      await File(pfad).delete();
    } catch (_) {
      // Schon weg
    }
  }

  static Future<void> _speichern() async {
    eintraege.value = List.of(_liste);
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_schluessel, jsonEncode([for (final e in _liste) e.zuJson()]));
  }

  /// Abgemeldet: Warteschlange und Bilder loeschen (das naechste Konto soll sie nicht senden).
  static Future<void> _leeren() async {
    _liste = [];
    await _speichern();
    try {
      final ordner = await bildOrdner();
      await ordner.delete(recursive: true);
    } catch (_) {
      // Nichts zu loeschen
    }
  }
}
