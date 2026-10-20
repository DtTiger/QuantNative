using System;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;
using QuantNative.Core;
using QuantNative.Interop;

namespace QuantNative.UI
{
    /// <summary>
    /// 480×360 面板外壳：管理自选 / 设置 两个页签 + 底部操作区。
    ///
    /// 打开方式（浮窗右键、托盘菜单）都走 AppHost.OpenPanel：
    ///   * 面板出现时先隐藏浮窗，锚定在浮窗原位（保留左下角），相当于「浮窗放大成面板」；
    ///   * 点「缩小」或按 Esc 收起面板并还原浮窗。
    /// 面板是无边框窗口，靠顶部标题区拖动。
    /// </summary>
    public sealed class PanelForm : Form
    {
        private const int PanelWidth = 680;
        private const int PanelHeight = 660;

        private static PanelForm _instance;

        private readonly AppHost _host;
        private WatchlistPage _watchPage;
        private SettingsPage _settingsPage;

        private Button _tabWatch, _tabSettings, _collapse, _checkUpdate, _openMain, _quit;
        private Label _hint;
        private Panel _header;
        private bool _restoreBar;

        private string _tab = "watchlist";
        private string _status = "右键浮窗可随时打开本面板";

        private bool _dragging;
        private bool _moved;
        private Point _dragOriginScreen;
        private Point _windowOrigin;

        private PanelForm(AppHost host)
        {
            _host = host;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Text = "QuantNative 面板";
            BackColor = Theme.Bg;
            DoubleBuffered = true;
            Size = new Size(PanelWidth, PanelHeight);

            BuildUi();

            // 构造里先设了 Size，此时控件还没建，OnResize 会空跑一遍；这里补一次真正的排版。
            LayoutChildren();

            _host.ThemeChanged += ApplyTheme;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;   // 面板同样不进任务栏/Alt-Tab
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.HideFromTaskbar(Handle);
            // 480×360 是逻辑像素，按显示器 DPI 放大成实际窗口尺寸
            float scale = Theme.Scale(this);
            Size = new Size(
                (int)Math.Round(PanelWidth * scale),
                (int)Math.Round(PanelHeight * scale));
        }

        private void BuildUi()
        {
            _header = new Panel();
            _header.BackColor = Theme.Card;
            _header.Cursor = Cursors.SizeAll;
            _header.MouseDown += OnHeaderMouseDown;
            _header.MouseMove += OnHeaderMouseMove;
            _header.MouseUp += OnHeaderMouseUp;
            _header.Paint += OnHeaderPaint;
            Controls.Add(_header);

            // 页签做成两个药丸按钮：选中态实底白字，未选浅底灰字
            _tabWatch = Ui.Button("管理自选", delegate { ShowTab("watchlist"); });
            _tabSettings = Ui.Button("设置", delegate { ShowTab("settings"); });
            Ui.Round(_tabWatch, 8);
            Ui.Round(_tabSettings, 8);
            _collapse = Ui.Button("缩小", delegate { Close2(); });
            Ui.Round(_collapse, 8);
            _header.Controls.Add(_tabWatch);
            _header.Controls.Add(_tabSettings);
            _header.Controls.Add(_collapse);

            _watchPage = new WatchlistPage(_host);
            _settingsPage = new SettingsPage(_host);
            Controls.Add(_watchPage);
            Controls.Add(_settingsPage);

            _hint = Ui.Label(_status, Theme.FSmall, Theme.TextTertiary);
            _hint.AutoSize = false;
            _hint.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_hint);

            _checkUpdate = Ui.Button("检查更新", delegate { Updater.CheckInteractive(_host, SetStatus); });
            _openMain = Ui.PrimaryButton("打开主界面", delegate { _host.ShowMain(); });
            _quit = Ui.Button("退出", delegate { _host.Quit(); });
            Controls.Add(_checkUpdate);
            Controls.Add(_openMain);
            Controls.Add(_quit);

            ShowTab(_tab);
        }

        // ── 打开 / 关闭 ──

        public static void ShowPanel(AppHost host, string tab)
        {
            if (_instance == null || _instance.IsDisposed) _instance = new PanelForm(host);
            _instance.OpenAt(tab);
        }

