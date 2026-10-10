using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIUsageManager;

internal static partial class Tests
{
    sealed class FakeHttp : HttpMessageHandler
    {
        public int Calls;
        public bool OmitPlan;
        public bool HeadersCorrect = true;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            HeadersCorrect &= request.Method == HttpMethod.Get && request.RequestUri.Scheme == "https" && request.Headers.Authorization.Scheme == "Bearer";
            bool claude = request.RequestUri.Host == "api.anthropic.com";
            HeadersCorrect &= claude ? request.Headers.Contains("anthropic-beta") : request.RequestUri.Host == "cli-chat-proxy.grok.com" && request.Headers.Contains("X-XAI-Token-Auth");
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Status != HttpStatusCode.OK ? "sensitive-server-error" : claude ? "{\"five_hour\":{\"utilization\":1},\"seven_day\":{\"utilization\":98}}" : "{\"subscriptionTier\":"+(OmitPlan?"null":"\"SuperGrok Heavy\"")+",\"config\":{\"creditUsagePercent\":42,\"currentPeriod\":{\"type\":\"weekly\"}}}") });
        }
    }
    static string Fixture()
    {
        var root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-fixtures",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,".codex","sessions"));
        Directory.CreateDirectory(Path.Combine(root,".grok","logs"));
        Directory.CreateDirectory(Path.Combine(root,".claude"));
        return root;
    }
    static string Weekly(double value)
    { return Codex("{\"primary\":{\"window_minutes\":10080,\"used_percent\":"+value+"},\"plan_type\":\"prolite\"}"); }
    static void Auth(string root)
    {
        File.WriteAllText(Path.Combine(root,".claude",".credentials.json"), "{\"claudeAiOauth\":{\"accessToken\":\"fixture-claude\",\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_20x\",\"expiresAt\":4102444800000}}");
        File.WriteAllText(Path.Combine(root,".grok","auth.json"), "{\"https://auth.x.ai::test\":{\"key\":\"fixture-grok\",\"expires_at\":\"2100-01-01T00:00:00Z\"}}");
    }
    static void RunProviderTests()
    {
        Check("Read latest valid record through partial tail", () => {
            var root=Fixture();var file=Path.Combine(root,".codex","sessions","one.jsonl");
            File.WriteAllText(file,Weekly(2)+"\n"+Weekly(50)+"\n{\"partial\":");
            Assert(FileSources.Codex(root).Windows[0].Percent==50);
        });
        Check("Newest empty session falls back", () => {
            var root=Fixture(); var dir=Path.Combine(root,".codex","sessions");
            File.WriteAllText(Path.Combine(dir,"old.jsonl"),Weekly(31));
            File.SetLastWriteTimeUtc(Path.Combine(dir,"old.jsonl"),DateTime.UtcNow.AddHours(-1));
            File.WriteAllText(Path.Combine(dir,"new.jsonl"),"{}\n");
            Assert(FileSources.Codex(root).Windows[0].Percent==31);
        });
        Check("Resumed old session cannot replace newer observation", () => {
            var root=Fixture(); var dir=Path.Combine(root,".codex","sessions");
            File.WriteAllText(Path.Combine(dir,"new-value.jsonl"),Weekly(70).Replace("2026-10-10T09:00:00Z","2026-10-10T10:00:00Z"));
            File.SetLastWriteTimeUtc(Path.Combine(dir,"new-value.jsonl"),DateTime.UtcNow.AddHours(-1));
            File.WriteAllText(Path.Combine(dir,"resumed-old.jsonl"),Weekly(31)+"\n{\"type\":\"response_item\",\"payload\":{}}\n");
            Assert(FileSources.Codex(root).Windows[0].Percent==70);
        });
        Check("Reads a log shared by a running writer", () => {
            var root=Fixture(); var path=Path.Combine(root,".codex","sessions","locked.jsonl"); File.WriteAllText(path,Weekly(0));
            using(var writer=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite)) Assert(FileSources.Codex(root).Windows[0].Percent==0);
        });
        Check("Grok log zero and source timestamp survive", () => {
            var root=Fixture(); File.WriteAllText(Path.Combine(root,".grok","logs","unified.jsonl"),"{\"ts\":\"2026-10-10T09:00:00Z\",\"ctx\":{\"config\":{\"creditUsagePercent\":0,\"currentPeriod\":{\"type\":\"weekly\"}}}}\n{partial");
            var r=FileSources.Grok(root);Assert(r.Windows[0].Percent==0 && r.RecordedAt==T);
        });
        Check("Live providers use GET and preserve service files", () => {
            var root=Fixture();Auth(root); var paths=Directory.GetFiles(root,"*",SearchOption.AllDirectories);var bytes=paths.Select(File.ReadAllText).ToArray();
            using(var handler=new FakeHttp()) using(var http=new HttpClient(handler)) {
                var values=new UsageService(root,http).FetchAllAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(values[0].Windows[1].Percent==98 && values[0].Plan=="Max 20x" && values[2].Windows[0].Percent==42 && handler.Calls==2 && handler.HeadersCorrect);
            }
            Assert(paths.Length==Directory.GetFiles(root,"*",SearchOption.AllDirectories).Length && paths.Select(File.ReadAllText).SequenceEqual(bytes));
        });
        Check("Network error is isolated and never emits server body", () => {
            var root=Fixture();Auth(root);File.WriteAllText(Path.Combine(root,".codex","sessions","one.jsonl"),Weekly(55));
            using(var handler=new FakeHttp { Status=HttpStatusCode.Unauthorized }) using(var http=new HttpClient(handler)) {
                var values=new UsageService(root,http).FetchAllAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(!values[0].HasData && values[1].Windows[0].Percent==55 && !values[2].HasData && values[0].Status.Contains("ログイン") && values.All(v=>!v.Status.Contains("sensitive")));
            }
        });
        Check("Expired login is never refreshed or written", () => {
            var root=Fixture();Auth(root);var path=Path.Combine(root,".claude",".credentials.json");File.WriteAllText(path,"{\"claudeAiOauth\":{\"accessToken\":\"expired\",\"expiresAt\":1000}}");
            using(var handler=new FakeHttp()) using(var http=new HttpClient(handler)) {
                var values=new UsageService(root,http).FetchAllAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(!values[0].HasData && values[0].Status.Contains("ログイン") && handler.Calls==1 && File.ReadAllText(path).Contains("expired"));
            }
        });
        Check("Unrecognized Grok credential issuer ignored", () => {
            var root=Fixture(); File.WriteAllText(Path.Combine(root,".grok","auth.json"),"{\"https://auth.x.ai.evil::test\":{\"key\":\"fixture\"}}");
            using(var handler=new FakeHttp()) using(var http=new HttpClient(handler)) {
                var values=new UsageService(root,http).FetchAllAsync(CancellationToken.None).GetAwaiter().GetResult();Assert(handler.Calls==0 && !values[2].HasData);
            }
        });
        Check("Missing live Grok plan uses labeled log plan", () => {
            var root=Fixture();Auth(root);File.WriteAllText(Path.Combine(root,".grok","logs","unified.jsonl"),"{\"ts\":\"2026-10-10T09:00:00Z\",\"ctx\":{\"subscriptionTier\":\"SuperGrok Heavy\",\"config\":{\"creditUsagePercent\":35,\"currentPeriod\":{\"type\":\"weekly\"}}}}");
            using(var handler=new FakeHttp { OmitPlan=true }) using(var http=new HttpClient(handler)) {
                var values=new UsageService(root,http).FetchAllAsync(CancellationToken.None).GetAwaiter().GetResult();Assert(values[2].Windows[0].Percent==42 && values[2].Plan=="SuperGrok Heavy");
            }
        });
        Check("Malformed credential cannot suppress Codex", () => {
            var root=Fixture();Auth(root);File.WriteAllText(Path.Combine(root,".claude",".credentials.json"),"{\"claudeAiOauth\":{\"accessToken\":\"invalid\\r\\ntoken\"}}");File.WriteAllText(Path.Combine(root,".codex","sessions","one.jsonl"),Weekly(61));
            using(var http=new HttpClient(new FakeHttp())) {var values=new UsageService(root,http).FetchAllAsync(CancellationToken.None).GetAwaiter().GetResult();Assert(!values[0].HasData && values[1].Windows[0].Percent==61);}
        });
        Check("Claude cache fallback preserves observation time", () => {
            var root=Fixture();File.WriteAllText(Path.Combine(root,".claude","usage-cache.json"),"{\"timestamp\":\"2026-10-10T09:00:00Z\",\"rate_limits\":{\"five_hour\":{\"used_percentage\":9}}}");
            using(var http=new HttpClient(new FakeHttp())) {var values=new UsageService(root,http).FetchAllAsync(CancellationToken.None).GetAwaiter().GetResult();Assert(values[0].Windows[0].Percent==9 && values[0].RecordedAt==T && values[0].Source.Contains("キャッシュ"));}
        });
    }
}
