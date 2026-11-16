// FinancialManager.cs
using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace FF14TreasureHunterACT
{
    /// <summary>
    /// 财务管理器：处理成本快照（存档旧参数阶段），并汇总全量成本/收入。
    /// 对应 Python 的 financial_manager.py
    /// </summary>
    public class FinancialManager
    {
        private readonly CostCalculator _calc;

        // 基准（最近一次快照时的总量）
        private int _baseMaps = 0;
        private Dictionary<string, int> _baseItems = new Dictionary<string, int>();
        private double _baseCostNoExtra = 0.0;   // 累计历史成本（不含附加成本），仅记录

        public FinancialManager(CostCalculator calc)
        {
            _calc = calc;
        }

        /// <summary>
        /// 有效地图数 = 真实完成的地图（map_complete）+ 手动调整（map_adjust）。
        ///
        /// 手动调整要真实反映到成本上：补录一张漏记的图，工资和图钱都应该跟着算。
        /// 这里返回可能小于真实完成数的值（用户手动减过），但不会小于 0。
        ///
        /// 注意：全部汇总/历史统计仍用 <see cref="Database.GetTotalMaps"/>（纯真实数据），
        /// 只有成本计算用这个口径。
        /// </summary>
        private static int GetEffectiveMaps(Database db, string runLabel)
        {
            int maps = db.GetTotalMaps(runLabel);
            try
            {
                foreach (var c in db.GetCustomRecordSummary(runLabel))
                {
                    if (c.RecordName == "map_adjust")
                        maps += (int)c.Total;
                }
            }
            catch { }
            return Math.Max(0, maps);
        }

        /// <summary>
        /// 用旧参数把当前未存档的增量存入 cost_snapshots。
        /// 对应 Python 的 snapshot_current_state。
        /// </summary>
        /// <param name="oldParams">必须包含 WorkerCount / SalaryPerMap / MapCost / ItemPrices</param>
        public void SnapshotCurrentState(Database db, string runLabel,
                                          Dictionary<string, double> oldParams,
                                          Dictionary<string, (double Price, double Bonus)> oldPrices)
        {
            int totalMaps = GetEffectiveMaps(db, runLabel);
            var totalLoot = db.GetLootSummary(runLabel);

            var curItems = new Dictionary<string, int>();
            foreach (var item in totalLoot)
                curItems[item.ItemName] = item.Quantity;

            int deltaMaps = totalMaps - _baseMaps;

            var deltaItems = new Dictionary<string, int>();
            foreach (var kv in curItems)
            {
                int baseQty = _baseItems.TryGetValue(kv.Key, out var b) ? b : 0;
                if (kv.Value > baseQty)
                    deltaItems[kv.Key] = kv.Value - baseQty;
            }

            if (deltaMaps == 0 && deltaItems.Count == 0) return;

            oldParams.TryGetValue("WorkerCount", out double workers);
            oldParams.TryGetValue("SalaryPerMap", out double salary);
            oldParams.TryGetValue("MapCost", out double mapCost);

            var prices = oldPrices ?? new Dictionary<string, (double, double)>();
            _calc.UpdatePrices(prices);

            double baseSalary = workers * salary * deltaMaps;
            double bonusSalary = 0.0;
            double itemIncome = 0.0;
            foreach (var kv in deltaItems)
            {
                (double Price, double Bonus) info;
                if (!prices.TryGetValue(kv.Key, out info)) info = (0, 0);
                itemIncome += kv.Value * info.Price;
                bonusSalary += kv.Value * info.Bonus;
            }

            double totalCostDelta = baseSalary + (workers * bonusSalary) + (deltaMaps * mapCost);
            double incomeDelta = itemIncome;

            // 更新基准
            _baseMaps = totalMaps;
            _baseItems = new Dictionary<string, int>(curItems);
            _baseCostNoExtra += totalCostDelta;

            // 写入快照
            string itemJson = JsonConvert.SerializeObject(deltaItems);
            db.AddCostSnapshot(
                runLabel,
                DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                workers, salary, mapCost,
                deltaMaps, 0.0, itemJson,
                incomeDelta, totalCostDelta, incomeDelta - totalCostDelta
            );
        }

        /// <summary>
        /// 全量重新计算：历史快照累加 + 当前未存档增量 + 系统金。
        /// 对应 Python 的 recalculate_all。
        /// </summary>
        public (double TotalIncome, double TotalCost, double Profit)
            RecalculateAll(Database db, string runLabel,
                            int workers, double salaryPerMap, double mapCost,
                            double extraCost,
                            Dictionary<string, (double Price, double Bonus)> itemPrices,
                            bool useWan)
        {
            int totalMaps = GetEffectiveMaps(db, runLabel);
            double totalGoldRaw = db.GetTotalSystemGold(runLabel);
            var totalLoot = db.GetLootSummary(runLabel);

            var curItems = new Dictionary<string, int>();
            foreach (var item in totalLoot)
                curItems[item.ItemName] = item.Quantity;

            int deltaMaps = totalMaps - _baseMaps;
            var deltaItems = new Dictionary<string, int>();
            foreach (var kv in curItems)
            {
                int baseQty = _baseItems.TryGetValue(kv.Key, out var b) ? b : 0;
                if (kv.Value > baseQty)
                    deltaItems[kv.Key] = kv.Value - baseQty;
            }

            _calc.UpdatePrices(itemPrices);

            double baseSalary = workers * salaryPerMap * deltaMaps;
            double bonusSalary = 0.0;
            double itemIncomeDelta = 0.0;
            foreach (var kv in deltaItems)
            {
                (double Price, double Bonus) info;
                if (!itemPrices.TryGetValue(kv.Key, out info)) info = (0, 0);
                itemIncomeDelta += kv.Value * info.Price;
                bonusSalary += kv.Value * info.Bonus;
            }

            double currentCostDelta = baseSalary + (workers * bonusSalary) + (deltaMaps * mapCost);

            // 历史快照累加
            var snapshots = db.GetCostSnapshotsByLabel(runLabel);
            double histItemIncome = 0.0, histCost = 0.0;
            foreach (var s in snapshots)
            {
                histItemIncome += s.TotalIncome;
                histCost += s.TotalCost;
            }

            double sysGold = useWan ? totalGoldRaw / 10000.0 : totalGoldRaw;

            double totalIncome = histItemIncome + itemIncomeDelta + sysGold;
            double totalCost = histCost + currentCostDelta + extraCost;
            double profit = totalIncome - totalCost;

            return (totalIncome, totalCost, profit);
        }
    }
}