using CounterStrikeSharp.API.Core;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;

namespace CS2AutoUpdater
{
    public partial class CS2AutoUpdater : BasePlugin
    {
        public override string ModuleName => "CS2AutoUpdater";
        public override string ModuleDescription => "CS2 plugins to check for game updates from steam API.";
        public override string ModuleVersion => "0.4";
        public override string ModuleAuthor => "mzmasterzz";

        private const string SteamApiEndpoint =
            "https://api.steampowered.com/ISteamApps/UpToDateCheck/v0001/?appid=730&version={0}";

        private static Dictionary<int, bool> playersNotified = new();
        private static ConVar? svVisibleMaxPlayers;
        private static double updateFoundTime;
        private static bool isServerLoading;
        private static bool restartRequired;
        private static bool updateAvailable;
        private static int requiredVersion;

        public required PluginConfig Config { get; set; }

        public CS2AutoUpdater()
        {
            Config = new PluginConfig();
        }

        /// <summary>
        /// Inital plugin load.
        /// Register events
        /// AddTimer for checking game version on an interval
        /// </summary>
        /// <param name="hotReload"></param>
        public override void Load(bool hotReload)
        {
            base.Load(hotReload);

            svVisibleMaxPlayers = ConVar.Find("sv_visiblemaxplayers");

            RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
            RegisterListener<Listeners.OnGameServerSteamAPIActivated>(() => Logger.LogInformation("Checking for updates..."));
            RegisterListener<Listeners.OnServerHibernationUpdate>((bool isHibernating) =>
            { if (isHibernating) Logger.LogInformation("'sv_hibernate_when_empty' ConVar is enabled. This plugin might not work as expected."); });
            RegisterListener<Listeners.OnClientConnected>(OnClientConnected);
            RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
            RegisterListener<Listeners.OnMapStart>(OnMapStart);
            RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

            AddTimer(Config.UpdateCheckInterval, CheckServerVersion, TimerFlags.REPEAT);
        }

        /// <summary>
        /// Unload events for the plugin, free memory
        /// </summary>
        /// <param name="hotReload"></param>
        public override void Unload(bool hotReload) => Dispose();

        /// <summary>
        /// OnMapStart reset players dictionnary
        /// </summary>
        /// <param name="mapName"></param>
        private static void OnMapStart(string mapName)
        {
            playersNotified.Clear();
            isServerLoading = false;
        }

        /// <summary>
        /// Shutdown on map end
        /// </summary>
        private void OnMapEnd()
        {
            if (restartRequired && Config.ShutdownOnMapChangeIfPendingUpdate)
                ShutdownServer();

            isServerLoading = true;
        }

        /// <summary>
        /// When active player connects, add it to dict of players
        /// </summary>
        /// <param name="playerSlot"></param>
        private static void OnClientConnected(int playerSlot)
        {
            CCSPlayerController player = Utilities.GetPlayerFromSlot(playerSlot);
            if (player.IsValid && !player.IsBot && !player.IsHLTV)
                playersNotified.Add(playerSlot, false);
        }

        /// <summary>
        /// remove player from dict when disconnecting
        /// </summary>
        /// <param name="playerSlot"></param>
        private static void OnClientDisconnect(int playerSlot) => playersNotified.Remove(playerSlot);

        /// <summary>
        /// Check for update, if udpate needed we start shutdown
        /// </summary>
        private async void CheckServerVersion()
        {
            try
            {
                if (restartRequired || !await IsUpdateAvailable()) return;

                Server.NextFrame(ManageServerUpdate);
            }
            catch (Exception ex)
            {
                Logger.LogError($"An error occurred checking the server for updates: {ex.Message}");
            }
        }

