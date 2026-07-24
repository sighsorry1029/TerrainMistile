using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TerrainMistile.Tests;

[TestClass]
public sealed class TerrainMistileSpawnRulesTests
{
    private const float Tolerance = 0.0001f;

    [TestMethod]
    public void EmptyYamlUsesBuiltInDefaults()
    {
        ParsedRules parsed = Parse("");

        Assert.AreEqual(60f, parsed.DefaultRule.Interval, Tolerance);
        Assert.AreEqual(32f, parsed.DefaultRule.PlayerSearchRadius, Tolerance);
        Assert.AreEqual(0.25f, parsed.DefaultRule.SpawnChance, Tolerance);
        Assert.AreEqual(0.25f, parsed.DefaultRule.MaxDeformationSpawnChanceBonus, Tolerance);
        Assert.IsTrue(parsed.DefaultRule.PerPlayerSpawn);
        Assert.AreEqual(1, parsed.DefaultRule.PlayerBaseValue);
        Assert.AreEqual(24f, parsed.DefaultRule.BaseCheckRadius, Tolerance);
        Assert.AreEqual(3, parsed.DefaultRule.MaxSpawn);
        Assert.AreEqual(16f, parsed.DefaultRule.SpawnRadiusMin, Tolerance);
        Assert.AreEqual(32f, parsed.DefaultRule.SpawnRadiusMax, Tolerance);
        Assert.AreEqual(8f, parsed.DefaultRule.SpawnAltitude, Tolerance);
        Assert.AreEqual(8f, parsed.DefaultRule.ResetRadius, Tolerance);
        Assert.AreEqual(1f, parsed.DefaultRule.Health, Tolerance);
        AssertColor(parsed.DefaultRule, 0x45, 0xFF, 0x5A, 0xFF);
        Assert.AreEqual(0, parsed.BiomeRules.Count);
        Assert.AreEqual(40, parsed.PlayerBasePrefabs.Count);
        Assert.IsTrue(parsed.PlayerBasePrefabs.Contains("bed"));
        Assert.IsTrue(parsed.PlayerBasePrefabs.Contains("piece_workbench"));
    }

