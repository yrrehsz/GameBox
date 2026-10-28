using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wpf.Ui.Controls;
using GameStartMenu.Library;
using System.Collections.Generic;

namespace GameStartMenu
{
    // 1. Cambiato da FluentWindow a Window così WPF-UI non inietta lo sfondo grigio/blu opaco!
    public partial class MainWindow : Window
    {
        private bool _isMenuOpen = false;
        private bool _ignoreDeactivate = false;
        private DateTime _lastDeactivatedTime = DateTime.MinValue;
        private double _anchorX = -1;
        private bool _isInitializing = true;

        
        private readonly GameLibraryService _libraryService = new GameLibraryService();
        private readonly ControllerDetector _controllerDetector;

        private SettingsWindow? _settingsWindow;

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        internal static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [StructLayout(LayoutKind.Sequential)]
        internal struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public uint GradientColor;
            public int AnimationId;
        }

        public MainWindow()
        {
            InitializeComponent();
            _controllerDetector = new ControllerDetector(_libraryService.Config.ControllerIcons);
            AvviaAscoltoSegnali();

            _controllerDetector.OnControllersChanged += AggiornaUIController;
            _controllerDetector.Start();
            _libraryService.OnLibraryUpdated += AggiornaGrigliaGiochi;
            _libraryService.LoadCacheAndLocalGames();

            _libraryService.Config.ControllerIcons["TEST_XBOX_ID"] = "pack://application:,,,/assets/controllers/xbox/special/fh5.png";
_libraryService.Config.Save();

            // Header events
            SelezionaComboPerTag(HeaderView.CmbShowFilter, _libraryService.Config.ShowFilter);
            SelezionaComboPerTag(HeaderView.CmbOrderBy, _libraryService.Config.OrderBy);
            HeaderView.SearchBox.TextChanged += SearchBox_TextChanged;
            HeaderView.CmbShowFilter.SelectionChanged += FilterOrSort_SelectionChanged;
            HeaderView.CmbOrderBy.SelectionChanged += FilterOrSort_SelectionChanged;

            // Library events
            LibraryView.OnPlayGameRequested += AvviaGioco;
            LibraryView.OnPlayModdedRequested += AvviaGiocoModdato;

            

            // Footer events
            FooterView.BtnSync.Click += BtnSync_Click;
            FooterView.BtnPinTaskbar.Click += BtnPinInfo_Click;
            FooterView.BtnSettings.Click += BtnSettings_Click;
            FooterView.BtnClose.Click += BtnEsci_Click;

            // Controller events
            FooterView.OnIdentifyRequested += async (ctrl) => await _controllerDetector.IdentifyControllerAsync(ctrl);
            FooterView.OnTurnOffRequested += (ctrl) => _controllerDetector.TurnOffController(ctrl);

            _isInitializing = false;

            _ = _libraryService.SyncCloudIfNeededAsync(force: false, isStartup: true);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            AttivaVetroSfocatoPuro();
        }

        private void AttivaVetroSfocatoPuro()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;

