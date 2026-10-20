using System;
using System.Collections.Generic;
using Disposio.Models;

namespace Disposio.Views
{
    /// <summary>
    /// Das bekommt jede Firmenansicht vom Rahmen (FirmenAnsicht): welche Ansicht sie ist, mit welchen
    /// Rollen gearbeitet wird, einen optionalen Startwert und die Möglichkeit, andere Ansichten zu öffnen.
    /// </summary>
    public sealed class FirmenAnsichtKontext
    {
        private readonly Action<string, object?> _oeffnen;
        private readonly Action _zugriffBeenden;

        public FirmenAnsichtKontext(AnsichtInfo ansicht, IReadOnlyList<FirmenRolle> rollen, bool istVorschau,
            object? parameter, Action<string, object?> oeffnen, Action zugriffBeenden)
        {
            Ansicht = ansicht;
            Rollen = rollen;
            IstVorschau = istVorschau;
            Parameter = parameter;
            _oeffnen = oeffnen;
            _zugriffBeenden = zugriffBeenden;
        }

        public AnsichtInfo Ansicht { get; }

        /// <summary>Die Rollen, mit denen die Firmenansicht gerade bedient wird.</summary>
        public IReadOnlyList<FirmenRolle> Rollen { get; }

        /// <summary>
        /// Debug-Vorschau ohne Anmeldung: Es gibt keine Firma, also werden auch keine Firmendaten geladen.
        /// Ansichten zeigen dann einen Hinweis statt einer Fehlermeldung.
        /// </summary>
        public bool IstVorschau { get; }

        /// <summary>Optionaler Startwert, zum Beispiel die Fahrzeug-Id, wenn F01 die Ansicht F02 öffnet.</summary>
        public object? Parameter { get; }

        /// <summary>Öffnet eine andere Ansicht, zum Beispiel <c>Oeffnen("F02", fahrzeugId)</c>.</summary>
        public void Oeffnen(string id, object? parameter = null) => _oeffnen(id, parameter);

        /// <summary>
        /// Aufrufen, wenn <c>AuthService.FirmenZugriffPruefenAsync</c> eine UnauthorizedAccessException wirft:
        /// Die Sitzung endet und die Anmeldung erscheint.
        /// </summary>
        public void ZugriffBeenden() => _zugriffBeenden();
    }
}
