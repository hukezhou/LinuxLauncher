using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using vllmLauncher.Models;

namespace vllmLauncher.Services
{
    public class ConfigService
    {
        private readonly string _configPath;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public ConfigService()
        {
            _configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        }

        public AppSettings Load()
        {
            if (!File.Exists(_configPath))
                return new AppSettings();

            try
            {
                var json = File.ReadAllText(_configPath);
                var settings = JsonSerializer.Deserialize<RawConfig>(json, JsonOptions);
                if (settings == null) return new AppSettings();

                var result = new AppSettings
                {
                    ServerAddress = settings.ServerAddress,
                    UseScreen = settings.UseScreen,
                    PasteWaitTime = settings.PasteWaitTime
                };

                if (settings.Models != null)
                {
                    foreach (var m in settings.Models)
                    {
                        // Support both new format (fullText) and old format (startCommand + environmentVariables)
                        string fullText = m.FullText ?? m.StartCommand ?? string.Empty;

                        // If using old format, try to reconstruct full text
                        if (string.IsNullOrEmpty(fullText) && m.EnvironmentVariables != null)
                        {
                            var parts = new List<string>();
                            foreach (var env in m.EnvironmentVariables)
                            {
                                if (!string.IsNullOrEmpty(env.Key))
                                    parts.Add($"export {env.Key}={env.Value ?? ""}");
                            }
                            if (!string.IsNullOrEmpty(m.StartCommand))
                                parts.Add(m.StartCommand);
                            fullText = string.Join("\n", parts);
                        }

                        // Command offsets are computed against '\n'-normalized text; normalize on load
                        fullText = fullText.Replace("\r\n", "\n").Replace("\r", "\n");

                        var modelConfig = new ModelConfig(m.Name ?? string.Empty, fullText);

                        // Command link markers (new format); validate against fullText bounds
                        if (m.Commands != null)
                        {
                            foreach (var c in m.Commands)
                            {
                                if (c != null && c.Start >= 0 && c.Length > 0 && c.Start + c.Length <= fullText.Length)
                                    modelConfig.Commands.Add(c);
                            }
                        }

                        // Rolled-up groups: the hidden lines live inside the record, so the
                        // record must be internally consistent and its group must still have
                        // a marker; otherwise the group is simply shown expanded.
                        if (m.CollapsedGroups != null)
                        {
                            foreach (var cg in m.CollapsedGroups)
                            {
                                if (cg == null || !cg.IsConsistent()) continue;
                                if (!modelConfig.Commands.Any(c => c.GroupId == cg.GroupId)) continue;
                                modelConfig.CollapsedGroups.Add(cg);
                            }
                        }

                        result.Models.Add(modelConfig);
                    }
                }

                return result;
            }
            catch
            {
                return new AppSettings();
            }
        }

        public void Backup()
        {
            if (!File.Exists(_configPath))
                throw new FileNotFoundException("未找到 config.json 文件", _configPath);

            string bakPath = _configPath + ".bak";
            File.Copy(_configPath, bakPath, true);
        }

        public void Save(AppSettings settings)
        {
            var raw = new RawConfig
            {
                ServerAddress = settings.ServerAddress,
                UseScreen = settings.UseScreen,
                PasteWaitTime = settings.PasteWaitTime,
                Models = new List<RawModelConfig>()
            };

            foreach (var model in settings.Models)
            {
                raw.Models.Add(new RawModelConfig
                {
                    Name = model.Name,
                    FullText = model.FullText,
                    Commands = model.Commands.Count > 0 ? model.Commands : null,
                    CollapsedGroups = model.CollapsedGroups.Count > 0 ? model.CollapsedGroups : null
                });
            }

            var json = JsonSerializer.Serialize(raw, JsonOptions);
            File.WriteAllText(_configPath, json);
        }
    }

    public class RawConfig
    {
        public string ServerAddress { get; set; } = string.Empty;
        public bool UseScreen { get; set; } = false;
        public double PasteWaitTime { get; set; } = 3.0;
        public List<RawModelConfig>? Models { get; set; }
    }

    public class RawModelConfig
    {
        public string? Name { get; set; }
        public string? FullText { get; set; }
        public List<CommandMarker>? Commands { get; set; }
        public List<CollapsedGroup>? CollapsedGroups { get; set; }

        // Old format support for migration
        [Obsolete]
        public string? StartCommand { get; set; }
        [Obsolete]
        public List<RawEnvVar>? EnvironmentVariables { get; set; }
    }

    [Obsolete]
    public class RawEnvVar
    {
        public string? Key { get; set; }
        public string? Value { get; set; }
    }
}
