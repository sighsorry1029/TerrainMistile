using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

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

[HarmonyPatch(typeof(Attack), nameof(Attack.Stop))]
internal static class AttackStopPatch
{
    private static void Prefix(Attack __instance)
    {
        if (__instance == null || !__instance.m_attackKillsSelf || __instance.m_attackDone || !__instance.m_character)
        {
            return;
        }

        TerrainMistileBehaviour behaviour = ((Component)__instance.m_character).GetComponent<TerrainMistileBehaviour>();
        if (behaviour)
        {
            behaviour.MarkSelfDestruct();
        }
    }
}

[HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
internal static class CharacterApplyDamagePatch
{
    private static void Prefix(Character __instance, HitData hit)
    {
        if (!__instance || hit == null || hit.m_hitType != HitData.HitType.Self)
        {
            return;
        }

        TerrainMistileBehaviour behaviour = ((Component)__instance).GetComponent<TerrainMistileBehaviour>();
        if (behaviour)
        {
            behaviour.MarkSelfDestruct();
        }
    }
}

[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.OnAttackTrigger))]
internal static class HumanoidOnAttackTriggerPatch
{
    private const string MistileKamikazePrefabName = "Mistile_kamikaze";

    private static void Prefix(Humanoid __instance)
    {
        if (!__instance || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner())
        {
            return;
        }

        TerrainMistileBehaviour behaviour = ((Component)__instance).GetComponent<TerrainMistileBehaviour>();
        if (!behaviour)
        {
            return;
        }

        ItemDrop.ItemData weapon = __instance.GetCurrentWeapon();
        if (weapon == null || !IsMistileKamikaze(weapon))
        {
            return;
        }

        behaviour.MarkSelfDestruct(MistileKamikazePrefabName);
    }

    private static bool IsMistileKamikaze(ItemDrop.ItemData weapon)
    {
        if (weapon.m_dropPrefab && string.Equals(((Object)weapon.m_dropPrefab).name, MistileKamikazePrefabName, System.StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(weapon.m_shared.m_name, MistileKamikazePrefabName, System.StringComparison.OrdinalIgnoreCase);
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
            behaviour.TryResetTerrain("destroy");
        }
    }
}
