using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using QuantNative.Core;

namespace QuantNative.Data
{
    /// <summary>
    /// 天天基金（东方财富）场外基金数据源。
    ///
    /// 为什么需要它：新浪 suggest / 腾讯 smartbox 只覆盖**交易所挂牌**的标的，
    /// 场外开放式基金（如「东方人工智能主题混合A 005844」）在那两家里根本搜不到，
    /// 也没有行情接口。场外基金必须走这条通道。
    ///
    /// 公开接口（2026-09 实测可用）：
    ///   · 搜索    fundsuggest.eastmoney.com/FundSearch/api/FundSearchAPI.ashx
    ///             返回 JSON，且 FundBaseInfo 里直接带最新单位净值 DWJZ 与净值日期 FSRQ。
    ///   · 最新净值 api.fund.eastmoney.com/f10/lsjz（小响应，含官方日涨跌）
    ///   · 净值历史 fund.eastmoney.com/pingzhongdata/{code}.js
    ///             含 Data_netWorthTrend = [{x:时间戳, y:单位净值, equityReturn:日涨跌幅%}]，
    ///             一次拿全历史，用于折线/日周月 K 线（约 400KB，按 12 小时缓存）。
    ///   · 前十大重仓 fundf10.eastmoney.com/FundArchivesDatas.aspx?type=jjcc
    ///             含股票代码 / 名称 / 占净值比例，按 24 小时缓存。
    ///
    /// 基金行情只返回正式单位净值；盘中估值由 <see cref="HoldingsEstimator"/> 用前十大重仓股的
    /// 实时涨跌加权算出来（养基宝/天天基金的同一套口径），关联板块等权只是兜底。
    /// 数据源本身不把板块涨跌幅伪装成基金净值。
    /// </summary>
    public sealed class EastmoneyFundSource : IQuoteSource
    {
        public const string SourceName = "eastmoney-fund";

        private const string SuggestUrl = "https://fundsuggest.eastmoney.com/FundSearch/api/FundSearchAPI.ashx?m=1&key=";
        private const string SuggestReferer = "http://fund.eastmoney.com/";
        private const string PingzhongUrl = "https://fund.eastmoney.com/pingzhongdata/";
        private const string PingzhongReferer = "http://fund.eastmoney.com/";
        private const string LatestNavUrl = "https://api.fund.eastmoney.com/f10/lsjz?fundCode=";
        private const string HoldingsUrl = "https://fundf10.eastmoney.com/FundArchivesDatas.aspx?type=jjcc&topline=10&year=&month=&code=";
        private const string HoldingsReferer = "http://fundf10.eastmoney.com/";

        private static readonly TimeSpan MetaTtl = TimeSpan.FromHours(12);
        private static readonly TimeSpan HoldingsTtl = TimeSpan.FromHours(24);

        private const int MaxSearch = 20;
        private const int MaxHistory = 400;          // 日 K 最长 400 根

        public string Name { get { return SourceName; } }
        public string DisplayName { get { return "天天基金"; } }

        // ── 缓存 ──

        private sealed class Holding
        {
            public string Exchange;   // sh / sz
            public string Code;       // 6 位股票代码
            public string Name;
            public double Weight;     // 占净值比例 %
        }

        private sealed class NavPoint
        {
            public DateTime Date;
            public double Nav;
            public double Pct;        // 该日涨跌幅 %
        }

        private sealed class FundMeta
        {
            public string Code;       // 6 位基金代码
            public string Name;
            public double Nav;        // 最新单位净值
            public double PrevNav;    // 上一交易日单位净值
            public double NavPct;     // 最新净值对应的日涨跌幅 %
            public string NavDate;    // 净值日期 yyyy-MM-dd

            public DateTime MetaAt = DateTime.MinValue;
            public DateTime HistoryAt = DateTime.MinValue;
            public DateTime HistoryAttemptAt = DateTime.MinValue;
            public DateTime HoldingsAt = DateTime.MinValue;

            public List<NavPoint> History;
            public List<Holding> Holdings;
            public bool HistoryLoading;
        }

        private readonly Dictionary<string, FundMeta> _cache =
            new Dictionary<string, FundMeta>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        private FundMeta GetMeta(string six)
        {
            lock (_gate)
            {
                FundMeta meta;
                if (!_cache.TryGetValue(six, out meta))
                {
                    meta = new FundMeta();
                    meta.Code = six;
                    meta.Name = six;
                    _cache[six] = meta;
                }
                return meta;
            }
        }

