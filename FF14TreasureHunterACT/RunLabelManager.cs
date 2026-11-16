// RunLabelManager.cs
using System;

namespace FF14TreasureHunterACT
{
    /// <summary>
    /// run_label 管理器：格式 "YYYY-MM-DD" 或 "YYYY-MM-DD(N)"。
    /// 依赖 ConfigManager 存的 "last_run_date" / "last_run_index"，
    /// 不查库、简单可靠。
    /// </summary>
    public static class RunLabelManager
    {
        private const string KEY_DATE  = "runtime.last_run_date";
        private const string KEY_INDEX = "runtime.last_run_index";

        public static string Generate()
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            string lastDate = ConfigManager.GetString(KEY_DATE, "");
            int lastIndex = ConfigManager.GetInt(KEY_INDEX, 0);

            int newIndex;
            if (lastDate == today)
                newIndex = lastIndex + 1;       // 当天第 N 次
            else
                newIndex = 0;                    // 新的一天，从 0 开始

            ConfigManager.Set(KEY_DATE, today);
            ConfigManager.Set(KEY_INDEX, newIndex);

            return newIndex == 0 ? today : $"{today}({newIndex})";
        }

        /// <summary>程序重启后调用，从已有 label 推断索引（可选，作为兜底）</summary>
        public static void SyncFromDatabase(Database db)
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            var labels = db.GetDistinctRunLabels(today);
            int max = -1;
            foreach (var lbl in labels)
            {
                if (lbl == today) { if (max < 0) max = 0; }
                else if (lbl.StartsWith(today + "(") && lbl.EndsWith(")"))
                {
                    var inner = lbl.Substring(today.Length + 1, lbl.Length - today.Length - 2);
                    if (int.TryParse(inner, out int n) && n > max) max = n;
                }
            }
            string lastDate = ConfigManager.GetString(KEY_DATE, "");
            if (lastDate == today)
            {
                int current = ConfigManager.GetInt(KEY_INDEX, 0);
                if (max > current) ConfigManager.Set(KEY_INDEX, max);
            }
            else
            {
                ConfigManager.Set(KEY_DATE, today);
                ConfigManager.Set(KEY_INDEX, Math.Max(0, max));
            }
        }
    }
}