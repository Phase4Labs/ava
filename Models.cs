using System.Text.Json.Serialization;
using System.Text.Json;
namespace get_assessment_no_graph;

public sealed record MinuteBar(
    string Ticker,
    DateTime BarStartUtc,        // inclusive
    DateTime BarCloseUtc,        // bar end timestamp (start + 1min)
    decimal O,
    decimal H,
    decimal L,
    decimal C,
    long V,
    bool IsFinal,
    DateTime ProviderTsUtc,
    string Source = "polygon"
)
{
    // Compatibility alias: many parts of the system use TsUtc to mean "bar start time".
    public DateTime TsUtc => BarStartUtc;
}

public sealed record MinuteBarFeatures(
    string Ticker,
    DateTime TsUtc,     // bar START time (UTC)

    decimal Vwap,
    decimal DistToVwap,
    decimal? DeltaClose,
    decimal? DeltaVwap,

    decimal Body,
    decimal Range,
    decimal UpperWick,
    decimal LowerWick,
    decimal BodyRatio,

    decimal AvgVolume5,
    decimal RelVolume,

    bool AboveVwap,
    bool BelowVwap,
    bool VwapCrossUp,
    bool VwapCrossDown
);

public sealed class MinuteBarRow
{
    [JsonPropertyName("ticker")]
    public string Ticker { get; set; } = "";

    [JsonPropertyName("ts_utc")]
    public DateTime TsUtc { get; set; }

    [JsonPropertyName("o")]
    public decimal O { get; set; }

    [JsonPropertyName("h")]
    public decimal H { get; set; }

    [JsonPropertyName("l")]
    public decimal L { get; set; }

    [JsonPropertyName("c")]
    public decimal C { get; set; }

    [JsonPropertyName("v")]
    public long V { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "polygon";
}


public sealed class MinuteBarFeaturesRow
{
    [JsonPropertyName("ticker")]
    public string Ticker { get; set; } = "";

    [JsonPropertyName("ts_utc")]
    public DateTime TsUtc { get; set; }

    [JsonPropertyName("vwap")]
    public decimal Vwap { get; set; }

    [JsonPropertyName("dist_to_vwap")]
    public decimal DistToVwap { get; set; }

    [JsonPropertyName("delta_close")]
    public decimal? DeltaClose { get; set; }

    [JsonPropertyName("delta_vwap")]
    public decimal? DeltaVwap { get; set; }

    [JsonPropertyName("body")]
    public decimal Body { get; set; }

    [JsonPropertyName("range")]
    public decimal Range { get; set; }

    [JsonPropertyName("upper_wick")]
    public decimal UpperWick { get; set; }

    [JsonPropertyName("lower_wick")]
    public decimal LowerWick { get; set; }

    [JsonPropertyName("body_ratio")]
    public decimal BodyRatio { get; set; }

    [JsonPropertyName("avg_volume_5")]
    public decimal AvgVolume5 { get; set; }

    [JsonPropertyName("rel_volume")]
    public decimal RelVolume { get; set; }

    [JsonPropertyName("above_vwap")]
    public bool AboveVwap { get; set; }

    [JsonPropertyName("below_vwap")]
    public bool BelowVwap { get; set; }

    [JsonPropertyName("vwap_cross_up")]
    public bool VwapCrossUp { get; set; }

    [JsonPropertyName("vwap_cross_down")]
    public bool VwapCrossDown { get; set; }
}


public sealed class AnalysisJobRow
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string Ticker { get; set; } = "";
    public DateTime TsFromUtc { get; set; }
    public DateTime TsToUtc { get; set; }

    public string Status { get; set; } = "queued"; // queued|running|done|error
    public string? ErrorMessage { get; set; }

    public string FrameworkName { get; set; } = "hv_framework_v1";
    public string PromptVersion { get; set; } = "v1";

    public Guid? LatestCardId { get; set; }
}

public sealed class ExecutionCardInsertRow
{
    public Guid Id { get; set; }
    public Guid? JobId { get; set; }
    public string Ticker { get; set; } = "";
    public DateTime AsofTsUtc { get; set; }

    public string Model { get; set; } = "";
    public string? ResponseId { get; set; }
    public string PromptVersion { get; set; } = "v1";

    public object CardJson { get; set; } = new { };
    public string? CardText { get; set; }
}

