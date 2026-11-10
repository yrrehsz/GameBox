using System.Windows;
using Wpf.Ui.Controls;
using GameStartMenu.Library;

namespace GameStartMenu
{
    public partial class SettingsWindow : FluentWindow
    {
        // ----------------------------------------------------------------
        // State
        // ----------------------------------------------------------------

        private readonly GameLibraryService _libraryService;

        // ----------------------------------------------------------------
        // Constructor
        // ----------------------------------------------------------------

        public SettingsWindow(GameLibraryService libraryService)
        {
            InitializeComponent();
            _libraryService = libraryService;

            // Wire up tab-level events
            PageLibrary.GameSettingsSaved += _ => { /* future: notify main window if needed */ };
            PageLibrary.GameDeleted       += () => { /* future: notify main window if needed */ };

            PageAddGame.GameAdded += normalizedId =>
            {
                // After a game is added, refresh the Library tab and select the new game
                PageLibrary.LoadGameList(normalizedId);
            };

            PageSync.SyncCompleted += () =>
            {
                // After a sync, refresh the Library tab so new data is reflected
                PageLibrary.LoadGameList();
            };

            // Inject the service into each tab
            PageLibrary.Initialize(_libraryService);
            PageAddGame.Initialize(_libraryService);
            PageAppearance.Initialize(_libraryService);
            PageSync.Initialize(_libraryService);
            // PageController has no service dependency yet
        }

        // ----------------------------------------------------------------
        // Navigation: show the tab whose radio button was just checked
        // ----------------------------------------------------------------

        private void NavTab_Checked(object sender, RoutedEventArgs e)
        {
            if (PageLibrary is null || PageAddGame is null || PageController is null || PageSync is null || PageAppearance is null)
                return;

            PageLibrary.Visibility    = NavLibrary.IsChecked    == true ? Visibility.Visible : Visibility.Collapsed;
            PageAddGame.Visibility    = NavAddGame.IsChecked    == true ? Visibility.Visible : Visibility.Collapsed;
            PageController.Visibility = NavController.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageSync.Visibility       = NavSync.IsChecked       == true ? Visibility.Visible : Visibility.Collapsed;
            PageAppearance.Visibility = NavAppearance.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
