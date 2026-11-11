using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media.Imaging;

namespace GameStartMenu.Library
{
    /// <summary>
    /// Represents a unified game entry aggregating metadata, executable paths,
    /// cover artwork, and multi-platform achievement progress (Steam, PlayStation, Xbox).
    /// </summary>
    public class UnifiedGame
    {
        // =====================================================================
        // Core Identification & Execution
        // =====================================================================

        public string NormalizedId { get; set; } = "";
        public string DisplayTitle { get; set; } = "";
        public GameActionType ActionType { get; set; } = GameActionType.DisplayOnly;
        public string? LaunchCommand { get; set; }
        public string? CoverUrl { get; set; }
        public string? LocalCoverPath { get; set; }
        public Dictionary<string, PlatformCompletion> Platforms { get; set; } = new();

        /// <summary>True if this game was added or customized by the user, protecting it from sync overrides.</summary>
        public bool IsCustomGame { get; set; } = false;

        /// <summary>If true, synchronization will not overwrite the local cover path.</summary>
        public bool HasCustomCover { get; set; } = false;

        // =====================================================================
        // Secondary / Modded Launcher
        // =====================================================================

        /// <summary>Path to an optional secondary launcher, mod engine, or emulator script.</summary>
        public string? ModLaunchCommand { get; set; }

        /// <summary>Display name for the secondary launcher button tooltip (e.g. "Play Modded", "Launch RPCS3").</summary>
        public string ModDisplayName { get; set; } = "Play Modded";

        [JsonIgnore]
        public Visibility PlayButtonVisibility => ActionType == GameActionType.Executable
            ? Visibility.Visible
            : Visibility.Collapsed;

        [JsonIgnore]
        public Visibility ModButtonVisibility => !string.IsNullOrEmpty(ModLaunchCommand)
            ? Visibility.Visible
            : Visibility.Collapsed;

        [JsonIgnore]
        public bool IsRunnable => ActionType == GameActionType.Executable || !string.IsNullOrEmpty(ModLaunchCommand);

        // =====================================================================
        // Cover Art & Image Cache
        // =====================================================================

        [JsonIgnore]
        private BitmapImage? _coverImageCache;

        /// <summary>Clears the cached BitmapImage so that changes on disk are loaded immediately.</summary>
        public void ReloadCoverImage()
        {
            _coverImageCache = null;
        }

        [JsonIgnore]
        public BitmapImage? CoverImage
        {
            get
            {
                if (_coverImageCache != null)
                    return _coverImageCache;

                if (string.IsNullOrEmpty(LocalCoverPath) || !File.Exists(LocalCoverPath))
                    return null;

                try
                {
                    // Read file bytes into memory and close the file handle immediately (zero file locks)
                    byte[] imageBytes = File.ReadAllBytes(LocalCoverPath);
                    using var ms = new MemoryStream(imageBytes);

                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();

                    _coverImageCache = bmp;
                    return _coverImageCache;
                }
                catch
                {
                    return null;
                }
            }
        }

        // =====================================================================
        // 100% Completion Cover Overlay Priority
        // =====================================================================

        /// <summary>Global platform priority order loaded from AppConfig (updated by GameLibraryService).</summary>
        [JsonIgnore]
        public static string[] GlobalOverlayPriority { get; set; } = new[] { "Xbox", "PlayStation", "Steam" };

        private static string? GetOverlayFileName(string platform) => platform switch
        {
            "Xbox" => "mythic_overlay.png",
            "PlayStation" => "platinum_overlay.png",
            "Steam" => "steam_overlay.png",
            _ => null
        };

        /// <summary>
        /// Determines which single platform (among those with 100% completion) earns the right
        /// to show its completion overlay on the cover, according to GlobalOverlayPriority.
        /// </summary>
        [JsonIgnore]
        public string? ActiveOverlayPlatform
        {
            get
            {
                foreach (var plat in GlobalOverlayPriority)
                {
                    if (Platforms.TryGetValue(plat, out var p) && (p.IsCompleted100 || p.Percentage >= 100))
                    {
                        string? fileName = GetOverlayFileName(plat);
                        if (!string.IsNullOrEmpty(fileName))
                        {
                            string fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "icons", "achievements", fileName);
                            if (File.Exists(fullPath))
                                return plat;
                        }
                    }
                }
                return null;
            }
        }

        [JsonIgnore]
        public Visibility PlatinumOverlayVisibility =>
            ActiveOverlayPlatform != null ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public BitmapImage? PlatinumOverlayImage
        {
            get
            {
                string? active = ActiveOverlayPlatform;
                if (active == null) return null;
                string? fileName = GetOverlayFileName(active);
                return fileName != null ? LoadIcon(fileName) : null;
            }
        }

