using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JomooRemote
{
    /// <summary>界面设置：持久化到 %APPDATA%\JomooRemote\settings.json，逐字段容错</summary>
    public sealed class AppSettings
    {
        public const int IdleMin = 5;
        public const int IdleMax = 300;
        public const int IdleStep = 5;
        /// <summary>滑到 300 表示不自动断开</summary>
        public const int IdleNever = 300;

        public const int PollMin = 300;
        public const int PollMax = 2000;
        public const int PollStep = 50;

        public const double OpacityMin = 0.70;
        public const double OpacityMax = 1.00;
        public const double OpacityStep = 0.05;

        public static readonly double[] Scales = { 1.00, 1.15, 1.30, 1.50 };

        public int IdleSeconds = 30;
        public int PollMs = 500;
        /// <summary>auto：扫描首个 JOMOO_SMART；mac：按 MAC 锁定</summary>
        public string DeviceMode = "auto";
        public string TargetMac = "";
        public double Opacity = 1.00;
        public double Scale = 1.00;
        public bool Logging = true;

        public bool IdleEnabled { get { return IdleSeconds < IdleNever; } }

        public AppSettings Clone()
        {
            var c = new AppSettings();
            c.IdleSeconds = IdleSeconds;
            c.PollMs = PollMs;
            c.DeviceMode = DeviceMode;
            c.TargetMac = TargetMac;
            c.Opacity = Opacity;
            c.Scale = Scale;
            c.Logging = Logging;
            return c;
        }

        public static string Dir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JomooRemote");
            }
        }

        public static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

        public static string LogDir { get { return Path.Combine(Dir, "logs"); } }

        public static string LogFile { get { return Path.Combine(LogDir, "jomoo_app.log"); } }

        public int ScaleIndex
        {
            get
            {
                int best = 0;
                for (int i = 1; i < Scales.Length; i++)
                    if (Math.Abs(Scales[i] - Scale) < Math.Abs(Scales[best] - Scale)) best = i;
                return best;
            }
        }

        public void Clamp()
        {
            IdleSeconds = ClampStep(IdleSeconds, IdleMin, IdleMax, IdleStep);
            PollMs = ClampStep(PollMs, PollMin, PollMax, PollStep);

            if (Opacity < OpacityMin) Opacity = OpacityMin;
            if (Opacity > OpacityMax) Opacity = OpacityMax;
            Opacity = Math.Round(Opacity / OpacityStep) * OpacityStep;
            if (Opacity < OpacityMin) Opacity = OpacityMin;
            if (Opacity > OpacityMax) Opacity = OpacityMax;

            Scale = Scales[ScaleIndex];

            if (!string.Equals(DeviceMode, "mac", StringComparison.OrdinalIgnoreCase))
            {
                DeviceMode = "auto";
            }
            else
            {
                DeviceMode = "mac";
                TargetMac = MacText(ParseMac(TargetMac));
            }

            if (TargetMac == null) TargetMac = "";
        }

        static int ClampStep(int v, int min, int max, int step)
        {
            if (v < min) v = min;
            if (v > max) v = max;
            int snapped = (int)(Math.Round((double)v / step) * step);
            if (snapped < min) snapped = min;
            if (snapped > max) snapped = max;
            return snapped;
        }

        // ---------------------------------------------------------- 读写

        sealed class Dto
        {
            [JsonPropertyName("idleDisconnectSeconds")] public int? IdleDisconnectSeconds { get; set; }
            [JsonPropertyName("pollIntervalMs")] public int? PollIntervalMs { get; set; }
            [JsonPropertyName("deviceBindingMode")] public string DeviceBindingMode { get; set; }
            [JsonPropertyName("targetDeviceMac")] public string TargetDeviceMac { get; set; }
            [JsonPropertyName("windowOpacity")] public double? WindowOpacity { get; set; }
            [JsonPropertyName("uiScale")] public double? UiScale { get; set; }
            [JsonPropertyName("logging")] public bool? Logging { get; set; }
        }

        /// <summary>读取设置；文件缺失补默认并写出，越界钳制，解析失败改名备份后重写</summary>
        public static AppSettings Load(out string note)
        {
            note = "";
            var s = new AppSettings();

            try
            {
                var path = FilePath;
                if (!File.Exists(path))
                {
                    s.Clamp();
                    s.Save();
                    note = "首次启动，已生成默认设置";
                    return s;
                }

                Dto dto;
                try
                {
                    dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path));
                }
                catch
                {
                    var broken = path + ".broken.json";
                    try { if (File.Exists(broken)) File.Delete(broken); File.Move(path, broken); } catch { }
                    s.Clamp();
                    s.Save();
                    note = "设置文件解析失败，已备份为 settings.broken.json 并重建";
                    return s;
                }

                if (dto != null)
                {
                    if (dto.IdleDisconnectSeconds.HasValue) s.IdleSeconds = dto.IdleDisconnectSeconds.Value;
                    if (dto.PollIntervalMs.HasValue) s.PollMs = dto.PollIntervalMs.Value;
                    if (!string.IsNullOrEmpty(dto.DeviceBindingMode)) s.DeviceMode = dto.DeviceBindingMode;
                    if (!string.IsNullOrEmpty(dto.TargetDeviceMac)) s.TargetMac = dto.TargetDeviceMac;
                    if (dto.WindowOpacity.HasValue) s.Opacity = dto.WindowOpacity.Value;
                    if (dto.UiScale.HasValue) s.Scale = dto.UiScale.Value;
                    if (dto.Logging.HasValue) s.Logging = dto.Logging.Value;
                }

                s.Clamp();
                return s;
            }
            catch
            {
                s.Clamp();
                note = "设置读取异常，已回退默认值";
                return s;
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var dto = new Dto
                {
                    IdleDisconnectSeconds = IdleSeconds,
                    PollIntervalMs = PollMs,
                    DeviceBindingMode = DeviceMode,
                    TargetDeviceMac = TargetMac,
                    WindowOpacity = Opacity,
                    UiScale = Scale,
                    Logging = Logging
                };
                var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }

        // ---------------------------------------------------------- MAC

        /// <summary>把 "AA:BB:CC:DD:EE:FF" 解析成蓝牙地址，非法返回 0</summary>
        public static ulong ParseMac(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var clean = text.Replace(":", "").Replace("-", "").Replace(" ", "").Trim();
            if (clean.Length != 12) return 0;
            ulong v = 0;
            for (int i = 0; i < 12; i++)
            {
                int c = HexVal(clean[i]);
                if (c < 0) return 0;
                v = (v << 4) | (ulong)(uint)c;
            }
            return v;
        }

        static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        public static string MacText(ulong addr)
        {
            if (addr == 0) return "";
            var sb = new System.Text.StringBuilder(17);
            for (int i = 5; i >= 0; i--)
            {
                int b = (int)((addr >> (8 * i)) & 0xFF);
                if (sb.Length > 0) sb.Append(':');
                sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
