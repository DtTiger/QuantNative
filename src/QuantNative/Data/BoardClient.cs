using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using QuantNative.Core;

namespace QuantNative.Data
{
    public sealed class BoardClient
    {
        private const string Referer = "https://quote.eastmoney.com/";
        private readonly Dictionary<string, BoardQuote> _quotes = new Dictionary<string, BoardQuote>();
        private readonly Dictionary<string, BoardTrend> _trends = new Dictionary<string, BoardTrend>();
        private readonly Dictionary<string, DateTime> _trendAt = new Dictionary<string, DateTime>();
        private List<BoardLink> _catalog = LoadInitialCatalog();
        private DateTime _catalogAt;
        private readonly object _gate = new object();
        public BoardQuote Get(string code) { lock (_gate) { BoardQuote q; return _quotes.TryGetValue(code, out q) ? q : null; } }

        /// <summary>内置快照 + 海外指数伪板块：目录接口不可达时也要能搜到 / 匹配到。</summary>
        private static List<BoardLink> LoadInitialCatalog()
        {
            List<BoardLink> list = BoardCatalogSnapshot.Load();
            list.AddRange(IndexBoards.Catalog());
            return list;
        }

        public async Task<List<BoardLink>> SearchAsync(string keyword)
        {
            var result = new List<BoardLink>(); var seen = new HashSet<string>();
            try
            {
                string body = await Http.GetTextAsync("https://searchapi.eastmoney.com/api/suggest/get?type=14&classify=BK&count=30&token=D43BF722C8E33BDC906FB84D85E326E8&input=" + Http.UrlEncode(keyword), Referer).ConfigureAwait(false);
                object[] items = Json.AsArray(Json.Path(Json.Parse(body), "QuotationCodeTable", "Data"));
                if (items != null) foreach (object item in items)
                {
                    string code = Json.Str(Json.Get(item, "Code"));
                    if (BoardLink.ValidCode(code) && seen.Add(code)) result.Add(new BoardLink { Code = code, Name = Json.Str(Json.Get(item, "Name"), code) });
                }
            }
            catch (Exception ex) { Log.Warn("板块提示搜索失败，尝试完整目录: " + ex.Message); }
            string key = (keyword ?? "").Trim();
            List<BoardLink> cached;
            lock (_gate) cached = new List<BoardLink>(_catalog);
            foreach (BoardLink link in cached)
                if (seen.Add(link.Code) && (link.Code.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0
                    || link.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)) result.Add(link);
            if (result.Count > 0) return result;
            // 搜索提示库和内置快照都漏收时，再尝试更新行业 + 概念完整目录。
            try
            {
                foreach (BoardLink link in await GetCatalogAsync().ConfigureAwait(false))
                    if (seen.Add(link.Code) && (link.Code.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0
                        || link.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)) result.Add(link);
            }
            catch (Exception ex) { Log.Warn("板块目录搜索失败: " + ex.Message); }
            return result;
        }

        public async Task<List<BoardLink>> GetCatalogAsync()
        {
            lock (_gate) if (_catalog != null && MarketClock.NowCst() - _catalogAt < TimeSpan.FromHours(1)) return new List<BoardLink>(_catalog);
            var industry = FetchCatalogPartAsync("push2.eastmoney.com", "2", "f3");
            var concept = FetchCatalogPartAsync("push2.eastmoney.com", "3", "f12");
            await Task.WhenAll(industry, concept).ConfigureAwait(false);
            var result = new List<BoardLink>(); var seen = new HashSet<string>();
            foreach (BoardLink link in industry.Result) if (seen.Add(link.Code)) result.Add(link);
            foreach (BoardLink link in concept.Result) if (seen.Add(link.Code)) result.Add(link);
            // 实时目录可能因区域网络不可达；内置快照始终作为搜索兜底。
            foreach (BoardLink link in _catalog) if (seen.Add(link.Code)) result.Add(link);
            lock (_gate) { _catalog = result; _catalogAt = MarketClock.NowCst(); }
            return new List<BoardLink>(result);
        }

