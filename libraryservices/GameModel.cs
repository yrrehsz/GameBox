using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media.Imaging;
using System.IO;
using System.Linq;

namespace GameStartMenu.Library
{
    public enum GameActionType
    {
        DisplayOnly,
        Executable
    }

    public class PlatformCompletion
    {
        public string PlatformName { get; set; } = "";
        public string SubPlatform { get; set; } = "";
        public int Unlocked { get; set; }
        public int Total { get; set; }
        public int Percentage { get; set; }
        public bool IsCompleted100 { get; set; }
        public string ExtraInfo { get; set; } = "";
    }

    public class UnifiedGame
    {
        public string NormalizedId { get; set; } = "";
        public string DisplayTitle { get; set; } = "";
        public GameActionType ActionType { get; set; } = GameActionType.DisplayOnly;
        public string? LaunchCommand { get; set; }
        public string? CoverUrl { get; set; }
        public string? LocalCoverPath { get; set; }
        public Dictionary<string, PlatformCompletion> Platforms { get; set; } = new();

        [JsonIgnore]
        public Visibility PlayButtonVisibility => ActionType == GameActionType.Executable ? Visibility.Visible : Visibility.Collapsed;

        // Ordine di priorità globale letto da AppConfig (aggiornato da GameLibraryService)
        [JsonIgnore]
        public static string[] GlobalOverlayPriority { get; set; } = new[] { "Xbox", "PlayStation", "Steam" };

        // Mappa piattaforma -> file immagine overlay dentro assets/icons/achievements/
        private static string? GetOverlayFileName(string platform) => platform switch
        {
            "Xbox" => "mythic_overlay.png",
            "PlayStation" => "platinum_overlay.png",
            "Steam" => "steam_overlay.png",
            _ => null
        };

