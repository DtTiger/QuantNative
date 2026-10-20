using System;
using System.Threading;
using System.Windows.Forms;
using QuantNative.Core;

class AutoAssociationNetworkTests
{
    [STAThread] static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var host = new AppHost(); host.Init(); host.ClearWatchlist();
        // 复位关联状态：上一轮跑完会留下 manual / auto 标记，直接复用会让自动匹配提前退出
        host.Store.Remove("fund_boards.of099998"); host.Store.Remove("fund_boards_mode.of099998");
        host.Store.Remove("fund_boards.of021000"); host.Store.Remove("fund_boards_mode.of021000");
        host.AddWatch(new StockBrief { Code = "of099998", Market = "OF", Name = "人工智能主题混合A" });
        // 场外 QDII 指数基金：A 股板块目录里没有纳斯达克，只能靠海外指数伪板块关联
        host.AddWatch(new StockBrief { Code = "of021000", Market = "OF", Name = "南方纳斯达克100指数发起(QDII)I" });
        host.Start();
        DateTime deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline
            && (host.GetFundBoards("of099998").Count == 0 || host.GetFundBoards("of021000").Count == 0))
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
        host.Hub.Stop();
        var links = host.GetFundBoards("of099998");
        if (links.Count == 0 || links[0].Code != "BK0800")
        {
            Console.WriteLine("FAIL automatic link was not saved");
            return 1;
        }
        Console.WriteLine("PASS newly added fund auto-linked to " + links[0].Name);

        var qdii = host.GetFundBoards("of021000");
        if (qdii.Count == 0 || qdii[0].Code != "ix100.NDX")
        {
            Console.WriteLine("FAIL nasdaq QDII fund left unassociated: "
                + (qdii.Count == 0 ? "none" : qdii[0].Code + " " + qdii[0].Name));
            return 1;
        }
        Console.WriteLine("PASS QDII index fund auto-linked to " + qdii[0].Name + " (" + qdii[0].Code + ")");

        host.SetFundBoards("of099998", new BoardLink[0]);
        if (host.GetSetting("fund_boards_mode.of099998", "") != "manual") return 1;
        Console.WriteLine("PASS manual clear overrides auto linking");
        return 0;
    }
}
