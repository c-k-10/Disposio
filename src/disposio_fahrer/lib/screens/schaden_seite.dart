import 'dart:io';

import 'package:flutter/material.dart';
import 'package:geolocator/geolocator.dart';

import '../services/api_client.dart';
import '../services/gps_dienst.dart';
import '../services/warteschlange.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';
import '../widgets/foto_auswahl.dart';
import '../widgets/tour_bausteine.dart';

/// Wireframe M15: Fahrzeugschaden melden. Art, Bauteil, Beschreibung, fahrbereit, Ort (GPS) und Fotos.
/// Nicht fahrbereit: die API schreibt automatisch in den Chat der aktiven Tour.
/// Gibt true zurueck, wenn die Meldung in der Warteschlange liegt.
class SchadenSeite extends StatefulWidget {
  const SchadenSeite({super.key});

  /// Werte, wie die API sie erwartet, mit Schluessel fuer die Anzeige.
  static const arten = {
    'Unfall': 'schaden_unfall',
    'Steinschlag': 'schaden_steinschlag',
    'Reifenschaden': 'schaden_reifen',
    'Technischer Defekt': 'schaden_technik',
    'Vandalismus': 'schaden_vandalismus',
    'Sonstiges': 'schaden_sonstiges',
  };

  static const bauteile = {
    'Karosserie': 'bauteil_karosserie',
    'Scheibe': 'bauteil_scheibe',
    'Reifen/Felge': 'bauteil_reifen',
    'Beleuchtung': 'bauteil_beleuchtung',
    'Spiegel': 'bauteil_spiegel',
    'Motor/Antrieb': 'bauteil_motor',
    'Bremsen': 'bauteil_bremsen',
    'Aufbau/Ladefläche': 'bauteil_aufbau',
    'Sonstiges': 'bauteil_sonstiges',
  };

  @override
  State<SchadenSeite> createState() => _SchadenSeiteState();
}

class _SchadenSeiteState extends State<SchadenSeite> {
  final _api = ApiClient();
  final _beschreibung = TextEditingController();
  final _ort = TextEditingController();
  AktuellesFahrzeug? _fahrzeug;
  String? _art;
  String? _bauteil;
  bool? _fahrbereit;
  Position? _position;
  bool _ortenLaeuft = false;
  List<String> _fotos = [];
  String? _fehler;
  bool _gespeichert = false;

  @override
  void initState() {
    super.initState();
    _fahrzeugLaden();
    _orten();
  }

  @override
  void dispose() {
    _beschreibung.dispose();
    _ort.dispose();
    if (!_gespeichert) {
      for (final pfad in _fotos) {
        File(pfad).delete().ignore();
      }
    }
    super.dispose();
  }

  Future<void> _fahrzeugLaden() async {
    try {
      final fahrzeug = await _api.fahrzeug();
      if (mounted) setState(() => _fahrzeug = fahrzeug);
    } on ApiFehler {
      // Ohne Netz: Fahrzeug bestimmt die API beim Senden
    }
  }

  /// Aktuellen Standort uebernehmen (nur mit Berechtigung, ohne Rueckfrage beim Oeffnen).
  Future<void> _orten({bool fragen = false}) async {
    setState(() => _ortenLaeuft = true);
    try {
      if (!await GpsDienst.berechtigt()) {
        if (!fragen || !await GpsDienst.berechtigungAnfragen()) return;
      }
      final position = await Geolocator.getCurrentPosition(
        locationSettings: const LocationSettings(accuracy: LocationAccuracy.high, timeLimit: Duration(seconds: 20)),
      );
      if (mounted) setState(() => _position = position);
    } catch (_) {
      if (fragen && mounted) meldung(context, Texte.t('ort_fehler'));
    } finally {
      if (mounted) setState(() => _ortenLaeuft = false);
    }
  }

