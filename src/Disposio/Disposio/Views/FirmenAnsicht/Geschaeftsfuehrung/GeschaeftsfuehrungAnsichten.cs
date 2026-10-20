using Microsoft.UI.Xaml;

namespace Disposio.Views
{
    /// <summary>
    /// Ansichten von Inhaber / Geschäftsführer (G01–G02). Eine fertige Ansicht bekommt hier genau eine Zeile.
    /// Alle Ansichten ohne Eintrag zeigen weiter den Platzhalter.
    /// </summary>
    public static class GeschaeftsfuehrungAnsichten
    {
        public static UIElement? Erstellen(FirmenAnsichtKontext kontext) => kontext.Ansicht.Id switch
        {
            _ => null
        };
    }
}
