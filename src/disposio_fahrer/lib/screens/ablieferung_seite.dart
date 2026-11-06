import 'dart:io';

import 'package:flutter/material.dart';

import '../services/api_client.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';
import '../widgets/foto_auswahl.dart';
import '../widgets/tour_bausteine.dart';
import 'empfang_seite.dart';

/// Abgelieferte Anzahl je Zustellauftrag (M06), weiter an M07.
class AblieferPosition {
  AblieferPosition(this.auftrag) : anzahl = auftrag.sollAnzahl;

  final ZustellAuftrag auftrag;
  int anzahl;

  /// Pflicht, wenn weniger als die Soll-Anzahl abgeliefert wird. Wert wie in der API (deutsch).
  String? ausnahmegrund;

  bool get teilmenge => anzahl < auftrag.sollAnzahl;
}

/// Wireframe M06: Ablieferung dokumentieren. Anzahl je Auftrag, Ausnahmegrund bei Teilmenge,
/// Bemerkung, Fotos der Ware und Lieferschein. Danach M07 (Empfang).
/// Gibt true zurueck, wenn der Nachweis in der Warteschlange liegt.
class AblieferungSeite extends StatefulWidget {
  const AblieferungSeite({super.key, required this.tour, required this.stopp});

  final TourDetails tour;
  final Stopp stopp;

  /// Werte, wie die API sie erwartet, mit Schluessel fuer die Anzeige.
  static const ausnahmegruende = {
    'Beschädigt': 'grund_beschaedigt',
    'Fehlmenge': 'grund_fehlmenge',
    'Annahme verweigert': 'grund_verweigert',
    'Sonstiges': 'grund_sonstiges',
  };

  @override
  State<AblieferungSeite> createState() => _AblieferungSeiteState();
}

class _AblieferungSeiteState extends State<AblieferungSeite> {
  final _api = ApiClient();
  final _bemerkung = TextEditingController();
  final _anzahlFelder = <int, TextEditingController>{};
  List<AblieferPosition>? _positionen;
  List<String> _fotos = [];
  List<String> _lieferscheine = [];
  String? _fehler;
  bool _offline = false;
  bool _uebergeben = false;

  @override
  void initState() {
    super.initState();
    _laden();
  }

  @override
  void dispose() {
    _bemerkung.dispose();
    for (final c in _anzahlFelder.values) {
      c.dispose();
    }
    // Abgebrochen: aufgenommene Bilder wieder loeschen
    if (!_uebergeben) {
      for (final pfad in [..._fotos, ..._lieferscheine]) {
        File(pfad).delete().ignore();
      }
    }
    super.dispose();
  }

  /// Mit Netz von der API (zeigt schon gesendete Nachweise), sonst aus den Tourdaten.
  Future<void> _laden() async {
    List<ZustellAuftrag> auftraege;
    try {
      auftraege = await _api.zustellAuftraege(widget.stopp.stoppId);
      _offline = false;
    } on ApiFehler catch (e) {
      if (!e.voruebergehend) {
        if (mounted) setState(() => _fehler = e.text);
        return;
      }
      _offline = true;
      auftraege = [
        for (final a in widget.stopp.auftraege)
          if (a.vorgangArt == 'Zustellung') ZustellAuftrag.ausTour(a),
      ];
    }
    if (!mounted) return;
    setState(() {
      _positionen = [for (final a in auftraege) AblieferPosition(a)];
      for (final p in _positionen!) {
        _anzahlFelder[p.auftrag.auftragId] = TextEditingController(text: '${p.anzahl}');
      }
    });
  }

  Future<void> _weiter() async {
    final positionen = _positionen!;
    for (final p in positionen) {
      final wert = int.tryParse(_anzahlFelder[p.auftrag.auftragId]!.text.trim());
      if (wert == null || wert < 0 || wert > p.auftrag.sollAnzahl) {
        setState(() => _fehler = '${p.auftrag.auftragsnummer}: ${Texte.t('anzahl_ungueltig')} ${p.auftrag.sollAnzahl}.');
        return;
      }
      p.anzahl = wert;
      if (p.teilmenge && p.ausnahmegrund == null) {
        setState(() => _fehler = '${p.auftrag.auftragsnummer}: ${Texte.t('ausnahmegrund_fehlt')}');
        return;
      }
      if (!p.teilmenge) p.ausnahmegrund = null;
    }
    setState(() => _fehler = null);

    final fertig = await Navigator.push<bool>(
      context,
      MaterialPageRoute(
        builder: (_) => EmpfangSeite(
          tour: widget.tour,
          stopp: widget.stopp,
          positionen: positionen,
          bemerkung: _bemerkung.text.trim(),
          fotos: _fotos,
          lieferscheine: _lieferscheine,
        ),
      ),
    );
    if (fertig == true && mounted) {
      _uebergeben = true;
      Navigator.pop(context, true);
    }
  }

