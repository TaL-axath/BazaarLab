using System.Text.Json;
using BazaarLab.Combat;

if (args.Length == 7 && string.Equals(
    args[0], "project-bpp-replay", StringComparison.OrdinalIgnoreCase))
{
    OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(args[1]);
    BppSnapshotValidationReport validation =
        BppSnapshotValidator.ValidateLive(args[2], catalog);
    if (!validation.PredictionReady)
        throw new InvalidDataException(string.Join("; ", validation.Errors));
    BppSnapshotImportResult imported = BppCombatSnapshotAdapter.Import(args[2], catalog);
    CombatSimulationResult simulation = CombatSimulation.Run(
        imported.State, int.Parse(args[3]), int.Parse(args[4]), captureReplayTrace: true);
    LocalReplayProjectionResult replay = LocalReplayProjection.Build(
        imported.BattleId ?? Path.GetFileNameWithoutExtension(args[2]), simulation);
    string json = JsonSerializer.Serialize(replay,
        new JsonSerializerOptions { WriteIndented = true });
    string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(args[5]));
    if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);
    File.WriteAllText(args[5], json + Environment.NewLine);
    File.WriteAllText(args[6], JsonSerializer.Serialize(simulation,
        new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        replay.BattleId,
        replay.FrameCount,
        replay.WinnerId,
        Effects = replay.Frames.Sum(frame => frame.Effects.Count),
        PlayerTransitions = replay.Frames.Sum(frame =>
            frame.PlayerAttributes.Count + frame.PlayerHealth.Count),
        OpponentTransitions = replay.Frames.Sum(frame =>
            frame.OpponentAttributes.Count + frame.OpponentHealth.Count),
        CardTransitions = replay.Frames.Sum(frame => frame.CardAttributes.Count),
    }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

if (args.Length == 3 && string.Equals(
    args[0], "rescore-monte-carlo", StringComparison.OrdinalIgnoreCase))
{
    BppMonteCarloReport input = JsonSerializer.Deserialize<BppMonteCarloReport>(
        File.ReadAllText(args[1]), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException("invalid Monte Carlo report");
    BppMonteCarloReport report = BppMonteCarloDifferential.Rescore(input);
    BppMonteCarloDifferential.Write(args[2], report);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        report.Total,
        report.SamplesPerBattle,
        report.Decided,
        report.Matches,
        report.Accuracy,
        report.ConfidentDecided,
        report.ConfidentMatches,
        report.ConfidentAccuracy,
        report.BrierScore,
        report.LogLoss,
    }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

if (args.Length == 8 && string.Equals(
    args[0], "fit-actual-bpp", StringComparison.OrdinalIgnoreCase))
{
    OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(args[1]);
    ActualCombatSeedFitReport report = ActualCombatSeedFitter.Fit(
        args[2], args[3], catalog, uint.Parse(args[4]), int.Parse(args[5]), int.Parse(args[6]));
    ActualCombatSeedFitter.Write(args[7], report);
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
    {
        WriteIndented = true,
    }));
    return;
}

if (args.Length == 10 && string.Equals(
    args[0], "fit-actual-bpp-adaptive", StringComparison.OrdinalIgnoreCase))
{
    OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(args[1]);
    ActualCombatSeedFitReport report = ActualCombatSeedFitter.FitAdaptive(
        args[2], args[3], catalog, uint.Parse(args[4]), int.Parse(args[5]),
        int.Parse(args[6]), int.Parse(args[7]), int.Parse(args[8]));
    ActualCombatSeedFitter.Write(args[9], report);
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
    {
        WriteIndented = true,
    }));
    return;
}

if (args.Length is 3 or 4 && string.Equals(
    args[0], "compare-actual", StringComparison.OrdinalIgnoreCase))
{
    ActualCombatDifferentialReport report = ActualCombatDifferential.Compare(args[1], args[2]);
    string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    if (args.Length == 4)
    {
        ActualCombatDifferential.Write(args[3], report);
    }
    Console.WriteLine(json);
    return;
}

if (args.Length == 3 && string.Equals(
    args[0], "validate-bpp-live", StringComparison.OrdinalIgnoreCase))
{
    OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(args[1]);
    BppSnapshotValidationReport report = BppSnapshotValidator.ValidateLive(args[2], catalog);
    Console.WriteLine(JsonSerializer.Serialize(report,
        new JsonSerializerOptions { WriteIndented = true }));
    Environment.ExitCode = report.PredictionReady ? 0 : 2;
    return;
}

if (args.Length is 6 or 7 && string.Equals(
    args[0], "predict-bpp", StringComparison.OrdinalIgnoreCase))
{
    OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(args[1]);
    BppPredictionResult prediction = BppMonteCarloDifferential.Predict(
        args[2], catalog, int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]));
    string json = JsonSerializer.Serialize(
        prediction, new JsonSerializerOptions { WriteIndented = true });
    if (args.Length == 7)
    {
        string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(args[6]));
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }
        File.WriteAllText(args[6], json + Environment.NewLine);
    }
    Console.WriteLine(json);
    return;
}

if (args.Length is 8 or 9 && string.Equals(
    args[0], "predict-bpp-adaptive", StringComparison.OrdinalIgnoreCase))
{
    OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(args[1]);
    BppPredictionResult prediction = BppMonteCarloDifferential.PredictAdaptive(
        args[2], catalog, int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]),
        int.Parse(args[6]), int.Parse(args[7]));
    string json = JsonSerializer.Serialize(
        prediction, new JsonSerializerOptions { WriteIndented = true });
    if (args.Length == 9)
    {
        string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(args[8]));
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }
        File.WriteAllText(args[8], json + Environment.NewLine);
    }
    Console.WriteLine(json);
    return;
}

if (args.Length == 4 && string.Equals(args[0], "serve-bpp", StringComparison.OrdinalIgnoreCase))
{
    OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(args[1]);
    int baseSeed = int.Parse(args[2]);
    int maximumTicks = int.Parse(args[3]);
    string? line;
    while ((line = Console.ReadLine()) is not null)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }
        try
        {
            BppSnapshotImportResult imported = BppCombatSnapshotAdapter.ImportJson(line, catalog);
            CombatSimulationResult simulation = CombatSimulation.Run(
                imported.State, baseSeed, maximumTicks);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = true,
                imported.BattleId,
                imported.ActualResult,
                result = simulation,
            }));
        }
        catch (Exception exception)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = false,
                error = exception.GetType().Name + ": " + exception.Message,
            }));
        }
    }
    return;
}

if (args.Length == 5 && string.Equals(
    args[0], "serve-bpp-fixed-files", StringComparison.OrdinalIgnoreCase))
{
    FixedPredictionServer.Run(args[1], int.Parse(args[2]), int.Parse(args[3]),
        int.Parse(args[4]));
    return;
}

if (args.Length == 5 && string.Equals(args[0], "coverage-bpp", StringComparison.OrdinalIgnoreCase))
{
    OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(args[1]);
    SnapshotRuleCoverageReport report = SnapshotRuleCoverage.Analyze(args[2], catalog, args[3]);
    string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(args[4], json + Environment.NewLine);
    Console.WriteLine(json);
    return;
}

if (args.Length == 7 && string.Equals(
    args[0], "monte-carlo-bpp", StringComparison.OrdinalIgnoreCase))
{
    OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(args[1]);
    BppMonteCarloReport report = BppMonteCarloDifferential.Run(
        args[2], catalog, int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]));
    BppMonteCarloDifferential.Write(args[6], report);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        report.Total,
        report.SamplesPerBattle,
        report.Decided,
        report.Matches,
        report.Accuracy,
        report.ConfidentDecided,
        report.ConfidentMatches,
        report.ConfidentAccuracy,
        report.BrierScore,
        report.LogLoss,
        report.UnsupportedActions,
    }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

if (args.Length >= 3 && string.Equals(args[0], "inspect", StringComparison.OrdinalIgnoreCase))
{
    OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(args[1]);
    string tier = args.Length >= 4 ? args[3] : "Diamond";
    string? enchantment = args.Length >= 5 ? args[4] : null;
    MaterializedCardDefinition card = catalog.Get(args[2]).Materialize(tier, enchantment);
    Console.WriteLine(JsonSerializer.Serialize(card, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

if (args.Length == 3 && string.Equals(args[0], "import-bpp", StringComparison.OrdinalIgnoreCase))
{
    OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(args[1]);
    BppSnapshotImportResult imported = BppCombatSnapshotAdapter.Import(args[2], catalog);
    _ = new CombatRuleRuntime(
        imported.State, new XorShiftCombatRandom(SeedMixer.Mix(0, 0)));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        imported.BattleId,
        Combatants = imported.State.Combatants.Select(combatant => new
        {
            combatant.Id,
            Cards = combatant.Cards.Count,
            Hand = combatant.Cards.Count(card => card.Section == "Hand"),
            Skills = combatant.Cards.Count(card => card.Section == "Skills"),
            CardDetails = combatant.Cards.Select(card => new
            {
                card.InstanceId,
                card.Definition.Name,
                card.Definition.Type,
                card.BoardPosition,
                Multicast = card.Attributes.GetValueOrDefault("Multicast"),
                Damage = card.Attributes.GetValueOrDefault("DamageAmount"),
                Shield = card.Attributes.GetValueOrDefault("ShieldApplyAmount"),
                Crit = card.Attributes.GetValueOrDefault("CritChance"),
                Cooldown = card.GetEffectiveCooldownMilliseconds(),
            }),
        }),
        SkippedCards = imported.SkippedCards,
    }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

if (args.Length is 6 or 7 && string.Equals(
    args[0], "simulate-bpp-indexed", StringComparison.OrdinalIgnoreCase))
{
    OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(args[1]);
    BppSnapshotImportResult imported = BppCombatSnapshotAdapter.Import(args[2], catalog);
    CombatSimulationResult simulationResult = CombatSimulation.RunIndexed(
        imported.State, uint.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]));
    string json = JsonSerializer.Serialize(
        simulationResult, new JsonSerializerOptions { WriteIndented = true });
    if (args.Length == 7)
    {
        string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(args[6]));
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }
        File.WriteAllText(args[6], json + Environment.NewLine);
    }
    Console.WriteLine(json);
    return;
}

if (args.Length is 5 or 6 &&
    string.Equals(args[0], "simulate-bpp", StringComparison.OrdinalIgnoreCase))
{
    OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(args[1]);
    BppSnapshotImportResult imported = BppCombatSnapshotAdapter.Import(args[2], catalog);
    CombatSimulationResult simulationResult = CombatSimulation.Run(
        imported.State, int.Parse(args[3]), int.Parse(args[4]));
    string json = JsonSerializer.Serialize(simulationResult, new JsonSerializerOptions { WriteIndented = true });
    if (args.Length == 6)
    {
        File.WriteAllText(args[5], json + Environment.NewLine);
    }
    Console.WriteLine(json);
    return;
}

if (args.Length == 6 && string.Equals(args[0], "batch-bpp", StringComparison.OrdinalIgnoreCase))
{
    OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(args[1]);
    BppDifferentialReport report = BppBatchDifferential.Run(
        args[2], catalog, int.Parse(args[3]), int.Parse(args[4]));
    BppBatchDifferential.Write(args[5], report);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        report.Total,
        report.Decided,
        report.Matches,
        report.Accuracy,
        report.UnsupportedActions,
    }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

