using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using GameStartMenu.Library;

namespace GameStartMenu
{
    public partial class SettingsWindow : FluentWindow
    {
        private readonly GameLibraryService _libraryService;
        private List<UnifiedGame> _games = new();
        private string? _pendingLibraryCoverPath = null;
        private string? _pendingNewGameCoverPath = null;

        public SettingsWindow(GameLibraryService libraryService)
        {
            InitializeComponent();
            _libraryService = libraryService;

            CaricaListaGiochi();
            CaricaImpostazioniSync();
        }

        // =====================================================================
        // NAVIGAZIONE TRA LE 4 SCHEDE LATERALI
        // =====================================================================
        private void NavTab_Checked(object sender, RoutedEventArgs e)
        {
            if (PageLibrary == null) return;

            PageLibrary.Visibility = NavLibrary.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageAddGame.Visibility = NavAddGame.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageController.Visibility = NavController.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageSync.Visibility = NavSync.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // =====================================================================
        // SCHEDA 1: LIBRARY (Scelta gioco con Dropdown o Frecce + Modifica)
        // =====================================================================
        private void CaricaListaGiochi(string? selectNormalizedId = null)
        {
            _games = _libraryService.GetSortedGames()
                                    .OrderBy(g => g.DisplayTitle)
                                    .ToList();

            CmbGamesList.ItemsSource = _games;

            if (_games.Count > 0)
            {
                int indexToSelect = 0;
                if (!string.IsNullOrEmpty(selectNormalizedId))
                {
                    int foundIdx = _games.FindIndex(g => g.NormalizedId == selectNormalizedId);
                    if (foundIdx >= 0) indexToSelect = foundIdx;
                }
                CmbGamesList.SelectedIndex = indexToSelect;
            }
            else
            {
                TxtGameCounter.Text = "0 / 0";
            }
        }

        private void CmbGamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbGamesList.SelectedItem is not UnifiedGame game) return;

            _pendingLibraryCoverPath = null;
            TxtSaveLibraryStatus.Visibility = Visibility.Collapsed;

            int idx = CmbGamesList.SelectedIndex;
            TxtGameCounter.Text = $"{idx + 1} / {_games.Count}";

            BtnPrevGame.IsEnabled = idx > 0;
            BtnNextGame.IsEnabled = idx < _games.Count - 1;

            TxtMainExecutable.Text = game.LaunchCommand ?? "";
            TxtModExecutable.Text = game.ModLaunchCommand ?? "";
            TxtModDisplayName.Text = string.IsNullOrEmpty(game.ModDisplayName) ? "Play Modded" : game.ModDisplayName;

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

        private void BtnBrowseMainExe_Click(object sender, RoutedEventArgs e)
        {
            string? file = SfogliaEseguibile();
            if (!string.IsNullOrEmpty(file))
                TxtMainExecutable.Text = file;
        }

        private void BtnBrowseModExe_Click(object sender, RoutedEventArgs e)
        {
            string? file = SfogliaEseguibile();
            if (!string.IsNullOrEmpty(file))
                TxtModExecutable.Text = file;
        }

        private void BtnChangeCover_Click(object sender, RoutedEventArgs e)
        {
            string? imgFile = SfogliaImmagine();
            if (!string.IsNullOrEmpty(imgFile))
            {
                _pendingLibraryCoverPath = imgFile;
                CaricaImmagineAnteprima(ImgLibraryCoverPreview, imgFile);
            }
        }

        // =====================================================================
        // SCHEDA 2: ADD A GAME (Browse Exe + Nome Normalizzato + Cover Auto/Custom)
        // =====================================================================
        private void BtnBrowseNewGameExe_Click(object sender, RoutedEventArgs e)
        {
            string? file = SfogliaEseguibile();
            if (!string.IsNullOrEmpty(file))
            {
                TxtNewGameExe.Text = file;

                // Se il titolo è ancora vuoto, suggerisce automaticamente il nome del file .exe pulito
                if (string.IsNullOrWhiteSpace(TxtNewGameTitle.Text))
                {
                    TxtNewGameTitle.Text = Path.GetFileNameWithoutExtension(file);
                }
            }
        }

