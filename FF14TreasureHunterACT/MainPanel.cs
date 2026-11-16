// MainPanel.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Label = System.Windows.Forms.Label;

namespace FF14TreasureHunterACT
{
    public class MainPanel : UserControl
    {
        // 依赖（Bind 后注入）
        private Database _db;
        private EventProcessor _processor;
        private StateTracker _state;
        private SegmentManager _segmentManager;
        private PostNamazuClient _postnamazu;
        private CostCalculator _costCalc;
        private FinancialManager _financialMgr;
        private CheckBox _chkShowPlayer;
        private CheckBox _chkDebugLog;
        private CheckBox _chkSingleMode;
        private CheckBox _chkAutoDetectParty;
        private bool _suppressSingleModeSave = false;
        // 成本参数快照跟踪（检测变化 → 用旧值存档）
        private int _lastCalcWorkers = -1;
        private double _lastCalcSalary = -1;
        private double _lastCalcMapCost = -1;
        // 工资设置控件
        private TextBox _txtWorkerAdjust;
        private TextBox _txtSalaryPerMap;
        private TextBox _txtMapCost;
        private TextBox _txtExtraCost;
        // 工资计算器控件
        private TextBox _txtCalcTotalMaps;    // 一车总图
        private TextBox _txtCalcTotalSalary;  // 每人总工资
        private Label _lblCalcResult;         // 计算结果
        private double _lastCalculatedPerMap = 0;  // 最近一次计算的每人每图

        // 手动调整（已挖地图 / 出现魔纹）
        private ComboBox _cmbAdjustTarget;    // 调整项目
        private TextBox _txtAdjustStep;       // 每次调整的数量
        private Button _btnAdjustPlus;
        private Button _btnAdjustMinus;
        private Label _lblAdjustInfo;         // 显示累计调整量

        // 手动调整支持的项目：显示名 → 数据库里的 record_name
        // 后续想扩展「出货 / 层级」等，在这里加一项即可（但要补对应的写入逻辑）
        private static readonly string[] AdjustTargetLabels = { "已挖地图数", "出现魔纹数" };
        private static readonly string[] AdjustTargetRecords = { "map_adjust", "seal_adjust" };

                                                   // 发送范围 / 分段通知
        private CheckBox _chkSendSegmentOnly;   // 仅发送当前分段
        private CheckBox _chkSegmentNotify;     // 分段后发送默语
        private Label _lblHtmlVersion;
        private Button _btnHtmlUpdate;
        private HtmlUpdateManager _htmlUpdater;
        private Label _lblDataVersion;
        private Button _btnDataCheck;
        private Button _btnDataMerge;
        private DataUpdateManager _dataUpdater;
        private Label _lblPluginVersion;
        private Button _btnPluginUpdate;
        private PluginUpdateManager _pluginUpdater;
        private MobileAccessWindow _mobileWindow;
        private PredictionManager _predictionManager;
        private OverlayServer _overlayServer;


        // 控件
        private Label _lblPlayer;
        private Label _lblMap;
        private Label _lblParty;
        private Label _lblRunLabel;
        private TextBox _txtLog;
        private TextBox _txtStats;     // 左上：全部汇总（全量，不扣分段）
        private TextBox _txtSegStats;  // 左上（下方）：分段数据（当前段 / 指定分段）
        private TextBox _txtItems;     // 右侧物品
        private TextBox _txtLayers;    // 右侧层级
        private ComboBox _cmbSegment;
        private System.Windows.Forms.Timer _refreshTimer;
        private System.Windows.Forms.Timer _pendingTimer;

        private double _runningSince = 0;
        private double _totalRuntime = 0;
        private string _lastGameStatus = "";

        public MainPanel()
        {
            InitializeUI();
        }

        // ============ UI 构建 ============
        private void InitializeUI()
        {
            this.Dock = DockStyle.Fill;
            this.Font = new Font("Microsoft YaHei", 9F);

            // ============ 顶部状态栏 ============
            var topPanel = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Color.FromArgb(245, 245, 245) };
            _lblPlayer = new Label { Text = "角色：未知", Dock = DockStyle.Left, Width = 240, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
            _lblMap = new Label { Text = "地图：未知", Dock = DockStyle.Left, Width = 280, TextAlign = ContentAlignment.MiddleLeft };
            _lblParty = new Label { Text = "队伍：0 人", Dock = DockStyle.Left, Width = 100, TextAlign = ContentAlignment.MiddleLeft };
            _lblRunLabel = new Label { Text = "运行：-", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 8, 0) };

            _chkShowPlayer = new CheckBox
            {
                Text = "显示角色名",
                Dock = DockStyle.Left,
                Width = 110,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0),
                Checked = ConfigManager.GetBool("ui.show_player_name", false)
            };
            _chkShowPlayer.CheckedChanged += (s, e) =>
            {
                ConfigManager.Set("ui.show_player_name", _chkShowPlayer.Checked);
                try { RefreshStats(); } catch { }
            };

