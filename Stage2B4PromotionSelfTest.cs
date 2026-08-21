namespace get_assessment_no_graph;

public static class Stage2B4PromotionSelfTest
{
    public static int Run()
    {
        var raw = new ExecutionCardJsonV1
        {
            SchemaVersion = 1,
            Verdict = "TRADE",
            Scenarios = new()
            {
                new ExecutionScenarioJsonV1
                {
                    ScenarioRank = 1,
                    Direction = "long",
                    EntryType = "break_hold",
                    EntryLow = 100m,
                    EntryHigh = 101m,
                    StopPrice = 98m,
                    T1 = 104m,
                    T2 = 108m,
                    Runner = 106m,
                    ScenarioProb = 0.70m,
                    SuccessProb = 0.65m,
                    Grade = "A"
                },
                new ExecutionScenarioJsonV1
                {
                    ScenarioRank = 2,
                    Direction = "long",
                    EntryType = "break_hold",
                    EntryLow = 100m,
                    EntryHigh = 101m,
                    StopPrice = 98m,
                    T1 = 99m,
                    ScenarioProb = 0.70m,
                    SuccessProb = 0.65m,
                    Grade = "A"
                }
            }
        };

        var originalRunner = raw.Scenarios[0].Runner;
        var structural = ScenarioStructuralValidator.Validate(raw);

        var pass =
            structural.EffectiveVerdict == "TRADE" &&
            structural.StructurallyValidScenarioCount == 1 &&
            structural.NormalizedCard.Scenarios.Count == 1 &&
            structural.NormalizedCard.Scenarios[0].ScenarioRank == 1 &&
            structural.NormalizedCard.Scenarios[0].Runner is null &&
            raw.Scenarios.Count == 2 &&
            raw.Scenarios[0].Runner == originalRunner &&
            structural.Scenarios.Single(x => x.ScenarioRank == 2).HardIssues.Any(x => x.Code == "t1_wrong_side");

        Console.WriteLine("AVA Stage 2B.4 promotion self-test");
        Console.WriteLine($"Configured gate mode : {Stage2B4GateConfig.Label}");
        Console.WriteLine($"Raw scenarios        : {raw.Scenarios.Count}");
        Console.WriteLine($"Executable scenarios : {structural.NormalizedCard.Scenarios.Count}");
        Console.WriteLine($"Raw card unchanged   : {raw.Scenarios[0].Runner == originalRunner}");
        Console.WriteLine($"Runner repair        : {structural.NormalizedCard.Scenarios[0].Runner is null}");
        Console.WriteLine($"Hard invalid removed : {!structural.NormalizedCard.Scenarios.Any(x => x.ScenarioRank == 2)}");
        Console.WriteLine(pass ? "PASS" : "FAIL");
        return pass ? 0 : 1;
    }
}