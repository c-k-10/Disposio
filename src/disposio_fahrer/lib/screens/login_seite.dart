import 'package:flutter/material.dart';

import '../services/api_client.dart';
import '../services/sitzung.dart';
import '../texte.dart';
import '../theme/disposio_theme.dart';

/// Wireframe M01: Anmeldung mit Unternehmenskennung, E-Mail, Passwort und Sprache.
/// Nach Erfolg wechselt main.dart automatisch zur StartSeite.
class LoginSeite extends StatefulWidget {
  const LoginSeite({super.key});

  @override
  State<LoginSeite> createState() => _LoginSeiteState();
}

class _LoginSeiteState extends State<LoginSeite> {
  final _api = ApiClient();
  final _kennung = TextEditingController();
  final _email = TextEditingController();
  final _passwort = TextEditingController();
  bool _passwortSichtbar = false;
  bool _laeuft = false;
  String? _fehler;

  @override
  void initState() {
    super.initState();
    _letzteEingabenLaden();
  }

  Future<void> _letzteEingabenLaden() async {
    final kennung = await Sitzung.lesen('kennung');
    final email = await Sitzung.lesen('email');
    final sprache = await Sitzung.lesen('sprache');
    if (!mounted) return;
    _kennung.text = kennung ?? '';
    _email.text = email ?? '';
    if (sprache != null) Texte.setzen(sprache);
  }

  @override
  void dispose() {
    _kennung.dispose();
    _email.dispose();
    _passwort.dispose();
    super.dispose();
  }

  Future<void> _anmelden() async {
    final kennung = _kennung.text.trim();
    final email = _email.text.trim();
    final passwort = _passwort.text;
    final sprache = Texte.sprache.value;

    if (kennung.isEmpty || email.isEmpty || passwort.isEmpty) {
      setState(() => _fehler = Texte.t('pflichtfelder'));
      return;
    }

    setState(() {
      _laeuft = true;
      _fehler = null;
    });
    try {
      await Sitzung.letzteEingabenMerken(kennung, email, sprache);
      await _api.login(kennung, email, passwort, sprache);
      // Erfolg: Sitzung.angemeldet ist jetzt true, main.dart zeigt die StartSeite.
    } on ApiFehler catch (e) {
      if (mounted) setState(() => _fehler = e.text);
    } catch (_) {
      if (mounted) setState(() => _fehler = Texte.t('unbekannter_fehler'));
    } finally {
      if (mounted) setState(() => _laeuft = false);
    }
  }

  void _passwortVergessen() {
    showDialog<void>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(Texte.t('passwort_vergessen')),
        content: Text(Texte.t('passwort_vergessen_text')),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context), child: Text(Texte.t('ok'))),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: SafeArea(
        child: Center(
          child: ListView(
            shrinkWrap: true,
            padding: const EdgeInsets.all(24),
            children: [
              const Text(
                'DISPOSIO',
                textAlign: TextAlign.center,
                style: TextStyle(
                  fontSize: 28,
                  fontWeight: FontWeight.w800,
                  letterSpacing: 2,
                  color: DisposioFarben.dunkelblau,
                ),
              ),
              const SizedBox(height: 32),
              TextField(
                controller: _kennung,
                decoration: InputDecoration(labelText: Texte.t('kennung')),
                textInputAction: TextInputAction.next,
                autocorrect: false,
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _email,
                decoration: InputDecoration(labelText: Texte.t('email')),
                keyboardType: TextInputType.emailAddress,
                textInputAction: TextInputAction.next,
                autocorrect: false,
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _passwort,
                obscureText: !_passwortSichtbar,
                decoration: InputDecoration(
                  labelText: Texte.t('passwort'),
                  suffixIcon: IconButton(
                    icon: Icon(_passwortSichtbar ? Icons.visibility_off : Icons.visibility),
                    onPressed: () => setState(() => _passwortSichtbar = !_passwortSichtbar),
                  ),
                ),
                onSubmitted: (_) => _laeuft ? null : _anmelden(),
              ),
              const SizedBox(height: 12),
              DropdownButtonFormField<String>(
                // Key: zeigt auch eine nachgeladene gespeicherte Sprache richtig an
                key: ValueKey(Texte.sprache.value),
                initialValue: Texte.sprache.value,
                decoration: InputDecoration(labelText: Texte.t('sprache')),
                items: [
                  for (final eintrag in Texte.sprachen.entries)
                    DropdownMenuItem(value: eintrag.key, child: Text(eintrag.value)),
                ],
                // Sprache wirkt sofort, auch auf diese Seite
                onChanged: (code) => code == null ? null : Texte.setzen(code),
              ),
              const SizedBox(height: 24),
              FilledButton(
                onPressed: _laeuft ? null : _anmelden,
                child: _laeuft
                    ? const SizedBox(
                        width: 22,
                        height: 22,
                        child: CircularProgressIndicator(strokeWidth: 2.5, color: Colors.white),
                      )
                    : Text(Texte.t('anmelden')),
              ),
              if (_fehler != null) ...[
                const SizedBox(height: 12),
                Text(_fehler!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
              ],
              const SizedBox(height: 8),
              TextButton(onPressed: _passwortVergessen, child: Text(Texte.t('passwort_vergessen'))),
            ],
          ),
        ),
      ),
    );
  }
}
