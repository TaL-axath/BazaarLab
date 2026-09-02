using System.Diagnostics;

using System.Collections.Concurrent;

namespace BazaarLab.Combat;

public sealed record PlacementPin(string InstanceId, int BoardPosition);

public sealed class PlacementSearchOptions
{
    public int BoardMinimumPosition { get; init; }
    public int BoardMaximumPosition { get; init; } = 9;
    public IReadOnlyList<string>? ReplaceableItemInstanceIds { get; init; }
    public IReadOnlyList<string>? StashCandidateInstanceIds { get; init; }
    public IReadOnlyList<PlacementPin>? PinnedItems { get; init; }
    public bool RequireEqualSwapSpan { get; init; } = true;
    public int BaseSeed { get; init; } = 777;
    public int MaximumTicks { get; init; } = 2400;
    public int ScoreMaximumMilliseconds { get; init; } = 10_000;
    public int DiscoveryTimeBudgetMilliseconds { get; init; } = 3_000;
    public int Parallelism { get; init; } = Math.Max(1, Math.Min(8, Environment.ProcessorCount));
    public double DefenseWeightPercent { get; init; }
    public int ScreeningSamples { get; init; } = 1;
    public int DiscoverySamples { get; init; } = 3;
    public int ValidationSamples { get; init; } = 50;
    public int ValidationCandidateCount { get; init; } = 2;
    public int RacingCandidateCount { get; init; } = 64;
    public int BeamWidth { get; init; } = 16;
    public int BeamRounds { get; init; } = 4;
    public int MaxInventorySets { get; init; } = 32;
    public int MaxInventoryCombinationsScanned { get; init; } = 100_000;
    public int MaxEvaluatedArrangements { get; init; } = 10_000;
    public int ExactArrangementLimit { get; init; } = 50_000;
}

public sealed record PlacementSearchProgress(
    string Stage,
    double Fraction,
    string Message);

public sealed record PlacementBoardItem(
    string InstanceId,
    string Name,
    int BoardPosition,
    int Span,
    bool FromStash);

public sealed record PlacementCandidateScore(
    double Score,
    double PlayerOutcomeProbability,
    int PlayerWins,
    int OpponentWins,
    int Draws,
    int Samples,
    double AverageHealthMargin,
    double AverageDamageScore,
    double AverageDefenseScore,
    IReadOnlyDictionary<string, int> UnsupportedActions);

public sealed record PlacementRecommendation(
    IReadOnlyList<PlacementBoardItem> Board,
    IReadOnlyList<string> ReplacedItemInstanceIds,
    IReadOnlyList<string> SelectedStashCandidateInstanceIds,
    bool ReplacementApplied,
    PlacementCandidateScore DiscoveryScore,
    PlacementCandidateScore ValidationScore);

public sealed record PlacementSearchDiagnostics(
    string SearchMode,
    bool IsExact,
    long InventoryCombinationCount,
    bool InventoryEnumerationTruncated,
    int RetainedInventorySetCount,
    long ArrangementDomainCount,
    int EvaluatedArrangementCount,
    int EvaluatedInventorySetCount,
    int CandidateSimulationCount,
    int SearchRoundsCompleted,
    int BeamWidth,
    int RacingCandidateCount,
    int ValidationCandidateCount,
    int ValidationSimulationCount,
    int ScoreCacheHitCount,
    int ScoreCacheMissCount,
    double PreparationMilliseconds,
    double CandidateEvaluationMilliseconds,
    double ValidationMilliseconds,
    double TotalMilliseconds);

public sealed record PlacementSearchResult(
    PlacementRecommendation Recommendation,
    PlacementSearchDiagnostics Diagnostics);

public static class PlacementOptimizer
{
    private sealed record Item(
        string Id,
        string Name,
        int Span,
        int OriginalPosition,
        bool WasOnBoard,
        double StructuralScore);

    private sealed record InventorySet(
        IReadOnlyList<Item> Items,
        double StructuralScore,
        string Key);

    private sealed record Candidate(
        IReadOnlyList<Item> Items,
        IReadOnlyList<int> Positions,
        string Key);

    private sealed class ScoreAccumulator
    {
        public int PlayerWins;
        public int OpponentWins;
        public int Draws;
        public double HealthMargin;
        public double DamageScore;
        public double DefenseScore;
        public double DefenseWeightPercent;
        public Dictionary<string, int> Unsupported { get; } = new(StringComparer.Ordinal);
        public int Samples => PlayerWins + OpponentWins + Draws;
    }

    private sealed class SearchContext
    {
        public required CombatState Baseline { get; init; }
        public required CombatantState BaselinePlayer { get; init; }
        public required PlacementSearchOptions Options { get; init; }
        public required IReadOnlyDictionary<string, Item> Items { get; init; }
        public ConcurrentDictionary<string, ScoreAccumulator> Scores { get; } =
            new(StringComparer.Ordinal);
        public int SimulationCount;
        public int CacheHits;
        public int CacheMisses;
    }

    public static PlacementSearchResult Optimize(
        string snapshotPath,
        OfficialCardCatalog catalog,
        PlacementSearchOptions? options = null,
        CancellationToken cancellationToken = default,
        Action<PlacementSearchProgress>? progress = null) => OptimizeJson(
            File.ReadAllText(snapshotPath), catalog, options, cancellationToken, progress);

