# GTerm
Garry's Mod external console software.

![screen1](https://cdn.discordapp.com/attachments/296410226742263809/924415240949596240/unknown.png)
![screen2](https://i.imgur.com/N0VEKPM.png)

## Installation
- Download the latest [release](https://github.com/Earu/GTerm/releases)
- Extract `GTerm.zip` wherever you feel is most suited
- Launch GTerm.exe whenever Garry's Mod is running
- Enjoy

## In case GTerm does NOT detect your Garry's Mod installation
- Download both `gmsv_xconsole_win32.dll` & `gmsv_xconsole_win64.dll` (you can find them here: https://github.com/Earu/GTerm/tree/master/Modules).
- Place both dll files in `GarrysMod/garrysmod/lua/bin` (if the `bin` folder doesnt exist, create it).
- Go to `GarrysMod/garrysmod/lua/menu`
- Open `menu.lua`
- At the end of the file add `require("xconsole")`
- Save
- Restart Garry's Mod
- Enjoy.