# TerrainMistile

Spawns a cloned Mistile prefab named `TerrainMistile` near changed terrain. TerrainMistiles use a neutral Dverger-style faction, fly toward the changed terrain point, and reset nearby `TerrainComp` deltas when they impact the terrain so the terrain returns to the world/location baseline rather than raw worldgen.

Requires Jotunn and BepInEx.

## Notes

- Spawn rules are loaded from `BepInEx/config/TerrainMistile.yml` and synced from the server with ServerSync.
- Changed terrain is grouped into 32 meter units. Each unit rolls on its biome rule interval while at least one player is within that biome rule's search range.
- TerrainMistiles do not hunt players; they target the changed terrain point that caused the spawn roll.
- Biomes can be disabled by setting their YAML `interval` or `maxSpawn` to `0`. The default YAML disables Meadows.
- YAML biome keys support vanilla biome names, Expand World Data custom biome names, or numeric biome values.
- `perPlayerSpawn` can make one successful unit roll spawn one TerrainMistile per nearby player when distinct changed targets are available, capped by `maxSpawn`.
- Player base checks count distinct PlayerBase prefab types within 20 meters of the changed terrain point. YAML `playerBaseValue: 0` disables the check.
- Resetting clears player-style `TerrainComp` height and paint deltas in the explosion radius. Location terrain deformation remains because it is reapplied by vanilla terrain modifiers during heightmap regeneration.
- Terrain reset only runs after TerrainMistile self-destruct, terrain impact, or stuck detonation. Killing it before self-destruct does not reset terrain.

## Spawn Rules YAML

```yml
# PlayerBase EffectArea prefabs from vanilla dump: ashwood_bed, bed, blackforge, blastfurnace, BogWitch_Fire_Pit, bonfire, charcoal_kiln, charred_shieldgenerator, dverger_guardstone, eitrrefinery, fermenter, fire_pit, fire_pit_haldor, fire_pit_hildir, fire_pit_iron, forge, guard_stone, hearth, piece_artisanstation, piece_bed02, piece_brazierceiling01, piece_brazierfloor01, piece_brazierfloor02, piece_groundtorch, piece_groundtorch_blue, piece_groundtorch_green, piece_groundtorch_mist, piece_groundtorch_wood, piece_magetable, piece_oven, piece_shieldgenerator, piece_spinningwheel, piece_stonecutter, piece_walltorch, piece_workbench, portal, portal_stone, portal_wood, smelter, windmill
# Vanilla biome names: Meadows, BlackForest, Swamp, Mountain, Plains, Mistlands, AshLands, DeepNorth, Ocean
# Expand World Data custom biomes can use their custom biome name or numeric biome value.
# defaults is reserved. Every other top-level key is treated as a biome rule.
defaults:
  interval: 60 # Seconds between spawn rolls for one 32m terrain unit. 0 disables that biome.
  searchRange: 24 # Players within this horizontal range of a changed terrain unit activate its rolls.
  spawnChance: 0.25 # Chance used when the unit interval is ready and at least one player is nearby.
  maxSpawn: 3 # Maximum active TerrainMistiles with targets within 32m of the target. 0 disables that biome.
  perPlayerSpawn: true # If true, one successful roll can spawn up to one TerrainMistile per nearby player, capped by maxSpawn and available targets.
  playerBaseValue: 1 # Unique PlayerBase prefab type count within 20m required to suppress TerrainMistile spawning. 0 disables the PlayerBase check.
  spawnRadius: 16~32 # Horizontal spawn distance from the selected nearby player. Use 24 for fixed distance or 16~32 for a random range.
  spawnAltitude: 8 # Height above solid ground where TerrainMistile spawns.
  resetRadius: 8 # Radius of terrain height and paint reset when TerrainMistile detonates.
Meadows:
  interval: 0
BlackForest:
  interval: 120
Mountain:
  interval: 180
```

