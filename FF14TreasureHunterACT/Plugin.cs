// Plugin.cs
using Advanced_Combat_Tracker;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Label = System.Windows.Forms.Label;

namespace FF14TreasureHunterACT
{
    public class Plugin : IActPluginV1
    {
        private Label _statusLabel;
        private MainPanel _mainPanel;
        private EventProcessor _processor;
        private Database _db;
        private StateTracker _state;
        private MapConfigManager _mapConfig;
        private RulesManager _rulesManager;
        private PredictionManager _predictionManager;
        private PostNamazuClient _postnamazu;
        private SegmentManager _segmentManager;
        private CostCalculator _costCalc;
        private FinancialManager _financialMgr;
        private Timer _tickTimer;   // System.Windows.Forms.Timer
        private OverlayServer _overlayServer;
        private HtmlUpdateManager _htmlUpdater;
        private DataUpdateManager _dataUpdater;
        private PluginUpdateManager _pluginUpdater;

        static Plugin()
        {
            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            }
            catch { }
        }

        /// <summary>定位本插件 DLL 的完整路径（多策略）</summary>
        /// <summary>日志里截断过长文本，避免把整段游戏原文刷屏</summary>
        private static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static string FindOwnDllPath()
        {
            // 策略 1：从 ACT 进程反查（最可靠）
            try
            {
                string[] procNames = { "Advanced Combat Tracker", "CafeACT", "ACT.DieMoe.Launcher" };
                foreach (var pn in procNames)
                {
                    foreach (var p in Process.GetProcessesByName(pn))
                    {
                        try
                        {
                            string exe = p.MainModule.FileName;
                            string root = Path.GetDirectoryName(exe);
                            if (string.IsNullOrEmpty(root)) continue;

                            string candidate = Path.Combine(root, "Plugins", "FF14TreasureHunterACT.dll");
                            if (File.Exists(candidate))
                                return candidate;

                            // 呆萌：主进程可能是 Launcher，ACT 在子目录
                            candidate = Path.Combine(root, "ACT.DieMoe", "Plugins", "FF14TreasureHunterACT.dll");
                            if (File.Exists(candidate))
                                return candidate;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // 策略 2：读安装器写入的 install.json
            try
            {
                string cfg = Path.Combine(ConfigManager.UserDataDir, "install.json");
                if (File.Exists(cfg))
                {
                    var obj = Newtonsoft.Json.Linq.JObject.Parse(
                        File.ReadAllText(cfg, System.Text.Encoding.UTF8));
                    string p = obj["plugin_dll"]?.ToString();
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                        return p;
                }
            }
            catch { }

            // 策略 3：从当前进程的 ACT 目录反查（兜底）
            try
            {
                string curProc = Process.GetCurrentProcess().MainModule.FileName;
                string curRoot = Path.GetDirectoryName(curProc);
                if (!string.IsNullOrEmpty(curRoot))
                {
                    string candidate = Path.Combine(curRoot, "Plugins", "FF14TreasureHunterACT.dll");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch { }

            return "";
        }


        /// <summary>获取插件 DLL 所在目录（多策略，取第一个有效的）</summary>
        private static string GetPluginDirectory()
        {
            var candidates = GetAllPluginDirCandidates();
            foreach (var dir in candidates)
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    return dir;
            }
            return Environment.CurrentDirectory;
        }

        /// <summary>返回所有可能的 DLL 所在目录（按优先级）</summary>
        private static List<string> GetAllPluginDirCandidates()
        {
            var list = new List<string>();

            // 策略 0：从 FindOwnDllPath 得到（最可靠）
            try
            {
                string ownDll = FindOwnDllPath();
                if (!string.IsNullOrEmpty(ownDll))
                {
                    string dir = Path.GetDirectoryName(ownDll);
                    if (!string.IsNullOrEmpty(dir)) list.Add(dir);
                }
            }
            catch { }

            // 策略 1：CodeBase（保留作为兜底）
            try
            {
                var asm = typeof(Plugin).Assembly;
                if (!string.IsNullOrEmpty(asm.CodeBase))
                {
                    var uri = new Uri(asm.CodeBase);
                    if (uri.IsFile)
                    {
                        string dir = Path.GetDirectoryName(uri.LocalPath);
                        if (!string.IsNullOrEmpty(dir)) list.Add(dir);
                    }
                }
            }
            catch { }

            // 策略 2：Location（★ 明确用插件程序集）
            try
            {
                string loc = typeof(Plugin).Assembly.Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    string dir = Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(dir)) list.Add(dir);
                }
            }
            catch { }

            // 策略 3：BaseDirectory
            try
            {
                list.Add(AppDomain.CurrentDomain.BaseDirectory);
            }
            catch { }

            // 策略 4：CurrentDirectory
            try
            {
                list.Add(Environment.CurrentDirectory);
            }
            catch { }

            // 去重
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in list)
            {
                if (string.IsNullOrEmpty(d)) continue;
                if (seen.Contains(d)) continue;
                seen.Add(d);
                result.Add(d);
            }
            return result;
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string assemblyName = new AssemblyName(args.Name).Name;
                string pluginDir = GetPluginDirectory();
                string dllPath = Path.Combine(pluginDir, assemblyName + ".dll");
                if (File.Exists(dllPath))
                    return Assembly.LoadFrom(dllPath);
            }
            catch { }
            return null;
        }

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            _statusLabel = pluginStatusText;
            pluginScreenSpace.Text = "挖宝会计";

            try
            {
                // ---------- 基础 ----------
                ConfigManager.Load();
                _db = new Database(ConfigManager.DatabasePath);
                RawLogWriter.Initialize();

                _mainPanel = new MainPanel();
                pluginScreenSpace.Controls.Add(_mainPanel);
                _mainPanel.Dock = DockStyle.Fill;


                // ---------- 模块初始化 ----------
                _state = new StateTracker(_db);

                string mapsPath = Path.Combine(ConfigManager.ConfigDir, "maps.json");
                _mapConfig = new MapConfigManager(mapsPath);
                _mapConfig.EnsureIds();   // 补齐缺失的 id（幂等）

                string rulesPath = Path.Combine(ConfigManager.ConfigDir, "rules.json");
                _rulesManager = new RulesManager(rulesPath);

                string predsPath = Path.Combine(ConfigManager.ConfigDir, "predictions.json");
                _predictionManager = new PredictionManager(predsPath);
                _predictionManager.LoadExampleRules();

                int pnPort = ConfigManager.GetInt("postnamazu.port", 2019);
                bool pnEnabled = ConfigManager.GetBool("postnamazu.enabled", true);
                _postnamazu = new PostNamazuClient(pnPort, pnEnabled);

                if (pnEnabled)
                {
                    // 后台线程测试，避免阻塞 UI（冷启动时鲶鱼精响应可能慢）
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        bool pnOk = _postnamazu.TestConnection();
                        _mainPanel.AppendLog(pnOk
                            ? "鲶鱼精连接成功"
                            : $"鲶鱼精未连接（端口 {pnPort}），可稍后点发送重试");
                    });
                }

