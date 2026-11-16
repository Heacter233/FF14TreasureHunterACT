// CostCalculator.cs
using System.Collections.Generic;

namespace FF14TreasureHunterACT
{
    /// <summary>
    /// 成本计算器：根据物品价格、工资、图钱计算某一段的收入/成本/盈亏。
    /// 对应 Python 的 cost_calculator.py
    /// </summary>
    public class CostCalculator
    {
        public Dictionary<string, (double Price, double Bonus)> ItemPrices { get; private set; }
            = new Dictionary<string, (double Price, double Bonus)>();

        public CostCalculator() { }

        public CostCalculator(Dictionary<string, (double Price, double Bonus)> itemPrices)
        {
            if (itemPrices != null) ItemPrices = itemPrices;
        }

        public void UpdatePrices(Dictionary<string, (double Price, double Bonus)> prices)
        {
            ItemPrices = prices ?? new Dictionary<string, (double Price, double Bonus)>();
        }

        /// <summary>
        /// 计算一个分段的收入/成本/盈亏。
        /// </summary>
        /// <returns>(Income, Cost, Profit, TotalItemIncome, TotalBonusExpense)</returns>
        public (double Income, double Cost, double Profit,
                double TotalItemIncome, double TotalBonusExpense)
            ComputeSegment(int maps, double sysGold,
                            Dictionary<string, int> itemsDict,
                            int workers, double salaryPerMap, double mapCost)
        {
            double totalItemIncome = 0.0;
            double totalBonusExpense = 0.0;

            if (itemsDict != null)
            {
                foreach (var kv in itemsDict)
                {
                    (double Price, double Bonus) info;
                    if (!ItemPrices.TryGetValue(kv.Key, out info))
                        info = (0, 0);

                    totalItemIncome += kv.Value * info.Price;
                    totalBonusExpense += kv.Value * info.Bonus;
                }
            }

            double baseSalary = workers * salaryPerMap * maps;
            double bonusSalary = workers * totalBonusExpense;
            double totalSalary = baseSalary + bonusSalary;
            double totalMapCost = maps * mapCost;

            double income = sysGold + totalItemIncome;
            double cost = totalSalary + totalMapCost;
            double profit = income - cost;

            return (income, cost, profit, totalItemIncome, totalBonusExpense);
        }
    }
}