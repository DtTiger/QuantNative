using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;
using QuantNative.Core;

namespace QuantNative.UI
{
    /// <summary>
    /// 个股详情区：左上角周期切换（分时 / 分钟K / 日周月K），中间图表，右侧五档盘口 + 摘要。
    /// 自动刷新节奏对齐旧版：盘口 3 秒、分时 5 秒、日K 30 秒、周/月K 60 秒。
    /// </summary>
    public sealed class DetailPanel : UserControl
    {
        private static readonly ChartPeriod[] Periods = new ChartPeriod[]
        {
            ChartPeriod.Minute, ChartPeriod.Min1, ChartPeriod.Min5, ChartPeriod.Min15,
            ChartPeriod.Min30, ChartPeriod.Min60, ChartPeriod.Daily, ChartPeriod.Weekly, ChartPeriod.Monthly
        };

        private readonly AppHost _host;
        private readonly ChartControl _chart;
        private readonly List<Button> _periodButtons = new List<Button>();
        private readonly Timer _timer;
        private readonly Label _title;
        private readonly Label _quote;
        private readonly Label _quoteHint;
        private readonly Panel _depth;
        private readonly Button _linkBoards;
        private readonly ComboBox _boardPicker;
        private bool _syncingBoards;
        private bool _lastFundNavMode;

        private WatchItem _item;
        private ChartPeriod _period = ChartPeriod.Minute;
        private Depth _depthData;
        private List<MinutePoint> _minuteData;
        private double _prevClose;
        private int _seconds;
        private bool _loading;
        private int _loadSeq;

        public DetailPanel(AppHost host)
        {
            _host = host;
            BackColor = Theme.Card;

            // 三行标题区（参照主流基金 App 的详情页头部）：
            //   第一行 名称 + 代码   —— 小号、次要色，只是「我在看什么」
            //   第二行 现价 + 涨跌幅 —— 大号加粗等宽、涨跌色，这是主角
            //   第三行 价格口径说明 —— 最小号、最弱的灰，只在需要澄清时出现
            _title = Ui.Label("请选择自选标的", Theme.FSection, Theme.TextPrimary);
            _title.AutoSize = false;
            Controls.Add(_title);
            _title.AutoEllipsis = true;

            _quote = Ui.Label("", Theme.FValueLarge, Theme.TextPrimary);
            _quote.AutoSize = false;
            Controls.Add(_quote);

            _quoteHint = Ui.Label("", Theme.FHint, Theme.TextTertiary);
            _quoteHint.AutoSize = false;
            Controls.Add(_quoteHint);

            _linkBoards = Ui.Button("关联板块", delegate { if (_item != null) using (var dialog = new FundBoardsForm(_host, _item)) dialog.ShowDialog(FindForm()); });
            _boardPicker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.FNormal, BackColor = Theme.Surface2, ForeColor = Theme.TextPrimary };
            _boardPicker.SelectedIndexChanged += delegate { if (!_syncingBoards) { ++_loadSeq; _loading = false; LoadData(true); } };
            Controls.Add(_linkBoards); Controls.Add(_boardPicker);

            for (int i = 0; i < Periods.Length; i++)
            {
                ChartPeriod period = Periods[i];
                Button button = Ui.Button(ChartPeriods.Display(period), delegate { SetPeriod(period); });
                Ui.Round(button, 6);
                _periodButtons.Add(button);
                Controls.Add(button);
            }

            _chart = new ChartControl();
            Controls.Add(_chart);

            _depth = new Panel();
            _depth.BackColor = Theme.Card;
            _depth.Paint += OnDepthPaint;
            Controls.Add(_depth);

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += OnTick;
            _timer.Start();

            _host.Hub.QuotesUpdated += OnQuotesUpdated;
            _host.ThemeChanged += ApplyTheme;

            UpdatePeriodButtons();
        }

