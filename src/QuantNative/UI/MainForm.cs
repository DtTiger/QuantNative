using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using QuantNative.Core;
using QuantNative.Data;

namespace QuantNative.UI
{
    /// <summary>
    /// 主窗口：指数条 + 自选表格 + 个股详情 + 市场概览 + 状态栏。
    /// 关闭时隐藏到托盘（与旧版一致），浮窗才是常驻界面。
    /// </summary>
    public sealed class MainForm : Form
    {
        private readonly AppHost _host;

        private Panel _topBar;
        private Button _addButton, _sourceButton, _themeButton, _tickerButton, _panelButton, _refreshButton;
        private IndexStrip _indexStrip;
        private CardPanel _gridCard, _detailCard;
        private DataGridView _grid;
        private DetailPanel _detail;
        private OverviewPanel _overview;
        private Label _statusLeft, _statusRight;
        private ContextMenuStrip _sourceMenu, _rowMenu;

        private const int ColName = 0;
        private const int ColCode = 1;
        private const int ColPrice = 2;
        private const int ColChange = 3;
        private const int ColPct = 4;
        private const int ColOpen = 5;
        private const int ColHigh = 6;
        private const int ColLow = 7;
        private const int ColTurnover = 8;
        private const int ColTurnoverRate = 9;
        private const int ColTicker = 10;

        /// <summary>退出流程才允许真正关闭窗口。</summary>
        public bool AllowClose;

        public MainForm(AppHost host)
        {
            _host = host;

            Text = "QuantNative · 免费实时 A 股看盘";
            Icon = AppIcon.Get();
            StartPosition = FormStartPosition.Manual;
            // 页面底色用 Bg（浅灰），卡片（顶栏 / 表格 / 详情 / 概览）用 Card（白）——
            // 这个「灰底 + 白卡」的落差就是养基宝这类 App 的基本观感。
            BackColor = Theme.Bg;
            ForeColor = Theme.TextPrimary;
            Font = Theme.FNormal;
            ClientSize = new Size(1280, 820);
            MinimumSize = new Size(980, 620);
            DoubleBuffered = true;

            BuildUi();
            RestoreGeometry();

            // 必须显式排一次版：构造里设置 ClientSize 时控件还没建，OnResize 会提前返回，
            // 而之后尺寸不再变化 → OnResize 不会再来，界面就会停在默认位置互相重叠。
            LayoutChildren();
            RebuildRows();

            _host.WatchlistChanged += OnWatchlistChanged;
            _host.Hub.QuotesUpdated += OnQuotesUpdated;
            _host.Hub.SessionChanged += OnSessionChanged;
            _host.SourceChanged += OnSourceChanged;
            _host.ThemeChanged += ApplyTheme;
        }

        // ── 构建 ──

        private void BuildUi()
        {
            _topBar = new Panel();
            _topBar.BackColor = Theme.Card;
            _topBar.Paint += OnTopBarPaint;
            Controls.Add(_topBar);

            _addButton = Ui.PrimaryButton("＋ 添加自选", delegate { new AddStockForm(_host).ShowDialog(this); });
            _refreshButton = Ui.Button("立即刷新", delegate
            {
                _host.Hub.ForceRefresh();
                UpdateStatus();
            });
            _sourceButton = Ui.Button("数据源", delegate { ShowSourceMenu(); });
            _themeButton = Ui.Button("配色", delegate { _host.CycleScheme(); });
            _tickerButton = Ui.Button("浮窗", delegate { _host.ToggleTicker(); });
            _panelButton = Ui.Button("设置", delegate { _host.OpenPanel("settings"); });
            _topBar.Controls.Add(_addButton);
            _topBar.Controls.Add(_refreshButton);
            _topBar.Controls.Add(_sourceButton);
            _topBar.Controls.Add(_themeButton);
            _topBar.Controls.Add(_tickerButton);
            _topBar.Controls.Add(_panelButton);

            _indexStrip = new IndexStrip(_host);
            Controls.Add(_indexStrip);

            // 表格与详情各自装在一张白色圆角卡片里，让内容从灰底上「浮」起来
            _gridCard = new CardPanel();
            Controls.Add(_gridCard);
            _grid = CreateGrid();
            _gridCard.Controls.Add(_grid);

            _detailCard = new CardPanel();
            Controls.Add(_detailCard);
            _detail = new DetailPanel(_host);
            _detailCard.Controls.Add(_detail);

            _overview = new OverviewPanel(_host);
            Controls.Add(_overview);

            _statusLeft = Ui.Label("", Theme.FSmall, Theme.TextTertiary);
            _statusLeft.AutoSize = false;
            Controls.Add(_statusLeft);
            _statusRight = Ui.Label("", Theme.FSmall, Theme.TextTertiary);
            _statusRight.AutoSize = false;
            _statusRight.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(_statusRight);

            _sourceMenu = ThemedMenu.Create();
            _rowMenu = BuildRowMenu();

            UpdateStatus();
        }

