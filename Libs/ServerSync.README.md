# Pinned ServerSync for Valheim 1.0.7

`ServerSync.dll` is the local compatibility build `valheim-1.0.7-r1`, internalized
into TerrainMistile by ILRepack. It is not a separate BepInEx plugin or an
upstream release. Assembly version: 1.0.0.0; file version: 1.0.0.1.

- Upstream: https://github.com/blaxxun-boop/ServerSync
- Upstream commit: `c57c2aa54e07cdcc7630d6068699ea781622323e`
- License: MIT-0, see `ServerSync.LICENSE.txt`.
- DLL SHA-256: `b4dd786997f4e90d770f09ef3e9d64154754fe7e8edfb4841795751895b35846`
- Replaces SHA-256: `166956302a294e224474b26f4c7d58409084ad3f48bd0af1feb7551f229c8f60`
- Reproduction source, patch, original input hashes and build verification:
  `C:\Users\blizz\.codex\references\valheim\integrations\serversync\versions\valheim-1.0.7-r1`.

Changes: compile against original 1.0.7 game DLLs (Everybody is now a constant),
use public ZNet.IsAdmin for authorization, and buffer the new initial player,
historical player, admin and time messages until RPC handlers are registered.
Config keys, serialization and version checks are preserved. This pin's isolated
tests do not constitute client/host/dedicated-server in-game validation.
