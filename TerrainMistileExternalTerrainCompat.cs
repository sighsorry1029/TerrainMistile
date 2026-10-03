using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TerrainMistile;

internal static class TerrainMistileExternalTerrainCompat
{
    private const string ExpandWorldDataLocationTypeName = "ExpandWorldData.LocationObjectDataAndSwap";
    private const string ExpandWorldDataLocationYamlTypeName = "ExpandWorldData.LocationYaml";
    private const string ExpandWorldDataLocationExtraTypeName = "ExpandWorldData.LocationExtra";
    private const string ExpandWorldDataBlueprintManagerTypeName = "ExpandWorldData.BlueprintManager";
    private const string ExpandWorldDataBlueprintTypeName = "ExpandWorldData.Blueprint";
    private const string ExpandWorldDataTerrainTypeName = "ExpandWorldData.Terrain";
    private const string ExpandWorldDataNoBuildManagerTypeName = "ExpandWorldData.NoBuildManager";
    private const string ExpandWorldDataBiomeManagerTypeName = "ExpandWorldData.BiomeManager";
    private const string BlueprintProtectedSourcePrefix = "Expand World Data blueprint terrain";
    private const float PatchRetryInterval = 2f;
    private const float MissingDependencyRetryInterval = 10f;
    private const int MaxPatchAttempts = 15;

    private static readonly Dictionary<string, Type> LoadedTypes = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, Dictionary<string, FieldInfo?>> FieldsByType = new();
    private static readonly Dictionary<Type, PropertyInfo> SnapshotHasValuesProperties = new();
    private static ManualLogSource? _logger;
    private static Harmony? _harmony;
    private static bool _terrainPatched;
    private static bool _blueprintTerrainPatched;
    private static bool _protectionSyncPatched;
    private static bool _biomeMappingRefreshPatched;
    private static bool _biomeNamesFromFilePatched;
    private static bool _biomeSetNamesPatched;
    private static bool _biomeLoadPatched;
    private static bool _biomeMappingRefreshRequested;
    private static bool _expandWorldDataDetected;
    private static bool _blueprintReflectionResolved;
    private static int _patchAttempts;
    private static float _nextPatchAttemptTime;
    private static MethodInfo? _tryGetLocationYamlMethod;
    private static MethodInfo? _isBlueprintPrefabMethod;
    private static MethodInfo? _tryGetBlueprintMethod;

    public static void Initialize(ManualLogSource logger, Harmony harmony)
    {
        _logger = logger;
        _harmony = harmony;
        bool dependencyLoaded = TryPatch();
        _nextPatchAttemptTime = Time.time +
                                (dependencyLoaded ? PatchRetryInterval : MissingDependencyRetryInterval);
    }

    public static void Update()
    {
        if ((_terrainPatched && _blueprintTerrainPatched && _protectionSyncPatched && _biomeMappingRefreshPatched) ||
            _patchAttempts >= MaxPatchAttempts ||
            Time.time < _nextPatchAttemptTime)
        {
            return;
        }

        bool dependencyLoaded = TryPatch();
        _nextPatchAttemptTime = Time.time +
                                (dependencyLoaded ? PatchRetryInterval : MissingDependencyRetryInterval);
    }

    internal static bool ConsumeBiomeMappingRefreshRequest()
    {
        if (!_biomeMappingRefreshRequested)
        {
            return false;
        }

        _biomeMappingRefreshRequested = false;
        return true;
    }

    private static bool TryPatch()
    {
        if (_harmony == null || _patchAttempts >= MaxPatchAttempts)
        {
            return false;
        }

        if (FindLoadedType(ExpandWorldDataBiomeManagerTypeName) == null)
        {
            return false;
        }

        if (!_expandWorldDataDetected)
        {
            _expandWorldDataDetected = true;
            _biomeMappingRefreshRequested = true;
        }

        _patchAttempts++;
        TryPatchTerrainHandler();
        TryPatchBlueprintTerrainHandler();
        TryPatchBlueprintProtectionSync();
        TryPatchBiomeMappingRefresh();
        return true;
    }

