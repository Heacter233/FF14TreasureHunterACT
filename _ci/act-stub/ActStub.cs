// ActStub.cs
//
// CI 专用：ACT (Advanced Combat Tracker) 的最小引用桩。
//
// 为什么需要它：
//   本插件编译时必须引用 ACT 的 `Advanced Combat Tracker.exe`，而该文件
//   不在本仓库中（体积大、且不适合随源码分发）。GitHub Actions 又无法
//   稳定地从官网下载（返回 403 / 软 404）。因此这里用「只声明、不实现」
//   的方式还原插件实际用到的 ACT API，仅供 CI 编译期引用。
//
// 关键点：ACT 是强命名的（PublicKeyToken=a946b61e93d97868）。
//   为了让编译出来的插件 DLL 在真实 ACT 中能被正确绑定，
//   本桩使用**公钥签名**（PublicSign）+ 相同的程序集版本，
//   使其 AssemblyIdentity 与真实 ACT 完全一致。
//   act-public.snk 只是公钥（本来就是公开信息），不含私钥。
//
// 该桩**不会**随插件发布，也**不参与**本地正常构建。
// 插件实际用到的 ACT API 仅有以下几项（见 Plugin.cs）：
//   - IActPluginV1.InitPlugin / DeInitPlugin
//   - ActGlobals.oFormActMain.OnLogLineRead 事件
//   - LogLineEventArgs.logLine 字段
//
// 如果插件以后用到了新的 ACT API，需要同步补到这里，否则 CI 会编译失败
// （这正是我们想要的：CI 尽早发现依赖变化）。

using System;
using System.Windows.Forms;

namespace Advanced_Combat_Tracker
{
    /// <summary>ACT 传给插件的日志行参数</summary>
    public class LogLineEventArgs : EventArgs
    {
        public string logLine;
        public int detectedType;
        public DateTime detectedTime;
        public string detectedZone;
        public bool inCombat;
        public string originalLogLine;
        public string companionLogName;
    }

    /// <summary>日志行事件委托</summary>
    public delegate void LogLineEventDelegate(bool isImport, LogLineEventArgs logInfo);

    /// <summary>ACT 主窗体（仅声明插件用到的成员）</summary>
    public class FormActMain
    {
        public event LogLineEventDelegate OnLogLineRead;

        // 防止编译器警告「事件从未使用」；运行时不会执行
        internal void RaiseForStub(bool isImport, LogLineEventArgs e)
        {
            var h = OnLogLineRead;
            if (h != null) h(isImport, e);
        }
    }

    /// <summary>ACT 全局对象</summary>
    public static class ActGlobals
    {
        public static FormActMain oFormActMain = new FormActMain();
    }

    /// <summary>ACT 插件接口（v1）</summary>
    public interface IActPluginV1
    {
        void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText);
        void DeInitPlugin();
    }
}
