using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace TerrainMistile;

internal static class TerrainMistileSystem
{
    private const string ResetEffectsRpcName = "TerrainMistile_ResetEffects";
    private const string ProtectedTerrainAreasRpcName = "TerrainMistile_ProtectedTerrainAreas";
    private const string RequestProtectedTerrainAreasRpcName = "TerrainMistile_RequestProtectedTerrainAreas";
    private const float ResetEffectGroundOffset = 0.1f;
    private const float FallbackEffectDestroyDelay = 20f;
    private const float ActiveTerrainMistileAreaRadius = 32f;
    private const float SpawnUnitSize = 32f;
    private const float TerrainHeightDeformationCap = 8f;
    private const float SpawnUnitScanInterval = 1f;
    private const float SpawnUnitRollStateRetention = 600f;
    private const float NoModifiedTerrainCompRetention = 3f;
    private const float PlayerBasePieceBucketRefreshInterval = 2f;
    private const float TargetReservationDuration = 60f;
    private const float ExternalTerrainIgnoreMergeDistance = 0.25f;
    private const float ProtectedTerrainAreaBucketSize = 32f;
    private const float ProtectedTerrainAreaSyncDelay = 0.5f;
    private const float ProtectedTerrainAreaRequestRetryInterval = 5f;
    internal const float LocationTerrainProtectionPadding = 5f;
    internal const float LocationTerrainIgnoreDuration = 10f;
    private const string ResetVfxPrefabName = "fx_greenroots_projectile_hit";
    private const string ResetSfxPrefabName = "sfx_staff_elder_grow";

    // Per-unit state keeps persistent scans cheap while still allowing changed terrain to re-enter after edits or expiry.
    private static readonly Dictionary<SpawnUnitKey, float> LastSpawnRollTimeByUnit = new();
    private static readonly Dictionary<TerrainComp, NoModifiedTerrainCompCache> NoModifiedTerrainComps = new();
    private static readonly Dictionary<SpawnUnitKey, ModifiedTerrainUnitCandidate> ModifiedTerrainUnits = new();

    // These scratch collections are reused by the scan/reset hot paths to avoid per-tick allocations.
    private static readonly List<SpawnUnitKey> TempSpawnUnitKeys = new();
    private static readonly HashSet<SpawnUnitKey> TempCooldownSpawnUnitKeys = new();
    private static readonly List<TerrainComp> TempNoModifiedTerrainComps = new();
    private static readonly List<TerrainMistileBehaviour> ActiveTerrainMistiles = new();
    private static readonly List<Player> TempPlayers = new();
    private static readonly List<Player> TempNearbyPlayers = new();
    private static readonly List<Heightmap> TempHeightmaps = new();

    // Reservations and protected areas prevent repeated TerrainMistiles from wasting rolls on the same reset target.
    private static readonly List<TargetReservation> TargetReservations = new();
    private static readonly List<ExternalTerrainIgnoreArea> ExternalTerrainIgnoreAreas = new();
    private static readonly List<ExternalTerrainIgnoreArea> ProtectedTerrainAreas = new();
    private static readonly Dictionary<TerrainAreaBucketKey, List<ExternalTerrainIgnoreArea>> ProtectedTerrainAreasByBucket = new();
    private static readonly Dictionary<PlayerBasePieceBucketKey, List<PlayerBasePiece>> PlayerBasePiecesByBucket = new();
    private static readonly HashSet<string> TempPlayerBasePrefabNames = new(StringComparer.OrdinalIgnoreCase);
    private static bool _playerBasePieceBucketsBuilt;
    private static float _nextPlayerBasePieceBucketRefreshTime;
    private static bool _resettingTerrain;
    private static float _nextSpawnUnitScanTime;
    private static bool _resetEffectsRpcRegistered;
    private static bool _protectedTerrainAreasRpcRegistered;
    private static bool _protectedTerrainAreasDirty;
    private static bool _protectedTerrainAreaBucketsDirty = true;
    private static bool _protectedTerrainAreasClientSynced;
    private static float _nextProtectedTerrainAreaSyncTime;
    private static float _nextProtectedTerrainAreaRequestTime;
    private static ZRoutedRpc? _registeredResetEffectsRpc;
    private static ZRoutedRpc? _registeredProtectedTerrainAreasRpc;

    internal static void UpdatePersistentTerrainSpawns()
    {
        if (_resettingTerrain || !TerrainMistileSpawnRules.HasEnabledRules)
        {
            return;
        }

        if (!ZNetScene.instance || Time.time < _nextSpawnUnitScanTime)
        {
            return;
        }

        _nextSpawnUnitScanTime = Time.time + SpawnUnitScanInterval;
        CollectEligiblePlayers();
        if (TempPlayers.Count == 0)
        {
            return;
        }

        CollectModifiedTerrainUnits();
        CleanupSpawnUnitRollState();
        foreach (ModifiedTerrainUnitCandidate unit in ModifiedTerrainUnits.Values)
        {
            TryRollSpawnForTerrainUnit(unit);
        }
    }

    internal static void UpdateResetEffectRpcRegistration()
    {
        if (ZRoutedRpc.instance == null ||
            (_resetEffectsRpcRegistered && ReferenceEquals(_registeredResetEffectsRpc, ZRoutedRpc.instance)))
        {
            return;
        }

        ZRoutedRpc.instance.Register(ResetEffectsRpcName, new Action<long, ZPackage>(RPC_ResetEffects));
        _resetEffectsRpcRegistered = true;
        _registeredResetEffectsRpc = ZRoutedRpc.instance;
    }