  @override
  Widget build(BuildContext context) {
    final positionen = _positionen;
    return Scaffold(
      appBar: AppBar(title: Text(Texte.t('ablieferung_dokumentieren'))),
      body: positionen == null
          ? Padding(
              padding: const EdgeInsets.all(16),
              child: _fehler == null
                  ? const Center(child: CircularProgressIndicator())
                  : LadeFehler(text: _fehler!, erneut: _laden),
            )
          : _inhalt(positionen),
    );
  }

  Widget _inhalt(List<AblieferPosition> positionen) {
    const leise = TextStyle(color: DisposioFarben.textLeise);
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Text('${widget.stopp.reihenfolge}. ${widget.stopp.bezeichnung} · ${widget.tour.tournummer}', style: leise),
        const SizedBox(height: 4),
        Text(Texte.t('ablieferung_titel'),
            style: Theme.of(context).textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w700)),
        if (_offline) ...[
          const SizedBox(height: 8),
          StatusZeile(Texte.t('ohne_netz_hinweis')),
        ],
        const SizedBox(height: 12),
        if (positionen.isEmpty)
          Card(child: Padding(padding: const EdgeInsets.all(16), child: Text(Texte.t('keine_zustellungen'), style: leise))),
        for (final p in positionen) _positionKarte(p),
        const SizedBox(height: 8),
        TextField(
          controller: _bemerkung,
          maxLength: 1000,
          minLines: 2,
          maxLines: 5,
          decoration: InputDecoration(labelText: Texte.t('bemerkung'), alignLabelWithHint: true),
        ),
        FotoAuswahl(
          titel: Texte.t('fotos_ware'),
          pfade: _fotos,
          geaendert: (neu) => setState(() => _fotos = neu),
        ),
        const SizedBox(height: 16),
        FotoAuswahl(
          titel: Texte.t('lieferschein_foto'),
          pfade: _lieferscheine,
          max: 3,
          geaendert: (neu) => setState(() => _lieferscheine = neu),
        ),
        if (_fehler != null) ...[
          const SizedBox(height: 12),
          Text(_fehler!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
        ],
        const SizedBox(height: 16),
        FilledButton.icon(
          icon: const Icon(Icons.arrow_forward),
          label: Text(Texte.t('weiter_zum_empfang')),
          onPressed: positionen.isEmpty ? null : _weiter,
        ),
        const SizedBox(height: 8),
        OutlinedButton(onPressed: () => Navigator.pop(context, false), child: Text(Texte.t('abbrechen'))),
      ],
    );
  }

  Widget _positionKarte(AblieferPosition p) {
    const leise = TextStyle(color: DisposioFarben.textLeise);
    final a = p.auftrag;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text('${a.auftragsnummer} · ${a.kunde}', style: const TextStyle(fontWeight: FontWeight.w700)),
            Text('${Texte.t('soll')}: ${a.sollAnzahl} × ${a.verpackungstyp}', style: leise),
            if (a.nachgewiesen != null)
              Text('${Texte.t('schon_nachgewiesen')}: ${a.nachgewiesen}', style: leise),
            const SizedBox(height: 8),
            TextField(
              controller: _anzahlFelder[a.auftragId],
              keyboardType: TextInputType.number,
              decoration: InputDecoration(labelText: Texte.t('abgeliefert_anzahl')),
              onChanged: (text) => setState(() => p.anzahl = int.tryParse(text.trim()) ?? p.anzahl),
            ),
            if (p.teilmenge) ...[
              const SizedBox(height: 8),
              DropdownButtonFormField<String>(
                initialValue: p.ausnahmegrund,
                isExpanded: true,
                decoration: InputDecoration(labelText: Texte.t('ausnahmegrund')),
                items: [
                  for (final e in AblieferungSeite.ausnahmegruende.entries)
                    DropdownMenuItem(value: e.key, child: Text(Texte.t(e.value))),
                ],
                onChanged: (wert) => setState(() => p.ausnahmegrund = wert),
              ),
            ],
          ],
        ),
      ),
    );
  }
}
