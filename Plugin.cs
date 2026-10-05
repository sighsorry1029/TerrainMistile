using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;

namespace TerrainMistile;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class TerrainMistilePlugin : BaseUnityPlugin
{
    internal const string ModName = "TerrainMistile";
    internal const string ModVersion = "1.1.2";
    internal const string Author = "sighsorry";
    internal const string DisplayNameToken = "$terrainmistile_creature_name";
    internal const string EnforcerDisplayNameToken = "$terrainmistile_enforcer_name";
    internal const string CompendiumTopicToken = "$terrainmistile_compendium_topic";
    internal const string CheckCompendiumMessageToken = "$terrainmistile_message_check_compendium";
    private const string ModGUID = $"{Author}.{ModName}";
    private const string ConfigFileName = $"{ModGUID}.cfg";
    private const string SpawnRulesFileName = $"{ModName}.yml";
    private static readonly string ConfigFileFullPath = Path.Combine(Paths.ConfigPath, ConfigFileName);
    private static readonly string SpawnRulesFileFullPath = Path.Combine(Paths.ConfigPath, SpawnRulesFileName);
    private readonly Harmony _harmony = new(ModGUID);
    private bool _initialized;
    public static readonly ManualLogSource TerrainMistileLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
    private static readonly ConfigSync ConfigSync = new(ModGUID)
    {
        DisplayName = ModName,
        CurrentVersion = ModVersion,
        MinimumRequiredVersion = ModVersion,
        ModRequired = true
    };
    internal static bool SpawningEnabled => _terrainMistileEnabled?.Value != Toggle.Off;
    private FileSystemWatcher _watcher = null!;
    private FileSystemWatcher _spawnRulesWatcher = null!;
    private readonly object _reloadLock = new();
    private DateTime _lastConfigReloadTime;
    private DateTime _lastSpawnRulesReloadTime;
    private string? _lastConfigFileText;
    private string? _appliedSpawnRulesYamlText;
    private const long RELOAD_DELAY = 10000000; // One second

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public void Awake()
    {
        TerrainMistileLocalization.Load(TerrainMistileLogger);
        TerrainCompAccess.Initialize();
        bool saveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        try
        {
            _terrainMistileEnabled = config(
                "1 - General",
                "Enable TerrainMistile",
                Toggle.On,
                "Globally enables new TerrainMistile spawns. Existing TerrainMistiles are not removed when disabled.");
            _serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
            _ = ConfigSync.AddLockingConfigEntry(_serverConfigLocked);
            RemoveObsoleteDisplayNameConfig();

            TerrainMistileSpawnRules.Initialize(TerrainMistileLogger);
            TerrainMistileSpawnRules.EnsureFileExists(SpawnRulesFileFullPath);
            SpawnRulesYaml = new CustomSyncedValue<string>(ConfigSync, "SpawnRulesYaml", string.Empty);
            SpawnRulesYaml.ValueChanged += OnSyncedSpawnRulesYamlChanged;
            ConfigSync.SourceOfTruthChanged += OnSourceOfTruthChanged;
            ApplySpawnRulesYaml(File.ReadAllText(SpawnRulesFileFullPath), "local fallback");

            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);
            TerrainMistileLocalization.ApplyToCurrentLanguage();
            TerrainMistileExternalTerrainCompat.Initialize(TerrainMistileLogger, _harmony);
            SetupWatcher();
            if (ConfigSync.IsSourceOfTruth)
            {
                PushLocalSpawnRulesYamlToSync();
            }

            Config.Save();
            _lastConfigFileText = ReadFileTextIfExists(ConfigFileFullPath);
            _initialized = true;
        }
        catch
        {
            Cleanup();
            throw;
        }
        finally
        {
            Config.SaveOnConfigSet = saveOnSet;
        }
    }

    private void OnDestroy()
    {
        try
        {
            if (_initialized) ConfigPersistence.Save(Config);
        }
        finally
        {
            Cleanup();
        }
    }

    private void Cleanup()
    {
        _initialized = false;
        try
        {
            if (SpawnRulesYaml != null)
            {
                SpawnRulesYaml.ValueChanged -= OnSyncedSpawnRulesYamlChanged;
            }
            ConfigSync.SourceOfTruthChanged -= OnSourceOfTruthChanged;
            try
            {
                _harmony.UnpatchSelf();
            }
            finally
            {
                TerrainMistilePrefab.ReleasePrefabs();
            }
        }
        finally
        {
            try
            {
                _watcher?.Dispose();
            }
            finally
            {
                _spawnRulesWatcher?.Dispose();
            }
        }
    }

    private void Update()
    {
        if (!_initialized) return;
        TerrainMistileSystem.UpdateResetEffectRpcRegistration();
        TerrainMistileSystem.UpdateProtectedTerrainAreaSync();
        TerrainMistileExternalTerrainCompat.Update();
        if (TerrainMistileExternalTerrainCompat.ConsumeBiomeMappingRefreshRequest() &&
            _appliedSpawnRulesYamlText != null)
        {
            ApplySpawnRulesYaml(_appliedSpawnRulesYamlText, "Expand World Data biome refresh");
        }

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

                ConfigPersistence.Save(Config, reload: true);
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
        if (string.Equals(_appliedSpawnRulesYamlText, yaml, StringComparison.Ordinal) &&
            string.Equals(SpawnRulesYaml.Value ?? string.Empty, yaml, StringComparison.Ordinal))
        {
            return;
        }

        if (ApplySpawnRulesYaml(yaml, "local file"))
        {
            if (!string.Equals(SpawnRulesYaml.Value ?? string.Empty, yaml, StringComparison.Ordinal))
            {
                SpawnRulesYaml.Value = yaml;
            }
        }
    }

    private void OnSyncedSpawnRulesYamlChanged()
    {
        string yaml = SpawnRulesYaml.Value ?? string.Empty;
        if (string.Equals(_appliedSpawnRulesYamlText, yaml, StringComparison.Ordinal))
        {
            return;
        }

        ApplySpawnRulesYaml(yaml, ConfigSync.IsSourceOfTruth ? "local sync" : "server sync");
    }

    private void OnSourceOfTruthChanged(bool isSourceOfTruth)
    {
        if (isSourceOfTruth)
        {
            PushLocalSpawnRulesYamlToSync();
            return;
        }

        string yaml = SpawnRulesYaml.Value ?? string.Empty;
        if (!string.Equals(_appliedSpawnRulesYamlText, yaml, StringComparison.Ordinal))
        {
            ApplySpawnRulesYaml(yaml, "server sync");
        }
    }

    private bool ApplySpawnRulesYaml(string yaml, string source)
    {
        TerrainMistileExpandWorldDataBiomeCompat.InvalidateFileMapping();
        if (!TerrainMistileSpawnRules.LoadYamlText(yaml, source))
        {
            return false;
        }

        _appliedSpawnRulesYamlText = yaml;
        TerrainMistileSystem.InvalidateSpawnRuleState();
        TerrainMistilePrefab.RefreshRegisteredPrefabVisuals();
        return true;
    }

    private static string? ReadFileTextIfExists(string path)
    {
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    // Kept separate from plugin initialization so config I/O can run without Unity or ServerSync.
    private static class ConfigPersistence
    {
        public static void Save(ConfigFile config, bool reload = false)
        {
            bool originalSaveOnSet = config.SaveOnConfigSet;
            config.SaveOnConfigSet = false;
            try
            {
                if (reload)
                    config.Reload();
                config.Save();
            }
            finally
            {
                config.SaveOnConfigSet = originalSaveOnSet;
            }
        }
    }

    #region ConfigOptions

    private static ConfigEntry<Toggle> _terrainMistileEnabled = null!;
    private static ConfigEntry<Toggle> _serverConfigLocked = null!;
    private static CustomSyncedValue<string> SpawnRulesYaml = null!;

    private void RemoveObsoleteDisplayNameConfig()
    {
        ConfigDefinition definition = new("2 - Display", "Display Name");
        Config.Bind(definition, string.Empty);
        Config.Remove(definition);
    }

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
