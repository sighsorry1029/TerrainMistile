using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TerrainMistile;

[HarmonyPatch(typeof(Piece))]
internal static class PlayerBasePieceLifecyclePatch
{
    [HarmonyPatch("Awake")]
    [HarmonyPostfix]
    private static void AwakePostfix(Piece __instance)
    {
        TerrainMistileSystem.NotifyPlayerBasePieceChanged(__instance);
    }

    [HarmonyPatch("OnDestroy")]
    [HarmonyPostfix]
    private static void OnDestroyPostfix(Piece __instance)
    {
        TerrainMistileSystem.NotifyPlayerBasePieceChanged(__instance);
    }

    [HarmonyPatch(nameof(Piece.SetCreator))]
    [HarmonyPostfix]
    private static void SetCreatorPostfix(Piece __instance)
    {
        TerrainMistileSystem.NotifyPlayerBasePieceChanged(__instance);
    }
}

[HarmonyPatch(
    typeof(ZoneSystem),
    "SpawnLocation",
    typeof(ZoneSystem.ZoneLocation),
    typeof(int),
    typeof(Vector3),
    typeof(Quaternion),
    typeof(ZoneSystem.SpawnMode),
    typeof(List<GameObject>),
    typeof(bool))]
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