        /// <summary>
        /// notify player that the server will shutdown, if full return before shutdown
        /// </summary>
        private void ManageServerUpdate()
        {
            if (!updateAvailable)
            {
                updateFoundTime = Server.CurrentTime;
                updateAvailable = true;
                Logger.LogInformation($"New Counter-Strike 2 update released (Version: {requiredVersion}). The server is preparing for a shutdown.");
            }

            List<CCSPlayerController> players = GetCurrentPlayers();

            if (isServerLoading || !ShouldShutdownServer(players.Count)) return;

            players.ForEach(NotifyPlayerAboutUpdate);
            players.ForEach(controller => playersNotified[controller.Slot] = true);

            AddTimer(players.Count <= Config.MinPlayersInstantShutdown ? 1 : Config.ShutdownDelay,
                PrepareServerShutdown,
                Config.ShutdownOnMapChangeIfPendingUpdate ? TimerFlags.STOP_ON_MAPCHANGE : 0);

            restartRequired = true;
        }

        /// <summary>
        /// if server is full, don't instantly shutdown server, otherwise shutdown.
        /// </summary>
        /// <param name="playerCount"></param>
        /// <returns>Bool if we shutdown now or later</returns>
        private bool ShouldShutdownServer(int playerCount)
        {
            int maxPlayers = svVisibleMaxPlayers?.GetPrimitiveValue<int>() ?? Server.MaxPlayers;
            return (float)playerCount / maxPlayers < Config.MinPlayerPercentageShutdownAllowed ||
                   Config.MinPlayersInstantShutdown >= playerCount;
        }

        /// <summary>
        /// On player spawn, if  is active and not spectating, notify about update.
        /// </summary>
        /// <param name="event"></param>
        /// <param name="info"></param>
        /// <returns></returns>
        private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
        {
            if (!updateAvailable) return HookResult.Continue;

            CCSPlayerController player = @event.Userid;
            if (!player.IsValid || player.IsBot || player.TeamNum <= (byte)CsTeam.Spectator) return HookResult.Continue;
            if (playersNotified.TryGetValue(player.Slot, out bool notified) && notified) return HookResult.Continue;

            playersNotified[player.Slot] = true;
            Server.NextFrame(() => NotifyPlayerAboutUpdate(player));

            return HookResult.Continue;
        }

        /// <summary>
        /// Notify directly to player with remaining time
        /// </summary>
        /// <param name="player"></param>
        private void NotifyPlayerAboutUpdate(CCSPlayerController player)
        {
            int remainingTime = Math.Max(1, Config.ShutdownDelay - (int)(Server.CurrentTime - updateFoundTime));
            string timeUnitLabel = remainingTime >= 60 ? "minute" : "second";
            string pluralSuffix = remainingTime > 120 || (remainingTime < 60 && remainingTime != 1)
                ? $"s"
                : string.Empty;

            string timeToRestart = $"{(remainingTime >= 60 ? remainingTime / 60 : remainingTime)} {timeUnitLabel}{pluralSuffix}";
            player.PrintToChat($" [{{green}}CS2AutoUpdater{{default}}] New Counter - Strike 2 update released(Version: {requiredVersion}) the server will restart in {timeToRestart}\r\n");
        }

        /// <summary>
        /// Check for game update, call steam API
        /// </summary>
        /// <returns>
        /// return UpToDate if update is needed
        /// </returns>
        private async Task<bool> IsUpdateAvailable()
        {
            string patchVersion = await GetSteamInfPatchVersion();
            if (string.IsNullOrWhiteSpace(patchVersion))
            {
                Logger.LogError("The current patch version of Counter-Strike 2 could not be retrieved. This server will not be checked for updates.");
                return false;
            }

            using HttpClient client = new();
            var response = await client.GetAsync(string.Format(SteamApiEndpoint, patchVersion));

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning($"Steam HTTP request failed with status code: {response.StatusCode}");
                return false;
            }

            var updateCheckResponse = await response.Content.ReadFromJsonAsync<UpToDateCheckResponse>();
            requiredVersion = updateCheckResponse?.Response?.RequiredVersion ?? 0;

