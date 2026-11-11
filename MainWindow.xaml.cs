using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GameStartMenu.Library;
using GameStartMenu.Services;

namespace GameStartMenu
{
    /// <summary>
    /// Popup shell window hosting the header, game library grid, and controller/settings footer.
    /// Uses a plain WPF <see cref="Window"/> rather than FluentWindow so DWM acrylic blur stays crisp.
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Constants & State

        private const double MenuSlideOffset = 35;
        private const int OpenSlideDurationMs = 200;
        private const int OpenFadeDurationMs = 140;
        private const int CloseSlideDurationMs = 140;
        private const int CloseFadeDurationMs = 120;
        private const int IpcToggleDebounceMs = 350;
        private const int OutsideClickPollIntervalMs = 50;
        private const int SyncSpinDurationMs = 850;

        private readonly GameLibraryService _libraryService = new();
        private readonly ControllerDetector _controllerDetector;
        private readonly IpcSignalListener _ipcListener;

        private DispatcherTimer? _outsideClickTimer;
        private SettingsWindow? _settingsWindow;
        private bool _isMenuOpen;
        private bool _isInitializing = true;
        private DateTime _lastDeactivatedTime = DateTime.MinValue;
        private double _anchorX = -1;

        #endregion

        #region Constructor & Initialization

        public MainWindow()
        {
            InitializeComponent();

            _controllerDetector = new ControllerDetector(_libraryService.Config.ControllerIcons);
            _ipcListener = new IpcSignalListener(
                onToggleRequested: () => Application.Current?.Dispatcher.Invoke(HandleIpcToggle),
                onQuitRequested: () => Environment.Exit(0));

            _ipcListener.Start();

            InitializeControllerDetection();
            InitializeLibraryState();
            BindChildViewEvents();

            _isInitializing = false;

            _ = _libraryService.SyncCloudIfNeededAsync(force: false, isStartup: true);
        }

