// RawLogWriter.cs
using System;
using System.IO;
using System.Text;

namespace FF14TreasureHunterACT
{
    /// <summary>
    /// 将 ACT 收到的原始日志行写入独立文件，供调试分析使用。
    /// 默认关闭，需用户在设置中勾选"输出Debug日志"才生效。
    /// </summary>
    public static class RawLogWriter
    {
        private static string _logPath;
        private static readonly object _lock = new object();
        private static bool _enabled = false;

        /// <summary>是否启用日志输出。切换为 true 时会立即创建新文件。</summary>
        public static bool Enabled
        {
            get { return _enabled; }
            set
            {
                lock (_lock)
                {
                    if (_enabled == value) return;
                    _enabled = value;
                    if (value)
                    {
                        // 勾选时立即创建新文件
                        InitializeFileInternal();
                    }
                    // 取消勾选时不删除已写文件，后续 Write 会直接丢弃
                }
            }
        }

        public static string CurrentPath { get { return _logPath; } }

        /// <summary>插件初始化时调用：从配置读取默认状态</summary>
        public static void Initialize()
        {
            _enabled = ConfigManager.GetBool("ui.debug_log", false);
            if (_enabled)
                InitializeFileInternal();
        }

        private static void InitializeFileInternal()
        {
            try
            {
                string dir = Path.Combine(ConfigManager.LogDir, "raw");
                Directory.CreateDirectory(dir);
                _logPath = Path.Combine(dir, $"raw_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                File.AppendAllText(_logPath,
                    $"# 日志开始于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
                _logPath = null;
                _enabled = false;
            }
        }

        public static void Write(string line)
        {
            if (!_enabled) return;              // 关键：关闭状态直接丢弃
            if (string.IsNullOrEmpty(_logPath)) return;

            lock (_lock)
            {
                try
                {
                    File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
    }
}