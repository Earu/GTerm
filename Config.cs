using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using GTerm.Extensions;

namespace GTerm
{
    internal class JsonConfig
    {
        public string[]? ExclusionPatterns { get; set; }
        public bool ArchiveLogs { get; set; }
        public bool MonitorGmod { get; set; }
        public bool StartAsGmod { get; set; }
        public bool? API { get; set; }
        public string? APISecret { get; set; }
        public int? APIPort { get; set; }
        public bool? MCP { get; set; }
        public int? MCPCollectionWindowMs { get; set; }
        public int? MCPPort { get; set; }
        public string? MCPSecret { get; set; }

        /// <summary>Tool packages the user accepted, written by GTerm itself (see MCP/ToolPackages.cs).</summary>
        public ToolPackageConsentEntry[]? ToolPackageConsent { get; set; }
    }

    internal class ToolPackageConsentEntry
    {
        /// <summary>Server IPv4, or "local".</summary>
        public string? Scope { get; set; }
        public string? Package { get; set; }
        public string? Hash { get; set; }
        public DateTime? AcceptedAt { get; set; }
    }

    internal class Config
    {
        internal List<Regex> ExclusionPatterns { get; set; } = [];
        internal bool ArchiveLogs { get; set; } = true;
        internal bool MonitorGmod { get; set; } = true;
        internal bool StartAsGmod { get; set; } = false;
        internal bool API { get; set; } = false;
        internal string? APISecret { get; set; }
        internal int APIPort { get; set; }
        internal bool MCP { get; set; } = false;
        internal int MCPCollectionWindowMs { get; set; } = 1000;
        internal int MCPPort { get; set; } = 27513;
        internal string? MCPSecret { get; set; }

        private static readonly object FileLock = new();

        internal Config() { }

        internal Config(string[] args)
        {
            JsonConfig cfg = new();

            string? configPath = JsonPath();
            if (configPath != null && File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                JsonConfig? extractedCfg = JsonConvert.DeserializeObject<JsonConfig>(json);
                if (extractedCfg != null)
                {
                    cfg = extractedCfg;
                }
            }

            if (args.Length > 0)
            {
                Dictionary<string, List<string>> options = ParseCLIArgs(args);
                ProcessOptions(options, ref cfg);
            }

            this.ProcessConfig(cfg);
        }

        /// <summary>Config.json next to the executable, or null when that cannot be resolved.</summary>
        internal static string? JsonPath()
        {
            string? appPath = Path.GetDirectoryName(Process.GetCurrentProcess().GetExecutablePath());
            return appPath == null ? null : Path.Combine(appPath, "Config.json");
        }

        /// <summary>The raw Config.json as a JObject. Missing or corrupt files read as empty.</summary>
        internal static JObject ReadJson()
        {
            lock (FileLock) return ReadJsonUnlocked();
        }

        /// <summary>
        /// Read-modify-write on Config.json. Only what <paramref name="mutate"/> touches changes;
        /// every other key, known to GTerm or not, survives.
        /// </summary>
        internal static bool UpdateJson(Action<JObject> mutate)
        {
            string? path = JsonPath();
            if (path == null) return false;

            lock (FileLock)
            {
                try
                {
                    JObject json = ReadJsonUnlocked();
                    mutate(json);
                    File.WriteAllText(path, json.ToString(Formatting.Indented));
                    return true;
                }
                catch (Exception ex)
                {
                    LocalLogger.WriteLine($"Could not update {path}: {ex.Message}");
                    return false;
                }
            }
        }

