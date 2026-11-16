// PluginInfo.cs
using System;
using System.Reflection;

namespace FF14TreasureHunterACT
{
    /// <summary>
    /// 插件自身信息的统一出口。
    /// UserAgent 会带上真实版本号（如 FF14TreasureHunterACT/1.0.3.0），
    /// 这样服务器端只靠 access log 的 cs(User-Agent) 就能统计版本分布 / 升级率。
    /// </summary>
    public static class PluginInfo
    {
        private static string _version;

        /// <summary>插件版本号，例如 "1.0.3.0"</summary>
        public static string Version
        {
            get
            {
                if (_version != null) return _version;
                try
                {
                    var ver = typeof(PluginInfo).Assembly.GetName().Version;
                    _version = ver != null ? ver.ToString() : "0.0.0.0";
                }
                catch { _version = "0.0.0.0"; }
                return _version;
            }
        }

        /// <summary>HTTP User-Agent，形如 FF14TreasureHunterACT/1.0.3.0</summary>
        public static string UserAgent
        {
            get { return "FF14TreasureHunterACT/" + Version; }
        }
    }
}
