using System.Linq;
using Disposio.Models;
using Disposio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Disposio.Views
{
    // K03 braucht keine Firmendaten: Die Rollen und ihre Aufgaben stehen im FirmenRollenKatalog.
    // Ansichten mit Firmendaten rufen vor dem Laden AuthService.FirmenZugriffPruefenAsync auf.
    public sealed partial class K03RollenUndRechte : UserControl
    {
        private readonly FirmenAnsichtKontext _kontext;

        public K03RollenUndRechte(FirmenAnsichtKontext kontext)
        {
            InitializeComponent();
            _kontext = kontext;

            // Nur ein echtes Firmenkonto hat eigene Rollen. Beim Admin-Zugang wird nichts markiert.
            var eigeneRollen = BenutzerSitzung.AktuellerBenutzer?.FirmenRollen ?? System.Array.Empty<FirmenRolle>();
            RollenListe.ItemsSource = FirmenRollenKatalog.Alle
                .Select(rolle => new RollenZeile(rolle, eigeneRollen.Contains(rolle)))
                .ToList();
        }

        private void ZurBenutzerliste_Click(object sender, RoutedEventArgs e) => _kontext.Oeffnen("K01");
    }

    // Eine Tabellenzeile. Die XAML-Vorlage bindet mit {x:Bind Name} an diese Eigenschaften.
    public sealed class RollenZeile
    {
        public RollenZeile(FirmenRolle rolle, bool eigeneRolle)
        {
            Bezeichnung = FirmenRollenKatalog.Bezeichnung(rolle);
            Bereich = FirmenRollenKatalog.Bereich(rolle);
            Aufgaben = FirmenRollenKatalog.Aufgaben(rolle);
            EigeneRolleSichtbarkeit = eigeneRolle ? Visibility.Visible : Visibility.Collapsed;
        }

        public string Bezeichnung { get; }
        public string Bereich { get; }
        public string Aufgaben { get; }
        public Visibility EigeneRolleSichtbarkeit { get; }
    }
}
