import 'package:flutter/material.dart';

import '../services/api_client.dart';
import '../services/chat_dienst.dart';
import '../services/gps_dienst.dart';
import '../services/warteschlange.dart';
import '../texte.dart';
import 'chat_seite.dart';
import 'profil_seite.dart';
import 'touren_seite.dart';
import 'uebertragung_seite.dart';

/// Grundgeruest mit der unteren Leiste aus den Wireframes:
/// Touren · Chat · Uebertragung · Profil.
/// Touren seit Phase 2 (M02), Chat seit Phase 4 (M08), Uebertragung seit Phase 5 (M11).
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
    Warteschlange.senden();
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
      const UebertragungSeite(),
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
          NavigationDestination(
            // Phase 5: Abzeichen mit der Zahl noch nicht gesendeter Eintraege (M11)
            icon: ListenableBuilder(
              listenable: UebertragungSeite.aenderungen,
              builder: (context, _) {
                final anzahl = UebertragungSeite.offen();
                return Badge(isLabelVisible: anzahl > 0, label: Text('$anzahl'), child: const Icon(Icons.sync));
              },
            ),
            label: Texte.t('tab_uebertragung'),
          ),
          NavigationDestination(icon: const Icon(Icons.person_outline), label: Texte.t('tab_profil')),
        ],
      ),
    );
  }
}