        private void OpenAt(string tab)
        {
            ShowTab(tab ?? "watchlist");

            // 锚定到浮窗位置（保留左下角），并暂时收起浮窗 —— 视觉上就是浮窗放大成面板
            TickerBarForm bar = _host.Ticker;
            // 面板已经开着时再次 OpenAt（托盘菜单再点一次），不能再重算「原本是否显示」——
            // 那时浮窗正被面板按住，重算会得到 false，收起面板后浮窗就再也回不来了。
            if (!Visible) _restoreBar = bar != null && bar.Visible;
            Screen screen = bar != null ? Screen.FromControl(bar) : Screen.PrimaryScreen;
            Rectangle work = screen.WorkingArea;

            int x = bar != null ? bar.Left : work.Right - Width - 10;
            int y = bar != null ? bar.Bottom - Height : work.Bottom - Height - 60;
            x = Math.Min(Math.Max(x, work.Left), Math.Max(work.Left, work.Right - Width));
            y = Math.Min(Math.Max(y, work.Top), Math.Max(work.Top, work.Bottom - Height));
            Location = new Point(x, y);

            // 不能直接 bar.Hide()：调度器每秒核对一次「该显示却没显示」，会在一秒内
            // 把浮窗重新拉到面板上面，于是「隐藏浮窗」看起来像是失效了。
            if (bar != null) bar.HoldForPanel();

            Show();
            Activate();
            SetStatus("右键浮窗可随时打开本面板");
        }