    private static void TryPatchTerrainHandler()
    {
        if (_terrainPatched || _harmony == null)
        {
            return;
        }

        Type? locationType = FindLoadedType(ExpandWorldDataLocationTypeName);
        Type? locationYamlType = FindLoadedType(ExpandWorldDataLocationYamlTypeName);
        if (locationType == null || locationYamlType == null)
        {
            return;
        }

        MethodInfo? target = AccessTools.Method(
            locationType,
            "HandleTerrain",
            new[] { typeof(Vector3), typeof(float), typeof(bool), locationYamlType });
        MethodInfo? patch = AccessTools.Method(typeof(TerrainMistileExternalTerrainCompat), nameof(HandleTerrainPrefix));
        if (target == null || patch == null)
        {
            return;
        }

        _harmony.Patch(target, prefix: new HarmonyMethod(patch));
        _terrainPatched = true;
        _logger?.LogInfo("Expand World Data terrain compat initialized.");
    }

    private static void TryPatchBlueprintProtectionSync()
    {
        if (_protectionSyncPatched || _harmony == null)
        {
            return;
        }

        Type? noBuildManagerType = FindLoadedType(ExpandWorldDataNoBuildManagerTypeName);
        if (noBuildManagerType == null)
        {
            return;
        }

        MethodInfo? target = AccessTools.Method(noBuildManagerType, "UpdateData");
        MethodInfo? patch = AccessTools.Method(typeof(TerrainMistileExternalTerrainCompat), nameof(BlueprintProtectionSyncPostfix));
        if (target == null || patch == null)
        {
            return;
        }

        _harmony.Patch(target, postfix: new HarmonyMethod(patch));
        _protectionSyncPatched = true;
        if (ZNet.instance != null &&
            ZNet.instance.IsServer() &&
            ZoneSystem.instance &&
            ZoneSystem.instance.m_locationInstances.Count > 0)
        {
            SyncBlueprintProtectedAreas();
        }

        _logger?.LogInfo("Expand World Data blueprint terrain protection initialized.");
    }

    private static void TryPatchBlueprintTerrainHandler()
    {
        if (_blueprintTerrainPatched || _harmony == null)
        {
            return;
        }

        Type? terrainType = FindLoadedType(ExpandWorldDataTerrainTypeName);
        Type? blueprintType = FindLoadedType(ExpandWorldDataBlueprintTypeName);
        if (terrainType == null || blueprintType == null)
        {
            return;
        }

        MethodInfo? target = AccessTools.Method(
            terrainType,
            "ApplyBlueprint",
            new[] { blueprintType, typeof(Vector3), typeof(Quaternion), typeof(ZoneSystem.SpawnMode), typeof(List<GameObject>) });
        if (target == null)
        {
            return;
        }

        _harmony.Patch(target, prefix: new HarmonyMethod(
            typeof(TerrainMistileExternalTerrainCompat), nameof(ApplyBlueprintPrefix)));
        _blueprintTerrainPatched = true;
        _logger?.LogInfo("Expand World Data blueprint terrain snapshot protection initialized.");
    }

    private static void TryPatchBiomeMappingRefresh()
    {
        if (_biomeMappingRefreshPatched || _harmony == null)
        {
            return;
        }

        Type? biomeManagerType = FindLoadedType(ExpandWorldDataBiomeManagerTypeName);
        if (biomeManagerType == null)
        {
            return;
        }

        MethodInfo? patch = AccessTools.Method(
            typeof(TerrainMistileExternalTerrainCompat),
            nameof(BiomeMappingChangedPostfix));
        if (patch == null)
        {
            return;
        }

        HarmonyMethod postfix = new(patch);
        List<string> unavailableMethods = new();
        TryPatchBiomeMappingRefreshMethod(
            biomeManagerType,
            "NamesFromFile",
            postfix,
            ref _biomeNamesFromFilePatched,
            unavailableMethods);
        TryPatchBiomeMappingRefreshMethod(
            biomeManagerType,
            "SetNames",
            postfix,
            ref _biomeSetNamesPatched,
            unavailableMethods);
        TryPatchBiomeMappingRefreshMethod(
            biomeManagerType,
            "Load",
            postfix,
            ref _biomeLoadPatched,
            unavailableMethods);

        _biomeMappingRefreshPatched =
            _biomeNamesFromFilePatched &&
            _biomeSetNamesPatched &&
            _biomeLoadPatched;
        if (_biomeMappingRefreshPatched)
        {
            _logger?.LogInfo("Expand World Data biome mapping refresh initialized.");
        }
        else if (_patchAttempts >= MaxPatchAttempts)
        {
            _logger?.LogWarning(
                "Expand World Data biome mapping refresh is only partially available. " +
                $"Unavailable methods: {string.Join(", ", unavailableMethods)}.");
        }
    }

