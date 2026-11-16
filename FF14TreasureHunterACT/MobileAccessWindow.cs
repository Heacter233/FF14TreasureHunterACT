// MobileAccessWindow.cs
using System;
using System.Drawing;
using System.Windows.Forms;
using QRCoder;

namespace FF14TreasureHunterACT
{
    public class MobileAccessWindow : Form
    {
        private OverlayServer _server;

        private RadioButton _rbOverlay;
        private RadioButton _rbMobile;
        private RadioButton _rbHistory;
        private PictureBox _qrBox;
        private Label _lblUrl;
        private Button _btnCopy;
        private CheckBox _chkLan;
        private Label _lblWarn;
        private Label _lblStatus;

        public MobileAccessWindow(OverlayServer server)
        {
            _server = server;

            this.Text = "手机查看";
            this.Size = new Size(420, 560);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Font = new Font("Microsoft YaHei", 9F);
            this.BackColor = Color.FromArgb(248, 245, 235);

            BuildUI();
            RefreshQr();
        }

        private void BuildUI()
        {
            // 切换按钮
            _rbOverlay = new RadioButton
            {
                Text = "悬浮窗",
                Location = new Point(20, 16),
                Size = new Size(85, 24),
                Font = new Font("Microsoft YaHei", 9.5F)
            };
            _rbMobile = new RadioButton
            {
                Text = "手机版",
                Location = new Point(110, 16),
                Size = new Size(85, 24),
                Checked = true,
                Font = new Font("Microsoft YaHei", 9.5F)
            };
            _rbHistory = new RadioButton
            {
                Text = "历史",
                Location = new Point(200, 16),
                Size = new Size(75, 24),
                Font = new Font("Microsoft YaHei", 9.5F)
            };
            _rbOverlay.CheckedChanged += (s, e) => { if (_rbOverlay.Checked) RefreshQr(); };
            _rbMobile.CheckedChanged += (s, e) => { if (_rbMobile.Checked) RefreshQr(); };
            _rbHistory.CheckedChanged += (s, e) => { if (_rbHistory.Checked) RefreshQr(); };

            // 二维码
            _qrBox = new PictureBox
            {
                Location = new Point((420 - 260) / 2 - 10, 54),
                Size = new Size(260, 260),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            // 地址
            _lblUrl = new Label
            {
                Location = new Point(20, 326),
                Size = new Size(370, 46),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Consolas", 9F),
                ForeColor = Color.FromArgb(60, 60, 60),
                Text = ""
            };

            // 复制
            _btnCopy = new Button
            {
                Text = "复制地址",
                Location = new Point(150, 380),
                Size = new Size(110, 30)
            };
            _btnCopy.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(_lblUrl.Text))
                {
                    try { Clipboard.SetText(_lblUrl.Text); }
                    catch { }
                }
            };

            // 局域网开关
            _chkLan = new CheckBox
            {
                Text = "允许局域网访问（手机同 Wi-Fi 下可访问）",
                Location = new Point(20, 424),
                Size = new Size(370, 24),
                Checked = _server != null && _server.LanMode
            };
            _chkLan.CheckedChanged += (s, e) => ToggleLan();

            // 提示
            _lblWarn = new Label
            {
                Location = new Point(20, 452),
                Size = new Size(370, 40),
                ForeColor = Color.FromArgb(180, 90, 60),
                Font = new Font("Microsoft YaHei", 8F),
                Text = "⚠ 仅在家庭 / 私有 Wi-Fi 下开启。\n   公共网络请勿开启。"
            };

            // 状态
            _lblStatus = new Label
            {
                Location = new Point(20, 496),
                Size = new Size(370, 22),
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font("Microsoft YaHei", 8F),
                TextAlign = ContentAlignment.MiddleCenter,
                Text = ""
            };

            this.Controls.Add(_rbOverlay);
            this.Controls.Add(_rbMobile);
            this.Controls.Add(_rbHistory);
            this.Controls.Add(_qrBox);
            this.Controls.Add(_lblUrl);
            this.Controls.Add(_btnCopy);
            this.Controls.Add(_chkLan);
            this.Controls.Add(_lblWarn);
            this.Controls.Add(_lblStatus);
        }

        private void ToggleLan()
        {
            if (_server == null) return;

            Cursor = Cursors.WaitCursor;
            try
            {
                bool ok;
                if (_chkLan.Checked)
                {
                    ok = _server.EnableLan();
                    if (!ok)
                    {
                        _chkLan.Checked = false;
                        MessageBox.Show(
                            "启用局域网访问失败。\n可能原因：端口被占用，或权限不足。",
                            "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                else
                {
                    ok = _server.DisableLan();
                }
            }
            finally
            {
                Cursor = Cursors.Default;
            }

            RefreshQr();
        }

        private void RefreshQr()
        {
            if (_server == null) return;

            string fileName;
            if (_rbHistory.Checked) fileName = "history.html";
            else if (_rbMobile.Checked) fileName = "mobile.html";
            else fileName = "overlay.html";
            string url;

            if (_server.LanMode)
            {
                url = _server.BuildLanUrl(fileName);
            }
            else
            {
                url = $"http://127.0.0.1:{_server.Port}/{fileName}";
            }

            _lblUrl.Text = url;

            if (_server.LanMode)
            {
                // 生成二维码
                try
                {
                    using (var qrGen = new QRCodeGenerator())
                    {
                        var qrData = qrGen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
                        using (var qrCode = new PngByteQRCode(qrData))
                        {
                            byte[] bytes = qrCode.GetGraphic(8);
                            using (var ms = new System.IO.MemoryStream(bytes))
                            {
                                _qrBox.Image = Image.FromStream(ms);
                            }
                        }
                    }
                    _lblStatus.Text = "✓ 局域网已开启，请用手机扫码";
                    _lblStatus.ForeColor = Color.FromArgb(60, 140, 60);
                }
                catch (Exception ex)
                {
                    _qrBox.Image = null;
                    _lblStatus.Text = "二维码生成失败：" + ex.Message;
                    _lblStatus.ForeColor = Color.FromArgb(180, 80, 80);
                }
            }
            else
            {
                _qrBox.Image = null;
                _lblStatus.Text = "局域网访问未开启";
                _lblStatus.ForeColor = Color.FromArgb(120, 120, 120);
            }
        }

        private void InitializeComponent()
        {
            this.SuspendLayout();
            // 
            // MobileAccessWindow
            // 
            this.ClientSize = new System.Drawing.Size(284, 261);
            this.Name = "MobileAccessWindow";
            this.Load += new System.EventHandler(this.MobileAccessWindow_Load);
            this.ResumeLayout(false);

        }

        private void MobileAccessWindow_Load(object sender, EventArgs e)
        {

        }
    }
}