using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using QuantNative.UI;   // 只为 Theme.DefaultScheme —— 默认配色只有一处定义

namespace QuantNative.Core
{
    /// <summary>
    /// 本地持久化：设置（key=value）与自选列表（TSV）。
    ///
    /// 为什么不用 SQLite：本应用的全部状态就是几十行自选 + 十几个设置项，
    /// 文本文件读取在微秒级，且不需要任何本地依赖（Microsoft.Data.Sqlite 会带
    /// 进 1.5MB 原生库，System.Data.SQLite 同理）。真实收益是包体和启动时间。
    ///
    /// 数据目录：
    ///   - 便携模式（exe 同目录存在 portable.dat）→ &lt;exeDir&gt;\data\
    ///   - 常规模式                                → %APPDATA%\quant-native\
    /// 与旧版（%APPDATA%\quant-desktop\quant-desktop.db，SQLite）互不影响，
    /// 不会破坏旧版数据。
    /// </summary>
    public sealed class Store
    {
        private const string SettingsFile = "settings.txt";
        private const string WatchlistFile = "watchlist.txt";

        private readonly Dictionary<string, string> _settings =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly string _dir;
        private readonly object _gate = new object();

        public string DataDir { get { return _dir; } }
        public bool IsPortable { get; private set; }

        public Store()
        {
            string exePath = null;
            try { exePath = System.Reflection.Assembly.GetEntryAssembly().Location; }
            catch { }
            string exeDir = string.IsNullOrEmpty(exePath) ? null : Path.GetDirectoryName(exePath);

            string portableMarker = exeDir == null ? null : Path.Combine(exeDir, "portable.dat");
            if (portableMarker != null && File.Exists(portableMarker))
            {
                IsPortable = true;
                _dir = Path.Combine(exeDir, "data");
            }
            else
            {
                _dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "quant-native");
            }

            try { Directory.CreateDirectory(_dir); }
            catch (Exception ex) { Log.Warn("创建数据目录失败: " + ex.Message); }

            LoadSettings();
        }

        // ── 设置 ────────────────────────────────────────────────────────────

        private string SettingsPath { get { return Path.Combine(_dir, SettingsFile); } }
        private string WatchlistPath { get { return Path.Combine(_dir, WatchlistFile); } }

