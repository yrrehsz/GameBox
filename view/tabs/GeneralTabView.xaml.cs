using System;
using System.Windows;
using System.Windows.Controls;
using GameBox.Library;
using GameBox.Services;

namespace GameBox.Views
{
    public partial class GeneralTabView : UserControl
    {
        private GameLibraryService? _libraryService;
        private bool _isInitializing;

        public GeneralTabView()
        {
            InitializeComponent();
        }

        /// <summary>Binds the tab to the shared library service and restores saved startup settings.</summary>
        public void Initialize(GameLibraryService libraryService)
        {
            _libraryService = libraryService;
            LoadSettings();
        }

        private void LoadSettings()
        {
            if (_libraryService is null) return;

            _isInitializing = true;
            try
            {
                TglLaunchAtStartup.IsChecked = _libraryService.Config.LaunchAtStartup;
                TxtLauncherPath.Text = TaskbarLauncherService.LauncherExePath;
            }
            finally
            {
                _isInitializing = false;
            }
        }

        private void TglLaunchAtStartup_Click(object sender, RoutedEventArgs e)
        {
            if (_isInitializing || _libraryService is null) return;

            bool enabled = TglLaunchAtStartup.IsChecked == true;
            try
            {
                WindowsStartupService.SetStartupEnabled(enabled);
                _libraryService.UpdateLaunchAtStartupSetting(enabled);
            }
            catch (Exception ex)
            {
                _isInitializing = true;
                TglLaunchAtStartup.IsChecked = !enabled;
                _isInitializing = false;

                MessageBox.Show(
                    $"Unable to update Windows startup setting:\n\n{ex.Message}",
                    "GameBox Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BtnRevealLauncher_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TaskbarLauncherService.CreateAndRevealLauncher();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error creating taskbar launcher:\n\n{ex.Message}",
                    "GameBox Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
