import 'dart:async';

import 'package:flutter/material.dart';

import '../services/api_client.dart';
import '../services/gps_dienst.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';
import '../widgets/tour_bausteine.dart';
import 'abschluss_seite.dart';
import 'chat_seite.dart';
import 'pause_seite.dart';
import 'problem_seite.dart';
import 'stopp_seite.dart';

/// Wireframe M03: Aktive Tour. Fahrt starten, naechster Stopp, Angekommen/Entladen/Pause/Problem,
/// Tour abschliessen. Phase 3: GPS waehrend der Fahrt, ETA je Stopp, Pause (M10) und Abschluss (M12).
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
  bool _pauseGezeigt = false;
  Timer? _aktualisieren;

  @override
  void initState() {
    super.initState();
    _laden();
    // Waehrend der Fahrt jede Minute neu laden, damit neue ETAs erscheinen
    _aktualisieren = Timer.periodic(const Duration(seconds: 60), (_) {
      if (_tour?.status == TourDetails.unterwegs && !_arbeitet) _laden();
    });
  }

  @override
  void dispose() {
    _aktualisieren?.cancel();
    super.dispose();
  }

  Future<void> _laden() async {
    setState(() => _fehler = null);
    try {
      final tour = await _api.tour(widget.tourId);
      if (!mounted) return;
      setState(() => _tour = tour);
      await _gpsAbgleichen(tour);
      // Pausierte Tour geoeffnet: einmal M10 zeigen
      if (tour.status == TourDetails.pausiert && !_pauseGezeigt && !_arbeitet && mounted) {
        _pauseGezeigt = true;
        await _pauseZeigen(tour);
      }
    } on ApiFehler catch (e) {
      if (mounted) setState(() => _fehler = e.text);
    }
  }

  /// GPS laeuft genau dann, wenn die Tour unterwegs ist (z. B. nach Neustart der App).
  Future<void> _gpsAbgleichen(TourDetails tour) async {
    if (tour.status == TourDetails.unterwegs) {
      await GpsDienst.starten(tour.tourId);
    } else if (GpsDienst.tourId == tour.tourId) {
      await GpsDienst.stoppen();
    }
  }

  /// Vor dem ersten „Fahrt starten“: kurzer Hinweis, dann fragt Android nach Standort und Benachrichtigungen.
  Future<void> _berechtigungEinholen() async {
    if (await GpsDienst.berechtigt() || !mounted) return;
    await showDialog<void>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(Texte.t('gps_hinweis_titel')),
        content: Text(Texte.t('gps_hinweis')),
        actions: [
          FilledButton(onPressed: () => Navigator.pop(context), child: Text(Texte.t('weiter'))),
        ],
      ),
    );
    await GpsDienst.berechtigungAnfragen();
  }

  Future<void> _starten() => _aktion(() async {
        await _berechtigungEinholen();
        await _api.tourStatus(widget.tourId, 'starten');
        final gps = await GpsDienst.starten(widget.tourId);
        return Texte.t(gps ? 'gemeldet' : 'gps_nicht_gestartet');
      });

  Future<void> _fortsetzen() => _aktion(() async {
        await _api.tourStatus(widget.tourId, 'fortsetzen');
        final gps = await GpsDienst.starten(widget.tourId);
        return Texte.t(gps ? 'gemeldet' : 'gps_nicht_gestartet');
      });

  Future<void> _pausieren() async {
    await _aktion(() async {
      // Gesammelte Punkte vorher senden, in der Pause lehnt die API sie ab
      await GpsDienst.jetztSenden();
      await _api.tourStatus(widget.tourId, 'pausieren');
      await GpsDienst.stoppen();
      _pauseGezeigt = true;
      return null;
    });
    final tour = _tour;
    if (tour != null && tour.status == TourDetails.pausiert && mounted) await _pauseZeigen(tour);
  }

  Future<void> _pauseZeigen(TourDetails tour) async {
    final fortgesetzt =
        await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => PauseSeite(tour: tour)));
    if (fortgesetzt == true && mounted) {
      meldung(context, Texte.t('gemeldet'));
      _laden();
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

  Future<void> _stoppStatus(Stopp stopp, String aktion) =>
      _aktion(() async {
        await _api.stoppStatus(stopp.stoppId, aktion);
        return Texte.t('gemeldet');
      });

  /// M12: eigener Bildschirm zum Abschliessen.
  Future<void> _beenden(TourDetails tour) async {
    final beendet = await Navigator.push<bool>(
      context,
      MaterialPageRoute(builder: (_) => AbschlussSeite(tourId: tour.tourId, tournummer: tour.tournummer)),
    );
    if (!mounted) return;
    if (beendet == true) meldung(context, Texte.t('tour_beendet'));
    _laden();
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
        TourDetails.unterwegs =>
          Texte.t(GpsDienst.aktiv.value && GpsDienst.tourId == widget.tourId ? 'unterwegs_gps_an' : 'unterwegs_gps_aus'),
        TourDetails.pausiert => Texte.t('pause_gps_pausiert'),
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
        ValueListenableBuilder<bool>(
          valueListenable: GpsDienst.aktiv,
          builder: (context, _, _) => StatusZeile(_statusZeile(tour.status)),
        ),
        const SizedBox(height: 12),
        if (tour.bereit)
          _GrosserKnopf(
            icon: Icons.play_arrow,
            text: Texte.t('fahrt_starten'),
            onPressed: frei ? _starten : null,
          ),
        if (tour.status == TourDetails.pausiert)
          _GrosserKnopf(
            icon: Icons.play_arrow,
            text: Texte.t('fahrt_fortsetzen'),
            onPressed: frei ? _fortsetzen : null,
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
              onPressed: unterwegs && frei ? _pausieren : null,
            ),
            const SizedBox(width: 8),
            _Kachel(
              icon: Icons.report_problem_outlined,
              text: Texte.t('problem_melden'),
              onPressed: tour.status != TourDetails.abgeschlossen && frei ? () => _problem(tour, naechster) : null,
            ),
          ],
        ),
        const SizedBox(height: 8),
        // Phase 4: Chat dieser Tour (M08)
        OutlinedButton.icon(
          style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(52), backgroundColor: Colors.white),
          icon: const Icon(Icons.chat_bubble_outline),
          label: Text(Texte.t('disposition_kontaktieren')),
          onPressed: () => dispositionKontaktieren(context, tour.tourId),
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
              subtitle: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('${stoppArtText(stopp.art)} · ${zeitfenster(stopp.zeitfensterVon, stopp.zeitfensterBis)}'),
                  if (stopp.status != Stopp.erledigt && stopp.aktuelleEta != null) EtaText(stopp),
                ],
              ),
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
            if (stopp.aktuelleEta != null) EtaText(stopp, gross: true),
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
