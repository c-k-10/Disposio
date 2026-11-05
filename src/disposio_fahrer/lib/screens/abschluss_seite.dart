import 'package:flutter/material.dart';

import '../services/api_client.dart';
import '../services/gps_dienst.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';
import '../widgets/tour_bausteine.dart';

/// Wireframe M12: Tour abschliessen. Offene Stopps mit Warnung, Abschlussbemerkung,
/// ausstehende GPS-Uebertragungen. Beim Beenden: Puffer senden, Tour beenden, GPS stoppen.
/// Gibt true zurueck, wenn die Tour beendet wurde.
class AbschlussSeite extends StatefulWidget {
  const AbschlussSeite({super.key, required this.tourId, required this.tournummer});

  final int tourId;
  final String tournummer;

  @override
  State<AbschlussSeite> createState() => _AbschlussSeiteState();
}

class _AbschlussSeiteState extends State<AbschlussSeite> {
  final _api = ApiClient();
  final _bemerkung = TextEditingController();
  TourAbschluss? _abschluss;
  String? _fehler;
  bool _arbeitet = false;

  @override
  void initState() {
    super.initState();
    _laden();
  }

  @override
  void dispose() {
    _bemerkung.dispose();
    super.dispose();
  }

  Future<void> _laden() async {
    setState(() => _fehler = null);
    try {
      final abschluss = await _api.abschluss(widget.tourId);
      if (mounted) setState(() => _abschluss = abschluss);
    } on ApiFehler catch (e) {
      if (mounted) setState(() => _fehler = e.text);
    }
  }

  Future<void> _senden() async {
    setState(() => _arbeitet = true);
    final fertig = await GpsDienst.jetztSenden();
    if (mounted) {
      meldung(context, Texte.t(fertig ? 'alles_uebertragen' : 'uebertragung_offen'));
      setState(() => _arbeitet = false);
    }
  }

  Future<void> _beenden(TourAbschluss abschluss) async {
    setState(() => _arbeitet = true);
    try {
      // Erst alle Punkte senden, sonst lehnt die API sie nach dem Beenden ab
      await GpsDienst.jetztSenden();
      if (GpsDienst.ausstehendFuer(widget.tourId) > 0) {
        if (mounted) meldung(context, Texte.t('uebertragung_offen'));
        return;
      }
      final bemerkung = _bemerkung.text.trim();
      await _api.tourStatus(widget.tourId, 'beenden',
          erzwingen: abschluss.offeneStopps.isNotEmpty, bemerkung: bemerkung.isEmpty ? null : bemerkung);
      if (GpsDienst.tourId == widget.tourId) await GpsDienst.stoppen();
      if (mounted) Navigator.pop(context, true);
    } on ApiFehler catch (e) {
      if (!mounted) return;
      meldung(context, e.text);
      // Die Anzeige war veraltet (z. B. 'offene_stopps'): neu laden, dann kann erneut beendet werden
      await _laden();
    } finally {
      if (mounted) setState(() => _arbeitet = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final abschluss = _abschluss;
    return Scaffold(
      appBar: AppBar(title: Text(Texte.t('tour_abschliessen'))),
      body: abschluss == null
          ? Padding(
              padding: const EdgeInsets.all(16),
              child: _fehler == null
                  ? const Center(child: CircularProgressIndicator())
                  : LadeFehler(text: '${Texte.t('tour_laden_fehlgeschlagen')} $_fehler', erneut: _laden),
            )
          : _inhalt(abschluss),
    );
  }

  Widget _inhalt(TourAbschluss abschluss) {
    final frei = !_arbeitet;
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Text('${Texte.t('tour')} ${widget.tournummer}',
            style: Theme.of(context).textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w700)),
        const SizedBox(height: 12),
        if (abschluss.offeneStopps.isEmpty)
          Card(
            child: ListTile(
              leading: const Icon(Icons.check_circle_outline, color: DisposioFarben.dunkelblau),
              title: Text(Texte.t('alle_stopps_erledigt')),
            ),
          )
        else
          Card(
            color: const Color(0xFFFFF4E5),
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      const Icon(Icons.warning_amber, color: Color(0xFFB26A00)),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(Texte.t('offene_stopps_warnung'),
                            style: const TextStyle(fontWeight: FontWeight.w600)),
                      ),
                    ],
                  ),
                  const SizedBox(height: 8),
                  for (final s in abschluss.offeneStopps) Text('• $s'),
                ],
              ),
            ),
          ),
        const SizedBox(height: 12),
        TextField(
          controller: _bemerkung,
          maxLength: 1000,
          minLines: 3,
          maxLines: 6,
          decoration: InputDecoration(labelText: Texte.t('abschlussbemerkung'), alignLabelWithHint: true),
        ),
        const SizedBox(height: 4),
        ValueListenableBuilder<int>(
          valueListenable: GpsDienst.ausstehend,
          builder: (context, _, _) => Card(
            child: ListTile(
              leading: const Icon(Icons.sync),
              title: Text('${Texte.t('ausstehende_uebertragungen')} ${GpsDienst.ausstehendFuer(widget.tourId)}'),
              trailing: TextButton(onPressed: frei ? _senden : null, child: Text(Texte.t('jetzt_senden'))),
            ),
          ),
        ),
        const SizedBox(height: 16),
        if (!abschluss.darfBeenden)
          Padding(
            padding: const EdgeInsets.only(bottom: 8),
            child: Text(Texte.t('tour_nicht_beendbar'), style: const TextStyle(color: DisposioFarben.textLeise)),
          ),
        FilledButton.icon(
          style: FilledButton.styleFrom(
            minimumSize: const Size.fromHeight(56),
            textStyle: const TextStyle(fontSize: 18, fontWeight: FontWeight.w700),
          ),
          icon: const Icon(Icons.check_circle_outline),
          label: Text(Texte.t('tour_beenden')),
          onPressed: frei && abschluss.darfBeenden ? () => _beenden(abschluss) : null,
        ),
      ],
    );
  }
}
