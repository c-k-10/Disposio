import 'package:flutter/material.dart';

import '../services/api_client.dart';
import '../services/chat_dienst.dart';
import '../services/gps_dienst.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';
import 'chat_seite.dart';
import 'profil_seite.dart';
import 'touren_seite.dart';

/// Grundgeruest mit der unteren Leiste aus den Wireframes:
/// Touren · Chat · Uebertragung · Profil.
/// Touren seit Phase 2 (M02), Chat seit Phase 4 (M08). Uebertragung kommt mit M11.
class StartSeite extends StatefulWidget {
  const StartSeite({super.key});

  static const tabChat = 1;

  /// Aktiver Tab. Andere Seiten koennen so wechseln, z. B. M10 „Disposition kontaktieren“ zum Chat.
  static final tab = ValueNotifier<int>(0);

  @override
  State<StartSeite> createState() => _StartSeiteState();
}

class _StartSeiteState extends State<StartSeite> {
  static const _tabUebertragung = 2;

  @override
  void initState() {
    super.initState();
    StartSeite.tab.value = 0;
    _gpsFortsetzen();
    ChatDienst.abfragen();
  }

  /// Nach einem Neustart der App: laeuft noch eine Tour, GPS wieder einschalten (ohne Rueckfrage).
  Future<void> _gpsFortsetzen() async {
    try {
      final touren = await ApiClient().touren();
      for (final t in touren) {
        if (t.status == TourDetails.unterwegs) {
          await GpsDienst.starten(t.tourId);
          return;
        }
      }
    } on ApiFehler {
      // Offline: beim Oeffnen der Tour wird es erneut versucht
    }
  }

  @override
  Widget build(BuildContext context) {
    return ValueListenableBuilder<int>(
      valueListenable: StartSeite.tab,
      builder: (context, tab, _) => _geruest(tab),
    );
  }

  Widget _geruest(int tab) {
    final seiten = [
      const TourenSeite(),
      const ChatListeSeite(),
      _Platzhalter(titel: Texte.t('uebertragung_titel'), hinweis: Texte.t('uebertragung_hinweis')),
      ProfilSeite(zuUebertragungen: () => StartSeite.tab.value = _tabUebertragung),
    ];

    return Scaffold(
      appBar: AppBar(title: const Text('DISPOSIO')),
      body: seiten[tab],
      bottomNavigationBar: NavigationBar(
        selectedIndex: tab,
        onDestinationSelected: (i) => StartSeite.tab.value = i,
        destinations: [
          NavigationDestination(icon: const Icon(Icons.route_outlined), label: Texte.t('tab_touren')),
          NavigationDestination(
            // Abzeichen mit der Zahl ungelesener Nachrichten der Disposition
            icon: ValueListenableBuilder<int>(
              valueListenable: ChatDienst.ungelesen,
              builder: (context, anzahl, _) => Badge(
                isLabelVisible: anzahl > 0,
                label: Text('$anzahl'),
                child: const Icon(Icons.chat_bubble_outline),
              ),
            ),
            label: Texte.t('tab_chat'),
          ),
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
