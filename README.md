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
