// EventProcessor.cs
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FF14TreasureHunterACT
{
    /// <summary>预测等待实例（等待 273 判断行）</summary>
    public class PendingPrediction
    {
        public string RuleId { get; set; }
        public double TriggerTime { get; set; }
    }

    /// <summary>两步规则等待实例（等待第二步条件）</summary>
    public class PendingRuleInstance
    {
        public string RuleName { get; set; }
        public double TriggerTime { get; set; }
    }

    /// <summary>
    /// 事件处理器：解析日志 → 更新状态 → 写数据库 → 触发预测/规则/通知。
    /// 对应 Python 的 event_processor.py
    /// </summary>
    public class EventProcessor
    {
        private readonly Database _db;
        private readonly StateTracker _state;
        private readonly MapConfigManager _mapConfig;
        private readonly RulesManager _rulesManager;
        private readonly PredictionManager _predictionManager;
        private readonly PostNamazuClient _postnamazu;
        private readonly LogParser _parser;
        private readonly Action<string> _logCallback;
        private readonly Action<string> _notifyCallback;

        private string _currentRunLabel;

        // 布尔开关（由 Plugin / MainPanel 修改）
        public volatile bool PredictionEnabled = true;
        public volatile bool AdvancedOutputEnabled = true;
        public volatile bool GlobalActionsEnabled = true;
        public volatile bool MapActionsEnabled = true;

        private readonly List<PendingPrediction> _pendingPredictions = new List<PendingPrediction>();
        private readonly List<PendingRuleInstance> _pendingInstances = new List<PendingRuleInstance>();
        private readonly object _lock = new object();

        private double _lastTimeoutCheck = 0;

        /// <summary>延迟动作调度器：参数 (延迟毫秒, 回调)。由 Plugin 注入。</summary>
        public Action<int, Action> Scheduler { get; set; }
        /// <summary>预测结果回调：(type, text)。type: "success" / "fail"</summary>
        public Action<string, string> OnPredictionResult { get; set; }

        public string CurrentRunLabel => _currentRunLabel;

        public EventProcessor(
            Database db,
            StateTracker state,
            MapConfigManager mapConfig,
            RulesManager rulesManager,
            PredictionManager predictionManager,
            PostNamazuClient postnamazu,
            Action<string> logCallback,
            Action<string> notifyCallback = null)
        {
            _db = db;
            _state = state;
            _mapConfig = mapConfig;
            _rulesManager = rulesManager;
            _predictionManager = predictionManager;
            _postnamazu = postnamazu;
            _parser = new LogParser();
            _logCallback = logCallback;
            _notifyCallback = notifyCallback;
            _currentRunLabel = RunLabelManager.Generate();
        }

        // ============================================================
        // 入口
        // ============================================================

        public void ProcessLine(string line)
        {
            ParsedEvent evt;
            try
            {
                evt = _parser.ParseLine(line);
            }
            catch (Exception ex)
            {
                _logCallback?.Invoke($"[解析异常] {ex.Message}");
                return;
            }
            if (evt == null) return;

            try
            {
                HandleEvent(evt);
            }
            catch (Exception ex)
            {
                _logCallback?.Invoke($"[处理异常] {ex.Message}");
            }
        }

        private void HandleEvent(ParsedEvent evt)
        {
            switch (evt.EventType)
            {
                case "loot": HandleLoot(evt); break;
                case "system_gold": HandleSystemGold(evt); break;
                case "map_change": HandleMapChange(evt); break;
                case "map_event": HandleMapEvent(evt); break;
                case "party_list": HandlePartyList(evt); break;
                case "self_update": HandleSelfUpdate(evt); break;
                case "player_info": HandlePlayerInfo(evt); break;
                case "tell_message":
                case "party_message": HandleChatMessage(evt); break;
                case "net_judge": HandleNetJudge(evt); break;
            }

            // 预测与规则（无论何种事件都跑一遍）
            ProcessPredictions(evt);
            ExecuteRules(evt);
        }

        // ============================================================
        // 各类事件处理
        // ============================================================

        private void HandleLoot(ParsedEvent evt)
        {
            string itemName = evt.Data["item_name"].ToString();
            int quantity = Convert.ToInt32(evt.Data["quantity"]);
            bool isOthers = evt.Data.ContainsKey("is_others") && (bool)evt.Data["is_others"];

            // 他人获得 → 跳过
            if (isOthers) return;

            // ========== 单人 / 组队模式过滤 ==========
            // 组队模式：只处理 0039 —— ROLL 点获得
            // 单人模式：只处理 083E —— 直接进背包
            // 避免"老板"同时记录两条（0039 + 083E）造成重复计数
            string lootType = evt.TypeCode ?? "";
            bool singleMode;

            bool autoMode = ConfigManager.GetBool("detection.auto_single_mode", true);
            if (autoMode)
            {
                // 自动模式：队伍人数 > 1 → 组队模式；≤ 1 → 单人模式
                singleMode = _state.Party.Count <= 1;
            }
            else
            {
                singleMode = ConfigManager.GetBool("detection.single_player_mode", false);
            }

            if (singleMode)
            {
                if (lootType != "083E") return;
            }
            else
            {
                if (lootType != "0039") return;
            }


            var match = _mapConfig.GetItemByName(itemName);
            if (match.Item1 == null || match.Item2 == null) return;
            if (!match.Item1.Enabled) return;

            long? vaultId = _state.VaultSm.CurrentVaultSegmentId;
            long eid = _db.LogEvent(evt.Timestamp, "loot", evt.RawLog, null, _currentRunLabel);
            _db.AddLoot(eid, itemName, quantity, _state.CurrentMap, vaultId);

            _logCallback?.Invoke($"[物品] {itemName} ×{quantity}");
        }

        private void HandleSystemGold(ParsedEvent evt)
        {
            double amount = Convert.ToDouble(evt.Data["amount"]);
            _db.LogEvent(evt.Timestamp, "system_gold", evt.RawLog,
                          amount.ToString(), _currentRunLabel);
            _logCallback?.Invoke($"[系统金] {amount:F0}");
        }

        private void HandleMapChange(ParsedEvent evt)
        {
            string mapName = evt.Data["map_name"].ToString();
            _state.UpdateMap(mapName);
            _logCallback?.Invoke($"[地图] {mapName}");
        }

        private void HandleMapEvent(ParsedEvent evt)
        {
            string content = evt.Data["content"].ToString();

            // 1. 地图完成关键词（多关键词 OR）
            string mapCompleteKw = ConfigManager.GetString("detection.map_complete_keyword", "");
            if (!string.IsNullOrEmpty(mapCompleteKw) && ContainsAny(content, mapCompleteKw))
            {
                _db.LogEvent(evt.Timestamp, "map_complete", evt.RawLog, null, _currentRunLabel);
                _logCallback?.Invoke("[地图完成]");
                return;
            }

            // 2. 魔纹出现关键词
            string magicSealKw = ConfigManager.GetString("detection.magic_seal_keyword", "");
            if (!string.IsNullOrEmpty(magicSealKw) && ContainsAny(content, magicSealKw))
            {
                _db.LogEvent(evt.Timestamp, "magic_seal", evt.RawLog, null, _currentRunLabel);
                _state.VaultSm.OnMagicSeal(evt.Timestamp);
                _logCallback?.Invoke("[魔纹出现]");
                return;
            }

            // 3. 层级关键词（多关键词 OR，跨地图匹配）
            var m = _mapConfig.MatchLayerContent(content);
            if (m.Map != null && m.Layer != null)
            {
                long? vaultId = _state.VaultSm.CurrentVaultSegmentId;
                long eid = _db.LogEvent(evt.Timestamp, "layer", evt.RawLog,
                                         _state.CurrentMap, _currentRunLabel);
                string displayName = string.IsNullOrEmpty(m.Layer.Nickname)
                    ? m.Layer.Keyword : m.Layer.Nickname;
                _db.AddLayer(eid, displayName, vaultId);
                _logCallback?.Invoke($"[层级] {displayName}");
            }
        }

        private void HandlePartyList(ParsedEvent evt)
        {
            int count = Convert.ToInt32(evt.Data["count"]);
            var ids = evt.Data["member_ids"] as List<string>;
            _state.UpdatePartyFromEvent(ids, count);
            _logCallback?.Invoke($"[队伍] {count} 人");
        }

        private void HandleSelfUpdate(ParsedEvent evt)
        {
            string name = evt.Data["player_name"].ToString();
            string eid = evt.Data["entity_id"].ToString();
            _state.UpdateSelfEntity(eid);

            var saved = _db.GetPlayerState();
            string world = (saved != null ? saved.Value.SelfWorld : "") ?? "";
            _state.UpdateSelf(name, world);
        }

        private void HandlePlayerInfo(ParsedEvent evt)
        {
            string eid = evt.Data["entity_id"].ToString();
            string name = evt.Data["name"].ToString();
            string world = evt.Data["world"].ToString();
            _state.UpdatePlayerInfo(eid, name, world);
        }

        private void HandleChatMessage(ParsedEvent evt)
        {
            string content = evt.Data.ContainsKey("content") ? evt.Data["content"].ToString() : "";
            string entity = evt.Data.ContainsKey("entity") ? evt.Data["entity"].ToString() : "";

            _logCallback?.Invoke($"[消息] {entity}: {content}");

            // ---------- 自动分段 ----------
            if (ConfigManager.GetBool("auto_segment.enabled", false))
            {
                string segMsg = ConfigManager.GetString("auto_segment.message_code", "00");
                string segType = ConfigManager.GetString("auto_segment.type_code", "");
                string segKeyword = ConfigManager.GetString("auto_segment.keyword", "");

                bool ok = true;
                if (!string.IsNullOrEmpty(segMsg) && segMsg != evt.MessageCode) ok = false;
                if (ok && !string.IsNullOrEmpty(segType) && segType != evt.TypeCode) ok = false;
                if (ok && !string.IsNullOrEmpty(segKeyword) && !content.Contains(segKeyword)) ok = false;
                if (ok) _state.PendingAutoSegment = true;
            }

            // ---------- 自动发送 ----------
            bool autoTell = ConfigManager.GetBool("auto_send.tell_enabled", false);
            bool autoParty = ConfigManager.GetBool("auto_send.party_enabled", false);
            if (!autoTell && !autoParty) return;

            string tellText = ConfigManager.GetString("auto_send.tell_text", "");
            string partyText = ConfigManager.GetString("auto_send.party_text", "");
            string tellType = GetPureCode(ConfigManager.GetString("auto_send.tell_type", ""));
            string partyType = GetPureCode(ConfigManager.GetString("auto_send.party_type", ""));
            string evType = evt.TypeCode;

            if (autoTell && tellText == content)
            {
                if (string.IsNullOrEmpty(tellType) || tellType == evType)
                    _state.PendingAutoSend = "tell";
            }
            else if (autoParty && partyText == content)
            {
                if (string.IsNullOrEmpty(partyType) || partyType == evType)
                    _state.PendingAutoSend = "party";
            }
        }

        private void HandleNetJudge(ParsedEvent evt)
        {
            // 判断行由 ProcessPredictions 消费；此处只做日志
            _logCallback?.Invoke($"[判断] {evt.Data["category"]}: {evt.Data["params"]}");
        }

        // ============================================================
        // 预测
        // ============================================================

        private void ProcessPredictions(ParsedEvent evt)
        {
            if (_predictionManager == null || !PredictionEnabled) return;

            // ---------- 阶段1：预备（0839 命中关键词） ----------
            if (evt.EventType == "map_event")
            {
                string content = evt.Data["content"].ToString();

                foreach (var rule in _predictionManager.Rules)
                {
                    if (!rule.Enabled) continue;

                    bool alreadyPending;
                    lock (_lock)
                    {
                        alreadyPending = _pendingPredictions.Exists(p => p.RuleId == rule.RuleId);
                    }
                    if (alreadyPending) continue;

                    if (!string.IsNullOrEmpty(rule.PrepMsg) && rule.PrepMsg != evt.MessageCode) continue;
                    if (!string.IsNullOrEmpty(rule.PrepType) && rule.PrepType != evt.TypeCode) continue;
                    if (!ContainsAny(content, rule.PrepKeywords)) continue;

                    lock (_lock)
                    {
                        _pendingPredictions.Add(new PendingPrediction
                        {
                            RuleId = rule.RuleId,
                            TriggerTime = NowSeconds()
                        });
                    }
                    _logCallback?.Invoke($"[预测] 等待判断: {rule.Name}");
                }
            }

            // ---------- 阶段2：判断（273 行） ----------
            if (evt.EventType == "net_judge")
            {
                string category = evt.Data["category"].ToString();
                string paramsStr = evt.Data["params"].ToString();

                List<PendingPrediction> snapshot;
                lock (_lock)
                {
                    snapshot = new List<PendingPrediction>(_pendingPredictions);
                }

                foreach (var pending in snapshot)
                {
                    var rule = _predictionManager.GetRuleById(pending.RuleId);
                    if (rule == null) { RemovePending(pending.RuleId); continue; }

                    if (!string.IsNullOrEmpty(rule.JudgeType) &&
                        !rule.JudgeType.Equals(category, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var failCodes = ParseCodePatterns(rule.FailCodes);
                    var successCodes = ParseCodePatterns(rule.SuccessCodes);
                    string lowerParams = paramsStr.ToLower();

                    bool failed = false;
                    foreach (var fc in failCodes)
                        if (lowerParams.Contains(fc)) { failed = true; break; }

                    if (failed)
                    {
                        ExecutePredictionAction(rule, "fail");
                    }
                    else if (successCodes.Count > 0)
                    {
                        bool matched = false;
                        foreach (var sc in successCodes)
                            if (lowerParams.Contains(sc)) { matched = true; break; }

                        if (matched)
                        {
                            ExecutePredictionAction(rule, "success");
                        }
                        else
                        {
                            // 成功特征码有值但都没匹配 → 按 unknown_action 处理
                            if (rule.UnknownAction == "send_unknown")
                                SendPredictionMessage(rule, "/e 未知");
                            // ignore → 什么都不做
                        }
                    }
                    else
                    {
                        // 成功特征码为空 = "通配"：只要失败没匹配就算成功（Python 语义）
                        ExecutePredictionAction(rule, "success");
                    }

                    RemovePending(pending.RuleId);
                }
            }
        }

        private void ExecutePredictionAction(PredictionRule rule, string result)
        {
            string text = result == "fail" ? rule.FailText : rule.SuccessText;

            if (result == "fail")
            {
                if (rule.FailSend) SendPredictionMessage(rule, rule.FailText);
            }
            else if (result == "success")
            {
                if (rule.SuccessSend) SendPredictionMessage(rule, rule.SuccessText);
            }

            // 推送到悬浮窗
            try
            {
                if (OnPredictionResult != null && !string.IsNullOrEmpty(text))
                    OnPredictionResult(result, text);
            }
            catch { }

            NotificationService.Send("预测结果",
                result == "fail" ? rule.FailText : rule.SuccessText, "prediction");
        }

        private void SendPredictionMessage(PredictionRule rule, string text)
        {
            // 不再用 Connected 当前置条件（会导致一次失败后永久静默丢弃）。
            // 每次尝试发送；真失败了才降级为本地通知。
            bool ok = false;
            if (_postnamazu != null)
            {
                try { ok = _postnamazu.SendCommand(text); }
                catch { ok = false; }
            }

            if (!ok)
                _notifyCallback?.Invoke($"{rule.Name}: {text}（鲶鱼精发送失败：{_postnamazu?.LastError}）");
        }

        private void RemovePending(string ruleId)
        {
            lock (_lock) { _pendingPredictions.RemoveAll(p => p.RuleId == ruleId); }
        }

        /// <summary>由 Plugin 定时调用（建议每 1 秒）。</summary>
        public void CheckPendingTimeouts()
        {
            double now = NowSeconds();
            if (now - _lastTimeoutCheck < 1.0) return;
            _lastTimeoutCheck = now;

            lock (_lock)
            {
                // 预测超时
                var expiredPred = new List<PendingPrediction>();
                foreach (var p in _pendingPredictions)
                {
                    var r = _predictionManager?.GetRuleById(p.RuleId);
                    double t = r?.Timeout ?? 30;
                    if (now - p.TriggerTime > t) expiredPred.Add(p);
                }
                foreach (var p in expiredPred)
                {
                    var r = _predictionManager?.GetRuleById(p.RuleId);
                    if (r != null) _notifyCallback?.Invoke($"预测规则 '{r.Name}' 等待超时");
                    _pendingPredictions.Remove(p);
                }

                // 两步规则超时
                var expiredInst = new List<PendingRuleInstance>();
                foreach (var inst in _pendingInstances)
                {
                    var r = FindRuleByName(inst.RuleName);
                    double t = r?.Timeout ?? 30;
                    if (now - inst.TriggerTime > t) expiredInst.Add(inst);
                }
                foreach (var inst in expiredInst)
                {
                    var r = FindRuleByName(inst.RuleName);
                    // 不检查 Connected：一次失败后永久跳过会让超时提示再也发不出去
                    if (r != null && r.TimeoutSend && _postnamazu != null)
                    {
                        try { _postnamazu.SendCommand("/e " + r.TimeoutText); }
                        catch (Exception ex) { _notifyCallback?.Invoke("[超时提示] 发送失败：" + ex.Message); }
                    }
                    _pendingInstances.Remove(inst);
                }
            }
        }

        // ============================================================
        // 触发器规则
        // ============================================================

        private void ExecuteRules(ParsedEvent evt)
        {
            if (_rulesManager == null) return;

            // 1. 已有等待实例 → 匹配第二步
            List<PendingRuleInstance> toRemove = null;
            lock (_lock)
            {
                foreach (var inst in _pendingInstances)
                {
                    var rule = FindRuleByName(inst.RuleName);
                    if (rule == null)
                    {
                        (toRemove ?? (toRemove = new List<PendingRuleInstance>())).Add(inst);
                        continue;
                    }
                    Match m;
                    if (MatchConditions(evt, rule.SecondConditions, out m))
                    {
                        ExecuteActions(rule, evt, m, 0);
                        (toRemove ?? (toRemove = new List<PendingRuleInstance>())).Add(inst);
                    }
                }
                if (toRemove != null)
                    foreach (var inst in toRemove) _pendingInstances.Remove(inst);
            }

            // 2. 遍历所有启用规则
            foreach (var rule in _rulesManager.GetRules(null, null, true))
            {
                if (rule.Scope == "global" && !GlobalActionsEnabled) continue;
                if (rule.Scope == "map")
                {
                    if (!MapActionsEnabled) continue;
                    var mc = _mapConfig.GetMapByName(rule.MapName);
                    if (mc != null && !mc.Enabled) continue;
                }

                if (rule.TriggerMode == "two_step")
                {
                    bool alreadyPending;
                    lock (_lock)
                    {
                        alreadyPending = _pendingInstances.Exists(x => x.RuleName == rule.Name);
                    }
                    if (alreadyPending) continue;

                    Match m;
                    if (MatchConditions(evt, rule.FirstConditions, out m))
                    {
                        lock (_lock)
                        {
                            _pendingInstances.Add(new PendingRuleInstance
                            {
                                RuleName = rule.Name,
                                TriggerTime = NowSeconds()
                            });
                        }
                    }
                }
                else
                {
                    var cond = new TriggerCondition
                    {
                        MessageCode = rule.MessageCode,
                        TypeCode = rule.TypeCode,
                        Keyword = rule.Keyword,
                        UseRegex = rule.UseRegex
                    };
                    Match m;
                    if (MatchConditions(evt, new List<TriggerCondition> { cond }, out m))
                        ExecuteActions(rule, evt, m, 0);
                }
            }
        }

        private bool MatchConditions(ParsedEvent evt, List<TriggerCondition> conds, out Match match)
        {
            match = null;
            if (conds == null || conds.Count == 0) return false;

            foreach (var c in conds)
            {
                if (!string.IsNullOrEmpty(c.MessageCode) && c.MessageCode != evt.MessageCode)
                    return false;

                if (!string.IsNullOrEmpty(c.TypeCode))
                {
                    if (string.IsNullOrEmpty(evt.TypeCode) ||
                        !c.TypeCode.Equals(evt.TypeCode, StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                if (!string.IsNullOrEmpty(c.Keyword))
                {
                    if (c.UseRegex)
                    {
                        var m = Regex.Match(evt.RawLog, c.Keyword);
                        if (!m.Success) return false;
                        match = m;
                    }
                    else
                    {
                        if (evt.RawLog.IndexOf(c.Keyword, StringComparison.OrdinalIgnoreCase) < 0)
                            return false;
                    }
                }
            }
            return true;
        }

        private void ExecuteActions(TriggerRule rule, ParsedEvent evt, Match match, int index)
        {
            if (index >= rule.Actions.Count) return;

            var action = rule.Actions[index];
            int delay = Math.Max(50, action.DelayMs);

            if (action.ActionType == "record")
            {
                double value = 1;
                if (action.RecordValue == "+")
                {
                    if (match != null && match.Groups.Count > 1)
                        double.TryParse(match.Groups[1].Value, out value);
                }
                else
                {
                    double.TryParse(action.RecordValue, out value);
                }

                long eid = _db.LogEvent(evt.Timestamp, "custom",
                                         "rule: " + rule.Name, rule.MapName, _currentRunLabel);
                string recName = string.IsNullOrEmpty(action.RecordName) ? rule.Name : action.RecordName;
                _db.AddCustomRecord(eid, recName, value);
            }
            else if (action.ActionType == "output")
            {
                string text = action.OutputText ?? "";
                if (!text.StartsWith("/")) text = "/e " + text;
                // 直接尝试发送，不检查 Connected（详见 PostNamazuClient.SendCommand 注释）
                if (_postnamazu != null)
                {
                    try { _postnamazu.SendCommand(text); }
                    catch (Exception ex) { _notifyCallback?.Invoke("[输出] 发送失败：" + ex.Message); }
                }
            }

            if (Scheduler != null)
            {
                Scheduler(delay, () => ExecuteActions(rule, evt, match, index + 1));
            }
            else
            {
                ExecuteActions(rule, evt, match, index + 1);
            }
        }

        private TriggerRule FindRuleByName(string name)
        {
            if (_rulesManager == null) return null;
            foreach (var r in _rulesManager.Rules)
                if (r.Name == name) return r;
            return null;
        }

        // ============================================================
        // 工具
        // ============================================================

        private static bool ContainsAny(string text, string commaSeparated)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(commaSeparated)) return false;
            // 同时支持英文逗号 ',' 和中文逗号 '，'（用户配置里可能两种都用）
            var parts = commaSeparated.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var kw in parts)
            {
                var k = kw.Trim();
                if (!string.IsNullOrEmpty(k) && text.Contains(k)) return true;
            }
            return false;
        }

        /// <summary>
        /// 解析预测规则中的"特征码"字段，兼容两种格式：
        ///   格式 A（多独立码）："1a79,1a73,1a76"  → 拆成 3 个独立字符串
        ///   格式 B（位置序列）："10|20"           → 转成 "10:20" 后作为整体匹配
        /// 返回的每一项均已小写，且把 Python 的 '|' 转成 C# 日志的 ':'。
        /// </summary>
        private static List<string> ParseCodePatterns(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;

            // 按逗号分隔 → 得到多个模式
            var rawParts = s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in rawParts)
            {
                var t = p.Trim();
                if (string.IsNullOrEmpty(t)) continue;

                // 把 Python 的管道符换成 C# 日志分隔符
                string normalized = t.Replace("|", ":").Replace(" ", "").ToLower();
                if (!string.IsNullOrEmpty(normalized))
                    list.Add(normalized);
            }
            return list;
        }
        private static string GetPureCode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int idx = s.IndexOf('/');
            return (idx >= 0 ? s.Substring(0, idx) : s).Trim();
        }

        private static double NowSeconds()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }
    }
}