if (args.Length != 1 || !string.Equals(args[0], "self-test", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project src/BazaarLab.Combat -- self-test");
    Console.WriteLine("  dotnet run --project src/BazaarLab.Combat -- inspect <cards.jsonl> <template-id> [tier] [enchantment]");
    Console.WriteLine("  dotnet run --project src/BazaarLab.Combat -- import-bpp <cards.jsonl> <snapshot.json>");
    Console.WriteLine("  dotnet run --project src/BazaarLab.Combat -- validate-bpp-live <cards.jsonl> <snapshot.json>");
    Console.WriteLine("  dotnet run --project src/BazaarLab.Combat -- simulate-bpp <cards.jsonl> <snapshot.json> <seed> <ticks> [output.json]");
    Console.WriteLine("  dotnet run --project src/BazaarLab.Combat -- simulate-bpp-indexed <cards.jsonl> <snapshot.json> <master-seed> <run-index> <ticks> [output.json]");
    Console.WriteLine("  dotnet run --project src/BazaarLab.Combat -- batch-bpp <cards.jsonl> <snapshot-directory> <seed> <ticks> <report.json>");
    Console.WriteLine("  dotnet run --project src/BazaarLab.Combat -- coverage-bpp <cards.jsonl> <snapshot-directory> <supported-types.json> <report.json>");
    Console.WriteLine("  dotnet run --project src/BazaarLab.Combat -- monte-carlo-bpp <cards.jsonl> <snapshot-directory> <base-seed> <samples> <ticks> <report.json>");
    Console.WriteLine("  dotnet run --project src/BazaarLab.Combat -- rescore-monte-carlo <input-report.json> <output-report.json>");
    Console.WriteLine("  dotnet run --project src/BazaarLab.Combat -- predict-bpp <cards.jsonl> <snapshot.json> <base-seed> <samples> <ticks> [output.json]");
    Console.WriteLine("  dotnet run --project src/BazaarLab.Combat -- predict-bpp-adaptive <cards.jsonl> <snapshot.json> <base-seed> <minimum-samples> <maximum-samples> <batch-samples> <ticks> [output.json]");
    Console.WriteLine("  dotnet run --project src/BazaarLab.Combat -- serve-bpp <cards.jsonl> <base-seed> <ticks>");
    Console.WriteLine("  dotnet run --project src/BazaarLab.Combat -- compare-actual <actual.json> <simulation.json> [output.json]");
    Console.WriteLine("  dotnet run --project src/BazaarLab.Combat -- fit-actual-bpp <cards.jsonl> <snapshot.json> <actual.json> <base-seed> <samples> <ticks> <report.json>");
    Console.WriteLine("  dotnet run --project src/BazaarLab.Combat -- fit-actual-bpp-adaptive <cards.jsonl> <snapshot.json> <actual.json> <base-seed> <minimum-samples> <maximum-samples> <batch-samples> <ticks> <report.json>");
    return;
}

AssertEqual(0x92CA2F0Eu, SeedMixer.Mix(0, 0), "MixSeed(0,0)");
AssertEqual(0x96A0F96Bu, SeedMixer.Mix(1, 0), "MixSeed(1,0)");
AssertEqual(0x8FC9495Fu, SeedMixer.Mix(123456789, 7), "MixSeed vector");
AssertEqual(true, BppMonteCarloDifferential.ClassifyConfidence(32, 69, 101) is null,
    "draw-heavy samples are not a confident loss");
AssertEqual(true, BppMonteCarloDifferential.ClassifyConfidence(66, 0, 101) is null,
    "raw 65 percent estimate is not statistically confident");
AssertEqual("win", BppMonteCarloDifferential.ClassifyConfidence(80, 0, 101)!,
    "Wilson-supported confident win");
AssertEqual("loss", BppMonteCarloDifferential.ClassifyConfidence(21, 0, 101)!,
    "Wilson-supported confident loss");
AssertEqual(true, BppMonteCarloDifferential.ClassifyConfidence(10, 0, 11) is null,
    "ten of eleven is below Wilson confidence threshold");
AssertEqual("win", BppMonteCarloDifferential.ClassifyConfidence(11, 0, 11)!,
    "unanimous eleven-sample win is Wilson confident");
AssertEqual(0.5, BppMonteCarloDifferential.OutcomeProbability(0, 10, 10),
    "draws contribute neutral binary outcome probability");
AssertEqual(true, ActualCombatSeedFitter.IsExactTraceMatch(
    new ActualCombatSeedCandidate(0, 1, "player", true, 5, 0, 0, 0, 0, 0)),
    "adaptive seed fit stops only on exact projected trace match");
AssertEqual(false, ActualCombatSeedFitter.IsExactTraceMatch(
    new ActualCombatSeedCandidate(0, 1, "player", true, 5, 0, 0, 1, 0, 0.1)),
    "adaptive seed fit does not stop on merely close trace match");

var random = new XorShiftCombatRandom(0x8FC9495F);
foreach (int expected in new[] { 90, 58, 39, 5, 52 })
{
    AssertEqual(expected, random.Next(100), "XorShift vector");
}

var state = new CombatState();
var target = new CombatantState
{
    Id = "target",
    MaxHealth = 100,
    Health = 100,
    Shield = 7,
    DamageReductionPercent = 25,
};
state.Combatants.Add(target);
DamageResult result = CombatEngine.DealDamage(state, target, 20);
AssertEqual(new DamageResult(7, 8), result, "damage split");
AssertEqual(92, target.Health, "damage health");

target.Shield = 3;
target.Health = 100;
target.DamageReductionPercent = 0;
CombatEngine.DealBurnDamage(state, target, 10);
AssertEqual(0, target.Shield, "burn shield");
AssertEqual(96, target.Health, "burn 2:1 shield conversion");

var periodic = new CombatState();
var periodicTarget = new CombatantState
{
    Id = "periodic",
    MaxHealth = 100,
    Health = 50,
    Poison = 4,
    Burn = 10,
    Regen = 3,
};
periodic.Combatants.Add(periodicTarget);
CombatEngine.AdvanceOneTick(periodic);
AssertEqual(39, periodicTarget.Health, "tick-zero poison/burn/regen");
AssertEqual(9, periodicTarget.Burn, "burn decay");

var storm = new CombatState();
storm.Combatants.Add(new CombatantState { Id = "a", MaxHealth = 10, Health = 10 });
storm.Combatants.Add(new CombatantState { Id = "b", MaxHealth = 10, Health = 10 });
CombatEngine.StartSandstorm(storm, forced: true);
AssertEqual(9, storm.Combatants[0].Health, "sandstorm immediate strike");
AssertEqual(240, storm.Sandstorm.IntervalMilliseconds, "sandstorm initial interval");

var automaticStorm = new CombatState { Tick = 600 };
automaticStorm.Combatants.Add(new CombatantState
    { Id = "automatic-a", MaxHealth = 10, Health = 10 });
automaticStorm.Combatants.Add(new CombatantState
    { Id = "automatic-b", MaxHealth = 10, Health = 10 });
CombatEngine.AdvanceOneTick(automaticStorm);
AssertEqual(true, automaticStorm.Sandstorm.Started,
    "natural PvP sandstorm starts at tick 601 (official frame 600)");
AssertEqual(9, automaticStorm.Combatants[0].Health,
    "natural PvP sandstorm applies its immediate opening strike");

string officialCards = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "catalog", "official-cards.jsonl"));
if (File.Exists(officialCards))
{
    OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(officialCards);
    AssertEqual(3289, catalog.Count, "official catalog count");
    using JsonDocument futureRuleDocument = JsonDocument.Parse(
        """{"Trigger":{"$type":"TTriggerOnItemUsed"},"Action":{"$type":"TActionFuturePatch"},"WorksIn":"CombatOnly"}""");
    var futureRuleCard = new MaterializedCardDefinition(
        "future-rule", "Future Rule", "TCardItem", "Small", "Diamond", null,
        new Dictionary<string, int>(), new HashSet<string>(), new HashSet<string>(),
        [new MaterializedEffectDefinition("future", "Ability", "test",
            futureRuleDocument.RootElement.Clone())]);
    AssertEqual("TActionFuturePatch",
        CombatRuleSupport.FindUnsupported(futureRuleCard, "Hand").Single(),
        "unknown combat action is rejected before simulation");
    BppSnapshotValidationReport validLiveSnapshot = BppSnapshotValidator.ValidateLiveJson(
        """
        {"schema":"lookingin-localcombat-bpp-snapshot-v1","combatants":[{"id":"player","hero":"Hero8","attributes":{"Health":100,"HealthMax":100}},{"id":"opponent","hero":"Vanessa","attributes":{"Health":100,"HealthMax":100}}],"card_sets":[{"owner":0,"section":"Hand","items":[]},{"owner":0,"section":"Skills","items":[]},{"owner":1,"section":"Hand","items":[{"instance_id":"valid-aila","template_id":"00ab28d4-c3d2-420e-ba71-b88bc29f4834","tier":"Diamond","enchant":"","size":"Small","socket":"Socket_0","attributes":{"CooldownMax":4000,"Multicast":1,"DamageAmount":80,"Custom_0":40},"tags":["Toy","Weapon","Friend"]}]},{"owner":1,"section":"Skills","items":[]}]}
        """, catalog);
    AssertEqual(true, validLiveSnapshot.PredictionReady,
        "complete live snapshot passes strict validation");
    AssertEqual(1, validLiveSnapshot.Warnings.Count,
        "unknown Hero8 Tempo phase remains a non-fatal warning");
    BppSnapshotValidationReport invalidLiveSnapshot = BppSnapshotValidator.ValidateLiveJson(
        """
        {"schema":"lookingin-localcombat-bpp-snapshot-v1","combatants":[{"id":"player","attributes":{"Health":100}},{"id":"opponent","attributes":{"Health":100,"HealthMax":100}}],"card_sets":[{"owner":4,"items":[{"instance_id":"x","template_id":"missing"}]}]}
        """, catalog);
    AssertEqual(false, invalidLiveSnapshot.PredictionReady,
        "incomplete live snapshot is rejected before prediction");
    AssertEqual(true, invalidLiveSnapshot.Errors.Count >= 3,
        "strict validation reports all independently actionable errors");
    BppSnapshotValidationReport invalidCardPayload = BppSnapshotValidator.ValidateLiveJson(
        """
        {"schema":"lookingin-localcombat-bpp-snapshot-v1","combatants":[{"id":"player","attributes":{"Health":100,"HealthMax":100}},{"id":"opponent","attributes":{"Health":100,"HealthMax":100}}],"card_sets":[{"owner":0,"section":"Hand","items":[{"instance_id":"bad-card","template_id":"00ab28d4-c3d2-420e-ba71-b88bc29f4834","tier":"Obsidian","enchant":"Missing","size":"Huge","socket":"invalid","attributes":{"DamageAmount":1.5},"tags":[1]}]},{"owner":0,"section":"Skills","items":[]},{"owner":1,"section":"Hand","items":[]},{"owner":1,"section":"Skills","items":[]}]}
        """, catalog);
    AssertEqual(false, invalidCardPayload.PredictionReady,
        "malformed live card payload is rejected before import");
    AssertEqual(true, invalidCardPayload.Errors.Count >= 5,
        "tier, enchantment, size, socket, attributes and tags are audited together");
    MaterializedCardDefinition glue = catalog
        .Get("004cb876-1ed2-4a4b-88d4-475cea76a03d")
        .Materialize("Silver", "Toxic");
    AssertEqual(5001, glue.Attributes["CooldownMax"], "tier fallback attribute");
    AssertEqual(16, glue.Attributes["Custom_0"], "tier override attribute");
    AssertEqual(4, glue.Effects.Count, "base plus enchantment effects");
    AssertEqual("TActionCardModifyAttribute", glue.Effects[^1].DefinitionType, "enchantment action type");
    bool rejectedUnknownEnchantment = false;
    try
    {
        _ = catalog.Get("004cb876-1ed2-4a4b-88d4-475cea76a03d")
            .Materialize("Silver", "UnknownEnchantment");
    }
    catch (InvalidDataException)
    {
        rejectedUnknownEnchantment = true;
    }
    AssertEqual(true, rejectedUnknownEnchantment,
        "catalog materialization rejects unknown enchantments instead of silently dropping them");

    var ruleState = new CombatState();
    var rulePlayer = new CombatantState { Id = "player", MaxHealth = 100, Health = 100 };
    var ruleOpponent = new CombatantState { Id = "opponent", MaxHealth = 100, Health = 100 };
    ruleState.Combatants.Add(rulePlayer);
    ruleState.Combatants.Add(ruleOpponent);
    MaterializedCardDefinition aila = catalog
        .Get("00ab28d4-c3d2-420e-ba71-b88bc29f4834")
        .Materialize("Diamond");
    AssertEqual(0, aila.ActivationPriority,
        "standard BuildItem activation priority default");
    CombatCardState ailaState = CombatCardState.Create("aila", aila, rulePlayer, 1);
    ActionExecutionResult damageExecution = CombatActionDispatcher.Execute(
        aila.Effects.Single(effect => effect.Id == "0"),
        new CombatActionContext(
            ruleState,
            ailaState,
            new XorShiftCombatRandom(SeedMixer.Mix(7, 0))));
    AssertEqual(true, damageExecution.Supported, "official damage action supported");
    AssertEqual(20, ruleOpponent.Health, "official DamageAmount action");

    var laneState = new CombatState();
    var lanePlayer = new CombatantState { Id = "lane-player", MaxHealth = 100, Health = 100 };
    var laneOpponent = new CombatantState { Id = "lane-opponent", MaxHealth = 200, Health = 200 };
    laneState.Combatants.Add(lanePlayer);
    laneState.Combatants.Add(laneOpponent);
    CombatCardState laneAila = CombatCardState.Create("lane-aila", aila, lanePlayer, 0);
    MaterializedEffectDefinition laneDamage = aila.Effects.Single(effect => effect.Id == "0");
    laneState.ScheduledRuleEffects.Add(new ScheduledRuleEffect(
        laneAila, laneDamage, 1, laneAila, laneAila, false, null));
    laneState.ScheduledRuleEffects.Add(new ScheduledRuleEffect(
        laneAila, laneDamage, 1, laneAila, laneAila, false, null));
    var laneRuntime = new CombatRuleRuntime(
        laneState, new XorShiftCombatRandom(SeedMixer.Mix(207, 0)));
    laneState.Tick = 1;
    laneRuntime.ProcessScheduledRuleEffects();
    AssertEqual(120, laneOpponent.Health,
        "scheduled effect lane releases only one accumulated instance");
    laneState.Tick = 5;
    laneRuntime.ProcessScheduledRuleEffects();
    AssertEqual(120, laneOpponent.Health,
        "scheduled effect lane holds backlog before five-tick boundary");
    laneState.Tick = 6;
    laneRuntime.ProcessScheduledRuleEffects();
    AssertEqual(40, laneOpponent.Health,
        "scheduled effect lane releases backlog at five-tick boundary");

    CombatCardState glueState = CombatCardState.Create("glue", glue, rulePlayer, 2);
    ActionExecutionResult modifierExecution = CombatActionDispatcher.Execute(
        glue.Effects.Single(effect => effect.Source == "enchantment:Toxic"),
        new CombatActionContext(
            ruleState,
            glueState,
            new XorShiftCombatRandom(SeedMixer.Mix(8, 0)),
            TriggerSource: ailaState));
    AssertEqual(true, modifierExecution.Supported, "official modifier action supported");
    AssertEqual(3, ailaState.Attributes["PoisonApplyAmount"], "reference value modifier rounding");

    ailaState.SetIntrinsicAttribute("Flying", 1);
    var triggerRuntime = new CombatRuleRuntime(
        ruleState,
        new XorShiftCombatRandom(SeedMixer.Mix(9, 0)));
    AssertEqual(40, glueState.Attributes["DamageAmount"], "adjacent card aura application");

    MaterializedCardDefinition growthSpurt = catalog
        .Get("7f803167-d540-4ea9-bd46-9b6bdad687d3")
        .Materialize("Silver");
    var healthAuraState = new CombatState();
    var healthAuraPlayer = new CombatantState
    {
        Id = "health-aura-player", MaxHealth = 2310, Health = 2310,
    };
    healthAuraPlayer.SetIntrinsicAttribute("HealthMax", 2310);
    healthAuraState.Combatants.Add(healthAuraPlayer);
    healthAuraState.Combatants.Add(new CombatantState
    {
        Id = "health-aura-opponent", MaxHealth = 100, Health = 100,
    });
    CombatCardState growthOne = CombatCardState.Create(
        "growth-one", growthSpurt, healthAuraPlayer, 0, "Hand");
    _ = CombatCardState.Create(
        "growth-two", growthSpurt, healthAuraPlayer, 1, "Hand");
    var healthAuras = new CombatAuraRuntime(
        healthAuraState, new XorShiftCombatRandom(SeedMixer.Mix(143, 0)));
    AssertEqual(2, healthAuras.Recompute(), "additive-multiply aura count");
    AssertEqual(2772, healthAuraPlayer.MaxHealth,
        "additive-multiply auras sum their factors instead of compounding");
    healthAuraPlayer.Health = 2000;
    growthOne.IsDisabled = true;
    healthAuras.Recompute();
    AssertEqual(2541, healthAuraPlayer.MaxHealth,
        "additive-multiply aura removal recomputes from intrinsic health");
    AssertEqual(1769, healthAuraPlayer.Health,
        "max-health aura removal preserves missing health");
    growthOne.IsDisabled = false;
    healthAuras.Recompute();
    AssertEqual(2000, healthAuraPlayer.Health,
        "max-health aura restoration preserves missing health");

    var precomputedHealthState = new CombatState { CardAttributesArePrecomputed = true };
    var precomputedHealthPlayer = new CombatantState
    {
        Id = "precomputed-health-player", MaxHealth = 2772, Health = 2772,
    };
    precomputedHealthPlayer.SetIntrinsicAttribute("HealthMax", 2772);
    precomputedHealthState.Combatants.Add(precomputedHealthPlayer);
    precomputedHealthState.Combatants.Add(new CombatantState
    {
        Id = "precomputed-health-opponent", MaxHealth = 100, Health = 100,
    });
    _ = CombatCardState.Create(
        "precomputed-growth-one", growthSpurt, precomputedHealthPlayer, 0, "Hand");
    _ = CombatCardState.Create(
        "precomputed-growth-two", growthSpurt, precomputedHealthPlayer, 1, "Hand");
    var precomputedHealthAuras = new CombatAuraRuntime(
        precomputedHealthState, new XorShiftCombatRandom(SeedMixer.Mix(144, 0)));
    precomputedHealthAuras.Recompute();
    AssertEqual(2310,
        precomputedHealthPlayer.IntrinsicAttributes.GetValueOrDefault("HealthMax"),
        "precomputed additive-multiply aura unbakes the grouped factor");
    AssertEqual(2772, precomputedHealthPlayer.MaxHealth,
        "precomputed additive-multiply aura round-trip");

    MaterializedCardDefinition pawnShop = catalog
        .Get("31b35938-9402-4990-b4d9-473ce5887af9").Materialize("Diamond");
    MaterializedCardDefinition massiveCleaver = catalog
        .Get("1340399f-3cb1-46b6-8aaa-8f7f272a0911").Materialize("Diamond");
    MaterializedCardDefinition scythe = catalog
        .Get("0c710f33-d0fd-40c8-aa2d-34fc20f23140").Materialize("Diamond");
    var staticMonsterAuraState = new CombatState { CardAttributesArePrecomputed = true };
    var staticMonster = new CombatantState
    {
        Id = "static-monster",
        MaxHealth = 1000,
        Health = 1000,
        AttributesArePrecomputed = false,
    };
    staticMonster.SetIntrinsicAttribute("HealthMax", 1000);
    staticMonsterAuraState.Combatants.Add(staticMonster);
    staticMonsterAuraState.Combatants.Add(new CombatantState
    {
        Id = "static-monster-opponent",
        MaxHealth = 3000,
        Health = 3000,
        AttributesArePrecomputed = true,
    });
    CombatCardState staticPawnShop = CombatCardState.Create(
        "static-pawn-shop", pawnShop, staticMonster, 6, "Hand", 3);
    staticPawnShop.SetIntrinsicAttribute("SellPrice", 20);
    staticPawnShop.AttributesArePrecomputed = false;
    CombatCardState healthReferenceWeapon = CombatCardState.Create(
        "health-reference-weapon", massiveCleaver, staticMonster, 0, "Hand", 3);
    healthReferenceWeapon.AttributesArePrecomputed = false;
    CombatCardState enemyHealthReferenceWeapon = CombatCardState.Create(
        "enemy-health-reference-weapon", scythe, staticMonster, 3, "Hand", 3);
    enemyHealthReferenceWeapon.AttributesArePrecomputed = false;
    var staticMonsterAuras = new CombatAuraRuntime(
        staticMonsterAuraState, new XorShiftCombatRandom(SeedMixer.Mix(152, 0)));
    staticMonsterAuras.Recompute();
    AssertEqual(1500, staticMonster.MaxHealth,
        "static monster receives Pawn Shop max-health aura instead of unbaking it");
    AssertEqual(1500, staticMonster.Health,
        "opening max-health aura grants the matching opening health");
    AssertEqual(225, healthReferenceWeapon.Attributes.GetValueOrDefault("DamageAmount"),
        "health-reference weapon sees max-health auras in the same opening pass");
    AssertEqual(1000,
        enemyHealthReferenceWeapon.Attributes.GetValueOrDefault("DamageAmount"),
        "Void Knight Scythe reads one third of the enemy's effective max health");

    var livePawnState = new CombatState { CardAttributesArePrecomputed = true };
    var livePawnOwner = new CombatantState
    {
        Id = "live-pawn-owner",
        MaxHealth = 1500,
        Health = 1500,
        AttributesArePrecomputed = true,
    };
    livePawnOwner.SetIntrinsicAttribute("HealthMax", 1500);
    livePawnState.Combatants.Add(livePawnOwner);
    livePawnState.Combatants.Add(new CombatantState
    {
        Id = "live-pawn-opponent", MaxHealth = 1000, Health = 1000,
        AttributesArePrecomputed = true,
    });
    CombatCardState livePawnShop = CombatCardState.Create(
        "live-pawn-shop", pawnShop, livePawnOwner, 0, "Hand", 3);
    livePawnShop.SetIntrinsicAttribute("SellPrice", 20);
    livePawnShop.AttributesArePrecomputed = true;
    var livePawnAuras = new CombatAuraRuntime(
        livePawnState, new XorShiftCombatRandom(SeedMixer.Mix(153, 0)));
    livePawnAuras.Recompute();
    AssertEqual(1000, livePawnOwner.IntrinsicAttributes.GetValueOrDefault("HealthMax"),
        "live Pawn Shop snapshot unbakes its already-applied max-health aura");
    AssertEqual(1500, livePawnOwner.MaxHealth,
        "live Pawn Shop snapshot does not double-apply max health");

    MaterializedCardDefinition heavyFogshroom = catalog
        .Get("15d84898-4217-4bc3-ae12-7bd70641e646")
        .Materialize("Diamond", "Heavy");
    var fractionalAuraState = new CombatState();
    var fractionalAuraPlayer = new CombatantState
    {
        Id = "fractional-aura-player", MaxHealth = 100, Health = 100,
    };
    fractionalAuraState.Combatants.Add(fractionalAuraPlayer);
    fractionalAuraState.Combatants.Add(new CombatantState
    {
        Id = "fractional-aura-opponent", MaxHealth = 100, Health = 100,
    });
    CombatCardState heavyFogshroomState = CombatCardState.Create(
        "heavy-fogshroom", heavyFogshroom, fractionalAuraPlayer, 0, "Hand");
    var fractionalAuras = new CombatAuraRuntime(
        fractionalAuraState, new XorShiftCombatRandom(SeedMixer.Mix(145, 0)));
    fractionalAuras.Recompute();
    AssertEqual(500, heavyFogshroomState.Attributes.GetValueOrDefault("SlowAmount"),
        "fractional multiply aura preserves its 0.5 factor");

    var precomputedFractionalState = new CombatState { CardAttributesArePrecomputed = true };
    var precomputedFractionalPlayer = new CombatantState
    {
        Id = "precomputed-fractional-player", MaxHealth = 100, Health = 100,
    };
    precomputedFractionalState.Combatants.Add(precomputedFractionalPlayer);
    precomputedFractionalState.Combatants.Add(new CombatantState
    {
        Id = "precomputed-fractional-opponent", MaxHealth = 100, Health = 100,
    });
    CombatCardState precomputedHeavyFogshroom = CombatCardState.Create(
        "precomputed-heavy-fogshroom", heavyFogshroom,
        precomputedFractionalPlayer, 0, "Hand");
    precomputedHeavyFogshroom.SetIntrinsicAttribute("SlowAmount", 500);
    var precomputedFractionalAuras = new CombatAuraRuntime(
        precomputedFractionalState, new XorShiftCombatRandom(SeedMixer.Mix(146, 0)));
    precomputedFractionalAuras.Recompute();
    AssertEqual(1000,
        precomputedHeavyFogshroom.IntrinsicAttributes.GetValueOrDefault("SlowAmount"),
        "precomputed fractional multiply aura unbakes by division");
    AssertEqual(500,
        precomputedHeavyFogshroom.Attributes.GetValueOrDefault("SlowAmount"),
        "precomputed fractional multiply aura round-trip");

    int triggeredEffects = triggerRuntime.FireCard(glueState);
    AssertEqual(2, triggeredEffects, "OnCardFired to attribute-change chain");
    AssertEqual(0, ailaState.Attributes["Flying"], "positional flying stop");
    AssertEqual(96, ailaState.Attributes["DamageAmount"], "attribute-change trigger modifier");

    MaterializedCardDefinition wirelessHeadset = catalog
        .Get("ea03ebf9-d6c3-4f03-875e-ae1a75931d60")
        .Materialize("Bronze", "Deadly");
    var neighborState = new CombatState();
    var neighborPlayer = new CombatantState { Id = "neighbor-player", MaxHealth = 100, Health = 100 };
    var neighborOpponent = new CombatantState { Id = "neighbor-opponent", MaxHealth = 100, Health = 100 };
    neighborState.Combatants.Add(neighborPlayer);
    neighborState.Combatants.Add(neighborOpponent);
    CombatCardState farNeighborCard = CombatCardState.Create(
        "neighbor-far", aila, neighborPlayer, 0, "Hand", 1);
    CombatCardState leftNeighborCard = CombatCardState.Create(
        "neighbor-left", aila, neighborPlayer, 3, "Hand", 2);
    CombatCardState wirelessState = CombatCardState.Create(
        "neighbor-source", wirelessHeadset, neighborPlayer, 5, "Hand", 1);
    CombatCardState rightNeighborCard = CombatCardState.Create(
        "neighbor-right", aila, neighborPlayer, 6, "Hand", 2);
    CombatCardState stashNeighborCard = CombatCardState.Create(
        "neighbor-stash", aila, neighborPlayer, 5, "Stash", 1);
    var neighborRules = new CombatRuleRuntime(
        neighborState, new XorShiftCombatRandom(SeedMixer.Mix(141, 0)));
    AssertEqual(1, neighborRules.FireCard(wirelessState),
        "random self-neighbor action count");
    AssertEqual(1000,
        leftNeighborCard.Attributes.GetValueOrDefault("Haste") +
        rightNeighborCard.Attributes.GetValueOrDefault("Haste"),
        "random neighbors use occupied socket boundaries");
    AssertEqual(0, farNeighborCard.Attributes.GetValueOrDefault("Haste"),
        "random neighbors exclude non-adjacent card");
    AssertEqual(0, stashNeighborCard.Attributes.GetValueOrDefault("Haste"),
        "random neighbors exclude other container");
    using JsonDocument selfBoardTargetDocument = JsonDocument.Parse("""
        {"$type":"TActionCardHaste","TargetCount":{"$type":"TFixedValue","Value":99},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfBoard","ExcludeSelf":false,"Conditions":null}}
        """);
    List<CombatCardState> selfBoardTargets = TargetResolver.ResolveCards(
        selfBoardTargetDocument.RootElement,
        new CombatActionContext(neighborState, wirelessState,
            new XorShiftCombatRandom(SeedMixer.Mix(142, 0))));
    AssertEqual(false, selfBoardTargets.Contains(stashNeighborCard),
        "self board excludes stash");

    MaterializedCardDefinition raven = catalog
        .Get("08ebe48b-29d0-4129-952d-7d140e54e7c5")
        .Materialize("Diamond");
    CombatCardState ravenState = CombatCardState.Create("raven", raven, rulePlayer, 4);
    ailaState.SetIntrinsicAttribute("Flying", 1);
    triggeredEffects = triggerRuntime.FireCard(glueState);
    AssertEqual(4, triggeredEffects, "card fired plus global item-used triggers");
    AssertEqual(2000, ravenState.Attributes["Charge"], "item-used charge action");
    AssertEqual(1, ravenState.Attributes["Flying"], "item-used flying start action");
    using JsonDocument repeatedFlyingStartDocument = JsonDocument.Parse("""
        {"Action":{"$type":"TActionCardFlyingStart","Target":{"$type":"TTargetCardSelf","Conditions":null}}}
        """);
    var repeatedFlyingStart = new MaterializedEffectDefinition(
        "repeat-flying-start", "Ability", "test",
        repeatedFlyingStartDocument.RootElement.Clone());
    int flyingEventsBefore = ruleState.Events.Count(value =>
        value.Kind == "CardFlying" && value.TargetId == "raven");
    int repeatedFlyingCallbacks = 0;
    ActionExecutionResult repeatedFlyingResult = CombatActionDispatcher.Execute(
        repeatedFlyingStart,
        new CombatActionContext(
            ruleState,
            ravenState,
            new XorShiftCombatRandom(SeedMixer.Mix(211, 0)),
            CardActionApplied: (_, _) => repeatedFlyingCallbacks++));
    AssertEqual(0, repeatedFlyingResult.TargetCount,
        "repeated flying start has no affected target");
    AssertEqual(flyingEventsBefore, ruleState.Events.Count(value =>
        value.Kind == "CardFlying" && value.TargetId == "raven"),
        "repeated flying start emits no lifecycle event");
    AssertEqual(0, repeatedFlyingCallbacks,
        "repeated flying start emits no performed callback");

    var schedulerState = new CombatState();
    var schedulerPlayer = new CombatantState { Id = "scheduler-player", MaxHealth = 100, Health = 100 };
    var schedulerOpponent = new CombatantState { Id = "scheduler-opponent", MaxHealth = 100, Health = 100 };
    schedulerState.Combatants.Add(schedulerPlayer);
    schedulerState.Combatants.Add(schedulerOpponent);
    CombatCardState scheduledAila = CombatCardState.Create(
        "scheduled-aila", aila, schedulerPlayer, 0);
    scheduledAila.SetIntrinsicAttribute("Haste", 4000);
    var schedulerRules = new CombatRuleRuntime(
        schedulerState,
        new XorShiftCombatRandom(SeedMixer.Mix(10, 0)));
    var scheduler = new CombatScheduler(schedulerState, schedulerRules);
    scheduler.StartFight();
    int scheduledFires = 0;
    for (int tick = 0; tick < 40; tick++)
    {
        scheduledFires += scheduler.AdvanceOneTick();
    }
    AssertEqual(1, scheduledFires, "hasted cooldown scheduler fire count");
    AssertEqual(100, schedulerOpponent.Health,
        "hasted cooldown scheduler defers medium action from its fire tick");
    AssertEqual(2000, scheduledAila.Attributes["Haste"], "haste duration decay");
    scheduler.AdvanceOneTick();
    AssertEqual(20, schedulerOpponent.Health,
        "hasted cooldown scheduler action executes in next scheduled phase");

    static MaterializedCardDefinition SchedulerTestCard(string id, int priority, int cooldown) =>
        new(id, id, "TCardItem", "Small", "Diamond", null,
            new Dictionary<string, int>
            {
                ["CooldownMax"] = cooldown,
                ["Multicast"] = 1,
            },
            new HashSet<string>(), new HashSet<string>(),
            Array.Empty<MaterializedEffectDefinition>(), priority);

    var orderState = new CombatState();
    var orderPlayer = new CombatantState { Id = "order-player", MaxHealth = 100, Health = 100 };
    var orderOpponent = new CombatantState { Id = "order-opponent", MaxHealth = 100, Health = 100 };
    orderState.Combatants.Add(orderPlayer);
    orderState.Combatants.Add(orderOpponent);
    CombatCardState.Create("z-owner0-pos1", SchedulerTestCard("z", 0, 50), orderPlayer, 1);
    CombatCardState.Create("b-owner0-pos0", SchedulerTestCard("b", 0, 50), orderPlayer, 0);
    CombatCardState.Create("a-owner0-pos0", SchedulerTestCard("a", 0, 50), orderPlayer, 0);
    CombatCardState.Create("owner1-pos0", SchedulerTestCard("owner1", 0, 50), orderOpponent, 0);
    CombatCardState.Create("priority-first", SchedulerTestCard("priority", 10, 50), orderOpponent, 7);
    var orderRules = new CombatRuleRuntime(
        orderState, new XorShiftCombatRandom(SeedMixer.Mix(15, 0)));
    var orderScheduler = new CombatScheduler(orderState, orderRules);
    orderScheduler.StartFight();
    AssertEqual(5, orderScheduler.AdvanceOneTick(), "same-tick scheduler fire count");
    string[] useOrder = orderState.Events
        .Where(value => value.Kind == "CardUsed")
        .Select(value => value.TargetId ?? string.Empty)
        .ToArray();
    AssertEqual("priority-first,a-owner0-pos0,b-owner0-pos0,z-owner0-pos1,owner1-pos0",
        string.Join(',', useOrder), "worker activation ordering");

    using JsonDocument sameTickDisableDocument = JsonDocument.Parse("""
        {"Id":"same-tick-disable","Trigger":{"$type":"TTriggerOnCardFired"},"Action":{"$type":"TActionCardDisable","TargetCount":{"$type":"TFixedValue","Value":1},"Target":{"$type":"TTargetCardRandom","TargetSection":"OpponentHand","ExcludeSelf":false,"Conditions":null}},"Prerequisites":null,"Priority":"Immediate"}
        """);
    MaterializedCardDefinition disablingDefinition = SchedulerTestCard(
        "same-tick-disabler", 10, 50) with
    {
        Effects = new[]
        {
            new MaterializedEffectDefinition("same-tick-disable", "Ability", "test",
                sameTickDisableDocument.RootElement.Clone()),
        },
    };
    var cancellationState = new CombatState();
    var cancellationPlayer = new CombatantState
        { Id = "cancellation-player", MaxHealth = 100, Health = 100 };
    var cancellationOpponent = new CombatantState
        { Id = "cancellation-opponent", MaxHealth = 100, Health = 100 };
    cancellationState.Combatants.Add(cancellationPlayer);
    cancellationState.Combatants.Add(cancellationOpponent);
    CombatCardState.Create("same-tick-disabler", disablingDefinition, cancellationPlayer, 0);
    CombatCardState cancelledCard = CombatCardState.Create(
        "same-tick-cancelled", SchedulerTestCard("same-tick-cancelled", 0, 50),
        cancellationOpponent, 0);
    var cancellationRules = new CombatRuleRuntime(
        cancellationState, new XorShiftCombatRandom(SeedMixer.Mix(151, 0)));
    var cancellationScheduler = new CombatScheduler(cancellationState, cancellationRules);
    cancellationScheduler.StartFight();
    AssertEqual(1, cancellationScheduler.AdvanceOneTick(),
        "same-tick mutation cancels later ready card");
    AssertEqual(true, cancelledCard.IsDisabled, "same-tick later card disabled");
    AssertEqual(1, cancellationState.Events.Count(value => value.Kind == "CardUsed"),
        "same-tick cancelled card has no use event");

    var overshootState = new CombatState();
    var overshootPlayer = new CombatantState { Id = "overshoot-player", MaxHealth = 100, Health = 100 };
    overshootState.Combatants.Add(overshootPlayer);
    CombatCardState overshootCard = CombatCardState.Create(
        "overshoot-card", SchedulerTestCard("overshoot", 0, 75), overshootPlayer, 0);
    var overshootRules = new CombatRuleRuntime(
        overshootState, new XorShiftCombatRandom(SeedMixer.Mix(16, 0)));
    var overshootScheduler = new CombatScheduler(overshootState, overshootRules);
    overshootScheduler.StartFight();
    AssertEqual(0, overshootScheduler.AdvanceOneTick(), "partial cooldown tick");
    AssertEqual(1, overshootScheduler.AdvanceOneTick(), "overshoot cooldown fire");
    AssertEqual(75, overshootCard.CooldownRemainingMilliseconds,
        "worker discards cooldown overshoot");

    var phaseState = new CombatState();
    var phasePlayer = new CombatantState { Id = "phase-player", MaxHealth = 100, Health = 100 };
    phaseState.Combatants.Add(phasePlayer);
    CombatCardState phaseForced = CombatCardState.Create(
        "phase-forced", SchedulerTestCard("phase-forced", 0, 1000), phasePlayer, 0);
    CombatCardState.Create(
        "phase-normal", SchedulerTestCard("phase-normal", 0, 50), phasePlayer, 1);
    phasePlayer.Poison = 1;
    var phaseRules = new CombatRuleRuntime(
        phaseState, new XorShiftCombatRandom(SeedMixer.Mix(161, 0)));
    var phaseScheduler = new CombatScheduler(phaseState, phaseRules);
    phaseScheduler.StartFight();
    phaseState.ScheduledForceUses.Add(new ScheduledForceUse(phaseForced, 1));
    phaseScheduler.AdvanceOneTick();
    string tickOnePhaseOrder = string.Join(',', phaseState.Events
        .Where(value => value.Tick == 1 && value.Kind == "CardUsed")
        .Select(value => value.TargetId));
    AssertEqual("phase-forced,phase-normal", tickOnePhaseOrder,
        "tick-one scheduled effects precede item advance");
    int tickOneLastUse = phaseState.Events.FindLastIndex(value =>
        value.Tick == 1 && value.Kind == "CardUsed");
    int tickOnePoison = phaseState.Events.FindIndex(value =>
        value.Tick == 1 && value.Kind == "Poison");
    AssertEqual(true, tickOneLastUse >= 0 && tickOneLastUse < tickOnePoison,
        "item advance precedes periodic effects");
    phaseState.ScheduledForceUses.Add(new ScheduledForceUse(phaseForced, 2));
    phaseScheduler.AdvanceOneTick();
    string laterPhaseOrder = string.Join(',', phaseState.Events
        .Where(value => value.Tick == 2 && value.Kind == "CardUsed")
        .Select(value => value.TargetId));
    AssertEqual("phase-normal,phase-forced", laterPhaseOrder,
        "later item advance precedes scheduled effects");

    var sandstormPhaseState = new CombatState();
    sandstormPhaseState.Combatants.Add(new CombatantState
        { Id = "sandstorm-phase-player", MaxHealth = 100, Health = 100 });
    CombatEngine.StartSandstorm(sandstormPhaseState, forced: true);
    var sandstormPhaseRules = new CombatRuleRuntime(
        sandstormPhaseState, new XorShiftCombatRandom(SeedMixer.Mix(162, 0)));
    var sandstormPhaseScheduler = new CombatScheduler(sandstormPhaseState, sandstormPhaseRules);
    sandstormPhaseScheduler.StartFight();
    sandstormPhaseScheduler.AdvanceOneTick();
    AssertEqual(0, sandstormPhaseState.Sandstorm.ElapsedMilliseconds,
        "tick-one branch skips sandstorm advance");
    sandstormPhaseScheduler.AdvanceOneTick();
    AssertEqual(50, sandstormPhaseState.Sandstorm.ElapsedMilliseconds,
        "later tick advances sandstorm clock");

    using JsonDocument mediumPriorityDocument = JsonDocument.Parse("""
        {"Id":"medium-priority","Trigger":{"$type":"TTriggerOnCardFired"},"Action":{"$type":"TActionPlayerDamage","ReferenceValue":{"$type":"TFixedValue","Value":10},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent","Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    MaterializedCardDefinition mediumPriorityDefinition = SchedulerTestCard(
        "medium-priority", 0, 50) with
    {
        Effects = new[]
        {
            new MaterializedEffectDefinition("medium-priority", "Ability", "test",
                mediumPriorityDocument.RootElement.Clone()),
        },
    };
    var priorityState = new CombatState();
    var priorityPlayer = new CombatantState { Id = "priority-player", MaxHealth = 100, Health = 100 };
    var priorityOpponent = new CombatantState { Id = "priority-opponent", MaxHealth = 100, Health = 100 };
    priorityState.Combatants.Add(priorityPlayer);
    priorityState.Combatants.Add(priorityOpponent);
    CombatCardState.Create("medium-priority-card", mediumPriorityDefinition, priorityPlayer, 0);
    var priorityRules = new CombatRuleRuntime(
        priorityState, new XorShiftCombatRandom(SeedMixer.Mix(163, 0)));
    var priorityScheduler = new CombatScheduler(priorityState, priorityRules);
    priorityScheduler.StartFight();
    priorityScheduler.AdvanceOneTick();
    AssertEqual(100, priorityOpponent.Health,
        "non-immediate effect is deferred from its use frame");
    AssertEqual(1, priorityState.ScheduledRuleEffects.Count,
        "medium-priority effect queued for next tick");
    priorityScheduler.AdvanceOneTick();
    AssertEqual(90, priorityOpponent.Health,
        "medium-priority effect executes in next scheduled phase");

    using JsonDocument immediatePriorityDocument = JsonDocument.Parse(
        mediumPriorityDocument.RootElement.GetRawText()
            .Replace("Medium", "Immediate", StringComparison.Ordinal));
    MaterializedCardDefinition immediatePriorityDefinition = mediumPriorityDefinition with
    {
        TemplateId = "immediate-priority",
        Effects = new[]
        {
            new MaterializedEffectDefinition("immediate-priority", "Ability", "test",
                immediatePriorityDocument.RootElement.Clone()),
        },
    };
    var immediateState = new CombatState();
    var immediatePlayer = new CombatantState { Id = "immediate-player", MaxHealth = 100, Health = 100 };
    var immediateOpponent = new CombatantState { Id = "immediate-opponent", MaxHealth = 100, Health = 100 };
    immediateState.Combatants.Add(immediatePlayer);
    immediateState.Combatants.Add(immediateOpponent);
    CombatCardState.Create("immediate-priority-card", immediatePriorityDefinition, immediatePlayer, 0);
    var immediateScheduler = new CombatScheduler(immediateState, new CombatRuleRuntime(
        immediateState, new XorShiftCombatRandom(SeedMixer.Mix(164, 0))));
    immediateScheduler.StartFight();
    immediateScheduler.AdvanceOneTick();
    AssertEqual(90, immediateOpponent.Health,
        "immediate effect executes synchronously in its use frame");

    using JsonDocument readyListenerDocument = JsonDocument.Parse("""
        {"Id":"ready-listener","Trigger":{"$type":"TTriggerOnItemUsed","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":false,"Conditions":{"$type":"TCardConditionalId","Id":"ready-source"}}},"Action":{"$type":"TActionPlayerDamage","ReferenceValue":{"$type":"TFixedValue","Value":10},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent","Conditions":null}},"Prerequisites":null,"Priority":"Immediate"}
        """);
    MaterializedCardDefinition readySourceDefinition = SchedulerTestCard(
        "ready-source", 0, 100_000) with
    {
        Attributes = new Dictionary<string, int>
        {
            ["CooldownMax"] = 100_000,
            ["Multicast"] = 2,
        },
    };
    MaterializedCardDefinition readyListenerDefinition = SchedulerTestCard(
        "ready-listener", 0, 0) with
    {
        Effects = new[]
        {
            new MaterializedEffectDefinition(
                "ready-listener", "Ability", "test",
                readyListenerDocument.RootElement.Clone()),
        },
    };
    var readyState = new CombatState();
    var readyPlayer = new CombatantState { Id = "ready-player", MaxHealth = 100, Health = 100 };
    var readyOpponent = new CombatantState { Id = "ready-opponent", MaxHealth = 100, Health = 100 };
    readyState.Combatants.Add(readyPlayer);
    readyState.Combatants.Add(readyOpponent);
    CombatCardState readySource = CombatCardState.Create(
        "ready-source-instance", readySourceDefinition, readyPlayer, 0);
    CombatCardState.Create(
        "ready-listener-instance", readyListenerDefinition, readyPlayer, 0, "Skills");
    var readyRules = new CombatRuleRuntime(
        readyState, new XorShiftCombatRandom(SeedMixer.Mix(165, 0)));
    AssertEqual(0, readyRules.FireCardScheduled(readySource),
        "use without own effects executes no rule effect immediately");
    AssertEqual(2, readyState.ScheduledReadySignals.Count,
        "multicast appends one ItemUsed ready signal per cast");
    var readyScheduler = new CombatScheduler(readyState, readyRules);
    readyScheduler.StartFight();
    readyScheduler.AdvanceOneTick();
    AssertEqual(90, readyOpponent.Health,
        "first ItemUsed signal drains one tick after use");
    for (int tick = 2; tick <= 5; tick++)
    {
        readyScheduler.AdvanceOneTick();
    }
    AssertEqual(90, readyOpponent.Health,
        "second multicast ItemUsed signal remains behind five-tick stagger");
    readyScheduler.AdvanceOneTick();
    AssertEqual(80, readyOpponent.Health,
        "second multicast ItemUsed signal drains at tick six");

    using JsonDocument readyCritListenerDocument = JsonDocument.Parse("""
        {"Id":"ready-crit-listener","Trigger":{"$type":"TTriggerOnCardCritted","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":false,"Conditions":{"$type":"TCardConditionalId","Id":"ready-crit-source"}}},"Action":{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":1},"AttributeType":"Custom_4","Operation":"Add","Target":{"$type":"TTargetCardSelf","Conditions":null}},"Prerequisites":null,"Priority":"Immediate"}
        """);
    MaterializedCardDefinition readyCritSourceDefinition = SchedulerTestCard(
        "ready-crit-source", 0, 100_000) with
    {
        Attributes = new Dictionary<string, int>
        {
            ["CooldownMax"] = 100_000,
            ["Multicast"] = 1,
            ["CritChance"] = 100,
        },
        Effects = mediumPriorityDefinition.Effects,
    };
    MaterializedCardDefinition readyCritListenerDefinition = SchedulerTestCard(
        "ready-crit-listener", 0, 0) with
    {
        Effects = new[]
        {
            new MaterializedEffectDefinition(
                "ready-crit-listener", "Ability", "test",
                readyCritListenerDocument.RootElement.Clone()),
        },
    };
    var readyCritState = new CombatState();
    var readyCritPlayer = new CombatantState
        { Id = "ready-crit-player", MaxHealth = 100, Health = 100 };
    var readyCritOpponent = new CombatantState
        { Id = "ready-crit-opponent", MaxHealth = 100, Health = 100 };
    readyCritState.Combatants.Add(readyCritPlayer);
    readyCritState.Combatants.Add(readyCritOpponent);
    CombatCardState readyCritSource = CombatCardState.Create(
        "ready-crit-source-instance", readyCritSourceDefinition, readyCritPlayer, 0);
    CombatCardState readyCritListener = CombatCardState.Create(
        "ready-crit-listener-instance", readyCritListenerDefinition,
        readyCritPlayer, 0, "Skills");
    var readyCritRules = new CombatRuleRuntime(
        readyCritState, new XorShiftCombatRandom(SeedMixer.Mix(166, 0)));
    readyCritRules.FireCardScheduled(readyCritSource);
    AssertEqual(1, readyCritState.ScheduledReadySignals.Count,
        "critical use keeps ItemUsed in the ready-signal queue");
    AssertEqual(true, readyCritState.ScheduledRuleEffects.Single()
        .EmitCardCrittedAfterCompletion,
        "highest-priority use effect carries CardCritted completion marker");
    var readyCritScheduler = new CombatScheduler(readyCritState, readyCritRules);
    readyCritScheduler.StartFight();
    AssertEqual(0, readyCritListener.Attributes.GetValueOrDefault("Custom_4"),
        "CardCritted listener is deferred from the use frame");
    readyCritScheduler.AdvanceOneTick();
    AssertEqual(1, readyCritListener.Attributes.GetValueOrDefault("Custom_4"),
        "CardCritted listener drains after use-effect completion");

    using JsonDocument scopedLowDocument = JsonDocument.Parse("""
        {"Id":"scoped-low","Trigger":{"$type":"TTriggerOnCardFired"},"Action":{"$type":"TActionPlayerDamage","ReferenceValue":{"$type":"TFixedValue","Value":20},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent","Conditions":null}},"Prerequisites":null,"Priority":"Low"}
        """);
    using JsonDocument scopedHighDocument = JsonDocument.Parse("""
        {"Id":"scoped-high","Trigger":{"$type":"TTriggerOnCardFired"},"Action":{"$type":"TActionPlayerDamage","ReferenceValue":{"$type":"TFixedValue","Value":30},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent","Conditions":null}},"Prerequisites":null,"Priority":"High"}
        """);
    using JsonDocument scopedItemUsedDocument = JsonDocument.Parse("""
        {"Id":"scoped-item-used","Trigger":{"$type":"TTriggerOnItemUsed","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":false,"Conditions":{"$type":"TCardConditionalId","Id":"scoped-source"}}},"Action":{"$type":"TActionPlayerDamage","ReferenceValue":{"$type":"TFixedValue","Value":10},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent","Conditions":null}},"Prerequisites":null,"Priority":"Immediate"}
        """);
    MaterializedCardDefinition scopedSourceDefinition = SchedulerTestCard(
        "scoped-source", 0, 100_000) with
    {
        Effects = new[]
        {
            new MaterializedEffectDefinition(
                "scoped-low", "Ability", "test", scopedLowDocument.RootElement.Clone()),
            new MaterializedEffectDefinition(
                "scoped-high", "Ability", "test", scopedHighDocument.RootElement.Clone()),
        },
    };
    MaterializedCardDefinition scopedListenerDefinition = SchedulerTestCard(
        "scoped-listener", 0, 0) with
    {
        Effects = new[]
        {
            new MaterializedEffectDefinition(
                "scoped-item-used", "Ability", "test",
                scopedItemUsedDocument.RootElement.Clone()),
        },
    };
    var scopedState = new CombatState();
    var scopedPlayer = new CombatantState { Id = "scoped-player", MaxHealth = 100, Health = 100 };
    var scopedOpponent = new CombatantState { Id = "scoped-opponent", MaxHealth = 100, Health = 100 };
    scopedState.Combatants.Add(scopedPlayer);
    scopedState.Combatants.Add(scopedOpponent);
    CombatCardState scopedSource = CombatCardState.Create(
        "scoped-source-instance", scopedSourceDefinition, scopedPlayer, 0);
    CombatCardState.Create(
        "scoped-listener-instance", scopedListenerDefinition, scopedPlayer, 0, "Skills");
    var scopedRules = new CombatRuleRuntime(
        scopedState, new XorShiftCombatRandom(SeedMixer.Mix(167, 0)));
    scopedRules.FireCardScheduled(scopedSource);
    ScheduledRuleEffect scopedCompletion = scopedState.ScheduledRuleEffects.Single(
        effect => effect.CompletesReadyScope);
    AssertEqual("scoped-high", scopedCompletion.Effect.Id,
        "definition-last selected effect is the use completion marker");
    var scopedScheduler = new CombatScheduler(scopedState, scopedRules);
    scopedScheduler.StartFight();
    scopedScheduler.AdvanceOneTick();
    int[] scopedDamageOrder = scopedState.Events
        .Where(value => value.Kind == "CardDamage" && value.TargetId == "scoped-opponent")
        .Select(value => value.Amount)
        .ToArray();
    AssertEqual("30,10,20", string.Join(',', scopedDamageOrder),
        "item-scoped ready drain occurs between high and low due effects");

    MaterializedCardDefinition heavyBass = catalog
        .Get("04312f35-0ac9-4fd0-9118-6367be0d4690")
        .Materialize("Silver", "Heavy");
    var performedState = new CombatState();
    var performedPlayer = new CombatantState { Id = "performed-player", MaxHealth = 100, Health = 100 };
    var performedOpponent = new CombatantState { Id = "performed-opponent", MaxHealth = 100, Health = 100 };
    performedState.Combatants.Add(performedPlayer);
    performedState.Combatants.Add(performedOpponent);
    CombatCardState bassState = CombatCardState.Create("bass", heavyBass, performedPlayer, 0);
    CombatCardState.Create("bass-target", aila, performedOpponent, 0);
    var performedRules = new CombatRuleRuntime(
        performedState,
        new XorShiftCombatRandom(SeedMixer.Mix(11, 0)));
    AssertEqual(3, performedRules.FireCard(bassState), "performed-slow trigger chain");
    AssertEqual(60, bassState.Attributes["DamageAmount"], "performed-slow self buff");

    var durationState = new CombatState();
    var durationPlayer = new CombatantState { Id = "duration-player", MaxHealth = 100, Health = 100 };
    durationState.Combatants.Add(durationPlayer);
    CombatCardState durationCard = CombatCardState.Create("duration-card", aila, durationPlayer, 0);
    using JsonDocument durationDocument = JsonDocument.Parse("""
        {"Action":{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":5},"AttributeType":"Heated","Operation":"Add","Duration":{"$type":"TCombatDuration","DurationInMs":100},"Target":{"$type":"TTargetCardSelf","Conditions":null}}}
        """);
    var durationEffect = new MaterializedEffectDefinition(
        "timed", "Ability", "test", durationDocument.RootElement.Clone());
    CombatActionDispatcher.Execute(
        durationEffect,
        new CombatActionContext(
            durationState, durationCard, new XorShiftCombatRandom(SeedMixer.Mix(12, 0))));
    var durationRules = new CombatRuleRuntime(
        durationState,
        new XorShiftCombatRandom(SeedMixer.Mix(13, 0)));
    var durationScheduler = new CombatScheduler(durationState, durationRules);
    durationScheduler.StartFight();
    durationScheduler.AdvanceOneTick();
    AssertEqual(5, durationCard.Attributes["Heated"], "timed modifier active");
    durationScheduler.AdvanceOneTick();
    AssertEqual(0, durationCard.Attributes.GetValueOrDefault("Heated"), "timed modifier expiry");

    var multicastState = new CombatState();
    var multicastPlayer = new CombatantState { Id = "multicast-player", MaxHealth = 100, Health = 100 };
    var multicastOpponent = new CombatantState { Id = "multicast-opponent", MaxHealth = 100, Health = 100 };
    multicastState.Combatants.Add(multicastPlayer);
    multicastState.Combatants.Add(multicastOpponent);
    CombatCardState multicastAila = CombatCardState.Create("multicast-aila", aila, multicastPlayer, 0);
    multicastAila.SetIntrinsicAttribute("Multicast", 2);
    var multicastRules = new CombatRuleRuntime(
        multicastState, new XorShiftCombatRandom(SeedMixer.Mix(14, 0)));
    AssertEqual(2, multicastRules.FireCard(multicastAila), "multicast effect count");
    AssertEqual(-60, multicastOpponent.Health, "multicast repeated damage");

    using JsonDocument multicastListenerDocument = JsonDocument.Parse("""
        {"Id":"listen","Trigger":{"$type":"TTriggerOnItemUsed","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":false,"Conditions":{"$type":"TCardConditionalId","Id":"00ab28d4-c3d2-420e-ba71-b88bc29f4834"}}},"Action":{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":1},"AttributeType":"Custom_0","Operation":"Add","Duration":{"$type":"TDeterminantDuration","DurationType":"UntilEndOfCombat"},"Target":{"$type":"TTargetCardSelf","Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    var multicastListenerDefinition = new MaterializedCardDefinition(
        "multicast-listener", "Multicast Listener", "TCardSkill", "Small", "Diamond", null,
        new Dictionary<string, int>(), new HashSet<string>(), new HashSet<string>(),
        new[] { new MaterializedEffectDefinition(
            "listen", "Ability", "test", multicastListenerDocument.RootElement.Clone()) });
    CombatCardState multicastListener = CombatCardState.Create(
        "multicast-listener", multicastListenerDefinition, multicastPlayer, 1, "Skills");
    AssertEqual(4, multicastRules.FireCard(multicastAila),
        "multicast item-used effect count");
    AssertEqual(2, multicastListener.Attributes.GetValueOrDefault("Custom_0"),
        "item-used fires once per multicast cast");

    using JsonDocument beforeUseListenerDocument = JsonDocument.Parse("""
        {"Id":"before","Trigger":{"$type":"TTriggerOnBeforeItemUsed","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":false,"Conditions":{"$type":"TCardConditionalId","Id":"00ab28d4-c3d2-420e-ba71-b88bc29f4834"}}},"Action":{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":1},"AttributeType":"Custom_8","Operation":"Add","Target":{"$type":"TTargetCardSelf","Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    using JsonDocument damageListenerDocument = JsonDocument.Parse("""
        {"Id":"damage","Trigger":{"$type":"TTriggerOnCardPerformedDamage","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":false,"Conditions":{"$type":"TCardConditionalId","Id":"00ab28d4-c3d2-420e-ba71-b88bc29f4834"}},"Target":null},"Action":{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":1},"AttributeType":"Custom_9","Operation":"Add","Target":{"$type":"TTargetCardSelf","Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    var phaseListenerDefinition = new MaterializedCardDefinition(
        "phase-listener", "Phase Listener", "TCardSkill", "Small", "Diamond", null,
        new Dictionary<string, int>(), new HashSet<string>(), new HashSet<string>(),
        new[]
        {
            new MaterializedEffectDefinition(
                "before", "Ability", "test", beforeUseListenerDocument.RootElement.Clone()),
            new MaterializedEffectDefinition(
                "damage", "Ability", "test", damageListenerDocument.RootElement.Clone()),
        });
    CombatCardState phaseListener = CombatCardState.Create(
        "phase-listener", phaseListenerDefinition, multicastPlayer, 2, "Skills");
    int beforeCardUsedIndex = multicastState.Events.Count;
    AssertEqual(8, multicastRules.FireCard(multicastAila),
        "before-use and performed-damage effects per multicast cast");
    AssertEqual(2, phaseListener.Attributes.GetValueOrDefault("Custom_8"),
        "before-item-used fires once per multicast cast");
    AssertEqual(2, phaseListener.Attributes.GetValueOrDefault("Custom_9"),
        "performed-damage fires once per multicast cast");
    CombatEvent[] phaseEvents = multicastState.Events.Skip(beforeCardUsedIndex).ToArray();
    AssertEqual(true,
        Array.FindIndex(phaseEvents, value => value.Kind == "CardModifyAttribute:Custom_8") <
        Array.FindIndex(phaseEvents, value => value.Kind == "CardUsed"),
        "before-item-used executes before card-used event");

    using JsonDocument multiTargetHasteDocument = JsonDocument.Parse("""
        {"Id":"haste-two","Trigger":{"$type":"TTriggerOnCardFired"},"Action":{"$type":"TActionCardHaste","Value":{"$type":"TFixedValue","Value":1000},"TargetCount":{"$type":"TFixedValue","Value":2},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true,"Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    using JsonDocument multiTargetListenerDocument = JsonDocument.Parse("""
        {"Id":"haste-listener","Trigger":{"$type":"TTriggerOnCardPerformedHaste","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":false,"Conditions":null},"Target":null},"Action":{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":1},"AttributeType":"Custom_3","Operation":"Add","Target":{"$type":"TTargetCardTriggerTarget","ExcludeSelf":false,"Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    using JsonDocument multiTargetCountListenerDocument = JsonDocument.Parse("""
        {"Id":"haste-count-listener","Trigger":{"$type":"TTriggerOnCardPerformedHaste","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":false,"Conditions":null},"Target":null},"Action":{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":1},"AttributeType":"Custom_4","Operation":"Add","Target":{"$type":"TTargetCardSelf","Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    using JsonDocument interleavedHasteListenerDocument = JsonDocument.Parse("""
        {"Id":"haste-interleaved-listener","Trigger":{"$type":"TTriggerOnCardPerformedHaste","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":false,"Conditions":null},"Target":null},"Action":{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":100},"AttributeType":"PercentHasteReduction","Operation":"Add","Target":{"$type":"TTargetCardXMost","TargetSection":"SelfHand","TargetMode":"RightMostCard","Conditions":null}},"Prerequisites":null,"Priority":"Immediate"}
        """);
    using JsonDocument interleavedAttributeListenerDocument = JsonDocument.Parse("""
        {"Id":"haste-attribute-order-listener","Trigger":{"$type":"TTriggerOnCardAttributeChanged","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":false,"Conditions":null},"Source":null,"AttributeType":"Haste","ChangeType":"Gain"},"Action":{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":1},"AttributeType":"Custom_6","Operation":"Add","Target":{"$type":"TTargetCardSelf","Conditions":null}},"Prerequisites":null,"Priority":"Immediate"}
        """);
    var blankDefinition = new MaterializedCardDefinition(
        "blank", "Blank", "TCardItem", "Small", "Diamond", null,
        new Dictionary<string, int> { ["CooldownMax"] = 5000 },
        new HashSet<string>(), new HashSet<string>(), []);
    var multiTargetSourceDefinition = new MaterializedCardDefinition(
        "haste-source", "Haste Source", "TCardItem", "Small", "Diamond", null,
        new Dictionary<string, int> { ["CooldownMax"] = 5000 },
        new HashSet<string>(), new HashSet<string>(),
        [new MaterializedEffectDefinition(
            "haste-two", "Ability", "test", multiTargetHasteDocument.RootElement.Clone())]);
    var multiTargetListenerDefinition = new MaterializedCardDefinition(
        "haste-listener", "Haste Listener", "TCardSkill", "Small", "Diamond", null,
        new Dictionary<string, int>(), new HashSet<string>(), new HashSet<string>(),
        [
            new MaterializedEffectDefinition(
                "haste-listener", "Ability", "test",
                multiTargetListenerDocument.RootElement.Clone()),
            new MaterializedEffectDefinition(
                "haste-count-listener", "Ability", "test",
                multiTargetCountListenerDocument.RootElement.Clone()),
            new MaterializedEffectDefinition(
                "haste-interleaved-listener", "Ability", "test",
                interleavedHasteListenerDocument.RootElement.Clone()),
            new MaterializedEffectDefinition(
                "haste-attribute-order-listener", "Ability", "test",
                interleavedAttributeListenerDocument.RootElement.Clone()),
        ]);
    var multiTargetState = new CombatState();
    var multiTargetPlayer = new CombatantState
        { Id = "multi-target-player", MaxHealth = 100, Health = 100 };
    var multiTargetOpponent = new CombatantState
        { Id = "multi-target-opponent", MaxHealth = 100, Health = 100 };
    multiTargetState.Combatants.Add(multiTargetPlayer);
    multiTargetState.Combatants.Add(multiTargetOpponent);
    CombatCardState multiTargetSource = CombatCardState.Create(
        "multi-target-source", multiTargetSourceDefinition, multiTargetPlayer, 0);
    CombatCardState multiTargetA = CombatCardState.Create(
        "multi-target-a", blankDefinition, multiTargetPlayer, 1);
    CombatCardState multiTargetB = CombatCardState.Create(
        "multi-target-b", blankDefinition, multiTargetPlayer, 2);
    CombatCardState multiTargetListener = CombatCardState.Create(
        "multi-target-listener", multiTargetListenerDefinition, multiTargetPlayer, 3, "Skills");
    var multiTargetRules = new CombatRuleRuntime(
        multiTargetState, new XorShiftCombatRandom(SeedMixer.Mix(140, 0)));
    multiTargetRules.FireCard(multiTargetSource);
    AssertEqual(1, multiTargetA.Attributes.GetValueOrDefault("Custom_3"),
        "performed signal exposes first affected card through TriggerTarget");
    AssertEqual(1, multiTargetB.Attributes.GetValueOrDefault("Custom_3"),
        "performed signal exposes every affected card through TriggerTarget");
    AssertEqual(0, multiTargetB.Attributes.GetValueOrDefault("Haste"),
        "first target performed signal can affect the next target before it resolves");
    AssertEqual(2, multiTargetListener.Attributes.GetValueOrDefault("Custom_4"),
        "performed card signal fires once per affected card");
    CombatEvent[] multiTargetEvents = multiTargetState.Events.ToArray();
    AssertEqual(true,
        Array.FindIndex(multiTargetEvents,
            value => value.Kind == "CardModifyAttribute:Custom_6") <
        Array.FindIndex(multiTargetEvents,
            value => value.Kind == "CardModifyAttribute:PercentHasteReduction"),
        "card-attribute-changed dispatches before performed for each target");

    using JsonDocument conditionalXMostDocument = JsonDocument.Parse("""
        {"$type":"TTargetCardXMost","TargetSection":"SelfHand","TargetMode":"RightMostCard","ExcludeSelf":true,"Conditions":{"$type":"TCardConditionalTag","Tags":["Weapon"],"Operator":"Any"}}
        """);
    var xMostState = new CombatState();
    var xMostPlayer = new CombatantState
        { Id = "xmost-player", MaxHealth = 100, Health = 100 };
    var xMostOpponent = new CombatantState
        { Id = "xmost-opponent", MaxHealth = 100, Health = 100 };
    xMostState.Combatants.Add(xMostPlayer);
    xMostState.Combatants.Add(xMostOpponent);
    CombatCardState xMostSource = CombatCardState.Create(
        "xmost-source", blankDefinition, xMostPlayer, 0);
    var weaponDefinition = blankDefinition with
    {
        TemplateId = "xmost-weapon",
        Name = "XMost Weapon",
        Tags = new HashSet<string> { "Weapon" },
    };
    CombatCardState xMostWeapon = CombatCardState.Create(
        "xmost-weapon", weaponDefinition, xMostPlayer, 1);
    CombatCardState xMostNonWeapon = CombatCardState.Create(
        "xmost-non-weapon", blankDefinition with
        {
            TemplateId = "xmost-non-weapon",
            Name = "XMost Non-Weapon",
        }, xMostPlayer, 2);
    List<CombatCardState> conditionalXMostTargets = TargetResolver.ResolveCardTarget(
        conditionalXMostDocument.RootElement,
        new CombatActionContext(
            xMostState, xMostSource,
            new XorShiftCombatRandom(SeedMixer.Mix(214, 0))),
        null);
    AssertEqual(1, conditionalXMostTargets.Count,
        "conditional XMost resolves one matching card");
    AssertEqual(true, ReferenceEquals(xMostWeapon, conditionalXMostTargets.Single()),
        "conditional XMost filters before choosing positional extreme");
    AssertEqual(false, conditionalXMostTargets.Contains(xMostNonWeapon),
        "conditional XMost skips a nonmatching outer card");

    using JsonDocument disableActionDocument = JsonDocument.Parse("""
        {"Id":"disable","Trigger":{"$type":"TTriggerOnCardFired"},"Action":{"$type":"TActionCardDisable","TargetCount":{"$type":"TFixedValue","Value":1},"Target":{"$type":"TTargetCardRandom","TargetSection":"OpponentHand","ExcludeSelf":false,"Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    using JsonDocument repairActionDocument = JsonDocument.Parse("""
        {"Id":"repair","Trigger":{"$type":"TTriggerOnCardFired"},"Action":{"$type":"TActionCardRepair","TargetCount":{"$type":"TFixedValue","Value":1},"Target":{"$type":"TTargetCardRandom","TargetSection":"OpponentHand","ExcludeSelf":false,"Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    using JsonDocument disabledListenerDocument = JsonDocument.Parse("""
        {"Id":"disabled-listener","Trigger":{"$type":"TTriggerOnCardDisabled","Subject":{"$type":"TTargetCardSection","TargetSection":"OpponentHand","ExcludeSelf":false,"Conditions":null},"Source":null},"Action":{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":1},"AttributeType":"Custom_6","Operation":"Add","Target":{"$type":"TTargetCardSelf","Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    using JsonDocument repairedListenerDocument = JsonDocument.Parse("""
        {"Id":"repaired-listener","Trigger":{"$type":"TTriggerOnCardRepaired","Subject":{"$type":"TTargetCardSection","TargetSection":"OpponentHand","ExcludeSelf":false,"Conditions":null},"Source":null},"Action":{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":1},"AttributeType":"Custom_7","Operation":"Add","Target":{"$type":"TTargetCardSelf","Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    using JsonDocument disableDestructionListenerDocument = JsonDocument.Parse("""
        {"Id":"disable-destruction-listener","Trigger":{"$type":"TTriggerOnCardPerformedDestruction","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":false,"Conditions":null},"Target":null},"Action":{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":1},"AttributeType":"Custom_9","Operation":"Add","Target":{"$type":"TTargetCardSelf","Conditions":null}},"Prerequisites":null,"Priority":"Immediate"}
        """);
    static MaterializedCardDefinition TestCardWithEffect(
        string id, string type, JsonElement effect) => new(
            id, id, type, "Small", "Diamond", null,
            new Dictionary<string, int> { ["Multicast"] = 1 },
            new HashSet<string>(), new HashSet<string>(),
            new[] { new MaterializedEffectDefinition(id, "Ability", "test", effect.Clone()) });

    using JsonDocument handOnlyFightEffectDocument = JsonDocument.Parse("""
        {"Id":"active-hand","ActiveIn":"HandOnly","WorksIn":"Anywhere","Trigger":{"$type":"TTriggerOnFightStarted"},"Action":{"$type":"TActionPlayerDamage","ReferenceValue":{"$type":"TFixedValue","Value":1},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent","Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    using JsonDocument handAndStashFightEffectDocument = JsonDocument.Parse("""
        {"Id":"active-stash","ActiveIn":"HandAndStash","WorksIn":"Anywhere","Trigger":{"$type":"TTriggerOnFightStarted"},"Action":{"$type":"TActionPlayerDamage","ReferenceValue":{"$type":"TFixedValue","Value":1},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent","Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    using JsonDocument outOfCombatFightEffectDocument = JsonDocument.Parse("""
        {"Id":"inactive-combat","ActiveIn":"HandOnly","WorksIn":"OutOfCombatOnly","Trigger":{"$type":"TTriggerOnFightStarted"},"Action":{"$type":"TActionPlayerDamage","ReferenceValue":{"$type":"TFixedValue","Value":1},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent","Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    var activationState = new CombatState();
    var activationPlayer = new CombatantState
    {
        Id = "activation-player", MaxHealth = 100, Health = 100,
    };
    var activationOpponent = new CombatantState
    {
        Id = "activation-opponent", MaxHealth = 100, Health = 100,
    };
    activationState.Combatants.Add(activationPlayer);
    activationState.Combatants.Add(activationOpponent);
    CombatCardState inactiveStashItem = CombatCardState.Create(
        "inactive-stash-item",
        TestCardWithEffect("inactive-stash-item", "TCardItem",
            handOnlyFightEffectDocument.RootElement),
        activationPlayer, 0, "Stash");
    CombatCardState activeStashItem = CombatCardState.Create(
        "active-stash-item",
        TestCardWithEffect("active-stash-item", "TCardItem",
            handAndStashFightEffectDocument.RootElement),
        activationPlayer, 1, "Stash");
    CombatCardState activeSkill = CombatCardState.Create(
        "active-skill",
        TestCardWithEffect("active-skill", "TCardSkill",
            handOnlyFightEffectDocument.RootElement),
        activationPlayer, 2, "Skills");
    CombatCardState inactiveOutOfCombatItem = CombatCardState.Create(
        "inactive-out-of-combat-item",
        TestCardWithEffect("inactive-out-of-combat-item", "TCardItem",
            outOfCombatFightEffectDocument.RootElement),
        activationPlayer, 3, "Hand");
    AssertEqual(false, CombatEffectActivation.IsActive(
        inactiveStashItem.Definition.Effects.Single(), inactiveStashItem),
        "HandOnly stash item effect is inactive");
    AssertEqual(true, CombatEffectActivation.IsActive(
        activeStashItem.Definition.Effects.Single(), activeStashItem),
        "HandAndStash stash item effect is active");
    AssertEqual(true, CombatEffectActivation.IsActive(
        activeSkill.Definition.Effects.Single(), activeSkill),
        "skill bypasses item inventory-section filtering");
    AssertEqual(false, CombatEffectActivation.IsActive(
        inactiveOutOfCombatItem.Definition.Effects.Single(), inactiveOutOfCombatItem),
        "OutOfCombatOnly effect is inactive in combat");
    var activationRules = new CombatRuleRuntime(
        activationState, new XorShiftCombatRandom(SeedMixer.Mix(204, 0)));
    AssertEqual(2, activationRules.StartFight(),
        "global dispatch executes only combat-active effects");
    AssertEqual(98, activationOpponent.Health,
        "active stash item and skill each execute once");

    var lifecycleState = new CombatState();
    var lifecyclePlayer = new CombatantState { Id = "lifecycle-player", MaxHealth = 100, Health = 100 };
    var lifecycleOpponent = new CombatantState { Id = "lifecycle-opponent", MaxHealth = 100, Health = 100 };
    lifecycleState.Combatants.Add(lifecyclePlayer);
    lifecycleState.Combatants.Add(lifecycleOpponent);
    CombatCardState disableSource = CombatCardState.Create("disable-source",
        TestCardWithEffect("disable-source", "TCardItem", disableActionDocument.RootElement),
        lifecyclePlayer, 0);
    CombatCardState disabledListener = CombatCardState.Create("disabled-listener",
        TestCardWithEffect("disabled-listener", "TCardSkill", disabledListenerDocument.RootElement),
        lifecyclePlayer, 1, "Skills");
    CombatCardState repairSource = CombatCardState.Create("repair-source",
        TestCardWithEffect("repair-source", "TCardItem", repairActionDocument.RootElement),
        lifecycleOpponent, 0);
    CombatCardState repairedListener = CombatCardState.Create("repaired-listener",
        TestCardWithEffect("repaired-listener", "TCardSkill", repairedListenerDocument.RootElement),
        lifecycleOpponent, 1, "Skills");
    CombatCardState disableDestructionListener = CombatCardState.Create(
        "disable-destruction-listener",
        TestCardWithEffect("disable-destruction-listener", "TCardSkill",
            disableDestructionListenerDocument.RootElement), lifecyclePlayer, 2, "Skills");
    var lifecycleRules = new CombatRuleRuntime(
        lifecycleState, new XorShiftCombatRandom(SeedMixer.Mix(143, 0)));
    AssertEqual(3, lifecycleRules.FireCard(disableSource),
        "disable action dispatches disabled and performed-destruction listeners");
    AssertEqual(true, repairSource.IsDisabled, "disable marks target inactive");
    AssertEqual(1, disabledListener.Attributes.GetValueOrDefault("Custom_6"),
        "card-disabled trigger");
    AssertEqual(1, disableDestructionListener.Attributes.GetValueOrDefault("Custom_9"),
        "combat disable emits performed-destruction trigger");
    AssertEqual(0, lifecycleRules.FireCard(repairSource), "disabled card cannot fire");
    repairSource.IsDisabled = false;
    disableSource.IsDisabled = true;
    AssertEqual(2, lifecycleRules.FireCard(repairSource), "repair action and listener count");
    AssertEqual(false, disableSource.IsDisabled, "repair selects a disabled target");
    AssertEqual(1, repairedListener.Attributes.GetValueOrDefault("Custom_7"),
        "card-repaired trigger");

    using JsonDocument destroyActionDocument = JsonDocument.Parse("""
        {"Id":"destroy","Trigger":{"$type":"TTriggerOnCardFired"},"Action":{"$type":"TActionCardDestroy","TargetCount":{"$type":"TFixedValue","Value":1},"Target":{"$type":"TTargetCardRandom","TargetSection":"OpponentHand","ExcludeSelf":false,"Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    using JsonDocument beforeDestroyDocument = JsonDocument.Parse("""
        {"Id":"before-destroy","Trigger":{"$type":"TTriggerOnBeforeCardDestroyed","Subject":{"$type":"TTargetCardSelf","Conditions":null},"Source":null},"Action":{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":1},"AttributeType":"DestroyImmunity","Operation":"Add","Target":{"$type":"TTargetCardSelf","Conditions":null}},"Prerequisites":null,"Priority":"Immediate"}
        """);
    using JsonDocument destructionListenerDocument = JsonDocument.Parse("""
        {"Id":"destruction-listener","Trigger":{"$type":"TTriggerOnCardPerformedDestruction","Subject":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":false,"Conditions":null},"Target":null},"Action":{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":1},"AttributeType":"Custom_5","Operation":"Add","Target":{"$type":"TTargetCardSelf","Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    var destructionState = new CombatState();
    var destructionPlayer = new CombatantState { Id = "destruction-player", MaxHealth = 100, Health = 100 };
    var destructionOpponent = new CombatantState { Id = "destruction-opponent", MaxHealth = 100, Health = 100 };
    destructionState.Combatants.Add(destructionPlayer);
    destructionState.Combatants.Add(destructionOpponent);
    CombatCardState destroySource = CombatCardState.Create("destroy-source",
        TestCardWithEffect("destroy-source", "TCardItem", destroyActionDocument.RootElement),
        destructionPlayer, 0);
    CombatCardState destructionListener = CombatCardState.Create("destruction-listener",
        TestCardWithEffect("destruction-listener", "TCardSkill", destructionListenerDocument.RootElement),
        destructionPlayer, 1, "Skills");
    CombatCardState destroyTarget = CombatCardState.Create("destroy-target",
        TestCardWithEffect("destroy-target", "TCardItem", beforeDestroyDocument.RootElement),
        destructionOpponent, 0);
    var destructionRules = new CombatRuleRuntime(
        destructionState, new XorShiftCombatRandom(SeedMixer.Mix(144, 0)));
    AssertEqual(2, destructionRules.FireCard(destroySource),
        "destroy request and before-destroy trigger count");
    AssertEqual(false, destroyTarget.IsDestroyed, "destroy immunity blocks destruction");
    AssertEqual(1, destructionState.Events.Count(value => value.Kind == "CardDestroyBlocked"),
        "blocked destruction diagnostic");
    destroyTarget.Definition = aila;
    destroyTarget.SetIntrinsicAttribute("DestroyImmunity", 0);
    AssertEqual(2, destructionRules.FireCard(destroySource),
        "destroy action and performed-destruction listener count");
    AssertEqual(true, destroyTarget.IsDestroyed, "successful destruction marks target inactive");
    AssertEqual(1, destructionListener.Attributes.GetValueOrDefault("Custom_5"),
        "performed-destruction trigger");

    string transformReplacementId = catalog
        .Get("00ab28d4-c3d2-420e-ba71-b88bc29f4834").Id;
    using JsonDocument transformOnDisableDocument = JsonDocument.Parse("""
        {"Id":"transform-on-disable","Trigger":{"$type":"TTriggerOnBeforeCardDestroyed","Subject":{"$type":"TTargetCardSelf","Conditions":null},"Source":null},"Action":{"$type":"TActionCardTransformDestroyed","SpawnContext":{"$type":"TSpawnContextQuery","Groups":[{"$type":"TSpawnGroup","Filters":[{"$type":"TSpawnFilterIdList","Ids":["__REPLACEMENT_ID__"]}],"SelectionMethod":"Random","Limit":null,"Prerequisites":null,"Behaviors":null}],"SelectionMethod":"Sequential","Limit":{"$type":"TFixedValue","Value":2},"Behaviors":null},"Target":{"$type":"TTargetCardTriggerTarget","ExcludeSelf":false,"Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """.Replace("__REPLACEMENT_ID__", transformReplacementId,
            StringComparison.Ordinal));
    var disableTransformState = new CombatState { CardCatalog = catalog, Tick = 1 };
    var disableTransformPlayer = new CombatantState
        { Id = "disable-transform-player", MaxHealth = 100, Health = 100 };
    var disableTransformOpponent = new CombatantState
        { Id = "disable-transform-opponent", MaxHealth = 100, Health = 100 };
    disableTransformState.Combatants.Add(disableTransformPlayer);
    disableTransformState.Combatants.Add(disableTransformOpponent);
    CombatCardState disableTransformSource = CombatCardState.Create(
        "disable-transform-source",
        TestCardWithEffect("disable-transform-source", "TCardItem",
            disableActionDocument.RootElement), disableTransformPlayer, 0);
    CombatCardState disableTransformTarget = CombatCardState.Create(
        "disable-transform-target",
        TestCardWithEffect("disable-transform-original", "TCardItem",
            transformOnDisableDocument.RootElement) with { Size = "Medium" },
        disableTransformOpponent, 0, "Hand", 2);
    var disableTransformRules = new CombatRuleRuntime(
        disableTransformState, new XorShiftCombatRandom(SeedMixer.Mix(1441, 0)));
    AssertEqual(2, disableTransformRules.FireCard(disableTransformSource),
        "combat disable dispatches before-destroy transformation");
    AssertEqual("00ab28d4-c3d2-420e-ba71-b88bc29f4834",
        disableTransformTarget.Definition.TemplateId,
        "before-destroy transformation replaces the disabled card");
    AssertEqual(false, disableTransformTarget.IsDisabled,
        "transformed combat-disable target remains active");
    AssertEqual(1, disableTransformState.Events.Count(value =>
        value.Kind == "CardTransformed" &&
        value.ActionType == "TActionCardTransformDestroyed"),
        "transform-destroyed event retains replay action metadata");
    AssertEqual(1, disableTransformState.Events.Count(value =>
        value.Kind == "CardTransformedSpawn" &&
        value.ActionType == "TActionCardTransformDestroyed"),
        "multi-card transform materializes the additional replacement");
    AssertEqual(0, disableTransformState.Events.Count(value =>
        value.Kind == "CardDisabled" && value.TargetId == disableTransformTarget.InstanceId),
        "replacement prevents stale disabled state");
    var transformReplaySimulation = new CombatSimulationResult(
        0, 0, 0, 1, null, Array.Empty<CombatantSimulationResult>(),
        disableTransformState.Events.Count,
        new Dictionary<string, int>(),
        new Dictionary<string, CombatEventAggregate>(),
        disableTransformState.Events,
        disableTransformState.Events,
        Array.Empty<CombatCardAttributeTransition>(), string.Empty);
    LocalReplayProjectionResult transformReplay = LocalReplayProjection.Build(
        "disable-transform-replay", transformReplaySimulation);
    AssertEqual(1, transformReplay.Frames.Single().Effects.Count(value =>
        value.ActionType == "CardTransformDestroyed"),
        "multi-card transform projects one native action against the destroyed card");

    using JsonDocument lethalDamageDocument = JsonDocument.Parse("""
        {"Id":"lethal","Trigger":{"$type":"TTriggerOnCardFired"},"Action":{"$type":"TActionPlayerDamage","ReferenceValue":{"$type":"TFixedValue","Value":150},"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent","Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    using JsonDocument reviveDocument = JsonDocument.Parse("""
        {"Id":"revive","Trigger":{"$type":"TTriggerOnPlayerDied","Subject":{"$type":"TTargetPlayerRelative","TargetMode":"Self","Conditions":null}},"Action":{"$type":"TActionPlayerReviveHeal","ReferenceValue":null,"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self","Conditions":null}},"Prerequisites":null,"Priority":"Immediate"}
        """);
    var reviveState = new CombatState();
    var reviveAttacker = new CombatantState { Id = "revive-attacker", MaxHealth = 100, Health = 100 };
    var reviveVictim = new CombatantState { Id = "revive-victim", MaxHealth = 100, Health = 100 };
    reviveState.Combatants.Add(reviveAttacker);
    reviveState.Combatants.Add(reviveVictim);
    CombatCardState lethalSource = CombatCardState.Create("lethal-source",
        TestCardWithEffect("lethal-source", "TCardItem", lethalDamageDocument.RootElement),
        reviveAttacker, 0);
    MaterializedCardDefinition reviveDefinition = TestCardWithEffect(
        "revive-listener", "TCardSkill", reviveDocument.RootElement) with
        { Attributes = new Dictionary<string, int> { ["HealAmount"] = 50 } };
    CombatCardState.Create("revive-listener", reviveDefinition, reviveVictim, 0, "Skills");
    var reviveRules = new CombatRuleRuntime(
        reviveState, new XorShiftCombatRandom(SeedMixer.Mix(145, 0)));
    AssertEqual(1, reviveRules.FireCard(lethalSource), "lethal damage effect count before death phase");
    AssertEqual(-50, reviveVictim.Health, "death trigger is deferred until frame resolution");
    AssertEqual(1, reviveRules.ResolvePlayerDeaths(), "deferred death and revive effect count");
    AssertEqual(50, reviveVictim.Health, "player-died trigger revives before combat terminates");
    AssertEqual(1, reviveState.Events.Count(value => value.Kind == "ReviveHeal"),
        "revive event count");
    reviveVictim.Health = 10;
    reviveVictim.Poison = 20;
    int firstPeriodicEvent = reviveState.Events.Count;
    CombatEngine.AdvanceOneTick(reviveState);
    reviveRules.ProcessEnginePlayerEvents(firstPeriodicEvent);
    reviveRules.ResolvePlayerDeaths();
    AssertEqual(50, reviveVictim.Health,
        "periodic engine damage also dispatches player-died trigger");

    using JsonDocument aggregateDocument = JsonDocument.Parse("""
        {"$type":"TReferenceValueCardAttributeAggregate","AttributeType":"SellPrice","Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":false,"Conditions":null},"DefaultValue":0,"Modifier":{"ModifyMode":"Multiply","Value":{"$type":"TFixedValue","Value":2},"ShouldRound":true}}
        """);
    disableSource.SetIntrinsicAttribute("SellPrice", 3);
    disabledListener.SetIntrinsicAttribute("SellPrice", 100);
    AssertEqual(6, RuleValueEvaluator.EvaluateToInt(
        aggregateDocument.RootElement,
        new CombatActionContext(lifecycleState, disableSource,
            new XorShiftCombatRandom(SeedMixer.Mix(146, 0)))),
        "card attribute aggregate sums selected hand cards before modifier");

    using JsonDocument rangeDocument = JsonDocument.Parse(
        """{"$type":"TRangeValue","DefaultValue":0,"MinValue":0,"MaxValue":3,"Modifier":null}""");
    var rangeRandom = new XorShiftCombatRandom(SeedMixer.Mix(202, 0));
    int[] rangeValues = Enumerable.Range(0, 8).Select(_ =>
        RuleValueEvaluator.EvaluateToInt(rangeDocument.RootElement,
            new CombatActionContext(lifecycleState, disableSource, rangeRandom))).ToArray();
    AssertEqual("1,3,0,1,1,1,2,2", string.Join(',', rangeValues),
        "range value is deterministic and inclusive");

    using JsonDocument fractionalMultiplyDocument = JsonDocument.Parse("""
        {"Action":{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":1.5},"AttributeType":"DamageAmount","Operation":"Multiply","Duration":{"$type":"TDeterminantDuration","DurationType":"UntilEndOfCombat"},"Target":{"$type":"TTargetCardSelf","Conditions":null}}}
        """);
    var fractionalActionDefinition = ConditionalCard(
        "fractional-action", "Small", 0) with
        {
            Attributes = new Dictionary<string, int>
            {
                ["DamageAmount"] = 5,
                ["Multicast"] = 1,
            },
        };
    CombatCardState fractionalActionCard = CombatCardState.Create(
        "fractional-action", fractionalActionDefinition, lifecyclePlayer, 10);
    ActionExecutionResult fractionalActionResult = CombatActionDispatcher.Execute(
        new MaterializedEffectDefinition(
            "fractional-action", "Ability", "test", fractionalMultiplyDocument.RootElement.Clone()),
        new CombatActionContext(lifecycleState, fractionalActionCard,
            new XorShiftCombatRandom(SeedMixer.Mix(205, 0))));
    AssertEqual(true, fractionalActionResult.Supported,
        "fractional action multiply is supported");
    AssertEqual(8, fractionalActionCard.Attributes.GetValueOrDefault("DamageAmount"),
        "fractional action multiply rounds product away from zero");

    using JsonDocument fractionalTimedMultiplyDocument = JsonDocument.Parse("""
        {"Action":{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":0.5},"AttributeType":"DamageAmount","Operation":"Multiply","Duration":{"$type":"TCombatDuration","DurationInMs":100},"Target":{"$type":"TTargetCardSelf","Conditions":null}}}
        """);
    CombatActionDispatcher.Execute(
        new MaterializedEffectDefinition(
            "fractional-timed", "Ability", "test",
            fractionalTimedMultiplyDocument.RootElement.Clone()),
        new CombatActionContext(lifecycleState, fractionalActionCard,
            new XorShiftCombatRandom(SeedMixer.Mix(206, 0))));
    AssertEqual(4, fractionalActionCard.Attributes.GetValueOrDefault("DamageAmount"),
        "timed fractional multiply preserves the multiplier");
    AssertEqual(8, fractionalActionCard.IntrinsicAttributes.GetValueOrDefault("DamageAmount"),
        "timed fractional multiply does not overwrite intrinsic value");

    using JsonDocument precomputedCooldownReductionDocument = JsonDocument.Parse("""
        {"Action":{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":50},"AttributeType":"PercentCooldownReduction","Operation":"Add","Duration":{"$type":"TDeterminantDuration","DurationType":"UntilEndOfCombat"},"Target":{"$type":"TTargetCardSelf","Conditions":null}}}
        """);
    var precomputedReductionDefinition = new MaterializedCardDefinition(
        "precomputed-reduction", "precomputed-reduction", "TCardItem", "Small",
        "Diamond", null,
        new Dictionary<string, int>
        {
            ["CooldownMax"] = 5339,
            ["PercentCooldownReduction"] = 11,
        },
        new HashSet<string>(), new HashSet<string>(), []);
    CombatCardState precomputedReductionCard = CombatCardState.Create(
        "precomputed-reduction", precomputedReductionDefinition, lifecyclePlayer, 4);
    precomputedReductionCard.AttributesArePrecomputed = true;
    precomputedReductionCard.CooldownRemainingMilliseconds = 1439;
    CombatActionDispatcher.Execute(
        new MaterializedEffectDefinition(
            "precomputed-reduction", "Ability", "test",
            precomputedCooldownReductionDocument.RootElement.Clone()),
        new CombatActionContext(lifecycleState, precomputedReductionCard,
            new XorShiftCombatRandom(SeedMixer.Mix(210, 0))));
    AssertEqual(2339,
        precomputedReductionCard.Attributes.GetValueOrDefault("CooldownMax"),
        "precomputed cooldown maximum follows dynamic percent reduction");
    AssertEqual(0, precomputedReductionCard.CooldownRemainingMilliseconds,
        "precomputed cooldown remainder follows maximum reduction");

    using JsonDocument percentCooldownAuraDocument = JsonDocument.Parse("""
        {"Action":{"$type":"TAuraActionCardModifyAttribute","AttributeType":"PercentCooldownReduction","Operation":"Add","Value":{"$type":"TReferenceValueCardAttribute","AttributeType":"Custom_0","Target":{"$type":"TTargetCardSelf","Conditions":null},"DefaultValue":0,"Modifier":null},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":false,"Conditions":null}}}
        """);
    using JsonDocument flatCooldownAuraDocument = JsonDocument.Parse("""
        {"Action":{"$type":"TAuraActionCardModifyAttribute","AttributeType":"FlatCooldownReduction","Operation":"Add","Value":{"$type":"TReferenceValueCardAttribute","AttributeType":"Custom_1","Target":{"$type":"TTargetCardSelf","Conditions":null},"DefaultValue":0,"Modifier":null},"Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":false,"Conditions":null}}}
        """);
    var cooldownAuraDefinition = new MaterializedCardDefinition(
        "cooldown-aura-source", "cooldown-aura-source", "TCardSkill", "Small",
        "Diamond", null,
        new Dictionary<string, int> { ["Custom_0"] = 20, ["Custom_1"] = 1000 },
        new HashSet<string>(), new HashSet<string>(),
        [
            new MaterializedEffectDefinition(
                "percent-cooldown-aura", "Aura", "test",
                percentCooldownAuraDocument.RootElement.Clone()),
            new MaterializedEffectDefinition(
                "flat-cooldown-aura", "Aura", "test",
                flatCooldownAuraDocument.RootElement.Clone()),
        ]);
    var cooldownAuraTargetDefinition = new MaterializedCardDefinition(
        "cooldown-aura-target", "cooldown-aura-target", "TCardItem", "Small",
        "Diamond", null,
        new Dictionary<string, int>
        {
            ["CooldownMax"] = 7200,
            ["PercentCooldownReduction"] = 20,
            ["FlatCooldownReduction"] = 1000,
        },
        new HashSet<string>(), new HashSet<string>(), []);
    var cooldownAuraState = new CombatState { CardAttributesArePrecomputed = true };
    var cooldownAuraPlayer = new CombatantState
        { Id = "cooldown-aura-player", MaxHealth = 100, Health = 100 };
    cooldownAuraState.Combatants.Add(cooldownAuraPlayer);
    cooldownAuraState.Combatants.Add(new CombatantState
        { Id = "cooldown-aura-opponent", MaxHealth = 100, Health = 100 });
    CombatCardState cooldownAuraSource = CombatCardState.Create(
        "cooldown-aura-source", cooldownAuraDefinition, cooldownAuraPlayer, 0, "Skills");
    CombatCardState cooldownAuraTarget = CombatCardState.Create(
        "cooldown-aura-target", cooldownAuraTargetDefinition, cooldownAuraPlayer, 0);
    cooldownAuraSource.AttributesArePrecomputed = true;
    cooldownAuraTarget.AttributesArePrecomputed = true;
    cooldownAuraTarget.CooldownRemainingMilliseconds = 1200;
    var cooldownAuras = new CombatAuraRuntime(
        cooldownAuraState, new XorShiftCombatRandom(SeedMixer.Mix(211, 0)));
    cooldownAuras.Recompute();
    AssertEqual(7200, cooldownAuraTarget.Attributes.GetValueOrDefault("CooldownMax"),
        "initial baked cooldown auras do not double-adjust maximum");
    AssertEqual(1200, cooldownAuraTarget.CooldownRemainingMilliseconds,
        "initial baked cooldown auras do not double-adjust remainder");
    cooldownAuraSource.IsDisabled = true;
    cooldownAuras.Recompute();
    AssertEqual(10000, cooldownAuraTarget.Attributes.GetValueOrDefault("CooldownMax"),
        "disabled cooldown aura source restores precomputed maximum");
    AssertEqual(4000, cooldownAuraTarget.CooldownRemainingMilliseconds,
        "disabled cooldown aura source shifts active remainder");
    cooldownAuraSource.IsDisabled = false;
    cooldownAuras.Recompute();
    AssertEqual(7200, cooldownAuraTarget.Attributes.GetValueOrDefault("CooldownMax"),
        "reenabled cooldown aura source reapplies precomputed maximum");
    AssertEqual(1200, cooldownAuraTarget.CooldownRemainingMilliseconds,
        "reenabled cooldown aura source shifts active remainder");
    var runtimeCooldownDefinition = new MaterializedCardDefinition(
        "runtime-cooldown", "runtime-cooldown", "TCardItem", "Small", "Diamond",
        null,
        new Dictionary<string, int> { ["CooldownMax"] = 10000 },
        new HashSet<string>(), new HashSet<string>(), []);
    CombatCardState runtimeCooldownCard = CombatCardState.Create(
        "runtime-cooldown", runtimeCooldownDefinition, cooldownAuraPlayer, 1);
    runtimeCooldownCard.CooldownRemainingMilliseconds = 5000;
    runtimeCooldownCard.AdjustCooldownForReductionTransition(0, 0, 20, 1000);
    AssertEqual(10000,
        runtimeCooldownCard.Attributes.GetValueOrDefault("CooldownMax"),
        "runtime card keeps intrinsic cooldown maximum across reduction transition");
    AssertEqual(2200, runtimeCooldownCard.CooldownRemainingMilliseconds,
        "runtime card shifts remainder by effective cooldown delta");

    var minimumCooldownDefinition = new MaterializedCardDefinition(
        "minimum-cooldown", "minimum-cooldown", "TCardItem", "Small", "Diamond",
        null,
        new Dictionary<string, int> { ["CooldownMax"] = 7000 },
        new HashSet<string>(), new HashSet<string>(), []);
    CombatCardState minimumCooldownCard = CombatCardState.Create(
        "minimum-cooldown", minimumCooldownDefinition, cooldownAuraPlayer, 2);
    minimumCooldownCard.AttributesArePrecomputed = true;
    minimumCooldownCard.CooldownRemainingMilliseconds = 1000;
    minimumCooldownCard.AdjustCooldownForReductionTransition(0, 0, 0, 7000);
    AssertEqual(500,
        minimumCooldownCard.Attributes.GetValueOrDefault("CooldownMax"),
        "active item cooldown reduction clamps to worker 500ms minimum");
    AssertEqual(500, minimumCooldownCard.GetEffectiveCooldownMilliseconds(),
        "active item remains schedulable at minimum cooldown");
    AssertEqual(0, minimumCooldownCard.CooldownRemainingMilliseconds,
        "minimum cooldown transition can make the current use immediately ready");

    var passiveCooldownDefinition = new MaterializedCardDefinition(
        "passive-cooldown", "passive-cooldown", "TCardItem", "Small", "Diamond",
        null,
        new Dictionary<string, int> { ["CooldownMax"] = 0 },
        new HashSet<string>(), new HashSet<string>(), []);
    CombatCardState passiveCooldownCard = CombatCardState.Create(
        "passive-cooldown", passiveCooldownDefinition, cooldownAuraPlayer, 3);
    AssertEqual(0, passiveCooldownCard.GetEffectiveCooldownMilliseconds(),
        "passive item remains unscheduled at zero cooldown");

    MaterializedCardDefinition coolant = catalog
        .Get("d576f5e8-abfb-44cb-a777-be7cf714e02d").Materialize("Diamond");
    var removalState = new CombatState();
    var removalPlayer = new CombatantState
    {
        Id = "removal-player", MaxHealth = 100, Health = 100, Burn = 7,
    };
    var removalOpponent = new CombatantState
    {
        Id = "removal-opponent", MaxHealth = 100, Health = 100,
    };
    removalState.Combatants.Add(removalPlayer);
    removalState.Combatants.Add(removalOpponent);
    CombatCardState coolantCard = CombatCardState.Create(
        "coolant", coolant, removalPlayer, 0);
    var removalRules = new CombatRuleRuntime(
        removalState, new XorShiftCombatRandom(SeedMixer.Mix(207, 0)));
    AssertEqual(4, coolantCard.Attributes.GetValueOrDefault("BurnRemoveAmount"),
        "Coolant aura rounds half-burn removal amount");
    AssertEqual(2, removalRules.FireCard(coolantCard),
        "Coolant executes freeze and burn-removal effects");
    AssertEqual(3, removalPlayer.Burn,
        "null-reference burn removal uses BurnRemoveAmount");
    AssertEqual(true, removalState.Events.Any(value =>
        value.Kind == "PlayerAttribute:Burn" && value.Amount == 3 &&
        value.SecondaryAmount == 7),
        "burn removal emits player attribute transition");

    using JsonDocument regenAuraDocument = JsonDocument.Parse("""
        {"Action":{"$type":"TAuraActionPlayerModifyAttribute","Value":{"$type":"TReferenceValueCardAttribute","AttributeType":"RegenApplyAmount","Target":{"$type":"TTargetCardSelf","Conditions":null},"DefaultValue":0,"Modifier":null},"AttributeType":"HealthRegen","Operation":"Add","Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self","Conditions":null}}}
        """);
    using JsonDocument regenRemoveDocument = JsonDocument.Parse("""
        {"Action":{"$type":"TActionPlayerRegenRemove","ReferenceValue":null,"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Self","Conditions":null}}}
        """);
    var regenAuraEffect = new MaterializedEffectDefinition(
        "regen-aura", "Aura", "test", regenAuraDocument.RootElement.Clone());
    var regenAuraDefinition = new MaterializedCardDefinition(
        "regen-aura-card", "regen-aura-card", "TCardSkill", "Small", "Diamond", null,
        new Dictionary<string, int> { ["RegenApplyAmount"] = 10 },
        new HashSet<string>(), new HashSet<string>(), [regenAuraEffect]);
    var regenRemoveDefinition = new MaterializedCardDefinition(
        "regen-remove-card", "regen-remove-card", "TCardItem", "Small", "Diamond", null,
        new Dictionary<string, int> { ["RegenRemoveAmount"] = 4 },
        new HashSet<string>(), new HashSet<string>(), []);
    var regenRemovalState = new CombatState();
    var regenRemovalPlayer = new CombatantState
        { Id = "regen-removal-player", MaxHealth = 100, Health = 100 };
    regenRemovalPlayer.SetIntrinsicAttribute("HealthRegen", 5);
    regenRemovalState.Combatants.Add(regenRemovalPlayer);
    regenRemovalState.Combatants.Add(new CombatantState
        { Id = "regen-removal-opponent", MaxHealth = 100, Health = 100 });
    CombatCardState.Create(
        "regen-aura-card", regenAuraDefinition, regenRemovalPlayer, 0, "Skills");
    CombatCardState regenRemoveCard = CombatCardState.Create(
        "regen-remove-card", regenRemoveDefinition, regenRemovalPlayer, 1);
    var regenRandom = new XorShiftCombatRandom(SeedMixer.Mix(208, 0));
    var regenAuras = new CombatAuraRuntime(regenRemovalState, regenRandom);
    regenAuras.Recompute();
    AssertEqual(15, regenRemovalPlayer.Regen,
        "regen aura is present before removal");
    CombatActionDispatcher.Execute(
        new MaterializedEffectDefinition(
            "regen-remove", "Ability", "test", regenRemoveDocument.RootElement.Clone()),
        new CombatActionContext(regenRemovalState, regenRemoveCard, regenRandom));
    regenAuras.Recompute();
    AssertEqual(1, regenRemovalPlayer.IntrinsicAttributes.GetValueOrDefault("HealthRegen"),
        "regen removal subtracts only intrinsic regen");
    AssertEqual(11, regenRemovalPlayer.Regen,
        "regen aura remains after intrinsic regen removal");
    AssertEqual(true, regenRemovalState.Events.Any(value =>
        value.Kind == "PlayerAttribute:Regen" && value.Amount == 11 &&
        value.SecondaryAmount == 15),
        "regen removal reports the effective transition with aura active");

    var conditionalState = new CombatState();
    var conditionalPlayer = new CombatantState
    {
        Id = "conditional-player", MaxHealth = 100, Health = 100,
    };
    conditionalState.Combatants.Add(conditionalPlayer);
    static MaterializedCardDefinition ConditionalCard(
        string id, string size, int power, params string[] tags) => new(
            id, id, "TCardItem", size, "Diamond", null,
            new Dictionary<string, int> { ["Power"] = power },
            tags.ToHashSet(StringComparer.Ordinal), new HashSet<string>(), []);
    CombatCardState conditionalA = CombatCardState.Create(
        "conditional-a", ConditionalCard("conditional-a", "Small", 5, "A", "B"),
        conditionalPlayer, 0, span: 1);
    CombatCardState conditionalB = CombatCardState.Create(
        "conditional-b", ConditionalCard("conditional-b", "Medium", 5, "A"),
        conditionalPlayer, 1, span: 2);
    CombatCardState conditionalC = CombatCardState.Create(
        "conditional-c", ConditionalCard("conditional-c", "Large", 2, "C"),
        conditionalPlayer, 3, span: 3);
    var conditionalContext = new CombatActionContext(
        conditionalState, conditionalA,
        new XorShiftCombatRandom(SeedMixer.Mix(203, 0)));
    static string ResolveConditionalIds(string json, CombatActionContext context)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return string.Join(',', TargetResolver.ResolveCardTarget(
            document.RootElement, context, null).Select(card => card.InstanceId));
    }
    AssertEqual("conditional-a", ResolveConditionalIds(
        """{"$type":"TTargetCardSection","TargetSection":"SelfHand","Conditions":{"$type":"TCardConditionalTag","Tags":["A","B"],"Operator":"All"}}""",
        conditionalContext), "tag conditional All");
    AssertEqual("conditional-b,conditional-c", ResolveConditionalIds(
        """{"$type":"TTargetCardSection","TargetSection":"SelfHand","Conditions":{"$type":"TCardConditionalTag","Tags":["B"],"Operator":"None"}}""",
        conditionalContext), "tag conditional None");
    AssertEqual("conditional-a", ResolveConditionalIds(
        """{"$type":"TTargetCardSection","TargetSection":"SelfHand","Conditions":{"$type":"TCardConditionalAttributeHighest","AttributeType":"Power"}}""",
        conditionalContext), "attribute highest chooses first strict maximum");
    AssertEqual("conditional-c", ResolveConditionalIds(
        """{"$type":"TTargetCardSection","TargetSection":"SelfHand","Conditions":{"$type":"TCardConditionalAttributeLowest","AttributeType":"Power"}}""",
        conditionalContext), "attribute lowest chooses first minimum");
    AssertEqual("conditional-c", ResolveConditionalIds(
        """{"$type":"TTargetCardSection","TargetSection":"SelfHand","Conditions":{"$type":"TCardConditionalSizeLargest"}}""",
        conditionalContext), "largest-size conditional");
    conditionalA.SetIntrinsicAttribute("Power", 0);
    conditionalB.SetIntrinsicAttribute("Power", 0);
    conditionalC.SetIntrinsicAttribute("Power", 0);
    AssertEqual(string.Empty, ResolveConditionalIds(
        """{"$type":"TTargetCardSection","TargetSection":"SelfHand","Conditions":{"$type":"TCardConditionalAttributeHighest","AttributeType":"Power"}}""",
        conditionalContext), "attribute highest rejects an all-zero set");
    _ = CombatCardState.Create(
        "conditional-socket",
        ConditionalCard("conditional-socket", "Small", 0) with
            { Type = "TCardMusicNoteSocketEffect" },
        conditionalPlayer, 10, "Hand");
    _ = CombatCardState.Create(
        "conditional-stash", ConditionalCard("conditional-stash", "Small", 0),
        conditionalPlayer, 11, "Stash");
    AssertEqual(
        "conditional-a,conditional-b,conditional-c,conditional-stash",
        ResolveConditionalIds(
            """{"$type":"TTargetCardSection","TargetSection":"AbsolutePlayerHandAndStash","Conditions":null}""",
            conditionalContext),
        "absolute hand-and-stash excludes socket-effect containers");

    using JsonDocument actionAndDocument = JsonDocument.Parse("""
        {"Id":"and","Trigger":{"$type":"TTriggerOnCardFired"},"Action":{"$type":"TActionAnd","Actions":[{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":2},"AttributeType":"Custom_1","Operation":"Add","Target":{"$type":"TTargetCardSelf","Conditions":null}},{"$type":"TActionCardModifyAttribute","Value":{"$type":"TFixedValue","Value":3},"AttributeType":"Custom_2","Operation":"Add","Target":{"$type":"TTargetCardSelf","Conditions":null}}],"Cost":null},"Prerequisites":null,"Priority":"Medium"}
        """);
    var andState = new CombatState();
    var andPlayer = new CombatantState { Id = "and-player", MaxHealth = 100, Health = 100 };
    andState.Combatants.Add(andPlayer);
    CombatCardState andCard = CombatCardState.Create("and-card",
        TestCardWithEffect("and-card", "TCardItem", actionAndDocument.RootElement), andPlayer, 0);
    var andRules = new CombatRuleRuntime(
        andState, new XorShiftCombatRandom(SeedMixer.Mix(147, 0)));
    AssertEqual(1, andRules.FireCard(andCard), "composite action dispatch count");
    AssertEqual(2, andCard.Attributes.GetValueOrDefault("Custom_1"),
        "composite action first child");
    AssertEqual(3, andCard.Attributes.GetValueOrDefault("Custom_2"),
        "composite action second child");

    MaterializedCardDefinition virus = catalog
        .Get("020a0ec0-21e6-41af-899f-063573ba9ca5").Materialize("Diamond");
    var transformState = new CombatState { CardCatalog = catalog };
    var transformPlayer = new CombatantState { Id = "transform-player", MaxHealth = 100, Health = 100 };
    var transformOpponent = new CombatantState { Id = "transform-opponent", MaxHealth = 100, Health = 100 };
    transformState.Combatants.Add(transformPlayer);
    transformState.Combatants.Add(transformOpponent);
    CombatCardState virusCard = CombatCardState.Create(
        "virus", virus, transformPlayer, 0, "Hand", 1);
    CombatCardState virusTarget = CombatCardState.Create(
        "virus-target", aila, transformOpponent, 0, "Hand", 1);
    var transformRules = new CombatRuleRuntime(
        transformState, new XorShiftCombatRandom(SeedMixer.Mix(148, 0)));
    transformRules.FireCard(virusCard);
    AssertEqual(virus.TemplateId, virusTarget.Definition.TemplateId,
        "id-list transform replaces opponent card");

    MaterializedCardDefinition hologram = catalog
        .Get("1d354dcf-8736-4bab-9dc1-7d9054d6c4d4").Materialize("Diamond");
    var copyState = new CombatState { CardCatalog = catalog };
    var copyPlayer = new CombatantState { Id = "copy-player", MaxHealth = 100, Health = 100 };
    copyState.Combatants.Add(copyPlayer);
    CombatCardState copySource = CombatCardState.Create(
        "copy-source", hologram, copyPlayer, 0, "Hand", 2);
    MaterializedCardDefinition ailaGold = catalog
        .Get("00ab28d4-c3d2-420e-ba71-b88bc29f4834")
        .Materialize("Gold");
    CombatCardState copyModel = CombatCardState.Create(
        "copy-model", ailaGold, copyPlayer, 2, "Hand", 1);
    copyModel.SetIntrinsicAttribute("Custom_9", 777);
    var copyRules = new CombatRuleRuntime(
        copyState, new XorShiftCombatRandom(SeedMixer.Mix(149, 0)));
    copyRules.StartFight();
    AssertEqual(ailaGold.TemplateId, copySource.Definition.TemplateId,
        "target-filter transform copies selected card definition");
    AssertEqual("Gold", copySource.Definition.Tier,
        "target-filter transform copies selected card tier");
    AssertEqual(777, copySource.IntrinsicAttributes.GetValueOrDefault("Custom_9"),
        "target-filter transform copies selected card intrinsic attributes");

    using JsonDocument splitTransformDocument = JsonDocument.Parse("""
        {"Id":"split-transform","Trigger":{"$type":"TTriggerOnCardFired"},"Action":{"$type":"TActionCardTransform","SpawnContext":{"$type":"TSpawnContextQuery","Groups":[{"$type":"TSpawnGroup","Filters":[{"$type":"TSpawnFilterIdList","Ids":["020a0ec0-21e6-41af-899f-063573ba9ca5"]}],"SelectionMethod":"Sequential","Limit":null,"Prerequisites":null,"RandomWeight":0,"Behaviors":null}],"SelectionMethod":"Sequential","Limit":{"$type":"TFixedValue","Value":2},"Behaviors":[{"$type":"TSpawnBehaviorInheritTier","Inherits":true}]},"Duration":{"$type":"TDeterminantDuration","DurationType":"UntilEndOfCombat"},"Abilities":null,"TargetCount":null,"Target":{"$type":"TTargetCardSelf","Conditions":null},"Cost":null},"Prerequisites":null,"Priority":"Medium"}
        """);
    var splitState = new CombatState { CardCatalog = catalog };
    var splitPlayer = new CombatantState { Id = "split-player", MaxHealth = 100, Health = 100 };
    splitState.Combatants.Add(splitPlayer);
    MaterializedCardDefinition splitDefinition = aila with
    {
        Size = "Medium",
        Effects = [new MaterializedEffectDefinition(
            "split-transform", "Ability", "test", splitTransformDocument.RootElement.Clone())],
    };
    CombatCardState splitSource = CombatCardState.Create(
        "split-source", splitDefinition, splitPlayer, 0, "Hand", 2);
    var splitRules = new CombatRuleRuntime(
        splitState, new XorShiftCombatRandom(SeedMixer.Mix(150, 0)));
    splitRules.FireCard(splitSource);
    AssertEqual(2, splitPlayer.Cards.Count,
        "medium transform can split into two small cards");
    AssertEqual(true, splitPlayer.Cards.All(card =>
        card.Definition.TemplateId == virus.TemplateId && card.Span == 1),
        "split transform materializes and positions every replacement");

    using JsonDocument emptyTransformDocument = JsonDocument.Parse("""
        {"Id":"empty-transform","Action":{"$type":"TActionCardTransform","SpawnContext":{"$type":"TSpawnContextQuery","Groups":[{"$type":"TSpawnGroup","Filters":[{"$type":"TSpawnFilterIdList","Ids":["missing-template"]}],"SelectionMethod":"Sequential","Limit":null,"Prerequisites":null,"RandomWeight":0,"Behaviors":null}],"SelectionMethod":"Sequential","Limit":{"$type":"TFixedValue","Value":1},"Behaviors":null},"Duration":null,"Abilities":null,"TargetCount":null,"Target":{"$type":"TTargetCardSelf","Conditions":null},"Cost":null}}
        """);
    ActionExecutionResult emptyTransformResult = CombatActionDispatcher.Execute(
        new MaterializedEffectDefinition(
            "empty-transform", "Ability", "test", emptyTransformDocument.RootElement.Clone()),
        new CombatActionContext(splitState, splitSource,
            new XorShiftCombatRandom(SeedMixer.Mix(151, 0))));
    AssertEqual(true, emptyTransformResult.Supported,
        "empty legal transform pool is a supported no-op");

    var frozenState = new CombatState();
    var frozenPlayer = new CombatantState { Id = "frozen-player", MaxHealth = 100, Health = 100 };
    var frozenOpponent = new CombatantState { Id = "frozen-opponent", MaxHealth = 100, Health = 100 };
    frozenState.Combatants.Add(frozenPlayer);
    frozenState.Combatants.Add(frozenOpponent);
    CombatCardState frozenAila = CombatCardState.Create("frozen-aila", aila, frozenPlayer, 0);
    var frozenRules = new CombatRuleRuntime(
        frozenState, new XorShiftCombatRandom(SeedMixer.Mix(140, 0)));
    var frozenScheduler = new CombatScheduler(frozenState, frozenRules);
    frozenScheduler.StartFight();
    frozenAila.SetIntrinsicAttribute("Charge", 5000);
    frozenAila.SetIntrinsicAttribute("Freeze", 100);
    frozenScheduler.AdvanceOneTick();
    frozenScheduler.AdvanceOneTick();
    AssertEqual(100, frozenOpponent.Health, "charged card remains blocked while frozen");
    frozenScheduler.AdvanceOneTick();
    AssertEqual(100, frozenOpponent.Health,
        "charged card fire defers its medium effect after freeze ends");
    AssertEqual(1, frozenState.Events.Count(value =>
        value.Kind == "CardUsed" && value.TargetId == "frozen-aila"),
        "excess charge does not carry into another cooldown");
    frozenScheduler.AdvanceOneTick();
    AssertEqual(20, frozenOpponent.Health,
        "charged card effect executes in the next scheduled phase");

    using JsonDocument readyChargeDocument = JsonDocument.Parse("""
        {"Action":{"$type":"TActionCardCharge","Value":{"$type":"TFixedValue","Value":1000},"Target":{"$type":"TTargetCardTriggerTarget","Conditions":null}}}
        """);
    using JsonDocument followupChargeDocument = JsonDocument.Parse("""
        {"Action":{"$type":"TActionCardCharge","Value":{"$type":"TFixedValue","Value":100},"Target":{"$type":"TTargetCardTriggerTarget","Conditions":null}}}
        """);
    var chargeInterleaveState = new CombatState();
    var chargeInterleavePlayer = new CombatantState
        { Id = "charge-interleave-player", MaxHealth = 100, Health = 100 };
    chargeInterleaveState.Combatants.Add(chargeInterleavePlayer);
    chargeInterleaveState.Combatants.Add(new CombatantState
        { Id = "charge-interleave-opponent", MaxHealth = 100, Health = 100 });
    CombatCardState chargeInterleaveSource = CombatCardState.Create(
        "charge-interleave-source",
        SchedulerTestCard("charge-interleave-source", 0, 0),
        chargeInterleavePlayer, 0, "Skills");
    CombatCardState chargeInterleaveTarget = CombatCardState.Create(
        "charge-interleave-target",
        SchedulerTestCard("charge-interleave-target", 0, 1000),
        chargeInterleavePlayer, 0);
    var chargeInterleaveRules = new CombatRuleRuntime(
        chargeInterleaveState,
        new XorShiftCombatRandom(SeedMixer.Mix(213, 0)));
    var chargeInterleaveScheduler = new CombatScheduler(
        chargeInterleaveState, chargeInterleaveRules);
    chargeInterleaveScheduler.StartFight();
    MaterializedEffectDefinition readyChargeEffect = new(
        "ready-charge", "Ability", "test", readyChargeDocument.RootElement.Clone());
    MaterializedEffectDefinition followupChargeEffect = new(
        "followup-charge", "Ability", "test", followupChargeDocument.RootElement.Clone());
    chargeInterleaveState.ScheduledRuleEffects.Add(new ScheduledRuleEffect(
        chargeInterleaveSource, readyChargeEffect, 1,
        chargeInterleaveSource, chargeInterleaveTarget, false, null));
    chargeInterleaveState.ScheduledRuleEffects.Add(new ScheduledRuleEffect(
        chargeInterleaveSource, followupChargeEffect, 1,
        chargeInterleaveSource, chargeInterleaveTarget, false, null));
    chargeInterleaveScheduler.AdvanceOneTick();
    AssertEqual(850, chargeInterleaveTarget.CooldownRemainingMilliseconds,
        "independent same-frame charge applies to the reset cooldown cycle");
    AssertEqual(1, chargeInterleaveState.ScheduledChargeReadyUses.Count,
        "charge-ready item use is deferred by one tick");
    chargeInterleaveScheduler.AdvanceOneTick();
    AssertEqual(1, chargeInterleaveState.Events.Count(value =>
        value.Kind == "CardUsed" && value.TargetId == "charge-interleave-target"),
        "charge-ready item uses without resetting the already charged new cycle");
    AssertEqual(800, chargeInterleaveTarget.CooldownRemainingMilliseconds,
        "charge-ready deferred use preserves later charge and tick progress");

    var critState = new CombatState();
    var critPlayer = new CombatantState { Id = "crit-player", MaxHealth = 100, Health = 100 };
    var critOpponent = new CombatantState { Id = "crit-opponent", MaxHealth = 100, Health = 100 };
    critState.Combatants.Add(critPlayer);
    critState.Combatants.Add(critOpponent);
    CombatCardState critAila = CombatCardState.Create("crit-aila", aila, critPlayer, 0);
    critAila.SetIntrinsicAttribute("CritChance", 100);
    var critRules = new CombatRuleRuntime(
        critState, new XorShiftCombatRandom(SeedMixer.Mix(18, 0)));
    AssertEqual(1, critRules.FireCard(critAila), "critical effect count");
    AssertEqual(-60, critOpponent.Health, "critical damage multiplier");
    AssertEqual(1, critState.Events.Count(value => value.Kind == "CardCrit"), "critical event count");

    MaterializedCardDefinition rifle = catalog
        .Get("0591d8b4-2632-4c41-9f73-48896237256d")
        .Materialize("Bronze");
    var ammoState = new CombatState();
    var ammoPlayer = new CombatantState { Id = "ammo-player", MaxHealth = 100, Health = 100 };
    var ammoOpponent = new CombatantState { Id = "ammo-opponent", MaxHealth = 100, Health = 100 };
    ammoState.Combatants.Add(ammoPlayer);
    ammoState.Combatants.Add(ammoOpponent);
    CombatCardState rifleState = CombatCardState.Create("rifle", rifle, ammoPlayer, 0);
    var ammoRules = new CombatRuleRuntime(
        ammoState, new XorShiftCombatRandom(SeedMixer.Mix(15, 0)));
    var ammoScheduler = new CombatScheduler(ammoState, ammoRules);
    ammoScheduler.StartFight();
    AssertEqual(1, ammoRules.FireCard(rifleState), "ammo first use");
    AssertEqual(0, ammoRules.FireCard(rifleState), "empty ammo prevents use");
    AssertEqual(50, ammoOpponent.Health, "ammo gated damage");
    AssertEqual(1, ammoState.Events.Count(value =>
        value.Kind == "CardAttribute:Ammo" && value.TargetId == "rifle" &&
        value.SecondaryAmount == 1 && value.Amount == 0),
        "ammo use emits loss attribute transition");
    AssertEqual(true, CombatSimulation.IsKeyTraceEvent(new CombatEvent(
        1, "CardAttribute:Ammo", "rifle", 0, 1, "rifle")),
        "internal ammo transition survives simulation key-trace filtering");
    AssertEqual(true, CombatSimulation.IsKeyTraceEvent(new CombatEvent(
        1, "Burn", "ammo-opponent", 7)),
        "periodic burn adjustment survives simulation key-trace filtering");
    AssertEqual(true, CombatSimulation.IsKeyTraceEvent(new CombatEvent(
        1, "Regen", "ammo-player", 4)),
        "periodic regen adjustment survives simulation key-trace filtering");
    using JsonDocument reloadDocument = JsonDocument.Parse("""
        {"Action":{"$type":"TActionCardReload","Value":{"$type":"TFixedValue","Value":99},"Target":{"$type":"TTargetCardSelf","Conditions":null}}}
        """);
    var reloadEffect = new MaterializedEffectDefinition(
        "reload", "Ability", "test", reloadDocument.RootElement.Clone());
    ActionExecutionResult reloadResult = CombatActionDispatcher.Execute(
        reloadEffect, new CombatActionContext(ammoState, rifleState,
            new XorShiftCombatRandom(SeedMixer.Mix(19, 0))));
    AssertEqual(true, reloadResult.Supported, "reload supported");
    AssertEqual(1, reloadResult.TargetCount, "reload changed target count");
    AssertEqual(1, rifleState.Attributes["Ammo"], "reload capped by ammo maximum");
    AssertEqual(1, ammoState.Events.Count(value =>
        value.Kind == "CardReload" && value.TargetId == "rifle"),
        "reload emits one action event");
    ActionExecutionResult fullReloadResult = CombatActionDispatcher.Execute(
        reloadEffect, new CombatActionContext(ammoState, rifleState,
            new XorShiftCombatRandom(SeedMixer.Mix(190, 0))));
    AssertEqual(0, fullReloadResult.TargetCount,
        "reload at full ammo has no changed target");
    AssertEqual(1, ammoState.Events.Count(value =>
        value.Kind == "CardReload" && value.TargetId == "rifle"),
        "reload at full ammo emits no action event");
    using JsonDocument noOpModifyDocument = JsonDocument.Parse("""
        {"Action":{"$type":"TActionCardModifyAttribute","AttributeType":"Slow","Operation":"Multiply","Value":{"$type":"TFixedValue","Value":0},"Target":{"$type":"TTargetCardSelf","Conditions":null}}}
        """);
    var noOpModifyEffect = new MaterializedEffectDefinition(
        "no-op-modify", "Ability", "test", noOpModifyDocument.RootElement.Clone());
    int noOpModifyEventsBefore = ammoState.Events.Count(value =>
        value.Kind == "CardModifyAttribute:Slow" && value.TargetId == "rifle");
    ActionExecutionResult noOpModifyResult = CombatActionDispatcher.Execute(
        noOpModifyEffect, new CombatActionContext(ammoState, rifleState,
            new XorShiftCombatRandom(SeedMixer.Mix(191, 0))));
    AssertEqual(1, noOpModifyResult.TargetCount,
        "card modify no-op retains resolved action target");
    AssertEqual(noOpModifyEventsBefore + 1, ammoState.Events.Count(value =>
        value.Kind == "CardModifyAttribute:Slow" && value.TargetId == "rifle" &&
        value.Amount == 0 && value.SecondaryAmount == 0),
        "card modify no-op retains action telemetry");
    AssertEqual(1, ammoRules.FireCard(rifleState), "reloaded ammo use");
    ammoOpponent.Health = 100;
    ammoState.ScheduledForceUses.Add(new ScheduledForceUse(rifleState, ammoState.Tick + 1));
    ammoScheduler.AdvanceOneTick();
    AssertEqual(100, ammoOpponent.Health, "force-use cannot fire an empty ammo item");
    AssertEqual(0, ammoState.Events.Count(value =>
        value.Tick == ammoState.Tick && value.Kind == "CardUsed" &&
        value.TargetId == "rifle"),
        "empty forced ammo produces no card-used signal");

    using JsonDocument forceUseDocument = JsonDocument.Parse("""
        {"Id":"force","Trigger":{"$type":"TTriggerOnCardFired"},"Action":{"$type":"TActionCardForceUse","Target":{"$type":"TTargetCardSection","TargetSection":"SelfHand","ExcludeSelf":true,"Conditions":{"$type":"TCardConditionalId","Id":"00ab28d4-c3d2-420e-ba71-b88bc29f4834"}}},"Prerequisites":null,"Priority":"Medium"}
        """);
    var forceUseDefinition = new MaterializedCardDefinition(
        "force-source", "Force Source", "TCardItem", "Small", "Diamond", null,
        new Dictionary<string, int> { ["Multicast"] = 1 },
        new HashSet<string>(), new HashSet<string>(),
        new[] { new MaterializedEffectDefinition(
            "force", "Ability", "test", forceUseDocument.RootElement.Clone()) });
    var forceState = new CombatState();
    var forcePlayer = new CombatantState { Id = "force-player", MaxHealth = 100, Health = 100 };
    var forceOpponent = new CombatantState { Id = "force-opponent", MaxHealth = 100, Health = 100 };
    forceState.Combatants.Add(forcePlayer);
    forceState.Combatants.Add(forceOpponent);
    CombatCardState forceSource = CombatCardState.Create("force-source", forceUseDefinition, forcePlayer, 0);
    CombatCardState.Create("force-aila", aila, forcePlayer, 1);
    var forceRules = new CombatRuleRuntime(
        forceState, new XorShiftCombatRandom(SeedMixer.Mix(20, 0)));
    AssertEqual(1, forceRules.FireCard(forceSource), "force-use scheduling effect count");
    var forceScheduler = new CombatScheduler(forceState, forceRules);
    forceScheduler.StartFight();
    forceScheduler.AdvanceOneTick();
    AssertEqual(20, forceOpponent.Health, "force-use card action executes on due tick");
    AssertEqual(1, forceState.Events.Single(value =>
        value.Kind == "CardDamage" && value.SourceId == "force-aila").Tick,
        "force-use action executes on the next official frame");

    var frozenForceState = new CombatState();
    var frozenForcePlayer = new CombatantState
        { Id = "frozen-force-player", MaxHealth = 100, Health = 100 };
    var frozenForceOpponent = new CombatantState
        { Id = "frozen-force-opponent", MaxHealth = 100, Health = 100 };
    frozenForceState.Combatants.Add(frozenForcePlayer);
    frozenForceState.Combatants.Add(frozenForceOpponent);
    CombatCardState frozenForceSource = CombatCardState.Create(
        "frozen-force-source", forceUseDefinition, frozenForcePlayer, 0);
    CombatCardState frozenForceTarget = CombatCardState.Create(
        "frozen-force-aila", aila, frozenForcePlayer, 1);
    var frozenForceRules = new CombatRuleRuntime(
        frozenForceState, new XorShiftCombatRandom(SeedMixer.Mix(200, 0)));
    AssertEqual(1, frozenForceRules.FireCard(frozenForceSource),
        "frozen force-use scheduling effect count");
    frozenForceTarget.SetIntrinsicAttribute("Freeze", 500);
    var frozenForceScheduler = new CombatScheduler(frozenForceState, frozenForceRules);
    frozenForceScheduler.StartFight();
    frozenForceScheduler.AdvanceOneTick();
    AssertEqual(100, frozenForceOpponent.Health,
        "queued force-use is discarded when target is frozen at execution");
    AssertEqual(1, frozenForceState.Events.Count(value =>
        value.Kind == "ForceUseBlockedByFreeze" && value.TargetId == "frozen-force-aila"),
        "blocked queued force-use diagnostic count");

    using JsonDocument prerequisiteDocument = JsonDocument.Parse("""
        {"Prerequisites":[{"$type":"TPrerequisiteCardCount","Subject":{"$type":"TTargetCardSelf","Conditions":{"$type":"TCardConditionalHasEnchantment","Enchantment":null,"IsNot":true}},"Comparison":"Equal","Amount":1}]}
        """);
    var prerequisiteEffect = new MaterializedEffectDefinition(
        "prerequisite", "Ability", "test", prerequisiteDocument.RootElement.Clone());
    AssertEqual(true, RulePrerequisiteEvaluator.AreSatisfied(
        prerequisiteEffect, new CombatActionContext(ruleState, glueState,
            new XorShiftCombatRandom(SeedMixer.Mix(16, 0)))), "enchanted card prerequisite");
    AssertEqual(false, RulePrerequisiteEvaluator.AreSatisfied(
        prerequisiteEffect, new CombatActionContext(ruleState, ailaState,
            new XorShiftCombatRandom(SeedMixer.Mix(17, 0)))), "unenchanted card prerequisite");

    MaterializedCardDefinition shinyPotion = catalog
        .Get("4fdc947b-3646-429e-ba4e-bb693fe44bc2")
        .Materialize("Silver", "Shiny");
    var playerEventState = new CombatState();
    var eventAttacker = new CombatantState { Id = "event-attacker", MaxHealth = 100, Health = 100 };
    var eventVictim = new CombatantState { Id = "event-victim", MaxHealth = 100, Health = 100 };
    playerEventState.Combatants.Add(eventAttacker);
    playerEventState.Combatants.Add(eventVictim);
    CombatCardState eventAila = CombatCardState.Create("event-aila", aila, eventAttacker, 0);
    CombatCardState potionState = CombatCardState.Create("shiny-potion", shinyPotion, eventVictim, 0);
    var playerEventRules = new CombatRuleRuntime(
        playerEventState, new XorShiftCombatRandom(SeedMixer.Mix(21, 0)));
    AssertEqual(3, playerEventRules.FireCard(eventAila), "health-loss force-use chain");
    AssertEqual(20, eventVictim.Health, "health-loss trigger victim health");
    AssertEqual(0, eventVictim.DamageReductionPercent,
        "health-loss force-use remains queued before its scheduled phase");
    AssertEqual(1, potionState.Attributes["Custom_7"], "same-batch immediate counter");
    var playerEventScheduler = new CombatScheduler(playerEventState, playerEventRules);
    playerEventScheduler.StartFight();
    for (int tick = 0; tick < 6; tick++)
    {
        playerEventScheduler.AdvanceOneTick();
    }
    AssertEqual(100, eventVictim.DamageReductionPercent,
        "force-used potion applies its timed player modifier after queued effects");
    AssertEqual(true, playerEventState.Events.Any(value =>
        value.Kind.StartsWith("PlayerModifyAttribute:", StringComparison.Ordinal) &&
        value.SourceId == "shiny-potion"),
        "player modifier diagnostics retain the action source card");

    MaterializedCardDefinition livingFlame = catalog
        .Get("ab4d7a85-3eb2-43e8-aa11-f94581f5690f").Materialize("Diamond");
    using JsonDocument thresholdDamageDocument = JsonDocument.Parse("""
        {"Id":"damage","Trigger":{"$type":"TTriggerOnCardFired"},"Action":{"$type":"TActionPlayerDamage","ReferenceValue":null,"Target":{"$type":"TTargetPlayerRelative","TargetMode":"Opponent","Conditions":null}},"Prerequisites":null,"Priority":"Medium"}
        """);
    var thresholdDamageEffect = new MaterializedEffectDefinition(
        "damage", "Ability", "test", thresholdDamageDocument.RootElement.Clone());
    var thresholdDamageDefinition = new MaterializedCardDefinition(
        "threshold-damage", "threshold-damage", "TCardItem", "Small", "Diamond", null,
        new Dictionary<string, int> { ["DamageAmount"] = 60 },
        new HashSet<string>(), new HashSet<string> { "Damage" }, [thresholdDamageEffect]);
    var thresholdBurnDefinition = new MaterializedCardDefinition(
        "threshold-burn", "threshold-burn", "TCardItem", "Small", "Diamond", null,
        new Dictionary<string, int>
        {
            ["CooldownMax"] = 4000,
            ["PercentCooldownReduction"] = 0,
        },
        new HashSet<string>(), new HashSet<string> { "Burn" }, []);
    var thresholdState = new CombatState();
    var thresholdPlayer = new CombatantState
        { Id = "threshold-player", MaxHealth = 100, Health = 100 };
    var thresholdEnemy = new CombatantState
        { Id = "threshold-enemy", MaxHealth = 100, Health = 100 };
    thresholdState.Combatants.Add(thresholdPlayer);
    thresholdState.Combatants.Add(thresholdEnemy);
    CombatCardState livingFlameState = CombatCardState.Create(
        "living-flame", livingFlame, thresholdPlayer, 0, "Skills");
    CombatCardState thresholdBurn = CombatCardState.Create(
        "threshold-burn", thresholdBurnDefinition, thresholdPlayer, 0);
    CombatCardState thresholdDamage = CombatCardState.Create(
        "threshold-damage", thresholdDamageDefinition, thresholdEnemy, 0);
    var thresholdRandom = new XorShiftCombatRandom(SeedMixer.Mix(209, 0));
    var thresholdRules = new CombatRuleRuntime(thresholdState, thresholdRandom);
    thresholdRules.FireCard(thresholdDamage);
    AssertEqual(40, thresholdPlayer.Health,
        "threshold damage crosses half health");
    AssertEqual(1, livingFlameState.Attributes.GetValueOrDefault("Custom_0"),
        "Living Flame immediate counter executes first");
    AssertEqual(0, thresholdBurn.Attributes.GetValueOrDefault("PercentCooldownReduction"),
        "Living Flame medium effect remains scheduled");
    var thresholdScheduler = new CombatScheduler(
        thresholdState, thresholdRules, thresholdRandom);
    thresholdScheduler.StartFight();
    thresholdScheduler.AdvanceOneTick();
    AssertEqual(50, thresholdBurn.Attributes.GetValueOrDefault("PercentCooldownReduction"),
        "scheduled prerequisite uses trigger-time eligibility");

    var tempoPhaseState = new CombatState();
    var tempoPhasePlayer = new CombatantState
    {
        Id = "tempo-phase-player",
        Hero = "Hero8",
        MaxHealth = 100,
        Health = 100,
        InitialTempoCooldownMilliseconds = 150,
    };
    tempoPhasePlayer.SetIntrinsicAttribute("TempoGainCooldownMax", 1000);
    var tempoPhaseOpponent = new CombatantState
    {
        Id = "tempo-phase-opponent",
        MaxHealth = 100,
        Health = 100,
    };
    tempoPhaseOpponent.SetIntrinsicAttribute("TempoGainCooldownMax", 999_999_999);
    tempoPhaseState.Combatants.Add(tempoPhasePlayer);
    tempoPhaseState.Combatants.Add(tempoPhaseOpponent);
    var tempoPhaseRandom = new XorShiftCombatRandom(SeedMixer.Mix(201, 0));
    var tempoPhaseScheduler = new CombatScheduler(
        tempoPhaseState,
        new CombatRuleRuntime(tempoPhaseState, tempoPhaseRandom),
        tempoPhaseRandom);
    tempoPhaseScheduler.StartFight();
    tempoPhaseScheduler.AdvanceOneTick();
    tempoPhaseScheduler.AdvanceOneTick();
    AssertEqual(0, tempoPhasePlayer.Attributes.GetValueOrDefault("Tempo"),
        "explicit opening Tempo remainder has not elapsed early");
    tempoPhaseScheduler.AdvanceOneTick();
    AssertEqual(1, tempoPhasePlayer.Attributes.GetValueOrDefault("Tempo"),
        "explicit opening Tempo remainder fires on its supplied tick");

    bool observedFullCooldownEndpoint = false;
    for (uint seed = 1; seed <= 256 && !observedFullCooldownEndpoint; seed++)
    {
        var endpointState = new CombatState();
        var endpointPlayer = new CombatantState
        {
            Id = "tempo-endpoint-player",
            Hero = "Hero8",
            MaxHealth = 100,
            Health = 100,
        };
        endpointPlayer.SetIntrinsicAttribute("TempoGainCooldownMax", 1000);
        var endpointOpponent = new CombatantState
        {
            Id = "tempo-endpoint-opponent",
            MaxHealth = 100,
            Health = 100,
        };
        endpointOpponent.SetIntrinsicAttribute("TempoGainCooldownMax", 999_999_999);
        endpointState.Combatants.Add(endpointPlayer);
        endpointState.Combatants.Add(endpointOpponent);
        var endpointRandom = new XorShiftCombatRandom(seed);
        var endpointScheduler = new CombatScheduler(
            endpointState,
            new CombatRuleRuntime(endpointState, endpointRandom),
            endpointRandom);
        endpointScheduler.StartFight();
        observedFullCooldownEndpoint =
            endpointPlayer.TempoCooldownRemainingMilliseconds == 1050;
    }
    AssertEqual(true, observedFullCooldownEndpoint,
        "seeded opening Tempo phase includes official frame-20 endpoint");

    BppSnapshotImportResult implicitCommonEffects = BppCombatSnapshotAdapter.ImportJson(
        """
        {"battle":{"id":"implicit-karnok","player_hero":"Karnok","opponent_hero":"Vanessa"},"combatants":[{"id":"player","hero":"Karnok","attributes":{"Health":100,"HealthMax":100,"Rage":0,"RageMax":100}},{"id":"opponent","hero":"Vanessa","attributes":{"Health":100,"HealthMax":100}}],"card_sets":[]}
        """,
        catalog);
    CombatCardState implicitBaseRage = implicitCommonEffects.State.Combatants[0].Cards
        .Single(card => card.Definition.TemplateId ==
            "4472da8a-26a3-4e10-bd9a-e93c2e22f19c");
    AssertEqual("Base Rage Effect", implicitBaseRage.Definition.Name,
        "combatant import materializes omitted common PlayerEffect");
    AssertEqual(3, implicitBaseRage.Definition.Effects.Count,
        "implicit Base Rage Effect includes reset abilities and cooldown aura");
    AssertEqual(2, implicitCommonEffects.State.Combatants
            .Sum(combatant => combatant.Cards.Count(card =>
                card.Definition.TemplateId ==
                    "4472da8a-26a3-4e10-bd9a-e93c2e22f19c")),
        "common Base Rage Effect is materialized for both heroes");
    AssertEqual(2, implicitCommonEffects.State.Combatants
            .Sum(combatant => combatant.Cards.Count(card =>
                card.Definition.TemplateId ==
                    "f74011cc-0f8b-462e-bc96-3a314afaa2af")),
        "common Gold Gained tracker is materialized for both heroes");

    var tourBusQuestAttributes = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["QuestCompletedCount"] = 2,
        ["Quest_5"] = 1,
        ["Quest_6"] = 1,
    };
    MaterializedCardDefinition questTourBus = catalog
        .Get("d1edc0f5-8c49-4c4a-b65b-6924a26888d9")
        .Materialize("Gold", runtimeAttributes: tourBusQuestAttributes);
    AssertEqual(true, questTourBus.Effects.Any(effect =>
        effect.Source == "quest:Quest_5" && effect.Id == "q5"),
        "completed quest ability is materialized");
    AssertEqual(true, questTourBus.Effects.Any(effect =>
        effect.Source == "quest:Quest_6" && effect.Id == "q6"),
        "completed quest aura is materialized");
    AssertEqual(true, questTourBus.Tags.Contains("Food"),
        "completed quest tag is materialized");

    var incompleteQuestAttributes = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["QuestCompletedCount"] = 0,
        ["Quest_1"] = 29,
        ["Quest_2"] = 24,
        ["Quest_3"] = 19,
    };
    MaterializedCardDefinition incompleteBlankSlate = catalog
        .Get("4a5b6c7d-8e9f-0a1b-2c3d-4e5f6a7b8c9d")
        .Materialize("Silver", runtimeAttributes: incompleteQuestAttributes);
    AssertEqual(false, incompleteBlankSlate.Effects.Any(effect =>
        effect.Source.StartsWith("quest:", StringComparison.Ordinal)),
        "positive quest progress below its target grants no reward");
    incompleteQuestAttributes["Quest_1"] = 30;
    MaterializedCardDefinition completedBlankSlate = catalog
        .Get("4a5b6c7d-8e9f-0a1b-2c3d-4e5f6a7b8c9d")
        .Materialize("Silver", runtimeAttributes: incompleteQuestAttributes);
    AssertEqual(true, completedBlankSlate.Effects.Any(effect =>
        effect.Source == "quest:Quest_1" && effect.Id == "q1"),
        "quest reaching its target grants reward even when completed count is zero");
    AssertEqual("CardForceUse", ActualCombatDifferential.MapLocalEventToAction("ForceUse")!,
        "actual differential force-use mapping");
    AssertEqual("FlyingStart", ActualCombatDifferential.MapLocalEventToAction("CardFlying")!,
        "actual differential flying mapping");
    ActualCombatDifferentialReport inMemoryDifferential =
        ActualCombatDifferential.CompareJson(
            """{"FrameCount":3,"Winner":"Player","Effects":[{"Frame":1,"Source":"card-a","ActionType":"PlayerDamage"}]}""",
            """{"Ticks":3,"WinnerId":"player","EventSummary":{"CardDamage":{"Count":1}},"KeyEventTrace":[{"Kind":"CardDamage","SourceId":"card-a"}]}""");
    AssertEqual(true, inMemoryDifferential.WinnerMatch,
        "in-memory actual differential winner");
    AssertEqual(1, inMemoryDifferential.ActualActionCounts["PlayerDamage"],
        "in-memory actual differential action count");
    AssertEqual(1, inMemoryDifferential.LocalSourceActionCounts["card-a|PlayerDamage"],
        "in-memory actual differential source action count");
    ActualCombatDifferentialReport noOpAttributeDifferential =
        ActualCombatDifferential.CompareJson(
            """{"FrameCount":2,"Winner":"Player","Effects":[{"Frame":0,"Source":"base-rage","ActionType":"CardModifyAttribute"},{"Frame":0,"Source":"base-rage","ActionType":"CardModifyAttribute"}],"CardAttributeChanges":[{"Frame":0,"CardId":"card-a","Attribute":"Slow","Previous":50,"Current":0}]}""",
            """{"Ticks":2,"WinnerId":"player","EventSummary":{"CardModifyAttribute:Slow":{"Count":2}},"KeyEventTrace":[{"Tick":1,"Kind":"CardModifyAttribute:Slow","TargetId":"card-a","Amount":0,"SecondaryAmount":0,"SourceId":"base-rage"},{"Tick":1,"Kind":"CardModifyAttribute:Slow","TargetId":"card-b","Amount":0,"SecondaryAmount":50,"SourceId":"base-rage"}]}""");
    AssertEqual(2, noOpAttributeDifferential.LocalActionCounts["CardModifyAttribute"],
        "no-op card modify remains in local action count");
    AssertEqual(1, noOpAttributeDifferential.LocalModifiedAttributeCounts["Slow"],
        "no-op card modify is excluded from changed-value count");
    ActualCombatDifferentialReport aggregatedStatusDifferential =
        ActualCombatDifferential.CompareJson(
            """{"FrameCount":2,"Winner":"Player","CardAttributeChanges":[{"Frame":0,"CardId":"card-a","Attribute":"Haste","Previous":1000,"Current":950},{"Frame":1,"CardId":"card-a","Attribute":"Haste","Previous":950,"Current":0},{"Frame":1,"CardId":"card-b","Attribute":"Ammo","Previous":2,"Current":1}]}""",
            """{"Ticks":2,"WinnerId":"player","EventSummary":{"CardHaste":{"Count":2}},"KeyEventTrace":[{"Tick":1,"Kind":"CardHaste","TargetId":"card-a","Amount":1000,"SecondaryAmount":0,"SourceId":"source-a"},{"Tick":1,"Kind":"CardHaste","TargetId":"card-a","Amount":2000,"SecondaryAmount":1000,"SourceId":"source-b"},{"Tick":2,"Kind":"CardAttribute:Ammo","TargetId":"card-b","Amount":1,"SecondaryAmount":2,"SourceId":"card-b"}]}""");
    AssertEqual(1, aggregatedStatusDifferential.ActualCardAttributeCounts["Haste"],
        "natural status countdown is excluded from official changed-value count");
    AssertEqual(1, aggregatedStatusDifferential.LocalModifiedAttributeCounts["Haste"],
        "same-tick local status changes aggregate to one net transition");
    AssertEqual(1, aggregatedStatusDifferential.LocalModifiedAttributeCounts["Ammo"],
        "internal ammo transition participates in changed-value count");
    AssertEqual(1, aggregatedStatusDifferential.ActualCardAttributeTargetCounts["card-a|Haste"],
        "official changed-value count retains the target card identity");
    AssertEqual(1, aggregatedStatusDifferential.LocalCardAttributeTargetCounts["card-a|Haste"],
        "same-tick local status aggregation retains the target card identity");
    AssertEqual(1, aggregatedStatusDifferential.ActualCardAttributeTargetCounts["card-b|Ammo"],
        "official ammo transition retains the target card identity");
    AssertEqual(1, aggregatedStatusDifferential.LocalCardAttributeTargetCounts["card-b|Ammo"],
        "local ammo transition retains the target card identity");
    AssertEqual(0, aggregatedStatusDifferential.CardAttributeTargetDeltas
        .Single(value => value.Kind == "card-a|Haste").Delta,
        "target-card haste differential matches after aggregation");
    AssertEqual(0, aggregatedStatusDifferential.CardAttributeTargetDeltas
        .Single(value => value.Kind == "card-b|Ammo").Delta,
        "target-card ammo differential matches");
    ActualCombatDifferentialReport healthAdjustmentDifferential =
        ActualCombatDifferential.CompareJson(
            """{"FrameCount":2,"Winner":"Player","HealthChanges":[{"Frame":0,"Side":"opponent","DamageType":"Damage","Attribute":"Health","Amount":-30},{"Frame":0,"Side":"opponent","DamageType":"Damage","Attribute":"Shield","Amount":-20},{"Frame":0,"Side":"player","DamageType":"Burn","Attribute":"Health","Amount":-7},{"Frame":0,"Side":"opponent","DamageType":"Burn","Attribute":"Shield","Amount":-5},{"Frame":1,"Side":"opponent","DamageType":"Regen","Attribute":"Health","Amount":4},{"Frame":1,"Side":"player","DamageType":"Poison","Attribute":"Health","Amount":-3},{"Frame":1,"Side":"player","DamageType":"Shield","Attribute":"Shield","Amount":8}]}""",
            """{"Ticks":2,"WinnerId":"player","KeyEventTrace":[{"Tick":1,"Kind":"CardDamage","TargetId":"opponent","Amount":30,"SecondaryAmount":20},{"Tick":1,"Kind":"Burn","TargetId":"player","Amount":7},{"Tick":1,"Kind":"BurnShield","TargetId":"opponent","SecondaryAmount":5},{"Tick":2,"Kind":"Regen","TargetId":"opponent","Amount":4},{"Tick":2,"Kind":"Poison","TargetId":"player","Amount":3},{"Tick":2,"Kind":"Shield","TargetId":"player","Amount":8}]}""");
    AssertEqual(7, healthAdjustmentDifferential.LocalHealthAdjustmentAmounts.Count,
        "local health adjustment projection covers damage and periodic lanes");
    AssertEqual(true, healthAdjustmentDifferential.HealthAdjustmentDeltas.All(
        value => value.Delta == 0),
        "actual and local health adjustment lanes match");
}

Console.WriteLine("self-test: core assertions passed");

static void AssertEqual<T>(T expected, T actual, string name)
    where T : IEquatable<T>
{
    if (!expected.Equals(actual))
    {
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
    }
}
