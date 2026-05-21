# Valheim PlanBuild

A BepInEx client-side planning mod for Valheim. Toggle `/planbuild` to enter a virtual building mode where hammer placements become visible ghost plans — no resources consumed, no real pieces placed.

## Commands

| Command | Description |
|---|---|
| `/planbuild` | Toggle planning mode on/off |
| `/planbuild on` / `off` | Set mode explicitly |
| `/planbuild create <name>` | Start a new empty named plan (enables planning mode) |
| `/planbuild save <name>` | Send current planned pieces to the relay |
| `/planbuild load <name>` | Load a named plan from the relay |
| `/planbuild remove` | Remove the ghost piece nearest the crosshair |
| `/planbuild clear` | Clear all local planned pieces |
| `/planbuild status` | Show mode, piece count, and relay connection state |
| `/planbuild reconnect` | Force the relay WebSocket to reconnect |

## Relay

Plans are shared between players via the **PlanBuildRelay** WebSocket server. See the [`planbuild-relay`](../../tree/planbuild-relay) branch for the relay source and setup instructions.

- Default relay URL: `wss://127.0.0.1:5001/planbuild`
- Relay listens on port `5001` and stores named save frames under `planbuild-saves/`
- Each world/save-name pair is its own live plan
- Ghost visuals are local-mode gated — relay data syncs in the background, but ghosts only appear while planning mode is on

## Building

Open `ValheimPlanBuild.csproj` in your IDE or build with:

```
dotnet build
```
