import 'dart:convert';
import 'dart:io';

import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';
import 'package:flutter/material.dart';
import 'package:flutter_local_notifications/flutter_local_notifications.dart';
import 'package:permission_handler/permission_handler.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../screens/chat_seite.dart';
import '../screens/start_seite.dart';
import '../screens/tour_seite.dart';
import '../texte.dart';
import 'chat_dienst.dart';
import 'sitzung.dart';

/// Push-Benachrichtigungen ueber Firebase Cloud Messaging (neue Nachricht der Disposition, neue Tour).
/// Die API sendet an das Thema `fahrer_<benutzer_id>`; die App abonniert es nach dem Login und kuendigt es
/// beim Abmelden oder wenn der Schalter in M09 aus ist.
/// Im Hintergrund zeigt Android die Benachrichtigung selbst. Im Vordergrund zeigt die App eine lokale
/// Benachrichtigung, ausser der passende Chat ist gerade offen. Vorgelesen wird weiter nur ueber den
/// ChatDienst (Phase 4), nicht hier.
/// Ohne google-services.json (Firebase nicht eingerichtet) ist alles still abgeschaltet.
class PushDienst {
  static const _schluesselAn = 'benachrichtigungen_an';
  static const _schluesselThema = 'push_thema';
  static const _kanalId = 'disposio'; // wie PushDienst.KanalId in der API

  /// Navigator der App, zum Oeffnen von Chat oder Tour beim Antippen.
  static final navigatorKey = GlobalKey<NavigatorState>();

  /// Schalter in M09 „Benachrichtigungen“, Standard an.
  static final an = ValueNotifier<bool>(true);

  /// Wird hochgezaehlt, wenn eine Tour-Benachrichtigung kommt (M02 laedt dann neu).
  static final tourenGeaendert = ValueNotifier<int>(0);

  /// false, wenn Firebase nicht eingerichtet ist (z. B. ohne google-services.json gebaut).
  static bool verfuegbar = false;

  static final _lokal = FlutterLocalNotificationsPlugin();
  static Map<String, dynamic>? _wartendesZiel;

  static Future<void> init() async {
    final prefs = await SharedPreferences.getInstance();
    an.value = prefs.getBool(_schluesselAn) ?? true;
    if (!Platform.isAndroid) return;

    try {
      await Firebase.initializeApp();
    } catch (e) {
      debugPrint('Firebase nicht eingerichtet, Push aus: $e');
      return;
    }
    verfuegbar = true;

    await _lokal.initialize(
      settings: const InitializationSettings(android: AndroidInitializationSettings('@mipmap/ic_launcher')),
      onDidReceiveNotificationResponse: (antwort) {
        final payload = antwort.payload;
        if (payload == null) return;
        try {
          _oeffnen(Map<String, dynamic>.from(jsonDecode(payload) as Map));
        } catch (_) {}
      },
    );
    await _lokal
        .resolvePlatformSpecificImplementation<AndroidFlutterLocalNotificationsPlugin>()
        ?.createNotificationChannel(_kanal());

    FirebaseMessaging.onMessage.listen(_imVordergrund);
    FirebaseMessaging.onMessageOpenedApp.listen((m) => _oeffnen(m.data));

    // App wurde durch Antippen einer Benachrichtigung gestartet
    try {
      final start = await FirebaseMessaging.instance.getInitialMessage();
      if (start != null) _wartendesZiel = start.data;
    } catch (_) {}
    // Lokale Benachrichtigung, die die App gestartet hat (selten, z. B. App kurz danach beendet)
    try {
      final details = await _lokal.getNotificationAppLaunchDetails();
      final payload = details?.notificationResponse?.payload;
      if (details?.didNotificationLaunchApp == true && payload != null) {
        _wartendesZiel ??= Map<String, dynamic>.from(jsonDecode(payload) as Map);
      }
    } catch (_) {}

    Sitzung.angemeldet.addListener(() {
      if (Sitzung.angemeldet.value == false) _kuendigen();
    });
  }

  static AndroidNotificationChannel _kanal() => AndroidNotificationChannel(
        _kanalId,
        'Disposio',
        description: Texte.t('benachrichtigung_kanal'),
        importance: Importance.high,
        playSound: true,
      );

  /// Nach dem Login (StartSeite): Erlaubnis abfragen (Android 13+), Thema abonnieren und eine
  /// Benachrichtigung oeffnen, mit der die App gestartet wurde.
  static Future<void> nachAnmeldung() async {
    if (!verfuegbar) return;
    // Zuerst (noch waehrend des Aufbaus der StartSeite) das Ziel einplanen: ein Callback, der erst nach
    // einem await angemeldet wird, liefe erst beim naechsten Neuzeichnen (z. B. erst nach einem Antippen).
    final ziel = _wartendesZiel;
    _wartendesZiel = null;
    if (ziel != null) {
      WidgetsBinding.instance.addPostFrameCallback((_) => _oeffnen(ziel));
      WidgetsBinding.instance.scheduleFrame();
    }
    if (an.value) {
      await Permission.notification.request();
      await _abonnieren();
    }
  }