                _segmentManager = new SegmentManager(
                    Path.Combine(ConfigManager.DataDir, "segment_snapshots.json"));

                // 与 Python 版一致：每次插件启动清空历史分段（避免跨 run_label 数据错乱）
                try
                {
                    _segmentManager.Snapshots.Clear();
                    _segmentManager.SaveSnapshots();
                }
                catch { }

                _costCalc = new CostCalculator();
                _financialMgr = new FinancialManager(_costCalc);

                // ---------- EventProcessor ----------
                _processor = new EventProcessor(
                    _db, _state, _mapConfig, _rulesManager, _predictionManager,
                    _postnamazu,
                    _mainPanel.AppendLog,
                    msg => _mainPanel.AppendLog("[通知] " + msg)
                );

                // 延迟动作调度：用 UI 线程的 Timer 保证回调在 UI 线程
                _processor.Scheduler = (delayMs, cb) =>
                {
                    var t = new Timer { Interval = Math.Max(50, delayMs) };
                    t.Tick += (s, e) =>
                    {
                        t.Stop();
                        t.Dispose();
                        try { cb(); } catch (Exception ex) { _mainPanel?.AppendLog("[Scheduler] " + ex.Message); }
                    };
                    t.Start();
                };
                _mainPanel.SetMapConfig(_mapConfig);
                _mainPanel.Bind(_db, _processor, _state, _segmentManager, _postnamazu, _costCalc, _financialMgr,_predictionManager);
                // 每秒检查超时
                _tickTimer = new Timer { Interval = 1000 };
                _tickTimer.Tick += (s, e) => { try { _processor?.CheckPendingTimeouts(); } catch { } };
                _tickTimer.Start();

