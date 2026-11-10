using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using GameStartMenu.Library;

namespace GameStartMenu.Views
{
    public partial class AddGameTabView : UserControl
    {
        // ----------------------------------------------------------------
        // Events raised to the parent SettingsWindow
        // ----------------------------------------------------------------

        /// <summary>Raised after a new game has been added; carries the new game's normalised ID.</summary>
        public event Action<string>? GameAdded;

        // ----------------------------------------------------------------
        // State
        // ----------------------------------------------------------------

        private GameLibraryService? _libraryService;
        private string? _pendingCoverPath;

        // ----------------------------------------------------------------
        // Initialisation
        // ----------------------------------------------------------------

        public AddGameTabView()
        {
            InitializeComponent();
        }

        /// <summary>Binds the tab to the shared library service.</summary>
        public void Initialize(GameLibraryService libraryService)
        {
            _libraryService = libraryService;
        }

        // ----------------------------------------------------------------
        // Step 1: browse for executable
        // ----------------------------------------------------------------

        private void BtnBrowseNewGameExe_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Select Game Executable or Launcher Script",
                Filter = "Executables & Scripts (*.exe;*.bat;*.cmd;*.lnk;*.url)|*.exe;*.bat;*.cmd;*.lnk;*.url|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            TxtNewGameExe.Text = dlg.FileName;

            // Auto-suggest the title from the exe filename when the title field is still empty
            if (string.IsNullOrWhiteSpace(TxtNewGameTitle.Text))
                TxtNewGameTitle.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
        }

        // ----------------------------------------------------------------
        // Step 2: live-preview of the normalised ID
        // ----------------------------------------------------------------

        private void TxtNewGameTitle_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtNormalizedPreview is null) return;

            string raw = TxtNewGameTitle.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(raw))
            {
                TxtNormalizedPreview.Text = "Normalized ID: -";
                return;
            }

            var (key, cleanTitle) = TitleNormalizer.Normalize(raw);
            TxtNormalizedPreview.Text = $"Normalized Title: {cleanTitle} (ID: {key})";
        }

        // ----------------------------------------------------------------
        // Step 3: cover options
        // ----------------------------------------------------------------

        private void ChkAutoDownloadCover_Changed(object sender, RoutedEventArgs e)
        {
            if (CustomCoverPanel is null) return;
            CustomCoverPanel.Visibility = ChkAutoDownloadCover.IsChecked == true
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void BtnBrowseNewGameCover_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Select Cover Image (600x900 recommended)",
                Filter = "Image Files (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp"
            };
            if (dlg.ShowDialog() != true) return;

            _pendingCoverPath = dlg.FileName;
            TxtNewGameCoverPath.Text = dlg.FileName;
            LibraryTabView.LoadImagePreview(ImgNewGameCustomCover, dlg.FileName);
        }

        // ----------------------------------------------------------------
        // Add to Library
        // ----------------------------------------------------------------

        private async void BtnConfirmAddGame_Click(object sender, RoutedEventArgs e)
        {
            if (_libraryService is null) return;

            string exePath = TxtNewGameExe.Text?.Trim() ?? "";
            string title   = TxtNewGameTitle.Text?.Trim() ?? "";

            if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(title))
            {
                MessageBox.Show(
                    "Please select an executable and enter a game title.",
                    "Missing Info");
                return;
            }

            BtnConfirmAddGame.IsEnabled = false;
            TxtAddGameStatus.Text = "Adding game...";

            try
            {
                bool autoCover    = ChkAutoDownloadCover.IsChecked == true;
                string? customCov = autoCover ? null : _pendingCoverPath;

                var added = await _libraryService.AddCustomGameWithOptionsAsync(title, exePath, autoCover, customCov);

                // Reset the form
                TxtNewGameExe.Text        = "";
                TxtNewGameTitle.Text      = "";
                _pendingCoverPath         = null;
                TxtNewGameCoverPath.Text  = "No image selected (Recommended ratio: 600x900)";
                ImgNewGameCustomCover.Source = null;

                TxtAddGameStatus.Text = $"'{added.DisplayTitle}' added to your library!";
                GameAdded?.Invoke(added.NormalizedId);
            }
            finally
            {
                BtnConfirmAddGame.IsEnabled = true;
            }
        }
    }
}
