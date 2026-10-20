using Microsoft.UI.Xaml;

namespace Disposio.Views
{
    /// <summary>
    /// Ansichten des Fuhrparkmanagers (F01–F05). Eine fertige Ansicht bekommt hier genau eine Zeile.
    /// Alle Ansichten ohne Eintrag zeigen weiter den Platzhalter.
    /// </summary>
    public static class FuhrparkAnsichten
    {
        public static UIElement? Erstellen(FirmenAnsichtKontext kontext) => kontext.Ansicht.Id switch
        {
            "F01" => new F01Fuhrpark(kontext),
            "F02" => new F02FahrzeugFormular(kontext),
            _ => null
        };
    }
}
