namespace get_assessment_no_graph;

public static class HistoricalAnalogueCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var corpus = ReadOption(args, "--corpus");
        var output = ReadOption(args, "--output");
        if (string.IsNullOrWhiteSpace(corpus))
        {
            Console.Error.WriteLine("--analogue-build requires --corpus=<Stage2B corpus JSONL path>.");
            return 2;
        }
        if (string.IsNullOrWhiteSpace(output))
            output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(corpus)) ?? ".", "ava_analogue_index.json");

        return await HistoricalAnalogueIndex.BuildAsync(corpus, output, ct);
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return args[i][(name.Length + 1)..].Trim('"');
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1].Trim('"');
        }
        return null;
    }
}
