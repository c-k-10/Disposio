import 'package:flutter/material.dart';

import 'screens/start_seite.dart';
import 'theme/disposio_theme.dart';

void main() {
  runApp(const DisposioFahrerApp());
}

class DisposioFahrerApp extends StatelessWidget {
  const DisposioFahrerApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Disposio Fahrer',
      debugShowCheckedModeBanner: false,
      theme: DisposioTheme.hell,
      home: const StartSeite(),
    );
  }
}
