using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using QuantNative.Core;

namespace QuantNative.Data
{
    /// <summary>
    /// 多源合并搜索：
    ///   1) 新浪 suggest（代码 / 中文名 / 拼音缩写，模糊匹配）
    ///   2) 腾讯 smartbox（同上，备用通道）
    ///   3) 各注册数据源自身的搜索：腾讯/新浪 = 精确代码查询；
    ///      **天天基金 = 场外基金模糊搜索**（新浪和腾讯都覆盖不到场外基金）
    ///
    /// 与旧版的关键差别：不再「哪一级先有结果就返回」，而是**全部跑完再合并去重**，
    /// 这样一次搜索能同时列出场内标的与场外基金，且每个结果都带上了能提供实时数据的
    /// 数据源（StockBrief.Source），轮询时按来源分别取数。
    /// 任一家失败只记日志，不影响其它来源出结果。
    /// </summary>
    public sealed class SearchService
    {
        private const string SinaSuggestUrl = "https://suggest3.sinajs.cn/suggest/name=cn";
        private const string TencentSuggestUrl = "http://smartbox.gtimg.cn/s3/";
        private const int MaxResults = 20;
        private const int MaxTotal = 30;

        public async Task<List<StockBrief>> SearchAsync(string keyword, IQuoteSource active, IList<IQuoteSource> allSources)
        {
            string trimmed = (keyword ?? "").Trim();
            List<StockBrief> merged = new List<StockBrief>();
            if (trimmed.Length == 0) return merged;

            HashSet<string> seenCodes = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> exchangeCodes = new HashSet<string>(StringComparer.Ordinal);

            // ── 1) 新浪 suggest ──
            try
            {
                string url = SinaSuggestUrl + "&key=" + Http.UrlEncode(trimmed);
                string body = await Http.GetGbkAsync(url, "https://finance.sina.com.cn").ConfigureAwait(false);
                List<StockBrief> hits = ParseSinaSuggest(body, MaxResults);
                Append(merged, seenCodes, exchangeCodes, hits, "sina", "新浪财经");
                if (hits.Count == 0) Log.Info("新浪 suggest 无结果: " + trimmed);
            }
            catch (Exception ex)
            {
                Log.Warn("新浪 suggest 失败: " + ex.Message);
            }

            // ── 2) 腾讯 smartbox ──
            try
            {
                string url = TencentSuggestUrl + "?q=" + Http.UrlEncode(trimmed) + "&t=all";
                string body = await Http.GetTextAsync(url, "https://gu.qq.com").ConfigureAwait(false);
                List<StockBrief> hits = ParseTencentSmartbox(body, MaxResults);
                Append(merged, seenCodes, exchangeCodes, hits, "tencent", "腾讯证券");
                if (hits.Count == 0) Log.Info("腾讯 smartbox 无结果: " + trimmed);
            }
            catch (Exception ex)
            {
                Log.Warn("腾讯 smartbox 失败: " + ex.Message);
            }

            // ── 3) 各数据源自身的搜索（含场外基金） ──
            if (allSources != null)
            {
                for (int i = 0; i < allSources.Count; i++)
                {
                    IQuoteSource source = allSources[i];
                    if (source == null) continue;
                    try
                    {
                        List<StockBrief> hits = await source.SearchAsync(trimmed, "CN").ConfigureAwait(false);
                        Append(merged, seenCodes, exchangeCodes, hits, source.Name, source.DisplayName);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("数据源 " + source.Name + " 搜索失败: " + ex.Message);
                    }
                }
            }

            // 代码完全匹配的排最前（用户直接粘代码时最想要的就是那一条）
            string target = Symbols.FormatCode(trimmed);
            merged.Sort(delegate (StockBrief a, StockBrief b)
            {
                bool ea = Symbols.FormatCode(a.Code) == target;
                bool eb = Symbols.FormatCode(b.Code) == target;
                if (ea != eb) return ea ? -1 : 1;
                return 0;
            });

            if (merged.Count > MaxTotal) merged.RemoveRange(MaxTotal, merged.Count - MaxTotal);
            return merged;
        }