  Future<void> _speichern() async {
    final beschreibung = _beschreibung.text.trim();
    if (_art == null || _bauteil == null || _fahrbereit == null || beschreibung.isEmpty) {
      setState(() => _fehler = Texte.t('pflichtfelder'));
      return;
    }

    final schaden = WarteEintrag(
      id: Warteschlange.neueId(),
      art: 'schaden',
      titel: 'w_schaden|${Texte.t(SchadenSeite.arten[_art]!)}',
      daten: {
        'schadensart': _art,
        'bauteil': _bauteil,
        'beschreibung': beschreibung,
        'fahrbereit': _fahrbereit,
        'ort': _ort.text.trim().isEmpty ? null : _ort.text.trim(),
        'lat': _position?.latitude,
        'lon': _position?.longitude,
        'datum': datumFuerApi(DateTime.now()),
      },
      erstellt: DateTime.now(),
    );
    await Warteschlange.hinzufuegen([
      schaden,
      for (final pfad in _fotos)
        WarteEintrag(
          id: Warteschlange.neueId(),
          art: 'dokument',
          titel: 'w_schadenfoto|${Texte.t(SchadenSeite.arten[_art]!)}',
          daten: {'art': 'Schadenfoto', 'eigentuemer': 'schadenId'},
          datei: pfad,
          eltern: schaden.id,
          erstellt: DateTime.now(),
        ),
    ]);
    _gespeichert = true;
    if (mounted) {
      meldung(context, Texte.t(_fahrbereit! ? 'gespeichert_wird_uebertragen' : 'schaden_nicht_fahrbereit_gespeichert'));
      Navigator.pop(context, true);
    }
  }

  @override
  Widget build(BuildContext context) {
    const leise = TextStyle(color: DisposioFarben.textLeise);
    final fahrzeug = _fahrzeug;
    final position = _position;
    return Scaffold(
      appBar: AppBar(title: Text(Texte.t('schaden_titel'))),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          if (fahrzeug != null) Text('${Texte.t('fahrzeug')}: ${fahrzeug.kennzeichen} (${fahrzeug.typ})', style: leise),
          const SizedBox(height: 8),
          DropdownButtonFormField<String>(
            initialValue: _art,
            isExpanded: true,
            decoration: InputDecoration(labelText: Texte.t('schadensart')),
            items: [
              for (final e in SchadenSeite.arten.entries) DropdownMenuItem(value: e.key, child: Text(Texte.t(e.value))),
            ],
            onChanged: (wert) => setState(() => _art = wert),
          ),
          const SizedBox(height: 12),
          DropdownButtonFormField<String>(
            initialValue: _bauteil,
            isExpanded: true,
            decoration: InputDecoration(labelText: Texte.t('bauteil')),
            items: [
              for (final e in SchadenSeite.bauteile.entries)
                DropdownMenuItem(value: e.key, child: Text(Texte.t(e.value))),
            ],
            onChanged: (wert) => setState(() => _bauteil = wert),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _beschreibung,
            minLines: 3,
            maxLines: 6,
            maxLength: 1500,
            decoration: InputDecoration(labelText: Texte.t('beschreibung'), alignLabelWithHint: true),
          ),
          Text(Texte.t('fahrbereit_frage'), style: const TextStyle(fontWeight: FontWeight.w700)),
          const SizedBox(height: 6),
          SegmentedButton<bool>(
            emptySelectionAllowed: true,
            segments: [
              ButtonSegment(value: true, label: Text(Texte.t('ja'))),
              ButtonSegment(value: false, label: Text(Texte.t('nein'))),
            ],
            selected: {?_fahrbereit},
            onSelectionChanged: (auswahl) => setState(() => _fahrbereit = auswahl.firstOrNull),
          ),
          if (_fahrbereit == false) ...[
            const SizedBox(height: 8),
            StatusZeile(Texte.t('nicht_fahrbereit_hinweis')),
          ],
          const SizedBox(height: 16),
          Text(Texte.t('ort'), style: const TextStyle(fontWeight: FontWeight.w700)),
          Row(
            children: [
              Expanded(
                child: Text(
                  position == null
                      ? Texte.t(_ortenLaeuft ? 'ort_wird_bestimmt' : 'ort_unbekannt')
                      : '${position.latitude.toStringAsFixed(5)}, ${position.longitude.toStringAsFixed(5)}',
                  style: leise,
                ),
              ),
              TextButton.icon(
                icon: const Icon(Icons.my_location),
                label: Text(Texte.t('gps_ort')),
                onPressed: _ortenLaeuft ? null : () => _orten(fragen: true),
              ),
            ],
          ),
          TextField(
            controller: _ort,
            maxLength: 200,
            decoration: InputDecoration(labelText: Texte.t('ort_text')),
          ),
          FotoAuswahl(
            titel: Texte.t('schadenfotos'),
            pfade: _fotos,
            geaendert: (neu) => setState(() => _fotos = neu),
          ),
          if (_fehler != null) ...[
            const SizedBox(height: 12),
            Text(_fehler!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
          ],
          const SizedBox(height: 16),
          FilledButton.icon(
            icon: const Icon(Icons.send_outlined),
            label: Text(Texte.t('schaden_melden')),
            onPressed: _speichern,
          ),
          const SizedBox(height: 8),
          OutlinedButton(onPressed: () => Navigator.pop(context, false), child: Text(Texte.t('abbrechen'))),
        ],
      ),
    );
  }
}
