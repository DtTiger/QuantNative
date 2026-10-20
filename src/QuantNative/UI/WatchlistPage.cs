using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using QuantNative.Core;
using QuantNative.Data;
using QuantNative.Interop;

namespace QuantNative.UI
{
    /// <summary>
    /// 面板「管理自选」页：搜索（代码 / 名称 / 拼音）→ 按主流证券类别勾选筛选 →
    /// 一键加入自选；搜索框为空时列出当前自选，可切换播报与删除。
    /// </summary>
    public sealed class WatchlistPage : UserControl
    {
        private readonly AppHost _host;

        private TextBox _search;
        private Label _capMarket, _capType;
        private CheckBox _mSh, _mSz, _mBj, _mHk, _mUs, _mOf;
        private CheckBox _tStock, _tFund, _tIndex, _tBond;
        private DataGridView _resultsGrid;
        private DataGridView _watchGrid;
        private Label _hint;
        private Timer _debounce;

        private List<StockBrief> _results = new List<StockBrief>();
        private int _searchSeq;
        private bool _searching;

        private const int ColTag = 0;
        private const int ColName = 1;
        private const int ColCode = 2;
        private const int ColAction = 3;
        private const int ColPrice = 3;
        private const int ColPct = 4;
        private const int ColTicker = 5;
        private const int ColDelete = 6;
        private const int ColBoard = 7;

        public WatchlistPage(AppHost host)
        {
            _host = host;
            BackColor = Theme.Card;
            BuildUi();

            _host.WatchlistChanged += OnWatchlistChanged;
            _host.Hub.QuotesUpdated += UpdateQuoteCells;
            _host.ThemeChanged += ApplyTheme;
        }

        private void BuildUi()
        {
            _debounce = new Timer();
            _debounce.Interval = 300;
            _debounce.Tick += OnDebounceTick;

            _search = Ui.Text("");
            _search.Location = new Point(0, 0);
            _search.Height = 26;
            _search.KeyDown += OnSearchKeyDown;
            _search.TextChanged += OnSearchTextChanged;
            Controls.Add(_search);

            _capMarket = Ui.Label("市场", Theme.FSmall, Theme.TextTertiary);
            Controls.Add(_capMarket);

            _mSh = Ui.Check("沪市", true);
            _mSz = Ui.Check("深市", true);
            _mBj = Ui.Check("北交所", true);
            _mHk = Ui.Check("港股", true);
            _mUs = Ui.Check("美股", true);
            _mOf = Ui.Check("场外", true);

            _capType = Ui.Label("类型", Theme.FSmall, Theme.TextTertiary);
            Controls.Add(_capType);

            _tStock = Ui.Check("股票", true);
            _tFund = Ui.Check("基金", true);
            _tIndex = Ui.Check("指数", true);
            _tBond = Ui.Check("可转债", true);

            CheckBox[] filters = new CheckBox[]
            {
                _mSh, _mSz, _mBj, _mHk, _mUs, _mOf,
                _tStock, _tFund, _tIndex, _tBond
            };
            for (int i = 0; i < filters.Length; i++)
            {
                filters[i].CheckedChanged += OnFilterChanged;
                Controls.Add(filters[i]);
            }

            _hint = Ui.Label("", Theme.FSmall, Theme.TextTertiary);
            _hint.AutoSize = false;
            _hint.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(_hint);

            _resultsGrid = CreateResultsGrid();
            _watchGrid = CreateWatchGrid();
            Controls.Add(_resultsGrid);
            Controls.Add(_watchGrid);

            _resultsGrid.Visible = false;
        }

        private DataGridView CreateResultsGrid()
        {
            DataGridView grid = new DataGridView();
            GridStyle.Apply(grid);
            grid.Columns.Add(GridStyle.TextColumn("市场", 46));
            grid.Columns.Add(GridStyle.TextColumn("名称", 170));
            grid.Columns.Add(GridStyle.TextColumn("代码", 70));
            DataGridViewButtonColumn action = new DataGridViewButtonColumn();
            action.HeaderText = "";
            action.Text = "加入";
            action.Width = 56;
            action.FlatStyle = FlatStyle.Flat;
            action.UseColumnTextForButtonValue = false;
            action.Resizable = DataGridViewTriState.False;
            grid.Columns.Add(action);
            grid.CellContentClick += OnResultsClick;
            grid.CellFormatting += OnResultsFormatting;
            return grid;
        }

