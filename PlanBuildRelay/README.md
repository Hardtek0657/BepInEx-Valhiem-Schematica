# PlanBuildRelay

A minimal ASP.NET Core WebSocket relay server for Valheim PlanBuild. Receives, persists, and broadcasts plan data between connected clients so players can share ghost builds in real time.

## How It Works

- Clients connect via WebSocket to `/planbuild`
- Each message is a tab-delimited frame prefixed with `PLANBUILD\t2\t`
- The relay groups clients by **world** and **save name** — only clients sharing the same world and loaded save receive broadcasts
- Save state is persisted to disk as `.frame` files under `planbuild-saves/`
- Protocol operations: `HELLO`, `PLACE`, `REMOVE`, `SAVE`, `LOAD`

## Running

```bash
dotnet run
```

The relay listens on **port 5001** on all interfaces (`http://0.0.0.0:5001`).

For production, put it behind a reverse proxy (nginx, Caddy, etc.) with TLS termination. The default client URL is `wss://127.0.0.1:5001/planbuild`.

## Persistence

Save files are stored in `planbuild-saves/` relative to the working directory. File naming:

```
<world_key>__<save_name>.frame
```

World and save names are sanitized — only alphanumeric characters, hyphens, and underscores are preserved.

## Protocol

All frames use the format:

```
PLANBUILD	2	<encodedClientId>	<encodedWorldKey>	<op>	[<encodedSaveName>	<payload...>]
```

String fields are Base64-encoded. The `2` is the protocol version.

### Operations

| Op | Description |
|---|---|
| `HELLO` | Client announces its world (no save context) |
| `PLACE` | Place or update a ghost piece (11 fields: id, prefab, pos x/y/z, rot x/y/z/w, owner, timestamp) |
| `REMOVE` | Remove a ghost piece by id |
| `SAVE` | Overwrite the full save state for the given save name |
| `LOAD` | Request the current state for a save (server replies with `LOAD_DATA`) |
| `LOAD_DATA` | Server response containing all pieces for the requested save |

## See Also

- [Valheim PlanBuild mod](../../tree/main) — the BepInEx client plugin
