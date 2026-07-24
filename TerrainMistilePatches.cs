using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TerrainMistile;

[HarmonyPatch(
    typeof(ZoneSystem),
    nameof(ZoneSystem.SpawnLocation),
    typeof(ZoneSystem.ZoneLocation),
    typeof(int),
    typeof(Vector3),
    typeof(Quaternion),
    typeof(ZoneSystem.SpawnMode),
    typeof(List<GameObject>))]
internal static class ZoneSystemSpawnLocationPatch
{
    private static void Prefix(ZoneSystem.ZoneLocation location, Vector3 pos)
    {
        TerrainMistileSystem.RegisterLocationTerrainLoadGrace(location, pos);
    }
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Destroy), typeof(GameObject))]
internal static class ZNetSceneDestroyPatch
{
    private static void Prefix(GameObject __0)
    {
        if (!__0)
        {
            return;
        }

        TerrainMistileBehaviour behaviour = __0.GetComponent<TerrainMistileBehaviour>();
        if (behaviour)
        {
            behaviour.TryResetTerrain();
        }
    }
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
internal static class ZNetShutdownPatch
{
    private static void Postfix()
    {
        TerrainMistileSystem.ClearWorldState();
    }
}
