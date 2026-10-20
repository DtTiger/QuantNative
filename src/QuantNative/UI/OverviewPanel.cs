using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;
using QuantNative.Core;

namespace QuantNative.UI
{
    /// <summary>
    /// 市场概览（「大盘」）：两市成交额 + 涨跌家数 + 行业/概念板块榜。
    /// 数据来自 OverviewClient（新浪成交额 + 东财涨跌家数/板块），节奏由 QuoteHub 控制。
    ///
    /// 2.4.0 重做：
    ///   * 三块内容各自成卡片（成交额 → 行业榜 → 概念榜），标题带强调竖条；
    ///   * 所有列的位置都按「实测文本宽度」计算，不再用固定像素 ——
    ///     面板被拉窄或数字位数变化时（成交额从「1.23万亿」变成「9876.54亿」），
    ///     左右两块数据不会再互相压到或跑出边界。这是旧版「大盘数据显示位置不正确」的根因。
    ///   * 涨跌幅换成色块药丸，扫一眼就能看出红绿。
    /// </summary>
    public sealed class OverviewPanel : UserControl
    {
        private readonly AppHost _host;
        private Button _upTab, _downTab;
        private string _direction = "up";

        public OverviewPanel(AppHost host)
        {
            _host = host;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;

            _upTab = Ui.Button("涨幅榜", delegate { SetDirection("up"); });
            _downTab = Ui.Button("跌幅榜", delegate { SetDirection("down"); });
            Ui.Round(_upTab, 6);
            Ui.Round(_downTab, 6);
            Controls.Add(_upTab);
            Controls.Add(_downTab);

            _host.Hub.OverviewUpdated += OnOverviewUpdated;
            _host.ThemeChanged += ApplyTheme;
            _host.Hub.SetOverviewEnabled(true);
            SetDirection("up");
        }

        private void SetDirection(string direction)
        {
            _direction = direction == "down" ? "down" : "up";
            _host.Hub.SetOverviewDirection(_direction);
            _host.Hub.SetOverviewEnabled(true);
            StyleTabs();
            Invalidate();
        }

        private void StyleTabs()
        {
            ApplyTabStyle(_upTab, _direction == "up");
            ApplyTabStyle(_downTab, _direction == "down");
        }

        private static void ApplyTabStyle(Button button, bool active)
        {
            button.BackColor = active ? Theme.Accent : Theme.Surface2;
            button.ForeColor = active ? Color.White : Theme.TextSecondary;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = active ? Theme.Accent : Theme.Surface3;
            button.Font = Theme.FTag;
        }

        private void OnOverviewUpdated()
        {
            Invalidate();
        }

        public void ApplyTheme()
        {
            BackColor = Theme.Bg;
            StyleTabs();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutTabs();
        }

        private void LayoutTabs()
        {
            if (_upTab == null || _downTab == null) return;
            float scale = Theme.Scale(this);
            int pad = (int)Math.Round(12 * scale);
            int height = (int)Math.Round(22 * scale);
            int downWidth = Math.Max((int)Math.Round(52 * scale), Ui.ButtonSize(_downTab).Width - 6);
            int upWidth = Math.Max((int)Math.Round(52 * scale), Ui.ButtonSize(_upTab).Width - 6);
            _downTab.SetBounds(ClientSize.Width - pad - downWidth, pad, downWidth, height);
            _upTab.SetBounds(_downTab.Left - upWidth - (int)Math.Round(4 * scale), pad, upWidth, height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);

            LayoutTabs();

            int width = ClientSize.Width;
            if (width < 80) return;

            float scale = Theme.Scale(this);
            int pad = (int)Math.Round(12 * scale);
            int gap = (int)Math.Round(10 * scale);
            int radius = (int)Math.Round(10 * scale);
            int cardWidth = width - pad * 2;
            if (cardWidth < 60) return;

            MarketOverview overview = _host.Hub.Overview;

            int summaryHeight = (int)Math.Round(98 * scale);
            int rows = 5;
            int listHeight = (int)Math.Round(28 * scale) + rows * (int)Math.Round(22 * scale) + (int)Math.Round(10 * scale);

            Rectangle summaryCard = new Rectangle(pad, pad, cardWidth, summaryHeight);
            Rectangle industryCard = new Rectangle(pad, summaryCard.Bottom + gap, cardWidth, listHeight);
            Rectangle conceptCard = new Rectangle(pad, industryCard.Bottom + gap, cardWidth, listHeight);

            using (StringFormat left = Theme.LeftMiddle())
            using (StringFormat right = Theme.RightMiddle())
            using (SolidBrush primary = new SolidBrush(Theme.TextPrimary))
            using (SolidBrush secondary = new SolidBrush(Theme.TextSecondary))
            using (SolidBrush tertiary = new SolidBrush(Theme.TextTertiary))
            {
                DrawSummaryCard(g, overview, summaryCard, scale, left, right, primary, secondary, tertiary);
                DrawListCard(g, overview == null ? null : overview.Industry,
                    _direction == "up" ? "行业涨幅榜" : "行业跌幅榜", industryCard, scale, left, right, primary, secondary, tertiary);
                DrawListCard(g, overview == null ? null : overview.Concept,
                    _direction == "up" ? "概念涨幅榜" : "概念跌幅榜", conceptCard, scale, left, right, primary, secondary, tertiary);
            }
        }