    public static PlacementSearchResult OptimizeJson(
        string snapshotJson,
        OfficialCardCatalog catalog,
        PlacementSearchOptions? options = null,
        CancellationToken cancellationToken = default,
        Action<PlacementSearchProgress>? progress = null)
    {
        options ??= new PlacementSearchOptions();
        ValidateOptions(options);
        Stopwatch totalWatch = Stopwatch.StartNew();
        Stopwatch phaseWatch = Stopwatch.StartNew();
        ReportProgress(progress, "preparing", 0.01, "正在读取阵容与卡牌目录");
        BppSnapshotValidationReport snapshotValidation =
            BppSnapshotValidator.ValidatePlacementJson(snapshotJson, catalog);
        if (!snapshotValidation.PredictionReady)
        {
            throw new InvalidDataException("placement snapshot is not prediction-ready: " +
                string.Join("; ", snapshotValidation.Errors));
        }
        BppSnapshotImportResult imported =
            BppCombatSnapshotAdapter.ImportJsonForPlacement(snapshotJson, catalog);
        CombatantState player = imported.State.Combatants.FirstOrDefault(value =>
            string.Equals(value.Id, "player", StringComparison.OrdinalIgnoreCase)) ??
            imported.State.Combatants[0];
        List<CombatCardState> boardCards = player.Cards.Where(value =>
            string.Equals(value.Section, "Hand", StringComparison.OrdinalIgnoreCase) &&
            !IsSocketEffect(value)).OrderBy(value => value.BoardPosition).ToList();
        List<CombatCardState> stashCards = player.Cards.Where(value =>
            string.Equals(value.Section, "Stash", StringComparison.OrdinalIgnoreCase) &&
            !IsSocketEffect(value)).OrderBy(value => value.BoardPosition).ToList();
        if (boardCards.Count == 0)
        {
            throw new InvalidDataException("player board contains no items");
        }

        Dictionary<string, Item> items = boardCards.Concat(stashCards).ToDictionary(
            value => value.InstanceId,
            value => new Item(
                value.InstanceId,
                string.IsNullOrWhiteSpace(value.Definition.Name)
                    ? value.InstanceId : value.Definition.Name,
                Math.Max(1, value.Span),
                value.BoardPosition,
                boardCards.Contains(value),
                StructuralScore(value)),
            StringComparer.Ordinal);
        var context = new SearchContext
        {
            Baseline = imported.State,
            BaselinePlayer = player,
            Options = options,
            Items = items,
        };

        HashSet<string> pinnedIds = new(
            options.PinnedItems?.Select(value => value.InstanceId) ?? [],
            StringComparer.Ordinal);
        Dictionary<string, int> pins = (options.PinnedItems ?? [])
            .GroupBy(value => value.InstanceId, StringComparer.Ordinal)
            .ToDictionary(value => value.Key, value => value.Single().BoardPosition,
                StringComparer.Ordinal);
        ValidatePins(pins, items, boardCards, options);
        HashSet<string> replaceable = options.ReplaceableItemInstanceIds is null
            ? new HashSet<string>(boardCards.Select(value => value.InstanceId), StringComparer.Ordinal)
            : new HashSet<string>(options.ReplaceableItemInstanceIds, StringComparer.Ordinal);
        replaceable.ExceptWith(pinnedIds);
        HashSet<string> stashCandidates = options.StashCandidateInstanceIds is null
            ? new HashSet<string>(stashCards.Select(value => value.InstanceId), StringComparer.Ordinal)
            : new HashSet<string>(options.StashCandidateInstanceIds, StringComparer.Ordinal);
        EnsureKnownIds(replaceable, boardCards, "replaceable board");
        EnsureKnownIds(stashCandidates, stashCards, "stash candidate");

        int originalSpan = boardCards.Sum(value => Math.Max(1, value.Span));
        int capacity = checked(options.BoardMaximumPosition - options.BoardMinimumPosition + 1);
        if (originalSpan > capacity)
        {
            throw new InvalidDataException("current board exceeds configured board capacity");
        }
        List<Item> fixedItems = boardCards
            .Where(value => !replaceable.Contains(value.InstanceId))
            .Select(value => items[value.InstanceId]).ToList();
        List<Item> optionalItems = boardCards
            .Where(value => replaceable.Contains(value.InstanceId))
            .Concat(stashCards.Where(value => stashCandidates.Contains(value.InstanceId)))
            .Select(value => items[value.InstanceId]).ToList();
        (List<InventorySet> inventories, long inventoryCount, bool inventoryTruncated) =
            GenerateInventorySets(fixedItems, optionalItems, boardCards, originalSpan,
                capacity, options, cancellationToken);
        ReportProgress(progress, "preparing", 0.04,
            $"已保留 {inventories.Count} 组上阵物品候选");

        var candidateMap = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        bool exact = !inventoryTruncated;
        long arrangementDomain = 0;
        foreach (InventorySet inventory in inventories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool complete = EnumeratePlacements(
                inventory.Items, pins, options, options.ExactArrangementLimit + 1,
                candidateMap, ref arrangementDomain, cancellationToken);
            if (!complete || candidateMap.Count > options.ExactArrangementLimit)
            {
                exact = false;
                break;
            }
        }
        if (!exact)
        {
            candidateMap.Clear();
            arrangementDomain = 0;
            List<List<Candidate>> seedsByInventory = inventories
                .Select(inventory => SeedPlacements(inventory.Items, pins, options).ToList())
                .ToList();
            int seedIndex = 0;
            bool added;
            do
            {
                added = false;
                foreach (List<Candidate> seeds in seedsByInventory)
                {
                    if (seedIndex >= seeds.Count) continue;
                    Candidate candidate = seeds[seedIndex];
                    candidateMap.TryAdd(candidate.Key, candidate);
                    arrangementDomain++;
                    added = true;
                }
                seedIndex++;
            }
            while (added);
        }
        bool candidateDomainEnumeratedExactly = exact;
        Candidate incumbent = CurrentBoardCandidate(boardCards, items, pins, options);
        candidateMap[incumbent.Key] = incumbent;
        double preparationMilliseconds = phaseWatch.Elapsed.TotalMilliseconds;

        phaseWatch.Restart();
        var evaluated = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        List<Candidate> discoveryOrder = ShuffleWithoutReplacement(
            candidateMap.Values.Where(value => value.Key != incumbent.Key), options.BaseSeed)
            .Take(Math.Max(0, options.MaxEvaluatedArrangements - 1)).ToList();
        discoveryOrder.Insert(0, incumbent);
        int discoveryTarget = discoveryOrder.Count;
        int batchSize = Math.Max(1, options.Parallelism * 2);
        for (int offset = 0; offset < discoveryOrder.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset > 0 && totalWatch.ElapsedMilliseconds >=
                    options.DiscoveryTimeBudgetMilliseconds)
            {
                break;
            }
            List<Candidate> batch = discoveryOrder.Skip(offset).Take(batchSize).ToList();
            EvaluateBatch(context, batch, options.ScreeningSamples, cancellationToken);
            foreach (Candidate candidate in batch) evaluated[candidate.Key] = candidate;
            double fraction = discoveryTarget == 0 ? 1d :
                Math.Min(1d, evaluated.Count / (double)discoveryTarget);
            ReportProgress(progress, "discovery", 0.05 + fraction * 0.60,
                $"发现阶段 {evaluated.Count}/{discoveryTarget} 个排列");
        }
        int rounds = 0;
        if (!exact && totalWatch.ElapsedMilliseconds < options.DiscoveryTimeBudgetMilliseconds)
        {
            for (int round = 0;
                 round < options.BeamRounds && evaluated.Count < options.MaxEvaluatedArrangements;
                 round++)
            {
                List<Candidate> beam = Rank(context, evaluated.Values)
                    .Take(options.BeamWidth).ToList();
                int before = evaluated.Count;
                var newCandidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
                foreach (Candidate parent in beam)
                {
                    foreach (Candidate neighbor in Neighbors(parent, pins, options))
                    {
                        if (evaluated.ContainsKey(neighbor.Key) ||
                            !newCandidates.TryAdd(neighbor.Key, neighbor))
                        {
                            continue;
                        }
                        if (evaluated.Count + newCandidates.Count >=
                            options.MaxEvaluatedArrangements)
                        {
                            break;
                        }
                    }
                    if (evaluated.Count + newCandidates.Count >=
                        options.MaxEvaluatedArrangements)
                    {
                        break;
                    }
                }
                List<Candidate> orderedNeighbors = ShuffleWithoutReplacement(
                    newCandidates.Values, options.BaseSeed + round + 1).ToList();
                for (int offset = 0; offset < orderedNeighbors.Count; offset += batchSize)
                {
                    if (totalWatch.ElapsedMilliseconds >=
                            options.DiscoveryTimeBudgetMilliseconds) break;
                    List<Candidate> batch = orderedNeighbors.Skip(offset)
                        .Take(batchSize).ToList();
                    EvaluateBatch(context, batch, options.ScreeningSamples,
                        cancellationToken);
                    foreach (Candidate candidate in batch)
                        evaluated[candidate.Key] = candidate;
                    ReportProgress(progress, "discovery", 0.65,
                        $"局部扩展第 {round + 1} 轮，已评估 {evaluated.Count} 个排列");
                }
                rounds++;
                if (evaluated.Count == before)
                {
                    break;
                }
            }
        }

