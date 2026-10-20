using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using QuantNative.Core;

namespace QuantNative.Data
{
    /// <summary>
    /// 腾讯证券适配器（默认数据源）。逐条对齐 Rust 端 datasource/tencent.rs：
    ///   * 实时行情 qt.gtimg.cn/q=   —— 字段索引 3=价 4=昨收 5=开 32=涨跌幅 33=高 34=低
    ///     37=成交额(万元) 38=换手率；量单位「手」需 ×100 归一化为股
    ///   * 指数 s_ 前缀紧凑格式：0=市场 1=名称 2=代码 3=价 4=涨跌 5=涨跌幅 6=量(手) 7=额(万元)
    ///   * 分钟 K / 分时：ifzq.gtimg.cn mkline（分钟端点不支持复权，为原始价）
    ///   * 日/周/月 K：web.ifzq.gtimg.cn fqkline，带 qfq 前复权
    ///   * 五档盘口：行情字段 9-28（买 1-5 在 9..18，卖 1-5 在 19..28）
    /// </summary>
    public sealed class TencentSource : IQuoteSource
    {
        private const string QuoteUrl = "http://qt.gtimg.cn/q=";
        private const string Referer = "https://gu.qq.com";

        public string Name { get { return "tencent"; } }
        public string DisplayName { get { return "腾讯证券"; } }

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

        public async Task<List<IndexQuote>> FetchIndicesAsync()
        {
            string body = await Http.GetGbkAsync(QuoteUrl + Symbols.IndexCodes, Referer).ConfigureAwait(false);
            List<IndexQuote> result = new List<IndexQuote>();
            foreach (string line in SplitLines(body))
            {
                IndexQuote index = ParseIndexLine(line);
                if (index != null) result.Add(index);
            }
            return result;
        }

        /// <summary>解析一行腾讯个股行情：v_sz000852="51~石化机械~000852~6.06~5.51~..."</summary>
        public static Quote ParseQuoteLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            int eq = line.IndexOf('=');
            if (eq <= 2) return null;
            string varPart = line.Substring(0, eq);
            if (!varPart.StartsWith("v_", StringComparison.Ordinal)) return null;
            string code = varPart.Substring(2);

            string data = ExtractQuoted(line, eq);
            if (data == null) return null;
            string[] f = data.Split('~');
            if (f.Length < 38) return null;

            double price = ParseDouble(f[3]);
            double prevClose = ParseDouble(f[4]);
            double changePct = ParseDouble(f[32]);
            // 收盘后接口可能返回 price=0 / 昨收>0，此时算出来的涨跌是假的，直接置 0
            double change = (price > 0 && prevClose > 0) ? price - prevClose : 0.0;

            Quote quote = new Quote();
            quote.Code = code;
            quote.Market = "CN";
            quote.Name = f[1];
            quote.Price = price;
            quote.Change = Math.Round(change, 2);
            quote.ChangePct = changePct;
            quote.Open = ParseDouble(f[5]);
            quote.High = ParseDouble(f[33]);
            quote.Low = ParseDouble(f[34]);
            quote.Volume = ParseLong(f[6]) * 100L;                       // 手 → 股
            quote.Turnover = Math.Round(ParseDouble(f[37]) * 10000.0, 2); // 万元 → 元
            double rate;
            quote.TurnoverRate = f.Length > 38 && double.TryParse(f[38], NumberStyles.Any, CultureInfo.InvariantCulture, out rate)
                ? (double?)rate : null;
            quote.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return quote;
        }

        /// <summary>解析指数：v_s_sh000001="1~上证指数~000001~3245.67~12.34~0.38~..."</summary>
        public static IndexQuote ParseIndexLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            int eq = line.IndexOf('=');
            if (eq <= 2) return null;
            string varPart = line.Substring(0, eq);
            if (!varPart.StartsWith("v_", StringComparison.Ordinal)) return null;
            string code = varPart.Substring(2);   // s_sh000001

            string data = ExtractQuoted(line, eq);
            if (data == null) return null;
            string[] f = data.Split('~');
            if (f.Length < 6) return null;

