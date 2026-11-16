// MessageComposer.cs
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FF14TreasureHunterACT
{
    /// <summary>消息拼装所需的原始数据</summary>
    public class MessageData
    {
        public int Maps { get; set; }
        public int MagicSeals { get; set; }
        public double SystemGold { get; set; }

        // 详细模式优先（与 Python 一致）
        public List<(string MapName, string ItemName, int Quantity)> ItemsByMap { get; set; }
        public List<(string MapName, string LayerName, int Count)> LayersByMap { get; set; }

        // 简单模式
        public List<(string ItemName, int Quantity)> Items { get; set; }
        public List<(string LayerName, int Count)> Layers { get; set; }

        public List<(string RecordName, double Total)> Customs { get; set; }
    }

    /// <summary>
    /// 消息拼装器：把统计数据格式化为可直接发送的多行文本。
    /// 对应 Python 的 message_composer.py
    /// </summary>
    public static class MessageComposer
    {
        /// <summary>
        /// 返回要发送的消息行（不含 /e 或 /p 前缀，由调用者决定）。
        /// </summary>
        public static List<string> Format(MessageData data, bool detailEnabled, bool useWan)
        {
            var lines = new List<string>();
            if (data == null) return lines;

            int maps = data.Maps;
            int seals = data.MagicSeals;
            double gold = data.SystemGold;

            string goldStr = useWan
                ? (gold / 10000.0).ToString("F4", CultureInfo.InvariantCulture) + "万"
                : gold.ToString("F0", CultureInfo.InvariantCulture);

            double rate = maps > 0 ? (seals * 100.0 / maps) : 0.0;
            lines.Add($"地图数：【{maps}】魔纹数：【{seals}】魔纹率：{rate.ToString("F1", CultureInfo.InvariantCulture)}%");
            lines.Add($"系统金收入：{goldStr}");

            // ---------- 出货 ----------
            if (data.ItemsByMap != null && data.ItemsByMap.Count > 0)
            {
                var groups = new Dictionary<string, List<string>>();
                var order = new List<string>();
                foreach (var entry in data.ItemsByMap)
                {
                    string mapName = string.IsNullOrEmpty(entry.MapName) ? "未知" : entry.MapName;
                    if (!groups.ContainsKey(mapName))
                    {
                        groups[mapName] = new List<string>();
                        order.Add(mapName);
                    }
                    groups[mapName].Add($"{entry.ItemName}×{entry.Quantity}");
                }
                var parts = new List<string>();
                foreach (var mapName in order)
                    parts.Add($"（{mapName}）" + string.Join("，", groups[mapName]));
                lines.Add("出货：" + (parts.Count > 0 ? string.Join("；", parts) : "无"));
            }
            else if (data.Items != null && data.Items.Count > 0)
            {
                var parts = new List<string>();
                foreach (var it in data.Items)
                    parts.Add($"{it.ItemName}×{it.Quantity}");
                lines.Add("出货：" + string.Join("，", parts));
            }
            else
            {
                lines.Add("出货：无");
            }

            // ---------- 层级 ----------
            if (data.LayersByMap != null && data.LayersByMap.Count > 0)
            {
                var groups = new Dictionary<string, List<string>>();
                var order = new List<string>();
                foreach (var entry in data.LayersByMap)
                {
                    string mapName = string.IsNullOrEmpty(entry.MapName) ? "未知" : entry.MapName;
                    if (!groups.ContainsKey(mapName))
                    {
                        groups[mapName] = new List<string>();
                        order.Add(mapName);
                    }
                    groups[mapName].Add($"{entry.LayerName}×{entry.Count}");
                }
                if (groups.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (var mapName in order)
                        parts.Add($"（{mapName}）" + string.Join("，", groups[mapName]));
                    lines.Add("层级：" + string.Join("；", parts));
                }
            }
            else if (data.Layers != null && data.Layers.Count > 0)
            {
                var parts = new List<string>();
                foreach (var ly in data.Layers)
                    parts.Add($"{ly.LayerName}×{ly.Count}");
                lines.Add("层级：" + string.Join("，", parts));
            }

            // ---------- 其他记录 ----------
            if (data.Customs != null && data.Customs.Count > 0)
            {
                var parts = new List<string>();
                foreach (var c in data.Customs)
                    parts.Add($"{c.RecordName}：{c.Total.ToString("F0", CultureInfo.InvariantCulture)}");
                lines.Add("其他：" + string.Join("，", parts));
            }

            return lines;
        }
    }
}