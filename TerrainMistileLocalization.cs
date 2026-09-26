using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using YamlDotNet.Serialization;

namespace TerrainMistile;

internal static class TerrainMistileLocalization
{
    private const string FileExtension = ".yml";
    private static readonly string FilePrefix = TerrainMistilePlugin.ModName + ".";
    private static readonly Dictionary<string, Dictionary<string, string>> Languages = new(StringComparer.Ordinal);
    private static readonly char[] InvalidTokenCharacters = " (){}[]+-!?/\\&%,.:-=<>\n".ToCharArray();
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreFields()
        .Build();

    internal static void Load(ManualLogSource logger)
    {
        Languages.Clear();
        bool embeddedEnglishLoaded = TryLoadEmbeddedYaml("English", logger);
        _ = TryLoadEmbeddedYaml("Korean", logger);
        LoadExternalYamlFiles(logger);

        if (!embeddedEnglishLoaded)
        {
            logger.LogWarning(
                "Embedded English localization was not found or was invalid. " +
                "External localization files may still provide a fallback.");
        }
    }

    // The game's AddWord is private. Resolve its backing table/cache once, never per frame.
    private static class GameAccess
    {
        internal static readonly AccessTools.FieldRef<Localization> Instance =
            AccessTools.StaticFieldRefAccess<Localization>(AccessTools.Field(typeof(Localization), "m_instance"));
        internal static readonly AccessTools.FieldRef<Localization, Dictionary<string, string>> Translations =
            AccessTools.FieldRefAccess<Localization, Dictionary<string, string>>("m_translations");
        internal static readonly AccessTools.FieldRef<Localization, LRUCache<string>> Cache =
            AccessTools.FieldRefAccess<Localization, LRUCache<string>>("m_cache");
    }

    internal static void ApplyToCurrentLanguage()
    {
        // Do not force UI/platform initialization from BepInEx Awake (including on servers).
        // A later vanilla constructor receives translations through the language hooks.
        Localization? localization = GameAccess.Instance();
        if (localization == null) return;
        AddLanguages(localization.GetLanguages());
        ApplyLanguage(GameAccess.Translations(localization), localization.GetSelectedLanguage());
        GameAccess.Cache(localization).EvictAll();
    }

    [HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage), typeof(string))]
    private static class SetupLanguagePatch
    {
        private static void Postfix(string language, Dictionary<string, string> ___m_translations,
            LRUCache<string> ___m_cache)
        {
            ApplyLanguage(___m_translations, language);
            ___m_cache.EvictAll();
        }
    }

    [HarmonyPatch(typeof(Localization), "LoadLanguages")]
    private static class LoadLanguagesPatch
    {
        private static void Postfix(List<string> __result) => AddLanguages(__result);
    }

    private static void AddLanguages(List<string> languages)
    {
        foreach (string language in Languages.Keys)
            if (!languages.Contains(language)) languages.Add(language);
    }

    private static void ApplyLanguage(Dictionary<string, string> target, string language)
    {
        Languages.TryGetValue("English", out Dictionary<string, string>? english);
        Languages.TryGetValue(language, out Dictionary<string, string>? selected);
        ApplyTranslations(target, english, selected);
    }

    internal static void ApplyTranslations(Dictionary<string, string> target,
        Dictionary<string, string>? english, Dictionary<string, string>? selected)
    {
        if (english != null)
            foreach (var entry in english) target[entry.Key] = entry.Value;
        if (selected != null && !ReferenceEquals(selected, english))
            foreach (var entry in selected) target[entry.Key] = entry.Value;
    }

    private static bool TryLoadEmbeddedYaml(
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

            if (!TryAddYamlTranslations(language, yaml, file, logger))
            {
                continue;
            }

            loadedFileByLanguage[language] = file;
        }
    }

    private static bool TryAddYamlTranslations(
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
            if (!Languages.TryGetValue(language, out Dictionary<string, string> target))
                Languages.Add(language, target = new Dictionary<string, string>(StringComparer.Ordinal));
            foreach (var entry in translations)
            {
                // Preserve the former loader's token normalization and rejection policy.
                string token = entry.Key.TrimStart('$');
                if (token.Length == 0 || token.IndexOfAny(InvalidTokenCharacters) >= 0)
                {
                    logger.LogWarning($"Ignoring invalid localization token '{entry.Key}' from {source}.");
                    continue;
                }
                target[token] = entry.Value;
            }
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
