using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn;
using ServerSync;

namespace TerrainMistile;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInDependency(Main.ModGuid)]
public class TerrainMistilePlugin : BaseUnityPlugin
{
    internal const string ModName = "TerrainMistile";
    internal const string ModVersion = "1.0.2";
    internal const string Author = "sighsorry";
    internal const string DefaultDisplayName = "Earth Warden";
    private const string ModGUID = $"{Author}.{ModName}";
    private static string ConfigFileName = $"{ModGUID}.cfg";
    private static string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
    private static string SpawnRulesFileName = $"{ModName}.yml";
    internal static string SpawnRulesFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + SpawnRulesFileName;
    internal static string ConnectionError = "";
    private readonly Harmony _harmony = new(ModGUID);
    public static readonly ManualLogSource TerrainMistileLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
    private static readonly ConfigSync ConfigSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };
    internal static string DisplayName
    {
        get
        {
            string value = (_displayName?.Value ?? "").Trim();
            return value.Length == 0 ? DefaultDisplayName : value;
        }
    }
    private FileSystemWatcher _watcher = null!;
    private FileSystemWatcher _spawnRulesWatcher = null!;
    private readonly object _reloadLock = new();
    private DateTime _lastConfigReloadTime;
    private DateTime _lastSpawnRulesReloadTime;
    private string? _lastConfigFileText;
    private string? _lastSpawnRulesYamlText;
    private const long RELOAD_DELAY = 10000000; // One second

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public void Awake()
    {
        bool saveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;

        _serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
        _ = ConfigSync.AddLockingConfigEntry(_serverConfigLocked);
        _displayName = config("2 - Display", "Display Name", DefaultDisplayName, "In-game name shown to players for TerrainMistile.");

        TerrainMistileSpawnRules.Initialize(TerrainMistileLogger);
        TerrainMistileSpawnRules.EnsureFileExists(SpawnRulesFileFullPath);
        SpawnRulesYaml = new CustomSyncedValue<string>(ConfigSync, "SpawnRulesYaml", string.Empty);
        SpawnRulesYaml.ValueChanged += OnSyncedSpawnRulesYamlChanged;
        ConfigSync.SourceOfTruthChanged += OnSourceOfTruthChanged;
        TerrainMistileSpawnRules.LoadYamlText(File.ReadAllText(SpawnRulesFileFullPath), "local fallback");

        TerrainMistilePrefab.RegisterPrefabHook();

        Assembly assembly = Assembly.GetExecutingAssembly();
        _harmony.PatchAll(assembly);
        TerrainMistileExternalTerrainCompat.Initialize(TerrainMistileLogger, _harmony);
        SetupWatcher();
        if (ConfigSync.IsSourceOfTruth)
        {
            PushLocalSpawnRulesYamlToSync();
        }

        Config.Save();
        _lastConfigFileText = ReadFileTextIfExists(ConfigFileFullPath);
        _lastSpawnRulesYamlText = ReadFileTextIfExists(SpawnRulesFileFullPath);
        if (saveOnSet)
        {
            Config.SaveOnConfigSet = saveOnSet;
        }
    }

    private void OnDestroy()
    {
        TerrainMistilePrefab.UnregisterPrefabHook();
        SpawnRulesYaml.ValueChanged -= OnSyncedSpawnRulesYamlChanged;
        ConfigSync.SourceOfTruthChanged -= OnSourceOfTruthChanged;
        SaveWithRespectToConfigSet();
        _watcher?.Dispose();
        _spawnRulesWatcher?.Dispose();
    }

    private void Update()
    {
        TerrainMistileSystem.UpdateResetEffectRpcRegistration();
        TerrainMistileSystem.UpdateProtectedTerrainAreaSync();
        TerrainMistileExternalTerrainCompat.Update();
        TerrainMistileSystem.UpdatePersistentTerrainSpawns();
    }

    private void SetupWatcher()
    {
        _watcher = new FileSystemWatcher(Paths.ConfigPath, ConfigFileName);
        _watcher.Changed += ReadConfigValues;
        _watcher.Created += ReadConfigValues;
        _watcher.Renamed += ReadConfigValues;
        _watcher.IncludeSubdirectories = true;
        _watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _watcher.EnableRaisingEvents = true;

        _spawnRulesWatcher = new FileSystemWatcher(Paths.ConfigPath, SpawnRulesFileName);
        _spawnRulesWatcher.Changed += ReadSpawnRulesValues;
        _spawnRulesWatcher.Created += ReadSpawnRulesValues;
        _spawnRulesWatcher.Renamed += ReadSpawnRulesValues;
        _spawnRulesWatcher.IncludeSubdirectories = false;
        _spawnRulesWatcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _spawnRulesWatcher.EnableRaisingEvents = true;
    }

    private void ReadConfigValues(object sender, FileSystemEventArgs e)
    {
        DateTime now = DateTime.Now;
        long time = now.Ticks - _lastConfigReloadTime.Ticks;
        if (time < RELOAD_DELAY)
        {
            return;
        }

        lock (_reloadLock)
        {
            if (!File.Exists(ConfigFileFullPath))
            {
                TerrainMistileLogger.LogWarning("Config file does not exist. Skipping reload.");
                return;
            }

            try
            {
                string configFileText = File.ReadAllText(ConfigFileFullPath);
                if (string.Equals(_lastConfigFileText, configFileText, StringComparison.Ordinal))
                {
                    return;
                }

                SaveWithRespectToConfigSet(true);
                _lastConfigFileText = ReadFileTextIfExists(ConfigFileFullPath);
                TerrainMistileLogger.LogInfo("Configuration reload complete.");
            }
            catch (Exception ex)
            {
                TerrainMistileLogger.LogError($"Error reloading configuration: {ex.Message}");
            }
        }

        _lastConfigReloadTime = now;
    }

    private void ReadSpawnRulesValues(object sender, FileSystemEventArgs e)
    {
        DateTime now = DateTime.Now;
        long time = now.Ticks - _lastSpawnRulesReloadTime.Ticks;
        if (time < RELOAD_DELAY)
        {
            return;
        }

        lock (_reloadLock)
        {
            if (!File.Exists(SpawnRulesFileFullPath))
            {
                TerrainMistileLogger.LogWarning("TerrainMistile spawn rules YAML does not exist. Skipping reload.");
                return;
            }

            if (!ConfigSync.IsSourceOfTruth)
            {
                return;
            }

            try
            {
                PushLocalSpawnRulesYamlToSync();
                TerrainMistileLogger.LogInfo("TerrainMistile spawn rules YAML reload complete.");
            }
            catch (Exception ex)
            {
                TerrainMistileLogger.LogError($"Error reloading TerrainMistile spawn rules YAML: {ex.Message}");
            }
        }

        _lastSpawnRulesReloadTime = now;
    }

    private void PushLocalSpawnRulesYamlToSync()
    {
        TerrainMistileSpawnRules.EnsureFileExists(SpawnRulesFileFullPath);
        string yaml = File.ReadAllText(SpawnRulesFileFullPath);
        if (string.Equals(_lastSpawnRulesYamlText, yaml, StringComparison.Ordinal) &&
            string.Equals(SpawnRulesYaml.Value ?? string.Empty, yaml, StringComparison.Ordinal))
        {
            return;
        }

        if (TerrainMistileSpawnRules.LoadYamlText(yaml, "local file"))
        {
            _lastSpawnRulesYamlText = yaml;
            if (!string.Equals(SpawnRulesYaml.Value ?? string.Empty, yaml, StringComparison.Ordinal))
            {
                SpawnRulesYaml.Value = yaml;
            }
        }
    }

    private void OnSyncedSpawnRulesYamlChanged()
    {
        string yaml = SpawnRulesYaml.Value ?? string.Empty;
        if (string.Equals(_lastSpawnRulesYamlText, yaml, StringComparison.Ordinal))
        {
            return;
        }

        if (TerrainMistileSpawnRules.LoadYamlText(yaml, ConfigSync.IsSourceOfTruth ? "local sync" : "server sync"))
        {
            _lastSpawnRulesYamlText = yaml;
        }
    }

    private void OnSourceOfTruthChanged(bool isSourceOfTruth)
    {
        if (isSourceOfTruth)
        {
            PushLocalSpawnRulesYamlToSync();
            return;
        }

        string yaml = SpawnRulesYaml.Value ?? string.Empty;
        if (!string.Equals(_lastSpawnRulesYamlText, yaml, StringComparison.Ordinal) &&
            TerrainMistileSpawnRules.LoadYamlText(yaml, "server sync"))
        {
            _lastSpawnRulesYamlText = yaml;
        }
    }

    private static string? ReadFileTextIfExists(string path)
    {
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private void SaveWithRespectToConfigSet(bool reload = false)
    {
        bool originalSaveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        if (reload)
            Config.Reload();
        Config.Save();
        if (originalSaveOnSet)
        {
            Config.SaveOnConfigSet = originalSaveOnSet;
        }
    }


    #region ConfigOptions

    private static ConfigEntry<Toggle> _serverConfigLocked = null!;
    private static ConfigEntry<string> _displayName = null!;
    internal static CustomSyncedValue<string> SpawnRulesYaml = null!;

    private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
    {
        ConfigDescription extendedDescription = new(description.Description + (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"), description.AcceptableValues, description.Tags);
        ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);
        //var configEntry = Config.Bind(group, name, value, description);

        SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
        syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

        return configEntry;
    }

    private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true)
    {
        return config(group, name, value, new ConfigDescription(description), synchronizedSetting);
    }

    #endregion
}
