import 'package:flutter/material.dart';

import '../services/api_client.dart';
import '../services/chat_dienst.dart';
import '../services/vorlese_dienst.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';
import '../widgets/tour_bausteine.dart';

/// M03/M10 „Disposition kontaktieren“: oeffnet den Chat der Tour (legt ihn beim ersten Mal an).
Future<void> dispositionKontaktieren(BuildContext context, int tourId) async {
  try {
    final chat = await ChatDienst.tourChat(tourId);
    if (!context.mounted) return;
    await Navigator.push(context, MaterialPageRoute(builder: (_) => ChatVerlaufSeite(chat: chat)));
  } on ApiFehler catch (e) {
    if (context.mounted) meldung(context, e.text);
  }
}

String _zeitKurz(DateTime z) {
  final jetzt = DateTime.now();
  final heute = z.year == jetzt.year && z.month == jetzt.month && z.day == jetzt.day;
  return heute ? uhrzeit(z) : '${datum(z)} ${uhrzeit(z)}';
}

/// Wireframe M08: Liste der Chats mit Abzeichen fuer ungelesene Nachrichten.
class ChatListeSeite extends StatelessWidget {
  const ChatListeSeite({super.key});

  @override
  Widget build(BuildContext context) {
    final titelStil = Theme.of(context).textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w700);
    return RefreshIndicator(
      onRefresh: ChatDienst.abfragen,
      child: ValueListenableBuilder<List<ChatKurz>>(
        valueListenable: ChatDienst.chats,
        builder: (context, chats, _) => ListView(
          physics: const AlwaysScrollableScrollPhysics(),
          padding: const EdgeInsets.all(16),
          children: [
            Text(Texte.t('chat_titel'), style: titelStil),
            const SizedBox(height: 12),
            ValueListenableBuilder<String?>(
              valueListenable: ChatDienst.listenFehler,
              builder: (context, fehler, _) => fehler == null
                  ? const SizedBox.shrink()
                  : Padding(
                      padding: const EdgeInsets.only(bottom: 8),
                      child: Text('${Texte.t('chats_laden_fehlgeschlagen')} $fehler',
                          style: const TextStyle(color: DisposioFarben.textLeise)),
                    ),
            ),
            if (chats.isEmpty)
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Text(Texte.t('keine_chats'), style: const TextStyle(color: DisposioFarben.textLeise)),
                ),
              ),
            for (final chat in chats) _ChatEintrag(chat),
          ],
        ),
      ),
    );
  }
}

class _ChatEintrag extends StatelessWidget {
  const _ChatEintrag(this.chat);

  final ChatKurz chat;

  @override
  Widget build(BuildContext context) {
    final letzte = chat.letzterText == null ? '' : '${chat.letzteEigen ? '${Texte.t('du')}: ' : ''}${chat.letzterText}';
    final fett = chat.ungelesen > 0;
    return Card(
      child: ListTile(
        leading: const CircleAvatar(
          backgroundColor: DisposioFarben.statusBox,
          child: Icon(Icons.support_agent, color: DisposioFarben.dunkelblau),
        ),
        title: Text('${Texte.t('tour')} ${chat.tournummer}',
            style: TextStyle(fontWeight: fett ? FontWeight.w700 : FontWeight.w600)),
        subtitle: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(chat.disponent, style: const TextStyle(fontSize: 12, color: DisposioFarben.textLeise)),
            Text(letzte.isEmpty ? Texte.t('keine_nachrichten') : letzte,
                maxLines: 2, overflow: TextOverflow.ellipsis,
                style: TextStyle(fontWeight: fett ? FontWeight.w600 : FontWeight.normal)),
          ],
        ),
        trailing: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          crossAxisAlignment: CrossAxisAlignment.end,
          children: [
            if (chat.letzteZeit != null)
              Text(_zeitKurz(chat.letzteZeit!), style: const TextStyle(fontSize: 12, color: DisposioFarben.textLeise)),
            if (fett) ...[
              const SizedBox(height: 4),
              Badge(label: Text('${chat.ungelesen}')),
            ],
          ],
        ),
        onTap: () => Navigator.push(context, MaterialPageRoute(builder: (_) => ChatVerlaufSeite(chat: chat))),
      ),
    );
  }
}

