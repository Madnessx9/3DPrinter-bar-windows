using System.Text.Json.Nodes;
using BambuBar.Models;
using BambuBar.Protocol;

namespace BambuBar.Tests;

public class ReportParserTests
{
    private static JsonObject Sample() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "p2s-sample.json")))!.AsObject();

    private static ReportDocument SampleDocument()
    {
        var doc = new ReportDocument();
        var sample = Sample();
        Assert.True(doc.Apply((JsonObject)sample["pushall"]!.DeepClone()));
        Assert.True(doc.Apply((JsonObject)sample["get_version"]!.DeepClone()));
        return doc;
    }

    [Fact]
    public void ParsesTheP2SFullReport()
    {
        var doc = SampleDocument();
        Assert.True(doc.HasFullReport);
        var s = ReportParser.Status(doc, HmsCatalog.Shared);
        Assert.Equal(JobState.Printing, s.State);
        Assert.Equal(64, s.Progress);
        Assert.Equal(62, s.RemainingMinutes);
        Assert.Equal(170, s.Layer);
        Assert.Equal(305, s.TotalLayers);
        Assert.Equal("Rear Dry Pod AMS 2 PRO", s.JobName);
        Assert.Equal(SpeedLevel.Standard, s.Speed);
        Assert.Equal(-68, s.WifiSignal);
        Assert.Equal(0.4, s.NozzleDiameter);
        Assert.True(s.Nozzle.Target > 0);
        Assert.True(s.Bed.Target > 0);
        Assert.NotEmpty(s.Ams);
        var first = s.Ams[0].Spools[0];
        Assert.Equal("PLA", first.Material);
        Assert.Equal("FFFFFFFF", first.Color);
        Assert.Equal(41, first.Remain);
        Assert.True(first.IsBambu);
        Assert.NotNull(s.Firmware);
    }

    [Fact]
    public void DeltasMergeIntoTheFullReport()
    {
        var doc = SampleDocument();
        doc.Apply(JsonNode.Parse("""{"print":{"command":"push_status","msg":1,"mc_percent":65,"gcode_state":"PAUSE"}}""")!.AsObject());
        var s = ReportParser.Status(doc, HmsCatalog.Shared);
        Assert.Equal(65, s.Progress);
        Assert.Equal(JobState.Paused, s.State);
        // Untouched fields survive.
        Assert.Equal(305, s.TotalLayers);
    }

    [Fact]
    public void CommandAcknowledgementsDoNotChangeTheReport()
    {
        var doc = SampleDocument();
        Assert.False(doc.Apply(JsonNode.Parse("""{"print":{"command":"pause","sequence_id":"3","result":"success"}}""")!.AsObject()));
    }

    [Fact]
    public void ArraysMergeById()
    {
        var target = JsonNode.Parse("""{"ams":[{"id":"0","humidity":"4","tray":[{"id":"0","remain":50},{"id":"1","remain":20}]}],"other":[1,2]}""")!.AsObject();
        ReportDocument.Merge(target, JsonNode.Parse("""{"ams":[{"id":"0","tray":[{"id":"1","remain":19}]},{"id":"1","humidity":"2"}],"other":[3]}""")!.AsObject());
        Assert.Equal("4", (string?)target["ams"]![0]!["humidity"]);
        Assert.Equal(50, (int)target["ams"]![0]!["tray"]![0]!["remain"]!);
        Assert.Equal(19, (int)target["ams"]![0]!["tray"]![1]!["remain"]!);
        Assert.Equal(2, target["ams"]!.AsArray().Count);
        Assert.Single(target["other"]!.AsArray());
    }

    [Fact]
    public void LightsMergeByNode()
    {
        var target = JsonNode.Parse("""{"lights_report":[{"node":"chamber_light","mode":"off"},{"node":"work_light","mode":"on"}]}""")!.AsObject();
        ReportDocument.Merge(target, JsonNode.Parse("""{"lights_report":[{"node":"chamber_light","mode":"on"}]}""")!.AsObject());
        Assert.Equal("on", (string?)target["lights_report"]![0]!["mode"]);
        Assert.Equal("on", (string?)target["lights_report"]![1]!["mode"]);
    }

    [Fact]
    public void LooseValuesReadAsNumbers()
    {
        Assert.Equal(42, J.Int(JsonValue.Create("42")));
        Assert.Equal(42, J.Int(JsonValue.Create(42.0)));
        Assert.Equal(1.5, J.Double(JsonValue.Create("1.5")));
        Assert.Null(J.Int(JsonValue.Create("abc")));
    }

    [Fact]
    public void StatusesCompareByValue()
    {
        var a = ReportParser.Status(SampleDocument(), HmsCatalog.Shared);
        var b = ReportParser.Status(SampleDocument(), HmsCatalog.Shared);
        b.UpdatedAt = a.UpdatedAt;
        Assert.Equal(a, b);
        var c = a.DeepClone();
        c.Ams[0].Spools[0].Remain = 1;
        Assert.NotEqual(a, c);
        Assert.Equal(41, a.Ams[0].Spools[0].Remain);
    }

    [Fact]
    public void FitDoesNotChangeTheOriginal()
    {
        var a = ReportParser.Status(SampleDocument(), HmsCatalog.Shared);
        var before = a.DeepClone();
        var copy = a with { };
        copy.Fit(PrinterModel.Find("N7", ""));
        Assert.Equal(before, a);
    }
}