        private void LoadSettings()
        {
            lock (_gate)
            {
                _settings.Clear();
                ApplyDefaults();
                try
                {
                    if (!File.Exists(SettingsPath)) return;
                    foreach (string raw in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                    {
                        string line = raw.TrimStart('\uFEFF');
                        if (line.Length == 0 || line[0] == '#') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim();
                        string value = line.Substring(eq + 1);
                        if (key.Length > 0) _settings[key] = value;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("读取设置失败，使用默认值: " + ex.Message);
                }
            }
        }

        private void ApplyDefaults()
        {
            SetDefault("active_datasource", "tencent");
            SetDefault("theme", Theme.DefaultScheme);
            SetDefault("ui_style_rev", "4");
            SetDefault("auto_launch", "false");
            SetDefault("refresh_profile", "fast");
            SetDefault("ticker_visible", "1");
            SetDefault("ticker_opacity", "92");
            SetDefault("ticker_font_size", "11");
            SetDefault("ticker_font_family", "");
            SetDefault("ticker_number_font_family", "");
            SetDefault("ticker_rows", "6");
            SetDefault("ticker_height", "0");
            SetDefault("ticker_interval_ms", "2000");
            SetDefault("ticker_always_on_top", "1");
            SetDefault("ticker_schedule", "0");
            SetDefault("ticker_from", "09:15");
            SetDefault("ticker_to", "23:30");
            SetDefault("overview_visible", "1");
        }

        private void SetDefault(string key, string value)
        {
            if (!_settings.ContainsKey(key)) _settings[key] = value;
        }

        public string Get(string key, string fallback)
        {
            lock (_gate)
            {
                string value;
                return _settings.TryGetValue(key, out value) ? value : fallback;
            }
        }

        public int GetInt(string key, int fallback, int min, int max)
        {
            string raw = Get(key, null);
            int parsed;
            if (string.IsNullOrEmpty(raw)
                || !int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                parsed = fallback;
            if (parsed < min) parsed = min;
            if (parsed > max) parsed = max;
            return parsed;
        }

        public bool GetBool(string key, bool fallback)
        {
            string raw = Get(key, null);
            if (string.IsNullOrEmpty(raw)) return fallback;
            raw = raw.Trim();
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>写入设置并立即落盘（键数量少、文件小，无需防抖）。</summary>
        public void Set(string key, string value)
        {
            lock (_gate)
            {
                _settings[key] = value ?? "";
            }
            SaveSettings();
        }

        public void SetBool(string key, bool value)
        {
            Set(key, value ? "1" : "0");
        }

        /// <summary>删掉一个键（回到「从未设置」的状态，而不是写成空值）。</summary>
        public void Remove(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_gate) { _settings.Remove(key); }
            SaveSettings();
        }

        public void SetInt(string key, int value)
        {
            Set(key, value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>批量写入（一次落盘），避免同时改多个键时重复写文件。</summary>
        public void SetMany(IDictionary<string, string> values)
        {
            lock (_gate)
            {
                foreach (KeyValuePair<string, string> kv in values) _settings[kv.Key] = kv.Value ?? "";
            }
            SaveSettings();
        }

        private void SaveSettings()
        {
            List<string> lines = new List<string>();
            lock (_gate)
            {
                lines.Add("# QuantNative 设置（UTF-8，key=value）");
                lines.Add("# 手工编辑后重启程序生效");
                foreach (KeyValuePair<string, string> kv in _settings)
                    lines.Add(kv.Key + "=" + kv.Value);
            }
            WriteAtomic(SettingsPath, string.Join("\r\n", lines.ToArray()) + "\r\n");
        }

        // ── 自选列表 ────────────────────────────────────────────────────────

        /// <summary>
        /// 一行一只：code \t market \t name \t tickerEnabled \t sortOrder \t addedAt \t source
        /// 用制表符分隔，名称里若含制表符/换行会在写入时替换为空格（股票名不会出现）。
        /// 第 7 列 source 是后加的（场外基金要记住它归天天基金管），
        /// 旧文件只有 6 列时按「跟随当前数据源」处理，读取不会出错。
        /// </summary>
        public List<WatchItem> LoadWatchlist()
        {
            List<WatchItem> items = new List<WatchItem>();
            try
            {
                if (!File.Exists(WatchlistPath)) return items;
                foreach (string raw in File.ReadAllLines(WatchlistPath, Encoding.UTF8))
                {
                    string line = raw.TrimStart('\uFEFF');
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length < 3) continue;

                    WatchItem item = new WatchItem();
                    item.Code = parts[0].Trim();
                    item.Market = parts[1].Trim();
                    item.Name = parts[2].Trim();
                    item.TickerEnabled = parts.Length < 4 || parts[3].Trim() != "0";
                    int order;
                    item.SortOrder = parts.Length >= 5
                        && int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out order)
                        ? order : items.Count;
                    item.AddedAt = parts.Length >= 6 ? parts[5].Trim() : "";
                    item.Source = parts.Length >= 7 ? parts[6].Trim() : "";
                    if (item.Code.Length == 0) continue;
                    item.Id = items.Count + 1;
                    items.Add(item);
                }
                items.Sort(delegate (WatchItem a, WatchItem b)
                {
                    int cmp = a.SortOrder.CompareTo(b.SortOrder);
                    return cmp != 0 ? cmp : a.Id.CompareTo(b.Id);
                });
                Reindex(items);
            }
            catch (Exception ex)
            {
                Log.Warn("读取自选失败: " + ex.Message);
            }
            return items;
        }

        public void SaveWatchlist(IList<WatchItem> items)
        {
            List<string> lines = new List<string>();
            lines.Add("# code\tmarket\tname\tticker\tsort\tadded\tsource");
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    WatchItem item = items[i];
                    lines.Add(string.Join("\t", new string[]
                    {
                        Clean(item.Code),
                        Clean(item.Market),
                        Clean(item.Name),
                        item.TickerEnabled ? "1" : "0",
                        i.ToString(CultureInfo.InvariantCulture),
                        Clean(item.AddedAt),
                        Clean(item.Source)
                    }));
                }
            }
            WriteAtomic(WatchlistPath, string.Join("\r\n", lines.ToArray()) + "\r\n");
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        /// <summary>重排序号，保证顺序稳定（删除/置顶后调用）。</summary>
        public static void Reindex(IList<WatchItem> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                items[i].SortOrder = i;
                items[i].Id = i + 1;
            }
        }

        /// <summary>先写临时文件再替换，避免掉电/崩溃时把配置写坏。</summary>
        private static void WriteAtomic(string path, string content)
        {
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                Log.Warn("写入失败 " + path + ": " + ex.Message);
            }
        }
    }
}
