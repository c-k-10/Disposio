import 'dart:io';

import 'package:flutter/material.dart';

import '../services/api_client.dart';
import '../services/warteschlange.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';
import '../widgets/foto_auswahl.dart';
import '../widgets/tour_bausteine.dart';
import 'schaden_seite.dart';

/// Knoepfe „Tanken“ und „Schaden“ fuer M02, M03 und das Profil (Phase 5).
class TankenSchadenKnoepfe extends StatelessWidget {
  const TankenSchadenKnoepfe({super.key});

  Future<void> _oeffnen(BuildContext context, Widget seite) =>
      Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => seite));

  @override
  Widget build(BuildContext context) {
    final stil = OutlinedButton.styleFrom(backgroundColor: Colors.white);
    return Row(
      children: [
        Expanded(
          child: OutlinedButton.icon(
            style: stil,
            icon: const Icon(Icons.local_gas_station_outlined),
            label: Text(Texte.t('tanken')),
            onPressed: () => _oeffnen(context, const TankenSeite()),
          ),
        ),
        const SizedBox(width: 8),
        Expanded(
          child: OutlinedButton.icon(
            style: stil,
            icon: const Icon(Icons.car_crash_outlined),
            label: Text(Texte.t('schaden')),
            onPressed: () => _oeffnen(context, const SchadenSeite()),
          ),
        ),
      ],
    );
  }
}

/// Wireframe M14: Tankvorgang erfassen. Fahrzeug bestimmt die API (aktive bzw. letzte Tour).
/// Kilometerstand-Vorschlag = letzter bekannter Stand. Belegfoto oder Grund ohne Beleg ist Pflicht.
/// Gibt true zurueck, wenn der Tankvorgang in der Warteschlange liegt.
class TankenSeite extends StatefulWidget {
  const TankenSeite({super.key});

  /// Werte, wie die API sie erwartet (Kraftstoffnamen sind in allen Sprachen gleich).
  static const kraftstoffe = ['Diesel', 'AdBlue', 'Benzin', 'Strom'];

  @override
  State<TankenSeite> createState() => _TankenSeiteState();
}

class _TankenSeiteState extends State<TankenSeite> {
  final _api = ApiClient();
  final _liter = TextEditingController();
  final _kosten = TextEditingController();
  final _km = TextEditingController();
  final _tankstelle = TextEditingController();
  final _grund = TextEditingController();
  AktuellesFahrzeug? _fahrzeug;
  bool _fahrzeugGeladen = false;
  String _kraftstoff = TankenSeite.kraftstoffe.first;
  DateTime _datum = DateTime.now();
  List<String> _beleg = [];
  String? _fehler;
  bool _gespeichert = false;

  @override
  void initState() {
    super.initState();
    _fahrzeugLaden();
  }

  @override
  void dispose() {
    for (final c in [_liter, _kosten, _km, _tankstelle, _grund]) {
      c.dispose();
    }
    if (!_gespeichert) {
      for (final pfad in _beleg) {
        File(pfad).delete().ignore();
      }
    }
    super.dispose();
  }

  Future<void> _fahrzeugLaden() async {
    try {
      final fahrzeug = await _api.fahrzeug();
      if (!mounted) return;
      setState(() => _fahrzeug = fahrzeug);
      if (_km.text.isEmpty) _km.text = '${fahrzeug.kilometerstand}';
    } on ApiFehler {
      // Ohne Netz oder ohne Tour: Fahrzeug bestimmt die API beim Senden
    } finally {
      if (mounted) setState(() => _fahrzeugGeladen = true);
    }
  }

  /// Dezimalzahl mit Komma oder Punkt.
  double? _zahl(TextEditingController c) => double.tryParse(c.text.trim().replaceAll(',', '.'));

  Future<void> _datumWaehlen() async {
    final heute = DateTime.now();
    final gewaehlt = await showDatePicker(
      context: context,
      initialDate: _datum,
      firstDate: heute.subtract(const Duration(days: 30)),
      lastDate: heute,
    );
    if (gewaehlt != null) setState(() => _datum = gewaehlt);
  }

