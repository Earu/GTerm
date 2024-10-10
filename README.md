# GTerm
Garry's Mod external console software.

![screen2](https://i.imgur.com/N0VEKPM.png)

## Installation
- Download the latest [release](https://github.com/Earu/GTerm/releases)
- Launch gterm.exe (or gterm64.exe) whenever Garry's Mod is running
- Restart Garry's Mod to complete the installation
- Enjoy

## In case GTerm does NOT detect your Garry's Mod installation
**IF YOU RUN GTERM ON A SERVER THIS IS YOUR CASE!!**

- Download both `gmsv_xconsole_win32.dll` & `gmsv_xconsole_win64.dll` (you can find them here: https://github.com/Earu/GTerm/tree/master/Modules).
- Place both dll files in `GarrysMod/garrysmod/lua/bin` (if the `bin` folder doesnt exist, create it).
- Go to `GarrysMod/garrysmod/lua/menu`
- Open `menu.lua`
- At the end of the file add `require("xconsole")`
- Save
- Restart Garry's Mod
- Voila!
