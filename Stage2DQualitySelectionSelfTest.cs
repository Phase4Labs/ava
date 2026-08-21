namespace get_assessment_no_graph;

public static class Stage2DQualitySelectionSelfTest
{
    public static int Run()
    {
        var raw = new ExecutionCardJsonV1
        {
            SchemaVersion = 1,
            Verdict = "TRADE",
            Scenarios = new()
            {
                // Structurally valid but low R:R => SECONDARY.
                new ExecutionScenarioJsonV1
                {
                    ScenarioRank = 1,
                    Direction = "long",
                    EntryType = "break_hold",
                    EntryLow = 100m,
                    EntryHigh = 101m,
                    StopPrice = 99m,
                    T1 = 102m,
                    ScenarioProb = 0.70m,
                    SuccessProb = 0.65m,
                    Grade = "C"
                },
                // Structurally valid and no selection penalty => PREFERRED.
                new ExecutionScenarioJsonV1
                {
                    ScenarioRank = 2,
                    Direction = "long",
                    EntryType = "break_hold",
                    EntryLow = 100m,
                    EntryHigh = 101m,
                    StopPrice = 99m,
                    T1 = 105m,
                    ScenarioProb = 0.70m,
                    SuccessProb = 0.65m,
                    Grade = "C"
                }
            }
        };

        var originalRawOrder = raw.Scenarios.Select(s => s.ScenarioRank).ToArray();
        var decision = AvaScenarioDecisionLayer.Evaluate(raw, "{}");
        var selection = Stage2DQualitySelector.Select(decision);

        var rawStillUnchanged = raw.Scenarios.Select(s => s.ScenarioRank).SequenceEqual(originalRawOrder);
        var structuralStillUnchanged = decision.Structural.NormalizedCard.Scenarios.Select(s => s.ScenarioRank).SequenceEqual(new[] { 1, 2 });
        var qualityOrderCorrect = selection.QualityExecutionOrder.SequenceEqual(new[] { 2, 1 });
        var ranksPreserved = selection.OrderedExecutableCard.Scenarios.Select(s => s.ScenarioRank).SequenceEqual(new[] { 2, 1 });
        var preferredRank2 = selection.Scenarios.Single(s => s.ScenarioRank == 2).SelectionTier == "PREFERRED";
        var secondaryRank1 = selection.Scenarios.Single(s => s.ScenarioRank == 1).SelectionTier == "SECONDARY";

        var pass =
            decision.Structural.StructurallyValidScenarioCount == 2 &&
            rawStillUnchanged &&
            structuralStillUnchanged &&
            qualityOrderCorrect &&
            ranksPreserved &&
            preferredRank2 &&
            secondaryRank1 &&
            selection.SelectionChanged;

        Console.WriteLine("AVA Stage 2D quality-selection self-test");
        Console.WriteLine($"Structural gate mode : {Stage2B4GateConfig.Label}");
        Console.WriteLine($"Quality select mode  : {Stage2DQualitySelectionConfig.Label}");
        Console.WriteLine($"Raw order            : {string.Join(",", originalRawOrder)}");
        Console.WriteLine($"Structural order     : {string.Join(",", decision.Structural.NormalizedCard.Scenarios.Select(s => s.ScenarioRank))}");
        Console.WriteLine($"Quality order        : {string.Join(",", selection.QualityExecutionOrder)}");
        Console.WriteLine($"Raw card unchanged   : {rawStillUnchanged}");
        Console.WriteLine($"Structural unchanged : {structuralStillUnchanged}");
        Console.WriteLine($"Rank 2 preferred     : {preferredRank2}");
        Console.WriteLine($"Rank 1 secondary     : {secondaryRank1}");
        Console.WriteLine(pass ? "PASS" : "FAIL");
        return pass ? 0 : 1;
    }
}
