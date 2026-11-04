import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:http/http.dart' as http;

import '../texte.dart';
import 'sitzung.dart';

/// Adresse der Disposio.Api.
/// Beim Starten mitgeben, z. B.:
///   flutter run --dart-define=API_URL=https://disposio.onrender.com
/// Ohne Angabe wird die lokale API genommen (10.0.2.2 = der PC aus Sicht des Emulators).
const apiUrl = String.fromEnvironment(
  'API_URL',
  defaultValue: 'http://10.0.2.2:5080',
);

/// Fehler der API. [code] ist ein Schluessel aus texte.dart, z. B. 'anmeldung_fehlgeschlagen'.
/// [daten] enthaelt die ganze Fehlerantwort, z. B. die offenen Stopps bei 'offene_stopps'.
class ApiFehler implements Exception {
  ApiFehler(this.code, [this.daten = const {}]);

  final String code;
  final Map<String, dynamic> daten;

  String get text => Texte.t(code);

  @override
  String toString() => code;
}

class LoginErgebnis {
  LoginErgebnis(this.json)
      : vorname = json['vorname'] as String,
        nachname = json['nachname'] as String,
        sprache = json['sprache'] as String;

  final Map<String, dynamic> json;
  final String vorname;
  final String nachname;
  final String sprache;
}

class Profil {
  Profil.ausJson(Map<String, dynamic> json)
      : vorname = json['vorname'] as String,
        nachname = json['nachname'] as String,
        email = json['email'] as String,
        firmenname = json['firmenname'] as String,
        fuehrerscheinklasse = json['fuehrerscheinklasse'] as String,
        verfuegbarkeit = json['verfuegbarkeit'] as String,
        sprache = json['sprache'] as String;

  final String vorname;
  final String nachname;
  final String email;
  final String firmenname;
  final String fuehrerscheinklasse;
  final String verfuegbarkeit;
  final String sprache;
}

/// M02: Eintrag in „Meine Touren“.
class TourKurz {
  TourKurz.ausJson(Map<String, dynamic> json)
      : tourId = json['tourId'] as int,
        tournummer = json['tournummer'] as String,
        startzeit = _zeit(json['startzeit'])!,
        endzeit = _zeit(json['endzeit'])!,
        startort = json['startort'] as String,
        zielort = json['zielort'] as String,
        status = json['status'] as String,
        anzahlStopps = json['anzahlStopps'] as int,
        naechsterStopp = json['naechsterStopp'] as String?;

  final int tourId;
  final String tournummer;
  final DateTime startzeit;
  final DateTime endzeit;
  final String startort;
  final String zielort;
  final String status;
  final int anzahlStopps;
  final String? naechsterStopp;
}

/// M03/M04: Tour mit Fahrzeug und Stopps.
class TourDetails {
  // Werte von tour.status
  static const geplant = 'Geplant';
  static const zugewiesen = 'Zugewiesen';
  static const unterwegs = 'Unterwegs';
  static const pausiert = 'Pausiert';
  static const abgeschlossen = 'Abgeschlossen';

  TourDetails.ausJson(Map<String, dynamic> json)
      : tourId = json['tourId'] as int,
        tournummer = json['tour']['tournummer'] as String,
        startzeit = _zeit(json['tour']['startzeit'])!,
        endzeit = _zeit(json['tour']['endzeit'])!,
        startort = json['tour']['startort'] as String,
        zielort = json['tour']['zielort'] as String,
        status = json['tour']['status'] as String,
        kennzeichen = json['tour']['fahrzeug']?['kennzeichen'] as String?,
        fahrzeugtyp = json['tour']['fahrzeug']?['typ'] as String?,
        stopps = [
          for (final s in json['stopps'] as List) Stopp.ausJson(s as Map<String, dynamic>),
        ];

  final int tourId;
  final String tournummer;
  final DateTime startzeit;
  final DateTime endzeit;
  final String startort;
  final String zielort;
  final String status;
  final String? kennzeichen;
  final String? fahrzeugtyp;
  final List<Stopp> stopps;

  bool get bereit => status == geplant || status == zugewiesen;

  /// Erster Stopp, der noch nicht erledigt ist.
  Stopp? get naechsterStopp {
    for (final s in stopps) {
      if (s.status != Stopp.erledigt) return s;
    }
    return null;
  }

  List<Stopp> get offeneStopps => [for (final s in stopps) if (s.status != Stopp.erledigt) s];
}

class Stopp {
  // Werte von tourstopp.status
  static const offen = 'Offen';
  static const angekommen = 'Angekommen';
  static const entladen = 'Entladen';
  static const erledigt = 'Erledigt';

