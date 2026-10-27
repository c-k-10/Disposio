using Microsoft.UI.Xaml;

namespace Disposio.Views
{
    /// <summary>
    /// Ansichten des Disponenten (D01–D15). Eine fertige Ansicht bekommt hier genau eine Zeile.
    /// Alle Ansichten ohne Eintrag zeigen weiter den Platzhalter.
    /// </summary>
    public static class DispositionAnsichten
    {
        public static UIElement? Erstellen(FirmenAnsichtKontext kontext) => kontext.Ansicht.Id switch
        {
            "D02" => new D02Sendungen(kontext),
            "D03" => new D03SendungFormular(kontext),
            "D04" => new D04Touren(kontext),
            "D05" => new D05TourPlanen(kontext),
            "D08" => new D08LiveKarte(kontext),
            _ => null
        };
    }
}