    private static void TryPatchBiomeMappingRefreshMethod(
        Type biomeManagerType,
        string methodName,
        HarmonyMethod postfix,
        ref bool patched,
        List<string> unavailableMethods)
    {
        if (patched || _harmony == null)
        {
            return;
        }

        try
        {
            MethodInfo? target = ResolveBiomeMappingRefreshMethod(biomeManagerType, methodName);
            if (target == null)
            {
                unavailableMethods.Add($"{methodName} (missing or unsupported signature)");
                return;
            }

            _harmony.Patch(target, postfix: postfix);
            patched = true;
        }
        catch (Exception ex)
        {
            unavailableMethods.Add($"{methodName} ({ex.Message})");
        }
    }

    internal static MethodInfo? ResolveBiomeMappingRefreshMethod(Type biomeManagerType, string methodName)
    {
        Type? biomeYamlType = methodName == "Load"
            ? biomeManagerType.Assembly.GetType("ExpandWorldData.BiomeYaml")
            : null;
        Type? biomeListType = biomeYamlType == null ? null : typeof(List<>).MakeGenericType(biomeYamlType);
        MethodInfo? target = null;
        foreach (MethodInfo method in biomeManagerType.GetMethods(
                     BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (method.Name != methodName || method.ReturnType != typeof(void) || method.ContainsGenericParameters)
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            // Only the reviewed data-loading contracts signal completed mapping changes.
            // In particular, the parameterless Load is initialization, not a data load.
            bool supported = methodName switch
            {
                "NamesFromFile" => parameters.Length == 0,
                "SetNames" => parameters.Length == 1 &&
                              parameters[0].ParameterType == typeof(Dictionary<Heightmap.Biome, string>),
                "Load" => parameters.Length == 1 &&
                          (parameters[0].ParameterType == typeof(string) ||
                           parameters[0].ParameterType == biomeListType),
                _ => false
            };
            if (!supported)
            {
                continue;
            }

            if (target != null)
            {
                throw new AmbiguousMatchException($"Multiple supported {biomeManagerType.FullName}.{methodName} methods.");
            }

            target = method;
        }

        return target;
    }

    private static void BiomeMappingChangedPostfix()
    {
        _biomeMappingRefreshRequested = true;
    }

    internal static Type? FindLoadedType(string fullName)
    {
        if (LoadedTypes.TryGetValue(fullName, out Type cachedType))
        {
            return cachedType;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type;
            try
            {
                type = assembly.GetType(fullName, throwOnError: false);
            }
            catch
            {
                continue;
            }

            if (type != null)
            {
                LoadedTypes[fullName] = type;
                return type;
            }
        }

        return null;
    }

    private static void HandleTerrainPrefix(Vector3 pos, float radius, bool isBlueprint, object data)
    {
        string prefab = GetString(data, "prefab");
        float ignoreRadius = GetTerrainRadius(radius, isBlueprint, data);
        if (ignoreRadius > 0f)
        {
            TerrainMistileSystem.RegisterExternalTerrainIgnoreArea(pos, ignoreRadius, "Expand World Data location terrain", TerrainMistileSystem.LocationTerrainIgnoreDuration);
        }

        // EWD's isBlueprint argument controls default leveling, not blueprint identity.
        try
        {
            if (TryGetLoadedBlueprint(prefab, out object? blueprint) && blueprint != null)
            {
                float protectedRadius = GetBlueprintProtectionRadius(radius, data, blueprint);
                TerrainMistileSystem.RegisterProtectedTerrainArea(pos, protectedRadius, $"{BlueprintProtectedSourcePrefix} {prefab}");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Expand World Data blueprint terrain protection failed: {ex.Message}");
        }
    }

    private static void ApplyBlueprintPrefix(object blueprint, Vector3 position)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        try
        {
            float radius = GetBlueprintSnapshotRadius(blueprint);
            if (radius > 0f)
            {
                TerrainMistileSystem.RegisterProtectedTerrainArea(
                    position,
                    radius + TerrainMistileSystem.LocationTerrainProtectionPadding,
                    $"{BlueprintProtectedSourcePrefix} {GetString(blueprint, "Name")}");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Expand World Data blueprint terrain snapshot protection failed: {ex.Message}");
        }
    }

    private static void BlueprintProtectionSyncPostfix()
    {
        SyncBlueprintProtectedAreas();
    }

    private static void SyncBlueprintProtectedAreas()
    {
        ZoneSystem zoneSystem = ZoneSystem.instance;
        if (!zoneSystem || !EnsureBlueprintReflectionMethods())
        {
            return;
        }

        List<TerrainMistileSystem.ProtectedTerrainAreaData> protectedAreas = new();
        try
        {
            foreach (ZoneSystem.LocationInstance locationInstance in zoneSystem.m_locationInstances.Values)
            {
                if (locationInstance.m_location == null)
                {
                    continue;
                }

                string prefab = locationInstance.m_location.m_prefabName;
                if (string.IsNullOrWhiteSpace(prefab))
                {
                    continue;
                }

                if (!TryGetLoadedBlueprint(prefab, out object? blueprint))
                {
                    return;
                }

                if (blueprint == null)
                {
                    continue;
                }

                bool foundData = TryGetLocationYaml(locationInstance.m_location, out object? locationYaml);
                if (!foundData && _tryGetLocationYamlMethod == null)
                {
                    return;
                }

                object? data = foundData ? locationYaml : null;
                float protectedRadius = GetBlueprintProtectionRadius(
                    locationInstance.m_location.m_exteriorRadius, data, blueprint);

                protectedAreas.Add(new TerrainMistileSystem.ProtectedTerrainAreaData(
                    locationInstance.m_position,
                    protectedRadius,
                    $"{BlueprintProtectedSourcePrefix} {prefab}"));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Expand World Data blueprint terrain protection refresh failed: {ex.Message}");
            return;
        }

        TerrainMistileSystem.ReplaceProtectedTerrainAreas(BlueprintProtectedSourcePrefix, protectedAreas);
    }

    private static bool TryGetLocationYaml(ZoneSystem.ZoneLocation location, out object? locationYaml)
    {
        locationYaml = null;
        MethodInfo? method = _tryGetLocationYamlMethod;
        if (method == null)
        {
            return false;
        }

        object?[] args = { location, null };
        try
        {
            bool found = method.Invoke(null, args) as bool? == true;
            locationYaml = args[1];
            return found && locationYaml != null;
        }
        catch (Exception ex)
        {
            _tryGetLocationYamlMethod = null;
            _logger?.LogWarning($"Expand World Data location lookup failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryGetLoadedBlueprint(string prefab, out object? blueprint)
    {
        blueprint = null;
        if (!EnsureBlueprintReflectionMethods())
        {
            return false;
        }

        try
        {
            if (_isBlueprintPrefabMethod!.Invoke(null, new object[] { prefab }) as bool? != true)
            {
                return true;
            }

            // TryGet can load files; Has above limits this lookup to already-loaded blueprints.
            object?[] args = { prefab, null };
            if (_tryGetBlueprintMethod!.Invoke(null, args) as bool? != true || args[1] == null)
            {
                return false;
            }

            blueprint = args[1];
            return true;
        }
        catch (Exception ex)
        {
            _isBlueprintPrefabMethod = null;
            _tryGetBlueprintMethod = null;
            _logger?.LogWarning($"Expand World Data blueprint lookup failed: {ex.Message}");
            return false;
        }
    }

    private static bool EnsureBlueprintReflectionMethods()
    {
        if (_blueprintReflectionResolved)
        {
            return _tryGetLocationYamlMethod != null && _isBlueprintPrefabMethod != null && _tryGetBlueprintMethod != null;
        }

        Type? locationExtraType = FindLoadedType(ExpandWorldDataLocationExtraTypeName);
        Type? locationYamlType = FindLoadedType(ExpandWorldDataLocationYamlTypeName);
        Type? blueprintManagerType = FindLoadedType(ExpandWorldDataBlueprintManagerTypeName);
        Type? blueprintType = FindLoadedType(ExpandWorldDataBlueprintTypeName);
        if (locationExtraType == null || locationYamlType == null || blueprintManagerType == null || blueprintType == null)
        {
            return false;
        }

        _tryGetLocationYamlMethod = AccessTools.Method(
            locationExtraType,
            "TryGetData",
            new[] { typeof(ZoneSystem.ZoneLocation), locationYamlType.MakeByRefType() });
        _isBlueprintPrefabMethod = AccessTools.Method(blueprintManagerType, "Has", new[] { typeof(string) });
        _tryGetBlueprintMethod = AccessTools.Method(
            blueprintManagerType, "TryGet", new[] { typeof(string), blueprintType.MakeByRefType() });
        _blueprintReflectionResolved = true;
        return _tryGetLocationYamlMethod != null && _isBlueprintPrefabMethod != null && _tryGetBlueprintMethod != null;
    }

    internal static float GetBlueprintProtectionRadius(float exteriorRadius, object? data, object blueprint)
    {
        float radius = Mathf.Max(exteriorRadius, GetBlueprintSnapshotRadius(blueprint));
        if (data != null)
        {
            float locationRadius = Mathf.Max(exteriorRadius, GetFloat(data, "exteriorRadius"));
            bool defaultLevel = GetSnapshotNodeSpacing(GetField(blueprint, "TerrainHeight")?.GetValue(blueprint)) == 0f;
            radius = Mathf.Max(radius, locationRadius, GetTerrainRadius(exteriorRadius, defaultLevel, data), GetNoBuildRadius(data, locationRadius));
        }

        return radius + TerrainMistileSystem.LocationTerrainProtectionPadding;
    }

    internal static float GetBlueprintSnapshotRadius(object blueprint)
    {
        float nodeSpacing = Mathf.Max(
            GetSnapshotNodeSpacing(GetField(blueprint, "TerrainHeight")?.GetValue(blueprint)),
            GetSnapshotNodeSpacing(GetField(blueprint, "TerrainPaint")?.GetValue(blueprint)));
        if (nodeSpacing == 0f)
        {
            return 0f;
        }

        // EWD computes Radius from centered object and terrain bounds when loading the blueprint.
        // Include sample spacing because nearest-node sampling also affects terrain beyond the last node.
        if (GetField(blueprint, "Radius")?.GetValue(blueprint) is not float radius ||
            float.IsNaN(radius) || float.IsInfinity(radius) || radius < 0f ||
            float.IsInfinity(radius + nodeSpacing))
        {
            throw new InvalidOperationException("Invalid Expand World Data blueprint terrain radius.");
        }

        return radius + nodeSpacing;
    }

    private static float GetSnapshotNodeSpacing(object? snapshot)
    {
        if (snapshot == null)
        {
            return 0f;
        }

        Type type = snapshot.GetType();
        if (!SnapshotHasValuesProperties.TryGetValue(type, out PropertyInfo property))
        {
            property = type.GetProperty("HasValues", BindingFlags.Instance | BindingFlags.Public)
                       ?? throw new MissingMemberException(type.FullName, "HasValues");
            SnapshotHasValuesProperties[type] = property;
        }

        if (property.GetValue(snapshot) is not true)
        {
            return 0f;
        }

        float spacing = GetFloat(snapshot, "DistanceBetweenNodes");
        if (float.IsNaN(spacing) || float.IsInfinity(spacing) || spacing <= 0f)
        {
            throw new InvalidOperationException("Invalid Expand World Data terrain node spacing.");
        }

        return spacing;
    }

    internal static float GetTerrainRadius(float exteriorRadius, bool isBlueprint, object data)
    {
        string levelArea = GetString(data, "levelArea");
        string paint = GetString(data, "paint");
        bool level = levelArea.Length == 0 ? isBlueprint : !levelArea.Equals("false", StringComparison.Ordinal);
        float radius = 0f;

        if (level)
        {
            float levelRadius = GetFloat(data, "levelRadius");
            float levelBorder = GetFloat(data, "levelBorder");
            radius = Mathf.Max(radius, levelRadius == 0f && levelBorder == 0f
                ? exteriorRadius
                : levelRadius + levelBorder);
        }

        if (paint.Length > 0)
        {
            float paintRadius = GetNullableFloat(data, "paintRadius") ?? exteriorRadius;
            float paintBorder = GetNullableFloat(data, "paintBorder") ?? 5f;
            radius = Mathf.Max(radius, paintRadius + paintBorder);
        }

        return radius;
    }

    private static float GetNoBuildRadius(object data, float exteriorRadius)
    {
        string noBuild = GetString(data, "noBuild").Trim();
        if (noBuild.Length == 0 || noBuild.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return 0f;
        }

        if (noBuild.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return exteriorRadius;
        }

        return float.TryParse(noBuild, NumberStyles.Float, CultureInfo.InvariantCulture, out float radius)
            ? Mathf.Max(0f, radius)
            : 0f;
    }

    private static string GetString(object data, string fieldName)
    {
        return GetField(data, fieldName)?.GetValue(data) as string ?? "";
    }

    private static float GetFloat(object data, string fieldName)
    {
        object? value = GetField(data, fieldName)?.GetValue(data);
        return value is float number ? number : 0f;
    }

    private static float? GetNullableFloat(object data, string fieldName)
    {
        object? value = GetField(data, fieldName)?.GetValue(data);
        return value is float number ? number : null;
    }

    private static FieldInfo? GetField(object data, string fieldName)
    {
        Type type = data.GetType();
        if (!FieldsByType.TryGetValue(type, out Dictionary<string, FieldInfo?> fields))
        {
            fields = new Dictionary<string, FieldInfo?>(StringComparer.Ordinal);
            FieldsByType[type] = fields;
        }

        if (!fields.TryGetValue(fieldName, out FieldInfo? field))
        {
            field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            fields[fieldName] = field;
        }

        return field;
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
    private static MethodInfo? _tryGetBiomeMethod;
    private static MethodInfo? _tryGetDisplayNameMethod;

    internal static bool TryGetVanillaBiome(string name, out int biome)
    {
        return OriginalBiomes.TryGetValue(name, out biome);
    }

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
        try
        {
            MethodInfo? method = _tryGetBiomeMethod;
            if (method == null)
            {
                Type? biomeManager =
                    TerrainMistileExternalTerrainCompat.FindLoadedType("ExpandWorldData.BiomeManager");
                if (biomeManager == null)
                {
                    return false;
                }

                method = biomeManager.GetMethod(
                    "TryGetBiome",
                    BindingFlags.Public | BindingFlags.Static);
                _tryGetBiomeMethod = method;
            }

            if (method == null)
            {
                return false;
            }

            ParameterInfo[] parameters = method.GetParameters();
            Type? biomeType = parameters.Length == 2
                ? parameters[1].ParameterType.GetElementType()
                : null;
            if (method.ReturnType != typeof(bool) ||
                parameters.Length != 2 ||
                parameters[0].ParameterType != typeof(string) ||
                !parameters[1].ParameterType.IsByRef ||
                !parameters[1].IsOut ||
                biomeType == null ||
                !biomeType.IsEnum)
            {
                _tryGetBiomeMethod = null;
                return false;
            }

            object[] args = { name, Enum.ToObject(biomeType, 0) };
            if (method.Invoke(null, args) is bool result && result)
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
        try
        {
            MethodInfo? method = _tryGetDisplayNameMethod;
            if (method == null)
            {
                Type? biomeManager =
                    TerrainMistileExternalTerrainCompat.FindLoadedType("ExpandWorldData.BiomeManager");
                if (biomeManager == null)
                {
                    return false;
                }

                method = biomeManager.GetMethod(
                    "TryGetDisplayName",
                    BindingFlags.Public | BindingFlags.Static);
                _tryGetDisplayNameMethod = method;
            }

            if (method == null)
            {
                return false;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (method.ReturnType != typeof(bool) ||
                parameters.Length != 2 ||
                !parameters[0].ParameterType.IsEnum ||
                !parameters[1].ParameterType.IsByRef ||
                !parameters[1].IsOut ||
                parameters[1].ParameterType.GetElementType() != typeof(string))
            {
                _tryGetDisplayNameMethod = null;
                return false;
            }

            object[] args = { Enum.ToObject(parameters[0].ParameterType, biome), "" };
            if (method.Invoke(null, args) is bool result &&
                result &&
                args[1] is string displayName &&
                !string.IsNullOrWhiteSpace(displayName))
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

    private static void EnsureFileMappingLoaded()
    {
        if (_fileMappingLoaded)
        {
            return;
        }

        string configPath = Paths.ConfigPath;
        string directory = string.IsNullOrWhiteSpace(configPath)
            ? ""
            : Path.Combine(configPath, "expand_world");
        bool mappingBuilt = TryBuildFileMapping(
                directory,
                out Dictionary<string, int> nameToBiome,
                out Dictionary<int, string> biomeToName,
                out string warning);
        if (!mappingBuilt)
        {
            _fileMappingLoaded = true;
            if (warning.Length > 0)
            {
                TerrainMistilePlugin.TerrainMistileLogger.LogWarning(warning);
            }

            return;
        }

        FileNameToBiome.Clear();
        foreach (KeyValuePair<string, int> entry in nameToBiome)
        {
            FileNameToBiome[entry.Key] = entry.Value;
        }

        FileBiomeToName.Clear();
        foreach (KeyValuePair<int, string> entry in biomeToName)
        {
            FileBiomeToName[entry.Key] = entry.Value;
        }

        _fileMappingLoaded = true;
        if (warning.Length > 0)
        {
            TerrainMistilePlugin.TerrainMistileLogger.LogWarning(warning);
        }
    }

    internal static void InvalidateFileMapping()
    {
        _fileMappingLoaded = false;
    }

    internal static bool TryBuildFileMappingForTests(
        string directory,
        out Dictionary<string, int> mapping,
        out string error)
    {
        return TryBuildFileMapping(directory, out mapping, out _, out error);
    }

    private static bool TryBuildFileMapping(
        string directory,
        out Dictionary<string, int> nameToBiome,
        out Dictionary<int, string> biomeToName,
        out string error)
    {
        nameToBiome = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        biomeToName = new Dictionary<int, string>();
        HashSet<string> exactBiomeNames = new(StringComparer.OrdinalIgnoreCase);
        error = "";

        foreach (KeyValuePair<string, int> biome in OriginalBiomes)
        {
            AddFileBiomeName(biome.Key, biome.Value, exactBiomeNames, nameToBiome, biomeToName);
        }

        if (!Directory.Exists(directory))
        {
            return true;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "expand_biomes*.yaml", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            error = $"Failed to enumerate Expand World Data biome files: {ex.Message}";
            return false;
        }

        Array.Sort(files, (left, right) => StringComparer.OrdinalIgnoreCase.Compare(right, left));
        int nextBiomeBase = FirstCustomBiomeBase;
        foreach (string file in files)
        {
            List<ExpandWorldDataBiomeYaml>? entries;
            try
            {
                entries = ExpandWorldDataBiomeDeserializer.Deserialize<List<ExpandWorldDataBiomeYaml>>(File.ReadAllText(file));
            }
            catch (Exception ex)
            {
                string warning =
                    $"Skipping invalid Expand World Data biome file '{file}': {ex.Message}";
                error = error.Length == 0 ? warning : error + Environment.NewLine + warning;
                continue;
            }

            if (entries == null)
            {
                continue;
            }

            foreach (ExpandWorldDataBiomeYaml entry in entries)
            {
                string biomeName = entry?.Biome?.Trim() ?? "";
                if (biomeName.Length == 0 || exactBiomeNames.Contains(biomeName))
                {
                    continue;
                }

                if (!TryGetNextBiome(nextBiomeBase, out nextBiomeBase))
                {
                    error = $"Expand World Data biome limit was exceeded while assigning '{biomeName}'.";
                    return false;
                }

                AddFileBiomeName(biomeName, nextBiomeBase, exactBiomeNames, nameToBiome, biomeToName);
            }
        }

        return true;
    }

    private static bool TryGetNextBiome(int biome, out int nextBiome)
    {
        uint value = unchecked((uint)biome);
        if (value == 0x80u)
        {
            nextBiome = 0;
            return false;
        }

        uint nextValue = value == 0x80000000u ? 0x80u : 2u * value;
        nextBiome = unchecked((int)nextValue);
        return true;
    }

    private static void AddFileBiomeName(
        string name,
        int biome,
        HashSet<string> exactBiomeNames,
        Dictionary<string, int> nameToBiome,
        Dictionary<int, string> biomeToName)
    {
        exactBiomeNames.Add(name);
        nameToBiome[name] = biome;
        string normalized = NormalizeName(name);
        if (!string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase) &&
            !exactBiomeNames.Contains(normalized))
        {
            nameToBiome[normalized] = biome;
        }

        biomeToName[biome] = name;
    }

    internal static string NormalizeName(string value)
    {
        return value.Trim().Replace(" ", "").Replace("_", "").Replace("-", "");
    }

    private sealed class ExpandWorldDataBiomeYaml
    {
        public string Biome { get; set; } = "";
    }
}