        // Determina quale SINGOLA piattaforma (tra quelle al 100%) vince il diritto di mostrare l'overlay
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
                                return plat; // Trovato il vincitore!
                        }
                    }
                }
                return null;
            }
        }

        // Dimensione dinamica per l'icona Steam (Default: 28px, 100%: 30px)
        [JsonIgnore]
        public double SteamIconSize => Platforms.TryGetValue("Steam", out var p)
            ? ((p.IsCompleted100 || p.Percentage >= 100) ? 32.0 : 32.0)
            : 32.0;

        // Dimensione dinamica per l'icona PlayStation (Platino più grande: 36px, Trofei base: 29px)
        [JsonIgnore]
        public double PsIconSize
        {
            get
            {
                if (!Platforms.TryGetValue("PlayStation", out var p)) return 27.0;
                if (p.IsCompleted100 || p.Percentage >= 100) return 34.0; // Trofeo di Platino più grande!
                return p.Percentage switch
                {
                    <= 10 => 27.0, // Locked
                    <= 25 => 27.0, // Bronzo
                    <= 49 => 27.0, // Argento
                    _     => 27.0  // Oro
                };
            }
        }

        // Dimensione dinamica per l'icona Xbox (Più compatta rispetto alle altre: 24px - 26px)
        [JsonIgnore]
        public double XboxIconSize
        {
            get
            {
                if (!Platforms.TryGetValue("Xbox", out var p)) return 28.0;
                if (p.IsCompleted100 || p.Percentage >= 100) return 28.0; // Mythic (se mostrato sotto)
                return p.Percentage switch
                {
                    <= 10 => 28.0, // Locked
                    <= 49 => 28.0, // Silver
                    _     => 28.0  // Diamond
                };
            }
        }

        // Visibilità delle 3 icone sotto la cover:
        // Appaiono SEMPRE (anche se al 100%), tranne per l'unica piattaforma che sta già mostrando l'overlay sopra!
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

        // Proprietà per il singolo Overlay dinamico sulla cover
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

        [JsonIgnore]
        public string SteamTooltip => Platforms.TryGetValue("Steam", out var p) ? $"Steam: {p.Percentage}% ({p.Unlocked}/{p.Total})" : "";

        [JsonIgnore]
        public string PsTooltip => Platforms.TryGetValue("PlayStation", out var p) ? $"PlayStation: {p.Percentage}% ({p.Unlocked}/{p.Total}) {p.ExtraInfo}" : "";

        [JsonIgnore]
        public string XboxTooltip => Platforms.TryGetValue("Xbox", out var p) ? $"Xbox: {p.Percentage}% ({p.ExtraInfo})" : "";

        // --- NUOVE PROPRIETÀ PER MOD / EMULATORI ---
        public string? ModLaunchCommand { get; set; }        // Percorso all'eseguibile alternativo (es. ModEngine o Emulatore)
        public string ModDisplayName { get; set; } = "Play Modded"; // Testo del tooltip o nome alternativo

        [JsonIgnore]
        public Visibility ModButtonVisibility => !string.IsNullOrEmpty(ModLaunchCommand) ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public bool IsRunnable => ActionType == GameActionType.Executable || !string.IsNullOrEmpty(ModLaunchCommand);

        public bool HasCustomCover { get; set; } = false; // Se true, il Sync non sovrascriverà mai LocalCoverPath!

        [System.Text.Json.Serialization.JsonIgnore]
        private BitmapImage? _coverImageCache;

        public void ReloadCoverImage()
        {
            _coverImageCache = null;
        }

        [System.Text.Json.Serialization.JsonIgnore]
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
                    // Legge tutti i byte in RAM e chiude istantaneamente il file su disco (Zero File Lock!)
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

        // --- LOGICA ICONE RANGO ACHIEVEMENT / TROFEI (SOTTO LA COVER) ---

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
                <= 10 => "ps_locked.png",   // 0% - 10%
                <= 25 => "ps_bronze.png",   // 11% - 25%
                <= 49 => "ps_silver.png",   // 26% - 49%
                _     => "ps_gold.png"      // 50% - 99%
            };
        }

        private static string GetXboxIconName(int pct, bool is100)
        {
            if (is100 || pct >= 100) return "xbox_mythic.png";
            return pct switch
            {
                <= 10 => "xbox_locked.png", // 0% - 10%
                <= 25 => "xbox_silver.png", // 11% - 25% (pronto se avrai xbox_bronze)
                <= 49 => "xbox_silver.png", // 26% - 49% (pronto se avrai xbox_silver)
                _     => "xbox_diamond.png"  // 50% - 99% (pronto se avrai xbox_gold)
            };
        }

        private static string GetSteamIconName(int pct, bool is100)
        {
            if (is100 || pct >= 100) return "steam_purple.png";
            return pct switch
            {
                <= 10 => "steam_locked.png", // 0% - 10%
                <= 25 => "steam_white.png", // 11% - 25%
                <= 49 => "steam_green.png", // 26% - 49%
                _     => "steam_red.png"  // 50% - 99%
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

        public bool IsCustomGame { get; set; } = false; // <--- Se true, la sync non toccherà mai il suo ActionType!

        // Utili per l'ordinamento (Order By)
        [JsonIgnore]
        public int TotalPlatinumsCount => Platforms.Values.Count(p => p.IsCompleted100 || p.Percentage >= 100);

        [JsonIgnore]
        public int MaxCompletionPercentage => Platforms.Values.Any() ? Platforms.Values.Max(p => p.Percentage) : 0;
    }

    public static class TitleNormalizer
    {
        private static readonly string[] SuffixesToRemove = new[]
        {
            @"-\s*Xbox Series X\|S",
            @"\(Xbox Series X\|S\)",
            @"\(Xbox One\)",
            @"Xbox One Edition",
            @"Xbox 360 Edition",
            @"Xbox 360",
            @"for Windows",
            @"per Android",
            @"-\s*Windows 10",
            @":\s*Save the World",
            @":\s*Edizione Cuore di Vuoto", // Hollow Knight: Voidheart Edition
            @":\s*Voidheart Edition",
            @":\s*Definitive Edition",
            @"Definitive Edition",
            @":\s*20\d\d Edition"
        };

        // Mappa per riportare in Inglese Originale i titoli che Xbox traduce in italiano
        private static readonly Dictionary<string, string> ItalianToEnglishFixes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Crash Bandicoot Trilogia N. Sane"] = "Crash Bandicoot N. Sane Trilogy",
            ["Injustice: Dei fra noi"] = "Injustice: Gods Among Us",
            ["SOUTH PARK: SCONTRI DI-RETTI"] = "South Park: The Fractured But Whole",
            ["La Mia Amica Peppa Pig"] = "My Friend Peppa Pig",
            ["Il Dominio Sui Mutanti"] = "Mutant Year Zero: Road to Eden",
            ["Le fantastiche avventure di Captain Spirit"] = "The Awesome Adventures of Captain Spirit"
        };

        public static (string normalizedKey, string cleanDisplayTitle) Normalize(string rawTitle)
        {
            if (string.IsNullOrWhiteSpace(rawTitle)) return ("unknown", "Unknown");

            string t = rawTitle.Trim()
                .Replace("®", "").Replace("™", "").Replace("©", "")
                .Replace("’", "'").Replace("‘", "'");

            t = Regex.Replace(t, @"^COD\s*:\s*", "Call of Duty: ", RegexOptions.IgnoreCase);

            foreach (var pattern in SuffixesToRemove)
                t = Regex.Replace(t, pattern, "", RegexOptions.IgnoreCase).Trim();

            t = t.TrimEnd('-', ':', ' ');

            // Se Xbox ha restituito un titolo tradotto in italiano, lo riportiamo al titolo originale inglese!
            if (ItalianToEnglishFixes.TryGetValue(t, out string? englishTitle))
            {
                t = englishTitle;
            }

            string key = Regex.Replace(t.ToLowerInvariant(), @"[^a-z0-9]", "");
            return (key, t);
        }
    }
}