        /// <summary>
        /// 合并一批结果：按完整代码去重；场内代码记入 exchangeCodes，
        /// 之后若天天基金又返回同一 6 位代码（510300 这类场内 ETF 两边都有），
        /// 就跳过基金那条 —— 场内 ETF 用腾讯/新浪的盘中行情更准更实时。
        /// </summary>
        private static void Append(List<StockBrief> merged, HashSet<string> seenCodes,
            HashSet<string> exchangeCodes, List<StockBrief> hits, string sourceName, string sourceDisplay)
        {
            if (hits == null) return;
            for (int i = 0; i < hits.Count; i++)
            {
                StockBrief brief = hits[i];
                if (brief == null || string.IsNullOrEmpty(brief.Code)) continue;

                bool isFund = Symbols.IsOpenFund(brief.Code);
                string stripped = Symbols.FormatCode(brief.Code);

                // Only an exchange-traded fund can duplicate an open-fund entry.
                // Stock 000001 and fund 000001 are different instruments.
                if (!isFund && (brief.Category == "ETF" || brief.Category == "LOF")) exchangeCodes.Add(stripped);
                else if (exchangeCodes.Contains(stripped)) continue;

                if (!seenCodes.Add(brief.Code)) continue;

                if (string.IsNullOrEmpty(brief.Source))
                {
                    brief.Source = sourceName;
                    brief.SourceName = sourceDisplay;
                }
                if (string.IsNullOrEmpty(brief.Market)) brief.Market = "CN";
                merged.Add(brief);
            }
        }

        /// <summary>
        /// 新浪 suggest 响应：var cn="sh600519,11,600519,sh600519,贵州茅台,,贵州茅台,99,1,ESG,,";
        /// 条目以 ';' 分隔，字段以 ',' 分隔：
        ///   [1]=类型(11 A股 / 12 B股 / 22&203 ETF / 23 LOF)，[2]=6 位代码，[3]=带前缀符号，[4]=名称
        /// 取 [3] 而不是 [0] 作为代码：北交所条目把名称放在 [0]，符号在 [3]。
        /// </summary>
        public static List<StockBrief> ParseSinaSuggest(string body, int limit)
        {
            List<StockBrief> results = new List<StockBrief>();
            if (string.IsNullOrEmpty(body)) return results;

            int start = body.IndexOf("cn=\"", StringComparison.Ordinal);
            if (start < 0) return results;
            start += 4;
            int end = body.IndexOf('"', start);
            if (end < 0) return results;
            string content = body.Substring(start, end - start);
            if (content.Length == 0) return results;

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            string[] entries = content.Split(';');
            for (int i = 0; i < entries.Length && results.Count < limit; i++)
            {
                if (entries[i].Length == 0) continue;
                string[] f = entries[i].Split(',');
                if (f.Length < 5) continue;

                string type = f[1];
                if (type != "11" && type != "12" && type != "22" && type != "23" && type != "203") continue;

                string code = f[2];
                if (!IsSixDigits(code)) continue;

                string name = f[4];
                if (name.Length == 0) continue;

                string fullCode = f[3];
                if (fullCode.Length == 0) continue;
                if (!seen.Add(fullCode)) continue;

                results.Add(new StockBrief
                {
                    Code = fullCode,
                    Market = "CN",
                    Name = name,
                    Category = Symbols.CnCategory(fullCode)
                });
            }
            return results;
        }

        /// <summary>
        /// 腾讯 smartbox 响应：v_hint="sh~600519~\u8d35\u5dde\u8305\u53f0~gzmt~GP-A^..."
        /// 条目以 '^' 分隔，字段以 '~' 分隔：[0]=交易所 [1]=代码 [2]=名称(\u 转义) [3]=拼音 [4]=类型
        /// </summary>
        public static List<StockBrief> ParseTencentSmartbox(string body, int limit)
        {
            List<StockBrief> results = new List<StockBrief>();
            if (string.IsNullOrEmpty(body)) return results;

            int start = body.IndexOf("v_hint=\"", StringComparison.Ordinal);
            if (start < 0) return results;
            start += 8;
            int end = body.IndexOf('"', start);
            if (end < 0) return results;
            string content = body.Substring(start, end - start);
            if (content.Length == 0 || content == "N") return results;

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            string[] entries = content.Split('^');
            for (int i = 0; i < entries.Length && results.Count < limit; i++)
            {
                if (entries[i].Length == 0) continue;
                string[] f = entries[i].Split('~');
                if (f.Length < 5) continue;

                string type = f[4];
                if (type != "GP-A" && type != "ETF" && type != "LOF") continue;

                string code = f[1];
                if (!IsSixDigits(code)) continue;

                string name = UnescapeUnicode(f[2]);
                if (name.Length == 0) continue;

                string fullCode = f[0] + code;
                if (!seen.Add(fullCode)) continue;

                results.Add(new StockBrief
                {
                    Code = fullCode,
                    Market = "CN",
                    Name = name,
                    Category = Symbols.CnCategory(fullCode)
                });
            }
            return results;
        }

        /// <summary>把 \uXXXX 转义还原为字符（腾讯 smartbox 用 ASCII 转义传中文）。</summary>
        public static string UnescapeUnicode(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length && text[i + 1] == 'u' && i + 5 < text.Length)
                {
                    int code;
                    if (int.TryParse(text.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                    {
                        sb.Append((char)code);
                        i += 5;
                        continue;
                    }
                    sb.Append("\\u");
                    i++;
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
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