- `interval`: seconds between spawn rolls for one changed 32 meter terrain unit. `0` disables that biome.
- `searchRange`: horizontal player range that activates changed terrain units in that biome.
- `spawnChance`: chance used when the unit interval is ready and at least one player is nearby.
- `maxSpawn`: maximum active TerrainMistiles with targets within 32 meters of the target. `0` disables that biome.
- `perPlayerSpawn`: if true, one successful unit roll can spawn one TerrainMistile per nearby player when distinct changed targets are available.
- `playerBaseValue`: unique PlayerBase prefab type threshold that skips spawn rolls near bases. `0` disables the check.
- `spawnRadius`: horizontal spawn distance from the selected nearby player. Use `24` for a fixed distance or `16~32` for a random range.
- `spawnAltitude`: height above solid ground where TerrainMistile spawns.
- `resetRadius`: terrain reset radius saved onto each TerrainMistile when it spawns.
- `defaults` is reserved. Every other top-level key is treated as a biome rule.
- Missing biome values use `defaults`. Expand World Data custom biome entries should use the `biome` name from `expand_biomes*.yaml`; numeric values are also accepted.

## PlayerBase Detection

`playerBaseValue` can skip TerrainMistile spawn checks near player bases. TerrainMistile counts distinct loaded prefab types that have a PlayerBase `EffectArea`; repeated copies of the same piece count once. When a 32 meter terrain unit is ignored by this check, that unit is briefly cached so later cells in the same unit are skipped without repeating the PlayerBase physics query. The default YAML includes the current vanilla PlayerBase prefab dump as a comment.

## Original Template Notes

This project started from a ServerSync template. ServerSync is still used for synchronized config and version checking.

Thank you Blaxxun for ServerSync!

ServerSync
==========

Bundling the dll
----------------

You need to ensure the dll is available to your mod.

Including the dll is best done via ILRepack (https://github.com/ravibpatel/ILRepack.Lib.MSBuild.Task). You can load this package (ILRepack.Lib.MSBuild.Task) from NuGet.

Then create a file ILRepack.targets in your project folder. File content:
```
<?xml version="1.0" encoding="utf-8"?>
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
    <Target Name="ILRepacker" AfterTargets="Build">
        <ItemGroup>
            <InputAssemblies Include="$(TargetPath)" />
            <InputAssemblies Include="$(OutputPath)\ServerSync.dll" />
        </ItemGroup>
        <ILRepack Parallel="true" DebugInfo="true" Internalize="true" InputAssemblies="@(InputAssemblies)" OutputFile="$(TargetPath)" TargetKind="SameAsPrimaryAssembly" LibraryPath="$(OutputPath)" />
    </Target>
</Project>
```

Using the ServerSync
--------------------

Declare a variable:

`ServerSync.ConfigSync configSync = new ServerSync.ConfigSync("my.mod.guid") { DisplayName = "My Mod Name", CurrentVersion = "1.2.3", MinimumRequiredVersion = "1.2.0" };`

All of DisplayName, CurrentVersion and MinimumRequiredVersion are optional.
If CurrentVersion is specified, then the user will see a warning in their BepInEx log if the server version does not match the client version.
If also MinimumRequiredVersion is specified and the client has an older version than the servers MinimumRequiredVersion, the client will be immediately disconnected and see an error message, explaining why.
To display a friendly name for your mod in the error messages, specify DisplayName, otherwise the primary identifier will be used.
Also note that the primary identifier (I propose using the GUID, "my.mod.guid") should never be changed (changing it will break backwards compatibility completely).

There are two public methods on the ServerSync.ConfigSync class:

- `AddConfigEntry<T>(ConfigEntry<T> configEntry)`

  Registers a BepInEx ConfigEntry to be synchronized.

- `AddLockingConfigEntry<T>(ConfigEntry<T> lockingConfig) where T : IConvertible`

  Registers a BepInEx ConfigEntry to be synchronized, whose value determines whether the config is locked. If the value is zero when converted to integer, the config is not locked. Otherwise it is locked.
  This method must be called at most once. If not called at all, the config will never be locked.

Useful properties:

- `static bool ProcessingServerUpdate`

  The mod is receiving and applying configs from the server. Used internally to avoid config writing loops.

- `bool IsSourceOfTruth`

  Whether the local config is currently being used. False if a remote config is currently applied.

Additionally, there is a class `ServerSync.CustomSyncedValue<T>(ConfigSync, string Identifier, T value = default)` to synchronize arbitrary data (more precisely: all data which Valheims native serialization supports).
This class registers itself to the passed ConfigSync instance upon instantiation.
It provides a Value property and a ValueChanged event handler.
The Identifier must be unique for the given ConfigSync instance.


Handy config function
---------------------

To avoid manually adding each config entry to the ConfigSync instance, I propose to add a simple wrapper `config()` (with the same signature as `Config.Bind()`) to your UnityBasePlugin class:

```
ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
{
    ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

    SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
    syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

    return configEntry;
}

ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);
```