        exact = exact && evaluated.Count == candidateMap.Count;

        List<Candidate> racing = IncludeIncumbent(
            Rank(context, evaluated.Values), incumbent, options.RacingCandidateCount);
        ReportProgress(progress, "racing", 0.68,
            $"竞速复筛 {racing.Count} 个候选");
        EvaluateBatch(context, racing, options.DiscoverySamples, cancellationToken);
        racing = Rank(context, racing);

        int semifinalSampleCount = Math.Min(options.ValidationSamples,
            Math.Max(options.DiscoverySamples, 11));
        List<Candidate> semifinal = IncludeIncumbent(racing, incumbent,
            Math.Min(16, racing.Count));
        ReportProgress(progress, "racing", 0.80,
            $"半决筛选 {semifinal.Count} 个候选，共同样本 {semifinalSampleCount}");
        EvaluateBatch(context, semifinal, semifinalSampleCount, cancellationToken);
        semifinal = Rank(context, semifinal);
        double candidateMilliseconds = phaseWatch.Elapsed.TotalMilliseconds;

        phaseWatch.Restart();
        List<Candidate> validation = TakeLeadersAndIncumbent(
            semifinal, incumbent, options.ValidationCandidateCount);
        Dictionary<string, PlacementCandidateScore> discoveryScores = validation.ToDictionary(
            value => value.Key,
            value => SnapshotScore(context.Scores[value.Key]),
            StringComparer.Ordinal);
        int simulationsBeforeValidation = context.SimulationCount;
        ReportProgress(progress, "validation", 0.90,
            $"最终验证 {validation.Count} 个候选，共同样本 {options.ValidationSamples}");
        EvaluateBatch(context, validation, options.ValidationSamples, cancellationToken);
        Candidate winner = Rank(context, validation).First();
        PlacementCandidateScore discoveryScore = discoveryScores[winner.Key];
        PlacementCandidateScore validationScore = SnapshotScore(context.Scores[winner.Key]);
        double validationMilliseconds = phaseWatch.Elapsed.TotalMilliseconds;
        ReportProgress(progress, "finalizing", 0.99, "正在生成推荐与移动目标");

