// Einstellungen der Webseite. Hier stehen KEINE Geheimnisse (keine Passwörter, keine Datenbank-Zugangsdaten),
// denn alles in diesem Ordner kann jeder Besucher im Browser lesen.
const DISPOSIO_CONFIG = {
  // Link zum Installer, z. B. aus GitHub Releases:
  // "https://github.com/<team>/<repo>/releases/latest/download/Disposio-Setup.exe"
  // Leer = Download noch nicht verfügbar.
  downloadUrl: "",

  // Adresse der Neon Function, die neue Sendungen annimmt (neon-function/functions/sendung.ts).
  // Leer = Demo-Modus: Das Formular schickt nichts ab, sondern zeigt nur an, was gesendet würde.
  // Nach dem Deploy mit "neon functions get sendung" prüfen (gleiches Muster wie konto in BenutzerKontoDienst.cs).
  sendungApiUrl: "https://br-shy-darkness-b21twd96-sendung.compute.c-6.eu-central-1.aws.neon.tech/",
};
