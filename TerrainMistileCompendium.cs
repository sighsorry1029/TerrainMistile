using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;

namespace TerrainMistile;

[HarmonyPatch(typeof(TextsDialog), "UpdateTextsList")]
internal static class TerrainMistileCompendium
{
    private const string PageTopic = "TerrainMistile";

    private static readonly VanillaBiomeEntry[] VanillaBiomes =
    {
        new(Heightmap.Biome.Meadows, "Meadows"),
        new(Heightmap.Biome.BlackForest, "Black Forest"),
        new(Heightmap.Biome.Swamp, "Swamp"),
        new(Heightmap.Biome.Mountain, "Mountain"),
        new(Heightmap.Biome.Plains, "Plains"),
        new(Heightmap.Biome.Mistlands, "Mistlands"),
        new(Heightmap.Biome.AshLands, "Ashlands"),
        new(Heightmap.Biome.DeepNorth, "Deep North"),
        new(Heightmap.Biome.Ocean, "Ocean")
    };

    private static void Postfix(TextsDialog __instance)
    {
        AddPage(__instance);
    }

    private static void AddPage(TextsDialog dialog)
    {
        if (dialog == null || dialog.m_texts == null)
        {
            return;
        }

        dialog.m_texts.RemoveAll(text => string.Equals(text?.m_topic, PageTopic, StringComparison.Ordinal));
        dialog.m_texts.Add(new TextsDialog.TextInfo(PageTopic, BuildPageText()));
        dialog.m_texts.Sort((left, right) => string.Compare(left?.m_topic, right?.m_topic, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildPageText()
    {
        StringBuilder builder = new(4096);
        AppendBiomeRules(builder);
        builder.Append("\n\n");
        AppendPlayerBasePrefabs(builder);
        return builder.ToString().TrimEnd();
    }

    private static void AppendBiomeRules(StringBuilder builder)
    {
        builder.Append("<color=#FFD27A><b>Player Base Protection by Biome</b></color>\n\n");

        HashSet<int> displayedBiomes = new();
        foreach (VanillaBiomeEntry biome in VanillaBiomes)
        {
            int biomeKey = (int)biome.Biome;
            displayedBiomes.Add(biomeKey);
            AppendBiomeRule(
                builder,
                GetLocalizedVanillaBiomeName(biome),
                TerrainMistileSpawnRules.GetRule(biomeKey));
        }

        List<BiomeDisplayEntry> customBiomes = new();
        foreach (int biomeKey in TerrainMistileSpawnRules.GetConfiguredBiomeKeysSnapshot())
        {
            if (biomeKey == 0 || !displayedBiomes.Add(biomeKey))
            {
                continue;
            }

            customBiomes.Add(new BiomeDisplayEntry(
                biomeKey,
                GetLocalizedCustomBiomeName(biomeKey)));
        }

        customBiomes.Sort((left, right) =>
        {
            int nameComparison = string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
            return nameComparison != 0 ? nameComparison : left.BiomeKey.CompareTo(right.BiomeKey);
        });

        foreach (BiomeDisplayEntry biome in customBiomes)
        {
            AppendBiomeRule(builder, biome.DisplayName, TerrainMistileSpawnRules.GetRule(biome.BiomeKey));
        }

        AppendBiomeRule(builder, "Other biomes (defaults)", TerrainMistileSpawnRules.DefaultRule);
    }

    private static void AppendBiomeRule(
        StringBuilder builder,
        string biomeName,
        TerrainMistileBiomeSpawnRule rule)
    {
        builder
            .Append("<color=orange><b>")
            .Append(biomeName)
            .Append("</b></color>: ");

        if (!rule.Enabled)
        {
            builder.Append("TerrainMistile disabled");
        }
        else if (rule.PlayerBaseValue <= 0 || rule.BaseCheckRadius <= 0f)
        {
            builder.Append("PlayerBase protection disabled");
        }
        else
        {
            builder
                .Append(rule.PlayerBaseValue)
                .Append(rule.PlayerBaseValue == 1 ? " unique listed base type within " : " unique listed base types within ")
                .Append(rule.BaseCheckRadius.ToString("0.##", CultureInfo.InvariantCulture))
                .Append(" m");
        }

        builder.Append('\n');
    }

    private static void AppendPlayerBasePrefabs(StringBuilder builder)
    {
        List<PrefabDisplayEntry> entries = new();
        foreach (string prefabName in TerrainMistileSpawnRules.GetPlayerBasePrefabNamesSnapshot())
        {
            entries.Add(new PrefabDisplayEntry(prefabName, GetPrefabDisplayName(prefabName)));
        }

        entries.Sort((left, right) =>
        {
            int nameComparison = string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
            return nameComparison != 0
                ? nameComparison
                : string.Compare(left.PrefabName, right.PrefabName, StringComparison.OrdinalIgnoreCase);
        });

        Dictionary<string, int> displayNameCounts = new(StringComparer.OrdinalIgnoreCase);
        foreach (PrefabDisplayEntry entry in entries)
        {
            displayNameCounts.TryGetValue(entry.DisplayName, out int count);
            displayNameCounts[entry.DisplayName] = count + 1;
        }

        builder
            .Append("<color=#FFD27A><b>Recognized Base Pieces (")
            .Append(entries.Count)
            .Append(")</b></color>\n\n");

        if (entries.Count == 0)
        {
            builder.Append("No base prefabs configured.\n");
            return;
        }

        foreach (PrefabDisplayEntry entry in entries)
        {
            builder.Append("- ").Append(entry.DisplayName);
            if (displayNameCounts[entry.DisplayName] > 1 &&
                !string.Equals(entry.DisplayName, entry.PrefabName, StringComparison.OrdinalIgnoreCase))
            {
                builder
                    .Append(" <color=#999999>[")
                    .Append(entry.PrefabName)
                    .Append("]</color>");
            }

            builder.Append('\n');
        }
    }

    private static string GetPrefabDisplayName(string prefabName)
    {
        GameObject? prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(prefabName) : null;
        if (!prefab)
        {
            prefab = PrefabManager.Instance.GetPrefab(prefabName);
        }

        Piece? piece = prefab ? prefab.GetComponent<Piece>() : null;
        if (!piece && prefab)
        {
            piece = prefab.GetComponentInChildren<Piece>(true);
        }

        return piece && !string.IsNullOrWhiteSpace(piece.m_name)
            ? LocalizeOrFallback(piece.m_name, prefabName)
            : prefabName;
    }

    private static string GetLocalizedVanillaBiomeName(VanillaBiomeEntry biome)
    {
        string localizationKey = "$biome_" + biome.Biome.ToString().ToLowerInvariant();
        return LocalizeOrFallback(localizationKey, biome.EnglishName);
    }

    private static string GetLocalizedCustomBiomeName(int biomeKey)
    {
        string displayName = TerrainMistileSpawnRules.GetBiomeName(biomeKey);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return biomeKey.ToString(CultureInfo.InvariantCulture);
        }

        string fallback = displayName[0] == '$' ? displayName.Substring(1) : displayName;
        return LocalizeOrFallback(displayName, fallback);
    }

    private static string LocalizeOrFallback(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || Localization.instance == null)
        {
            return fallback;
        }

        string localized = Localization.instance.Localize(value).Trim();
        if (localized.Length == 0)
        {
            return fallback;
        }

        if (value[0] == '$')
        {
            string missingValue = "[" + value.Substring(1) + "]";
            if (string.Equals(localized, missingValue, StringComparison.Ordinal) ||
                localized.IndexOf("MISSING KEY", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return fallback;
            }
        }

        return localized;
    }

    private readonly struct VanillaBiomeEntry
    {
        internal readonly Heightmap.Biome Biome;
        internal readonly string EnglishName;

        internal VanillaBiomeEntry(Heightmap.Biome biome, string englishName)
        {
            Biome = biome;
            EnglishName = englishName;
        }
    }

    private readonly struct BiomeDisplayEntry
    {
        internal readonly int BiomeKey;
        internal readonly string DisplayName;

        internal BiomeDisplayEntry(int biomeKey, string displayName)
        {
            BiomeKey = biomeKey;
            DisplayName = displayName;
        }
    }

    private readonly struct PrefabDisplayEntry
    {
        internal readonly string PrefabName;
        internal readonly string DisplayName;

        internal PrefabDisplayEntry(string prefabName, string displayName)
        {
            PrefabName = prefabName;
            DisplayName = displayName;
        }
    }
}
