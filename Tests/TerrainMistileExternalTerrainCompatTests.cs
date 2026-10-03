using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TerrainMistile.Tests
{
    [TestClass]
    public sealed class TerrainMistileExternalTerrainCompatTests
    {
        [TestMethod]
        [DataRow(typeof(StringLoader), typeof(string))]
        [DataRow(typeof(ListLoader), typeof(List<ExpandWorldData.BiomeYaml>))]
        public void ResolvesDataLoadInsteadOfParameterlessInitialization(Type manager, Type parameterType)
        {
            MethodInfo? method = TerrainMistileExternalTerrainCompat.ResolveBiomeMappingRefreshMethod(manager, "Load");
            Assert.IsNotNull(method);
            Assert.AreEqual(parameterType, method.GetParameters()[0].ParameterType);
            Assert.IsTrue(method.IsPrivate);
        }

        [TestMethod]
        [DataRow(typeof(UnsupportedLoader))]
        [DataRow(typeof(InheritedLoader))]
        public void RejectsUnknownOrInheritedLoadContracts(Type manager)
        {
            Assert.IsNull(TerrainMistileExternalTerrainCompat.ResolveBiomeMappingRefreshMethod(manager, "Load"));
        }

        [TestMethod]
        public void RejectsAmbiguousLoadContracts()
        {
            Assert.ThrowsExactly<AmbiguousMatchException>(() =>
                TerrainMistileExternalTerrainCompat.ResolveBiomeMappingRefreshMethod(typeof(AmbiguousLoader), "Load"));
        }

        [TestMethod]
        public void ResolvesExactNameSyncContractsAndRejectsOtherOverloads()
        {
            foreach (string name in new[] { "NamesFromFile", "SetNames" })
            {
                Assert.IsNotNull(TerrainMistileExternalTerrainCompat.ResolveBiomeMappingRefreshMethod(typeof(NameSync), name));
                Assert.IsNull(TerrainMistileExternalTerrainCompat.ResolveBiomeMappingRefreshMethod(typeof(UnsupportedNameSync), name));
            }
        }

        [TestMethod]
        public void MappingRefreshNotificationsAreCoalescedAndConsumedOnce()
        {
            TerrainMistileExternalTerrainCompat.ConsumeBiomeMappingRefreshRequest();
            MethodInfo notify = typeof(TerrainMistileExternalTerrainCompat).GetMethod(
                "BiomeMappingChangedPostfix", BindingFlags.Static | BindingFlags.NonPublic)!;
            try
            {
                notify.Invoke(null, null);
                notify.Invoke(null, null);
                Assert.IsTrue(TerrainMistileExternalTerrainCompat.ConsumeBiomeMappingRefreshRequest());
                Assert.IsFalse(TerrainMistileExternalTerrainCompat.ConsumeBiomeMappingRefreshRequest());
            }
            finally
            {
                TerrainMistileExternalTerrainCompat.ConsumeBiomeMappingRefreshRequest();
            }
        }

        [TestMethod]
        [TestCategory("EwdContract")]
        public void SuppliedEwdAssemblyExposesAllMappingRefreshContracts()
        {
            // Opt-in DLL inspection, without installing EWD or running its Unity/Harmony code.
            string? path = Environment.GetEnvironmentVariable("TERRAINMISTILE_EWD_DLL");
            if (string.IsNullOrWhiteSpace(path))
            {
                Assert.Inconclusive("Set TERRAINMISTILE_EWD_DLL to an original ExpandWorldData.dll to check its contracts.");
            }

            Assert.IsTrue(File.Exists(path), $"EWD assembly not found: {path}");
            AssemblyLoadContext context = new("EWD contract inspection", isCollectible: true);
            try
            {
                Assembly assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path!));
                Type manager = assembly.GetType("ExpandWorldData.BiomeManager", throwOnError: true)!;
                foreach (string name in new[] { "NamesFromFile", "SetNames", "Load" })
                {
                    MethodInfo? method = TerrainMistileExternalTerrainCompat.ResolveBiomeMappingRefreshMethod(manager, name);
                    Assert.IsNotNull(method, $"Unsupported {manager.FullName}.{name} in {path}");
                    Assert.AreEqual(manager, method.DeclaringType);
                }
            }
            finally
            {
                context.Unload();
            }
        }

        private sealed class StringLoader
        {
            public static void Load() { }
            private static void Load(string yaml) { }
        }

        private sealed class ListLoader
        {
            public static void Load() { }
            private static void Load(List<ExpandWorldData.BiomeYaml> data) { }
        }

        private sealed class UnsupportedLoader
        {
            public void Load(string yaml) { }
            public static void Load() { }
            public static int Load(List<ExpandWorldData.BiomeYaml> data) => 0;
            public static void Load(List<int> data) { }
            public static void Load<T>(string yaml) { }
            public static void Load(ref string yaml) { }
        }

        private class BaseLoader
        {
            public static void Load(string yaml) { }
        }

        private sealed class InheritedLoader : BaseLoader { }

        private sealed class AmbiguousLoader
        {
            public static void Load(string yaml) { }
            public static void Load(List<ExpandWorldData.BiomeYaml> data) { }
        }

        private sealed class NameSync
        {
            public static void NamesFromFile() { }
            public static void NamesFromFile(string ignored) { }
            public static void SetNames(Dictionary<Heightmap.Biome, string> names) { }
            public static void SetNames(string ignored) { }
        }

        private sealed class UnsupportedNameSync
        {
            public static int NamesFromFile() => 0;
            public static void SetNames(Dictionary<int, string> names) { }
        }
    }
}

namespace ExpandWorldData
{
    internal sealed class BiomeYaml { }
}