    [TestMethod]
    public void GeneratedYamlKeepsBuiltInDefaultsInSync()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "TerrainMistile.yml");
            TerrainMistileSpawnRules.EnsureFileExists(path);

            ParsedRules builtIn = Parse("");
            ParsedRules generated = Parse(File.ReadAllText(path));

            AssertRulesEqual(builtIn.DefaultRule, generated.DefaultRule);
            Assert.IsTrue(builtIn.PlayerBasePrefabs.SetEquals(generated.PlayerBasePrefabs));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public void InvalidPlayerBasePrefabsFallsBackButExplicitEmptyListDoesNot()
    {
        ParsedRules invalid = Parse("playerBasePrefabs:\n  unexpected: mapping\n");
        ParsedRules empty = Parse("playerBasePrefabs: []\n");

        Assert.AreEqual(40, invalid.PlayerBasePrefabs.Count);
        Assert.AreEqual(0, empty.PlayerBasePrefabs.Count);
    }

    [TestMethod]
    public void BiomeOverrideInheritsDefaultsAndNormalizesNames()
    {
        const string yaml =
            "defaults:\n" +
            "  interval: 90\n" +
            "  player_search-radius: 48\n" +
            "  spawnChance: 0.4\n" +
            "  maxDeformationSpawnChanceBonus: 0.1\n" +
            "  maxSpawn: 5\n" +
            "  perPlayerSpawn: false\n" +
            "  playerBaseValue: 2\n" +
            "  baseCheckRadius: 30\n" +
            "  spawnRadius: 12~24\n" +
            "  spawnAltitude: 10\n" +
            "  resetRadius: 6\n" +
            "  health: 12\n" +
            "  visualColor: \"#112233\"\n" +
            "Black_Forest:\n" +
            "  health: 25\n" +
            "playerBasePrefabs:\n" +
            "  - bed\n" +
            "  - forge, portal\n" +
            "  - BED\n";

        ParsedRules parsed = Parse(yaml);
        TerrainMistileBiomeSpawnRule rule = parsed.BiomeRules[8];

        Assert.AreEqual(90f, rule.Interval, Tolerance);
        Assert.AreEqual(48f, rule.PlayerSearchRadius, Tolerance);
        Assert.AreEqual(0.4f, rule.SpawnChance, Tolerance);
        Assert.AreEqual(0.1f, rule.MaxDeformationSpawnChanceBonus, Tolerance);
        Assert.IsFalse(rule.PerPlayerSpawn);
        Assert.AreEqual(2, rule.PlayerBaseValue);
        Assert.AreEqual(30f, rule.BaseCheckRadius, Tolerance);
        Assert.AreEqual(5, rule.MaxSpawn);
        Assert.AreEqual(12f, rule.SpawnRadiusMin, Tolerance);
        Assert.AreEqual(24f, rule.SpawnRadiusMax, Tolerance);
        Assert.AreEqual(10f, rule.SpawnAltitude, Tolerance);
        Assert.AreEqual(6f, rule.ResetRadius, Tolerance);
        Assert.AreEqual(25f, rule.Health, Tolerance);
        AssertColor(rule, 0x11, 0x22, 0x33, 0xFF);
        Assert.AreEqual(3, parsed.PlayerBasePrefabs.Count);
        Assert.IsTrue(parsed.PlayerBasePrefabs.SetEquals(new[] { "bed", "forge", "portal" }));
    }

    [TestMethod]
    public void OutOfRangeValuesAreClampedAndSpawnRadiusIsSorted()
    {
        const string yaml =
            "defaults:\n" +
            "  interval: 9999\n" +
            "  playerSearchRadius: 999\n" +
            "  spawnChance: 2\n" +
            "  maxDeformationSpawnChanceBonus: -2\n" +
            "  maxSpawn: 99\n" +
            "  playerBaseValue: -3\n" +
            "  baseCheckRadius: 999\n" +
            "  spawnRadius: 300~-10\n" +
            "  spawnAltitude: 0\n" +
            "  resetRadius: 999\n" +
            "  health: 20000\n";

        TerrainMistileBiomeSpawnRule rule = Parse(yaml).DefaultRule;

        Assert.AreEqual(3600f, rule.Interval, Tolerance);
        Assert.AreEqual(512f, rule.PlayerSearchRadius, Tolerance);
        Assert.AreEqual(1f, rule.SpawnChance, Tolerance);
        Assert.AreEqual(0f, rule.MaxDeformationSpawnChanceBonus, Tolerance);
        Assert.AreEqual(50, rule.MaxSpawn);
        Assert.AreEqual(0, rule.PlayerBaseValue);
        Assert.AreEqual(128f, rule.BaseCheckRadius, Tolerance);
        Assert.AreEqual(1f, rule.SpawnRadiusMin, Tolerance);
        Assert.AreEqual(256f, rule.SpawnRadiusMax, Tolerance);
        Assert.AreEqual(1f, rule.SpawnAltitude, Tolerance);
        Assert.AreEqual(64f, rule.ResetRadius, Tolerance);
        Assert.AreEqual(10000f, rule.Health, Tolerance);
    }

    [TestMethod]
    public void InvalidValuesFallBackWithoutProducingNonFiniteRules()
    {
        const string yaml =
            "defaults:\n" +
            "  interval: 123\n" +
            "  playerSearchRadius: 45\n" +
            "  spawnChance: 0.4\n" +
            "  maxDeformationSpawnChanceBonus: 0.2\n" +
            "  maxSpawn: 7\n" +
            "  playerBaseValue: 3\n" +
            "  baseCheckRadius: 20\n" +
            "  spawnRadius: 10~20\n" +
            "  spawnAltitude: 9\n" +
            "  resetRadius: 7\n" +
            "  health: 11\n" +
            "  visualColor: \"#11223344\"\n" +
            "Meadows:\n" +
            "  interval: not-a-number\n" +
            "  playerSearchRadius: NaN\n" +
            "  spawnChance: Infinity\n" +
            "  maxDeformationSpawnChanceBonus: -Infinity\n" +
            "  maxSpawn: 2147483648\n" +
            "  playerBaseValue: -2147483649\n" +
            "  baseCheckRadius: NaN\n" +
            "  spawnRadius: not-a-radius\n" +
            "  spawnAltitude: NaN\n" +
            "  resetRadius: Infinity\n" +
            "  health: NaN\n" +
            "  visualColor: \"#GGGGGG\"\n";

        ParsedRules parsed = Parse(yaml);
        TerrainMistileBiomeSpawnRule actual = parsed.BiomeRules[1];

        AssertRulesEqual(parsed.DefaultRule, actual);
        Assert.IsFalse(float.IsNaN(actual.Interval));
        Assert.IsFalse(float.IsNaN(actual.PlayerSearchRadius));
        Assert.IsFalse(float.IsNaN(actual.SpawnChance));
        Assert.IsFalse(float.IsNaN(actual.MaxDeformationSpawnChanceBonus));
        Assert.IsFalse(float.IsNaN(actual.BaseCheckRadius));
        Assert.IsFalse(float.IsNaN(actual.SpawnAltitude));
        Assert.IsFalse(float.IsNaN(actual.ResetRadius));
        Assert.IsFalse(float.IsNaN(actual.Health));
    }

    [TestMethod]
    public void MalformedYamlReturnsAnError()
    {
        bool success = TerrainMistileSpawnRules.TryParseYaml(
            "defaults: [",
            out _,
            out _,
            out _,
            out string error);

        Assert.IsFalse(success);
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public void NumericAndVanillaBiomeKeysResolveWithoutLoadingTheGame()
    {
        const string yaml =
            "Black-Forest:\n" +
            "  health: 2\n" +
            "\"4294967295\":\n" +
            "  health: 3\n" +
            "\"0\":\n" +
            "  health: 5\n" +
            "UnknownBiome:\n" +
            "  health: 4\n";

        ParsedRules parsed = Parse(yaml);

        Assert.AreEqual(2, parsed.BiomeRules.Count);
        Assert.AreEqual(2f, parsed.BiomeRules[8].Health, Tolerance);
        Assert.AreEqual(3f, parsed.BiomeRules[-1].Health, Tolerance);
    }

    [TestMethod]
    [DataRow("ABC", 0xAA, 0xBB, 0xCC, 0xFF)]
    [DataRow("#1234", 0x11, 0x22, 0x33, 0x44)]
    [DataRow("112233", 0x11, 0x22, 0x33, 0xFF)]
    [DataRow("#11223344", 0x11, 0x22, 0x33, 0x44)]
    public void VisualColorSupportsUnityCompatibleHexForms(
        string value,
        int red,
        int green,
        int blue,
        int alpha)
    {
        TerrainMistileBiomeSpawnRule rule = Parse(
            "defaults:\n  visualColor: \"" + value + "\"\n").DefaultRule;

        AssertColor(rule, red, green, blue, alpha);
    }

    [TestMethod]
    public void EwdFallbackUsesReverseFileOrderAndPreservesEntryOrder()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "expand_biomes_a.yaml"),
                "- biome: FromA\n");
            File.WriteAllText(
                Path.Combine(directory, "expand_biomes_z.yaml"),
                "- biome: FromZ1\n- biome: FromZ2\n");

            bool success = TerrainMistileExpandWorldDataBiomeCompat.TryBuildFileMappingForTests(
                directory,
                out Dictionary<string, int> mapping,
                out string error);

            Assert.IsTrue(success, error);
            Assert.AreEqual(512, mapping["Mistlands"]);
            Assert.AreEqual(1024, mapping["FromZ1"]);
            Assert.AreEqual(2048, mapping["FromZ2"]);
            Assert.AreEqual(4096, mapping["FromA"]);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public void EwdFallbackKeepsExactNamesSeparateFromNormalizedAliases()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "expand_biomes.yaml"),
                "- biome: A-B\n" +
                "- biome: AB\n" +
                "- biome: CD\n" +
                "- biome: C-D\n" +
                "- biome: Black-Forest\n" +
                "- biome: Solo-Biome\n" +
                "- biome: After\n");

            bool success = TerrainMistileExpandWorldDataBiomeCompat.TryBuildFileMappingForTests(
                directory,
                out Dictionary<string, int> mapping,
                out string error);

            Assert.IsTrue(success, error);
            Assert.AreEqual(1024, mapping["A-B"]);
            Assert.AreEqual(2048, mapping["AB"]);
            Assert.AreEqual(4096, mapping["CD"]);
            Assert.AreEqual(8192, mapping["C-D"]);
            Assert.AreEqual(8, mapping["BlackForest"]);
            Assert.AreEqual(16384, mapping["Black-Forest"]);
            Assert.AreEqual(32768, mapping["Solo-Biome"]);
            Assert.AreEqual(32768, mapping["SoloBiome"]);
            Assert.AreEqual(65536, mapping["After"]);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    [DataRow("false", false, 0f)]
    [DataRow("False", false, 20f)]
    [DataRow("FALSE", false, 20f)]
    [DataRow("", false, 0f)]
    [DataRow("", true, 20f)]
    public void EwdTerrainRadiusMatchesLevelAreaSemantics(
        string levelArea,
        bool isBlueprint,
        float expectedRadius)
    {
        ExternalTerrainData data = new()
        {
            levelArea = levelArea
        };

        float radius = TerrainMistileExternalTerrainCompat.GetTerrainRadius(
            exteriorRadius: 20f,
            isBlueprint,
            data);

        Assert.AreEqual(expectedRadius, radius, Tolerance);
    }

    [TestMethod]
    public void EwdFallbackRejectsBiomeOverflowInsteadOfReusing128()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            using (StreamWriter writer = File.CreateText(Path.Combine(directory, "expand_biomes.yaml")))
            {
                for (int index = 1; index <= 24; index++)
                {
                    writer.WriteLine("- biome: Custom" + index.ToString(CultureInfo.InvariantCulture));
                }
            }

            bool success = TerrainMistileExpandWorldDataBiomeCompat.TryBuildFileMappingForTests(
                directory,
                out Dictionary<string, int> mapping,
                out string error);

            Assert.IsFalse(success);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));
            Assert.IsFalse(mapping.ContainsKey("Custom24"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public void EwdFallbackSkipsMalformedFilesAndKeepsValidMappings()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "expand_biomes_z.yaml"),
                "- biome: [\n");
            File.WriteAllText(
                Path.Combine(directory, "expand_biomes_a.yaml"),
                "- biome: ValidBiome\n");

            bool success = TerrainMistileExpandWorldDataBiomeCompat.TryBuildFileMappingForTests(
                directory,
                out Dictionary<string, int> mapping,
                out string warning);

            Assert.IsTrue(success);
            Assert.IsFalse(string.IsNullOrWhiteSpace(warning));
            Assert.AreEqual(1024, mapping["ValidBiome"]);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static ParsedRules Parse(string yaml)
    {
        bool success = TerrainMistileSpawnRules.TryParseYaml(
            yaml,
            out TerrainMistileBiomeSpawnRule defaultRule,
            out Dictionary<int, TerrainMistileBiomeSpawnRule> biomeRules,
            out HashSet<string> playerBasePrefabs,
            out string error);

        Assert.IsTrue(success, error);
        return new ParsedRules(defaultRule, biomeRules, playerBasePrefabs);
    }

    private static void AssertRulesEqual(
        TerrainMistileBiomeSpawnRule expected,
        TerrainMistileBiomeSpawnRule actual)
    {
        Assert.AreEqual(expected.Interval, actual.Interval, Tolerance);
        Assert.AreEqual(expected.PlayerSearchRadius, actual.PlayerSearchRadius, Tolerance);
        Assert.AreEqual(expected.SpawnChance, actual.SpawnChance, Tolerance);
        Assert.AreEqual(expected.MaxDeformationSpawnChanceBonus, actual.MaxDeformationSpawnChanceBonus, Tolerance);
        Assert.AreEqual(expected.PerPlayerSpawn, actual.PerPlayerSpawn);
        Assert.AreEqual(expected.PlayerBaseValue, actual.PlayerBaseValue);
        Assert.AreEqual(expected.BaseCheckRadius, actual.BaseCheckRadius, Tolerance);
        Assert.AreEqual(expected.MaxSpawn, actual.MaxSpawn);
        Assert.AreEqual(expected.SpawnRadiusMin, actual.SpawnRadiusMin, Tolerance);
        Assert.AreEqual(expected.SpawnRadiusMax, actual.SpawnRadiusMax, Tolerance);
        Assert.AreEqual(expected.SpawnAltitude, actual.SpawnAltitude, Tolerance);
        Assert.AreEqual(expected.ResetRadius, actual.ResetRadius, Tolerance);
        Assert.AreEqual(expected.Health, actual.Health, Tolerance);
        Assert.AreEqual(expected.VisualColor.r, actual.VisualColor.r, Tolerance);
        Assert.AreEqual(expected.VisualColor.g, actual.VisualColor.g, Tolerance);
        Assert.AreEqual(expected.VisualColor.b, actual.VisualColor.b, Tolerance);
        Assert.AreEqual(expected.VisualColor.a, actual.VisualColor.a, Tolerance);
    }

    private static void AssertColor(
        TerrainMistileBiomeSpawnRule rule,
        int red,
        int green,
        int blue,
        int alpha)
    {
        Assert.AreEqual(red / 255f, rule.VisualColor.r, Tolerance);
        Assert.AreEqual(green / 255f, rule.VisualColor.g, Tolerance);
        Assert.AreEqual(blue / 255f, rule.VisualColor.b, Tolerance);
        Assert.AreEqual(alpha / 255f, rule.VisualColor.a, Tolerance);
    }

    private static string CreateTemporaryDirectory()
    {
        string parent = Path.Combine(Path.GetTempPath(), "TerrainMistile.Tests");
        string directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        string expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TerrainMistile.Tests"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string resolvedDirectory = Path.GetFullPath(directory);
        if (!resolvedDirectory.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete an unexpected test directory.");
        }

        Directory.Delete(resolvedDirectory, recursive: true);
    }

    private sealed class ParsedRules
    {
        internal ParsedRules(
            TerrainMistileBiomeSpawnRule defaultRule,
            Dictionary<int, TerrainMistileBiomeSpawnRule> biomeRules,
            HashSet<string> playerBasePrefabs)
        {
            DefaultRule = defaultRule;
            BiomeRules = biomeRules;
            PlayerBasePrefabs = playerBasePrefabs;
        }

        internal TerrainMistileBiomeSpawnRule DefaultRule { get; }
        internal Dictionary<int, TerrainMistileBiomeSpawnRule> BiomeRules { get; }
        internal HashSet<string> PlayerBasePrefabs { get; }
    }

    private sealed class ExternalTerrainData
    {
        public string levelArea = "";
        public string paint = "";
        public float levelRadius = 0f;
        public float levelBorder = 0f;
    }
}
