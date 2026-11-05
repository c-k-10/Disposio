import 'package:flutter/material.dart';

import '../services/api_client.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';

/// Gemeinsame Bausteine fuer Touren und Stopps (M02 bis M05).

String _zwei(int n) => n.toString().padLeft(2, '0');

String uhrzeit(DateTime z) => '${_zwei(z.hour)}:${_zwei(z.minute)}';

String datum(DateTime z) => '${_zwei(z.day)}.${_zwei(z.month)}.${z.year}';

bool _gleicherTag(DateTime a, DateTime b) => a.year == b.year && a.month == b.month && a.day == b.day;

/// "Heute" oder Datum, danach Uhrzeit, z. B. "Heute 06:00".
String tagUndZeit(DateTime z) =>
    '${_gleicherTag(z, DateTime.now()) ? Texte.t('heute') : datum(z)} ${uhrzeit(z)}';

/// Zeitfenster, z. B. "08:00–10:00". Ueber Tage hinweg mit Datum.
String zeitfenster(DateTime von, DateTime bis) => _gleicherTag(von, bis)
    ? '${uhrzeit(von)}–${uhrzeit(bis)}'
    : '${datum(von)} ${uhrzeit(von)} – ${datum(bis)} ${uhrzeit(bis)}';

String tourStatusText(String status) => switch (status) {
      TourDetails.geplant => Texte.t('status_geplant'),
      TourDetails.zugewiesen => Texte.t('status_zugewiesen'),
      TourDetails.unterwegs => Texte.t('status_unterwegs'),
      TourDetails.pausiert => Texte.t('status_pausiert'),
      TourDetails.abgeschlossen => Texte.t('status_abgeschlossen'),
      _ => status,
    };

String stoppStatusText(String status) => switch (status) {
      Stopp.offen => Texte.t('stopp_offen'),
      Stopp.angekommen => Texte.t('stopp_angekommen'),
      Stopp.entladen => Texte.t('stopp_entladen'),
      Stopp.erledigt => Texte.t('stopp_erledigt'),
      _ => status,
    };

String stoppArtText(String art) => switch (art) {
      'Abholung' => Texte.t('art_abholung'),
      'Zustellung' => Texte.t('art_zustellung'),
      'Kombiniert' => Texte.t('art_kombiniert'),
      _ => art,
    };

/// Ware eines Stopps kurz, z. B. "6 Europalette · 2 Gitterbox".
String wareKurz(Stopp stopp) {
  final summe = <String, int>{};
  for (final a in stopp.auftraege) {
    summe[a.verpackungstyp] = (summe[a.verpackungstyp] ?? 0) + a.anzahl;
  }
  return [for (final e in summe.entries) '${e.value} ${e.key}'].join(' · ');
}

/// Kleines Etikett mit dem Status einer Tour oder eines Stopps.
class StatusBadge extends StatelessWidget {
  const StatusBadge({super.key, required this.text, required this.aktiv, this.erledigt = false});

  StatusBadge.tour(String status, {super.key})
      : text = tourStatusText(status),
        aktiv = status == TourDetails.unterwegs || status == TourDetails.pausiert,
        erledigt = status == TourDetails.abgeschlossen;

  StatusBadge.stopp(String status, {super.key})
      : text = stoppStatusText(status),
        aktiv = status == Stopp.angekommen || status == Stopp.entladen,
        erledigt = status == Stopp.erledigt;

  final String text;
  final bool aktiv;
  final bool erledigt;

  @override
  Widget build(BuildContext context) {
    final (hintergrund, schrift) = aktiv
        ? (DisposioFarben.dunkelblau, Colors.white)
        : erledigt
            ? (DisposioFarben.statusBox, DisposioFarben.textLeise)
            : (Colors.white, DisposioFarben.dunkelblau);
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: hintergrund,
        borderRadius: BorderRadius.circular(4),
        border: Border.all(color: aktiv ? DisposioFarben.dunkelblau : DisposioFarben.rand),
      ),
      child: Text(text, style: TextStyle(color: schrift, fontSize: 12, fontWeight: FontWeight.w600)),
    );
  }
}

/// Graue Statuszeile aus den Wireframes, z. B. "Bereit · GPS aus".
class StatusZeile extends StatelessWidget {
  const StatusZeile(this.text, {super.key});

  final String text;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(12),
      decoration: const BoxDecoration(
        color: DisposioFarben.statusBox,
        border: Border(left: BorderSide(color: DisposioFarben.dunkelblau, width: 3)),
      ),
      child: Text(text, style: const TextStyle(fontWeight: FontWeight.w600)),
    );
  }
}

/// Phase 3: voraussichtliche Ankunft, z. B. "ETA 14:35". Rot mit "verspätet", wenn nach dem Zeitfenster.
class EtaText extends StatelessWidget {
  const EtaText(this.stopp, {super.key, this.gross = false});

  final Stopp stopp;
  final bool gross;

  static const rot = Color(0xFFC62828);

  @override
  Widget build(BuildContext context) {
    final eta = stopp.aktuelleEta;
    if (eta == null) return const SizedBox.shrink();
    final zusatz = stopp.verspaetet ? ' · ${Texte.t('verspaetet')}' : '';
    return Text(
      '${Texte.t('eta')} ${uhrzeit(eta)}$zusatz',
      style: TextStyle(
        color: stopp.verspaetet ? rot : DisposioFarben.dunkelblau,
        fontWeight: FontWeight.w700,
        fontSize: gross ? 16 : null,
      ),
    );
  }
}

/// Fehlertext mit "Erneut versuchen".
class LadeFehler extends StatelessWidget {
  const LadeFehler({super.key, required this.text, required this.erneut});

  final String text;
  final VoidCallback erneut;

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(text),
        const SizedBox(height: 12),
        OutlinedButton(onPressed: erneut, child: Text(Texte.t('erneut_versuchen'))),
      ],
    );
  }
}

void meldung(BuildContext context, String text) {
  ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(text)));
}
