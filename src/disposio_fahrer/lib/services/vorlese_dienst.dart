import 'dart:async';

import 'package:audioplayers/audioplayers.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_tts/flutter_tts.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../texte.dart';

/// Phase 4: liest Nachrichten der Disposition vor (flutter_tts).
/// Automatisch nur bei neuen Nachrichten waehrend der Fahrt (Tour Unterwegs) und wenn in M09 eingeschaltet:
/// erst ein kurzer Ton, dann „Nachricht von der Disposition: …“.
/// Von Hand ueber den Lautsprecher-Knopf an jeder Nachricht der Disposition.
/// Sprache wie die App: de-DE, en-US oder pl-PL.
class VorleseDienst {
  static const _schluessel = 'nachrichten_vorlesen';
  static const _stimmen = {'de': 'de-DE', 'en': 'en-US', 'pl': 'pl-PL'};

  /// Schalter in M09 „Nachrichten vorlesen“, Standard an.
  static final an = ValueNotifier<bool>(true);

  static final _tts = FlutterTts();
  static final _ton = AudioPlayer();

  // Mehrere neue Nachrichten nacheinander vorlesen, nicht durcheinander
  static Future<void> _kette = Future.value();

  static Future<void> init() async {
    final prefs = await SharedPreferences.getInstance();
    an.value = prefs.getBool(_schluessel) ?? true;
    await _tts.awaitSpeakCompletion(true);
  }

  static Future<void> setzen(bool wert) async {
    an.value = wert;
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool(_schluessel, wert);
    if (!wert) await _tts.stop();
  }

  /// Neue Nachricht waehrend der Fahrt: mit Ton und Ansage, wenn der Schalter an ist.
  static void automatisch(String text) {
    if (!an.value) return;
    _kette = _kette.then((_) => _sprechen(text, mitAnsage: true));
  }

  /// Lautsprecher-Knopf: sofort vorlesen, Laufendes abbrechen.
  static Future<void> vorlesen(String text) async {
    await _tts.stop();
    await _sprechen(text, mitAnsage: false);
  }

  static Future<void> _sprechen(String text, {required bool mitAnsage}) async {
    try {
      if (mitAnsage) {
        // Auf das Ende des Tons warten, hoechstens 2 Sekunden
        final fertig = _ton.onPlayerComplete.first.then((_) {});
        await _ton.play(AssetSource('ton.wav'));
        await Future.any([fertig, Future<void>.delayed(const Duration(seconds: 2))]);
      }
      await _tts.setLanguage(_stimmen[Texte.sprache.value] ?? 'de-DE');
      await _tts.speak(mitAnsage ? '${Texte.t('vorlesen_ansage')} $text' : text);
    } catch (e) {
      debugPrint('Vorlesen fehlgeschlagen: $e');
      // Ohne Sprachausgabe auf dem Geraet bleibt es still; der Text steht ja im Chat
    }
  }
}