        /// <summary>切换当前标的（自选表格选中行变化时调用）。</summary>
        public void SelectItem(WatchItem item)
        {
            ++_loadSeq;
            _loading = false;
            _item = item;
            if (item != null && Symbols.IsOpenFund(item.Code)) _period = ChartPeriod.Minute;
            _depthData = null;
            _minuteData = null;
            _prevClose = 0;
            RefreshBoardPicker();
            _lastFundNavMode = item != null && Symbols.IsOpenFund(item.Code)
                && FundPresentation.UseNav(_host.Hub.GetQuote(item.Code, item.Market), MarketClock.NowCst());
            UpdatePeriodButtons();
            LayoutChildren();

            if (item == null)
            {
                RefreshTitle(null);
                _chart.SetEmpty("暂无选中标的");
                _depth.Invalidate();
                return;
            }

            Quote quote = _host.Hub.GetQuote(item.Code, item.Market);
            RefreshTitle(quote);
            _chart.SetEmpty(Symbols.IsOpenFund(item.Code)
                ? "正在加载关联板块行情…"
                : "加载中…");
            _depth.Invalidate();
            LoadData(true);
        }

        /// <summary>
        /// 头部三行的内容与配色。涨跌幅用 A 股惯例的红涨绿跌；场外基金在拿到正式净值前
        /// 明确标注「关联板块参考」，避免把板块涨跌误读成基金涨跌。
        /// </summary>
        private void RefreshTitle(Quote quote)
        {
            if (_item == null)
            {
                _title.Text = "请选择自选标的";
                _title.ForeColor = Theme.TextPrimary;
                _quote.Text = "";
                _quoteHint.Text = "从上方自选列表里选一只，这里会显示具体行情";
                _quoteHint.ForeColor = Theme.TextTertiary;
                return;
            }

            string category = Symbols.CnCategory(_item.Code);
            string tag = Symbols.MarketTag(_item.Code, category);
            _title.Text = _item.Name + "  " + Symbols.FormatCode(_item.Code) + (tag.Length > 0 ? "  " + tag : "");
            _title.ForeColor = Theme.TextPrimary;

            if (quote == null)
            {
                _quote.Text = "等待行情…";
                _quote.ForeColor = Theme.TextTertiary;
                _quoteHint.Text = "";
                return;
            }

            if (Symbols.IsOpenFund(_item.Code))
            {
                // 场外基金三档口径：板块等权估值（盘中/午休）> 当天正式净值 > 上一净值。
                // 以前这里只在「非净值模式」显示上一净值，主数值不跟板块走，
                // 用户会觉得基金的涨跌是错的。
                FundEstimate estimate = _host.EstimateFund(_item.Code);
                FundDisplay display = FundPresentation.Describe(quote, estimate, MarketClock.NowCst());
                if (display != null && display.Estimated)
                {
                    _quote.Text = display.PriceText + "   " + Symbols.FormatPct(display.Pct);
                    _quote.ForeColor = Theme.Pct(display.Pct);
                    _quoteHint.Text = FundPresentation.EstimateHint(quote, estimate, MarketClock.NowCst());
                }
                else if (display != null && display.Official)
                {
                    _quote.Text = display.PriceText + "   " + Symbols.FormatPct(display.Pct);
                    _quote.ForeColor = Theme.Pct(display.Pct);
                    _quoteHint.Text = Symbols.PriceKindText(quote);
                }
                else
                {
                    _quote.Text = quote.Price > 0 ? Symbols.FormatPrice(quote.Price) : "—";
                    _quote.ForeColor = Theme.TextTertiary;
                    _quoteHint.Text = FundPresentation.BoardModeHint(quote);
                }
                _quoteHint.ForeColor = Theme.TextTertiary;
                return;
            }

            string price = Symbols.FormatQuotePrice(quote);
            string pct = Symbols.FormatPct(quote.ChangePct);
            _quote.Text = price + "   " + pct;
            _quote.ForeColor = Theme.Pct(quote.ChangePct);
            _quoteHint.Text = Symbols.PriceKindText(quote);
            _quoteHint.ForeColor = Theme.TextTertiary;
        }

        private void RefreshBoardPicker()
        {
            _syncingBoards = true;
            string previous = (_boardPicker.SelectedItem as BoardLink) == null ? null : ((BoardLink)_boardPicker.SelectedItem).Code;
            _boardPicker.Items.Clear();
            bool fund = _item != null && Symbols.IsOpenFund(_item.Code);
            _linkBoards.Visible = fund; _boardPicker.Visible = fund;
            if (fund) foreach (var link in _host.GetFundBoards(_item.Code)) _boardPicker.Items.Add(link);
            if (_boardPicker.Items.Count > 0) _boardPicker.SelectedIndex = 0;
            for (int i = 0; i < _boardPicker.Items.Count; i++) if (((BoardLink)_boardPicker.Items[i]).Code == previous) _boardPicker.SelectedIndex = i;
            _syncingBoards = false;
        }

