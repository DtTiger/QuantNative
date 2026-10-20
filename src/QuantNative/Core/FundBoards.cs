using System;
using System.Collections.Generic;
using System.Globalization;

namespace QuantNative.Core
{
    public sealed class BoardLink
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public override string ToString() { return Name + "  " + Code; }
        /// <summary>
        /// 合法板块代码：A 股行业/概念板块 BKxxxx，或海外指数伪板块 ix100.NDX
        /// （见 <see cref="IndexBoards"/>）。后者让 QDII 指数基金也能有可跟踪的行情。
        /// </summary>
        public static bool ValidCode(string code)
        {
            if (IndexBoards.IsIndexCode(code)) return true;
            if (code == null || code.Length != 6 || !code.StartsWith("BK", StringComparison.Ordinal)) return false;
            for (int i = 2; i < code.Length; i++) if (!char.IsDigit(code[i])) return false;
            return true;
        }

        /// <summary>展示用标签：海外指数标「海外指」，A 股板块标「板块」。</summary>
        public static string KindLabel(string code)
        {
            return IndexBoards.IsIndexCode(code) ? "海外指数" : "板块";
        }
        public static string Encode(IList<BoardLink> links)
        {
            List<string> values = new List<string>();
            foreach (BoardLink link in links)
                if (ValidCode(link.Code)) values.Add(link.Code + ":" + Uri.EscapeDataString(link.Name ?? link.Code));
            return string.Join("|", values.ToArray());
        }
        public static List<BoardLink> Decode(string value)
        {
            var result = new List<BoardLink>(); var seen = new HashSet<string>();
            foreach (string part in (value ?? "").Split('|'))
            {
                int colon = part.IndexOf(':'); if (colon < 0) continue;
                string code = part.Substring(0, colon);
                if (!ValidCode(code) || !seen.Add(code)) continue;
                try { result.Add(new BoardLink { Code = code, Name = Uri.UnescapeDataString(part.Substring(colon + 1)) }); } catch { }
            }
            return result;
        }
    }
    public sealed class BoardQuote
    {
        public string Code, Name;
        public double ChangePct;
        public DateTime DataTime;
        public DateTime ReceivedAt;
        /// <summary>海外指数：数据跨隔夜/周末仍是「最新一期」，不能按 A 股口径判过期。</summary>
        public bool Overseas;

        public bool IsCurrent(DateTime now)
        {
            if (now - ReceivedAt >= TimeSpan.FromMinutes(2)) return false;
            if (Overseas) return (now - DataTime).TotalDays <= 5;
            return DataTime.Date == now.Date;
        }
    }
    public sealed class BoardTrend
    {
        public string Name;
        public DateTime Date;
        public List<MinutePoint> Points = new List<MinutePoint>();
    }

    /// <summary>场外基金的盘中估算结果。</summary>
    public sealed class FundEstimate
    {
        /// <summary>是否有可用的实时数据。</summary>
        public bool HasValue;
        /// <summary>估算涨跌幅（%）。</summary>
        public double Pct;
        /// <summary>参与估算的标的数（重仓股只数 / 板块个数）。</summary>
        public int Count;
        /// <summary>有数据但已过期、没参与计算的个数。</summary>
        public int Stale;
        /// <summary>估算来源：holdings=前十大重仓加权；boards=关联板块等权；index=海外指数。</summary>
        public string Source = "boards";
        /// <summary>口径说明，UI 直接显示（如「重仓股 10 只 · 覆盖 84%」）。</summary>
        public string Detail = "";
    }

    /// <summary>
    /// 一只场外基金此刻该显示的数值（UI 只负责画，不算）。
    ///   · <see cref="Official"/>：当天正式净值已公布 → 显示净值与官方日涨跌；
    ///   · <see cref="Estimated"/>：盘中（含午休）→ 显示板块等权估算，价格带 * 号；
    ///   · 两者都不成立时退回「上一净值」，涨跌留空。
    /// </summary>
    public sealed class FundDisplay
    {
        /// <summary>今天的正式净值已公布（pct 是真实的当日涨跌）。</summary>
        public bool Official;
        /// <summary>按关联板块等权估算（价格带 *）。</summary>
        public bool Estimated;
        /// <summary>既没有今日净值也没有板块行情：只显示上一净值，涨跌幅无效。</summary>
        public bool Fallback;
        public double Price;
        public double Change;
        public double Pct;
        /// <summary>现价文本：今日净值 4 位；估值 4 位 + "*"；退回时按价格精度。</summary>
        public string PriceText;

        /// <summary>是否有可信的当日涨跌幅（Official / Estimated）。</summary>
        public bool HasLivePct { get { return Official || Estimated; } }
    }
    public static class FundPresentation
    {
        /// <summary>A 股午休时段（上午收盘后到下午开盘前，盘面完全不动）。</summary>
        public static bool IsLunchBreak(TimeSpan time)
        {
            return time >= new TimeSpan(11, 30, 0) && time < new TimeSpan(13, 0, 0);
        }

        public static bool UseNav(Quote quote, DateTime now)
        {
            if (quote == null || quote.PriceKind != "净值" || quote.Price <= 0) return false;
            DateTime date;
            if (!DateTime.TryParseExact(quote.PriceTime, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return false;
            if (date.Date > now.Date) return false;
            bool weekend = now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday;
            // Published NAV remains visible overnight/weekends. At 09:00 a new
            // weekday starts board mode; continuous trading always stays in board mode.
            if (weekend || now.TimeOfDay < TimeSpan.FromHours(9)) return true;
            // 午休（11:30–13:00）：盘面不动、板块行情也停在上午收盘价，此时继续隐藏净值
            // 只会让「详情 / 自选 / 浮窗」一起变成一片空白。这里直接显示已公布的最新净值，
            // 口径说明里带着净值日期（「净值 2026-10-16」），不会冒充当日净值。
            if (IsLunchBreak(now.TimeOfDay)) return true;
            if (now.TimeOfDay < TimeSpan.FromHours(15)) return false;
            return date.Date == now.Date;
        }

        /// <summary>净值日期的短形式（10-16），列表里跟在「上一净值」后面用。</summary>
        public static string NavDateShort(Quote quote)
        {
            if (quote == null || string.IsNullOrEmpty(quote.PriceTime)) return "";
            string date = quote.PriceTime;
            return date.Length >= 10 ? date.Substring(5) : date;
        }

        /// <summary>
        /// 「上一净值 3.216（10-16）」—— 板块模式下也要让人看得到最新净值，
        /// 否则用户会觉得「这只基金没有数据」。
        /// </summary>
        public static string LastNavText(Quote quote)
        {
            if (quote == null || quote.Price <= 0 || quote.PriceKind != "净值") return "";
            string date = NavDateShort(quote);
            return "上一净值 " + Symbols.FormatPrice(quote.Price) + (date.Length > 0 ? "（" + date + "）" : "");
        }

        /// <summary>盘中标题下的说明：主数值是上一净值，这里说清口径 + 净值日期。</summary>
        public static string BoardModeHint(Quote quote)
        {
            if (quote != null && quote.Price > 0 && !string.IsNullOrEmpty(quote.PriceTime))
                return "上一净值 " + quote.PriceTime + " · 盘中参考关联板块";
            return "关联板块参考 · 等待当日净值更新";
        }

        // ── 盘中估算（关联板块等权） ──

        /// <summary>
        /// 关联板块等权平均。这是**兜底**口径：拿不到重仓股（QDII、季报未更新、接口不通）时才用。
        /// 真正的主口径是 HoldingsEstimator 的前十大重仓加权 —— 板块等权与基金真实持仓可能差很远
        /// （017811 关联「半导体材料+半导体设备」，板块等权 +1.84%，重仓股加权只有 −0.21%）。
        /// </summary>
        public static FundEstimate Estimate(IList<BoardQuote> quotes, DateTime now)
        {
            FundEstimate estimate = new FundEstimate();
            estimate.Source = "boards";
            if (quotes == null) return estimate;
            double sum = 0;
            int used = 0, stale = 0;
            bool allOverseas = true;
            for (int i = 0; i < quotes.Count; i++)
            {
                BoardQuote quote = quotes[i];
                if (quote == null) { stale++; continue; }
                if (!quote.IsCurrent(now)) { stale++; continue; }
                sum += quote.ChangePct;
                used++;
                if (!quote.Overseas) allOverseas = false;
            }
            if (used > 0)
            {
                estimate.HasValue = true;
                estimate.Pct = sum / used;
                estimate.Count = used;
                if (allOverseas) estimate.Source = "index";
            }
            estimate.Stale = stale;
            estimate.Detail = (estimate.Source == "index" ? "海外指数" : "板块") + " " + used + " 个等权";
            return estimate;
        }

        /// <summary>估算净值 = 上一净值 × (1 + 估算涨跌幅)。没有估值依据时返回 0。</summary>
        public static double EstimateNav(Quote quote, FundEstimate estimate)
        {
            if (quote == null || quote.Price <= 0 || estimate == null || !estimate.HasValue) return 0;
            return quote.Price * (1 + estimate.Pct / 100.0);
        }

        /// <summary>
        /// 是不是「今天的」正式净值 —— 只有它后面的日涨跌才是今天的涨跌。
        /// 净值日期还是昨天/上周五时，那个涨跌幅是上一交易日的，绝不能当成今日涨跌显示。
        /// </summary>
        public static bool IsTodayNav(Quote quote, DateTime now)
        {
            return quote != null
                && quote.PriceKind == "净值"
                && quote.Price > 0
                && string.Equals(quote.PriceTime, now.ToString("yyyy-MM-dd"), StringComparison.Ordinal);
        }

        /// <summary>
        /// 把「今日正式净值 / 板块估算 / 上一净值」收敛成一个显示结果；无行情返回 null。
        ///
        /// 优先级（这三条是「涨跌显示对不对」的全部规则，UI 不要再自己判断）：
        ///   1. 今日正式净值已公布 → 净值 + 官方日涨跌（唯一真实的当日涨跌）；
        ///   2. 否则有板块行情 → 板块等权估值（价格带 *），午休也一样；
        ///   3. 两者都没有 → 只给上一净值（弱色），**涨跌幅留空**。
        ///      第 3 条是 2.5.2 修的：以前撤回落到「上一净值 + 上一交易日的涨跌幅」，
        ///      用户看到「跌幅 0.08%」以为是今天的，其实那是昨天收盘的净值涨跌。
        /// </summary>
        public static FundDisplay Describe(Quote quote, FundEstimate estimate, DateTime now)
        {
            if (quote == null || quote.Price <= 0) return null;
            FundDisplay display = new FundDisplay();

            if (IsTodayNav(quote, now))
            {
                display.Official = true;
                display.Price = quote.Price;
                display.Pct = quote.ChangePct;
                display.Change = quote.Change;
                display.PriceText = quote.Price.ToString("F4", CultureInfo.InvariantCulture);
                return display;
            }

            if (estimate != null && estimate.HasValue)
            {
                display.Estimated = true;
                display.Price = EstimateNav(quote, estimate);
                display.Change = display.Price - quote.Price;
                display.Pct = estimate.Pct;
                display.PriceText = display.Price.ToString("F4", CultureInfo.InvariantCulture) + "*";
                return display;
            }

            display.Fallback = true;
            display.Price = quote.Price;
            display.Pct = double.NaN;      // 没有可用的当日涨跌，UI 必须留空
            display.PriceText = Symbols.FormatPrice(quote.Price);
            return display;
        }

        /// <summary>
        /// 自选表「关联板块 / 净值日期」列：正式净值 → 净值 + 日期；估值 → 估值口径；
        /// 退回 → 上一净值 + 板块摘要。
        /// </summary>
        public static string DetailText(Quote quote, FundEstimate estimate, FundDisplay display, string boardSummary)
        {
            if (display != null && display.Official) return "净值 " + quote.PriceTime;
            if (display != null && display.Estimated)
            {
                string text = "估值 · " + (estimate == null ? "" : estimate.Detail);
                // 海外指数基金的净值按 T+1/T+2 公布，盘中只能给跟踪指数的隔夜涨跌
                if (estimate != null && estimate.Source == "index") text += "（QDII 净值 T+1 公布）";
                if (estimate != null && estimate.Stale > 0) text += "（" + estimate.Stale + " 个待更新）";
                return text + " · 上一净值 " + NavDateShort(quote);
            }
            return quote != null && quote.Price > 0
                ? "上一净值 " + NavDateShort(quote) + " · " + boardSummary
                : boardSummary;
        }

        /// <summary>估值口径说明：「估值 11:37 · 重仓股 10 只 · 覆盖 84% · 上一净值 3.216（10-16）」。</summary>
        public static string EstimateHint(Quote quote, FundEstimate estimate, DateTime now)
        {
            if (estimate == null || !estimate.HasValue) return "关联板块暂无可用的实时行情";
            string text = "估值 " + now.ToString("HH:mm") + " · " + estimate.Detail;
            if (estimate.Source == "index") text += "（QDII 净值 T+1 公布）";
            if (estimate.Stale > 0) text += "（" + estimate.Stale + " 个待更新）";
            string nav = LastNavText(quote);
            return nav.Length > 0 ? text + " · " + nav : text;
        }

        public static string BoardText(BoardLink link, BoardQuote quote, DateTime now)
        {
            if (quote == null) return link.Name + " --（等待行情）";
            if (!quote.IsCurrent(now)) return link.Name + " --（数据待更新）";
            return link.Name + " " + Symbols.FormatPct(quote.ChangePct);
        }
    }
}
