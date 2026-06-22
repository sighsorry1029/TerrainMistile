using System;
using System.Globalization;
using System.Reflection;
using BepInEx.Bootstrap;
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

    private static ManualLogSource? _logger;
    private static Harmony? _harmony;
    private static bool _terrainPatched;
    private static bool _protectionSyncPatched;
    private static float _nextPatchAttemptTime;

    public static void Initialize(ManualLogSource logger, Harmony harmony)
    {
        _logger = logger;
        _harmony = harmony;
        TryPatch();
    }

    public static void Update()
    {
        if ((_terrainPatched && _protectionSyncPatched) || Time.time < _nextPatchAttemptTime)
        {
            return;
        }

        _nextPatchAttemptTime = Time.time + PatchRetryInterval;
        TryPatch();
    }

    private static void TryPatch()
    {
        if (_harmony == null)
        {
            return;
        }

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
            _logger?.LogDebug("Expand World Data terrain compat skipped: HandleTerrain was not found.");
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
            _logger?.LogDebug("Expand World Data noBuild compat skipped: NoBuildManager.UpdateData was not found.");
            return;
        }

        _harmony.Patch(target, postfix: new HarmonyMethod(patch));
        _protectionSyncPatched = true;
        _logger?.LogInfo("Expand World Data blueprint terrain protection initialized.");
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (var pluginInfo in Chainloader.PluginInfos.Values)
        {
            Type? type = pluginInfo.Instance?.GetType().Assembly.GetType(fullName, throwOnError: false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static void HandleTerrainPrefix(Vector3 pos, float radius, bool isBlueprint, object data)
    {
        string prefab = GetString(data, "prefab");
        string noBuild = GetString(data, "noBuild").Trim();
        float ignoreRadius = GetTerrainRadius(radius, isBlueprint, data);
        if (ignoreRadius > 0f)
        {
            TerrainMistileSystem.RegisterExternalTerrainIgnoreArea(pos, ignoreRadius, "Expand World Data location terrain", TerrainMistileSystem.LocationTerrainIgnoreDuration);
        }

        float noBuildRadius = GetNoBuildRadius(data, Mathf.Max(GetFloat(data, "exteriorRadius"), radius));
        float protectedRadius = Mathf.Max(ignoreRadius, radius, GetFloat(data, "exteriorRadius"), noBuildRadius) + TerrainMistileSystem.LocationTerrainProtectionPadding;
        _logger?.LogDebug(
            $"EWD terrain compat HandleTerrain prefab='{prefab}', isBlueprint={isBlueprint}, pos={TerrainMistileSystem.FormatPoint(pos)}, inputRadius={radius:0.##}, " +
            $"terrainRadius={ignoreRadius:0.##}, noBuild='{noBuild}', noBuildRadius={noBuildRadius:0.##}, protectedRadius={protectedRadius:0.##}.");
        if (TerrainMistilePlugin.DebugLoggingEnabled)
        {
            TerrainMistilePlugin.LogDebugDiagnostic(
                $"EWD HandleTerrain prefab='{prefab}', isBlueprint={isBlueprint}, pos={TerrainMistileSystem.FormatPoint(pos)}, inputRadius={radius:0.##}, " +
                $"terrainRadius={ignoreRadius:0.##}, noBuild='{noBuild}', noBuildRadius={noBuildRadius:0.##}, protectedRadius={protectedRadius:0.##}, willRegisterProtected={isBlueprint}.");
        }

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
        if (!zoneSystem)
        {
            _logger?.LogDebug("EWD blueprint protected terrain sync skipped. HasZoneSystem=false.");
            return;
        }

        TerrainMistileSystem.ClearProtectedTerrainAreas(BlueprintProtectedSourcePrefix);
        int checkedLocations = 0;
        int registered = 0;
        foreach (ZoneSystem.LocationInstance locationInstance in zoneSystem.m_locationInstances.Values)
        {
            if (locationInstance.m_location == null)
            {
                continue;
            }

            string prefab = locationInstance.m_location.m_prefabName;
            if (string.IsNullOrWhiteSpace(prefab) ||
                !TryIsBlueprintPrefab(prefab, out bool isBlueprint) ||
                !isBlueprint)
            {
                continue;
            }

            checkedLocations++;
            object? data = TryGetLocationYaml(locationInstance.m_location, out object? locationYaml) ? locationYaml : null;
            float terrainRadius = data == null ? 0f : GetTerrainRadius(locationInstance.m_location.m_exteriorRadius, isBlueprint: true, data);
            float noBuildRadius = data == null ? 0f : GetNoBuildRadius(data, locationInstance.m_location.m_exteriorRadius);
            float protectedRadius = Mathf.Max(terrainRadius, locationInstance.m_location.m_exteriorRadius, noBuildRadius) + TerrainMistileSystem.LocationTerrainProtectionPadding;

            TerrainMistileSystem.RegisterProtectedTerrainArea(
                locationInstance.m_position,
                protectedRadius,
                $"{BlueprintProtectedSourcePrefix} {prefab}");
            registered++;
        }

        _logger?.LogDebug(
            $"EWD blueprint protected terrain sync complete. matchedLocations={checkedLocations}, protectedRegistered={registered}.");
        if (TerrainMistilePlugin.DebugLoggingEnabled)
        {
            TerrainMistilePlugin.LogDebugDiagnostic(
                $"EWD blueprint protected terrain sync complete. matchedLocations={checkedLocations}, protectedRegistered={registered}.");
        }
    }

    private static bool TryGetLocationYaml(ZoneSystem.ZoneLocation location, out object? locationYaml)
    {
        locationYaml = null;
        Type? locationExtraType = FindLoadedType(ExpandWorldDataLocationExtraTypeName);
        Type? locationYamlType = FindLoadedType(ExpandWorldDataLocationYamlTypeName);
        if (locationExtraType == null || locationYamlType == null)
        {
            return false;
        }

        MethodInfo? method = AccessTools.Method(
            locationExtraType,
            "TryGetData",
            new[] { typeof(ZoneSystem.ZoneLocation), locationYamlType.MakeByRefType() });
        if (method == null)
        {
            return false;
        }

        object?[] args = { location, null };
        bool found = method.Invoke(null, args) as bool? == true;
        locationYaml = args[1];
        return found && locationYaml != null;
    }

    private static bool TryIsBlueprintPrefab(string prefab, out bool isBlueprint)
    {
        isBlueprint = false;
        Type? blueprintManagerType = FindLoadedType(ExpandWorldDataBlueprintManagerTypeName);
        if (blueprintManagerType == null)
        {
            return false;
        }

        MethodInfo? method = AccessTools.Method(blueprintManagerType, "Has", new[] { typeof(string) });
        if (method == null)
        {
            return false;
        }

        isBlueprint = method.Invoke(null, new object[] { prefab }) as bool? == true;
        return true;
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
        return data.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }
}