        private async Task<List<BoardLink>> FetchCatalogPartAsync(string host, string type, string sort)
        {
            try
            {
                string url = "https://" + host + "/api/qt/clist/get?pn=1&pz=1000&po=1&np=1&ut=bd1d9ddb04089700cf9c27f6f7426281&fltt=2&invt=2&fid="
                    + sort + "&fs=m%3A90+t%3A" + type + "+f%3A%2150&fields=f12%2Cf14";
                byte[] bytes = null;
                try { bytes = await Http.GetBytesAsync(url, Referer).ConfigureAwait(false); }
                catch { }
                if (bytes == null) bytes = await Http.GetBytesViaProxyAsync(url, Referer).ConfigureAwait(false);
                string body = System.Text.Encoding.UTF8.GetString(bytes);
                if (body.IndexOf('\uFFFD') >= 0) body = Http.Gbk.GetString(bytes);
                return ParseCatalog(body);
            }
            catch (Exception ex) { Log.Warn("板块目录 " + type + " 获取失败: " + ex.Message); return new List<BoardLink>(); }
        }

        public static List<BoardLink> ParseCatalog(string body)
        {
            var result = new List<BoardLink>(); var seen = new HashSet<string>();
            object[] rows = Json.AsArray(Json.Path(Json.Parse(body), "data", "diff"));
            if (rows == null) return result;
            foreach (object row in rows)
            {
                string code = Json.Str(Json.Get(row, "f12"));
                string name = Json.Str(Json.Get(row, "f14"));
                if (BoardLink.ValidCode(code) && name.Length > 0 && seen.Add(code))
                    result.Add(new BoardLink { Code = code, Name = name });
            }
            return result;
        }

        /// <summary>
        /// 基金 → 板块推荐。两路信号叠加，先重仓行业（最贴近主流基金 App 的做法），
        /// 再名称 / 主题词；同一板块只保留一次。
        /// </summary>
        public Task<List<BoardLink>> SuggestForFundAsync(string fundName)
        {
            return SuggestForFundAsync(fundName, null);
        }

        public async Task<List<BoardLink>> SuggestForFundAsync(string fundName, IList<string> holdingIndustries)
        {
            List<BoardLink> result = new List<BoardLink>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                if (holdingIndustries != null && holdingIndustries.Count > 0)
                {
                    List<BoardLink> catalog = await GetCatalogAsync().ConfigureAwait(false);
                    foreach (BoardLink link in FundBoardMatcher.MatchIndustries(holdingIndustries, catalog))
                        if (seen.Add(link.Code)) result.Add(link);
                }
            }
            catch (Exception ex) { Log.Warn("重仓行业匹配失败: " + ex.Message); }

            try
            {
                foreach (BoardLink link in await SuggestByNameAsync(fundName).ConfigureAwait(false))
                    if (seen.Add(link.Code)) result.Add(link);
            }
            catch (Exception ex) { Log.Warn("名称匹配失败: " + ex.Message); }

            return result;
        }