        private void TxtNewGameTitle_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtNormalizedPreview == null) return;
            string rawTitle = TxtNewGameTitle.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(rawTitle))
            {
                TxtNormalizedPreview.Text = "Normalized ID: -";
                return;
            }

            var (key, cleanTitle) = TitleNormalizer.Normalize(rawTitle);
            TxtNormalizedPreview.Text = $"Normalized Title: {cleanTitle} (ID: {key})";
        }

        private void ChkAutoDownloadCover_Changed(object sender, RoutedEventArgs e)
        {
            if (CustomCoverPanel == null) return;
            CustomCoverPanel.Visibility = ChkAutoDownloadCover.IsChecked == true
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void BtnBrowseNewGameCover_Click(object sender, RoutedEventArgs e)
        {
            string? imgFile = SfogliaImmagine();
            if (!string.IsNullOrEmpty(imgFile))
            {
                _pendingNewGameCoverPath = imgFile;
                TxtNewGameCoverPath.Text = imgFile;
                CaricaImmagineAnteprima(ImgNewGameCustomCover, imgFile);
            }
        }

        private async void BtnConfirmAddGame_Click(object sender, RoutedEventArgs e)
        {
            string exePath = TxtNewGameExe.Text?.Trim() ?? "";
            string title = TxtNewGameTitle.Text?.Trim() ?? "";

            if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(title))
            {
                System.Windows.MessageBox.Show("Please select an executable and enter a game title.", "Missing Info");
                return;
            }

            BtnConfirmAddGame.IsEnabled = false;
            TxtAddGameStatus.Text = "Adding game...";

            try
            {
                bool autoCover = ChkAutoDownloadCover.IsChecked == true;
                string? customCover = autoCover ? null : _pendingNewGameCoverPath;

                var addedGame = await _libraryService.AddCustomGameWithOptionsAsync(title, exePath, autoCover, customCover);

                // Reset form e aggiorna la lista nella scheda Library selezionando il gioco appena aggiunto
                TxtNewGameExe.Text = "";
                TxtNewGameTitle.Text = "";
                _pendingNewGameCoverPath = null;
                TxtNewGameCoverPath.Text = "No image selected (Recommended ratio: 600x900)";
                ImgNewGameCustomCover.Source = null;

                CaricaListaGiochi(addedGame.NormalizedId);
                TxtAddGameStatus.Text = $"'{addedGame.DisplayTitle}' added to your library!";
            }
            finally
            {
                BtnConfirmAddGame.IsEnabled = true;
            }
        }

        // =====================================================================
        // HELPERS DIALOGHI FILE E ANTEPRIMA IMMAGINI
        // =====================================================================
        private static string? SfogliaEseguibile()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select Game Executable or Launcher Script",
                Filter = "Executables & Scripts (*.exe;*.bat;*.cmd;*.lnk;*.url)|*.exe;*.bat;*.cmd;*.lnk;*.url|All files (*.*)|*.*"
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        private static string? SfogliaImmagine()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select Cover Image (600x900 recommended)",
                Filter = "Image Files (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp"
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        private void BtnSaveGameChanges_Click(object sender, RoutedEventArgs e)
        {
            if (CmbGamesList.SelectedItem is not UnifiedGame game) return;

            _libraryService.UpdateGameSettings(
                game,
                TxtMainExecutable.Text,
                TxtModExecutable.Text,
                TxtModDisplayName.Text,
                _pendingLibraryCoverPath);

            _pendingLibraryCoverPath = null;
            ImgLibraryCoverPreview.Source = game.CoverImage; // Aggiorna subito anche la preview
            TxtSaveLibraryStatus.Visibility = Visibility.Visible;
        }

        private static void CaricaImmagineAnteprima(System.Windows.Controls.Image targetImage, string? filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                targetImage.Source = null;
                return;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                using var ms = new MemoryStream(bytes);

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();

                targetImage.Source = bmp;
            }
            catch
            {
                targetImage.Source = null;
            }
        }

        private void BtnDeleteGame_Click(object sender, RoutedEventArgs e)
        {
            if (CmbGamesList.SelectedItem is not UnifiedGame game) return;

            var result = System.Windows.MessageBox.Show(
                $"Are you sure you want to remove '{game.DisplayTitle}' from your library?",
                "Delete Game",
                System.Windows.MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                _libraryService.DeleteGame(game);
                CaricaListaGiochi();
            }
        }

        // =====================================================================
        // SCHEDA 4: SYNC (API Keys + Frequenza di Sincronizzazione)
        // =====================================================================
        private void CaricaImpostazioniSync()
        {
            var cfg = _libraryService.Config;

            // Seleziona la frequenza nel ComboBox
            string freqTag = string.IsNullOrEmpty(cfg.SyncFrequency) ? "Hourly" : cfg.SyncFrequency;
            foreach (ComboBoxItem item in CmbSyncFrequency.Items)
            {
                if (item.Tag?.ToString() == freqTag)
                {
                    CmbSyncFrequency.SelectedItem = item;
                    break;
                }
            }
            if (CmbSyncFrequency.SelectedItem == null && CmbSyncFrequency.Items.Count > 0)
                CmbSyncFrequency.SelectedIndex = 0;

            // Popola le API Key attuali
            TxtSteamApiKey.Password = cfg.SteamApiKey ?? "";
            TxtSteamId64.Text = cfg.SteamId64 ?? "";
            TxtPsnNpsso.Password = cfg.PsnNpsso ?? "";
            TxtOpenXblApiKey.Password = cfg.OpenXblApiKey ?? "";
            TxtSteamGridDbApiKey.Password = cfg.SteamGridDbApiKey ?? "";

            AggiornaTestoUltimoSync();
        }

        private void AggiornaTestoUltimoSync()
        {
            var lastSync = _libraryService.Config.LastCloudSyncUtc;
            if (lastSync == DateTime.MinValue || lastSync.Year < 2020)
            {
                TxtLastSyncInfo.Text = "Last cloud sync: Never";
            }
            else
            {
                TxtLastSyncInfo.Text = $"Last cloud sync: {lastSync.ToLocalTime():dd/MM/yyyy HH:mm}";
            }
        }

        private void BtnSaveSyncSettings_Click(object sender, RoutedEventArgs e)
        {
            string freqTag = (CmbSyncFrequency.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Hourly";

            _libraryService.SaveSyncSettings(
                freqTag,
                TxtSteamApiKey.Password,
                TxtSteamId64.Text,
                TxtPsnNpsso.Password,
                TxtOpenXblApiKey.Password,
                TxtSteamGridDbApiKey.Password);

            TxtSaveSyncStatus.Text = "Sync settings saved!";
            TxtSaveSyncStatus.Visibility = Visibility.Visible;
        }

        private async void BtnSyncNowSettings_Click(object sender, RoutedEventArgs e)
        {
            // Salva prima eventuali nuove API Key appena inserite dall'utente
            BtnSaveSyncSettings_Click(sender, e);

            BtnSyncNowSettings.IsEnabled = false;
            TxtSaveSyncStatus.Text = "Syncing cloud data...";
            TxtSaveSyncStatus.Visibility = Visibility.Visible;

            try
            {
                _libraryService.LoadCacheAndLocalGames();
                await _libraryService.SyncCloudIfNeededAsync(force: true);

                AggiornaTestoUltimoSync();
                CaricaListaGiochi();
                TxtSaveSyncStatus.Text = "Cloud sync completed!";
            }
            finally
            {
                BtnSyncNowSettings.IsEnabled = true;
            }
        }
    }
}