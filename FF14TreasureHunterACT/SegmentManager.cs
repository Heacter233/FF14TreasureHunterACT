// SegmentManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FF14TreasureHunterACT
{
    /// <summary>分段快照的数据内容</summary>
    public class SegmentData
    {
        [JsonProperty("maps")] public int Maps { get; set; }
        [JsonProperty("magic_seals")] public int MagicSeals { get; set; }
        [JsonProperty("system_gold")] public double SystemGold { get; set; }
        [JsonProperty("items")] public List<SegmentItemEntry> Items { get; set; } = new List<SegmentItemEntry>();
        [JsonProperty("layers")] public List<SegmentLayerEntry> Layers { get; set; } = new List<SegmentLayerEntry>();
        [JsonProperty("customs")] public List<SegmentCustomEntry> Customs { get; set; } = new List<SegmentCustomEntry>();
        [JsonProperty("total_cost")] public double TotalCost { get; set; }
        [JsonProperty("total_income")] public double TotalIncome { get; set; }
        [JsonProperty("profit")] public double Profit { get; set; }
        [JsonProperty("elapsed")] public double Elapsed { get; set; }
    }

    public class SegmentItemEntry
    {
        [JsonProperty("item_name")] public string ItemName { get; set; } = "";
        [JsonProperty("quantity")] public int Quantity { get; set; }
    }

    public class SegmentLayerEntry
    {
        [JsonProperty("layer_name")] public string LayerName { get; set; } = "";
        [JsonProperty("count")] public int Count { get; set; }
    }

    public class SegmentCustomEntry
    {
        [JsonProperty("record_name")] public string RecordName { get; set; } = "";
        [JsonProperty("total")] public double Total { get; set; }
    }

    /// <summary>单条分段快照</summary>
    public class SegmentSnapshot
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("data")] public SegmentData Data { get; set; } = new SegmentData();
        [JsonProperty("created_at")] public string CreatedAt { get; set; } = "";
        [JsonProperty("start_time")] public double StartTime { get; set; }
        [JsonProperty("end_time")] public double EndTime { get; set; }
        [JsonProperty("total_elapsed_at_snapshot")] public double TotalElapsedAtSnapshot { get; set; }
    }

    /// <summary>
    /// 分段管理器：管理快照列表，并提供"当前段数据 = 全量 - 所有快照"的计算。
    /// 对应 Python 的 segment_manager.py
    /// </summary>
    public class SegmentManager
    {
        public string SnapshotFile { get; private set; }
        public List<SegmentSnapshot> Snapshots { get; private set; } = new List<SegmentSnapshot>();

        private readonly object _lock = new object();

        public SegmentManager(string snapshotFile)
        {
            SnapshotFile = snapshotFile;
            LoadSnapshots();
        }

        private void LoadSnapshots()
        {
            lock (_lock)
            {
                if (!File.Exists(SnapshotFile)) return;
                try
                {
                    var json = File.ReadAllText(SnapshotFile, System.Text.Encoding.UTF8);
                    var root = JObject.Parse(json);
                    var arr = root["snapshots"] as JArray;
                    if (arr != null)
                    {
                        foreach (var item in arr)
                        {
                            try
                            {
                                var snap = item.ToObject<SegmentSnapshot>();
                                if (snap != null) Snapshots.Add(snap);
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Segment] 加载失败: {ex.Message}");
                }
            }
        }

        public void SaveSnapshots()
        {
            lock (_lock)
            {
                try
                {
                    var root = new JObject();
                    var arr = new JArray();
                    foreach (var s in Snapshots)
                        arr.Add(JObject.FromObject(s));
                    root["snapshots"] = arr;

                    Directory.CreateDirectory(Path.GetDirectoryName(SnapshotFile));
                    File.WriteAllText(SnapshotFile,
                        root.ToString(Formatting.Indented),
                        System.Text.Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Segment] 保存失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 当前段数据 = 全量 - 所有快照。
        /// 对应 Python 的 get_current_segment_data。
        /// </summary>
        public SegmentData GetCurrentSegmentData(Database db, string runLabel)
        {
            int totalMaps = db.GetTotalMaps(runLabel);
            int totalSeals = db.GetTotalMagicSeals(runLabel);
            double totalGold = db.GetTotalSystemGold(runLabel);
            var totalLoot = db.GetLootSummary(runLabel);
            var totalLayers = db.GetLayerSummary(runLabel);
            var totalCustom = db.GetCustomRecordSummary(runLabel);

            var summary = db.GetRunSummary(runLabel);
            double curTotalCost = summary?.TotalCost ?? 0.0;
            double curTotalIncome = summary?.TotalIncome ?? 0.0;
            double curProfit = summary?.Profit ?? 0.0;

            // 用字典便于相减
            var lootDict = new Dictionary<string, int>();
            foreach (var it in totalLoot) lootDict[it.ItemName] = it.Quantity;

            var layerDict = new Dictionary<string, int>();
            foreach (var ly in totalLayers) layerDict[ly.LayerName] = ly.Count;

            var customDict = new Dictionary<string, double>();
            foreach (var c in totalCustom) customDict[c.RecordName] = c.Total;

            // 减去所有快照
            foreach (var snap in Snapshots)
            {
                var d = snap.Data;
                if (d == null) continue;

                totalMaps -= d.Maps;
                totalSeals -= d.MagicSeals;
                totalGold -= d.SystemGold;
                curTotalCost -= d.TotalCost;
                curTotalIncome -= d.TotalIncome;
                curProfit -= d.Profit;

                if (d.Items != null)
                    foreach (var it in d.Items)
                        if (lootDict.ContainsKey(it.ItemName))
                            lootDict[it.ItemName] -= it.Quantity;

                if (d.Layers != null)
                    foreach (var ly in d.Layers)
                        if (layerDict.ContainsKey(ly.LayerName))
                            layerDict[ly.LayerName] -= ly.Count;

                if (d.Customs != null)
                    foreach (var c in d.Customs)
                        if (customDict.ContainsKey(c.RecordName))
                            customDict[c.RecordName] -= c.Total;
            }

            // 构造结果
            // ★ Maps/MagicSeals 这里【不能】先 clamp：
            //   手动调整（map_adjust/seal_adjust）是在下方才加上的，
            //   如果先用 Math.Max(0, 全量-快照)，负数部分会被吃掉，
            //   随后再加调整量就会重复计算（分段后当前段凭空多出调整值）。
            //   正确做法：先保留可能为负的原值 → 加完调整量 → 再统一 clamp 到 0。
            var data = new SegmentData
            {
                Maps = totalMaps,
                MagicSeals = totalSeals,
                SystemGold = Math.Max(0, totalGold),
                TotalCost = Math.Max(0, curTotalCost),
                TotalIncome = Math.Max(0, curTotalIncome),
                Profit = curProfit
            };

            foreach (var kv in lootDict)
                if (kv.Value > 0)
                    data.Items.Add(new SegmentItemEntry { ItemName = kv.Key, Quantity = kv.Value });

            foreach (var kv in layerDict)
                if (kv.Value > 0)
                    data.Layers.Add(new SegmentLayerEntry { LayerName = kv.Key, Count = kv.Value });

            // 剔除内部调整项
            double mapAdjust = 0, sealAdjust = 0;
            foreach (var kv in customDict)
            {
                if (kv.Key == "map_adjust") { mapAdjust += kv.Value; continue; }
                if (kv.Key == "seal_adjust") { sealAdjust += kv.Value; continue; }
                if (kv.Value > 0)
                    data.Customs.Add(new SegmentCustomEntry { RecordName = kv.Key, Total = kv.Value });
            }
            data.Maps = Math.Max(0, data.Maps + (int)mapAdjust);
            data.MagicSeals = Math.Max(0, data.MagicSeals + (int)sealAdjust);

            // 耗时
            double totalElapsed = summary?.Elapsed ?? 0.0;
            double lastSnapshotElapsed = 0.0;
            if (Snapshots.Count > 0)
                lastSnapshotElapsed = Snapshots[Snapshots.Count - 1].TotalElapsedAtSnapshot;
            data.Elapsed = Math.Max(0, totalElapsed - lastSnapshotElapsed);

            return data;
        }

        /// <summary>生成一个分段快照并保存。对应 Python 的 create_segment。</summary>
        public SegmentSnapshot CreateSegment(Database db, string runLabel, double currentStartTime)
        {
            lock (_lock)
            {
                var currentData = GetCurrentSegmentData(db, runLabel);
                double now = UnixNow();

                if (Snapshots.Count > 0 && Snapshots[Snapshots.Count - 1].EndTime == 0)
                    Snapshots[Snapshots.Count - 1].EndTime = now;

                double totalElapsed = db.GetRunSummary(runLabel)?.Elapsed ?? 0.0;

                int segNum = Snapshots.Count + 1;
                var snapshot = new SegmentSnapshot
                {
                    Name = $"分段{segNum}",
                    Data = currentData,
                    CreatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                    StartTime = currentStartTime,
                    EndTime = now,
                    TotalElapsedAtSnapshot = totalElapsed
                };
                Snapshots.Add(snapshot);
                SaveSnapshots();
                return snapshot;
            }
        }

        private static double UnixNow()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }
    }
}