        // ── 成交额 + 涨跌家数 ──

        private void DrawSummaryCard(Graphics g, MarketOverview overview, Rectangle card, float scale,
            StringFormat left, StringFormat right,
            SolidBrush primary, SolidBrush secondary, SolidBrush tertiary)
        {
            Theme.DrawCard(g, card, (int)Math.Round(10 * scale));
            int padx = (int)Math.Round(12 * scale);
            int inner = card.Width - padx * 2;
            int y = card.Y + (int)Math.Round(9 * scale);

            DrawSectionTitle(g, "大盘", new Rectangle(card.X + padx, y, inner, (int)Math.Round(18 * scale)), scale, primary, left);
            y += (int)Math.Round(22 * scale);

            if (overview == null)
            {
                g.DrawString("加载中…", Theme.FSmall, tertiary, new RectangleF(card.X + padx, y, inner, 18), left);
                return;
            }

            // 成交额：标签在左、大号数值在右。数值用「按字体行高撑开」的矩形 ——
            // 17pt 的 FDisplay 行高 27px，随手给个 24px 的框 GDI+ 会整行不画（见 Theme.TextRow）。
            int bigHeight = Theme.FDisplay.Height + (int)Math.Round(6 * scale);
            Rectangle amountBand = new Rectangle(card.X + padx, y, inner, bigHeight);
            g.DrawString("两市成交额", Theme.FSmall, tertiary,
                Theme.TextRow(amountBand, Theme.FSmall, (int)Math.Round(2 * scale)), left);
            g.DrawString(Symbols.FormatAmount(overview.Turnover), Theme.FDisplay, primary,
                Theme.TextRow(amountBand, Theme.FDisplay, 0), right);
            y += bigHeight + (int)Math.Round(2 * scale);

            // 涨跌家数：三段按实测宽度依次右对齐排列（不再用固定 176px 起算）
            string flatText = overview.Flat.ToString();
            string upText = overview.Up.ToString();
            string downText = overview.Down.ToString();
            int rowHeight = Theme.FMono.Height + (int)Math.Round(4 * scale);
            Rectangle row = new Rectangle(card.X + padx, y, inner, rowHeight);
            int flatWidth = Theme.Measure(g, "— " + flatText, Theme.FMono);
            int upWidth = Theme.Measure(g, "↑ " + upText, Theme.FMono);
            int downWidth = Theme.Measure(g, "↓ " + downText, Theme.FMono);
            int segGap = (int)Math.Round(10 * scale);

            g.DrawString("涨跌家数", Theme.FSmall, tertiary,
                Theme.TextRow(row, Theme.FSmall, (int)Math.Round(2 * scale)), left);

            int rightEdge = card.Right - padx;
            int x = rightEdge - flatWidth;
            g.DrawString("— " + flatText, Theme.FMono, secondary, new RectangleF(x, row.Y, flatWidth, row.Height), right);
            x -= downWidth + segGap;
            using (SolidBrush downBrush = new SolidBrush(Theme.Down))
                g.DrawString("↓ " + downText, Theme.FMono, downBrush, new RectangleF(x, row.Y, downWidth, row.Height), right);
            x -= upWidth + segGap;
            using (SolidBrush upBrush = new SolidBrush(Theme.Up))
                g.DrawString("↑ " + upText, Theme.FMono, upBrush, new RectangleF(x, row.Y, upWidth, row.Height), right);
        }

