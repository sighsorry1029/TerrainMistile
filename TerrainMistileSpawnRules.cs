using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
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
    internal const float MaximumResetRadius = 64f;
    internal const string DefaultVisualColorHex = "#45FF5A";
    private static readonly Color FallbackVisualColor = new(0x45 / 255f, 1f, 0x5A / 255f, 1f);
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

    internal static bool TryParseYaml(
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

        object? defaults = null;
        foreach (KeyValuePair<object, object?> entry in file)
        {
            string key = GetYamlKey(entry.Key);
            if (key.Equals("defaults", StringComparison.OrdinalIgnoreCase))
            {
                defaults = entry.Value;
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

            string biomeName = TerrainMistileExpandWorldDataBiomeCompat.NormalizeName(key);
            if (biomeName.Length == 0 || biomeName.Equals(nameof(Heightmap.Biome.None), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryResolveBiomeKey(key, out int biome))
            {
                _logger?.LogWarning($"Ignoring unknown biome '{key}' in TerrainMistile spawn rules.");
                continue;
            }

            if (biome == 0)
            {
                continue;
            }

            parsedRules[biome] = CreateRule(entry.Value, defaultRule);
        }

        return true;
    }

    private static string GetYamlKey(object? key)
    {
        return key?.ToString()?.Trim() ?? "";
    }

    private static TerrainMistileBiomeSpawnRule CreateRule(object? value, TerrainMistileBiomeSpawnRule fallback)
    {
        if (value is not IDictionary<object, object?> map)
        {
            return fallback;
        }

        float interval = fallback.Interval;
        float playerSearchRadius = fallback.PlayerSearchRadius;
        float spawnChance = fallback.SpawnChance;
        float maxDeformationSpawnChanceBonus = fallback.MaxDeformationSpawnChanceBonus;
        int maxSpawn = fallback.MaxSpawn;
        bool perPlayerSpawn = fallback.PerPlayerSpawn;
        int playerBaseValue = fallback.PlayerBaseValue;
        float baseCheckRadius = fallback.BaseCheckRadius;
        string? spawnRadiusValue = null;
        float spawnAltitude = fallback.SpawnAltitude;
        float resetRadius = fallback.ResetRadius;
        float health = fallback.Health;
        string? visualColorValue = null;

        foreach (KeyValuePair<object, object?> entry in map)
        {
            string key = NormalizeFieldName(GetYamlKey(entry.Key));
            switch (key)
            {
                case "interval":
                    if (TryGetFloat(entry.Value, out float parsedInterval))
                    {
                        interval = parsedInterval;
                    }

                    break;
                case "playersearchradius":
                    if (TryGetFloat(entry.Value, out float parsedPlayerSearchRadius))
                    {
                        playerSearchRadius = parsedPlayerSearchRadius;
                    }

                    break;
                case "spawnchance":
                    if (TryGetFloat(entry.Value, out float parsedSpawnChance))
                    {
                        spawnChance = parsedSpawnChance;
                    }

                    break;
                case "maxdeformationspawnchancebonus":
                    if (TryGetFloat(entry.Value, out float parsedMaxDeformationSpawnChanceBonus))
                    {
                        maxDeformationSpawnChanceBonus = parsedMaxDeformationSpawnChanceBonus;
                    }

                    break;
                case "maxspawn":
                    if (TryGetInt(entry.Value, out int parsedMaxSpawn))
                    {
                        maxSpawn = parsedMaxSpawn;
                    }

                    break;
                case "perplayerspawn":
                    if (TryGetBool(entry.Value, out bool parsedPerPlayerSpawn))
                    {
                        perPlayerSpawn = parsedPerPlayerSpawn;
                    }

                    break;
                case "playerbasevalue":
                    if (TryGetInt(entry.Value, out int parsedPlayerBaseValue))
                    {
                        playerBaseValue = parsedPlayerBaseValue;
                    }

                    break;
                case "basecheckradius":
                    if (TryGetFloat(entry.Value, out float parsedBaseCheckRadius))
                    {
                        baseCheckRadius = parsedBaseCheckRadius;
                    }

                    break;
                case "spawnradius":
                    spawnRadiusValue = entry.Value?.ToString();
                    break;
                case "spawnaltitude":
                    if (TryGetFloat(entry.Value, out float parsedSpawnAltitude))
                    {
                        spawnAltitude = parsedSpawnAltitude;
                    }

                    break;
                case "resetradius":
                    if (TryGetFloat(entry.Value, out float parsedResetRadius))
                    {
                        resetRadius = parsedResetRadius;
                    }

                    break;
                case "health":
                    if (TryGetFloat(entry.Value, out float parsedHealth))
                    {
                        health = parsedHealth;
                    }

                    break;
                case "visualcolor":
                    visualColorValue = entry.Value?.ToString();
                    break;
                default:
                    _logger?.LogWarning($"Ignoring unknown TerrainMistile spawn rule field '{GetYamlKey(entry.Key)}'.");
                    break;
            }
        }

        float spawnRadiusMin = fallback.SpawnRadiusMin;
        float spawnRadiusMax = fallback.SpawnRadiusMax;
        if (!string.IsNullOrWhiteSpace(spawnRadiusValue) &&
            !TryParseSpawnRadius(spawnRadiusValue!, out spawnRadiusMin, out spawnRadiusMax))
        {
            _logger?.LogWarning($"Ignoring invalid spawnRadius '{spawnRadiusValue}' in TerrainMistile spawn rules. Use a number like '24' or a range like '16~32'.");
            spawnRadiusMin = fallback.SpawnRadiusMin;
            spawnRadiusMax = fallback.SpawnRadiusMax;
        }

        Color visualColor = fallback.VisualColor;
        if (!string.IsNullOrWhiteSpace(visualColorValue))
        {
            if (TryParseVisualColor(visualColorValue!, out Color parsedColor))
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
        resetRadius = Mathf.Clamp(resetRadius, 1f, MaximumResetRadius);
        health = Mathf.Clamp(health, 1f, 10000f);
        if (spawnRadiusMin > spawnRadiusMax)
        {
            (spawnRadiusMin, spawnRadiusMax) = (spawnRadiusMax, spawnRadiusMin);
        }

        return new TerrainMistileBiomeSpawnRule(
            interval: interval,
            playerSearchRadius: playerSearchRadius,
            spawnChance: spawnChance,
            maxDeformationSpawnChanceBonus: maxDeformationSpawnChanceBonus,
            perPlayerSpawn: perPlayerSpawn,
            playerBaseValue: playerBaseValue,
            baseCheckRadius: baseCheckRadius,
            maxSpawn: maxSpawn,
            spawnRadiusMin: spawnRadiusMin,
            spawnRadiusMax: spawnRadiusMax,
            spawnAltitude: spawnAltitude,
            resetRadius: resetRadius,
            health: health,
            visualColor: visualColor);
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
        return CreateDefaultPlayerBasePrefabNames();
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
            case float floatValue when IsFinite(floatValue):
                result = floatValue;
                return true;
            case double doubleValue when
                !double.IsNaN(doubleValue) &&
                !double.IsInfinity(doubleValue) &&
                doubleValue >= -float.MaxValue &&
                doubleValue <= float.MaxValue:
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
            case long longValue when longValue is >= int.MinValue and <= int.MaxValue:
                result = (int)longValue;
                return true;
            case float floatValue:
                return TryRoundToInt(floatValue, out result);
            case double doubleValue:
                return TryRoundToInt(doubleValue, out result);
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
        string normalized = TerrainMistileExpandWorldDataBiomeCompat.NormalizeName(raw);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out biome))
        {
            return true;
        }

        if (uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint unsignedBiome))
        {
            biome = unchecked((int)unsignedBiome);
            return true;
        }

        if (TerrainMistileExpandWorldDataBiomeCompat.TryGetVanillaBiome(normalized, out biome))
        {
            return true;
        }

        if (TerrainMistileExpandWorldDataBiomeCompat.TryGetBiome(raw, out biome))
        {
            return true;
        }

        return !string.Equals(raw, normalized, StringComparison.Ordinal) &&
               TerrainMistileExpandWorldDataBiomeCompat.TryGetBiome(normalized, out biome);
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
            interval: DefaultInterval,
            playerSearchRadius: DefaultPlayerSearchRadius,
            spawnChance: DefaultSpawnChance,
            maxDeformationSpawnChanceBonus: DefaultMaxDeformationSpawnChanceBonus,
            perPlayerSpawn: DefaultPerPlayerSpawn,
            playerBaseValue: DefaultPlayerBaseValue,
            baseCheckRadius: DefaultBaseCheckRadius,
            maxSpawn: DefaultMaxSpawn,
            spawnRadiusMin: DefaultSpawnRadiusMin,
            spawnRadiusMax: DefaultSpawnRadiusMax,
            spawnAltitude: DefaultSpawnAltitude,
            resetRadius: DefaultResetRadius,
            health: DefaultHealth,
            visualColor: FallbackVisualColor);
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
        return float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
               IsFinite(result);
    }

    private static bool TryRoundToInt(double value, out int result)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            result = 0;
            return false;
        }

        double rounded = Math.Round(value);
        if (rounded < int.MinValue || rounded > int.MaxValue)
        {
            result = 0;
            return false;
        }

        result = (int)rounded;
        return true;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool TryParseVisualColor(string value, out Color color)
    {
        string normalized = value.Trim();
        if (normalized.Length > 0 && normalized[0] == '#')
        {
            normalized = normalized.Substring(1);
        }

        if (normalized.Length is not (3 or 4 or 6 or 8))
        {
            color = default;
            return false;
        }

        if (normalized.Length is 3 or 4)
        {
            StringBuilder expanded = new(normalized.Length * 2);
            foreach (char digit in normalized)
            {
                expanded.Append(digit).Append(digit);
            }

            normalized = expanded.ToString();
        }

        byte alpha = byte.MaxValue;
        if (!TryParseHexByte(normalized, 0, out byte red) ||
            !TryParseHexByte(normalized, 2, out byte green) ||
            !TryParseHexByte(normalized, 4, out byte blue) ||
            (normalized.Length == 8 && !TryParseHexByte(normalized, 6, out alpha)))
        {
            color = default;
            return false;
        }

        color = new Color(red / 255f, green / 255f, blue / 255f, alpha / 255f);
        return true;
    }

    private static bool TryParseHexByte(string value, int startIndex, out byte result)
    {
        return byte.TryParse(
            value.Substring(startIndex, 2),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out result);
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
            $"  interval: {FormatYamlNumber(DefaultInterval)} # Seconds between spawn rolls for one 32m terrain unit. 0 disables that biome.\n" +
            $"  playerSearchRadius: {FormatYamlNumber(DefaultPlayerSearchRadius)} # Players within this horizontal radius of a changed terrain unit activate its rolls.\n" +
            $"  spawnChance: {FormatYamlNumber(DefaultSpawnChance)} # Base chance used when the unit interval is ready and at least one player is nearby.\n" +
            $"  maxDeformationSpawnChanceBonus: {FormatYamlNumber(DefaultMaxDeformationSpawnChanceBonus)} # Added to spawnChance when the largest height deformation in the 32m terrain unit reaches the 8m cap. Scales linearly from 0m to 8m.\n" +
            $"  maxSpawn: {DefaultMaxSpawn.ToString(CultureInfo.InvariantCulture)} # Maximum active TerrainMistiles with targets within 32m of the target. 0 disables that biome.\n" +
            $"  perPlayerSpawn: {(DefaultPerPlayerSpawn ? "true" : "false")} # If true, one successful roll can spawn up to one TerrainMistile per nearby player, capped by maxSpawn and available targets.\n" +
            $"  playerBaseValue: {DefaultPlayerBaseValue.ToString(CultureInfo.InvariantCulture)} # playerBaseValue N skips spawn checks when at least N unique listed player-placed base prefab types are within baseCheckRadius meters horizontally of the changed terrain. 0 disables the PlayerBase check.\n" +
            $"  baseCheckRadius: {FormatYamlNumber(DefaultBaseCheckRadius)} # Horizontal radius used by playerBaseValue to count listed player-placed base prefab types.\n" +
            $"  spawnRadius: {FormatYamlNumber(DefaultSpawnRadiusMin)}~{FormatYamlNumber(DefaultSpawnRadiusMax)} # Horizontal spawn distance from the selected nearby player. Use 24 for fixed distance or 16~32 for a random range.\n" +
            $"  spawnAltitude: {FormatYamlNumber(DefaultSpawnAltitude)} # Height above solid ground where TerrainMistile spawns.\n" +
            $"  resetRadius: {FormatYamlNumber(DefaultResetRadius)} # Radius of terrain height and paint reset when TerrainMistile detonates.\n" +
            $"  health: {FormatYamlNumber(DefaultHealth)} # Maximum and current health applied when TerrainMistile spawns. Biome rules can override it.\n" +
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

    private static string FormatYamlNumber(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
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
        return $"interval={Interval:0.##}, playerSearchRadius={PlayerSearchRadius:0.##}, spawnChance={SpawnChance:0.###}, maxDeformationSpawnChanceBonus={MaxDeformationSpawnChanceBonus:0.###}, maxSpawn={MaxSpawn}, perPlayerSpawn={PerPlayerSpawn}, playerBaseValue={PlayerBaseValue}, baseCheckRadius={BaseCheckRadius:0.##}, spawnRadius={SpawnRadiusMin:0.##}~{SpawnRadiusMax:0.##}, spawnAltitude={SpawnAltitude:0.##}, resetRadius={ResetRadius:0.##}, health={Health:0.##}, visualColor=#{ColorUtility.ToHtmlStringRGB(VisualColor)}";
    }
}