  Stopp.ausJson(Map<String, dynamic> json)
      : stoppId = json['stoppId'] as int,
        reihenfolge = json['reihenfolge'] as int,
        art = json['art'] as String,
        bezeichnung = json['bezeichnung'] as String,
        adresse = json['adresse'] as String,
        breitengrad = (json['breitengrad'] as num?)?.toDouble(),
        laengengrad = (json['laengengrad'] as num?)?.toDouble(),
        zeitfensterVon = _zeit(json['zeitfensterVon'])!,
        zeitfensterBis = _zeit(json['zeitfensterBis'])!,
        geplanteAnkunft = _zeit(json['geplanteAnkunft']),
        istAnkunft = _zeit(json['istAnkunft']),
        istAbfahrt = _zeit(json['istAbfahrt']),
        status = json['status'] as String,
        auftraege = [
          for (final a in json['auftraege'] as List) Auftrag.ausJson(a as Map<String, dynamic>),
        ];

  final int stoppId;
  final int reihenfolge;
  final String art;
  final String bezeichnung;
  final String adresse;
  final double? breitengrad;
  final double? laengengrad;
  final DateTime zeitfensterVon;
  final DateTime zeitfensterBis;
  final DateTime? geplanteAnkunft;
  final DateTime? istAnkunft;
  final DateTime? istAbfahrt;
  final String status;
  final List<Auftrag> auftraege;
}

class Auftrag {
  Auftrag.ausJson(Map<String, dynamic> json)
      : vorgangArt = json['vorgangArt'] as String,
        auftragsnummer = json['auftragsnummer'] as String,
        kunde = json['kunde'] as String,
        anzahl = json['anzahl'] as int,
        verpackungstyp = json['verpackungstyp'] as String,
        gewichtKg = (json['gewichtKg'] as num).toDouble(),
        gutart = json['gutart'] as String,
        gefahrgut = json['gefahrgut'] as bool,
        temperaturMin = (json['temperaturMin'] as num?)?.toDouble(),
        temperaturMax = (json['temperaturMax'] as num?)?.toDouble(),
        ansprechpartner = json['ansprechpartner'] as String,
        ladeEntladeequipment = json['ladeEntladeequipment'] as String,
        avisierungErforderlich = json['avisierungErforderlich'] as bool,
        kundenreferenz = json['kundenreferenz'] as String?;

  final String vorgangArt;
  final String auftragsnummer;
  final String kunde;
  final int anzahl;
  final String verpackungstyp;
  final double gewichtKg;
  final String gutart;
  final bool gefahrgut;
  final double? temperaturMin;
  final double? temperaturMax;
  final String ansprechpartner;
  final String ladeEntladeequipment;
  final bool avisierungErforderlich;
  final String? kundenreferenz;
}

/// Zeiten kommen als UTC von der API und werden in Ortszeit angezeigt.
DateTime? _zeit(Object? wert) => wert == null ? null : DateTime.parse(wert as String).toLocal();

class ApiClient {
  ApiClient({http.Client? client}) : _client = client ?? http.Client();

  final http.Client _client;

  // Render braucht beim Aufwachen bis zu ~1 Minute
  static const _zeitlimit = Duration(seconds: 60);

  /// Phase 1 (M01): Anmeldung. Speichert das Token bei Erfolg.
  Future<LoginErgebnis> login(String kennung, String email, String passwort, String sprache) async {
    final antwort = await _senden(() => _client.post(
          Uri.parse('$apiUrl/auth/login'),
          headers: _kopf(mitToken: false),
          body: jsonEncode({'kennung': kennung, 'email': email, 'passwort': passwort, 'sprache': sprache}),
        ));
    final daten = _auswerten(antwort, abmeldenBei401: false) as Map<String, dynamic>;
    await Sitzung.anmelden(daten['token'] as String);
    return LoginErgebnis(daten);
  }

  /// Phase 1 (M09): Profil des angemeldeten Fahrers.
  Future<Profil> me() async {
    final antwort = await _senden(() => _client.get(Uri.parse('$apiUrl/me'), headers: _kopf()));
    return Profil.ausJson(_auswerten(antwort) as Map<String, dynamic>);
  }

  Future<void> spracheSetzen(String sprache) async {
    final antwort = await _senden(() => _client.put(Uri.parse('$apiUrl/me/sprache'),
        headers: _kopf(), body: jsonEncode({'sprache': sprache})));
    _auswerten(antwort);
  }

  Future<void> verfuegbarkeitSetzen(String verfuegbarkeit) async {
    final antwort = await _senden(() => _client.put(Uri.parse('$apiUrl/me/verfuegbarkeit'),
        headers: _kopf(), body: jsonEncode({'verfuegbarkeit': verfuegbarkeit})));
    _auswerten(antwort);
  }

