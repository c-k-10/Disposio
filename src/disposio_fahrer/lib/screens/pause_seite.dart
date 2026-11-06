import 'dart:async';

import 'package:flutter/material.dart';

import '../services/api_client.dart';
import '../services/gps_dienst.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';
import '../widgets/tour_bausteine.dart';
import 'chat_seite.dart';

/// Wireframe M10: Fahrt pausiert. GPS ist aus, bis „Fahrt fortsetzen“ gedrueckt wird.
/// Gibt true zurueck, wenn die Fahrt fortgesetzt wurde.
class PauseSeite extends StatefulWidget {
  const PauseSeite({super.key, required this.tour});

  final TourDetails tour;

  @override
  State<PauseSeite> createState() => _PauseSeiteState();
}

class _PauseSeiteState extends State<PauseSeite> {
  final _api = ApiClient();
  late final DateTime _seit = widget.tour.pauseSeit ?? DateTime.now();
  Timer? _uhr;
  bool _arbeitet = false;

  @override
  void initState() {
    super.initState();
    // Laufende Dauer jede Sekunde neu zeichnen
    _uhr = Timer.periodic(const Duration(seconds: 1), (_) => setState(() {}));
  }

  @override
  void dispose() {
    _uhr?.cancel();
    super.dispose();
  }

  Future<void> _fortsetzen() async {
    setState(() => _arbeitet = true);
    try {
      await _api.tourStatus(widget.tour.tourId, 'fortsetzen');
      final gps = await GpsDienst.starten(widget.tour.tourId);
      if (!mounted) return;
      if (!gps) meldung(context, Texte.t('gps_nicht_gestartet'));
      Navigator.pop(context, true);
    } on ApiFehler catch (e) {
      if (mounted) meldung(context, e.text);
    } finally {
      if (mounted) setState(() => _arbeitet = false);
    }
  }

  /// Phase 4: oeffnet den Chat dieser Tour (M08).
  void _kontaktieren() => dispositionKontaktieren(context, widget.tour.tourId);

  String _dauer() {
    final d = DateTime.now().difference(_seit);
    final h = d.inHours;
    final m = d.inMinutes.remainder(60).toString().padLeft(2, '0');
    final s = d.inSeconds.remainder(60).toString().padLeft(2, '0');
    return h > 0 ? '$h:$m:$s' : '$m:$s';
  }

  @override
  Widget build(BuildContext context) {
    const leise = TextStyle(color: DisposioFarben.textLeise);
    return Scaffold(
      appBar: AppBar(title: Text(widget.tour.tournummer)),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          StatusZeile(Texte.t('pause_gps_pausiert')),
          const SizedBox(height: 32),
          const Icon(Icons.pause_circle_outline, size: 120, color: DisposioFarben.dunkelblau),
          const SizedBox(height: 12),
          Text(Texte.t('fahrt_pausiert'),
              textAlign: TextAlign.center,
              style: Theme.of(context).textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w700)),
          const SizedBox(height: 8),
          Text('${Texte.t('pause_seit')} ${uhrzeit(_seit)}',
              textAlign: TextAlign.center, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w600)),
          Text(_dauer(), textAlign: TextAlign.center, style: const TextStyle(fontSize: 32, fontWeight: FontWeight.w700)),
          const SizedBox(height: 12),
          Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              const Icon(Icons.location_off_outlined, size: 18, color: DisposioFarben.textLeise),
              const SizedBox(width: 6),
              Text(Texte.t('uebertragung_unterbrochen'), style: leise),
            ],
          ),
          const SizedBox(height: 32),
          FilledButton.icon(
            style: FilledButton.styleFrom(
              minimumSize: const Size.fromHeight(64),
              textStyle: const TextStyle(fontSize: 18, fontWeight: FontWeight.w700),
            ),
            icon: const Icon(Icons.play_arrow),
            label: Text(Texte.t('fahrt_fortsetzen')),
            onPressed: _arbeitet ? null : _fortsetzen,
          ),
          const SizedBox(height: 12),
          OutlinedButton.icon(
            style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(52)),
            icon: const Icon(Icons.chat_bubble_outline),
            label: Text(Texte.t('disposition_kontaktieren')),
            onPressed: _kontaktieren,
          ),
        ],
      ),
    );
  }
}
