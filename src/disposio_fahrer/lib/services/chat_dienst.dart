import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'api_client.dart';
import 'sitzung.dart';
import 'vorlese_dienst.dart';

/// Nachricht, die noch nicht beim Server angekommen ist (Uhr-Symbol im Verlauf).
class WartendeNachricht {
  WartendeNachricht(this.lokalId, this.chatId, this.text, this.erstellt);

  WartendeNachricht.ausJson(Map<String, dynamic> json)
      : lokalId = json['lokalId'] as int,
        chatId = json['chatId'] as int,
        text = json['text'] as String,
        erstellt = DateTime.parse(json['erstellt'] as String);

  final int lokalId;
  final int chatId;
  final String text;
  final DateTime erstellt;

  Map<String, dynamic> zuJson() =>
      {'lokalId': lokalId, 'chatId': chatId, 'text': text, 'erstellt': erstellt.toIso8601String()};
}

/// Phase 4 (M08): Chat mit der Disposition.
/// Fragt alle 5 Sekunden die Chat-Liste ab (laeuft auch im Hintergrund weiter, waehrend der Fahrt haelt
/// der GPS-Vordergrunddienst die App wach). Hat sich ein Chat geaendert oder ist er offen, werden die
/// neuen Nachrichten geholt. Neue Nachrichten der Disposition werden bei laufender Tour vorgelesen.
/// Ohne Netz bleiben gesendete Nachrichten im Puffer (shared_preferences) und werden nachgesendet.
class ChatDienst {
  static const _takt = Duration(seconds: 5);
  static const _pufferSchluessel = 'chat_puffer';

  /// Chat-Liste (M08), neueste zuerst.
  static final chats = ValueNotifier<List<ChatKurz>>([]);

  /// Ungelesene Nachrichten der Disposition in allen Chats (Abzeichen am Tab).
  static final ungelesen = ValueNotifier<int>(0);

  /// Wird hochgezaehlt, wenn sich ein Verlauf oder der Puffer aendert.
  static final aenderung = ValueNotifier<int>(0);

  /// Fehler beim Senden einer gepufferten Nachricht (Text fuer die Anzeige), danach wieder null.
  static final sendeFehler = ValueNotifier<String?>(null);

  /// Fehler beim Laden der Liste, null wenn ok.
  static final listenFehler = ValueNotifier<String?>(null);

  static final _api = ApiClient();
  static final _verlaeufe = <int, List<ChatNachricht>>{};
  static final _schreibbar = <int, bool>{};
  static final _letzteZeit = <int, DateTime?>{};
  static List<WartendeNachricht> _puffer = [];
  static bool _listeGeladen = false;
  static int? _offenerChat;
  static Timer? _timer;
  static Future<void>? _laufendeAbfrage;
  static Future<void>? _laufendesSenden;

  static Future<void> init() async {
    final prefs = await SharedPreferences.getInstance();
    final gespeichert = prefs.getString(_pufferSchluessel);
    if (gespeichert != null) {
      try {
        _puffer = [
          for (final n in jsonDecode(gespeichert) as List) WartendeNachricht.ausJson(Map<String, dynamic>.from(n as Map)),
        ];
      } catch (_) {
        _puffer = [];
      }
    }
    _timer ??= Timer.periodic(_takt, (_) => abfragen());
    Sitzung.angemeldet.addListener(() {
      if (Sitzung.angemeldet.value == false) _leeren();
    });
  }

  /// Verlauf eines Chats (null = noch nicht geladen).
  static List<ChatNachricht>? verlauf(int chatId) => _verlaeufe[chatId];

  static bool schreibbar(int chatId) =>
      _schreibbar[chatId] ?? chats.value.where((c) => c.chatId == chatId).firstOrNull?.schreibbar ?? true;

  static List<WartendeNachricht> wartend(int chatId) => [for (final n in _puffer) if (n.chatId == chatId) n];

