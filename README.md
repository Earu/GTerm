# GTerm
Garry's Mod external console software.

![screen2](https://i.imgur.com/N0VEKPM.png)

## Client Installation
- Download the latest [release](https://github.com/Earu/GTerm/releases).
- Launch the gterm executable whenever Garry's Mod is running.
- Restart Garry's Mod to complete the installation.
- Enjoy!

## Server Installation (steamcmd)
- Download the latest [release of `xconsole`](https://github.com/Earu/gmsv_xconsole_x64/releases).
- Move the `.dll` (even on macos/linux!) under `srcds/garrysmod/lua/bin` (if the `bin` folder doesnt exist, create it).
- In `srcds/garrysmod/lua/includes/init.lua` add at the top of the file `require("xconsole")`.
- Restart the server.
- Launch the gterm executable.
- Enjoy!

*IMPORTANT NOTE: If you run your server inside a docker container or any other sandbox you will need to install gterm within that sandbox or give the container the rights to write to `/tmp` on UNIX systems.*

## In case GTerm does NOT detect your Garry's Mod CLIENT installation
- Download the latest [release of `xconsole`](https://github.com/Earu/gmsv_xconsole_x64/releases).
- Move the `.dll` (even on macos/linux!) under `GarrysMod/garrysmod/lua/bin` (if the `bin` folder doesnt exist, create it).
- In `GarrysMod/garrysmod/lua/menu/menu.lua` add at the bottom of the file `require("xconsole")`.
- Restart Garry's Mod.
- Voila!

## WebSocket API
GTerm includes a WebSocket server for console streaming and command execution.

**Setup:**
1. Enable API in `Config.json`: `"API": true`
2. Connect WebSocket clients to `ws://localhost:27512/ws/`

**Configuration Options:**
```json
{
  "API": true,
  "APIPort": 27512,
  "APISecret": "your_secret_here"
}
```

**Example Payloads:**

Sending commands (text message):
```
status
```

Receiving console output (JSON):
```json
{
  "Time": 1704123456,
  "Data": [
    {
      "Color": { "R": 255, "G": 255, "B": 255, "A": 255 },
      "Text": "hostname: My Server\n"
    }
  ]
}
```

## MCP Server Integration
GTerm includes an MCP (Model Context Protocol) server for AI agents such as Cursor, Warp, etc..

**Setup:**
1. Enable MCP in `Config.json`: `"MCP": true`
2. Configure your MCP client (e.g., Cursor) to connect to `http://localhost:27513`

**Available Tools:**
- `run_gmod_command` - Execute console commands
- `list_gmod_directory` - Browse Garry's Mod file structure
- `read_gmod_file` - Read text files from installation
- `execute_lua_code` - Execute CLIENT-SIDE Lua code (if you have the permissions to do it)
- `capture_console_output` - Monitor console output for a specified duration

**Configuration Options:**
```json
{
  "MCP": true,
  "MCPPort": 27513,
  "MCPCollectionWindowMs": 1000
}
```

