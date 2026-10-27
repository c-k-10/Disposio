using Microsoft.UI.Xaml;

namespace Disposio.Views
{
    /// <summary>
    /// Ansichten des Kundenadmins (K01–K05). Eine fertige Ansicht bekommt hier genau eine Zeile.
    /// Alle Ansichten ohne Eintrag zeigen weiter den Platzhalter.
    /// </summary>
    public static class KundenadminAnsichten
    {
        public static UIElement? Erstellen(FirmenAnsichtKontext kontext) => kontext.Ansicht.Id switch
        {
            "K03" => new K03RollenUndRechte(kontext),
            "K06" => new K05Abteilungen(kontext),
            "K05" => new AktionsprotokollAnsicht(alleFirmen: false, key => Application.Current.Resources[key], kontext.ZugriffBeenden),
            _ => null
        };
    }
}
