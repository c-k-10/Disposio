import 'package:flutter/material.dart';

import '../texte.dart';
import '../theme/disposio_theme.dart';
import 'profil_seite.dart';

/// Grundgeruest mit der unteren Leiste aus den Wireframes:
/// Touren · Chat · Uebertragung · Profil.
/// Touren, Chat und Uebertragung kommen in den naechsten Phasen (M02, M08, M11).
class StartSeite extends StatefulWidget {
  const StartSeite({super.key});

  @override
  State<StartSeite> createState() => _StartSeiteState();
}

class _StartSeiteState extends State<StartSeite> {
  static const _tabUebertragung = 2;

  int _tab = 0;

  @override
  Widget build(BuildContext context) {
    final seiten = [
      _Platzhalter(titel: Texte.t('touren_titel'), hinweis: Texte.t('touren_hinweis')),
      _Platzhalter(titel: Texte.t('chat_titel'), hinweis: Texte.t('chat_hinweis')),
      _Platzhalter(titel: Texte.t('uebertragung_titel'), hinweis: Texte.t('uebertragung_hinweis')),
      ProfilSeite(zuUebertragungen: () => setState(() => _tab = _tabUebertragung)),
    ];

    return Scaffold(
      appBar: AppBar(title: const Text('DISPOSIO')),
      body: seiten[_tab],
      bottomNavigationBar: NavigationBar(
        selectedIndex: _tab,
        onDestinationSelected: (i) => setState(() => _tab = i),
        destinations: [
          NavigationDestination(icon: const Icon(Icons.route_outlined), label: Texte.t('tab_touren')),
          NavigationDestination(icon: const Icon(Icons.chat_bubble_outline), label: Texte.t('tab_chat')),
          NavigationDestination(icon: const Icon(Icons.sync), label: Texte.t('tab_uebertragung')),
          NavigationDestination(icon: const Icon(Icons.person_outline), label: Texte.t('tab_profil')),
        ],
      ),
    );
  }
}

class _Platzhalter extends StatelessWidget {
  const _Platzhalter({required this.titel, required this.hinweis});

  final String titel;
  final String hinweis;

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Text(titel, style: Theme.of(context).textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w700)),
        const SizedBox(height: 12),
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Text(hinweis, style: const TextStyle(color: DisposioFarben.textLeise)),
          ),
        ),
      ],
    );
  }
}
