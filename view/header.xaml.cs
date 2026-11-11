using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace GameStartMenu.Views
{
    /// <summary>
    /// Top header bar containing the game search box, Windows 11-style icon filter checkboxes,
    /// and the library sort order dropdown.
    /// </summary>
    public partial class HeaderView : UserControl
    {
        private const string AllFilterKey = "All";

        private readonly List<FilterOption> _filterOptions;

        /// <summary>
        /// Raised whenever the user toggles one of the header filter checkboxes.
        /// Passes the updated comma-separated filter string (e.g. "All" or "Xbox,Steam").
        /// </summary>
        public event Action<string>? FilterChanged;

        public HeaderView()
        {
            InitializeComponent();

            _filterOptions = new List<FilterOption>
            {
                new(AllFilterKey,   "All games",                    LoadFilterIcon("all.png"),          SymbolRegular.Library24),
                new("Executable",   "Installed / Executable games", LoadFilterIcon("executable.png"),   SymbolRegular.Play24),
                new("Xbox",         "Xbox games",                   LoadFilterIcon("xbox.png"),         SymbolRegular.Games24),
                new("Steam",        "Steam games",                  LoadFilterIcon("steam.png"),        SymbolRegular.CloudSync24),
                new("PlayStation",  "PlayStation games",            LoadFilterIcon("playstation.png"),  SymbolRegular.Sparkle24),
                new("Completed100", "100% completed",               LoadFilterIcon("completed100.png"), SymbolRegular.Trophy24)
            };

            FilterCheckBoxesList.ItemsSource = _filterOptions;
            SetActiveFilters(AllFilterKey);
        }

        /// <summary>
        /// Restores checkbox selection state from a saved comma-separated filter string without raising FilterChanged.
        /// </summary>
        public void SetActiveFilters(string? filterCsv)
        {
            var tokens = (filterCsv ?? AllFilterKey)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            bool showAll = tokens.Count == 0 || tokens.Contains(AllFilterKey);

            // Only apply specific filters if any recognized specific key is present
            bool anySpecificSelected = !showAll && _filterOptions.Any(o => o.Key != AllFilterKey && tokens.Contains(o.Key));
            if (!anySpecificSelected)
                showAll = true;

            foreach (var option in _filterOptions)
            {
                option.IsSelected = option.Key == AllFilterKey
                    ? showAll
                    : !showAll && tokens.Contains(option.Key);
            }
        }

        /// <summary>
        /// Returns the active filter state formatted as "All" or a comma-separated list of active filter keys.
        /// </summary>
        public string GetActiveFilterCsv()
        {
            var allOption = _filterOptions.First(o => o.Key == AllFilterKey);
            if (allOption.IsSelected)
                return AllFilterKey;

            var selectedKeys = _filterOptions
                .Where(o => o.Key != AllFilterKey && o.IsSelected)
                .Select(o => o.Key)
                .ToList();

            return selectedKeys.Count > 0
                ? string.Join(",", selectedKeys)
                : AllFilterKey;
        }

        private void FilterCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox chk || chk.DataContext is not FilterOption clicked)
                return;

            var allOption = _filterOptions.First(o => o.Key == AllFilterKey);
            var specificOptions = _filterOptions.Where(o => o.Key != AllFilterKey).ToList();

            if (clicked.Key == AllFilterKey)
            {
                // Keep "All" selected if clicked when no specific filter is active,
                // and always clear all specific filters when "All" is enabled.
                clicked.IsSelected = true;
                foreach (var opt in specificOptions)
                    opt.IsSelected = false;
            }
            else
            {
                if (clicked.IsSelected)
                {
                    allOption.IsSelected = false;
                }
                else if (!specificOptions.Any(o => o.IsSelected))
                {
                    // Fall back to "All" automatically when the last specific filter is unchecked
                    allOption.IsSelected = true;
                }
            }

            FilterChanged?.Invoke(GetActiveFilterCsv());
        }

        private static BitmapImage? LoadFilterIcon(string fileName)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "icons", "filters", fileName);
                if (!File.Exists(path)) return null;

                byte[] imageBytes = File.ReadAllBytes(path);
                using var ms = new MemoryStream(imageBytes);

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Represents a single toggleable filter icon button in the header toolbar.
        /// </summary>
        public sealed class FilterOption : INotifyPropertyChanged
        {
            private bool _isSelected;

            public string Key { get; }
            public string Tooltip { get; }
            public BitmapImage? IconImage { get; }
            public SymbolRegular FallbackSymbol { get; }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public FilterOption(string key, string tooltip, BitmapImage? iconImage, SymbolRegular fallbackSymbol)
            {
                Key = key;
                Tooltip = tooltip;
                IconImage = iconImage;
                FallbackSymbol = fallbackSymbol;
            }
        }
    }
}
