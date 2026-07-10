using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TerrainMistile;

internal static class TerrainMistileExternalTerrainCompat
{
    private const string ExpandWorldDataLocationTypeName = "ExpandWorldData.LocationObjectDataAndSwap";
    private const string ExpandWorldDataLocationYamlTypeName = "ExpandWorldData.LocationYaml";
    private const string ExpandWorldDataLocationExtraTypeName = "ExpandWorldData.LocationExtra";
    private const string ExpandWorldDataBlueprintManagerTypeName = "ExpandWorldData.BlueprintManager";
    private const string ExpandWorldDataNoBuildManagerTypeName = "ExpandWorldData.NoBuildManager";
    private const string BlueprintProtectedSourcePrefix = "Expand World Data blueprint terrain";
    private const float PatchRetryInterval = 2f;
    private const int MaxPatchAttempts = 15;

    private static readonly Dictionary<string, Type> LoadedTypes = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, Dictionary<string, FieldInfo?>> FieldsByType = new();
    private static ManualLogSource? _logger;
    private static Harmony? _harmony;
    private static bool _terrainPatched;
    private static bool _protectionSyncPatched;
    private static bool _blueprintReflectionResolved;
    private static int _patchAttempts;
    private static float _nextPatchAttemptTime;
    private static MethodInfo? _tryGetLocationYamlMethod;
    private static MethodInfo? _isBlueprintPrefabMethod;

    public static void Initialize(ManualLogSource logger, Harmony harmony)
    {
        _logger = logger;
        _harmony = harmony;
        if (!IsExpandWorldDataLoaded())
        {
            _patchAttempts = MaxPatchAttempts;
            return;
        }

        TryPatch();
        _nextPatchAttemptTime = Time.time + PatchRetryInterval;
    }

    public static void Update()
    {
        if ((_terrainPatched && _protectionSyncPatched) ||
            _patchAttempts >= MaxPatchAttempts ||
            Time.time < _nextPatchAttemptTime)
        {
            return;
        }

        _nextPatchAttemptTime = Time.time + PatchRetryInterval;
        TryPatch();
    }

    private static void TryPatch()
    {
        if (_harmony == null || _patchAttempts >= MaxPatchAttempts)
        {
            return;
        }

        _patchAttempts++;
        TryPatchTerrainHandler();
        TryPatchBlueprintProtectionSync();
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
        _logger?.LogInfo("Expand World Data blueprint terrain protection initialized.");
    }

    private static Type? FindLoadedType(string fullName)
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

    private static bool IsExpandWorldDataLoaded()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (string.Equals(assembly.GetName().Name, "ExpandWorldData", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore dynamic assemblies that cannot expose their name.
            }
        }

        return false;
    }

    private static void HandleTerrainPrefix(Vector3 pos, float radius, bool isBlueprint, object data)
    {
        string prefab = GetString(data, "prefab");
        float ignoreRadius = GetTerrainRadius(radius, isBlueprint, data);
        if (ignoreRadius > 0f)
        {
            TerrainMistileSystem.RegisterExternalTerrainIgnoreArea(pos, ignoreRadius, "Expand World Data location terrain", TerrainMistileSystem.LocationTerrainIgnoreDuration);
        }

        float noBuildRadius = GetNoBuildRadius(data, Mathf.Max(GetFloat(data, "exteriorRadius"), radius));
        float protectedRadius = Mathf.Max(ignoreRadius, radius, GetFloat(data, "exteriorRadius"), noBuildRadius) + TerrainMistileSystem.LocationTerrainProtectionPadding;
        if (isBlueprint)
        {
            TerrainMistileSystem.RegisterProtectedTerrainArea(pos, protectedRadius, $"{BlueprintProtectedSourcePrefix} {prefab}");
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

                if (!TryIsBlueprintPrefab(prefab, out bool isBlueprint))
                {
                    return;
                }

                if (!isBlueprint)
                {
                    continue;
                }

                bool foundData = TryGetLocationYaml(locationInstance.m_location, out object? locationYaml);
                if (!foundData && _tryGetLocationYamlMethod == null)
                {
                    return;
                }

                object? data = foundData ? locationYaml : null;
                float terrainRadius = data == null ? 0f : GetTerrainRadius(locationInstance.m_location.m_exteriorRadius, isBlueprint: true, data);
                float noBuildRadius = data == null ? 0f : GetNoBuildRadius(data, locationInstance.m_location.m_exteriorRadius);
                float protectedRadius = Mathf.Max(terrainRadius, locationInstance.m_location.m_exteriorRadius, noBuildRadius) + TerrainMistileSystem.LocationTerrainProtectionPadding;

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

    private static bool TryIsBlueprintPrefab(string prefab, out bool isBlueprint)
    {
        isBlueprint = false;
        MethodInfo? method = _isBlueprintPrefabMethod;
        if (method == null)
        {
            return false;
        }

        try
        {
            isBlueprint = method.Invoke(null, new object[] { prefab }) as bool? == true;
            return true;
        }
        catch (Exception ex)
        {
            _isBlueprintPrefabMethod = null;
            _logger?.LogWarning($"Expand World Data blueprint lookup failed: {ex.Message}");
            return false;
        }
    }

    private static bool EnsureBlueprintReflectionMethods()
    {
        if (_blueprintReflectionResolved)
        {
            return _tryGetLocationYamlMethod != null && _isBlueprintPrefabMethod != null;
        }

        Type? locationExtraType = FindLoadedType(ExpandWorldDataLocationExtraTypeName);
        Type? locationYamlType = FindLoadedType(ExpandWorldDataLocationYamlTypeName);
        Type? blueprintManagerType = FindLoadedType(ExpandWorldDataBlueprintManagerTypeName);
        if (locationExtraType == null || locationYamlType == null || blueprintManagerType == null)
        {
            return false;
        }

        _tryGetLocationYamlMethod = AccessTools.Method(
            locationExtraType,
            "TryGetData",
            new[] { typeof(ZoneSystem.ZoneLocation), locationYamlType.MakeByRefType() });
        _isBlueprintPrefabMethod = AccessTools.Method(blueprintManagerType, "Has", new[] { typeof(string) });
        _blueprintReflectionResolved = true;
        return _tryGetLocationYamlMethod != null && _isBlueprintPrefabMethod != null;
    }

    private static float GetTerrainRadius(float exteriorRadius, bool isBlueprint, object data)
    {
        string levelArea = GetString(data, "levelArea");
        string paint = GetString(data, "paint");
        bool level = levelArea.Length == 0 ? isBlueprint : !levelArea.Equals("false", StringComparison.OrdinalIgnoreCase);
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
