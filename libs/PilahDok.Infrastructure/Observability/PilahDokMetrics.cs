using System.Diagnostics.Metrics;

namespace PilahDok.Infrastructure.Observability;

public sealed class PilahDokMetrics
{
    public const string MeterName = "PilahDok";
    private readonly Counter<long> _uploads;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _cleanups;
    private readonly Histogram<double> _analysisDuration;
    private readonly Histogram<double> _processingDuration;
    private readonly Histogram<long> _outputSize;

    public PilahDokMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _uploads = meter.CreateCounter<long>("pilahdok.uploads");
        _failures = meter.CreateCounter<long>("pilahdok.failures");
        _cleanups = meter.CreateCounter<long>("pilahdok.cleanups");
        _analysisDuration = meter.CreateHistogram<double>("pilahdok.analysis.duration", "s");
        _processingDuration = meter.CreateHistogram<double>("pilahdok.processing.duration", "s");
        _outputSize = meter.CreateHistogram<long>("pilahdok.output.size", "By");
    }

    public void UploadCompleted(long bytes) => _uploads.Add(1, new KeyValuePair<string, object?>("size_bucket", Bucket(bytes)));
    public void Failure(string code) => _failures.Add(1, new KeyValuePair<string, object?>("code", code));
    public void Cleanup() => _cleanups.Add(1);
    public void AnalysisCompleted(TimeSpan duration) => _analysisDuration.Record(duration.TotalSeconds);
    public void ProcessingCompleted(TimeSpan duration, long bytes)
    {
        _processingDuration.Record(duration.TotalSeconds);
        _outputSize.Record(bytes);
    }

    private static string Bucket(long bytes) => bytes switch
    {
        < 1_048_576 => "lt_1mb",
        < 10_485_760 => "1_10mb",
        < 52_428_800 => "10_50mb",
        _ => "gte_50mb"
    };
}
