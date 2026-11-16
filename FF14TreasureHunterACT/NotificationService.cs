// NotificationService.cs
using System.Drawing;
using System.Windows.Forms;

namespace FF14TreasureHunterACT
{
    /// <summary>
    /// Windows 通知服务（托盘气泡）。
    /// 对应 Python 的 notification_service.py（Python 用 winotify，C# 用 NotifyIcon）。
    /// 
    /// 必须在 UI 线程调用（WinForms 消息循环所在的线程）。
    /// </summary>
    public static class NotificationService
    {
        private static NotifyIcon _notifyIcon;
        private static readonly object _lock = new object();

        /// <summary>
        /// 发送 Windows 通知。
        /// module 可选： "summary"（成果发送）、"trigger"（触发器动作）、"prediction"（预测）
        /// </summary>
        public static void Send(string title, string message, string module = "summary")
        {
            // 总开关
            if (!ConfigManager.GetBool("notification.enabled", false)) return;

            // 模块开关
            string moduleKey = $"notification.{module}_enabled";
            if (!ConfigManager.GetBool(moduleKey, true)) return;

            try
            {
                lock (_lock)
                {
                    if (_notifyIcon == null)
                    {
                        _notifyIcon = new NotifyIcon
                        {
                            Icon = SystemIcons.Information,
                            Visible = true,
                            Text = "FF14 挖宝会计"
                        };
                    }
                    _notifyIcon.BalloonTipTitle = title ?? "";
                    _notifyIcon.BalloonTipText = message ?? "";
                    _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
                    _notifyIcon.ShowBalloonTip(5000);   // 5 秒
                }
            }
            catch (System.Exception ex)
            {
                // 通知失败不影响主流程
                System.Diagnostics.Debug.WriteLine($"[Notify] 失败: {ex.Message}");
            }
        }

        /// <summary>程序退出时释放托盘图标，避免残留。</summary>
        public static void Dispose()
        {
            lock (_lock)
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }
            }
        }
    }
}