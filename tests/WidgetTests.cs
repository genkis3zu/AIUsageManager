using System;
using System.Text;
using System.Web.Script.Serialization;
using AIUsageManager;

internal static partial class Tests
{
    static ServiceUsage[] WidgetValues()
    {
        var values=new[]{ServiceUsage.Empty("Claude",""),ServiceUsage.Empty("Codex",""),ServiceUsage.Empty("Grok","")};
        values[0].Plan="Max 20x";values[0].Windows[0].Percent=1;values[0].Windows[1].Percent=98;
        values[0].Windows[0].ResetsAt=T.AddHours(4);values[0].Windows[1].ResetsAt=T.AddDays(2);
        values[1].Windows[0].Percent=0;
        foreach(var value in values){value.RecordedAt=T.AddHours(-1);value.Source="CLI 記録";}
        return values;
    }
    static void RunWidgetTests()
    {
        Check("Widget login buttons appear only for services needing authentication",()=> {
            var values=WidgetValues();values[1].NeedsLogin=true;values[2].NeedsLogin=true;
            string json=WidgetCards.Create(values,T);Assert(json.Contains("\"verb\":\"login-codex\"")&&json.Contains("\"verb\":\"login-grok\""));
            Assert(!WidgetCards.Create(WidgetValues(),T).Contains("login-"));
        });
        Check("Widget has two independently valid Adaptive Cards",()=> {
            var root=Json.Parse(WidgetCards.Create(WidgetValues(),T));
            Assert(Json.Str(Json.Obj(root,"medium"),"type")=="AdaptiveCard" && Json.Str(Json.Obj(root,"large"),"type")=="AdaptiveCard");
            Assert(root.Count==2);
        });
        Check("Widget keeps missing distinct from acquired zero",()=> {
            string json=WidgetCards.Create(WidgetValues(),T);Assert(json.Contains("未取得")&&json.Contains("0%"));
        });
        Check("Widget includes each provider and both Claude windows",()=> {
            var root=Json.Parse(WidgetCards.Create(WidgetValues(),T));var serializer=new JavaScriptSerializer();
            foreach(string size in new[]{"medium","large"}) {
                string card=serializer.Serialize(root[size]);
                Assert(card.Contains("Claude")&&card.Contains("Codex")&&card.Contains("Grok")&&card.Contains("Max 20x")&&card.Contains("1%")&&card.Contains("98%"));
            }
        });
        Check("Widget high usage uses attention text",()=>Assert(WidgetCards.Create(WidgetValues(),T).Contains("\"color\":\"attention\"")));
        Check("Widget keeps original observation time",()=> {
            string json=WidgetCards.Create(WidgetValues(),T);Assert(json.Contains(T.AddHours(-1).ToLocalTime().ToString("MM/dd HH:mm"))&&json.Contains("CLI 記録"));
        });
        Check("Widget expired data is not reset to zero",()=> {
            var values=WidgetValues();values[2].Windows[0].Percent=100;values[2].Windows[0].ResetsAt=T.AddDays(-1);
            string json=WidgetCards.Create(values,T);Assert(json.Contains("100%")&&json.Contains("期限経過"));
        });
        Check("Widget output escapes untrusted display strings",()=> {
            var values=WidgetValues();values[0].Plan="quote\"\\\nend";
            string json=WidgetCards.Create(values,T);Assert(Json.Parse(json)!=null&&json.Contains("quote\\\"\\\\\\nend"));
        });
        Check("Widget uses inline images and known actions only",()=> {
            string json=WidgetCards.Create(WidgetValues(),T);Assert(json.Contains("data:image/png;base64,")&&json.Contains("\"verb\":\"refresh\"")&&json.Contains("\"verb\":\"open\""));
            Assert(!json.Contains("accessToken")&&!json.Contains("Authorization")&&!json.Contains("api.anthropic.com")&&!json.Contains("Action.OpenUrl"));
        });
        Check("Widget output stays below transport size bound",()=>Assert(Encoding.UTF8.GetByteCount(WidgetCards.Create(WidgetValues(),T))<262144));
    }
}
