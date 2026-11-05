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
            "F03" => new F03Fahrerzuordnung(kontext),
            "F04" => new F04Wartung(kontext),
            "F05" => new F05WartungFormular(kontext),
            _ => null
        };
    }
}
