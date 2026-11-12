using System;
using System.IO;
using System.Text.Json;

namespace UnzipProxy.Core
{
    public class UserSettings
    {
        public bool RecycleOnSuccess { get; set; } = false;
        public string InvalidCharsAction { get; set; } = "ask"; // "ask", "batch_rename", "skip"
        public bool RememberInvalidCharsAction { get; set; } = false;
        public bool IsDetailsExpanded { get; set; } = false; // 记忆详细信息展开状态，默认折叠
    }

    public static class ConfigManager
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnzipProxy"
        );
        private static readonly string ConfigPath = Path.Combine(ConfigDir, "settings.json");

        private static UserSettings? _cachedSettings;

        public static UserSettings Load()
        {
            if (_cachedSettings != null) return _cachedSettings;

            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    _cachedSettings = JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
                    return _cachedSettings;
                }
            }
            catch
            {
                // 出错时使用默认值
            }

            _cachedSettings = new UserSettings();
            return _cachedSettings;
        }

        public static void Save(UserSettings settings)
        {
            _cachedSettings = settings;
            try
            {
                if (!Directory.Exists(ConfigDir))
                {
                    Directory.CreateDirectory(ConfigDir);
                }
                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch
            {
                // 忽略写入失败
            }
        }
    }
}
