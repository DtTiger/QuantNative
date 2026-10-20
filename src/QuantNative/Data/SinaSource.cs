using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using QuantNative.Core;

namespace QuantNative.Data
{
    /// <summary>
    /// 新浪财经适配器（备用数据源）。对齐 Rust 端 datasource/sina.rs：
    ///   * 实时行情 hq.sinajs.cn/list= —— 形如
    ///     var hq_str_sz000852="石化机械,开,昨收,现价,高,低,买一,卖一,量(股),额(元),..."
    ///     注意：新浪返回的量/额**已是股与元**，不要再归一化（×100 / ×10000 会放大 100 倍）
    ///   * 指数走「个股格式」（不带 s_ 前缀），比紧凑指数格式数据更可靠；
    ///     上证系数量恒为正确值的 1/100，这里 ×100 校正
    ///   * K 线 money.finance.sina.com.cn getKLineData（scale: 240=日 1200=周 7200=月，
    ///     分钟为 5/15/30/60）；不支持 1 分钟
    ///   * 盘口：新浪的买卖盘接口已失效，回退到腾讯
    /// </summary>
    public sealed class SinaSource : IQuoteSource
    {
        private const string QuoteUrl = "http://hq.sinajs.cn/list=";
        private const string Referer = "https://finance.sina.com.cn";
        private const string KLineUrl = "http://money.finance.sina.com.cn/quotes_service/api/json_v2.php/CN_MarketData.getKLineData";

        public string Name { get { return "sina"; } }
        public string DisplayName { get { return "新浪财经"; } }

        // ── 实时行情 ──

        public async Task<List<Quote>> FetchRealtimeAsync(IList<string> codes, string market)
        {
            string[] mapped = new string[codes.Count];
            for (int i = 0; i < codes.Count; i++) mapped[i] = Symbols.ToExchangeCode(codes[i], market);

            string body = await Http.GetGbkAsync(QuoteUrl + string.Join(",", mapped), Referer).ConfigureAwait(false);
            List<Quote> result = new List<Quote>();
            foreach (string line in SplitLines(body))
            {
                Quote quote = ParseQuoteLine(line);
                if (quote != null) result.Add(quote);
            }
            return result;
        }

        /// <summary>解析一行新浪个股行情。</summary>
        public static Quote ParseQuoteLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            int eq = line.IndexOf('=');
            if (eq < 0) return null;
            string varPart = line.Substring(0, eq).Trim();
            const string prefix = "var hq_str_";
            if (!varPart.StartsWith(prefix, StringComparison.Ordinal)) return null;
            string code = varPart.Substring(prefix.Length);

            string data = ExtractQuoted(line, eq);
            if (data == null) return null;
            string[] f = data.Split(',');
            if (f.Length < 10) return null;

            double open = ParseDouble(f[1]);
            double prevClose = ParseDouble(f[2]);
            double price = ParseDouble(f[3]);

            // 收盘后新浪返回 price=0 而昨收仍在，直接相减会得到 -100% 的假跌幅
            double change = 0, changePct = 0;
            if (price > 0 && prevClose > 0)
            {
                change = price - prevClose;
                changePct = change / prevClose * 100.0;
            }

            Quote quote = new Quote();
            quote.Code = code;
            quote.Market = "CN";
            quote.Name = f[0];
            quote.Price = price;
            quote.Change = Math.Round(change, 2);
            quote.ChangePct = Math.Round(changePct, 2);
            quote.Open = open;
            quote.High = ParseDouble(f[4]);
            quote.Low = ParseDouble(f[5]);
            quote.Volume = ParseLong(f[8]);      // 已是股
            quote.Turnover = ParseDouble(f[9]);  // 已是元
            quote.TurnoverRate = null;           // 该接口不提供换手率
            quote.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return quote;
        }

        public async Task<List<IndexQuote>> FetchIndicesAsync()
        {
            string[] codes = Symbols.IndexCodes.Split(',');
            for (int i = 0; i < codes.Length; i++) codes[i] = Symbols.StripIndexPrefix(codes[i]);

            string body = await Http.GetGbkAsync(QuoteUrl + string.Join(",", codes), Referer).ConfigureAwait(false);
            List<IndexQuote> result = new List<IndexQuote>();
            foreach (string line in SplitLines(body))
            {
                IndexQuote index = ParseIndexLine(line);
                if (index != null) result.Add(index);
            }
            return result;
        }