            return updateCheckResponse?.Response?.Success == true && updateCheckResponse?.Response?.UpToDate == false;
        }

        /// <summary>
        /// Retrieve current game version from steam.inf in game folders
        /// </summary>
        /// <returns>
        /// return game version formatted using PatchVersionRegex
        /// </returns>
        private async Task<string> GetSteamInfPatchVersion()
        {
            string steamInfPath = Path.Combine(Server.GameDirectory, "csgo", "steam.inf");
            if (!File.Exists(steamInfPath))
            {
                Logger.LogError($"The 'steam.inf' file was not found in the root directory of Counter - Strike 2.Path: {steamInfPath}");
                return string.Empty;
            }

            try
            {
                string content = await File.ReadAllTextAsync(steamInfPath);
                var match = PatchVersionRegex().Match(content);
                if (match.Success) return match.Groups[1].Value;

                Logger.LogError($"The 'PatchVersion' key could not be located in the steam.inf file.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"An error occurred while reading the 'steam.inf' file: {ex.Message}");
            }

            return string.Empty;
        }

        /// <summary>
        /// Prepare the shutdown, kick all active players from the server with a message.
        /// </summary>
        private void PrepareServerShutdown()
        {
            var players = GetCurrentPlayers();

            foreach (var player in players)
            {
                if (player.Connected is PlayerConnectedState.PlayerConnected or
                    PlayerConnectedState.PlayerConnecting or
                    PlayerConnectedState.PlayerReconnecting)
                {
                    Server.ExecuteCommand($"kickid {player.UserId} Due to the game update (Version: {requiredVersion}), the server is now restarting.");
                }
            }

            AddTimer(1, ShutdownServer, TimerFlags.STOP_ON_MAPCHANGE);
        }

        /// <summary>
        /// Shutdown the server by sending quit command
        /// </summary>
        private void ShutdownServer() => Server.ExecuteCommand("quit");

        /// <summary>
        /// Utility that returns the active player list
        /// </summary>
        /// <returns>
        /// List of players entity
        /// </returns>
        private List<CCSPlayerController> GetCurrentPlayers() => Utilities.GetPlayers()
            .Where(player => !player.IsBot)
            .ToList();

        /// <summary>
        /// </summary>
        /// <returns></returns>
        private static Regex PatchVersionRegex() => new(@"PatchVersion=(\d+)");
    }

    // Plugin config
    public sealed class PluginConfig : BasePluginConfig
    {
        [JsonPropertyName("ConfigVersion")]
        public override int Version { get; set; } = 2;

        [JsonPropertyName("UpdateCheckInterval")]
        public int UpdateCheckInterval { get; set; } = 1800;

        [JsonPropertyName("ShutdownDelay")]
        public int ShutdownDelay { get; set; } = 120;

        [JsonPropertyName("MinPlayersInstantShutdown")]
        public int MinPlayersInstantShutdown { get; set; } = 1;

        [JsonPropertyName("MinPlayerPercentageShutdownAllowed")]
        public float MinPlayerPercentageShutdownAllowed { get; set; } = 0.6f;

        [JsonPropertyName("ShutdownOnMapChangeIfPendingUpdate")]
        public bool ShutdownOnMapChangeIfPendingUpdate { get; set; } = true;
    }

    // Steam API json response mapper
    public class UpToDateCheckResponse
    {
        [JsonPropertyName("response")]
        public UpToDateCheck? Response { get; init; }

        public class UpToDateCheck
        {
            [JsonPropertyName("success")]
            public bool Success { get; set; }

            [JsonPropertyName("up_to_date")]
            public bool UpToDate { get; set; }

            [JsonPropertyName("version_is_listable")]
            public bool VersionIsListable { get; set; }

            [JsonPropertyName("required_version")]
            public int RequiredVersion { get; set; }

            [JsonPropertyName("message")]
            public string? Message { get; set; }
        }
    }

}

