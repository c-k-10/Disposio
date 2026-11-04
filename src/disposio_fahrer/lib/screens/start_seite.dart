import 'package:flutter/material.dart';

import '../services/api_client.dart';
import '../theme/disposio_theme.dart';

/// Grundgeruest mit der unteren Leiste aus den Wireframes:
/// Touren · Chat · Uebertragung · Profil.
/// Die Inhalte kommen in den naechsten Phasen (M02, M08, M11, M09).
class StartSeite extends StatefulWidget {
  const StartSeite({super.key});

  @override
  State<StartSeite> createState() => _StartSeiteState();
}

class _StartSeiteState extends State<StartSeite> {
  int _tab = 0;

  @override
  Widget build(BuildContext context) {
    final seiten = [
      const _Platzhalter(titel: 'Meine Touren', hinweis: 'Kommt in Phase 2 (M02)'),
      const _Platzhalter(titel: 'Chat mit Disposition', hinweis: 'Kommt in Phase 4 (M08)'),
      const _Platzhalter(titel: 'Übertragungen', hinweis: 'Kommt mit M11'),
      const _VerbindungsTest(),
    ];

    return Scaffold(
      appBar: AppBar(title: const Text('DISPOSIO')),
      body: seiten[_tab],
      bottomNavigationBar: NavigationBar(
        selectedIndex: _tab,
        onDestinationSelected: (i) => setState(() => _tab = i),
        destinations: const [
          NavigationDestination(icon: Icon(Icons.route_outlined), label: 'Touren'),
          NavigationDestination(icon: Icon(Icons.chat_bubble_outline), label: 'Chat'),
          NavigationDestination(icon: Icon(Icons.sync), label: 'Übertragung'),
          NavigationDestination(icon: Icon(Icons.person_outline), label: 'Profil'),
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

/// Phase 0: Knopf, der Handy -> API -> Neon einmal komplett testet.
class _VerbindungsTest extends StatefulWidget {
  const _VerbindungsTest();

  @override
  State<_VerbindungsTest> createState() => _VerbindungsTestState();
}

class _VerbindungsTestState extends State<_VerbindungsTest> {
  final _api = ApiClient();
  String _ergebnis = 'Noch nicht getestet';
  bool _laeuft = false;

  Future<void> _testen() async {
    setState(() => _laeuft = true);
    final text = await _api.verbindungTesten();
    if (!mounted) return;
    setState(() {
      _ergebnis = text;
      _laeuft = false;
    });
  }

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Text('Profil', style: Theme.of(context).textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w700)),
        const SizedBox(height: 12),
        Container(
          padding: const EdgeInsets.all(12),
          decoration: const BoxDecoration(
            color: DisposioFarben.statusBox,
            border: Border(left: BorderSide(color: DisposioFarben.dunkelblau, width: 3)),
          ),
          child: Text(_ergebnis),
        ),
        const SizedBox(height: 12),
        Text('API: $apiUrl', style: const TextStyle(color: DisposioFarben.textLeise, fontSize: 12)),
        const SizedBox(height: 16),
        FilledButton(
          onPressed: _laeuft ? null : _testen,
          child: Text(_laeuft ? 'Teste …' : 'Verbindung testen'),
        ),
      ],
    );
  }
}
