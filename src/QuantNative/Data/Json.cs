using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using QuantNative.Core;

namespace QuantNative.Data
{
    /// <summary>
    /// 行情 JSON 解析薄封装。
    ///
    /// 用 .NET Framework 内置的 JavaScriptSerializer（System.Web.Extensions）而不是
    /// Newtonsoft / System.Text.Json：前者是框架自带程序集，零 NuGet 依赖、零额外体积；
    /// 后者要么加包（Newtonsoft ~700KB）要么 .NET Core 才有。
    ///
    /// 所有取值都写成「宽容转换」：接口在不同版本下会把同一个字段返回成
    /// 数字 / 字符串 / 整数，这里统一兜住，避免上游格式微调就白屏。
    /// </summary>
    public static class Json
    {
        private static readonly System.Web.Script.Serialization.JavaScriptSerializer Serializer =
            new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };

        /// <summary>解析为 Dictionary / object[] / 基础类型；失败返回 null（调用方按空处理）。</summary>
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                return Serializer.DeserializeObject(text);
            }
            catch (Exception ex)
            {
                Core.Log.Warn("JSON 解析失败: " + ex.Message);
                return null;
            }
        }

        public static Dictionary<string, object> AsDict(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static object[] AsArray(object value)
        {
            return value as object[];
        }

        /// <summary>按 key 取子节点（非字典或缺失返回 null）。</summary>
        public static object Get(object node, string key)
        {
            Dictionary<string, object> dict = node as Dictionary<string, object>;
            if (dict == null) return null;
            object value;
            return dict.TryGetValue(key, out value) ? value : null;
        }

        /// <summary>按路径逐层取子节点，任一层缺失即返回 null。</summary>
        public static object Path(object root, params string[] keys)
        {
            object node = root;
            for (int i = 0; i < keys.Length; i++)
            {
                node = Get(node, keys[i]);
                if (node == null) return null;
            }
            return node;
        }

        /// <summary>取字典里第一个 value（腾讯 K 线的 data 下是以代码为 key 的对象）。</summary>
        public static object FirstValue(object node)
        {
            Dictionary<string, object> dict = node as Dictionary<string, object>;
            if (dict == null) return null;
            foreach (KeyValuePair<string, object> kv in dict) return kv.Value;
            return null;
        }

        public static string Str(object value, string fallback = "")
        {
            if (value == null) return fallback;
            string s = value as string;
            if (s != null) return s;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static double Num(object value, double fallback = 0.0)
        {
            if (value == null) return fallback;
            if (value is double) { double d = (double)value; return double.IsNaN(d) ? fallback : d; }
            if (value is decimal) return (double)(decimal)value;
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            if (value is float) return (float)value;
            string s = value as string;
            if (s != null)
            {
                double parsed;
                if (double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)) return parsed;
                return fallback;
            }
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static long Long(object value, long fallback = 0)
        {
            if (value == null) return fallback;
            if (value is long) return (long)value;
            if (value is int) return (int)value;
            double d = Num(value, double.NaN);
            if (double.IsNaN(d)) return fallback;
            if (d > 9.2e18) return long.MaxValue;
            if (d < -9.2e18) return long.MinValue;
            return (long)d;
        }

        /// <summary>取字符串型数值（接口常见 "10.00" 形式）。</summary>
        public static double NumStr(object value, double fallback = 0.0)
        {
            string s = value as string;
            if (s == null) return value == null ? fallback : Num(value, fallback);
            double parsed;
            if (double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)) return parsed;
            return fallback;
        }

        /// <summary>是否为可解析的数值（用于分钟 K 的 turnover 位置探测）。</summary>
        public static bool TryNumStr(object value, out double result)
        {
            result = 0;
            string s = value as string;
            if (string.IsNullOrEmpty(s)) return false;
            return double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
        }
    }

    /// <summary>
    /// 统一 HTTP 入口。对齐 Rust 端 datasource/headers.rs：
    /// 轮换 User-Agent + 带 Referer + Connection: keep-alive，
    /// 且**不**主动发送 Accept / Accept-Encoding（腾讯 K 线等接口对内容协商敏感）。
    /// 响应用 GBK(936) 解码 —— net48 自带，无需 CodePages 包。
    /// </summary>
    public static class Http
    {
        private static readonly string[] UserAgents =
        {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36"
        };

        private static int _uaIndex = -1;

        // 两条通道都留着：直连和系统代理。
        // 2026-10-23 线上事故：这里原来是 UseProxy=false 硬编码直连，
        // 用户那台机器必须走本地代理（127.0.0.1:10808）才能出网，
        // 结果所有行情都是 10 秒超时（日志里全是「已取消一个任务」），界面一片空白。
        // 现在按系统代理设置选首选通道，失败自动换另一条并记住。
        private static readonly System.Net.Http.HttpClient ProxyClient = CreateClient(true);
        private static readonly System.Net.Http.HttpClient DirectClient = CreateClient(false);
        private static int _preferProxy = -1;   // -1 未定 / 1 系统代理 / 0 直连

        /// <summary>GBK（代码页 936）。新浪 / 腾讯的行情接口都是 GBK 编码。</summary>
        public static readonly System.Text.Encoding Gbk = System.Text.Encoding.GetEncoding(936);

        private static System.Net.Http.HttpClient CreateClient(bool useProxy)
        {
            // UseProxy=true 且 Proxy=null → 走 WebRequest.DefaultWebProxy（IE/系统代理，含 PAC）
            System.Net.Http.HttpClientHandler handler = new System.Net.Http.HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                UseProxy = useProxy,
                Proxy = null,
                AllowAutoRedirect = true
            };
            System.Net.Http.HttpClient client = new System.Net.Http.HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.ConnectionClose = false;
            return client;
        }

        /// <summary>系统里到底配没配代理（ProxyEnable=1 或 PAC）。</summary>
        private static bool SystemProxyConfigured(string url)
        {
            try
            {
                System.Net.IWebProxy proxy = System.Net.WebRequest.DefaultWebProxy;
                if (proxy == null) return false;
                Uri target = new Uri(url);
                Uri routed = proxy.GetProxy(target);
                return routed != null && !routed.Equals(target);
            }
            catch { return false; }
        }

        private static int Preference(string url)
        {
            int decided = System.Threading.Volatile.Read(ref _preferProxy);
            if (decided >= 0) return decided;
            int choice = SystemProxyConfigured(url) ? 1 : 0;
            System.Threading.Interlocked.Exchange(ref _preferProxy, choice);
            Log.Info("行情通道 → " + (choice == 1 ? "系统代理（" + DescribeProxy() + "）" : "直连"));
            return choice;
        }

        private static string DescribeProxy()
        {
            try
            {
                Uri routed = System.Net.WebRequest.DefaultWebProxy.GetProxy(new Uri("https://qt.gtimg.cn/"));
                return routed == null ? "未配置" : routed.Authority;
            }
            catch { return "未知"; }
        }

        private static void RememberChannel(int channel, Exception failed)
        {
            if (System.Threading.Interlocked.Exchange(ref _preferProxy, channel) != channel)
            {
                Log.Warn("行情通道切换 → " + (channel == 1 ? "系统代理" : "直连")
                    + "（另一条失败: " + failed.Message + "）");
            }
        }

        private static async System.Threading.Tasks.Task<byte[]> SendAsync(
            System.Net.Http.HttpClient client, string url, string referer)
        {
            using (System.Net.Http.HttpRequestMessage request =
                new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", NextUserAgent());
                if (!string.IsNullOrEmpty(referer)) request.Headers.TryAddWithoutValidation("Referer", referer);
                request.Headers.TryAddWithoutValidation("Connection", "keep-alive");

                using (System.Net.Http.HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
        }

        private static string NextUserAgent()
        {
            int index = Interlocked.Increment(ref _uaIndex);
            index = index % UserAgents.Length;
            if (index < 0) index += UserAgents.Length;
            return UserAgents[index];
        }

        /// <summary>取原始字节（GBK 接口用）。首选通道失败会自动换另一条再试一次。</summary>
        public static byte[] GetBytes(string url, string referer)
        {
            return GetBytesAsync(url, referer).GetAwaiter().GetResult();
        }

        public static async System.Threading.Tasks.Task<byte[]> GetBytesAsync(string url, string referer)
        {
            int first = Preference(url);
            Exception firstError;
            try
            {
                return await SendAsync(first == 1 ? ProxyClient : DirectClient, url, referer).ConfigureAwait(false);
            }
            catch (Exception ex) { firstError = ex; }

            int second = first == 1 ? 0 : 1;
            try
            {
                byte[] bytes = await SendAsync(second == 1 ? ProxyClient : DirectClient, url, referer).ConfigureAwait(false);
                RememberChannel(second, firstError);
                return bytes;
            }
            catch (Exception secondError)
            {
                throw new InvalidOperationException(
                    (first == 1 ? "代理" : "直连") + "失败: " + firstError.Message
                    + "；" + (second == 1 ? "代理" : "直连") + "也失败: " + secondError.Message, secondError);
            }
        }

        /// <summary>个别目录域名需系统代理才能访问；只在直连失败后使用。</summary>
        public static async System.Threading.Tasks.Task<byte[]> GetBytesViaProxyAsync(string url, string referer)
        {
            using (System.Net.Http.HttpRequestMessage request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", NextUserAgent());
                if (!string.IsNullOrEmpty(referer)) request.Headers.TryAddWithoutValidation("Referer", referer);
                using (System.Net.Http.HttpResponseMessage response = await ProxyClient.SendAsync(request).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>取 GBK 文本。</summary>
        public static async System.Threading.Tasks.Task<string> GetGbkAsync(string url, string referer)
        {
            byte[] bytes = await GetBytesAsync(url, referer).ConfigureAwait(false);
            return Gbk.GetString(bytes);
        }

        /// <summary>取 UTF-8 文本（JSON 接口用）。</summary>
        public static async System.Threading.Tasks.Task<string> GetTextAsync(string url, string referer)
        {
            byte[] bytes = await GetBytesAsync(url, referer).ConfigureAwait(false);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>URL 编码（参数里可能含中文，如搜索关键词）。</summary>
        public static string UrlEncode(string text)
        {
            return Uri.EscapeDataString(text ?? "");
        }
    }
}