        /// <summary>收起面板并还原浮窗。</summary>
        private void Close2()
        {
            Hide();
            if (_host.Ticker != null) _host.Ticker.ReleaseFromPanel(_restoreBar);
            else if (_restoreBar) _host.ShowTicker();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                // 搜索框里按 Esc 只清空输入，不收起面板
                Control active = ActiveControl;
                if (!(active is TextBox)) Close2();
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Close2();
                return;
            }
            base.OnFormClosing(e);
        }

        private void ShowTab(string tab)
        {
            _tab = tab == "settings" ? "settings" : "watchlist";
            _watchPage.Visible = _tab == "watchlist";
            _settingsPage.Visible = _tab == "settings";
            if (_tab == "settings") _settingsPage.SyncFromHost();

            StyleTab(_tabWatch, _tab == "watchlist");
            StyleTab(_tabSettings, _tab == "settings");
        }

        /// <summary>页签样式：选中 = 强调色实底 + 白字，未选 = 浅底 + 次级字。</summary>
        private static void StyleTab(Button button, bool active)
        {
            button.ForeColor = active ? Color.White : Theme.TextSecondary;
            button.BackColor = active ? Theme.Accent : Theme.Surface2;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = active ? Theme.Accent : Theme.Surface3;
        }

        /// <summary>标题区自绘：左侧品牌圆点 + 产品名，右侧留给「缩小」。</summary>
        private void OnHeaderPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float scale = Theme.Scale(this);
            int dot = (int)Math.Round(9 * scale);
            int titleWidth = TitleWidth(scale);
            using (SolidBrush accent = new SolidBrush(Theme.Accent))
                g.FillEllipse(accent, 2, (_header.Height - dot) / 2, dot, dot);
            using (SolidBrush brush = new SolidBrush(Theme.TextPrimary))
            using (StringFormat format = Theme.LeftMiddle())
                g.DrawString("QuantNative", Theme.FBold, brush,
                    new RectangleF(dot + (int)Math.Round(8 * scale), 0, titleWidth, _header.Height), format);
        }

        private static int TitleWidth(float scale)
        {
            Size text = TextRenderer.MeasureText("QuantNative", Theme.FBold);
            return text.Width + (int)Math.Round(34 * scale);
        }

        private void SetStatus(string text)
        {
            _status = text ?? "";
            if (_hint != null) _hint.Text = _status;
            _hint.ForeColor = Theme.TextTertiary;
        }

        // ── 布局 ──

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
        }

        private void LayoutChildren()
        {
            float scale = Theme.Scale(this);
            int width = ClientSize.Width;
            int height = ClientSize.Height;
            if (width <= 0 || height <= 0) return;

            // 构造函数里 Size = ... 会先触发一次 OnResize，那时控件还没建好 —— 直接返回。
            if (_header == null || _watchPage == null || _settingsPage == null
                || _tabWatch == null || _tabSettings == null || _collapse == null
                || _checkUpdate == null || _openMain == null || _quit == null || _hint == null) return;

            int pad = (int)Math.Round(10 * scale);
            int headerHeight = Math.Max((int)Math.Round(30 * scale), Ui.ButtonSize(_tabWatch).Height + 4);
            int footerHeight = Math.Max((int)Math.Round(36 * scale), Ui.ButtonSize(_openMain).Height + 8);

            _header.SetBounds(pad, (int)Math.Round(6 * scale), width - pad * 2, headerHeight);
            int titleWidth = TitleWidth(scale);
            int tabWidth = Math.Max((int)Math.Round(82 * scale), Ui.ButtonSize(_tabWatch).Width);
            int settingsWidth = Math.Max((int)Math.Round(56 * scale), Ui.ButtonSize(_tabSettings).Width);
            int collapseWidth = Math.Max((int)Math.Round(54 * scale), Ui.ButtonSize(_collapse).Width);
            _tabWatch.SetBounds(titleWidth, 0, tabWidth, headerHeight - 2);
            _tabSettings.SetBounds(titleWidth + tabWidth + 6, 0, settingsWidth, headerHeight - 2);
            _collapse.SetBounds(_header.Width - collapseWidth, 0, collapseWidth, headerHeight - 2);

            int bodyTop = _header.Bottom + (int)Math.Round(6 * scale);
            int bodyHeight = height - bodyTop - footerHeight - (int)Math.Round(6 * scale);
            if (bodyHeight < 40) bodyHeight = 40;
            _watchPage.SetBounds(pad, bodyTop, width - pad * 2, bodyHeight);
            _settingsPage.SetBounds(pad, bodyTop, width - pad * 2, bodyHeight);

            int footerTop = height - footerHeight;
            int buttonHeight = Math.Max((int)Math.Round(28 * scale), Ui.ButtonSize(_openMain).Height);
            int buttonTop = footerTop + (footerHeight - buttonHeight) / 2;

            int quitWidth = Math.Max((int)Math.Round(48 * scale), Ui.ButtonSize(_quit).Width);
            int mainWidth = Math.Max((int)Math.Round(78 * scale), Ui.ButtonSize(_openMain).Width);
            int updateWidth = Math.Max((int)Math.Round(64 * scale), Ui.ButtonSize(_checkUpdate).Width);
            _quit.SetBounds(width - pad - quitWidth, buttonTop, quitWidth, buttonHeight);
            _openMain.SetBounds(_quit.Left - mainWidth - 6, buttonTop, mainWidth, buttonHeight);
            _checkUpdate.SetBounds(_openMain.Left - updateWidth - 6, buttonTop, updateWidth, buttonHeight);
            if (_checkUpdate.Left < pad + 40) _checkUpdate.Visible = false;
            else _checkUpdate.Visible = true;

            _hint.SetBounds(pad, footerTop + (footerHeight - 16) / 2, Math.Max(40, _checkUpdate.Left - pad * 2), 16);
        }

        public void ApplyTheme()
        {
            BackColor = Theme.Bg;
            _header.BackColor = Theme.Card;
            _hint.ForeColor = Theme.TextTertiary;
            foreach (Control control in Controls)
            {
                // 页签与主按钮由各自逻辑负责配色，这里只管底部的次级功能按钮
                if (!(control is Button) || control == _tabWatch || control == _tabSettings
                    || control == _collapse || control == _openMain) continue;
                Button button = (Button)control;
                button.BackColor = Theme.Surface2;
                button.ForeColor = Theme.TextSecondary;
                button.FlatAppearance.BorderColor = Theme.Border0;
                button.FlatAppearance.MouseOverBackColor = Theme.Surface3;
            }
            _collapse.BackColor = Theme.Surface2;
            _collapse.ForeColor = Theme.TextSecondary;
            _openMain.BackColor = Theme.Accent;
            _openMain.ForeColor = Color.White;
            _openMain.FlatAppearance.BorderColor = Theme.Accent;
            _openMain.FlatAppearance.MouseOverBackColor = Theme.Accent;
            _header.Invalidate();
            ShowTab(_tab);
        }

        // ── 拖动（无边框窗口只能靠标题区） ──

        private void OnHeaderMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _moved = false;
            _dragOriginScreen = Cursor.Position;
            _windowOrigin = Location;
        }

        private void OnHeaderMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Point now = Cursor.Position;
            int dx = now.X - _dragOriginScreen.X;
            int dy = now.Y - _dragOriginScreen.Y;
            if (!_moved && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3)) _moved = true;
            if (_moved) Location = new Point(_windowOrigin.X + dx, _windowOrigin.Y + dy);
        }

        private void OnHeaderMouseUp(object sender, MouseEventArgs e)
        {
            _dragging = false;
            _moved = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _host != null) _host.ThemeChanged -= ApplyTheme;
            base.Dispose(disposing);
        }
    }
}
