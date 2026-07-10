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
    private static void Prefix(object[] __args)
    {
        if (__args.Length <= 0 || __args[0] is not GameObject go || !go)
        {
            return;
        }

        TerrainMistileBehaviour behaviour = go.GetComponent<TerrainMistileBehaviour>();
        if (behaviour)
        {
            behaviour.TryResetTerrain();
        }
    }
}