public sealed class SignalEventRow
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("ticker")] public string Ticker { get; set; } = "";
    [JsonPropertyName("asof_ts_utc")] public DateTime AsofTsUtc { get; set; }
    [JsonPropertyName("scenario_rank")] public int ScenarioRank { get; set; }

    [JsonPropertyName("direction")] public string Direction { get; set; } = ""; // long|short
    [JsonPropertyName("entry_low")] public decimal? EntryLow { get; set; }
    [JsonPropertyName("entry_high")] public decimal? EntryHigh { get; set; }

    [JsonPropertyName("entry_type")] public string EntryType { get; set; } = ""; // reclaim_hold|break_hold|fade_pop

    [JsonPropertyName("stop_price")] public decimal? StopPrice { get; set; }
    [JsonPropertyName("t1")] public decimal? T1 { get; set; }
    [JsonPropertyName("t2")] public decimal? T2 { get; set; }
    [JsonPropertyName("runner")] public decimal? Runner { get; set; }

    [JsonPropertyName("scenario_prob")] public decimal? ScenarioProb { get; set; }
    [JsonPropertyName("success_prob")]  public decimal? SuccessProb  { get; set; }

    [JsonPropertyName("grade")]          public string?  Grade          { get; set; }
    [JsonPropertyName("grade_rationale")] public string? GradeRationale { get; set; }

    [JsonPropertyName("trigger_reason")] public string? TriggerReason { get; set; }
    [JsonPropertyName("triggered")] public bool Triggered { get; set; } = true;
    [JsonPropertyName("event_type")] public string EventType { get; set; } = "entry"; // entry|exit

}

public sealed class TraderStateRow
{
    [JsonPropertyName("ticker")]          public string    Ticker       { get; set; } = "";
    [JsonPropertyName("position")]        public string    Position     { get; set; } = "flat"; // flat|pending|long|short

    [JsonPropertyName("entry_price")]     public decimal?  EntryPrice   { get; set; }
    [JsonPropertyName("stop_price")]      public decimal?  StopPrice    { get; set; }
    [JsonPropertyName("opened_at_utc")]   public DateTime? OpenedAtUtc  { get; set; }

    [JsonPropertyName("t1")]              public decimal?  T1           { get; set; }
    [JsonPropertyName("t2")]              public decimal?  T2           { get; set; }
    [JsonPropertyName("runner")]          public decimal?  Runner       { get; set; }

    [JsonPropertyName("t1_hit")]          public bool      T1Hit        { get; set; }
    [JsonPropertyName("t2_hit")]          public bool      T2Hit        { get; set; }
    [JsonPropertyName("runner_hit")]      public bool      RunnerHit    { get; set; }

    [JsonPropertyName("last_signal_id")]  public Guid?     LastSignalId { get; set; }
    [JsonPropertyName("entry_type")]      public string?   EntryType    { get; set; } // reclaim_hold|break_hold|fade_pop|vwap_reclaim|overextension_fade
    [JsonPropertyName("reeval_at_utc")]   public DateTime? ReevalAtUtc  { get; set; }
    [JsonPropertyName("pending_signal_id")] public Guid?     PendingSignalId { get; set; }
    [JsonPropertyName("pending_since_utc")] public DateTime? PendingSinceUtc { get; set; }

    // ── reeval fields — populated after first re-eval ─────────────
    [JsonPropertyName("reeval_stop")]     public decimal?  ReevalStop   { get; set; }
    [JsonPropertyName("reeval_t1")]       public decimal?  ReevalT1     { get; set; }
    [JsonPropertyName("reeval_t2")]       public decimal?  ReevalT2     { get; set; }
    [JsonPropertyName("reeval_runner")]   public decimal?  ReevalRunner { get; set; }

    // A re-evaluation belongs only to the position that was open when it was
    // produced. This prevents stale reeval_* values from a prior position on
    // the same ticker from affecting exits or appearing as working levels.
    [JsonIgnore]
    public bool HasApplicableReeval
    {
        get
        {
            if (!ReevalAtUtc.HasValue || !OpenedAtUtc.HasValue || !ReevalStop.HasValue)
                return false;

            var opened = EnsureUtc(OpenedAtUtc.Value);
            var openedMinute = new DateTime(
                opened.Year, opened.Month, opened.Day,
                opened.Hour, opened.Minute, 0, DateTimeKind.Utc);
            return EnsureUtc(ReevalAtUtc.Value) >= openedMinute;
        }
    }

    [JsonIgnore] public decimal? EffectiveStop  => HasApplicableReeval ? ReevalStop   : StopPrice;
    [JsonIgnore] public decimal? EffectiveT1    => HasApplicableReeval ? ReevalT1     : T1;
    [JsonIgnore] public decimal? EffectiveT2    => HasApplicableReeval ? ReevalT2     : T2;
    [JsonIgnore] public decimal? EffectiveRunner=> HasApplicableReeval ? ReevalRunner : Runner;

    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc   => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _                  => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

public sealed class PendingClaimResult
{
    [JsonPropertyName("claimed")]   public bool   Claimed  { get; set; }
    [JsonPropertyName("signal_id")] public Guid?  SignalId { get; set; }
    [JsonPropertyName("ticker")]    public string? Ticker  { get; set; }
    [JsonPropertyName("reason")]    public string Reason   { get; set; } = "";
}

public sealed class ReEvalRequestRow
{
    [JsonPropertyName("ticker")] public string Ticker { get; set; } = "";
}
public class ExecutionCardJsonV1
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("verdict")]
    public string Verdict { get; set; } = "NO_TRADE"; // TRADE | NO_TRADE

    [JsonPropertyName("scenarios")]
    public List<ExecutionScenarioJsonV1> Scenarios { get; set; } = new();
}

