import 'package:flutter/material.dart';

import '../services/api_client.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';
import '../widgets/tour_bausteine.dart';
import 'problem_seite.dart';
import 'stopp_seite.dart';

/// Wireframe M03: Aktive Tour. Fahrt starten, naechster Stopp, Angekommen/Entladen/Pause/Problem,
/// Tour abschliessen. GPS kommt in Phase 3, bis dahin zeigt die Statuszeile "GPS aus".
class TourSeite extends StatefulWidget {
  const TourSeite({super.key, required this.tourId});

  final int tourId;

  @override
  State<TourSeite> createState() => _TourSeiteState();
}

class _TourSeiteState extends State<TourSeite> {
  final _api = ApiClient();
  TourDetails? _tour;
  String? _fehler;
  bool _arbeitet = false;

  @override
  void initState() {
    super.initState();
    _laden();
  }

  Future<void> _laden() async {
    setState(() => _fehler = null);
    try {
      final tour = await _api.tour(widget.tourId);
      if (mounted) setState(() => _tour = tour);
    } on ApiFehler catch (e) {
      if (mounted) setState(() => _fehler = e.text);
    }
  }

  /// Fuehrt eine Aktion aus, zeigt Erfolg oder Fehler und laedt die Tour neu.
  /// [aufruf] gibt den Erfolgstext zurueck, oder null wenn abgebrochen wurde.
  Future<void> _aktion(Future<String?> Function() aufruf) async {
    setState(() => _arbeitet = true);
    try {
      final erfolg = await aufruf();
      if (erfolg != null && mounted) meldung(context, erfolg);
    } on ApiFehler catch (e) {
      if (mounted) meldung(context, _fehlerText(e));
    } finally {
      await _laden();
      if (mounted) setState(() => _arbeitet = false);
    }
  }

  String _fehlerText(ApiFehler e) =>
      e.code == 'andere_tour_aktiv' ? '${e.text} (${e.daten['tournummer']})' : e.text;

  Future<void> _tourStatus(String aktion) =>
      _aktion(() async {
        await _api.tourStatus(widget.tourId, aktion);
        return Texte.t('gemeldet');
      });

  Future<void> _stoppStatus(Stopp stopp, String aktion) =>
      _aktion(() async {
        await _api.stoppStatus(stopp.stoppId, aktion);
        return Texte.t('gemeldet');
      });

  Future<void> _beenden(TourDetails tour) async {
    final offene = [for (final s in tour.offeneStopps) '${s.reihenfolge}. ${s.bezeichnung}'];
    if (await _frageBeenden(offene) != true) return;
    await _aktion(() async {
      try {
        await _api.tourStatus(widget.tourId, 'beenden', erzwingen: offene.isNotEmpty);
      } on ApiFehler catch (e) {
        // Die Anzeige war veraltet: Server kennt noch offene Stopps, also erneut nachfragen
        if (e.code != 'offene_stopps') rethrow;
        final liste = [
          for (final s in e.daten['stopps'] as List) '${s['reihenfolge']}. ${s['bezeichnung']}',
        ];
        if (!mounted || await _frageBeenden(liste) != true) return null;
        await _api.tourStatus(widget.tourId, 'beenden', erzwingen: true);
      }
      return Texte.t('tour_beendet');
    });
  }

