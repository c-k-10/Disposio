// Formular "Sendung anmelden".
// Gegenstück auf dem Server: neon-function/functions/sendung.ts
// Prüft die Eingaben im Browser und schickt sie als JSON an die Neon Function (siehe js/config.js).
// WICHTIG: Die Prüfung hier ist nur Komfort für den Nutzer. Die Neon Function muss alles noch einmal
// selbst prüfen, denn jeder kann Anfragen auch ohne diese Seite schicken.

const form = document.getElementById("sendung-form");
const meldung = document.getElementById("meldung");
const button = document.getElementById("absenden");

document.getElementById("jahr").textContent = new Date().getFullYear();

// ---------------------------------------------------------------------------
// 1. Für welche Spedition ist die Sendung?  Auswahlfeld, die Liste kommt von der Neon Function (GET).
//    Ein Link mit ?firma=<anmelde_code> wählt die Spedition schon vor.
// ---------------------------------------------------------------------------
const demoModus = !DISPOSIO_CONFIG.sendungApiUrl;
const speditionAuswahl = document.getElementById("spedition");
const vorauswahl = new URLSearchParams(location.search).get("firma");

async function speditionenLaden() {
  if (demoModus) {
    speditionAuswahl.innerHTML = `<option value="demo-spedition">Demo-Spedition</option>`;
    meldung.insertAdjacentHTML("beforeend",
      `<div class="meldung info">Demo-Modus: Es wird nichts gesendet. Trage in <code>js/config.js</code> die Adresse der Neon Function ein.</div>`);
    return;
  }
  try {
    const antwort = await fetch(DISPOSIO_CONFIG.sendungApiUrl);
    const body = await antwort.json();
    if (!antwort.ok || !Array.isArray(body.firmen)) throw new Error(body.fehler || "Liste fehlt");

    speditionAuswahl.innerHTML = `<option value="">Bitte wählen …</option>`;
    for (const f of body.firmen) {
      const option = document.createElement("option");
      option.value = f.code;
      option.textContent = f.name; // textContent: Namen aus der Datenbank können kein HTML einschleusen
      speditionAuswahl.appendChild(option);
    }
    if (vorauswahl && body.firmen.some((f) => f.code === vorauswahl)) speditionAuswahl.value = vorauswahl;
  } catch (fehler) {
    console.error("Speditionen laden:", fehler);
    speditionAuswahl.innerHTML = `<option value="">Liste konnte nicht geladen werden</option>`;
    zeigeMeldung("fehler", "Die Liste der Speditionen konnte nicht geladen werden. Bitte laden Sie die Seite neu.");
  }
}
speditionenLaden();

// Datum: frühestens heute
const heute = new Date().toISOString().slice(0, 10);
document.getElementById("abhol_datum").min = heute;
document.getElementById("liefer_datum").min = heute;
document.getElementById("abhol_datum").addEventListener("change", (e) => {
  document.getElementById("liefer_datum").min = e.target.value || heute;
});

// Kühlung: Temperaturfelder nur bei „Frische“ zeigen – dann sind sie Pflicht.
const gutart = document.getElementById("gutart");
function kuehlungAnzeigen() {
  const gekuehlt = gutart.value === "Frische";
  document.querySelectorAll(".kuehlung").forEach((box) => {
    box.hidden = !gekuehlt;
    box.querySelector("input").required = gekuehlt;
  });
}
gutart.addEventListener("change", kuehlungAnzeigen);
kuehlungAnzeigen();

// ---------------------------------------------------------------------------
// 2. Prüfen
// ---------------------------------------------------------------------------
function fehlerFeld(el) {
  // Das Fehlertext-Element gehört zum Feld-Container im Raster (oder zur ganzen Gruppe).
  const box = el.closest(".raster > div") || el.closest("fieldset");
  return box ? box.querySelector(".fehlertext") : null;
}

function setzeFehler(el, text) {
  el.setAttribute("aria-invalid", "true");
  const f = fehlerFeld(el);
  if (f) f.textContent = text;
}

function loescheFehler() {
  form.querySelectorAll("[aria-invalid]").forEach((el) => el.removeAttribute("aria-invalid"));
  form.querySelectorAll(".fehlertext").forEach((f) => (f.textContent = ""));
}

function pruefe() {
  loescheFehler();
  let erstesFehlerFeld = null;
  const fehler = (el, text) => {
    setzeFehler(el, text);
    erstesFehlerFeld ??= el;
  };

  // Standardprüfungen des Browsers (required, type="email", min/max, pattern)
  for (const el of form.querySelectorAll("input, select, textarea")) {
    if (el.name === "website" || el.closest("[hidden]")) continue;
    if (!el.checkValidity()) {
      if (el.validity.valueMissing) fehler(el, el.type === "checkbox" ? "Bitte bestätigen." : "Bitte ausfüllen.");
      else if (el.validity.typeMismatch) fehler(el, "Bitte eine gültige E-Mail-Adresse eingeben.");
      else if (el.validity.patternMismatch) fehler(el, "Bitte eine gültige Postleitzahl eingeben.");
      else if (el.validity.rangeUnderflow || el.validity.rangeOverflow) fehler(el, "Der Wert ist außerhalb des erlaubten Bereichs.");
      else fehler(el, "Bitte prüfen.");
    }
  }

  // Eigene Prüfungen
  const wert = (id) => document.getElementById(id).value;
  if (wert("abhol_von") && wert("abhol_bis") && wert("abhol_von") >= wert("abhol_bis")) {
    fehler(document.getElementById("abhol_bis"), "„Bis“ muss nach „Von“ liegen.");
  }
  if (wert("liefer_von") && wert("liefer_bis") && wert("liefer_von") >= wert("liefer_bis")) {
    fehler(document.getElementById("liefer_bis"), "„Bis“ muss nach „Von“ liegen.");
  }
  if (wert("abhol_datum") && wert("liefer_datum") && wert("liefer_datum") < wert("abhol_datum")) {
    fehler(document.getElementById("liefer_datum"), "Die Lieferung kann nicht vor der Abholung sein.");
  }

  if (gutart.value === "Frische" && wert("temperatur_min") !== "" && wert("temperatur_max") !== ""
      && Number(wert("temperatur_min")) > Number(wert("temperatur_max"))) {
    fehler(document.getElementById("temperatur_max"), "Max. muss mindestens so hoch sein wie min.");
  }

  if (erstesFehlerFeld) erstesFehlerFeld.focus();
  return !erstesFehlerFeld;
}

