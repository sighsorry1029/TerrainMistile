using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using Jotunn.Entities;
using Jotunn.Managers;
using YamlDotNet.Serialization;

namespace TerrainMistile;

internal static class TerrainMistileLocalization
{
    private const string FileExtension = ".yml";
    private static readonly string FilePrefix = TerrainMistilePlugin.ModName + ".";
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreFields()
        .Build();

    internal static void Load(ManualLogSource logger)
    {
        CustomLocalization localization = LocalizationManager.Instance.GetLocalization();
        bool embeddedEnglishLoaded = TryLoadEmbeddedYaml(localization, "English", logger);
        _ = TryLoadEmbeddedYaml(localization, "Korean", logger);
        LoadExternalYamlFiles(localization, logger);

        if (!embeddedEnglishLoaded)
        {
            logger.LogWarning(
                "Embedded English localization was not found or was invalid. " +
                "External localization files may still provide a fallback.");
        }
    }

    private static bool TryLoadEmbeddedYaml(
        CustomLocalization localization,
        string language,
        ManualLogSource logger)
    {
        string resourceSuffix =
            ".Translations." + TerrainMistilePlugin.ModName + "." + language + FileExtension;
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.Ordinal));
        if (resourceName == null)
        {
            logger.LogWarning(
                $"Embedded localization resource not found: " +
                $"Translations/{TerrainMistilePlugin.ModName}.{language}{FileExtension}");
            return false;
        }

        try
        {
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                logger.LogWarning($"Embedded localization resource could not be opened: {resourceName}");
                return false;
            }

            using StreamReader reader = new(stream, Encoding.UTF8, true);
            return TryAddYamlTranslations(
                localization,
                language,
                reader.ReadToEnd(),
                $"embedded {TerrainMistilePlugin.ModName}.{language}{FileExtension}",
                logger);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                $"Could not load embedded {language} localization: {exception.Message}");
            return false;
        }
    }

    private static void LoadExternalYamlFiles(
        CustomLocalization localization,
        ManualLogSource logger)
    {
        string rootPath = Paths.BepInExRootPath;
        string[] files;

        try
        {
            files = Directory.GetFiles(
                rootPath,
                FilePrefix + "*" + FileExtension,
                SearchOption.AllDirectories);
        }
        catch (Exception exception)
        {
            logger.LogWarning($"Could not search for TerrainMistile localization files: {exception.Message}");
            return;
        }

        string[] orderedFiles = files
            .OrderBy(path => IsInsideDirectory(path, Paths.ConfigPath) ? 1 : 0)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, string> loadedFileByLanguage = new(StringComparer.Ordinal);

        foreach (string file in orderedFiles)
        {
            if (!TryGetLanguageFromFileName(file, out string language))
            {
                logger.LogWarning(
                    $"Ignoring localization file with an invalid name: {file}. " +
                    $"Expected {TerrainMistilePlugin.ModName}.<Language>{FileExtension}.");
                continue;
            }

            string yaml;
            try
            {
                yaml = File.ReadAllText(file, Encoding.UTF8);
            }
            catch (Exception exception)
            {
                logger.LogWarning($"Could not read {language} localization from {file}: {exception.Message}");
                continue;
            }

            if (loadedFileByLanguage.TryGetValue(language, out string previousFile))
            {
                logger.LogWarning(
                    $"Loading another {language} localization file from {file}. " +
                    $"Its duplicate tokens override values loaded from {previousFile}.");
            }

            if (!TryAddYamlTranslations(localization, language, yaml, file, logger))
            {
                continue;
            }

            loadedFileByLanguage[language] = file;
        }
    }

    private static bool TryAddYamlTranslations(
        CustomLocalization localization,
        string language,
        string yaml,
        string source,
        ManualLogSource logger)
    {
        if (!TryParseYaml(yaml, out Dictionary<string, string> translations, out string error))
        {
            logger.LogWarning($"Could not load {language} localization from {source}: {error}");
            return false;
        }

        try
        {
            localization.AddTranslation(language, translations);
            logger.LogInfo(
                $"Loaded {translations.Count} {language} localization entries from {source}.");
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                $"Could not register {language} localization from {source}: {exception.Message}");
            return false;
        }
    }

    internal static bool TryGetLanguageFromFileName(string path, out string language)
    {
        language = string.Empty;
        string fileName = Path.GetFileName(path);
        if (!fileName.StartsWith(FilePrefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(FileExtension, StringComparison.Ordinal))
        {
            return false;
        }

        int languageLength = fileName.Length - FilePrefix.Length - FileExtension.Length;
        if (languageLength <= 0)
        {
            return false;
        }

        language = fileName.Substring(FilePrefix.Length, languageLength);
        if (language.IndexOf('.') >= 0 || !char.IsUpper(language[0]))
        {
            language = string.Empty;
            return false;
        }

        return true;
    }

    internal static bool TryParseYaml(
        string yaml,
        out Dictionary<string, string> translations,
        out string error)
    {
        translations = new Dictionary<string, string>(StringComparer.Ordinal);
        error = string.Empty;

        try
        {
            Dictionary<string, string?>? parsed =
                Deserializer.Deserialize<Dictionary<string, string?>>(yaml);
            if (parsed == null || parsed.Count == 0)
            {
                error = "The localization file is empty.";
                return false;
            }

            foreach (KeyValuePair<string, string?> entry in parsed)
            {
                string key = entry.Key?.Trim() ?? string.Empty;
                if (key.Length == 0)
                {
                    error = "Localization keys must not be empty.";
                    return false;
                }

                if (entry.Value == null)
                {
                    error = $"Localization value for '{key}' must not be null.";
                    return false;
                }

                if (translations.ContainsKey(key))
                {
                    error = $"Localization key '{key}' is duplicated.";
                    return false;
                }

                translations.Add(key, entry.Value);
            }

            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        string directoryPrefix = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
