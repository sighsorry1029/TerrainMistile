using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TerrainMistile;

internal static class TerrainMistileSpawnRules
{
    private const float DefaultInterval = 60f;
    private const float DefaultPlayerSearchRadius = 32f;
    private const float DefaultSpawnChance = 0.25f;
    private const float DefaultMaxDeformationSpawnChanceBonus = 0.25f;
    private const bool DefaultPerPlayerSpawn = true;
    private const int DefaultPlayerBaseValue = 1;
    private const float DefaultBaseCheckRadius = 24f;
    private const int DefaultMaxSpawn = 3;
    private const float DefaultSpawnRadiusMin = 16f;
    private const float DefaultSpawnRadiusMax = 32f;
    private const float DefaultSpawnAltitude = 8f;
    private const float DefaultResetRadius = 8f;
    private const float DefaultHealth = 1f;
    internal const string DefaultVisualColorHex = "#45FF5A";
    private static readonly Color FallbackVisualColor = new(0.270f, 1.000f, 0.353f, 1f);
    private static readonly string[] DefaultPlayerBasePrefabNames =
    {
        "ashwood_bed",
        "bed",
        "blackforge",
        "blastfurnace",
        "BogWitch_Fire_Pit",
        "bonfire",
        "charcoal_kiln",
        "charred_shieldgenerator",
        "dverger_guardstone",
        "eitrrefinery",
        "fermenter",
        "fire_pit",
        "fire_pit_haldor",
        "fire_pit_hildir",
        "fire_pit_iron",
        "forge",
        "guard_stone",
        "hearth",
        "piece_artisanstation",
        "piece_bed02",
        "piece_brazierceiling01",
        "piece_brazierfloor01",
        "piece_brazierfloor02",
        "piece_groundtorch",
        "piece_groundtorch_blue",
        "piece_groundtorch_green",
        "piece_groundtorch_mist",
        "piece_groundtorch_wood",
        "piece_magetable",
        "piece_oven",
        "piece_shieldgenerator",
        "piece_spinningwheel",
        "piece_stonecutter",
        "piece_walltorch",
        "piece_workbench",
        "portal",
        "portal_stone",
        "portal_wood",
        "smelter",
        "windmill"
    };

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly Dictionary<int, TerrainMistileBiomeSpawnRule> BiomeRules = new();
    private static TerrainMistileBiomeSpawnRule _defaultRule = CreateDefaultRule();
    private static HashSet<string> _playerBasePrefabNames = CreateDefaultPlayerBasePrefabNames();
    private static float _maxPlayerSearchRadius = DefaultPlayerSearchRadius;
    private static float _maxResetRadius = DefaultResetRadius;
    private static bool _hasEnabledRules = true;
    private static ManualLogSource? _logger;

    internal static float MaxPlayerSearchRadius => _maxPlayerSearchRadius;
    internal static float MaxResetRadius => _maxResetRadius;
    internal static bool HasEnabledRules => _hasEnabledRules;
    internal static Color DefaultVisualColor => _defaultRule.VisualColor;
    internal static bool IsPlayerBasePrefabName(string prefabName) => _playerBasePrefabNames.Contains(prefabName);
    internal static TerrainMistileBiomeSpawnRule DefaultRule => _defaultRule;

    internal static List<string> GetPlayerBasePrefabNamesSnapshot()
    {
        return new List<string>(_playerBasePrefabNames);
    }

    internal static List<int> GetConfiguredBiomeKeysSnapshot()
    {
        return new List<int>(BiomeRules.Keys);
    }

