using System.Text.Json;

namespace BazaarLab.Combat;

public sealed record BppMonteCarloCase(
    string BattleId,
    string? Actual,
    string? Predicted,
    bool Match,
    int PlayerWins,
    int OpponentWins,
    int Draws,
    double PlayerWinRate,
    double PlayerOutcomeProbability,
    double DecisiveRate,
    double ConservativePlayerProbabilityLower95,
    double ConservativePlayerProbabilityUpper95,
    string? ConfidentPrediction,
    bool ConfidentMatch,
    IReadOnlyDictionary<string, int> UnsupportedActions,
    string? Error);

public sealed record BppMonteCarloReport(
    int Total,
    int SamplesPerBattle,
    int Decided,
    int Matches,
    double Accuracy,
    int ConfidentDecided,
    int ConfidentMatches,
    double ConfidentAccuracy,
    double BrierScore,
    double LogLoss,
    IReadOnlyDictionary<string, int> UnsupportedActions,
    IReadOnlyList<BppMonteCarloCase> Cases);

public sealed record BppPredictionResult(
    string BattleId,
    int Samples,
    int PlayerWins,
    int OpponentWins,
    int Draws,
    double PlayerWinRate,
    double PlayerOutcomeProbability,
    double DecisiveRate,
    double ConservativePlayerProbabilityLower95,
    double ConservativePlayerProbabilityUpper95,
    string? ConfidentPrediction,
    bool StoppedEarly,
    string? Predicted,
    IReadOnlyDictionary<string, int> UnsupportedActions,
    bool PredictionReady,
    IReadOnlyList<string> ValidationErrors,
    IReadOnlyList<string> ValidationWarnings,
    IReadOnlyList<string> SkippedCards);

public static class BppMonteCarloDifferential
{
    private readonly record struct FixedPredictionSample(
        string? BattleId,
        IReadOnlyList<string> SkippedCards,
        CombatSimulationOutcome Simulation);

    public static BppPredictionResult Predict(
        string snapshotPath,
        OfficialCardCatalog catalog,
        int baseSeed,
        int samples,
        int maximumTicks)
    {
        string json = File.ReadAllText(snapshotPath);
        return PredictJson(json, Path.GetFileNameWithoutExtension(snapshotPath), catalog,
            baseSeed, samples, maximumTicks);
    }

    public static BppPredictionResult PredictJson(
        string snapshotJson,
        string fallbackBattleId,
        OfficialCardCatalog catalog,
        int baseSeed,
        int samples,
        int maximumTicks) => PredictCore(
            snapshotJson, fallbackBattleId, catalog, baseSeed, samples, samples, samples,
            maximumTicks, adaptive: false);

