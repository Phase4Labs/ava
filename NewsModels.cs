using System.Text.Json.Serialization;

namespace get_assessment_no_graph;

public sealed class TickerNewsItem
{
    [JsonPropertyName("id")]            public string? Id { get; set; }
    [JsonPropertyName("title")]         public string? Title { get; set; }
    [JsonPropertyName("description")]   public string? Description { get; set; }
    [JsonPropertyName("article_url")]   public string? ArticleUrl { get; set; }
    [JsonPropertyName("published_utc")] public DateTime PublishedUtc { get; set; }
    [JsonPropertyName("publisher")]     public TickerNewsPublisher? Publisher { get; set; }
    [JsonPropertyName("tickers")]       public List<string>? Tickers { get; set; }
    [JsonPropertyName("keywords")]      public List<string>? Keywords { get; set; }
    [JsonPropertyName("insights")]      public List<TickerNewsInsight>? Insights { get; set; }
}

public sealed class TickerNewsPublisher
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class TickerNewsInsight
{
    [JsonPropertyName("ticker")]              public string? Ticker { get; set; }
    [JsonPropertyName("sentiment")]           public string? Sentiment { get; set; }
    [JsonPropertyName("sentiment_reasoning")] public string? SentimentReasoning { get; set; }
}

internal sealed class TickerNewsResponse
{
    [JsonPropertyName("status")]  public string? Status { get; set; }
    [JsonPropertyName("results")] public List<TickerNewsItem>? Results { get; set; }
}
