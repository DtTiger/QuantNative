using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuantNative.Core;

namespace QuantNative.Data
{
    // 第三家股票报价只在腾讯/新浪缺数时补位，不改变详情页原有数据源选择。
    public sealed class EastmoneyStockQuoteClient
    {
        public async Task<List<Quote>> FetchAsync(IList<string> codes)
        {
            var secids = new List<string>();
            foreach (string code in codes)
            {
                if (code != null && code.Length == 8 && (code.StartsWith("sh") || code.StartsWith("sz")))
                    secids.Add((code.StartsWith("sh") ? "1." : "0.") + code.Substring(2));
            }
            if (secids.Count == 0) return new List<Quote>();
            string url = "https://push2.eastmoney.com/api/qt/ulist.np/get?secids=" + string.Join(",", secids.ToArray())
                + "&fields=f12,f13,f14,f2,f3,f4,f5,f6,f8,f15,f16,f17,f18,f124&fltt=2";
            string body = await Http.GetTextAsync(url, "https://quote.eastmoney.com/").ConfigureAwait(false);
            return Parse(body);
        }

        public static List<Quote> Parse(string body)
        {
            var result = new List<Quote>();
            object[] rows = Json.AsArray(Json.Path(Json.Parse(body), "data", "diff"));
            if (rows == null) return result;
            foreach (object row in rows)
            {
                string six = Json.Str(Json.Get(row, "f12"));
                long exchange = Json.Long(Json.Get(row, "f13"), -1);
                double price = Json.Num(Json.Get(row, "f2"), 0);
                long stamp = Json.Long(Json.Get(row, "f124"));
                if (six.Length != 6 || (exchange != 0 && exchange != 1) || price <= 0 || stamp <= 0) continue;
                result.Add(new Quote {
                    Code = (exchange == 1 ? "sh" : "sz") + six,
                    Market = "CN", Name = Json.Str(Json.Get(row, "f14")),
                    Price = price, Change = Json.Num(Json.Get(row, "f4")),
                    ChangePct = Json.Num(Json.Get(row, "f3")),
                    Open = Json.Num(Json.Get(row, "f17")), High = Json.Num(Json.Get(row, "f15")),
                    Low = Json.Num(Json.Get(row, "f16")), Volume = Json.Long(Json.Get(row, "f5")) * 100,
                    Turnover = Json.Num(Json.Get(row, "f6")), TurnoverRate = Json.Num(Json.Get(row, "f8")),
                    Timestamp = stamp
                });
            }
            return result;
        }
    }
}
