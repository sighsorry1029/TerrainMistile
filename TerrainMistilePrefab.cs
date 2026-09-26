using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TerrainMistile;

internal static class TerrainMistilePrefab
{
    internal const string PrefabName = "TerrainMistile";
    internal const string EnforcerPrefabName = "TerrainMistileEnforcer";
    private const string SourcePrefabName = "Mistile";
    private static ZNetScene? _registeredScene;
    private static GameObject? _container;
    private static Dictionary<int, GameObject>? _namedPrefabs;
    private static GameObject? _registeredPrefab;
    private static GameObject? _registeredEnforcerPrefab;
    private static readonly List<Material> RegisteredPrefabVisualMaterials = new();
    private static readonly List<Material> RegisteredEnforcerPrefabVisualMaterials = new();

    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    private static class RegisterPrefabsPatch
    {
        private static void Prefix(ZNetScene __instance, Dictionary<int, GameObject> ___m_namedPrefabs)
        {
            RegisterPrefabs(__instance, ___m_namedPrefabs);
        }
    }

    [HarmonyPatch(typeof(ZNetScene), "OnDestroy")]
    private static class ReleasePrefabsPatch
    {
        private static void Prefix(ZNetScene __instance)
        {
            if (ReferenceEquals(_registeredScene, __instance))
                ReleasePrefabs();
        }
    }

