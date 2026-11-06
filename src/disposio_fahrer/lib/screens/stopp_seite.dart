import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

import '../services/api_client.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';
import '../widgets/tour_bausteine.dart';
import 'problem_seite.dart';

/// Wireframe M04: Stoppdetails mit Adresse, Zeitfenster, Auftraegen und Navigation.
class StoppSeite extends StatefulWidget {
  const StoppSeite({super.key, required this.tourId, required this.stoppId});

  final int tourId;
  final int stoppId;

  @override
  State<StoppSeite> createState() => _StoppSeiteState();
}

class _StoppSeiteState extends State<StoppSeite> {
  final _api = ApiClient();
  TourDetails? _tour;
  String? _fehler;
  bool _arbeitet = false;

  Stopp? get _stopp {
    for (final s in _tour?.stopps ?? const <Stopp>[]) {
      if (s.stoppId == widget.stoppId) return s;
    }
    return null;
  }

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

  Future<void> _status(String aktion) async {
    setState(() => _arbeitet = true);
    try {
      await _api.stoppStatus(widget.stoppId, aktion);
      if (mounted) meldung(context, Texte.t('gemeldet'));
    } on ApiFehler catch (e) {
      if (mounted) meldung(context, e.text);
    } finally {
      await _laden();
      if (mounted) setState(() => _arbeitet = false);
    }
  }

  /// Google Maps mit Koordinaten, ohne Koordinaten mit der Adresse.
  Future<void> _navigation(Stopp stopp) async {
    final ziel = stopp.breitengrad != null && stopp.laengengrad != null
        ? '${stopp.breitengrad},${stopp.laengengrad}'
        : stopp.adresse;
    final uri = Uri.https('www.google.com', '/maps/dir/', {'api': '1', 'destination': ziel});
    var geoeffnet = false;
    try {
      geoeffnet = await launchUrl(uri, mode: LaunchMode.externalApplication);
    } catch (_) {
      // Keine App fuer den Link vorhanden
    }
    if (!geoeffnet && mounted) meldung(context, Texte.t('navigation_fehler'));
  }

  Future<void> _problem(TourDetails tour) async {
    final gemeldet = await Navigator.push<bool>(
      context,
      MaterialPageRoute(builder: (_) => ProblemSeite(tour: tour, stoppId: widget.stoppId)),
    );
    if (gemeldet == true && mounted) meldung(context, Texte.t('problem_gemeldet'));
  }

  @override
  Widget build(BuildContext context) {
    final tour = _tour;
    final stopp = _stopp;
    return Scaffold(
      appBar: AppBar(title: Text(Texte.t('stoppdetails'))),
      body: tour == null || stopp == null
          ? Padding(
              padding: const EdgeInsets.all(16),
              child: _fehler == null && tour == null
                  ? const Center(child: CircularProgressIndicator())
                  : LadeFehler(
                      text: '${Texte.t('tour_laden_fehlgeschlagen')} ${_fehler ?? Texte.t('stopp_nicht_gefunden')}',
                      erneut: _laden,
                    ),
            )
          : RefreshIndicator(onRefresh: _laden, child: _inhalt(tour, stopp)),
    );
  }

