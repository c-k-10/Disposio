import 'dart:io';

import 'package:flutter/material.dart';
import 'package:signature/signature.dart';

import '../services/api_client.dart';
import '../services/warteschlange.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';
import '../widgets/foto_auswahl.dart';
import '../widgets/tour_bausteine.dart';
import 'ablieferung_seite.dart';

/// Wireframe M07: Empfang bestaetigen. Empfaengername und Unterschrift; ohne Unterschrift ist ein Foto Pflicht,
/// ebenso bei „Empfänger nicht anwesend“. Danach liegen Nachweise und Dokumente in der Warteschlange (M11)
/// und der Stopp wird wie in Phase 2 abgeschlossen. Gibt true zurueck, wenn gespeichert wurde.
class EmpfangSeite extends StatefulWidget {
  const EmpfangSeite({
    super.key,
    required this.tour,
    required this.stopp,
    required this.positionen,
    required this.bemerkung,
    required this.fotos,
    required this.lieferscheine,
  });

  final TourDetails tour;
  final Stopp stopp;
  final List<AblieferPosition> positionen;
  final String bemerkung;
  final List<String> fotos;
  final List<String> lieferscheine;

  @override
  State<EmpfangSeite> createState() => _EmpfangSeiteState();
}

class _EmpfangSeiteState extends State<EmpfangSeite> {
  final _api = ApiClient();
  final _name = TextEditingController();
  final _unterschrift = SignatureController(
    penStrokeWidth: 3,
    penColor: Colors.black,
    exportBackgroundColor: Colors.white,
  );
  List<String> _weitereFotos = [];
  bool _nichtAnwesend = false;
  bool _speichert = false;
  bool _gespeichert = false;
  bool _zeichnet = false;
  String? _fehler;

  @override
  void dispose() {
    _name.dispose();
    _unterschrift.dispose();
    if (!_gespeichert) {
      for (final pfad in _weitereFotos) {
        File(pfad).delete().ignore();
      }
    }
    super.dispose();
  }

