// HtmlUpdateManager.cs
using System;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;

namespace FF14TreasureHunterACT
{
    /// <summary>
    /// HTML 页面版本检查与下载。
    /// 文件缓存到 %APPDATA%\FF14TreasureHunter\web\
    /// </summary>
    public class HtmlUpdateManager
    {
        private const string RemoteBaseUrl = "https://heikadi.top/ff14ds/wbkj/ACThtml/";

        public string WebDir { get; private set; }
        public string LocalVersion { get; private set; } = "";
        public string RemoteVersion { get; private set; } = "";
        public bool IsUpdating { get; private set; } = false;
        public string LastError { get; private set; } = "";

        public bool UpdateAvailable =>
            !string.IsNullOrEmpty(RemoteVersion) && RemoteVersion != LocalVersion;

        public event Action<string> OnLog;
        public event Action OnStateChanged;

        public HtmlUpdateManager(string webDir)
        {
            WebDir = webDir;
            try { Directory.CreateDirectory(WebDir); } catch { }
            LoadLocalVersion();
        }

        private void LoadLocalVersion()
        {
            try
            {
                string path = Path.Combine(WebDir, "version.json");
                if (File.Exists(path))
                {
                    var obj = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                    // 兼容新旧格式：优先 html_version，回退到 version
                    LocalVersion = obj["html_version"]?.ToString();
                    if (string.IsNullOrEmpty(LocalVersion))
                        LocalVersion = obj["version"]?.ToString() ?? "";
                }
            }
            catch { }
        }

        /// <summary>从本地 version.json 读取 html_version（静态方法，供发布页使用）</summary>
        public static string ReadLocalHtmlVersion(string webDir)
        {
            try
            {
                string path = Path.Combine(webDir, "version.json");
                if (!File.Exists(path)) return "";
                var obj = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                return obj["html_version"]?.ToString() ?? obj["version"]?.ToString() ?? "";
            }
            catch { return ""; }
        }

        /// <summary>后台检查服务器版本</summary>
        public void CheckRemoteAsync()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string json = HttpGet(RemoteBaseUrl + "version.json", 15000);
                    var obj = JObject.Parse(json);
                    // 兼容新旧格式
                    RemoteVersion = obj["html_version"]?.ToString();
                    if (string.IsNullOrEmpty(RemoteVersion))
                        RemoteVersion = obj["version"]?.ToString() ?? ""; LastError = "";
                    Log($"[HTML] 检查完成：本地 {LocalVersion} / 远程 {RemoteVersion}");
                    NotifyState();
                }
                catch (Exception ex)
                {
                    LastError = "检查失败：" + ex.Message;
                    Log($"[HTML] {LastError}");
                    NotifyState();
                }
            });
        }

        /// <summary>后台下载全部 HTML（version.json 中 files 列表）</summary>
        public void DownloadAllAsync()
        {
            if (IsUpdating) return;
            IsUpdating = true;
            LastError = "";
            NotifyState();

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // 1. 拉 version.json
                    string verUrl = RemoteBaseUrl + "version.json";
                    string verJson = HttpGet(verUrl, 15000);
                    var verObj = JObject.Parse(verJson);
                    // 兼容新旧格式：优先 html_version
                    string remoteVer = verObj["html_version"]?.ToString();
                    if (string.IsNullOrEmpty(remoteVer))
                        remoteVer = verObj["version"]?.ToString() ?? "";
                    var files = verObj["files"] as JArray;

                    if (files == null || files.Count == 0)
                        throw new Exception("version.json 缺少 files 字段");

                    // 2. 逐个下载 HTML
                    foreach (var f in files)
                    {
                        string name = f.ToString();
                        if (string.IsNullOrEmpty(name)) continue;
                        string content = HttpGet(RemoteBaseUrl + name, 15000);

                        string target = Path.Combine(WebDir, name);
                        string tmp = target + ".tmp";
                        File.WriteAllText(tmp, content, Encoding.UTF8);
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(tmp, target);

                        Log($"[HTML] 下载 {name}（{content.Length} 字节）");
                    }

                    // 3. 最后写 version.json（原子）
                    string verTarget = Path.Combine(WebDir, "version.json");
                    string verTmp = verTarget + ".tmp";
                    File.WriteAllText(verTmp, verJson, Encoding.UTF8);
                    if (File.Exists(verTarget)) File.Delete(verTarget);
                    File.Move(verTmp, verTarget);

                    // 只有真的拿到了版本号才更新 LocalVersion
                    if (!string.IsNullOrEmpty(remoteVer))
                    {
                        LocalVersion = remoteVer;
                        // 同时把 RemoteVersion 对齐（避免不一致）
                        RemoteVersion = remoteVer;
                    }
                    LastError = "";
                    Log($"[HTML] 更新完成 → {LocalVersion}");
                }
                catch (Exception ex)
                {
                    LastError = "下载失败：" + ex.Message;
                    Log($"[HTML] {LastError}");
                }
                finally
                {
                    IsUpdating = false;
                    NotifyState();
                }
            });
        }

        private string HttpGet(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.KeepAlive = false;
            req.Proxy = null;
            req.UserAgent = PluginInfo.UserAgent;

            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                int code = (int)resp.StatusCode;
                if (code < 200 || code >= 300)
                    throw new Exception($"HTTP {code}");

                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
        }

        private void Log(string msg) { try { OnLog?.Invoke(msg); } catch { } }
        private void NotifyState() { try { OnStateChanged?.Invoke(); } catch { } }
    }
}