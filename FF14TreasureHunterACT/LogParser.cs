// LogParser.cs
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FF14TreasureHunterACT
{
    public class ParsedEvent
    {
        public string MessageCode { get; set; }
        public string TypeCode { get; set; }
        public string Timestamp { get; set; }
        public string RawLog { get; set; }
        public string EventType { get; set; }
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
    }

    public class LogParser
    {
        // [HH:MM:SS.fff] <LogType> <payload>
        private static readonly Regex LineRegex = new Regex(
            @"^\[(\d{2}:\d{2}:\d{2}\.\d{3})\]\s+(\S+)\s+(.*)$",
            RegexOptions.Compiled);

        // 带弯引号的物品名：“xxx”或「xxx」
        private static readonly Regex QuotedLootRegex = new Regex(
            "获得了[\u201C\u201D\u300C\u300D](.+?)[\u201C\u201D\u300C\u300D](?:×\\s*(\\d+))?",
            RegexOptions.Compiled);

        // 系统金：获得了3,430金币。
        private static readonly Regex GoldRegex = new Regex(
            @"获得了([\d,]+)金币", RegexOptions.Compiled);

        public ParsedEvent ParseLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            var m = LineRegex.Match(line);
            if (!m.Success) return null;

            string timeOnly = m.Groups[1].Value;
            string logType = m.Groups[2].Value;
            string payload = m.Groups[3].Value.Trim();

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd") + "T" + timeOnly;

            switch (logType)
            {
                case "ChatLog": return ParseChatLog(payload, timestamp, line);
                case "ChangeMap": return ParseChangeMap(payload, timestamp, line);
                case "ChangePrimaryPlayer": return ParseChangePrimaryPlayer(payload, timestamp, line);
                case "AddCombatant": return ParseAddCombatant(payload, timestamp, line);
                case "PartyList": return ParsePartyList(payload, timestamp, line);
                case "273": return ParseJudge273(payload, timestamp, line);
                default:
                    return null;  // 261/263/264/14/15 等忽略
            }
        }

        // ---------- ChatLog ----------
        private ParsedEvent ParseChatLog(string payload, string ts, string raw)
        {
            // payload: msgCode:typeCode:entity:content
            // 注意 entity 可能为空，且 content 中可能含 ':'，所以 Split 限 4 段
            var parts = payload.Split(new[] { ':' }, 4);
            if (parts.Length < 3) return null;

            string msgCode = parts[0];
            string typeCode = parts[1];
            string entity = parts.Length > 2 ? parts[2] : "";
            string content = parts.Length > 3 ? parts[3] : "";

            switch (typeCode)
            {
                case "0039":
                case "083E":
                case "203E":
                    return ParseLoot(msgCode, typeCode, entity, content, ts, raw);
                case "003E":
                    return ParseSystemGold(msgCode, typeCode, content, ts, raw);
                case "0839":
                    return ParseMapEvent(msgCode, typeCode, content, ts, raw);
                case "000E":
                    return ParsePartyMessage(msgCode, typeCode, entity, content, ts, raw);
                case "0038":
                    return ParseTellMessage(msgCode, typeCode, entity, content, ts, raw);
                default:
                    return null;
            }
        }

        private ParsedEvent ParseLoot(string msgCode, string typeCode, string entity,
                                       string content, string ts, string raw)
        {
            string itemName = null;
            int quantity = 1;

            // 情况1：自己 —— “获得了新的战利品薄亚麻布。”
            const string selfPrefix = "获得了新的战利品";
            if (content.StartsWith(selfPrefix))
            {
                itemName = content.Substring(selfPrefix.Length).Trim().TrimEnd('。', '.');
            }
            // 情况2：带名字 —— “顶之座黑卡蒂获得了“深红木原木”×3。”
            else
            {
                var m = QuotedLootRegex.Match(content);
                if (m.Success)
                {
                    itemName = m.Groups[1].Value.Trim();
                    if (m.Groups[2].Success)
                        int.TryParse(m.Groups[2].Value, out quantity);
                }
            }

            if (string.IsNullOrEmpty(itemName)) return null;

            // 清除 FFXIV 特殊图标字符（整个私用区 U+E000 ~ U+F8FF）
            itemName = System.Text.RegularExpressions.Regex.Replace(
                itemName, @"[\uE000-\uF8FF]", "").Trim();
            if (string.IsNullOrEmpty(itemName)) return null;

            var evt = new ParsedEvent
            {
                MessageCode = msgCode,
                TypeCode = typeCode,
                Timestamp = ts,
                RawLog = raw,
                EventType = "loot"
            };
            evt.Data["item_name"] = itemName;
            evt.Data["quantity"] = quantity;
            evt.Data["is_others"] = (typeCode == "203E");
            return evt;
        }

        private ParsedEvent ParseSystemGold(string msgCode, string typeCode,
                                             string content, string ts, string raw)
        {
            var m = GoldRegex.Match(content);
            if (!m.Success) return null;

            string goldStr = m.Groups[1].Value.Replace(",", "");
            if (!double.TryParse(goldStr, out double gold)) return null;

            var evt = new ParsedEvent
            {
                MessageCode = msgCode,
                TypeCode = typeCode,
                Timestamp = ts,
                RawLog = raw,
                EventType = "system_gold"
            };
            evt.Data["amount"] = gold;
            return evt;
        }

        private ParsedEvent ParseMapEvent(string msgCode, string typeCode,
                                           string content, string ts, string raw)
        {
            var evt = new ParsedEvent
            {
                MessageCode = msgCode,
                TypeCode = typeCode,
                Timestamp = ts,
                RawLog = raw,
                EventType = "map_event"
            };
            evt.Data["content"] = content;
            return evt;
        }

        private ParsedEvent ParsePartyMessage(string msgCode, string typeCode, string entity,
                                               string content, string ts, string raw)
        {
            var evt = new ParsedEvent
            {
                MessageCode = msgCode,
                TypeCode = typeCode,
                Timestamp = ts,
                RawLog = raw,
                EventType = "party_message"
            };
            evt.Data["entity"] = entity;
            evt.Data["content"] = content;
            return evt;
        }

        private ParsedEvent ParseTellMessage(string msgCode, string typeCode, string entity,
                                              string content, string ts, string raw)
        {
            var evt = new ParsedEvent
            {
                MessageCode = msgCode,
                TypeCode = typeCode,
                Timestamp = ts,
                RawLog = raw,
                EventType = "tell_message"
            };
            evt.Data["entity"] = entity;
            evt.Data["content"] = content;
            return evt;
        }

        // ---------- ChangeMap ----------
        private ParsedEvent ParseChangeMap(string payload, string ts, string raw)
        {
            // "28:1059:无失世界:巡梦金库:"
            var parts = payload.Split(':');
            if (parts.Length < 4) return null;

            string region = parts[2];
            string zone = parts[3];

            var evt = new ParsedEvent
            {
                MessageCode = parts[0],
                TypeCode = "",
                Timestamp = ts,
                RawLog = raw,
                EventType = "map_change"
            };
            evt.Data["map_name"] = string.IsNullOrEmpty(region) ? zone : $"{region}|{zone}";
            return evt;
        }

        // ---------- ChangePrimaryPlayer ----------
        private ParsedEvent ParseChangePrimaryPlayer(string payload, string ts, string raw)
        {
            // "02:1002CAB5:顶之座黑卡蒂"
            var parts = payload.Split(':');
            if (parts.Length < 3) return null;

            var evt = new ParsedEvent
            {
                MessageCode = parts[0],
                TypeCode = "",
                Timestamp = ts,
                RawLog = raw,
                EventType = "self_update"
            };
            evt.Data["entity_id"] = parts[1];
            evt.Data["player_name"] = parts[2];
            return evt;
        }

        // ---------- AddCombatant ----------
        private ParsedEvent ParseAddCombatant(string payload, string ts, string raw)
        {
            // "03:10048F04:Ritare:13:64:0000:495:宇宙和音:0:0:..."
            var parts = payload.Split(':');
            if (parts.Length < 8) return null;

            var evt = new ParsedEvent
            {
                MessageCode = parts[0],
                TypeCode = "",
                Timestamp = ts,
                RawLog = raw,
                EventType = "player_info"
            };
            evt.Data["entity_id"] = parts[1];
            evt.Data["name"] = parts[2];
            evt.Data["world"] = parts[7];
            return evt;
        }

        // ---------- PartyList ----------
        private ParsedEvent ParsePartyList(string payload, string ts, string raw)
        {
            // "0B:5:10048F04:1002CAB5:1002DB42:10027315:100318BB"
            var parts = payload.Split(':');
            if (parts.Length < 2) return null;
            if (!int.TryParse(parts[1], out int count)) return null;

            var ids = new List<string>();
            for (int i = 2; i < parts.Length && ids.Count < count; i++)
                if (!string.IsNullOrEmpty(parts[i])) ids.Add(parts[i]);

            var evt = new ParsedEvent
            {
                MessageCode = parts[0],
                TypeCode = "",
                Timestamp = ts,
                RawLog = raw,
                EventType = "party_list"
            };
            evt.Data["count"] = count;
            evt.Data["member_ids"] = ids;
            return evt;
        }

        // ---------- 273 判断行 ----------
        private ParsedEvent ParseJudge273(string payload, string ts, string raw)
        {
            // "111:40003DB9:0197:399C:0:0:0"
            var parts = payload.Split(':');
            if (parts.Length < 4) return null;

            string entity = parts[1];
            string category = parts[2];
            var rest = new List<string>();
            for (int i = 3; i < parts.Length; i++) rest.Add(parts[i]);

            var evt = new ParsedEvent
            {
                MessageCode = "273",
                TypeCode = category,
                Timestamp = ts,
                RawLog = raw,
                EventType = "net_judge"
            };
            evt.Data["entity"] = entity;
            evt.Data["category"] = category;
            evt.Data["params"] = string.Join(":", rest);   // "399C:0:0:0"
            return evt;
        }
    }
}