        private void SetPeriod(ChartPeriod period)
        {
            if (_item != null && Symbols.IsOpenFund(_item.Code)
                && !FundPresentation.UseNav(_host.Hub.GetQuote(_item.Code, _item.Market), MarketClock.NowCst())
                && period != ChartPeriod.Minute) return;
            if (_period == period) return;
            ++_loadSeq;
            _loading = false;
            _period = period;
            UpdatePeriodButtons();
            LoadData(true);
        }

        private void UpdatePeriodButtons()
        {
            for (int i = 0; i < _periodButtons.Count; i++)
            {
                bool active = Periods[i] == _period;
                // 选中态用「强调色实底 + 白字」的药丸，和概览页的榜单切换保持一致
                _periodButtons[i].ForeColor = active ? Color.White : Theme.TextSecondary;
                _periodButtons[i].BackColor = active ? Theme.Accent : Theme.Surface2;
                _periodButtons[i].FlatAppearance.BorderSize = active ? 0 : 1;
                _periodButtons[i].FlatAppearance.BorderColor = Theme.Border0;
                _periodButtons[i].FlatAppearance.MouseOverBackColor = active ? Theme.Accent : Theme.Surface3;
            }
        }

        public void ApplyTheme()
        {
            BackColor = Theme.Card;
            _depth.BackColor = Theme.Card;
            _title.ForeColor = Theme.TextPrimary;
            _quoteHint.ForeColor = Theme.TextTertiary;
            foreach (Button button in _periodButtons)
            {
                button.BackColor = Theme.Surface1;
                button.ForeColor = Theme.TextSecondary;
                button.FlatAppearance.BorderColor = Theme.Border1;
            }
            UpdatePeriodButtons();
            _chart.ApplyTheme();
            _boardPicker.BackColor = Theme.Surface2; _boardPicker.ForeColor = Theme.TextPrimary;
            _linkBoards.BackColor = Theme.Surface1; _linkBoards.ForeColor = Theme.TextPrimary;
            _depth.Invalidate();
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
            if (_chart == null || _depth == null || _title == null || _periodButtons == null) return;

            float scale = Theme.Scale(this);
            int depthWidth = (int)Math.Round(196 * scale);

            // 头部三行：名称行 / 数值行 / 口径说明行。数值行给足高度，让大号数字不拥挤。
            int gap = (int)Math.Round(4 * scale);
            int nameHeight = Math.Max(18, _title.Font.Height + 2);
            int quoteHeight = Math.Max(24, _quote.Font.Height + 4);
            int hintHeight = Math.Max(14, _quoteHint.Font.Height);
            _title.SetBounds(0, 0, width, nameHeight);
            _quote.SetBounds(0, nameHeight + gap, width, quoteHeight);
            _quoteHint.SetBounds(0, nameHeight + gap + quoteHeight, width, hintHeight);

            int titleHeight = nameHeight + gap + quoteHeight + hintHeight;
            int x = 0, y = titleHeight + (int)Math.Round(8 * scale), rowHeight = 0;
            bool fund = _item != null && Symbols.IsOpenFund(_item.Code);
            for (int i = 0; i < _periodButtons.Count; i++)
            {
                Button button = _periodButtons[i];
                bool show = !fund || Periods[i] == ChartPeriod.Minute
                    || (_lastFundNavMode && (Periods[i] == ChartPeriod.Daily || Periods[i] == ChartPeriod.Weekly || Periods[i] == ChartPeriod.Monthly));
                button.Visible = show;
                if (!show) continue;
                button.Text = fund && Periods[i] == ChartPeriod.Minute ? "板块分时" : ChartPeriods.Display(Periods[i]);
                Size size = Ui.ButtonSize(button);
                if (x + size.Width > width && x > 0) { x = 0; y += rowHeight + 4; rowHeight = 0; }
                button.SetBounds(x, y, size.Width, size.Height); x += size.Width + 4; rowHeight = Math.Max(rowHeight, size.Height);
            }
            int bodyTop = y + rowHeight + (int)Math.Round(8 * scale);
            if (fund)
            {
                Size size = Ui.ButtonSize(_linkBoards);
                _linkBoards.SetBounds(0, bodyTop, size.Width, size.Height);
                _boardPicker.SetBounds(size.Width + 8, bodyTop + 2, Math.Max(80, width - size.Width - 8), size.Height);
                bodyTop += size.Height + 6;
            }
            int bodyHeight = Math.Max(40, height - bodyTop);
            _depth.SetBounds(width - depthWidth, bodyTop, depthWidth, bodyHeight);
            _chart.SetBounds(0, bodyTop, Math.Max(40, width - depthWidth - 8), bodyHeight);
        }

