PROJECT_GENERATOR_VERSION = 3

newoption({
	trigger = "gmcommon",
	description = "Sets the path to the garrysmod_common (https://github.com/danielga/garrysmod_common) directory",
	value = "path to garrysmod_common directory"
})

include(assert(_OPTIONS.gmcommon or os.getenv("GARRYSMOD_COMMON"),
	"you didn't provide a path to your garrysmod_common (https://github.com/danielga/garrysmod_common) directory"))

CreateWorkspace({name = "gterm"})
	CreateProject({serverside = true})
		warnings("Default")
		IncludeSDKCommon()
		IncludeSDKTier0()
		IncludeSDKTier1()

		filter("system:linux")
			links({"pthread", "dl"})

		filter("system:windows")
			links({"ws2_32"})
