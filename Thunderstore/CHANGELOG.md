| `Version` | `Update Notes`    |
|-----------|-------------------|
| 1.0.4     | - Prevented PlayerBase protection from being missed while nearby zones and pieces are still loading.<br>- Added a Compendium page showing the server-synced PlayerBase prefab list with localized in-game names and effective protection requirements by biome. |
| 1.0.3     | - Improved terrain target reservation and reset reliability, and replaced the redundant version handshake with ServerSync validation.<br>- Made Expand World Data protected-area refreshes atomic and avoided unchanged full-list syncs.<br>- Added modified terrain cell-index caching and explicit visual Material cleanup for lower long-session overhead. |
| 1.0.2     | - Added synced protected terrain areas for Expand World Data blueprint locations so clients preserve EWD terrain on dedicated servers.<br>- Added protected area bucket caching and shared terrain cell scan helpers to reduce repeated lookup work.<br>- Removed debug logging and the debug config option for leaner runtime behavior. |
| 1.0.1     | - Added `maxDeformationSpawnChanceBonus` so heavily raised or dug terrain can increase TerrainMistile spawn chance.<br>- Documented the new deformation-based spawn chance setting. |
| 1.0.0     | - Initial release. |