  Future<void> _abschliessen() async {
    final name = _name.text.trim();
    final fotos = [...widget.fotos, ..._weitereFotos];
    final mitUnterschrift = !_nichtAnwesend && _unterschrift.isNotEmpty;
    if (!_nichtAnwesend && name.isEmpty) {
      setState(() => _fehler = Texte.t('empfaenger_fehlt'));
      return;
    }
    // Ohne Unterschrift (nicht anwesend oder verweigert) beweist ein Foto die Ablieferung
    if (!mitUnterschrift && fotos.isEmpty) {
      setState(() => _fehler = Texte.t(_nichtAnwesend ? 'foto_pflicht_abwesend' : 'unterschrift_oder_foto'));
      return;
    }

    setState(() {
      _fehler = null;
      _speichert = true;
    });
    try {
      String? unterschriftPfad;
      if (mitUnterschrift) {
        final png = await _unterschrift.toPngBytes();
        if (png != null) unterschriftPfad = await Warteschlange.bildSichern(png, endung: 'png');
      }
      final art = _nichtAnwesend ? 'Ohne Empfänger' : (unterschriftPfad != null ? 'Unterschrift' : 'Foto');
      final zeitpunkt = DateTime.now().toUtc().toIso8601String();

      // Je Auftrag ein Nachweis, dahinter seine Dokumente (gleiche Bilder fuer alle Auftraege des Stopps)
      final eintraege = <WarteEintrag>[];
      for (final p in widget.positionen) {
        final nachweis = WarteEintrag(
          id: Warteschlange.neueId(),
          art: 'nachweis',
          titel: 'w_nachweis|${p.auftrag.auftragsnummer}',
          daten: {
            'stoppId': widget.stopp.stoppId,
            'auftragId': p.auftrag.auftragId,
            'anzahl': p.anzahl,
            'empfaengername': _nichtAnwesend ? null : name,
            'bestaetigungsart': art,
            'ausnahmegrund': p.ausnahmegrund,
            'bemerkung': widget.bemerkung.isEmpty ? null : widget.bemerkung,
            'zeitpunkt': zeitpunkt,
          },
          erstellt: DateTime.now(),
        );
        eintraege.add(nachweis);
        void dokument(String pfad, String dokumentart, String titel) => eintraege.add(WarteEintrag(
              id: Warteschlange.neueId(),
              art: 'dokument',
              titel: '$titel|${p.auftrag.auftragsnummer}',
              daten: {'art': dokumentart, 'eigentuemer': 'nachweisId'},
              datei: pfad,
              eltern: nachweis.id,
              erstellt: DateTime.now(),
            ));
        if (unterschriftPfad != null) dokument(unterschriftPfad, 'Unterschrift', 'w_unterschrift');
        for (final f in fotos) {
          dokument(f, 'Foto', 'w_foto');
        }
        for (final f in widget.lieferscheine) {
          dokument(f, 'Lieferschein', 'w_lieferschein');
        }
      }
      await Warteschlange.hinzufuegen(eintraege);
      _gespeichert = true;

      // Stopp abschliessen wie in Phase 2 (braucht Netz). Der Nachweis bleibt in jedem Fall gespeichert.
      var text = Texte.t('nachweis_gespeichert');
      final status = widget.stopp.status;
      if (widget.tour.status == TourDetails.unterwegs && (status == Stopp.angekommen || status == Stopp.entladen)) {
        try {
          await _api.stoppStatus(widget.stopp.stoppId, 'abgeschlossen');
          text = Texte.t('ablieferung_fertig');
        } on ApiFehler catch (e) {
          text = '${Texte.t('nachweis_gespeichert')} ${Texte.t('stopp_nicht_abgeschlossen')} (${e.text})';
        }
      }
      if (mounted) {
        meldung(context, text);
        Navigator.pop(context, true);
      }
    } finally {
      if (mounted) setState(() => _speichert = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    const leise = TextStyle(color: DisposioFarben.textLeise);
    final frei = !_speichert;
    final summe = widget.positionen.fold<int>(0, (s, p) => s + p.anzahl);
    return Scaffold(
      appBar: AppBar(title: Text(Texte.t('empfang_titel'))),
      body: ListView(
        physics: _zeichnet ? const NeverScrollableScrollPhysics() : null,
        padding: const EdgeInsets.all(16),
        children: [
          Text('${widget.stopp.reihenfolge}. ${widget.stopp.bezeichnung}', style: leise),
          const SizedBox(height: 4),
          Text('${Texte.t('abgeliefert')}: $summe · ${widget.positionen.length} ${Texte.t('auftraege')}',
              style: const TextStyle(fontWeight: FontWeight.w600)),
          const SizedBox(height: 12),
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            title: Text(Texte.t('empfaenger_nicht_anwesend')),
            value: _nichtAnwesend,
            activeTrackColor: DisposioFarben.dunkelblau,
            onChanged: frei ? (wert) => setState(() => _nichtAnwesend = wert) : null,
          ),
          if (!_nichtAnwesend) ...[
            TextField(
              controller: _name,
              enabled: frei,
              maxLength: 100,
              textCapitalization: TextCapitalization.words,
              decoration: InputDecoration(labelText: Texte.t('empfaengername')),
            ),
            Row(
              children: [
                Expanded(child: Text(Texte.t('unterschrift'), style: const TextStyle(fontWeight: FontWeight.w700))),
                TextButton(onPressed: frei ? _unterschrift.clear : null, child: Text(Texte.t('loeschen'))),
              ],
            ),
            // Waehrend des Unterschreibens nicht scrollen
            Listener(
              onPointerDown: (_) => setState(() => _zeichnet = true),
              onPointerUp: (_) => setState(() => _zeichnet = false),
              onPointerCancel: (_) => setState(() => _zeichnet = false),
              child: Container(
                decoration: BoxDecoration(border: Border.all(color: DisposioFarben.rand)),
                child: Signature(controller: _unterschrift, height: 180, backgroundColor: Colors.white),
              ),
            ),
            const SizedBox(height: 4),
            Text(Texte.t('unterschrift_hinweis'), style: leise),
          ] else
            StatusZeile(Texte.t('abwesend_hinweis')),
          const SizedBox(height: 16),
          FotoAuswahl(
            titel: Texte.t(_nichtAnwesend ? 'foto_abstellort' : 'weitere_fotos'),
            pfade: _weitereFotos,
            aktiv: frei,
            geaendert: (neu) => setState(() => _weitereFotos = neu),
          ),
          if (widget.fotos.isNotEmpty)
            Padding(
              padding: const EdgeInsets.only(top: 4),
              child: Text('${Texte.t('fotos_aus_ablieferung')}: ${widget.fotos.length}', style: leise),
            ),
          if (_fehler != null) ...[
            const SizedBox(height: 12),
            Text(_fehler!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
          ],
          const SizedBox(height: 16),
          FilledButton.icon(
            style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(56)),
            icon: _speichert
                ? const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2))
                : const Icon(Icons.check_circle_outline),
            label: Text(Texte.t('ablieferung_abschliessen')),
            onPressed: frei ? _abschliessen : null,
          ),
          const SizedBox(height: 8),
          OutlinedButton(onPressed: frei ? () => Navigator.pop(context, false) : null, child: Text(Texte.t('zurueck'))),
        ],
      ),
    );
  }
}
