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
    private const string PageTopic = TerrainMistilePlugin.CompendiumTopicToken;
    private const string ExplanationToken = "$terrainmistile_compendium_explanation";
    private const string ProtectionHeadingToken = "$terrainmistile_compendium_protection_heading";
    private const string RequirementToken = "$terrainmistile_compendium_requirement";
    private const string MistileDisabledToken = "$terrainmistile_compendium_mistile_disabled";
    private const string ProtectionDisabledToken = "$terrainmistile_compendium_protection_disabled";
    private const string OtherBiomesToken = "$terrainmistile_compendium_other_biomes";
    private const string PiecesHeadingToken = "$terrainmistile_compendium_pieces_heading";
    private const string NoPiecesToken = "$terrainmistile_compendium_no_pieces";
    private const float GuidanceCooldown = 60f;
    private static float _nextGuidanceTime;

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

    private static void Postfix(List<TextsDialog.TextInfo> ___m_texts)
    {
        AddPage(___m_texts);
    }

    internal static void ResetGuidanceState()
    {
        _nextGuidanceTime = 0f;
    }

    internal static void TryShowGuidance(Vector3 center)
    {
        Player? localPlayer = Player.m_localPlayer;
        if (!localPlayer ||
            localPlayer.IsDead() ||
            Time.time < _nextGuidanceTime)
        {
            return;
        }

        TerrainMistileBiomeSpawnRule rule =
            TerrainMistileSpawnRules.GetRule(TerrainMistileSpawnRules.GetBiomeKey(center));
        if (rule.PlayerBaseValue <= 0 || rule.BaseCheckRadius <= 0f)
        {
            return;
        }

        Vector3 playerPosition = ((Component)localPlayer).transform.position;
        float dx = playerPosition.x - center.x;
        float dz = playerPosition.z - center.z;
        if (dx * dx + dz * dz > ZoneSystem.c_ZoneSize * ZoneSystem.c_ZoneSize)
        {
            return;
        }

        string topic = Localization.instance != null
            ? Localization.instance.Localize(TerrainMistilePlugin.CompendiumTopicToken)
            : TerrainMistilePlugin.ModName;
        string message = Localization.instance != null
            ? Localization.instance.Localize(TerrainMistilePlugin.CheckCompendiumMessageToken, topic)
            : $"Check the Compendium: {topic}";

        localPlayer.Message(MessageHud.MessageType.Center, message);
        _nextGuidanceTime = Time.time + GuidanceCooldown;
    }

    private static void AddPage(List<TextsDialog.TextInfo> texts)
    {
        if (texts == null)
        {
            return;
        }

        string localizedTopic = LocalizeOrFallback(PageTopic, TerrainMistilePlugin.ModName);
        texts.RemoveAll(text =>
            string.Equals(text?.m_topic, PageTopic, StringComparison.Ordinal) ||
            string.Equals(text?.m_topic, localizedTopic, StringComparison.Ordinal));
        texts.Add(new TextsDialog.TextInfo(localizedTopic, BuildPageText(localizedTopic)));
        texts.Sort((left, right) => string.Compare(left?.m_topic, right?.m_topic, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildPageText(string localizedTopic)
    {
        StringBuilder builder = new(4096);
        builder.Append(LocalizeOrFallback(
            ExplanationToken,
            "Protection counts different recognized settlement facility types around each altered terrain point. " +
            "Multiple copies of the same facility count as one type. " +
            $"{localizedTopic} spawning is blocked on protected terrain.",
            localizedTopic));
        builder.Append("\n\n");
        AppendBiomeRules(builder, localizedTopic);
        builder.Append("\n\n");
        AppendPlayerBasePrefabs(builder);
        return builder.ToString().TrimEnd();
    }

    private static void AppendBiomeRules(StringBuilder builder, string localizedTopic)
    {
        builder
            .Append("<color=#FFD27A><b>")
            .Append(LocalizeOrFallback(
                ProtectionHeadingToken,
                "Settlement Protection by Biome"))
            .Append("</b></color>\n\n");

        HashSet<int> displayedBiomes = new();
        foreach (VanillaBiomeEntry biome in VanillaBiomes)
        {
            int biomeKey = (int)biome.Biome;
            displayedBiomes.Add(biomeKey);
            AppendBiomeRule(
                builder,
                GetLocalizedVanillaBiomeName(biome),
                TerrainMistileSpawnRules.GetRule(biomeKey),
                localizedTopic);
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
            AppendBiomeRule(
                builder,
                biome.DisplayName,
                TerrainMistileSpawnRules.GetRule(biome.BiomeKey),
                localizedTopic);
        }

        AppendBiomeRule(
            builder,
            LocalizeOrFallback(OtherBiomesToken, "Other biomes (defaults)"),
            TerrainMistileSpawnRules.DefaultRule,
            localizedTopic);
    }

    private static void AppendBiomeRule(
        StringBuilder builder,
        string biomeName,
        TerrainMistileBiomeSpawnRule rule,
        string localizedTopic)
    {
        builder
            .Append("<color=orange><b>")
            .Append(biomeName)
            .Append("</b></color>: ");

        if (!rule.Enabled)
        {
            builder.Append(LocalizeOrFallback(
                MistileDisabledToken,
                $"{localizedTopic} spawning disabled",
                localizedTopic));
        }
        else if (rule.PlayerBaseValue <= 0 || rule.BaseCheckRadius <= 0f)
        {
            builder.Append(LocalizeOrFallback(
                ProtectionDisabledToken,
                "Settlement protection disabled"));
        }
        else
        {
            string requiredTypes = rule.PlayerBaseValue.ToString(CultureInfo.InvariantCulture);
            string radius = rule.BaseCheckRadius.ToString("0.##", CultureInfo.InvariantCulture);
            builder.Append(LocalizeOrFallback(
                RequirementToken,
                $"Required facility types: {requiredTypes} · Check radius: {radius} m",
                requiredTypes,
                radius));
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

        string entryCount = entries.Count.ToString(CultureInfo.InvariantCulture);
        builder
            .Append("<color=#FFD27A><b>")
            .Append(LocalizeOrFallback(
                PiecesHeadingToken,
                $"Recognized Settlement Facilities ({entryCount})",
                entryCount))
            .Append("</b></color>\n\n");

        if (entries.Count == 0)
        {
            builder
                .Append(LocalizeOrFallback(
                    NoPiecesToken,
                    "No settlement facilities configured."))
                .Append('\n');
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

    private static string LocalizeOrFallback(
        string value,
        string fallback,
        params string[] words)
    {
        if (string.IsNullOrWhiteSpace(value) || Localization.instance == null)
        {
            return fallback;
        }

        string localized = Localization.instance.Localize(value, words).Trim();
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
