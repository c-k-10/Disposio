import 'package:flutter/material.dart';

import '../services/api_client.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';
import '../widgets/tour_bausteine.dart';

/// Wireframe M05: Problem melden. Gibt true zurueck, wenn gemeldet wurde.
/// Vorlaeufig ohne Foto; die API speichert es als Statusmeldung.
class ProblemSeite extends StatefulWidget {
  const ProblemSeite({super.key, required this.tour, this.stoppId});

  final TourDetails tour;

  /// Vorauswahl fuer „Bezug“. null = ganze Tour.
  final int? stoppId;

  /// Werte, wie die API sie erwartet, mit Schluessel fuer die Anzeige.
  static const arten = {
    'Stau': 'problem_stau',
    'Panne': 'problem_panne',
    'Kunde nicht da': 'problem_kunde',
    'Adresse falsch': 'problem_adresse',
    'Ware beschädigt': 'problem_ware',
    'Sonstiges': 'problem_sonstiges',
  };

  @override
  State<ProblemSeite> createState() => _ProblemSeiteState();
}

class _ProblemSeiteState extends State<ProblemSeite> {
  // Im Dropdown steht 0 fuer die ganze Tour, sonst die stoppId
  static const _ganzeTour = 0;

  final _api = ApiClient();
  final _beschreibung = TextEditingController();
  final _dauer = TextEditingController();
  late int _bezug = widget.stoppId ?? _ganzeTour;
  String? _art;
  String? _fehler;
  bool _sendet = false;

  @override
  void dispose() {
    _beschreibung.dispose();
    _dauer.dispose();
    super.dispose();
  }

  Future<void> _melden() async {
    final beschreibung = _beschreibung.text.trim();
    final dauerText = _dauer.text.trim();
    final dauer = int.tryParse(dauerText);
    if (_art == null || beschreibung.isEmpty) {
      setState(() => _fehler = Texte.t('pflichtfelder'));
      return;
    }
    if (dauerText.isNotEmpty && (dauer == null || dauer < 1 || dauer > 1440)) {
      setState(() => _fehler = Texte.t('dauer_ungueltig'));
      return;
    }

    setState(() {
      _fehler = null;
      _sendet = true;
    });
    try {
      await _api.problemMelden(
        widget.tour.tourId,
        stoppId: _bezug == _ganzeTour ? null : _bezug,
        art: _art!,
        beschreibung: beschreibung,
        dauerMinuten: dauer,
      );
      if (mounted) Navigator.pop(context, true);
    } on ApiFehler catch (e) {
      if (mounted) setState(() => _fehler = e.text);
    } finally {
      if (mounted) setState(() => _sendet = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: Text(Texte.t('problem_melden'))),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          Text('${Texte.t('tour')} ${widget.tour.tournummer}', style: const TextStyle(color: DisposioFarben.textLeise)),
          const SizedBox(height: 12),
          DropdownButtonFormField<int>(
            initialValue: _bezug,
            isExpanded: true,
            decoration: InputDecoration(labelText: Texte.t('bezug')),
            items: [
              DropdownMenuItem(value: _ganzeTour, child: Text(Texte.t('ganze_tour'))),
              for (final s in widget.tour.stopps)
                DropdownMenuItem(
                  value: s.stoppId,
                  child: Text('${s.reihenfolge}. ${s.bezeichnung}', overflow: TextOverflow.ellipsis),
                ),
            ],
            onChanged: _sendet ? null : (wert) => setState(() => _bezug = wert ?? _ganzeTour),
          ),
          const SizedBox(height: 12),
          DropdownButtonFormField<String>(
            initialValue: _art,
            isExpanded: true,
            decoration: InputDecoration(labelText: Texte.t('art')),
            items: [
              for (final e in ProblemSeite.arten.entries)
                DropdownMenuItem(value: e.key, child: Text(Texte.t(e.value))),
            ],
            onChanged: _sendet ? null : (wert) => setState(() => _art = wert),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _beschreibung,
            enabled: !_sendet,
            minLines: 3,
            maxLines: 6,
            maxLength: 1000,
            decoration: InputDecoration(labelText: Texte.t('beschreibung'), alignLabelWithHint: true),
          ),
          const SizedBox(height: 4),
          TextField(
            controller: _dauer,
            enabled: !_sendet,
            keyboardType: TextInputType.number,
            decoration: InputDecoration(labelText: Texte.t('dauer')),
          ),
          if (_fehler != null) ...[
            const SizedBox(height: 12),
            Text(_fehler!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
          ],
          const SizedBox(height: 16),
          FilledButton(
            onPressed: _sendet ? null : _melden,
            child: _sendet
                ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2))
                : Text(Texte.t('problem_melden')),
          ),
          const SizedBox(height: 8),
          OutlinedButton(
            onPressed: _sendet ? null : () => Navigator.pop(context, false),
            child: Text(Texte.t('abbrechen')),
          ),
          const SizedBox(height: 8),
          // Schadensmeldung (M07) kommt in Phase 5
          TextButton.icon(
            icon: const Icon(Icons.car_crash_outlined),
            label: Text(Texte.t('fahrzeugschaden')),
            onPressed: () => meldung(context, Texte.t('kommt_phase5')),
          ),
        ],
      ),
    );
  }
}
