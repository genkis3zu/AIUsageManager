using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIUsageManager;

internal static partial class Tests
{
    static byte[] Join(params byte[][] values) { return values.SelectMany(v=>v).ToArray(); }
    static byte[] Varint(ulong value) { var bytes=new List<byte>();do {byte next=(byte)(value&127);value>>=7;bytes.Add((byte)(next|(value>0?128:0)));}while(value>0);return bytes.ToArray(); }
    static byte[] IntField(int n,ulong value) {return Join(Varint((ulong)n*8),Varint(value));}
    static byte[] MessageField(int n,byte[] value) {return Join(Varint((ulong)n*8+2),Varint((ulong)value.Length),value);}
    static byte[] FloatField(int n,float value) {return Join(Varint((ulong)n*8+5),BitConverter.GetBytes(value));}
    static byte[] TimeField(int n,DateTimeOffset at) {return MessageField(n,IntField(1,(ulong)(at-new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero)).TotalSeconds));}
    static byte[] WebConfig(DateTimeOffset start,DateTimeOffset end,float? percent=null,ulong kind=2)
    {return Join(percent.HasValue?FloatField(1,percent.Value):new byte[0],MessageField(8,Join(IntField(1,kind),TimeField(2,start),TimeField(3,end))));}
    static byte[] Frame(byte flag,byte[] value) {return Join(new[]{flag,(byte)(value.Length>>24),(byte)(value.Length>>16),(byte)(value.Length>>8),(byte)value.Length},value);}
    static byte[] WebReply(byte[] config,string status="0")
    {return Join(Frame(0,MessageField(1,config)),Frame(128,Encoding.ASCII.GetBytes("grpc-status: "+status+"\r\n")));}
    sealed class GrokWebHttp : HttpMessageHandler
    {
        public int WebCalls;
        public bool HeadersCorrect=true;
        public string ProxyPercent="";
        public HttpStatusCode ProxyStatus=HttpStatusCode.OK,WebStatus=HttpStatusCode.OK;
        public string RpcStatus="0",HeaderStatus;
        public bool MismatchedPeriod,Timeout;
        public readonly DateTimeOffset Start=new DateTimeOffset(DateTimeOffset.UtcNow.AddHours(-1).Ticks/TimeSpan.TicksPerSecond*TimeSpan.TicksPerSecond,TimeSpan.Zero);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            if(request.RequestUri.Host=="api.anthropic.com")return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{}")};
            if(request.RequestUri.Host=="cli-chat-proxy.grok.com")return new HttpResponseMessage(ProxyStatus){Content=new StringContent("{\"config\":{"+ProxyPercent+"\"currentPeriod\":{\"type\":\"weekly\",\"start\":\""+Start.ToString("o")+"\",\"end\":\""+Start.AddDays(7).ToString("o")+"\"}}}")};
            WebCalls++;
            HeadersCorrect &= request.RequestUri.AbsoluteUri=="https://grok.com/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig" && request.Method==HttpMethod.Post && request.Headers.Authorization.Parameter=="fixture-grok" && !request.Headers.Contains("Cookie") && request.Content.Headers.ContentType.MediaType=="application/grpc-web+proto";
            HeadersCorrect &= (await request.Content.ReadAsByteArrayAsync()).SequenceEqual(new byte[]{0,0,0,0,2,8,0});
            if(Timeout)throw new TaskCanceledException();
            var webStart=Start.AddMinutes(MismatchedPeriod?5:0);
            var response=new HttpResponseMessage(WebStatus){Content=new ByteArrayContent(WebReply(WebConfig(webStart,webStart.AddDays(7)),RpcStatus))};
            response.Content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc-web+proto");
            if(HeaderStatus!=null)response.Headers.Add("grpc-status",HeaderStatus);
            return response;
        }
    }
    static void RunGrokWebTests()
    {
        Check("Grok web active complete weekly response decodes default zero and published percent",()=> {
            foreach(float? percent in new float?[]{null,0,24.5f,125}) {
                var result=GrokWebBilling.Parse(WebReply(WebConfig(T.AddHours(-1),T.AddDays(7).AddHours(-1),percent)),T);
                Assert(result!=null && result.Percent==(percent??0) && result.End==T.AddDays(7).AddHours(-1));
            }
        });
        Check("Grok web rejects absent inactive monthly or duplicated current periods",()=> {
            var valid=WebConfig(T.AddHours(-1),T.AddDays(7).AddHours(-1));
            foreach(var config in new[]{new byte[0],WebConfig(T.AddDays(-8),T.AddDays(-1)),WebConfig(T.AddDays(1),T.AddDays(8)),WebConfig(T.AddHours(-1),T.AddDays(7).AddHours(-1),null,1),Join(valid,valid),Join(valid,MessageField(1,new byte[0])),Join(valid,FloatField(1,float.NaN))})Assert(GrokWebBilling.Parse(WebReply(config),T)==null);
        });
        Check("Grok web rejects truncated compressed error and duplicate frames",()=> {
            var config=WebConfig(T.AddHours(-1),T.AddDays(7).AddHours(-1));var valid=WebReply(config);
            foreach(var bytes in new[]{valid.Take(valid.Length-1).ToArray(),Frame(0,MessageField(1,config)),WebReply(config,"16"),Join(valid,valid),Join(Frame(1,MessageField(1,config)),Frame(128,Encoding.ASCII.GetBytes("grpc-status: 0\r\n"))),Join(valid,new byte[]{0}),WebReply(Join(config,new byte[]{0})),WebReply(Join(config,new byte[]{128,128,128,128,128,128,128,128,128,2})),WebReply(Join(config,FloatField(1,1),FloatField(1,2)))})Assert(GrokWebBilling.Parse(bytes,T)==null);
        });
        Check("Grok web unknown fields cannot inject percentages",()=> {
            var config=Join(WebConfig(T.AddHours(-1),T.AddDays(7).AddHours(-1)),MessageField(100,FloatField(1,88)));
            var result=GrokWebBilling.Parse(WebReply(config),T);Assert(result!=null && result.Percent==0);
        });
        Check("Grok unreported proxy percentage is confirmed with same login and period without writing files",()=> {
            var root=Fixture();Auth(root);var files=System.IO.Directory.GetFiles(root,"*",System.IO.SearchOption.AllDirectories);var before=files.Select(System.IO.File.ReadAllText).ToArray();
            using(var handler=new GrokWebHttp())using(var http=new HttpClient(handler)) {
                var result=new UsageService(root,http).FetchAllAsync(CancellationToken.None).GetAwaiter().GetResult()[2];
                Assert(result.Windows[0].Percent==0 && result.Status=="" && !result.NeedsLogin && handler.WebCalls==1 && handler.HeadersCorrect);
            }
            Assert(files.Select(System.IO.File.ReadAllText).SequenceEqual(before));
        });
        Check("Grok web failure or mismatched period preserves unknown proxy value",()=> {
            var root=Fixture();Auth(root);
            foreach(var handler in new[]{new GrokWebHttp{WebStatus=HttpStatusCode.Unauthorized},new GrokWebHttp{RpcStatus="16"},new GrokWebHttp{HeaderStatus="16"},new GrokWebHttp{MismatchedPeriod=true},new GrokWebHttp{Timeout=true}})using(handler)using(var http=new HttpClient(handler)) {
                var result=new UsageService(root,http).FetchAllAsync(CancellationToken.None).GetAwaiter().GetResult()[2];Assert(!result.HasData && !result.NeedsLogin && result.Status.Contains("未報告") && handler.WebCalls==1);
            }
        });
        Check("Grok web is not requested for proxy auth failure explicit percent or invalid percent",()=> {
            var root=Fixture();Auth(root);
            foreach(var handler in new[]{new GrokWebHttp{ProxyStatus=HttpStatusCode.Unauthorized},new GrokWebHttp{ProxyPercent="\"creditUsagePercent\":0,"},new GrokWebHttp{ProxyPercent="\"creditUsagePercent\":null,"},new GrokWebHttp{ProxyPercent="\"creditUsagePercent\":\"invalid\","}})using(handler)using(var http=new HttpClient(handler)) {
                new UsageService(root,http).FetchAllAsync(CancellationToken.None).GetAwaiter().GetResult();Assert(handler.WebCalls==0);
            }
        });
    }
}
