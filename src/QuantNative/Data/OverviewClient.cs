using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using QuantNative.Core;

namespace QuantNative.Data
{
    /// <summary>
    /// 市场概览聚合（对齐 Rust 端 datasource/market.rs）：
    ///   * 两市成交额：新浪 hq.sinajs.cn/list=sh000001,sz399106，第 10 个字段（index 9）= 成交额(元)
    ///     用「深证综指 sz399106」覆盖深市全市场，不用成指 sz399001
    ///   * 涨跌家数：东财 push2ex getTopicZDFenBu，按区间正负自算涨/跌/平
    ///   * 板块榜：东财 push2delay api/qt/clist/get，行业 m:90+t:2、概念 m:90+t:3
    ///
    /// ⚠ 域名坑：push2.eastmoney.com 在部分网络被拦截，必须用 push2delay / push2ex。
    /// </summary>
    public sealed class OverviewClient
    {
        private const string SinaIndexUrl = "https://hq.sinajs.cn/list=sh000001,sz399106";
        private const string Push2Ex = "https://push2ex.eastmoney.com";
        private const string Push2Delay = "https://push2delay.eastmoney.com";
        private const string BreadthUt = "7eea3edcaed734bea9cbfc24409ed989";
        private const string SinaReferer = "https://finance.sina.com.cn";
        private const string EmReferer = "https://quote.eastmoney.com/";

        /// <summary>并行拉取四块数据；任一块失败只降级该字段，不影响整份概览。</summary>
        public async Task<MarketOverview> FetchAsync(string direction)
        {
            Task<double> turnover = FetchTurnoverSafeAsync();
            Task<int[]> breadth = FetchBreadthSafeAsync();
            Task<List<SectorItem>> industry = FetchIndustrySafeAsync(direction);
            Task<List<SectorItem>> concept = FetchConceptSafeAsync(direction);

            await Task.WhenAll(turnover, breadth, industry, concept).ConfigureAwait(false);

            MarketOverview overview = new MarketOverview();
            overview.Turnover = turnover.Result;
            overview.Up = breadth.Result[0];
            overview.Down = breadth.Result[1];
            overview.Flat = breadth.Result[2];
            overview.Industry = industry.Result;
            overview.Concept = concept.Result;
            return overview;
        }

        private async Task<double> FetchTurnoverSafeAsync()
        {
            try { return await FetchTurnoverAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("成交额获取失败，降级为 0: " + ex.Message); return 0; }
        }

        private async Task<int[]> FetchBreadthSafeAsync()
        {
            try { return await FetchBreadthAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("涨跌家数获取失败，降级为 0: " + ex.Message); return new int[3]; }
        }

        private async Task<List<SectorItem>> FetchIndustrySafeAsync(string direction)
        {
            try { return await FetchSectorRankingAsync("m:90+t:2", direction, 5).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("行业板块获取失败，降级为空: " + ex.Message); return new List<SectorItem>(); }
        }

        private async Task<List<SectorItem>> FetchConceptSafeAsync(string direction)
        {
            try { return await FetchConceptRankingAsync(direction).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("概念板块获取失败，降级为空: " + ex.Message); return new List<SectorItem>(); }
        }

        // ── 成交额 ──

        public async Task<double> FetchTurnoverAsync()
        {
            string body = await Http.GetGbkAsync(SinaIndexUrl, SinaReferer).ConfigureAwait(false);
            double total = 0;
            foreach (string line in body.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                int open = line.IndexOf('"', eq + 1);
                if (open < 0) continue;
                int close = line.IndexOf('"', open + 1);
                if (close < 0) continue;
                string[] f = line.Substring(open + 1, close - open - 1).Split(',');
                if (f.Length < 10) continue;
                double value;
                if (double.TryParse(f[9].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out value)) total += value;
            }
            return total;
        }

        // ── 涨跌家数 ──

        /// <summary>返回 [上涨, 下跌, 平盘]。</summary>
        public async Task<int[]> FetchBreadthAsync()
        {
            string url = Push2Ex + "/getTopicZDFenBu?ut=" + BreadthUt + "&dpt=wz.ztzt";
            string text = await Http.GetTextAsync(url, EmReferer).ConfigureAwait(false);

            object fenbuNode = Json.Path(Json.Parse(text), "data", "fenbu");
            object[] fenbu = Json.AsArray(fenbuNode);
            if (fenbu == null || fenbu.Length == 0)
                throw new InvalidOperationException("涨跌家数响应缺少 data.fenbu");

            int up = 0, down = 0, flat = 0;
            for (int i = 0; i < fenbu.Length; i++)
            {
                Dictionary<string, object> item = Json.AsDict(fenbu[i]);
                if (item == null) continue;
                foreach (KeyValuePair<string, object> kv in item)
                {
                    int count = (int)Json.Long(kv.Value);
                    int bucket;
                    if (!int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out bucket)) continue;
                    if (bucket < 0) down += count;
                    else if (bucket > 0) up += count;
                    else flat += count;
                }
            }
            return new[] { up, down, flat };
        }