            // Arrotonda il riquadro del Blur a livello hardware su Windows 11 (2 = DWMWCP_ROUND)
            int cornerPreference = 2;
            DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));

            // Attiva il Blur trasparente:
            // ATTENZIONE: non mettere MAI "00" come primi due numeri altrimenti Windows 11 mostra grigio solido!
            // Usa "01" (99% trasparente) oppure "66" (60% trasparente)
            var accent = new AccentPolicy
            {
                AccentState = 4, // 3 = Blur Behind puro (zero grigio di fallback!), 4 = Acrylic Blur
                AccentFlags = 2,
                GradientColor = 0x22181818
            };

            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(accent, ptr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = 19,
                SizeOfData = size,
                Data = ptr
            };

            SetWindowCompositionAttribute(handle, ref data);
            Marshal.FreeHGlobal(ptr);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ApriMenu();
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            if (_isMenuOpen && !_ignoreDeactivate)
            {
                _lastDeactivatedTime = DateTime.Now;
                ChiudiMenu();
            }
        }

        private void BtnEsci_Click(object sender, RoutedEventArgs e)
        {
            Environment.Exit(0);
        }

        private void BtnPinInfo_Click(object sender, RoutedEventArgs e)
        {
            // Crea nella cartella Documenti un piccolo eseguibile "LauncherGiochi.exe" 
            // che NON viene mai bloccato da VS Code e NON mostra MAI la lineetta sulla Taskbar!
            string docsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string launcherPath = Path.Combine(docsDir, "LauncherGiochi.exe");
            string cscPath = @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe";

            string tempCs = Path.Combine(Path.GetTempPath(), "LauncherGiochi.cs");
            string currentExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameStartMenu.exe");

            // Piccolo codice C# che sveglia il menu o lo avvia se è spento, e si chiude in 1ms (Zero lineetta!)
            string code = $@"
using System.Threading;
using System.Diagnostics;
class Program {{
    static void Main() {{
        EventWaitHandle s;
        if (EventWaitHandle.TryOpenExisting(""GameStartMenu_ToggleSignal"", out s)) {{
            s.Set();
        }} else {{
            Process.Start(new ProcessStartInfo {{ FileName = @""{currentExe}"", UseShellExecute = true }});
        }}
    }}
}}";
            File.WriteAllText(tempCs, code);

            Process.Start(new ProcessStartInfo
            {
                FileName = cscPath,
                Arguments = $"/target:winexe /out:\"{launcherPath}\" \"{tempCs}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            })?.WaitForExit();

            // Apre Esplora File selezionando LauncherGiochi.exe pronto da fissare sulla Taskbar!
            Process.Start("explorer.exe", $"/select,\"{launcherPath}\"");
        }

        public void ApriMenu()
        {
            _isMenuOpen = true;
            this.Show();
            this.WindowState = WindowState.Normal;
            PosizionaSopraIcona();
            this.Activate();

            DoubleAnimation slideUp = new DoubleAnimation
            {
                From = 35,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            DoubleAnimation fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(140)
            };

            SlideTransform.BeginAnimation(TranslateTransform.YProperty, slideUp);
            MainContentGrid.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }

        public void ChiudiMenu()
        {
            if (!_isMenuOpen) return;
            _isMenuOpen = false;

            DoubleAnimation slideDown = new DoubleAnimation
            {
                From = 0,
                To = 35,
                Duration = TimeSpan.FromMilliseconds(140),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            DoubleAnimation fadeOut = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(120)
            };

            fadeOut.Completed += (s, a) =>
            {
                if (!_isMenuOpen)
                {
                    this.Hide();
                }
            };

            SlideTransform.BeginAnimation(TranslateTransform.YProperty, slideDown);
            MainContentGrid.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        private void PosizionaSopraIcona()
        {
            Rect workArea = SystemParameters.WorkArea;

            if (GetCursorPos(out POINT mousePos))
            {
                var source = PresentationSource.FromVisual(this);
                double dpiX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
                double dpiY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

                double mouseX = mousePos.X * dpiX;
                double mouseY = mousePos.Y * dpiY;

                if (mouseY >= workArea.Bottom - 40)
                {
                    _anchorX = mouseX - (this.Width / 2);
                }
                else if (_anchorX < 0)
                {
                    _anchorX = workArea.Left + 12;
                }
            }

            this.Left = Math.Max(workArea.Left + 12, Math.Min(_anchorX, workArea.Right - this.Width - 12));
            this.Top = workArea.Bottom - this.Height - 12;
        }

        private void AvviaAscoltoSegnali()
        {
            // 1. Ascolta il clic sull'icona della Taskbar (Toggle)
            Task.Run(() =>
            {
                var signal = new EventWaitHandle(false, EventResetMode.AutoReset, "GameStartMenu_ToggleSignal");
                while (true)
                {
                    signal.WaitOne();
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        if (_isMenuOpen)
                        {
                            ChiudiMenu();
                        }
                        else if ((DateTime.Now - _lastDeactivatedTime).TotalMilliseconds > 350)
                        {
                            ApriMenu();
                        }
                    });
                }
            });

            // 2. Ascolta il segnale di chiusura automatica quando ricompili
            Task.Run(() =>
            {
                var quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "GameStartMenu_QuitSignal");
                quitSignal.WaitOne();
                Environment.Exit(0);
            });
        }

        private void AggiornaUIController(List<ControllerInfo> controllers)
        {
            Dispatcher.Invoke(() =>
            {
                if (controllers.Count == 0)
                {
                    FooterView.NoControllerBadge.Visibility = Visibility.Visible;
                    FooterView.ControllersList.Visibility = Visibility.Collapsed;
                }
                else
                {
                    FooterView.NoControllerBadge.Visibility = Visibility.Collapsed;
                    FooterView.ControllersList.ItemsSource = controllers;
                    FooterView.ControllersList.Visibility = Visibility.Visible;
                }
            });
        }

        /*private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            // Impedisce alla finestra principale di chiudersi mentre il menu a comparsa è aperto
            _ignoreDeactivate = true;

            if (sender is FrameworkElement btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.IsOpen = true;
            }
        }*/

        /*private void SettingsPopupMenu_Closed(object sender, RoutedEventArgs e)
        {
            // Riattiva la chiusura automatica quando il menu a comparsa si chiude
            _ignoreDeactivate = false;
            this.Activate();
        }*/

        private void AggiornaGrigliaGiochi(List<UnifiedGame> games)
        {
            Dispatcher.Invoke(() =>
            {
                LibraryView.GamesGridControl.ItemsSource = null;
                LibraryView.GamesGridControl.ItemsSource = games;
            });
        }

        private void BtnPlayGame_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement btn && btn.Tag is UnifiedGame game && !string.IsNullOrEmpty(game.LaunchCommand))
            {
                try
                {
                    // Avvia il gioco tramite il protocollo (es. steam://rungameid/1245620)
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = game.LaunchCommand,
                        UseShellExecute = true
                    });

                    // Chiude elegantemente il menu Start mentre il gioco si avvia!
                    ChiudiMenu();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Impossibile avviare {game.DisplayTitle}: {ex.Message}");
                }
            }
        }

        /*private async void BtnAddGame_Click(object sender, RoutedEventArgs e)
        {
            // Forza un aggiornamento immediato della libreria e delle copertine
            await _libraryService.SyncCloudIfNeededAsync(force: true);
        }*/

        private void SelezionaComboPerTag(System.Windows.Controls.ComboBox combo, string targetTag)
        {
            foreach (System.Windows.Controls.ComboBoxItem item in combo.Items)
            {
                if (item.Tag?.ToString() == targetTag)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_isInitializing) return;
            _libraryService.SetSearchQuery(HeaderView.SearchBox.Text);
        }

        private void FilterOrSort_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializing || HeaderView?.CmbShowFilter == null || HeaderView?.CmbOrderBy == null) return;

            string showTag = (HeaderView.CmbShowFilter.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "All";
            string orderTag = (HeaderView.CmbOrderBy.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "Default";

            _libraryService.UpdateViewSettings(showTag, orderTag);
        }

        private void AvviaGioco(UnifiedGame game)
        {
            if (string.IsNullOrEmpty(game.LaunchCommand)) return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = game.LaunchCommand,
                    UseShellExecute = true
                });
                ChiudiMenu();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Impossibile avviare {game.DisplayTitle}: {ex.Message}");
            }
        }

        private void AvviaGiocoModdato(UnifiedGame game)
        {
            if (string.IsNullOrEmpty(game.ModLaunchCommand)) return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = game.ModLaunchCommand,
                    UseShellExecute = true
                });
                ChiudiMenu();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Impossibile avviare la versione moddata di {game.DisplayTitle}: {ex.Message}");
            }
        }

        private async void BtnSync_Click(object sender, RoutedEventArgs e)
        {
            FooterView.BtnSync.IsEnabled = false;

            // Avvia l'animazione di rotazione continua sull'icona Sync
            var spinAnimation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromMilliseconds(850),
                RepeatBehavior = RepeatBehavior.Forever
            };
            FooterView.SyncRotateTransform.BeginAnimation(RotateTransform.AngleProperty, spinAnimation);

            try
            {
                // 1. Rilegge eventuali nuovi giochi installati in locale
                _libraryService.LoadCacheAndLocalGames();
                

                // 2. Forza la sincronizzazione Cloud (Steam, PlayStation, Xbox) e scarica le cover mancanti
                await _libraryService.SyncCloudIfNeededAsync(force: true);
            }
            finally
            {
                // Ferma la rotazione e riabilita il pulsante
                FooterView.SyncRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
                FooterView.SyncRotateTransform.Angle = 0;
                FooterView.BtnSync.IsEnabled = true;
            }
        }

        

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            // Chiude il popup del menu Start mentre lavoriamo comodamente nella finestra Impostazioni
            ChiudiMenu();

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

        
    }
}