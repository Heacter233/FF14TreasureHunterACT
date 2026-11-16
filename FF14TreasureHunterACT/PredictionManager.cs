// PredictionManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FF14TreasureHunterACT
{
    /// <summary>预测规则（对应 Python 的 PredictionRule）</summary>
    public class PredictionRule
    {
        [JsonProperty("rule_id")] public string RuleId { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
        [JsonProperty("published")] public bool Published { get; set; } = false;
        [JsonProperty("prep_msg")] public string PrepMsg { get; set; } = "00";
        [JsonProperty("prep_type")] public string PrepType { get; set; } = "0839";
        [JsonProperty("prep_keywords")] public string PrepKeywords { get; set; } = "";
        [JsonProperty("timeout")] public int Timeout { get; set; } = 30;
        [JsonProperty("judge_msg")] public string JudgeMsg { get; set; } = "273";
        [JsonProperty("judge_type")] public string JudgeType { get; set; } = "0197";
        [JsonProperty("fail_send")] public bool FailSend { get; set; } = true;
        [JsonProperty("fail_text")] public string FailText { get; set; } = "/e 失败";
        [JsonProperty("fail_codes")] public string FailCodes { get; set; } = "";
        [JsonProperty("success_send")] public bool SuccessSend { get; set; } = true;
        [JsonProperty("success_text")] public string SuccessText { get; set; } = "/e 成功";
        [JsonProperty("success_codes")] public string SuccessCodes { get; set; } = "";
        [JsonProperty("unknown_action")] public string UnknownAction { get; set; } = "ignore";
        [JsonProperty("net_prep_keywords")] public string NetPrepKeywords { get; set; } = "";
        [JsonProperty("net_judge_category")] public string NetJudgeCategory { get; set; } = "";
    }

    /// <summary>
    /// 预测规则管理器（读写 config/predictions.json）
    /// 对应 Python 的 prediction_manager.py
    /// </summary>
    public class PredictionManager
    {
        public string FilePath { get; private set; }
        public List<PredictionRule> Rules { get; private set; } = new List<PredictionRule>();

        private readonly object _lock = new object();

        public PredictionManager(string filePath)
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
                    var arr = root["predictions"] as JArray;
                    Rules.Clear();
                    if (arr != null)
                    {
                        foreach (var item in arr)
                        {
                            try
                            {
                                var rule = item.ToObject<PredictionRule>();
                                if (rule != null) Rules.Add(rule);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Predictions] 单条解析失败: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Predictions] 加载失败: {ex.Message}");
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
                    root["predictions"] = arr;

                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.WriteAllText(FilePath,
                        root.ToString(Formatting.Indented),
                        System.Text.Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Predictions] 保存失败: {ex.Message}");
                }
            }
        }

        // ---------- 增删改查 ----------

        public PredictionRule GetRuleById(string ruleId)
        {
            lock (_lock)
            {
                foreach (var r in Rules)
                    if (r.RuleId == ruleId) return r;
            }
            return null;
        }

        public void AddRule(PredictionRule rule)
        {
            lock (_lock)
            {
                // 重名自动加序号
                string baseName = rule.Name;
                var existing = new HashSet<string>();
                foreach (var r in Rules) existing.Add(r.Name);
                if (existing.Contains(rule.Name))
                {
                    int n = 1;
                    while (existing.Contains($"{baseName}({n})")) n++;
                    rule.Name = $"{baseName}({n})";
                }
                Rules.Add(rule);
            }
            Save();
        }

        public bool UpdateRuleById(string ruleId, PredictionRule updated)
        {
            lock (_lock)
            {
                for (int i = 0; i < Rules.Count; i++)
                {
                    if (Rules[i].RuleId == ruleId)
                    {
                        // 重名检查（排除自身）
                        var others = new HashSet<string>();
                        foreach (var r in Rules) if (r.RuleId != ruleId) others.Add(r.Name);
                        if (others.Contains(updated.Name))
                        {
                            string baseName = updated.Name;
                            int n = 1;
                            while (others.Contains($"{baseName}({n})")) n++;
                            updated.Name = $"{baseName}({n})";
                        }
                        Rules[i] = updated;
                        Save();
                        return true;
                    }
                }
            }
            return false;
        }

        public void DeleteRuleById(string ruleId)
        {
            lock (_lock) { Rules.RemoveAll(r => r.RuleId == ruleId); }
            Save();
        }

        // ---------- 内置范例 ----------

        /// <summary>首次运行时加载内置预测规则（对应 Python load_example_rules）</summary>
        public void LoadExampleRules()
        {
            // ========== 规则迁移：把旧版本的 G18 规则升级到日志可用版本 ==========
            bool migrated = false;
            var g18Old = GetRuleById("sys_g18_xunmeng");
            if (g18Old != null && g18Old.PrepType == "0939")
            {
                g18Old.PrepType = "0839";
                g18Old.PrepKeywords = "潜网巡梦";
                migrated = true;
            }
            if (migrated) Save();

            if (ConfigManager.GetBool("example_predictions_loaded", false))
                return;

            // G18 巡梦金库
            if (GetRuleById("sys_g18_xunmeng") == null)
            {
                Rules.Add(new PredictionRule
                {
                    RuleId = "sys_g18_xunmeng",
                    Name = "G18 巡梦金库",
                    Enabled = true,
                    PrepMsg = "00",
                    PrepType = "0839",
                    PrepKeywords = "潜网巡梦",
                    Timeout = 10,
                    JudgeMsg = "273",
                    JudgeType = "0197",
                    FailSend = true,
                    FailText = "/e 失败<se.11>",
                    FailCodes = "39A0",
                    SuccessSend = true,
                    SuccessText = "/e 成功<se.8>",
                    SuccessCodes = "399c,399d,39a1,399f",
                    UnknownAction = "ignore",
                    NetPrepKeywords = "|2c04|",
                    NetJudgeCategory = "197"
                });
            }

            // 运河开门通用
            if (GetRuleById("sys_kaimen_ty") == null)
            {
                Rules.Add(new PredictionRule
                {
                    RuleId = "sys_kaimen_ty",
                    Name = "运河开门通用",
                    Enabled = true,
                    PrepMsg = "00",
                    PrepType = "0939",
                    PrepKeywords = "获得了宝物库的钥匙",
                    Timeout = 90,
                    JudgeMsg = "273",
                    JudgeType = "019D",
                    FailSend = true,
                    FailText = "/e 失败<se.11>",
                    FailCodes = "10|20",
                    SuccessSend = true,
                    SuccessText = "/e 成功<se.8>",
                    SuccessCodes = "",
                    UnknownAction = "ignore",
                    NetPrepKeywords = "|7ef|",
                    NetJudgeCategory = "19d"
                });
            }

            // 盘子通用
            if (GetRuleById("sys_panzi_ty") == null)
            {
                Rules.Add(new PredictionRule
                {
                    RuleId = "sys_panzi_ty",
                    Name = "盘子通用",
                    Enabled = true,
                    PrepMsg = "00",
                    PrepType = "0939",
                    PrepKeywords = "启动了召唤魔法阵",
                    Timeout = 10,
                    JudgeMsg = "273",
                    JudgeType = "0197",
                    FailSend = true,
                    FailText = "/e 失败<se.11>",
                    FailCodes = "1a79,1a73,1a76",
                    SuccessSend = true,
                    SuccessText = "/e 成功<se.8>",
                    SuccessCodes = "1a74,1a75,1996,1997,1a77,1a78",
                    UnknownAction = "ignore",
                    NetPrepKeywords = "|248c|",
                    NetJudgeCategory = "197"
                });
            }

            Save();
            ConfigManager.Set("example_predictions_loaded", true);
        }

        // ---------- 导入导出 ----------

        public int ImportPredictions(List<PredictionRule> list, bool overwrite)
        {
            int count = 0;
            lock (_lock)
            {
                foreach (var r in list)
                {
                    if (!string.IsNullOrEmpty(r.RuleId))
                    {
                        bool exists = false;
                        foreach (var x in Rules) if (x.RuleId == r.RuleId) { exists = true; break; }
                        if (exists && !overwrite) continue;
                        if (exists) Rules.RemoveAll(x => x.RuleId == r.RuleId);
                    }
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
            return new JObject { ["predictions"] = arr }.ToString(Formatting.None);
        }

        public bool SaveRuleFromJson(JObject ruleObj)
        {
            try
            {
                if (ruleObj == null) return false;
                var rule = ruleObj.ToObject<PredictionRule>();
                if (rule == null) return false;
                if (string.IsNullOrEmpty(rule.RuleId))
                    rule.RuleId = "pred_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                  + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                // 从 JSON 中显式取 published 字段（ToObject 有时会漏）
                if (ruleObj["published"] != null)
                    rule.Published = ruleObj["published"].ToObject<bool>();

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
                System.Diagnostics.Debug.WriteLine($"[Predictions] SaveRuleFromJson 失败: {ex.Message}");
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

        /// <summary>导出已发布的预测规则（剥离 published 字段）</summary>
        public string GetPublishablePredictionsJson()
        {
            var arr = new JArray();
            foreach (var r in Rules)
            {
                if (!r.Published) continue;
                var obj = JObject.FromObject(r);
                obj.Remove("published");
                arr.Add(obj);
            }
            return new JObject { ["predictions"] = arr }.ToString(Formatting.None);
        }
    }
}