        /// <summary>个股格式解析指数（成交额/成交量比紧凑指数格式可靠）。</summary>
        public static IndexQuote ParseIndexLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            int eq = line.IndexOf('=');
            if (eq < 0) return null;
            string varPart = line.Substring(0, eq).Trim();
            const string prefix = "var hq_str_";
            if (!varPart.StartsWith(prefix, StringComparison.Ordinal)) return null;
            string codeRaw = varPart.Substring(prefix.Length);

            string data = ExtractQuoted(line, eq);
            if (data == null) return null;
            string[] f = data.Split(',');
            if (f.Length < 10) return null;

            double prevClose = ParseDouble(f[2]);
            double price = ParseDouble(f[3]);
            double change = 0, changePct = 0;
            if (price > 0 && prevClose > 0)
            {
                change = price - prevClose;
                changePct = change / prevClose * 100.0;
            }

            IndexQuote index = new IndexQuote();
            index.Code = "s_" + codeRaw;   // 内部统一用 s_ 前缀形式作为 key
            index.Name = f[0];
            index.Price = price;
            index.Change = Math.Round(change, 2);
            index.ChangePct = Math.Round(changePct, 2);
            long volume = ParseLong(f[8]);
            // 上证指数成交量在**所有**新浪格式下都是正确值的 1/100，这里校正
            index.Volume = codeRaw.StartsWith("sh", StringComparison.Ordinal) ? volume * 100L : volume;
            index.Turnover = ParseDouble(f[9]);
            return index;
        }

        // ── 搜索 ──

        public async Task<List<StockBrief>> SearchAsync(string keyword, string market)
        {
            List<StockBrief> result = new List<StockBrief>();
            string trimmed = (keyword ?? "").Trim();
            if (!IsSixDigits(trimmed)) return result;

            string body = await Http.GetGbkAsync(QuoteUrl + Symbols.ToExchangeCode(trimmed, market), Referer).ConfigureAwait(false);
            foreach (string line in SplitLines(body))
            {
                Quote quote = ParseQuoteLine(line);
                if (quote != null && !string.IsNullOrEmpty(quote.Name))
                {
                    result.Add(new StockBrief
                    {
                        Code = quote.Code,
                        Market = quote.Market,
                        Name = quote.Name,
                        Category = Symbols.CnCategory(quote.Code)
                    });
                    break;
                }
            }
            return result;
        }

        // ── 分时（5 分钟 K 拼当日） ──

        public async Task<List<MinutePoint>> FetchMinuteAsync(string code, string market)
        {
            string symbol = Symbols.ToExchangeCode(Symbols.StripIndexPrefix(code), market);
            string url = string.Format(CultureInfo.InvariantCulture,
                "{0}?symbol={1}&scale=5&datalen=240", KLineUrl, symbol);

            string text = await Http.GetTextAsync(url, Referer).ConfigureAwait(false);
            string json = TrimToArray(text);
            object[] rows = Json.AsArray(Json.Parse(json));

            List<MinutePoint> points = new List<MinutePoint>();
            if (rows == null) return points;

            for (int i = 0; i < rows.Length; i++)
            {
                string day = Json.Str(Json.Get(rows[i], "day"));
                if (day.Length == 0) continue;

                MinutePoint point = new MinutePoint();
                point.Time = day.Length >= 16 ? day.Substring(11, 5) : day;
                point.Open = Json.NumStr(Json.Get(rows[i], "open"));
                point.Price = Json.NumStr(Json.Get(rows[i], "close"));
                double high = Json.NumStr(Json.Get(rows[i], "high"), double.NaN);
                double low = Json.NumStr(Json.Get(rows[i], "low"), double.NaN);
                point.High = double.IsNaN(high) ? point.Price : high;
                point.Low = double.IsNaN(low) ? point.Price : low;
                point.Volume = (long)Json.NumStr(Json.Get(rows[i], "volume"));
                // 新浪 5 分钟 K 不提供均价，用开盘价近似（与旧版一致）
                point.AvgPrice = point.Open;
                points.Add(point);
            }
            return points;
        }

        // ── K 线 ──

        public async Task<List<KLineBar>> FetchKLineAsync(string code, string market, ChartPeriod period, int count)
        {
            if (period == ChartPeriod.Min1)
                throw new InvalidOperationException("新浪数据源不支持 1 分钟 K 线，请切换到腾讯数据源");

            int scale = SinaScale(period);
            string symbol = Symbols.ToExchangeCode(Symbols.StripIndexPrefix(code), market);
            string url = string.Format(CultureInfo.InvariantCulture,
                "{0}?symbol={1}&scale={2}&ma=no&datalen={3}", KLineUrl, symbol, scale,
                period == ChartPeriod.Daily || period == ChartPeriod.Weekly || period == ChartPeriod.Monthly ? 600 : 300);

            string text = await Http.GetTextAsync(url, Referer).ConfigureAwait(false);
            if (string.IsNullOrEmpty(text) || text.Trim() == "null") return new List<KLineBar>();

            string json = TrimToArray(text);
            if (json.Length == 0)
                throw new InvalidOperationException("新浪 K 线响应格式异常：未找到 JSON 数组");

            object[] rows = Json.AsArray(Json.Parse(json));
            List<KLineBar> bars = new List<KLineBar>();
            if (rows == null) return bars;

            for (int i = 0; i < rows.Length; i++)
            {
                string day = Json.Str(Json.Get(rows[i], "day"));
                if (day.Length == 0) continue;

                KLineBar bar = new KLineBar();
                bar.Date = day;
                bar.Open = Json.NumStr(Json.Get(rows[i], "open"));
                bar.High = Json.NumStr(Json.Get(rows[i], "high"));
                bar.Low = Json.NumStr(Json.Get(rows[i], "low"));
                bar.Close = Json.NumStr(Json.Get(rows[i], "close"));
                bar.Volume = (long)Json.NumStr(Json.Get(rows[i], "volume"));
                bar.Turnover = 0;   // 新浪 K 线不提供成交额
                bars.Add(bar);
            }
            return bars;
        }

        private static int SinaScale(ChartPeriod period)
        {
            switch (period)
            {
                case ChartPeriod.Daily: return 240;
                case ChartPeriod.Weekly: return 1200;
                case ChartPeriod.Monthly: return 7200;
                case ChartPeriod.Min5: return 5;
                case ChartPeriod.Min15: return 15;
                case ChartPeriod.Min30: return 30;
                case ChartPeriod.Min60: return 60;
                default:
                    throw new InvalidOperationException("新浪数据源不支持该周期，请切换到腾讯数据源");
            }
        }

        // ── 盘口（回退腾讯） ──

        public async Task<Depth> FetchDepthAsync(string code, string market)
        {
            Depth depth = new Depth { Code = code };
            string tcCode = Symbols.ToExchangeCode(code, market);
            string body = await Http.GetGbkAsync("http://qt.gtimg.cn/q=" + tcCode, "https://gu.qq.com").ConfigureAwait(false);

            foreach (string line in SplitLines(body))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string data = ExtractQuoted(line, eq);
                if (data == null) continue;
                string[] f = data.Split('~');
                if (f.Length < 29) continue;

                for (int i = 0; i < 5; i++)
                {
                    int pi = 9 + i * 2;
                    AddLevel(depth.Bids, f[pi], f[pi + 1]);
                }
                for (int i = 0; i < 5; i++)
                {
                    int pi = 19 + i * 2;
                    AddLevel(depth.Asks, f[pi], f[pi + 1]);
                }
                break;
            }
            return depth;
        }

        private static void AddLevel(List<Level> target, string priceText, string volumeText)
        {
            double price = ParseDouble(priceText);
            long volume = ParseLong(volumeText);
            if (price > 0 && volume > 0) target.Add(new Level(price, volume * 100L));
        }

        public async Task<bool> HealthCheckAsync()
        {
            List<Quote> quotes = await FetchRealtimeAsync(new List<string> { "000001" }, "CN").ConfigureAwait(false);
            return quotes.Count > 0;
        }

        // ── 工具 ──

        /// <summary>截到最后一个 ']'：新浪响应尾部常带 JS 回调注释。</summary>
        private static string TrimToArray(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int last = text.LastIndexOf(']');
            if (last < 0) return "";
            return text.Substring(0, last + 1).Trim();
        }

        private static string ExtractQuoted(string line, int eqIndex)
        {
            int open = line.IndexOf('"', eqIndex + 1);
            if (open < 0) return null;
            int close = line.IndexOf('"', open + 1);
            if (close < 0) return null;
            return line.Substring(open + 1, close - open - 1);
        }

        private static string[] SplitLines(string text)
        {
            return (text ?? "").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static double ParseDouble(string text)
        {
            double value;
            return double.TryParse((text ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out value) ? value : 0.0;
        }

        private static long ParseLong(string text)
        {
            long value;
            if (long.TryParse((text ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out value)) return value;
            double d = ParseDouble(text);
            return d > 9.2e18 ? long.MaxValue : (long)d;
        }

        private static bool IsSixDigits(string text)
        {
            if (text.Length != 6) return false;
            for (int i = 0; i < text.Length; i++)
                if (text[i] < '0' || text[i] > '9') return false;
            return true;
        }
    }
}
