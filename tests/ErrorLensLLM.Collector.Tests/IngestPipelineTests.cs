using ErrorLensLLM.Collector.Ingestion;
using ErrorLensLLM.Core.Ingestion;
using ErrorLensLLM.Core.Models;
using ErrorLensLLM.Core.Processing;

namespace ErrorLensLLM.Collector.Tests;

public class IngestPipelineTests
{
    private readonly IngestPipeline _pipeline = new(new SensitiveDataMasker(["password"]));

    private static IngestBatch Batch(params IngestLogEvent[] events) =>
        new() { Source = new IngestSource { Application = "kayit", StartedAt = DateTimeOffset.UtcNow }, Events = events };

    private static IngestLogEvent Log(string message, string? template = null, IngestException? exception = null) =>
        new() { Timestamp = DateTimeOffset.UtcNow, Level = "Information", Message = message, MessageTemplate = template, Exception = exception };

    private static IngestException Error(string type, string message, string method) =>
        new(type, message, $"{type}: {message}", [new StackFrameInfo("Demo.KayitService", method, "/src/KayitService.cs", 10, "Demo", true)]);

    [Fact]
    public void Personal_data_is_masked_before_anything_else()
    {
        var prepared = _pipeline.Prepare(Batch(Log("Mail gonderilemedi ali@ornek.edu.tr password=gizli")));

        Assert.Equal("Mail gonderilemedi *** password=***", prepared.Events[0].Row.Message);
    }

    [Fact]
    public void Message_template_is_used_when_the_application_supplies_it()
    {
        var prepared = _pipeline.Prepare(Batch(Log("Kayit 42 bulunamadi", template: "Kayit {OgrenciId} bulunamadi")));

        Assert.Equal(("Kayit {OgrenciId} bulunamadi", "template"), (prepared.Events[0].TemplateText, prepared.Events[0].TemplateSource));
    }

    [Fact]
    public void Plain_text_messages_are_templated_with_drain()
    {
        var prepared = _pipeline.Prepare(Batch(Log("Odeme 1201 icin banka yaniti 503"), Log("Odeme 977 icin banka yaniti 504")));

        Assert.All(prepared.Events, e => Assert.Equal("drain", e.TemplateSource));
        Assert.Equal(prepared.Events[0].TemplateText, prepared.Events[1].TemplateText);
        Assert.Equal("Odeme <*> icin banka yaniti <*>", prepared.Events[0].TemplateText);
    }

    [Fact]
    public void Same_error_with_different_data_gets_one_fingerprint()
    {
        var prepared = _pipeline.Prepare(Batch(
            Log("x", exception: Error("Npgsql.PostgresException", "column \"donem_id\" of ogrenci 42 does not exist", "Kaydet")),
            Log("x", exception: Error("Npgsql.PostgresException", "column \"donem_id\" of ogrenci 97 does not exist", "Kaydet")),
            Log("x", exception: Error("System.TimeoutException", "Banka 30 sn icinde yanit vermedi", "Kaydet"))));

        Assert.Equal(prepared.Events[0].Row.Fingerprint, prepared.Events[1].Row.Fingerprint);
        Assert.NotEqual(prepared.Events[0].Row.Fingerprint, prepared.Events[2].Row.Fingerprint);
        Assert.Equal(3, prepared.Errors.Count);
        Assert.Equal("Demo.KayitService.Kaydet", prepared.Errors[0].TopFrame);
    }

    [Fact]
    public void Error_logs_without_an_exception_are_grouped_by_application_and_template()
    {
        IngestLogEvent Rejected(string app, string id) => Log($"Banka odemeyi reddetti: ogrenci {id}", template: "Banka odemeyi reddetti: ogrenci {OgrenciId}") with
        {
            Level = "Error",
            Properties = new Dictionary<string, string> { ["SourceContext"] = "Demo.OdemeService" }
        };

        var odeme = _pipeline.Prepare(Batch(Rejected("odeme", "4"), Rejected("odeme", "9"), Log("Odeme alindi") with { Level = "Warning" }));

        Assert.Equal(2, odeme.Errors.Count);
        Assert.Equal(odeme.Events[0].Row.Fingerprint, odeme.Events[1].Row.Fingerprint);
        Assert.Null(odeme.Events[2].Row.Fingerprint);
        Assert.Equal((IngestPipeline.LoggedError, "Demo.OdemeService"), (odeme.Errors[0].ExceptionType, odeme.Errors[0].TopFrame));
        Assert.Equal(IngestPipeline.LoggedError, odeme.Events[0].Row.ExceptionType);
    }

    [Fact]
    public void Sensitive_properties_are_masked_whole()
    {
        var e = Log("x") with { Properties = new Dictionary<string, string> { ["UserPassword"] = "abc", ["Email"] = "a@b.co", ["Donem"] = "2026-Bahar" } };

        var json = _pipeline.Prepare(Batch(e)).Events[0].Row.PropertiesJson!;

        Assert.Contains("\"UserPassword\":\"***\"", json);
        Assert.Contains("\"Email\":\"***\"", json);
        Assert.Contains("\"Donem\":\"2026-Bahar\"", json);
    }

    [Fact]
    public void Timestamps_are_stored_in_utc_and_unknown_levels_are_normalized()
    {
        var local = new DateTimeOffset(2026, 11, 12, 15, 0, 0, TimeSpan.FromHours(3));
        var prepared = _pipeline.Prepare(Batch(Log("x") with { Timestamp = local, Level = "TRACE" }));

        Assert.Equal(TimeSpan.Zero, prepared.Events[0].Row.Timestamp.Offset);
        Assert.Equal(local, prepared.Events[0].Row.Timestamp);
        Assert.Equal("Information", prepared.Events[0].Row.Level);
    }
}
