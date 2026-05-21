# Valheim Plan Build

Valheim Plan Build is a BepInEx client mod for planning builds before spending materials. When plan build mode is enabled, normal hammer placement creates translucent ghost pieces instead of real world objects. These planned pieces cost no resources, can be removed, saved, loaded, and synced with other players through a WebSocket relay.

## What It Does

- Adds `/planbuild` chat commands for creating, loading, saving, and managing build plans.
- Lets players place ghost versions of furniture, buildings, and other build pieces at no material cost.
- Prevents real resource consumption while planning mode is active.
- Shows ghost pieces only to players who have plan build mode enabled.
- Syncs planned placements/removals between players in the same Valheim world.
- Saves relay state automatically so plans can survive game or relay restarts.
- Separates plans by Valheim world name and world UID.

## Basic Flow

1. A player runs `/planbuild create mybase` or loads an existing plan.
2. The player turns plan build mode on with `/planbuild`.
3. Hammer placements are intercepted before Valheim creates real objects.
4. The mod records the selected prefab, position, rotation, owner, and id.
5. A translucent local ghost object is created.
6. The placement is sent to the relay.
7. Other connected players in the same world receive the update.
8. If their plan build mode is on, they see the ghost appear.
9. The relay writes each placement/removal to the active named save file before broadcasting it.

## Main Commands

- `/planbuild` toggles planning mode.
- `/planbuild create <name>` starts a new empty plan.
- `/planbuild save <name>` sends the current plan to the relay.
- `/planbuild load <name>` requests a named plan from the relay.
- `/planbuild remove` removes the planned ghost nearest the crosshair.
- Middle-click/destroy removes aimed planned ghosts while planning mode is on.
- `/planbuild clear` clears the local plan.
- `/planbuild status` shows mode, plan name, piece count, and relay status.
- `/planbuild reconnect` reconnects to the relay.

## Relay

The relay is a small ASP.NET Core WebSocket server. Clients connect to:

`wss://127.0.0.1:5001/planbuild`

The relay receives placement/removal messages, keeps active plan state per Valheim world and save name, writes each update to the matching named save file, and broadcasts changes only to players who already loaded that same save. Clients do not write local plan save files.

The relay stores:
- piece id
- prefab name
- position coordinates
- rotation/facing quaternion
- owner name
- created timestamp
- world key

This lets players reconnect later and reload the same plan state.
