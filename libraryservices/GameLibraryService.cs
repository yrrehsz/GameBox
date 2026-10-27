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

        public AppConfig Config { get; private set; }
        public event Action<List<UnifiedGame>>? OnLibraryUpdated;

        public GameLibraryService()
        {
            Config = AppConfig.Load();
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
                "Executable"   => query.Where(g => g.ActionType == GameActionType.Executable),
                "Steam"        => query.Where(g => g.Platforms.ContainsKey("Steam")),
                "PlayStation"  => query.Where(g => g.Platforms.ContainsKey("PlayStation")),
                "Xbox"         => query.Where(g => g.Platforms.ContainsKey("Xbox")),
                "Completed100" => query.Where(g => g.TotalPlatinumsCount > 0),
                _              => query // "All"
            };

            // 3. REGOLA D'ORO: I giochi avviabili (Installati o con Mod/Emulatore) vanno SEMPRE in cima!
            var primarySort = query.OrderByDescending(g => g.IsRunnable);

            // 4. ORDINAMENTO SECONDARIO (OrderBy salvato in settings.json)
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
                        foreach (var g in cachedList)
                            _library[g.NormalizedId] = g;
                    }
                }
            }
            catch { }

            PlatformProviders.SyncSteamLocal(_library);
            SaveCache();
            OnLibraryUpdated?.Invoke(GetSortedGames());
        }

        // 2. Sincronizzazione Cloud (Eseguita solo se sono passati > 60 min o se forzata dall'utente)
        public async Task SyncCloudIfNeededAsync(bool force = false)
        {
            if (_isSyncingCloud) return;

            bool isExpired = (DateTime.UtcNow - Config.LastCloudSyncUtc).TotalMinutes >= 60;
            if (!force && !isExpired && _library.Count > 0)
                return;

            _isSyncingCloud = true;
            try
            {
                var installedSteamIds = PlatformProviders.SyncSteamLocal(_library);

                // Esegue le 3 sincronizzazioni Cloud contemporaneamente!
                await Task.WhenAll(
                    PlatformProviders.SyncSteamCloudAsync(_library, Config.SteamApiKey, Config.SteamId64, installedSteamIds),
                    PlatformProviders.SyncPlayStationAsync(_library, Config.PsnNpsso),
                    PlatformProviders.SyncXboxAsync(_library, Config.OpenXblApiKey)
                );

                foreach (var game in _library.Values)
                {
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

        

        
    }
}