/*public sealed class ScenarioJsonV1
{
    public int ScenarioRank { get; set; }                 // 1..3
    public string Direction { get; set; } = "";           // "long" | "short"
    public string EntryType { get; set; } = "";           // "reclaim_hold" | "break_hold" | "fade_pop"
    public decimal? ScenarioProb { get; set; }            // 0..1
    public decimal? SuccessProb { get; set; }             // 0..1
    public decimal? EntryLow { get; set; }
    public decimal? EntryHigh { get; set; }
    public decimal? StopPrice { get; set; }
    public decimal? T1 { get; set; }
    public decimal? T2 { get; set; }
    public decimal? Runner { get; set; }
}*/

public sealed class ExecutionScenarioJsonV1
{
    [JsonPropertyName("scenario_rank")]
    public int ScenarioRank { get; set; }  // 1..3

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "long"; // long|short

    [JsonPropertyName("entry_type")]
    public string EntryType { get; set; } = "reclaim_hold"; // reclaim_hold|break_hold|fade_pop|vwap_reclaim

    [JsonPropertyName("scenario_prob")]
    public decimal? ScenarioProb { get; set; } // 0..1

    [JsonPropertyName("success_prob")]
    public decimal? SuccessProb { get; set; } // 0..1

    [JsonPropertyName("entry_low")]
    public decimal? EntryLow { get; set; }

    [JsonPropertyName("entry_high")]
    public decimal? EntryHigh { get; set; }

    [JsonPropertyName("stop_price")]
    public decimal? StopPrice { get; set; }

    [JsonPropertyName("t1")]
    public decimal? T1 { get; set; }

    [JsonPropertyName("t2")]
    public decimal? T2 { get; set; }

    [JsonPropertyName("runner")]
    public decimal? Runner { get; set; }

    [JsonPropertyName("grade")]
    public string? Grade { get; set; }  // A|B|C|D|F

    [JsonPropertyName("grade_rationale")]
    public string? GradeRationale { get; set; }  // one sentence
}

public static class JsonEnvelope
{
    public static bool TryExtractFirstJsonObject(string s, out string json)
    {
        json = "";
        if (string.IsNullOrWhiteSpace(s)) return false;

        int start = -1;
        int depth = 0;
        bool inString = false;
        bool escape = false;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];

            if (start < 0)
            {
                if (c == '{')
                {
                    start = i;
                    depth = 1;
                }
                continue;
            }

            if (inString)
            {
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == '"') inString = false;
                continue;
            }

            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}') depth--;

            if (depth == 0)
            {
                json = s.Substring(start, i - start + 1).Trim();
                return true;
            }
        }

        return false;
    }

    public sealed class ExecutionCardScenarioRow
    {
        public string Ticker { get; set; } = "";
        public DateTime AsofTsUtc { get; set; }
        public int Rank { get; set; }

        public string Direction { get; set; } = "";     // long|short
        public string EntryType { get; set; } = "";     // reclaim_hold|break_hold|fade_pop
        public string? Setup { get; set; }

        public decimal? EntryLow { get; set; }
        public decimal? EntryHigh { get; set; }
        public decimal? Stop { get; set; }
        public decimal? T1 { get; set; }
        public decimal? T2 { get; set; }
        public decimal? Runner { get; set; }
        public decimal? ScenarioProb { get; set; }
        public decimal? SuccessProb { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    /*private sealed class ExecutionCardJsonV1
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("verdict")]
        public string Verdict { get; set; } = "NO_TRADE";

        [JsonPropertyName("scenarios")]
        public List<ScenarioJsonV1> Scenarios { get; set; } = new();
    }*/

    /*private sealed class ScenarioJsonV1
    {
        [JsonPropertyName("scenario_rank")]
        public int ScenarioRank { get; set; }

        [JsonPropertyName("direction")]
        public string Direction { get; set; } = "";

        [JsonPropertyName("entry_type")]
        public string EntryType { get; set; } = "";

        [JsonPropertyName("scenario_prob")]
        public decimal? ScenarioProb { get; set; }

        [JsonPropertyName("success_prob")]
        public decimal? SuccessProb { get; set; }

        [JsonPropertyName("entry_low")]
        public decimal? EntryLow { get; set; }

        [JsonPropertyName("entry_high")]
        public decimal? EntryHigh { get; set; }

        [JsonPropertyName("stop_price")]
        public decimal? StopPrice { get; set; }

        [JsonPropertyName("t1")]
        public decimal? T1 { get; set; }

        [JsonPropertyName("t2")]
        public decimal? T2 { get; set; }

        [JsonPropertyName("runner")]
        public decimal? Runner { get; set; }
    }*/

}

public sealed record DailyBar(
    string   Ticker,
    DateTime Date,
    decimal  O,
    decimal  H,
    decimal  L,
    decimal  C,
    long     V,
    decimal? Vw   // VWAP if returned by Polygon
);