        private DataGridView CreateGrid()
        {
            DataGridView grid = new DataGridView();
            GridStyle.Apply(grid);
            grid.Columns.Add(GridStyle.TextColumn("名称", 200));
            grid.Columns.Add(GridStyle.TextColumn("代码", 76));
            // 现价 / 涨跌幅是「一眼要看」的两列，用加粗大号等宽字体与其余数值列拉开层级
            grid.Columns.Add(GridStyle.EmphasisColumn("现价", 88));
            grid.Columns.Add(GridStyle.NumberColumn("涨跌", 68));
            grid.Columns.Add(GridStyle.EmphasisColumn("涨跌幅", 82));
            grid.Columns.Add(GridStyle.NumberColumn("今开", 62));
            grid.Columns.Add(GridStyle.NumberColumn("最高", 62));
            grid.Columns.Add(GridStyle.NumberColumn("最低", 62));
            grid.Columns.Add(GridStyle.NumberColumn("成交额", 84));
            grid.Columns.Add(GridStyle.NumberColumn("换手率", 64));
            DataGridViewCheckBoxColumn ticker = new DataGridViewCheckBoxColumn();
            ticker.HeaderText = "浮窗";
            ticker.Width = 46;
            ticker.FlatStyle = FlatStyle.Flat;
            ticker.Resizable = DataGridViewTriState.False;
            grid.Columns.Add(ticker);
            grid.Columns.Add(GridStyle.TextColumn("关联板块 / 净值日期", 320));
            grid.Columns[11].DisplayIndex = 3;

            grid.SelectionChanged += OnGridSelectionChanged;
            grid.CellContentClick += OnGridCellClick;
            grid.CellMouseDown += OnGridMouseDown;
            grid.CellFormatting += OnGridFormatting;
            grid.CellPainting += OnGridCellPainting;
            return grid;
        }