    internal static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
    }

    internal static void EnsureFileExists(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, BuildDefaultYaml());
    }

    internal static bool LoadYamlText(string yaml, string source)
    {
        if (!TryParseYaml(
                yaml,
                out TerrainMistileBiomeSpawnRule defaultRule,
                out Dictionary<int, TerrainMistileBiomeSpawnRule> parsedRules,
                out HashSet<string> playerBasePrefabNames,
                out string error))
        {
            _logger?.LogError($"Failed to parse TerrainMistile spawn rules from {source}: {error}");
            return false;
        }

        _defaultRule = defaultRule;
        _playerBasePrefabNames = playerBasePrefabNames;
        BiomeRules.Clear();
        foreach (KeyValuePair<int, TerrainMistileBiomeSpawnRule> entry in parsedRules)
        {
            BiomeRules[entry.Key] = entry.Value;
        }

        RecalculateRuntimeState();
        _logger?.LogInfo($"Loaded TerrainMistile spawn rules from {source}. Default={_defaultRule}; overrides={BiomeRules.Count}; playerBasePrefabs={_playerBasePrefabNames.Count}; enabled={_hasEnabledRules}.");
        TerrainMistileSystem.ClearSpawnUnitRollState();
        TerrainMistilePrefab.RefreshRegisteredPrefabVisuals();
        return true;
    }

    internal static bool TryGetEnabledRule(int biome, out TerrainMistileBiomeSpawnRule rule)
    {
        rule = GetRule(biome);
        return biome != 0 && rule.Enabled;
    }

    internal static TerrainMistileBiomeSpawnRule GetRule(int biome)
    {
        return BiomeRules.TryGetValue(biome, out TerrainMistileBiomeSpawnRule rule) ? rule : _defaultRule;
    }

    internal static int GetBiomeKey(Vector3 point)
    {
        return (int)Heightmap.FindBiome(point);
    }

    internal static string GetBiomeName(int biome)
    {
        if (TerrainMistileExpandWorldDataBiomeCompat.TryGetDisplayName(biome, out string displayName))
        {
            return displayName;
        }

        string enumName = ((Heightmap.Biome)biome).ToString();
        return string.IsNullOrWhiteSpace(enumName) ? biome.ToString(CultureInfo.InvariantCulture) : enumName;
    }

    private static bool TryParseYaml(
        string yaml,
        out TerrainMistileBiomeSpawnRule defaultRule,
        out Dictionary<int, TerrainMistileBiomeSpawnRule> parsedRules,
        out HashSet<string> playerBasePrefabNames,
        out string error)
    {
        defaultRule = CreateDefaultRule();
        parsedRules = new Dictionary<int, TerrainMistileBiomeSpawnRule>();
        playerBasePrefabNames = CreateDefaultPlayerBasePrefabNames();
        error = "";

        if (string.IsNullOrWhiteSpace(yaml))
        {
            return true;
        }

        Dictionary<object, object?>? file;
        try
        {
            file = Deserializer.Deserialize<Dictionary<object, object?>>(yaml);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (file == null)
        {
            return true;
        }

        TerrainMistileSpawnRuleValues? defaults = null;
        foreach (KeyValuePair<object, object?> entry in file)
        {
            string key = GetYamlKey(entry.Key);
            if (key.Equals("defaults", StringComparison.OrdinalIgnoreCase))
            {
                defaults = CreateValues(entry.Value);
                continue;
            }

            if (key.Equals("playerBasePrefabs", StringComparison.OrdinalIgnoreCase))
            {
                playerBasePrefabNames = CreatePlayerBasePrefabNames(entry.Value);
            }
        }

        if (defaults != null)
        {
            defaultRule = CreateRule(defaults, defaultRule);
        }

        foreach (KeyValuePair<object, object?> entry in file)
        {
            string key = GetYamlKey(entry.Key);
            if (key.Equals("defaults", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("playerBasePrefabs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string biomeName = NormalizeBiomeName(key);
            if (biomeName.Length == 0 || biomeName.Equals(nameof(Heightmap.Biome.None), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryResolveBiomeKey(key, out int biome))
            {
                _logger?.LogWarning($"Ignoring unknown biome '{key}' in TerrainMistile spawn rules.");
                continue;
            }

            parsedRules[biome] = CreateRule(CreateValues(entry.Value), defaultRule);
        }

        return true;
    }

    private static string GetYamlKey(object? key)
    {
        return key?.ToString()?.Trim() ?? "";
    }

    private static TerrainMistileSpawnRuleValues? CreateValues(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is not IDictionary<object, object?> map)
        {
            return null;
        }

        TerrainMistileSpawnRuleValues values = new();
        foreach (KeyValuePair<object, object?> entry in map)
        {
            string key = NormalizeFieldName(GetYamlKey(entry.Key));
            switch (key)
            {
                case "interval":
                    if (TryGetFloat(entry.Value, out float interval))
                    {
                        values.Interval = interval;
                    }

                    break;
                case "playersearchradius":
                    if (TryGetFloat(entry.Value, out float playerSearchRadius))
                    {
                        values.PlayerSearchRadius = playerSearchRadius;
                    }

                    break;
                case "spawnchance":
                    if (TryGetFloat(entry.Value, out float spawnChance))
                    {
                        values.SpawnChance = spawnChance;
                    }

                    break;
                case "maxdeformationspawnchancebonus":
                    if (TryGetFloat(entry.Value, out float maxDeformationSpawnChanceBonus))
                    {
                        values.MaxDeformationSpawnChanceBonus = maxDeformationSpawnChanceBonus;
                    }

                    break;
                case "maxspawn":
                    if (TryGetInt(entry.Value, out int maxSpawn))
                    {
                        values.MaxSpawn = maxSpawn;
                    }

                    break;
                case "perplayerspawn":
                    if (TryGetBool(entry.Value, out bool perPlayerSpawn))
                    {
                        values.PerPlayerSpawn = perPlayerSpawn;
                    }

                    break;
                case "playerbasevalue":
                    if (TryGetInt(entry.Value, out int playerBaseValue))
                    {
                        values.PlayerBaseValue = playerBaseValue;
                    }

                    break;
                case "basecheckradius":
                    if (TryGetFloat(entry.Value, out float baseCheckRadius))
                    {
                        values.BaseCheckRadius = baseCheckRadius;
                    }

                    break;
                case "spawnradius":
                    values.SpawnRadius = entry.Value?.ToString();
                    break;
                case "spawnaltitude":
                    if (TryGetFloat(entry.Value, out float spawnAltitude))
                    {
                        values.SpawnAltitude = spawnAltitude;
                    }

                    break;
                case "resetradius":
                    if (TryGetFloat(entry.Value, out float resetRadius))
                    {
                        values.ResetRadius = resetRadius;
                    }

                    break;
                case "health":
                    if (TryGetFloat(entry.Value, out float health))
                    {
                        values.Health = health;
                    }

                    break;
                case "visualcolor":
                    values.VisualColor = entry.Value?.ToString();
                    break;
                default:
                    _logger?.LogWarning($"Ignoring unknown TerrainMistile spawn rule field '{GetYamlKey(entry.Key)}'.");
                    break;
            }
        }

        return values;
    }

    private static HashSet<string> CreatePlayerBasePrefabNames(object? value)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        if (value is string scalar)
        {
            AddPlayerBasePrefabNames(scalar, names);
            return names;
        }

        if (value is IEnumerable<object?> sequence)
        {
            foreach (object? item in sequence)
            {
                AddPlayerBasePrefabNames(item?.ToString(), names);
            }

            return names;
        }

        _logger?.LogWarning("Ignoring invalid playerBasePrefabs value in TerrainMistile spawn rules. Use a YAML list or comma-separated string.");
        return names;
    }

    private static HashSet<string> CreateDefaultPlayerBasePrefabNames()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string prefabName in DefaultPlayerBasePrefabNames)
        {
            AddPlayerBasePrefabNames(prefabName, names);
        }

        return names;
    }

    private static void AddPlayerBasePrefabNames(string? value, HashSet<string> names)
    {
        string? normalized = value?.Trim();
        if (normalized == null || normalized.Length == 0)
        {
            return;
        }

        foreach (string part in normalized.Split(','))
        {
            string prefabName = part.Trim();
            if (prefabName.Length > 0)
            {
                names.Add(prefabName);
            }
        }
    }

    private static string NormalizeFieldName(string value)
    {
        return value.Trim().Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
    }

    private static bool TryGetFloat(object? value, out float result)
    {
        switch (value)
        {
            case float floatValue:
                result = floatValue;
                return true;
            case double doubleValue:
                result = (float)doubleValue;
                return true;
            case decimal decimalValue:
                result = (float)decimalValue;
                return true;
            case int intValue:
                result = intValue;
                return true;
            case long longValue:
                result = longValue;
                return true;
            case string stringValue:
                return TryParseFloat(stringValue, out result);
            default:
                result = 0f;
                return false;
        }
    }

    private static bool TryGetInt(object? value, out int result)
    {
        switch (value)
        {
            case int intValue:
                result = intValue;
                return true;
            case long longValue:
                result = (int)longValue;
                return true;
            case float floatValue:
                result = Mathf.RoundToInt(floatValue);
                return true;
            case double doubleValue:
                result = (int)Math.Round(doubleValue);
                return true;
            case string stringValue:
                return int.TryParse(stringValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryGetBool(object? value, out bool result)
    {
        switch (value)
        {
            case bool boolValue:
                result = boolValue;
                return true;
            case string stringValue:
                return bool.TryParse(stringValue.Trim(), out result);
            default:
                result = false;
                return false;
        }
    }

    private static bool TryResolveBiomeKey(string value, out int biome)
    {
        string raw = value.Trim();
        string normalized = NormalizeBiomeName(raw);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out biome))
        {
            return true;
        }

        if (uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint unsignedBiome))
        {
            biome = unchecked((int)unsignedBiome);
            return true;
        }

        if (Enum.TryParse(normalized, ignoreCase: true, out Heightmap.Biome vanillaBiome))
        {
            biome = (int)vanillaBiome;
            return true;
        }

        if (TerrainMistileExpandWorldDataBiomeCompat.TryGetBiome(raw, out biome))
        {
            return true;
        }

        return !string.Equals(raw, normalized, StringComparison.Ordinal) &&
               TerrainMistileExpandWorldDataBiomeCompat.TryGetBiome(normalized, out biome);
    }

    private static TerrainMistileBiomeSpawnRule CreateRule(TerrainMistileSpawnRuleValues? values, TerrainMistileBiomeSpawnRule fallback)
    {
        if (values == null)
        {
            return fallback;
        }

        float interval = values.Interval ?? fallback.Interval;
        float playerSearchRadius = values.PlayerSearchRadius ?? fallback.PlayerSearchRadius;
        float spawnChance = values.SpawnChance ?? fallback.SpawnChance;
        float maxDeformationSpawnChanceBonus = values.MaxDeformationSpawnChanceBonus ?? fallback.MaxDeformationSpawnChanceBonus;
        bool perPlayerSpawn = values.PerPlayerSpawn ?? fallback.PerPlayerSpawn;
        int playerBaseValue = values.PlayerBaseValue ?? fallback.PlayerBaseValue;
        float baseCheckRadius = values.BaseCheckRadius ?? fallback.BaseCheckRadius;
        int maxSpawn = values.MaxSpawn ?? fallback.MaxSpawn;
        float spawnRadiusMin = fallback.SpawnRadiusMin;
        float spawnRadiusMax = fallback.SpawnRadiusMax;
        if (values.SpawnRadius is { } spawnRadiusValue &&
            !string.IsNullOrWhiteSpace(spawnRadiusValue) &&
            !TryParseSpawnRadius(spawnRadiusValue, out spawnRadiusMin, out spawnRadiusMax))
        {
            _logger?.LogWarning($"Ignoring invalid spawnRadius '{spawnRadiusValue}' in TerrainMistile spawn rules. Use a number like '24' or a range like '16~32'.");
            spawnRadiusMin = fallback.SpawnRadiusMin;
            spawnRadiusMax = fallback.SpawnRadiusMax;
        }

        float spawnAltitude = values.SpawnAltitude ?? fallback.SpawnAltitude;
        float resetRadius = values.ResetRadius ?? fallback.ResetRadius;
        float health = values.Health ?? fallback.Health;
        Color visualColor = fallback.VisualColor;
        if (values.VisualColor is { } visualColorValue && !string.IsNullOrWhiteSpace(visualColorValue))
        {
            if (TryParseVisualColor(visualColorValue, out Color parsedColor))
            {
                visualColor = parsedColor;
            }
            else
            {
                _logger?.LogWarning($"Ignoring invalid visualColor '{visualColorValue}' in TerrainMistile spawn rules. Use an HTML hex color like '#45FF5A' or '45FF5A'.");
            }
        }

        interval = Mathf.Clamp(interval, 0f, 3600f);
        playerSearchRadius = Mathf.Clamp(playerSearchRadius, 0f, 512f);
        spawnChance = Mathf.Clamp01(spawnChance);
        maxDeformationSpawnChanceBonus = Mathf.Clamp01(maxDeformationSpawnChanceBonus);
        playerBaseValue = Mathf.Clamp(playerBaseValue, 0, 10);
        baseCheckRadius = Mathf.Clamp(baseCheckRadius, 0f, 128f);
        maxSpawn = Mathf.Clamp(maxSpawn, 0, 50);
        spawnRadiusMin = Mathf.Clamp(spawnRadiusMin, 1f, 256f);
        spawnRadiusMax = Mathf.Clamp(spawnRadiusMax, 1f, 256f);
        spawnAltitude = Mathf.Clamp(spawnAltitude, 1f, 64f);
        resetRadius = Mathf.Clamp(resetRadius, 1f, 64f);
        health = Mathf.Clamp(health, 1f, 10000f);
        if (spawnRadiusMin > spawnRadiusMax)
        {
            (spawnRadiusMin, spawnRadiusMax) = (spawnRadiusMax, spawnRadiusMin);
        }

        return new TerrainMistileBiomeSpawnRule(
            interval,
            playerSearchRadius,
            spawnChance,
            maxDeformationSpawnChanceBonus,
            perPlayerSpawn,
            playerBaseValue,
            baseCheckRadius,
            maxSpawn,
            spawnRadiusMin,
            spawnRadiusMax,
            spawnAltitude,
            resetRadius,
            health,
            visualColor);
    }

    private static void RecalculateRuntimeState()
    {
        _maxPlayerSearchRadius = _defaultRule.Enabled ? _defaultRule.PlayerSearchRadius : 0f;
        _maxResetRadius = _defaultRule.ResetRadius;
        _hasEnabledRules = _defaultRule.Enabled;

        foreach (TerrainMistileBiomeSpawnRule rule in BiomeRules.Values)
        {
            _maxResetRadius = Mathf.Max(_maxResetRadius, rule.ResetRadius);
            if (!rule.Enabled)
            {
                continue;
            }

            _hasEnabledRules = true;
            _maxPlayerSearchRadius = Mathf.Max(_maxPlayerSearchRadius, rule.PlayerSearchRadius);
        }
    }

    private static TerrainMistileBiomeSpawnRule CreateDefaultRule()
    {
        return new TerrainMistileBiomeSpawnRule(
            DefaultInterval,
            DefaultPlayerSearchRadius,
            DefaultSpawnChance,
            DefaultMaxDeformationSpawnChanceBonus,
            DefaultPerPlayerSpawn,
            DefaultPlayerBaseValue,
            DefaultBaseCheckRadius,
            DefaultMaxSpawn,
            DefaultSpawnRadiusMin,
            DefaultSpawnRadiusMax,
            DefaultSpawnAltitude,
            DefaultResetRadius,
            DefaultHealth,
            FallbackVisualColor);
    }

    private static bool TryParseSpawnRadius(string value, out float minRadius, out float maxRadius)
    {
        minRadius = 0f;
        maxRadius = 0f;

        string[] parts = value.Split('~');
        if (parts.Length == 1)
        {
            if (!TryParseFloat(parts[0], out minRadius))
            {
                return false;
            }

            maxRadius = minRadius;
            return true;
        }

        if (parts.Length != 2 ||
            !TryParseFloat(parts[0], out minRadius) ||
            !TryParseFloat(parts[1], out maxRadius))
        {
            return false;
        }

        return true;
    }

    private static bool TryParseFloat(string value, out float result)
    {
        return float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParseVisualColor(string value, out Color color)
    {
        string normalized = value.Trim();
        if (normalized.Length > 0 && normalized[0] != '#')
        {
            normalized = "#" + normalized;
        }

        return ColorUtility.TryParseHtmlString(normalized, out color);
    }

    private static string NormalizeBiomeName(string value)
    {
        return value.Trim().Replace(" ", "").Replace("_", "").Replace("-", "");
    }

    private static string BuildDefaultPlayerBasePrefabsYaml()
    {
        StringBuilder builder = new();
        builder.AppendLine("# TerrainMistile base prefabs used by playerBaseValue. Only instances with ZDO longs.creator != 0 are counted.");
        builder.AppendLine("playerBasePrefabs:");
        foreach (string prefabName in DefaultPlayerBasePrefabNames)
        {
            builder.Append("  - ").AppendLine(prefabName);
        }

        return builder.ToString();
    }

    private static string BuildDefaultYaml()
    {
        return
            "# Expand World Data custom biomes can use their custom biome name or numeric biome value.\n" +
            "# defaults and playerBasePrefabs are reserved. Every other top-level key is treated as a biome rule.\n" +
            "\n" +
            "defaults:\n" +
            "  interval: 60 # Seconds between spawn rolls for one 32m terrain unit. 0 disables that biome.\n" +
            "  playerSearchRadius: 32 # Players within this horizontal radius of a changed terrain unit activate its rolls.\n" +
            "  spawnChance: 0.25 # Base chance used when the unit interval is ready and at least one player is nearby.\n" +
            "  maxDeformationSpawnChanceBonus: 0.25 # Added to spawnChance when the largest height deformation in the 32m terrain unit reaches the 8m cap. Scales linearly from 0m to 8m.\n" +
            "  maxSpawn: 3 # Maximum active TerrainMistiles with targets within 32m of the target. 0 disables that biome.\n" +
            "  perPlayerSpawn: true # If true, one successful roll can spawn up to one TerrainMistile per nearby player, capped by maxSpawn and available targets.\n" +
            "  playerBaseValue: 1 # playerBaseValue N skips spawn checks when at least N unique listed player-placed base prefab types are within baseCheckRadius meters horizontally of the changed terrain. 0 disables the PlayerBase check.\n" +
            "  baseCheckRadius: 24 # Horizontal radius used by playerBaseValue to count listed player-placed base prefab types.\n" +
            "  spawnRadius: 16~32 # Horizontal spawn distance from the selected nearby player. Use 24 for fixed distance or 16~32 for a random range.\n" +
            "  spawnAltitude: 8 # Height above solid ground where TerrainMistile spawns.\n" +
            "  resetRadius: 8 # Radius of terrain height and paint reset when TerrainMistile detonates.\n" +
            "  health: 1 # Maximum and current health applied when TerrainMistile spawns. Biome rules can override it.\n" +
            $"  visualColor: \"{DefaultVisualColorHex}\" # HTML hex color used for TerrainMistile flames, sparks, and light. Biome rules can override it.\n" +
            "Meadows:\n" +
            "  interval: 120\n" +
            "  spawnChance: 0.1\n" +
            "  resetRadius: 4\n" +
            "  visualColor: \"#7CFF6B\"\n" +
            "BlackForest:\n" +
            "  interval: 120\n" +
            "  resetRadius: 6\n" +
            "  visualColor: \"#2ED36F\"\n" +
            "Swamp:\n" +
            "  playerBaseValue: 2\n" +
            "  visualColor: \"#8FBF3F\"\n" +
            "Mountain:\n" +
            "  interval: 120\n" +
            "  playerBaseValue: 2\n" +
            "  resetRadius: 6\n" +
            "  visualColor: \"#8FE8FF\"\n" +
            "Plains:\n" +
            "  interval: 120\n" +
            "  playerBaseValue: 3\n" +
            "  resetRadius: 6\n" +
            "  visualColor: \"#FFD15C\"\n" +
            "Mistlands:\n" +
            "  interval: 120\n" +
            "  playerBaseValue: 3\n" +
            "  resetRadius: 6\n" +
            "  visualColor: \"#B58CFF\"\n" +
            "AshLands:\n" +
            "  playerBaseValue: 4\n" +
            "  visualColor: \"#FF5A2E\"\n" +
            "DeepNorth:\n" +
            "  playerBaseValue: 4\n" +
            "  visualColor: \"#BFEFFF\"\n" +
            "Ocean:\n" +
            "  visualColor: \"#3EA7FF\"\n" +
            "\n" +
            BuildDefaultPlayerBasePrefabsYaml();
    }

    private sealed class TerrainMistileSpawnRuleValues
    {
        public float? Interval { get; set; }
        public float? PlayerSearchRadius { get; set; }
        public float? SpawnChance { get; set; }
        public float? MaxDeformationSpawnChanceBonus { get; set; }
        public int? MaxSpawn { get; set; }
        public bool? PerPlayerSpawn { get; set; }
        public int? PlayerBaseValue { get; set; }
        public float? BaseCheckRadius { get; set; }
        public string? SpawnRadius { get; set; }
        public float? SpawnAltitude { get; set; }
        public float? ResetRadius { get; set; }
        public float? Health { get; set; }
        public string? VisualColor { get; set; }
    }
}

internal static class TerrainMistileExpandWorldDataBiomeCompat
{
    private const int FirstCustomBiomeBase = 512;

    private static readonly Dictionary<string, int> OriginalBiomes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["None"] = 0,
        ["Meadows"] = 1,
        ["Swamp"] = 2,
        ["Mountain"] = 4,
        ["BlackForest"] = 8,
        ["Plains"] = 16,
        ["AshLands"] = 32,
        ["DeepNorth"] = 64,
        ["Ocean"] = 256,
        ["Mistlands"] = 512
    };

    private static readonly IDeserializer ExpandWorldDataBiomeDeserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly Dictionary<string, int> FileNameToBiome = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string> FileBiomeToName = new();
    private static bool _fileMappingLoaded;
    private static bool _reflectionMethodsResolved;
    private static MethodInfo? _tryGetBiomeMethod;
    private static MethodInfo? _tryGetDisplayNameMethod;

    internal static bool TryGetBiome(string name, out int biome)
    {
        biome = 0;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (TryGetBiomeFromExpandWorldData(name.Trim(), out biome))
        {
            return true;
        }

        EnsureFileMappingLoaded();
        return FileNameToBiome.TryGetValue(name.Trim(), out biome) ||
               FileNameToBiome.TryGetValue(NormalizeName(name), out biome);
    }

    internal static bool TryGetDisplayName(int biome, out string name)
    {
        if (TryGetDisplayNameFromExpandWorldData(biome, out name))
        {
            return true;
        }

        EnsureFileMappingLoaded();
        return FileBiomeToName.TryGetValue(biome, out name);
    }

    private static bool TryGetBiomeFromExpandWorldData(string name, out int biome)
    {
        biome = 0;
        EnsureReflectionMethods();
        if (_tryGetBiomeMethod == null)
        {
            return false;
        }

        ParameterInfo[] parameters = _tryGetBiomeMethod.GetParameters();
        object[] args = { name, Enum.ToObject(parameters[1].ParameterType.GetElementType() ?? parameters[1].ParameterType, 0) };
        try
        {
            if (_tryGetBiomeMethod.Invoke(null, args) is bool result && result)
            {
                biome = Convert.ToInt32(args[1], CultureInfo.InvariantCulture);
                return true;
            }
        }
        catch
        {
            _tryGetBiomeMethod = null;
        }

        return false;
    }

    private static bool TryGetDisplayNameFromExpandWorldData(int biome, out string name)
    {
        name = "";
        EnsureReflectionMethods();
        if (_tryGetDisplayNameMethod == null)
        {
            return false;
        }

        ParameterInfo[] parameters = _tryGetDisplayNameMethod.GetParameters();
        Type biomeType = parameters[0].ParameterType;
        object[] args = { Enum.ToObject(biomeType, biome), "" };
        try
        {
            if (_tryGetDisplayNameMethod.Invoke(null, args) is bool result && result && args[1] is string displayName && !string.IsNullOrWhiteSpace(displayName))
            {
                name = displayName;
                return true;
            }
        }
        catch
        {
            _tryGetDisplayNameMethod = null;
        }

        return false;
    }

    private static void EnsureReflectionMethods()
    {
        if (_reflectionMethodsResolved)
        {
            return;
        }

        _reflectionMethodsResolved = true;

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            AssemblyName assemblyName;
            try
            {
                assemblyName = assembly.GetName();
            }
            catch
            {
                continue;
            }

            if (!string.Equals(assemblyName.Name, "ExpandWorldData", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Type? biomeManager = assembly.GetType("ExpandWorldData.BiomeManager", throwOnError: false);
            if (biomeManager == null)
            {
                continue;
            }

            _tryGetBiomeMethod ??= biomeManager.GetMethod("TryGetBiome", BindingFlags.Public | BindingFlags.Static);
            _tryGetDisplayNameMethod ??= biomeManager.GetMethod("TryGetDisplayName", BindingFlags.Public | BindingFlags.Static);
            return;
        }
    }

    private static void EnsureFileMappingLoaded()
    {
        if (_fileMappingLoaded)
        {
            return;
        }

        _fileMappingLoaded = true;
        foreach (KeyValuePair<string, int> biome in OriginalBiomes)
        {
            AddFileBiomeName(biome.Key, biome.Value);
        }

        string directory = Path.Combine(Paths.ConfigPath, "expand_world");
        if (!Directory.Exists(directory))
        {
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "expand_biomes*.yaml");
        }
        catch
        {
            return;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        int nextBiomeBase = FirstCustomBiomeBase;
        foreach (string file in files)
        {
            List<ExpandWorldDataBiomeYaml>? entries;
            try
            {
                entries = ExpandWorldDataBiomeDeserializer.Deserialize<List<ExpandWorldDataBiomeYaml>>(File.ReadAllText(file));
            }
            catch
            {
                continue;
            }

            if (entries == null)
            {
                continue;
            }

            foreach (ExpandWorldDataBiomeYaml entry in entries)
            {
                string biomeName = entry.Biome.Trim();
                if (biomeName.Length == 0 || FileNameToBiome.ContainsKey(biomeName))
                {
                    continue;
                }

                nextBiomeBase = NextBiome(nextBiomeBase);
                AddFileBiomeName(biomeName, nextBiomeBase);
            }
        }
    }

    private static int NextBiome(int biome)
    {
        uint value = unchecked((uint)biome);
        return unchecked((int)(value switch
        {
            128u => 128u,
            2147483648u => 128u,
            _ => 2u * value
        }));
    }

    private static void AddFileBiomeName(string name, int biome)
    {
        FileNameToBiome[name] = biome;
        string normalized = NormalizeName(name);
        if (!string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase))
        {
            FileNameToBiome[normalized] = biome;
        }

        FileBiomeToName[biome] = name;
    }

    private static string NormalizeName(string value)
    {
        return value.Trim().Replace(" ", "").Replace("_", "").Replace("-", "");
    }

    private sealed class ExpandWorldDataBiomeYaml
    {
        public string Biome { get; set; } = "";
    }
}

