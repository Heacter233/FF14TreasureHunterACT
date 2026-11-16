// PluginUpdateManager.cs
using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;

namespace FF14TreasureHunterACT
{
    /// <summary>
    /// 插件 DLL 自更新管理器。
    /// 流程：检查版本 → 下载新 DLL → 备份旧 DLL → 覆盖 → 提示用户重载。
    /// </summary>
    public class PluginUpdateManager
    {
        private const string RemoteBaseUrl = "https://heikadi.top/ff14ds/wbkj/ACThtml/";

        public string LocalVersion { get; private set; } = "";
        public string RemoteVersion { get; private set; } = "";
        public string RemoteUrl { get; private set; } = "";
        public string LastError { get; private set; } = "";
        public bool IsUpdating { get; private set; } = false;
        public bool UpdateCompleted { get; private set; } = false;

        private readonly string _localDllPath;
        private readonly string _backupDir;

        /// <summary>当前插件 DLL 的实际路径（供 UI 做提示）</summary>
        public string LocalDllPath { get { return _localDllPath; } }

        public bool UpdateAvailable
        {
            get
            {
                if (UpdateCompleted) return false;
                if (string.IsNullOrEmpty(RemoteVersion)) return false;
                if (string.IsNullOrEmpty(LocalVersion)) return false;
                return CompareVersion(RemoteVersion, LocalVersion) > 0;
            }
        }

        public event Action<string> OnLog;
        public event Action OnStateChanged;
        /// <summary>更新失败时抛出，供 UI 弹窗（不能只写日志，用户会看不到）</summary>
        public event Action<string> OnFailed;

        public PluginUpdateManager(string localDllPath, string backupDir)
        {
            _localDllPath = localDllPath;
            _backupDir = backupDir;
            try { Directory.CreateDirectory(_backupDir); } catch { }
            LoadLocalVersion();
        }

        private void LoadLocalVersion()
        {
            try
            {
                // ★ 明确用插件程序集（PluginUpdateManager 定义在插件 DLL 里）
                var asm = typeof(PluginUpdateManager).Assembly;
                var ver = asm.GetName().Version;
                if (ver != null) LocalVersion = ver.ToString();
                else LocalVersion = "0.0.0.0";
            }
            catch { LocalVersion = "0.0.0.0"; }
        }

