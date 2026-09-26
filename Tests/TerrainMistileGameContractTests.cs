using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnityEngine;

namespace TerrainMistile.Tests;

// These run against original game assemblies. They resolve managed contracts without running Unity.
[TestClass]
public sealed class TerrainMistileGameContractTests
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    [TestMethod]
    public void TerrainCompilerPrivateBindingsResolveOnOriginalGame()
    {
        MethodInfo? save = typeof(TerrainComp).GetMethod("Save", Members, null, new[] { typeof(bool) }, null);
        Assert.IsNotNull(save);
        Assert.IsTrue(save.IsPrivate, "Tests must use the original, non-publicized game DLL.");
        // Resolve all names/types without executing Harmony's Mono-specific code generator on CoreCLR.
        MethodInfo resolve = typeof(TerrainCompAccess).GetMethod("ResolveBindings", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsNotNull(resolve.Invoke(null, null));
    }

    [TestMethod]
    public void LocationAndScenePatchTargetsRetainExpectedContracts()
    {
        Assert.IsNotNull(typeof(ZoneSystem).GetMethod("SpawnLocation", Members, null,
            new[] { typeof(ZoneSystem.ZoneLocation), typeof(int), typeof(Vector3), typeof(Quaternion),
                typeof(ZoneSystem.SpawnMode), typeof(List<GameObject>), typeof(bool) }, null));
        Assert.IsNotNull(typeof(ZNetScene).GetMethod("Awake", Members, null, Type.EmptyTypes, null));
        Assert.IsNotNull(typeof(ZNetScene).GetMethod("OnDestroy", Members, null, Type.EmptyTypes, null));
        FieldInfo? prefabs = typeof(ZNetScene).GetField("m_namedPrefabs", Members);
        Assert.IsNotNull(prefabs);
        Assert.IsTrue(prefabs.IsPrivate);
        Assert.AreEqual(typeof(Dictionary<int, GameObject>), prefabs.FieldType);
    }

    [TestMethod]
    public void LocalizationBackingFieldsAndLanguageHooksResolveOnOriginalGame()
    {
        Assert.IsNotNull(typeof(Localization).GetMethod("SetupLanguage", Members, null, new[] { typeof(string) }, null));
        Assert.IsNotNull(typeof(Localization).GetMethod("LoadLanguages", Members, null, Type.EmptyTypes, null));
        FieldInfo translations = typeof(Localization).GetField("m_translations", Members)!;
        Assert.IsTrue(translations.IsPrivate);
        Assert.AreEqual(typeof(Dictionary<string, string>), translations.FieldType);
        FieldInfo cache = typeof(Localization).GetField("m_cache", Members)!;
        Assert.IsTrue(cache.IsPrivate);
        Assert.AreEqual(typeof(LRUCache<string>), cache.FieldType);
        FieldInfo instance = typeof(Localization).GetField("m_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.IsTrue(instance.IsPrivate);
        Assert.AreEqual(typeof(Localization), instance.FieldType);
    }

    [TestMethod]
    public void MergedPluginDoesNotReferenceJotunnOrSeparateServerSync()
    {
        string[] references = typeof(TerrainMistilePlugin).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        CollectionAssert.DoesNotContain(references, "Jotunn");
        CollectionAssert.DoesNotContain(references, "ServerSync");
        Assert.IsFalse(typeof(TerrainMistilePlugin).GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType.FullName == "BepInEx.BepInDependency" &&
            attribute.ConstructorArguments.Any(argument => Equals(argument.Value, "com.jotunn.jotunn"))));
    }
}