  /// Phase 2 (M02): eigene Touren, aktive zuerst.
  Future<List<TourKurz>> touren() async {
    final antwort = await _senden(() => _client.get(Uri.parse('$apiUrl/touren'), headers: _kopf()));
    return [for (final t in _auswerten(antwort) as List) TourKurz.ausJson(t as Map<String, dynamic>)];
  }

  /// Phase 2 (M03/M04): Tour mit Stopps und Auftraegen.
  Future<TourDetails> tour(int tourId) async {
    final antwort = await _senden(() => _client.get(Uri.parse('$apiUrl/touren/$tourId'), headers: _kopf()));
    return TourDetails.ausJson(_auswerten(antwort) as Map<String, dynamic>);
  }

  /// [aktion]: starten, pausieren, fortsetzen, beenden.
  /// Beenden mit offenen Stopps wirft ApiFehler('offene_stopps'), ausser [erzwingen] ist true.
  Future<void> tourStatus(int tourId, String aktion, {bool erzwingen = false}) async {
    final antwort = await _senden(() => _client.post(Uri.parse('$apiUrl/touren/$tourId/status'),
        headers: _kopf(), body: jsonEncode({'aktion': aktion, 'erzwingen': erzwingen})));
    _auswerten(antwort);
  }

  /// [aktion]: angekommen, entladen, abgeschlossen.
  Future<void> stoppStatus(int stoppId, String aktion) async {
    final antwort = await _senden(() => _client.post(Uri.parse('$apiUrl/stopps/$stoppId/status'),
        headers: _kopf(), body: jsonEncode({'aktion': aktion})));
    _auswerten(antwort);
  }

  /// M05: Problem melden. [art] ist einer der deutschen Werte aus ProblemSeite.arten.
  Future<void> problemMelden(int tourId,
      {int? stoppId, required String art, required String beschreibung, int? dauerMinuten}) async {
    final antwort = await _senden(() => _client.post(Uri.parse('$apiUrl/touren/$tourId/problem'),
        headers: _kopf(),
        body: jsonEncode({
          'stoppId': stoppId,
          'art': art,
          'beschreibung': beschreibung,
          'dauerMinuten': dauerMinuten,
        })));
    _auswerten(antwort);
  }

  /// Phase 0: prueft, ob API und Datenbank erreichbar sind.
  /// Gibt einen lesbaren Text fuer die Anzeige zurueck.
  Future<String> verbindungTesten() async {
    try {
      final antwort = await _client.get(Uri.parse('$apiUrl/health')).timeout(_zeitlimit);
      final daten = jsonDecode(antwort.body) as Map<String, dynamic>;
      if (antwort.statusCode == 200) {
        return Texte.t('verbindung_ok');
      }
      return '${Texte.t('verbindung_db')} ${daten['datenbank']}';
    } catch (e) {
      return '${Texte.t('verbindung_fehler')} ($e)';
    }
  }

  Map<String, String> _kopf({bool mitToken = true}) => {
        'Content-Type': 'application/json',
        if (mitToken && Sitzung.token != null) 'Authorization': 'Bearer ${Sitzung.token}',
      };

  Future<http.Response> _senden(Future<http.Response> Function() anfrage) async {
    try {
      return await anfrage().timeout(_zeitlimit);
    } on SocketException {
      throw ApiFehler('server_nicht_erreichbar');
    } on TimeoutException {
      throw ApiFehler('server_nicht_erreichbar');
    } on http.ClientException {
      throw ApiFehler('server_nicht_erreichbar');
    }
  }

  /// Wertet die Antwort aus. Bei 401 (Token abgelaufen oder Konto gesperrt) wird abgemeldet,
  /// die App zeigt dann automatisch den Login.
  Object? _auswerten(http.Response antwort, {bool abmeldenBei401 = true}) {
    if (antwort.statusCode >= 200 && antwort.statusCode < 300) {
      return antwort.body.isEmpty ? null : jsonDecode(antwort.body);
    }
    if (antwort.statusCode == 401 && abmeldenBei401) {
      Sitzung.abmelden();
    }
    Map<String, dynamic> daten = const {};
    try {
      daten = jsonDecode(antwort.body) as Map<String, dynamic>;
    } catch (_) {
      // Kein JSON, z. B. leere 401-Antwort
    }
    final code = daten['fehler'] as String?;
    throw ApiFehler(code ?? (antwort.statusCode == 401 ? 'anmeldung_fehlgeschlagen' : 'unbekannter_fehler'), daten);
  }
}
