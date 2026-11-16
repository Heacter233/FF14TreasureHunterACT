// DataUpdateManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;

namespace FF14TreasureHunterACT
{
    /// <summary>
    /// 地图 / 预测规则的数据更新与合并。
    /// 从服务器拉取 maps.json + predictions.json，按 id / rule_id 去重合并。
    /// 规则：本地已有的（无论是否改过）→ 保留本地；本地没有的 → 新增。
    /// </summary>
    public class DataUpdateManager
    {
        private const string RemoteBaseUrl = "https://heikadi.top/ff14ds/wbkj/ACThtml/";

        public string VersionFile { get; private set; }
        public string LocalVersion { get; private set; } = "";
        public string RemoteVersion { get; private set; } = "";
        public string LastError { get; private set; } = "";
        public bool IsUpdating { get; private set; } = false;

        public bool UpdateAvailable =>
            !string.IsNullOrEmpty(RemoteVersion) && RemoteVersion != LocalVersion;

        public event Action<string> OnLog;
        public event Action OnStateChanged;

        public DataUpdateManager(string dataDir)
        {
            try { Directory.CreateDirectory(dataDir); } catch { }
            VersionFile = Path.Combine(dataDir, "data_version.json");
            LoadLocalVersion();
        }

        private void LoadLocalVersion()
        {
            try
            {
                if (File.Exists(VersionFile))
                {
                    var obj = JObject.Parse(File.ReadAllText(VersionFile, Encoding.UTF8));
                    LocalVersion = obj["data_version"]?.ToString() ?? "";
                }
            }
            catch { }
        }

        private void SaveLocalVersion(string version)
        {
            try
            {
                var obj = new JObject
                {
                    ["data_version"] = version,
                    ["last_check"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                    ["source_url"] = RemoteBaseUrl
                };
                File.WriteAllText(VersionFile, obj.ToString(Newtonsoft.Json.Formatting.Indented), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log("[数据] 保存本地版本失败: " + ex.Message);
            }
        }

        // ============================================================
        // 检查远端版本（只读版本号，不下载数据）
        // ============================================================
        public void CheckRemoteAsync()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string json = HttpGet(RemoteBaseUrl + "version.json", 15000);
                    var obj = JObject.Parse(json);
                    // 兼容：优先 data_version，回退到 data_version 字段
                    RemoteVersion = obj["data_version"]?.ToString();
                    if (string.IsNullOrEmpty(RemoteVersion))
                        RemoteVersion = obj["dataVersion"]?.ToString() ?? "";

                    LastError = "";
                    Log($"[数据] 检查完成：本地 {LocalVersion} / 远程 {RemoteVersion}");
                    NotifyState();
                }
                catch (Exception ex)
                {
                    LastError = "检查失败：" + ex.Message;
                    Log("[数据] " + LastError);
                    NotifyState();
                }
            });
        }

        // ============================================================
        // 合并远端数据到本地（用户优先）
        // ============================================================
        public void MergeAsync(MapConfigManager mapConfig, PredictionManager predMgr)
        {
            if (IsUpdating) return;
            IsUpdating = true;
            LastError = "";
            NotifyState();

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // 1. 拉取远端 maps.json
                    string mapsJson = HttpGet(RemoteBaseUrl + "maps.json", 20000);
                    var mapsObj = JObject.Parse(mapsJson);
                    var remoteMaps = mapsObj["maps"] as JArray ?? new JArray();

                    // 2. 拉取远端 predictions.json
                    string predsJson = HttpGet(RemoteBaseUrl + "predictions.json", 20000);
                    var predsObj = JObject.Parse(predsJson);
                    var remotePreds = predsObj["predictions"] as JArray ?? new JArray();

                    // 3. 合并地图
                    int mapAdded = 0, mapSkipped = 0;
                    foreach (var m in remoteMaps)
                    {
                        string id = m["id"]?.ToString() ?? "";
                        string name = m["name"]?.ToString() ?? "";

                        // 去重键优先 id，其次 name
                        MapConfig existing = null;
                        if (!string.IsNullOrEmpty(id))
                            existing = mapConfig.GetMapById(id);
                        if (existing == null && !string.IsNullOrEmpty(name))
                            existing = mapConfig.GetMapByName(name);

                        if (existing != null)
                        {
                            mapSkipped++;
                            Log($"[数据] 跳过地图 {name}（本地已存在）");
                            continue;
                        }

                        // 新增
                        if (mapConfig.SaveMapFromJson((JObject)m))
                        {
                            mapAdded++;
                            Log($"[数据] 新增地图 {name}");
                        }
                    }

                    // 4. 合并预测规则
                    int predAdded = 0, predSkipped = 0;
                    foreach (var p in remotePreds)
                    {
                        string ruleId = p["rule_id"]?.ToString() ?? "";
                        string name = p["name"]?.ToString() ?? "";

                        if (string.IsNullOrEmpty(ruleId))
                        {
                            // 没有 id 的规则：跳过（无法去重）
                            Log($"[数据] 跳过预测规则 {name}（缺少 rule_id）");
                            continue;
                        }

                        var existing = predMgr.GetRuleById(ruleId);
                        if (existing != null)
                        {
                            predSkipped++;
                            Log($"[数据] 跳过预测规则 {name}（本地已存在）");
                            continue;
                        }

                        if (predMgr.SaveRuleFromJson((JObject)p))
                        {
                            predAdded++;
                            Log($"[数据] 新增预测规则 {name}");
                        }
                    }

                    // 5. 更新本地数据版本
                    // 需要重新读一次远端 version.json 获取 data_version
                    string verJson = HttpGet(RemoteBaseUrl + "version.json", 15000);
                    var verObj = JObject.Parse(verJson);
                    string newVer = verObj["data_version"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(newVer))
                    {
                        LocalVersion = newVer;
                        SaveLocalVersion(newVer);
                    }

                    Log($"[数据] 合并完成：新增地图 {mapAdded}，新增预测 {predAdded}，" +
                        $"已存在跳过 地图 {mapSkipped} / 预测 {predSkipped}");
                }
                catch (Exception ex)
                {
                    LastError = "合并失败：" + ex.Message;
                    Log("[数据] " + LastError);
                }
                finally
                {
                    IsUpdating = false;
                    NotifyState();
                }
            });
        }

        // ============================================================
        // HTTP
        // ============================================================
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