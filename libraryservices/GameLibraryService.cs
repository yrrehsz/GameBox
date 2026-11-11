using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameStartMenu.Library
{
    /// <summary>
    /// Core service managing the game library: aggregation across platforms, searching,
    /// sorting, background cloud synchronization, and local JSON caching.
    /// </summary>
    public class GameLibraryService
    {
        // =====================================================================
        // State & Events
        // =====================================================================

        private readonly Dictionary<string, UnifiedGame> _library = new();
        private bool _isSyncingCloud = false;
        private string _searchQuery = "";
        private readonly System.Timers.Timer _hourlySyncTimer;

        public AppConfig Config { get; private set; }

        /// <summary>Raised whenever games are loaded, filtered, sorted, or synchronized.</summary>
        public event Action<List<UnifiedGame>>? OnLibraryUpdated;

        // =====================================================================
        // Constructor & Timer Setup
        // =====================================================================

        public GameLibraryService()
        {
            Config = AppConfig.Load();
            ApplyOverlayPriority(Config.OverlayPriority);

            // Timer checking every 5 minutes if 60 minutes have elapsed since the last sync
            _hourlySyncTimer = new System.Timers.Timer(TimeSpan.FromMinutes(5).TotalMilliseconds);
            _hourlySyncTimer.Elapsed += async (s, e) =>
            {
                if (Config.SyncFrequency == "Hourly")
                {
                    await SyncCloudIfNeededAsync(force: false);
                }
            };
            _hourlySyncTimer.Start();
        }

        // =====================================================================
        // Search, Filter & Sort Queries
        // =====================================================================

        /// <summary>Updates the active search query filter and triggers a library update.</summary>
        public void SetSearchQuery(string query)
        {
            _searchQuery = query?.Trim() ?? "";
            OnLibraryUpdated?.Invoke(GetSortedGames());
        }

        /// <summary>
        /// Returns the game collection filtered by the search query and active ShowFilter,
        /// and sorted according to the active OrderBy preference with runnable games prioritized.
        /// </summary>
        public List<UnifiedGame> GetSortedGames()
        {
            IEnumerable<UnifiedGame> query = _library.Values;

            // 1. Search bar filter (instant)
            if (!string.IsNullOrWhiteSpace(_searchQuery))
            {
                query = query.Where(g => g.DisplayTitle.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
            }

            // 2. Platform / status filter (OR union across comma-separated tokens in ShowFilter)
            if (!string.IsNullOrWhiteSpace(Config.ShowFilter) &&
                !Config.ShowFilter.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                var activeFilters = Config.ShowFilter
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!activeFilters.Contains("All") && activeFilters.Count > 0)
                {
                    query = query.Where(g =>
                        (activeFilters.Contains("Executable")   && g.IsRunnable) ||
                        (activeFilters.Contains("Steam")        && g.Platforms.ContainsKey("Steam")) ||
                        (activeFilters.Contains("PlayStation")  && g.Platforms.ContainsKey("PlayStation")) ||
                        (activeFilters.Contains("Xbox")         && g.Platforms.ContainsKey("Xbox")) ||
                        (activeFilters.Contains("Completed100") && g.TotalPlatinumsCount > 0));
                }
            }

            // 3. Primary rule: runnable games (executable or with secondary launcher) are sorted first
            var primarySort = query.OrderByDescending(g => g.IsRunnable);

            // 4. Secondary sort (OrderBy preference)
            query = Config.OrderBy switch
            {
                "Alphabetical"  => primarySort.ThenBy(g => g.DisplayTitle),

                "Platinums"     => primarySort.ThenByDescending(g => g.TotalPlatinumsCount)
                                              .ThenByDescending(g => g.MaxCompletionPercentage)
                                              .ThenBy(g => g.DisplayTitle),

                "CompletionPct" => primarySort.ThenByDescending(g => g.MaxCompletionPercentage)
                                              .ThenBy(g => g.DisplayTitle),

                _               => primarySort.ThenByDescending(g => g.Platforms.Count)
                                              .ThenBy(g => g.DisplayTitle) // "Default"
            };

            return query.ToList();
        }

        /// <summary>Updates filter and sorting preferences on the fly, persists them, and refreshes UI.</summary>
        public void UpdateViewSettings(string showFilter, string orderBy)
        {
            Config.ShowFilter = showFilter;
            Config.OrderBy = orderBy;
            Config.Save();
            OnLibraryUpdated?.Invoke(GetSortedGames());
        }

        // =====================================================================
        // Fast Startup & Local Scanning
        // =====================================================================

        /// <summary>
        /// Fast startup routine: reads the on-disk cache then scans local Steam and Xbox installs (~20ms, no network).
        /// Heals custom games with configured local executables.
        /// </summary>
        public void LoadCacheAndLocalGames()
        {
            try
            {
                if (File.Exists(AppConfig.CacheFilePath))
                {
                    var cachedList = JsonSerializer.Deserialize<List<UnifiedGame>>(File.ReadAllText(AppConfig.CacheFilePath));
                    if (cachedList != null)
                    {
                        _library.Clear();
                        foreach (var g in cachedList)
                            _library[g.NormalizedId] = g;
                    }
                }
            }
            catch { }

            // Restore ActionType for any game that has a local launch command configured
            foreach (var g in _library.Values)
            {
                if (!string.IsNullOrEmpty(g.LaunchCommand))
                {
                    if (!g.LaunchCommand.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
                    {
                        g.IsCustomGame = true;
                        g.ActionType = GameActionType.Executable;
                    }
                }
                else if (g.IsCustomGame)
                {
                    g.ActionType = GameActionType.DisplayOnly;
                }
            }

            var installedSteamIds = PlatformProviders.SyncSteamLocal(_library);
            var installedXboxIds = PlatformProviders.SyncXboxLocal(_library);
            PlatformProviders.CleanupUninstalledGames(_library, installedSteamIds, installedXboxIds);

            PurgeHiddenGames();
            SaveCache();
            OnLibraryUpdated?.Invoke(GetSortedGames());
        }

        private void PurgeHiddenGames()
        {
            if (Config.HiddenGames == null || Config.HiddenGames.Count == 0) return;
            foreach (var hiddenId in Config.HiddenGames)
            {
                _library.Remove(hiddenId);
            }
        }

        // =====================================================================
        // Cloud Synchronization
        // =====================================================================

        /// <summary>
        /// Synchronizes cloud platforms (Steam, PlayStation, Xbox) if the configured interval has elapsed,
        /// or immediately if forced by user action.
        /// </summary>
        public async Task SyncCloudIfNeededAsync(bool force = false, bool isStartup = false)
        {
            if (_isSyncingCloud) return;

            if (!force)
            {
                if (Config.SyncFrequency == "Manual")
                    return;

                if (Config.SyncFrequency == "Hourly")
                {
                    bool isExpired = (DateTime.UtcNow - Config.LastCloudSyncUtc).TotalMinutes >= 60;
                    if (!isExpired && _library.Count > 0)
                        return;
                }
                else if (Config.SyncFrequency == "Startup" && !isStartup)
                {
                    return;
                }
            }

            _isSyncingCloud = true;
            try
            {
                var installedSteamIds = PlatformProviders.SyncSteamLocal(_library);
                var installedXboxIds = PlatformProviders.SyncXboxLocal(_library);
                PlatformProviders.CleanupUninstalledGames(_library, installedSteamIds, installedXboxIds);

                PurgeHiddenGames();

                await Task.WhenAll(
                    PlatformProviders.SyncSteamCloudAsync(_library, Config.SteamApiKey, Config.SteamId64, installedSteamIds),
                    PlatformProviders.SyncPlayStationAsync(_library, Config.PsnNpsso),
                    PlatformProviders.SyncXboxAsync(_library, Config.OpenXblApiKey)
                );

                PurgeHiddenGames();

                foreach (var game in _library.Values)
                {
                    if (game.HasCustomCover && !string.IsNullOrEmpty(game.LocalCoverPath) && File.Exists(game.LocalCoverPath))
                        continue;

                    await CoverCacheHelper.EnsureCoverCachedAsync(game, Config.SteamGridDbApiKey);
                }

                Config.LastCloudSyncUtc = DateTime.UtcNow;
                Config.Save();
                SaveCache();

                OnLibraryUpdated?.Invoke(GetSortedGames());
            }
            finally
            {
                _isSyncingCloud = false;
            }
        }

        // =====================================================================
        // Cache Persistence
        // =====================================================================

        private void SaveCache()
        {
            Directory.CreateDirectory(AppConfig.AppDataFolder);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            // Serialize the entire library so active filters never drop temporarily hidden items
            File.WriteAllText(AppConfig.CacheFilePath,
                JsonSerializer.Serialize(_library.Values.ToList(), options));
        }

        // =====================================================================
        // Custom Games & Cover Management
        // =====================================================================

        /// <summary>Adds a custom game pointing to a local executable or URL.</summary>
        public async Task AddCustomGameAsync(string title, string exePathOrUrl)
        {
            var (key, cleanTitle) = TitleNormalizer.Normalize(title);
            if (!_library.TryGetValue(key, out var game))
            {
                game = new UnifiedGame { NormalizedId = key, DisplayTitle = cleanTitle };
                _library[key] = game;
            }

            game.IsCustomGame = true;
            game.ActionType = GameActionType.Executable;
            game.LaunchCommand = exePathOrUrl;

            await CoverCacheHelper.EnsureCoverCachedAsync(game, Config.SteamGridDbApiKey);

            SaveCache();
            OnLibraryUpdated?.Invoke(GetSortedGames());
        }

        /// <summary>
        /// Adds a custom game with options for automatic cover downloading or a custom local artwork file.
        /// </summary>
        public async Task<UnifiedGame> AddCustomGameWithOptionsAsync(
            string title,
            string exePathOrUrl,
            bool autoDownloadCover,
            string? customCoverPath)
        {
            var (key, cleanTitle) = TitleNormalizer.Normalize(title);
            if (!_library.TryGetValue(key, out var game))
            {
                game = new UnifiedGame { NormalizedId = key, DisplayTitle = cleanTitle };
                _library[key] = game;
            }

            game.IsCustomGame = true;
            game.ActionType = GameActionType.Executable;
            game.LaunchCommand = exePathOrUrl;

            if (!string.IsNullOrEmpty(customCoverPath) && File.Exists(customCoverPath))
            {
                SetCustomCover(game, customCoverPath);
            }
            else if (autoDownloadCover)
            {
                await CoverCacheHelper.EnsureCoverCachedAsync(game, Config.SteamGridDbApiKey);
                game.ReloadCoverImage();
            }

            Config.HiddenGames.Remove(key);
            Config.Save();

            SaveCache();
            OnLibraryUpdated?.Invoke(GetSortedGames());
            return game;
        }

        /// <summary>Updates launch commands, secondary launcher settings, and cover artwork for an existing game.</summary>
        public void UpdateGameSettings(
            UnifiedGame game,
            string? launchCmd,
            string? modLaunchCmd,
            string? modDisplayName,
            string? newCoverFilePath = null)
        {
            game.LaunchCommand = string.IsNullOrWhiteSpace(launchCmd) ? null : launchCmd.Trim();
            game.ModLaunchCommand = string.IsNullOrWhiteSpace(modLaunchCmd) ? null : modLaunchCmd.Trim();
            game.ModDisplayName = string.IsNullOrWhiteSpace(modDisplayName) ? "Play Modded" : modDisplayName.Trim();

            game.ActionType = !string.IsNullOrEmpty(game.LaunchCommand)
                ? GameActionType.Executable
                : GameActionType.DisplayOnly;

            if (!string.IsNullOrEmpty(newCoverFilePath))
            {
                SetCustomCover(game, newCoverFilePath);
            }

            SaveCache();
            OnLibraryUpdated?.Invoke(GetSortedGames());
        }

        private void SetCustomCover(UnifiedGame game, string sourceImagePath)
        {
            if (string.IsNullOrEmpty(sourceImagePath) || !File.Exists(sourceImagePath))
                return;

            try
            {
                string coversDir = Path.Combine(AppConfig.AppDataFolder, "covers");
                Directory.CreateDirectory(coversDir);

                string ext = Path.GetExtension(sourceImagePath).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                string destPath = Path.Combine(coversDir, $"{game.NormalizedId}{ext}");

                game.ReloadCoverImage();

                if (!string.Equals(Path.GetFullPath(sourceImagePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(game.LocalCoverPath) &&
                        File.Exists(game.LocalCoverPath) &&
                        !string.Equals(Path.GetFullPath(game.LocalCoverPath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(game.LocalCoverPath); } catch { }
                    }

                    byte[] bytes = File.ReadAllBytes(sourceImagePath);
                    File.WriteAllBytes(destPath, bytes);
                }

                game.LocalCoverPath = destPath;
                game.HasCustomCover = true;
                game.ReloadCoverImage();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Cover Update Error] {ex.Message}");
            }
        }

        /// <summary>
        /// Permanently removes a game from memory, deletes its cached cover file, and adds it
        /// to HiddenGames so cloud sync will not resurrect it.
        /// </summary>
        public void DeleteGame(UnifiedGame game)
        {
            if (game == null) return;

            _library.Remove(game.NormalizedId);

            if (!game.IsCustomGame || game.Platforms.Count > 0)
            {
                Config.HiddenGames.Add(game.NormalizedId);
                Config.Save();
            }

            if (!string.IsNullOrEmpty(game.LocalCoverPath) && File.Exists(game.LocalCoverPath))
            {
                try { File.Delete(game.LocalCoverPath); } catch { }
            }

            SaveCache();
            OnLibraryUpdated?.Invoke(GetSortedGames());
        }

        // =====================================================================
        // Settings & Overlay Configuration
        // =====================================================================

        /// <summary>Saves all cloud API keys and sync frequency to settings.json.</summary>
        public void SaveSyncSettings(
            string syncFrequency,
            string steamApiKey,
            string steamId64,
            string psnNpsso,
            string openXblKey,
            string steamGridDbKey)
        {
            Config.SyncFrequency = syncFrequency;
            Config.SteamApiKey = steamApiKey.Trim();
            Config.SteamId64 = steamId64.Trim();
            Config.PsnNpsso = psnNpsso.Trim();
            Config.OpenXblApiKey = openXblKey.Trim();
            Config.SteamGridDbApiKey = steamGridDbKey.Trim();
            Config.Save();
        }

        /// <summary>Applies the overlay priority CSV string to UnifiedGame.GlobalOverlayPriority.</summary>
        public void ApplyOverlayPriority(string priorityCsv)
        {
            Config.OverlayPriority = string.IsNullOrWhiteSpace(priorityCsv) ? "Xbox,PlayStation,Steam" : priorityCsv;

            if (Config.OverlayPriority.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                UnifiedGame.GlobalOverlayPriority = Array.Empty<string>();
            }
            else
            {
                UnifiedGame.GlobalOverlayPriority = Config.OverlayPriority
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }

        /// <summary>Updates the overlay priority preference, saves configuration, and refreshes UI.</summary>
        public void UpdateOverlayPrioritySetting(string priorityCsv)
        {
            ApplyOverlayPriority(priorityCsv);
            Config.Save();
            OnLibraryUpdated?.Invoke(GetSortedGames());
        }
    }
}