/// Wireframe M08: Verlauf mit Sprechblasen, Schnellantworten und Eingabefeld.
class ChatVerlaufSeite extends StatefulWidget {
  const ChatVerlaufSeite({super.key, required this.chat});

  final ChatKurz chat;

  @override
  State<ChatVerlaufSeite> createState() => _ChatVerlaufSeiteState();
}

class _ChatVerlaufSeiteState extends State<ChatVerlaufSeite> {
  static const _schnellantworten = ['schnell_stau', 'schnell_gleich_da', 'schnell_kunde_nicht_da', 'schnell_verspaetung'];

  final _eingabe = TextEditingController();
  // Nachrichten, bei denen „Original anzeigen“ gedrueckt wurde
  final _originalSichtbar = <int>{};
  bool _laedt = true;

  int get _chatId => widget.chat.chatId;

  @override
  void initState() {
    super.initState();
    ChatDienst.sendeFehler.addListener(_sendeFehler);
    ChatDienst.oeffnen(_chatId).whenComplete(() {
      if (mounted) setState(() => _laedt = false);
    });
  }

  @override
  void dispose() {
    ChatDienst.sendeFehler.removeListener(_sendeFehler);
    ChatDienst.schliessen(_chatId);
    _eingabe.dispose();
    super.dispose();
  }

  void _sendeFehler() {
    final fehler = ChatDienst.sendeFehler.value;
    if (fehler != null && mounted) meldung(context, '${Texte.t('nachricht_nicht_gesendet')} $fehler');
  }