        /// <summary>按重仓股代码取所属行业名（东财 f100），去重后按传入顺序返回。</summary>
        public async Task<List<string>> FetchStockIndustriesAsync(IList<string> exchangeCodes)
        {
            List<string> result = new List<string>();
            if (exchangeCodes == null || exchangeCodes.Count == 0) return result;

            List<string> secids = new List<string>();
            foreach (string code in exchangeCodes)
            {
                if (code != null && code.Length == 8
                    && (code.StartsWith("sh", StringComparison.Ordinal) || code.StartsWith("sz", StringComparison.Ordinal)))
                    secids.Add((code.StartsWith("sh", StringComparison.Ordinal) ? "1." : "0.") + code.Substring(2));
            }
            if (secids.Count == 0) return result;

            string url = "https://push2.eastmoney.com/api/qt/ulist.np/get?secids="
                + string.Join(",", secids.ToArray()) + "&fields=f12,f13,f100&fltt=2";
            string body = null;
            try { body = await Http.GetTextAsync(url, Referer).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("重仓行业（push2）失败: " + ex.Message); }
            if (string.IsNullOrEmpty(body))
            {
                try
                {
                    body = await Http.GetTextAsync(
                        url.Replace("push2.eastmoney.com", "push2delay.eastmoney.com"), Referer).ConfigureAwait(false);
                }
                catch (Exception ex) { Log.Warn("重仓行业获取失败: " + ex.Message); return result; }
            }

            object[] rows = Json.AsArray(Json.Path(Json.Parse(body), "data", "diff"));
            if (rows == null) return result;
            foreach (object row in rows)
            {
                string industry = Json.Str(Json.Get(row, "f100")).Trim();
                if (industry.Length == 0 || industry == "-") continue;
                if (!result.Contains(industry)) result.Add(industry);
            }
            return result;
        }

        private async Task<List<BoardLink>> SuggestByNameAsync(string fundName)
        {
            try
            {
                List<BoardLink> catalog = await GetCatalogAsync().ConfigureAwait(false);
                List<BoardLink> matches = FundBoardMatcher.Match(fundName, catalog);
                if (matches.Count > 0)
                {
                    // 目录中的「医疗器械概念」等近义项可能有同名行业板块；
                    // 用搜索提示补进精确名称，再按明确命中程度排序。
                    foreach (BoardLink match in matches)
                    {
                        try { catalog.AddRange(await SearchAsync(FundBoardMatcher.RootName(match.Name)).ConfigureAwait(false)); }
                        catch (Exception ex) { Log.Warn("关联精确搜索失败: " + ex.Message); }
                    }
                    return FundBoardMatcher.Match(fundName, catalog);
                }
            }
            catch (Exception ex) { Log.Warn("自动板块目录不可用: " + ex.Message); }
            var candidates = new List<BoardLink>(); var seen = new HashSet<string>();
            foreach (string term in FundBoardMatcher.SearchTerms(fundName))
            {
                try
                {
                    foreach (BoardLink link in await SearchAsync(term).ConfigureAwait(false))
                        if (seen.Add(link.Code)) candidates.Add(link);
                    List<BoardLink> matches = FundBoardMatcher.Match(fundName, candidates);
                    if (matches.Count > 0) return matches;
                }
                catch (Exception ex) { Log.Warn("自动板块搜索 " + term + " 失败: " + ex.Message); }
            }
            return new List<BoardLink>();
        }
        public async Task RefreshAsync(IEnumerable<BoardLink> links)
        {
            var pending = new List<BoardLink>(); var indexPending = new List<BoardLink>(); var seen = new HashSet<string>();
            DateTime now = MarketClock.NowCst();
            foreach (BoardLink link in links)
            {
                if (!BoardLink.ValidCode(link.Code) || !seen.Add(link.Code)) continue;
                // 海外指数一个交易日只动一段，没必要 5 秒一问
                bool index = IndexBoards.IsIndexCode(link.Code);
                TimeSpan ttl = TimeSpan.FromSeconds(index ? 20 : 5);
                BoardQuote old = Get(link.Code);
                if (old == null || now - old.ReceivedAt >= ttl)
                    (index ? indexPending : pending).Add(link);
            }
            if (pending.Count == 0 && indexPending.Count == 0) return;

            Task indexTask = RefreshIndexBatchAsync(indexPending);

            var missing = new List<BoardLink>(pending);
            try
            {
                if (pending.Count > 0)
                {
                    string[] secids = new string[pending.Count];
                    for (int i = 0; i < pending.Count; i++) secids[i] = "90." + pending[i].Code;
                    string query = "/api/qt/ulist.np/get?secids=" + string.Join(",", secids) + "&fields=f12,f14,f3,f124&fltt=2";
                    string body = null;
                    // 主域被区域网络挡住时，延时域通常还能连 —— 否则整块板块行会一直「待更新」
                    try { body = await Http.GetTextAsync("https://push2.eastmoney.com" + query, Referer).ConfigureAwait(false); }
                    catch (Exception) { body = null; }
                    List<BoardQuote> quotes;
                    if (body != null) quotes = ParseBatchQuotes(body, MarketClock.NowCst());
                    else quotes = ParseBatchQuotes(await Http.GetTextAsync("https://push2delay.eastmoney.com" + query, Referer).ConfigureAwait(false), MarketClock.NowCst());
                    lock (_gate) foreach (BoardQuote q in quotes) _quotes[q.Code] = q;
                    missing.RemoveAll(delegate(BoardLink link) { foreach (BoardQuote q in quotes) if (q.Code == link.Code) return true; return false; });
                }
            }
            catch (Exception ex) { WarnThrottled("batch", "板块批量行情失败，逐项回退: " + ex.Message); }
            var tasks = new List<Task>();
            foreach (BoardLink link in missing) tasks.Add(RefreshOneAsync(link));
            await Task.WhenAll(tasks).ConfigureAwait(false);
            await indexTask.ConfigureAwait(false);
        }

