import 'package:flutter/material.dart';
import 'package:permission_handler/permission_handler.dart';

import '../services/api_client.dart';
import '../services/sitzung.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';

/// Wireframe M09: Profil mit Fahrerdaten, Verfuegbarkeit, App-Sprache und Abmelden.
class ProfilSeite extends StatefulWidget {
  const ProfilSeite({super.key, required this.zuUebertragungen});

  /// Wechselt in den Tab Uebertragung (Knopf „Übertragungen ansehen“).
  final VoidCallback zuUebertragungen;

  @override
  State<ProfilSeite> createState() => _ProfilSeiteState();
}

class _ProfilSeiteState extends State<ProfilSeite> {
  // Werte der Spalte fahrer.verfuegbarkeit
  static const _verfuegbar = 'Verfügbar';
  static const _nichtVerfuegbar = 'Nicht verfügbar';

  final _api = ApiClient();
  Profil? _profil;
  String? _fehler;
  String? _verfuegbarkeit;
  bool _speichert = false;
  late String _gewaehlteSprache = Texte.sprache.value;

  @override
  void initState() {
    super.initState();
    _laden();
  }

  Future<void> _laden() async {
    setState(() => _fehler = null);
    try {
      final profil = await _api.me();
      if (!mounted) return;
      setState(() {
        _profil = profil;
        _verfuegbarkeit = profil.verfuegbarkeit;
      });
    } on ApiFehler catch (e) {
      if (mounted) setState(() => _fehler = e.text);
    }
  }

  Future<void> _verfuegbarkeitAendern(bool verfuegbar) async {
    final neu = verfuegbar ? _verfuegbar : _nichtVerfuegbar;
    final alt = _verfuegbarkeit;
    setState(() {
      _verfuegbarkeit = neu;
      _speichert = true;
    });
    try {
      await _api.verfuegbarkeitSetzen(neu);
    } on ApiFehler catch (e) {
      // Zuruecksetzen, damit der Schalter nicht etwas Falsches zeigt
      if (mounted) setState(() => _verfuegbarkeit = alt);
      _meldung(e.text);
    } finally {
      if (mounted) setState(() => _speichert = false);
    }
  }

  Future<void> _spracheUebernehmen() async {
    setState(() => _speichert = true);
    try {
      await _api.spracheSetzen(_gewaehlteSprache);
      await Sitzung.spracheMerken(_gewaehlteSprache);
      Texte.setzen(_gewaehlteSprache);
      _meldung(Texte.t('sprache_gespeichert'));
    } on ApiFehler catch (e) {
      _meldung(e.text);
    } finally {
      if (mounted) setState(() => _speichert = false);
    }
  }