  /// Schalter in M09.
  static Future<void> setzen(bool wert) async {
    an.value = wert;
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool(_schluesselAn, wert);
    if (!verfuegbar) return;
    if (wert) {
      await Permission.notification.request();
      await _abonnieren();
    } else {
      await _kuendigen();
    }
  }

  static Future<void> _abonnieren() async {
    final benutzerId = _benutzerIdAusToken();
    if (benutzerId == null) return;
    final thema = 'fahrer_$benutzerId';
    final prefs = await SharedPreferences.getInstance();
    final vorher = prefs.getString(_schluesselThema);
    try {
      // Anderer Fahrer auf demselben Geraet: altes Thema zuerst kuendigen
      if (vorher != null && vorher != thema) await FirebaseMessaging.instance.unsubscribeFromTopic(vorher);
      await FirebaseMessaging.instance.subscribeToTopic(thema);
      await prefs.setString(_schluesselThema, thema);
    } catch (e) {
      debugPrint('Thema abonnieren fehlgeschlagen: $e');
    }
  }

  static Future<void> _kuendigen() async {
    if (!verfuegbar) return;
    final prefs = await SharedPreferences.getInstance();
    final thema = prefs.getString(_schluesselThema);
    if (thema == null) return;
    try {
      await FirebaseMessaging.instance.unsubscribeFromTopic(thema);
      await prefs.remove(_schluesselThema);
    } catch (e) {
      // Thema bleibt gemerkt und wird beim naechsten Mal erneut gekuendigt
      debugPrint('Thema kuendigen fehlgeschlagen: $e');
    }
  }

  /// benutzer_id aus dem Anmelde-Token (JWT, Claim benutzer_id). Die Signatur prueft die API.
  static String? _benutzerIdAusToken() {
    final token = Sitzung.token;
    if (token == null) return null;
    final teile = token.split('.');
    if (teile.length != 3) return null;
    try {
      final inhalt = jsonDecode(utf8.decode(base64Url.decode(base64Url.normalize(teile[1])))) as Map;
      final id = inhalt['benutzer_id'];
      return id?.toString();
    } catch (_) {
      return null;
    }
  }

  static Future<void> _imVordergrund(RemoteMessage nachricht) async {
    final daten = nachricht.data;
    final typ = daten['typ'];
    if (typ == 'nachricht') {
      // Liste und Verlauf aktualisieren (liest bei laufender Tour wie bisher vor)
      ChatDienst.abfragen();
      if (int.tryParse('${daten['chatId']}') == ChatDienst.offenerChat) return;
    } else if (typ == 'tour') {
      tourenGeaendert.value++;
    }
    if (!an.value || Sitzung.token == null) return;

    final hinweis = nachricht.notification;
    if (hinweis == null) return;
    await _lokal.show(
      id: nachricht.messageId?.hashCode ?? DateTime.now().millisecondsSinceEpoch ~/ 1000,
      title: hinweis.title,
      body: hinweis.body,
      notificationDetails: NotificationDetails(
        android: AndroidNotificationDetails(
          _kanalId,
          'Disposio',
          channelDescription: Texte.t('benachrichtigung_kanal'),
          importance: Importance.high,
          priority: Priority.high,
          playSound: true,
        ),
      ),
      payload: jsonEncode(daten),
    );
  }

  /// Antippen: typ=nachricht oeffnet den Chat-Verlauf, typ=tour die Tour (M03). Listen werden neu geladen.
  static Future<void> _oeffnen(Map<String, dynamic> daten) async {
    debugPrint('Push angetippt: $daten');
    if (Sitzung.token == null) return;
    final navigator = navigatorKey.currentState;
    if (navigator == null) {
      _wartendesZiel = daten;
      return;
    }

    switch (daten['typ']) {
      case 'nachricht':
        final chatId = int.tryParse('${daten['chatId']}');
        await ChatDienst.abfragen();
        final chat = ChatDienst.chats.value.where((c) => c.chatId == chatId).firstOrNull;
        StartSeite.tab.value = StartSeite.tabChat;
        if (chat == null || chatId == ChatDienst.offenerChat) return;
        navigator.popUntil((r) => r.isFirst);
        navigator.push(MaterialPageRoute(builder: (_) => ChatVerlaufSeite(chat: chat)));
      case 'tour':
        final tourId = int.tryParse('${daten['tourId']}');
        tourenGeaendert.value++;
        if (tourId == null) return;
        StartSeite.tab.value = 0;
        navigator.popUntil((r) => r.isFirst);
        await navigator.push(MaterialPageRoute(builder: (_) => TourSeite(tourId: tourId)));
        tourenGeaendert.value++;
    }
  }
}
