using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameStartMenu.Library
{
    public class GameLibraryService
    {
        private readonly Dictionary<string, UnifiedGame> _library = new();
        private bool _isSyncingCloud = false;

        private readonly System.Timers.Timer _hourlySyncTimer;

        public AppConfig Config { get; private set; }
        public event Action<List<UnifiedGame>>? OnLibraryUpdated;

        public GameLibraryService()
        {
            Config = AppConfig.Load();
            ApplyOverlayPriority(Config.OverlayPriority);

            // Timer che controlla ogni 5 minuti se è passata un'ora dall'ultimo sync (attivo solo se SyncFrequency == "Hourly")
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

        // Restituisce i giochi già ordinati (Prima gli Eseguibili, poi i multipiattaforma, poi alfabetico)
        // Restituisce i giochi filtrati e ordinati in base alle scelte salvate in settings.json!
        private string _searchQuery = "";

        // Chiamato ad ogni lettera digitata nella barra di ricerca
        public void SetSearchQuery(string query)
        {
            _searchQuery = query?.Trim() ?? "";
            OnLibraryUpdated?.Invoke(GetSortedGames());
        }

        // Restituisce i giochi filtrati e ordinati (con gli ESEGUIBILI sempre in cima!)
        public List<UnifiedGame> GetSortedGames()
        {
            IEnumerable<UnifiedGame> query = _library.Values;

            // 1. FILTRO BARRA DI RICERCA (Istantaneo)
            if (!string.IsNullOrWhiteSpace(_searchQuery))
            {
                query = query.Where(g => g.DisplayTitle.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
            }

            // 2. FILTRO PIATTAFORMA / STATO (ShowFilter salvato in settings.json)
            query = Config.ShowFilter switch
            {
                "Executable" => query.Where(g => g.ActionType == GameActionType.Executable),
                "Steam" => query.Where(g => g.Platforms.ContainsKey("Steam")),
                "PlayStation" => query.Where(g => g.Platforms.ContainsKey("PlayStation")),
                "Xbox" => query.Where(g => g.Platforms.ContainsKey("Xbox")),
                "Completed100" => query.Where(g => g.TotalPlatinumsCount > 0),
                _ => query // "All"
            };

            // 3. REGOLA D'ORO: I giochi avviabili (Installati o con Mod/Emulatore) vanno SEMPRE in cima!
            var primarySort = query.OrderByDescending(g => g.IsRunnable);

            // 4. ORDINAMENTO SECONDARIO (OrderBy salvato in settings.json)
            query = Config.OrderBy switch
            {
                "Alphabetical" => primarySort.ThenBy(g => g.DisplayTitle),

                "Platinums" => primarySort.ThenByDescending(g => g.TotalPlatinumsCount)
                                              .ThenByDescending(g => g.MaxCompletionPercentage)
                                              .ThenBy(g => g.DisplayTitle),

                "CompletionPct" => primarySort.ThenByDescending(g => g.MaxCompletionPercentage)
                                              .ThenBy(g => g.DisplayTitle),

                _ => primarySort.ThenByDescending(g => g.Platforms.Count)
                                              .ThenBy(g => g.DisplayTitle) // "Default"
            };

            return query.ToList();
        }

        // Cambia filtro/ordinamento al volo, salva in settings.json e aggiorna la UI
        public void UpdateViewSettings(string showFilter, string orderBy)
        {
            Config.ShowFilter = showFilter;
            Config.OrderBy = orderBy;
            Config.Save();
            OnLibraryUpdated?.Invoke(GetSortedGames());
        }

        // Permette di aggiungere un gioco esterno (.exe o URL) senza che venga mai cancellato dalla sync!
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

            // Scarica automaticamente la cover 600x900 da Steam / SteamGridDB anche per il gioco esterno!
            await CoverCacheHelper.EnsureCoverCachedAsync(game, Config.SteamGridDbApiKey);

            SaveCache();
            OnLibraryUpdated?.Invoke(GetSortedGames());
        }

        // 1. Avvio istantaneo: legge la cache su disco + controlla i file .acf di Steam (impiega ~20ms totali!)
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

        // 2. Sincronizzazione Cloud (Eseguita solo se sono passati > 60 min o se forzata dall'utente)
        public async Task SyncCloudIfNeededAsync(bool force = false, bool isStartup = false)
        {
            if (_isSyncingCloud) return;

            if (!force)
            {
                // Se l'utente ha scelto "Manual", non sincronizza mai in automatico
                if (Config.SyncFrequency == "Manual")
                    return;

                // Se ha scelto "Hourly", controlla che siano passati almeno 60 minuti
                if (Config.SyncFrequency == "Hourly")
                {
                    bool isExpired = (DateTime.UtcNow - Config.LastCloudSyncUtc).TotalMinutes >= 60;
                    if (!isExpired && _library.Count > 0)
                        return;
                }
                // Se ha scelto "Startup", sincronizza all'avvio (isStartup == true), ma ignora il timer orario
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

        private void SaveCache()
        {
            Directory.CreateDirectory(AppConfig.AppDataFolder);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            File.WriteAllText(AppConfig.CacheFilePath, JsonSerializer.Serialize(GetSortedGames(), options));


        }

        // Aggiorna i parametri di lancio e/o la copertina personalizzata di un gioco esistente
        public void UpdateGameSettings(UnifiedGame game, string? launchCmd, string? modLaunchCmd, string? modDisplayName, string? newCoverFilePath = null)
        {
            game.LaunchCommand = string.IsNullOrWhiteSpace(launchCmd) ? null : launchCmd.Trim();
            game.ModLaunchCommand = string.IsNullOrWhiteSpace(modLaunchCmd) ? null : modLaunchCmd.Trim();
            game.ModDisplayName = string.IsNullOrWhiteSpace(modDisplayName) ? "Play Modded" : modDisplayName.Trim();

            game.ActionType = !string.IsNullOrEmpty(game.LaunchCommand) ? GameActionType.Executable : GameActionType.DisplayOnly;

            // Sostituisce direttamente la cover originale e forza il refresh dell'immagine
            if (!string.IsNullOrEmpty(newCoverFilePath))
            {
                SetCustomCover(game, newCoverFilePath);
            }

            SaveCache();
            OnLibraryUpdated?.Invoke(GetSortedGames());
        }

        // Aggiunge un nuovo gioco (con scelta se scaricare automaticamente la cover o usarne una locale)
        public async Task<UnifiedGame> AddCustomGameWithOptionsAsync(string title, string exePathOrUrl, bool autoDownloadCover, string? customCoverPath)
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

                // Svuota prima il riferimento in memoria
                game.ReloadCoverImage();

                // Se l'utente non sta selezionando lo stesso identico file già presente in covers:
                if (!string.Equals(Path.GetFullPath(sourceImagePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                {
                    // Rimuove eventuale vecchia cover con estensione diversa (.png vs .jpg)
                    if (!string.IsNullOrEmpty(game.LocalCoverPath) &&
                        File.Exists(game.LocalCoverPath) &&
                        !string.Equals(Path.GetFullPath(game.LocalCoverPath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(game.LocalCoverPath); } catch { }
                    }

                    // Legge i byte e sovrascrive direttamente il file originale senza conflitti di lock
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

        // Elimina definitivamente un gioco dalla libreria in memoria, dal JSON, cancella la cover e impedisce che riappaia al Sync!
        public void DeleteGame(UnifiedGame game)
        {
            if (game == null) return;

            // 1. Rimuove dal dizionario in memoria
            _library.Remove(game.NormalizedId);

            // 2. Se non era solo un gioco custom (o per sicurezza generale), lo aggiunge a HiddenGames
            // così Steam/PSN/Xbox non lo ricreano al prossimo Sync!
            if (!game.IsCustomGame || game.Platforms.Count > 0)
            {
                Config.HiddenGames.Add(game.NormalizedId);
                Config.Save();
            }

            // 3. Elimina il file della copertina dalla cache su disco
            if (!string.IsNullOrEmpty(game.LocalCoverPath) && File.Exists(game.LocalCoverPath))
            {
                try { File.Delete(game.LocalCoverPath); } catch { }
            }

            // 4. Salva subito library_cache.json e aggiorna la UI
            SaveCache();
            OnLibraryUpdated?.Invoke(GetSortedGames());
        }

        // Salva tutte le API Key e la frequenza di sincronizzazione
        public void SaveSyncSettings(string syncFrequency, string steamApiKey, string steamId64, string psnNpsso, string openXblKey, string steamGridDbKey)
        {
            Config.SyncFrequency = syncFrequency;
            Config.SteamApiKey = steamApiKey.Trim();
            Config.SteamId64 = steamId64.Trim();
            Config.PsnNpsso = psnNpsso.Trim();
            Config.OpenXblApiKey = openXblKey.Trim();
            Config.SteamGridDbApiKey = steamGridDbKey.Trim();
            Config.Save();
        }

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

        public void UpdateOverlayPrioritySetting(string priorityCsv)
        {
            ApplyOverlayPriority(priorityCsv);
            Config.Save();
            OnLibraryUpdated?.Invoke(GetSortedGames());
        }


    }
}