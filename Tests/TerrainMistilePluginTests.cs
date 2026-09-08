using System;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TerrainMistile.Tests;

[TestClass]
[DoNotParallelize]
public sealed class TerrainMistilePluginTests
{
    private string _temporaryDirectory = string.Empty;

    [TestInitialize]
    public void CreateTemporaryDirectory()
    {
        _temporaryDirectory = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "TerrainMistile.PluginTests",
            Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [TestCleanup]
    public void DeleteTemporaryDirectory()
    {
        if (string.IsNullOrEmpty(_temporaryDirectory))
        {
            return;
        }

        string expectedRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "TerrainMistile.PluginTests")) + Path.DirectorySeparatorChar;
        string actualDirectory = Path.GetFullPath(_temporaryDirectory);
        Assert.IsTrue(
            actualDirectory.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase),
            "Only the temporary directory owned by this test may be removed.");
        if (Directory.Exists(actualDirectory))
        {
            Directory.Delete(actualDirectory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public void SaveOrReloadPreservesSaveOnConfigSetAndPersistsExpectedValue(
        bool reload,
        bool originalSaveOnSet)
    {
        string configPath = Path.Combine(_temporaryDirectory, "settings.cfg");
        ConfigFile config = new(configPath, saveOnInit: false)
        {
            SaveOnConfigSet = false
        };
        ConfigEntry<int> value = config.Bind("Tests", "Value", 41);
        config.Save();
        value.Value = 42;
        config.SaveOnConfigSet = originalSaveOnSet;

        InvokeConfigSave(config, reload);

        int expectedValue = reload ? 41 : 42;
        Assert.AreEqual(originalSaveOnSet, config.SaveOnConfigSet);
        Assert.AreEqual(expectedValue, value.Value);
        ConfigFile savedConfig = new(configPath, saveOnInit: false)
        {
            SaveOnConfigSet = false
        };
        Assert.AreEqual(expectedValue, savedConfig.Bind("Tests", "Value", -1).Value);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void SaveOrReloadFailureRestoresSaveOnConfigSet(
        bool reload,
        bool originalSaveOnSet)
    {
        string configPath = reload
            ? Path.Combine(_temporaryDirectory, "missing.cfg")
            : _temporaryDirectory;
        ConfigFile config = new(configPath, saveOnInit: false)
        {
            SaveOnConfigSet = originalSaveOnSet
        };

        TargetInvocationException exception = Assert.ThrowsExactly<TargetInvocationException>(
            () => InvokeConfigSave(config, reload));

        Assert.IsInstanceOfType(
            exception.InnerException,
            reload ? typeof(FileNotFoundException) : typeof(UnauthorizedAccessException));
        Assert.AreEqual(originalSaveOnSet, config.SaveOnConfigSet);
    }

    private static void InvokeConfigSave(ConfigFile config, bool reload)
    {
        Type? persistence = typeof(TerrainMistilePlugin).GetNestedType(
            "ConfigPersistence", BindingFlags.NonPublic);
        Assert.IsNotNull(persistence);
        MethodInfo? method = persistence.GetMethod("Save", BindingFlags.Static | BindingFlags.Public);
        Assert.IsNotNull(method);
        method.Invoke(null, new object[] { config, reload });
    }
}