        [JsonIgnore]
        public string PlatinumOverlayTooltip => ActiveOverlayPlatform switch
        {
            "Xbox" => XboxTooltip,
            "PlayStation" => PsTooltip,
            "Steam" => SteamTooltip,
            _ => ""
        };

        // =====================================================================
        // Platform Achievement Badge Bar (Below Cover)
        // =====================================================================

        [JsonIgnore]
        public double SteamIconSize => 32.0;

        [JsonIgnore]
        public double PsIconSize
        {
            get
            {
                if (!Platforms.TryGetValue("PlayStation", out var p)) return 27.0;
                if (p.IsCompleted100 || p.Percentage >= 100) return 34.0;
                return 27.0;
            }
        }

        [JsonIgnore]
        public double XboxIconSize => 28.0;

        [JsonIgnore]
        public Visibility SteamBadgeVisibility =>
            Platforms.ContainsKey("Steam") && ActiveOverlayPlatform != "Steam"
                ? Visibility.Visible
                : Visibility.Collapsed;

        [JsonIgnore]
        public Visibility PsBadgeVisibility =>
            Platforms.ContainsKey("PlayStation") && ActiveOverlayPlatform != "PlayStation"
                ? Visibility.Visible
                : Visibility.Collapsed;

        [JsonIgnore]
        public Visibility XboxBadgeVisibility =>
            Platforms.ContainsKey("Xbox") && ActiveOverlayPlatform != "Xbox"
                ? Visibility.Visible
                : Visibility.Collapsed;

        [JsonIgnore]
        public string SteamTooltip => Platforms.TryGetValue("Steam", out var p)
            ? $"Steam: {p.Percentage}% ({p.Unlocked}/{p.Total})"
            : "";

        [JsonIgnore]
        public string PsTooltip => Platforms.TryGetValue("PlayStation", out var p)
            ? $"PlayStation: {p.Percentage}% ({p.Unlocked}/{p.Total}) {p.ExtraInfo}"
            : "";

        [JsonIgnore]
        public string XboxTooltip => Platforms.TryGetValue("Xbox", out var p)
            ? $"Xbox: {p.Percentage}% ({p.ExtraInfo})"
            : "";

        [JsonIgnore]
        public BitmapImage? SteamRankIcon => Platforms.TryGetValue("Steam", out var p)
            ? LoadIcon(GetSteamIconName(p.Percentage, p.IsCompleted100))
            : null;

        [JsonIgnore]
        public BitmapImage? PsRankIcon => Platforms.TryGetValue("PlayStation", out var p)
            ? LoadIcon(GetPsIconName(p.Percentage, p.IsCompleted100))
            : null;

        [JsonIgnore]
        public BitmapImage? XboxRankIcon => Platforms.TryGetValue("Xbox", out var p)
            ? LoadIcon(GetXboxIconName(p.Percentage, p.IsCompleted100))
            : null;

        private static string GetPsIconName(int pct, bool is100)
        {
            if (is100 || pct >= 100) return "ps_platinum.png";
            return pct switch
            {
                <= 10 => "ps_locked.png",
                <= 25 => "ps_bronze.png",
                <= 49 => "ps_silver.png",
                _     => "ps_gold.png"
            };
        }

        private static string GetXboxIconName(int pct, bool is100)
        {
            if (is100 || pct >= 100) return "xbox_mythic.png";
            return pct switch
            {
                <= 10 => "xbox_locked.png",
                <= 25 => "xbox_silver.png",
                <= 49 => "xbox_silver.png",
                _     => "xbox_diamond.png"
            };
        }

        private static string GetSteamIconName(int pct, bool is100)
        {
            if (is100 || pct >= 100) return "steam_purple.png";
            return pct switch
            {
                <= 10 => "steam_locked.png",
                <= 25 => "steam_white.png",
                <= 49 => "steam_green.png",
                _     => "steam_red.png"
            };
        }

        private static BitmapImage? LoadIcon(string fileName)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "icons", "achievements", fileName);
                if (!File.Exists(path)) return null;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        // =====================================================================
        // Sorting & Filtering Helpers
        // =====================================================================

        [JsonIgnore]
        public int TotalPlatinumsCount => Platforms.Values.Count(p => p.IsCompleted100 || p.Percentage >= 100);

        [JsonIgnore]
        public int MaxCompletionPercentage => Platforms.Values.Any() ? Platforms.Values.Max(p => p.Percentage) : 0;
    }
}