        /// <summary>
        /// 海外指数一次性批量取（secid 原样传，没有 90. 前缀）。
        /// 拿不到的再逐个回退，避免一个接口抖动就把指数行变成「待更新」。
        /// </summary>
        private async Task RefreshIndexBatchAsync(List<BoardLink> links)
        {
            if (links == null || links.Count == 0) return;
            List<BoardLink> missing = new List<BoardLink>(links);
            try
            {
                List<string> secids = new List<string>();
                for (int i = 0; i < links.Count; i++) secids.Add(IndexBoards.ToSecId(links[i].Code));
                string url = "https://push2.eastmoney.com/api/qt/ulist.np/get?secids=" + string.Join(",", secids.ToArray())
                    + "&fields=f12,f13,f14,f3,f124&fltt=2";
                string body = await Http.GetTextAsync(url, Referer).ConfigureAwait(false);
                List<BoardQuote> quotes = ParseIndexQuotes(body, MarketClock.NowCst());
                lock (_gate) foreach (BoardQuote q in quotes) _quotes[q.Code] = q;
                missing.RemoveAll(delegate(BoardLink link) { foreach (BoardQuote q in quotes) if (q.Code == link.Code) return true; return false; });
            }
            catch (Exception ex) { WarnThrottled("index", "海外指数行情失败，逐项回退: " + ex.Message); }
            for (int i = 0; i < missing.Count; i++) await RefreshOneAsync(missing[i]).ConfigureAwait(false);
        }

