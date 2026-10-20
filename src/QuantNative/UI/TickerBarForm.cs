using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using QuantNative.Core;
using QuantNative.Interop;

namespace QuantNative.UI
{
    /// <summary>
    /// 桌面浮窗（常态行情条）。
    ///
    /// 与 Tauri 版的对应关系：
    ///   * 逐像素透明：用 UpdateLayeredWindow 自绘（见 Interop/Native.cs），
    ///     底图透明度可调而文字保持不透明 —— Form.Opacity 做不到这点。
    ///   * 默认显示 6 条，自选超过 6 条时按设置的间隔整屏切换并循环。
    ///   * 左键单击打开主界面；左键拖拽移动（位置落盘）；右键出菜单；悬停暂停轮播。
    ///   * 不进任务栏 / 不进 Alt-Tab（WS_EX_TOOLWINDOW），不抢焦点（ShowWithoutActivation）。
    ///
    /// 2.4.0 新增：
    ///   * 定时隐藏：按「每日显示时段」自动显示/隐藏（见 TickerSchedule）。
    ///   * 自由尺寸：四边 + 四角都能拖，宽度与高度都落盘。
    ///   * 字体与精度：正文与数字都能换成用户本机的字体，所有数字统一 3 位小数。
    /// </summary>
    public sealed class TickerBarForm : Form
    {
        private readonly AppHost _host;

        private readonly Timer _cycleTimer;
        private readonly Timer _scheduleTimer;
        private readonly ContextMenuStrip _menu;

        private Bitmap _surface;
        private int _scale = 1;
        private int _rowHeight;
        private int _fontSize = 11;
        private Font _font;
        private Font _fontMono;
        private bool _fontsDirty = true;

        private int _offset;
        private bool _hovered;
        private bool _dragging;
        private bool _moved;
        private Point _dragOriginScreen;
        private Point _windowOrigin;
        private bool _forceClosed;

        // ── 定时隐藏 ──
        /// <summary>用户手动显示/隐藏后，在下一个时段边界之前不再被调度覆盖。</summary>
        private bool _scheduleOverride;
        /// <summary>面板（管理自选 / 设置）临时接管：期间调度器不得把浮窗拉回来。</summary>
        private bool _panelHeld;
        /// <summary>上一次调度判定的可见性，用于识别「时段边界翻转」。</summary>
        private bool _lastScheduleVisible = true;

        // ── 自由尺寸 ──
        private ResizeZone _resizeZone = ResizeZone.None;
        private Point _resizeOriginScreen;
        private Rectangle _resizeOriginBounds;
        private Cursor _hoverCursor;

        private enum ResizeZone
        {
            None, Left, Right, Top, Bottom,
            TopLeft, TopRight, BottomLeft, BottomRight
        }

        /// <summary>一行要显示的内容（已经从自选 + 行情缓存里取好的快照）。</summary>
        private sealed class Row
        {
            public string Name;
            public double? Price;
            public double? ChangePct;
            /// <summary>"估值" / "净值" / "板块" / "待更新" / 空。</summary>
            public string Kind;
        }

        public TickerBarForm(AppHost host)
        {
            _host = host;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = host.TickerAlwaysOnTop;
            Text = "QuantNative 浮窗";
            BackColor = Color.Black;          // 分层窗口下不参与绘制，仅作占位
            DoubleBuffered = false;
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

            _menu = AppMenus.BuildTickerMenu(host);
            _cycleTimer = new Timer();
            _cycleTimer.Tick += OnCycleTick;

            _scheduleTimer = new Timer();
            _scheduleTimer.Interval = 1000;
            _scheduleTimer.Tick += OnScheduleTick;

            _host.Hub.QuotesUpdated += OnQuotesUpdated;
            _host.WatchlistChanged += OnWatchlistChanged;
            _host.TickerPrefsChanged += OnPrefsChanged;
            _host.ThemeChanged += OnThemeChanged;
        }

