# TerrainMistile

Spawns configurable TerrainMistiles from changed terrain. They seek the edit, detonate, and reset player-made height/paint changes while preserving world/location terrain. Tune biome chance, interval, radius, health, color, and base protection.

![](https://i.ibb.co/21mZ5f8f/meadowexample.gif) <br>
TerrainMistiles try to reset changed terrain, but players can stop them by destroying them before impact.

![](https://i.ibb.co/VYWYBqRh/playerbasesafe.gif) <br>
Terrain changes near player bases can be protected from TerrainMistile spawn checks. The base-check radius and required number of unique base prefabs are configurable per biome.

![](https://i.ibb.co/DfmbJd3t/mountainexample.gif) <br>
![](https://i.ibb.co/7x7f01kR/tarpitexample.gif) <br>
![](https://i.ibb.co/RkFrvQWY/ashlandexample.gif) <br>
TerrainMistile spawn chance, interval, visual color, health, and reset radius can be configured per biome.

## How It Works

- Changed terrain is grouped into 32m terrain units.
- Each changed unit rolls on its biome rule interval while at least one player is within `playerSearchRadius`.
- TerrainMistiles target changed terrain points, not players.
- A TerrainMistile reset happens when it reaches terrain impact or uses its self-destruct attack.
- Killing a TerrainMistile before self-destruct does not reset terrain.
- Reset clears player-style `TerrainComp` height and paint deltas in `resetRadius`.
- Location and world baseline terrain are preserved, so location terrain changes can remain after reset.
- TerrainMistiles use nonblocking colliders so terrain and pieces do not trap them before impact.

## TerrainMistile.yml

- Main spawn rule file.
- Synced from the server.
- Generated automatically if missing.
- `defaults` supplies fallback values.
- `playerBasePrefabs` is the editable base-protection prefab list.
- Every other top-level key is treated as a biome rule.
- Expand World Data custom biomes can use their custom biome name or numeric biome value.

## YAML Example

```yml
# Expand World Data custom biomes can use their custom biome name or numeric biome value.
# defaults and playerBasePrefabs are reserved. Every other top-level key is treated as a biome rule.

defaults:
  interval: 60
  playerSearchRadius: 32
  spawnChance: 0.25
  maxDeformationSpawnChanceBonus: 0.25
  maxSpawn: 3
  perPlayerSpawn: true
  playerBaseValue: 1
  baseCheckRadius: 24
  spawnRadius: 16~32
  spawnAltitude: 8
  resetRadius: 8
  health: 1
  visualColor: "#45FF5A"

Meadows:
  interval: 120
  spawnChance: 0.1
  resetRadius: 4
  visualColor: "#7CFF6B"
BlackForest:
  interval: 120
  resetRadius: 6
  visualColor: "#2ED36F"
Swamp:
  playerBaseValue: 2
  visualColor: "#8FBF3F"
Mountain:
  interval: 120
  playerBaseValue: 2
  resetRadius: 6
  visualColor: "#8FE8FF"
Plains:
  interval: 120
  playerBaseValue: 3
  resetRadius: 6
  visualColor: "#FFD15C"
Mistlands:
  interval: 120
  playerBaseValue: 3
  resetRadius: 6
  visualColor: "#B58CFF"
AshLands:
  playerBaseValue: 4
  visualColor: "#FF5A2E"
DeepNorth:
  playerBaseValue: 4
  visualColor: "#BFEFFF"
Ocean:
  visualColor: "#3EA7FF"

playerBasePrefabs:
  - workbench
  - forge
  - portal
```

The generated YAML includes the full vanilla `playerBasePrefabs` list. Add custom player-placed piece prefab names there if they should protect terrain from TerrainMistile spawn checks.

## Spawn Rule Fields

- `interval`: seconds between spawn rolls for one changed 32m terrain unit. `0` disables that biome.
- `playerSearchRadius`: players within this horizontal radius activate rolls for a changed unit.
- `spawnChance`: base chance used when a unit interval is ready and at least one player is nearby.
- `maxDeformationSpawnChanceBonus`: bonus added to `spawnChance` when the largest height deformation in the 32m terrain unit reaches Valheim's 8m deformation cap. The bonus scales linearly from 0m to 8m and the final chance is clamped to 1.
- `maxSpawn`: maximum active TerrainMistiles with targets within 32m of the target. `0` disables that biome.
- `perPlayerSpawn`: if true, one successful roll can spawn up to one TerrainMistile per nearby player, capped by `maxSpawn` and available targets.
- `playerBaseValue`: unique listed player-placed base prefab type count required to skip spawn checks. `0` disables the player base check.
- `baseCheckRadius`: horizontal radius used by `playerBaseValue`.
- `spawnRadius`: horizontal spawn distance from the selected nearby player. Use `24` for fixed distance or `16~32` for a random range.
- `spawnAltitude`: height above solid ground where TerrainMistile spawns.
- `resetRadius`: terrain height and paint reset radius saved onto each spawned TerrainMistile.
- `health`: maximum and current health applied when TerrainMistile spawns.
- `visualColor`: HTML hex color used for flames, sparks, and light.

## Player Base Protection

`playerBaseValue` counts unique prefab names from `playerBasePrefabs` within `baseCheckRadius` meters of the changed terrain point. Repeated copies of the same prefab count once.

Only player-placed instances are counted. Internally, TerrainMistile checks the ZDO `longs.creator` field and ignores matching prefabs when `creator == 0`, which filters out noCreator world and location pieces.

Examples:

- `playerBaseValue: 0`: no base protection check.
- `playerBaseValue: 1`: one listed player-placed prefab nearby is enough to skip spawn checks.
- `playerBaseValue: 3`: three different listed player-placed prefab types must be nearby to skip spawn checks.

## Tuning Tips

- Increase `interval` to reduce spawn roll frequency.
- Lower `spawnChance` to make TerrainMistiles rarer without disabling a biome.
- Lower `maxDeformationSpawnChanceBonus` if near-cap terrain edits should not strongly increase spawn chance.
- Set both `spawnChance` and `maxDeformationSpawnChanceBonus` to `0` for chance-based suppression, or use `interval: 0` / `maxSpawn: 0` to disable a biome.
- Increase `playerSearchRadius` if changed terrain should stay active from farther away.
- Increase `resetRadius` for tall or wide terrain edits that need a larger reset area.
- Raise `playerBaseValue` in dangerous biomes if simple one-piece bases should not fully suppress spawns.
