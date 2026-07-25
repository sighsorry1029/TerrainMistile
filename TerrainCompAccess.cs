using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TerrainMistile;

internal static class TerrainCompAccess
{
    private delegate void SaveTerrainComp(TerrainComp terrainComp);

    private static AccessTools.FieldRef<List<TerrainComp>> _instances = null!;
    private static AccessTools.FieldRef<TerrainComp, Heightmap> _heightmap = null!;
    private static AccessTools.FieldRef<TerrainComp, int> _operations = null!;
    private static AccessTools.FieldRef<TerrainComp, bool[]> _modifiedHeight = null!;
    private static AccessTools.FieldRef<TerrainComp, float[]> _levelDelta = null!;
    private static AccessTools.FieldRef<TerrainComp, float[]> _smoothDelta = null!;
    private static AccessTools.FieldRef<TerrainComp, bool[]> _modifiedPaint = null!;
    private static AccessTools.FieldRef<TerrainComp, Color[]> _paintMask = null!;
    private static AccessTools.FieldRef<TerrainComp, Vector3> _lastOperationPoint = null!;
    private static AccessTools.FieldRef<TerrainComp, float> _lastOperationRadius = null!;
    private static SaveTerrainComp _save = null!;
    private static bool _initialized;

    internal static List<TerrainComp> Instances => _instances();