// ---------------------------------------------------------------------------
// 3. Daten zusammenbauen (Feldnamen angelehnt an SendungEntwurf in der Desktop-App)
// ---------------------------------------------------------------------------
function sammleDaten() {
  const d = new FormData(form);
  const text = (n) => (d.get(n) || "").toString().trim();
  const zahl = (n) => (text(n) === "" ? null : Number(text(n)));
  const ja = (n) => d.get(n) === "on";

  return {
    firma: speditionAuswahl.value,           // anmelde_code der gewählten Spedition
    auftraggeber: {
      firmenname: text("firmenname"),
      ansprechpartner: text("ansprechpartner"),
      email: text("email"),
      telefon: text("telefon"),
    },
    kundenreferenz: text("kundenreferenz") || null,
    abholung: {
      adresse: text("abhol_adresse"), plz: text("abhol_plz"), ort: text("abhol_ort"),
      datum: text("abhol_datum"), von: text("abhol_von"), bis: text("abhol_bis"),
    },
    lieferung: {
      adresse: text("liefer_adresse"), plz: text("liefer_plz"), ort: text("liefer_ort"),
      datum: text("liefer_datum"), von: text("liefer_von"), bis: text("liefer_bis"),
    },
    avisierungErforderlich: ja("avisierung"),
    gutart: text("gutart"),
    verpackungstyp: text("verpackungstyp"),
    anzahl: zahl("anzahl"),
    gewichtKg: zahl("gewicht_kg"),
    laengeM: zahl("laenge_m"),
    breiteM: zahl("breite_m"),
    hoeheM: zahl("hoehe_m"),
    temperaturMinC: gutart.value === "Frische" ? zahl("temperatur_min") : null,
    temperaturMaxC: gutart.value === "Frische" ? zahl("temperatur_max") : null,
    ladeEntladeequipment: text("ladeequipment"),
    stapelbar: ja("stapelbar"),
    gefahrgut: ja("gefahrgut"),
    zollrelevanz: ja("zollrelevanz"),
    ladehilfsmitteltausch: ja("ladehilfsmitteltausch"),
  };
}

// ---------------------------------------------------------------------------
// 4. Absenden
// ---------------------------------------------------------------------------
form.addEventListener("submit", async (e) => {
  e.preventDefault();

  // Honeypot ausgefüllt → vermutlich ein Bot. Still "Erfolg" zeigen und nichts senden.
  if (document.getElementById("website").value) {
    zeigeMeldung("erfolg", "Vielen Dank! Ihre Sendung wurde angemeldet.");
    return;
  }

  if (!pruefe()) {
    zeigeMeldung("fehler", "Bitte prüfen Sie die markierten Felder.");
    return;
  }

  const daten = sammleDaten();

  if (demoModus) {
    console.log("Demo-Modus – diese Daten würden gesendet:", daten);
    zeigeMeldung("erfolg", "Demo: Die Eingaben sind gültig. Die gesendeten Daten stehen in der Browser-Konsole (F12).");
    return;
  }

  button.disabled = true;
  button.textContent = "Wird gesendet …";
  try {
    const antwort = await fetch(DISPOSIO_CONFIG.sendungApiUrl, {
      method: "POST",
      // text/plain statt application/json: so schickt der Browser keine extra OPTIONS-Anfrage vorweg.
      // Die Funktion liest den Text trotzdem als JSON.
      headers: { "Content-Type": "text/plain" },
      body: JSON.stringify(daten),
    });
    const body = await antwort.json().catch(() => ({}));

    if (antwort.ok) {
      const gewaehlt = speditionAuswahl.value;
      form.reset();
      speditionAuswahl.value = gewaehlt;
      kuehlungAnzeigen();
      zeigeMeldung("erfolg",
        "Vielen Dank! Ihre Sendung wurde angemeldet." +
        (body.auftragsnummer ? ` Ihre Auftragsnummer: ${body.auftragsnummer}.` : "") +
        " Ihre Spedition prüft die Anfrage und meldet sich bei Ihnen.");
    } else {
      zeigeMeldung("fehler", body.fehler || `Die Sendung konnte nicht angemeldet werden (Fehler ${antwort.status}). Bitte versuchen Sie es später erneut.`);
    }
  } catch (fehler) {
    console.error("Senden:", fehler);
    zeigeMeldung("fehler", "Keine Verbindung zum Server. Bitte prüfen Sie Ihre Internetverbindung.");
  } finally {
    button.disabled = false;
    button.textContent = "Sendung anmelden";
  }
});

function zeigeMeldung(art, text) {
  meldung.innerHTML = "";
  const div = document.createElement("div");
  div.className = `meldung ${art}`;
  div.textContent = text; // textContent, damit Texte vom Server kein HTML einschleusen können
  meldung.appendChild(div);
  meldung.scrollIntoView({ behavior: "smooth", block: "nearest" });
}