    private static void RegisterPrefabs(ZNetScene scene, Dictionary<int, GameObject> namedPrefabs)
    {
        if (ReferenceEquals(_registeredScene, scene))
            return;

        ReleasePrefabs();
        try
        {
            GameObject? source = null;
            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (!prefab) continue;
                CheckNameConflict(prefab);
                if (prefab.name == SourcePrefabName) source = prefab;
            }
            foreach (GameObject prefab in scene.m_nonNetViewPrefabs)
                if (prefab) CheckNameConflict(prefab);

            if (!source || namedPrefabs.ContainsKey(PrefabName.GetStableHashCode()) ||
                namedPrefabs.ContainsKey(EnforcerPrefabName.GetStableHashCode()))
                throw new InvalidOperationException("Mistile source is missing or a TerrainMistile prefab hash is already registered.");

            _registeredScene = scene;
            _namedPrefabs = namedPrefabs;
            _container = new GameObject("TerrainMistile prefabs");
            // Configure inactive templates before any Character/ZNetView/behaviour Awake can run.
            _container.SetActive(false);
            _container.transform.SetParent(scene.transform, false);
            _registeredPrefab = CreateTerrainMistilePrefab(source!, PrefabName, false, RegisteredPrefabVisualMaterials);
            _registeredEnforcerPrefab = CreateTerrainMistilePrefab(source!, EnforcerPrefabName, true, RegisteredEnforcerPrefabVisualMaterials);
            // Vanilla Awake builds the network name map from these lists after this prefix.
            scene.m_prefabs.Add(_registeredPrefab);
            scene.m_prefabs.Add(_registeredEnforcerPrefab);
            TerrainMistilePlugin.TerrainMistileLogger.LogInfo("Registered TerrainMistile and TerrainMistileEnforcer prefabs.");
        }
        catch (Exception exception)
        {
            ReleasePrefabs();
            TerrainMistilePlugin.TerrainMistileLogger.LogError($"TerrainMistile prefab registration failed: {exception}");
        }
    }

    private static void CheckNameConflict(GameObject prefab)
    {
        int hash = prefab.name.GetStableHashCode();
        if (hash == PrefabName.GetStableHashCode() || hash == EnforcerPrefabName.GetStableHashCode())
            throw new InvalidOperationException($"Prefab '{prefab.name}' conflicts with a TerrainMistile prefab hash.");
    }

    internal static void ReleasePrefabs()
    {
        RemovePrefab(_registeredPrefab);
        RemovePrefab(_registeredEnforcerPrefab);
        _registeredPrefab = null;
        _registeredEnforcerPrefab = null;
        _registeredScene = null;
        _namedPrefabs = null;
        if (_container) Object.Destroy(_container);
        _container = null;
        ReleaseVisualMaterials(RegisteredPrefabVisualMaterials);
        ReleaseVisualMaterials(RegisteredEnforcerPrefabVisualMaterials);
    }

    private static void RemovePrefab(GameObject? prefab)
    {
        if (ReferenceEquals(prefab, null)) return;
        if (_registeredScene) _registeredScene!.m_prefabs.Remove(prefab);
        if (prefab && _namedPrefabs != null &&
            _namedPrefabs.TryGetValue(prefab!.name.GetStableHashCode(), out GameObject registered) &&
            ReferenceEquals(registered, prefab))
            _namedPrefabs.Remove(prefab.name.GetStableHashCode());
    }

    private static GameObject CreateTerrainMistilePrefab(
        GameObject source,
        string prefabName,
        bool enforcer,
        List<Material> registeredVisualMaterials)
    {
        GameObject prefab = Object.Instantiate(source, _container!.transform);
        prefab.name = prefabName;
        prefab.SetActive(true);

        Character character = prefab.GetComponent<Character>();
        if (character)
        {
            character.m_faction = Character.Faction.Boss;
            character.m_aiSkipTarget = true;
        }

        MonsterAI monsterAI = prefab.GetComponent<MonsterAI>();
        if (monsterAI)
        {
            monsterAI.m_enableHuntPlayer = false;
            monsterAI.m_attackPlayerObjects = false;
            monsterAI.m_aggravatable = false;
            monsterAI.m_alertRange = 0f;
            monsterAI.m_viewRange = 0f;
            monsterAI.m_hearRange = 0f;
        }

        Humanoid humanoid = prefab.GetComponent<Humanoid>();
        if (humanoid)
        {
            humanoid.m_defaultItems = Array.Empty<GameObject>();
        }

        TerrainMistileBehaviour behaviour = prefab.GetComponent<TerrainMistileBehaviour>();
        if (!behaviour)
        {
            behaviour = prefab.AddComponent<TerrainMistileBehaviour>();
        }

        behaviour.ConfigureEnforcer(enforcer);
        if (character)
        {
            character.m_name = enforcer
                ? TerrainMistilePlugin.EnforcerDisplayNameToken
                : TerrainMistilePlugin.DisplayNameToken;
        }

        MakeCollidersNonBlocking(prefab);
        ApplyVisuals(prefab, TerrainMistileSpawnRules.DefaultVisualColor, registeredVisualMaterials);
        return prefab;
    }

    internal static void RefreshRegisteredPrefabVisuals()
    {
        RefreshRegisteredPrefabVisuals(
            PrefabName,
            _registeredPrefab,
            RegisteredPrefabVisualMaterials);
        RefreshRegisteredPrefabVisuals(
            EnforcerPrefabName,
            _registeredEnforcerPrefab,
            RegisteredEnforcerPrefabVisualMaterials);
    }

    private static void RefreshRegisteredPrefabVisuals(
        string prefabName,
        GameObject? registeredPrefab,
        List<Material> registeredVisualMaterials)
    {
        GameObject? prefab = registeredPrefab
            ? registeredPrefab
            : ZNetScene.instance
                ? ZNetScene.instance.GetPrefab(prefabName)
                : null;
        if (prefab)
        {
            RefreshRegisteredPrefabVisuals(
                prefab,
                TerrainMistileSpawnRules.DefaultVisualColor,
                registeredVisualMaterials);
        }
    }

    internal static void ApplyVisuals(GameObject root, Color color, List<Material>? ownedMaterials = null)
    {
        if (!root)
        {
            return;
        }

        Material[] previousMaterials = ownedMaterials is { Count: > 0 }
            ? ownedMaterials.ToArray()
            : Array.Empty<Material>();
        ownedMaterials?.Clear();

        ApplyLightColor(root, color);
        ApplyParticleColor(root, color);
        ApplyMaterialColor(root, color, ownedMaterials);
        ReleaseVisualMaterials(previousMaterials);
    }

    internal static void ReleaseVisualMaterials(List<Material> materials)
    {
        foreach (Material material in materials)
        {
            if (material)
            {
                Object.Destroy(material);
            }
        }

        materials.Clear();
    }

    private static void ReleaseVisualMaterials(Material[] materials)
    {
        foreach (Material material in materials)
        {
            if (material)
            {
                Object.Destroy(material);
            }
        }
    }

    private static void RefreshRegisteredPrefabVisuals(
        GameObject root,
        Color color,
        List<Material> registeredVisualMaterials)
    {
        ApplyLightColor(root, color);
        ApplyParticleColor(root, color);

        for (int i = registeredVisualMaterials.Count - 1; i >= 0; i--)
        {
            Material material = registeredVisualMaterials[i];
            if (!material)
            {
                registeredVisualMaterials.RemoveAt(i);
                continue;
            }

            ApplyMaterialProperties(material, color);
        }

        if (registeredVisualMaterials.Count == 0)
        {
            ApplyMaterialColor(root, color, registeredVisualMaterials);
        }
    }

    internal static void MakeCollidersNonBlocking(GameObject root)
    {
        if (!root)
        {
            return;
        }

        foreach (Collider collider in root.GetComponentsInChildren<Collider>(includeInactive: true))
        {
            if (collider)
            {
                collider.isTrigger = true;
            }
        }
    }

    private static void ApplyLightColor(GameObject root, Color color)
    {
        foreach (Light light in root.GetComponentsInChildren<Light>(includeInactive: true))
        {
            light.color = WithAlpha(color, light.color.a);
        }
    }

    private static void ApplyParticleColor(GameObject root, Color color)
    {
        foreach (ParticleSystem particleSystem in root.GetComponentsInChildren<ParticleSystem>(includeInactive: true))
        {
            ParticleSystem.MainModule main = particleSystem.main;
            main.startColor = Recolor(main.startColor, color);

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = particleSystem.colorOverLifetime;
            if (colorOverLifetime.enabled)
            {
                colorOverLifetime.color = Recolor(colorOverLifetime.color, color);
            }
        }
    }

    private static ParticleSystem.MinMaxGradient Recolor(ParticleSystem.MinMaxGradient source, Color color)
    {
        return source.mode switch
        {
            ParticleSystemGradientMode.Color => new ParticleSystem.MinMaxGradient(WithAlpha(color, source.color.a)),
            ParticleSystemGradientMode.TwoColors => new ParticleSystem.MinMaxGradient(
                WithAlpha(ScaleRgb(color, 0.65f), source.colorMin.a),
                WithAlpha(ScaleRgb(color, 1.15f), source.colorMax.a)),
            ParticleSystemGradientMode.Gradient => new ParticleSystem.MinMaxGradient(CreateGradient(source.gradient, color)),
            ParticleSystemGradientMode.TwoGradients => new ParticleSystem.MinMaxGradient(
                CreateGradient(source.gradientMin, color),
                CreateGradient(source.gradientMax, color)),
            _ => new ParticleSystem.MinMaxGradient(color)
        };
    }

    private static Gradient CreateGradient(Gradient source, Color color)
    {
        Gradient gradient = new();
        GradientColorKey[] sourceColorKeys = source.colorKeys;
        GradientAlphaKey[] alphaKeys = source.alphaKeys;

        if (sourceColorKeys == null || sourceColorKeys.Length == 0)
        {
            sourceColorKeys = new[]
            {
                new GradientColorKey(color, 0f),
                new GradientColorKey(color, 1f)
            };
        }

        GradientColorKey[] colorKeys = new GradientColorKey[sourceColorKeys.Length];
        for (int i = 0; i < sourceColorKeys.Length; i++)
        {
            colorKeys[i] = new GradientColorKey(GetGradientShade(color, i, sourceColorKeys.Length), sourceColorKeys[i].time);
        }

        if (alphaKeys == null || alphaKeys.Length == 0)
        {
            alphaKeys = new[]
            {
                new GradientAlphaKey(color.a, 0f),
                new GradientAlphaKey(0f, 1f)
            };
        }

        gradient.SetKeys(colorKeys, alphaKeys);
        gradient.mode = source.mode;
        return gradient;
    }

    private static Color GetGradientShade(Color color, int index, int count)
    {
        float t = count <= 1 ? 0f : index / (float)(count - 1);
        return ScaleRgb(color, Mathf.Lerp(1.3f, 0.7f, t));
    }

    private static void ApplyMaterialColor(GameObject root, Color color, List<Material>? ownedMaterials)
    {
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(includeInactive: true))
        {
            Material[] materials = renderer.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (!material || !ShouldColorMaterial(renderer, material))
                {
                    continue;
                }

                Material copy = Object.Instantiate(material);
                ApplyMaterialProperties(copy, color);
                materials[i] = copy;
                ownedMaterials?.Add(copy);
                changed = true;
            }

            if (changed)
            {
                renderer.sharedMaterials = materials;
            }
        }
    }

    private static bool ShouldColorMaterial(Renderer renderer, Material material)
    {
        string objectName = renderer.gameObject ? renderer.gameObject.name : "";
        string materialName = material ? material.name : "";
        return ContainsAny(objectName, "flame", "flare", "spark", "ember") ||
               ContainsAny(materialName, "flame", "spark", "glow", "pixel_unlit");
    }

    private static void ApplyMaterialProperties(Material material, Color color)
    {
        SetMaterialColor(material, "_TintColor", color, preserveIntensity: false);
        SetMaterialColor(material, "_Color", color, preserveIntensity: false);
        SetMaterialColor(material, "_EmissionColor", color, preserveIntensity: true);
    }

    private static void SetMaterialColor(Material material, string property, Color color, bool preserveIntensity)
    {
        if (!material.HasProperty(property))
        {
            return;
        }

        Color existing = material.GetColor(property);
        float intensity = preserveIntensity ? Mathf.Max(1f, existing.r, existing.g, existing.b) : 1f;
        material.SetColor(property, WithAlpha(ScaleRgb(color, intensity), existing.a));
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (string needle in needles)
        {
            if (value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static Color ScaleRgb(Color color, float scale)
    {
        return new Color(
            color.r * scale,
            color.g * scale,
            color.b * scale,
            color.a);
    }

    private static Color WithAlpha(Color color, float alpha)
    {
        color.a = alpha;
        return color;
    }
}