  /// Phase 5 (M11): noch nicht gesendete Nachrichten in allen Chats.
  static int get wartendAnzahl => _puffer.length;

  /// Verlauf geoeffnet: ganz laden und Nachrichten der Disposition als gelesen melden.
  static Future<void> oeffnen(int chatId) async {
    _offenerChat = chatId;
    await _verlaufLaden(chatId, voll: true);
    await _gelesenMelden(chatId);
  }

  static void schliessen(int chatId) {
    if (_offenerChat == chatId) _offenerChat = null;
  }

  /// „Disposition kontaktieren“: Chat der Tour holen oder anlegen.
  static Future<ChatKurz> tourChat(int tourId) async {
    final chat = await _api.tourChat(tourId);
    if (!chats.value.any((c) => c.chatId == chat.chatId)) {
      chats.value = [chat, ...chats.value];
      _letzteZeit[chat.chatId] = chat.letzteZeit;
    }
    return chat;
  }

  /// Nachricht senden: erst in den Puffer (sofort mit Uhr sichtbar), dann sofort versuchen.
  static Future<void> senden(int chatId, String text) async {
    _puffer.add(WartendeNachricht(DateTime.now().microsecondsSinceEpoch, chatId, text, DateTime.now()));
    await _pufferSpeichern();
    await pufferSenden();
  }

  /// Einmal abfragen (Takt alle 5 s, Ziehen zum Aktualisieren). Laeuft schon eine Abfrage, wird darauf gewartet.
  static Future<void> abfragen() => _laufendeAbfrage ??= _abfragen().whenComplete(() => _laufendeAbfrage = null);

  static Future<void> _abfragen() async {
    if (Sitzung.token == null) return;
    await pufferSenden();

    final List<ChatKurz> liste;
    try {
      liste = await _api.chats();
    } on ApiFehler catch (e) {
      listenFehler.value = e.text;
      return;
    }
    listenFehler.value = null;

    final ersteAbfrage = !_listeGeladen;
    _listeGeladen = true;
    for (final c in liste) {
      _schreibbar[c.chatId] = c.schreibbar;
      final bekannt = _letzteZeit.containsKey(c.chatId);
      final vorher = _letzteZeit[c.chatId];
      _letzteZeit[c.chatId] = c.letzteZeit;
      final geaendert = !ersteAbfrage && c.letzteZeit != null && c.letzteZeit != vorher;
      if (c.chatId != _offenerChat && !geaendert) continue;

      final neue = await _verlaufLaden(c.chatId, voll: _verlaeufe[c.chatId] == null, nachZeit: bekannt ? vorher : null);
      final vonDisposition = [for (final n in neue) if (!n.eigen) n];
      if (vonDisposition.isEmpty) continue;
      if (c.tourStatus == TourDetails.unterwegs) {
        for (final n in vonDisposition) {
          VorleseDienst.automatisch(n.text);
        }
      }
      if (c.chatId == _offenerChat) await _gelesenMelden(c.chatId);
    }

    chats.value = [
      // Der offene Chat wurde eben als gelesen gemeldet
      for (final c in liste) c.chatId == _offenerChat && c.ungelesen > 0 ? _ohneUngelesen(c) : c,
    ];
    _ungelesenZaehlen();
  }

  static void _ungelesenZaehlen() {
    ungelesen.value = chats.value.where((c) => c.chatId != _offenerChat).fold(0, (s, c) => s + c.ungelesen);
  }

