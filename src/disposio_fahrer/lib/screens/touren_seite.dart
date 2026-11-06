import 'package:flutter/material.dart';

import '../services/api_client.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';
import '../widgets/tour_bausteine.dart';
import 'tour_seite.dart';

/// Wireframe M02: Meine Touren. Aktive Tour zuerst, dann heute und die naechsten 7 Tage.
class TourenSeite extends StatefulWidget {
  const TourenSeite({super.key});

  @override
  State<TourenSeite> createState() => _TourenSeiteState();
}

class _TourenSeiteState extends State<TourenSeite> {
  final _api = ApiClient();
  List<TourKurz>? _touren;
  String? _fehler;

  @override
  void initState() {
    super.initState();
    _laden();
  }

  Future<void> _laden() async {
    setState(() => _fehler = null);
    try {
      final touren = await _api.touren();
      if (mounted) setState(() => _touren = touren);
    } on ApiFehler catch (e) {
      if (mounted) setState(() => _fehler = e.text);
    }
  }

  Future<void> _oeffnen(TourKurz tour) async {
    await Navigator.push(context, MaterialPageRoute(builder: (_) => TourSeite(tourId: tour.tourId)));
    // Status kann sich in M03 geaendert haben
    _laden();
  }

  @override
  Widget build(BuildContext context) {
    final touren = _touren;
    final titelStil = Theme.of(context).textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w700);

    return RefreshIndicator(
      onRefresh: _laden,
      child: ListView(
        // Pull-to-Refresh soll auch bei kurzer Liste funktionieren
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.all(16),
        children: [
          Text(Texte.t('touren_titel'), style: titelStil),
          const SizedBox(height: 12),
          // Tanken (M06) und Schaden (M07) kommen in Phase 5
          Row(
            children: [
              Expanded(
                child: OutlinedButton.icon(
                  icon: const Icon(Icons.local_gas_station_outlined),
                  label: Text(Texte.t('tanken')),
                  onPressed: () => meldung(context, Texte.t('kommt_phase5')),
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: OutlinedButton.icon(
                  icon: const Icon(Icons.car_crash_outlined),
                  label: Text(Texte.t('schaden')),
                  onPressed: () => meldung(context, Texte.t('kommt_phase5')),
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),
          if (_fehler != null)
            LadeFehler(text: '${Texte.t('touren_laden_fehlgeschlagen')} $_fehler', erneut: _laden)
          else if (touren == null)
            const Padding(padding: EdgeInsets.all(24), child: Center(child: CircularProgressIndicator()))
          else if (touren.isEmpty)
            Card(
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: Text(Texte.t('keine_touren'), style: const TextStyle(color: DisposioFarben.textLeise)),
              ),
            )
          else
            for (final tour in touren) _TourKarte(tour: tour, onTap: () => _oeffnen(tour)),
        ],
      ),
    );
  }
}

class _TourKarte extends StatelessWidget {
  const _TourKarte({required this.tour, required this.onTap});

  final TourKurz tour;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    const leise = TextStyle(color: DisposioFarben.textLeise);
    return Card(
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(tour.tournummer, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w700)),
                  ),
                  StatusBadge.tour(tour.status),
                ],
              ),
              const SizedBox(height: 4),
              Text('${tagUndZeit(tour.startzeit)} – ${uhrzeit(tour.endzeit)}', style: leise),
              const SizedBox(height: 8),
              Text('${tour.startort} → ${tour.zielort}', style: const TextStyle(fontWeight: FontWeight.w600)),
              const SizedBox(height: 4),
              Text(
                [
                  '${tour.anzahlStopps} ${Texte.t('stopps')}',
                  if (tour.naechsterStopp != null) '${Texte.t('naechster_stopp')}: ${tour.naechsterStopp}',
                ].join(' · '),
                style: leise,
              ),
            ],
          ),
        ),
      ),
    );
  }
}
