namespace NotADoctor99.LibreHardwareMonitorPlugin
{
    using System;
    using System.IO;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// User-editable settings loaded from / saved to:
    ///   %APPDATA%\Logi\LibreHardwareMonitorPlugin\settings.json
    ///
    /// To change the network max speed, edit that file and restart the plugin service.
    /// </summary>
    public sealed class PluginSettings
    {
        private static readonly String SettingsDir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Logi", "LibreHardwareMonitorPlugin");

        private static readonly String SettingsPath = Path.Combine(SettingsDir, "settings.json");

        private static PluginSettings _current;

        public static PluginSettings Current
        {
            get
            {
                if (_current == null)
                {
                    _current = Load();
                }
                return _current;
            }
        }

        /// <summary>
        /// Maximum network speed in MB/s used to scale the ↓/↑ throughput bars.
        /// Default 100 = 100 MB/s (typical ~1 Gbps fiber).
        /// Set to 12.5 for 100 Mbps, 1250 for 10 Gbps, etc.
        /// </summary>
        [JsonPropertyName("networkMaxSpeedMBps")]
        public Single NetworkMaxSpeedMBps { get; set; } = 100f;

        /// <summary>Derived: max in KB/s (used internally).</summary>
        [JsonIgnore]
        public Single NetworkMaxSpeedKBps => this.NetworkMaxSpeedMBps * 1024f;

        private static PluginSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var settings = JsonSerializer.Deserialize<PluginSettings>(json);
                    if (settings != null)
                    {
                        PluginLog.Info($"PluginSettings loaded from {SettingsPath}: networkMaxSpeedMBps={settings.NetworkMaxSpeedMBps}");
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Could not load settings from {SettingsPath}, using defaults");
            }

            var defaults = new PluginSettings();
            SaveDefaults(defaults);
            return defaults;
        }

        private static void SaveDefaults(PluginSettings settings)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, options));
                PluginLog.Info($"PluginSettings defaults written to {SettingsPath}");
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Could not write default settings to {SettingsPath}");
            }
        }
    }
}
