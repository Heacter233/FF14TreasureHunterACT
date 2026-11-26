// OverlayServer.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FF14TreasureHunterACT
{
    /// <summary>
    /// 悬浮窗后端服务器：HTTP（拉历史）+ WebSocket（推实时）。
    /// 单端口（默认 8765），同源避免跨域问题。
    /// </summary>
    public class OverlayServer
    {
        public int Port { get; private set; }
        public bool Running { get; private set; }

        private HttpListener _listener;
        private Thread _listenThread;
        private readonly List<WebSocket> _wsClients = new List<WebSocket>();
        private readonly object _lock = new object();

        private Database _db;
        private EventProcessor _processor;
        private SegmentManager _segmentManager;
        private StateTracker _state;
        private MapConfigManager _mapConfig;
        private List<string> _htmlSearchDirs = new List<string>();
        // 预测通知队列（Broadcast 时附带，显示后清空）
        private readonly List<object> _pendingNotifications = new List<object>();
        private readonly object _notifyLock = new object();
        private PredictionManager _predictionManager;
        private RulesManager _rulesManager;
        private string _webDir = "";
        // 局域网模式
        public bool LanMode { get; private set; } = false;
        public string LanToken { get; private set; } = "";
        public string LanAddress { get; private set; } = "";

        public OverlayServer(int port)
        {
            Port = port;
        }

        public void Bind(Database db, EventProcessor processor, StateTracker state,
                          SegmentManager segMgr, MapConfigManager mapConfig,
                          PredictionManager predMgr, RulesManager rulesMgr,
                          List<string> htmlSearchDirs, string webDir)
        {
            _db = db;
            _processor = processor;
            _state = state;
            _segmentManager = segMgr;
            _mapConfig = mapConfig;
            _predictionManager = predMgr;
            _rulesManager = rulesMgr;
            _htmlSearchDirs = htmlSearchDirs ?? new List<string>();
            _webDir = webDir ?? "";
        }

        public void Start()
        {
            StartInternal(bindAll: false);
        }

        private void StartInternal(bool bindAll)
        {
            if (Running) return;
            try
            {
                _listener = new HttpListener();
                if (bindAll)
                    _listener.Prefixes.Add($"http://+:{Port}/");
                else
                    _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");

                _listener.Start();
                Running = true;

                _listenThread = new Thread(ListenLoop) { IsBackground = true };
                _listenThread.Start();

                System.Diagnostics.Debug.WriteLine($"[Overlay] 已启动 (bindAll={bindAll}): 端口 {Port}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Overlay] 启动失败: {ex.Message}");
                Running = false;
            }
        }

        // ============================================================
        // 局域网访问开关
        // ============================================================

        public bool EnableLan()
        {
            if (LanMode) return true;
            try
            {
                LanToken = GenerateToken(6);
                LanAddress = GetLocalIPv4();

                Stop();
                System.Threading.Thread.Sleep(300);   // 让端口释放
                StartInternal(bindAll: true);

                if (!Running)
                {
                    LanMode = false;
                    LanToken = "";
                    StartInternal(bindAll: false);
                    return false;
                }

                LanMode = true;
                System.Diagnostics.Debug.WriteLine($"[Overlay] 局域网模式已启用 {LanAddress}:{Port} token={LanToken}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Overlay] 启用局域网失败: {ex.Message}");
                LanMode = false;
                LanToken = "";
                try { StartInternal(bindAll: false); } catch { }
                return false;
            }
        }

        public bool DisableLan()
        {
            if (!LanMode) return true;
            try
            {
                LanMode = false;
                LanToken = "";

                Stop();
                System.Threading.Thread.Sleep(300);
                StartInternal(bindAll: false);
                return Running;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Overlay] 关闭局域网失败: {ex.Message}");
                return false;
            }
        }

        private static string GenerateToken(int length)
        {
            const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
            var rand = new Random(Guid.NewGuid().GetHashCode());
            var sb = new StringBuilder();
            for (int i = 0; i < length; i++)
                sb.Append(chars[rand.Next(chars.Length)]);
            return sb.ToString();
        }

        private static string GetLocalIPv4()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string s = addr.Address.ToString();
                            if (s.StartsWith("169.254.")) continue;   // APIPA，跳过
                            return s;
                        }
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        /// <summary>生成局域网访问 URL（含 token）</summary>
        public string BuildLanUrl(string fileName)
        {
            if (!LanMode || string.IsNullOrEmpty(LanToken)) return "";
            string addr = string.IsNullOrEmpty(LanAddress) ? "127.0.0.1" : LanAddress;
            return $"http://{addr}:{Port}/{fileName}?t={LanToken}";
        }

        public void Stop()
        {
            Running = false;
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;

            // 等待监听线程结束，避免端口未释放
            try
            {
                if (_listenThread != null && _listenThread.IsAlive)
                    _listenThread.Join(1500);
            }
            catch { }
            _listenThread = null;

            lock (_lock)
            {
                foreach (var ws in _wsClients)
                {
                    try { ws.Dispose(); } catch { }
                }
                _wsClients.Clear();
            }
        }

        private void ListenLoop()
        {
            while (Running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }

                ThreadPool.QueueUserWorkItem(_ => HandleContext(ctx));
            }
        }

        private void HandleContext(object state)
        {
            var ctx = (HttpListenerContext)state;
            try
            {
                // ---------- token 校验（局域网模式） ----------
                if (!CheckAuth(ctx))
                {
                    try
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(
                            "Unauthorized: 需要正确的访问令牌。请扫描最新二维码。");
                        ctx.Response.StatusCode = 401;
                        ctx.Response.ContentType = "text/plain; charset=utf-8";
                        ctx.Response.ContentLength64 = bytes.Length;
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    }
                    catch { }
                    return;
                }

                string path = ctx.Request.Url.AbsolutePath;

                if (path == "/ws" && ctx.Request.IsWebSocketRequest)
                {
                    HandleWebSocket(ctx);
                    return;
                }

                // ---------- CORS 预检请求 ----------
                if (ctx.Request.HttpMethod == "OPTIONS")
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
                    ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                    ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                    return;
                }

                // ---------- 通用 HTML 文件服务 ----------
                // 优先级：webDir（%APPDATA%\FF14TreasureHunter\web\）> HTML 搜索目录（DLL 同目录等）
                if (path == "/" || path == "/index.html" || path.EndsWith(".html"))
                {
                    string fileName;
                    if (path == "/" || path == "/index.html")
                        fileName = "overlay.html";
                    else
                        fileName = path.TrimStart('/');

                    string html = null;

                    // 1. 优先从 webDir 读取（在线更新的主目录）
                    if (!string.IsNullOrEmpty(_webDir))
                    {
                        try
                        {
                            string candidate = Path.Combine(_webDir, fileName);
                            if (File.Exists(candidate))
                                html = File.ReadAllText(candidate, Encoding.UTF8);
                        }
                        catch { }
                    }

                    // 2. 兜底：从 DLL 同目录等候选目录
                    if (html == null)
                    {
                        foreach (var dir in _htmlSearchDirs)
                        {
                            if (string.IsNullOrEmpty(dir)) continue;
                            try
                            {
                                string candidate = Path.Combine(dir, fileName);
                                if (File.Exists(candidate))
                                {
                                    html = File.ReadAllText(candidate, Encoding.UTF8);
                                    break;
                                }
                            }
                            catch { }
                        }
                    }

                    if (html != null)
                    {
                        // 注入 token（本地也注入，方便前端统一处理）
                        if (!string.IsNullOrEmpty(LanToken))
                        {
                            string inject = "<script>window.__LAN_TOKEN__='" + LanToken + "';</script>";
                            if (html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase) >= 0)
                                html = System.Text.RegularExpressions.Regex.Replace(
                                    html, "</head>", inject + "</head>",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            else
                                html = inject + html;
                        }

                        byte[] bytes = Encoding.UTF8.GetBytes(html);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "text/html; charset=utf-8";
                        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
                        ctx.Response.Headers["Cache-Control"] = "no-cache, no-store";
                        ctx.Response.ContentLength64 = bytes.Length;
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        return;
                    }

                    string msg = "HTML 文件未找到: " + fileName + "\n\n搜索路径：\n";
                    if (!string.IsNullOrEmpty(_webDir))
                        msg += "  - " + _webDir + "\n";
                    foreach (var dir in _htmlSearchDirs)
                        msg += "  - " + dir + "\n";
                    ServeText(ctx, 404, msg);
                    return;
                }

                // ---------- API 路由 ----------
                switch (path)
                {
                    // ---------- 实时数据 ----------
                    case "/api/summary":
                        ServeJson(ctx, BuildSummaryJson());
                        break;

                    // ---------- 历史 ----------
                    case "/api/history/dates":
                        ServeJson(ctx, BuildDatesJson());
                        break;
                    case "/api/history/timeline":
                        ServeJson(ctx, BuildTimelineJson(ctx.Request.QueryString["date"]));
                        break;
                    case "/api/history/loot":
                        ServeJson(ctx, BuildLootByDateJson(ctx.Request.QueryString["date"]));
                        break;
                    case "/api/history/layers":
                        ServeJson(ctx, BuildLayersByDateJson(ctx.Request.QueryString["date"]));
                        break;
                    case "/api/history/meta":
                        ServeJson(ctx, BuildHistoryMetaJson());
                        break;
                    case "/api/history/overview":
                        ServeJson(ctx, BuildHistoryOverviewJson(BuildHistoryQueryDict(ctx)));
                        break;
                    case "/api/history/pivot":
                        ServeJson(ctx, BuildHistoryPivotJson(BuildHistoryQueryDict(ctx)));
                        break;
                    case "/api/history/detail":
                        ServeJson(ctx, BuildHistoryDetailJson(BuildHistoryQueryDict(ctx)));
                        break;

                    // ---------- 配置 ----------
                    case "/api/config/all":
                        ServeJson(ctx, ConfigManager.GetAllAsJObject().ToString(Formatting.None));
                        break;
                    case "/api/config/set":
                        HandleConfigSet(ctx);
                        break;
                    case "/api/config/batch-set":
                        HandleConfigBatchSet(ctx);
                        break;
                    case "/api/config/reload":
                        HandleConfigReload(ctx);
                        break;
                    // 内置默认值：供设置页"恢复默认"按钮使用（避免前端硬编码中文关键词）
                    case "/api/config/defaults":
                        ServeJson(ctx, ConfigDefaults.Create().ToString(Formatting.None));
                        break;

                    // ---------- 地图 ----------
                    case "/api/maps/all":
                        ServeJson(ctx, _mapConfig?.GetAllAsJson() ?? "{\"maps\":[]}");
                        break;
                    case "/api/maps/save":
                        HandleMapSave(ctx);
                        break;
                    case "/api/maps/delete":
                        HandleMapDelete(ctx);
                        break;

                    // ---------- 预测规则 ----------
                    case "/api/predictions/all":
                        ServeJson(ctx, _predictionManager?.GetAllAsJson() ?? "{\"predictions\":[]}");
                        break;
                    case "/api/predictions/save":
                        HandlePredictionSave(ctx);
                        break;
                    case "/api/predictions/delete":
                        HandlePredictionDelete(ctx);
                        break;

                    // ---------- 触发器规则 ----------
                    case "/api/rules/all":
                        ServeJson(ctx, _rulesManager?.GetAllAsJson() ?? "{\"rules\":[]}");
                        break;
                    case "/api/rules/save":
                        HandleRuleSave(ctx);
                        break;
                    case "/api/rules/delete":
                        HandleRuleDelete(ctx);
                        break;

                    // ---------- 发布管理 ----------
                    case "/api/publish/preview":
                        HandlePublishPreview(ctx);
                        break;
                    case "/api/publish/html-version":
                        ServeJson(ctx, "{\"html_version\":\"" +
                            (HtmlUpdateManager.ReadLocalHtmlVersion(_webDir) ?? "")
                            .Replace("\"", "'") + "\"}");
                        break;

                    default:
                        System.Diagnostics.Debug.WriteLine($"[Overlay] 未匹配路径: {path}");
                        ServeText(ctx, 404, "Not Found: " + path);
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Overlay] 请求处理失败: {ex.Message}");
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }

        private void HandleWebSocket(HttpListenerContext ctx)
        {
            WebSocket ws = null;
            try
            {
                var wsContext = ctx.AcceptWebSocketAsync(null).Result;
                ws = wsContext.WebSocket;
                lock (_lock) { _wsClients.Add(ws); }

                // 立即推一次
                try
                {
                    string json = BuildSummaryJsonWithNotifications(); var bytes = Encoding.UTF8.GetBytes(json);
                    ws.SendAsync(new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text, true, CancellationToken.None)
                        .Wait(1000);
                }
                catch { }

                // 保持监听，直到客户端断开
                var buffer = new byte[1024];
                while (ws.State == WebSocketState.Open && Running)
                {
                    try
                    {
                        var result = ws.ReceiveAsync(new ArraySegment<byte>(buffer),
                            CancellationToken.None).Result;
                        if (result.MessageType == WebSocketMessageType.Close) break;
                    }
                    catch { break; }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Overlay] WS 失败: {ex.Message}");
            }
            finally
            {
                if (ws != null)
                {
                    lock (_lock) { _wsClients.Remove(ws); }
                    try { ws.Dispose(); } catch { }
                }
            }
        }


        /// <summary>推送一条预测通知到悬浮窗。type: "success" / "fail" / "info"</summary>
        public void PushPredictionNotification(string type, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_notifyLock)
            {
                _pendingNotifications.Add(new
                {
                    type = type ?? "info",
                    text = text,
                    timestamp = DateTime.Now.ToString("HH:mm:ss")
                });
                // 队列上限，防止离线时堆积
                if (_pendingNotifications.Count > 20)
                    _pendingNotifications.RemoveAt(0);
            }
        }

        /// <summary>由 MainPanel 每秒调用一次，广播最新数据。</summary>
        public void BroadcastSummary()
        {
            List<WebSocket> clients;
            lock (_lock) { clients = new List<WebSocket>(_wsClients); }
            if (clients.Count == 0) return;

            string json;
            try { json = BuildSummaryJsonWithNotifications(); } catch { return; }

            var bytes = Encoding.UTF8.GetBytes(json);
            List<WebSocket> dead = null;

            foreach (var ws in clients)
            {
                if (ws.State != WebSocketState.Open) continue;
                try
                {
                    ws.SendAsync(new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text, true, CancellationToken.None)
                        .Wait(800);
                }
                catch
                {
                    (dead ?? (dead = new List<WebSocket>())).Add(ws);
                }
            }

            if (dead != null)
            {
                lock (_lock)
                {
                    foreach (var ws in dead) _wsClients.Remove(ws);
                }
            }
        }

        // ============================================================
        // JSON 构建
        // ============================================================
        /// <summary>在 summary JSON 里附带待推送的通知队列</summary>
        private string BuildSummaryJsonWithNotifications()
        {
            string baseJson = BuildSummaryJson();
            try
            {
                var obj = JObject.Parse(baseJson);

                var arr = new JArray();
                lock (_notifyLock)
                {
                    foreach (var n in _pendingNotifications)
                        arr.Add(JObject.FromObject(n));
                    _pendingNotifications.Clear();
                }
                obj["notifications"] = arr;
                return obj.ToString(Formatting.None);
            }
            catch
            {
                return baseJson;
            }
        }


        private string BuildSummaryJson()
        {
            if (_db == null || _processor == null) return "{}";

            string runLabel = _processor.CurrentRunLabel;
            var data = _segmentManager.GetCurrentSegmentData(_db, runLabel);

            // 根据配置决定是否隐藏角色名
            bool showPlayer = ConfigManager.GetBool("ui.show_player_name", false);
            string playerName = showPlayer ? (_state?.SelfName ?? "") : "挖宝小助手";
            string playerWorld = showPlayer ? (_state?.SelfWorld ?? "") : "隐";

            var obj = new JObject
            {
                ["run_label"] = runLabel,
                ["maps"] = data.Maps,
                ["magic_seals"] = data.MagicSeals,
                ["system_gold"] = data.SystemGold,
                // 收入/成本/盈亏 用分段数据（当前段 = 全量 - 已存档快照）
                ["total_income"] = data.TotalIncome,
                ["total_cost"] = data.TotalCost,
                ["profit"] = data.Profit,
                ["segments"] = _segmentManager.Snapshots.Count,
                ["player"] = playerName,
                ["world"] = playerWorld,
                ["map"] = _state?.CurrentMap ?? "",
                ["party_count"] = _state?.Party.Count ?? 0
            };

            var itemsArr = new JArray();
            if (data.Items != null)
            {
                foreach (var it in data.Items)
                    itemsArr.Add(new JObject
                    {
                        ["map"] = GetItemMapShort(it.ItemName),
                        ["name"] = it.ItemName,
                        ["qty"] = it.Quantity
                    });
            }
            obj["items"] = itemsArr;

            var layersArr = new JArray();
            if (data.Layers != null)
                foreach (var ly in data.Layers)
                    layersArr.Add(new JObject
                    {
                        ["name"] = ly.LayerName,
                        ["count"] = ly.Count
                    });
            obj["layers"] = layersArr;

            var customsArr = new JArray();
            if (data.Customs != null)
                foreach (var c in data.Customs)
                    customsArr.Add(new JObject
                    {
                        ["name"] = c.RecordName,
                        ["total"] = c.Total
                    });
            obj["customs"] = customsArr;

            return obj.ToString(Formatting.None);
        }

        private string BuildDatesJson()
        {
            var arr = new JArray();
            foreach (var d in _db.GetAvailableDates()) arr.Add(d);
            return new JObject { ["dates"] = arr }.ToString(Formatting.None);
        }

        private string BuildTimelineJson(string date)
        {
            if (string.IsNullOrEmpty(date)) date = DateTime.Now.ToString("yyyy-MM-dd");
            var timeline = _db.GetDailyTimeline(date);
            var arr = new JArray();
            foreach (var t in timeline)
                arr.Add(new JObject
                {
                    ["type"] = t.Type,
                    ["name"] = t.Name,
                    ["value"] = t.Value,
                    ["timestamp"] = t.Timestamp,
                    ["vault_map"] = t.VaultMap
                });
            return new JObject { ["date"] = date, ["timeline"] = arr }
                .ToString(Formatting.None);
        }

        private string BuildLootByDateJson(string date)
        {
            if (string.IsNullOrEmpty(date)) date = DateTime.Now.ToString("yyyy-MM-dd");
            var arr = new JArray();
            // 直接读当日时间线，筛选 loot
            var timeline = _db.GetDailyTimeline(date);
            foreach (var t in timeline)
            {
                if (t.Type != "loot") continue;
                arr.Add(new JObject
                {
                    ["name"] = t.Name,
                    ["qty"] = t.Value,
                    ["vault"] = t.VaultMap
                });
            }
            return new JObject { ["date"] = date, ["items"] = arr }
                .ToString(Formatting.None);
        }

        private string BuildLayersByDateJson(string date)
        {
            if (string.IsNullOrEmpty(date)) date = DateTime.Now.ToString("yyyy-MM-dd");
            var arr = new JArray();
            var timeline = _db.GetDailyTimeline(date);
            foreach (var t in timeline)
            {
                if (t.Type != "layer" && t.Type != "custom") continue;
                arr.Add(new JObject
                {
                    ["type"] = t.Type,
                    ["name"] = t.Name,
                    ["value"] = t.Value
                });
            }
            return new JObject { ["date"] = date, ["records"] = arr }
                .ToString(Formatting.None);
        }

        // ============================================================
        // 历史分析 API（meta / overview / pivot / detail）
        // 设计：底层只查“按日期/小时/车次/物品/层级/宝物库聚合的事实”，
        // 上层在内存里做任意维度透视，避免为每种行列组合各写一套 SQL。
        // ============================================================

        private struct HistoryItemInfo
        {
            public double Price;
            public double Bonus;
            public string MapName;
            public string MapDisplay;
        }

        private sealed class HistoryDayAgg
        {
            public int Maps;
            public int Seals;
            public int Layers;
            public int Events;
            public int Vaults;
            public double LootQty;
            public double LootValue;
            public double Gold;
        }

        private sealed class HistoryMapAgg
        {
            public double Qty;
            public double Value;
            public double Maps;
            public double Seals;
            public double Layers;
            public readonly HashSet<string> Runs = new HashSet<string>(StringComparer.Ordinal);
        }

        private static readonly string[] HistoryWeekdays =
            { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

        private static string HistoryQuery(HttpListenerContext ctx, string key, string def = "")
        {
            try
            {
                string v = ctx.Request.QueryString[key];
                return string.IsNullOrEmpty(v) ? def : v.Trim();
            }
            catch { return def; }
        }

        private static Dictionary<string, string> BuildHistoryQueryDict(HttpListenerContext ctx)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string key in ctx.Request.QueryString.AllKeys)
                {
                    if (string.IsNullOrEmpty(key)) continue;
                    dict[key] = ctx.Request.QueryString[key];
                }
            }
            catch { }
            return dict;
        }

        private static string HistoryQ(Dictionary<string, string> q, string key, string def = "")
        {
            string v;
            if (q != null && q.TryGetValue(key, out v) && !string.IsNullOrEmpty(v))
                return v.Trim();
            return def;
        }

        private static List<string> SplitHistoryList(string raw)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (var part in raw.Split(','))
            {
                string s = part.Trim();
                if (!string.IsNullOrEmpty(s) && !list.Contains(s)) list.Add(s);
            }
            return list;
        }

        private static DateTime? ParseHistoryDate(string s)
        {
            DateTime d;
            if (DateTime.TryParseExact(s ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out d))
                return d;
            return null;
        }

        private static string MondayOf(string date)
        {
            var d = ParseHistoryDate(date);
            if (!d.HasValue) return date ?? "";
            int offset = ((int)d.Value.DayOfWeek + 6) % 7;   // 周一 = 0
            return d.Value.AddDays(-offset).ToString("yyyy-MM-dd");
        }

        private static int HistoryWeekdayIndex(string date)
        {
            var d = ParseHistoryDate(date);
            if (!d.HasValue) return -1;
            return ((int)d.Value.DayOfWeek + 6) % 7;
        }

        private static double HistorySecondsBetween(string a, string b)
        {
            DateTime da, db;
            if (!DateTime.TryParse(a ?? "", CultureInfo.InvariantCulture, DateTimeStyles.None, out da))
                return 0;
            if (!DateTime.TryParse(b ?? "", CultureInfo.InvariantCulture, DateTimeStyles.None, out db))
                return 0;
            double sec = (db - da).TotalSeconds;
            return sec > 0 ? sec : 0;
        }

        private void NormalizeHistoryRange(ref string from, ref string to)
        {
            var dates = _db != null ? _db.GetAvailableDates() : new List<string>();
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            {
                if (dates.Count > 0)
                {
                    if (string.IsNullOrEmpty(to)) to = dates[0];
                    if (string.IsNullOrEmpty(from)) from = dates[dates.Count - 1];
                }
                else
                {
                    if (string.IsNullOrEmpty(from)) from = today;
                    if (string.IsNullOrEmpty(to)) to = today;
                }
            }
            if (!ParseHistoryDate(from).HasValue) from = today;
            if (!ParseHistoryDate(to).HasValue) to = today;
            if (string.CompareOrdinal(from, to) > 0)
            {
                string tmp = from; from = to; to = tmp;
            }
        }

        private Dictionary<string, HistoryItemInfo> BuildHistoryPriceMap()
        {
            var dict = new Dictionary<string, HistoryItemInfo>(StringComparer.Ordinal);
            if (_mapConfig == null) return dict;
            foreach (var mc in _mapConfig.Maps.Values)
            {
                foreach (var it in mc.Items)
                {
                    if (string.IsNullOrEmpty(it.Name)) continue;
                    HistoryItemInfo exist;
                    bool known = dict.TryGetValue(it.Name, out exist);
                    if (known && !mc.Enabled) continue;      // 已启用地图优先
                    dict[it.Name] = new HistoryItemInfo
                    {
                        Price = it.Price,
                        Bonus = it.Bonus,
                        MapName = mc.Name,
                        MapDisplay = mc.MapName
                    };
                }
            }
            return dict;
        }

        /// <summary>把一次运行归到唯一地图类型（用于给地图完成/魔纹等无物品事件归类）。</summary>
        private static Dictionary<string, string> BuildHistoryRunMap(
            Dictionary<string, HistoryItemInfo> prices, List<HistoryFact> facts)
        {
            var sets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var f in facts)
            {
                if (f.Type != "loot") continue;
                HistoryItemInfo info;
                if (!prices.TryGetValue(f.ItemName, out info) || string.IsNullOrEmpty(info.MapName))
                    continue;
                HashSet<string> set;
                if (!sets.TryGetValue(f.RunLabel, out set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    sets[f.RunLabel] = set;
                }
                set.Add(info.MapName);
            }
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in sets)
                result[kv.Key] = kv.Value.Count == 1 ? kv.Value.First() : "混合";
            return result;
        }

        private static string NormalizeHistoryDim(string dim)
        {
            switch ((dim ?? "").ToLowerInvariant())
            {
                case "date":
                case "week":
                case "month":
                case "run":
                case "item":
                case "map":
                case "layer":
                case "hour":
                case "weekday":
                case "vault":
                case "zone":
                case "custom":
                    return dim.ToLowerInvariant();
                default:
                    return "date";
            }
        }

        private static string NormalizeHistoryMeasure(string m)
        {
            switch ((m ?? "").ToLowerInvariant())
            {
                case "qty":
                case "value":
                case "events":
                case "maps":
                case "seals":
                case "layers":
                case "gold":
                case "custom":
                case "loot_rows":
                    return m.ToLowerInvariant();
                default:
                    return "qty";
            }
        }

        private static string HistoryDimKey(HistoryFact f, string dim,
            Dictionary<string, HistoryItemInfo> prices, Dictionary<string, string> runMap)
        {
            switch (dim)
            {
                case "date": return f.Date;
                case "week": return MondayOf(f.Date);
                case "month":
                    return string.IsNullOrEmpty(f.Date) ? "" :
                        f.Date.Substring(0, Math.Min(7, f.Date.Length));
                case "run": return f.RunLabel;
                case "item": return f.Type == "loot" ? f.ItemName : "";
                case "layer": return f.Type == "layer" ? f.LayerName : "";
                case "hour": return f.Hour;
                case "weekday":
                    {
                        int idx = HistoryWeekdayIndex(f.Date);
                        return idx >= 0 ? HistoryWeekdays[idx] : "";
                    }
                case "vault": return f.VaultMap;
                case "zone": return f.Zone;
                case "custom": return f.Type == "custom" ? f.CustomName : "";
                case "map":
                    {
                        if (f.Type == "loot")
                        {
                            HistoryItemInfo info;
                            if (!string.IsNullOrEmpty(f.ItemName) &&
                                prices.TryGetValue(f.ItemName, out info) &&
                                !string.IsNullOrEmpty(info.MapName))
                                return info.MapName;
                            return "未知";
                        }
                        string mapped;
                        if (runMap.TryGetValue(f.RunLabel, out mapped)) return mapped;
                        return "未标注";
                    }
                default: return f.Date;
            }
        }

        private static string HistoryDimLabel(string dim)
        {
            switch (dim)
            {
                case "date": return "日期";
                case "week": return "周";
                case "month": return "月份";
                case "run": return "车次";
                case "item": return "物品";
                case "map": return "地图类型";
                case "layer": return "层级";
                case "hour": return "小时";
                case "weekday": return "星期";
                case "vault": return "宝物库";
                case "zone": return "区域";
                case "custom": return "其他记录";
                default: return dim;
            }
        }

        private static string HistoryMeasureLabel(string m)
        {
            switch (m)
            {
                case "qty": return "物品数量";
                case "value": return "物品价值";
                case "events": return "事件数";
                case "maps": return "地图完成数";
                case "seals": return "魔纹数";
                case "layers": return "层级次数";
                case "gold": return "系统金";
                case "custom": return "其他记录值";
                case "loot_rows": return "出货次数";
                default: return m;
            }
        }

        private static double HistoryMeasureValue(HistoryFact f, string measure,
            Dictionary<string, HistoryItemInfo> prices)
        {
            switch (measure)
            {
                case "qty": return f.Type == "loot" ? f.Quantity : 0;
                case "value":
                    {
                        if (f.Type != "loot") return 0;
                        HistoryItemInfo info;
                        return prices.TryGetValue(f.ItemName, out info)
                            ? f.Quantity * info.Price : 0;
                    }
                case "events": return f.Records;
                case "maps": return f.Type == "map_complete" ? f.Quantity : 0;
                case "seals": return f.Type == "magic_seal" ? f.Quantity : 0;
                case "layers": return f.Type == "layer" ? f.Quantity : 0;
                case "gold": return f.Type == "system_gold" ? f.Quantity : 0;
                case "custom": return f.Type == "custom" ? f.Quantity : 0;
                case "loot_rows": return f.Type == "loot" ? f.Records : 0;
                default: return 0;
            }
        }

        private static List<string> OrderHistoryDimKeys(
            IEnumerable<string> keys, string dim, Dictionary<string, double> totals)
        {
            if (dim == "weekday")
            {
                return keys.OrderBy(k =>
                {
                    int i = Array.IndexOf(HistoryWeekdays, k);
                    return i < 0 ? 99 : i;
                }).ToList();
            }
            if (dim == "hour" || dim == "date" || dim == "week" || dim == "month")
                return keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (dim == "run")
                return keys.OrderByDescending(k => k, StringComparer.Ordinal).ToList();
            return keys
                .OrderByDescending(k =>
                {
                    double v;
                    return totals.TryGetValue(k, out v) ? v : 0;
                })
                .ThenBy(k => k, StringComparer.Ordinal)
                .ToList();
        }

        // ---------- meta ----------
        private string BuildHistoryMetaJson()
        {
            if (_db == null) return "{}";
            var dates = _db.GetAvailableDates();
            string from = dates.Count > 0 ? dates[dates.Count - 1] : DateTime.Now.ToString("yyyy-MM-dd");
            string to = dates.Count > 0 ? dates[0] : from;

            var itemsArr = new JArray();
            var seenItems = new HashSet<string>(StringComparer.Ordinal);
            var mapsArr = new JArray();
            if (_mapConfig != null)
            {
                foreach (var mc in _mapConfig.Maps.Values)
                {
                    var itemNames = new JArray();
                    foreach (var it in mc.Items)
                    {
                        if (string.IsNullOrEmpty(it.Name)) continue;
                        itemNames.Add(it.Name);
                        if (!seenItems.Add(it.Name)) continue;
                        itemsArr.Add(new JObject
                        {
                            ["name"] = it.Name,
                            ["price"] = it.Price,
                            ["bonus"] = it.Bonus,
                            ["map"] = mc.Name,
                            ["map_name"] = mc.MapName
                        });
                    }
                    mapsArr.Add(new JObject
                    {
                        ["name"] = mc.Name,
                        ["map_name"] = mc.MapName,
                        ["prefix"] = mc.MapItemPrefix,
                        ["enabled"] = mc.Enabled,
                        ["items"] = itemNames
                    });
                }
            }
            foreach (var name in _db.GetDistinctLootItems())
            {
                if (!seenItems.Add(name)) continue;
                itemsArr.Add(new JObject
                {
                    ["name"] = name, ["price"] = 0, ["bonus"] = 0,
                    ["map"] = "", ["map_name"] = ""
                });
            }

            var layerSeen = new HashSet<string>(StringComparer.Ordinal);
            var layersArr = new JArray();
            if (_mapConfig != null)
            {
                foreach (var mc in _mapConfig.Maps.Values)
                    foreach (var ly in mc.Layers)
                    {
                        string label = string.IsNullOrEmpty(ly.Nickname) ? ly.Keyword : ly.Nickname;
                        if (!string.IsNullOrEmpty(label) && layerSeen.Add(label))
                            layersArr.Add(label);
                    }
            }
            foreach (var name in _db.GetDistinctLayerNames())
                if (layerSeen.Add(name)) layersArr.Add(name);

            var runsArr = new JArray();
            foreach (var r in _db.GetHistoryRuns(from, to))
            {
                runsArr.Add(new JObject
                {
                    ["label"] = r.Label,
                    ["date"] = r.FirstTs.Length >= 10 ? r.FirstTs.Substring(0, 10) : "",
                    ["first_ts"] = r.FirstTs,
                    ["last_ts"] = r.LastTs,
                    ["maps"] = r.Maps,
                    ["seals"] = r.Seals,
                    ["gold"] = r.Gold,
                    ["events"] = r.Events,
                    ["cost"] = r.Cost,
                    ["elapsed"] = r.Elapsed,
                    ["has_summary"] = r.HasSummary
                });
            }

            var datesArr = new JArray();
            foreach (var d in dates) datesArr.Add(d);

            return new JObject
            {
                ["dates"] = datesArr,
                ["bounds"] = new JObject { ["from"] = from, ["to"] = to },
                ["runs"] = runsArr,
                ["items"] = itemsArr,
                ["layers"] = layersArr,
                ["maps"] = mapsArr,
                ["money_unit_wan"] = ConfigManager.MoneyUnitWan,
                ["price_source"] = "maps.json"
            }.ToString(Formatting.None);
        }

        // ---------- pivot ----------
        private string BuildHistoryPivotJson(Dictionary<string, string> q)
        {
            if (_db == null) return "{}";
            string from = HistoryQ(q, "from");
            string to = HistoryQ(q, "to");
            NormalizeHistoryRange(ref from, ref to);
            var runLabels = SplitHistoryList(HistoryQ(q, "runs"));
            string row = NormalizeHistoryDim(HistoryQ(q, "row", "item"));
            string col = NormalizeHistoryDim(HistoryQ(q, "col", "date"));
            string measure = NormalizeHistoryMeasure(HistoryQ(q, "measure", "qty"));

            var facts = _db.GetHistoryFacts(from, to, runLabels);
            var prices = BuildHistoryPriceMap();
            var runMap = BuildHistoryRunMap(prices, facts);

            var rowTotals = new Dictionary<string, double>(StringComparer.Ordinal);
            var colTotals = new Dictionary<string, double>(StringComparer.Ordinal);
            var cellValues = new Dictionary<string, double>(StringComparer.Ordinal);
            double grand = 0;

            foreach (var f in facts)
            {
                string rk = HistoryDimKey(f, row, prices, runMap);
                string ck = HistoryDimKey(f, col, prices, runMap);
                if (string.IsNullOrEmpty(rk) || string.IsNullOrEmpty(ck)) continue;
                double v = HistoryMeasureValue(f, measure, prices);
                if (Math.Abs(v) < 1e-9) continue;

                double cur;
                string key = rk + "\u0001" + ck;
                cellValues.TryGetValue(key, out cur);
                cellValues[key] = cur + v;

                rowTotals.TryGetValue(rk, out cur);
                rowTotals[rk] = cur + v;
                colTotals.TryGetValue(ck, out cur);
                colTotals[ck] = cur + v;
                grand += v;
            }

            var rows = OrderHistoryDimKeys(rowTotals.Keys, row, rowTotals);
            var cols = OrderHistoryDimKeys(colTotals.Keys, col, colTotals);
            const int cap = 500;
            bool truncated = rows.Count > cap || cols.Count > cap;
            if (rows.Count > cap) rows = rows.Take(cap).ToList();
            if (cols.Count > cap) cols = cols.Take(cap).ToList();

            var rIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < rows.Count; i++) rIndex[rows[i]] = i;
            var cIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < cols.Count; i++) cIndex[cols[i]] = i;

            var cells = new JArray();
            foreach (var kv in cellValues)
            {
                string[] parts = kv.Key.Split('\u0001');
                if (parts.Length != 2) continue;
                int ri, ci;
                if (!rIndex.TryGetValue(parts[0], out ri)) continue;
                if (!cIndex.TryGetValue(parts[1], out ci)) continue;
                cells.Add(new JObject { ["r"] = ri, ["c"] = ci, ["v"] = kv.Value });
            }

            var rowsArr = new JArray();
            var rowTotalsArr = new JArray();
            foreach (var k in rows)
            {
                rowsArr.Add(k);
                double v;
                rowTotals.TryGetValue(k, out v);
                rowTotalsArr.Add(v);
            }
            var colsArr = new JArray();
            var colTotalsArr = new JArray();
            foreach (var k in cols)
            {
                colsArr.Add(k);
                double v;
                colTotals.TryGetValue(k, out v);
                colTotalsArr.Add(v);
            }

            return new JObject
            {
                ["from"] = from,
                ["to"] = to,
                ["row"] = row,
                ["col"] = col,
                ["measure"] = measure,
                ["row_label"] = HistoryDimLabel(row),
                ["col_label"] = HistoryDimLabel(col),
                ["measure_label"] = HistoryMeasureLabel(measure),
                ["rows"] = rowsArr,
                ["cols"] = colsArr,
                ["cells"] = cells,
                ["row_totals"] = rowTotalsArr,
                ["col_totals"] = colTotalsArr,
                ["grand"] = grand,
                ["truncated"] = truncated
            }.ToString(Formatting.None);
        }

        // ---------- detail ----------
        private string BuildHistoryDetailJson(Dictionary<string, string> q)
        {
            if (_db == null) return "{}";
            string from = HistoryQ(q, "from");
            string to = HistoryQ(q, "to");
            NormalizeHistoryRange(ref from, ref to);
            var runLabels = SplitHistoryList(HistoryQ(q, "runs"));
            string type = HistoryQ(q, "type");
            var items = SplitHistoryList(HistoryQ(q, "items"));
            string layer = HistoryQ(q, "layer");
            string vault = HistoryQ(q, "vault");
            string hour = HistoryQ(q, "hour");
            string weekday = HistoryQ(q, "weekday");
            string zone = HistoryQ(q, "zone");
            string custom = HistoryQ(q, "custom");
            string keyword = HistoryQ(q, "q");

            int page = 1, size = 100;
            int.TryParse(HistoryQ(q, "page", "1"), out page);
            int.TryParse(HistoryQ(q, "size", "100"), out size);
            if (page < 1) page = 1;
            if (size < 1) size = 100;
            if (size > 5000) size = 5000;

            int total;
            var rows = _db.GetHistoryDetail(from, to, runLabels, type, items, layer,
                vault, hour, weekday, zone, custom, keyword, page, size, out total);
            var prices = BuildHistoryPriceMap();

            var arr = new JArray();
            foreach (var r in rows)
            {
                double price = 0, value = 0;
                HistoryItemInfo info;
                if (r.Type == "loot" && prices.TryGetValue(r.Name, out info))
                {
                    price = info.Price;
                    value = r.Qty * info.Price;
                }
                string date = r.Ts.Length >= 10 ? r.Ts.Substring(0, 10) : r.Ts;
                string time = r.Ts.Length >= 19 ? r.Ts.Substring(11, 8) : "";
                arr.Add(new JObject
                {
                    ["ts"] = r.Ts,
                    ["date"] = date,
                    ["time"] = time,
                    ["run"] = r.RunLabel,
                    ["type"] = r.Type,
                    ["name"] = r.Name,
                    ["qty"] = r.Qty,
                    ["gold"] = r.Gold,
                    ["price"] = price,
                    ["value"] = value,
                    ["vault"] = r.Vault,
                    ["zone"] = r.Zone
                });
            }

            return new JObject
            {
                ["from"] = from,
                ["to"] = to,
                ["page"] = page,
                ["size"] = size,
                ["total"] = total,
                ["rows"] = arr
            }.ToString(Formatting.None);
        }

        // ---------- overview ----------
        private string BuildHistoryOverviewJson(Dictionary<string, string> q)
        {
            if (_db == null) return "{}";
            string from = HistoryQ(q, "from");
            string to = HistoryQ(q, "to");
            NormalizeHistoryRange(ref from, ref to);
            var runLabels = SplitHistoryList(HistoryQ(q, "runs"));

            var facts = _db.GetHistoryFacts(from, to, runLabels);
            var prices = BuildHistoryPriceMap();
            var runMap = BuildHistoryRunMap(prices, facts);

            var runRows = _db.GetHistoryRuns(from, to);
            if (runLabels.Count > 0)
            {
                var set = new HashSet<string>(runLabels, StringComparer.Ordinal);
                runRows = runRows.Where(r => set.Contains(r.Label)).ToList();
            }

            var vaults = _db.GetHistoryVaults(from, to);
            if (runLabels.Count > 0)
            {
                var windows = runRows.Select(r => new { r.FirstTs, r.LastTs }).ToList();
                vaults = vaults.Where(v =>
                    windows.Any(w =>
                        string.CompareOrdinal(v.StartTime, w.FirstTs) >= 0 &&
                        string.CompareOrdinal(v.StartTime, w.LastTs) <= 0)).ToList();
            }

            int maps = 0, seals = 0, layersCount = 0, events = 0;
            double lootQty = 0, lootValue = 0, gold = 0;
            var daily = new Dictionary<string, HistoryDayAgg>(StringComparer.Ordinal);
            var itemQty = new Dictionary<string, double>(StringComparer.Ordinal);
            var itemValue = new Dictionary<string, double>(StringComparer.Ordinal);
            var layerAgg = new Dictionary<string, double>(StringComparer.Ordinal);
            var mapAgg = new Dictionary<string, HistoryMapAgg>(StringComparer.Ordinal);
            var hourEvents = new int[24];
            var hourLoot = new double[24];
            var hourValue = new double[24];
            var weekdayEvents = new int[7];
            var weekdayLoot = new double[7];
            var weekdayValue = new double[7];

            var runLootQty = new Dictionary<string, double>(StringComparer.Ordinal);
            var runLootValue = new Dictionary<string, double>(StringComparer.Ordinal);
            var runMapsDate = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            var runFirstDate = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var f in facts)
            {
                HistoryDayAgg day;
                if (!daily.TryGetValue(f.Date, out day))
                {
                    day = new HistoryDayAgg();
                    daily[f.Date] = day;
                }
                day.Events += f.Records;
                events += f.Records;

                string first;
                if (!runFirstDate.TryGetValue(f.RunLabel, out first) ||
                    string.CompareOrdinal(f.Date, first) < 0)
                    runFirstDate[f.RunLabel] = f.Date;

                int wd = HistoryWeekdayIndex(f.Date);
                int hh = 0;
                int.TryParse(f.Hour, out hh);
                if (hh < 0 || hh > 23) hh = 0;

                switch (f.Type)
                {
                    case "loot":
                        {
                            lootQty += f.Quantity;
                            day.LootQty += f.Quantity;
                            itemQty.TryGetValue(f.ItemName, out var iq);
                            itemQty[f.ItemName] = iq + f.Quantity;

                            double val = 0;
                            HistoryItemInfo info;
                            if (prices.TryGetValue(f.ItemName, out info))
                            {
                                val = f.Quantity * info.Price;
                                string mn = string.IsNullOrEmpty(info.MapName) ? "未知" : info.MapName;
                                HistoryMapAgg ma;
                                if (!mapAgg.TryGetValue(mn, out ma))
                                {
                                    ma = new HistoryMapAgg();
                                    mapAgg[mn] = ma;
                                }
                                ma.Qty += f.Quantity;
                                ma.Value += val;
                                ma.Runs.Add(f.RunLabel);
                            }
                            lootValue += val;
                            day.LootValue += val;
                            itemValue.TryGetValue(f.ItemName, out var iv);
                            itemValue[f.ItemName] = iv + val;
                            runLootQty.TryGetValue(f.RunLabel, out var rq);
                            runLootQty[f.RunLabel] = rq + f.Quantity;
                            runLootValue.TryGetValue(f.RunLabel, out var rv);
                            runLootValue[f.RunLabel] = rv + val;

                            if (wd >= 0 && wd < 7)
                            {
                                weekdayEvents[wd] += f.Records;
                                weekdayLoot[wd] += f.Quantity;
                                weekdayValue[wd] += val;
                            }
                            hourEvents[hh] += f.Records;
                            hourLoot[hh] += f.Quantity;
                            hourValue[hh] += val;
                        }
                        break;

                    case "layer":
                        layersCount += (int)f.Quantity;
                        day.Layers += (int)f.Quantity;
                        layerAgg.TryGetValue(f.LayerName, out var lc);
                        layerAgg[f.LayerName] = lc + f.Quantity;

                        string layerMap = null;
                        if (_mapConfig != null && !string.IsNullOrEmpty(f.LayerName))
                        {
                            var match = _mapConfig.GetLayerByKeyword(f.LayerName);
                            if (match.Item1 != null) layerMap = match.Item1.Name;
                        }
                        if (string.IsNullOrEmpty(layerMap) && runMap.ContainsKey(f.RunLabel))
                            layerMap = runMap[f.RunLabel];
                        if (!string.IsNullOrEmpty(layerMap))
                        {
                            HistoryMapAgg ma;
                            if (!mapAgg.TryGetValue(layerMap, out ma))
                            {
                                ma = new HistoryMapAgg();
                                mapAgg[layerMap] = ma;
                            }
                            ma.Layers += f.Quantity;
                            ma.Runs.Add(f.RunLabel);
                        }
                        if (wd >= 0 && wd < 7) weekdayEvents[wd] += f.Records;
                        hourEvents[hh] += f.Records;
                        break;

                    case "map_complete":
                        maps += (int)f.Quantity;
                        day.Maps += (int)f.Quantity;
                        {
                            Dictionary<string, int> perDate;
                            if (!runMapsDate.TryGetValue(f.RunLabel, out perDate))
                            {
                                perDate = new Dictionary<string, int>(StringComparer.Ordinal);
                                runMapsDate[f.RunLabel] = perDate;
                            }
                            perDate.TryGetValue(f.Date, out var mc);
                            perDate[f.Date] = mc + (int)f.Quantity;
                        }
                        {
                            string mn;
                            if (!runMap.TryGetValue(f.RunLabel, out mn)) mn = "未标注";
                            HistoryMapAgg ma;
                            if (!mapAgg.TryGetValue(mn, out ma))
                            {
                                ma = new HistoryMapAgg();
                                mapAgg[mn] = ma;
                            }
                            ma.Maps += f.Quantity;
                            ma.Runs.Add(f.RunLabel);
                        }
                        if (wd >= 0 && wd < 7) weekdayEvents[wd] += f.Records;
                        hourEvents[hh] += f.Records;
                        break;

                    case "magic_seal":
                        seals += (int)f.Quantity;
                        day.Seals += (int)f.Quantity;
                        {
                            string mn;
                            if (!runMap.TryGetValue(f.RunLabel, out mn)) mn = "未标注";
                            HistoryMapAgg ma;
                            if (!mapAgg.TryGetValue(mn, out ma))
                            {
                                ma = new HistoryMapAgg();
                                mapAgg[mn] = ma;
                            }
                            ma.Seals += f.Quantity;
                            ma.Runs.Add(f.RunLabel);
                        }
                        if (wd >= 0 && wd < 7) weekdayEvents[wd] += f.Records;
                        hourEvents[hh] += f.Records;
                        break;

                    case "system_gold":
                        gold += f.Quantity;
                        day.Gold += f.Quantity;
                        if (wd >= 0 && wd < 7) weekdayEvents[wd] += f.Records;
                        hourEvents[hh] += f.Records;
                        break;
                }
            }

            // 宝物库按开始日期计入当天
            foreach (var v in vaults)
            {
                string vd = v.StartTime.Length >= 10 ? v.StartTime.Substring(0, 10) : "";
                HistoryDayAgg day;
                if (!string.IsNullOrEmpty(vd) && daily.TryGetValue(vd, out day))
                    day.Vaults++;
            }

            // 日成本：按每车地图数分摊（无地图的整车成本记到首日）
            var dateCost = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var r in runRows)
            {
                Dictionary<string, int> perDate;
                if (!runMapsDate.TryGetValue(r.Label, out perDate))
                    perDate = new Dictionary<string, int>(StringComparer.Ordinal);
                int totalMaps = perDate.Values.Sum();
                if (totalMaps > 0 && Math.Abs(r.Cost) > 1e-9)
                {
                    foreach (var kv in perDate)
                    {
                        double add = r.Cost * kv.Value / totalMaps;
                        dateCost.TryGetValue(kv.Key, out var cur);
                        dateCost[kv.Key] = cur + add;
                    }
                }
                else if (Math.Abs(r.Cost) > 1e-9)
                {
                    string first = runFirstDate.ContainsKey(r.Label)
                        ? runFirstDate[r.Label]
                        : (r.FirstTs.Length >= 10 ? r.FirstTs.Substring(0, 10) : from);
                    dateCost.TryGetValue(first, out var cur);
                    dateCost[first] = cur + r.Cost;
                }
            }

            // 连续日期序列（范围过大时只列有数据的日期）
            var dateList = new List<string>();
            var d0 = ParseHistoryDate(from);
            var d1 = ParseHistoryDate(to);
            if (d0.HasValue && d1.HasValue && (d1.Value - d0.Value).TotalDays <= 400)
            {
                for (var d = d0.Value; d <= d1.Value; d = d.AddDays(1))
                    dateList.Add(d.ToString("yyyy-MM-dd"));
            }
            else
            {
                dateList = daily.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            }

            var dailyArr = new JArray();
            foreach (var date in dateList)
            {
                HistoryDayAgg day;
                if (!daily.TryGetValue(date, out day))
                    day = new HistoryDayAgg();
                double cost;
                dateCost.TryGetValue(date, out cost);
                double income = day.LootValue + day.Gold;
                dailyArr.Add(new JObject
                {
                    ["date"] = date,
                    ["maps"] = day.Maps,
                    ["seals"] = day.Seals,
                    ["loot_qty"] = day.LootQty,
                    ["loot_value"] = day.LootValue,
                    ["gold"] = day.Gold,
                    ["income"] = income,
                    ["cost"] = cost,
                    ["profit"] = income - cost,
                    ["layers"] = day.Layers,
                    ["events"] = day.Events,
                    ["vaults"] = day.Vaults
                });
            }

            var runsArr = new JArray();
            foreach (var r in runRows)
            {
                double rq, rv;
                runLootQty.TryGetValue(r.Label, out rq);
                runLootValue.TryGetValue(r.Label, out rv);
                double income = rv + r.Gold;
                int vaultCount = 0;
                foreach (var v in vaults)
                {
                    if (string.CompareOrdinal(v.StartTime, r.FirstTs) >= 0 &&
                        string.CompareOrdinal(v.StartTime, r.LastTs) <= 0)
                        vaultCount++;
                }
                runsArr.Add(new JObject
                {
                    ["label"] = r.Label,
                    ["date"] = r.FirstTs.Length >= 10 ? r.FirstTs.Substring(0, 10) : "",
                    ["first_ts"] = r.FirstTs,
                    ["last_ts"] = r.LastTs,
                    ["maps"] = r.Maps,
                    ["seals"] = r.Seals,
                    ["seal_rate"] = r.Maps > 0 ? 100.0 * r.Seals / r.Maps : 0,
                    ["loot_qty"] = rq,
                    ["loot_value"] = rv,
                    ["gold"] = r.Gold,
                    ["income"] = income,
                    ["cost"] = r.Cost,
                    ["profit"] = income - r.Cost,
                    ["elapsed"] = r.Elapsed,
                    ["events"] = r.Events,
                    ["vaults"] = vaultCount,
                    ["has_summary"] = r.HasSummary
                });
            }

            var itemsArr = new JArray();
            foreach (var name in itemQty.Keys
                .OrderByDescending(k => { double v; return itemValue.TryGetValue(k, out v) ? v : 0; })
                .ThenByDescending(k => { double v; return itemQty.TryGetValue(k, out v) ? v : 0; }))
            {
                double qty, val;
                itemQty.TryGetValue(name, out qty);
                itemValue.TryGetValue(name, out val);
                HistoryItemInfo info;
                bool known = prices.TryGetValue(name, out info);
                itemsArr.Add(new JObject
                {
                    ["name"] = name,
                    ["qty"] = qty,
                    ["value"] = val,
                    ["price"] = known ? info.Price : 0,
                    ["bonus"] = known ? info.Bonus : 0,
                    ["map"] = known ? info.MapName : "",
                    ["map_name"] = known ? info.MapDisplay : ""
                });
            }

            var layersArr = new JArray();
            foreach (var name in layerAgg.Keys
                .OrderByDescending(k => { double v; return layerAgg.TryGetValue(k, out v) ? v : 0; })
                .ThenBy(k => k, StringComparer.Ordinal))
            {
                double c;
                layerAgg.TryGetValue(name, out c);
                layersArr.Add(new JObject { ["name"] = name, ["count"] = c });
            }

            var mapTypesArr = new JArray();
            foreach (var name in mapAgg.Keys
                .OrderByDescending(k => mapAgg[k].Value)
                .ThenByDescending(k => mapAgg[k].Qty))
            {
                var ma = mapAgg[name];
                mapTypesArr.Add(new JObject
                {
                    ["name"] = name,
                    ["qty"] = ma.Qty,
                    ["value"] = ma.Value,
                    ["maps"] = ma.Maps,
                    ["seals"] = ma.Seals,
                    ["layers"] = ma.Layers,
                    ["runs"] = ma.Runs.Count
                });
            }

            var hoursArr = new JArray();
            for (int h = 0; h < 24; h++)
            {
                hoursArr.Add(new JObject
                {
                    ["hour"] = h.ToString("00"),
                    ["events"] = hourEvents[h],
                    ["loot_qty"] = hourLoot[h],
                    ["value"] = hourValue[h]
                });
            }

            var weekdaysArr = new JArray();
            for (int w = 0; w < 7; w++)
            {
                weekdaysArr.Add(new JObject
                {
                    ["weekday"] = HistoryWeekdays[w],
                    ["events"] = weekdayEvents[w],
                    ["loot_qty"] = weekdayLoot[w],
                    ["value"] = weekdayValue[w]
                });
            }

            double vaultSeconds = 0;
            int vaultLoot = 0, vaultLayers = 0;
            foreach (var v in vaults)
            {
                vaultSeconds += HistorySecondsBetween(v.StartTime, v.LastTs);
                vaultLoot += v.LootQty;
                vaultLayers += v.LayerCount;
            }

            double totalCost = runRows.Sum(r => r.Cost);
            double totalIncome = lootValue + gold;
            int activeDays = daily.Values.Count(d => d.Events > 0);

            return new JObject
            {
                ["from"] = from,
                ["to"] = to,
                ["runs_selected"] = new JArray(runLabels),
                ["totals"] = new JObject
                {
                    ["maps"] = maps,
                    ["magic_seals"] = seals,
                    ["seal_rate"] = maps > 0 ? 100.0 * seals / maps : 0,
                    ["loot_qty"] = lootQty,
                    ["loot_value"] = lootValue,
                    ["gold"] = gold,
                    ["income"] = totalIncome,
                    ["cost"] = totalCost,
                    ["profit"] = totalIncome - totalCost,
                    ["layers"] = layersCount,
                    ["events"] = events,
                    ["runs"] = runRows.Count,
                    ["days"] = dateList.Count,
                    ["active_days"] = activeDays,
                    ["vaults"] = vaults.Count,
                    ["vault_seconds"] = vaultSeconds,
                    ["vault_loot"] = vaultLoot,
                    ["vault_layers"] = vaultLayers,
                    ["per_map_loot"] = maps > 0 ? lootQty / maps : 0,
                    ["per_map_value"] = maps > 0 ? lootValue / maps : 0,
                    ["per_map_income"] = maps > 0 ? totalIncome / maps : 0,
                    ["per_run_profit"] = runRows.Count > 0 ? (totalIncome - totalCost) / runRows.Count : 0
                },
                ["daily"] = dailyArr,
                ["runs"] = runsArr,
                ["items"] = itemsArr,
                ["layers"] = layersArr,
                ["map_types"] = mapTypesArr,
                ["hours"] = hoursArr,
                ["weekdays"] = weekdaysArr,
                ["vaults"] = new JObject
                {
                    ["count"] = vaults.Count,
                    ["seconds"] = vaultSeconds,
                    ["loot_qty"] = vaultLoot,
                    ["layers"] = vaultLayers,
                    ["avg_seconds"] = vaults.Count > 0 ? vaultSeconds / vaults.Count : 0,
                    ["avg_loot"] = vaults.Count > 0 ? (double)vaultLoot / vaults.Count : 0
                },
                ["cost_estimated"] = true,
                ["price_source"] = "maps.json",
                ["notes"] = new JObject
                {
                    ["price"] = "物品价值按当前 maps.json 价格估算",
                    ["cost"] = "日成本按每车地图数分摊，日盈亏为估算值；车次盈亏使用插件记录成本",
                    ["vault"] = "宝物库时长按进入时间到最后一条掉落/层级事件估算",
                    ["map"] = "地图类型：出货按物品归属；地图完成/魔纹按整车唯一地图类型归类"
                }
            }.ToString(Formatting.None);
        }

        // ============================================================
        // 响应辅助
        // ============================================================

        private void ServeHtml(HttpListenerContext ctx)
        {
            string html = "<h1>FF14 Treasure Hunter</h1><p>overlay.html 未找到。</p>";
            foreach (var dir in _htmlSearchDirs)
            {
                try
                {
                    string candidate = Path.Combine(dir, "overlay.html");
                    if (File.Exists(candidate))
                    {
                        html = File.ReadAllText(candidate, Encoding.UTF8);
                        break;
                    }
                }
                catch { }
            }
            byte[] bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        // ============================================================
        // 配置 / 地图 / 规则 的 POST 处理
        // ============================================================

        /// <summary>判断请求是否允许。本地请求直接放行；外部请求需要正确的 token</summary>
        private bool CheckAuth(HttpListenerContext ctx)
        {
            try
            {
                var remote = ctx.Request.RemoteEndPoint?.Address;
                if (remote != null && IPAddress.IsLoopback(remote))
                    return true;   // 本地访问，放行
            }
            catch { }

            // 非本地 → 需要局域网模式 + 正确 token
            if (!LanMode) return false;
            if (string.IsNullOrEmpty(LanToken)) return false;

            string t = ctx.Request.QueryString["t"] ?? "";
            return t == LanToken;
        }


        private string ReadRequestBody(HttpListenerContext ctx)
        {
            try
            {
                // 强制 UTF-8：HttpListener 在未指定 charset 时默认用 ISO-8859-1，
                // 会导致中文乱码。前端发的是 UTF-8，这里必须写死。
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    // 去除可能存在的 BOM
                    if (!string.IsNullOrEmpty(body) && body[0] == '\uFEFF')
                        body = body.Substring(1);
                    return body;
                }
            }
            catch { return ""; }
        }

        private void HandleConfigSet(HttpListenerContext ctx)
        {
            try
            {
                var body = ReadRequestBody(ctx);
                var obj = JObject.Parse(body);
                string path = obj["path"]?.ToString() ?? "";
                var val = obj["value"];
                if (string.IsNullOrEmpty(path) || val == null)
                {
                    ServeJson(ctx, "{\"ok\":false,\"error\":\"missing path or value\"}");
                    return;
                }

                object value;
                if (val.Type == JTokenType.Integer) value = val.Value<long>();
                else if (val.Type == JTokenType.Float) value = val.Value<double>();
                else if (val.Type == JTokenType.Boolean) value = val.Value<bool>();
                else if (val.Type == JTokenType.Array)
                {
                    var list = new List<string>();
                    foreach (var x in val) list.Add(x.ToString());
                    value = list;
                }
                else value = val.ToString();

                ConfigManager.Set(path, value);
                ServeJson(ctx, "{\"ok\":true}");
            }
            catch (Exception ex)
            {
                ServeJson(ctx, "{\"ok\":false,\"error\":\"" +
                    ex.Message.Replace("\"", "'") + "\"}");
            }
        }

        private void HandleConfigBatchSet(HttpListenerContext ctx)
        {
            try
            {
                var body = ReadRequestBody(ctx);
                var obj = JObject.Parse(body);
                var items = obj["items"] as JArray;
                if (items == null)
                {
                    ServeJson(ctx, "{\"ok\":false,\"error\":\"missing items\"}");
                    return;
                }
                ConfigManager.BatchSet(items);
                ServeJson(ctx, "{\"ok\":true,\"count\":" + items.Count + "}");
            }
            catch (Exception ex)
            {
                ServeJson(ctx, "{\"ok\":false,\"error\":\"" +
                    ex.Message.Replace("\"", "'") + "\"}");
            }
        }

        private void HandleConfigReload(HttpListenerContext ctx)
        {
            try
            {
                ConfigManager.Load();
                ServeJson(ctx, "{\"ok\":true}");
            }
            catch (Exception ex)
            {
                ServeJson(ctx, "{\"ok\":false,\"error\":\"" +
                    ex.Message.Replace("\"", "'") + "\"}");
            }
        }

        private void HandleMapSave(HttpListenerContext ctx)
        {
            try
            {
                var body = ReadRequestBody(ctx);
                var obj = JObject.Parse(body);
                bool ok = _mapConfig != null && _mapConfig.SaveMapFromJson(obj);
                ServeJson(ctx, ok ? "{\"ok\":true}" : "{\"ok\":false}");
            }
            catch (Exception ex)
            {
                ServeJson(ctx, "{\"ok\":false,\"error\":\"" +
                    ex.Message.Replace("\"", "'") + "\"}");
            }
        }

        private void HandleMapDelete(HttpListenerContext ctx)
        {
            try
            {
                var body = ReadRequestBody(ctx);
                var obj = JObject.Parse(body);
                string name = obj["name"]?.ToString() ?? "";
                bool ok = _mapConfig != null && _mapConfig.DeleteMap(name);
                ServeJson(ctx, ok ? "{\"ok\":true}" : "{\"ok\":false}");
            }
            catch (Exception ex)
            {
                ServeJson(ctx, "{\"ok\":false,\"error\":\"" +
                    ex.Message.Replace("\"", "'") + "\"}");
            }
        }

        private void HandlePredictionSave(HttpListenerContext ctx)
        {
            try
            {
                var body = ReadRequestBody(ctx);
                var obj = JObject.Parse(body);
                bool ok = _predictionManager != null &&
                          _predictionManager.SaveRuleFromJson(obj);
                ServeJson(ctx, ok ? "{\"ok\":true}" : "{\"ok\":false}");
            }
            catch (Exception ex)
            {
                ServeJson(ctx, "{\"ok\":false,\"error\":\"" +
                    ex.Message.Replace("\"", "'") + "\"}");
            }
        }

        private void HandlePredictionDelete(HttpListenerContext ctx)
        {
            try
            {
                var body = ReadRequestBody(ctx);
                var obj = JObject.Parse(body);
                string ruleId = obj["rule_id"]?.ToString() ?? "";
                bool ok = _predictionManager != null &&
                          _predictionManager.DeleteRule(ruleId);
                ServeJson(ctx, ok ? "{\"ok\":true}" : "{\"ok\":false}");
            }
            catch (Exception ex)
            {
                ServeJson(ctx, "{\"ok\":false,\"error\":\"" +
                    ex.Message.Replace("\"", "'") + "\"}");
            }
        }

        private void HandleRuleSave(HttpListenerContext ctx)
        {
            try
            {
                var body = ReadRequestBody(ctx);
                var obj = JObject.Parse(body);
                bool ok = _rulesManager != null && _rulesManager.SaveRuleFromJson(obj);
                ServeJson(ctx, ok ? "{\"ok\":true}" : "{\"ok\":false}");
            }
            catch (Exception ex)
            {
                ServeJson(ctx, "{\"ok\":false,\"error\":\"" +
                    ex.Message.Replace("\"", "'") + "\"}");
            }
        }

        private void HandleRuleDelete(HttpListenerContext ctx)
        {
            try
            {
                var body = ReadRequestBody(ctx);
                var obj = JObject.Parse(body);
                string ruleId = obj["rule_id"]?.ToString() ?? "";
                bool ok = _rulesManager != null && _rulesManager.DeleteRule(ruleId);
                ServeJson(ctx, ok ? "{\"ok\":true}" : "{\"ok\":false}");
            }
            catch (Exception ex)
            {
                ServeJson(ctx, "{\"ok\":false,\"error\":\"" +
                    ex.Message.Replace("\"", "'") + "\"}");
            }
        }
        // ============================================================
        // 发布管理 API
        // ============================================================

        private void HandlePublishPreview(HttpListenerContext ctx)
        {
            try
            {
                string dataVersion = ctx.Request.QueryString["data_version"] ?? "";

                string mapsJson = _mapConfig?.GetPublishableMapsJson() ?? "{\"maps\":[]}";
                string predsJson = _predictionManager?.GetPublishablePredictionsJson() ?? "{\"predictions\":[]}";

                var mapsArr = JObject.Parse(mapsJson)["maps"] as JArray ?? new JArray();
                var predsArr = JObject.Parse(predsJson)["predictions"] as JArray ?? new JArray();

                string htmlVer = HtmlUpdateManager.ReadLocalHtmlVersion(_webDir) ?? "";

                var versionObj = new JObject
                {
                    ["html_version"] = htmlVer,
                    ["data_version"] = dataVersion,
                    ["files"] = new JArray("overlay.html", "history.html", "settings.html", "mobile.html")
                };

                var result = new JObject
                {
                    ["maps"] = mapsArr,
                    ["predictions"] = predsArr,
                    ["version"] = versionObj,
                    ["stats"] = new JObject
                    {
                        ["maps_count"] = mapsArr.Count,
                        ["predictions_count"] = predsArr.Count
                    }
                };

                ServeJson(ctx, result.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                ServeJson(ctx, "{\"ok\":false,\"error\":\"" +
                    ex.Message.Replace("\"", "'") + "\"}");
            }
        }
        private void ServeJson(HttpListenerContext ctx, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json ?? "{}");
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        private void ServeText(HttpListenerContext ctx, int code, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        private string GetItemMapShort(string itemName)
        {
            if (_mapConfig == null) return "未知";
            var match = _mapConfig.GetItemByName(itemName);
            if (match.Item1 == null) return "未知";
            return string.IsNullOrEmpty(match.Item1.MapItemPrefix)
                ? match.Item1.Name : match.Item1.MapItemPrefix;
        }
    }
}