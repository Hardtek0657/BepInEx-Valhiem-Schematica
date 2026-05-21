# Valheim Plan Build

Client-side planning mode for Valheim. Use `/planbuild` to toggle virtual placement: hammer placements become visible ghost plans, cost no resources, and do not create real world pieces.

## Commands

- `/planbuild` toggles planning mode.
- `/planbuild on` and `/planbuild off` set the mode explicitly.
- `/planbuild create <name>` starts a new empty named plan when no plan is loaded and turns planning mode on.
- `/planbuild save <name>` sends the current planned pieces to the relay.
- `/planbuild load <name>` requests the named save from the relay.
- `/planbuild remove` removes the planned piece nearest the crosshair hit point.
- `/planbuild clear` clears local planned pieces.
- `/planbuild status` shows current mode, piece count, and relay state.
- `/planbuild reconnect` forces the relay WebSocket to reconnect.

## Relay

The default relay URL is `wss://127.0.0.1:5001/planbuild`, intended for a TLS web-server proxy forwarding to the relay.

`PlanBuildRelay` is a minimal ASP.NET Core WebSocket relay. It listens on port `5001`, stores named save frames under `planbuild-saves`, and treats each world/save-name pair as its own live plan. Clients do not keep local plan save files.

Every realtime `PLACE` and `REMOVE` includes the active save name. The relay updates that named file before broadcasting the change to other clients that already loaded the same save.

Ghost visuals are local-mode gated: relay data can sync while `/planbuild` is off, but ghosts are only instantiated while plan build mode is enabled.
