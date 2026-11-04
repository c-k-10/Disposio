import 'package:flutter/material.dart';

import 'screens/login_seite.dart';
import 'screens/start_seite.dart';
import 'services/sitzung.dart';
import 'texte.dart';
import 'theme/disposio_theme.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  // Gespeicherte Sprache und Token laden: mit Token direkt zur StartSeite, sonst Login
  final sprache = await Sitzung.lesen('sprache');
  if (sprache != null) Texte.setzen(sprache);
  await Sitzung.laden();
  runApp(const DisposioFahrerApp());
}

class DisposioFahrerApp extends StatelessWidget {
  const DisposioFahrerApp({super.key});

  @override
  Widget build(BuildContext context) {
    // Sprachwechsel baut die ganze App sofort neu auf
    return ValueListenableBuilder<String>(
      valueListenable: Texte.sprache,
      builder: (context, sprache, _) => MaterialApp(
        title: 'Disposio Fahrer',
        debugShowCheckedModeBanner: false,
        theme: DisposioTheme.hell,
        // Bei Abmelden (auch nach 401) wechselt die Anzeige automatisch zum Login
        home: ValueListenableBuilder<bool?>(
          valueListenable: Sitzung.angemeldet,
          builder: (context, angemeldet, _) => switch (angemeldet) {
            true => const StartSeite(),
            false => const LoginSeite(),
            null => const Scaffold(body: Center(child: CircularProgressIndicator())),
          },
        ),
      ),
    );
  }
}
