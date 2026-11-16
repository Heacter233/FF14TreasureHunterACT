// ConfigDefaults.cs
using Newtonsoft.Json.Linq;

namespace FF14TreasureHunterACT
{
    /// <summary>
    /// 内置默认配置。
    ///
    /// 为什么必须内嵌到代码里：
    /// 旧版程序会在 config 目录写一个 defaults.json，新装的用户没有这个文件，
    /// 于是 ConfigManager 拿到的默认值是空 JObject。
    /// 结果 EventProcessor 里 GetString("detection.map_complete_keyword", "")
    /// 返回空字符串，地图完成的事件永远匹配不上 —— 表现为"能打宝但统计不到地图数"。
    ///
    /// 所以默认值必须来自代码本身（永远存在），defaults.json 只作为可选的覆盖层。
    /// </summary>
    public static class ConfigDefaults
    {
        /// <summary>当前配置结构的版本号。新增必需字段时 +1，触发缺口补全。</summary>
        public const int CurrentVersion = 2;

        /// <summary>地图完成关键词（可在设置页修改，支持英文/中文逗号分隔多个）</summary>
        public const string MapCompleteKeyword =
            "将陷阱引来的魔物全部打倒了！，“运河宝物库深层”任务开始。";

        /// <summary>魔纹出现关键词</summary>
        public const string MagicSealKeyword =
            "通往宝物库的传送魔纹出现了！";

        public static JObject Create()
        {
            return new JObject
            {
                ["_config_version"] = CurrentVersion,

                ["act"] = new JObject
                {
                    ["log_directory"] = "",
                    ["scan_interval"] = 3
                },

                // ===== 识别规则（决定能否统计到地图/魔纹）=====
                ["detection"] = new JObject
                {
                    ["map_complete_keyword"] = MapCompleteKeyword,
                    ["magic_seal_keyword"] = MagicSealKeyword,
                    ["auto_single_mode"] = true,
                    ["single_player_mode"] = false,
                    // 以下为旧版 Python 遗留字段，C# 版不再读取，
                    // 保留是为了让从旧版迁移过来的配置文件保持结构完整
                    ["loot_codes"] = new JArray { "0039" },
                    ["single_loot_codes"] = "083E",
                    ["auto_single_enabled"] = true,
                    ["layer_codes"] = new JArray { "0839", "0889" },
                    ["map_event_code"] = "0839",
                    ["map_complete_type_code"] = "0839",
                    ["magic_seal_type_code"] = "0839",
                    ["map_complete_message_code"] = "00",
                    ["magic_seal_message_code"] = "00"
                },

                ["postnamazu"] = new JObject
                {
                    ["port"] = 2019,
                    ["enabled"] = true
                },

                ["ui"] = new JObject
                {
                    ["show_player_name"] = false,
                    ["overlay_port"] = "8765",
                    ["overlay_enabled"] = true,
                    ["auto_check_update"] = true,
                    ["debug_log"] = false
                },

                ["salary"] = new JObject
                {
                    ["worker_adjust"] = "0",
                    ["per_map"] = 0.0,
                    ["map_cost"] = 0.0,
                    ["extra_cost"] = 0.0
                },

                ["notification"] = new JObject
                {
                    ["enabled"] = false,
                    ["summary_enabled"] = true,
                    ["trigger_enabled"] = true,
                    ["prediction_enabled"] = true
                },

                ["auto_segment"] = new JObject
                {
                    ["enabled"] = false,
                    ["message_code"] = "00",
                    ["type_code"] = "0038",
                    ["keyword"] = "挖宝会计分段"
                },

                ["auto_send"] = new JObject
                {
                    ["tell_enabled"] = true,
                    ["tell_text"] = "挖宝会计",
                    ["tell_type"] = "0038",
                    ["party_enabled"] = false,
                    ["party_text"] = "挖宝会计小队",
                    ["party_type"] = "0038"
                },

                ["send"] = new JObject
                {
                    ["segment_only"] = false
                },

                ["money_unit_wan"] = true
            };
        }

        /// <summary>
        /// 必需字段清单：缺失或为空时会导致功能静默失效。
        /// 用于 Load() 后的自检与修复。
        /// </summary>
        public static readonly string[] CriticalStringPaths =
        {
            "detection.map_complete_keyword",
            "detection.magic_seal_keyword"
        };
    }
}