  Widget _inhalt(TourDetails tour, Stopp stopp) {
    const leise = TextStyle(color: DisposioFarben.textLeise);
    final unterwegs = tour.status == TourDetails.unterwegs;
    final frei = !_arbeitet;

    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.all(16),
      children: [
        Row(
          children: [
            Expanded(
              child: Text('${stopp.reihenfolge}. ${stoppArtText(stopp.art)} · ${tour.tournummer}', style: leise),
            ),
            StatusBadge.stopp(stopp.status),
          ],
        ),
        const SizedBox(height: 4),
        Text(stopp.bezeichnung,
            style: Theme.of(context).textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w700)),
        const SizedBox(height: 12),
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                _Zeile(Texte.t('adresse'), stopp.adresse),
                _Zeile(Texte.t('zeitfenster'), zeitfenster(stopp.zeitfensterVon, stopp.zeitfensterBis)),
                if (stopp.geplanteAnkunft != null)
                  _Zeile(Texte.t('geplante_ankunft'), tagUndZeit(stopp.geplanteAnkunft!)),
                if (stopp.istAnkunft != null) _Zeile(Texte.t('ankunft'), tagUndZeit(stopp.istAnkunft!)),
                if (stopp.istAbfahrt != null) _Zeile(Texte.t('abfahrt'), tagUndZeit(stopp.istAbfahrt!)),
              ],
            ),
          ),
        ),
        const SizedBox(height: 6),
        Text(Texte.t('auftraege'), style: const TextStyle(fontWeight: FontWeight.w700)),
        if (stopp.auftraege.isEmpty)
          Card(child: Padding(padding: const EdgeInsets.all(16), child: Text(Texte.t('keine_auftraege'), style: leise))),
        for (final auftrag in stopp.auftraege) _AuftragKarte(auftrag: auftrag),
        const SizedBox(height: 12),
        FilledButton.icon(
          icon: const Icon(Icons.navigation_outlined),
          label: Text(Texte.t('navigation_oeffnen')),
          onPressed: () => _navigation(stopp),
        ),
        const SizedBox(height: 8),
        Row(
          children: [
            Expanded(
              child: OutlinedButton(
                onPressed: unterwegs && frei && stopp.status == Stopp.offen ? () => _status('angekommen') : null,
                child: Text(Texte.t('angekommen')),
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: OutlinedButton(
                onPressed: unterwegs && frei && stopp.status == Stopp.angekommen ? () => _status('entladen') : null,
                child: Text(Texte.t('entladen')),
              ),
            ),
          ],
        ),
        const SizedBox(height: 8),
        OutlinedButton(
          onPressed: unterwegs && frei && (stopp.status == Stopp.angekommen || stopp.status == Stopp.entladen)
              ? () => _status('abgeschlossen')
              : null,
          child: Text(Texte.t('stopp_abschliessen')),
        ),
        const SizedBox(height: 8),
        // Foto und Unterschrift kommen in Phase 5
        OutlinedButton.icon(
          icon: const Icon(Icons.photo_camera_outlined),
          label: Text(Texte.t('ablieferung_dokumentieren')),
          onPressed: () => meldung(context, Texte.t('kommt_phase5')),
        ),
        const SizedBox(height: 8),
        OutlinedButton.icon(
          icon: const Icon(Icons.report_problem_outlined),
          label: Text(Texte.t('problem_zustellung')),
          onPressed: tour.status != TourDetails.abgeschlossen ? () => _problem(tour) : null,
        ),
        const SizedBox(height: 8),
        TextButton.icon(
          icon: const Icon(Icons.arrow_back),
          label: Text(Texte.t('zur_tour')),
          onPressed: () => Navigator.pop(context),
        ),
      ],
    );
  }
}

class _AuftragKarte extends StatelessWidget {
  const _AuftragKarte({required this.auftrag});

  final Auftrag auftrag;

  String _zahl(double wert) => wert == wert.roundToDouble() ? wert.toStringAsFixed(0) : wert.toString();

  @override
  Widget build(BuildContext context) {
    final a = auftrag;
    final temperatur = switch ((a.temperaturMin, a.temperaturMax)) {
      (null, null) => null,
      (final min?, null) => '≥ ${_zahl(min)} °C',
      (null, final max?) => '≤ ${_zahl(max)} °C',
      (final min?, final max?) => '${_zahl(min)} – ${_zahl(max)} °C',
    };
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text('${a.auftragsnummer} · ${a.kunde}', style: const TextStyle(fontWeight: FontWeight.w700)),
                ),
                Text(stoppArtText(a.vorgangArt), style: const TextStyle(color: DisposioFarben.textLeise)),
              ],
            ),
            const SizedBox(height: 6),
            Text('${a.anzahl} × ${a.verpackungstyp} · ${a.gutart} · ${_zahl(a.gewichtKg)} kg'),
            if (a.gefahrgut)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: Row(
                  children: [
                    const Icon(Icons.warning_amber, size: 18, color: Colors.orange),
                    const SizedBox(width: 4),
                    Text(Texte.t('gefahrgut'), style: const TextStyle(fontWeight: FontWeight.w700)),
                  ],
                ),
              ),
            const SizedBox(height: 4),
            if (temperatur != null) _Zeile(Texte.t('temperatur'), temperatur),
            _Zeile(Texte.t('ansprechpartner'), a.ansprechpartner),
            _Zeile(Texte.t('equipment'), a.ladeEntladeequipment),
            if (a.kundenreferenz != null && a.kundenreferenz!.isNotEmpty) _Zeile(Texte.t('referenz'), a.kundenreferenz!),
            if (a.avisierungErforderlich)
              Text(Texte.t('avisierung'), style: const TextStyle(fontWeight: FontWeight.w600)),
          ],
        ),
      ),
    );
  }
}

class _Zeile extends StatelessWidget {
  const _Zeile(this.bezeichnung, this.wert);

  final String bezeichnung;
  final String wert;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 2),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 130,
            child: Text(bezeichnung, style: const TextStyle(color: DisposioFarben.textLeise)),
          ),
          Expanded(child: Text(wert)),
        ],
      ),
    );
  }
}