        // ── 搜索 ──

        public async Task<List<StockBrief>> SearchAsync(string keyword, string market)
        {
            List<StockBrief> results = new List<StockBrief>();
            string key = (keyword ?? "").Trim();
            if (key.Length == 0) return results;

            string body = await Http.GetTextAsync(SuggestUrl + Http.UrlEncode(key), SuggestReferer)
                .ConfigureAwait(false);
            List<StockBrief> hits = ParseSuggest(body, MaxSearch);

            // 顺手把搜索结果里带的净值信息塞进缓存：这样刚加入自选就能立刻显示，
            // 不必等第一次行情轮询（也省掉一次 400KB 的历史请求）。
            for (int i = 0; i < hits.Count; i++)
            {
                string six = Symbols.OpenFundCode(hits[i].Code);
                double[] seed;
                if (_suggestSeed.TryGetValue(six, out seed))
                {
                    string date = _suggestDate.ContainsKey(six) ? _suggestDate[six] : null;
                    SeedMeta(six, hits[i].Name, date, seed);
                }
            }
            return hits;
        }

        private readonly Dictionary<string, double[]> _suggestSeed = new Dictionary<string, double[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _suggestDate = new Dictionary<string, string>(StringComparer.Ordinal);

        private void SeedMeta(string six, string name, string navDate, double[] seed)
        {
            FundMeta meta = GetMeta(six);
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(name)) meta.Name = name;
                if (seed != null && seed.Length >= 1 && seed[0] > 0)
                {
                    // 只在「缓存里还没有更新的净值」时才覆盖
                    if (string.CompareOrdinal(navDate ?? "", meta.NavDate ?? "") >= 0)
                    {
                        meta.Nav = seed[0];
                        meta.NavDate = navDate;
                        if (seed.Length >= 2) meta.NavPct = seed[1];
                    }
                    meta.MetaAt = MarketClock.NowCst();
                }
            }
        }