        private DataGridView CreateWatchGrid()
        {
            DataGridView grid = new DataGridView();
            GridStyle.Apply(grid);
            grid.Columns.Add(GridStyle.TextColumn("市场", 46));
            grid.Columns.Add(GridStyle.TextColumn("名称", 120));
            grid.Columns.Add(GridStyle.TextColumn("代码", 62));
            grid.Columns.Add(GridStyle.NumberColumn("现价", 60));
            grid.Columns.Add(GridStyle.NumberColumn("涨跌幅", 64));
            DataGridViewCheckBoxColumn ticker = new DataGridViewCheckBoxColumn();
            ticker.HeaderText = "播报";
            ticker.Width = 44;
            ticker.Resizable = DataGridViewTriState.False;
            ticker.FlatStyle = FlatStyle.Flat;
            grid.Columns.Add(ticker);
            DataGridViewButtonColumn delete = new DataGridViewButtonColumn();
            delete.HeaderText = "";
            delete.Text = "✕";
            delete.Width = 34;
            delete.FlatStyle = FlatStyle.Flat;
            delete.UseColumnTextForButtonValue = true;
            delete.Resizable = DataGridViewTriState.False;
            grid.Columns.Add(delete);
            DataGridViewButtonColumn board = new DataGridViewButtonColumn();
            board.HeaderText = "关联板块";
            board.Width = 90;
            board.FlatStyle = FlatStyle.Flat;
            board.UseColumnTextForButtonValue = false;
            board.Resizable = DataGridViewTriState.False;
            grid.Columns.Add(board);
            grid.CellContentClick += OnWatchClick;
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex == ColDelete || e.ColumnIndex == ColTicker) return;
                var item = grid.Rows[e.RowIndex].Tag as WatchItem;
                if (item != null && Symbols.IsOpenFund(item.Code)) using (var dialog = new FundBoardsForm(_host, item)) dialog.ShowDialog(FindForm());
            };
            grid.CellFormatting += OnWatchFormatting;
            return grid;
        }

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
            if (_search == null || _capMarket == null || _capType == null || _hint == null
                || _resultsGrid == null || _watchGrid == null || _mSh == null) return;

            _search.Width = width;
            _search.Top = 0;

            // 两行筛选：市场 / 类型
            int marketTop = _search.Bottom + 6;
            int typeTop = marketTop + 22;

            int x = 0;
            _capMarket.Location = new Point(x, marketTop + 2);
            x = _capMarket.Right + 8;
            CheckBox[] markets = new CheckBox[] { _mSh, _mSz, _mBj, _mHk, _mUs, _mOf };
            for (int i = 0; i < markets.Length; i++)
            {
                markets[i].Location = new Point(x, marketTop);
                // 用 PreferredSize 而不是 Right：勾选项是 AutoSize，宽度要等布局后才确定
                x += markets[i].PreferredSize.Width + 8;
            }

            x = 0;
            _capType.Location = new Point(x, typeTop + 2);
            x = _capType.Right + 8;
            CheckBox[] types = new CheckBox[] { _tStock, _tFund, _tIndex, _tBond };
            for (int i = 0; i < types.Length; i++)
            {
                types[i].Location = new Point(x, typeTop);
                x += types[i].PreferredSize.Width + 8;
            }

            _hint.Width = Math.Max(0, width - x - 2);
            _hint.Left = x;
            _hint.Top = typeTop;
            _hint.Height = 18;

            int listTop = typeTop + 24;
            int listHeight = Math.Max(40, height - listTop);
            _resultsGrid.SetBounds(0, listTop, width, listHeight);
            _watchGrid.SetBounds(0, listTop, width, listHeight);

            _resultsGrid.Columns[ColName].Width = Math.Max(90, width - 46 - 70 - 56 - 24);
            _watchGrid.Columns[ColName].Width = Math.Max(100, width - 46 - 62 - 60 - 64 - 44 - 34 - 90 - 24);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 占位提示要在输入框句柄就绪后才能设（EM_SETCUEBANNER）
            Native.SetCueBanner(_search, "输入代码或名称快速搜索：600519 / 茅台 / 沪深300 / ETF");
            RefreshWatchGrid();
        }

        // ── 搜索 ──

        private void OnSearchTextChanged(object sender, EventArgs e)
        {
            _debounce.Stop();
            if ((_search.Text ?? "").Trim().Length == 0)
            {
                _results = new List<StockBrief>();
                _resultsGrid.Visible = false;
                _watchGrid.Visible = true;
                _hint.Text = "共 " + _host.Watchlist.Count + " 只 · 播报 " + CountBroadcasting() + " 只";
                RefreshWatchGrid();
                return;
            }
            _debounce.Start();
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                _debounce.Stop();
                RunSearch();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void OnDebounceTick(object sender, EventArgs e)
        {
            _debounce.Stop();
            RunSearch();
        }

        private async void RunSearch()
        {
            string keyword = (_search.Text ?? "").Trim();
            if (keyword.Length == 0) return;

            int seq = ++_searchSeq;
            _searching = true;
            _hint.Text = "搜索中…";
            try
            {
                List<StockBrief> hits = await _host.Search.SearchAsync(keyword, _host.Hub.ActiveSource, _host.Hub.Sources);
                if (seq != _searchSeq) return;   // 已有更新的搜索，丢弃这次结果
                _results = hits ?? new List<StockBrief>();
                _searching = false;
                _resultsGrid.Visible = true;
                _watchGrid.Visible = false;
                RefreshResultsGrid();
            }
            catch (Exception ex)
            {
                if (seq != _searchSeq) return;
                _searching = false;
                _results = new List<StockBrief>();
                _resultsGrid.Visible = true;
                _watchGrid.Visible = false;
                RefreshResultsGrid();
                _hint.Text = "搜索失败：" + ex.Message;
                Log.Warn("搜索失败: " + ex.Message);
            }
        }

        private void RefreshResultsGrid()
        {
            _resultsGrid.SuspendLayout();
            _resultsGrid.Rows.Clear();
            int shown = 0;
            for (int i = 0; i < _results.Count; i++)
            {
                StockBrief brief = _results[i];
                if (!PassesFilter(brief)) continue;
                string tag = Symbols.MarketTag(brief.Code, brief.Category);
                bool added = _host.Contains(brief.Code);
                int index = _resultsGrid.Rows.Add(tag, brief.Name, Symbols.FormatCode(brief.Code), added ? "已添加" : "加入");
                _resultsGrid.Rows[index].Tag = brief;
                shown++;
            }
            _resultsGrid.ResumeLayout();
            _hint.Text = shown == 0
                ? (_searching ? "搜索中…" : "未找到匹配标的（可调整筛选）")
                : "匹配 " + shown + " 条";
        }

        private void OnResultsFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            if (e.ColumnIndex == ColTag)
            {
                string tag = e.Value as string;
                if (tag == "ETF" || tag == "LOF") e.CellStyle.ForeColor = Theme.TextSecondary;
                else if (tag == "沪指" || tag == "深指" || tag == "北指" || tag == "指数") e.CellStyle.ForeColor = Theme.Warning;
                else e.CellStyle.ForeColor = Theme.Accent;
            }
            else if (e.ColumnIndex == ColAction)
            {
                e.CellStyle.ForeColor = Theme.Accent;
                e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            }
        }

        private void OnResultsClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ColAction) return;
            StockBrief brief = _resultsGrid.Rows[e.RowIndex].Tag as StockBrief;
            if (brief == null) return;
            if (_host.AddWatch(brief))
            {
                _resultsGrid.Rows[e.RowIndex].Cells[ColAction].Value = "已添加";
                _hint.Text = "已加入 " + brief.Name;
            }
            else
            {
                _hint.Text = brief.Name + " 已在自选中";
            }
        }

        // ── 自选列表 ──

        private void OnWatchlistChanged()
        {
            if (!_resultsGrid.Visible)
            {
                RefreshWatchGrid();
            }
        }

        private void RefreshWatchGrid()
        {
            _watchGrid.SuspendLayout();
            _watchGrid.Rows.Clear();
            List<WatchItem> items = _host.Watchlist;
            for (int i = 0; i < items.Count; i++)
            {
                WatchItem item = items[i];
                string category = Symbols.CnCategory(item.Code);
                if (!PassesFilter(item.Code, item.Market, category)) continue;

                Quote quote = _host.Hub.GetQuote(item.Code, item.Market);
                int index = _watchGrid.Rows.Add(
                    Symbols.MarketTag(item.Code, category),
                    item.Name,
                    Symbols.FormatCode(item.Code),
                    quote == null ? "--" : Symbols.FormatQuotePrice(quote),
                    quote == null ? "--" : Symbols.FormatPct(quote.ChangePct),
                    item.TickerEnabled,
                    "✕",
                    BoardActionText(item));
                _watchGrid.Rows[index].Tag = item;
            }
            _watchGrid.ResumeLayout();
            _hint.Text = "共 " + items.Count + " 只 · 播报 " + CountBroadcasting() + " 只";
        }

        /// <summary>
        /// 关联按钮上的文案带上「自动 / 手动」标记 —— 自动关联的结果随时可能被下一轮重算，
        /// 用户需要一眼看出这只会不会被覆盖（手动关联的永不覆盖）。
        /// </summary>
        private string BoardActionText(WatchItem item)
        {
            if (!Symbols.IsOpenFund(item.Code)) return "—";
            string mode = _host.FundBoardMode(item.Code);
            if (mode == "manual") return "手动关联";
            if (mode == "auto") return "自动匹配";
            return "去关联";
        }

        private int CountBroadcasting()
        {
            int count = 0;
            for (int i = 0; i < _host.Watchlist.Count; i++)
                if (_host.Watchlist[i].TickerEnabled) count++;
            return count;
        }

        /// <summary>行情刷新：只更新已有行的数值，不重建行（避免打断选择与滚动位置）。</summary>
        private void UpdateQuoteCells()
        {
            if (_watchGrid == null || _watchGrid.Rows.Count == 0) return;
            _watchGrid.SuspendLayout();
            for (int i = 0; i < _watchGrid.Rows.Count; i++)
            {
                DataGridViewRow row = _watchGrid.Rows[i];
                WatchItem item = row.Tag as WatchItem;
                if (item == null) continue;
                Quote quote = _host.Hub.GetQuote(item.Code, item.Market);
                if (Symbols.IsOpenFund(item.Code))
                {
                    // 口径与自选表/浮窗完全一致：正式净值 > 板块等权估值 > 上一净值
                    FundEstimate estimate = _host.EstimateFund(item.Code);
                    FundDisplay display = FundPresentation.Describe(quote, estimate, MarketClock.NowCst());
                    bool marked = display != null && display.HasLivePct;

                    row.Cells[ColPrice].Value = display == null ? "—" : display.PriceText;
                    row.Cells[ColPct].Value = marked ? Symbols.FormatPct(display.Pct) : "查看关联";
                    row.Cells[ColName].ToolTipText = _host.FundBoardSummary(item.Code) + "（双击设置）";
                    row.Cells[ColPct].ToolTipText = display != null && display.Estimated
                        ? "按关联板块 " + estimate.Count + " 个等权估算（非基金公司净值）\r\n" + _host.FundBoardSummary(item.Code)
                        : _host.FundBoardSummary(item.Code);
                    row.Cells[ColPrice].Style.ForeColor = marked ? Theme.TextPrimary : Theme.TextTertiary;
                    row.Cells[ColPct].Style.ForeColor = marked ? Theme.Pct(display.Pct) : Theme.TextSecondary;
                    continue;
                }
                if (quote == null) continue;
                row.Cells[ColPrice].Value = Symbols.FormatQuotePrice(quote);
                row.Cells[ColPct].Value = Symbols.FormatPct(quote.ChangePct);
                Color color = Theme.Pct(quote.ChangePct);
                row.Cells[ColPrice].Style.ForeColor = color;
                row.Cells[ColPct].Style.ForeColor = color;
                if (!string.IsNullOrEmpty(quote.Name) && row.Cells[ColName].Value as string != quote.Name)
                    row.Cells[ColName].Value = item.Name;
            }
            _watchGrid.ResumeLayout();
        }

        private void OnWatchFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            if (e.ColumnIndex == ColTag)
            {
                string tag = e.Value as string;
                if (tag == "ETF" || tag == "LOF") e.CellStyle.ForeColor = Theme.TextSecondary;
                else if (tag == "沪指" || tag == "深指" || tag == "北指" || tag == "指数") e.CellStyle.ForeColor = Theme.Warning;
                else e.CellStyle.ForeColor = Theme.Accent;
            }
            else if (e.ColumnIndex == ColDelete)
            {
                e.CellStyle.ForeColor = Theme.TextTertiary;
                e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            }
            else if (e.ColumnIndex == ColBoard)
            {
                // 手动关联用强调色（可点），自动匹配用次级色（会被重算），未关联用弱色（引导去点）
                string text = e.Value as string;
                e.CellStyle.ForeColor = text == "手动关联" ? Theme.Accent
                    : text == "自动匹配" ? Theme.TextSecondary
                    : text == "去关联" ? Theme.Warning : Theme.TextTertiary;
                e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            }
        }

        private void OnWatchClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            WatchItem item = _watchGrid.Rows[e.RowIndex].Tag as WatchItem;
            if (item == null) return;

            if (e.ColumnIndex == ColBoard)
            {
                if (Symbols.IsOpenFund(item.Code)) using (var dialog = new FundBoardsForm(_host, item)) dialog.ShowDialog(FindForm());
                return;
            }

            if (e.ColumnIndex == ColTicker)
            {
                bool next = !item.TickerEnabled;
                _host.SetTickerEnabled(item, next);
                _watchGrid.Rows[e.RowIndex].Cells[ColTicker].Value = next;
                _hint.Text = next ? (item.Name + " 已加入播报") : (item.Name + " 已停止播报");
                return;
            }

            if (e.ColumnIndex == ColDelete)
            {
                _host.RemoveWatch(item);
                _watchGrid.Rows.RemoveAt(e.RowIndex);
                _hint.Text = "已删除 " + item.Name;
            }
        }

        // ── 筛选 ──

        private void OnFilterChanged(object sender, EventArgs e)
        {
            if (_resultsGrid.Visible) RefreshResultsGrid();
            else RefreshWatchGrid();
        }

        /// <summary>
        /// 两组筛选：市场（沪市/深市/北交所/港股/美股）与类型（股票/基金/指数/可转债）。
        /// 每组「一个都不勾」视为该组不筛选（勾空后一片空白不符合直觉）。
        /// 同时作用于搜索结果与自选列表。
        /// </summary>
        private bool PassesFilter(string code, string market, string category)
        {
            bool anyMarket = _mSh.Checked || _mSz.Checked || _mBj.Checked || _mHk.Checked || _mUs.Checked || _mOf.Checked;
            if (anyMarket)
            {
                string group = Symbols.MarketGroup(code, market);
                bool hit =
                    (_mSh.Checked && group == "SHA") ||
                    (_mSz.Checked && group == "SZA") ||
                    (_mBj.Checked && group == "BJ") ||
                    (_mHk.Checked && group == "HK") ||
                    (_mUs.Checked && group == "US") ||
                    (_mOf.Checked && group == "OF");
                if (!hit) return false;
            }

            bool anyType = _tStock.Checked || _tFund.Checked || _tIndex.Checked || _tBond.Checked;
            if (anyType)
            {
                string group = Symbols.TypeGroup(code, category);
                bool hit =
                    (_tStock.Checked && group == "STOCK") ||
                    (_tFund.Checked && group == "FUND") ||
                    (_tIndex.Checked && group == "INDEX") ||
                    (_tBond.Checked && group == "BOND");
                if (!hit) return false;
            }

            return true;
        }

        private bool PassesFilter(StockBrief brief)
        {
            if (brief == null) return false;
            return PassesFilter(brief.Code, brief.Market, brief.Category);
        }

        // ── 生命周期 ──

        public void ApplyTheme()
        {
            BackColor = Theme.Card;
            GridStyle.RefreshTheme(_resultsGrid);
            GridStyle.RefreshTheme(_watchGrid);
            ApplyControlColors(this);
        }

        private static void ApplyControlColors(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is DataGridView) continue;
                // ComboBox / Button 等不支持透明背景，用安全封装（否则会抛"控件不支持透明的背景色"）
                if (control is TextBox)
                {
                    Ui.SetBackColor(control, Theme.Surface2);
                    Ui.SetForeColor(control, Theme.TextPrimary);
                }
                else if (control is Label || control is CheckBox)
                {
                    Ui.SetBackColor(control, Color.Transparent);
                    Ui.SetForeColor(control, control is Label ? Theme.TextTertiary : Theme.TextSecondary);
                }
                if (control.HasChildren) ApplyControlColors(control);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _host.WatchlistChanged -= OnWatchlistChanged;
                _host.Hub.QuotesUpdated -= UpdateQuoteCells;
                _host.ThemeChanged -= ApplyTheme;
                if (_debounce != null) _debounce.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