    internal static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        Bindings bindings = ResolveBindings();
        _instances = AccessTools.StaticFieldRefAccess<List<TerrainComp>>(bindings.Instances);
        _heightmap = AccessTools.FieldRefAccess<TerrainComp, Heightmap>(bindings.Heightmap);
        _operations = AccessTools.FieldRefAccess<TerrainComp, int>(bindings.Operations);
        _modifiedHeight = AccessTools.FieldRefAccess<TerrainComp, bool[]>(bindings.ModifiedHeight);
        _levelDelta = AccessTools.FieldRefAccess<TerrainComp, float[]>(bindings.LevelDelta);
        _smoothDelta = AccessTools.FieldRefAccess<TerrainComp, float[]>(bindings.SmoothDelta);
        _modifiedPaint = AccessTools.FieldRefAccess<TerrainComp, bool[]>(bindings.ModifiedPaint);
        _paintMask = AccessTools.FieldRefAccess<TerrainComp, Color[]>(bindings.PaintMask);
        _lastOperationPoint = AccessTools.FieldRefAccess<TerrainComp, Vector3>(bindings.LastOperationPoint);
        _lastOperationRadius = AccessTools.FieldRefAccess<TerrainComp, float>(bindings.LastOperationRadius);
        _save = AccessTools.MethodDelegate<SaveTerrainComp>(bindings.Save, virtualCall: false);
        _initialized = true;
    }

    internal static bool TryCaptureScanData(TerrainComp terrainComp, out ScanData data)
    {
        Heightmap heightmap = _heightmap(terrainComp);
        bool[] modifiedHeight = _modifiedHeight(terrainComp);
        float[] levelDelta = _levelDelta(terrainComp);
        float[] smoothDelta = _smoothDelta(terrainComp);
        int vertexWidth = heightmap ? heightmap.m_width + 1 : 0;
        long requiredCellCount = (long)vertexWidth * vertexWidth;
        if (!heightmap ||
            vertexWidth <= 0 ||
            requiredCellCount > int.MaxValue ||
            modifiedHeight == null ||
            levelDelta == null ||
            smoothDelta == null ||
            modifiedHeight.Length < requiredCellCount ||
            levelDelta.Length < requiredCellCount ||
            smoothDelta.Length < requiredCellCount)
        {
            data = default;
            return false;
        }

        data = new ScanData(
            heightmap,
            vertexWidth,
            _operations(terrainComp),
            modifiedHeight,
            levelDelta,
            smoothDelta);
        return true;
    }

    internal static bool TryCaptureResetData(
        TerrainComp terrainComp,
        bool includePaint,
        out ResetData data)
    {
        if (!TryCaptureScanData(terrainComp, out ScanData scan))
        {
            data = default;
            return false;
        }

        if (!includePaint)
        {
            data = new ResetData(scan, Array.Empty<bool>(), Array.Empty<Color>());
            return true;
        }

        bool[] modifiedPaint = _modifiedPaint(terrainComp);
        Color[] paintMask = _paintMask(terrainComp);
        int requiredCellCount = scan.VertexWidth * scan.VertexWidth;
        if (modifiedPaint == null ||
            paintMask == null ||
            modifiedPaint.Length < requiredCellCount ||
            paintMask.Length < requiredCellCount)
        {
            data = default;
            return false;
        }

        data = new ResetData(scan, modifiedPaint, paintMask);
        return true;
    }

    internal static void CommitReset(TerrainComp terrainComp, Vector3 center, float radius)
    {
        ref int operations = ref _operations(terrainComp);
        operations++;
        _lastOperationPoint(terrainComp) = center;
        _lastOperationRadius(terrainComp) = radius;
        _save(terrainComp);
    }

    private static Bindings ResolveBindings()
    {
        return new Bindings();
    }

    private static FieldInfo RequireField(string name, Type fieldType, bool isStatic = false)
    {
        FieldInfo field = typeof(TerrainComp).GetField(
                              name,
                              BindingFlags.Public |
                              BindingFlags.NonPublic |
                              BindingFlags.Instance |
                              BindingFlags.Static |
                              BindingFlags.DeclaredOnly) ??
                          throw new MissingFieldException(typeof(TerrainComp).FullName, name);
        if (field.FieldType != fieldType || field.IsStatic != isStatic)
        {
            throw new InvalidOperationException(
                $"{typeof(TerrainComp).FullName}.{name} no longer matches the expected field contract.");
        }

        return field;
    }

    private static MethodInfo RequireMethod(string name)
    {
        MethodInfo method = typeof(TerrainComp).GetMethod(
                                name,
                                BindingFlags.Public |
                                BindingFlags.NonPublic |
                                BindingFlags.Instance |
                                BindingFlags.DeclaredOnly,
                                binder: null,
                                types: Type.EmptyTypes,
                                modifiers: null) ??
                            throw new MissingMethodException(typeof(TerrainComp).FullName, name);
        if (method.IsStatic || method.ReturnType != typeof(void) || method.GetParameters().Length != 0)
        {
            throw new InvalidOperationException(
                $"{typeof(TerrainComp).FullName}.{name} no longer matches the expected method contract.");
        }

        return method;
    }

    private sealed class Bindings
    {
        internal readonly FieldInfo Instances =
            RequireField("s_instances", typeof(List<TerrainComp>), isStatic: true);
        internal readonly FieldInfo Heightmap = RequireField("m_hmap", typeof(Heightmap));
        internal readonly FieldInfo Operations = RequireField("m_operations", typeof(int));
        internal readonly FieldInfo ModifiedHeight = RequireField("m_modifiedHeight", typeof(bool[]));
        internal readonly FieldInfo LevelDelta = RequireField("m_levelDelta", typeof(float[]));
        internal readonly FieldInfo SmoothDelta = RequireField("m_smoothDelta", typeof(float[]));
        internal readonly FieldInfo ModifiedPaint = RequireField("m_modifiedPaint", typeof(bool[]));
        internal readonly FieldInfo PaintMask = RequireField("m_paintMask", typeof(Color[]));
        internal readonly FieldInfo LastOperationPoint = RequireField("m_lastOpPoint", typeof(Vector3));
        internal readonly FieldInfo LastOperationRadius = RequireField("m_lastOpRadius", typeof(float));
        internal readonly MethodInfo Save = RequireMethod("Save");
    }

    internal readonly struct ScanData
    {
        internal readonly Heightmap Heightmap;
        internal readonly int VertexWidth;
        internal readonly int Operations;
        internal readonly bool[] ModifiedHeight;
        internal readonly float[] LevelDelta;
        internal readonly float[] SmoothDelta;

        internal ScanData(
            Heightmap heightmap,
            int vertexWidth,
            int operations,
            bool[] modifiedHeight,
            float[] levelDelta,
            float[] smoothDelta)
        {
            Heightmap = heightmap;
            VertexWidth = vertexWidth;
            Operations = operations;
            ModifiedHeight = modifiedHeight;
            LevelDelta = levelDelta;
            SmoothDelta = smoothDelta;
        }
    }

    internal readonly struct ResetData
    {
        internal readonly ScanData Scan;
        internal readonly bool[] ModifiedPaint;
        internal readonly Color[] PaintMask;

        internal ResetData(ScanData scan, bool[] modifiedPaint, Color[] paintMask)
        {
            Scan = scan;
            ModifiedPaint = modifiedPaint;
            PaintMask = paintMask;
        }
    }
}
