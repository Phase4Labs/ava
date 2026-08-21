namespace get_assessment_no_graph;

/// <summary>
/// Latest realtime trade/NBBO values exposed only to open-position re-evaluation.
/// The normal scanner scoring path does not consume this record.
/// </summary>
public sealed record ReEvalLiveMarketSnapshot(
    decimal? LastTrade,
    DateTime? LastTradeAtUtc,
    decimal? Bid,
    decimal? Ask,
    DateTime? QuoteAtUtc);