  Future<void> _senden(String text) async {
    final t = text.trim();
    if (t.isEmpty) return;
    _eingabe.clear();
    await ChatDienst.senden(_chatId, t);
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(widget.chat.disponent),
            Text('${Texte.t('tour')} ${widget.chat.tournummer}',
                style: const TextStyle(fontSize: 12, fontWeight: FontWeight.normal, color: DisposioFarben.textLeise)),
          ],
        ),
      ),
      body: ValueListenableBuilder<int>(
        valueListenable: ChatDienst.aenderung,
        builder: (context, _, _) {
          final nachrichten = ChatDienst.verlauf(_chatId) ?? const <ChatNachricht>[];
          final wartend = ChatDienst.wartend(_chatId);
          final schreibbar = ChatDienst.schreibbar(_chatId);
          // Neueste unten: Liste umgedreht aufbauen
          final blasen = <Widget>[
            for (final n in wartend.reversed) _Blase.wartend(n),
            for (final n in nachrichten.reversed)
              _Blase(
                nachricht: n,
                originalSichtbar: _originalSichtbar.contains(n.nachrichtId),
                umschalten: () => setState(() {
                  if (!_originalSichtbar.remove(n.nachrichtId)) _originalSichtbar.add(n.nachrichtId);
                }),
              ),
          ];
          return Column(
            children: [
              Expanded(
                child: blasen.isEmpty
                    ? Center(
                        child: _laedt
                            ? const CircularProgressIndicator()
                            : Text(Texte.t('keine_nachrichten'), style: const TextStyle(color: DisposioFarben.textLeise)),
                      )
                    : ListView(reverse: true, padding: const EdgeInsets.all(12), children: blasen),
              ),
              if (schreibbar) _eingabeBereich() else _geschlossen(),
            ],
          );
        },
      ),
    );
  }

  Widget _geschlossen() => Container(
        width: double.infinity,
        color: DisposioFarben.statusBox,
        padding: const EdgeInsets.all(16),
        child: SafeArea(
          top: false,
          child: Text(Texte.t('chat_geschlossen'), style: const TextStyle(color: DisposioFarben.textLeise)),
        ),
      );

  Widget _eingabeBereich() => Material(
        color: Colors.white,
        child: SafeArea(
          top: false,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              SizedBox(
                height: 48,
                child: ListView(
                  scrollDirection: Axis.horizontal,
                  padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
                  children: [
                    for (final s in _schnellantworten)
                      Padding(
                        padding: const EdgeInsets.only(right: 6),
                        child: ActionChip(label: Text(Texte.t(s)), onPressed: () => _senden(Texte.t(s))),
                      ),
                  ],
                ),
              ),
              Padding(
                padding: const EdgeInsets.fromLTRB(12, 0, 4, 8),
                child: Row(
                  children: [
                    Expanded(
                      child: TextField(
                        controller: _eingabe,
                        minLines: 1,
                        maxLines: 4,
                        maxLength: 2000,
                        textCapitalization: TextCapitalization.sentences,
                        decoration: InputDecoration(hintText: Texte.t('nachricht_eingeben'), counterText: ''),
                        onSubmitted: _senden,
                      ),
                    ),
                    IconButton(
                      tooltip: Texte.t('senden'),
                      icon: const Icon(Icons.send, color: DisposioFarben.dunkelblau),
                      onPressed: () => _senden(_eingabe.text),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      );
}

/// Sprechblase: eigene rechts dunkelblau, Disposition links weiss.
class _Blase extends StatelessWidget {
  const _Blase({required this.nachricht, required this.originalSichtbar, required this.umschalten})
      : wartendeNachricht = null;

  const _Blase.wartend(WartendeNachricht n)
      : wartendeNachricht = n,
        nachricht = null,
        originalSichtbar = false,
        umschalten = null;

  final ChatNachricht? nachricht;
  final WartendeNachricht? wartendeNachricht;
  final bool originalSichtbar;
  final VoidCallback? umschalten;

  @override
  Widget build(BuildContext context) {
    final n = nachricht;
    final eigen = n?.eigen ?? true;
    final text = n == null ? wartendeNachricht!.text : (originalSichtbar ? n.original : n.text);
    final zeit = n?.zeit ?? wartendeNachricht!.erstellt;
    final vordergrund = eigen ? Colors.white : DisposioFarben.textHaupt;
    final leise = TextStyle(fontSize: 11, color: eigen ? Colors.white70 : DisposioFarben.textLeise);

    final fuss = <Widget>[
      Text(uhrzeit(zeit), style: leise),
      if (eigen) ...[
        const SizedBox(width: 4),
        if (n == null)
          Tooltip(message: Texte.t('wartet_auf_netz'), child: const Icon(Icons.schedule, size: 14, color: Colors.white70))
        else
          Icon(n.gelesen ? Icons.done_all : Icons.done, size: 14, color: n.gelesen ? Colors.lightBlueAccent : Colors.white70),
      ],
    ];

    return Align(
      alignment: eigen ? Alignment.centerRight : Alignment.centerLeft,
      child: ConstrainedBox(
        constraints: BoxConstraints(maxWidth: MediaQuery.sizeOf(context).width * 0.8),
        child: Container(
          margin: const EdgeInsets.symmetric(vertical: 4),
          padding: const EdgeInsets.fromLTRB(12, 8, 12, 6),
          decoration: BoxDecoration(
            color: eigen ? DisposioFarben.dunkelblau : Colors.white,
            border: eigen ? null : Border.all(color: DisposioFarben.rand),
            borderRadius: BorderRadius.only(
              topLeft: const Radius.circular(12),
              topRight: const Radius.circular(12),
              bottomLeft: Radius.circular(eigen ? 12 : 2),
              bottomRight: Radius.circular(eigen ? 2 : 12),
            ),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(text, style: TextStyle(color: vordergrund, fontSize: 15)),
              const SizedBox(height: 4),
              Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  if (!eigen) ...[
                    // Lautsprecher: Nachricht vorlesen (das, was gerade angezeigt wird)
                    InkWell(
                      onTap: () => VorleseDienst.vorlesen(text),
                      child: Padding(
                        padding: const EdgeInsets.only(right: 8),
                        child: Icon(Icons.volume_up, size: 20, semanticLabel: Texte.t('vorlesen'),
                            color: DisposioFarben.dunkelblau),
                      ),
                    ),
                    if (n != null && n.uebersetzt)
                      InkWell(
                        onTap: umschalten,
                        child: Padding(
                          padding: const EdgeInsets.only(right: 8),
                          child: Text(Texte.t(originalSichtbar ? 'uebersetzung_anzeigen' : 'original_anzeigen'),
                              style: const TextStyle(
                                  fontSize: 12, color: DisposioFarben.dunkelblau, decoration: TextDecoration.underline)),
                        ),
                      ),
                  ],
                  ...fuss,
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}