        // ── 板块榜 ──

        private void DrawListCard(Graphics g, List<SectorItem> items, string title, Rectangle card, float scale,
            StringFormat left, StringFormat right,
            SolidBrush primary, SolidBrush secondary, SolidBrush tertiary)
        {
            Theme.DrawCard(g, card, (int)Math.Round(10 * scale));
            int padx = (int)Math.Round(12 * scale);
            int inner = card.Width - padx * 2;
            int y = card.Y + (int)Math.Round(10 * scale);

            DrawSectionTitle(g, _direction == "up" ? title : title,
                new Rectangle(card.X + padx, y, inner, (int)Math.Round(18 * scale)), scale, primary, left);
            y += (int)Math.Round(24 * scale);

            if (items == null || items.Count == 0)
            {
                g.DrawString("暂无数据", Theme.FSmall, tertiary, new RectangleF(card.X + padx, y, inner, 18), left);
                return;
            }

            // 列宽按实测内容算：涨跌幅用 3 位小数等宽字体，整列小数点对齐
            int pillPad = (int)Math.Round(6 * scale);
            int nameWidth = 0, pctWidth = 0;
            for (int i = 0; i < items.Count; i++)
            {
                nameWidth = Math.Max(nameWidth, Theme.Measure(g, items[i].Name, Theme.FNormal));
                pctWidth = Math.Max(pctWidth, Theme.Measure(g, Symbols.FormatPct(items[i].ChangePct), Theme.FMonoSmall));
            }
            pctWidth += pillPad * 2;
            int maxNameWidth = (int)Math.Round(inner * 0.42);
            nameWidth = Math.Min(nameWidth + (int)Math.Round(8 * scale), maxNameWidth);
            int colGap = (int)Math.Round(8 * scale);

            int rowHeight = (int)Math.Round(22 * scale);
            int pillHeight = (int)Math.Round(17 * scale);

            int stockX = card.X + padx + nameWidth + colGap;
            int stockWidth = Math.Max(20, card.Right - padx - pctWidth - colGap - stockX);

            for (int i = 0; i < items.Count && i < 5; i++)
            {
                SectorItem item = items[i];
                Rectangle row = new Rectangle(card.X + padx, y, inner, rowHeight);

                g.DrawString(item.Name, Theme.FNormal, primary,
                    new RectangleF(row.X, row.Y, nameWidth, row.Height), left);

                string stock = _direction == "up" ? item.LeaderName : item.LaggardName;
                double? pct = _direction == "up" ? item.LeaderPct : item.LaggardPct;
                if (!string.IsNullOrEmpty(stock))
                {
                    string text = stock + (pct.HasValue ? " " + Symbols.FormatPct(pct) : "");
                    g.DrawString(text, Theme.FSmall, secondary,
                        new RectangleF(stockX, row.Y, stockWidth, row.Height), left);
                }

                Rectangle pill = new Rectangle(card.Right - padx - pctWidth,
                    row.Y + (row.Height - pillHeight) / 2, pctWidth, pillHeight);
                Theme.DrawPill(g, pill, Theme.PctSoft(item.ChangePct));
                using (SolidBrush pctBrush = new SolidBrush(Theme.Pct(item.ChangePct)))
                using (StringFormat center = Theme.CenterMiddle())
                    g.DrawString(Symbols.FormatPct(item.ChangePct), Theme.FMonoSmall, pctBrush, pill, center);

                y += rowHeight;
            }
        }

        private void DrawSectionTitle(Graphics g, string text, Rectangle rect, float scale,
            SolidBrush brush, StringFormat left)
        {
            Theme.DrawAccentBar(g, new Rectangle(rect.X, rect.Y + Math.Max(1, (rect.Height - (int)Math.Round(12 * scale)) / 2),
                Math.Max(2, (int)Math.Round(3 * scale)), (int)Math.Round(12 * scale)));
            g.DrawString(text, Theme.FHeader, brush,
                new RectangleF(rect.X + (int)Math.Round(9 * scale), rect.Y, Math.Max(10, rect.Width - 9), rect.Height), left);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _host.Hub.OverviewUpdated -= OnOverviewUpdated;
                _host.ThemeChanged -= ApplyTheme;
                _host.Hub.SetOverviewEnabled(false);
            }
            base.Dispose(disposing);
        }
    }
}