        private ContextMenuStrip BuildRowMenu()
        {
            ContextMenuStrip menu = ThemedMenu.Create();
            menu.Items.Add(ThemedMenu.Item("基金关联板块…", delegate
            {
                WithSelected(delegate (WatchItem item) { if (Symbols.IsOpenFund(item.Code)) using (var dialog = new FundBoardsForm(_host, item)) dialog.ShowDialog(this); });
            }));
            menu.Items.Add(ThemedMenu.Item("置顶", delegate { WithSelected(delegate (WatchItem item) { _host.MoveWatchTop(item); }); }));
            menu.Items.Add(ThemedMenu.Item("上移", delegate { WithSelected(delegate (WatchItem item) { _host.MoveWatch(item, -1); }); }));
            menu.Items.Add(ThemedMenu.Item("下移", delegate { WithSelected(delegate (WatchItem item) { _host.MoveWatch(item, 1); }); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(ThemedMenu.Item("切换浮窗播报", delegate
            {
                WithSelected(delegate (WatchItem item) { _host.SetTickerEnabled(item, !item.TickerEnabled); });
            }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(ThemedMenu.Item("删除自选", delegate
            {
                WithSelected(delegate (WatchItem item) { _host.RemoveWatch(item); });
            }, true));
            return menu;
        }

        private WatchItem SelectedItem
        {
            get
            {
                if (_grid == null || _grid.CurrentRow == null) return null;
                return _grid.CurrentRow.Tag as WatchItem;
            }
        }

        private void WithSelected(Action<WatchItem> action)
        {
            WatchItem item = SelectedItem;
            if (item != null && action != null) action(item);
        }

        // ── 布局 ──

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
        }

        private void LayoutChildren()
        {
            int width = ClientSize.Width;
            int height = ClientSize.Height;
            if (width <= 0 || height <= 0) return;
            if (_grid == null || _topBar == null || _statusLeft == null || _statusRight == null
                || _indexStrip == null || _detail == null || _overview == null
                || _gridCard == null || _detailCard == null) return;

            float scale = Theme.Scale(this);
            int pad = (int)Math.Round(10 * scale);
            int gap = (int)Math.Round(10 * scale);
            int topHeight = (int)Math.Round(46 * scale);
            int stripHeight = (int)Math.Round(84 * scale);
            int statusHeight = (int)Math.Round(24 * scale);
            int overviewWidth = (int)Math.Round(330 * scale);

            _topBar.SetBounds(0, 0, width, topHeight);
            int buttonWidth = (int)Math.Round(78 * scale);
            int buttonHeight = Math.Max((int)Math.Round(30 * scale), Ui.ButtonSize(_addButton).Height);
            int buttonTop = (topHeight - buttonHeight) / 2;
            int bx = width - pad - buttonWidth;
            Button[] buttons = new Button[] { _panelButton, _tickerButton, _themeButton, _refreshButton, _sourceButton, _addButton };
            for (int i = 0; i < buttons.Length; i++)
            {
                int actualWidth = Math.Max(buttonWidth, Ui.ButtonSize(buttons[i]).Width);
                if (i == 0) bx = width - pad;
                bx -= actualWidth;
                buttons[i].SetBounds(bx, Math.Max(0, buttonTop), actualWidth, buttonHeight);
                bx -= (int)Math.Round(6 * scale);
            }

            _indexStrip.SetBounds(pad, topHeight, Math.Max(80, width - pad * 2), stripHeight);

            int bodyTop = topHeight + stripHeight + (int)Math.Round(2 * scale);
            int bodyHeight = height - bodyTop - statusHeight - pad;
            if (bodyHeight < 80) bodyHeight = 80;

            int leftWidth = Math.Max(320, width - overviewWidth - pad * 3);
            _overview.SetBounds(width - overviewWidth - pad, bodyTop, overviewWidth, bodyHeight);

            int gridHeight = (int)(bodyHeight * 0.42);
            if (gridHeight < 130) gridHeight = Math.Min(130, bodyHeight / 2);
            int detailHeight = bodyHeight - gridHeight - gap;
            if (detailHeight < 60) detailHeight = 60;

            int inset = 1;
            _gridCard.SetBounds(pad, bodyTop, leftWidth, gridHeight);
            _grid.SetBounds(inset, inset, leftWidth - inset * 2, gridHeight - inset * 2);
            _detailCard.SetBounds(pad, bodyTop + gridHeight + gap, leftWidth, detailHeight);
            _detail.SetBounds(inset, inset, leftWidth - inset * 2, detailHeight - inset * 2);

            _statusLeft.SetBounds(pad, height - statusHeight, width / 2, statusHeight);
            _statusRight.SetBounds(width / 2, height - statusHeight, width / 2 - pad, statusHeight);

            if (_grid.Columns[ColName].Width != 168) _grid.Columns[ColName].Width = 168;
        }

        // ── 几何持久化 ──

        private void RestoreGeometry()
        {
            int x, y, w, h;
            if (int.TryParse(_host.GetSetting("window_w", ""), out w) && int.TryParse(_host.GetSetting("window_h", ""), out h)
                && w >= 900 && h >= 600)
            {
                ClientSize = new Size(w, h);
            }
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            if (int.TryParse(_host.GetSetting("window_x", ""), out x) && int.TryParse(_host.GetSetting("window_y", ""), out y)
                && x + 200 > work.Left && x < work.Right - 200 && y + 100 > work.Top && y < work.Bottom - 100)
            {
                Location = new Point(x, y);
            }
            else
            {
                Location = new Point(
                    work.Left + Math.Max(0, (work.Width - Width) / 2),
                    work.Top + Math.Max(0, (work.Height - Height) / 2));
            }
        }

        private void SaveGeometry()
        {
            _host.SetSetting("window_x", Location.X.ToString());
            _host.SetSetting("window_y", Location.Y.ToString());
            _host.SetSetting("window_w", ClientSize.Width.ToString());
            _host.SetSetting("window_h", ClientSize.Height.ToString());
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                SaveGeometry();
                Hide();
                ShowInTaskbar = false;
                return;
            }
            SaveGeometry();
            base.OnFormClosing(e);
        }

        // ── 数据 ──

        private void OnWatchlistChanged()
        {
            RebuildRows();
        }

        private void RebuildRows()
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            List<WatchItem> items = _host.Watchlist;
            for (int i = 0; i < items.Count; i++)
            {
                WatchItem item = items[i];
                int index = _grid.Rows.Add(
                    item.Name,
                    Symbols.FormatCode(item.Code),
                    "--", "--", "--", "--", "--", "--", "--", "--",
                    item.TickerEnabled);
                _grid.Rows[index].Tag = item;
            }
            _grid.ResumeLayout();
            UpdateQuoteCells();
            if (_grid.Rows.Count > 0 && _grid.CurrentRow == null) _grid.CurrentCell = _grid.Rows[0].Cells[ColName];
            _detail.SelectItem(SelectedItem);
            UpdateStatus();
        }

        private void OnQuotesUpdated()
        {
            UpdateQuoteCells();
            _indexStrip.Invalidate();
            UpdateStatus();
        }

        private void UpdateQuoteCells()
        {
            if (_grid.Rows.Count == 0) return;
            _grid.SuspendLayout();
            for (int i = 0; i < _grid.Rows.Count; i++)
            {
                DataGridViewRow row = _grid.Rows[i];
                WatchItem item = row.Tag as WatchItem;
                if (item == null) continue;

                Quote quote = _host.Hub.GetQuote(item.Code, item.Market);
                if (Symbols.IsOpenFund(item.Code))
                {
                    // 场外基金：当天净值公布了看净值，否则看关联板块等权估算（含午休）。
                    // 口径收敛在 FundPresentation.Describe 里，四个界面共用同一套判断。
                    FundEstimate estimate = _host.EstimateFund(item.Code);
                    FundDisplay display = FundPresentation.Describe(quote, estimate, MarketClock.NowCst());
                    bool marked = display != null && display.HasLivePct;

                    row.Cells[ColPrice].Value = display == null ? "—" : display.PriceText;
                    // Official daily return can include cash distributions; raw NAV subtraction
                    // is not the fund's realised return per unit.
                    row.Cells[ColChange].Value = display != null && display.Estimated
                        ? Symbols.FormatChange(display.Change) : "—";
                    row.Cells[ColPct].Value = marked ? Symbols.FormatPct(display.Pct) : "—";
                    row.Cells[11].Value = FundPresentation.DetailText(quote, estimate, display, _host.FundBoardSummary(item.Code));
                    row.Cells[11].ToolTipText = _host.FundBoardSummary(item.Code);
                    row.Cells[ColName].Value = item.Name;
                    for (int c = ColOpen; c <= ColTurnoverRate; c++) row.Cells[c].Value = "—";

                    Color fundColor = marked ? Theme.Pct(display.Pct) : Theme.TextTertiary;
                    row.Cells[ColPrice].Style.ForeColor = fundColor;
                    row.Cells[ColChange].Style.ForeColor = fundColor;
                    row.Cells[ColPct].Style.ForeColor = marked ? fundColor : Theme.TextSecondary;
                    row.Cells[ColPrice].ToolTipText = display == null ? "等待净值"
                        : display.Estimated
                            ? "估值 = 上一净值 ×（1 + 关联板块等权涨跌幅）；板块每几秒刷新一次，不是基金公司公布的净值"
                            : display.Official ? ""
                            : "上一净值（" + quote.PriceTime + "），关联板块行情未到";
                    continue;
                }
                if (quote == null) continue;

                row.Cells[ColName].Value = item.Name;
                row.Cells[ColPrice].Value = Symbols.FormatQuotePrice(quote);
                row.Cells[ColChange].Value = Symbols.FormatChange(quote.Change);
                row.Cells[ColPct].Value = Symbols.FormatPct(quote.ChangePct);
                row.Cells[ColOpen].Value = Symbols.FormatPrice(quote.Open);
                row.Cells[ColHigh].Value = Symbols.FormatPrice(quote.High);
                row.Cells[ColLow].Value = Symbols.FormatPrice(quote.Low);
                row.Cells[ColTurnover].Value = Symbols.FormatAmount(quote.Turnover);
                row.Cells[ColTurnoverRate].Value = quote.TurnoverRate.HasValue
                    ? quote.TurnoverRate.Value.ToString("F2") + "%" : "--";

                Color color = Theme.Pct(quote.ChangePct);
                row.Cells[ColPrice].Style.ForeColor = color;
                row.Cells[ColChange].Style.ForeColor = color;
                row.Cells[ColPct].Style.ForeColor = color;
                row.Cells[ColOpen].Style.ForeColor = Theme.Pct(quote.Open >= quote.Price - quote.Change ? 1 : -1);
                row.Cells[ColHigh].Style.ForeColor = Theme.Pct(quote.High >= quote.Price - quote.Change ? 1 : -1);
                row.Cells[ColLow].Style.ForeColor = Theme.Pct(quote.Low >= quote.Price - quote.Change ? 1 : -1);
                row.Cells[ColTicker].Value = item.TickerEnabled;
            }
            _grid.ResumeLayout();
        }

        private void OnGridSelectionChanged(object sender, EventArgs e)
        {
            if (_detail != null) _detail.SelectItem(SelectedItem);
        }

        private void OnGridCellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ColTicker) return;
            WatchItem item = _grid.Rows[e.RowIndex].Tag as WatchItem;
            if (item == null) return;
            _host.SetTickerEnabled(item, !item.TickerEnabled);
            _grid.Rows[e.RowIndex].Cells[ColTicker].Value = item.TickerEnabled;
        }