        // ── 数据加载 ──

        private async void LoadData(bool immediate)
        {
            if (_item == null || _loading || IsDisposed) return;
            _loading = true;
            int seq = ++_loadSeq;

            try
            {
                // 盘口与图表可以并行
                System.Threading.Tasks.Task<Depth> depthTask = FetchDepthSafeAsync(_item.Code, _item.Market);

                if (_period == ChartPeriod.Minute)
                {
                    if (Symbols.IsOpenFund(_item.Code))
                    {
                        Quote latestNav = _host.Hub.GetQuote(_item.Code, _item.Market);
                        // 分时图跟着「主数值」走：主数值是估值（盘中/午休）就画关联板块分时，
                        // 主数值是正式净值（当天净值已公布）才画净值曲线 —— 标题里的百分比和
                        // 图上那条线必须是同一套口径。
                        FundDisplay fundDisplay = FundPresentation.Describe(
                            latestNav, _host.EstimateFund(_item.Code), MarketClock.NowCst());
                        // 主数值是估值就画板块分时；是今日净值就画净值曲线；
                        // 压根没关联板块（连板块分时都拿不到）时也只能画净值曲线。
                        bool showNavLine = fundDisplay == null || fundDisplay.Official || _boardPicker.Items.Count == 0;
                        if (showNavLine)
                        {
                            var history = await _host.Hub.FetchKLineAsync(_item.Code, _item.Market, ChartPeriod.Daily);
                            if (seq != _loadSeq || IsDisposed) return;
                            _chart.ShowNavLine(history, "净值已更新 " + latestNav.PriceTime + " · 实际涨跌 " + Symbols.FormatPct(latestNav.ChangePct));
                        }
                        else
                        {
                        BoardLink board = _boardPicker.SelectedItem as BoardLink;
                        if (board == null) _chart.SetEmpty("点击「关联板块」选择要跟踪的板块");
                        else
                        {
                            var trend = await _host.Hub.Boards.FetchTrendAsync(board.Code);
                            if (seq != _loadSeq || IsDisposed) return;
                            // 海外指数（纳斯达克/恒生科技…）按当地时段交易，最新一段分时
                            // 常常不在「今天」，直接画出来并注明交易日，比报一句「未更新」有用。
                            bool overseas = IndexBoards.IsIndexCode(board.Code);
                            if (trend.Points.Count == 0) _chart.SetEmpty("当日板块分时等待开盘更新");
                            else if (trend.Date == MarketClock.NowCst().Date)
                                _chart.ShowPercentTrend(trend.Points, board.Name + " · 当日涨跌幅 · " + trend.Date.ToString("MM-dd"));
                            else if (overseas)
                                _chart.ShowPercentTrend(trend.Points, board.Name + " · 最近交易日 " + trend.Date.ToString("MM-dd") + " · 涨跌幅");
                            else _chart.SetEmpty("当日板块分时未更新（最近 " + trend.Date.ToString("MM-dd") + "）");
                        }
                        }
                    }
                    else
                    {
                        List<MinutePoint> points = await _host.Hub.FetchMinuteAsync(_item.Code, _item.Market);
                        if (seq != _loadSeq || IsDisposed) return;
                        _minuteData = points;
                        Quote quote = _host.Hub.GetQuote(_item.Code, _item.Market);
                        _prevClose = quote != null && quote.Price > 0 && quote.Change != 0
                            ? quote.Price - quote.Change
                            : (points.Count > 0 ? points[0].Open : 0);
                        _chart.ShowMinute(points, _prevClose, "分时 · " + _item.Name);
                    }
                }
                else
                {
                    List<KLineBar> bars = await _host.Hub.FetchKLineAsync(_item.Code, _item.Market, _period);
                    if (seq != _loadSeq || IsDisposed) return;
                    bool fund = Symbols.IsOpenFund(_item.Code);
                    string caption = ChartPeriods.Display(_period) + " · " + _item.Name
                        + (fund ? "   单位净值曲线" : "   MA5/10/20");
                    if (fund)
                    {
                        // 场外基金没有 K 线，画的是单位净值折线 —— 明确标注，别让人误以为是交易数据
                        Quote nav = _host.Hub.GetQuote(_item.Code, _item.Market);
                        string kind = FundPresentation.UseNav(nav, MarketClock.NowCst()) ? Symbols.PriceKindText(nav) : "";
                        if (kind.Length > 0) caption += " · " + kind;
                    }
                    if (fund) _chart.ShowNavLine(bars, caption);
                    else _chart.ShowKLine(bars, caption);
                }

                Depth depth = await depthTask;
                if (seq != _loadSeq || IsDisposed) return;
                _depthData = depth;
                _depth.Invalidate();
                _seconds = 0;
            }
            catch (Exception ex)
            {
                if (seq != _loadSeq || IsDisposed) return;
                Log.Warn("详情加载失败: " + ex.Message);
                _chart.SetEmpty("加载失败：" + ex.Message);
            }
            finally
            {
                if (seq == _loadSeq) _loading = false;
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_item == null || !Visible) return;
            _seconds++;

            // 盘口 3 秒、分时 5 秒、日K 30 秒、周月K 60 秒
            int chartInterval = _period == ChartPeriod.Minute ? 5
                : (_period == ChartPeriod.Weekly || _period == ChartPeriod.Monthly) ? 60 : 30;

            if (_seconds % 3 == 0) RefreshDepth();
            if (_seconds % chartInterval == 0) LoadData(false);
        }

