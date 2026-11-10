using System;
using System.Windows;
using System.Windows.Controls;
using GameStartMenu.Library;

namespace GameStartMenu.Views
{
    public partial class SyncTabView : UserControl
    {
        // ----------------------------------------------------------------
        // Events raised to the parent SettingsWindow
        // ----------------------------------------------------------------

        /// <summary>Raised after a Sync Now has completed successfully.</summary>
        public event Action? SyncCompleted;

        // ----------------------------------------------------------------
        // State
        // ----------------------------------------------------------------

        private GameLibraryService? _libraryService;

        // ----------------------------------------------------------------
        // Initialisation
        // ----------------------------------------------------------------

        public SyncTabView()
        {
            InitializeComponent();
        }

        /// <summary>Binds the tab to the shared library service and restores saved settings.</summary>
        public void Initialize(GameLibraryService libraryService)
        {
            _libraryService = libraryService;
            LoadSettings();
        }

        // ----------------------------------------------------------------
        // Settings load
        // ----------------------------------------------------------------

        private void LoadSettings()
        {
            if (_libraryService is null) return;
            var cfg = _libraryService.Config;

            // Restore selected sync frequency
            string freqTag = string.IsNullOrEmpty(cfg.SyncFrequency) ? "Hourly" : cfg.SyncFrequency;
            foreach (ComboBoxItem item in CmbSyncFrequency.Items)
            {
                if (item.Tag?.ToString() == freqTag)
                {
                    CmbSyncFrequency.SelectedItem = item;
                    break;
                }
            }
            if (CmbSyncFrequency.SelectedItem is null && CmbSyncFrequency.Items.Count > 0)
                CmbSyncFrequency.SelectedIndex = 0;

            // Restore API credentials
            TxtSteamApiKey.Password     = cfg.SteamApiKey        ?? "";
            TxtSteamId64.Text           = cfg.SteamId64          ?? "";
            TxtPsnNpsso.Password        = cfg.PsnNpsso           ?? "";
            TxtOpenXblApiKey.Password   = cfg.OpenXblApiKey      ?? "";
            TxtSteamGridDbApiKey.Password = cfg.SteamGridDbApiKey ?? "";

            RefreshLastSyncLabel();
        }

        // ----------------------------------------------------------------
        // Event handlers
        // ----------------------------------------------------------------

        private void BtnSaveSyncSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_libraryService is null) return;

            string freqTag = (CmbSyncFrequency.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Hourly";

            _libraryService.SaveSyncSettings(
                freqTag,
                TxtSteamApiKey.Password,
                TxtSteamId64.Text,
                TxtPsnNpsso.Password,
                TxtOpenXblApiKey.Password,
                TxtSteamGridDbApiKey.Password);

            TxtSaveSyncStatus.Text       = "Sync settings saved!";
            TxtSaveSyncStatus.Visibility = Visibility.Visible;
        }

        private async void BtnSyncNow_Click(object sender, RoutedEventArgs e)
        {
            if (_libraryService is null) return;

            // Save any freshly-entered API keys before syncing
            BtnSaveSyncSettings_Click(sender, e);

            BtnSyncNow.IsEnabled         = false;
            TxtSaveSyncStatus.Text       = "Syncing cloud data...";
            TxtSaveSyncStatus.Visibility = Visibility.Visible;

            try
            {
                _libraryService.LoadCacheAndLocalGames();
                await _libraryService.SyncCloudIfNeededAsync(force: true);

                RefreshLastSyncLabel();
                TxtSaveSyncStatus.Text = "Cloud sync completed!";
                SyncCompleted?.Invoke();
            }
            finally
            {
                BtnSyncNow.IsEnabled = true;
            }
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private void RefreshLastSyncLabel()
        {
            if (_libraryService is null) return;
            var last = _libraryService.Config.LastCloudSyncUtc;

            TxtLastSyncInfo.Text = (last == DateTime.MinValue || last.Year < 2020)
                ? "Last cloud sync: Never"
                : $"Last cloud sync: {last.ToLocalTime():dd/MM/yyyy HH:mm}";
        }
    }
}