        HashSet<string> originalIds = new(
            boardCards.Select(value => value.InstanceId), StringComparer.Ordinal);
        HashSet<string> winnerIds = new(
            winner.Items.Select(value => value.Id), StringComparer.Ordinal);
        string[] replaced = originalIds.Except(winnerIds).Order(StringComparer.Ordinal).ToArray();
        string[] selected = winnerIds.Except(originalIds).Order(StringComparer.Ordinal).ToArray();
        PlacementBoardItem[] resultBoard = winner.Items.Select((item, index) =>
            new PlacementBoardItem(item.Id, item.Name, winner.Positions[index],
                item.Span, !item.WasOnBoard)).OrderBy(value => value.BoardPosition).ToArray();
        var recommendation = new PlacementRecommendation(
            resultBoard, replaced, selected, replaced.Length > 0 || selected.Length > 0,
            discoveryScore, validationScore);
        var diagnostics = new PlacementSearchDiagnostics(
            exact ? "exact" : candidateDomainEnumeratedExactly
                ? "random-without-replacement-budgeted"
                : stashCandidates.Count > 0 ? "inventory-beam-local" : "beam-local",
            exact,
            inventoryCount,
            inventoryTruncated,
            inventories.Count,
            arrangementDomain,
            evaluated.Count,
            evaluated.Values.Select(value => string.Join('|', value.Items
                    .Select(item => item.Id).Order(StringComparer.Ordinal)))
                .Distinct(StringComparer.Ordinal).Count(),
            context.SimulationCount,
            rounds,
            options.BeamWidth,
            racing.Count,
            validation.Count,
            context.SimulationCount - simulationsBeforeValidation,
            context.CacheHits,
            context.CacheMisses,
            preparationMilliseconds,
            candidateMilliseconds,
            validationMilliseconds,
            totalWatch.Elapsed.TotalMilliseconds);
        ReportProgress(progress, "complete", 1.0,
            $"完成：评估 {evaluated.Count} 个排列，终验 {validationScore.Samples} 个样本");
        return new PlacementSearchResult(recommendation, diagnostics);
    }

    private static void ValidateOptions(PlacementSearchOptions value)
    {
        if (value.BoardMaximumPosition < value.BoardMinimumPosition ||
            value.ScreeningSamples <= 0 || value.DiscoverySamples < value.ScreeningSamples ||
            value.ValidationSamples < value.DiscoverySamples || value.MaximumTicks <= 0 ||
            value.ScoreMaximumMilliseconds <= 0 ||
            value.DiscoveryTimeBudgetMilliseconds <= 0 || value.Parallelism <= 0 ||
            value.DefenseWeightPercent < 0 ||
            value.MaxInventorySets <= 0 || value.MaxInventoryCombinationsScanned <= 0 ||
            value.MaxEvaluatedArrangements <= 0 || value.ExactArrangementLimit <= 0 ||
            value.BeamWidth <= 0 || value.RacingCandidateCount < 2 ||
            value.ValidationCandidateCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "invalid placement search options");
        }
    }

    private static void ValidatePins(
        IReadOnlyDictionary<string, int> pins,
        IReadOnlyDictionary<string, Item> items,
        IReadOnlyList<CombatCardState> board,
        PlacementSearchOptions options)
    {
        HashSet<string> boardIds = new(board.Select(value => value.InstanceId), StringComparer.Ordinal);
        foreach ((string id, int position) in pins)
        {
            if (!items.ContainsKey(id) || !boardIds.Contains(id))
            {
                throw new InvalidDataException($"pinned item is not on the current board: {id}");
            }
            Item item = items[id];
            if (position < options.BoardMinimumPosition ||
                position + item.Span - 1 > options.BoardMaximumPosition)
            {
                throw new InvalidDataException($"pinned item is outside the board: {id}");
            }
        }
        Item[] pinned = pins.Keys.Select(id => items[id]).ToArray();
        for (int i = 0; i < pinned.Length; i++)
        {
            for (int j = i + 1; j < pinned.Length; j++)
            {
                if (Overlaps(pins[pinned[i].Id], pinned[i].Span,
                    pins[pinned[j].Id], pinned[j].Span))
                {
                    throw new InvalidDataException("pinned items overlap");
                }
            }
        }
    }

    private static void EnsureKnownIds(
        IEnumerable<string> ids,
        IReadOnlyList<CombatCardState> cards,
        string kind)
    {
        HashSet<string> known = new(cards.Select(value => value.InstanceId), StringComparer.Ordinal);
        string? unknown = ids.FirstOrDefault(value => !known.Contains(value));
        if (unknown is not null)
        {
            throw new InvalidDataException($"unknown {kind} item: {unknown}");
        }
    }

    private static (List<InventorySet> Sets, long Count, bool Truncated) GenerateInventorySets(
        IReadOnlyList<Item> fixedItems,
        IReadOnlyList<Item> optionalItems,
        IReadOnlyList<CombatCardState> originalBoard,
        int originalSpan,
        int capacity,
        PlacementSearchOptions options,
        CancellationToken cancellationToken)
    {
        var retained = new Dictionary<string, InventorySet>(StringComparer.Ordinal);
        long count = 0;
        bool truncated = false;
        var selected = new List<Item>(fixedItems);
        void Visit(int index, int span)
        {
            if (truncated)
            {
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (index == optionalItems.Count)
            {
                if (span > capacity || span == 0 ||
                    options.RequireEqualSwapSpan && span != originalSpan)
                {
                    return;
                }
                if (count >= options.MaxInventoryCombinationsScanned)
                {
                    truncated = true;
                    return;
                }
                count++;
                Item[] ordered = selected.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
                string key = string.Join('|', ordered.Select(value => value.Id));
                double score = ordered.Sum(value => value.StructuralScore);
                retained[key] = new InventorySet(ordered, score, key);
                if (retained.Count > options.MaxInventorySets * 2)
                {
                    TrimInventorySets(retained, options.MaxInventorySets);
                }
                return;
            }
            Visit(index + 1, span);
            Item item = optionalItems[index];
            if (span + item.Span <= capacity)
            {
                selected.Add(item);
                Visit(index + 1, span + item.Span);
                selected.RemoveAt(selected.Count - 1);
            }
        }
        Visit(0, fixedItems.Sum(value => value.Span));

        Item[] incumbent = originalBoard.Select(value =>
            fixedItems.Concat(optionalItems).Single(item => item.Id == value.InstanceId)).ToArray();
        string incumbentKey = string.Join('|', incumbent.OrderBy(value => value.Id,
            StringComparer.Ordinal).Select(value => value.Id));
        retained[incumbentKey] = new InventorySet(
            incumbent, incumbent.Sum(value => value.StructuralScore), incumbentKey);
        TrimInventorySets(retained, options.MaxInventorySets, incumbentKey);
        return (retained.Values
            .OrderByDescending(value => value.StructuralScore)
            .ThenBy(value => value.Key, StringComparer.Ordinal).ToList(), count, truncated);
    }

    private static void TrimInventorySets(
        Dictionary<string, InventorySet> sets,
        int maximum,
        string? preserveKey = null)
    {
        HashSet<string> keep = sets.Values
            .OrderByDescending(value => value.StructuralScore)
            .ThenBy(value => value.Key, StringComparer.Ordinal)
            .Take(maximum).Select(value => value.Key).ToHashSet(StringComparer.Ordinal);
        if (preserveKey is not null)
        {
            keep.Add(preserveKey);
        }
        foreach (string key in sets.Keys.Where(value => !keep.Contains(value)).ToArray())
        {
            sets.Remove(key);
        }
    }

    private static bool EnumeratePlacements(
        IReadOnlyList<Item> items,
        IReadOnlyDictionary<string, int> pins,
        PlacementSearchOptions options,
        int limit,
        Dictionary<string, Candidate> output,
        ref long domain,
        CancellationToken cancellationToken)
    {
        int minimum = options.BoardMinimumPosition;
        int capacity = options.BoardMaximumPosition - minimum + 1;
        bool[] occupied = new bool[capacity];
        var positions = new List<int>();
        bool complete = true;
        long localDomain = 0;
        void Visit(int itemIndex)
        {
            if (!complete)
            {
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (itemIndex == items.Count)
            {
                localDomain++;
                Candidate candidate = CreateCandidate(items, positions);
                output.TryAdd(candidate.Key, candidate);
                if (output.Count >= limit)
                {
                    complete = false;
                }
                return;
            }
            Item item = items[itemIndex];
            IEnumerable<int> starts = pins.TryGetValue(item.Id, out int pin)
                ? [pin] : Enumerable.Range(minimum, capacity - item.Span + 1);
            foreach (int start in starts)
            {
                int local = start - minimum;
                if (Enumerable.Range(local, item.Span).Any(index => occupied[index]))
                {
                    continue;
                }
                for (int index = local; index < local + item.Span; index++) occupied[index] = true;
                positions.Add(start);
                Visit(itemIndex + 1);
                positions.RemoveAt(positions.Count - 1);
                for (int index = local; index < local + item.Span; index++) occupied[index] = false;
                if (!complete) return;
            }
        }
        Visit(0);
        domain += localDomain;
        return complete;
    }

    private static IEnumerable<Candidate> SeedPlacements(
        IReadOnlyList<Item> items,
        IReadOnlyDictionary<string, int> pins,
        PlacementSearchOptions options)
    {
        IEnumerable<IReadOnlyList<Item>> orders =
        [
            items.OrderBy(value => value.WasOnBoard ? value.OriginalPosition : int.MaxValue)
                .ThenBy(value => value.Id, StringComparer.Ordinal).ToArray(),
            items.OrderByDescending(value => value.StructuralScore)
                .ThenBy(value => value.Id, StringComparer.Ordinal).ToArray(),
            items.OrderByDescending(value => value.Span)
                .ThenBy(value => value.Id, StringComparer.Ordinal).ToArray(),
        ];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (IReadOnlyList<Item> order in orders)
        {
            foreach (int alignment in new[] { -1, 0, 1 })
            {
                Candidate? candidate = Pack(order, pins, options, alignment);
                if (candidate is not null && seen.Add(candidate.Key))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static Candidate CurrentBoardCandidate(
        IReadOnlyList<CombatCardState> board,
        IReadOnlyDictionary<string, Item> items,
        IReadOnlyDictionary<string, int> pins,
        PlacementSearchOptions options)
    {
        Item[] ordered = board.OrderBy(value => value.BoardPosition)
            .Select(value => items[value.InstanceId]).ToArray();
        int[] positions = board.OrderBy(value => value.BoardPosition)
            .Select(value => value.BoardPosition).ToArray();
        Candidate candidate = CreateCandidate(ordered, positions);
        if (!IsLegal(candidate, pins, options))
        {
            throw new InvalidDataException("current board is incompatible with pins or board bounds");
        }
        return candidate;
    }

    private static Candidate? Pack(
        IReadOnlyList<Item> order,
        IReadOnlyDictionary<string, int> pins,
        PlacementSearchOptions options,
        int alignment)
    {
        int capacity = options.BoardMaximumPosition - options.BoardMinimumPosition + 1;
        bool[] occupied = new bool[capacity];
        int[] positions = new int[order.Count];
        Array.Fill(positions, int.MinValue);
        for (int index = 0; index < order.Count; index++)
        {
            if (!pins.TryGetValue(order[index].Id, out int pin))
            {
                continue;
            }
            int local = pin - options.BoardMinimumPosition;
            if (local < 0 || local + order[index].Span > capacity ||
                Enumerable.Range(local, order[index].Span).Any(value => occupied[value]))
            {
                return null;
            }
            positions[index] = pin;
            for (int value = local; value < local + order[index].Span; value++)
                occupied[value] = true;
        }
        for (int index = 0; index < order.Count; index++)
        {
            if (positions[index] != int.MinValue)
            {
                continue;
            }
            IEnumerable<int> starts = Enumerable.Range(
                options.BoardMinimumPosition, capacity - order[index].Span + 1);
            starts = alignment switch
            {
                1 => starts.Reverse(),
                0 => starts.OrderBy(value => Math.Abs(
                    value - (options.BoardMinimumPosition + capacity / 2))),
                _ => starts,
            };
            int chosen = starts.FirstOrDefault(start =>
            {
                int local = start - options.BoardMinimumPosition;
                return !Enumerable.Range(local, order[index].Span)
                    .Any(value => occupied[value]);
            }, int.MinValue);
            if (chosen == int.MinValue)
            {
                return null;
            }
            positions[index] = chosen;
            int chosenLocal = chosen - options.BoardMinimumPosition;
            for (int value = chosenLocal; value < chosenLocal + order[index].Span; value++)
                occupied[value] = true;
        }
        Candidate candidate = CreateCandidate(order, positions);
        return IsLegal(candidate, pins, options) ? candidate : null;
    }

    private static IEnumerable<Candidate> Neighbors(
        Candidate source,
        IReadOnlyDictionary<string, int> pins,
        PlacementSearchOptions options)
    {
        var output = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        for (int index = 0; index + 1 < source.Items.Count; index++)
        {
            Item[] order = source.Items.ToArray();
            (order[index], order[index + 1]) = (order[index + 1], order[index]);
            foreach (int alignment in new[] { -1, 0, 1 })
            {
                Candidate? packed = Pack(order, pins, options, alignment);
                if (packed is not null) output.TryAdd(packed.Key, packed);
            }
        }
        for (int index = 0; index < source.Items.Count; index++)
        {
            foreach (int delta in new[] { -1, 1 })
            {
                int[] positions = source.Positions.ToArray();
                positions[index] += delta;
                Candidate shifted = CreateCandidate(source.Items, positions);
                if (IsLegal(shifted, pins, options)) output.TryAdd(shifted.Key, shifted);
            }
        }
        return output.Values;
    }

    private static Candidate CreateCandidate(
        IReadOnlyList<Item> items,
        IReadOnlyList<int> positions)
    {
        (Item Item, int Position)[] ordered = items.Zip(positions)
            .OrderBy(value => value.Second)
            .ThenBy(value => value.First.Id, StringComparer.Ordinal)
            .Select(value => (value.First, value.Second)).ToArray();
        string key = string.Join(';', ordered.Select(value => $"{value.Item.Id}@{value.Position}"));
        return new Candidate(ordered.Select(value => value.Item).ToArray(),
            ordered.Select(value => value.Position).ToArray(), key);
    }

    private static bool IsLegal(
        Candidate candidate,
        IReadOnlyDictionary<string, int> pins,
        PlacementSearchOptions options)
    {
        for (int index = 0; index < candidate.Items.Count; index++)
        {
            Item item = candidate.Items[index];
            int position = candidate.Positions[index];
            if (position < options.BoardMinimumPosition ||
                position + item.Span - 1 > options.BoardMaximumPosition ||
                pins.TryGetValue(item.Id, out int pin) && pin != position)
            {
                return false;
            }
            for (int other = index + 1; other < candidate.Items.Count; other++)
            {
                if (Overlaps(position, item.Span,
                    candidate.Positions[other], candidate.Items[other].Span))
                {
                    return false;
                }
            }
        }
        return pins.Keys.All(id => candidate.Items.Any(item => item.Id == id));
    }

    private static bool Overlaps(int first, int firstSpan, int second, int secondSpan) =>
        first < second + secondSpan && second < first + firstSpan;

    private static List<Candidate> Rank(SearchContext context, IEnumerable<Candidate> values) =>
        values.OrderByDescending(value => SnapshotScore(context.Scores[value.Key]).Score)
            .ThenBy(value => value.Key, StringComparer.Ordinal).ToList();

    private static List<Candidate> IncludeIncumbent(
        IReadOnlyList<Candidate> ranked,
        Candidate incumbent,
        int maximum)
    {
        List<Candidate> result = ranked.Take(Math.Max(0, maximum - 1)).ToList();
        if (!result.Any(value => value.Key == incumbent.Key))
        {
            result.Add(incumbent);
        }
        foreach (Candidate candidate in ranked)
        {
            if (result.Count >= maximum)
            {
                break;
            }
            if (!result.Any(value => value.Key == candidate.Key))
            {
                result.Add(candidate);
            }
        }
        return result;
    }

    private static List<Candidate> TakeLeadersAndIncumbent(
        IReadOnlyList<Candidate> ranked,
        Candidate incumbent,
        int leaderCount)
    {
        List<Candidate> result = ranked.Take(Math.Max(1, leaderCount)).ToList();
        if (!result.Any(value => value.Key == incumbent.Key)) result.Add(incumbent);
        return result;
    }

    private static List<Candidate> ShuffleWithoutReplacement(
        IEnumerable<Candidate> candidates,
        int seed)
    {
        List<Candidate> result = candidates.OrderBy(value => value.Key,
            StringComparer.Ordinal).ToList();
        var random = new Random(seed);
        for (int index = result.Count - 1; index > 0; index--)
        {
            int other = random.Next(index + 1);
            (result[index], result[other]) = (result[other], result[index]);
        }
        return result;
    }

    private static void EvaluateBatch(
        SearchContext context,
        IReadOnlyList<Candidate> candidates,
        int requestedSamples,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return;
        Parallel.ForEach(candidates, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = context.Options.Parallelism,
        }, candidate => _ = Evaluate(context, candidate, requestedSamples,
            cancellationToken));
    }

    private static void ReportProgress(
        Action<PlacementSearchProgress>? progress,
        string stage,
        double fraction,
        string message) => progress?.Invoke(new PlacementSearchProgress(stage,
            Math.Clamp(fraction, 0d, 1d), message));

    private static PlacementCandidateScore Evaluate(
        SearchContext context,
        Candidate candidate,
        int requestedSamples,
        CancellationToken cancellationToken)
    {
        if (!context.Scores.TryGetValue(candidate.Key, out ScoreAccumulator? score))
        {
            var created = new ScoreAccumulator
            {
                DefenseWeightPercent = context.Options.DefenseWeightPercent,
            };
            if (context.Scores.TryAdd(candidate.Key, created))
            {
                score = created;
                Interlocked.Increment(ref context.CacheMisses);
            }
            else
            {
                score = context.Scores[candidate.Key];
                Interlocked.Increment(ref context.CacheHits);
            }
        }
        else
        {
            Interlocked.Increment(ref context.CacheHits);
        }
        while (score.Samples < requestedSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int runIndex = score.Samples;
            CombatState state = CloneInitialState(
                context.Baseline, context.BaselinePlayer.Id, candidate);
            PrepareNeutralEvaluation(state, context.BaselinePlayer.Id);
            int evaluationTicks = Math.Min(context.Options.MaximumTicks,
                Math.Max(1, (int)Math.Ceiling(context.Options.ScoreMaximumMilliseconds /
                    (double)CombatEngine.TickMilliseconds)));
            CombatSimulationResult simulation = CombatSimulation.RunIndexed(
                state, unchecked((uint)context.Options.BaseSeed), runIndex,
                evaluationTicks);
            Interlocked.Increment(ref context.SimulationCount);
            if (string.Equals(simulation.WinnerId, context.BaselinePlayer.Id,
                StringComparison.OrdinalIgnoreCase))
            {
                score.PlayerWins++;
            }
            else if (simulation.WinnerId is null)
            {
                score.Draws++;
            }
            else
            {
                score.OpponentWins++;
            }
            CombatantSimulationResult player = simulation.Combatants.Single(value =>
                string.Equals(value.Id, context.BaselinePlayer.Id,
                    StringComparison.OrdinalIgnoreCase));
            CombatantSimulationResult opponent = simulation.Combatants.First(value =>
                !string.Equals(value.Id, context.BaselinePlayer.Id,
                    StringComparison.OrdinalIgnoreCase));
            int playerMaximum = Math.Max(1, context.BaselinePlayer.MaxHealth);
            CombatantState baselineOpponent = context.Baseline.Combatants.First(value =>
                !string.Equals(value.Id, context.BaselinePlayer.Id,
                    StringComparison.OrdinalIgnoreCase));
            int opponentMaximum = Math.Max(1, baselineOpponent.MaxHealth);
            score.HealthMargin += Math.Clamp((double)player.Health / playerMaximum, -1, 1) -
                Math.Clamp((double)opponent.Health / opponentMaximum, -1, 1);
            foreach (CombatEvent combatEvent in simulation.FullEventTrace)
            {
                if (string.Equals(combatEvent.TargetId, opponent.Id,
                        StringComparison.OrdinalIgnoreCase) && IsDamage(combatEvent.Kind))
                {
                    score.DamageScore += Math.Max(0, combatEvent.Amount) +
                        Math.Max(0, combatEvent.SecondaryAmount);
                }
                if (string.Equals(combatEvent.TargetId, player.Id,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (combatEvent.Kind == "Shield")
                        score.DefenseScore += Math.Max(0, combatEvent.Amount);
                    else if (IsHealing(combatEvent.Kind))
                        score.DefenseScore += Math.Max(0, combatEvent.Amount);
                    else if (combatEvent.Kind == "PlayerAttribute:HealthMax")
                        score.DefenseScore += Math.Max(0,
                            combatEvent.Amount - combatEvent.SecondaryAmount);
                }
            }
            foreach ((string action, int count) in simulation.UnsupportedActions)
            {
                score.Unsupported[action] = score.Unsupported.GetValueOrDefault(action) + count;
            }
        }
        return SnapshotScore(score);
    }

    private static PlacementCandidateScore SnapshotScore(ScoreAccumulator value)
    {
        int samples = value.Samples;
        if (samples <= 0)
        {
            return new PlacementCandidateScore(0, 0, 0, 0, 0, 0, 0, 0, 0,
                new Dictionary<string, int>());
        }
        double outcome = (value.PlayerWins + value.Draws * 0.5) / samples;
        double margin = value.HealthMargin / samples;
        double damage = value.DamageScore / samples;
        double defense = value.DefenseScore / samples;
        double combined = damage + defense * 0.01 * value.DefenseWeightPercent;
        return new PlacementCandidateScore(combined, outcome, value.PlayerWins,
            value.OpponentWins, value.Draws, samples, margin, damage, defense,
            new Dictionary<string, int>(value.Unsupported, StringComparer.Ordinal));
    }

    private static CombatState CloneInitialState(
        CombatState source,
        string projectedPlayerId,
        Candidate candidate)
    {
        Dictionary<string, int> selected = candidate.Items.Zip(candidate.Positions)
            .ToDictionary(value => value.First.Id, value => value.Second, StringComparer.Ordinal);
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
                string section = card.Section;
                int boardPosition = card.BoardPosition;
                if (string.Equals(combatant.Id, projectedPlayerId,
                        StringComparison.OrdinalIgnoreCase) &&
                    card.Section is "Hand" or "Stash" && !IsSocketEffect(card))
                {
                    if (selected.TryGetValue(card.InstanceId, out int selectedPosition))
                    {
                        section = "Hand";
                        boardPosition = selectedPosition;
                    }
                    else
                    {
                        section = "Stash";
                    }
                }
                CombatCardState copy = CombatCardState.Create(card.InstanceId,
                    card.Definition, owners[combatant], boardPosition,
                    section, card.Span);
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

    private static void PrepareNeutralEvaluation(CombatState state, string playerId)
    {
        const int infiniteHealth = 1_000_000_000;
        CombatantState player = state.Combatants.Single(value =>
            string.Equals(value.Id, playerId, StringComparison.OrdinalIgnoreCase));
        CombatantState target = state.Combatants.First(value =>
            !string.Equals(value.Id, playerId, StringComparison.OrdinalIgnoreCase));
        PrepareWhiteboardCombatant(player, infiniteHealth / 2, infiniteHealth);
        PrepareWhiteboardCombatant(target, infiniteHealth, infiniteHealth);
        target.Cards.Clear();
        state.Sandstorm.Enabled = false;
    }

    private static void PrepareWhiteboardCombatant(
        CombatantState combatant, int health, int maximumHealth)
    {
        combatant.MaxHealth = maximumHealth;
        combatant.SetIntrinsicAttribute("HealthMax", maximumHealth);
        combatant.Health = health;
        combatant.IntrinsicAttributes["Health"] = health;
        combatant.Attributes["Health"] = health;
        combatant.Shield = 0;
        combatant.Poison = 0;
        combatant.Burn = 0;
        combatant.Regen = 0;
        combatant.SetIntrinsicAttribute("Shield", 0);
        combatant.SetIntrinsicAttribute("Poison", 0);
        combatant.SetIntrinsicAttribute("Burn", 0);
        combatant.SetIntrinsicAttribute("HealthRegen", 0);
        combatant.SetIntrinsicAttribute("PercentDamageReduction", 0);
        combatant.SetIntrinsicAttribute("FlatDamageReduction", 0);
    }

    private static bool IsDamage(string kind) => kind is
        "Damage" or "CardDamage" or "Burn" or "BurnShield" or "Poison";

    private static bool IsHealing(string kind) => kind is
        "Heal" or "LifeSteal" or "Regen" or "ReviveHeal";

    private static double StructuralScore(CombatCardState card)
    {
        double cooldown = Math.Max(500, card.Attributes.GetValueOrDefault("CooldownMax"));
        double active =
            card.Attributes.GetValueOrDefault("DamageAmount") * 1.0 +
            card.Attributes.GetValueOrDefault("ShieldApplyAmount") * 0.7 +
            card.Attributes.GetValueOrDefault("HealAmount") * 0.7 +
            card.Attributes.GetValueOrDefault("BurnApplyAmount") * 0.8 +
            card.Attributes.GetValueOrDefault("PoisonApplyAmount") * 0.8;
        double tags = card.Tags.Count * 0.01;
        return active * Math.Max(1, card.Attributes.GetValueOrDefault("Multicast")) /
            cooldown + tags;
    }

    private static bool IsSocketEffect(CombatCardState card) =>
        card.Definition.Type.Contains("Socket", StringComparison.OrdinalIgnoreCase);
}
