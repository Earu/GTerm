# GTerm
Garry's Mod external console software.
<img width="1113" height="1025" alt="WindowsTerminal_2025-12-21_19-19-37" src="https://github.com/user-attachments/assets/19e2ad2f-274b-4ce6-84a5-ac8cf027fc22" />
<img width="1113" height="1025" alt="WindowsTerminal_2025-12-21_19-19-21" src="https://github.com/user-attachments/assets/b2ec6827-ef3b-49f1-9b7f-fbea110139cc" />
<img width="1113" height="1025" alt="WindowsTerminal_2025-12-21_19-19-31" src="https://github.com/user-attachments/assets/d897eda9-5639-4564-8e94-b8c08d0faf34" />

## Client Installation
- Download the latest [release](https://github.com/Earu/GTerm/releases).
- Launch the gterm executable whenever Garry's Mod is running.
- Restart Garry's Mod to complete the installation.
- Enjoy!

## Server Installation (steamcmd)
- Download the latest [release of `gterm`](https://github.com/Earu/gmsv_xconsole_x64/releases).
- Move the `.dll` (even on macos/linux!) under `srcds/garrysmod/lua/bin` (if the `bin` folder doesnt exist, create it).
- In `srcds/garrysmod/lua/includes/init.lua` add at the top of the file `require("gterm")`.
- Restart the server.
- Launch the gterm executable.
- Enjoy!

*IMPORTANT NOTE: GTerm communicates with the gmod module over a localhost TCP socket (`127.0.0.1:27514`), so GTerm must run on the same host as the server. If you run your server inside a docker container or any other sandbox, run GTerm inside that same sandbox (or otherwise share the loopback interface).*

## In case GTerm does NOT detect your Garry's Mod CLIENT installation
- Download the latest [release of `gterm`](https://github.com/Earu/gmsv_xconsole_x64/releases).
- Move the `.dll` (even on macos/linux!) under `GarrysMod/garrysmod/lua/bin` (if the `bin` folder doesnt exist, create it).
- In `GarrysMod/garrysmod/lua/menu/menu.lua` add at the bottom of the file `require("gterm")`.
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
GTerm includes an MCP (Model Context Protocol) server for AI agents such as Cursor, Vscode, Zed, Claude Code, etc..

**Setup:**
1. Enable MCP in `Config.json`: `"MCP": true`
2. (Optional) Set `"MCPSecret"` for authentication
3. Configure your MCP client to connect to `http://localhost:27513` (add `?secret=...` if using authentication)

**Available Tools:**
- `get_game_status` - Report whether GMod is connected, in a session, and which Lua realms can run code right now (map, gamemode, players). Works even while GMod is closed. Agents should call this first.
- `run_gmod_command` - Execute a console command and capture its output
- `execute_lua_code` - Execute Lua in a **required** realm (`server` or `client`).
- `validate_lua_syntax` - Compile-check Lua with the game's own `CompileString` without executing it
- `check_game_file` - Ask the running game whether a path exists in its virtual filesystem (mounted addons/GMAs included), not just on disk
- `capture_console_output` - Monitor console output for a duration
- `list_gmod_directory` - Browse the Garry's Mod file structure **on disk**
- `read_gmod_file` - Read a text file from the installation **on disk**
- `read_game_file` - Read a file's contents from the **running game's** virtual filesystem (mounted addons, Workshop GMAs), which the on-disk reader can't see
- `take_screenshot_region` - Capture one rectangle of the screen and return it enlarged, so a HUD element, viewmodel or panel is actually legible. Preferred over the full-screen shot for anything specific
- `take_screenshot` - Capture the whole screen. Last resort: prove things with Lua state and arithmetic first
- `read_gmod_wiki` - Fetch a page from the Garry's Mod wiki (wiki.facepunch.com/gmod) to check a function's real signature before using it

Every tool result is prefixed with a `[GTerm]` status line so the agent always knows the connection and realm state. When a precondition is not met (disconnected, wrong realm, no session), the tool returns an error explaining what to do rather than failing silently; pass `force: true` to attempt the call anyway.

**Seeing what an agent is doing.** MCP clients show the tool name but usually not the arguments, so GTerm surfaces them itself: every tool call prints a magenta `[agent]` line in GTerm's console *before* it runs. Lua is shown in full on its own lines with Monokai syntax highlighting, and console commands are highlighted inline.

Both screenshot tools capture through Lua's `render.Capture`, so they need `sv_allowcslua 1` and a reachable client realm. That is deliberate: the engine's `jpeg` command works without Lua, but makes Steam save a full-resolution copy plus a thumbnail into your screenshot library on *every* call, and can only ever grab the whole screen.

**Configuration Options:**
```json
{
  "MCP": true,
  "MCPPort": 27513,
  "MCPCollectionWindowMs": 1000,
  "MCPSecret": "your_secret_here"
}
```

**MCP Client Example (with secret):**
```json
{
  "mcpServers": {
    "gterm": {
      "url": "http://localhost:27513?secret=your_secret_here"
    }
  }
}
```
<img width="874" height="1305" alt="image" src="https://github.com/user-attachments/assets/962e21db-b6bf-4ef3-b919-d09b32117386" />
<img width="1123" height="1207" alt="image" src="https://github.com/user-attachments/assets/42e953e6-1f37-478d-b024-e1e77e4d48eb" />