        private void OnGridMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
            _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)];
            _rowMenu.Show(_grid, new Point(e.X + 4, e.Y + 4));
        }

        private void OnGridFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            if (e.ColumnIndex == ColName)
            {
                WatchItem item = _grid.Rows[e.RowIndex].Tag as WatchItem;
                if (item != null)
                {
                    string category = Symbols.CnCategory(item.Code);
                    e.CellStyle.ForeColor = (category == "ETF" || category == "LOF")
                        ? Theme.TextSecondary
                        : (category == "ZS" ? Theme.Warning : Theme.Accent);
                    // 名称列显示成「名称（沪A）」形式，和旧版的市场标签等价
                    string tag = Symbols.MarketTag(item.Code, category);
                    if (tag.Length > 0) e.Value = item.Name + "  " + tag;
                }
            }
        }

        private void OnGridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

            // 涨跌幅列画成色块药丸（红涨绿跌），让整列变成一列「红绿条」，一眼扫过去就能定位
            if (e.ColumnIndex == ColPct)
            {
                e.PaintBackground(e.ClipBounds, true);
                PaintPctPill(e);
                e.Handled = true;
                return;
            }

            // 名称列：选中行左侧加一条强调竖条
            if (e.ColumnIndex == ColName)
            {
                e.PaintBackground(e.ClipBounds, true);
                e.PaintContent(e.ClipBounds);
                if (_grid.CurrentRow != null && _grid.CurrentRow.Index == e.RowIndex)
                {
                    float scale = Theme.Scale(_grid);
                    Rectangle bar = new Rectangle(e.CellBounds.X + 2,
                        e.CellBounds.Y + (int)Math.Round(6 * scale),
                        Math.Max(2, (int)Math.Round(3 * scale)),
                        Math.Max(4, e.CellBounds.Height - (int)Math.Round(12 * scale)));
                    using (GraphicsPath path = Theme.RoundedRect(bar, Math.Max(1, bar.Width / 2)))
                    using (SolidBrush brush = new SolidBrush(Theme.Accent))
                        e.Graphics.FillPath(brush, path);
                }
                e.Handled = true;
                return;
            }

            // 播报勾选框：用应用配色画，避免系统蓝色方块在暗色主题里跳出来
            if (e.ColumnIndex != ColTicker) return;

            e.PaintBackground(e.ClipBounds, true);
            bool on = false;
            object value = _grid.Rows[e.RowIndex].Cells[ColTicker].Value;
            if (value is bool) on = (bool)value;

            Rectangle box = new Rectangle(
                e.CellBounds.X + (e.CellBounds.Width - 14) / 2,
                e.CellBounds.Y + (e.CellBounds.Height - 14) / 2, 14, 14);
            using (SolidBrush brush = new SolidBrush(on ? Theme.Accent : Theme.Surface1))
            using (Pen pen = new Pen(on ? Theme.Accent : Theme.Border1, 1f))
            {
                e.Graphics.FillRectangle(brush, box);
                e.Graphics.DrawRectangle(pen, box);
                if (on)
                {
                    using (Pen tick = new Pen(Color.White, 2f))
                    {
                        e.Graphics.DrawLine(tick, box.Left + 3, box.Top + 7, box.Left + 6, box.Top + 10);
                        e.Graphics.DrawLine(tick, box.Left + 6, box.Top + 10, box.Left + 11, box.Top + 4);
                    }
                }
            }
            e.Handled = true;
        }

        /// <summary>涨跌幅色块药丸。非数值（例如场外基金的「—」）保持默认绘制。</summary>
        private void PaintPctPill(DataGridViewCellPaintingEventArgs e)
        {
            string text = Convert.ToString(e.Value);
            double pct;
            if (!TryParsePct(text, out pct))
            {
                using (SolidBrush brush = new SolidBrush(Theme.TextSecondary))
                using (StringFormat format = Theme.RightMiddle())
                    e.Graphics.DrawString(text ?? "", Theme.FMono, brush, e.CellBounds, format);
                return;
            }

            float scale = Theme.Scale(_grid);
            Font font = Theme.FMonoSmall;
            int pillWidth = Theme.Measure(e.Graphics, text, font) + (int)Math.Round(16 * scale);
            int pillHeight = Math.Min(e.CellBounds.Height - (int)Math.Round(8 * scale), (int)Math.Round(22 * scale));
            int maxWidth = Math.Max(20, e.CellBounds.Width - (int)Math.Round(8 * scale));
            if (pillWidth > maxWidth) pillWidth = maxWidth;
            if (pillHeight < 12) pillHeight = 12;

            Rectangle pill = new Rectangle(
                e.CellBounds.Right - (int)Math.Round(4 * scale) - pillWidth,
                e.CellBounds.Y + (e.CellBounds.Height - pillHeight) / 2,
                pillWidth, pillHeight);

            Theme.DrawPill(e.Graphics, pill, Theme.PctSoft(pct));
            using (SolidBrush brush = new SolidBrush(Theme.Pct(pct)))
            using (StringFormat center = Theme.CenterMiddle())
                e.Graphics.DrawString(text, font, brush, pill, center);
        }

        private static bool TryParsePct(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) return false;
            string raw = text.Replace("%", "").Replace("+", "").Trim();
            return double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        // ── 顶栏 / 状态栏 ──

        private void OnTopBarPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float scale = Theme.Scale(this);
            int pad = (int)Math.Round(14 * scale);
            int height = _topBar.Height;
            int y = (height - (int)Math.Round(9 * scale)) / 2;

            // 品牌色圆点 + 字号层级：产品名大而黑，副标题小而灰
            using (SolidBrush accent = new SolidBrush(Theme.Accent))
                g.FillEllipse(accent, pad, y, (int)Math.Round(9 * scale), (int)Math.Round(9 * scale));

            int x = pad + (int)Math.Round(9 * scale) + (int)Math.Round(9 * scale);
            using (SolidBrush brush = new SolidBrush(Theme.TextPrimary))
            using (StringFormat format = Theme.LeftMiddle())
                g.DrawString("QuantNative", Theme.FTitle, brush, new RectangleF(x, 0, 150, height), format);

            x += Theme.Measure(g, "QuantNative", Theme.FTitle) + (int)Math.Round(10 * scale);
            string subtitle = "实时行情 · 多源切换 · 原生轻量";
            using (SolidBrush brush = new SolidBrush(Theme.TextTertiary))
            using (StringFormat format = Theme.LeftMiddle())
                g.DrawString(subtitle, Theme.FSmall, brush, new RectangleF(x, 1, 320, height), format);

            // 交易时段小药丸：盘前 / 早盘 / 午休 / 午盘 / 休市
            x += Theme.Measure(g, subtitle, Theme.FSmall) + (int)Math.Round(12 * scale);
            MarketSession session = _host.Hub.Session;
            string sessionName = MarketClock.Name(session);
            bool trading = MarketClock.IsTrading(session);
            int pillWidth = Theme.Measure(g, sessionName, Theme.FTag) + (int)Math.Round(18 * scale);
            int pillHeight = Math.Max(16, Theme.FTag.Height + (int)Math.Round(5 * scale));
            Rectangle pill = new Rectangle(x, (height - pillHeight) / 2, pillWidth, pillHeight);
            if (pill.Right < _topBar.Width - (int)Math.Round(520 * scale))
            {
                Color pillFill = trading ? Theme.PctSoft(1) : Theme.Hover;
                Theme.DrawPill(g, pill, pillFill);
                using (SolidBrush brush = new SolidBrush(trading ? Theme.Up : Theme.TextTertiary))
                using (StringFormat center = Theme.CenterMiddle())
                    g.DrawString(sessionName, Theme.FTag, brush, pill, center);
            }

            using (Pen pen = new Pen(Theme.Border0, 1f))
                g.DrawLine(pen, 0, height - 1, _topBar.Width, height - 1);
        }

        private void ShowSourceMenu()
        {
            _sourceMenu.Items.Clear();
            IList<IQuoteSource> sources = _host.Hub.Sources;
            for (int i = 0; i < sources.Count; i++)
            {
                IQuoteSource source = sources[i];
                string label = source.DisplayName + (_host.Hub.ActiveSource == source ? "  ✓" : "");
                ToolStripMenuItem item = ThemedMenu.Item(label, delegate { _host.SwitchSource(source.Name); });
                _sourceMenu.Items.Add(item);
            }
            _sourceMenu.Show(_sourceButton, new Point(0, _sourceButton.Height));
        }

        private void OnSessionChanged()
        {
            UpdateStatus();
            if (_topBar != null) _topBar.Invalidate();   // 顶部时段药丸跟着换
        }

        private void OnSourceChanged()
        {
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_statusLeft == null) return;
            MarketSession session = _host.Hub.Session;
            string source = _host.Hub.ActiveSource == null ? "-" : _host.Hub.ActiveSource.DisplayName;
            _statusLeft.Text = "  " + MarketClock.Name(session) + " · " + source
                + " · " + MarketClock.ProfileName(_host.RefreshProfile)
                + (_host.Hub.LastUpdate == default(DateTime) ? "" : " · 更新 " + _host.Hub.LastUpdate.ToString("HH:mm:ss"))
                + (_host.Hub.LastError == null ? "" : " · " + _host.Hub.LastError);

            _statusRight.Text = "自选 " + _host.Watchlist.Count + " 只 · v" + Updater.CurrentVersion
                + " · 浮窗可拖动缩放  ";
            _statusRight.ForeColor = _host.Hub.LastError == null ? Theme.TextTertiary : Theme.Warning;
        }

        private void ApplyTheme()
        {
            BackColor = Theme.Bg;
            ForeColor = Theme.TextPrimary;
            _topBar.BackColor = Theme.Card;
            _statusLeft.ForeColor = Theme.TextTertiary;
            _statusRight.ForeColor = Theme.TextTertiary;
            foreach (Control control in _topBar.Controls)
            {
                if (!(control is Button)) continue;
                Button button = (Button)control;
                // 主按钮（添加自选）保持强调色实底，其余走「浅底 + 细边」
                bool primary = button == _addButton;
                button.BackColor = primary ? Theme.Accent : Theme.Surface2;
                button.ForeColor = primary ? Color.White : Theme.TextSecondary;
                button.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Border0;
                button.FlatAppearance.MouseOverBackColor = primary ? Theme.Accent : Theme.Surface3;
            }
            if (_gridCard != null) _gridCard.Invalidate();
            if (_detailCard != null) _detailCard.Invalidate();
            GridStyle.RefreshTheme(_grid);
            _grid.Invalidate();
            _indexStrip.Invalidate();
            _topBar.Invalidate();
            UpdateStatus();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _host.WatchlistChanged -= OnWatchlistChanged;
                _host.Hub.QuotesUpdated -= OnQuotesUpdated;
                _host.Hub.SessionChanged -= OnSessionChanged;
                _host.SourceChanged -= OnSourceChanged;
                _host.ThemeChanged -= ApplyTheme;
            }
            base.Dispose(disposing);
        }

        // ── 指数条（自绘） ──

        /// <summary>
        /// 指数条：每个指数一张圆角白卡（灰底上浮起），卡内三行 ——
        ///   名称（小、灰）→ 点位（大、粗、涨跌色）→ 涨跌额 + 涨跌幅（小、等宽、涨跌色）。
        /// 卡片宽度先按「实测文本宽度」算，再按可用宽度均分收缩，避免指数名变长（「中证500」）
        /// 时文字溢出到隔壁卡片；顺序由 QuoteHub 按固定名次排好，这里不再重排。
        /// </summary>
        private sealed class IndexStrip : Panel
        {
            private readonly AppHost _host;

            public IndexStrip(AppHost host)
            {
                _host = host;
                BackColor = Theme.Bg;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(BackColor);

                IReadOnlyList<IndexQuote> indices = _host.Hub.Indices;
                int count = indices == null ? 0 : indices.Count;
                int width = ClientSize.Width;
                int height = ClientSize.Height;
                if (width < 80 || height < 28) return;

                using (StringFormat left = Theme.LeftMiddle())
                {
                    if (count == 0)
                    {
                        using (SolidBrush brush = new SolidBrush(Theme.TextTertiary))
                            g.DrawString("大盘数据加载中…", Theme.FSmall, brush, new RectangleF(4, 0, width, height), left);
                        return;
                    }

                    float scale = Theme.Scale(this);
                    int gap = (int)Math.Round(8 * scale);
                    int padx = (int)Math.Round(11 * scale);
                    int radius = (int)Math.Round(10 * scale);
                    int minCard = (int)Math.Round(118 * scale);

                    // 先量内容：三行里最宽的那一行决定卡片的最小宽度
                    int contentWidth = 0;
                    int[] nameWidths = new int[count];
                    int[] priceWidths = new int[count];
                    int[] deltaWidths = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        IndexQuote index = indices[i];
                        nameWidths[i] = Theme.Measure(g, index.Name, Theme.FSmall);
                        priceWidths[i] = Theme.Measure(g, Symbols.FormatPrice(index.Price), Theme.FValueLarge);
                        deltaWidths[i] = Theme.Measure(g, DeltaText(index), Theme.FMonoSmall);
                        contentWidth = Math.Max(contentWidth, Math.Max(nameWidths[i], Math.Max(priceWidths[i], deltaWidths[i])));
                    }

                    int cardWidth = Math.Max(minCard, contentWidth + padx * 2);
                    int available = width - gap;
                    int maxCard = Math.Max((int)Math.Round(90 * scale), (available - gap * (count - 1)) / count);
                    if (cardWidth > maxCard) cardWidth = maxCard;

                    int total = count * cardWidth + gap * (count - 1);
                    int x = total >= width ? 0 : (width - total) / 2;

                    int nameH = Math.Max(14, Theme.FSmall.Height);
                    int priceH = Math.Max(20, Theme.FValueLarge.Height);
                    int deltaH = Math.Max(13, Theme.FMonoSmall.Height);
                    int contentH = nameH + priceH + deltaH;

                    for (int i = 0; i < count; i++)
                    {
                        IndexQuote index = indices[i];
                        Rectangle card = new Rectangle(x, 0, cardWidth, height);
                        if (card.Right > width) card.Width = width - card.X;
                        if (card.Width < 20) break;

                        Theme.DrawCard(g, card, radius);
                        Color color = Theme.Pct(index.ChangePct);

                        int top = card.Y + Math.Max(3, (card.Height - contentH) / 2);
                        int innerX = card.X + padx;
                        int innerW = Math.Max(20, card.Width - padx * 2);

                        using (SolidBrush nameBrush = new SolidBrush(Theme.TextSecondary))
                            g.DrawString(index.Name, Theme.FSmall, nameBrush,
                                new RectangleF(innerX, top, innerW, nameH), left);

                        using (SolidBrush priceBrush = new SolidBrush(color))
                            g.DrawString(Symbols.FormatPrice(index.Price), Theme.FValueLarge, priceBrush,
                                new RectangleF(innerX, top + nameH, innerW, priceH), left);

                        using (SolidBrush deltaBrush = new SolidBrush(color))
                            g.DrawString(DeltaText(index), Theme.FMonoSmall, deltaBrush,
                                new RectangleF(innerX, top + nameH + priceH, innerW, deltaH), left);

                        x += cardWidth + gap;
                    }
                }
            }

            /// <summary>"+12.34  +0.38%" —— 涨跌额与涨跌幅并排，等宽字体下各指数之间纵向对齐。</summary>
            private static string DeltaText(IndexQuote index)
            {
                return Symbols.FormatChange(index.Change) + "   " + Symbols.FormatPct(index.ChangePct);
            }
        }

        /// <summary>
        /// 一张白色圆角卡片容器。它本身只负责「画底色 + 描边」，
        /// 真正的内容（表格 / 详情面板）内缩 1px 放在里面，靠圆角遮住方形边角。
        /// </summary>
        private sealed class CardPanel : Panel
        {
            public CardPanel()
            {
                BackColor = Theme.Card;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Parent == null ? Theme.Bg : Parent.BackColor);
                float scale = Theme.Scale(this);
                Theme.DrawCard(g, new Rectangle(0, 0, Width - 1, Height - 1), (int)Math.Round(10 * scale));
            }
        }
    }
}
