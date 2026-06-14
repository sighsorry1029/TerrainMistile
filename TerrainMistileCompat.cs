using UnityEngine;

namespace TerrainMistile;

public static class TerrainMistileCompat
{
    public static void RegisterIgnoredTerrainArea(Vector3 center, float radius, string source)
    {
        TerrainMistileSystem.RegisterExternalTerrainIgnoreArea(center, radius, source);
    }
}
