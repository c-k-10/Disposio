import 'package:flutter/material.dart';

import '../services/chat_dienst.dart';
import '../services/gps_dienst.dart';
import '../services/warteschlange.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';
import '../widgets/tour_bausteine.dart';

/// Wireframe M11: Uebertragungen. Alles, was noch nicht beim Server ist: Nachweise, Fotos, Belege,
/// Tankvorgaenge und Schaeden (Warteschlange), dazu wartende Chat-Nachrichten und GPS-Punkte.
class UebertragungSeite extends StatefulWidget {
  const UebertragungSeite({super.key});

  /// Zahl fuer das Abzeichen am Tab: Eintraege der Warteschlange und wartende Chat-Nachrichten.
  /// GPS-Punkte zaehlen nicht mit, die kommen waehrend der Fahrt laufend dazu.
  static int offen() => Warteschlange.eintraege.value.length + ChatDienst.wartendAnzahl;

  static Listenable get aenderungen => Listenable.merge([Warteschlange.eintraege, ChatDienst.aenderung]);

  @override
  State<UebertragungSeite> createState() => _UebertragungSeiteState();
}

class _UebertragungSeiteState extends State<UebertragungSeite> {
  bool _sendet = false;

  Future<void> _alleSenden() async {
    setState(() => _sendet = true);
    await Future.wait([Warteschlange.senden(), ChatDienst.pufferSenden(), GpsDienst.jetztSenden()]);
    if (!mounted) return;
    setState(() => _sendet = false);
    final rest = Warteschlange.eintraege.value.where((e) => e.status != Warteschlange.fehlerStatus).length;
    meldung(context, Texte.t(rest == 0 && ChatDienst.wartendAnzahl == 0 ? 'alles_uebertragen' : 'uebertragung_offen_allg'));
  }

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: Listenable.merge([UebertragungSeite.aenderungen, GpsDienst.ausstehend]),
      builder: (context, _) => RefreshIndicator(onRefresh: _alleSenden, child: _inhalt()),
    );
  }

  Widget _inhalt() {
    const leise = TextStyle(color: DisposioFarben.textLeise);
    final eintraege = Warteschlange.eintraege.value;
    final chat = ChatDienst.wartendAnzahl;
    final gps = GpsDienst.ausstehend.value;
    final nichts = eintraege.isEmpty && chat == 0 && gps == 0;

    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.all(16),
      children: [
        Text(Texte.t('uebertragung_titel'),
            style: Theme.of(context).textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w700)),
        const SizedBox(height: 4),
        Text(Texte.t('uebertragung_erklaerung'), style: leise),
        const SizedBox(height: 12),
        FilledButton.icon(
          icon: _sendet
              ? const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2))
              : const Icon(Icons.sync),
          label: Text(Texte.t('jetzt_senden')),
          onPressed: _sendet || nichts ? null : _alleSenden,
        ),
        const SizedBox(height: 12),
        if (nichts)
          Card(
            child: ListTile(
              leading: const Icon(Icons.check_circle_outline, color: DisposioFarben.dunkelblau),
              title: Text(Texte.t('alles_uebertragen')),
            ),
          ),
        for (final e in eintraege) _EintragKarte(eintrag: e),
        if (chat > 0)
          Card(
            child: ListTile(
              leading: const Icon(Icons.chat_bubble_outline),
              title: Text('${Texte.t('w_chat')}: $chat'),
              subtitle: Text(Texte.t('status_wartet')),
            ),
          ),
        if (gps > 0)
          Card(
            child: ListTile(
              leading: const Icon(Icons.location_on_outlined),
              title: Text('${Texte.t('w_gps')}: $gps'),
              subtitle: Text(Texte.t('status_wartet')),
            ),
          ),
      ],
    );
  }
}

class _EintragKarte extends StatelessWidget {
  const _EintragKarte({required this.eintrag});

  final WarteEintrag eintrag;

  /// Titel ist "schluessel|zusatz", z. B. "w_nachweis|A-NILS-21": beim Anzeigen uebersetzen.
  String get _titel {
    final teile = eintrag.titel.split('|');
    return teile.length > 1 ? '${Texte.t(teile.first)} ${teile.sublist(1).join('|')}' : Texte.t(teile.first);
  }

  IconData get _icon => switch (eintrag.art) {
        'nachweis' => Icons.assignment_turned_in_outlined,
        'tank' => Icons.local_gas_station_outlined,
        'schaden' => Icons.car_crash_outlined,
        _ => Icons.image_outlined,
      };

  @override
  Widget build(BuildContext context) {
    final fehler = eintrag.status == Warteschlange.fehlerStatus;
    final (statusText, farbe) = switch (eintrag.status) {
      Warteschlange.sendet => (Texte.t('status_sendet'), DisposioFarben.dunkelblau),
      Warteschlange.fehlerStatus => (Texte.t('status_fehler'), const Color(0xFFC62828)),
      _ => (Texte.t('status_wartet'), DisposioFarben.textLeise),
    };
    final grund = eintrag.fehler == null ? null : Texte.t(eintrag.fehler!);

    return Card(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 12, 8, 8),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(_icon, color: DisposioFarben.dunkelblau),
                const SizedBox(width: 12),
                Expanded(child: Text(_titel, style: const TextStyle(fontWeight: FontWeight.w600))),
                Text(statusText, style: TextStyle(color: farbe, fontWeight: FontWeight.w700, fontSize: 12)),
              ],
            ),
            Padding(
              padding: const EdgeInsets.only(left: 36, top: 2),
              child: Text(
                [tagUndZeit(eintrag.erstellt), ?grund].join(' · '),
                style: const TextStyle(color: DisposioFarben.textLeise, fontSize: 12),
              ),
            ),
            // Verwerfen z. B. bei Tippfehlern; Dokumente nur zusammen mit ihrem Eltern-Eintrag
            if (eintrag.status != Warteschlange.sendet && (fehler || eintrag.eltern == null))
              Row(
                mainAxisAlignment: MainAxisAlignment.end,
                children: [
                  TextButton(
                    onPressed: () => _verwerfen(context),
                    child: Text(Texte.t('verwerfen')),
                  ),
                  if (fehler)
                    TextButton(
                      onPressed: () => Warteschlange.erneut(eintrag.id),
                      child: Text(Texte.t('erneut_senden')),
                    ),
                ],
              ),
          ],
        ),
      ),
    );
  }

  Future<void> _verwerfen(BuildContext context) async {
    final ja = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        content: Text(Texte.t('verwerfen_frage')),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: Text(Texte.t('abbrechen'))),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: Text(Texte.t('verwerfen'))),
        ],
      ),
    );
    if (ja == true) await Warteschlange.verwerfen(eintrag.id);
  }
}
