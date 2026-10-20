using System;
using System.Globalization;

namespace QuantNative.Core
{
    /// <summary>
    /// 代码规则与显示格式化。逐条对齐 Tauri 版：
    ///   src-tauri/src/datasource/mod.rs::cn_category / minute_span
    ///   src/utils/format.ts
    /// 两边必须保持一致，否则同一只股票在旧版/新版里会显示成不同市场标签。
    /// </summary>
    public static class Symbols
    {
        /// <summary>主要指数（带 s_ 前缀的紧凑代码形式，内部统一用这个 key）。</summary>
        public const string IndexCodes =
            "s_sh000001,s_sz399001,s_sz399006,s_sh000688,s_sh000698,s_sh000905,s_sh000680";

        /// <summary>
        /// 指数条的固定显示顺序。界面上「大盘」永远按这个顺序从左到右排列，
        /// 不跟随数据源返回顺序 —— 否则某家接口少返/多返一项时，
        /// 指数就会整体错位（上证指数的位置显示成深证成指）。
        /// </summary>
        public static readonly string[] IndexOrder = IndexCodes.Split(',');

        /// <summary>
        /// 指数在固定顺序里的名次。未在列表中的代码排到最后（按代码字典序兜底），
        /// 保证排序结果稳定、不随数据源返回顺序抖动。
        /// </summary>
        public static int IndexRank(string code)
        {
            string key = NormalizeIndexKey(code);
            for (int i = 0; i < IndexOrder.Length; i++)
                if (string.Equals(IndexOrder[i], key, StringComparison.OrdinalIgnoreCase)) return i;
            return IndexOrder.Length;
        }

        /// <summary>"sh000001" / "s_sh000001" 统一成 "s_sh000001"。</summary>
        public static string NormalizeIndexKey(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            return code.StartsWith("s_", StringComparison.Ordinal) ? code : "s_" + code;
        }

        /// <summary>指数在接口返回名称缺失时的兜底显示名。</summary>
        public static string IndexFallbackName(string code)
        {
            switch (NormalizeIndexKey(code))
            {
                case "s_sh000001": return "上证指数";
                case "s_sz399001": return "深证成指";
                case "s_sz399006": return "创业板指";
                case "s_sh000688": return "科创50";
                case "s_sh000905": return "中证500";
                default: return FormatCode(code);
            }
        }

        /// <summary>
        /// 从完整代码推导证券类别，与 Rust 端 cn_category 完全一致：
        ///   sh 0xxxxx→ZS  sh 5xxxxx→ETF  sh 6xxxxx→GP-A  sh 9xxxxx→GP-B
        ///   sz 39xxxx→ZS  sz 159xxx→ETF  sz 16xxxx→LOF  sz 2xxxxx→GP-B  其余→GP-A
        ///   bj 899xxx→ZS  其余 bj→GP-A；无交易所前缀返回 ""
        /// 另外新增场外基金前缀 of（天天基金口径），归为 OF。
        /// </summary>
        public static string CnCategory(string fullCode)
        {
            if (string.IsNullOrEmpty(fullCode)) return "";
            if (IsOpenFund(fullCode)) return "OF";
            if (fullCode.StartsWith("sh", StringComparison.Ordinal))
                return CnCategoryByPrefix(fullCode.Substring(2), "0|ZS", "5|ETF", "9|GP-B");
            if (fullCode.StartsWith("sz", StringComparison.Ordinal))
                return CnCategoryByPrefix(fullCode.Substring(2), "39|ZS", "159|ETF", "16|LOF", "2|GP-B");
            if (fullCode.StartsWith("bj", StringComparison.Ordinal))
                return fullCode.Substring(2).StartsWith("899", StringComparison.Ordinal) ? "ZS" : "GP-A";
            return "";
        }

        /// <summary>场外基金代码统一带 of 前缀（of005844）。</summary>
        public const string OpenFundPrefix = "of";

        public static bool IsOpenFund(string code)
        {
            return !string.IsNullOrEmpty(code)
                && code.StartsWith(OpenFundPrefix, StringComparison.OrdinalIgnoreCase)
                && code.Length > OpenFundPrefix.Length;
        }

