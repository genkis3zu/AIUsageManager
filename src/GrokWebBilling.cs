using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AIUsageManager
{
    internal sealed class GrokWebUsage
    {
        public double Percent { get; set; }
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset End { get; set; }
    }
    internal static class GrokWebBilling
    {
        const int MaximumBytes=1048576;
        static readonly DateTimeOffset Epoch=new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero);
        sealed class Field
        {
            public int Number,Wire;
            public ulong Value;
            public byte[] Bytes;
        }
        static ulong ReadInteger(byte[] bytes,ref int position)
        {
            ulong value=0;
            for(int i=0;i<10;i++) {
                if(position>=bytes.Length)throw new InvalidDataException();
                byte next=bytes[position++];
                if(i==9 && next>1)throw new InvalidDataException();
                value|=(ulong)(next&127)<<(i*7);
                if((next&128)==0)return value;
            }
            throw new InvalidDataException();
        }
        static byte[] Slice(byte[] bytes,ref int position,int length)
        {
            if(length<0 || length>bytes.Length-position)throw new InvalidDataException();
            var result=new byte[length];Buffer.BlockCopy(bytes,position,result,0,length);position+=length;return result;
        }
        static List<Field> Fields(byte[] bytes)
        {
            var result=new List<Field>();int position=0;
            while(position<bytes.Length) {
                if(result.Count>=4096)throw new InvalidDataException();
                ulong tag=ReadInteger(bytes,ref position),number=tag>>3;
                if(number==0 || number>536870911 || (number>=19000 && number<=19999))throw new InvalidDataException();
                var field=new Field {Number=(int)number,Wire=(int)(tag&7)};
                if(field.Wire==0)field.Value=ReadInteger(bytes,ref position);
                else if(field.Wire==2) {
                    ulong length=ReadInteger(bytes,ref position);
                    if(length>(ulong)(bytes.Length-position))throw new InvalidDataException();
                    field.Bytes=Slice(bytes,ref position,(int)length);
                }
                else if(field.Wire==1 || field.Wire==5)field.Bytes=Slice(bytes,ref position,field.Wire==1?8:4);
                else throw new InvalidDataException();
                result.Add(field);
            }
            return result;
        }
        static Field Single(List<Field> fields,int number,int wire,bool required)
        {
            var matches=fields.Where(f=>f.Number==number).ToArray();
            if(matches.Length>1 || (required && matches.Length!=1) || (matches.Length==1 && matches[0].Wire!=wire))throw new InvalidDataException();
            return matches.FirstOrDefault();
        }
        static DateTimeOffset Timestamp(Field field)
        {
            var fields=Fields(field.Bytes);ulong seconds=Single(fields,1,0,true).Value;
            var nanos=Single(fields,2,0,false);
            if(seconds>253402300799UL || (nanos!=null && nanos.Value>=1000000000))throw new InvalidDataException();
            return Epoch.AddTicks((long)seconds*TimeSpan.TicksPerSecond+(long)(nanos==null?0:nanos.Value/100));
        }
        static bool SuccessfulTrailers(byte[] bytes)
        {
            if(bytes.Any(b=>b!=9 && b!=10 && b!=13 && (b<32 || b>126)))return false;
            int statuses=0;
            foreach(string line in Encoding.ASCII.GetString(bytes).Split(new[]{"\r\n"},StringSplitOptions.RemoveEmptyEntries)) {
                int colon=line.IndexOf(':');if(colon<=0)return false;
                if(line.Substring(0,colon).Equals("grpc-status",StringComparison.OrdinalIgnoreCase)) {
                    if(line.Substring(colon+1).Trim()!="0")return false;statuses++;
                }
            }
            return statuses==1;
        }
        public static GrokWebUsage Parse(byte[] bytes,DateTimeOffset now)
        {
            // Only a complete unary gRPC-web response with successful trailers can confirm zero.
            if(bytes==null || bytes.Length>MaximumBytes)return null;
            try {
                int position=0;byte[] payload=null;bool complete=false;
                while(position<bytes.Length) {
                    if(complete || bytes.Length-position<5)return null;
                    byte flags=bytes[position++];uint length=0;
                    for(int i=0;i<4;i++)length=(length<<8)|bytes[position++];
                    if(length>(uint)(bytes.Length-position))return null;
                    var frame=Slice(bytes,ref position,(int)length);
                    if(flags==0 && payload==null)payload=frame;
                    else if(flags==128 && payload!=null && SuccessfulTrailers(frame))complete=true;
                    else return null;
                }
                if(!complete || payload==null)return null;
                var config=Fields(Single(Fields(payload),1,2,true).Bytes);
                var period=Fields(Single(config,8,2,true).Bytes);
                if(Single(period,1,0,true).Value!=2)return null; // Official UsagePeriodType: weekly = 2.
                var start=Timestamp(Single(period,2,2,true));var end=Timestamp(Single(period,3,2,true));
                if(start>now || end<=now || Math.Abs((end-start).TotalDays-7)>=0.1)return null;
                var published=Single(config,1,5,false);
                // Official proto3 descriptor: this implicit-presence float defaults to zero.
                double percent=0;
                if(published!=null) {
                    var raw=published.Bytes;if(!BitConverter.IsLittleEndian)Array.Reverse(raw);
                    percent=BitConverter.ToSingle(raw,0);
                    if(double.IsNaN(percent) || double.IsInfinity(percent) || percent<0 || percent>100000)return null;
                }
                return new GrokWebUsage {Percent=percent,Start=start,End=end};
            }
            catch(InvalidDataException) {return null;}
            catch(ArgumentOutOfRangeException) {return null;}
        }
        public static async Task<double?> ConfirmUnreportedAsync(HttpClient http,string token,string proxyJson,CancellationToken cancellation)
        {
            var config=Json.Obj(Json.Parse(proxyJson),"config");var period=Json.Obj(config,"currentPeriod");
            if(config==null || config.ContainsKey("creditUsagePercent"))return null;
            string kind=Json.Str(period,"type")??Json.Str(period,"periodType");
            if(!string.Equals(kind,"weekly",StringComparison.OrdinalIgnoreCase) && kind!="USAGE_PERIOD_TYPE_WEEKLY")return null;
            var start=Json.Date(Json.Get(period,"start"));var end=Json.Date(Json.Get(period,"end"));var now=DateTimeOffset.UtcNow;
            if(!start.HasValue || !end.HasValue || start>now || end<=now || Math.Abs((end.Value-start.Value).TotalDays-7)>=0.1)return null;
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using(var request=new HttpRequestMessage(HttpMethod.Post,"https://grok.com/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig")) {
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
                request.Headers.Add("Origin","https://grok.com");request.Headers.Referrer=new Uri("https://grok.com/?_s=usage");
                request.Headers.Add("x-grpc-web","1");request.Headers.Add("x-user-agent","connect-es/2.1.1");
                // Read-only query: exclude_legacy_monthly_usage=false, never a mutation or redemption.
                request.Content=new ByteArrayContent(new byte[]{0,0,0,0,2,8,0});
                request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/grpc-web+proto");
                try {
                    using(var response=await http.SendAsync(request,timeout.Token).ConfigureAwait(false)) {
                        if((int)response.StatusCode!=200 || response.Content.Headers.ContentType==null || response.Content.Headers.ContentType.MediaType!="application/grpc-web+proto")return null;
                        IEnumerable<string> status;
                        if(response.Headers.TryGetValues("grpc-status",out status) && (status.Count()!=1 || status.Single().Trim()!="0"))return null;
                        var usage=Parse(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false),DateTimeOffset.UtcNow);
                        if(usage==null || Math.Abs((usage.Start-start.Value).TotalSeconds)>1 || Math.Abs((usage.End-end.Value).TotalSeconds)>1)return null;
                        return usage.Percent;
                    }
                }
                catch(HttpRequestException) {return null;}
                catch(OperationCanceledException) {return null;}
            }
        }
    }
}