    public static BppPredictionResult PredictAdaptive(
        string snapshotPath,
        OfficialCardCatalog catalog,
        int baseSeed,
        int minimumSamples,
        int maximumSamples,
        int batchSamples,
        int maximumTicks)
    {
        if (minimumSamples <= 0 || maximumSamples < minimumSamples || batchSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSamples),
                "adaptive samples require 0 < minimum <= maximum and batch > 0");
        }
        return PredictCore(File.ReadAllText(snapshotPath),
            Path.GetFileNameWithoutExtension(snapshotPath), catalog, baseSeed, minimumSamples,
            maximumSamples, batchSamples, maximumTicks, adaptive: true);
    }

    private static BppPredictionResult PredictCore(
        string snapshotJson,
        string fallbackBattleId,
        OfficialCardCatalog catalog,
        int baseSeed,
        int minimumSamples,
        int maximumSamples,
        int batchSamples,
        int maximumTicks,
        bool adaptive)
    {
        string battleId = fallbackBattleId;
        BppSnapshotValidationReport validation =
            BppSnapshotValidator.ValidateLiveJson(snapshotJson, catalog);
        if (!validation.PredictionReady)
        {
            return new BppPredictionResult(
                battleId, 0, 0, 0, 0,
                0, 0, 0, 0, 1, null, false, null,
                new Dictionary<string, int>(StringComparer.Ordinal),
                false, validation.Errors, validation.Warnings, Array.Empty<string>());
        }
        if (!adaptive)
        {
            return PredictFixedParallel(snapshotJson, catalog, baseSeed, maximumSamples,
                maximumTicks, battleId, validation);
        }
        int playerWins = 0;
        int opponentWins = 0;
        int draws = 0;
        int completedSamples = 0;
        bool stoppedEarly = false;
        var unsupported = new Dictionary<string, int>(StringComparer.Ordinal);
        var skippedCards = new HashSet<string>(StringComparer.Ordinal);
        for (int sample = 0; sample < maximumSamples; sample++)
        {
            BppSnapshotImportResult imported = BppCombatSnapshotAdapter.ImportJson(
                snapshotJson, catalog);
            battleId = imported.BattleId ?? battleId;
            foreach (string skippedCard in imported.SkippedCards)
            {
                skippedCards.Add(skippedCard);
            }
            CombatSimulationResult simulation = CombatSimulation.RunIndexed(
                imported.State, unchecked((uint)baseSeed), sample, maximumTicks);
            if (simulation.WinnerId == "player") playerWins++;
            else if (simulation.WinnerId == "opponent") opponentWins++;
            else draws++;
            completedSamples = sample + 1;
            foreach ((string action, int count) in simulation.UnsupportedActions)
            {
                unsupported[action] = unsupported.GetValueOrDefault(action) + count;
            }
            bool checkpoint = completedSamples == minimumSamples ||
                completedSamples == maximumSamples ||
                completedSamples > minimumSamples &&
                    (completedSamples - minimumSamples) % batchSamples == 0;
            if (adaptive && checkpoint && completedSamples >= minimumSamples &&
                ClassifyConfidence(playerWins, draws, completedSamples) is not null &&
                completedSamples < maximumSamples)
            {
                stoppedEarly = true;
                break;
            }
        }
        string? predicted = playerWins == opponentWins
            ? null
            : playerWins > opponentWins ? "win" : "loss";
        double playerWinRate = Rate(playerWins, completedSamples);
        (double lower, _) = WilsonInterval(playerWins, completedSamples);
        (_, double upper) = WilsonInterval(playerWins + draws, completedSamples);
        string[] runtimeErrors = skippedCards
            .Select(card => "skipped card: " + card)
            .Concat(unsupported.Keys.Select(action => "unsupported action: " + action))
            .ToArray();
        return new BppPredictionResult(
            battleId, completedSamples, playerWins, opponentWins, draws,
            playerWinRate,
            OutcomeProbability(playerWins, draws, completedSamples),
            Rate(playerWins + opponentWins, completedSamples),
            lower,
            upper,
            ClassifyConfidence(playerWins, draws, completedSamples),
            stoppedEarly,
            predicted, unsupported,
            runtimeErrors.Length == 0,
            runtimeErrors, validation.Warnings, skippedCards.ToArray());
    }

    private static BppPredictionResult PredictFixedParallel(
        string snapshotJson,
        OfficialCardCatalog catalog,
        int baseSeed,
        int samples,
        int maximumTicks,
        string fallbackBattleId,
        BppSnapshotValidationReport validation)
    {
        BppSnapshotImportResult imported = BppCombatSnapshotAdapter.ImportJson(
            snapshotJson, catalog);
        var results = new FixedPredictionSample[samples];
        Parallel.For(0, samples, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1,
                Math.Min(samples, Math.Min(16, Environment.ProcessorCount))),
        }, sample =>
        {
            CombatState state = CloneInitialState(imported.State);
            CombatSimulationOutcome simulation = CombatSimulation.RunOutcomeIndexed(
                state, unchecked((uint)baseSeed), sample, maximumTicks);
            results[sample] = new FixedPredictionSample(
                imported.BattleId, imported.SkippedCards, simulation);
        });

        string battleId = results.Select(value => value.BattleId)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? fallbackBattleId;
        int playerWins = 0;
        int opponentWins = 0;
        int draws = 0;
        var unsupported = new Dictionary<string, int>(StringComparer.Ordinal);
        var skippedCards = new HashSet<string>(StringComparer.Ordinal);
        foreach (FixedPredictionSample result in results)
        {
            if (result.Simulation.WinnerId == "player") playerWins++;
            else if (result.Simulation.WinnerId == "opponent") opponentWins++;
            else draws++;
            foreach (string skippedCard in result.SkippedCards)
                skippedCards.Add(skippedCard);
            foreach ((string action, int count) in result.Simulation.UnsupportedActions)
                unsupported[action] = unsupported.GetValueOrDefault(action) + count;
        }

        double playerWinRate = Rate(playerWins, samples);
        (double lower, _) = WilsonInterval(playerWins, samples);
        (_, double upper) = WilsonInterval(playerWins + draws, samples);
        string? predicted = playerWins == opponentWins
            ? null
            : playerWins > opponentWins ? "win" : "loss";
        string[] runtimeErrors = skippedCards
            .Select(card => "skipped card: " + card)
            .Concat(unsupported.Keys.Select(action => "unsupported action: " + action))
            .ToArray();
        return new BppPredictionResult(
            battleId, samples, playerWins, opponentWins, draws,
            playerWinRate,
            OutcomeProbability(playerWins, draws, samples),
            Rate(playerWins + opponentWins, samples),
            lower,
            upper,
            ClassifyConfidence(playerWins, draws, samples),
            false,
            predicted,
            unsupported,
            runtimeErrors.Length == 0,
            runtimeErrors,
            validation.Warnings,
            skippedCards.ToArray());
    }

    private static CombatState CloneInitialState(CombatState source)
    {
        var clone = new CombatState
        {
            CardCatalog = source.CardCatalog,
            CardAttributesArePrecomputed = source.CardAttributesArePrecomputed,
        };
        clone.Sandstorm.Enabled = source.Sandstorm.Enabled;
        var owners = new Dictionary<CombatantState, CombatantState>();
        foreach (CombatantState combatant in source.Combatants)
        {
            var copy = new CombatantState
            {
                Id = combatant.Id,
                Hero = combatant.Hero,
                AttributesArePrecomputed = combatant.AttributesArePrecomputed,
                MaxHealth = combatant.MaxHealth,
                Health = combatant.Health,
                Shield = combatant.Shield,
                Poison = combatant.Poison,
                Burn = combatant.Burn,
                Regen = combatant.Regen,
                DamageReductionPercent = combatant.DamageReductionPercent,
                FlatDamageReduction = combatant.FlatDamageReduction,
                InitialTempoCooldownMilliseconds = combatant.InitialTempoCooldownMilliseconds,
                TempoCooldownRemainingMilliseconds = combatant.TempoCooldownRemainingMilliseconds,
            };
            foreach ((string key, int value) in combatant.IntrinsicAttributes)
                copy.IntrinsicAttributes[key] = value;
            foreach ((string key, int value) in combatant.Attributes)
                copy.Attributes[key] = value;
            clone.Combatants.Add(copy);
            owners[combatant] = copy;
        }
        foreach (CombatantState combatant in source.Combatants)
        {
            foreach (CombatCardState card in combatant.Cards)
            {
                CombatCardState copy = CombatCardState.Create(card.InstanceId,
                    card.Definition, owners[combatant], card.BoardPosition,
                    card.Section, card.Span);
                copy.CooldownRemainingMilliseconds = card.CooldownRemainingMilliseconds;
                copy.IsDisabled = card.IsDisabled;
                copy.IsDestroyed = card.IsDestroyed;
                copy.AttributesArePrecomputed = card.AttributesArePrecomputed;
                copy.IntrinsicAttributes.Clear();
                copy.Attributes.Clear();
                copy.IntrinsicTags.Clear();
                copy.Tags.Clear();
                copy.IntrinsicHiddenTags.Clear();
                copy.HiddenTags.Clear();
                foreach ((string key, int value) in card.IntrinsicAttributes)
                    copy.IntrinsicAttributes[key] = value;
                foreach ((string key, int value) in card.Attributes)
                    copy.Attributes[key] = value;
                copy.IntrinsicTags.UnionWith(card.IntrinsicTags);
                copy.Tags.UnionWith(card.Tags);
                copy.IntrinsicHiddenTags.UnionWith(card.IntrinsicHiddenTags);
                copy.HiddenTags.UnionWith(card.HiddenTags);
            }
        }
        return clone;
    }

    public static BppMonteCarloReport Run(
        string directory,
        OfficialCardCatalog catalog,
        int baseSeed,
        int samples,
        int maximumTicks)
    {
        var cases = new List<BppMonteCarloCase>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.json")
            .OrderBy(value => value, StringComparer.Ordinal))
        {
            string battleId = Path.GetFileNameWithoutExtension(path);
            string? actual = null;
            int playerWins = 0;
            int opponentWins = 0;
            int draws = 0;
            var unsupported = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                for (int sample = 0; sample < samples; sample++)
                {
                    BppSnapshotImportResult imported = BppCombatSnapshotAdapter.Import(path, catalog);
                    battleId = imported.BattleId ?? battleId;
                    actual = imported.ActualResult;
                    CombatSimulationResult simulation = CombatSimulation.RunIndexed(
                        imported.State, unchecked((uint)baseSeed), sample, maximumTicks);
                    if (simulation.WinnerId == "player") playerWins++;
                    else if (simulation.WinnerId == "opponent") opponentWins++;
                    else draws++;
                    foreach ((string action, int count) in simulation.UnsupportedActions)
                    {
                        unsupported[action] = unsupported.GetValueOrDefault(action) + count;
                    }
                }
                string? predicted = playerWins == opponentWins
                    ? null
                    : playerWins > opponentWins ? "win" : "loss";
                double playerWinRate = Rate(playerWins, samples);
                double outcomeProbability = OutcomeProbability(
                    playerWins, draws, samples);
                double decisiveRate = Rate(playerWins + opponentWins, samples);
                (double lower, _) = WilsonInterval(playerWins, samples);
                (_, double upper) = WilsonInterval(playerWins + draws, samples);
                string? confidentPrediction = ClassifyConfidence(
                    playerWins, draws, samples);
                cases.Add(new BppMonteCarloCase(
                    battleId, actual, predicted,
                    predicted is not null && predicted == actual,
                    playerWins, opponentWins, draws,
                    playerWinRate,
                    outcomeProbability,
                    decisiveRate,
                    lower,
                    upper,
                    confidentPrediction,
                    confidentPrediction is not null && confidentPrediction == actual,
                    unsupported, null));
            }
            catch (Exception exception)
            {
                cases.Add(new BppMonteCarloCase(
                    battleId, actual, null, false, playerWins, opponentWins, draws,
                    Rate(playerWins, samples),
                    OutcomeProbability(playerWins, draws, samples),
                    Rate(playerWins + opponentWins, samples),
                    WilsonInterval(playerWins, samples).Lower,
                    WilsonInterval(playerWins + draws, samples).Upper,
                    null, false,
                    unsupported, exception.GetType().Name + ": " + exception.Message));
            }
        }
        int decided = cases.Count(value => value.Predicted is not null);
        int matches = cases.Count(value => value.Match);
        int confidentDecided = cases.Count(value => value.ConfidentPrediction is not null);
        int confidentMatches = cases.Count(value => value.ConfidentMatch);
        List<BppMonteCarloCase> scored = cases.Where(value =>
            value.Error is null && value.Actual is "win" or "loss").ToList();
        double brierScore = scored.Count == 0 ? 0 : scored.Average(value =>
        {
            double actualValue = value.Actual == "win" ? 1.0 : 0.0;
            double difference = value.PlayerOutcomeProbability - actualValue;
            return difference * difference;
        });
        double logLoss = scored.Count == 0 ? 0 : scored.Average(value =>
        {
            double probability = Math.Clamp(
                value.PlayerOutcomeProbability, 0.000001, 0.999999);
            return value.Actual == "win"
                ? -Math.Log(probability)
                : -Math.Log(1.0 - probability);
        });
        Dictionary<string, int> allUnsupported = cases
            .SelectMany(value => value.UnsupportedActions)
            .GroupBy(value => value.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(value => value.Value),
                StringComparer.Ordinal);
        return new BppMonteCarloReport(
            cases.Count, samples, decided, matches,
            decided == 0 ? 0 : (double)matches / decided,
            confidentDecided, confidentMatches,
            confidentDecided == 0 ? 0 : (double)confidentMatches / confidentDecided,
            brierScore, logLoss,
            allUnsupported, cases);
    }

    public static void Write(string path, BppMonteCarloReport report) =>
        File.WriteAllText(path, JsonSerializer.Serialize(
            report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

    public static BppMonteCarloReport Rescore(BppMonteCarloReport input)
    {
        BppMonteCarloCase[] cases = input.Cases.Select(value =>
        {
            string? confidence = ClassifyConfidence(
                value.PlayerWins, value.Draws, input.SamplesPerBattle);
            (double lower, _) = WilsonInterval(
                value.PlayerWins, input.SamplesPerBattle);
            (_, double upper) = WilsonInterval(
                value.PlayerWins + value.Draws, input.SamplesPerBattle);
            return value with
            {
                PlayerWinRate = Rate(value.PlayerWins, input.SamplesPerBattle),
                PlayerOutcomeProbability = OutcomeProbability(
                    value.PlayerWins, value.Draws, input.SamplesPerBattle),
                DecisiveRate = Rate(
                    value.PlayerWins + value.OpponentWins, input.SamplesPerBattle),
                ConservativePlayerProbabilityLower95 = lower,
                ConservativePlayerProbabilityUpper95 = upper,
                ConfidentPrediction = confidence,
                ConfidentMatch = confidence is not null && confidence == value.Actual,
            };
        }).ToArray();
        int decided = cases.Count(value => value.Predicted is not null);
        int matches = cases.Count(value => value.Match);
        int confidentDecided = cases.Count(value => value.ConfidentPrediction is not null);
        int confidentMatches = cases.Count(value => value.ConfidentMatch);
        BppMonteCarloCase[] scored = cases.Where(value =>
            value.Error is null && value.Actual is "win" or "loss").ToArray();
        double brier = scored.Length == 0 ? 0 : scored.Average(value =>
        {
            double expected = value.Actual == "win" ? 1 : 0;
            double difference = value.PlayerOutcomeProbability - expected;
            return difference * difference;
        });
        double logLoss = scored.Length == 0 ? 0 : scored.Average(value =>
        {
            double probability = Math.Clamp(
                value.PlayerOutcomeProbability, 0.000001, 0.999999);
            return value.Actual == "win"
                ? -Math.Log(probability)
                : -Math.Log(1 - probability);
        });
        return new BppMonteCarloReport(
            cases.Length,
            input.SamplesPerBattle,
            decided,
            matches,
            decided == 0 ? 0 : (double)matches / decided,
            confidentDecided,
            confidentMatches,
            confidentDecided == 0 ? 0 : (double)confidentMatches / confidentDecided,
            brier,
            logLoss,
            input.UnsupportedActions,
            cases);
    }

    internal static string? ClassifyConfidence(
        int playerWins,
        int draws,
        int samples)
    {
        if (samples <= 0)
        {
            return null;
        }
        // A timeout/draw has an unknown eventual winner. Use the 95% Wilson
        // interval after resolving every draw against the proposed direction.
        // A raw proportion crossing 65% is not sufficient evidence by itself.
        (double lowerPlayerProbability, _) = WilsonInterval(playerWins, samples);
        (_, double upperPlayerProbability) = WilsonInterval(playerWins + draws, samples);
        return lowerPlayerProbability >= 0.65
            ? "win"
            : upperPlayerProbability <= 0.35 ? "loss" : null;
    }

    internal static (double Lower, double Upper) WilsonInterval(
        int successes,
        int samples)
    {
        if (samples <= 0)
        {
            return (0, 1);
        }
        const double z = 1.959963984540054;
        double proportion = Math.Clamp((double)successes / samples, 0, 1);
        double zSquared = z * z;
        double denominator = 1 + zSquared / samples;
        double center = (proportion + zSquared / (2 * samples)) / denominator;
        double margin = z * Math.Sqrt(
            (proportion * (1 - proportion) + zSquared / (4 * samples)) / samples) /
            denominator;
        return (Math.Max(0, center - margin), Math.Min(1, center + margin));
    }

    internal static double OutcomeProbability(
        int playerWins,
        int draws,
        int samples) => samples <= 0
            ? 0
            : (playerWins + 0.5 * draws) / samples;

    private static double Rate(int count, int total) =>
        total <= 0 ? 0 : (double)count / total;
}
