using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuantNative.Core;
using QuantNative.Data;
class NetworkTests {
 static int failures;
 static int Main(){System.Net.ServicePointManager.SecurityProtocol|=System.Net.SecurityProtocolType.Tls12;Run().GetAwaiter().GetResult();return failures==0?0:1;}
 static async Task Check(string name,Func<Task<string>> test){try{Console.WriteLine("PASS "+name+": "+await test());}catch(Exception ex){System.Threading.Interlocked.Increment(ref failures);Console.WriteLine("FAIL "+name+": "+ex.GetBaseException().Message);}}
 static async Task Run(){
  var fund=new EastmoneyFundSource();
  await Task.WhenAll(
   Check("Tencent quote",async delegate{var q=await new TencentSource().FetchRealtimeAsync(new List<string>{"sh600519"},"CN");if(q.Count==0)throw new Exception("empty");return q[0].Name+" "+q[0].Price;}),
   Check("Sina quote",async delegate{var q=await new SinaSource().FetchRealtimeAsync(new List<string>{"sh600519"},"CN");if(q.Count==0)throw new Exception("empty");return q[0].Name+" "+q[0].Price;}),
   Check("Tencent indices",async delegate{var q=await new TencentSource().FetchIndicesAsync();if(q.Count==0)throw new Exception("empty");return "count="+q.Count;}),
   Check("Fund search",async delegate{var q=await fund.SearchAsync("东方人工智能","OF");if(q.Count==0)throw new Exception("empty");return "count="+q.Count+" "+q[0].Code+" "+q[0].Name;})
  );
  await Check("Fund realtime",async delegate{var q=await fund.FetchRealtimeAsync(new List<string>{"of005844"},"OF");if(q.Count==0)throw new Exception("empty");return q[0].Name+" "+q[0].Price+" "+q[0].PriceKind+" "+q[0].PriceTime;});
  await Check("Fund daily history",async delegate{var q=await fund.FetchKLineAsync("of005844","OF",ChartPeriod.Daily,30);if(q.Count==0)throw new Exception("empty");return "bars="+q.Count+" last="+q[q.Count-1].Date;});
  Console.WriteLine("Network failures: "+failures);
 }
}