        /// <summary>浮窗不抢焦点（启动时尤其重要，否则会打断用户正在做的事）。</summary>
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED;   // 逐像素透明的前提
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW; // 不进任务栏 / Alt-Tab
                return cp;
            }
        }

        // 分层窗口的内容全部由 UpdateLayeredWindow 推送，WinForms 的擦除/绘制要彻底让位，
        // 否则会出现整块黑底把透明区域盖住。
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.EnsureLayered(Handle);
            Native.HideFromTaskbar(Handle);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            RestorePosition();
            Relayout();
            RefreshSchedule(true);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 关闭请求一律转成隐藏（用户应当用托盘/菜单退出），除非是退出流程
            if (!_forceClosed && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _host.Store.SetBool("ticker_visible", false);
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Render();
            RefreshSchedule(false);
        }

        public void ForceClose()
        {
            _forceClosed = true;
            try { _cycleTimer.Stop(); } catch { }
            try { _scheduleTimer.Stop(); } catch { }
            try { Close(); } catch { }
        }

        // ── 定时隐藏 ──

        /// <summary>
        /// 重新计算调度状态。<paramref name="immediate"/> = true 时立刻套用（启动、
        /// 改设置后），否则等下一次 tick。
        /// </summary>
        public void RefreshSchedule(bool immediate)
        {
            bool enabled = _host.TickerScheduleEnabled;
            if (enabled)
            {
                if (!_scheduleTimer.Enabled) _scheduleTimer.Start();
            }
            else
            {
                _scheduleTimer.Stop();
                _scheduleOverride = false;
                _lastScheduleVisible = true;
            }
            if (immediate) ApplySchedule(DateTime.Now);
        }

        /// <summary>
        /// 按「上次退出时是隐藏」启动：保持隐藏，并且在下一个时段边界之前不让调度器拉起来。
        /// 启动时若直接 RefreshSchedule(true)，调度器看到「该显示却没显示」会立刻把浮窗拉出来
        /// —— 用户会觉得昨天明明关掉了，今天开机它自己又冒出来了。
        /// </summary>
        public void StartHiddenByIntent()
        {
            _scheduleOverride = true;
            _lastScheduleVisible = _host.TickerShouldBeVisible(DateTime.Now);
            RefreshSchedule(false);
        }

        private void OnScheduleTick(object sender, EventArgs e)
        {
            ApplySchedule(DateTime.Now);
        }

        private void ApplySchedule(DateTime now)
        {
            if (!_host.TickerScheduleEnabled) return;
            if (_panelHeld) return;                  // 面板接管期间不碰可见性

            bool shouldShow = _host.TickerShouldBeVisible(now);
            bool boundaryFlipped = shouldShow != _lastScheduleVisible;
            _lastScheduleVisible = shouldShow;

            // 到了时段边界，用户之前的手动操作失效，重新交给调度控制
            if (boundaryFlipped) _scheduleOverride = false;
            if (_scheduleOverride) return;

            if (shouldShow && !Visible) ShowBySchedule();
            else if (!shouldShow && Visible) HideBySchedule();
        }

        /// <summary>
        /// 调度触发的显示（不算用户操作，不写 ticker_visible）。
        ///
        /// 历史上的坑：这里原来有一句 `if (!IsHandleCreated) return;`。
        /// 启动时如果正处在「隐藏时段」（比如下午 4 点启动），浮窗从头到尾没调用过 Show()，
        /// 句柄始终不存在 —— 之后用户点「显示浮窗」永远走进这个提前返回，按钮彻底失效。
        /// 现在先保证句柄存在再显示，并且在显示后复查窗口是否真的画出来了。
        /// </summary>
        public void ShowBySchedule()
        {
            if (_forceClosed || IsDisposed) return;
            try
            {
                if (!IsHandleCreated)
                {
                    // 创建句柄但不显示：Show() 会连带做 OnLoad/RestorePosition
                    IntPtr unused = Handle;
                    GC.KeepAlive(unused);
                }
                EnsureOnScreen();
                Show();
                ApplyAlwaysOnTop();
                Render();
                if (!Native.IsWindowVisible(Handle))
                {
                    // 少数情况下（被系统隐藏 / 显示在屏幕外）再补一次
                    ClampToScreen();
                    Native.SetTopMost(Handle, _host.TickerAlwaysOnTop);
                    Render();
                    Log.WarnOnce("ticker:show", "浮窗显示后仍不可见，已重新贴边并置顶", TimeSpan.FromMinutes(1));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("浮窗显示失败: " + ex.Message);
            }
        }

        /// <summary>调度触发的隐藏（不算用户操作，不写 ticker_visible）。</summary>
        public void HideBySchedule()
        {
            if (!Visible || _forceClosed || IsDisposed) return;
            try { Hide(); }
            catch (Exception ex) { Log.Warn("浮窗隐藏失败: " + ex.Message); }
        }

        /// <summary>用户主动显示：在下一个时段边界之前不受调度影响。</summary>
        public void ShowByUser()
        {
            _scheduleOverride = true;
            _panelHeld = false;
            ShowBySchedule();
        }

        /// <summary>用户主动隐藏：在下一个时段边界之前不被调度重新拉起。</summary>
        public void HideByUser()
        {
            _scheduleOverride = true;
            _panelHeld = false;
            HideBySchedule();
        }

        // ── 面板临时接管 ──

        /// <summary>
        /// 面板打开时把浮窗收起来。必须走这里而不是直接 Hide()：
        /// 调度器每秒核对一次「该显示却没显示」，直接 Hide() 会被它在 1 秒内重新拉起来
        /// —— 表现就是浮窗怎么也关不掉、或者关了又自己弹回面板上面。
        /// </summary>
        public void HoldForPanel()
        {
            _panelHeld = true;
            HideBySchedule();
        }

        /// <summary>面板收起后交还给调度器；<paramref name="restore"/> 表示面板打开前浮窗是显示的。</summary>
        public void ReleaseFromPanel(bool restore)
        {
            _panelHeld = false;
            _scheduleOverride = false;
            if (restore && _host.TickerShouldBeVisible(DateTime.Now)) ShowBySchedule();
        }

        /// <summary>浮窗此刻是否真的显示在屏幕上（不是托管标志位）。</summary>
        public bool IsShown
        {
            get { return Visible && IsHandleCreated && Native.IsWindowVisible(Handle); }
        }

        // ── 尺寸 / 位置 ──

        /// <summary>按「条数 × 字号」或用户拖出来的固定高度重算窗口尺寸并重绘。</summary>
        public void Relayout()
        {
            _scale = Math.Max(1, (int)Math.Round(Theme.Scale(this)));
            _fontSize = _host.TickerFontSize;
            _rowHeight = (int)Math.Round(AppHost.TickerRowHeight(_host.TickerFontSize) * (double)_scale);
            _fontsDirty = true;

            int width = (int)Math.Round(_host.TickerWidth * (double)_scale);
            int height = (int)Math.Round(_host.TickerBarHeight() * (double)_scale);
            Size = new Size(width, height);

            DisposeSurface();
            ClampToScreen();
            Render();
            UpdateCycleTimer();
        }

        public void ApplyAlwaysOnTop()
        {
            TopMost = _host.TickerAlwaysOnTop;
            if (IsHandleCreated) Native.SetTopMost(Handle, TopMost);
        }

        private void RestorePosition()
        {
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            int x = Screen.PrimaryScreen.WorkingArea.Right - Width - 10;
            int y = Screen.PrimaryScreen.WorkingArea.Bottom - Height - 60;

            int savedX, savedY;
            if (int.TryParse(_host.GetSetting("ticker_x", ""), out savedX)
                && int.TryParse(_host.GetSetting("ticker_y", ""), out savedY)
                && savedX + Width > work.Left && savedY + Height > work.Top
                && savedX < work.Right && savedY < work.Bottom)
            {
                x = savedX;
                y = savedY;
            }
            Location = new Point(x, y);
        }

        private void ClampToScreen()
        {
            Screen screen = Screen.FromControl(this) ?? Screen.PrimaryScreen;
            Rectangle work = screen.WorkingArea;
            int maxX = Math.Max(work.Left, work.Right - Width);
            int maxY = Math.Max(work.Top, work.Bottom - Height);
            int x = Math.Min(Math.Max(Location.X, work.Left), maxX);
            int y = Math.Min(Math.Max(Location.Y, work.Top), maxY);
            if (x != Location.X || y != Location.Y) Location = new Point(x, y);
        }

        /// <summary>
        /// 把窗口拉回工作区。换分辨率、拔掉副屏、任务栏挪位之后，
        /// 落盘的位置可能落在可视区之外 —— 那时「显示浮窗」看起来就像什么也没发生。
        /// </summary>
        public void EnsureOnScreen()
        {
            try { ClampToScreen(); }
            catch (Exception ex) { Log.Warn("浮窗位置修正失败: " + ex.Message); }
        }

        private void PersistPosition()
        {
            Screen screen = Screen.FromControl(this) ?? Screen.PrimaryScreen;
            Rectangle work = screen.WorkingArea;
            if (Bounds.Right < work.Left + 20 || Bounds.Bottom < work.Top + 10) return;  // 拖到屏幕外不记
            _host.SetSetting("ticker_x", Location.X.ToString());
            _host.SetSetting("ticker_y", Location.Y.ToString());
        }

        // ── 数据 ──

        private List<Row> BuildRows()
        {
            List<Row> rows = new List<Row>();
            List<WatchItem> items = _host.Watchlist;
            for (int i = 0; i < items.Count; i++)
            {
                WatchItem item = items[i];
                if (!item.TickerEnabled) continue;
                Quote quote = _host.Hub.GetQuote(item.Code, item.Market);
                if (Symbols.IsOpenFund(item.Code))
                {
                    // 浮窗里的基金只占一行：正式净值或板块等权估值，跟着板块实时跳。
                    // （旧版是一只基金按关联板块铺开成好几行，看不到「基金自己的涨跌」。）
                    FundEstimate estimate = _host.EstimateFund(item.Code);
                    FundDisplay display = FundPresentation.Describe(quote, estimate, MarketClock.NowCst());
                    if (display != null && display.HasLivePct)
                    {
                        rows.Add(new Row
                        {
                            Name = item.Name,
                            Price = display.Price,
                            ChangePct = display.Pct,
                            Kind = display.Estimated ? "估值" : (quote == null ? null : quote.PriceKind)
                        });
                        continue;
                    }
                    // 没有今日净值、也没有板块估值：**不要**拿上一交易日的净值涨跌顶上去
                    // （用户看到「跌幅 0.08%」正是上一交易日的数据）。这时浮窗只列板块。
                    var links = _host.GetFundBoards(item.Code);
                    if (links.Count == 0)
                    {
                        rows.Add(new Row { Name = item.Name + " · 未关联", Kind = "板块" });
                        continue;
                    }
                    // 板块行情还没到 / 全是过期数据：逐条列出来并标「待更新」，总比空白强
                    foreach (var link in links)
                    {
                        BoardQuote board = _host.Hub.Boards.Get(link.Code);
                        bool current = board != null && board.IsCurrent(MarketClock.NowCst());
                        rows.Add(new Row { Name = link.Name + " · " + Symbols.FormatCode(item.Code), Kind = current ? "板块" : "待更新", ChangePct = current ? (double?)board.ChangePct : null });
                    }
                    continue;
                }
                Row row = new Row();
                row.Name = item.Name;
                row.Price = quote == null ? (double?)null : quote.Price;
                row.ChangePct = quote == null ? (double?)null : quote.ChangePct;
                row.Kind = quote == null ? null : quote.PriceKind;
                rows.Add(row);
            }
            return rows;
        }

        private void UpdateCycleTimer()
        {
            List<Row> rows = BuildRows();
            int visible = Math.Min(SlotCount(), rows.Count);
            if (rows.Count > visible && visible > 0)
            {
                int interval = _host.TickerIntervalMs;
                if (interval < 500) interval = 500;
                if (_cycleTimer.Interval != interval) _cycleTimer.Interval = interval;
                _cycleTimer.Start();
            }
            else
            {
                _cycleTimer.Stop();
                _offset = 0;
            }
            if (rows.Count > 0 && _offset >= rows.Count) _offset = 0;
        }

        /// <summary>
        /// 当前窗口高度能塞下多少行。拖动上下边框后高度变了、能显示的行数就跟着变，
        /// 不必再去设置页改「常态条数」—— 这是「自由缩放」和「条数」两个设置的衔接点：
        /// 高度自动（ticker_height=0）时它恰好等于常态条数，两边算法一致。
        /// </summary>
        private int SlotCount()
        {
            int logicalHeight = (int)Math.Round(Height / (double)_scale);
            int slots = AppHost.RowsForHeight(logicalHeight, _host.TickerFontSize, AppHost.MaxTickerRows);
            int configured = _host.TickerRows;
            // 自动高度下以「常态条数」为准，固定高度下以窗口实际能放下的行为准
            return _host.TickerHeight > 0 ? slots : Math.Min(configured, slots);
        }

        private void OnCycleTick(object sender, EventArgs e)
        {
            if (_hovered || _menu.Visible) return;
            List<Row> rows = BuildRows();
            int visible = Math.Min(SlotCount(), rows.Count);
            if (rows.Count <= visible || visible <= 0) return;
            _offset = (_offset + visible) % rows.Count;
            Render();
        }

        private void OnQuotesUpdated()
        {
            _host.SyncNamesFromQuotes(_host.Hub);
            Render();
            UpdateCycleTimer();
        }

        private void OnWatchlistChanged()
        {
            _offset = 0;
            Render();
            UpdateCycleTimer();
        }

        private void OnPrefsChanged()
        {
            ApplyAlwaysOnTop();
            Relayout();
            RefreshSchedule(false);
        }

        private void OnThemeChanged()
        {
            _fontsDirty = true;
            Render();
        }

        // ── 绘制 ──

        private void DisposeSurface()
        {
            if (_surface != null)
            {
                _surface.Dispose();
                _surface = null;
            }
        }

        private void EnsureFonts()
        {
            if (!_fontsDirty) return;
            if (_font != null) _font.Dispose();
            if (_fontMono != null) _fontMono.Dispose();
            float pt = Math.Max(6f, _fontSize * _scale * 0.75f);   // CSS px → pt
            // 字体族由设置决定（Theme.UiFamily / MonoFamily，见 AppHost.ApplyTickerFonts）；
            // 用户选的字体不存在时 Theme 内部自动退回默认字体，不会抛异常。
            _font = Theme.Ui(pt, FontStyle.Regular);
            _fontMono = Theme.Mono(pt, FontStyle.Bold);
            _fontsDirty = false;
        }

        private void Render()
        {
            if (!IsHandleCreated || IsDisposed) return;

            EnsureFonts();
            int width = Width;
            int height = Height;
            if (width <= 0 || height <= 0) return;

            if (_surface == null || _surface.Width != width || _surface.Height != height)
            {
                DisposeSurface();
                _surface = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            }

            List<Row> rows = BuildRows();
            using (Graphics g = Graphics.FromImage(_surface))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // 透明底上必须用灰度抗锯齿：ClearType 是子像素渲染，会在透明面上留彩边
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;

                DrawPlate(g, width, height);
                DrawRows(g, rows, width, height);
                DrawResizeGrip(g, width, height);
            }

            Native.PushLayeredBitmap(Handle, _surface, Left, Top);
        }

        private void DrawPlate(Graphics g, int width, int height)
        {
            int alpha = (int)Math.Round(Math.Min(100, Math.Max(0, _host.TickerOpacity)) / 100.0 * 255.0);
            if (alpha == 0) return; // 纯文字模式：连边框也不绘制
            Color plate = Theme.TickerPlate;
            int radius = Math.Max(2, (int)Math.Round(9 * (double)_scale));

            Rectangle outer = new Rectangle(0, 0, width, height);
            using (GraphicsPath path = Theme.RoundedRect(outer, radius))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(alpha, plate.R, plate.G, plate.B)))
            {
                g.FillPath(brush, path);
            }

            Rectangle inner = new Rectangle(1, 1, width - 2, height - 2);
            Color borderColor = Theme.Dark
                ? Color.FromArgb(30, 255, 255, 255)
                : Color.FromArgb(26, 0, 0, 0);
            using (GraphicsPath path = Theme.RoundedRect(inner, Math.Max(2, radius - 1)))
            using (Pen pen = new Pen(borderColor, 1f))
            {
                g.DrawPath(pen, path);
            }
        }

        /// <summary>右下角的三道斜线，提示「这里可以拖动改尺寸」。</summary>
        private void DrawResizeGrip(Graphics g, int width, int height)
        {
            if (!_hovered) return;
            int unit = Math.Max(2, (int)Math.Round(3 * (double)_scale));
            Color color = Theme.Dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(60, 0, 0, 0);
            using (Pen pen = new Pen(color, 1.4f))
            {
                for (int i = 1; i <= 3; i++)
                {
                    int offset = i * unit * 2;
                    g.DrawLine(pen, width - 3 - offset, height - 4, width - 4, height - 3 - offset);
                }
            }
        }

        private void DrawRows(Graphics g, List<Row> rows, int width, int height)
        {
            int padX = (int)Math.Round(10 * (double)_scale);
            int padY = (int)Math.Round(5 * (double)_scale);
            int visible = Math.Min(SlotCount(), rows.Count);

            if (visible <= 0)
            {
                string text = _host.Watchlist.Count == 0 ? "暂无自选 · 右键管理自选" : "暂未设置播报标的";
                using (StringFormat format = Theme.CenterMiddle())
                using (SolidBrush brush = new SolidBrush(Theme.TextTertiary))
                {
                    g.DrawString(text, _font, brush, new RectangleF(0, 0, width, height), format);
                }
                return;
            }

            // 列宽按实际文本测量：数字统一 3 位小数后等宽，整列小数点严格对齐
            int pillPad = (int)Math.Round(7 * (double)_scale);
            int rawPctWidth = 0, rawPriceWidth = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                rawPctWidth = Math.Max(rawPctWidth, Theme.Measure(g, PctText(rows[i]), _fontMono));
                rawPriceWidth = Math.Max(rawPriceWidth, Theme.Measure(g, PriceText(rows[i]), _fontMono));
            }
            int pctWidth = rawPctWidth + pillPad * 2;
            int priceWidth = rawPriceWidth + (int)Math.Round(8 * (double)_scale);
            int gap = (int)Math.Round(6 * (double)_scale);
            int nameWidth = Math.Max(10, width - padX * 2 - priceWidth - pctWidth - gap);

            int contentHeight = visible * _rowHeight;
            int top0 = padY + Math.Max(0, (height - padY * 2 - contentHeight) / 2);

            using (StringFormat left = Theme.LeftMiddle())
            using (StringFormat right = Theme.RightMiddle())
            using (StringFormat center = Theme.CenterMiddle())
            using (SolidBrush textBrush = new SolidBrush(Theme.TextPrimary))
            using (SolidBrush dimBrush = new SolidBrush(Theme.TextTertiary))
            {
                for (int i = 0; i < visible; i++)
                {
                    Row row = rows[(_offset + i) % rows.Count];
                    int top = top0 + i * _rowHeight;

                    g.DrawString(row.Name ?? "", _font, textBrush,
                        new RectangleF(padX, top, nameWidth, _rowHeight), left);

                    int priceX = width - padX - pctWidth - gap - priceWidth;
                    if (row.Price.HasValue)
                    {
                        using (SolidBrush priceBrush = new SolidBrush(Theme.Pct(row.ChangePct)))
                        {
                            g.DrawString(PriceText(row), _fontMono, priceBrush,
                                new RectangleF(priceX, top, priceWidth, _rowHeight), right);
                        }
                    }
                    else
                    {
                        g.DrawString(row.Kind == "板块" ? "板块" : row.Kind == "待更新" ? "待更新" : "--", _font, dimBrush,
                            new RectangleF(priceX, top, priceWidth, _rowHeight), right);
                    }

                    // 涨跌幅做成色块药丸：一眼能看出涨跌，而不是只靠文字颜色。
                    // 纯文字模式（不透明度 0）下不画底色，保持「只有文字」的约定。
                    Rectangle pill = new Rectangle(width - padX - pctWidth, top + (_rowHeight - PillHeight()) / 2, pctWidth, PillHeight());
                    if (row.ChangePct.HasValue)
                    {
                        if (_host.TickerOpacity > 0) Theme.DrawPill(g, pill, Theme.PctSoft(row.ChangePct));
                        using (SolidBrush pctBrush = new SolidBrush(Theme.Pct(row.ChangePct)))
                            g.DrawString(PctText(row), _fontMono, pctBrush, pill, center);
                    }
                    else
                    {
                        g.DrawString("--", _fontMono, dimBrush, pill, center);
                    }
                }
            }
        }

        private int PillHeight()
        {
            return Math.Max(12, (int)Math.Round(_fontSize * 1.25 * _scale));
        }

        private static string PriceText(Row row)
        {
            if (!row.Price.HasValue) return "--";
            // 浮窗数字统一 3 位小数（含正式净值，不再用 4 位）
            string text = Symbols.FormatPrice3(row.Price);
            // 场外基金盘中是估算净值，加 * 与正式净值区分
            if (row.Kind == "估值") text += "*";
            return text;
        }

        private static string PctText(Row row)
        {
            return Symbols.FormatPct3(row.ChangePct);
        }

        // ── 交互 ──

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _resizeZone = HitTest(e.Location);
                if (_resizeZone != ResizeZone.None)
                {
                    _resizeOriginScreen = PointToScreen(e.Location);
                    _resizeOriginBounds = Bounds;
                    Capture = true;      // 拖出窗口范围也要继续收到 MouseMove
                    base.OnMouseDown(e);
                    return;
                }
                _dragging = true;
                _moved = false;
                _dragOriginScreen = PointToScreen(e.Location);
                _windowOrigin = Location;
                Capture = true;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_resizeZone != ResizeZone.None && (e.Button & MouseButtons.Left) == MouseButtons.Left)
            {
                ApplyResize(PointToScreen(e.Location));
            }
            else if (_dragging && (e.Button & MouseButtons.Left) == MouseButtons.Left)
            {
                Point now = PointToScreen(e.Location);
                int dx = now.X - _dragOriginScreen.X;
                int dy = now.Y - _dragOriginScreen.Y;
                // 3px 阈值区分「点击」与「拖拽」（与旧版一致）
                if (!_moved && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3)) _moved = true;
                if (_moved) Location = new Point(_windowOrigin.X + dx, _windowOrigin.Y + dy);
            }
            else
            {
                UpdateCursor(HitTest(e.Location));
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            try { Capture = false; } catch { }
            if (e.Button == MouseButtons.Left)
            {
                if (_resizeZone != ResizeZone.None)
                {
                    _resizeZone = ResizeZone.None;
                    // 一次落盘宽 / 高 / 条数，随后由 TickerPrefsChanged 统一重排
                    _host.SetTickerUserSize(
                        (int)Math.Round(Width / (double)_scale),
                        (int)Math.Round(Height / (double)_scale));
                    PersistPosition();
                }
                else
                {
                    bool wasMoved = _moved;
                    _dragging = false;
                    _moved = false;
                    if (wasMoved) PersistPosition();
                    else _host.ShowMain();
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                _menu.Show(this, new Point(e.X, e.Y));
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Render();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            UpdateCursor(ResizeZone.None);
            Render();
            base.OnMouseLeave(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) _host.ShowMain();
            base.OnKeyDown(e);
        }

        // ── 自由调整尺寸 ──

        private int GripSize()
        {
            return Math.Max(4, (int)Math.Round(6 * (double)_scale));
        }

        private ResizeZone HitTest(Point point)
        {
            if (point.X < 0 || point.Y < 0 || point.X >= Width || point.Y >= Height) return ResizeZone.None;
            int grip = GripSize();
            bool left = point.X < grip;
            bool right = point.X >= Width - grip;
            bool top = point.Y < grip;
            bool bottom = point.Y >= Height - grip;

            if (left && top) return ResizeZone.TopLeft;
            if (right && top) return ResizeZone.TopRight;
            if (left && bottom) return ResizeZone.BottomLeft;
            if (right && bottom) return ResizeZone.BottomRight;
            if (left) return ResizeZone.Left;
            if (right) return ResizeZone.Right;
            if (top) return ResizeZone.Top;
            if (bottom) return ResizeZone.Bottom;
            return ResizeZone.None;
        }

        private static Cursor CursorFor(ResizeZone zone)
        {
            switch (zone)
            {
                case ResizeZone.Left:
                case ResizeZone.Right: return Cursors.SizeWE;
                case ResizeZone.Top:
                case ResizeZone.Bottom: return Cursors.SizeNS;
                case ResizeZone.TopLeft: return Cursors.SizeNWSE;
                case ResizeZone.TopRight: return Cursors.SizeNESW;
                case ResizeZone.BottomLeft: return Cursors.SizeNESW;
                case ResizeZone.BottomRight: return Cursors.SizeNWSE;
                default: return Cursors.SizeAll;
            }
        }

        private void UpdateCursor(ResizeZone zone)
        {
            Cursor wanted = CursorFor(zone);
            if (_hoverCursor == wanted) return;
            _hoverCursor = wanted;
            try { Cursor = wanted; } catch { }
        }

        private void ApplyResize(Point screenPoint)
        {
            int dx = screenPoint.X - _resizeOriginScreen.X;
            int dy = screenPoint.Y - _resizeOriginScreen.Y;

            Rectangle bounds = _resizeOriginBounds;
            int left = bounds.Left, top = bounds.Top, right = bounds.Right, bottom = bounds.Bottom;

            bool horizontal = _resizeZone == ResizeZone.Left || _resizeZone == ResizeZone.Right
                || _resizeZone == ResizeZone.TopLeft || _resizeZone == ResizeZone.BottomLeft
                || _resizeZone == ResizeZone.TopRight || _resizeZone == ResizeZone.BottomRight;
            bool vertical = _resizeZone == ResizeZone.Top || _resizeZone == ResizeZone.Bottom
                || _resizeZone == ResizeZone.TopLeft || _resizeZone == ResizeZone.BottomLeft
                || _resizeZone == ResizeZone.TopRight || _resizeZone == ResizeZone.BottomRight;

            int minWidth = (int)Math.Round(AppHost.MinTickerWidth * (double)_scale);
            int maxWidth = (int)Math.Round(AppHost.MaxTickerWidth * (double)_scale);
            int minHeight = (int)Math.Round(AppHost.MinTickerHeight * (double)_scale);
            int maxHeight = (int)Math.Round(AppHost.MaxTickerHeight * (double)_scale);

            if (horizontal)
            {
                if (_resizeZone == ResizeZone.Left || _resizeZone == ResizeZone.TopLeft || _resizeZone == ResizeZone.BottomLeft)
                    left = Math.Min(right - minWidth, Math.Max(right - maxWidth, left + dx));
                else
                    right = Math.Max(left + minWidth, Math.Min(left + maxWidth, right + dx));
            }
            if (vertical)
            {
                if (_resizeZone == ResizeZone.Top || _resizeZone == ResizeZone.TopLeft || _resizeZone == ResizeZone.TopRight)
                    top = Math.Min(bottom - minHeight, Math.Max(bottom - maxHeight, top + dy));
                else
                    bottom = Math.Max(top + minHeight, Math.Min(top + maxHeight, bottom + dy));
            }

            Rectangle target = Rectangle.FromLTRB(left, top, right, bottom);
            if (target == Bounds) return;

            Bounds = target;
            DisposeSurface();
            Render();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 必须先退订：AppHost 的事件比窗口活得久，浮窗销毁后仍会被
                // TickerPrefsChanged / ThemeChanged 回调到，那时再改 TopMost、Size
                // 就会抛 ObjectDisposedException（改字号 / 换配色时必现）。
                _host.Hub.QuotesUpdated -= OnQuotesUpdated;
                _host.WatchlistChanged -= OnWatchlistChanged;
                _host.TickerPrefsChanged -= OnPrefsChanged;
                _host.ThemeChanged -= OnThemeChanged;
                _cycleTimer.Stop();
                _cycleTimer.Dispose();
                _scheduleTimer.Stop();
                _scheduleTimer.Dispose();
                if (_menu != null) _menu.Dispose();
                DisposeSurface();
                if (_font != null) _font.Dispose();
                if (_fontMono != null) _fontMono.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
