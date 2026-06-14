using System;
using Jotunn.Managers;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TerrainMistile;

internal static class TerrainMistilePrefab
{
    internal const string PrefabName = "TerrainMistile";
    private const string SourcePrefabName = "Mistile";
    private static bool _hooked;
    private static bool _registered;
    private static GameObject? _registeredPrefab;

    internal static void RegisterPrefabHook()
    {
        if (_hooked)
        {
            return;
        }

        PrefabManager.OnVanillaPrefabsAvailable += CreateTerrainMistilePrefab;
        _hooked = true;
    }

    internal static void UnregisterPrefabHook()
    {
        if (!_hooked)
        {
            return;
        }

        PrefabManager.OnVanillaPrefabsAvailable -= CreateTerrainMistilePrefab;
        _hooked = false;
    }

    private static void CreateTerrainMistilePrefab()
    {
        if (_registered)
        {
            return;
        }

        GameObject prefab = PrefabManager.Instance.CreateClonedPrefab(PrefabName, SourcePrefabName);
        if (!prefab)
        {
            TerrainMistilePlugin.TerrainMistileLogger.LogError($"Could not clone vanilla prefab '{SourcePrefabName}'.");
            return;
        }

        Character character = prefab.GetComponent<Character>();
        if (character)
        {
            character.m_name = TerrainMistilePlugin.DisplayName;
            character.m_faction = Character.Faction.Dverger;
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
            humanoid.m_defaultItems = new GameObject[0];
        }

        if (!prefab.GetComponent<TerrainMistileBehaviour>())
        {
            prefab.AddComponent<TerrainMistileBehaviour>();
        }

        MakeCollidersNonBlocking(prefab);
        ApplyVisuals(prefab, TerrainMistileSpawnRules.DefaultVisualColor);
        PrefabManager.Instance.AddPrefab(prefab);
        _registeredPrefab = prefab;
        _registered = true;
        TerrainMistilePlugin.TerrainMistileLogger.LogInfo($"Registered '{PrefabName}' prefab from '{SourcePrefabName}'.");
    }

    internal static void RefreshRegisteredPrefabVisuals()
    {
        if (_registeredPrefab)
        {
            ApplyVisuals(_registeredPrefab, TerrainMistileSpawnRules.DefaultVisualColor);
            return;
        }

        GameObject? prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(PrefabName) : null;
        if (prefab)
        {
            ApplyVisuals(prefab, TerrainMistileSpawnRules.DefaultVisualColor);
        }
    }

    internal static void ApplyVisuals(GameObject root, Color color)
    {
        if (!root)
        {
            return;
        }

        ApplyLightColor(root, color);
        ApplyParticleColor(root, color);
        ApplyMaterialColor(root, color);
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

    private static void ApplyMaterialColor(GameObject root, Color color)
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
