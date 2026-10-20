using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuantNative.Core;
using QuantNative.Data;

class BoardNetworkTests
{
    static int failed;
    static int Main() { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; Log.Init(AppDomain.CurrentDomain.BaseDirectory); Run().GetAwaiter().GetResult(); return failed; }
    static async Task Check(string name, Func<Task<string>> task)
    {
        try { Console.WriteLine("PASS " + name + ": " + await task()); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
    }
    static async Task Run()
    {
        var boards = new BoardClient();
        await Check("search exact BK code",async delegate { var found=await boards.SearchAsync("BK0800"); if(found.Count==0 || found[0].Code!="BK0800")throw new Exception("missing BK0800"); return found[0].Name; });
        await Check("search board by partial name",async delegate { var found=await boards.SearchAsync("人工"); foreach(var link in found)if(!BoardLink.ValidCode(link.Code))throw new Exception("non-board result"); if(found.Count==0)throw new Exception("empty"); return "count="+found.Count; });
        await Check("full industry and concept catalog",async delegate { var found=await boards.GetCatalogAsync();if(found.Count<100)throw new Exception("catalog too small: "+found.Count);return "boards="+found.Count; });
        await Check("automatic fund-name association",async delegate { var found=await boards.SuggestForFundAsync("东方人工智能主题混合A");if(found.Count==0||found[0].Code!="BK0800")throw new Exception("wrong suggestion");return found[0].Name; });
        await Check("exact industry outranks similar concept",async delegate { var found=await boards.SuggestForFundAsync("医疗器械主题混合A");if(found.Count==0||found[0].Code!="BK1041")throw new Exception("wrong industry suggestion: "+(found.Count==0?"empty":found[0].Code));return found[0].Name; });
        await Check("two linked board quotes",async delegate {
            var links=new List<BoardLink>{new BoardLink{Code="BK0800",Name="人工智能"},new BoardLink{Code="BK1041",Name="医疗器械"}};
            await boards.RefreshAsync(links);
            var one=boards.Get("BK0800");var two=boards.Get("BK1041");
            if(one==null || two==null)throw new Exception("one or more boards missing");
            return one.Name+" "+one.ChangePct.ToString("F2")+"% "+one.DataTime.ToString("yyyy-MM-dd")+"; "+two.Name+" "+two.ChangePct.ToString("F2")+"% "+two.DataTime.ToString("yyyy-MM-dd");
        });
        await Check("third live stock quote source",async delegate { var client=new EastmoneyStockQuoteClient();var quotes=await client.FetchAsync(new List<string>{"sh600519","sz000001"});if(quotes.Count!=2||quotes[0].Price<=0||quotes[1].Price<=0)throw new Exception("stock quotes missing");return quotes[0].Name+" "+quotes[0].Price.ToString("F2")+"; "+quotes[1].Name+" "+quotes[1].Price.ToString("F2"); });
        await Check("current board minute trend",async delegate { var trend=await boards.FetchTrendAsync("BK0800"); if(trend.Points.Count==0 && MarketClock.NowCst().TimeOfDay >= TimeSpan.FromHours(9.5))throw new Exception("empty after open");return trend.Name+" "+trend.Date.ToString("yyyy-MM-dd")+" points="+trend.Points.Count; });
        await Check("overseas index board quote",async delegate {
            // QDII 基金关联的纳斯达克100：secid 100.NDX，走海外指数批量通道
            var links=new List<BoardLink>{new BoardLink{Code="ix100.NDX",Name="纳斯达克100"},new BoardLink{Code="ix124.HSTECH",Name="恒生科技指数"}};
            await boards.RefreshAsync(links);
            var ndx=boards.Get("ix100.NDX");var tech=boards.Get("ix124.HSTECH");
            if(ndx==null || tech==null)throw new Exception("overseas index quote missing");
            if(!ndx.IsCurrent(MarketClock.NowCst()))throw new Exception("overnight index treated as stale");
            return ndx.Name+" "+ndx.ChangePct.ToString("F2")+"% "+ndx.DataTime.ToString("yyyy-MM-dd HH:mm")+"; "+tech.Name+" "+tech.ChangePct.ToString("F2")+"%";
        });
        await Check("overseas index minute trend",async delegate {
            var trend=await boards.FetchTrendAsync("ix100.NDX");
            if(trend.Points.Count<30)throw new Exception("trend too short (cross-midnight session dropped?): "+trend.Points.Count);
            return trend.Name+" "+trend.Date.ToString("yyyy-MM-dd")+" points="+trend.Points.Count;
        });
        await Check("holdings based fund estimate",async delegate {
            // 主口径：前十大重仓加权（养基宝同一套算法），不走被限流的 push2
            var fund=new EastmoneyFundSource();
            var stocks=new TencentSource();
            var estimator=new HoldingsEstimator();
            await estimator.RefreshAsync(new List<string>{"of017811"},fund,stocks);
            var estimate=estimator.Get("of017811");
            if(estimate==null||!estimate.HasValue)throw new Exception("no holdings estimate for 017811");
            return estimate.Detail+" => "+estimate.Pct.ToString("F3")+"%";
        });
        await Check("official NAV is kept separate",async delegate {var fund=new EastmoneyFundSource();var q=await fund.FetchRealtimeAsync(new List<string>{"of005844"},"OF");if(q.Count!=1||q[0].PriceKind!="净值")throw new Exception("NAV unavailable or estimated quote used");return q[0].Price.ToString("F4")+" "+q[0].PriceTime;});
        Console.WriteLine("Network failures: "+failed);
    }
}