  /// Laedt den Verlauf (ganz oder nur Neues) und gibt die bisher unbekannten Nachrichten zurueck.
  /// Beim ersten vollen Laden zaehlen nur Nachrichten nach [nachZeit] als neu.
  static Future<List<ChatNachricht>> _verlaufLaden(int chatId, {required bool voll, DateTime? nachZeit}) async {
    final bisher = _verlaeufe[chatId];
    final ChatVerlauf antwort;
    try {
      final seit = voll || bisher == null || bisher.isEmpty ? null : bisher.last.nachrichtId;
      antwort = await _api.nachrichten(chatId, seit: seit);
    } on ApiFehler {
      return [];
    }

    final bekannt = {for (final n in bisher ?? <ChatNachricht>[]) n.nachrichtId};
    final nachId = {for (final n in bisher ?? <ChatNachricht>[]) n.nachrichtId: n};
    for (final n in antwort.nachrichten) {
      nachId[n.nachrichtId] = n;
    }
    final alle = nachId.values.toList()
      ..sort((a, b) => a.zeit != b.zeit ? a.zeit.compareTo(b.zeit) : a.nachrichtId.compareTo(b.nachrichtId));
    for (final n in alle) {
      if (n.eigen) n.gelesen = !antwort.eigeneUngelesen.contains(n.nachrichtId);
    }
    _verlaeufe[chatId] = alle;
    _schreibbar[chatId] = antwort.schreibbar;
    aenderung.value++;

    if (bisher == null) {
      return nachZeit == null ? [] : [for (final n in alle) if (n.zeit.isAfter(nachZeit)) n];
    }
    return [for (final n in antwort.nachrichten) if (!bekannt.contains(n.nachrichtId)) n];
  }

  static Future<void> _gelesenMelden(int chatId) async {
    try {
      await _api.gelesen(chatId);
      chats.value = [
        for (final c in chats.value) c.chatId == chatId ? _ohneUngelesen(c) : c,
      ];
      _ungelesenZaehlen();
    } on ApiFehler {
      // Beim naechsten Oeffnen oder der naechsten neuen Nachricht erneut
    }
  }

  static ChatKurz _ohneUngelesen(ChatKurz c) => ChatKurz.ausJson({
        'chatId': c.chatId,
        'tourId': c.tourId,
        'tournummer': c.tournummer,
        'tourStatus': c.tourStatus,
        'disponent': c.disponent,
        'schreibbar': c.schreibbar,
        'letzteNachricht': c.letzteZeit == null
            ? null
            : {'text': c.letzterText, 'zeit': c.letzteZeit!.toUtc().toIso8601String(), 'eigen': c.letzteEigen},
        'ungelesen': 0,
      });

  /// Sendet den Puffer der Reihe nach. Ohne Netz bleibt der Rest liegen.
  static Future<void> pufferSenden() => _laufendesSenden ??= _pufferSenden().whenComplete(() => _laufendesSenden = null);

  static Future<void> _pufferSenden() async {
    while (_puffer.isNotEmpty && Sitzung.token != null) {
      final n = _puffer.first;
      try {
        final gesendet = await _api.nachrichtSenden(n.chatId, n.text);
        final verlauf = _verlaeufe[n.chatId];
        if (verlauf != null && !verlauf.any((v) => v.nachrichtId == gesendet.nachrichtId)) verlauf.add(gesendet);
        _letzteZeit[n.chatId] = gesendet.zeit;
      } on ApiFehler catch (e) {
        if (e.code == 'server_nicht_erreichbar' || e.code == 'anmeldung_fehlgeschlagen') return;
        // Chat geschlossen, nicht gefunden oder ungueltig: nicht endlos neu senden, aber Bescheid geben
        sendeFehler.value = '"${n.text}": ${e.text}';
        sendeFehler.value = null;
      }
      _puffer.remove(n);
      await _pufferSpeichern();
    }
  }

  static Future<void> _pufferSpeichern() async {
    aenderung.value++;
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_pufferSchluessel, jsonEncode([for (final n in _puffer) n.zuJson()]));
  }

  /// Abgemeldet: alles vergessen, auch nicht gesendete Nachrichten.
  static void _leeren() {
    _verlaeufe.clear();
    _schreibbar.clear();
    _letzteZeit.clear();
    _listeGeladen = false;
    _offenerChat = null;
    _puffer = [];
    _pufferSpeichern();
    chats.value = [];
    ungelesen.value = 0;
  }
}
