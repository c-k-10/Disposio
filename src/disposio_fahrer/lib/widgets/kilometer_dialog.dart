import 'package:flutter/material.dart';

import '../texte.dart';

/// #171–#173: Kilometerstand bei „Fahrt starten“ und „Tour beenden“ abfragen und prüfen.
/// Prüfung wie beim Tanken: kleiner als der letzte bekannte Stand oder ein sehr großer Sprung
/// nur nach Rückfrage; kleiner als bei Abfahrt geht gar nicht.

/// Größter erlaubter Sprung zum letzten bekannten Stand ohne Rückfrage.
const kmSprungWarnung = 3000;

/// „123.456“, „123 456“ oder „123456“ → 123456. null, wenn keine gültige Zahl (0 bis 5.000.000).
int? kmLesen(String text) {
  final ziffern = text.trim().replaceAll('.', '').replaceAll(' ', '');
  final km = int.tryParse(ziffern);
  if (km == null || km < 0 || km > 5000000) return null;
  return km;
}

/// 123456 → „123.456 km“
String kmText(int km) {
  final s = km.toString();
  final teile = <String>[];
  for (var ende = s.length; ende > 0; ende -= 3) {
    teile.insert(0, s.substring(ende - 3 < 0 ? 0 : ende - 3, ende));
  }
  return '${teile.join('.')} km';
}

/// Prüft [km] gegen [mindestens] (Abfahrt, harte Grenze) und [letzter] (letzter bekannter Stand, Rückfrage).
/// true = übernehmen.
Future<bool> kilometerstandBestaetigen(BuildContext context, int km, {int? letzter, int? mindestens}) async {
  if (mindestens != null && km < mindestens) {
    await showDialog<void>(
      context: context,
      builder: (context) => AlertDialog(
        content: Text('${Texte.t('km_kleiner_start')} (${kmText(mindestens)})'),
        actions: [FilledButton(onPressed: () => Navigator.pop(context), child: Text(Texte.t('ok')))],
      ),
    );
    return false;
  }
  String? warnung;
  if (letzter != null && km < letzter) {
    warnung = '${Texte.t('km_kleiner')} (${kmText(letzter)})';
  } else if (letzter != null && km - letzter > kmSprungWarnung) {
    warnung = '${Texte.t('km_sprung')} (${Texte.t('letzter_stand')}: ${kmText(letzter)})';
  }
  if (warnung == null) return true;
  final text = warnung;
  if (!context.mounted) return false;
  final trotzdem = await showDialog<bool>(
    context: context,
    builder: (context) => AlertDialog(
      content: Text(text),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context, false), child: Text(Texte.t('abbrechen'))),
        FilledButton(onPressed: () => Navigator.pop(context, true), child: Text(Texte.t('trotzdem_speichern'))),
      ],
    ),
  );
  return trotzdem == true;
}

/// Dialog vor „Fahrt starten“. Gibt den geprüften Kilometerstand zurück, null = abgebrochen.
Future<int?> kilometerstandAbfahrtAbfragen(BuildContext context, {int? letzter}) async {
  var vorschlag = letzter;
  while (context.mounted) {
    final km = await showDialog<int>(
      context: context,
      barrierDismissible: false,
      builder: (context) => _KilometerDialog(vorschlag: vorschlag, letzter: letzter),
    );
    if (km == null || !context.mounted) return null;
    if (await kilometerstandBestaetigen(context, km, letzter: letzter)) return km;
    vorschlag = km;
  }
  return null;
}

class _KilometerDialog extends StatefulWidget {
  const _KilometerDialog({required this.vorschlag, required this.letzter});

  final int? vorschlag;
  final int? letzter;

  @override
  State<_KilometerDialog> createState() => _KilometerDialogState();
}

class _KilometerDialogState extends State<_KilometerDialog> {
  late final _feld = TextEditingController(text: widget.vorschlag?.toString() ?? '');
  String? _fehler;

  @override
  void dispose() {
    _feld.dispose();
    super.dispose();
  }

  void _uebernehmen() {
    final km = kmLesen(_feld.text);
    if (km == null) {
      setState(() => _fehler = Texte.t('km_ungueltig'));
      return;
    }
    Navigator.pop(context, km);
  }

  @override
  Widget build(BuildContext context) {
    final letzter = widget.letzter;
    return AlertDialog(
      title: Text(Texte.t('km_abfahrt')),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(Texte.t('km_hinweis')),
          const SizedBox(height: 12),
          TextField(
            controller: _feld,
            autofocus: true,
            keyboardType: TextInputType.number,
            onSubmitted: (_) => _uebernehmen(),
            decoration: InputDecoration(
              labelText: Texte.t('kilometerstand'),
              suffixText: 'km',
              helperText: letzter == null ? null : '${Texte.t('letzter_stand')}: ${kmText(letzter)}',
              errorText: _fehler,
            ),
          ),
        ],
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context), child: Text(Texte.t('abbrechen'))),
        FilledButton(onPressed: _uebernehmen, child: Text(Texte.t('fahrt_starten'))),
      ],
    );
  }
}