        /// <summary>场外基金去掉 of 前缀后的 6 位基金代码。</summary>
        public static string OpenFundCode(string code)
        {
            return IsOpenFund(code) ? code.Substring(OpenFundPrefix.Length) : code;
        }

        /// <summary>按「前缀→类别」规则表推导，顺序敏感（159 必须比 16 先判）。</summary>
        private static string CnCategoryByPrefix(string code, params string[] rules)
        {
            for (int i = 0; i < rules.Length; i++)
            {
                int bar = rules[i].IndexOf('|');
                string prefix = rules[i].Substring(0, bar);
                string category = rules[i].Substring(bar + 1);
                if (code.StartsWith(prefix, StringComparison.Ordinal)) return category;
            }
            return "GP-A";
        }

        /// <summary>市场标签（「沪A」「深指」「ETF」等），空字符串表示不显示标签。</summary>
        public static string MarketTag(string code, string category)
        {
            if (string.IsNullOrEmpty(code)) return "";
            if (IsOpenFund(code)) return "场外";
            if (string.IsNullOrEmpty(category)) return "";
            bool isSh = code.StartsWith("sh", StringComparison.Ordinal);
            bool isSz = code.StartsWith("sz", StringComparison.Ordinal);
            bool isBj = code.StartsWith("bj", StringComparison.Ordinal);
            switch (category)
            {
                case "ZS": return isSh ? "沪指" : isSz ? "深指" : isBj ? "北指" : "指数";
                case "ETF": return "ETF";
                case "LOF": return "LOF";
                case "GP-B": return isSh ? "沪B" : isSz ? "深B" : "B股";
                case "GP-A": return isSh ? "沪A" : isSz ? "深A" : isBj ? "北A" : "A股";
                default: return "";
            }
        }

        /// <summary>去掉交易所前缀用于展示（sz000852 → 000852，of005844 → 005844）。</summary>
        public static string FormatCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            if (IsOpenFund(code)) return code.Substring(OpenFundPrefix.Length);
            if (code.StartsWith("sh", StringComparison.Ordinal)
                || code.StartsWith("sz", StringComparison.Ordinal)
                || code.StartsWith("bj", StringComparison.Ordinal))
                return code.Substring(2);
            return code;
        }

        /// <summary>
        /// 补齐交易所前缀（腾讯/新浪的实时行情接口都需要）：
        /// 6/5/9 开头 → sh，其余 → sz；已带前缀的原样返回。
        /// </summary>
        public static string ToExchangeCode(string code, string market)
        {
            if (code == null) return "";
            if (!string.Equals(market, "CN", StringComparison.Ordinal)) return code;
            if (code.StartsWith("sh", StringComparison.Ordinal)
                || code.StartsWith("sz", StringComparison.Ordinal)
                || code.StartsWith("bj", StringComparison.Ordinal)) return code;
            if (code.StartsWith("6", StringComparison.Ordinal)
                || code.StartsWith("5", StringComparison.Ordinal)
                || code.StartsWith("9", StringComparison.Ordinal)) return "sh" + code;
            return "sz" + code;
        }

        /// <summary>指数代码 "s_sh000001" → "sh000001"（腾讯 mkline / 新浪 K 线要的形状）。</summary>
        public static string StripIndexPrefix(string code)
        {
            if (code != null && code.StartsWith("s_", StringComparison.Ordinal)) return code.Substring(2);
            return code;
        }

        /// <summary>
        /// 价格精度：小数第 3 位有值则 3 位，否则 2 位（覆盖 ETF &lt;1 元、可转债等）。
        /// </summary>
        public static int PricePrecision(double price)
        {
            if (double.IsNaN(price) || price == 0) return 2;
            double abs = Math.Abs(price);
            double remainder = Math.Abs(abs * 100.0 - Math.Round(abs * 100.0));
            return remainder > 0.001 ? 3 : 2;
        }

