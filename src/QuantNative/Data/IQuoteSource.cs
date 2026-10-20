using System.Collections.Generic;
using System.Threading.Tasks;
using QuantNative.Core;

namespace QuantNative.Data
{
    /// <summary>
    /// 数据源抽象（对齐 Rust 端 datasource/mod.rs 的 DataSource trait）。
    /// 所有实现必须返回**已归一化**的数据：Volume=股、Turnover=元。
    /// </summary>
    public interface IQuoteSource
    {
        string Name { get; }
        string DisplayName { get; }

        Task<List<Quote>> FetchRealtimeAsync(IList<string> codes, string market);
        Task<List<IndexQuote>> FetchIndicesAsync();
        Task<List<StockBrief>> SearchAsync(string keyword, string market);
        Task<Depth> FetchDepthAsync(string code, string market);
        Task<List<MinutePoint>> FetchMinuteAsync(string code, string market);
        Task<List<KLineBar>> FetchKLineAsync(string code, string market, ChartPeriod period, int count);
        Task<bool> HealthCheckAsync();
    }
}