        // ── 板块榜 ──

        public async Task<List<SectorItem>> FetchSectorRankingAsync(string fs, string direction, int pageSize)
        {
            string po = direction == "down" ? "0" : "1";   // 1=降序（涨幅榜），0=升序（跌幅榜）
            string url = string.Format(CultureInfo.InvariantCulture,
                "{0}/api/qt/clist/get?pn=1&pz={1}&po={2}&np=1&fltt=2&invt=2&fid=f3&fs={3}&fields=f12,f14,f3,f128,f136,f207,f222",
                Push2Delay, pageSize, po, fs);

            string text = await Http.GetTextAsync(url, EmReferer).ConfigureAwait(false);
            object[] diff = Json.AsArray(Json.Path(Json.Parse(text), "data", "diff"));
            List<SectorItem> items = new List<SectorItem>();
            if (diff == null) return items;

            for (int i = 0; i < diff.Length; i++)
            {
                SectorItem item = new SectorItem();
                item.Code = Json.Str(Json.Get(diff[i], "f12"));
                item.Name = Json.Str(Json.Get(diff[i], "f14"));
                if (item.Name.Length == 0) continue;
                item.ChangePct = Json.Num(Json.Get(diff[i], "f3"));
                item.LeaderName = NullIfEmpty(Json.Str(Json.Get(diff[i], "f128")));
                item.LeaderPct = NullableNum(Json.Get(diff[i], "f136"));
                // 领跌股必须取 f207/f222：跌幅榜里 f128 依然是该板块涨得最好的成分股
                item.LaggardName = NullIfEmpty(Json.Str(Json.Get(diff[i], "f207")));
                item.LaggardPct = NullableNum(Json.Get(diff[i], "f222"));
                items.Add(item);
            }
            return items;
        }

        /// <summary>
        /// 概念榜：先多拉 50 条再剔除「非主题概念」（风格/指数/资金/业绩），最后取前 5。
        /// 直接 pz=5 会在涨停潮等极端行情下被非主题板块占满。
        /// </summary>
        public async Task<List<SectorItem>> FetchConceptRankingAsync(string direction)
        {
            List<SectorItem> items = await FetchSectorRankingAsync("m:90+t:3", direction, 50).ConfigureAwait(false);
            List<SectorItem> filtered = new List<SectorItem>();
            for (int i = 0; i < items.Count; i++)
                if (!IsNonConceptBoard(items[i].Code)) filtered.Add(items[i]);
            if (filtered.Count > 5) filtered.RemoveRange(5, filtered.Count - 5);
            return filtered;
        }

        private static string NullIfEmpty(string value)
        {
            return string.IsNullOrEmpty(value) || value == "-" ? null : value;
        }

        private static double? NullableNum(object value)
        {
            if (value == null) return null;
            string s = value as string;
            if (!string.IsNullOrEmpty(s) && s == "-") return null;
            double d = Json.Num(value, double.NaN);
            return double.IsNaN(d) ? (double?)null : d;
        }

        // ── 非主题概念板黑名单（取自东财 f12，来源：Rust 端 market.rs 实测） ──

        private static readonly string[] NonConceptBoards =
        {
            // 宽基/成分指数 + 股份类别/交易机制
            "BK0498","BK0499","BK0500","BK0568","BK0610","BK0611","BK0612",
            "BK0636","BK0638","BK0701","BK0705","BK0742","BK0743","BK0821",
            "BK0867","BK0868","BK0879","BK0999","BK1000","BK1107","BK1108",
            // 风格/因子（历史新高、微盘股、昨日涨停系列…）
            "BK0501","BK0505","BK0511","BK0815","BK0816","BK0817",
            "BK1050","BK1051","BK1053","BK1059","BK1112","BK1158",
            "BK1630","BK1631","BK1632","BK1633","BK1635","BK1636","BK1637",
            "BK1638","BK1639","BK1640","BK1641","BK1642","BK1643","BK1644",
            "BK1645","BK1661","BK1662","BK1663","BK1664","BK1665","BK1666",
            "BK1667","BK1668","BK1669","BK1670","BK1671","BK1672","BK1673",
            "BK1674","BK1675","BK1676","BK1693","BK1698","BK1699","BK1700",
            "BK1710","BK1711","BK1712","BK1713","BK1714","BK1715","BK1716",
            "BK1717","BK1721","BK1722","BK1723","BK1743",
            // 资金/持仓/互联互通
            "BK0520","BK0528","BK0535","BK0536","BK0552","BK0596","BK0707",
            "BK0718","BK0804","BK0823",
            // 业绩预披露
            "BK1198","BK1199","BK1628","BK1680","BK1681","BK1682","BK1749",
            "BK1750","BK1751","BK1752"
        };

        private static readonly HashSet<string> NonConceptSet = new HashSet<string>(NonConceptBoards, StringComparer.Ordinal);

        public static bool IsNonConceptBoard(string code)
        {
            return !string.IsNullOrEmpty(code) && NonConceptSet.Contains(code);
        }
    }
}
