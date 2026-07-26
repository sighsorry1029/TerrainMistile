using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TerrainMistile.Tests;

[TestClass]
public sealed class TerrainMistileLocalizationTests
{
    private static readonly string[] ExpectedTokens =
    {
        "terrainmistile_creature_name",
        "terrainmistile_compendium_topic",
        "terrainmistile_message_check_compendium",
        "terrainmistile_compendium_explanation",
        "terrainmistile_compendium_protection_heading",
        "terrainmistile_compendium_requirement",
        "terrainmistile_compendium_mistile_disabled",
        "terrainmistile_compendium_protection_disabled",
        "terrainmistile_compendium_other_biomes",
        "terrainmistile_compendium_pieces_heading",
        "terrainmistile_compendium_no_pieces"
    };

    [TestMethod]
    [DataRow("TerrainMistile.French.yml", true, "French")]
    [DataRow("TerrainMistile.Portuguese_Brazilian.yml", true, "Portuguese_Brazilian")]
    [DataRow("TerrainMistile.yml", false, "")]
    [DataRow("TerrainMistile.french.yml", false, "")]
    [DataRow("TerrainMistile.French.yaml", false, "")]
    [DataRow("TerrainMistile.French.extra.yml", false, "")]
    [DataRow("terrainmistile.French.yml", false, "")]
    public void LocalizationFileNameUsesExactFlatConvention(
        string fileName,
        bool expectedSuccess,
        string expectedLanguage)
    {
        string path = Path.Combine("BepInEx", "plugins", fileName);

        bool success = TerrainMistileLocalization.TryGetLanguageFromFileName(
            path,
            out string language);

        Assert.AreEqual(expectedSuccess, success);
        Assert.AreEqual(expectedLanguage, language);
    }

    [TestMethod]
    public void LocalizationYamlSupportsUnicodeAndRuntimePlaceholders()
    {
        const string yaml =
            "terrainmistile_creature_name: \"대지의 수호자\"\n" +
            "terrainmistile_message_check_compendium: \"발헤임 개요 확인: $1\"\n" +
            "terrainmistile_compendium_requirement: \"필요 시설 종류: $1 · 판정 반경: $2m\"\n";

        bool success = TerrainMistileLocalization.TryParseYaml(
            yaml,
            out Dictionary<string, string> translations,
            out string error);

        Assert.IsTrue(success, error);
        Assert.AreEqual("대지의 수호자", translations["terrainmistile_creature_name"]);
        StringAssert.Contains(translations["terrainmistile_message_check_compendium"], "$1");
        StringAssert.Contains(translations["terrainmistile_compendium_requirement"], "$2");
    }

    [TestMethod]
    public void MalformedOrNullLocalizationValuesAreRejected()
    {
        Assert.IsFalse(TerrainMistileLocalization.TryParseYaml(
            "terrainmistile_compendium_topic: [",
            out _,
            out string malformedError));
        Assert.IsFalse(string.IsNullOrWhiteSpace(malformedError));

        Assert.IsFalse(TerrainMistileLocalization.TryParseYaml(
            "terrainmistile_compendium_topic:\n",
            out _,
            out string nullValueError));
        Assert.IsFalse(string.IsNullOrWhiteSpace(nullValueError));
    }

    [TestMethod]
    public void BuiltInLocalizationFilesContainTheSameCompleteTokenSet()
    {
        string directory = Path.Combine(
            System.AppContext.BaseDirectory,
            "Translations");
        string[] files = Directory.GetFiles(directory, "TerrainMistile.*.yml");

        Assert.AreEqual(2, files.Length);
        Dictionary<string, Dictionary<string, string>> translationsByLanguage =
            new(System.StringComparer.Ordinal);

        foreach (string file in files)
        {
            Assert.IsTrue(
                TerrainMistileLocalization.TryGetLanguageFromFileName(file, out string language),
                file);
            Assert.IsTrue(
                TerrainMistileLocalization.TryParseYaml(
                    File.ReadAllText(file),
                    out Dictionary<string, string> translations,
                    out string error),
                $"{file}: {error}");
            CollectionAssert.AreEquivalent(
                ExpectedTokens,
                translations.Keys.ToArray(),
                file);
            StringAssert.Contains(
                translations["terrainmistile_compendium_explanation"],
                "$1",
                file);
            StringAssert.Contains(
                translations["terrainmistile_compendium_mistile_disabled"],
                "$1",
                file);
            translationsByLanguage.Add(language, translations);
        }

        CollectionAssert.AreEquivalent(
            new[] { "English", "Korean" },
            translationsByLanguage.Keys.ToArray());
        Assert.AreEqual(
            "대지의 수호자",
            translationsByLanguage["Korean"]["terrainmistile_creature_name"]);
    }

    [TestMethod]
    public void BuiltInLocalizationFilesAreEmbeddedInThePlugin()
    {
        string[] resources = typeof(TerrainMistilePlugin).Assembly.GetManifestResourceNames();

        foreach (string language in new[] { "English", "Korean" })
        {
            string suffix = $".Translations.TerrainMistile.{language}.yml";
            Assert.IsTrue(
                resources.Any(name => name.EndsWith(suffix, System.StringComparison.Ordinal)),
                $"Embedded resource ending with '{suffix}' was not found.");
        }
    }
}
