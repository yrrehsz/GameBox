using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using GameStartMenu.Library;

namespace GameStartMenu.Views
{
    public partial class AppearanceTabView : UserControl
    {
        // ----------------------------------------------------------------
        // State
        // ----------------------------------------------------------------

        private GameLibraryService? _libraryService;

        /// <summary>Ordered list of platform names representing the current overlay priority.</summary>
        private readonly List<string> _overlayPriority = new();

        // ----------------------------------------------------------------
        // Initialisation
        // ----------------------------------------------------------------

        public AppearanceTabView()
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

            _overlayPriority.Clear();
            string saved = _libraryService.Config.OverlayPriority ?? "Xbox,PlayStation,Steam";

            if (!saved.Equals("None", System.StringComparison.OrdinalIgnoreCase))
            {
                foreach (var p in saved.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
                {
                    if ((p == "Xbox" || p == "PlayStation" || p == "Steam") && !_overlayPriority.Contains(p))
                        _overlayPriority.Add(p);
                }
            }

            RefreshOverlayUI();
        }

        // ----------------------------------------------------------------
        // Overlay tile click handlers
        // ----------------------------------------------------------------

        private void OverlayPlatformCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox chk) return;

            string platform = chk == ChkOverlayXbox ? "Xbox"
                            : chk == ChkOverlayPs   ? "PlayStation"
                            : "Steam";

            if (chk.IsChecked == true)
            {
                // Append to priority list in click order (first click = highest priority)
                if (!_overlayPriority.Contains(platform))
                    _overlayPriority.Add(platform);
            }
            else
            {
                _overlayPriority.Remove(platform);
            }

            SaveAndRefresh();
        }

        private void OverlayNoneCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (ChkOverlayNone.IsChecked == true)
            {
                // Disable all overlays
                _overlayPriority.Clear();
            }
            else if (_overlayPriority.Count == 0)
            {
                // If the user unchecks "None" without selecting anything, restore the default order
                _overlayPriority.AddRange(new[] { "Xbox", "PlayStation", "Steam" });
            }

            SaveAndRefresh();
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private void SaveAndRefresh()
        {
            RefreshOverlayUI();

            string value = _overlayPriority.Count == 0
                ? "None"
                : string.Join(",", _overlayPriority);

            _libraryService?.UpdateOverlayPrioritySetting(value);
        }

        private void RefreshOverlayUI()
        {
            bool isNone = _overlayPriority.Count == 0;

            ChkOverlayNone.IsChecked  = isNone;
            ChkOverlayXbox.IsChecked  = _overlayPriority.Contains("Xbox");
            ChkOverlayPs.IsChecked    = _overlayPriority.Contains("PlayStation");
            ChkOverlaySteam.IsChecked = _overlayPriority.Contains("Steam");

            // Show the numeric priority badge (1, 2, 3) on each checked tile
            ChkOverlayXbox.Tag  = (_overlayPriority.IndexOf("Xbox") + 1).ToString();
            ChkOverlayPs.Tag    = (_overlayPriority.IndexOf("PlayStation") + 1).ToString();
            ChkOverlaySteam.Tag = (_overlayPriority.IndexOf("Steam") + 1).ToString();

            TxtOverlayPrioritySummary.Text = isNone
                ? "Active Priority: None (Overlays disabled, all achievements shown as bottom icons)"
                : $"Active Priority: {string.Join("  ›  ", _overlayPriority)}";
        }
    }
}
