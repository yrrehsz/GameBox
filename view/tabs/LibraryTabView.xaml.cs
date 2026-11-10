using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using GameStartMenu.Library;

namespace GameStartMenu.Views
{
    public partial class LibraryTabView : UserControl
    {
        // ----------------------------------------------------------------
        // Events raised to the parent SettingsWindow
        // ----------------------------------------------------------------

        /// <summary>Raised after a game's settings have been saved successfully.</summary>
        public event Action<UnifiedGame>? GameSettingsSaved;

        /// <summary>Raised after a game has been deleted from the library.</summary>
        public event Action? GameDeleted;

        // ----------------------------------------------------------------
        // State
        // ----------------------------------------------------------------

        private GameLibraryService? _libraryService;
        private List<UnifiedGame> _games = new();
        private string? _pendingCoverPath;

        // ----------------------------------------------------------------
        // Initialisation
        // ----------------------------------------------------------------

        public LibraryTabView()
        {
            InitializeComponent();
        }

        /// <summary>Binds the tab to the shared library service and populates the game list.</summary>
        public void Initialize(GameLibraryService libraryService)
        {
            _libraryService = libraryService;
            LoadGameList();
        }

        // ----------------------------------------------------------------
        // Public helpers (called by parent window after external changes)
        // ----------------------------------------------------------------

        /// <summary>Reloads and optionally re-selects a game by its normalised ID.</summary>
        public void LoadGameList(string? selectNormalizedId = null)
        {
            if (_libraryService is null) return;

            _games = _libraryService.GetSortedGames()
                                    .OrderBy(g => g.DisplayTitle)
                                    .ToList();

            CmbGamesList.ItemsSource = _games;

            if (_games.Count > 0)
            {
                int idx = 0;
                if (!string.IsNullOrEmpty(selectNormalizedId))
                {
                    int found = _games.FindIndex(g => g.NormalizedId == selectNormalizedId);
                    if (found >= 0) idx = found;
                }
                CmbGamesList.SelectedIndex = idx;
            }
            else
            {
                TxtGameCounter.Text = "0 / 0";
            }
        }

        // ----------------------------------------------------------------
        // Game selector: ComboBox + Prev / Next
        // ----------------------------------------------------------------

        private void CmbGamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbGamesList.SelectedItem is not UnifiedGame game) return;

            _pendingCoverPath = null;
            TxtSaveLibraryStatus.Visibility = Visibility.Collapsed;

            int idx = CmbGamesList.SelectedIndex;
            TxtGameCounter.Text = $"{idx + 1} / {_games.Count}";
            BtnPrevGame.IsEnabled = idx > 0;
            BtnNextGame.IsEnabled = idx < _games.Count - 1;

            TxtMainExecutable.Text  = game.LaunchCommand ?? "";
            TxtModExecutable.Text   = game.ModLaunchCommand ?? "";
            TxtModDisplayName.Text  = string.IsNullOrEmpty(game.ModDisplayName)
                                        ? "Play Modded"
                                        : game.ModDisplayName;

            ImgLibraryCoverPreview.Source = game.CoverImage;
        }

        private void BtnPrevGame_Click(object sender, RoutedEventArgs e)
        {
            if (CmbGamesList.SelectedIndex > 0)
                CmbGamesList.SelectedIndex--;
        }

        private void BtnNextGame_Click(object sender, RoutedEventArgs e)
        {
            if (CmbGamesList.SelectedIndex < _games.Count - 1)
                CmbGamesList.SelectedIndex++;
        }

        // ----------------------------------------------------------------
        // Executable browse buttons
        // ----------------------------------------------------------------

        private void BtnBrowseMainExe_Click(object sender, RoutedEventArgs e)
        {
            string? file = BrowseExecutable();
            if (!string.IsNullOrEmpty(file)) TxtMainExecutable.Text = file;
        }

        private void BtnBrowseModExe_Click(object sender, RoutedEventArgs e)
        {
            string? file = BrowseExecutable();
            if (!string.IsNullOrEmpty(file)) TxtModExecutable.Text = file;
        }

        // ----------------------------------------------------------------
        // Cover change
        // ----------------------------------------------------------------

        private void BtnChangeCover_Click(object sender, RoutedEventArgs e)
        {
            string? img = BrowseImage();
            if (!string.IsNullOrEmpty(img))
            {
                _pendingCoverPath = img;
                LoadImagePreview(ImgLibraryCoverPreview, img);
            }
        }

        // ----------------------------------------------------------------
        // Save / Delete
        // ----------------------------------------------------------------

        private void BtnSaveGameChanges_Click(object sender, RoutedEventArgs e)
        {
            if (_libraryService is null) return;
            if (CmbGamesList.SelectedItem is not UnifiedGame game) return;

            _libraryService.UpdateGameSettings(
                game,
                TxtMainExecutable.Text,
                TxtModExecutable.Text,
                TxtModDisplayName.Text,
                _pendingCoverPath);

            _pendingCoverPath = null;
            ImgLibraryCoverPreview.Source = game.CoverImage;
            TxtSaveLibraryStatus.Visibility = Visibility.Visible;

            GameSettingsSaved?.Invoke(game);
        }

        private void BtnDeleteGame_Click(object sender, RoutedEventArgs e)
        {
            if (_libraryService is null) return;
            if (CmbGamesList.SelectedItem is not UnifiedGame game) return;

            var result = MessageBox.Show(
                $"Are you sure you want to remove '{game.DisplayTitle}' from your library?",
                "Delete Game",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _libraryService.DeleteGame(game);
                LoadGameList();
                GameDeleted?.Invoke();
            }
        }

        // ----------------------------------------------------------------
        // Static file-dialog helpers
        // ----------------------------------------------------------------

        private static string? BrowseExecutable()
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Select Game Executable or Launcher Script",
                Filter = "Executables & Scripts (*.exe;*.bat;*.cmd;*.lnk;*.url)|*.exe;*.bat;*.cmd;*.lnk;*.url|All files (*.*)|*.*"
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        private static string? BrowseImage()
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Select Cover Image (600x900 recommended)",
                Filter = "Image Files (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp"
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        internal static void LoadImagePreview(System.Windows.Controls.Image target, string? filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                target.Source = null;
                return;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                using var ms = new MemoryStream(bytes);

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption  = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();

                target.Source = bmp;
            }
            catch
            {
                target.Source = null;
            }
        }
    }
}
