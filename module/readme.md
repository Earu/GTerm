# gmsv\_gterm
A Garry's Mod module that provides an interface for external consoles (used by [GTerm](https://github.com/Earu/GTerm)).
It exposes the server console over a localhost TCP socket (`127.0.0.1:27514`); GTerm connects to it as a client.
**This fork provides both x86 and x64 compatible versions.**

## Compiling
### For the x86_64 branch:
#### Building the project for linux/macos
1) Get [premake](https://premake.github.io/download/) add it to your `PATH`
2) Get [garrysmod_common](https://github.com/danielga/garrysmod_common) (with `git clone https://github.com/danielga/garrysmod_common --recursive --branch=x86-64-support-sourcesdk`) and set an env var called `GARRYSMOD_COMMON` to the path of the local repo
3) Run `premake5 gmake` in your local copy of **this** repo
4) Navigate to the makefile directory (`cd /projects/linux/gmake` or `cd /projects/macosx/gmake`)
5) Run `make config=releasewithsymbols_x86_64`

#### Building the project on windows
1) Get [premake](https://premake.github.io/download/) add it to your `PATH`
2) Get [garrysmod_common](https://github.com/danielga/garrysmod_common) (with `git clone https://github.com/danielga/garrysmod_common --recursive --branch=x86-64-support-sourcesdk`) and set an env var called `GARRYSMOD_COMMON` to the path of the local repo
3) Run `premake5 vs2019` in your local copy of **this** repo
4) Navigate to the project directory `cd /projects/windows/vs2019`
5) Open the .sln in Visual Studio 2019+
6) Select Release, and either x64 or x86
7) Build


### For the *current (x86)* main branch:
#### Building the project for linux/macos
1) Get [premake](https://premake.github.io/download/) add it to your `PATH`
2) Get [garrysmod_common](https://github.com/danielga/garrysmod_common) (with `git clone https://github.com/danielga/garrysmod_common --recursive`) and set an env var called `GARRYSMOD_COMMON` to the path of the local repo
3) Edit premake5.lua and change `PROJECT_GENERATOR_VERSION` to `2`
4) Run `premake5 gmake` in your local copy of **this** repo
5) Navigate to the makefile directory (`cd /projects/linux/gmake` or `cd /projects/macosx/gmake`)
6) Run `make`

#### Building the project on windows
1) Get [premake](https://premake.github.io/download/) add it to your `PATH`
2) Get [garrysmod_common](https://github.com/danielga/garrysmod_common) (with `git clone https://github.com/danielga/garrysmod_common --recursive`) and set an env var called `GARRYSMOD_COMMON` to the path of the local repo
3) Run `premake5 vs2019` in your local copy of **this** repo
4) Edit premake5.lua and change `PROJECT_GENERATOR_VERSION` to `2`
5) Navigate to the project directory `cd /projects/windows/vs2019`
6) Open the .sln in Visual Studio 2019+
7) Select Release, and either x64 or x86
8) Build