  Future<void> _abmelden() async {
    final ja = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(Texte.t('abmelden')),
        content: Text(Texte.t('abmelden_frage')),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: Text(Texte.t('abbrechen'))),
          FilledButton(
            style: FilledButton.styleFrom(minimumSize: const Size(0, 44)),
            onPressed: () => Navigator.pop(context, true),
            child: Text(Texte.t('abmelden')),
          ),
        ],
      ),
    );
    if (ja == true) await Sitzung.abmelden();
  }

  void _meldung(String text) {
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(text)));
  }

  String _verfuegbarkeitText(String? wert) => switch (wert) {
        _verfuegbar => Texte.t('verfuegbar'),
        _nichtVerfuegbar => Texte.t('nicht_verfuegbar'),
        'Urlaub' => Texte.t('urlaub'),
        _ => wert ?? '',
      };

  @override
  Widget build(BuildContext context) {
    final profil = _profil;
    final titelStil = Theme.of(context).textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w700);

    if (profil == null) {
      return ListView(
        padding: const EdgeInsets.all(16),
        children: [
          Text(Texte.t('profil'), style: titelStil),
          const SizedBox(height: 24),
          if (_fehler == null)
            const Center(child: CircularProgressIndicator())
          else ...[
            Text('${Texte.t('laden_fehlgeschlagen')} $_fehler'),
            const SizedBox(height: 12),
            OutlinedButton(onPressed: _laden, child: Text(Texte.t('erneut_versuchen'))),
          ],
          const SizedBox(height: 24),
          const _VerbindungsTest(),
        ],
      );
    }

    return RefreshIndicator(
      onRefresh: _laden,
      child: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          Text(Texte.t('profil'), style: titelStil),
          const SizedBox(height: 12),
          Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('${profil.vorname} ${profil.nachname}',
                      style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w700)),
                  const SizedBox(height: 8),
                  _Zeile(Texte.t('firma'), profil.firmenname),
                  _Zeile('E-Mail', profil.email),
                  _Zeile(Texte.t('fuehrerscheinklasse'), profil.fuehrerscheinklasse),
                ],
              ),
            ),
          ),
          Card(
            child: SwitchListTile(
              title: Text(Texte.t('verfuegbarkeit')),
              subtitle: Text(_verfuegbarkeitText(_verfuegbarkeit)),
              value: _verfuegbarkeit == _verfuegbar,
              activeTrackColor: DisposioFarben.dunkelblau,
              onChanged: _speichert ? null : _verfuegbarkeitAendern,
            ),
          ),
          Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  DropdownButtonFormField<String>(
                    initialValue: _gewaehlteSprache,
                    decoration: InputDecoration(labelText: Texte.t('app_sprache')),
                    items: [
                      for (final eintrag in Texte.sprachen.entries)
                        DropdownMenuItem(value: eintrag.key, child: Text(eintrag.value)),
                    ],
                    onChanged: (code) => setState(() => _gewaehlteSprache = code ?? _gewaehlteSprache),
                  ),
                  const SizedBox(height: 12),
                  OutlinedButton(
                    onPressed: _speichert ? null : _spracheUebernehmen,
                    child: Text(Texte.t('sprache_uebernehmen')),
                  ),
                ],
              ),
            ),
          ),
          const SizedBox(height: 6),
          OutlinedButton.icon(
            icon: const Icon(Icons.security),
            label: Text(Texte.t('berechtigungen')),
            // Phase 3: oeffnet die App-Einstellungen von Android (Standort, Benachrichtigungen)
            onPressed: () async {
              if (!await openAppSettings()) _meldung(Texte.t('berechtigungen_fehler'));
            },
          ),
          const SizedBox(height: 8),
          OutlinedButton.icon(
            icon: const Icon(Icons.sync),
            label: Text(Texte.t('uebertragungen_ansehen')),
            onPressed: widget.zuUebertragungen,
          ),
          const SizedBox(height: 8),
          FilledButton.icon(
            icon: const Icon(Icons.logout),
            label: Text(Texte.t('abmelden')),
            onPressed: _abmelden,
          ),
          const SizedBox(height: 24),
          const _VerbindungsTest(),
        ],
      ),
    );
  }
}

class _Zeile extends StatelessWidget {
  const _Zeile(this.bezeichnung, this.wert);

  final String bezeichnung;
  final String wert;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 2),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 150,
            child: Text(bezeichnung, style: const TextStyle(color: DisposioFarben.textLeise)),
          ),
          Expanded(child: Text(wert)),
        ],
      ),
    );
  }
}

/// Phase 0: kleiner Verbindungstest Handy -> API -> Neon, unten im Profil.
class _VerbindungsTest extends StatefulWidget {
  const _VerbindungsTest();

  @override
  State<_VerbindungsTest> createState() => _VerbindungsTestState();
}

class _VerbindungsTestState extends State<_VerbindungsTest> {
  final _api = ApiClient();
  String? _ergebnis;
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
    const leise = TextStyle(color: DisposioFarben.textLeise, fontSize: 12);
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: const BoxDecoration(
        color: DisposioFarben.statusBox,
        border: Border(left: BorderSide(color: DisposioFarben.dunkelblau, width: 3)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('${Texte.t('verbindung')}: ${_ergebnis ?? Texte.t('nicht_getestet')}', style: leise),
          Text('API: $apiUrl', style: leise),
          Align(
            alignment: Alignment.centerLeft,
            child: TextButton(
              onPressed: _laeuft ? null : _testen,
              child: Text(_laeuft ? Texte.t('teste') : Texte.t('verbindung_testen')),
            ),
          ),
        ],
      ),
    );
  }
}