        /// <summary>
        /// 海外指数批量响应：f12=代码符号（NDX）、f13=市场（100）、f3=涨跌幅、f124=时间戳。
        /// 拼回 secid 后映射成 ix100.NDX 这样的伪板块代码。
        /// </summary>
        public static List<BoardQuote> ParseIndexQuotes(string body, DateTime received)
        {
            object[] rows = Json.AsArray(Json.Path(Json.Parse(body), "data", "diff"));
            if (rows == null) throw new InvalidOperationException("海外指数行情为空");
            var quotes = new List<BoardQuote>();
            foreach (object row in rows)
            {
                string symbol = Json.Str(Json.Get(row, "f12")).Trim();
                long market = Json.Long(Json.Get(row, "f13"), 0);
                double pct = Json.Num(Json.Get(row, "f3"), double.NaN);
                long stamp = Json.Long(Json.Get(row, "f124"));
                if (symbol.Length == 0 || market <= 0 || double.IsNaN(pct) || stamp <= 0) continue;
                string code = IndexBoards.CodeFromSecId(market.ToString() + "." + symbol);
                if (!IndexBoards.IsIndexCode(code)) continue;
                quotes.Add(new BoardQuote
                {
                    Code = code,
                    Name = Json.Str(Json.Get(row, "f14"), IndexBoards.NameOf(code)),
                    ChangePct = pct,
                    DataTime = new DateTime(1970, 1, 1).AddSeconds(stamp).AddHours(8),
                    ReceivedAt = received,
                    Overseas = true
                });
            }
            return quotes;
        }
        public static List<BoardQuote> ParseBatchQuotes(string body, DateTime received)
        {
            object[] rows = Json.AsArray(Json.Path(Json.Parse(body), "data", "diff"));
            if (rows == null) throw new InvalidOperationException("板块批量行情为空");
            var quotes = new List<BoardQuote>();
            foreach (object row in rows)
            {
                string code = Json.Str(Json.Get(row, "f12"));
                double pct = Json.Num(Json.Get(row, "f3"), double.NaN);
                long stamp = Json.Long(Json.Get(row, "f124"));
                if (!BoardLink.ValidCode(code) || double.IsNaN(pct) || stamp <= 0) continue;
                quotes.Add(new BoardQuote { Code = code, Name = Json.Str(Json.Get(row, "f14")), ChangePct = pct,
                    DataTime = new DateTime(1970,1,1).AddSeconds(stamp).AddHours(8), ReceivedAt = received });
            }
            return quotes;
        }
        private async Task RefreshOneAsync(BoardLink link)
        {
            bool index = IndexBoards.IsIndexCode(link.Code);
            BoardQuote old = Get(link.Code);
            if (old != null && MarketClock.NowCst() - old.ReceivedAt < TimeSpan.FromSeconds(index ? 20 : 5)) return;

            string secId = index ? IndexBoards.ToSecId(link.Code) : "90." + link.Code;
            string query = "api/qt/stock/get?secid=" + secId + "&fields=f58,f170,f86&fltt=2";
            // A 股板块先走延时域（限频更宽松），海外指数先走主域（延时域对 100.x 不稳）
            string[] hosts = index
                ? new string[] { "push2.eastmoney.com", "push2delay.eastmoney.com" }
                : new string[] { "push2delay.eastmoney.com", "push2.eastmoney.com" };

            Exception last = null;
            for (int i = 0; i < hosts.Length; i++)
            {
                try
                {
                    string body = await Http.GetTextAsync("https://" + hosts[i] + "/" + query, Referer).ConfigureAwait(false);
                    BoardQuote q = ParseQuote(body, link.Code, MarketClock.NowCst());
                    q.Overseas = index;
                    if (string.IsNullOrEmpty(q.Name)) q.Name = link.Name ?? IndexBoards.NameOf(link.Code);
                    lock (_gate) _quotes[link.Code] = q;
                    return;
                }
                catch (Exception ex) { last = ex; }
            }
            // 板块接口整段挂掉时每秒刷屏会把日志撑到几 MB —— 同一板块 1 分钟只记一次
            WarnThrottled("board:" + link.Code, "板块 " + link.Code + " 更新失败: "
                + (last == null ? "未知错误" : last.Message));
        }