            IndexQuote index = new IndexQuote();
            index.Code = code;
            index.Name = f[1];
            index.Price = ParseDouble(f[3]);
            index.Change = ParseDouble(f[4]);
            index.ChangePct = ParseDouble(f[5]);
            index.Volume = ParseLong(f[6]) * 100L;
            index.Turnover = f.Length > 7 ? ParseDouble(f[7]) * 10000.0 : 0.0;
            return index;
        }

        // ── 搜索（6 位数字代码直查） ──

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

        // ── 分时（用 1 分钟 K 线拼当日，242 根覆盖 9:30-11:30 + 13:00-15:00） ──

        public async Task<List<MinutePoint>> FetchMinuteAsync(string code, string market)
        {
            string tcCode = Symbols.ToExchangeCode(Symbols.StripIndexPrefix(code), market);
            string url = "http://ifzq.gtimg.cn/appstock/app/kline/mkline?param=" + tcCode + ",m1,,242";
            string text = await Http.GetTextAsync(url, Referer).ConfigureAwait(false);

            object root = Json.Parse(text);
            object lines = Json.Path(root, "data");
            lines = Json.FirstValue(lines);
            lines = Json.Get(lines, "m1");
            object[] array = Json.AsArray(lines);

            List<MinutePoint> points = new List<MinutePoint>();
            if (array == null) return points;

            for (int i = 0; i < array.Length; i++)
            {
                object[] item = Json.AsArray(array[i]);
                if (item == null || item.Length < 6) continue;
                string raw = Json.Str(item[0]);
                string time = raw.Length >= 12 ? raw.Substring(8, 2) + ":" + raw.Substring(10, 2) : raw;

                MinutePoint point = new MinutePoint();
                point.Time = time;
                point.Open = Json.NumStr(item[1]);
                point.Price = Json.NumStr(item[2]);
                double high = Json.NumStr(item[3], double.NaN);
                double low = Json.NumStr(item[4], double.NaN);
                point.High = double.IsNaN(high) ? point.Price : high;
                point.Low = double.IsNaN(low) ? point.Price : low;
                point.Volume = (long)(Json.NumStr(item[5]) * 100.0);
                // 腾讯 m1 不给均价，用当根开盘价近似（与旧版一致）
                point.AvgPrice = point.Open;
                points.Add(point);
            }
            return points;
        }

        // ── K 线 ──

        public async Task<List<KLineBar>> FetchKLineAsync(string code, string market, ChartPeriod period, int count)
        {
            string tcCode = Symbols.ToExchangeCode(Symbols.StripIndexPrefix(code), market);
            int? span = ChartPeriods.MinuteSpan(period);

            if (span.HasValue)
            {
                // 分钟 K：mkline（不支持复权参数，为不复权原始价）
                string url = string.Format(
                    CultureInfo.InvariantCulture,
                    "http://ifzq.gtimg.cn/appstock/app/kline/mkline?param={0},m{1},,,{2}",
                    tcCode, span.Value, count <= 0 ? 320 : count);
                string text = await Http.GetTextAsync(url, Referer).ConfigureAwait(false);

                object node = Json.FirstValue(Json.Path(Json.Parse(text), "data"));
                object[] rows = Json.AsArray(Json.Get(node, "m" + span.Value));
                return ParseBarArray(rows, true);
            }

            string periodParam = period == ChartPeriod.Weekly ? "week" : period == ChartPeriod.Monthly ? "month" : "day";
            string dayUrl = string.Format(
                CultureInfo.InvariantCulture,
                "http://web.ifzq.gtimg.cn/appstock/app/fqkline/get?param={0},{1},,{2},{3},qfq",   // 空 end = 最新
                tcCode, periodParam, "", count <= 0 ? 200 : count);
            string dayText = await Http.GetTextAsync(dayUrl, Referer).ConfigureAwait(false);

            object stock = Json.FirstValue(Json.Path(Json.Parse(dayText), "data"));
            object bars = Json.Get(stock, periodParam);
            if (bars == null) bars = Json.Get(stock, "qfq" + periodParam);
            return ParseBarArray(Json.AsArray(bars), false);
        }

        /// <summary>
        /// 腾讯 K 线数组：[日期, 开, 收, 高, 低, 量(手), 额?, ...]
        /// 分钟 K 的成交额位置随接口版本变化（index 6 可能是空对象 {} 或金额字符串），
        /// 故对分钟 K 扫描 index 6-7 取首个可解析数值；日/周/月 K 固定取 index 6。
        /// </summary>
        private static List<KLineBar> ParseBarArray(object[] rows, bool minute)
        {
            List<KLineBar> result = new List<KLineBar>();
            if (rows == null) return result;

            for (int i = 0; i < rows.Length; i++)
            {
                object[] row = Json.AsArray(rows[i]);
                if (row == null || row.Length < 6) continue;

                string date = Json.Str(row[0]);
                if (minute && date.Length >= 12)
                {
                    // "202608070930" → "2026-08-07 09:30"（腾讯返回周期起止时间，直接透传）
                    date = date.Substring(0, 4) + "-" + date.Substring(4, 2) + "-" + date.Substring(6, 2)
                         + " " + date.Substring(8, 2) + ":" + date.Substring(10, 2);
                }

                KLineBar bar = new KLineBar();
                bar.Date = date;
                bar.Open = Json.NumStr(row[1]);
                bar.Close = Json.NumStr(row[2]);
                bar.High = Json.NumStr(row[3]);
                bar.Low = Json.NumStr(row[4]);
                bar.Volume = (long)(Json.NumStr(row[5]) * 100.0);

                if (minute)
                {
                    for (int k = 6; k <= 7 && k < row.Length; k++)
                    {
                        double value;
                        if (Json.TryNumStr(row[k], out value)) { bar.Turnover = value; break; }
                    }
                }
                else if (row.Length > 6)
                {
                    bar.Turnover = Json.NumStr(row[6]);
                }
                result.Add(bar);
            }
            return result;
        }

        // ── 五档盘口 ──

        public async Task<Depth> FetchDepthAsync(string code, string market)
        {
            Depth depth = new Depth { Code = code };
            string tcCode = Symbols.ToExchangeCode(code, market);
            string body = await Http.GetGbkAsync(QuoteUrl + tcCode, Referer).ConfigureAwait(false);

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
                break;   // 只看第一行（单只股票）
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

        /// <summary>取等号后第一个引号里的内容。</summary>
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
