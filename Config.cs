using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace GTerm
{
    internal struct JsonConfig
    {
        public string[] ExclusionPatterns { get; set; }
        public bool ArchiveLogs { get; set; }
        public bool MonitorGmod { get; set; }
        public bool StartAsGmod { get; set; }
    }

    internal class Config
    {
        internal List<Regex> ExclusionPatterns { get; set; } = new List<Regex>();
        internal bool ArchiveLogs { get; set; } = true;
        internal bool MonitorGmod { get; set; } = true;
        internal bool StartAsGmod { get; set; } = false;

        internal Config(string appPath) 
        {
            string configPath = Path.Combine(appPath, "Config.json");
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                JsonConfig cfg = JsonConvert.DeserializeObject<JsonConfig>(json);
                this.ArchiveLogs = cfg.ArchiveLogs;
                this.MonitorGmod = cfg.MonitorGmod;
                this.StartAsGmod = cfg.StartAsGmod;

                foreach (string pattern in cfg.ExclusionPatterns)
                {
                    this.ExclusionPatterns.Add(new Regex(pattern, RegexOptions.Compiled));
                }
            }
        }
    }
}
