import 'dart:io';

import 'package:flutter/material.dart';
import 'package:image_picker/image_picker.dart';

import '../services/warteschlange.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';
import 'tour_bausteine.dart';

/// Phase 5: Fotos aufnehmen (M06, M07, M14, M15). Bilder werden beim Aufnehmen verkleinert
/// (laengste Seite 1600 px, JPEG 70 %) und in den App-Ordner der Warteschlange kopiert.
/// [pfade] gehoert der Seite; geaendert wird nur ueber [geaendert].
class FotoAuswahl extends StatelessWidget {
  const FotoAuswahl({
    super.key,
    required this.titel,
    required this.pfade,
    required this.geaendert,
    this.max = 6,
    this.aktiv = true,
  });

  final String titel;
  final List<String> pfade;
  final ValueChanged<List<String>> geaendert;
  final int max;
  final bool aktiv;

  static const _maxSeite = 1600.0;
  static const _qualitaet = 70;

  Future<void> _aufnehmen(BuildContext context) async {
    final quelle = await showModalBottomSheet<ImageSource>(
      context: context,
      builder: (context) => SafeArea(
        child: Wrap(
          children: [
            ListTile(
              leading: const Icon(Icons.photo_camera_outlined),
              title: Text(Texte.t('kamera')),
              onTap: () => Navigator.pop(context, ImageSource.camera),
            ),
            ListTile(
              leading: const Icon(Icons.photo_library_outlined),
              title: Text(Texte.t('galerie')),
              onTap: () => Navigator.pop(context, ImageSource.gallery),
            ),
          ],
        ),
      ),
    );
    if (quelle == null) return;

    try {
      final bild = await ImagePicker().pickImage(
        source: quelle,
        maxWidth: _maxSeite,
        maxHeight: _maxSeite,
        imageQuality: _qualitaet,
      );
      if (bild == null) return;
      final pfad = await Warteschlange.bildSichern(await bild.readAsBytes());
      // Kamera-Cache aufraeumen, die Kopie liegt im App-Ordner
      try {
        await File(bild.path).delete();
      } catch (_) {
        // Galerie-Bilder duerfen nicht geloescht werden
      }
      geaendert([...pfade, pfad]);
    } catch (_) {
      if (context.mounted) meldung(context, Texte.t('kamera_fehler'));
    }
  }

  Future<void> _entfernen(String pfad) async {
    geaendert([for (final p in pfade) if (p != pfad) p]);
    try {
      await File(pfad).delete();
    } catch (_) {
      // Schon weg
    }
  }

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(titel, style: const TextStyle(fontWeight: FontWeight.w700)),
        const SizedBox(height: 6),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            for (final pfad in pfade)
              Stack(
                children: [
                  ClipRRect(
                    borderRadius: BorderRadius.circular(6),
                    child: Image.file(File(pfad), width: 88, height: 88, fit: BoxFit.cover, cacheWidth: 176),
                  ),
                  if (aktiv)
                    Positioned(
                      right: 0,
                      top: 0,
                      child: InkWell(
                        onTap: () => _entfernen(pfad),
                        child: Container(
                          color: Colors.black54,
                          padding: const EdgeInsets.all(2),
                          child: const Icon(Icons.close, size: 18, color: Colors.white),
                        ),
                      ),
                    ),
                ],
              ),
            if (pfade.length < max)
              SizedBox(
                width: 88,
                height: 88,
                child: OutlinedButton(
                  style: OutlinedButton.styleFrom(
                    padding: EdgeInsets.zero,
                    minimumSize: const Size(88, 88),
                    backgroundColor: Colors.white,
                  ),
                  onPressed: aktiv ? () => _aufnehmen(context) : null,
                  child: const Icon(Icons.add_a_photo_outlined, color: DisposioFarben.dunkelblau),
                ),
              ),
          ],
        ),
      ],
    );
  }
}