        private static JObject ReadJsonUnlocked()
        {
            string? path = JsonPath();
            if (path == null || !File.Exists(path)) return [];

            try
            {
                return JObject.Parse(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                LocalLogger.WriteLine($"Could not parse {path}: {ex.Message}");
                return [];
            }
        }

        private void ProcessConfig(JsonConfig cfg)
        {
            this.ArchiveLogs = cfg.ArchiveLogs;
            this.MonitorGmod = cfg.MonitorGmod;
            this.StartAsGmod = cfg.StartAsGmod;
            this.API = cfg.API ?? false;
            this.APIPort = cfg.APIPort ?? 27512;
            this.APISecret = cfg.APISecret;
            this.MCP = cfg.MCP ?? false;
            this.MCPCollectionWindowMs = cfg.MCPCollectionWindowMs ?? 1000;
            this.MCPPort = cfg.MCPPort ?? 27513;
            this.MCPSecret = cfg.MCPSecret;

            if (cfg.ExclusionPatterns != null)
            {
                foreach (string pattern in cfg.ExclusionPatterns)
                {
                    this.ExclusionPatterns.Add(new Regex(pattern, RegexOptions.Compiled));
                }
            }

            LocalLogger.WriteLine("Config params:");
            LocalLogger.WriteLine("Logs archiving: " + this.ArchiveLogs);
            LocalLogger.WriteLine("Gmod monitoring: " + this.MonitorGmod);
            LocalLogger.WriteLine("Start as Gmod: " + this.StartAsGmod);
            LocalLogger.WriteLine("MCP: " + this.MCP);
            LocalLogger.WriteLine("MCP Collection Window: " + this.MCPCollectionWindowMs + "ms");
            LocalLogger.WriteLine("MCP Port: " + this.MCPPort);
            LocalLogger.WriteLine("MCP Secret: " + (string.IsNullOrWhiteSpace(this.MCPSecret) ? "(none)" : "***configured***"));
            LocalLogger.WriteLine("Exclusion Patterns: \n", string.Join("\n", this.ExclusionPatterns.Select(r => r.ToString())));
        }

        private static Dictionary<string, List<string>> ParseCLIArgs(string[] args)
        {
            args = args.Select(arg => arg.Trim().ToLower()).ToArray();

            List<string>? knownOptionParams = null;
            Dictionary<string, List<string>> options = [];

            string? curOption = null;
            List<string> curOptionParams = [];
            foreach (string arg in args)
            {
                if (arg.StartsWith("--"))
                {
                    if (curOption != null)
                    {
                        if (options.TryGetValue(curOption, out knownOptionParams))
                        {
                            knownOptionParams.AddRange(curOptionParams);
                            curOptionParams.Clear();
                        }
                        else
                        {
                            options.Add(curOption, curOptionParams);
                            curOptionParams.Clear();
                        }
                    }

                    if (arg.Length > 3)
                        curOption = arg.Substring(2);
                }
                else
                {
                    curOptionParams.Add(arg);
                }
            }

            // for the last option
            if (curOption != null)
            {
                if (options.TryGetValue(curOption, out knownOptionParams))
                    knownOptionParams.AddRange(curOptionParams);
                else
                    options.Add(curOption, curOptionParams);
            }

            return options;
        }

        private static void ProcessOptions(Dictionary<string, List<string>> options, ref JsonConfig curCfg)
        {
            Type baseCfgType = typeof(JsonConfig);
            PropertyInfo[] props = baseCfgType.GetProperties();
            foreach (KeyValuePair<string, List<string>> option in options)
            {
                PropertyInfo? prop = props.FirstOrDefault(p => p.Name.Equals(option.Key, StringComparison.CurrentCultureIgnoreCase));
                if (prop == null) continue;

                switch (prop.PropertyType)
                {
                    case Type t when t == typeof(bool):
                        bool value = true;
                        if (option.Value.Count > 0 && int.TryParse(string.Join(' ', option.Value), out int parsedValue))
                            value = parsedValue > 0;

                        prop.SetValue(curCfg, value);
                        break;

                    case Type t when t == typeof(string[]):
                        prop.SetValue(curCfg, option.Value.ToArray());
                        break;

                    case Type t when t == typeof(int):
                        int number = 0;
                        if (option.Value.Count > 0 && int.TryParse(string.Join(' ', option.Value), out int parsedNumber))
                            number = parsedNumber;
                        prop.SetValue(curCfg, number);
                        break;

                    case Type t when t == typeof(string):
                        prop.SetValue(curCfg, string.Join(' ', option.Value));
                        break;

                    default:
                        break;
                }
            }
        }
    }
}