        public static string FormatPrice(double? price, string fallback = "--")
        {
            if (!price.HasValue || double.IsNaN(price.Value)) return fallback;
            return price.Value.ToString("F" + PricePrecision(price.Value), CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 现价文本：场外基金盘中给的是**估算**净值，后面加 * 提醒用户这不是正式净值
        /// （净值公布后不加标记）。* 的含义在详情面板与 README 里说明。
        /// </summary>
        public static string FormatQuotePrice(Quote quote)
        {
            if (quote == null) return "--";
            string text = FormatPrice(quote.Price);
            if (quote.PriceKind == "估值") text += "*";
            return text;
        }

        /// <summary>价格口径说明："估值 15:04（按重仓股估算）" / "净值 2026-09-17" / ""。</summary>
        public static string PriceKindText(Quote quote)
        {
            if (quote == null || string.IsNullOrEmpty(quote.PriceKind)) return "";
            string text = quote.PriceKind;
            if (!string.IsNullOrEmpty(quote.PriceTime)) text += " " + quote.PriceTime;
            if (quote.PriceKind == "估值") text += "（按前十大重仓股估算）";
            return text;
        }

        /// <summary>价格口径的短形式（标题栏用）："估值 15:04" / "净值 2026-09-17"。</summary>
        public static string PriceKindShort(Quote quote)
        {
            if (quote == null || string.IsNullOrEmpty(quote.PriceKind)) return "";
            return quote.PriceKind + (string.IsNullOrEmpty(quote.PriceTime) ? "" : " " + quote.PriceTime);
        }

        /// <summary>涨跌幅字符串："+1.23%" / "-0.45%" / "--"。</summary>
        public static string FormatPct(double? pct, string fallback = "--")
        {
            if (!pct.HasValue || double.IsNaN(pct.Value)) return fallback;
            string sign = pct.Value >= 0 ? "+" : "";
            return sign + pct.Value.ToString("F2", CultureInfo.InvariantCulture) + "%";
        }

        // ── 浮窗专用：所有数字统一 3 位小数 ──
        // 浮窗是「一眼看数」的场景，混用 2 位 / 4 位小数会让小数点对不齐、数字长度跳变，
        // 因此浮窗里的价格、涨跌、涨跌幅一律 3 位小数（等宽字体下整列小数点严格对齐）。

        /// <summary>浮窗统一精度：3 位小数。</summary>
        public const int TickerDecimals = 3;

        /// <summary>3 位小数数值（浮窗价格 / 净值）。</summary>
        public static string FormatPrice3(double? price, string fallback = "--")
        {
            if (!price.HasValue || double.IsNaN(price.Value)) return fallback;
            return price.Value.ToString("F" + TickerDecimals, CultureInfo.InvariantCulture);
        }

        /// <summary>带符号的 3 位小数涨跌额（浮窗）。</summary>
        public static string FormatChange3(double? change, string fallback = "--")
        {
            if (!change.HasValue || double.IsNaN(change.Value)) return fallback;
            string sign = change.Value >= 0 ? "+" : "";
            return sign + change.Value.ToString("F" + TickerDecimals, CultureInfo.InvariantCulture);
        }

        /// <summary>带符号的 3 位小数涨跌幅（浮窗）。</summary>
        public static string FormatPct3(double? pct, string fallback = "--")
        {
            if (!pct.HasValue || double.IsNaN(pct.Value)) return fallback;
            string sign = pct.Value >= 0 ? "+" : "";
            return sign + pct.Value.ToString("F" + TickerDecimals, CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>涨跌额字符串（带符号）。</summary>
        public static string FormatChange(double? change)
        {
            if (!change.HasValue || double.IsNaN(change.Value)) return "--";
            string sign = change.Value >= 0 ? "+" : "";
            return sign + change.Value.ToString("F" + PricePrecision(change.Value), CultureInfo.InvariantCulture);
        }

        /// <summary>成交额：万亿 / 亿 / 万 / 元。</summary>
        public static string FormatAmount(double amount, string fallback = "--")
        {
            if (double.IsNaN(amount) || amount <= 0) return fallback;
            if (amount >= 1e12) return (amount / 1e12).ToString("F2", CultureInfo.InvariantCulture) + "万亿";
            if (amount >= 1e8) return (amount / 1e8).ToString("F2", CultureInfo.InvariantCulture) + "亿";
            if (amount >= 1e4) return (amount / 1e4).ToString("F2", CultureInfo.InvariantCulture) + "万";
            return amount.ToString("F0", CultureInfo.InvariantCulture) + "元";
        }

        /// <summary>成交量（输入为股，显示为手/万手/亿手）。</summary>
        public static string FormatVolume(long volume, string fallback = "--")
        {
            if (volume <= 0) return fallback;
            double shou = volume / 100.0;
            if (shou >= 10000.0)
            {
                double wan = shou / 10000.0;
                if (wan >= 10000.0) return (wan / 10000.0).ToString("F2", CultureInfo.InvariantCulture) + "亿手";
                if (wan >= 100.0) return wan.ToString("F0", CultureInfo.InvariantCulture) + "万手";
                return wan.ToString("F2", CultureInfo.InvariantCulture) + "万手";
            }
            return shou.ToString("F0", CultureInfo.InvariantCulture) + "手";
        }

        /// <summary>A 股「红涨绿跌」的颜色选择，供各控件统一调用（见 UI/Theme.cs）。</summary>
        public static bool IsUp(double? changePct)
        {
            return changePct.HasValue && changePct.Value >= 0;
        }

        // ── 筛选分组（搜索框下方的勾选项用） ──

        /// <summary>市场分组：SHA 沪市 / SZA 深市 / BJ 北交所 / HK 港股 / US 美股 / OF 场外基金 / OTHER。</summary>
        public static string MarketGroup(string code, string market)
        {
            if (IsOpenFund(code)) return "OF";
            if (string.Equals(market, "OF", StringComparison.OrdinalIgnoreCase)) return "OF";
            if (string.Equals(market, "HK", StringComparison.OrdinalIgnoreCase)) return "HK";
            if (string.Equals(market, "US", StringComparison.OrdinalIgnoreCase)) return "US";

            string body = code ?? "";
            if (body.StartsWith("hk", StringComparison.OrdinalIgnoreCase)) return "HK";
            if (body.StartsWith("us", StringComparison.OrdinalIgnoreCase)) return "US";
            if (body.StartsWith("sh", StringComparison.OrdinalIgnoreCase)) return "SHA";
            if (body.StartsWith("sz", StringComparison.OrdinalIgnoreCase)) return "SZA";
            if (body.StartsWith("bj", StringComparison.OrdinalIgnoreCase)) return "BJ";
            return "OTHER";
        }

        /// <summary>类型分组：STOCK 股票 / FUND 基金 / INDEX 指数 / BOND 可转债 / OTHER 其他。</summary>
        public static string TypeGroup(string code, string category)
        {
            if (IsOpenFund(code)) return "FUND";

            string body = code ?? "";
            if (body.StartsWith("sh", StringComparison.OrdinalIgnoreCase)
                || body.StartsWith("sz", StringComparison.OrdinalIgnoreCase)
                || body.StartsWith("bj", StringComparison.OrdinalIgnoreCase))
            {
                string digits = body.Substring(2);
                // 可转债：沪 110/113/120+，深 123/127/128 —— 这类代码在 cn_category 里会被
                // 归成 GP-A，所以要在类型分组里先单独识别出来。
                if (digits.StartsWith("11", StringComparison.Ordinal)
                    || digits.StartsWith("12", StringComparison.Ordinal)) return "BOND";
            }

            string cat = string.IsNullOrEmpty(category) ? CnCategory(code) : category;
            switch (cat)
            {
                case "ETF":
                case "LOF":
                case "OF": return "FUND";
                case "ZS": return "INDEX";
                default: return "STOCK";
            }
        }

        /// <summary>市场分组的中文名（用于筛选提示）。</summary>
        public static string MarketGroupName(string group)
        {
            switch (group)
            {
                case "SHA": return "沪市";
                case "SZA": return "深市";
                case "BJ": return "北交所";
                case "HK": return "港股";
                case "US": return "美股";
                case "OF": return "场外基金";
                default: return "其他";
            }
        }

        /// <summary>类型分组的中文名。</summary>
        public static string TypeGroupName(string group)
        {
            switch (group)
            {
                case "STOCK": return "股票";
                case "FUND": return "基金";
                case "INDEX": return "指数";
                case "BOND": return "可转债";
                default: return "其他";
            }
        }

        private static string Body(string code)
        {
            return code ?? "";
        }
    }
}