            _chkDebugLog = new CheckBox
            {
                Text = "输出Debug日志",
                Dock = DockStyle.Left,
                Width = 130,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0),
                Checked = ConfigManager.GetBool("ui.debug_log", false)
            };
            _chkDebugLog.CheckedChanged += (s, e) =>
            {
                ConfigManager.Set("ui.debug_log", _chkDebugLog.Checked);
                try { RawLogWriter.Enabled = _chkDebugLog.Checked; } catch { }
                AppendLog(_chkDebugLog.Checked
                    ? "[日志] Debug 日志已启用 → " + (RawLogWriter.CurrentPath ?? "(创建失败)")
                    : "[日志] Debug 日志已关闭");
            };

            // ---------- 单人/组队模式 ----------
            _chkSingleMode = new CheckBox
            {
                Text = "单人挖宝模式",
                Dock = DockStyle.Left,
                Width = 120,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0),
                Checked = ConfigManager.GetBool("detection.single_player_mode", false)
            };
            _chkSingleMode.CheckedChanged += (s, e) =>
            {
                if (_suppressSingleModeSave) return;   // 自动模式下，程序修改不保存
                ConfigManager.Set("detection.single_player_mode", _chkSingleMode.Checked);
                if (_chkAutoDetectParty == null || !_chkAutoDetectParty.Checked)
                {
                    AppendLog(_chkSingleMode.Checked
                        ? "[模式] 单人模式：只记录 083E（直接进背包）"
                        : "[模式] 组队模式：只记录 0039（ROLL 点获得）");
                }
            };

            // ---------- 自动检测队伍复选框 ----------
            _chkAutoDetectParty = new CheckBox
            {
                Text = "自动检测队伍",
                Dock = DockStyle.Left,
                Width = 110,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0),
                Checked = ConfigManager.GetBool("detection.auto_single_mode", true)
            };
            _chkAutoDetectParty.CheckedChanged += (s, e) =>
            {
                ConfigManager.Set("detection.auto_single_mode", _chkAutoDetectParty.Checked);
                // 自动模式时，单选/组队复选框由系统控制 → 禁用手动修改
                _chkSingleMode.Enabled = !_chkAutoDetectParty.Checked;
                AppendLog(_chkAutoDetectParty.Checked
                    ? "[模式] 已启用自动检测：队伍人数 > 1 → 组队；≤ 1 → 单人"
                    : "[模式] 已关闭自动检测：可手动选择");
                try { RefreshStats(); } catch { }
            };
            // 初始状态应用
            _chkSingleMode.Enabled = !_chkAutoDetectParty.Checked;

            topPanel.Controls.Add(_lblRunLabel);
            topPanel.Controls.Add(_chkDebugLog);
            topPanel.Controls.Add(_chkShowPlayer);
            topPanel.Controls.Add(_chkSingleMode);
            topPanel.Controls.Add(_chkAutoDetectParty);   // ★ 新增
            topPanel.Controls.Add(_lblParty);
            topPanel.Controls.Add(_lblMap);
            topPanel.Controls.Add(_lblPlayer);


            // ============ 底部日志 ============
            var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 150 };
            _txtLog = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9F),
                BackColor = Color.FromArgb(250, 250, 250)
            };
            var lblLog = new Label { Text = "运行日志", Dock = DockStyle.Top, Height = 20, Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold) };
            bottomPanel.Controls.Add(_txtLog);
            bottomPanel.Controls.Add(lblLog);


            // ============ 中间：3 列 × 2 行 ============
            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 2,
                Padding = new Padding(6),
                BackColor = Color.FromArgb(240, 240, 240),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.Single   // ★ 每格加边框
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));   // 列 1
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));   // 列 2
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));   // 列 3
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 58));          // 上排（含全部汇总 + 分段数据）
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 42));          // 下排（分段按钮 / 层级 / 物品）

            // ---------- (0,0) 全部汇总 + 分段数据 ----------
            var statsPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
            var lblStats = new Label { Text = "全部汇总", Dock = DockStyle.Top, Height = 22, Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold) };
            _txtStats = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5F),
                ScrollBars = ScrollBars.Vertical
            };

            // 分段数据（位于全部汇总下方）
            var lblSegStats = new Label
            {
                Text = "分段数据",
                Dock = DockStyle.Top,
                Height = 22,
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(70, 110, 170)
            };
            _txtSegStats = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5F),
                ScrollBars = ScrollBars.Vertical
            };

            // 用两行布局：行0=全部汇总，行1=分段数据，各占一半
            // 每行内部靠 Panel + Dock 排列（先加 Fill，再加 Top 标签，标签才不会被遮挡）
            var totalBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0) };
            totalBox.Controls.Add(_txtStats);
            totalBox.Controls.Add(lblStats);

            var segStatsBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0) };
            segStatsBox.Controls.Add(_txtSegStats);
            segStatsBox.Controls.Add(lblSegStats);

            var statsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            statsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            statsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            statsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            statsLayout.Controls.Add(totalBox, 0, 0);
            statsLayout.Controls.Add(segStatsBox, 0, 1);

            statsPanel.Controls.Add(statsLayout);

            // ---------- (1,0) 工资设置 + 计算器 ----------
            var salaryPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
            var lblSalaryTitle = new Label
            {
                Text = "💰 工资设置",
                Dock = DockStyle.Top,
                Height = 22,
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(180, 140, 60)
            };

            var salaryFields = new Panel { Dock = DockStyle.Fill };

            // ===== 2×2 字段布局 =====
            var lblWa = new Label { Text = "打工仔修正：", Location = new Point(0, 4), Size = new Size(80, 22), TextAlign = ContentAlignment.MiddleLeft };
            _txtWorkerAdjust = new TextBox
            {
                Location = new Point(82, 4),
                Size = new Size(60, 22),
                Text = ConfigManager.GetString("salary.worker_adjust", "0")
            };
            var lblSp = new Label { Text = "每人每图：", Location = new Point(150, 4), Size = new Size(80, 22), TextAlign = ContentAlignment.MiddleLeft };
            _txtSalaryPerMap = new TextBox
            {
                Location = new Point(232, 4),
                Size = new Size(60, 22),
                Text = ConfigManager.GetString("salary.per_map", "0")
            };

            var lblMc = new Label { Text = "每图图钱：", Location = new Point(0, 30), Size = new Size(80, 22), TextAlign = ContentAlignment.MiddleLeft };
            _txtMapCost = new TextBox
            {
                Location = new Point(82, 30),
                Size = new Size(60, 22),
                Text = ConfigManager.GetString("salary.map_cost", "0")
            };
            var lblEc = new Label { Text = "附加成本：", Location = new Point(150, 30), Size = new Size(80, 22), TextAlign = ContentAlignment.MiddleLeft };
            _txtExtraCost = new TextBox
            {
                Location = new Point(232, 30),
                Size = new Size(60, 22),
                Text = ConfigManager.GetString("salary.extra_cost", "0")
            };

            // ===== 工资计算器 =====
            var lblCalcTitle = new Label
            {
                Text = "── 工资计算器 ──",
                Location = new Point(0, 58),
                Size = new Size(300, 16),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(140, 140, 140),
                Font = new Font("Microsoft YaHei", 8F)
            };

            var lblCalcMaps = new Label { Text = "一车总图：", Location = new Point(0, 78), Size = new Size(70, 22), TextAlign = ContentAlignment.MiddleLeft };
            _txtCalcTotalMaps = new TextBox
            {
                Location = new Point(72, 78),
                Size = new Size(60, 22)
            };
            var lblCalcSalary = new Label { Text = "每人总工资：", Location = new Point(150, 78), Size = new Size(80, 22), TextAlign = ContentAlignment.MiddleLeft };
            _txtCalcTotalSalary = new TextBox
            {
                Location = new Point(232, 78),
                Size = new Size(60, 22)
            };

            var btnCalc = new Button { Text = "计算", Location = new Point(0, 106), Size = new Size(60, 26) };
            _lblCalcResult = new Label
            {
                Text = "每人每图：--",
                Location = new Point(66, 106),
                Size = new Size(140, 26),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(150, 150, 150),
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
            };
            var btnApply = new Button { Text = "应用", Location = new Point(212, 106), Size = new Size(60, 26), Enabled = false };

            // ===== 手动调整（已挖地图 / 出现魔纹）=====
            var lblAdjustTitle = new Label
            {
                Text = "── 手动调整 ──",
                Location = new Point(0, 138),
                Size = new Size(300, 16),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(140, 140, 140),
                Font = new Font("Microsoft YaHei", 8F)
            };

            _cmbAdjustTarget = new ComboBox
            {
                Location = new Point(0, 158),
                Size = new Size(92, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Microsoft YaHei", 8.5F)
            };
            foreach (var name in AdjustTargetLabels) _cmbAdjustTarget.Items.Add(name);
            _cmbAdjustTarget.SelectedIndex = 0;

            _txtAdjustStep = new TextBox
            {
                Location = new Point(96, 158),
                Size = new Size(34, 24),
                Text = "1",
                TextAlign = HorizontalAlignment.Center,
                Font = new Font("Microsoft YaHei", 8.5F)
            };

            _btnAdjustPlus = new Button
            {
                Text = "+",
                Location = new Point(134, 158),
                Size = new Size(30, 24),
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
            };
            _btnAdjustMinus = new Button
            {
                Text = "−",
                Location = new Point(166, 158),
                Size = new Size(30, 24),
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
            };

            _lblAdjustInfo = new Label
            {
                Text = "累计调整：地图 +0　魔纹 +0",
                Location = new Point(0, 186),
                Size = new Size(230, 18),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(140, 140, 140),
                Font = new Font("Microsoft YaHei", 8F)
            };

            _btnAdjustPlus.Click += (s, e) => DoManualAdjust(+1);
            _btnAdjustMinus.Click += (s, e) => DoManualAdjust(-1);
            _cmbAdjustTarget.SelectedIndexChanged += (s, e) => { try { RefreshStats(); } catch { } };
            // 回车直接加
            _txtAdjustStep.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { DoManualAdjust(+1); e.SuppressKeyPress = true; }
            };

            salaryFields.Controls.Add(lblAdjustTitle);
            salaryFields.Controls.Add(_cmbAdjustTarget);
            salaryFields.Controls.Add(_txtAdjustStep);
            salaryFields.Controls.Add(_btnAdjustPlus);
            salaryFields.Controls.Add(_btnAdjustMinus);
            salaryFields.Controls.Add(_lblAdjustInfo);

            salaryFields.Controls.Add(lblWa);
            salaryFields.Controls.Add(_txtWorkerAdjust);
            salaryFields.Controls.Add(lblSp);
            salaryFields.Controls.Add(_txtSalaryPerMap);
            salaryFields.Controls.Add(lblMc);
            salaryFields.Controls.Add(_txtMapCost);
            salaryFields.Controls.Add(lblEc);
            salaryFields.Controls.Add(_txtExtraCost);
            salaryFields.Controls.Add(lblCalcTitle);
            salaryFields.Controls.Add(lblCalcMaps);
            salaryFields.Controls.Add(_txtCalcTotalMaps);
            salaryFields.Controls.Add(lblCalcSalary);
            salaryFields.Controls.Add(_txtCalcTotalSalary);
            salaryFields.Controls.Add(btnCalc);
            salaryFields.Controls.Add(_lblCalcResult);
            salaryFields.Controls.Add(btnApply);

            salaryPanel.Controls.Add(salaryFields);
            salaryPanel.Controls.Add(lblSalaryTitle);

            EventHandler saveSalary = (s, e) =>
            {
                try
                {
                    ConfigManager.Set("salary.worker_adjust", _txtWorkerAdjust.Text.Trim());
                    ConfigManager.Set("salary.per_map", ParseDoubleOrDefault(_txtSalaryPerMap.Text, 0));
                    ConfigManager.Set("salary.map_cost", ParseDoubleOrDefault(_txtMapCost.Text, 0));
                    ConfigManager.Set("salary.extra_cost", ParseDoubleOrDefault(_txtExtraCost.Text, 0));
                    AppendLog("[工资] 已保存（回主界面点'重载配置'生效）");
                }
                catch (Exception ex) { AppendLog("[工资] 保存失败：" + ex.Message); }
            };
            _txtWorkerAdjust.Leave += saveSalary;
            _txtSalaryPerMap.Leave += saveSalary;
            _txtMapCost.Leave += saveSalary;
            _txtExtraCost.Leave += saveSalary;

            KeyEventHandler enterHandler = (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    saveSalary(s, EventArgs.Empty);
                    e.SuppressKeyPress = true;
                }
            };
            _txtWorkerAdjust.KeyDown += enterHandler;
            _txtSalaryPerMap.KeyDown += enterHandler;
            _txtMapCost.KeyDown += enterHandler;
            _txtExtraCost.KeyDown += enterHandler;

            // ===== 工资计算器事件 =====
            btnCalc.Click += (s, e) =>
            {
                double maps = ParseDoubleOrDefault(_txtCalcTotalMaps.Text, 0);
                double total = ParseDoubleOrDefault(_txtCalcTotalSalary.Text, 0);

                if (maps <= 0)
                {
                    _lblCalcResult.Text = "每人每图：--（总图需 > 0）";
                    _lblCalcResult.ForeColor = Color.FromArgb(200, 100, 100);
                    btnApply.Enabled = false;
                    _lastCalculatedPerMap = 0;
                    return;
                }

                double perMap = total / maps;
                _lblCalcResult.Text = "每人每图：" + perMap.ToString("F2");
                _lblCalcResult.ForeColor = Color.FromArgb(120, 180, 120);
                btnApply.Enabled = true;
                _lastCalculatedPerMap = perMap;
            };

            btnApply.Click += (s, e) =>
            {
                if (_lastCalculatedPerMap <= 0)
                {
                    AppendLog("[计算器] 无有效结果，请先点'计算'");
                    return;
                }
                _txtSalaryPerMap.Text = _lastCalculatedPerMap.ToString("F2");
                saveSalary(s, EventArgs.Empty);   // 复用已有的保存逻辑
                AppendLog($"[计算器] 已应用：每人每图 = {_lastCalculatedPerMap:F2}");
            };

            // ---------- (2,0) 按钮区 + HTML 版本 ----------
            var btnPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), Height = 200 };

            var btnSendTell = new Button { Text = "默语发送", Location = new Point(0, 4), Size = new Size(80, 30) };
            btnSendTell.Click += (s, e) => DoSendSummary("tell");

            var btnSendParty = new Button { Text = "小队发送", Location = new Point(84, 4), Size = new Size(80, 30) };
            btnSendParty.Click += (s, e) => DoSendSummary("party");

            var btnClipboard = new Button { Text = "复制汇总", Location = new Point(168, 4), Size = new Size(80, 30) };
            btnClipboard.Click += (s, e) => DoCopySummary();

            var btnHistory = new Button { Text = "打开历史", Location = new Point(0, 38), Size = new Size(80, 30) };
            btnHistory.Click += (s, e) => DoOpenHistory();

            var btnReloadConfig = new Button { Text = "重载配置", Location = new Point(84, 38), Size = new Size(80, 30) };
            btnReloadConfig.Click += (s, e) => DoReloadConfig();

            var btnOpenSettings = new Button { Text = "⚙️ 设置", Location = new Point(168, 38), Size = new Size(80, 30) };
            btnOpenSettings.Click += (s, e) => DoOpenSettings();

            var lblHtmlTitle = new Label
            {
                Text = "HTML 版本：",
                Location = new Point(0, 136),
                Size = new Size(80, 22),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _lblHtmlVersion = new Label
            {
                Text = "加载中...",
                Location = new Point(82, 136),
                Size = new Size(130, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei", 9F)
            };
            _btnHtmlUpdate = new Button
            {
                Text = "检查更新",
                Location = new Point(216, 135),
                Size = new Size(80, 24),
                Enabled = false
            };
            _btnHtmlUpdate.Click += (s, e) =>
            {
                if (_htmlUpdater == null) return;
                if (_htmlUpdater.UpdateAvailable)
                    _htmlUpdater.DownloadAllAsync();
                else
                    _htmlUpdater.CheckRemoteAsync();
            };

            // ---------- 数据版本行 ----------
            var lblDataTitle = new Label
            {
                Text = "数据版本：",
                Location = new Point(0, 164),
                Size = new Size(80, 22),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _lblDataVersion = new Label
            {
                Text = "加载中...",
                Location = new Point(82, 164),
                Size = new Size(130, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei", 9F)
            };
            _btnDataCheck = new Button
            {
                Text = "检查",
                Location = new Point(216, 163),
                Size = new Size(38, 24),
                Enabled = false
            };
            _btnDataCheck.Click += (s, e) =>
            {
                if (_dataUpdater == null) return;
                _dataUpdater.CheckRemoteAsync();
            };
            _btnDataMerge = new Button
            {
                Text = "合并",
                Location = new Point(258, 163),
                Size = new Size(38, 24),
                Enabled = false
            };
            _btnDataMerge.Click += (s, e) =>
            {
                if (_dataUpdater == null) return;
                if (!_dataUpdater.UpdateAvailable)
                {
                    AppendLog("[数据] 无更新可合并");
                    return;
                }
                if (MessageBox.Show(
                        $"将从服务器合并最新地图和预测规则。\n\n" +
                        $"本地版本：{_dataUpdater.LocalVersion}\n" +
                        $"服务器版本：{_dataUpdater.RemoteVersion}\n\n" +
                        $"本地已存在的地图/规则不会被覆盖，仅新增缺失项。是否继续？",
                        "合并数据", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                    != DialogResult.Yes) return;

                _dataUpdater.MergeAsync(_mapConfig, _predictionManager);
            };

            // ---------- 手机查看按钮 ----------
            var btnMobile = new Button
            {
                Text = "📱 手机查看",
                Location = new Point(0, 72),
                Size = new Size(100, 28)
            };
            btnMobile.Click += (s, e) => DoOpenMobileWindow();

            // ---------- 插件版本行（下移） ----------
            var lblPlugTitle = new Label
            {
                Text = "插件版本：",
                Location = new Point(0, 108),
                Size = new Size(80, 22),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _lblPluginVersion = new Label
            {
                Text = "加载中...",
                Location = new Point(82, 108),
                Size = new Size(130, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei", 9F)
            };
            _btnPluginUpdate = new Button
            {
                Text = "检查",
                Location = new Point(216, 107),
                Size = new Size(80, 24),
                Enabled = false
            };
            
            _btnPluginUpdate.Click += (s, e) =>
            {
                if (_pluginUpdater == null) return;

                // 更新完成状态：显示"如何重载"提示
                if (_pluginUpdater.UpdateCompleted)
                {
                    MessageBox.Show(
                        "新版本已覆盖，但需要重载才能生效。\n\n" +
                        "方法一：在 ACT 的插件标签页，右键挖宝会计 → 重新加载\n" +
                        "方法二：重启 ACT",
                        "如何生效", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // 有更新：走下载流程
                if (_pluginUpdater.UpdateAvailable)
                {
                    // 云同步目录会回滚写入的 DLL，提前警告（不阻断）
                    string syncWarn = "";
                    try
                    {
                        string hint = PluginUpdateManager.DetectCloudSync(_pluginUpdater.LocalDllPath);
                        if (!string.IsNullOrEmpty(hint))
                            syncWarn = $"\n⚠ 注意：插件位于{hint}。\n" +
                                       "这类目录可能会在写入后把文件还原成旧版本，\n" +
                                       "导致更新“成功”但版本没变。\n" +
                                       "建议先把 ACT 移出同步目录，或暂时暂停同步。\n";
                    }
                    catch { }

                    if (MessageBox.Show(
                            $"将下载新版本插件并覆盖当前 DLL。\n\n" +
                            $"本地版本：{_pluginUpdater.LocalVersion}\n" +
                            $"远程版本：{_pluginUpdater.RemoteVersion}\n" +
                            syncWarn +
                            $"\n覆盖完成后需要重载插件或重启 ACT 才能生效。是否继续？",
                            "插件更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                        != DialogResult.Yes) return;
                    _pluginUpdater.DownloadAndReplaceAsync();
                    return;
                }

                // 无更新：重新检查
                _pluginUpdater.CheckRemoteAsync();
            };

            btnPanel.Controls.Add(btnSendTell);

            btnPanel.Controls.Add(btnSendTell);
            btnPanel.Controls.Add(btnSendParty);
            btnPanel.Controls.Add(btnClipboard);
            btnPanel.Controls.Add(btnHistory);
            btnPanel.Controls.Add(btnReloadConfig);
            btnPanel.Controls.Add(btnOpenSettings);
            btnPanel.Controls.Add(lblHtmlTitle);
            btnPanel.Controls.Add(_lblHtmlVersion);
            btnPanel.Controls.Add(_btnHtmlUpdate);
            btnPanel.Controls.Add(lblDataTitle);
            btnPanel.Controls.Add(_lblDataVersion);
            btnPanel.Controls.Add(_btnDataCheck);
            btnPanel.Controls.Add(_btnDataMerge);
            btnPanel.Controls.Add(btnMobile);
            btnPanel.Controls.Add(lblPlugTitle);
            btnPanel.Controls.Add(_lblPluginVersion);
            btnPanel.Controls.Add(_btnPluginUpdate);

            // ---------- (0,1) 分段按钮 + 下拉（竖向排列） ----------
            var segmentPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
            var btnSegment = new Button { Text = "分段", Location = new Point(0, 4), Size = new Size(120, 32) };
            btnSegment.Click += (s, e) => DoCreateSegment();

            // 分段数据来源选择（下移：位于"分段"按钮下方）
            var lblSegHint = new Label
            {
                Text = "选择分段：",
                Location = new Point(0, 44),
                Size = new Size(120, 20),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _cmbSegment = new ComboBox
            {
                Location = new Point(0, 66),
                Size = new Size(150, 24),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbSegment.Items.Add("目前数据");
            _cmbSegment.SelectedIndex = 0;
            _cmbSegment.SelectedIndexChanged += (s, e) => RefreshStats();

            // 分段成功发送默语
            _chkSegmentNotify = new CheckBox
            {
                Text = "分段后发送默语提示",
                Location = new Point(0, 96),
                Size = new Size(200, 22),
                Checked = ConfigManager.GetBool("segment.notify_enabled", true)
            };
            _chkSegmentNotify.CheckedChanged += (s, e) =>
                ConfigManager.Set("segment.notify_enabled", _chkSegmentNotify.Checked);

            // 仅发送当前分段
            _chkSendSegmentOnly = new CheckBox
            {
                Text = "仅发送当前分段数据",
                Location = new Point(0, 120),
                Size = new Size(200, 22),
                Checked = ConfigManager.GetBool("send.segment_only", false)
            };
            _chkSendSegmentOnly.CheckedChanged += (s, e) =>
                ConfigManager.Set("send.segment_only", _chkSendSegmentOnly.Checked);

            segmentPanel.Controls.Add(btnSegment);
            segmentPanel.Controls.Add(lblSegHint);
            segmentPanel.Controls.Add(_cmbSegment);
            segmentPanel.Controls.Add(_chkSegmentNotify);
            segmentPanel.Controls.Add(_chkSendSegmentOnly);

            // ---------- (1,1) 层级 / 其他 ----------
            var layersBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
            var lblLayers = new Label { Text = "层级 / 其他", Dock = DockStyle.Top, Height = 24, Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold) };
            _txtLayers = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5F),
                ScrollBars = ScrollBars.Vertical
            };
            layersBox.Controls.Add(_txtLayers);
            layersBox.Controls.Add(lblLayers);

            // ---------- (2,1) 物品出货 ----------
            var itemsBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
            var lblItems = new Label { Text = "物品出货", Dock = DockStyle.Top, Height = 24, Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold) };
            _txtItems = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5F),
                ScrollBars = ScrollBars.Vertical
            };
            itemsBox.Controls.Add(_txtItems);
            itemsBox.Controls.Add(lblItems);

            // 加入表格
            table.Controls.Add(statsPanel, 0, 0);
            table.Controls.Add(salaryPanel, 1, 0);
            table.Controls.Add(btnPanel, 2, 0);
            table.Controls.Add(segmentPanel, 0, 1);
            table.Controls.Add(layersBox, 1, 1);
            table.Controls.Add(itemsBox, 2, 1);

            // ★ 关键：按此顺序添加（后加的优先 Dock）
            // 效果：topPanel 占顶部 → bottomPanel 占底部 → table 占剩余中间
            this.Controls.Add(table);
            this.Controls.Add(bottomPanel);
            this.Controls.Add(topPanel);
        }
        // ============ 绑定依赖 ============
        public void Bind(Database db, EventProcessor processor, StateTracker state,
                          SegmentManager segmentMgr, PostNamazuClient postnamazu,
                          CostCalculator costCalc, FinancialManager financialMgr,
                          PredictionManager predMgr)
        {
            _db = db;
            _processor = processor;
            _state = state;
            _segmentManager = segmentMgr;
            _postnamazu = postnamazu;
            _costCalc = costCalc;
            _financialMgr = financialMgr;
            _predictionManager = predMgr;

            RefreshSegmentCombo();

            if (_lblRunLabel != null && processor != null)
                _lblRunLabel.Text = "运行：" + processor.CurrentRunLabel;

            // 定时刷新（1 秒）
            _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _refreshTimer.Tick += (s, e) => SafeRefresh();
            _refreshTimer.Start();

            // 处理待办（分段/发送）
            _pendingTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _pendingTimer.Tick += (s, e) => ProcessPending();
            _pendingTimer.Start();
        }

        // ============ 定时刷新 ============
        private void SafeRefresh()
        {
            try { RefreshStats(); }
            catch (Exception ex) { AppendLog("[刷新异常] " + ex.Message); }

            try { _overlayServer?.BroadcastSummary(); }
            catch { }
        }

        private void RefreshStats()
        {
            if (_db == null || _state == null || _processor == null) return;

            // ========== 成本盈亏计算（每次刷新前重新计算并写入数据库） ==========
            try
            {
                if (_financialMgr != null && _mapConfig != null)
                {
                    int workerAdjust = (int)ParseDoubleOrDefault(_txtWorkerAdjust.Text, 0);
                    int workers = _state.GetWorkerCount(workerAdjust);
                    double salaryPerMap = ParseDoubleOrDefault(_txtSalaryPerMap.Text, 0);
                    double mapCost = ParseDoubleOrDefault(_txtMapCost.Text, 0);
                    double extraCost = ParseDoubleOrDefault(_txtExtraCost.Text, 0);

                    // 从地图配置提取物品价格
                    var itemPrices = new Dictionary<string, (double Price, double Bonus)>();
                    foreach (var mc in _mapConfig.Maps.Values)
                    {
                        if (!mc.Enabled) continue;
                        foreach (var it in mc.Items)
                        {
                            if (!string.IsNullOrEmpty(it.Name))
                                itemPrices[it.Name] = (it.Price, it.Bonus);
                        }
                    }

                    bool useWan = ConfigManager.GetBool("money_unit_wan", true);
                    string calcRunLabel = _processor.CurrentRunLabel;

                    // ========== 参数变化 → 用旧值把当前增量存档 ==========
                    // 场景：队伍 3 人挖了 2 张图 → 队伍解散 → 若不存档，成本会瞬间归零
                    if (_lastCalcWorkers >= 0 &&
                        (_lastCalcWorkers != workers ||
                         _lastCalcSalary != salaryPerMap ||
                         _lastCalcMapCost != mapCost))
                    {
                        try
                        {
                            var oldParams = new Dictionary<string, double>
                            {
                                { "WorkerCount",  _lastCalcWorkers },
                                { "SalaryPerMap", _lastCalcSalary },
                                { "MapCost",      _lastCalcMapCost }
                            };
                            _financialMgr.SnapshotCurrentState(_db, calcRunLabel,
                                                                oldParams, itemPrices);
                            AppendLog($"[成本] 参数变化 人数 {_lastCalcWorkers}→{workers}，" +
                                      $"工资 {_lastCalcSalary}→{salaryPerMap}，" +
                                      $"图钱 {_lastCalcMapCost}→{mapCost}，已存档上一阶段");
                        }
                        catch (Exception ex) { AppendLog("[成本-存档] " + ex.Message); }
                    }
                    _lastCalcWorkers = workers;
                    _lastCalcSalary = salaryPerMap;
                    _lastCalcMapCost = mapCost;

                    var result = _financialMgr.RecalculateAll(
                        _db, calcRunLabel, workers, salaryPerMap, mapCost,
                        extraCost, itemPrices, useWan);

                    _db.UpsertRunSummary(calcRunLabel,
                                          result.TotalIncome, result.TotalCost, result.Profit,
                                          extraCost, 0);
                }
            }
            catch (Exception ex) { AppendLog("[成本] " + ex.Message); }

            // 状态栏（根据配置决定是否隐藏角色名）
            string playerText;
            if (ConfigManager.GetBool("ui.show_player_name", false))
            {
                playerText = string.IsNullOrEmpty(_state.SelfName)
                    ? "未知" : _state.SelfName + "@" + _state.SelfWorld;
            }
            else
            {
                playerText = "挖宝小助手@隐";
            }
            _lblPlayer.Text = "角色：" + playerText;
            _lblMap.Text = "地图：" + (string.IsNullOrEmpty(_state.CurrentMap) ? "未知" : _state.CurrentMap);
            _lblParty.Text = "队伍：" + _state.Party.Count + " 人";
            // 自动模式：根据队伍人数更新单人模式复选框
            try
            {
                if (_chkAutoDetectParty != null && _chkAutoDetectParty.Checked && _chkSingleMode != null)
                {
                    bool shouldBeSingle = _state.Party.Count <= 1;
                    if (_chkSingleMode.Checked != shouldBeSingle)
                    {
                        _suppressSingleModeSave = true;
                        _chkSingleMode.Checked = shouldBeSingle;
                        _suppressSingleModeSave = false;
                    }
                }
            }
            catch { }

            string runLabel = _processor.CurrentRunLabel;

            // 全部汇总：始终显示全量数据（不扣任何分段快照）
            var totalData = GetTotalData(_db, runLabel);

            // 分段数据：下拉选中的当前段 / 指定分段
            SegmentData segData;
            int segIndex = (_cmbSegment != null) ? _cmbSegment.SelectedIndex : 0;
            if (segIndex <= 0 || _segmentManager == null ||
                segIndex - 1 >= _segmentManager.Snapshots.Count)
            {
                // 当前段 = 全量 - 已存档快照
                segData = _segmentManager != null
                    ? _segmentManager.GetCurrentSegmentData(_db, runLabel)
                    : totalData;
            }
            else
            {
                segData = _segmentManager.Snapshots[segIndex - 1].Data;
            }

            RenderStats(totalData, segData);

            // 手动调整的累计量 + 减号按钮可用状态
            UpdateManualAdjustUI(segData);
        }

        /// <summary>刷新「手动调整」区的显示：累计调整量 + 减号是否可用</summary>
        private void UpdateManualAdjustUI(SegmentData seg)
        {
            if (_lblAdjustInfo == null || _db == null || seg == null) return;
            try
            {
                int mapAdj = 0, sealAdj = 0;
                string runLabel = _processor != null ? _processor.CurrentRunLabel : null;
                foreach (var c in _db.GetCustomRecordSummary(runLabel))
                {
                    if (c.RecordName == "map_adjust") mapAdj = (int)c.Total;
                    else if (c.RecordName == "seal_adjust") sealAdj = (int)c.Total;
                }

                _lblAdjustInfo.Text = "累计调整：地图 " + FmtAdj(mapAdj) + "　魔纹 " + FmtAdj(sealAdj);
                _lblAdjustInfo.ForeColor = (mapAdj == 0 && sealAdj == 0)
                    ? Color.FromArgb(150, 150, 150)
                    : Color.FromArgb(70, 110, 170);

                if (_btnAdjustMinus != null && _cmbAdjustTarget != null)
                {
                    int idx = _cmbAdjustTarget.SelectedIndex;
                    bool isMap = (idx <= 0);
                    int cur = isMap ? seg.Maps : seg.MagicSeals;
                    _btnAdjustMinus.Enabled = cur > 0;
                }
            }
            catch { }
        }

        /// <summary>带符号显示调整量：+3 / −2 / 0</summary>
        private static string FmtAdj(int v)
        {
            if (v > 0) return "+" + v;
            if (v < 0) return "−" + Math.Abs(v);
            return "0";
        }

        /// <summary>
        /// 全量数据（不扣任何分段快照）。
        /// 只填"全部汇总"要用的标量，不查询物品/层级明细（那部分走分段数据），
        /// 避免每秒刷新时多打三次明细查询。
        ///
        /// 地图数 / 魔纹数会叠加手动调整（map_adjust / seal_adjust）：
        /// 成本计算已经按"有效地图数"算，这里如果还用纯真实值，
        /// 就会出现「12 张图但成本按 14 张算」的矛盾。
        /// </summary>
        private SegmentData GetTotalData(Database db, string runLabel)
        {
            int maps = db.GetTotalMaps(runLabel);
            int seals = db.GetTotalMagicSeals(runLabel);

            try
            {
                foreach (var c in db.GetCustomRecordSummary(runLabel))
                {
                    if (c.RecordName == "map_adjust") maps += (int)c.Total;
                    else if (c.RecordName == "seal_adjust") seals += (int)c.Total;
                }
            }
            catch { }

            var data = new SegmentData
            {
                Maps = Math.Max(0, maps),
                MagicSeals = Math.Max(0, seals),
                SystemGold = db.GetTotalSystemGold(runLabel)
            };

            var summary = db.GetRunSummary(runLabel);
            data.TotalCost = summary?.TotalCost ?? 0;
            data.TotalIncome = summary?.TotalIncome ?? 0;
            data.Profit = summary?.Profit ?? 0;

            return data;
        }

        /// <summary>
        /// 渲染：total = 全量数据（全部汇总），seg = 分段数据（当前段或指定段）。
        /// 物品/层级/其他记录跟随"分段数据"的选择（与悬浮窗推送口径一致）。
        /// </summary>
        private void RenderStats(SegmentData total, SegmentData seg)
        {
            if (total == null) return;
            if (seg == null) seg = total;

            bool useWan = ConfigManager.GetBool("money_unit_wan", true);
            string wanSuffix = useWan ? "万" : "";

            // ---------- 全部汇总（全量） ----------
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"已挖地图数：{total.Maps}");
            sb.AppendLine($"出现魔纹数：{total.MagicSeals}");
            sb.AppendLine($"系统金收入：{(useWan ? total.SystemGold / 10000.0 : total.SystemGold):F4}{wanSuffix}");
            sb.AppendLine($"全部成本：{total.TotalCost:F2}");
            sb.AppendLine($"收入：{total.TotalIncome:F2}");
            sb.AppendLine($"盈亏：{total.Profit:F2}");
            _txtStats.Text = sb.ToString();

            // ---------- 分段数据（当前段 / 指定分段） ----------
            var sbSeg = new System.Text.StringBuilder();
            sbSeg.AppendLine($"已挖地图数：{seg.Maps}");
            sbSeg.AppendLine($"出现魔纹数：{seg.MagicSeals}");
            sbSeg.AppendLine($"系统金收入：{(useWan ? seg.SystemGold / 10000.0 : seg.SystemGold):F4}{wanSuffix}");
            sbSeg.AppendLine($"全部成本：{seg.TotalCost:F2}");
            sbSeg.AppendLine($"收入：{seg.TotalIncome:F2}");
            sbSeg.AppendLine($"盈亏：{seg.Profit:F2}");
            sbSeg.AppendLine($"已分段：{(_segmentManager != null ? _segmentManager.Snapshots.Count : 0)}");
            _txtSegStats.Text = sbSeg.ToString();

            // 物品 / 层级 / 其他记录：按"分段数据"所选来源渲染
            var data = seg;

            // 物品
            var sbItems = new System.Text.StringBuilder();
            if (data.Items != null && data.Items.Count > 0)
            {
                // 按物品所属地图分组
                var groups = new Dictionary<string, List<string>>();
                var order = new List<string>();
                foreach (var it in data.Items)
                {
                    string mapShort = GetItemMapShort(it.ItemName);
                    if (!groups.ContainsKey(mapShort))
                    {
                        groups[mapShort] = new List<string>();
                        order.Add(mapShort);
                    }
                    groups[mapShort].Add(it.ItemName + " ×" + it.Quantity);
                }
                foreach (var mapName in order)
                {
                    sbItems.AppendLine("======= " + mapName + " =======");
                    foreach (var line in groups[mapName])
                        sbItems.AppendLine(line);
                }
            }
            else
            {
                sbItems.AppendLine("（无物品）");
            }
            _txtItems.Text = sbItems.ToString();

            // 层级 + 其他
            var sbLayers = new System.Text.StringBuilder();
            if (data.Layers != null && data.Layers.Count > 0)
            {
                foreach (var ly in data.Layers)
                    sbLayers.AppendLine($"层级：{ly.LayerName} ×{ly.Count}");
            }
            if (data.Customs != null && data.Customs.Count > 0)
            {
                if (sbLayers.Length > 0) sbLayers.AppendLine();
                sbLayers.AppendLine("--- 其他记录 ---");
                foreach (var c in data.Customs)
                    sbLayers.AppendLine($"{c.RecordName}：{c.Total:F0}");
            }
            if (sbLayers.Length == 0) sbLayers.AppendLine("（无）");
            _txtLayers.Text = sbLayers.ToString();
        }

        private string GetItemMapShort(string itemName)
        {
            if (_mapConfig == null) return "未知";
            var (mc, _) = _mapConfig.GetItemByName(itemName);
            if (mc == null) return "未知";
            return string.IsNullOrEmpty(mc.MapItemPrefix) ? mc.Name : mc.MapItemPrefix;
        }
        private string GetLayerMapShort(string layerName)
        {
            if (_mapConfig == null) return "未知";
            var match = _mapConfig.GetLayerByKeyword(layerName);
            if (match.Item1 == null) return "未知";
            return string.IsNullOrEmpty(match.Item1.MapItemPrefix)
                ? match.Item1.Name : match.Item1.MapItemPrefix;
        }

        // 需要 MapConfig 引用
        private MapConfigManager _mapConfig;
        public void SetMapConfig(MapConfigManager mc) { _mapConfig = mc; }

        public void SetOverlayServer(OverlayServer s) { _overlayServer = s; }

        public void SetHtmlUpdateManager(HtmlUpdateManager h)
        {
            _htmlUpdater = h;
            RefreshHtmlVersion();
        }

        public void SetDataUpdateManager(DataUpdateManager d)
        {
            _dataUpdater = d;
            RefreshDataVersion();
        }

        public void RefreshDataVersion()
        {
            if (_lblDataVersion == null || _dataUpdater == null) return;

            if (this.InvokeRequired)
            {
                try { this.BeginInvoke(new Action(RefreshDataVersion)); } catch { }
                return;
            }

            try
            {
                var d = _dataUpdater;

                if (d.IsUpdating)
                {
                    _lblDataVersion.Text = "合并中...";
                    _lblDataVersion.ForeColor = Color.FromArgb(200, 170, 90);
                    _btnDataCheck.Enabled = false;
                    _btnDataMerge.Enabled = false;
                    return;
                }

                if (!string.IsNullOrEmpty(d.LastError))
                {
                    _lblDataVersion.Text = (d.LocalVersion ?? "?") + "（检查失败）";
                    _lblDataVersion.ForeColor = Color.FromArgb(200, 100, 100);
                }
                else if (d.UpdateAvailable)
                {
                    _lblDataVersion.Text = d.LocalVersion + " → " + d.RemoteVersion;
                    _lblDataVersion.ForeColor = Color.FromArgb(230, 150, 60);
                }
                else if (!string.IsNullOrEmpty(d.LocalVersion))
                {
                    _lblDataVersion.Text = d.LocalVersion + " ✓";
                    _lblDataVersion.ForeColor = Color.FromArgb(140, 200, 140);
                }
                else
                {
                    _lblDataVersion.Text = "未安装";
                    _lblDataVersion.ForeColor = Color.FromArgb(180, 180, 180);
                }

                _btnDataCheck.Enabled = true;
                _btnDataMerge.Enabled = d.UpdateAvailable;
            }
            catch { }
        }
        public void SetPluginUpdateManager(PluginUpdateManager p)
        {
            _pluginUpdater = p;
            RefreshPluginVersion();
        }

        public void RefreshPluginVersion()
        {
            if (_lblPluginVersion == null || _pluginUpdater == null) return;

            if (this.InvokeRequired)
            {
                try { this.BeginInvoke(new Action(RefreshPluginVersion)); } catch { }
                return;
            }

            try
            {
                var p = _pluginUpdater;

                if (p.IsUpdating)
                {
                    _lblPluginVersion.Text = "下载中...";
                    _lblPluginVersion.ForeColor = Color.FromArgb(200, 170, 90);
                    _btnPluginUpdate.Text = "更新中";
                    _btnPluginUpdate.Enabled = false;
                    return;
                }

                if (p.UpdateCompleted)
                {
                    _lblPluginVersion.Text = p.RemoteVersion + " ✓ 待重载";
                    _lblPluginVersion.ForeColor = Color.FromArgb(140, 200, 140);
                    _btnPluginUpdate.Text = "如何重载";
                    _btnPluginUpdate.Enabled = true;
                    return;
                }

                if (!string.IsNullOrEmpty(p.LastError))
                {
                    _lblPluginVersion.Text = p.LocalVersion + "（失败）";
                    _lblPluginVersion.ForeColor = Color.FromArgb(200, 100, 100);
                }
                else if (p.UpdateAvailable)
                {
                    _lblPluginVersion.Text = p.LocalVersion + " → " + p.RemoteVersion;
                    _lblPluginVersion.ForeColor = Color.FromArgb(230, 150, 60);
                }
                else if (!string.IsNullOrEmpty(p.LocalVersion))
                {
                    _lblPluginVersion.Text = p.LocalVersion + " ✓";
                    _lblPluginVersion.ForeColor = Color.FromArgb(140, 200, 140);
                }
                else
                {
                    _lblPluginVersion.Text = "未知";
                    _lblPluginVersion.ForeColor = Color.FromArgb(180, 180, 180);
                }

                _btnPluginUpdate.Text = p.UpdateAvailable ? "立即更新" : "检查";
                _btnPluginUpdate.Enabled = true;
            }
            catch { }
        }


        /// <summary>刷新 HTML 版本标签（由 HtmlUpdateManager 状态变化时回调）</summary>
        public void RefreshHtmlVersion()
        {
            if (_lblHtmlVersion == null || _htmlUpdater == null) return;

            // 跨线程安全
            if (this.InvokeRequired)
            {
                try { this.BeginInvoke(new Action(RefreshHtmlVersion)); } catch { }
                return;
            }

            try
            {
                var h = _htmlUpdater;

                if (h.IsUpdating)
                {
                    _lblHtmlVersion.Text = "HTML：更新中...";
                    _lblHtmlVersion.ForeColor = Color.FromArgb(200, 170, 90);
                    _btnHtmlUpdate.Text = "更新中";
                    _btnHtmlUpdate.Enabled = false;
                    return;
                }

                if (!string.IsNullOrEmpty(h.LastError))
                {
                    _lblHtmlVersion.Text = "HTML：" + h.LocalVersion + "（检查失败）";
                    _lblHtmlVersion.ForeColor = Color.FromArgb(200, 100, 100);
                }
                else if (h.UpdateAvailable)
                {
                    _lblHtmlVersion.Text = "HTML：" + h.LocalVersion + " → " + h.RemoteVersion;
                    _lblHtmlVersion.ForeColor = Color.FromArgb(230, 150, 60);
                }
                else if (!string.IsNullOrEmpty(h.LocalVersion))
                {
                    _lblHtmlVersion.Text = "HTML：" + h.LocalVersion + " ✓";
                    _lblHtmlVersion.ForeColor = Color.FromArgb(140, 200, 140);
                }
                else
                {
                    _lblHtmlVersion.Text = "HTML：未安装";
                    _lblHtmlVersion.ForeColor = Color.FromArgb(180, 180, 180);
                }

                _btnHtmlUpdate.Enabled = true;
                _btnHtmlUpdate.Text = h.UpdateAvailable ? "立即更新" : "检查更新";
            }
            catch { }
        }

        private void ProcessPending()
        {
            if (_state == null) return;

            // 自动分段
            if (_state.PendingAutoSegment)
            {
                _state.PendingAutoSegment = false;
                DoCreateSegment();
            }

            // 自动发送
            string channel = _state.PendingAutoSend;
            if (!string.IsNullOrEmpty(channel))
            {
                _state.PendingAutoSend = null;
                DoSendSummary(channel);
            }
        }

        // ============ 动作 ============

        /// <summary>
        /// 手动调整「已挖地图数 / 出现魔纹数」。
        /// 实现方式：写一条 manual_adjust 事件 + 一条 custom_records 记录，
        /// record_name 复用 map_adjust / seal_adjust（SegmentManager 已能消费）。
        /// 负数不允许把计数减到 0 以下（按当前【当前段】的显示值判断）。
        /// </summary>
        private void DoManualAdjust(int sign)
        {
            if (_db == null || _processor == null) return;
            try
            {
                int idx = _cmbAdjustTarget != null ? _cmbAdjustTarget.SelectedIndex : 0;
                if (idx < 0 || idx >= AdjustTargetRecords.Length) idx = 0;

                string recordName = AdjustTargetRecords[idx];
                string label = AdjustTargetLabels[idx];
                bool isMap = (recordName == "map_adjust");

                // 每次调整多少个
                int step = 1;
                if (_txtAdjustStep != null)
                {
                    double parsed;
                    if (double.TryParse(_txtAdjustStep.Text.Trim(), out parsed) && parsed >= 1)
                        step = (int)Math.Min(parsed, 9999);
                }

                string runLabel = _processor.CurrentRunLabel;

                // 当前段显示值（已含历史调整）
                int cur = 0;
                if (_segmentManager != null)
                {
                    var curData = _segmentManager.GetCurrentSegmentData(_db, runLabel);
                    cur = isMap ? curData.Maps : curData.MagicSeals;
                }
                else
                {
                    cur = isMap ? _db.GetTotalMaps(runLabel) : _db.GetTotalMagicSeals(runLabel);
                }

                int delta = step * sign;
                if (delta < 0 && cur + delta < 0)
                {
                    // 只能减到 0。这里不能简单拒绝：用户按一次 -100 期望归零。
                    // 也不能写成 -100（会透支未来的真实数据），只记录实际减少量。
                    delta = -cur;
                    if (delta == 0)
                    {
                        AppendLog($"[手动] {label}当前为 0，不能继续减");
                        return;
                    }
                    AppendLog($"[手动] {label}最多减到 0，本次实际 {(delta > 0 ? "+" : "")}{delta}");
                }

                // 写库：事件 + 调整记录
                string ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                long eventId = _db.LogEvent(ts, "manual_adjust", null, recordName, runLabel);
                _db.AddCustomRecord(eventId, recordName, delta);

                // 地图数会影响工资/图钱成本，日志里把这笔账算给用户看
                string costHint = "";
                if (isMap)
                {
                    try
                    {
                        int workers = _state != null
                            ? _state.GetWorkerCount((int)ParseDoubleOrDefault(_txtWorkerAdjust.Text, 0))
                            : 0;
                        double salary = ParseDoubleOrDefault(_txtSalaryPerMap.Text, 0);
                        double mapCost = ParseDoubleOrDefault(_txtMapCost.Text, 0);
                        double perMap = workers * salary + mapCost;
                        if (perMap > 0 && delta != 0)
                            costHint = $"（成本 {delta * perMap:+0.##;−0.##;0}，每张 {perMap:F2}）";
                    }
                    catch { }
                }

                AppendLog($"[手动] {label} {delta:+#;−#;0}，当前 {cur + delta}{costHint}");
                RefreshStats();
            }
            catch (Exception ex)
            {
                AppendLog("[手动调整] " + ex.Message);
            }
        }

        private void DoCreateSegment()
        {
            if (_segmentManager == null || _db == null || _processor == null) return;
            try
            {
                var now = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                _segmentManager.CreateSegment(_db, _processor.CurrentRunLabel, now);
                RefreshSegmentCombo();
                _cmbSegment.SelectedIndex = 0;

                int segNum = _segmentManager.Snapshots.Count;
                AppendLog($"[分段] 已创建，当前共 {segNum} 段");

                // 分段成功后发送默语提示（可在 UI 中关闭）
                if (_chkSegmentNotify == null || _chkSegmentNotify.Checked)
                {
                    if (_postnamazu != null)
                    {
                        string text = $"/e 分段成功，目前已分段{segNum}。";
                        // 直接尝试发送，不检查 Connected（否则一次失败后会永久跳过）
                        if (_postnamazu.SendCommand(text))
                        {
                            AppendLog($"[分段] 已发送默语：{text}");
                        }
                        else
                        {
                            AppendLog($"[分段] 默语发送失败：{_postnamazu.LastError}");
                        }
                    }
                    else
                    {
                        AppendLog("[分段] 鲶鱼精客户端未初始化，跳过默语");
                    }
                }
            }
            catch (Exception ex) { AppendLog("[分段失败] " + ex.Message); }
        }

        private void RefreshSegmentCombo()
        {
            if (_cmbSegment == null || _segmentManager == null) return;
            _cmbSegment.Items.Clear();
            _cmbSegment.Items.Add("目前数据");
            foreach (var snap in _segmentManager.Snapshots)
                _cmbSegment.Items.Add(snap.Name);
            _cmbSegment.SelectedIndex = 0;
        }

        private MessageData BuildMessageData()
        {
            var runLabel = _processor.CurrentRunLabel;

            // 根据"仅发送当前分段"复选框选择数据源
            SegmentData data;
            if (_chkSendSegmentOnly != null && _chkSendSegmentOnly.Checked)
            {
                data = _segmentManager.GetCurrentSegmentData(_db, runLabel);
            }
            else
            {
                // 发送全量（不减去分段快照，含物品/层级/其他记录明细）
                // 地图/魔纹数叠加手动调整，与「全部汇总」和控制成本口径保持一致
                int totalMaps = _db.GetTotalMaps(runLabel);
                int totalSeals = _db.GetTotalMagicSeals(runLabel);
                var allCustoms = _db.GetCustomRecordSummary(runLabel);
                foreach (var c in allCustoms)
                {
                    if (c.RecordName == "map_adjust") totalMaps += (int)c.Total;
                    else if (c.RecordName == "seal_adjust") totalSeals += (int)c.Total;
                }

                data = new SegmentData
                {
                    Maps = Math.Max(0, totalMaps),
                    MagicSeals = Math.Max(0, totalSeals),
                    SystemGold = _db.GetTotalSystemGold(runLabel)
                };

                var summary = _db.GetRunSummary(runLabel);
                data.TotalCost = summary?.TotalCost ?? 0;
                data.TotalIncome = summary?.TotalIncome ?? 0;
                data.Profit = summary?.Profit ?? 0;

                foreach (var it in _db.GetLootSummary(runLabel))
                    data.Items.Add(new SegmentItemEntry { ItemName = it.ItemName, Quantity = it.Quantity });
                foreach (var ly in _db.GetLayerSummary(runLabel))
                    data.Layers.Add(new SegmentLayerEntry { LayerName = ly.LayerName, Count = ly.Count });
                foreach (var c in allCustoms)
                {
                    if (c.RecordName == "map_adjust" || c.RecordName == "seal_adjust") continue;
                    if (c.Total > 0)
                        data.Customs.Add(new SegmentCustomEntry { RecordName = c.RecordName, Total = c.Total });
                }
            }

            var md = new MessageData
            {
                Maps = data.Maps,
                MagicSeals = data.MagicSeals,
                SystemGold = data.SystemGold
            };

            md.ItemsByMap = new List<(string, string, int)>();
            foreach (var it in data.Items)
                md.ItemsByMap.Add((GetItemMapShort(it.ItemName), it.ItemName, it.Quantity));

            md.LayersByMap = new List<(string, string, int)>();
            foreach (var ly in data.Layers)
                md.LayersByMap.Add((GetLayerMapShort(ly.LayerName), ly.LayerName, ly.Count));

            md.Customs = new List<(string, double)>();
            foreach (var c in data.Customs)
                md.Customs.Add((c.RecordName, c.Total));

            return md;
        }

        private void DoSendSummary(string channel)
        {
            try
            {
                var md = BuildMessageData();
                bool useWan = ConfigManager.GetBool("money_unit_wan", true);
                var lines = MessageComposer.Format(md, true, useWan);

                if (lines.Count == 0) { AppendLog("无数据可发送"); return; }

                if (_postnamazu == null)
                {
                    AppendLog("鲶鱼精客户端未初始化");
                    return;
                }

                AppendLog($"[发送] 开始发送 {lines.Count} 行...");

                // 后台线程执行，避免阻塞 UI
                System.Threading.Tasks.Task.Run(() =>
                {
                    int okCount = 0;
                    foreach (var line in lines)
                    {
                        string cmd = channel == "party" ? "/p " + line : "/e " + line;
                        if (_postnamazu.SendCommand(cmd))
                            okCount++;
                        else
                            break;

                        // 每条之间 300ms 间隔（在后台线程睡眠，不卡 UI）
                        System.Threading.Thread.Sleep(300);
                    }

                    int finalOk = okCount;
                    if (finalOk == 0)
                    {
                        string reason = _postnamazu?.LastError ?? "未知";
                        AppendLog("鲶鱼精发送失败：" + reason);
                    }
                    else
                    {
                        AppendLog($"[发送] 已发送 {finalOk}/{lines.Count} 行");
                        NotificationService.Send("成果发送", "挖宝汇总已发送", "summary");
                    }
                });
            }
            catch (Exception ex) { AppendLog("[发送失败] " + ex.Message); }
        }

        private void DoCopySummary()
        {
            try
            {
                var md = BuildMessageData();
                bool useWan = ConfigManager.GetBool("money_unit_wan", true);
                var lines = MessageComposer.Format(md, true, useWan);
                string full = string.Join(" | ", lines);
                Clipboard.SetText(full);
                AppendLog("[复制] 已复制到剪贴板");
            }
            catch (Exception ex) { AppendLog("[复制失败] " + ex.Message); }
        }

        private void DoOpenHistory()
        {
            try
            {
                int port = ConfigManager.GetInt("ui.overlay_port", 8765);
                if (port <= 0) port = 8765;
                string url = $"http://127.0.0.1:{port}/history.html";

                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                    AppendLog("[历史] 已打开浏览器：" + url);
                }
                catch (Exception ex)
                {
                    AppendLog("[历史] 打开失败：" + ex.Message);
                }
            }
            catch (Exception ex)
            {
                AppendLog("[历史] 异常：" + ex.Message);
            }
        }

        private void DoReloadConfig()
        {
            try
            {
                ConfigManager.Load();
                AppendLog("[配置] 已重载");
            }
            catch (Exception ex) { AppendLog("[配置重载失败] " + ex.Message); }
        }

        private void DoOpenSettings()
        {
            try
            {
                int port = ConfigManager.GetInt("ui.overlay_port", 8765);
                if (port <= 0) port = 8765;
                string url = $"http://127.0.0.1:{port}/settings.html";

                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                    AppendLog("[设置] 已打开浏览器：" + url);
                }
                catch (Exception ex)
                {
                    AppendLog("[设置] 打开失败：" + ex.Message);
                }
            }
            catch (Exception ex)
            {
                AppendLog("[设置] 异常：" + ex.Message);
            }
        }

        private void DoOpenMobileWindow()
        {
            try
            {
                if (_overlayServer == null)
                {
                    AppendLog("[手机查看] 悬浮窗服务器未启动");
                    MessageBox.Show("悬浮窗服务器未启动，无法使用此功能。",
                        "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (_mobileWindow == null || _mobileWindow.IsDisposed)
                {
                    _mobileWindow = new MobileAccessWindow(_overlayServer);
                    _mobileWindow.Show(this);
                }
                else
                {
                    if (_mobileWindow.WindowState == FormWindowState.Minimized)
                        _mobileWindow.WindowState = FormWindowState.Normal;
                    _mobileWindow.BringToFront();
                    _mobileWindow.Activate();
                }
            }
            catch (Exception ex)
            {
                AppendLog("[手机查看] " + ex.Message);
            }
        }


        private static double ParseDoubleOrDefault(string text, double defaultValue)
        {
            if (string.IsNullOrWhiteSpace(text)) return defaultValue;
            if (double.TryParse(text.Trim(), out double v)) return v;
            return defaultValue;
        }

        // ============ 日志输出 ============
        public void AppendLog(string msg)
        {
            if (_txtLog == null) return;
            if (this.InvokeRequired)
            {
                try { this.BeginInvoke(new Action<string>(AppendLog), msg); } catch { }
                return;
            }
            string ts = DateTime.Now.ToString("HH:mm:ss");
            _txtLog.AppendText($"[{ts}] {msg}\r\n");
        }

        // 释放资源
        public void StopTimers()
        {
            try { _refreshTimer?.Stop(); _refreshTimer?.Dispose(); } catch { }
            try { _pendingTimer?.Stop(); _pendingTimer?.Dispose(); } catch { }
        }

        private void InitializeComponent()
        {
            this.SuspendLayout();
            // 
            // MainPanel
            // 
            this.Name = "MainPanel";
            this.Load += new System.EventHandler(this.MainPanel_Load);
            this.ResumeLayout(false);

        }

        private void MainPanel_Load(object sender, EventArgs e)
        {

        }
    }
}