        /// <summary>同一 key 在 1 分钟内只写一条日志（板块接口抖动时避免日志刷屏）。</summary>
        private static void WarnThrottled(string key, string message)
        {
            Log.WarnOnce(key, message, TimeSpan.FromMinutes(1));
        }
        public static BoardQuote ParseQuote(string body, string code, DateTime received)
        {
            object data = Json.Get(Json.Parse(body), "data");
            double pct = Json.Num(Json.Get(data, "f170"), double.NaN);
            long stamp = Json.Long(Json.Get(data, "f86"));
            if (double.IsNaN(pct) || double.IsInfinity(pct) || stamp <= 0) throw new InvalidOperationException("板块行情缺少涨跌幅或日期");
            return new BoardQuote { Code = code, Name = Json.Str(Json.Get(data, "f58")), ChangePct = pct,
                DataTime = new DateTime(1970,1,1).AddSeconds(stamp).AddHours(8), ReceivedAt = received };
        }
        public async Task<BoardTrend> FetchTrendAsync(string code)
        {
            if (!BoardLink.ValidCode(code)) throw new ArgumentException("无效板块代码");
            BoardTrend cached = null;
            lock (_gate)
            {
                DateTime at;
                _trends.TryGetValue(code, out cached);
                if (cached != null && _trendAt.TryGetValue(code, out at)
                    && MarketClock.NowCst() - at < TimeSpan.FromSeconds(25)) return cached;
            }
            string url = "https://push2his.eastmoney.com/api/qt/stock/trends2/get?secid="
                + (IndexBoards.IsIndexCode(code) ? IndexBoards.ToSecId(code) : "90." + code)
                + "&fields1=f1,f2,f3,f4,f5,f6,f7,f8,f9,f10,f11&fields2=f51,f52,f53,f54,f55,f56,f57,f58&ndays=1&iscr=0";
            try
            {
                BoardTrend result = ParseTrend(await Http.GetTextAsync(url, Referer).ConfigureAwait(false));
                lock (_gate) { _trends[code] = result; _trendAt[code] = MarketClock.NowCst(); }
                return result;
            }
            catch
            {
                if (cached != null && cached.Points.Count > 0)
                {
                    // 海外指数最近一个交易日可能就在昨天/上周五，不能按「必须是今天」判废
                    bool fresh = IndexBoards.IsIndexCode(code)
                        ? (MarketClock.NowCst() - cached.Date).TotalDays <= 5
                        : cached.Date == MarketClock.NowCst().Date;
                    if (fresh) return cached;
                }
                throw;
            }
        }
        public static BoardTrend ParseTrend(string body)
        {
            object data = Json.Get(Json.Parse(body), "data");
            double previous = Json.Num(Json.Get(data, "preClose"));
            object[] rows = Json.AsArray(Json.Get(data, "trends"));
            if (previous <= 0 || rows == null) throw new InvalidOperationException("板块分时数据不可用");

            // 先落地成 (时间, 涨跌幅) 再决定「哪一段算当天」——
            // 海外指数的交易时段跨零点（北京时间 21:30 → 次日 04:00），
            // 按日期切成两半的话图上就只剩凌晨那几根。
            var points = new List<MinutePoint>();
            var stamps = new List<DateTime>();
            foreach (object row in rows)
            {
                string[] parts = Json.Str(row).Split(','); DateTime time; double close;
                if (parts.Length < 3 || !DateTime.TryParseExact(parts[0], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time)
                    || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out close) || close <= 0) continue;
                points.Add(new MinutePoint { Time = time.ToString("HH:mm"), Price = (close / previous - 1) * 100, Open = 0 });
                stamps.Add(time);
            }
            var trend = new BoardTrend { Name = Json.Str(Json.Get(data, "name")) };
            if (points.Count == 0)
            {
                long stamp = Json.Long(Json.Get(data, "time"));
                if (stamp > 0) trend.Date = new DateTime(1970, 1, 1).AddSeconds(stamp).AddHours(8).Date;
                return trend;
            }

            DateTime lastStamp = stamps[stamps.Count - 1];
            DateTime lastDay = lastStamp.Date;
            // 最后一根落在凌晨 = 跨零点时段（海外指数 21:30 → 次日 04:00），
            // 整段归到「前一天」这个交易日，并且把前一天的行一起算进来。
            bool overnight = lastStamp.TimeOfDay < TimeSpan.FromHours(6)
                && stamps.Exists(delegate(DateTime s) { return s.Date == lastDay.AddDays(-1); });
            DateTime from = overnight ? lastDay.AddDays(-1) : lastDay;
            trend.Date = from;
            for (int i = 0; i < points.Count; i++)
            {
                DateTime day = stamps[i].Date;
                if (day == lastDay || (overnight && day == from)) trend.Points.Add(points[i]);
            }
            return trend;
        }
    }
}