                // ---------- HTML 更新管理器 ----------
                string webDir = Path.Combine(ConfigManager.UserDataDir, "web");
                _htmlUpdater = new HtmlUpdateManager(webDir);
                _htmlUpdater.OnLog += msg => _mainPanel?.AppendLog(msg);
                _htmlUpdater.OnStateChanged += () =>
                {
                    try { _mainPanel?.RefreshHtmlVersion(); } catch { }
                };
                _mainPanel.SetHtmlUpdateManager(_htmlUpdater);

                // 启动后延迟检查（避免和初始化抢资源）
                System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ =>
                {
                    try { _htmlUpdater.CheckRemoteAsync(); } catch { }
                });

                // ---------- 数据更新管理器 ----------
                _dataUpdater = new DataUpdateManager(ConfigManager.DataDir);
                _dataUpdater.OnLog += msg => _mainPanel?.AppendLog(msg);
                _dataUpdater.OnStateChanged += () =>
                {
                    try { _mainPanel?.RefreshDataVersion(); } catch { }
                };
                _mainPanel.SetDataUpdateManager(_dataUpdater);

                // 延迟检查（与 HTML 检查错开，避免同时请求）
                System.Threading.Tasks.Task.Delay(5000).ContinueWith(_ =>
                {
                    try { _dataUpdater.CheckRemoteAsync(); } catch { }
                });

                // ---------- 插件自更新管理器 ----------
                string localDllPath = FindOwnDllPath();

                if (string.IsNullOrEmpty(localDllPath))
                {
                    _mainPanel?.AppendLog("[插件] ⚠ 无法定位本插件 DLL 路径，更新功能将不可用");
                    _mainPanel?.AppendLog("[插件]    请手动确认 DLL 位于 ACT 的 Plugins 目录下");
                }
                else
                {
                    _mainPanel?.AppendLog($"[插件] 本机 DLL 路径：{localDllPath}");
                }

                string backupDir = Path.Combine(ConfigManager.UserDataDir, "backup");
                _pluginUpdater = new PluginUpdateManager(localDllPath, backupDir);
                _pluginUpdater.OnLog += msg => _mainPanel?.AppendLog(msg);
                _pluginUpdater.OnStateChanged += () =>
                {
                    try { _mainPanel?.RefreshPluginVersion(); } catch { }
                };
                // 更新失败必须弹窗：只写日志用户根本注意不到
                _pluginUpdater.OnFailed += msg =>
                {
                    try
                    {
                        if (_mainPanel == null) return;
                        _mainPanel.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                MessageBox.Show(
                                    "插件自动更新失败。\n\n" + msg +
                                    "\n\n还可以手动下载替换：\n" +
                                    "https://heikadi.top/ff14ds/wbkj/ACThtml/FF14TreasureHunterACT.dll",
                                    "挖宝会计 · 更新失败",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                            catch { }
                        }));
                    }
                    catch { }
                };
                _mainPanel.SetPluginUpdateManager(_pluginUpdater);

                System.Threading.Tasks.Task.Delay(7000).ContinueWith(_ =>
                {
                    try { _pluginUpdater.CheckRemoteAsync(); } catch { }
                });

                // ---------- 悬浮窗服务器 ----------
                bool overlayEnabled = ConfigManager.GetBool("ui.overlay_enabled", true);
                if (overlayEnabled)
                {
                    int overlayPort = ConfigManager.GetInt("ui.overlay_port", 8765);

                    // 多目录候选：DLL 目录 + ACT 主程序目录
                    var htmlDirs = GetAllPluginDirCandidates();
                    string extraDir = ConfigManager.GetString("ui.resource_dir", "");
                    if (!string.IsNullOrEmpty(extraDir) && Directory.Exists(extraDir))
                        htmlDirs.Insert(0, extraDir);

                    _overlayServer = new OverlayServer(overlayPort);
                    _overlayServer.Bind(_db, _processor, _state, _segmentManager, _mapConfig,
                                        _predictionManager, _rulesManager, htmlDirs, webDir);
                    _overlayServer.Start();
                    // 把预测通知转发到 OverlayServer
                    if (_processor != null)
                    {
                        _processor.OnPredictionResult = (type, text) =>
                        {
                            try { _overlayServer?.PushPredictionNotification(type, text); } catch { }
                        };
                    }

                    if (_overlayServer.Running)
                        _mainPanel.AppendLog($"悬浮窗：http://127.0.0.1:{overlayPort}/");
                    else
                        _mainPanel.AppendLog("悬浮窗启动失败（端口可能被占用，或需要管理员权限）");
                }
                _mainPanel.SetOverlayServer(_overlayServer);

                // ---------- 订阅 ACT 日志 ----------
                ActGlobals.oFormActMain.OnLogLineRead += OnLogLineRead;

                _statusLabel.Text = "挖宝会计插件已启动";
                _mainPanel.AppendLog("插件初始化成功");
                _mainPanel.AppendLog($"数据库：{ConfigManager.DatabasePath}");
                _mainPanel.AppendLog($"配置目录：{ConfigManager.ConfigDir}");
                _mainPanel.AppendLog($"本次运行标签：{_processor.CurrentRunLabel}");

                // 打印当前生效的识别关键词：
                // 用户反馈"地图统计不到"时，这一行能立刻看出配置是否为空/是否正确
                try
                {
                    string mk = ConfigManager.GetString("detection.map_complete_keyword", "");
                    string sk = ConfigManager.GetString("detection.magic_seal_keyword", "");
                    if (string.IsNullOrWhiteSpace(mk) || string.IsNullOrWhiteSpace(sk))
                    {
                        _mainPanel.AppendLog(
                            "[识别] ⚠ 关键词为空，地图/魔纹将无法统计！请在设置页「识别规则」中填写");
                    }
                    else
                    {
                        _mainPanel.AppendLog($"[识别] 地图完成关键词：{Shorten(mk, 40)}");
                        _mainPanel.AppendLog($"[识别] 魔纹出现关键词：{Shorten(sk, 40)}");
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "挖宝会计插件初始化失败";

                Exception inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;

                MessageBox.Show(
                    $"插件初始化失败:\n\n【顶层异常】{ex.GetType().Name}: {ex.Message}\n\n" +
                    $"【根本原因】{inner.GetType().Name}: {inner.Message}",
                    "挖宝会计", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void DeInitPlugin()
        {
            try { ActGlobals.oFormActMain.OnLogLineRead -= OnLogLineRead; } catch { }

            try { if (_tickTimer != null) { _tickTimer.Stop(); _tickTimer.Dispose(); _tickTimer = null; } } catch { }

            try { _mainPanel?.StopTimers(); } catch { }

            try { _overlayServer?.Stop(); } catch { }

            NotificationService.Dispose();

            try { _db?.Close(); } catch { }

            _statusLabel.Text = "挖宝会计插件已停止";
        }

        private void OnLogLineRead(bool isImport, LogLineEventArgs logInfo)
        {
            if (isImport) return;

            RawLogWriter.Write(logInfo.logLine);

            try
            {
                _processor?.ProcessLine(logInfo.logLine);
            }
            catch (Exception ex)
            {
                _mainPanel?.AppendLog("[错误] " + ex.Message);
            }
        }
    }
}