internal readonly struct TerrainMistileBiomeSpawnRule
{
    public TerrainMistileBiomeSpawnRule(
        float interval,
        float playerSearchRadius,
        float spawnChance,
        float maxDeformationSpawnChanceBonus,
        bool perPlayerSpawn,
        int playerBaseValue,
        float baseCheckRadius,
        int maxSpawn,
        float spawnRadiusMin,
        float spawnRadiusMax,
        float spawnAltitude,
        float resetRadius,
        float health,
        Color visualColor)
    {
        Interval = interval;
        PlayerSearchRadius = playerSearchRadius;
        SpawnChance = spawnChance;
        MaxDeformationSpawnChanceBonus = maxDeformationSpawnChanceBonus;
        PerPlayerSpawn = perPlayerSpawn;
        PlayerBaseValue = playerBaseValue;
        BaseCheckRadius = baseCheckRadius;
        MaxSpawn = maxSpawn;
        SpawnRadiusMin = spawnRadiusMin;
        SpawnRadiusMax = spawnRadiusMax;
        SpawnAltitude = spawnAltitude;
        ResetRadius = resetRadius;
        Health = health;
        VisualColor = visualColor;
    }

    public float Interval { get; }
    public float PlayerSearchRadius { get; }
    public float SpawnChance { get; }
    public float MaxDeformationSpawnChanceBonus { get; }
    public bool PerPlayerSpawn { get; }
    public int PlayerBaseValue { get; }
    public float BaseCheckRadius { get; }
    public int MaxSpawn { get; }
    public float SpawnRadiusMin { get; }
    public float SpawnRadiusMax { get; }
    public float SpawnAltitude { get; }
    public float ResetRadius { get; }
    public float Health { get; }
    public Color VisualColor { get; }
    public bool Enabled => Interval > 0f && PlayerSearchRadius > 0f && (SpawnChance > 0f || MaxDeformationSpawnChanceBonus > 0f) && MaxSpawn > 0;

    public float GetEffectiveSpawnChance(float deformationPressure)
    {
        return Mathf.Clamp01(SpawnChance + MaxDeformationSpawnChanceBonus * Mathf.Clamp01(deformationPressure));
    }

    public override string ToString()
    {
        return $"interval={Interval:0.##}, playerSearchRadius={PlayerSearchRadius:0.##}, spawnChance={SpawnChance:0.###}, maxDeformationSpawnChanceBonus={MaxDeformationSpawnChanceBonus:0.###}, maxSpawn={MaxSpawn}, perPlayerSpawn={PerPlayerSpawn}, playerBaseValue={PlayerBaseValue}, baseCheckRadius={BaseCheckRadius:0.##}, spawnRadius={SpawnRadiusMin:0.##}~{SpawnRadiusMax:0.##}, resetRadius={ResetRadius:0.##}, health={Health:0.##}, visualColor=#{ColorUtility.ToHtmlStringRGB(VisualColor)}";
    }
}