  /// Rueckfrage vor dem Abschliessen. Bei offenen Stopps werden diese aufgelistet.
  Future<bool?> _frageBeenden(List<String> offeneStopps) {
    return showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(Texte.t('tour_abschliessen')),
        content: offeneStopps.isEmpty
            ? Text(Texte.t('tour_abschliessen_frage'))
            : Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(Texte.t('offene_stopps_frage')),
                  const SizedBox(height: 8),
                  for (final s in offeneStopps) Text('• $s'),
                ],
              ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: Text(Texte.t('abbrechen'))),
          FilledButton(
            style: FilledButton.styleFrom(minimumSize: const Size(0, 44)),
            onPressed: () => Navigator.pop(context, true),
            child: Text(Texte.t(offeneStopps.isEmpty ? 'tour_abschliessen' : 'trotzdem_abschliessen')),
          ),
        ],
      ),
    );
  }

  Future<void> _problem(TourDetails tour, Stopp? stopp) async {
    final gemeldet = await Navigator.push<bool>(
      context,
      MaterialPageRoute(builder: (_) => ProblemSeite(tour: tour, stoppId: stopp?.stoppId)),
    );
    if (gemeldet == true && mounted) meldung(context, Texte.t('problem_gemeldet'));
  }

  Future<void> _stoppOeffnen(Stopp stopp) async {
    await Navigator.push(
      context,
      MaterialPageRoute(builder: (_) => StoppSeite(tourId: widget.tourId, stoppId: stopp.stoppId)),
    );
    _laden();
  }

  String _statusZeile(String status) => switch (status) {
        TourDetails.unterwegs => Texte.t('status_unterwegs'),
        TourDetails.pausiert => Texte.t('status_pausiert'),
        TourDetails.abgeschlossen => Texte.t('status_abgeschlossen'),
        _ => Texte.t('bereit_gps_aus'),
      };

  @override
  Widget build(BuildContext context) {
    final tour = _tour;
    return Scaffold(
      appBar: AppBar(title: Text(tour?.tournummer ?? Texte.t('tour'))),
      body: tour == null
          ? Padding(
              padding: const EdgeInsets.all(16),
              child: _fehler == null
                  ? const Center(child: CircularProgressIndicator())
                  : LadeFehler(text: '${Texte.t('tour_laden_fehlgeschlagen')} $_fehler', erneut: _laden),
            )
          : RefreshIndicator(onRefresh: _laden, child: _inhalt(tour)),
    );
  }

  Widget _inhalt(TourDetails tour) {
    final naechster = tour.naechsterStopp;
    final unterwegs = tour.status == TourDetails.unterwegs;
    final laeuft = unterwegs || tour.status == TourDetails.pausiert;
    final frei = !_arbeitet;
    const leise = TextStyle(color: DisposioFarben.textLeise);

    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.all(16),
      children: [
        Text('${Texte.t('tour')} ${tour.tournummer}',
            style: Theme.of(context).textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w700)),
        const SizedBox(height: 4),
        Text('${tour.startort} → ${tour.zielort}', style: const TextStyle(fontWeight: FontWeight.w600)),
        Text('${tagUndZeit(tour.startzeit)} – ${uhrzeit(tour.endzeit)}', style: leise),
        if (tour.kennzeichen != null)
          Text('${Texte.t('fahrzeug')}: ${tour.kennzeichen} (${tour.fahrzeugtyp})', style: leise),
        const SizedBox(height: 12),
        StatusZeile(_statusZeile(tour.status)),
        const SizedBox(height: 12),
        if (tour.bereit)
          _GrosserKnopf(
            icon: Icons.play_arrow,
            text: Texte.t('fahrt_starten'),
            onPressed: frei ? () => _tourStatus('starten') : null,
          ),
        if (tour.status == TourDetails.pausiert)
          _GrosserKnopf(
            icon: Icons.play_arrow,
            text: Texte.t('fahrt_fortsetzen'),
            onPressed: frei ? () => _tourStatus('fortsetzen') : null,
          ),
        if (naechster != null)
          _NaechsterStoppKarte(
            stopp: naechster,
            oeffnen: () => _stoppOeffnen(naechster),
            abschliessen: unterwegs && frei && naechster.status != Stopp.offen
                ? () => _stoppStatus(naechster, 'abgeschlossen')
                : null,
          )
        else if (tour.stopps.isNotEmpty)
          Card(child: Padding(padding: const EdgeInsets.all(16), child: Text(Texte.t('alle_stopps_erledigt')))),
        const SizedBox(height: 6),
        // 2x2 grosse Knoepfe aus dem Wireframe
        Row(
          children: [
            _Kachel(
              icon: Icons.flag_outlined,
              text: Texte.t('angekommen'),
              onPressed: unterwegs && frei && naechster?.status == Stopp.offen
                  ? () => _stoppStatus(naechster!, 'angekommen')
                  : null,
            ),
            const SizedBox(width: 8),
            _Kachel(
              icon: Icons.inventory_2_outlined,
              text: Texte.t('entladen'),
              onPressed: unterwegs && frei && naechster?.status == Stopp.angekommen
                  ? () => _stoppStatus(naechster!, 'entladen')
                  : null,
            ),
          ],
        ),
        const SizedBox(height: 8),
        Row(
          children: [
            _Kachel(
              icon: Icons.pause,
              text: Texte.t('pause'),
              onPressed: unterwegs && frei ? () => _tourStatus('pausieren') : null,
            ),
            const SizedBox(width: 8),
            _Kachel(
              icon: Icons.report_problem_outlined,
              text: Texte.t('problem_melden'),
              onPressed: tour.status != TourDetails.abgeschlossen && frei ? () => _problem(tour, naechster) : null,
            ),
          ],
        ),
        const SizedBox(height: 16),
        Text(Texte.t('alle_stopps'), style: const TextStyle(fontWeight: FontWeight.w700)),
        for (final stopp in tour.stopps)
          Card(
            child: ListTile(
              leading: CircleAvatar(
                radius: 14,
                backgroundColor: DisposioFarben.statusBox,
                child: Text('${stopp.reihenfolge}', style: const TextStyle(fontSize: 12)),
              ),
              title: Text(stopp.bezeichnung),
              subtitle: Text(
                  '${stoppArtText(stopp.art)} · ${zeitfenster(stopp.zeitfensterVon, stopp.zeitfensterBis)}'),
              trailing: StatusBadge.stopp(stopp.status),
              onTap: () => _stoppOeffnen(stopp),
            ),
          ),
        const SizedBox(height: 12),
        if (laeuft)
          OutlinedButton.icon(
            icon: const Icon(Icons.check_circle_outline),
            label: Text(Texte.t('tour_abschliessen')),
            onPressed: frei ? () => _beenden(tour) : null,
          ),
        const SizedBox(height: 12),
        Text(Texte.t('sicherer_stand'), textAlign: TextAlign.center, style: leise),
      ],
    );
  }
}

