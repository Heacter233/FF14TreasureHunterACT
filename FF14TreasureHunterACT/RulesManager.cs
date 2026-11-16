// RulesManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FF14TreasureHunterACT
{
    /// <summary>触发器条件（用于 two_step 模式）</summary>
    public class TriggerCondition
    {
        [JsonProperty("message_code")] public string MessageCode { get; set; } = "";
        [JsonProperty("type_code")] public string TypeCode { get; set; } = "";
        [JsonProperty("keyword")] public string Keyword { get; set; } = "";
        [JsonProperty("use_regex")] public bool UseRegex { get; set; } = false;
    }

    /// <summary>触发器动作</summary>
    public class RuleAction
    {
        [JsonProperty("action_type")] public string ActionType { get; set; } = "record";
        [JsonProperty("delay_ms")] public int DelayMs { get; set; } = 0;
        [JsonProperty("record_name")] public string RecordName { get; set; } = "";
        [JsonProperty("record_value")] public string RecordValue { get; set; } = "1";
        [JsonProperty("output_text")] public string OutputText { get; set; } = "";
    }

    /// <summary>触发器规则（对应 Python 的 TriggerRule）</summary>
    public class TriggerRule
    {
        [JsonProperty("rule_id")] public string RuleId { get; set; }
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
        [JsonProperty("message_code")] public string MessageCode { get; set; } = "";
        [JsonProperty("type_code")] public string TypeCode { get; set; } = "";
        [JsonProperty("keyword")] public string Keyword { get; set; } = "";
        [JsonProperty("use_regex")] public bool UseRegex { get; set; } = false;
        [JsonProperty("trigger_mode")] public string TriggerMode { get; set; } = "simple";
        [JsonProperty("first_conditions")] public List<TriggerCondition> FirstConditions { get; set; } = new List<TriggerCondition>();
        [JsonProperty("second_conditions")] public List<TriggerCondition> SecondConditions { get; set; } = new List<TriggerCondition>();
        [JsonProperty("timeout")] public double Timeout { get; set; } = 30.0;
        [JsonProperty("actions")] public List<RuleAction> Actions { get; set; } = new List<RuleAction>();
        [JsonProperty("scope")] public string Scope { get; set; } = "global";
        [JsonProperty("map_name")] public string MapName { get; set; } = "";
        [JsonProperty("timeout_text")] public string TimeoutText { get; set; } = "";
        [JsonProperty("timeout_send")] public bool TimeoutSend { get; set; } = false;
        [JsonProperty("category")] public string Category { get; set; } = "general";

        public TriggerRule()
        {
            RuleId = GenerateRuleId();
        }

        public static string GenerateRuleId()
        {
            return "rule_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                   + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }
    }

    /// <summary>
    /// 触发器规则管理器（读写 config/rules.json）
    /// 对应 Python 的 rules_manager.py
    /// </summary>
    public class RulesManager
    {
        public string FilePath { get; private set; }
        public List<TriggerRule> Rules { get; private set; } = new List<TriggerRule>();

        private readonly object _lock = new object();

        public RulesManager(string filePath)
        {
            FilePath = filePath;
            Load();
        }

        private void Load()
        {
            lock (_lock)
            {
                if (!File.Exists(FilePath))
                {
                    Save();
                    return;
                }
                try
                {
                    var json = File.ReadAllText(FilePath, System.Text.Encoding.UTF8);
                    var root = JObject.Parse(json);
                    var arr = root["rules"] as JArray;
                    Rules.Clear();
                    if (arr != null)
                    {
                        foreach (var item in arr)
                        {
                            try
                            {
                                var rule = item.ToObject<TriggerRule>();
                                if (rule == null) continue;
                                if (string.IsNullOrEmpty(rule.RuleId))
                                    rule.RuleId = TriggerRule.GenerateRuleId();
                                // 兼容旧格式：actions 为空时补一个
                                if (rule.Actions == null || rule.Actions.Count == 0)
                                {
                                    rule.Actions = new List<RuleAction>
                                    {
                                        new RuleAction { ActionType = "record", RecordValue = "1" }
                                    };
                                }
                                Rules.Add(rule);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Rules] 单条解析失败: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Rules] 加载失败: {ex.Message}");
                }
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                try
                {
                    var root = new JObject();
                    var arr = new JArray();
                    foreach (var r in Rules)
                        arr.Add(JObject.FromObject(r));
                    root["rules"] = arr;

                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.WriteAllText(FilePath,
                        root.ToString(Formatting.Indented),
                        System.Text.Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Rules] 保存失败: {ex.Message}");
                }
            }
        }

        // ---------- 增删改查 ----------

        public void AddRule(TriggerRule rule)
        {
            lock (_lock) { Rules.Add(rule); }
            Save();
        }

        public void UpdateRule(int index, TriggerRule rule)
        {
            lock (_lock)
            {
                if (index >= 0 && index < Rules.Count)
                    Rules[index] = rule;
            }
            Save();
        }

        public void DeleteRule(int index)
        {
            lock (_lock)
            {
                if (index >= 0 && index < Rules.Count)
                    Rules.RemoveAt(index);
            }
            Save();
        }

        public TriggerRule GetRuleById(string ruleId)
        {
            lock (_lock)
            {
                foreach (var r in Rules)
                    if (r.RuleId == ruleId) return r;
            }
            return null;
        }

        /// <summary>按作用域/地图/启用状态筛选</summary>
        public List<TriggerRule> GetRules(string scope = null, string mapName = null,
                                           bool enabledOnly = false)
        {
            var result = new List<TriggerRule>();
            lock (_lock)
            {
                foreach (var r in Rules)
                {
                    if (!string.IsNullOrEmpty(scope) && r.Scope != scope) continue;
                    if (!string.IsNullOrEmpty(mapName))
                    {
                        if (r.Scope != "map" || r.MapName != mapName) continue;
                    }
                    if (enabledOnly && !r.Enabled) continue;
                    result.Add(r);
                }
            }
            return result;
        }

        public void DeleteRulesByMap(string mapName)
        {
            lock (_lock)
            {
                Rules.RemoveAll(r => r.Scope == "map" && r.MapName == mapName);
            }
            Save();
        }

        public List<TriggerRule> ExportAll()
        {
            lock (_lock) { return new List<TriggerRule>(Rules); }
        }

        /// <summary>导入（同 rule_id 时按 overwrite 决定是否覆盖）</summary>
        public int ImportRules(List<TriggerRule> list, bool overwrite)
        {
            int count = 0;
            lock (_lock)
            {
                foreach (var r in list)
                {
                    if (!string.IsNullOrEmpty(r.RuleId))
                        Rules.RemoveAll(x => x.RuleId == r.RuleId);
                    else if (!overwrite)
                        continue;

                    if (string.IsNullOrEmpty(r.RuleId))
                        r.RuleId = TriggerRule.GenerateRuleId();

                    Rules.Add(r);
                    count++;
                }
            }
            Save();
            return count;
        }
        // ========== 供设置页使用的 JSON 接口 ==========

        public string GetAllAsJson()
        {
            var arr = new JArray();
            foreach (var r in Rules)
                arr.Add(JObject.FromObject(r));
            return new JObject { ["rules"] = arr }.ToString(Formatting.None);
        }

        public bool SaveRuleFromJson(JObject ruleObj)
        {
            try
            {
                if (ruleObj == null) return false;
                var rule = ruleObj.ToObject<TriggerRule>();
                if (rule == null) return false;
                if (string.IsNullOrEmpty(rule.RuleId))
                    rule.RuleId = TriggerRule.GenerateRuleId();

                lock (_lock)
                {
                    Rules.RemoveAll(x => x.RuleId == rule.RuleId);
                    Rules.Add(rule);
                }
                Save();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Rules] SaveRuleFromJson 失败: {ex.Message}");
                return false;
            }
        }

        public bool DeleteRule(string ruleId)
        {
            try
            {
                if (string.IsNullOrEmpty(ruleId)) return false;
                lock (_lock) { Rules.RemoveAll(r => r.RuleId == ruleId); }
                Save();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}