        // ============================================================
        // 检查远端版本
        // ============================================================
        public void CheckRemoteAsync()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string json = HttpGet(RemoteBaseUrl + "version.json", 15000);
                    var obj = JObject.Parse(json);
                    RemoteVersion = obj["plugin_version"]?.ToString() ?? "";
                    RemoteUrl = obj["plugin_url"]?.ToString() ?? "";
                    LastError = "";
                    Log($"[插件] 检查完成：本地 {LocalVersion} / 远程 {RemoteVersion}");
                    NotifyState();
                }
                catch (Exception ex)
                {
                    LastError = "检查失败：" + ex.Message;
                    Log("[插件] " + LastError);
                    NotifyState();
                }
            });
        }

        /// <summary>
        /// 检测插件路径是否落在云同步目录里。
        /// 返回 null 表示没检测到；否则返回一句人类可读的原因。
        ///
        /// 背景：Win11 的「备份我的文档」会把 Documents 接管给 OneDrive。
        /// 插件调用 File.Copy 写入新 DLL 是成功的，但同步客户端随后
        /// 可能把文件回滚成云端旧版本，导致"提示更新成功、重启后版本没变"。
        /// 这种情况程序拿不到任何异常，只能在写入后延迟复查来发现。
        /// </summary>
        public static string DetectCloudSync(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p = path.Replace('/', '\\').ToLowerInvariant();

            // 1) 路径里直接带同步目录名
            string[] names = {
                "onedrive", "dropbox", "google drive", "googledrive",
                "nutstore", "坚果云", "baidunetdisk", "百度网盘",
                "weiyun", "微云", "icloud drive"
            };
            foreach (var n in names)
            {
                if (p.Contains(n)) return "云同步目录（" + n + "）";
            }

            // 2) Known Folder Move：「我的文档」被重定向到 OneDrive
            try
            {
                var personal = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                    "Personal", null) as string;

                if (!string.IsNullOrEmpty(personal))
                {
                    string real = Environment.ExpandEnvironmentVariables(personal)
                                               .Replace('/', '\\').ToLowerInvariant();
                    if (real.Contains("onedrive") && p.StartsWith(real))
                        return "已被 OneDrive 接管的「我的文档」";
                }
            }
            catch { }

            // 3) 兜底：路径在 \Documents\ 下（未必真的同步，只作温和提醒）
            if (p.Contains("\\documents\\"))
                return "「我的文档」（若开启了云备份，更新可能被还原）";

            return null;
        }

        // ============================================================
        // 下载并覆盖 DLL
        // ============================================================
        public void DownloadAndReplaceAsync()
        {
            if (IsUpdating) return;
            if (string.IsNullOrEmpty(RemoteUrl))
            {
                LastError = "无下载地址";
                NotifyState();
                return;
            }

            IsUpdating = true;
            UpdateCompleted = false;
            LastError = "";
            NotifyState();

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // 1. 下载到临时文件
                    string tmpDir = Path.Combine(Path.GetTempPath(), "FF14TreasureHunter_update");
                    Directory.CreateDirectory(tmpDir);
                    string tmpFile = Path.Combine(tmpDir, "FF14TreasureHunterACT.new.dll");

                    Log($"[插件] 开始下载 {RemoteUrl}");
                    DownloadFile(RemoteUrl, tmpFile, 60000);
                    long size = new FileInfo(tmpFile).Length;
                    Log($"[插件] 下载完成（{size / 1024} KB）");
                    ValidateDownloadedFile(tmpFile, size);

                    // 2. 备份现有 DLL
                    if (File.Exists(_localDllPath))
                    {
                        try
                        {
                            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                            string bakFile = Path.Combine(_backupDir,
                                $"FF14TreasureHunterACT_{ts}.dll.bak");
                            File.Copy(_localDllPath, bakFile, true);
                            Log($"[插件] 已备份：{Path.GetFileName(bakFile)}");
                            CleanOldBackups();
                        }
                        catch (Exception ex)
                        {
                            Log("[插件] 备份失败（继续）：" + ex.Message);
                        }
                    }

                    // 3. 覆盖 DLL
                    string syncHint = DetectCloudSync(_localDllPath);
                    if (syncHint != null)
                        Log($"[插件] 注意：插件位于{syncHint}，写入后可能被同步客户端还原");

                    ApplyFile(tmpFile, _localDllPath);
                    Log($"[插件] 已覆盖：{_localDllPath}");

                    try { File.Delete(tmpFile); } catch { }

                    // 4. 覆盖后校验：确认磁盘上的版本真的变了
                    //    云同步/安全软件的回滚是异步的，必须等一会儿再读，
                    //    否则写完之后立刻验证会"通过"，重启才发现还是旧版本。
                    int waitMs = (syncHint != null) ? 3000 : 600;
                    try { System.Threading.Thread.Sleep(waitMs); } catch { }
                    VerifyApplied(syncHint);

                    UpdateCompleted = true;
                    LastError = "";
                    Log("[插件] 更新完成，请重载插件或重启 ACT 使新版本生效");
                }
                catch (Exception ex)
                {
                    LastError = "更新失败：" + ex.Message;
                    Log("[插件] " + LastError);
                    try { OnFailed?.Invoke(LastError); } catch { }
                }
                finally
                {
                    IsUpdating = false;
                    NotifyState();
                }
            });
        }

        private void CleanOldBackups()
        {
            try
            {
                var files = new DirectoryInfo(_backupDir)
                    .GetFiles("FF14TreasureHunterACT_*.dll.bak");
                Array.Sort(files, (a, b) => b.CreationTime.CompareTo(a.CreationTime));
                for (int i = 5; i < files.Length; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }

        // ============================================================
        // 文件写入 / 校验
        // ============================================================

        /// <summary>下载后校验：必须是 PE 文件，防止把 HTML 错误页当成 DLL 写入</summary>
        private static void ValidateDownloadedFile(string path, long size)
        {
            if (size < 20 * 1024)
                throw new Exception($"下载文件只有 {size} 字节，不像是有效的 DLL");

            var head = new byte[2];
            using (var fs = File.OpenRead(path))
            {
                if (fs.Read(head, 0, 2) != 2)
                    throw new Exception("下载文件读取失败");
            }
            if (head[0] != 0x4D || head[1] != 0x5A)   // 'M' 'Z'
                throw new Exception("下载内容不是有效的 DLL（缺少 MZ 头），可能是服务器错误页");
        }

        /// <summary>
        /// 把新 DLL 写到目标位置。
        ///
        /// 先用普通覆盖；如果目标文件正被 ACT 加载（Windows 不允许覆盖已加载的 DLL），
        /// 退而求其次：把旧文件改名到 .old，再把新文件放进去。
        /// 改名能成功是因为 .NET 加载程序集时带了 FILE_SHARE_DELETE。
        /// 两者都失败时给出可操作的提示，而不是笼统的异常信息。
        /// </summary>
        private void ApplyFile(string src, string dst)
        {
            try
            {
                File.Copy(src, dst, true);
                return;
            }
            catch (Exception ex)
            {
                Log("[插件] 直接覆盖失败，尝试改名替换：" + ex.Message);
            }

            // 回退方案：旧文件改名 → 新文件就位
            string oldPath = dst + ".old";
            try
            {
                if (File.Exists(oldPath)) File.Delete(oldPath);
            }
            catch { }

            try
            {
                if (File.Exists(dst))
                    File.Move(dst, oldPath);
                File.Copy(src, dst, true);
                Log("[插件] 已通过改名方式完成替换（旧文件保留为 .old，可手动删除）");
            }
            catch (Exception ex2)
            {
                throw new Exception(
                    "无法写入插件文件：文件正被 ACT 占用，且改名替换也失败。\n\n" +
                    "解决办法：\n" +
                    "1. 完全退出 ACT（任务管理器确认没有 Advanced Combat Tracker 进程）\n" +
                    "2. 重新点『立即更新』\n" +
                    "3. 或者手动下载 DLL 覆盖到：\n   " + dst + "\n\n" +
                    "原始错误：" + ex2.Message, ex2);
            }
        }

        /// <summary>
        /// 校验磁盘上的 DLL 版本是否真的变成了远程版本。
        /// 这一步能抓住"日志显示成功但实际没生效"的情况
        /// （云同步回滚、安全软件还原、写到了别处等）。
        /// </summary>
        /// <param name="syncHint">DetectCloudSync 的结果，用于给出针对性提示</param>
        private void VerifyApplied(string syncHint)
        {
            if (string.IsNullOrEmpty(RemoteVersion)) return;

            string diskVersion;
            try
            {
                var an = AssemblyName.GetAssemblyName(_localDllPath);
                diskVersion = an.Version != null ? an.Version.ToString() : "";
            }
            catch (Exception ex)
            {
                // 文件可能正被锁着，读不到版本——不算致命，提示用户重载后确认
                Log("[插件] 无法读取磁盘版本（可能被占用）：" + ex.Message);
                return;
            }

            if (string.IsNullOrEmpty(diskVersion))
            {
                Log("[插件] 磁盘版本为空，跳过校验");
                return;
            }

            Log($"[插件] 校验：磁盘版本 {diskVersion} / 远程版本 {RemoteVersion}");
            if (string.Equals(diskVersion, RemoteVersion, StringComparison.OrdinalIgnoreCase))
                return;

            // 版本不对：组装针对性说明
            string cause;
            string advice;
            if (syncHint != null)
            {
                cause =
                    "文件被自动还原了。检测到插件位于" + syncHint + "。\n" +
                    "云同步客户端在写入后把 DLL 回滚成了云端旧版本。";
                advice =
                    "解决办法（任选其一）：\n" +
                    "1. 把 ACT 整个目录移到非同步位置（如 D:\\ACT），\n" +
                    "   这是根治办法\n" +
                    "2. 更新前先暂停 OneDrive/网盘同步，更新完成后再恢复\n" +
                    "3. 手动下载 DLL 覆盖（已验证可行）";
            }
            else
            {
                cause =
                    "常见原因：\n" +
                    "1. 安全软件（360/火绒 等）把文件还原了\n" +
                    "2. 写入的路径与 ACT 实际加载的不是同一个文件\n" +
                    "3. 文件所在目录被云同步/网盘回滚";
                advice =
                    "建议：\n" +
                    "1. 把插件目录加入安全软件白名单后重试\n" +
                    "2. 确认 ACT 实际加载的 DLL 路径（插件日志里有 [插件] 本机 DLL 路径）\n" +
                    "3. 或手动下载 DLL 替换";
            }

            throw new Exception(
                $"文件已写入，但磁盘上的版本仍是 {diskVersion}（期望 {RemoteVersion}）。\n\n" +
                cause + "\n\n" + advice);
        }

        // ============================================================
        // 版本比较
        // ============================================================
        private static int CompareVersion(string a, string b)
        {
            try
            {
                var va = new Version(NormalizeVersion(a));
                var vb = new Version(NormalizeVersion(b));
                return va.CompareTo(vb);
            }
            catch
            {
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string NormalizeVersion(string v)
        {
            if (string.IsNullOrEmpty(v)) return "0.0.0.0";
            // 去掉后缀（如 "1.0.1-beta"）
            int idx = v.IndexOf('-');
            if (idx > 0) v = v.Substring(0, idx);
            var parts = v.Split('.');
            if (parts.Length == 1) return v + ".0.0.0";
            if (parts.Length == 2) return v + ".0.0";
            if (parts.Length == 3) return v + ".0";
            return v;
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

        private void DownloadFile(string url, string targetPath, int timeoutMs)
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
                using (var stream = resp.GetResponseStream())
                using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(fs);
                }
            }
        }

        private void Log(string msg) { try { OnLog?.Invoke(msg); } catch { } }
        private void NotifyState() { try { OnStateChanged?.Invoke(); } catch { } }
    }
}