class _GrosserKnopf extends StatelessWidget {
  const _GrosserKnopf({required this.icon, required this.text, required this.onPressed});

  final IconData icon;
  final String text;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: FilledButton.icon(
        style: FilledButton.styleFrom(
          minimumSize: const Size.fromHeight(64),
          textStyle: const TextStyle(fontSize: 18, fontWeight: FontWeight.w700),
        ),
        icon: Icon(icon),
        label: Text(text),
        onPressed: onPressed,
      ),
    );
  }
}

class _Kachel extends StatelessWidget {
  const _Kachel({required this.icon, required this.text, required this.onPressed});

  final IconData icon;
  final String text;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) {
    return Expanded(
      child: OutlinedButton(
        style: OutlinedButton.styleFrom(
          minimumSize: const Size.fromHeight(76),
          backgroundColor: Colors.white,
        ),
        onPressed: onPressed,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon),
            const SizedBox(height: 4),
            Text(text, textAlign: TextAlign.center, style: const TextStyle(fontWeight: FontWeight.w600)),
          ],
        ),
      ),
    );
  }
}

class _NaechsterStoppKarte extends StatelessWidget {
  const _NaechsterStoppKarte({required this.stopp, required this.oeffnen, required this.abschliessen});

  final Stopp stopp;
  final VoidCallback oeffnen;
  final VoidCallback? abschliessen;

  @override
  Widget build(BuildContext context) {
    const leise = TextStyle(color: DisposioFarben.textLeise);
    final ware = wareKurz(stopp);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    Texte.t(stopp.art == 'Abholung' ? 'naechste_abholung' : 'naechste_zustellung'),
                    style: leise,
                  ),
                ),
                StatusBadge.stopp(stopp.status),
              ],
            ),
            const SizedBox(height: 4),
            Text(stopp.bezeichnung, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w700)),
            Text(stopp.adresse),
            const SizedBox(height: 4),
            Text('${Texte.t('zeitfenster')}: ${zeitfenster(stopp.zeitfensterVon, stopp.zeitfensterBis)}', style: leise),
            if (ware.isNotEmpty) Text(ware, style: leise),
            const SizedBox(height: 12),
            Row(
              children: [
                Expanded(child: OutlinedButton(onPressed: oeffnen, child: Text(Texte.t('stopp_oeffnen')))),
                if (abschliessen != null) ...[
                  const SizedBox(width: 8),
                  Expanded(child: FilledButton(onPressed: abschliessen, child: Text(Texte.t('stopp_abschliessen')))),
                ],
              ],
            ),
          ],
        ),
      ),
    );
  }
}
