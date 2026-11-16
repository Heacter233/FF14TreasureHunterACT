// ConfigManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FF14TreasureHunterACT
{
    /// <summary>
    /// 配置管理，对应 Python 的 config_manager.py
    /// 使用 JSON 结构，支持深度路径读取与写回
    /// </summary>
    public static class ConfigManager
    {
        public static string UserDataDir { get; private set; }
        public static string DataDir { get; private set; }
        public static string ConfigDir { get; private set; }
        public static string LogDir { get; private set; }
        public static string DatabasePath { get; private set; }
        public static string SettingsPath { get; private set; }
        public static string DefaultsPath { get; private set; }

        private static JObject _config = new JObject();

        public static void Load()
        {
            UserDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FF14TreasureHunter");
            DataDir = Path.Combine(UserDataDir, "data");
            ConfigDir = Path.Combine(UserDataDir, "config");
            LogDir = Path.Combine(UserDataDir, "log");
            DatabasePath = Path.Combine(DataDir, "events.db");
            SettingsPath = Path.Combine(ConfigDir, "settings.json");
            DefaultsPath = Path.Combine(ConfigDir, "defaults.json");

            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(ConfigDir);
            Directory.CreateDirectory(LogDir);

            // 1. 内置默认（代码里，永远存在）
            //    ★ 关键：不再依赖 defaults.json 存在。新装用户没有该文件时，
            //      以前会得到空默认值，导致 detection.map_complete_keyword 为空，
            //      地图完成事件永远匹配不上（"能打宝但统计不到地图数"）。
            JObject defaults = ConfigDefaults.Create();

            // 2. 外部 defaults.json 作为可选覆盖层（兼容旧版程序写的文件）
            if (File.Exists(DefaultsPath))
            {
                try
                {
                    var fileDefaults = JObject.Parse(
                        File.ReadAllText(DefaultsPath, System.Text.Encoding.UTF8));
                    DeepMerge(defaults, fileDefaults);
                }
                catch { }
            }

            // 3. 加载用户配置
            JObject userData = new JObject();
            if (File.Exists(SettingsPath))
            {
                try
                {
                    userData = JObject.Parse(File.ReadAllText(SettingsPath, System.Text.Encoding.UTF8));
                }
                catch { }
            }

            // 4. 深度合并（用户值覆盖默认值）
            _config = defaults;
            DeepMerge(_config, userData);

            // 5. 修复必需字段缺失/为空（老配置或手改坏了的配置）
            int repaired = RepairCriticalFields();

            // 6. 回写（含新补齐的字段），保证文件结构与当前版本一致
            _config["_config_version"] = ConfigDefaults.CurrentVersion;
            Save();

            if (repaired > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Config] 已自动补齐 {repaired} 个缺失的识别配置项");
            }
        }

        /// <summary>
        /// 补齐缺失或为空的必需字段。返回修复数量。
        /// 这些字段为空会导致功能**静默失效**，所以必须自动兜底。
        /// </summary>
        private static int RepairCriticalFields()
        {
            int n = 0;
            try
            {
                var builtin = ConfigDefaults.Create();

                foreach (var path in ConfigDefaults.CriticalStringPaths)
                {
                    var cur = GetToken(path);
                    bool bad = (cur == null || cur.Type == JTokenType.Null ||
                                string.IsNullOrWhiteSpace(cur.ToString()));
                    if (!bad) continue;

                    var def = builtin.SelectToken(path);
                    if (def == null) continue;

                    SetInMemory(path, def.ToString());
                    n++;
                }
            }
            catch { }
            return n;
        }

        /// <summary>
        /// 只改内存中的配置，不写文件（供 Load 内部使用，避免反复 IO）。
        /// </summary>
        private static void SetInMemory(string keyPath, object value)
        {
            string[] keys = keyPath.Split('.');
            JToken current = _config;
            for (int i = 0; i < keys.Length - 1; i++)
            {
                if (current[keys[i]] is JObject next)
                {
                    current = next;
                }
                else
                {
                    JObject newObj = new JObject();
                    current[keys[i]] = newObj;
                    current = newObj;
                }
            }
            string lastKey = keys[keys.Length - 1];
            if (value is string s) current[lastKey] = s;
            else if (value is bool b) current[lastKey] = b;
            else if (value is int i2) current[lastKey] = i2;
            else if (value is double d) current[lastKey] = d;
            else current[lastKey] = value?.ToString() ?? "";
        }

        public static void Save()
        {
            try
            {
                File.WriteAllText(SettingsPath,
                    _config.ToString(Formatting.Indented),
                    System.Text.Encoding.UTF8);
            }
            catch { }
        }

        private static void DeepMerge(JObject baseObj, JObject update)
        {
            foreach (var prop in update.Properties())
            {
                if (baseObj[prop.Name] is JObject baseChild && prop.Value is JObject updateChild)
                {
                    DeepMerge(baseChild, updateChild);
                }
                else
                {
                    baseObj[prop.Name] = prop.Value.DeepClone();
                }
            }
        }

        // ---------- 读取方法 ----------
        public static string GetString(string keyPath, string defaultValue = "")
        {
            var token = GetToken(keyPath);
            return token != null && token.Type != JTokenType.Null ? token.ToString() : defaultValue;
        }

        public static double GetDouble(string keyPath, double defaultValue = 0.0)
        {
            var token = GetToken(keyPath);
            if (token != null && token.Type != JTokenType.Null)
            {
                if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                    return token.Value<double>();
                if (double.TryParse(token.ToString(), out double v)) return v;
            }
            return defaultValue;
        }

        public static int GetInt(string keyPath, int defaultValue = 0)
        {
            var token = GetToken(keyPath);
            if (token != null && token.Type != JTokenType.Null)
            {
                if (token.Type == JTokenType.Integer) return token.Value<int>();
                if (int.TryParse(token.ToString(), out int v)) return v;
            }
            return defaultValue;
        }

        public static bool GetBool(string keyPath, bool defaultValue = false)
        {
            var token = GetToken(keyPath);
            if (token != null && token.Type == JTokenType.Boolean)
                return token.Value<bool>();
            return defaultValue;
        }

        public static List<string> GetStringList(string keyPath)
        {
            var list = new List<string>();
            var token = GetToken(keyPath);
            if (token is JArray arr)
            {
                foreach (var item in arr)
                    list.Add(item.ToString());
            }
            return list;
        }

        // ---------- 写入方法 ----------
        public static void Set(string keyPath, object value)
        {
            string[] keys = keyPath.Split('.');
            JToken current = _config;
            for (int i = 0; i < keys.Length - 1; i++)
            {
                if (current[keys[i]] is JObject next)
                {
                    current = next;
                }
                else
                {
                    JObject newObj = new JObject();
                    current[keys[i]] = newObj;
                    current = newObj;
                }
            }
            string lastKey = keys[keys.Length - 1];

            if (value is string s) current[lastKey] = s;
            else if (value is bool b) current[lastKey] = b;
            else if (value is int i2) current[lastKey] = i2;
            else if (value is double d) current[lastKey] = d;
            else if (value is List<string> list)
            {
                JArray arr = new JArray();
                foreach (var item in list) arr.Add(item);
                current[lastKey] = arr;
            }
            else if (value is JToken jt) current[lastKey] = jt;
            else current[lastKey] = value?.ToString() ?? "";

            Save();
        }

        private static JToken GetToken(string keyPath)
        {
            string[] keys = keyPath.Split('.');
            JToken current = _config;
            foreach (var k in keys)
            {
                if (current == null) return null;
                current = current[k];
            }
            return current;
        }

        // ========== 供设置页使用的接口 ==========

        /// <summary>返回全部配置（脱敏：不返回任何 API Key 之类，因为本机访问）</summary>
        public static JObject GetAllAsJObject()
        {
            return (JObject)_config.DeepClone();
        }

        /// <summary>批量设置：items 是 [{path, value}, ...]</summary>
        public static void BatchSet(JArray items)
        {
            if (items == null) return;
            foreach (var item in items)
            {
                try
                {
                    string path = item["path"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(path)) continue;

                    JToken val = item["value"];
                    if (val == null) continue;

                    // 按类型转换
                    object value;
                    if (val.Type == JTokenType.Integer) value = val.Value<long>();
                    else if (val.Type == JTokenType.Float) value = val.Value<double>();
                    else if (val.Type == JTokenType.Boolean) value = val.Value<bool>();
                    else if (val.Type == JTokenType.Array)
                    {
                        var list = new List<string>();
                        foreach (var x in val) list.Add(x.ToString());
                        value = list;
                    }
                    else value = val.ToString();

                    Set(path, value);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Config] BatchSet 失败: {ex.Message}");
                }
            }
        }

        // ---------- 便捷属性 ----------
        public static string LogDirectory => GetString("act.log_directory", "");
        public static double SalaryPerMap => GetDouble("salary.per_map", 0);
        public static double MapCost => GetDouble("salary.map_cost", 0);
        public static double ExtraCost => GetDouble("salary.extra_cost", 0);
        public static int WorkerAdjust => GetInt("salary.worker_adjust", 0);
        public static bool MoneyUnitWan => GetBool("money_unit_wan", true);
    }
}