        private void InitializeControllerDetection()
        {
            _controllerDetector.OnControllersChanged += UpdateControllersUi;
            _controllerDetector.OnToggleMenuShortcutPressed += () =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() => ToggleMenu(fromController: true));
            };
            _controllerDetector.OnButtonPressed += button =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() => HandleControllerButton(button));
            };
            _controllerDetector.Start();
        }

        private void InitializeLibraryState()
        {
            _libraryService.OnLibraryUpdated += UpdateGamesGrid;
            _libraryService.LoadCacheAndLocalGames();

            _libraryService.Config.ControllerIcons["TEST_XBOX_ID"] = "pack://application:,,,/assets/controllers/xbox/special/fh5.png";
            _libraryService.Config.Save();
        }

        private void BindChildViewEvents()
        {
            // Header
            HeaderView.SetActiveFilters(_libraryService.Config.ShowFilter);
            SelectComboBoxItemByTag(HeaderView.CmbOrderBy, _libraryService.Config.OrderBy);
            HeaderView.SearchBox.TextChanged += SearchBox_TextChanged;
            HeaderView.FilterChanged += _ => ApplyViewSettings();
            HeaderView.CmbOrderBy.SelectionChanged += SortOrder_SelectionChanged;

            // Library
            LibraryView.OnPlayGameRequested += game => LaunchGameCommand(game.LaunchCommand, $"Unable to launch {game.DisplayTitle}");
            LibraryView.OnPlayModdedRequested += game => LaunchGameCommand(game.ModLaunchCommand, $"Unable to launch the modded version of {game.DisplayTitle}");
            LibraryView.OnGameInfoRequested += HandleGameInfoRequested;

            // Footer
            FooterView.BtnSync.Click += BtnSync_Click;
            FooterView.BtnPinTaskbar.Click += BtnPinTaskbar_Click;
            FooterView.BtnSettings.Click += BtnSettings_Click;
            FooterView.BtnClose.Click += BtnClose_Click;
            FooterView.OnIdentifyRequested += async ctrl => await _controllerDetector.IdentifyControllerAsync(ctrl);
            FooterView.OnTurnOffRequested += ctrl => _controllerDetector.TurnOffController(ctrl);
        }

        #endregion

        #region Window Lifecycle & Acrylic Blur

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Win32WindowHelper.EnableAcrylicBlur(this);
        }

        protected override void OnClosed(EventArgs e)
        {
            _outsideClickTimer?.Stop();
            _ipcListener.Dispose();
            base.OnClosed(e);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            OpenMenu();
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            if (!_isMenuOpen) return;

            _lastDeactivatedTime = DateTime.Now;
            CloseMenu();
        }

        #endregion

        #region Menu Open / Close & Outside-Click Handling

        /// <summary>
        /// Shows the menu popup anchored above the taskbar icon and steals foreground focus cleanly.
        /// </summary>
        public void OpenMenu(bool selectFirstCard = false)
        {
            _isMenuOpen = true;
            Show();
            WindowState = WindowState.Normal;

            Win32WindowHelper.PositionNearTaskbar(this, ref _anchorX);
            Win32WindowHelper.ForceForegroundFocus(this);
            EnsureOutsideClickMonitorRunning();

            if (selectFirstCard)
            {
                LibraryView.SelectFirstItem();
            }

            AnimateMenuTransition(isOpening: true);
        }

        /// <summary>
        /// Slides out and hides the menu popup if it is currently open.
        /// </summary>
        public void CloseMenu()
        {
            if (!_isMenuOpen) return;

            _isMenuOpen = false;
            _outsideClickTimer?.Stop();
            LibraryView.ClearSelection();

            AnimateMenuTransition(isOpening: false, onCompleted: () =>
            {
                if (!_isMenuOpen)
                {
                    Hide();
                }
            });
        }

        private void ToggleMenu(bool fromController = false)
        {
            if (_isMenuOpen)
            {
                CloseMenu();
            }
            else
            {
                OpenMenu(selectFirstCard: fromController);
            }
        }

        private void HandleControllerButton(ControllerButton button)
        {
            if (!_isMenuOpen || !IsVisible) return;

            switch (button)
            {
                case ControllerButton.DPadUp:
                case ControllerButton.DPadDown:
                case ControllerButton.DPadLeft:
                case ControllerButton.DPadRight:
                    LibraryView.Navigate(button);
                    break;

                case ControllerButton.A:
                case ControllerButton.X:
                case ControllerButton.Y:
                    LibraryView.ExecuteAction(button);
                    break;

                case ControllerButton.B:
                    CloseMenu();
                    break;
            }
        }

        private void HandleIpcToggle()
        {
            if (_isMenuOpen)
            {
                CloseMenu();
            }
            else if ((DateTime.Now - _lastDeactivatedTime).TotalMilliseconds > IpcToggleDebounceMs)
            {
                OpenMenu();
            }
        }

        private void AnimateMenuTransition(bool isOpening, Action? onCompleted = null)
        {
            var slideAnimation = new DoubleAnimation
            {
                From = isOpening ? MenuSlideOffset : 0,
                To = isOpening ? 0 : MenuSlideOffset,
                Duration = TimeSpan.FromMilliseconds(isOpening ? OpenSlideDurationMs : CloseSlideDurationMs),
                EasingFunction = new CubicEase { EasingMode = isOpening ? EasingMode.EaseOut : EasingMode.EaseIn }
            };

            var fadeAnimation = new DoubleAnimation
            {
                From = isOpening ? 0 : 1,
                To = isOpening ? 1 : 0,
                Duration = TimeSpan.FromMilliseconds(isOpening ? OpenFadeDurationMs : CloseFadeDurationMs)
            };

            if (onCompleted != null)
            {
                fadeAnimation.Completed += (_, _) => onCompleted();
            }

            SlideTransform.BeginAnimation(TranslateTransform.YProperty, slideAnimation);
            MainContentGrid.BeginAnimation(OpacityProperty, fadeAnimation);
        }

        private void EnsureOutsideClickMonitorRunning()
        {
            if (_outsideClickTimer == null)
            {
                _outsideClickTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(OutsideClickPollIntervalMs)
                };
                _outsideClickTimer.Tick += OutsideClickTimer_Tick;
            }

            _outsideClickTimer.Start();
        }

        private void OutsideClickTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isMenuOpen || !Win32WindowHelper.IsMouseButtonDown()) return;

            if (Win32WindowHelper.IsCursorOutsideWindow(this))
            {
                _lastDeactivatedTime = DateTime.Now;
                CloseMenu();
            }
        }

        #endregion

        #region Library, Filter & Controller Updates

        private void UpdateControllersUi(List<ControllerInfo> controllers)
        {
            void Apply()
            {
                bool hasControllers = controllers.Count > 0;
                LibraryView.IsControllerConnected = hasControllers;
                if (!hasControllers)
                {
                    LibraryView.ClearSelection();
                }

                FooterView.NoControllerBadge.Visibility = hasControllers ? Visibility.Collapsed : Visibility.Visible;
                FooterView.ControllersList.ItemsSource = hasControllers ? controllers : null;
                FooterView.ControllersList.Visibility = hasControllers ? Visibility.Visible : Visibility.Collapsed;
            }

            if (Dispatcher.CheckAccess()) Apply();
            else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Apply);
        }

        private void UpdateGamesGrid(List<UnifiedGame> games)
        {
            void Apply() => LibraryView.UpdateGames(games);

            if (Dispatcher.CheckAccess()) Apply();
            else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Apply);
        }

        private void HandleGameInfoRequested(UnifiedGame game)
        {
            // Reserved for the future Game Info & Achievements view (currently triggered by Info button or controller Y).
        }

        private static void SelectComboBoxItemByTag(ComboBox combo, string targetTag)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (item.Tag?.ToString() == targetTag)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }

            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitializing) return;
            _libraryService.SetSearchQuery(HeaderView.SearchBox.Text);
        }

        private void SortOrder_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyViewSettings();
        }

        private void ApplyViewSettings()
        {
            if (_isInitializing || HeaderView?.CmbOrderBy == null) return;

            string showFilterCsv = HeaderView.GetActiveFilterCsv();
            string orderTag = (HeaderView.CmbOrderBy.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Default";

            _libraryService.UpdateViewSettings(showFilterCsv, orderTag);
        }

        #endregion

        #region Game Launch & Footer Actions

        private void LaunchGameCommand(string? command, string errorPrefix)
        {
            if (string.IsNullOrEmpty(command)) return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = command,
                    UseShellExecute = true
                });
                CloseMenu();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{errorPrefix}: {ex.Message}", "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnSync_Click(object sender, RoutedEventArgs e)
        {
            FooterView.BtnSync.IsEnabled = false;

            var spinAnimation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromMilliseconds(SyncSpinDurationMs),
                RepeatBehavior = RepeatBehavior.Forever
            };
            FooterView.SyncRotateTransform.BeginAnimation(RotateTransform.AngleProperty, spinAnimation);

            try
            {
                _libraryService.LoadCacheAndLocalGames();
                await _libraryService.SyncCloudIfNeededAsync(force: true);
            }
            finally
            {
                FooterView.SyncRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
                FooterView.SyncRotateTransform.Angle = 0;
                FooterView.BtnSync.IsEnabled = true;
            }
        }

        private void BtnPinTaskbar_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TaskbarLauncherService.CreateAndRevealLauncher();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error creating taskbar launcher:\n\n{ex.Message}", "GameBox Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            // Close the start menu popup while working in Settings.
            CloseMenu();

            try
            {
                if (_settingsWindow == null || !_settingsWindow.IsLoaded)
                {
                    _settingsWindow = new SettingsWindow(_libraryService);
                    _settingsWindow.Show();
                }
                else
                {
                    _settingsWindow.Activate();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening Settings:\n\n{ex.Message}\n\n{ex.StackTrace}", "GameBox Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Environment.Exit(0);
        }

        #endregion
    }
}