        private async void RefreshDepth()
        {
            if (_item == null || _loading || IsDisposed) return;
            int seq = _loadSeq;
            try
            {
                Depth depth = await _host.Hub.FetchDepthAsync(_item.Code, _item.Market);
                if (seq != _loadSeq || IsDisposed) return;
                _depthData = depth;
                _depth.Invalidate();
            }
            catch (Exception ex)
            {
                Log.Warn("盘口刷新失败: " + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task<Depth> FetchDepthSafeAsync(string code, string market)
        {
            try { return await _host.Hub.FetchDepthAsync(code, market); }
            catch (Exception ex)
            {
                Log.Warn("盘口加载失败: " + ex.Message);
                return new Depth { Code = code };
            }
        }

        private void OnQuotesUpdated()
        {
            if (_item == null) return;
            Quote quote = _host.Hub.GetQuote(_item.Code, _item.Market);
            if (quote == null) return;
            RefreshTitle(quote);
            if (Symbols.IsOpenFund(_item.Code))
            {
                bool mode = FundPresentation.UseNav(quote, MarketClock.NowCst());
                if (mode != _lastFundNavMode)
                {
                    _lastFundNavMode = mode;
                    if (!mode) { _period = ChartPeriod.Minute; UpdatePeriodButtons(); }
                    LayoutChildren();
                    ++_loadSeq; _loading = false; LoadData(true);
                }
            }
            _depth.Invalidate();
        }

        // ── 盘口 + 摘要 ──

        private void OnDepthPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.Card);
            if (_item == null) return;

            if (Symbols.IsOpenFund(_item.Code))
            {
                DrawFundSummary(g);
                return;
            }

            int width = _depth.ClientSize.Width;
            using (StringFormat left = Theme.LeftMiddle())
            using (StringFormat right = Theme.RightMiddle())
            using (SolidBrush secondary = new SolidBrush(Theme.TextSecondary))
            using (SolidBrush tertiary = new SolidBrush(Theme.TextTertiary))
            using (SolidBrush primary = new SolidBrush(Theme.TextPrimary))
            {
                int y = 0;
                g.DrawString("五档盘口", Theme.FHeader, secondary, new RectangleF(0, y, width, 16), left);
                y += 22;

                Depth depth = _depthData;
                if (depth == null)
                {
                    g.DrawString("加载中…", Theme.FSmall, tertiary, new RectangleF(0, y, width, 16), left);
                }
                else
                {
                    // 卖 5→卖 1
                    for (int i = depth.Asks.Count - 1; i >= 0; i--)
                    {
                        y = DrawDepthRow(g, depth.Asks[i], "卖" + (i + 1), y, width, left, right, tertiary);
                    }
                    using (Pen line = new Pen(Theme.Divider, 1f))
                        g.DrawLine(line, 0, y + 2, width, y + 2);
                    y += 6;
                    for (int i = 0; i < depth.Bids.Count; i++)
                    {
                        y = DrawDepthRow(g, depth.Bids[i], "买" + (i + 1), y, width, left, right, tertiary);
                    }
                }

                y += 14;
                g.DrawString("摘要", Theme.FHeader, secondary, new RectangleF(0, y, width, 16), left);
                y += 22;

                Quote quote = _host.Hub.GetQuote(_item.Code, _item.Market);
                if (quote == null)
                {
                    g.DrawString("等待行情…", Theme.FSmall, tertiary, new RectangleF(0, y, width, 16), left);
                    return;
                }

                // 标签列宽按实测文本算，长标签（「成交量」）不会被当成短标签处理而挤压数值列
                string[] captions = new string[] { "今开", "最高", "最低", "成交量", "成交额", "换手率" };
                int labelWidth = Theme.MeasureMax(g, captions, Theme.FSmall) + 8;

                y = DrawSummaryRow(g, "今开", Symbols.FormatPrice(quote.Open), quote.Open, quote, y, width, labelWidth, left, right, secondary, primary);
                y = DrawSummaryRow(g, "最高", Symbols.FormatPrice(quote.High), quote.High, quote, y, width, labelWidth, left, right, secondary, primary);
                y = DrawSummaryRow(g, "最低", Symbols.FormatPrice(quote.Low), quote.Low, quote, y, width, labelWidth, left, right, secondary, primary);
                y = DrawSummaryRow(g, "成交量", Symbols.FormatVolume(quote.Volume), 0, null, y, width, labelWidth, left, right, secondary, primary);
                y = DrawSummaryRow(g, "成交额", Symbols.FormatAmount(quote.Turnover), 0, null, y, width, labelWidth, left, right, secondary, primary);
                if (quote.TurnoverRate.HasValue)
                    DrawSummaryRow(g, "换手率", quote.TurnoverRate.Value.ToString("F2") + "%", 0, null, y, width, labelWidth, left, right, secondary, primary);

                y += 26;
                g.DrawString("更新 " + _host.Hub.LastUpdate.ToString("HH:mm:ss"), Theme.FHint, tertiary,
                    new RectangleF(0, y, width, 16), left);
            }
        }

        private void DrawFundSummary(Graphics g)
        {
            Quote quote = _host.Hub.GetQuote(_item.Code, _item.Market);
            int width = _depth.ClientSize.Width, y = 0;
            using (var primary = new SolidBrush(Theme.TextPrimary))
            using (var secondary = new SolidBrush(Theme.TextSecondary))
            using (var tertiary = new SolidBrush(Theme.TextTertiary))
            using (StringFormat left = Theme.LeftMiddle())
            {
                if (quote == null)
                {
                    g.DrawString("等待行情…", Theme.FHeader, secondary, new RectangleF(0, y, width, 16), left);
                    return;
                }

                FundEstimate estimate = _host.EstimateFund(_item.Code);
                FundDisplay display = FundPresentation.Describe(quote, estimate, MarketClock.NowCst());
                bool official = display != null && display.Official;
                bool estimated = display != null && display.Estimated;
                g.DrawString(official ? "最新净值 · " + quote.PriceTime
                        : estimated ? "盘中估值 · 板块等权" : "关联板块 · 当日行情",
                    Theme.FHeader, secondary, new RectangleF(0, y, width, 16), left);
                y += 22;

                if (official)
                {
                    using (var brush = new SolidBrush(Theme.Pct(quote.ChangePct)))
                        g.DrawString(quote.Price.ToString("F4") + "   " + Symbols.FormatPct(quote.ChangePct),
                            Theme.FValueLarge, brush, new RectangleF(0, y, width, 26), left);
                    y += 32;
                    string kind = Symbols.PriceKindText(quote);
                    if (kind.Length > 0)
                    {
                        g.DrawString(kind, Theme.FHint, tertiary, new RectangleF(0, y, width, 16), left);
                        y += 20;
                    }
                }
                else if (estimated)
                {
                    using (var brush = new SolidBrush(Theme.Pct(display.Pct)))
                        g.DrawString(display.PriceText + "   " + Symbols.FormatPct(display.Pct),
                            Theme.FValueLarge, brush, new RectangleF(0, y, width, 26), left);
                    y += 32;
                    string hint = FundPresentation.LastNavText(quote);
                    if (hint.Length > 0)
                    {
                        g.DrawString(hint, Theme.FHint, tertiary, new RectangleF(0, y, width, 16), left);
                        y += 20;
                    }
                }
                else
                {
                    // 拿不到板块行情：把上一净值放在这里，避免「整块面板一个数字都没有」
                    string lastNav = FundPresentation.LastNavText(quote);
                    if (lastNav.Length > 0)
                    {
                        g.DrawString(lastNav, Theme.FNormal, secondary, new RectangleF(0, y, width, 18), left);
                        y += 22;
                    }
                }

                y += 6;
                g.DrawString("关联板块", Theme.FHeader, secondary, new RectangleF(0, y, width, 16), left);
                y += 22;

                var links = _host.GetFundBoards(_item.Code);
                if (links.Count == 0)
                {
                    g.DrawString("尚未关联板块\n点上方「关联板块」按钮设置", Theme.FSmall, tertiary,
                        new RectangleF(0, y, width, 44), left);
                    return;
                }
                for (int i = 0; i < links.Count; i++)
                {
                    BoardLink link = links[i];
                    string name = link.Name;
                    g.DrawString(name, Theme.FNormal, primary, new RectangleF(0, y, width, 18), left);

                    double? pct = null;
                    BoardQuote board = _host.Hub.Boards.Get(link.Code);
                    if (board != null) pct = board.ChangePct;

                    if (pct.HasValue)
                    {
                        string text = Symbols.FormatPct(pct);
                        int pillWidth = Theme.Measure(g, text, Theme.FMonoSmall) + 14;
                        Rectangle pill = new Rectangle(width - pillWidth, y + 1, pillWidth, 16);
                        Theme.DrawPill(g, pill, Theme.PctSoft(pct));
                        using (var brush = new SolidBrush(Theme.Pct(pct)))
                        using (StringFormat center = Theme.CenterMiddle())
                            g.DrawString(text, Theme.FMonoSmall, brush, pill, center);
                    }

                    string detail = board == null ? "等待行情"
                        : !board.IsCurrent(MarketClock.NowCst()) ? "数据待更新" : "";
                    if (detail.Length > 0)
                    {
                        g.DrawString(detail, Theme.FHint, tertiary, new RectangleF(0, y + 18, width, 16), left);
                        y += 38;
                    }
                    else y += 24;
                }
            }
        }

        private int DrawDepthRow(Graphics g, Level level, string label, int y, int width,
            StringFormat left, StringFormat right, SolidBrush labelBrush)
        {
            g.DrawString(label, Theme.FSmall, labelBrush, new RectangleF(0, y, 28, 16), left);
            g.DrawString(Symbols.FormatPrice(level.Price), Theme.FMono, labelBrush,
                new RectangleF(30, y, 62, 16), right);
            g.DrawString(Symbols.FormatVolume(level.Volume, "0"), Theme.FMono, labelBrush,
                new RectangleF(96, y, width - 96, 16), right);
            return y + 17;
        }

        private int DrawSummaryRow(Graphics g, string label, string value, double comparePrice, Quote quote,
            int y, int width, int labelWidth, StringFormat left, StringFormat right, SolidBrush labelBrush, SolidBrush valueBrush)
        {
            g.DrawString(label, Theme.FSmall, labelBrush, new RectangleF(0, y, labelWidth, 16), left);

            Color color = Theme.TextPrimary;
            if (quote != null && comparePrice > 0)
            {
                double prevClose = quote.Price - quote.Change;
                if (prevClose > 0) color = comparePrice >= prevClose ? Theme.Up : Theme.Down;
            }
            using (SolidBrush brush = new SolidBrush(color))
                g.DrawString(value, Theme.FMono, brush, new RectangleF(labelWidth, y, Math.Max(10, width - labelWidth), 16), right);
            return y + 18;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ++_loadSeq;
                _timer.Stop();
                _timer.Dispose();
                _host.Hub.QuotesUpdated -= OnQuotesUpdated;
                _host.ThemeChanged -= ApplyTheme;
            }
            base.Dispose(disposing);
        }
    }
}