  Future<void> _speichern() async {
    final liter = _zahl(_liter);
    final kosten = _zahl(_kosten);
    final km = int.tryParse(_km.text.trim().replaceAll('.', ''));
    final tankstelle = _tankstelle.text.trim();
    final grund = _grund.text.trim();
    if (liter == null || kosten == null || km == null || tankstelle.isEmpty) {
      setState(() => _fehler = Texte.t('pflichtfelder'));
      return;
    }
    if (liter <= 0 || liter > 5000 || kosten < 0 || kosten > 100000 || km < 0 || km > 5000000) {
      setState(() => _fehler = Texte.t('eingabe_ungueltig'));
      return;
    }
    if (_beleg.isEmpty && grund.isEmpty) {
      setState(() => _fehler = Texte.t('beleg_oder_grund'));
      return;
    }
    final letzter = _fahrzeug?.kilometerstand;
    if (letzter != null && km < letzter) {
      final trotzdem = await showDialog<bool>(
        context: context,
        builder: (context) => AlertDialog(
          content: Text('${Texte.t('km_kleiner')} ($letzter km)'),
          actions: [
            TextButton(onPressed: () => Navigator.pop(context, false), child: Text(Texte.t('abbrechen'))),
            FilledButton(onPressed: () => Navigator.pop(context, true), child: Text(Texte.t('trotzdem_speichern'))),
          ],
        ),
      );
      if (trotzdem != true) return;
    }

    final tank = WarteEintrag(
      id: Warteschlange.neueId(),
      art: 'tank',
      titel: 'w_tank|${liter.toStringAsFixed(2)} l $_kraftstoff',
      daten: {
        'liter': liter,
        'kosten': kosten,
        'kilometerstand': km,
        'tankstelle': tankstelle,
        'kraftstoff': _kraftstoff,
        'datum': datumFuerApi(_datum),
        // Nur ohne Belegfoto (M14: Grund ohne Beleg)
        'grundOhneBeleg': _beleg.isEmpty ? grund : null,
      },
      erstellt: DateTime.now(),
    );
    await Warteschlange.hinzufuegen([
      tank,
      for (final pfad in _beleg)
        WarteEintrag(
          id: Warteschlange.neueId(),
          art: 'dokument',
          titel: 'w_tankbeleg|$tankstelle',
          daten: {'art': 'Tankbeleg', 'eigentuemer': 'tankvorgangId'},
          datei: pfad,
          eltern: tank.id,
          erstellt: DateTime.now(),
        ),
    ]);
    _gespeichert = true;
    if (mounted) {
      meldung(context, Texte.t('gespeichert_wird_uebertragen'));
      Navigator.pop(context, true);
    }
  }

  @override
  Widget build(BuildContext context) {
    const leise = TextStyle(color: DisposioFarben.textLeise);
    final fahrzeug = _fahrzeug;
    return Scaffold(
      appBar: AppBar(title: Text(Texte.t('tanken_titel'))),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          if (fahrzeug != null)
            Text('${Texte.t('fahrzeug')}: ${fahrzeug.kennzeichen} (${fahrzeug.typ})', style: leise)
          else if (_fahrzeugGeladen)
            Text(Texte.t('fahrzeug_unbekannt'), style: leise),
          const SizedBox(height: 8),
          ListTile(
            contentPadding: EdgeInsets.zero,
            leading: const Icon(Icons.event),
            title: Text('${Texte.t('datum')}: ${datum(_datum)}'),
            trailing: TextButton(onPressed: _datumWaehlen, child: Text(Texte.t('aendern'))),
          ),
          TextField(
            controller: _liter,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            decoration: InputDecoration(labelText: Texte.t(_kraftstoff == 'Strom' ? 'menge_kwh' : 'menge_liter')),
          ),
          const SizedBox(height: 8),
          TextField(
            controller: _kosten,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            decoration: InputDecoration(labelText: Texte.t('kosten_eur')),
          ),
          const SizedBox(height: 8),
          TextField(
            controller: _km,
            keyboardType: TextInputType.number,
            decoration: InputDecoration(
              labelText: Texte.t('kilometerstand'),
              helperText: fahrzeug == null ? null : '${Texte.t('letzter_stand')}: ${fahrzeug.kilometerstand} km',
            ),
          ),
          const SizedBox(height: 8),
          TextField(
            controller: _tankstelle,
            maxLength: 200,
            decoration: InputDecoration(labelText: Texte.t('tankstelle')),
          ),
          DropdownButtonFormField<String>(
            initialValue: _kraftstoff,
            decoration: InputDecoration(labelText: Texte.t('kraftstoff')),
            items: [for (final k in TankenSeite.kraftstoffe) DropdownMenuItem(value: k, child: Text(k))],
            onChanged: (wert) => setState(() => _kraftstoff = wert ?? _kraftstoff),
          ),
          const SizedBox(height: 16),
          FotoAuswahl(
            titel: Texte.t('belegfoto'),
            pfade: _beleg,
            max: 2,
            geaendert: (neu) => setState(() => _beleg = neu),
          ),
          if (_beleg.isEmpty) ...[
            const SizedBox(height: 8),
            TextField(
              controller: _grund,
              maxLength: 500,
              decoration: InputDecoration(labelText: Texte.t('grund_ohne_beleg')),
            ),
          ],
          if (_fehler != null) ...[
            const SizedBox(height: 12),
            Text(_fehler!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
          ],
          const SizedBox(height: 16),
          FilledButton.icon(
            icon: const Icon(Icons.save_outlined),
            label: Text(Texte.t('speichern')),
            onPressed: _speichern,
          ),
          const SizedBox(height: 8),
          OutlinedButton(onPressed: () => Navigator.pop(context, false), child: Text(Texte.t('abbrechen'))),
        ],
      ),
    );
  }
}