        /// <summary>
        /// 基金搜索响应：
        /// {"ErrCode":0,"Datas":[{"CODE":"005844","NAME":"东方人工智能主题混合A","JP":"DFRGZNZTHHA",
        ///   "CATEGORYDESC":"基金","FundBaseInfo":{"SHORTNAME":"...","FTYPE":"混合型-偏股",
        ///   "FSRQ":"2026-09-17","DWJZ":3.341,...}}]}
        /// 只保留带 FundBaseInfo 的条目 —— 不带的是期权等非基金结果。
        /// </summary>
        private List<StockBrief> ParseSuggest(string body, int limit)
        {
            List<StockBrief> results = new List<StockBrief>();
            if (string.IsNullOrEmpty(body)) return results;

            object root;
            try { root = Json.Parse(body); }
            catch (Exception ex)
            {
                Log.Warn("基金搜索响应解析失败: " + ex.Message);
                return results;
            }

            object[] datas = Json.AsArray(Json.Get(root, "Datas"));
            if (datas == null) return results;

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < datas.Length && results.Count < limit; i++)
            {
                object info = Json.Get(datas[i], "FundBaseInfo");
                if (info == null) continue;

                string code = Json.Str(Json.Get(info, "FCODE"));
                if (code.Length == 0) code = Json.Str(Json.Get(datas[i], "CODE"));
                if (!IsSixDigits(code)) continue;

                string name = Json.Str(Json.Get(info, "SHORTNAME"));
                if (name.Length == 0) name = Json.Str(Json.Get(datas[i], "NAME"));
                if (name.Length == 0) continue;

                if (!seen.Add(code)) continue;

                string typeDesc = Json.Str(Json.Get(info, "FTYPE"));
                string navDate = Json.Str(Json.Get(info, "FSRQ"));
                double nav = Json.Num(Json.Get(info, "DWJZ"), 0);
                double pct = Json.Num(Json.Get(info, "RZDF"), 0);

                lock (_gate)
                {
                    _suggestSeed[code] = new double[] { nav, pct };
                    _suggestDate[code] = navDate;
                }

                StockBrief brief = new StockBrief();
                brief.Code = Symbols.OpenFundPrefix + code;
                brief.Market = "OF";
                brief.Name = name;
                brief.Category = "OF";
                brief.Source = SourceName;
                brief.SourceName = DisplayName;
                brief.TypeDesc = typeDesc;
                results.Add(brief);
            }
            return results;
        }

        // ── 实时行情 ──

        public async Task<List<Quote>> FetchRealtimeAsync(IList<string> codes, string market)
        {
            var result = new List<Quote>();
            if (codes == null) return result;
            var tasks = new List<Task<Quote>>();
            foreach (string code in codes)
            {
                string six = Symbols.OpenFundCode(code);
                if (!IsSixDigits(six)) continue;
                tasks.Add(FetchOneNavAsync(six));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (Task<Quote> task in tasks) if (task.Result != null) result.Add(task.Result);
            return result;
        }

        private async Task<Quote> FetchOneNavAsync(string six)
        {
            FundMeta meta = GetMeta(six);
            DateTime now = MarketClock.NowCst();
            bool published;
            DateTime at;
            lock (_gate) { published = meta.NavDate == now.ToString("yyyy-MM-dd"); at = meta.MetaAt; }
            TimeSpan ttl = published ? TimeSpan.FromHours(12)
                : now.TimeOfDay >= TimeSpan.FromHours(15) && now.TimeOfDay < TimeSpan.FromHours(23)
                    ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(2);
            if (now - at >= ttl)
            {
                bool failed = false;
                try
                {
                    string url = LatestNavUrl + six + "&pageIndex=1&pageSize=2&startDate=&endDate=";
                    string body = await Http.GetTextAsync(url, "https://fundf10.eastmoney.com/jjjz_" + six + ".html").ConfigureAwait(false);
                    UpdateLatestNav(meta, body, now);
                }
                catch (Exception ex)
                {
                    Log.Warn("基金最新净值获取失败 " + six + ": " + ex.Message);
                    failed = true;
                }
                if (failed && NeedsHistory(meta)) await EnsureHistoryAsync(meta).ConfigureAwait(false);
            }
            lock (_gate) return BuildQuote(meta, new Dictionary<string, Quote>(), now.ToString("yyyy-MM-dd"), true);
        }

        private void UpdateLatestNav(FundMeta meta, string body, DateTime received)
        {
            object[] rows = Json.AsArray(Json.Path(Json.Parse(body), "Data", "LSJZList"));
            if (rows == null || rows.Length == 0) throw new InvalidOperationException("基金最新净值列表为空");
            string date = Json.Str(Json.Get(rows[0], "FSRQ"));
            double nav = Json.Num(Json.Get(rows[0], "DWJZ"), 0);
            double pct = Json.Num(Json.Get(rows[0], "JZZZL"), double.NaN);
            if (date.Length != 10 || nav <= 0 || double.IsNaN(pct)) throw new InvalidOperationException("基金最新净值字段不完整");
            double previous = rows.Length > 1 ? Json.Num(Json.Get(rows[1], "DWJZ"), 0) : 0;
            lock (_gate)
            {
                if (string.CompareOrdinal(date, meta.NavDate ?? "") >= 0)
                {
                    meta.NavDate = date; meta.Nav = nav; meta.NavPct = pct; meta.PrevNav = previous;
                }
                meta.MetaAt = received;
            }
        }

        private static bool ContainsMeta(List<FundMeta> list, FundMeta meta)
        {
            for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], meta)) return true;
            return false;
        }

        private static bool NeedsHistory(FundMeta meta)
        {
            DateTime now = MarketClock.NowCst();
            if (now - meta.HistoryAttemptAt < TimeSpan.FromMinutes(1)) return false;
            TimeSpan ttl = meta.NavDate == now.ToString("yyyy-MM-dd")
                ? MetaTtl : TimeSpan.FromMinutes(5);
            return meta.History == null || now - meta.HistoryAt >= ttl;
        }

        private Quote BuildQuote(FundMeta meta, Dictionary<string, Quote> stockQuotes, string today, bool weekend)
        {
            if (meta.Nav <= 0) return null;

            Quote quote = new Quote();
            quote.Code = Symbols.OpenFundPrefix + meta.Code;
            quote.Market = "OF";
            quote.Name = meta.Name;
            quote.Timestamp = UnixNow();

            // 只发布已公布的单位净值；板块涨跌另存于 BoardClient。
            quote.Change = meta.PrevNav > 0 ? Round4(meta.Nav - meta.PrevNav) : 0;
            quote.Price = meta.Nav;
            quote.ChangePct = meta.NavPct;
            quote.Open = meta.PrevNav > 0 ? meta.PrevNav : meta.Nav;
            quote.High = meta.Nav;
            quote.Low = meta.Nav;
            quote.PriceKind = "净值";
            quote.PriceTime = meta.NavDate;

            return quote;
        }

        private static double Round4(double value)
        {
            return Math.Round(value, 4, MidpointRounding.AwayFromZero);
        }

        private static long UnixNow()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        // ── 净值历史（pingzhongdata） ──

        private async Task EnsureHistoryAsync(FundMeta meta)
        {
            lock (_gate)
            {
                if (meta.HistoryLoading) return;
                if (!NeedsHistory(meta)) return;
                meta.HistoryLoading = true;
                meta.HistoryAttemptAt = MarketClock.NowCst();
            }
            try
            {
                string url = PingzhongUrl + meta.Code + ".js";
                string text = await Http.GetTextAsync(url, PingzhongReferer).ConfigureAwait(false);
                List<NavPoint> points = ParseNetWorthTrend(text);
                if (points.Count == 0) throw new InvalidOperationException("净值历史为空，保留已有数据");
                string name = ExtractVar(text, "fS_name");

                lock (_gate)
                {
                    meta.History = points;
                    meta.HistoryAt = MarketClock.NowCst();
                    if (points.Count > 0)
                    {
                        NavPoint last = points[points.Count - 1];
                        string date = last.Date.ToString("yyyy-MM-dd");
                        if (string.CompareOrdinal(date, meta.NavDate ?? "") >= 0)
                        {
                            meta.Nav = last.Nav;
                            meta.NavDate = date;
                            meta.NavPct = last.Pct;
                            meta.PrevNav = points.Count >= 2 ? points[points.Count - 2].Nav : 0;
                        }
                    }
                    if (!string.IsNullOrEmpty(name)) meta.Name = name;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("基金净值历史获取失败 " + meta.Code + ": " + ex.Message);
                // HistoryAttemptAt limits retry to once per minute without extending stale data TTL.
            }
            finally
            {
                lock (_gate) { meta.HistoryLoading = false; }
            }
        }

        /// <summary>
        /// 从 pingzhongdata 里取 Data_netWorthTrend 数组：
        ///   var Data_netWorthTrend = [{"x":1430841600000,"y":1.0,"equityReturn":0,"unitMoney":""}, ...];
        /// 数组元素是扁平对象，直接按 '[' 到 ']' 截取即可，不需要完整 JS 解析。
        /// </summary>
        private static List<NavPoint> ParseNetWorthTrend(string text)
        {
            List<NavPoint> points = new List<NavPoint>();
            if (string.IsNullOrEmpty(text)) return points;

            int nameAt = text.IndexOf("Data_netWorthTrend", StringComparison.Ordinal);
            if (nameAt < 0) return points;
            int start = text.IndexOf('[', nameAt);
            if (start < 0) return points;
            int end = text.IndexOf(']', start);
            if (end <= start) return points;

            string json = text.Substring(start, end - start + 1);
            object[] items;
            try { items = Json.AsArray(Json.Parse(json)); }
            catch (Exception ex)
            {
                Log.Warn("基金净值历史解析失败: " + ex.Message);
                return points;
            }
            if (items == null) return points;

            for (int i = 0; i < items.Length; i++)
            {
                object node = items[i];
                double nav = Json.Num(Json.Get(node, "y"), double.NaN);
                if (double.IsNaN(nav) || nav <= 0) continue;
                long ms = Json.Long(Json.Get(node, "x"), 0);
                if (ms <= 0) continue;

                NavPoint point = new NavPoint();
                try { point.Date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).AddHours(8); }
                catch { continue; }
                point.Nav = nav;
                point.Pct = Json.Num(Json.Get(node, "equityReturn"), 0);
                points.Add(point);
            }
            points.Sort(delegate (NavPoint a, NavPoint b) { return a.Date.CompareTo(b.Date); });
            return points;
        }

        private static string ExtractVar(string text, string varName)
        {
            if (string.IsNullOrEmpty(text)) return "";
            Match match = Regex.Match(text, Regex.Escape(varName) + "\\s*=\\s*\"([^\"]*)\"");
            return match.Success ? match.Groups[1].Value : "";
        }

        // ── 前十大重仓（fundf10 HTML） ──

        private async Task EnsureHoldingsAsync(FundMeta meta, bool force)
        {
            lock (_gate)
            {
                if (!force && meta.Holdings != null && DateTime.Now - meta.HoldingsAt <= HoldingsTtl) return;
            }
            try
            {
                string url = HoldingsUrl + meta.Code + "&rt=" + UnixNow();
                string html = await Http.GetTextAsync(url, HoldingsReferer).ConfigureAwait(false);
                List<Holding> holdings = ParseHoldings(html);
                lock (_gate)
                {
                    meta.Holdings = holdings;
                    meta.HoldingsAt = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("基金重仓股获取失败 " + meta.Code + ": " + ex.Message);
                lock (_gate)
                {
                    if (meta.Holdings == null) meta.Holdings = new List<Holding>();
                    meta.HoldingsAt = DateTime.Now;
                }
            }
        }

        /// <summary>
        /// 重仓股表格每行形如（已去掉多余属性）：
        ///   &lt;tr&gt;&lt;td&gt;1&lt;/td&gt;&lt;td&gt;&lt;a href='//quote.eastmoney.com/unify/r/0.300750'&gt;300750&lt;/a&gt;&lt;/td&gt;
        ///   &lt;td&gt;&lt;a&gt;宁德时代&lt;/a&gt;&lt;/td&gt;...&lt;td class='tor'&gt;5.13%&lt;/td&gt;...
        /// 提取：交易所（0=深 1=沪）、6 位代码、名称、占净值比例。
        /// </summary>
        private static List<Holding> ParseHoldings(string html)
        {
            List<Holding> list = new List<Holding>();
            if (string.IsNullOrEmpty(html)) return list;

            // The response includes multiple reporting periods. Never add older
            // quarterly weights to the latest period's holdings.
            Match table = Regex.Match(html, @"<table\b[^>]*>(.*?)</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (table.Success) html = table.Groups[1].Value;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            MatchCollection rows = Regex.Matches(html, "<tr>(.*?)</tr>", RegexOptions.Singleline);
            for (int i = 0; i < rows.Count && list.Count < 10; i++)
            {
                string row = rows[i].Groups[1].Value;

                Match codeMatch = Regex.Match(row, @"unify/r/(\d)\.(\d{6})");
                if (!codeMatch.Success) continue;
                string exchange = codeMatch.Groups[1].Value == "1" ? "sh" : "sz";
                string code = codeMatch.Groups[2].Value;
                if (!seen.Add(exchange + code)) continue;

                string name = "";
                Match nameMatch = Regex.Match(row, @"unify/r/\d\.\d{6}'[^>]*>([^<]+)</a>");
                if (nameMatch.Success) name = nameMatch.Groups[1].Value.Trim();

                double weight = 0;
                Match weightMatch = Regex.Match(row, @">(\d+(?:\.\d+)?)%<");
                if (weightMatch.Success)
                    double.TryParse(weightMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out weight);

                Holding holding = new Holding();
                holding.Exchange = exchange;
                holding.Code = code;
                holding.Name = name;
                holding.Weight = weight;
                list.Add(holding);
            }
            return list;
        }

        // ── 其它接口 ──

        /// <summary>
        /// 前十大重仓股（带交易所前缀，按权重降序）。取不到时返回空列表 ——
        /// 调用方（自动关联板块）据此退回到「按名称匹配」这一路。
        /// </summary>
        public async Task<List<FundHolding>> FetchTopHoldingsAsync(string fundCode)
        {
            return await FetchTopHoldingsAsync(fundCode, false).ConfigureAwait(false);
        }

        /// <summary>
        /// 同 <see cref="FetchTopHoldingsAsync(string)"/>；<paramref name="force"/>=true 时无视 24 小时缓存
        /// 重新拉一次（重仓股估算按 6 小时 + 失败 5 分钟重试的节奏自己管缓存）。
        /// </summary>
        public async Task<List<FundHolding>> FetchTopHoldingsAsync(string fundCode, bool force)
        {
            List<FundHolding> result = new List<FundHolding>();
            string six = Symbols.OpenFundCode(fundCode ?? "");
            if (!IsSixDigits(six)) return result;

            try
            {
                FundMeta meta = GetMeta(six);
                bool cached;
                lock (_gate) cached = meta.Holdings != null && meta.Holdings.Count > 0;
                if (!cached || force) await EnsureHoldingsAsync(meta, force).ConfigureAwait(false);

                List<Holding> holdings;
                lock (_gate) holdings = meta.Holdings == null ? new List<Holding>() : new List<Holding>(meta.Holdings);
                for (int i = 0; i < holdings.Count; i++)
                {
                    Holding holding = holdings[i];
                    if (string.IsNullOrEmpty(holding.Code)) continue;
                    result.Add(new FundHolding
                    {
                        Code = holding.Exchange + holding.Code,
                        Name = holding.Name,
                        Weight = holding.Weight
                    });
                }
                result.Sort(delegate(FundHolding a, FundHolding b) { return b.Weight.CompareTo(a.Weight); });
            }
            catch (Exception ex)
            {
                Log.Warn("重仓股读取失败 " + six + ": " + ex.Message);
            }
            return result;
        }

        public Task<List<IndexQuote>> FetchIndicesAsync()
        {
            return Task.FromResult(new List<IndexQuote>());
        }

        public Task<Depth> FetchDepthAsync(string code, string market)
        {
            // 场外基金没有五档盘口
            return Task.FromResult(new Depth { Code = code });
        }

        public Task<List<MinutePoint>> FetchMinuteAsync(string code, string market)
        {
            // 场外基金没有分时
            return Task.FromResult(new List<MinutePoint>());
        }

        public async Task<List<KLineBar>> FetchKLineAsync(string code, string market, ChartPeriod period, int count)
        {
            List<KLineBar> bars = new List<KLineBar>();
            string six = Symbols.OpenFundCode(code);
            if (!IsSixDigits(six)) return bars;

            if (period == ChartPeriod.Minute || period == ChartPeriod.Min1 || period == ChartPeriod.Min5
                || period == ChartPeriod.Min15 || period == ChartPeriod.Min30 || period == ChartPeriod.Min60)
            {
                // 场外基金没有分钟级数据（净值一天一个）
                return bars;
            }

            FundMeta meta = GetMeta(six);
            await EnsureHistoryAsync(meta).ConfigureAwait(false);

            List<NavPoint> history;
            lock (_gate) { history = meta.History; }
            if (history == null || history.Count == 0) return bars;

            // 单位净值当成收盘价，开高低同值 —— 折线图足够表达，K 线会退化成横线（符合事实）
            List<KLineBar> daily = new List<KLineBar>();
            for (int i = 0; i < history.Count; i++)
            {
                NavPoint point = history[i];
                KLineBar bar = new KLineBar();
                bar.Date = point.Date.ToString("yyyy-MM-dd");
                bar.Close = point.Nav;
                bar.Open = point.Nav;
                bar.High = point.Nav;
                bar.Low = point.Nav;
                bar.Volume = 0;
                bar.Turnover = 0;
                daily.Add(bar);
            }

            if (period == ChartPeriod.Daily)
            {
                bars = daily;
            }
            else
            {
                // 周/月：取区间内最后一个净值作为收盘（净值不累计，用末值最直观）
                bars = Aggregate(daily, period == ChartPeriod.Weekly);
            }

            int limit = count > 0 ? count : MaxHistory;
            if (bars.Count > limit) bars.RemoveRange(0, bars.Count - limit);
            return bars;
        }

        private static List<KLineBar> Aggregate(List<KLineBar> daily, bool weekly)
        {
            List<KLineBar> result = new List<KLineBar>();
            string lastKey = null;
            KLineBar current = null;
            for (int i = 0; i < daily.Count; i++)
            {
                KLineBar bar = daily[i];
                DateTime date;
                string key;
                if (!DateTime.TryParse(bar.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                    key = bar.Date;
                else if (weekly)
                    key = date.AddDays(-(int)date.DayOfWeek).ToString("yyyy-MM-dd");
                else
                    key = date.ToString("yyyy-MM");

                if (current == null || key != lastKey)
                {
                    current = new KLineBar();
                    current.Date = bar.Date;
                    current.Open = bar.Open;
                    current.High = bar.High;
                    current.Low = bar.Low;
                    current.Close = bar.Close;
                    current.Volume = bar.Volume;
                    current.Turnover = bar.Turnover;
                    result.Add(current);
                    lastKey = key;
                }
                else
                {
                    current.Date = bar.Date;           // 用区间最后一根日期做标签
                    current.Close = bar.Close;
                    if (bar.High > current.High) current.High = bar.High;
                    if (bar.Low < current.Low) current.Low = bar.Low;
                    current.Volume += bar.Volume;
                    current.Turnover += bar.Turnover;
                }
            }
            return result;
        }

        public async Task<bool> HealthCheckAsync()
        {
            try
            {
                string body = await Http.GetTextAsync(SuggestUrl + "000001", SuggestReferer).ConfigureAwait(false);
                return body != null && body.IndexOf("Datas", StringComparison.Ordinal) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSixDigits(string text)
        {
            if (text == null || text.Length != 6) return false;
            for (int i = 0; i < text.Length; i++)
                if (text[i] < '0' || text[i] > '9') return false;
            return true;
        }
    }
}
