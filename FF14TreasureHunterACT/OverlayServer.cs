// OverlayServer.cs
using System;
using System.Collections.Generic;
using System.IO;
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