    internal static void UpdateProtectedTerrainAreaSync()
    {
        if (ZRoutedRpc.instance == null)
        {
            return;
        }

        if (!_protectedTerrainAreasRpcRegistered ||
            !ReferenceEquals(_registeredProtectedTerrainAreasRpc, ZRoutedRpc.instance))
        {
            ZRoutedRpc.instance.Register(ProtectedTerrainAreasRpcName, new Action<long, ZPackage>(RPC_ProtectedTerrainAreas));
            ZRoutedRpc.instance.Register(RequestProtectedTerrainAreasRpcName, new Action<long, ZPackage>(RPC_RequestProtectedTerrainAreas));
            _protectedTerrainAreasRpcRegistered = true;
            _registeredProtectedTerrainAreasRpc = ZRoutedRpc.instance;
            _protectedTerrainAreasClientSynced = false;
            _nextProtectedTerrainAreaRequestTime = 0f;
        }

        if (ZNet.instance == null)
        {
            return;
        }

        if (ZNet.instance.IsServer())
        {
            if (_protectedTerrainAreasDirty && Time.time >= _nextProtectedTerrainAreaSyncTime)
            {
                BroadcastProtectedTerrainAreas();
                _protectedTerrainAreasDirty = false;
            }

            return;
        }

        if (_protectedTerrainAreasClientSynced || Time.time < _nextProtectedTerrainAreaRequestTime)
        {
            return;
        }

        _nextProtectedTerrainAreaRequestTime = Time.time + ProtectedTerrainAreaRequestRetryInterval;
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RequestProtectedTerrainAreasRpcName, new ZPackage());
        if (TerrainMistilePlugin.DebugLoggingEnabled)
        {
            TerrainMistilePlugin.LogDebugDiagnostic("Requested protected terrain area sync from server.");
        }
    }

    internal static void ResetTerrainAround(Vector3 center, float radius, bool resetPaint)
    {
        if (_resettingTerrain)
        {
            return;
        }

        _resettingTerrain = true;
        try
        {
            int changedCells = 0;
            int protectedSkippedCells = 0;
            TempHeightmaps.Clear();
            Heightmap.FindHeightmap(center, radius, TempHeightmaps);

            foreach (Heightmap hmap in TempHeightmaps)
            {
                if (!hmap)
                {
                    continue;
                }

                TerrainComp terrainComp = TerrainComp.FindTerrainCompiler(((Component)hmap).transform.position);
                if (!terrainComp || !terrainComp.m_initialized)
                {
                    continue;
                }

                if (!terrainComp.IsOwner())
                {
                    terrainComp.m_nview?.ClaimOwnership();
                }

                if (!terrainComp.IsOwner())
                {
                    TerrainMistilePlugin.TerrainMistileLogger.LogDebug("Skipping terrain reset because this peer does not own the TerrainComp.");
                    continue;
                }

                int changedOnHeightmap = ClearTerrainCompRadius(terrainComp, hmap, center, radius, resetPaint, out int protectedSkippedOnHeightmap);
                protectedSkippedCells += protectedSkippedOnHeightmap;
                if (changedOnHeightmap <= 0)
                {
                    continue;
                }

                changedCells += changedOnHeightmap;
                terrainComp.m_operations++;
                terrainComp.m_lastOpPoint = center;
                terrainComp.m_lastOpRadius = radius;
                terrainComp.Save();
                hmap.Poke(delayed: false);
            }

            if (changedCells > 0 && ClutterSystem.instance)
            {
                ClutterSystem.instance.ResetGrass(center, radius);
            }

            if (changedCells > 0)
            {
                CreateResetEffects(center);
            }

            TerrainMistilePlugin.TerrainMistileLogger.LogInfo($"TerrainMistile reset {changedCells} terrain cells around {center}.");
            if (TerrainMistilePlugin.DebugLoggingEnabled)
            {
                TerrainMistilePlugin.LogDebugDiagnostic(
                    $"Terrain reset summary center={FormatPoint(center)}, radius={radius:0.##}, changedCells={changedCells}, protectedSkippedCells={protectedSkippedCells}, {DescribeTerrainProtection(center)}");
            }
        }
        finally
        {
            TempHeightmaps.Clear();
            _resettingTerrain = false;
        }
    }

    private static int ClearTerrainCompRadius(TerrainComp terrainComp, Heightmap hmap, Vector3 center, float radius, bool resetPaint, out int protectedSkipped)
    {
        int width = terrainComp.m_width + 1;
        float radiusSqr = radius * radius;
        int changed = 0;
        protectedSkipped = 0;
        Vector3 hmapPosition = ((Component)hmap).transform.position;

        for (int y = 0; y < width; y++)
        {
            for (int x = 0; x < width; x++)
            {
                GetTerrainCellWorldXZ(hmap, hmapPosition, x, y, out float worldX, out float worldZ);
                if (!IsWithinHorizontalRadius(worldX, worldZ, center, radiusSqr))
                {
                    continue;
                }

                Vector3 cellPoint = new(worldX, center.y, worldZ);
                int index = GetTerrainCellIndex(width, x, y);
                if (IsProtectedTerrainArea(cellPoint, logSkip: false))
                {
                    if (IsResettableTerrainCell(terrainComp, index, resetPaint))
                    {
                        protectedSkipped++;
                    }

                    continue;
                }

                bool cellChanged = false;

                if (index < terrainComp.m_modifiedHeight.Length &&
                    (terrainComp.m_modifiedHeight[index] || terrainComp.m_levelDelta[index] != 0f || terrainComp.m_smoothDelta[index] != 0f))
                {
                    terrainComp.m_modifiedHeight[index] = false;
                    terrainComp.m_levelDelta[index] = 0f;
                    terrainComp.m_smoothDelta[index] = 0f;
                    cellChanged = true;
                }

                if (resetPaint && index < terrainComp.m_modifiedPaint.Length && terrainComp.m_modifiedPaint[index])
                {
                    terrainComp.m_modifiedPaint[index] = false;
                    terrainComp.m_paintMask[index] = Color.black;
                    cellChanged = true;
                }

                if (cellChanged)
                {
                    changed++;
                }
            }
        }

        return changed;
    }

    private static bool IsResettableTerrainCell(TerrainComp terrainComp, int index, bool resetPaint)
    {
        bool heightChanged = index < terrainComp.m_modifiedHeight.Length &&
                             (terrainComp.m_modifiedHeight[index] || terrainComp.m_levelDelta[index] != 0f || terrainComp.m_smoothDelta[index] != 0f);
        bool paintChanged = resetPaint && index < terrainComp.m_modifiedPaint.Length && terrainComp.m_modifiedPaint[index];
        return heightChanged || paintChanged;
    }

    internal static void ClearSpawnUnitRollState()
    {
        LastSpawnRollTimeByUnit.Clear();
        NoModifiedTerrainComps.Clear();
        PlayerBasePiecesByBucket.Clear();
        _playerBasePieceBucketsBuilt = false;
        _nextPlayerBasePieceBucketRefreshTime = 0f;
    }

    private static void CollectEligiblePlayers()
    {
        TempPlayers.Clear();
        foreach (Player player in Player.GetAllPlayers())
        {
            if (!player || player.IsDead())
            {
                continue;
            }

            TempPlayers.Add(player);
        }
    }

    private static bool TryRollSpawnForTerrainUnit(ModifiedTerrainUnitCandidate unit)
    {
        if (!TerrainMistileSpawnRules.TryGetEnabledRule(unit.Biome, out TerrainMistileBiomeSpawnRule rule))
        {
            return false;
        }

        GetNearbyPlayers(unit.TargetPoint, rule.PlayerSearchRadius, TempPlayers, TempNearbyPlayers);
        if (TempNearbyPlayers.Count == 0)
        {
            return false;
        }

        if (LastSpawnRollTimeByUnit.TryGetValue(unit.Key, out float lastSpawnRoll) && Time.time - lastSpawnRoll < rule.Interval)
        {
            return false;
        }

        int activeCount = CountActiveTerrainMistilesNearTarget(unit.TargetPoint);
        int remainingActiveSlots = rule.MaxActiveTerrainMistilesPerArea - activeCount;
        if (remainingActiveSlots <= 0)
        {
            return false;
        }

        LastSpawnRollTimeByUnit[unit.Key] = Time.time;
        float effectiveSpawnChance = rule.GetEffectiveSpawnChance(unit.MaxDeformationPressure);
        if (TerrainMistilePlugin.DebugLoggingEnabled)
        {
            TerrainMistilePlugin.LogDebugDiagnostic(
                $"TerrainMistile spawn roll unit={unit.Key}, target={FormatPoint(unit.TargetPoint)}, chance={effectiveSpawnChance:0.###}, deformationPressure={unit.MaxDeformationPressure:0.###}, nearbyPlayers={TempNearbyPlayers.Count}, activeNearTarget={activeCount}, {DescribeTerrainProtection(unit.TargetPoint)}");
        }
        if (Random.value > effectiveSpawnChance)
        {
            TerrainMistilePlugin.TerrainMistileLogger.LogDebug($"TerrainMistile spawn roll failed for terrain unit {unit.Key} in {TerrainMistileSpawnRules.GetBiomeName(unit.Biome)}. chance={effectiveSpawnChance:0.###}, deformationPressure={unit.MaxDeformationPressure:0.###}");
            return false;
        }

        int desiredSpawnCount = rule.ScaleSpawnsWithNearbyPlayers
            ? TempNearbyPlayers.Count
            : 1;
        int spawnCount = Mathf.Min(desiredSpawnCount, remainingActiveSlots);
        if (spawnCount <= 0)
        {
            return false;
        }

        bool spawnedAny = false;
        int playerOffset = TempNearbyPlayers.Count > 1 ? Random.Range(0, TempNearbyPlayers.Count) : 0;
        for (int i = 0; i < spawnCount; i++)
        {
            Vector3 terrainTarget = unit.TargetPoint;
            if (i > 0 && !TryFindModifiedTerrainNear(unit.TargetPoint, ActiveTerrainMistileAreaRadius, out terrainTarget))
            {
                break;
            }

            int targetBiome = TerrainMistileSpawnRules.GetBiomeKey(terrainTarget);
            if (!TerrainMistileSpawnRules.TryGetEnabledRule(targetBiome, out TerrainMistileBiomeSpawnRule targetRule))
            {
                break;
            }

            if (CountActiveTerrainMistilesNearTarget(terrainTarget) >= targetRule.MaxActiveTerrainMistilesPerArea)
            {
                break;
            }

            Player spawnPlayer = TempNearbyPlayers[(playerOffset + i) % TempNearbyPlayers.Count];
            if (!TryFindSpawnPoint(spawnPlayer, targetRule, out Vector3 spawnPoint))
            {
                continue;
            }

            if (TerrainMistilePlugin.DebugLoggingEnabled)
            {
                TerrainMistilePlugin.LogDebugDiagnostic(
                    $"TerrainMistile spawn selected target={FormatPoint(terrainTarget)}, spawnPoint={FormatPoint(spawnPoint)}, resetRadius={targetRule.ResetRadius:0.##}, {DescribeTerrainProtection(terrainTarget)}");
            }
            spawnedAny |= SpawnTerrainMistile(spawnPlayer, spawnPoint, terrainTarget, targetRule.ResetRadius, targetRule.Health);
        }

        return spawnedAny;
    }

    private static void GetNearbyPlayers(Vector3 point, float range, List<Player> players, List<Player> nearbyPlayers)
    {
        nearbyPlayers.Clear();
        float rangeSqr = range * range;
        foreach (Player player in players)
        {
            if (!player)
            {
                continue;
            }

            Vector3 playerPosition = ((Component)player).transform.position;
            if (HorizontalDistanceSqr(playerPosition, point) <= rangeSqr)
            {
                nearbyPlayers.Add(player);
            }
        }
    }

    private static void CleanupSpawnUnitRollState()
    {
        TempSpawnUnitKeys.Clear();
        TempCooldownSpawnUnitKeys.Clear();
        float now = Time.time;
        foreach (KeyValuePair<SpawnUnitKey, float> entry in LastSpawnRollTimeByUnit)
        {
            if (!ModifiedTerrainUnits.ContainsKey(entry.Key) && now - entry.Value > SpawnUnitRollStateRetention)
            {
                TempSpawnUnitKeys.Add(entry.Key);
            }
        }

        foreach (SpawnUnitKey key in TempSpawnUnitKeys)
        {
            LastSpawnRollTimeByUnit.Remove(key);
        }
    }

    private static void CollectModifiedTerrainUnits()
    {
        CleanupTargetState();
        CleanupNoModifiedTerrainComps();
        ModifiedTerrainUnits.Clear();
        TempCooldownSpawnUnitKeys.Clear();
        float maxPlayerSearchRadius = TerrainMistileSpawnRules.MaxPlayerSearchRadius;
        if (maxPlayerSearchRadius <= 0f)
        {
            return;
        }

        foreach (TerrainComp terrainComp in TerrainComp.s_instances)
        {
            if (!terrainComp || !terrainComp.m_initialized || !terrainComp.IsOwner() || !terrainComp.m_hmap)
            {
                continue;
            }

            if (!IsTerrainCompNearAnyPlayer(terrainComp, maxPlayerSearchRadius))
            {
                continue;
            }

            if (IsKnownNoModifiedTerrainComp(terrainComp))
            {
                continue;
            }

            CollectModifiedTerrainUnits(terrainComp);
        }
    }

    private static void CollectModifiedTerrainUnits(TerrainComp terrainComp)
    {
        if (IsKnownNoModifiedTerrainComp(terrainComp))
        {
            return;
        }

        bool foundModifiedHeightCell = false;
        int width = terrainComp.m_width + 1;
        Heightmap hmap = terrainComp.m_hmap;
        Vector3 hmapPosition = ((Component)hmap).transform.position;

        for (int y = 0; y < width; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = GetTerrainCellIndex(width, x, y);
                if (!IsModifiedHeightCell(terrainComp, index))
                {
                    continue;
                }

                foundModifiedHeightCell = true;
                GetTerrainCellWorldXZ(hmap, hmapPosition, x, y, out float worldX, out float worldZ);
                Vector3 candidate = new(worldX, hmapPosition.y, worldZ);

                int biome = TerrainMistileSpawnRules.GetBiomeKey(candidate);
                if (!TerrainMistileSpawnRules.TryGetEnabledRule(biome, out TerrainMistileBiomeSpawnRule rule))
                {
                    continue;
                }

                SpawnUnitKey key = GetSpawnUnitKey(candidate, biome);
                if (TempCooldownSpawnUnitKeys.Contains(key) || IsSpawnUnitWaitingForInterval(key, rule))
                {
                    TempCooldownSpawnUnitKeys.Add(key);
                    continue;
                }

                if (!IsEligibleModifiedTerrainTarget(candidate, rule, logSkip: false))
                {
                    continue;
                }

                float deformationPressure = GetHeightDeformationPressure(terrainComp, index);
                Vector3 unitCenter = GetSpawnUnitCenter(key);
                float score = GetTargetSpreadScore(candidate, unitCenter, deformationPressure);
                if (ModifiedTerrainUnits.TryGetValue(key, out ModifiedTerrainUnitCandidate unit))
                {
                    unit.ModifiedCellCount++;
                    unit.MaxDeformationPressure = Mathf.Max(unit.MaxDeformationPressure, deformationPressure);
                    if (score > unit.BestScore)
                    {
                        unit.TargetPoint = candidate;
                        unit.BestScore = score;
                    }

                    continue;
                }

                ModifiedTerrainUnits[key] = new ModifiedTerrainUnitCandidate
                {
                    Key = key,
                    Biome = biome,
                    TargetPoint = candidate,
                    BestScore = score,
                    MaxDeformationPressure = deformationPressure,
                    ModifiedCellCount = 1
                };
            }
        }

        UpdateNoModifiedTerrainCompCache(terrainComp, foundModifiedHeightCell);
    }

    private static bool TryFindModifiedTerrainNear(Vector3 center, float range, out Vector3 modifiedPoint)
    {
        CleanupTargetState();

        float rangeSqr = range * range;
        bool found = false;
        float bestScore = float.MinValue;
        Vector3 bestPoint = default;

        foreach (TerrainComp terrainComp in TerrainComp.s_instances)
        {
            if (!terrainComp || !terrainComp.m_initialized || !terrainComp.IsOwner() || !terrainComp.m_hmap)
            {
                continue;
            }

            if (IsKnownNoModifiedTerrainComp(terrainComp))
            {
                continue;
            }

            float halfSizeWithRange = terrainComp.m_size / 2f + range;
            Vector3 terrainCompPosition = ((Component)terrainComp).transform.position;
            if (center.x < terrainCompPosition.x - halfSizeWithRange ||
                center.x > terrainCompPosition.x + halfSizeWithRange ||
                center.z < terrainCompPosition.z - halfSizeWithRange ||
                center.z > terrainCompPosition.z + halfSizeWithRange)
            {
                continue;
            }

            bool foundModifiedHeightCell = TryFindBestModifiedCellNear(terrainComp, center, rangeSqr, ref found, ref bestScore, ref bestPoint);
            UpdateNoModifiedTerrainCompCache(terrainComp, foundModifiedHeightCell);
        }

        modifiedPoint = bestPoint;
        return found;
    }

    private static bool TryFindBestModifiedCellNear(
        TerrainComp terrainComp,
        Vector3 center,
        float rangeSqr,
        ref bool found,
        ref float bestScore,
        ref Vector3 bestPoint)
    {
        int width = terrainComp.m_width + 1;
        Heightmap hmap = terrainComp.m_hmap;
        Vector3 hmapPosition = ((Component)hmap).transform.position;
        bool foundModifiedHeightCell = false;

        for (int y = 0; y < width; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = GetTerrainCellIndex(width, x, y);
                if (!IsModifiedHeightCell(terrainComp, index))
                {
                    continue;
                }

                foundModifiedHeightCell = true;
                GetTerrainCellWorldXZ(hmap, hmapPosition, x, y, out float worldX, out float worldZ);
                if (!IsWithinHorizontalRadius(worldX, worldZ, center, rangeSqr))
                {
                    continue;
                }

                Vector3 candidate = new(worldX, center.y, worldZ);
                int biome = TerrainMistileSpawnRules.GetBiomeKey(candidate);
                if (!TerrainMistileSpawnRules.TryGetEnabledRule(biome, out TerrainMistileBiomeSpawnRule rule))
                {
                    continue;
                }

                if (!IsEligibleModifiedTerrainTarget(candidate, rule, logSkip: false))
                {
                    continue;
                }

                float deformationPressure = GetHeightDeformationPressure(terrainComp, index);
                float score = GetTargetSpreadScore(candidate, center, deformationPressure);
                if (!found || score > bestScore)
                {
                    found = true;
                    bestScore = score;
                    bestPoint = candidate;
                }
            }
        }

        return foundModifiedHeightCell;
    }

    private static bool IsModifiedHeightCell(TerrainComp terrainComp, int index)
    {
        return index < terrainComp.m_modifiedHeight.Length &&
               (terrainComp.m_modifiedHeight[index] || terrainComp.m_levelDelta[index] != 0f || terrainComp.m_smoothDelta[index] != 0f);
    }

    private static float GetHeightDeformationPressure(TerrainComp terrainComp, int index)
    {
        float delta = 0f;
        if (index < terrainComp.m_levelDelta.Length)
        {
            delta += terrainComp.m_levelDelta[index];
        }

        if (index < terrainComp.m_smoothDelta.Length)
        {
            delta += terrainComp.m_smoothDelta[index];
        }

        return Mathf.Clamp01(Mathf.Abs(delta) / TerrainHeightDeformationCap);
    }

    private static bool IsSpawnUnitWaitingForInterval(SpawnUnitKey key, TerrainMistileBiomeSpawnRule rule)
    {
        return LastSpawnRollTimeByUnit.TryGetValue(key, out float lastSpawnRoll) && Time.time - lastSpawnRoll < rule.Interval;
    }

    private static bool IsEligibleModifiedTerrainTarget(Vector3 point, TerrainMistileBiomeSpawnRule rule, bool logSkip)
    {
        if (IsIgnoredByExternalTerrain(point, logSkip))
        {
            return false;
        }

        if (IsProtectedTerrainArea(point, logSkip))
        {
            return false;
        }

        if (IsIgnoredByPlayerBase(point, rule, logSkip))
        {
            return false;
        }

        if (IsTargetSuppressedWithoutCleanup(point, logSkip))
        {
            return false;
        }

        return true;
    }

    private static bool IsIgnoredByPlayerBase(Vector3 point, TerrainMistileBiomeSpawnRule rule, bool logSkip)
    {
        int threshold = rule.IgnorePlayerBaseBaseValue;
        if (threshold <= 0 || rule.BaseCheckRadius <= 0f)
        {
            return false;
        }

        int baseValue = GetUniquePlayerBasePrefabCount(point, threshold, rule.BaseCheckRadius);
        if (baseValue < threshold)
        {
            return false;
        }

        if (logSkip)
        {
            TerrainMistilePlugin.TerrainMistileLogger.LogDebug($"Skipping TerrainMistile spawn roll at {point}; unique player base prefab count {baseValue} >= configured threshold {threshold} within {rule.BaseCheckRadius:0.##}m.");
        }

        return true;
    }

    private static int GetUniquePlayerBasePrefabCount(Vector3 point, int threshold, float radius)
    {
        EnsurePlayerBasePieceBuckets();
        TempPlayerBasePrefabNames.Clear();
        float radiusSqr = radius * radius;
        PlayerBasePieceBucketKey centerKey = GetPlayerBasePieceBucketKey(point);
        int bucketRange = Mathf.CeilToInt(radius / SpawnUnitSize);

        for (int z = centerKey.Z - bucketRange; z <= centerKey.Z + bucketRange; z++)
        {
            for (int x = centerKey.X - bucketRange; x <= centerKey.X + bucketRange; x++)
            {
                PlayerBasePieceBucketKey key = new(x, z);
                if (!PlayerBasePiecesByBucket.TryGetValue(key, out List<PlayerBasePiece> pieces))
                {
                    continue;
                }

                foreach (PlayerBasePiece piece in pieces)
                {
                    if (HorizontalDistanceSqr(point, piece.Position) > radiusSqr)
                    {
                        continue;
                    }

                    TempPlayerBasePrefabNames.Add(piece.PrefabName);
                    if (TempPlayerBasePrefabNames.Count >= threshold)
                    {
                        return TempPlayerBasePrefabNames.Count;
                    }
                }
            }
        }

        return TempPlayerBasePrefabNames.Count;
    }

    private static void EnsurePlayerBasePieceBuckets()
    {
        if (_playerBasePieceBucketsBuilt && Time.time < _nextPlayerBasePieceBucketRefreshTime)
        {
            return;
        }

        PlayerBasePiecesByBucket.Clear();
        foreach (Piece piece in Piece.s_allPieces)
        {
            if (!piece)
            {
                continue;
            }

            if (!HasNonZeroCreator(piece))
            {
                continue;
            }

            GameObject gameObject = ((Component)piece).gameObject;
            string prefabName = GetStablePrefabName(gameObject);
            if (!TerrainMistileSpawnRules.IsPlayerBasePrefabName(prefabName))
            {
                continue;
            }

            Vector3 position = ((Component)piece).transform.position;
            PlayerBasePieceBucketKey key = GetPlayerBasePieceBucketKey(position);
            if (!PlayerBasePiecesByBucket.TryGetValue(key, out List<PlayerBasePiece> pieces))
            {
                pieces = new List<PlayerBasePiece>();
                PlayerBasePiecesByBucket[key] = pieces;
            }

            pieces.Add(new PlayerBasePiece(prefabName, position));
        }

        _playerBasePieceBucketsBuilt = true;
        _nextPlayerBasePieceBucketRefreshTime = Time.time + PlayerBasePieceBucketRefreshInterval;
    }

    private static bool HasNonZeroCreator(Piece piece)
    {
        ZNetView zNetView = ((Component)piece).GetComponent<ZNetView>();
        ZDO? zdo = zNetView ? zNetView.GetZDO() : null;
        return zdo != null && zdo.GetLong(ZDOVars.s_creator, 0L) != 0L;
    }

    private static PlayerBasePieceBucketKey GetPlayerBasePieceBucketKey(Vector3 point)
    {
        return new PlayerBasePieceBucketKey(
            Mathf.FloorToInt(point.x / SpawnUnitSize),
            Mathf.FloorToInt(point.z / SpawnUnitSize));
    }

    private static bool IsKnownNoModifiedTerrainComp(TerrainComp terrainComp)
    {
        if (!NoModifiedTerrainComps.TryGetValue(terrainComp, out NoModifiedTerrainCompCache cache))
        {
            return false;
        }

        if (Time.time < cache.ExpireTime && terrainComp.m_operations == cache.Operations)
        {
            return true;
        }

        NoModifiedTerrainComps.Remove(terrainComp);
        return false;
    }

    private static void UpdateNoModifiedTerrainCompCache(TerrainComp terrainComp, bool foundModifiedHeightCell)
    {
        if (foundModifiedHeightCell)
        {
            NoModifiedTerrainComps.Remove(terrainComp);
            return;
        }

        NoModifiedTerrainComps[terrainComp] = new NoModifiedTerrainCompCache(
            terrainComp.m_operations,
            Time.time + NoModifiedTerrainCompRetention);
    }

    private static void CleanupNoModifiedTerrainComps()
    {
        TempNoModifiedTerrainComps.Clear();
        float now = Time.time;
        foreach (KeyValuePair<TerrainComp, NoModifiedTerrainCompCache> entry in NoModifiedTerrainComps)
        {
            TerrainComp terrainComp = entry.Key;
            if (!terrainComp || entry.Value.ExpireTime <= now || terrainComp.m_operations != entry.Value.Operations)
            {
                TempNoModifiedTerrainComps.Add(terrainComp!);
            }
        }

        foreach (TerrainComp terrainComp in TempNoModifiedTerrainComps)
        {
            NoModifiedTerrainComps.Remove(terrainComp);
        }
    }

    private static string GetStablePrefabName(GameObject gameObject)
    {
        string name = gameObject ? gameObject.name : "";
        int cloneIndex = name.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase);
        if (cloneIndex >= 0)
        {
            name = name.Substring(0, cloneIndex);
        }

        int variantIndex = name.IndexOf('(');
        if (variantIndex > 0)
        {
            name = name.Substring(0, variantIndex);
        }

        name = name.Trim();
        return name.Length > 0 ? name : "unknown";
    }

    private static void CreateResetEffects(Vector3 center)
    {
        if (ZRoutedRpc.instance != null && _resetEffectsRpcRegistered)
        {
            ZPackage package = new();
            package.Write(center);
            package.Write(ResetVfxPrefabName);
            package.Write(ResetSfxPrefabName);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, ResetEffectsRpcName, package);
            return;
        }

        CreateResetEffectsLocal(center, ResetVfxPrefabName, ResetSfxPrefabName);
    }

    private static void RPC_ResetEffects(long sender, ZPackage package)
    {
        Vector3 center = package.ReadVector3();
        string vfxPrefab = package.ReadString();
        string sfxPrefab = package.ReadString();
        CreateResetEffectsLocal(center, vfxPrefab, sfxPrefab);
    }

    private static void RPC_RequestProtectedTerrainAreas(long sender, ZPackage package)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        BroadcastProtectedTerrainAreas();
        _protectedTerrainAreasDirty = false;
        if (TerrainMistilePlugin.DebugLoggingEnabled)
        {
            TerrainMistilePlugin.LogDebugDiagnostic(
                $"Protected terrain area sync requested by peer={sender}; sent count={ProtectedTerrainAreas.Count}.");
        }
    }

    private static void RPC_ProtectedTerrainAreas(long sender, ZPackage package)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            return;
        }

        try
        {
            int count = Mathf.Max(0, package.ReadInt());
            ProtectedTerrainAreas.Clear();
            for (int i = 0; i < count; i++)
            {
                string source = package.ReadString();
                Vector3 center = package.ReadVector3();
                float radius = package.ReadSingle();
                if (radius <= 0f)
                {
                    continue;
                }

                ProtectedTerrainAreas.Add(new ExternalTerrainIgnoreArea
                {
                    Center = center,
                    Radius = radius,
                    Source = string.IsNullOrWhiteSpace(source) ? "protected terrain" : source
                });
            }

            MarkProtectedTerrainAreaBucketsDirty();
            _protectedTerrainAreasClientSynced = true;
            if (TerrainMistilePlugin.DebugLoggingEnabled)
            {
                TerrainMistilePlugin.LogDebugDiagnostic(
                    $"Received protected terrain area sync from peer={sender}; protectedCount={ProtectedTerrainAreas.Count}.");
            }
        }
        catch (Exception ex)
        {
            TerrainMistilePlugin.TerrainMistileLogger.LogWarning($"Failed to read TerrainMistile protected terrain area sync: {ex.Message}");
        }
    }

    private static void BroadcastProtectedTerrainAreas()
    {
        if (ZRoutedRpc.instance == null || !_protectedTerrainAreasRpcRegistered)
        {
            return;
        }

        ZPackage package = new();
        package.Write(ProtectedTerrainAreas.Count);
        foreach (ExternalTerrainIgnoreArea area in ProtectedTerrainAreas)
        {
            package.Write(area.Source);
            package.Write(area.Center);
            package.Write(area.Radius);
        }

        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, ProtectedTerrainAreasRpcName, package);
        if (TerrainMistilePlugin.DebugLoggingEnabled)
        {
            TerrainMistilePlugin.LogDebugDiagnostic($"Broadcast protected terrain area sync. protectedCount={ProtectedTerrainAreas.Count}.");
        }
    }

    private static void CreateResetEffectsLocal(Vector3 center, string vfxPrefab, string sfxPrefab)
    {
        Vector3 effectPoint = center;
        if (TryGetGroundHeight(effectPoint, out float groundHeight))
        {
            effectPoint.y = groundHeight + ResetEffectGroundOffset;
        }

        CreateEffectPrefab(vfxPrefab, effectPoint);
        CreateEffectPrefab(sfxPrefab, effectPoint);
    }

    private static void CreateEffectPrefab(string prefabName, Vector3 point)
    {
        prefabName = prefabName.Trim();
        if (prefabName.Length == 0)
        {
            return;
        }

        GameObject? prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(prefabName) : null;
        if (!prefab)
        {
            TerrainMistilePlugin.TerrainMistileLogger.LogWarning($"Could not create TerrainMistile reset effect '{prefabName}': prefab is not registered in ZNetScene.");
            return;
        }

        GameObject instance = Object.Instantiate(prefab, point, Quaternion.identity);
        Object.Destroy(instance, FallbackEffectDestroyDelay);
    }

    private static bool SpawnTerrainMistile(Player target, Vector3 spawnPoint, Vector3 terrainOperationPoint, float resetRadius, float health)
    {
        GameObject? prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(TerrainMistilePrefab.PrefabName) : null;
        if (!prefab)
        {
            TerrainMistilePlugin.TerrainMistileLogger.LogWarning($"Could not spawn {TerrainMistilePrefab.PrefabName}: prefab is not registered in ZNetScene yet.");
            return false;
        }

        Vector3 direction = ((Component)target).transform.position - spawnPoint;
        direction.y = 0f;
        Quaternion rotation = direction.sqrMagnitude > 0.001f ? Quaternion.LookRotation(direction.normalized, Vector3.up) : Quaternion.identity;
        GameObject instance = Object.Instantiate(prefab, spawnPoint, rotation);

        TerrainMistileBehaviour behaviour = instance.GetComponent<TerrainMistileBehaviour>();
        if (behaviour)
        {
            behaviour.Initialize(target, terrainOperationPoint, resetRadius, health);
        }

        TerrainMistilePlugin.TerrainMistileLogger.LogDebug($"Spawned {TerrainMistilePrefab.PrefabName} near {target.GetPlayerName()}.");

        return true;
    }

    private static bool TryFindSpawnPoint(Player player, TerrainMistileBiomeSpawnRule rule, out Vector3 spawnPoint)
    {
        Vector3 playerPosition = ((Component)player).transform.position;
        float minRadius = Mathf.Min(rule.SpawnRadiusMin, rule.SpawnRadiusMax);
        float maxRadius = Mathf.Max(rule.SpawnRadiusMin, rule.SpawnRadiusMax);

        for (int attempt = 0; attempt < 24; attempt++)
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float distance = Random.Range(minRadius, maxRadius);
            Vector3 candidate = playerPosition + new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);

            if (ZoneSystem.instance && !ZoneSystem.instance.IsZoneLoaded(candidate))
            {
                continue;
            }

            if (TryGetGroundHeight(candidate, out float groundHeight))
            {
                candidate.y = groundHeight + rule.SpawnAltitude;
                spawnPoint = candidate;
                return true;
            }
        }

        spawnPoint = playerPosition + Vector3.up * rule.SpawnAltitude;
        return true;
    }

    internal static bool TryGetGroundHeight(Vector3 point, out float height)
    {
        if (ZoneSystem.instance && ZoneSystem.instance.GetSolidHeight(point, out height))
        {
            return true;
        }

        return Heightmap.GetHeight(point, out height);
    }

    private static int CountActiveTerrainMistilesNearTarget(Vector3 targetPoint)
    {
        float areaRadiusSqr = ActiveTerrainMistileAreaRadius * ActiveTerrainMistileAreaRadius;
        int count = 0;
        for (int i = ActiveTerrainMistiles.Count - 1; i >= 0; i--)
        {
            TerrainMistileBehaviour behaviour = ActiveTerrainMistiles[i];
            if (!behaviour)
            {
                ActiveTerrainMistiles.RemoveAt(i);
                continue;
            }

            Character character = ((Component)behaviour).GetComponent<Character>();
            if (!character || character.IsDead())
            {
                continue;
            }

            Vector3 existingTargetPoint = ((Component)character).transform.position;
            if (behaviour.TryGetTerrainTarget(out Vector3 terrainTarget))
            {
                existingTargetPoint = terrainTarget;
            }

            if (HorizontalDistanceSqr(existingTargetPoint, targetPoint) <= areaRadiusSqr)
            {
                count++;
            }
        }

        return count;
    }

    internal static void RegisterActiveTerrainMistile(TerrainMistileBehaviour behaviour)
    {
        if (!behaviour || ActiveTerrainMistiles.Contains(behaviour))
        {
            return;
        }

        ActiveTerrainMistiles.Add(behaviour);
    }

    internal static void UnregisterActiveTerrainMistile(TerrainMistileBehaviour behaviour)
    {
        ActiveTerrainMistiles.Remove(behaviour);
    }

    internal static void ReserveTerrainTarget(Vector3 point, float radius)
    {
        CleanupTargetState();
        if (radius <= 0f)
        {
            return;
        }

        TargetReservations.Add(new TargetReservation
        {
            Point = point,
            Radius = radius,
            ExpireTime = Time.time + TargetReservationDuration
        });
    }

    internal static void ReleaseTerrainTarget(Vector3 point)
    {
        CleanupTargetState();
        for (int i = TargetReservations.Count - 1; i >= 0; i--)
        {
            if (HorizontalDistanceSqr(TargetReservations[i].Point, point) <= 1f)
            {
                TargetReservations.RemoveAt(i);
                return;
            }
        }
    }

    internal static bool HasModifiedTerrainAround(Vector3 center, float radius)
    {
        float radiusSqr = radius * radius;
        foreach (TerrainComp terrainComp in TerrainComp.s_instances)
        {
            if (!terrainComp || !terrainComp.m_initialized || !terrainComp.m_hmap)
            {
                continue;
            }

            if (IsKnownNoModifiedTerrainComp(terrainComp))
            {
                continue;
            }

            if (!IsTerrainCompNearPoint(terrainComp, center, radius))
            {
                continue;
            }

            bool hasModifiedCellInRadius = HasModifiedCellInRadius(terrainComp, center, radiusSqr, out bool foundModifiedHeightCell);
            UpdateNoModifiedTerrainCompCache(terrainComp, foundModifiedHeightCell);
            if (hasModifiedCellInRadius)
            {
                return true;
            }
        }

        return false;
    }

    internal static void RegisterExternalTerrainIgnoreArea(Vector3 center, float radius, string source, float durationSeconds = 0f)
    {
        if (radius <= 0f)
        {
            return;
        }

        CleanupExternalTerrainIgnoreAreas();
        source = string.IsNullOrWhiteSpace(source) ? "external terrain" : source.Trim();
        float expireTime = durationSeconds > 0f ? Time.time + durationSeconds : 0f;
        float mergeDistanceSqr = ExternalTerrainIgnoreMergeDistance * ExternalTerrainIgnoreMergeDistance;
        foreach (ExternalTerrainIgnoreArea area in ExternalTerrainIgnoreAreas)
        {
            if (!string.Equals(area.Source, source, StringComparison.OrdinalIgnoreCase) ||
                HorizontalDistanceSqr(area.Center, center) > mergeDistanceSqr)
            {
                continue;
            }

            area.Center = center;
            area.Radius = Mathf.Max(area.Radius, radius);
            area.ExpireTime = MergeExpireTime(area.ExpireTime, expireTime);
            return;
        }

        ExternalTerrainIgnoreAreas.Add(new ExternalTerrainIgnoreArea
        {
            Center = center,
            Radius = radius,
            Source = source,
            ExpireTime = expireTime
        });

        string duration = durationSeconds > 0f ? $" for {durationSeconds:0.#}s" : "";
        TerrainMistilePlugin.TerrainMistileLogger.LogDebug($"Ignoring TerrainMistile reactions for {source} at {center}, radius {radius:0.##}{duration}.");
    }

    internal static void ClearProtectedTerrainAreas(string sourcePrefix)
    {
        if (string.IsNullOrWhiteSpace(sourcePrefix))
        {
            return;
        }

        int removed = 0;
        for (int i = ProtectedTerrainAreas.Count - 1; i >= 0; i--)
        {
            if (ProtectedTerrainAreas[i].Source.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                ProtectedTerrainAreas.RemoveAt(i);
                removed++;
            }
        }

        if (removed > 0)
        {
            TerrainMistilePlugin.TerrainMistileLogger.LogDebug($"Cleared {removed} TerrainMistile protected terrain area(s) for source prefix '{sourcePrefix}'.");
            if (TerrainMistilePlugin.DebugLoggingEnabled)
            {
                TerrainMistilePlugin.LogDebugDiagnostic($"Cleared {removed} protected terrain area(s) for sourcePrefix='{sourcePrefix}'.");
            }

            MarkProtectedTerrainAreasDirty();
        }
    }

    internal static void RegisterProtectedTerrainArea(Vector3 center, float radius, string source)
    {
        if (radius <= 0f)
        {
            return;
        }

        source = string.IsNullOrWhiteSpace(source) ? "protected terrain" : source.Trim();
        float mergeDistanceSqr = ExternalTerrainIgnoreMergeDistance * ExternalTerrainIgnoreMergeDistance;
        foreach (ExternalTerrainIgnoreArea area in ProtectedTerrainAreas)
        {
            if (!string.Equals(area.Source, source, StringComparison.OrdinalIgnoreCase) ||
                HorizontalDistanceSqr(area.Center, center) > mergeDistanceSqr)
            {
                continue;
            }

            float previousRadius = area.Radius;
            area.Center = center;
            area.Radius = Mathf.Max(area.Radius, radius);
            TerrainMistilePlugin.TerrainMistileLogger.LogDebug(
                $"Updated TerrainMistile protected terrain area for {source} at {FormatPoint(center)}, radius {previousRadius:0.##}->{area.Radius:0.##}. ProtectedCount={ProtectedTerrainAreas.Count}.");
            if (TerrainMistilePlugin.DebugLoggingEnabled)
            {
                TerrainMistilePlugin.LogDebugDiagnostic(
                    $"Updated protected terrain area source='{source}', center={FormatPoint(center)}, radius {previousRadius:0.##}->{area.Radius:0.##}, protectedCount={ProtectedTerrainAreas.Count}.");
            }

            MarkProtectedTerrainAreasDirty();
            return;
        }

        ProtectedTerrainAreas.Add(new ExternalTerrainIgnoreArea
        {
            Center = center,
            Radius = radius,
            Source = source
        });

        TerrainMistilePlugin.TerrainMistileLogger.LogDebug(
            $"Registered TerrainMistile protected terrain area for {source} at {FormatPoint(center)}, radius {radius:0.##}. ProtectedCount={ProtectedTerrainAreas.Count}.");
        if (TerrainMistilePlugin.DebugLoggingEnabled)
        {
            TerrainMistilePlugin.LogDebugDiagnostic(
                $"Registered protected terrain area source='{source}', center={FormatPoint(center)}, radius={radius:0.##}, protectedCount={ProtectedTerrainAreas.Count}.");
        }

        MarkProtectedTerrainAreasDirty();
    }

    private static void MarkProtectedTerrainAreasDirty()
    {
        MarkProtectedTerrainAreaBucketsDirty();
        _protectedTerrainAreasDirty = true;
        _nextProtectedTerrainAreaSyncTime = Time.time + ProtectedTerrainAreaSyncDelay;
    }

    internal static void RegisterLocationTerrainLoadGrace(ZoneSystem.ZoneLocation location, Vector3 position)
    {
        if (location == null || location.m_exteriorRadius <= 0f)
        {
            return;
        }

        RegisterExternalTerrainIgnoreArea(
            position,
            location.m_exteriorRadius + LocationTerrainProtectionPadding,
            $"location load terrain {location.m_prefabName}",
            LocationTerrainIgnoreDuration);
        TerrainMistilePlugin.TerrainMistileLogger.LogDebug(
            $"Registered TerrainMistile location load grace for {location.m_prefabName} at {FormatPoint(position)}, radius {location.m_exteriorRadius + LocationTerrainProtectionPadding:0.##}, duration {LocationTerrainIgnoreDuration:0.#}s.");
    }

    private static bool IsIgnoredByExternalTerrain(Vector3 point, bool logSkip)
    {
        CleanupExternalTerrainIgnoreAreas();
        return IsIgnoredByTerrainAreas(point, ExternalTerrainIgnoreAreas, logSkip);
    }

    private static bool IsProtectedTerrainArea(Vector3 point, bool logSkip)
    {
        if (ProtectedTerrainAreas.Count == 0)
        {
            return false;
        }

        RebuildProtectedTerrainAreaBucketsIfNeeded();
        TerrainAreaBucketKey bucketKey = GetTerrainAreaBucketKey(point);
        if (!ProtectedTerrainAreasByBucket.TryGetValue(bucketKey, out List<ExternalTerrainIgnoreArea> areas))
        {
            return false;
        }

        foreach (ExternalTerrainIgnoreArea area in areas)
        {
            if (HorizontalDistanceSqr(point, area.Center) > area.Radius * area.Radius)
            {
                continue;
            }

            if (logSkip)
            {
                TerrainMistilePlugin.TerrainMistileLogger.LogDebug($"Skipping TerrainMistile terrain reaction at {point}; inside {area.Source} ignore area near {area.Center}.");
            }

            return true;
        }

        return false;
    }

    internal static string DescribeTerrainProtection(Vector3 point)
    {
        CleanupExternalTerrainIgnoreAreas();
        return $"ProtectedAreas={ProtectedTerrainAreas.Count} {DescribeNearestTerrainArea(point, ProtectedTerrainAreas, includeExpiry: false)}; " +
               $"TemporaryIgnoreAreas={ExternalTerrainIgnoreAreas.Count} {DescribeNearestTerrainArea(point, ExternalTerrainIgnoreAreas, includeExpiry: true)}.";
    }

    internal static string FormatPoint(Vector3 point)
    {
        return $"({point.x:0.##}, {point.y:0.##}, {point.z:0.##})";
    }

    private static string DescribeNearestTerrainArea(Vector3 point, List<ExternalTerrainIgnoreArea> areas, bool includeExpiry)
    {
        if (areas.Count == 0)
        {
            return "nearest=none";
        }

        ExternalTerrainIgnoreArea nearest = areas[0];
        float nearestDistanceSqr = HorizontalDistanceSqr(point, nearest.Center);
        for (int i = 1; i < areas.Count; i++)
        {
            float distanceSqr = HorizontalDistanceSqr(point, areas[i].Center);
            if (distanceSqr >= nearestDistanceSqr)
            {
                continue;
            }

            nearest = areas[i];
            nearestDistanceSqr = distanceSqr;
        }

        float distance = Mathf.Sqrt(nearestDistanceSqr);
        float gap = distance - nearest.Radius;
        string expiry = includeExpiry && nearest.ExpireTime > 0f ? $", remaining={Mathf.Max(0f, nearest.ExpireTime - Time.time):0.#}s" : "";
        return $"nearest='{nearest.Source}', center={FormatPoint(nearest.Center)}, radius={nearest.Radius:0.##}, distance={distance:0.##}, gap={gap:0.##}{expiry}";
    }

    private static void CleanupExternalTerrainIgnoreAreas()
    {
        for (int i = ExternalTerrainIgnoreAreas.Count - 1; i >= 0; i--)
        {
            float expireTime = ExternalTerrainIgnoreAreas[i].ExpireTime;
            if (expireTime > 0f && Time.time >= expireTime)
            {
                ExternalTerrainIgnoreAreas.RemoveAt(i);
            }
        }
    }

    private static void MarkProtectedTerrainAreaBucketsDirty()
    {
        _protectedTerrainAreaBucketsDirty = true;
    }

    private static void RebuildProtectedTerrainAreaBucketsIfNeeded()
    {
        if (!_protectedTerrainAreaBucketsDirty)
        {
            return;
        }

        ProtectedTerrainAreasByBucket.Clear();
        foreach (ExternalTerrainIgnoreArea area in ProtectedTerrainAreas)
        {
            if (area.Radius <= 0f)
            {
                continue;
            }

            int minX = GetTerrainAreaBucketCoord(area.Center.x - area.Radius);
            int maxX = GetTerrainAreaBucketCoord(area.Center.x + area.Radius);
            int minZ = GetTerrainAreaBucketCoord(area.Center.z - area.Radius);
            int maxZ = GetTerrainAreaBucketCoord(area.Center.z + area.Radius);
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    TerrainAreaBucketKey key = new(x, z);
                    if (!ProtectedTerrainAreasByBucket.TryGetValue(key, out List<ExternalTerrainIgnoreArea> bucketAreas))
                    {
                        bucketAreas = new List<ExternalTerrainIgnoreArea>();
                        ProtectedTerrainAreasByBucket[key] = bucketAreas;
                    }

                    bucketAreas.Add(area);
                }
            }
        }

        _protectedTerrainAreaBucketsDirty = false;
        if (TerrainMistilePlugin.DebugLoggingEnabled)
        {
            TerrainMistilePlugin.LogDebugDiagnostic(
                $"Rebuilt protected terrain area buckets. protectedCount={ProtectedTerrainAreas.Count}, bucketCount={ProtectedTerrainAreasByBucket.Count}.");
        }
    }

    private static TerrainAreaBucketKey GetTerrainAreaBucketKey(Vector3 point)
    {
        return new TerrainAreaBucketKey(GetTerrainAreaBucketCoord(point.x), GetTerrainAreaBucketCoord(point.z));
    }

    private static int GetTerrainAreaBucketCoord(float value)
    {
        return Mathf.FloorToInt(value / ProtectedTerrainAreaBucketSize);
    }

    private static float MergeExpireTime(float currentExpireTime, float newExpireTime)
    {
        if (currentExpireTime <= 0f || newExpireTime <= 0f)
        {
            return 0f;
        }

        return Mathf.Max(currentExpireTime, newExpireTime);
    }

    private static bool IsIgnoredByTerrainAreas(Vector3 point, List<ExternalTerrainIgnoreArea> areas, bool logSkip)
    {
        foreach (ExternalTerrainIgnoreArea area in areas)
        {
            if (HorizontalDistanceSqr(point, area.Center) > area.Radius * area.Radius)
            {
                continue;
            }

            if (logSkip)
            {
                TerrainMistilePlugin.TerrainMistileLogger.LogDebug($"Skipping TerrainMistile terrain reaction at {point}; inside {area.Source} ignore area near {area.Center}.");
            }

            return true;
        }

        return false;
    }

    internal static bool TryFindReplacementTerrainTarget(Vector3 center, out Vector3 modifiedPoint)
    {
        float range = Mathf.Max(TerrainMistileSpawnRules.MaxPlayerSearchRadius, TerrainMistileSpawnRules.MaxResetRadius);
        if (TryFindCachedModifiedTerrainNear(center, range, out modifiedPoint))
        {
            return true;
        }

        return TryFindModifiedTerrainNear(center, range, out modifiedPoint);
    }

    private static bool TryFindCachedModifiedTerrainNear(Vector3 center, float range, out Vector3 modifiedPoint)
    {
        CleanupTargetState();

        float rangeSqr = range * range;
        bool found = false;
        float bestScore = float.MinValue;
        Vector3 bestPoint = default;

        foreach (ModifiedTerrainUnitCandidate unit in ModifiedTerrainUnits.Values)
        {
            Vector3 candidate = unit.TargetPoint;
            if (HorizontalDistanceSqr(candidate, center) > rangeSqr)
            {
                continue;
            }

            if (!TerrainMistileSpawnRules.TryGetEnabledRule(unit.Biome, out TerrainMistileBiomeSpawnRule rule))
            {
                continue;
            }

            if (!IsEligibleModifiedTerrainTarget(candidate, rule, logSkip: false))
            {
                continue;
            }

            if (!HasModifiedTerrainAround(candidate, rule.ResetRadius))
            {
                continue;
            }

            float score = GetTargetSpreadScore(candidate, center, unit.MaxDeformationPressure);
            if (!found || score > bestScore)
            {
                found = true;
                bestScore = score;
                bestPoint = candidate;
            }
        }

        modifiedPoint = bestPoint;
        return found;
    }

    internal static float GetResetRadiusForPoint(Vector3 point)
    {
        int biome = TerrainMistileSpawnRules.GetBiomeKey(point);
        TerrainMistileBiomeSpawnRule rule = TerrainMistileSpawnRules.GetRule(biome);
        return rule.ResetRadius;
    }

    private static bool HasModifiedCellInRadius(
        TerrainComp terrainComp,
        Vector3 center,
        float radiusSqr,
        out bool foundModifiedHeightCell)
    {
        int width = terrainComp.m_width + 1;
        Heightmap hmap = terrainComp.m_hmap;
        Vector3 hmapPosition = ((Component)hmap).transform.position;
        foundModifiedHeightCell = false;

        for (int y = 0; y < width; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = GetTerrainCellIndex(width, x, y);
                if (!IsModifiedHeightCell(terrainComp, index))
                {
                    continue;
                }

                foundModifiedHeightCell = true;
                GetTerrainCellWorldXZ(hmap, hmapPosition, x, y, out float worldX, out float worldZ);
                if (IsWithinHorizontalRadius(worldX, worldZ, center, radiusSqr))
                {
                    Vector3 cellPoint = new(worldX, center.y, worldZ);
                    if (IsProtectedTerrainArea(cellPoint, logSkip: false))
                    {
                        continue;
                    }

                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsTargetSuppressedWithoutCleanup(Vector3 point, bool logSkip)
    {
        foreach (TargetReservation reservation in TargetReservations)
        {
            if (HorizontalDistanceSqr(point, reservation.Point) > reservation.Radius * reservation.Radius)
            {
                continue;
            }

            if (logSkip)
            {
                TerrainMistilePlugin.TerrainMistileLogger.LogDebug($"Skipping TerrainMistile spawn roll at {point}; target is already reserved near {reservation.Point}.");
            }

            return true;
        }

        return false;
    }

    private static float GetTargetSpreadScore(Vector3 point, Vector3 center, float deformationPressure)
    {
        float nearestSuppressionGap = GetNearestSuppressionGap(point);
        float score = nearestSuppressionGap == float.MaxValue ? 0f : nearestSuppressionGap;
        score += Mathf.Min(HorizontalDistanceSqr(point, center), 4096f) * 0.001f;
        score += Mathf.Clamp01(deformationPressure);
        score += Random.Range(0f, 0.25f);
        return score;
    }

    private static float GetNearestSuppressionGap(Vector3 point)
    {
        float nearestGap = float.MaxValue;

        foreach (TargetReservation reservation in TargetReservations)
        {
            float gap = Mathf.Sqrt(HorizontalDistanceSqr(point, reservation.Point)) - reservation.Radius;
            nearestGap = Mathf.Min(nearestGap, gap);
        }

        return nearestGap;
    }

    private static void CleanupTargetState()
    {
        float now = Time.time;
        for (int i = TargetReservations.Count - 1; i >= 0; i--)
        {
            if (TargetReservations[i].ExpireTime <= now)
            {
                TargetReservations.RemoveAt(i);
            }
        }
    }

    private static bool IsTerrainCompNearPoint(TerrainComp terrainComp, Vector3 point, float range)
    {
        float halfSizeWithRange = terrainComp.m_size / 2f + range;
        Vector3 terrainCompPosition = ((Component)terrainComp).transform.position;
        return point.x >= terrainCompPosition.x - halfSizeWithRange &&
               point.x <= terrainCompPosition.x + halfSizeWithRange &&
               point.z >= terrainCompPosition.z - halfSizeWithRange &&
               point.z <= terrainCompPosition.z + halfSizeWithRange;
    }

    private static bool IsTerrainCompNearAnyPlayer(TerrainComp terrainComp, float range)
    {
        foreach (Player player in TempPlayers)
        {
            if (!player)
            {
                continue;
            }

            if (IsTerrainCompNearPoint(terrainComp, ((Component)player).transform.position, range))
            {
                return true;
            }
        }

        return false;
    }

    private static SpawnUnitKey GetSpawnUnitKey(Vector3 point, int biome)
    {
        return new SpawnUnitKey(
            Mathf.FloorToInt(point.x / SpawnUnitSize),
            Mathf.FloorToInt(point.z / SpawnUnitSize),
            biome);
    }

    private static Vector3 GetSpawnUnitCenter(SpawnUnitKey key)
    {
        return new Vector3(
            (key.X + 0.5f) * SpawnUnitSize,
            0f,
            (key.Z + 0.5f) * SpawnUnitSize);
    }

    private static int GetTerrainCellIndex(int width, int x, int y)
    {
        return y * width + x;
    }

    private static void GetTerrainCellWorldXZ(Heightmap hmap, Vector3 hmapPosition, int x, int y, out float worldX, out float worldZ)
    {
        worldX = hmapPosition.x + (x - hmap.m_width / 2) * hmap.m_scale;
        worldZ = hmapPosition.z + (y - hmap.m_width / 2) * hmap.m_scale;
    }

    private static bool IsWithinHorizontalRadius(float worldX, float worldZ, Vector3 center, float radiusSqr)
    {
        float dx = worldX - center.x;
        float dz = worldZ - center.z;
        return dx * dx + dz * dz <= radiusSqr;
    }

    private static float HorizontalDistanceSqr(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    private sealed class TargetReservation
    {
        public Vector3 Point;
        public float Radius;
        public float ExpireTime;
    }

    private sealed class ExternalTerrainIgnoreArea
    {
        public Vector3 Center;
        public float Radius;
        public string Source = "";
        public float ExpireTime;
    }

    private readonly struct PlayerBasePiece
    {
        public PlayerBasePiece(string prefabName, Vector3 position)
        {
            PrefabName = prefabName;
            Position = position;
        }

        public string PrefabName { get; }
        public Vector3 Position { get; }
    }

    private readonly struct TerrainAreaBucketKey : IEquatable<TerrainAreaBucketKey>
    {
        public TerrainAreaBucketKey(int x, int z)
        {
            X = x;
            Z = z;
        }

        public int X { get; }
        public int Z { get; }

        public bool Equals(TerrainAreaBucketKey other)
        {
            return X == other.X && Z == other.Z;
        }

        public override bool Equals(object? obj)
        {
            return obj is TerrainAreaBucketKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (X * 397) ^ Z;
            }
        }
    }

    private readonly struct PlayerBasePieceBucketKey : IEquatable<PlayerBasePieceBucketKey>
    {
        public PlayerBasePieceBucketKey(int x, int z)
        {
            X = x;
            Z = z;
        }

        public int X { get; }
        public int Z { get; }

        public bool Equals(PlayerBasePieceBucketKey other)
        {
            return X == other.X && Z == other.Z;
        }

        public override bool Equals(object? obj)
        {
            return obj is PlayerBasePieceBucketKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (X * 397) ^ Z;
            }
        }
    }

    private readonly struct NoModifiedTerrainCompCache
    {
        public NoModifiedTerrainCompCache(int operations, float expireTime)
        {
            Operations = operations;
            ExpireTime = expireTime;
        }

        public int Operations { get; }
        public float ExpireTime { get; }
    }

    private readonly struct SpawnUnitKey : IEquatable<SpawnUnitKey>
    {
        public SpawnUnitKey(int x, int z, int biome)
        {
            X = x;
            Z = z;
            Biome = biome;
        }

        public int X { get; }
        public int Z { get; }
        public int Biome { get; }

        public bool Equals(SpawnUnitKey other)
        {
            return X == other.X && Z == other.Z && Biome == other.Biome;
        }

        public override bool Equals(object? obj)
        {
            return obj is SpawnUnitKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X;
                hash = (hash * 397) ^ Z;
                hash = (hash * 397) ^ Biome;
                return hash;
            }
        }

        public override string ToString()
        {
            return $"{TerrainMistileSpawnRules.GetBiomeName(Biome)}@{X},{Z}";
        }
    }

    private sealed class ModifiedTerrainUnitCandidate
    {
        public SpawnUnitKey Key;
        public int Biome;
        public Vector3 TargetPoint;
        public float BestScore;
        public float MaxDeformationPressure;
        public int ModifiedCellCount;
    }
}
