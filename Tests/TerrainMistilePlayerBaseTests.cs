using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnityEngine;

namespace TerrainMistile.Tests;

[TestClass]
public sealed class TerrainMistilePlayerBaseTests
{
    private const BindingFlags StaticPrivate = BindingFlags.NonPublic | BindingFlags.Static;

    [TestMethod]
    public void InvalidatingPlayerBaseCacheAlsoExpiresSameFrameZoneReadiness()
    {
        FieldInfo piecesBuilt = GetField("_playerBasePieceBucketsBuilt");
        FieldInfo nextRefresh = GetField("_nextPlayerBasePieceBucketRefreshTime");
        FieldInfo readinessFrame = GetField("_playerBaseZoneReadinessFrame");
        object? previousBuilt = piecesBuilt.GetValue(null);
        object? previousRefresh = nextRefresh.GetValue(null);
        object? previousFrame = readinessFrame.GetValue(null);
        try
        {
            piecesBuilt.SetValue(null, true);
            nextRefresh.SetValue(null, float.MaxValue);
            readinessFrame.SetValue(null, 123);

            MethodInfo invalidate = typeof(TerrainMistileSystem).GetMethod("InvalidatePlayerBaseCache", StaticPrivate)!;
            invalidate.Invoke(null, null);

            Assert.AreEqual(false, piecesBuilt.GetValue(null));
            Assert.AreEqual(0f, nextRefresh.GetValue(null));
            Assert.AreEqual(-1, readinessFrame.GetValue(null));
        }
        finally
        {
            piecesBuilt.SetValue(null, previousBuilt);
            nextRefresh.SetValue(null, previousRefresh);
            readinessFrame.SetValue(null, previousFrame);
        }
    }

    [TestMethod]
    [DataRow(0, 24)]
    [DataRow(3, 0)]
    public void DisabledBaseProtectionDoesNotRequireLoadedZones(int threshold, int radius)
    {
        Assert.IsTrue(TerrainMistileSpawnRules.TryParseYaml(
            $"defaults:\n  playerBaseValue: {threshold}\n  baseCheckRadius: {radius}\n",
            out TerrainMistileBiomeSpawnRule rule, out _, out _, out string error), error);

        MethodInfo check = typeof(TerrainMistileSystem).GetMethod("PassesPlayerBaseProtection", StaticPrivate)!;
        Assert.AreEqual(true, check.Invoke(null, new object[] { Vector3.zero, rule, false }));
    }

    [TestMethod]
    public void EnforcerImpactBypassesPlayerBaseWithoutWorldOrCacheAccess()
    {
        FieldInfo piecesBuilt = GetField("_playerBasePieceBucketsBuilt");
        FieldInfo readinessFrame = GetField("_playerBaseZoneReadinessFrame");
        object? previousBuilt = piecesBuilt.GetValue(null);
        object? previousFrame = readinessFrame.GetValue(null);
        try
        {
            piecesBuilt.SetValue(null, true);
            readinessFrame.SetValue(null, 123);

            // No Unity scene is running: this must not resolve biomes or check loaded pieces.
            Assert.IsTrue(TerrainMistileSystem.CanResetTerrainAt(Vector3.zero, ignorePlayerBaseProtection: true));
            Assert.AreEqual(true, piecesBuilt.GetValue(null));
            Assert.AreEqual(123, readinessFrame.GetValue(null));
        }
        finally
        {
            piecesBuilt.SetValue(null, previousBuilt);
            readinessFrame.SetValue(null, previousFrame);
        }
    }

    private static FieldInfo GetField(string name)
    {
        return typeof(TerrainMistileSystem).GetField(name, StaticPrivate)!;
    }
}
