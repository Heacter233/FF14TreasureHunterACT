// PostNamazuClient.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace FF14TreasureHunterACT
{
    /// <summary>
    /// 鲶鱼精邮差 HTTP 客户端。
    /// 对应 Python 的 postnamazu_client.py
    /// 
    /// 通道优先级（当前只实现 HTTP）：
    ///   1. Triggernometry 命名回调（TODO 扩展点）
    ///   2. ACT 直发游戏 SendToFFXIV（TODO 扩展点）
    ///   3. HTTP POST 127.0.0.1:2019/command ← 当前实现
    /// </summary>
    public class PostNamazuClient
    {
        public bool Enabled { get; set; }
        public int Port { get; private set; }
        public bool Connected { get; private set; }
        public string LastError { get; private set; } = "";

        private readonly string _baseUrl;
        private readonly object _lock = new object();

        public PostNamazuClient(int port = 2019, bool enabled = true)
        {
            Port = port;
            Enabled = enabled;
            _baseUrl = $"http://127.0.0.1:{port}";
        }

        /// <summary>尝试连接（发送一条测试命令）。返回是否成功。</summary>
        public bool TestConnection()
        {
            try
            {
                return SendCommand("/e 挖宝会计连接测试");
            }
            catch
            {
                Connected = false;
                return false;
            }
        }

        /// <summary>
        /// 发送一条命令（自动加 / 前缀如果缺失）。返回是否成功。
        ///
        /// 重要设计说明（修复消息静默丢失）：
        ///  - 这里【绝不】用 Connected 当前置条件。以前调用方写 `if (Connected) Send(...)`，
        ///    一旦某次发送失败把 Connected 置 false，后续就再也不会尝试发送，
        ///    于是 Connected 永远无法恢复 —— 形成死锁，预测/分段消息被静默丢弃。
        ///    正确做法：每次都尝试发送，用结果更新 Connected。
        ///  - 用 _lock 串行化：预测走日志线程、汇总走后台线程、分段走 UI 线程，
        ///    并发 POST 可能让鲶鱼精处理不过来而丢消息。
        ///  - 瞬时失败（连接被拒）自动重试，覆盖游戏/鲶鱼精重启的窗口期。
        /// </summary>
        public bool SendCommand(string command)
        {
            return SendCommand(command, DefaultRetries);
        }

        private const int DefaultRetries = 2;

        private bool SendCommand(string command, int retriesLeft)
        {
            if (!Enabled) { LastError = "客户端已禁用"; return false; }
            if (string.IsNullOrEmpty(command)) { LastError = "命令为空"; return false; }
            if (!command.StartsWith("/")) command = "/" + command;

            lock (_lock)
            {
                bool transient;
                bool ok = TrySendOnce(command, out transient);

                // 只有"连接层面"的瞬时失败才重试（端口还没起来/正在重启）。
                // HTTP 4xx/5xx 说明鲶鱼精已收到请求，重试无意义。
                if (!ok && transient && retriesLeft > 0)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[PostNamazu] 瞬时失败，重试中（剩余 {retriesLeft}）：{LastError}");
                    try { Thread.Sleep(300); } catch { }
                    return SendCommand(command, retriesLeft - 1);
                }
                return ok;
            }
        }

        /// <summary>单次发送尝试。transient=是否为可重试的连接级失败。</summary>
        private bool TrySendOnce(string command, out bool transient)
        {
            transient = false;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(_baseUrl + "/command");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 4000;            // 连接+等待响应总超时
                req.ReadWriteTimeout = 4000;   // 读写超时
                req.KeepAlive = false;         // 避免连接复用导致的卡顿
                req.Proxy = null;              // 不走系统代理（否则可能被拦截）

                byte[] body = Encoding.UTF8.GetBytes(command);
                req.ContentLength = body.Length;
                using (var stream = req.GetRequestStream())
                {
                    stream.Write(body, 0, body.Length);
                }

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    int code = (int)resp.StatusCode;
                    if (code >= 200 && code < 300)
                    {
                        Connected = true;
                        LastError = "";
                        return true;
                    }

                    string respBody = "";
                    try
                    {
                        using (var sr = new StreamReader(resp.GetResponseStream()))
                            respBody = sr.ReadToEnd();
                    }
                    catch { }

                    LastError = $"HTTP {code}: {respBody}";
                    System.Diagnostics.Debug.WriteLine($"[PostNamazu] {LastError}");
                    Connected = false;
                    return false;
                }
            }
            catch (WebException wex)
            {
                string respBody = "";
                try
                {
                    if (wex.Response != null)
                        using (var sr = new StreamReader(wex.Response.GetResponseStream()))
                            respBody = sr.ReadToEnd();
                }
                catch { }

                // 连接被拒 / 连接被中断 / 无法解析 → 可重试（鲶鱼精正在重启，端口暂时不在）。
                // 注意：Timeout 不重试 —— 已经等了 4 秒，再等只会拖慢日志解析线程。
                transient =
                    wex.Status == WebExceptionStatus.ConnectFailure ||
                    wex.Status == WebExceptionStatus.ConnectionClosed ||
                    wex.Status == WebExceptionStatus.NameResolutionFailure;

                LastError = $"WebException({wex.Status}): {wex.Message}";
                if (!string.IsNullOrEmpty(respBody)) LastError += " | " + respBody;
                System.Diagnostics.Debug.WriteLine($"[PostNamazu] {LastError}");
                Connected = false;
                return false;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                System.Diagnostics.Debug.WriteLine($"[PostNamazu] {LastError}");
                Connected = false;
                return false;
            }
        }

        /// <summary>
        /// 后台异步发送（不阻塞调用线程）。用于预测/输出这类在日志线程上的高频调用：
        /// 万一鲶鱼精卡住，日志解析不会被拖慢。
        /// </summary>
        public void SendCommandAsync(string command)
        {
            if (!Enabled || string.IsNullOrEmpty(command)) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { SendCommand(command); } catch { }
            });
        }

        /// <summary>
        /// 发送多条消息。channel: "tell" 或 "party"。
        /// 对应 Python 的 send_messages。
        /// </summary>
        public bool SendMessages(List<string> messages, string channel = "tell", double delay = 0.5)
        {
            if (messages == null || messages.Count == 0) return false;

            int success = 0;
            foreach (var msg in messages)
            {
                string cmd;
                if (channel == "tell") cmd = "/e " + msg;
                else if (channel == "party") cmd = "/p " + msg;
                else cmd = "/" + channel + " " + msg;

                if (SendCommand(cmd))
                {
                    success++;
                    if (delay > 0)
                        Thread.Sleep((int)(delay * 1000));
                }
                else
                {
                    break;
                }
            }
            return success == messages.Count;
        }

        public void Enable(int? port = null)
        {
            if (port.HasValue)
            {
                Port = port.Value;
                // 注意：_baseUrl 是 readonly，如需切换端口要重建对象
            }
            Enabled = true;
            TestConnection();
        }

        public void Disable()
        {
            Enabled = false;
            Connected = false;
        }

        // -----------------------------------------------------------------
        // TODO 扩展点：Triggernometry 命名回调 / ACT 直发游戏
        // -----------------------------------------------------------------
        // 若需接入 Triggernometry：
        //   1. 用户在 Tm 中注册一个 NamedCallback（例如 "FF14TreasureHunter.Send"）
        //   2. C# 侧通过 Tm 的对外 API 触发回调；Tm 内部执行相应命令
        // 若需 ACT 直发游戏：
        //   1. 引用 Advanced_Combat_Tracker
        //   2. 调用 ActGlobals.oFormActMain.SendToFFXIV(cmd)
        //   3. 注意：ACT 通常拦截这类调用，需用户在 ACT 允许
        // -----------------------------------------------------------------
    }
}