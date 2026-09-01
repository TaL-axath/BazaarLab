using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BazaarGameClient.Domain.Models.Cards;
using BazaarGameShared.Domain.Core.Types;
using BepInEx;
using TheBazaar;
using UnityEngine;

namespace BazaarLab.Plugin;

public sealed partial class Plugin
{
    private Process? _placementProcess;
    private string? _placementResultPath;
    private string? _placementOptionsPath;
    private string? _placementInputFingerprint;
    private PlacementResultDto? _placementResult;
    private string _placementStatus = "就绪";
    private string _placementProcessLog = string.Empty;
    private float _placementStartedAt;
    private List<InventoryState>? _moveExpectedStates;
    private List<MoveStep>? _movePlan;
    private List<InventoryState>? _undoExpectedStates;
    private List<MoveStep>? _undoPlan;
    private bool _moveIsUndo;
    private int _moveStepIndex;
    private float _moveIssuedAt;
    private bool _moveIssued;
    private bool _moveSawServerWait;
    private Vector2 _placementScroll;
    private Rect _placementWindowRect;
    private bool _placementWindowInitialized;
    private bool _placementWindowMinimized;

    private const int PlacementWindowId = 191104;
    private const float PlacementWindowWidth = 310f;

    private bool IsSearching => _placementProcess is not null;
    private bool IsMoving => _movePlan is not null;

    private void InitializePlacementControls()
    {
        _placementStatus = "就绪——进入对局后点击“规划”";
    }

    private void DisposePlacementControls()
    {
        if (_placementProcess is not null)
        {
            try
            {
                if (!_placementProcess.HasExited)
                {
                    _placementProcess.Kill();
                }
            }
            catch (Exception)
            {
                // The child may already have exited between checks.
            }
            _placementProcess.Dispose();
            _placementProcess = null;
        }
        StopMovePlan();
    }

    private void UpdatePlacementControls()
    {
        if (_placementProcess is not null &&
            Time.realtimeSinceStartup - _placementStartedAt >= 10f)
        {
            CancelPlacementSearch("摆位规划超过 10 秒，已自动中断；请减少候选物品后重试");
        }
        else if (_placementProcess is not null && IsCombatOrReplayActive())
        {
            CancelPlacementSearch("已进入战斗，摆位规划已中断");
        }
        PollPlacementSearch();
        UpdateMovePlan();
    }

    private void CancelPlacementSearch(string status)
    {
        Process? process = _placementProcess;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (Exception exception)
        {
            Logger.LogWarning("cannot stop placement search: " + exception.Message);
        }
        process.Dispose();
        _placementProcess = null;
        _placementProcessLogBuilder = null;
        _placementResultPath = null;
        _placementResult = null;
        SetPlacementStatus(status);
    }

    private void DrawPlacementControls()
    {
        if (Data.Run?.Player is null)
        {
            SetFloatingWindowBlocker(0, default, false);
            return;
        }

        float height = _placementWindowMinimized ? 30f :
            (_placementResult is null ? 152f : 252f);
        if (!_placementWindowInitialized)
        {
            _placementWindowRect = new Rect(Screen.width - PlacementWindowWidth - 18f, 70f,
                PlacementWindowWidth, height);
            _placementWindowInitialized = true;
        }
        _placementWindowRect.width = PlacementWindowWidth;
        _placementWindowRect.height = height;
        _placementWindowRect = GUI.Window(PlacementWindowId, _placementWindowRect,
            DrawPlacementWindow, "本地摆位搜索");
        _placementWindowRect.x = Mathf.Clamp(_placementWindowRect.x,
            -PlacementWindowWidth + 46f, Screen.width - 46f);
        _placementWindowRect.y = Mathf.Clamp(_placementWindowRect.y, 0f,
            Mathf.Max(0f, Screen.height - 30f));
        SetFloatingWindowBlocker(0, _placementWindowRect, true);
    }

    private void DrawPlacementWindow(int windowId)
    {
        if (GUI.Button(new Rect(PlacementWindowWidth - 28f, 3f, 24f, 21f),
                _placementWindowMinimized ? "+" : "—"))
        {
            _placementWindowMinimized = !_placementWindowMinimized;
        }
        GUI.DragWindow(new Rect(0f, 0f, PlacementWindowWidth - 32f, 25f));
        if (_placementWindowMinimized) return;

        float height = _placementResult is null ? 152f : 252f;
        GUILayout.BeginArea(new Rect(10f, 25f, PlacementWindowWidth - 20f, height - 34f));
        GUILayout.BeginHorizontal();
        GUI.enabled = !IsSearching && !IsMoving;
        if (GUILayout.Button(IsSearching ? "规划中……" : "规划", GUILayout.Height(34f)))
        {
            StartPlacementSearch();
        }
        GUI.enabled = !IsSearching && !IsMoving && _placementResult is not null;
        if (GUILayout.Button(IsMoving ? "执行中……" : "应用", GUILayout.Height(34f)))
        {
            StartMovePlan();
        }
        GUI.enabled = !IsSearching && !IsMoving && _undoPlan is not null;
        if (GUILayout.Button("撤销", GUILayout.Height(34f)))
        {
            StartUndoPlan();
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        GUILayout.Label(_placementStatus);
        if (_placementResult?.Recommendation?.Board is { Count: > 0 } board)
        {
            PlacementScoreDto? score = _placementResult.Recommendation.ValidationScore;
            GUILayout.Label($"10秒评分：{score?.Score ?? 0d:0.#}  " +
                $"伤害：{score?.AverageDamageScore ?? 0d:0.#}  " +
                $"防御：{score?.AverageDefenseScore ?? 0d:0.#}  样本：{score?.Samples ?? 0}");
            _placementScroll = GUILayout.BeginScrollView(_placementScroll, GUILayout.Height(112f));
            foreach (PlacementBoardItemDto item in board.OrderBy(value => value.BoardPosition))
            {
                string source = item.FromStash ? " [来自背包]" : string.Empty;
                GUILayout.Label($"{item.BoardPosition}: {item.Name} ({item.Span}){source}");
            }
            GUILayout.EndScrollView();
        }
        GUILayout.EndArea();
    }

    private void StartPlacementSearch()
    {
        if (IsSearching || IsMoving || IsLocalDuelCalculating)
        {
            return;
        }
        if (!TryReadInventory(out InventoryState current, out string error))
        {
            SetPlacementStatus("无法读取物品栏：" + error);
            return;
        }
        if (Data.IsInCombat || AppState.CurrentState is null ||
            !AppState.CurrentState.CanHandleOperation(StateOps.MoveItem))
        {
            SetPlacementStatus("当前游戏状态无法进行摆位规划");
            return;
        }
        try
        {
            _undoPlan = null;
            _undoExpectedStates = null;
            CaptureLiveInventory(DateTime.UtcNow);
            string gameRoot = Paths.GameRootPath;
            string runtime = GetRuntimeFile("BazaarLab.PlacementSearch.dll");
            string catalog = GetCatalogFile();
            string input = Path.Combine(_outputDirectory, "live-inventory.json");
            if (!File.Exists(runtime) || !File.Exists(catalog) || !File.Exists(input))
            {
                SetPlacementStatus("缺少摆位运行库、卡牌目录或实时快照");
                return;
            }

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            _placementResultPath = Path.Combine(_outputDirectory,
                "placement-result-" + stamp + ".json");
            if (!TryGetContiguousUnlockedBoardRange(current.Unlocked,
                    out int boardMinimum, out int boardMaximum, out error))
            {
                SetPlacementStatus("无法确定可用棋盘范围：" + error);
                return;
            }
            _placementOptionsPath = Path.Combine(_outputDirectory,
                "placement-options-" + stamp + ".json");
            File.WriteAllText(_placementOptionsPath, JsonSerializer.Serialize(new
            {
                BoardMinimumPosition = boardMinimum,
                BoardMaximumPosition = boardMaximum,
            }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            _placementInputFingerprint = current.Fingerprint();
            _placementResult = null;
            _placementProcessLog = string.Empty;
            var output = new StringBuilder();
            string dotnetExecutable = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (!File.Exists(dotnetExecutable)) dotnetExecutable = "dotnet";
            var start = new ProcessStartInfo
            {
                FileName = dotnetExecutable,
                Arguments = Quote(runtime) + " " + Quote(catalog) + " " + Quote(input) + " " +
                    Quote(_placementResultPath) + " " + Quote(_placementOptionsPath),
                WorkingDirectory = Path.GetDirectoryName(runtime) ?? gameRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var process = new Process { StartInfo = start, EnableRaisingEvents = false };
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    lock (output) output.AppendLine(args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    lock (output) output.AppendLine(args.Data);
                }
            };
            if (!process.Start())
            {
                process.Dispose();
                SetPlacementStatus("无法启动摆位搜索进程");
                return;
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _placementProcess = process;
            _placementProcessLogBuilder = output;
            _placementStartedAt = Time.realtimeSinceStartup;
            SetPlacementStatus("正在搜索布阵区与背包候选方案……");
        }
        catch (Exception exception)
        {
            SetPlacementStatus("搜索失败：" + exception.Message);
        }
    }

    private StringBuilder? _placementProcessLogBuilder;

    private void PollPlacementSearch()
    {
        Process? process = _placementProcess;
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                return;
            }
            process.WaitForExit();
            int exitCode = process.ExitCode;
            if (_placementProcessLogBuilder is not null)
            {
                lock (_placementProcessLogBuilder)
                {
                    _placementProcessLog = _placementProcessLogBuilder.ToString();
                }
            }
            process.Dispose();
            _placementProcess = null;
            _placementProcessLogBuilder = null;
            if (exitCode != 0 || string.IsNullOrEmpty(_placementResultPath) ||
                !File.Exists(_placementResultPath))
            {
                SetPlacementStatus("摆位进程失败（退出码 " + exitCode + "）：" +
                    LastLine(_placementProcessLog));
                return;
            }
            _placementResult = JsonSerializer.Deserialize<PlacementResultDto>(
                File.ReadAllText(_placementResultPath), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            int count = _placementResult?.Recommendation?.Board?.Count ?? 0;
            SetPlacementStatus(count == 0 ? "没有有效推荐方案" :
                $"推荐已就绪：{count} 件上阵物品；确认后点击“应用”");
        }
        catch (Exception exception)
        {
            process.Dispose();
            _placementProcess = null;
            _placementProcessLogBuilder = null;
            SetPlacementStatus("无法读取摆位结果：" + exception.Message);
        }
    }

    private void StartMovePlan()
    {
        if (IsSearching || IsMoving || _placementResult?.Recommendation?.Board is not { Count: > 0 } board)
        {
            return;
        }
        if (!TryReadInventory(out InventoryState current, out string error))
        {
            SetPlacementStatus("应用已取消：" + error);
            return;
        }
        if (!string.Equals(current.Fingerprint(), _placementInputFingerprint,
                StringComparison.Ordinal))
        {
            SetPlacementStatus("应用已取消：物品栏已变化，请重新规划");
            _placementResult = null;
            return;
        }
        if (Data.IsInCombat || AppState.IsWaitingForServerResponse ||
            CardController.IsAnyCardDragging || AppState.CurrentState is null ||
            !AppState.CurrentState.CanHandleOperation(StateOps.MoveItem))
        {
            SetPlacementStatus("应用已取消：游戏正忙或当前无法移动物品");
            return;
        }

        var targets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (PlacementBoardItemDto item in board)
        {
            if (!targets.TryAdd(item.InstanceId, item.BoardPosition))
            {
                SetPlacementStatus("应用已取消：推荐方案含重复物品");
                return;
            }
            InventoryItem? live = current.Items.FirstOrDefault(value =>
                string.Equals(value.Id, item.InstanceId, StringComparison.Ordinal));
            if (live is null || live.Size != item.Span)
            {
                SetPlacementStatus("应用已取消：物品身份或尺寸不匹配");
                return;
            }
        }

        if (!MovePlanner.TryPlan(current, targets, 120000,
                out List<MoveStep> moves, out List<InventoryState> expected, out error))
        {
            SetPlacementStatus("无法生成安全的移动顺序：" + error);
            return;
        }
        if (moves.Any(move => move.Zone == 1 || current.Find(move.ItemId)?.Zone == 1) &&
            !Data.IsStorageOpen)
        {
            SetPlacementStatus("请先打开背包，再次点击“应用”");
            return;
        }
        if (moves.Count == 0)
        {
            SetPlacementStatus("推荐方案已经应用");
            return;
        }

        _movePlan = moves;
        _moveExpectedStates = expected;
        _undoPlan = BuildReverseMoves(moves, expected);
        _undoExpectedStates = expected.AsEnumerable().Reverse().ToList();
        _moveIsUndo = false;
        _moveStepIndex = 0;
        _moveIssued = false;
        _moveSawServerWait = false;
        SetPlacementStatus($"正在应用：0/{moves.Count} 步……");
    }

    private void StartUndoPlan()
    {
        if (IsSearching || IsMoving || _undoPlan is null || _undoExpectedStates is null)
        {
            return;
        }
        if (!TryReadInventory(out InventoryState current, out string error) ||
            !current.SamePlacement(_undoExpectedStates[0]))
        {
            _undoPlan = null;
            _undoExpectedStates = null;
            SetPlacementStatus("撤销已取消：应用后物品栏发生变化");
            return;
        }
        if (Data.IsInCombat || AppState.IsWaitingForServerResponse ||
            CardController.IsAnyCardDragging || AppState.CurrentState is null ||
            !AppState.CurrentState.CanHandleOperation(StateOps.MoveItem))
        {
            SetPlacementStatus("撤销已取消：游戏正忙或当前无法移动物品");
            return;
        }
        if (_undoPlan.Any(move => move.Zone == 1 || current.Find(move.ItemId)?.Zone == 1) &&
            !Data.IsStorageOpen)
        {
            SetPlacementStatus("请先打开背包，再次点击“撤销”");
            return;
        }
        _movePlan = _undoPlan.ToList();
        _moveExpectedStates = _undoExpectedStates.ToList();
        _moveIsUndo = true;
        _moveStepIndex = 0;
        _moveIssued = false;
        _moveSawServerWait = false;
        SetPlacementStatus($"正在撤销：0/{_movePlan.Count} 步……");
    }

    private static List<MoveStep> BuildReverseMoves(IReadOnlyList<MoveStep> moves,
        IReadOnlyList<InventoryState> states)
    {
        var reverse = new List<MoveStep>(moves.Count);
        for (int index = moves.Count - 1; index >= 0; index--)
        {
            InventoryItem prior = states[index].Find(moves[index].ItemId)
                ?? throw new InvalidOperationException("reverse move item is missing");
            reverse.Add(new MoveStep(prior.Id, prior.Zone, prior.Position));
        }
        return reverse;
    }

    private void UpdateMovePlan()
    {
        if (_movePlan is null || _moveExpectedStates is null)
        {
            return;
        }
        if (Data.IsInCombat || CardController.IsAnyCardDragging || AppState.CurrentState is null ||
            !AppState.CurrentState.CanHandleOperation(StateOps.MoveItem))
        {
            FailMovePlan("game state changed");
            return;
        }
        if (AppState.IsWaitingForServerResponse)
        {
            _moveSawServerWait = true;
            if (_moveIssued && Time.realtimeSinceStartup - _moveIssuedAt > 20f)
            {
                FailMovePlan("server response timed out");
            }
            return;
        }
        if (!TryReadInventory(out InventoryState actual, out string error))
        {
            FailMovePlan(error);
            return;
        }

        if (_moveIssued)
        {
            InventoryState expectedAfter = _moveExpectedStates[_moveStepIndex + 1];
            if (actual.SamePlacement(expectedAfter) &&
                (_moveSawServerWait || Time.realtimeSinceStartup - _moveIssuedAt >= 0.35f))
            {
                _moveStepIndex++;
                _moveIssued = false;
                _moveSawServerWait = false;
                if (_moveStepIndex >= _movePlan.Count)
                {
                    int count = _movePlan.Count;
                    bool wasUndo = _moveIsUndo;
                    StopMovePlan();
                    _placementInputFingerprint = actual.Fingerprint();
                    if (wasUndo)
                    {
                        _undoPlan = null;
                        _undoExpectedStates = null;
                        _placementResult = null;
                        SetPlacementStatus($"撤销完成（{count} 步）");
                    }
                    else
                    {
                        SetPlacementStatus($"应用成功（{count} 步）；可以撤销");
                    }
                    return;
                }
                SetPlacementStatus((_moveIsUndo ? "正在撤销：" : "正在应用：") +
                    $"{_moveStepIndex}/{_movePlan.Count} 步……");
                return;
            }
            if (Time.realtimeSinceStartup - _moveIssuedAt > 3f)
            {
                FailMovePlan("authoritative inventory did not reach the expected state");
            }
            return;
        }

        InventoryState expectedBefore = _moveExpectedStates[_moveStepIndex];
        if (!actual.SamePlacement(expectedBefore))
        {
            FailMovePlan("inventory changed outside the executor");
            return;
        }
        MoveStep step = _movePlan[_moveStepIndex];
        InventoryItem? item = actual.Find(step.ItemId);
        ItemCard? card = Data.Run?.Player.Hand.GetItemsAsEnumerable().OfType<ItemCard>()
            .Concat(Data.Run.Player.Stash.GetItemsAsEnumerable().OfType<ItemCard>())
            .FirstOrDefault(value => string.Equals(value.InstanceId.Value, step.ItemId,
                StringComparison.Ordinal));
        if (item is null || card is null)
        {
            FailMovePlan("move item disappeared");
            return;
        }
        var sockets = Enumerable.Range(step.Position, item.Size)
            .Select(value => (EContainerSocketId)value).ToList();
        AppState.CurrentState.MoveCardCommand(card, sockets,
            step.Zone == 0 ? EInventorySection.Hand : EInventorySection.Stash);
        _moveIssued = true;
        _moveIssuedAt = Time.realtimeSinceStartup;
        _moveSawServerWait = false;
    }

    private void FailMovePlan(string reason)
    {
        int completed = _moveStepIndex;
        StopMovePlan();
        _undoPlan = null;
        _undoExpectedStates = null;
        _placementResult = null;
        SetPlacementStatus($"执行在第 {completed} 步后停止：{reason}；请重新规划");
    }

    private void StopMovePlan()
    {
        _movePlan = null;
        _moveExpectedStates = null;
        _moveStepIndex = 0;
        _moveIssued = false;
        _moveSawServerWait = false;
        _moveIsUndo = false;
    }

    private bool TryReadInventory(out InventoryState state, out string error)
    {
        state = new InventoryState(Array.Empty<InventoryItem>(), new bool[20]);
        error = string.Empty;
        try
        {
            if (Data.Run?.Player is null)
            {
                error = "no active player run";
                return false;
            }
            var items = new List<InventoryItem>();
            foreach (ItemCard card in Data.Run.Player.Hand.GetItemsAsEnumerable().OfType<ItemCard>())
            {
                if (!card.LeftSocketId.HasValue)
                {
                    error = "board item has no socket";
                    return false;
                }
                items.Add(new InventoryItem(card.InstanceId.Value, (int)card.Size, 0,
                    (int)card.LeftSocketId.Value));
            }
            foreach (ItemCard card in Data.Run.Player.Stash.GetItemsAsEnumerable().OfType<ItemCard>())
            {
                if (!card.LeftSocketId.HasValue)
                {
                    error = "stash item has no socket";
                    return false;
                }
                items.Add(new InventoryItem(card.InstanceId.Value, (int)card.Size, 1,
                    (int)card.LeftSocketId.Value));
            }
            if (items.GroupBy(value => value.Id, StringComparer.Ordinal).Any(group => group.Count() != 1))
            {
                error = "duplicate item instance id";
                return false;
            }
            bool[] unlocked = new bool[20];
            if (!TryReadUnlocked(Data.Run.Player.Hand.Container, unlocked, 0) ||
                !TryReadUnlocked(Data.Run.Player.Stash.Container, unlocked, 10))
            {
                error = "cannot read authoritative unlocked sockets";
                return false;
            }
            state = new InventoryState(items.OrderBy(value => value.Id,
                StringComparer.Ordinal).ToArray(), unlocked);
            return state.Validate(out error);
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name + ": " + exception.Message;
            return false;
        }
    }

    private static bool TryReadUnlocked(object container, bool[] destination, int offset)
    {
        MethodInfo? method = container.GetType().GetMethod("GetUnlockedSockets",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        object? value = method?.Invoke(container, Array.Empty<object>());
        if (value is null)
        {
            return false;
        }
        int count = 0;
        if (value is IEnumerable<bool> flags)
        {
            foreach (bool flag in flags)
            {
                if (count >= 10) return false;
                destination[offset + count++] = flag;
            }
            return count == 10;
        }
        if (value is IEnumerable values)
        {
            foreach (object socket in values)
            {
                int index = Convert.ToInt32(socket);
                if (index < 0 || index >= 10) return false;
                destination[offset + index] = true;
                count++;
            }
            return count > 0;
        }
        return false;
    }

    private static bool TryGetContiguousUnlockedBoardRange(bool[] unlocked,
        out int minimum, out int maximum, out string error)
    {
        minimum = -1;
        maximum = -1;
        error = string.Empty;
        if (unlocked.Length < 10)
        {
            error = "解锁槽位数据不完整";
            return false;
        }
        for (int index = 0; index < 10; index++)
        {
            if (!unlocked[index]) continue;
            if (minimum < 0) minimum = index;
            maximum = index;
        }
        if (minimum < 0)
        {
            error = "没有已解锁的棋盘槽位";
            return false;
        }
        for (int index = minimum; index <= maximum; index++)
        {
            if (unlocked[index]) continue;
            error = "已解锁棋盘槽位不是连续区间，已拒绝生成不安全方案";
            return false;
        }
        return true;
    }

    private void SetPlacementStatus(string status)
    {
        _placementStatus = status;
        Logger.LogInfo("placement: " + status);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    private static string LastLine(string value)
    {
        string[] lines = value.Split(new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        string? exception = lines.FirstOrDefault(line =>
            line.Contains("Exception:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Unhandled exception", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exception))
        {
            const string prefix = "Unhandled exception. ";
            return exception.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? exception.Substring(prefix.Length) : exception.Trim();
        }
        return lines.LastOrDefault(line =>
            !line.TrimStart().StartsWith("at ", StringComparison.OrdinalIgnoreCase))?.Trim() ??
            lines.LastOrDefault()?.Trim() ?? "no diagnostic output";
    }

    public static string PlacementPlannerSelfTest()
    {
        bool[] all = Enumerable.Repeat(true, 20).ToArray();
        VerifyPlan("board-swap", new InventoryState(new[]
        {
            new InventoryItem("a", 1, 0, 0),
            new InventoryItem("b", 1, 0, 1),
        }, all), new Dictionary<string, int> { ["a"] = 1, ["b"] = 0 });
        VerifyPlan("stash-replacement", new InventoryState(new[]
        {
            new InventoryItem("weak", 1, 0, 0),
            new InventoryItem("strong", 1, 1, 0),
        }, all), new Dictionary<string, int> { ["strong"] = 0 });
        VerifyPlan("mixed-sizes", new InventoryState(new[]
        {
            new InventoryItem("large", 3, 0, 0),
            new InventoryItem("small", 1, 0, 3),
            new InventoryItem("medium", 2, 1, 0),
        }, all), new Dictionary<string, int>
        {
            ["small"] = 0,
            ["medium"] = 1,
            ["large"] = 3,
        });
        VerifyReverse("reverse-chain", new InventoryState(new[]
        {
            new InventoryItem("a", 1, 0, 0),
            new InventoryItem("b", 1, 0, 1),
        }, all), new Dictionary<string, int> { ["a"] = 1, ["b"] = 0 });
        return "PASS: board-swap, stash-replacement, mixed-sizes, reverse-chain";
    }

    private static void VerifyReverse(string name, InventoryState start,
        IReadOnlyDictionary<string, int> targets)
    {
        if (!MovePlanner.TryPlan(start, targets, 120000, out List<MoveStep> moves,
                out List<InventoryState> expected, out string error))
            throw new InvalidOperationException(name + " forward failed: " + error);
        List<MoveStep> reverse = BuildReverseMoves(moves, expected);
        List<InventoryState> reverseStates = expected.AsEnumerable().Reverse().ToList();
        if (reverse.Count != moves.Count || !reverseStates[^1].SamePlacement(start))
            throw new InvalidOperationException(name + " did not restore the initial state");
        for (int index = 0; index < reverse.Count; index++)
        {
            InventoryItem prior = reverseStates[index + 1].Find(reverse[index].ItemId)
                ?? throw new InvalidOperationException(name + " lost an item");
            if (prior.Zone != reverse[index].Zone || prior.Position != reverse[index].Position)
                throw new InvalidOperationException(name + " reverse step mismatch");
        }
    }

    private static void VerifyPlan(string name, InventoryState start,
        IReadOnlyDictionary<string, int> targets)
    {
        if (!MovePlanner.TryPlan(start, targets, 120000, out List<MoveStep> moves,
                out List<InventoryState> expected, out string error))
        {
            throw new InvalidOperationException(name + " failed: " + error);
        }
        if (expected.Count != moves.Count + 1 || !expected[0].SamePlacement(start))
        {
            throw new InvalidOperationException(name + " produced an invalid state chain");
        }
        InventoryState goal = expected[^1];
        foreach (InventoryItem item in goal.Items)
        {
            if (targets.TryGetValue(item.Id, out int target))
            {
                if (item.Zone != 0 || item.Position != target)
                    throw new InvalidOperationException(name + " missed a target");
            }
            else if (item.Zone == 0)
            {
                throw new InvalidOperationException(name + " left a replaced item on board");
            }
        }
    }

    private sealed class PlacementResultDto
    {
        public PlacementRecommendationDto? Recommendation { get; set; }
    }

    private sealed class PlacementRecommendationDto
    {
        public List<PlacementBoardItemDto>? Board { get; set; }
        public PlacementScoreDto? ValidationScore { get; set; }
    }

    private sealed class PlacementBoardItemDto
    {
        public string InstanceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int BoardPosition { get; set; }
        public int Span { get; set; }
        public bool FromStash { get; set; }
    }

    private sealed class PlacementScoreDto
    {
        public double Score { get; set; }
        public double PlayerOutcomeProbability { get; set; }
        public double AverageDamageScore { get; set; }
        public double AverageDefenseScore { get; set; }
        public int Samples { get; set; }
        public int PlayerWins { get; set; }
        public int OpponentWins { get; set; }
        public int Draws { get; set; }
    }

    private static (double Lower, double Upper) WilsonInterval(PlacementScoreDto? score)
    {
        if (score is null || score.Samples <= 0) return (0d, 1d);
        const double z = 1.959963984540054;
        double n = score.Samples;
        double p = (score.PlayerWins + score.Draws * 0.5d) / n;
        double denominator = 1d + z * z / n;
        double center = (p + z * z / (2d * n)) / denominator;
        double radius = z * Math.Sqrt(p * (1d - p) / n + z * z / (4d * n * n)) /
            denominator;
        return (Math.Max(0d, center - radius), Math.Min(1d, center + radius));
    }

    private sealed class InventoryItem
    {
        public string Id { get; }
        public int Size { get; }
        public int Zone { get; }
        public int Position { get; }

        public InventoryItem(string id, int size, int zone, int position)
        {
            Id = id;
            Size = size;
            Zone = zone;
            Position = position;
        }

        public InventoryItem At(int zone, int position) =>
            new InventoryItem(Id, Size, zone, position);
    }

    private sealed class MoveStep
    {
        public string ItemId { get; }
        public int Zone { get; }
        public int Position { get; }

        public MoveStep(string itemId, int zone, int position)
        {
            ItemId = itemId;
            Zone = zone;
            Position = position;
        }
    }

    private sealed class InventoryState
    {
        public InventoryItem[] Items { get; }
        public bool[] Unlocked { get; }

        public InventoryState(IEnumerable<InventoryItem> items, bool[] unlocked)
        {
            Items = items.ToArray();
            Unlocked = (bool[])unlocked.Clone();
        }

        public InventoryItem? Find(string id) => Items.FirstOrDefault(value =>
            string.Equals(value.Id, id, StringComparison.Ordinal));

        public string Key() => string.Join("|", Items.Select(value =>
            value.Zone.ToString() + ":" + value.Position.ToString()));

        public string Fingerprint() => string.Join("|", Items.Select(value =>
            value.Id + ":" + value.Size + ":" + value.Zone + ":" + value.Position)) +
            "#" + new string(Unlocked.Select(value => value ? '1' : '0').ToArray());

        public bool SamePlacement(InventoryState other) =>
            string.Equals(Fingerprint(), other.Fingerprint(), StringComparison.Ordinal);

        public bool Validate(out string error)
        {
            error = string.Empty;
            bool[] occupied = new bool[20];
            foreach (InventoryItem item in Items)
            {
                if (item.Size < 1 || item.Size > 3 || item.Zone is < 0 or > 1 ||
                    item.Position < 0 || item.Position + item.Size > 10)
                {
                    error = "invalid item geometry: " + item.Id;
                    return false;
                }
                int baseIndex = item.Zone * 10 + item.Position;
                for (int index = 0; index < item.Size; index++)
                {
                    int cell = baseIndex + index;
                    if (!Unlocked[cell] || occupied[cell])
                    {
                        error = "locked or overlapping item socket: " + item.Id;
                        return false;
                    }
                    occupied[cell] = true;
                }
            }
            return true;
        }

        public InventoryState Move(int itemIndex, int zone, int position)
        {
            InventoryItem[] copy = (InventoryItem[])Items.Clone();
            InventoryItem item = copy[itemIndex];
            copy[itemIndex] = item.At(zone, position);
            return new InventoryState(copy, Unlocked);
        }
    }

    private static class MovePlanner
    {
        private sealed class Node
        {
            public InventoryState State { get; set; } = null!;
            public int Cost { get; set; }
            public int Parent { get; set; }
            public MoveStep? Move { get; set; }
        }

        private sealed class MinHeap
        {
            private readonly List<(int Priority, int Sequence, int Node)> _values = new();
            private int _sequence;
            public int Count => _values.Count;

            public void Push(int priority, int node)
            {
                var value = (priority, _sequence++, node);
                _values.Add(value);
                int index = _values.Count - 1;
                while (index > 0)
                {
                    int parent = (index - 1) / 2;
                    if (Compare(_values[parent], value) <= 0) break;
                    _values[index] = _values[parent];
                    index = parent;
                }
                _values[index] = value;
            }

            public int Pop()
            {
                var result = _values[0];
                var last = _values[^1];
                _values.RemoveAt(_values.Count - 1);
                if (_values.Count == 0) return result.Node;
                int index = 0;
                while (true)
                {
                    int left = index * 2 + 1;
                    if (left >= _values.Count) break;
                    int right = left + 1;
                    int child = right < _values.Count && Compare(_values[right], _values[left]) < 0
                        ? right : left;
                    if (Compare(last, _values[child]) <= 0) break;
                    _values[index] = _values[child];
                    index = child;
                }
                _values[index] = last;
                return result.Node;
            }

            private static int Compare((int Priority, int Sequence, int Node) left,
                (int Priority, int Sequence, int Node) right)
            {
                int result = left.Priority.CompareTo(right.Priority);
                return result != 0 ? result : left.Sequence.CompareTo(right.Sequence);
            }
        }

        public static bool TryPlan(InventoryState start, IReadOnlyDictionary<string, int> targets,
            int maxNodes, out List<MoveStep> moves, out List<InventoryState> expected,
            out string error)
        {
            moves = new List<MoveStep>();
            expected = new List<InventoryState>();
            error = string.Empty;
            foreach (KeyValuePair<string, int> target in targets)
            {
                InventoryItem? item = start.Find(target.Key);
                if (item is null || target.Value < 0 || target.Value + item.Size > 10)
                {
                    error = "invalid target item or socket: " + target.Key;
                    return false;
                }
                for (int cell = target.Value; cell < target.Value + item.Size; cell++)
                {
                    if (!start.Unlocked[cell])
                    {
                        error = "target uses a locked board socket";
                        return false;
                    }
                }
            }
            bool[] targetCells = new bool[10];
            foreach (KeyValuePair<string, int> target in targets)
            {
                InventoryItem item = start.Find(target.Key)!;
                for (int cell = target.Value; cell < target.Value + item.Size; cell++)
                {
                    if (targetCells[cell])
                    {
                        error = "recommendation overlaps board sockets";
                        return false;
                    }
                    targetCells[cell] = true;
                }
            }

            var nodes = new List<Node>();
            var best = new Dictionary<string, int>(StringComparer.Ordinal);
            var heap = new MinHeap();
            nodes.Add(new Node { State = start, Cost = 0, Parent = -1, Move = null });
            best[start.Key()] = 0;
            heap.Push(Heuristic(start, targets), 0);

            int goal = -1;
            while (heap.Count > 0 && nodes.Count < maxNodes)
            {
                int nodeIndex = heap.Pop();
                Node node = nodes[nodeIndex];
                if (best.TryGetValue(node.State.Key(), out int known) && known < node.Cost)
                {
                    continue;
                }
                if (IsGoal(node.State, targets))
                {
                    goal = nodeIndex;
                    break;
                }
                foreach ((int itemIndex, int zone, int position) in GenerateMoves(node.State, targets))
                {
                    InventoryState next = node.State.Move(itemIndex, zone, position);
                    int cost = node.Cost + 1;
                    string key = next.Key();
                    if (best.TryGetValue(key, out int previous) && previous <= cost) continue;
                    best[key] = cost;
                    InventoryItem moved = node.State.Items[itemIndex];
                    int nextIndex = nodes.Count;
                    nodes.Add(new Node
                    {
                        State = next,
                        Cost = cost,
                        Parent = nodeIndex,
                        Move = new MoveStep(moved.Id, zone, position),
                    });
                    heap.Push(cost + Heuristic(next, targets), nextIndex);
                    if (nodes.Count >= maxNodes) break;
                }
            }

            if (goal < 0)
            {
                error = $"no legal sequence found within {maxNodes} states; free stash space may be required";
                return false;
            }
            var reverseMoves = new List<MoveStep>();
            var reverseStates = new List<InventoryState> { nodes[goal].State };
            for (int index = goal; nodes[index].Parent >= 0; index = nodes[index].Parent)
            {
                reverseMoves.Add(nodes[index].Move!);
                reverseStates.Add(nodes[nodes[index].Parent].State);
            }
            reverseMoves.Reverse();
            reverseStates.Reverse();
            moves = reverseMoves;
            expected = reverseStates;
            return true;
        }

        private static int Heuristic(InventoryState state,
            IReadOnlyDictionary<string, int> targets)
        {
            int wrong = 0;
            foreach (InventoryItem item in state.Items)
            {
                if (targets.TryGetValue(item.Id, out int position))
                {
                    if (item.Zone != 0 || item.Position != position) wrong++;
                }
                else if (item.Zone == 0)
                {
                    wrong++;
                }
            }
            return wrong;
        }

        private static bool IsGoal(InventoryState state,
            IReadOnlyDictionary<string, int> targets)
        {
            foreach (InventoryItem item in state.Items)
            {
                if (targets.TryGetValue(item.Id, out int position))
                {
                    if (item.Zone != 0 || item.Position != position) return false;
                }
                else if (item.Zone == 0)
                {
                    return false;
                }
            }
            return true;
        }

        private static IEnumerable<(int ItemIndex, int Zone, int Position)> GenerateMoves(
            InventoryState state, IReadOnlyDictionary<string, int> targets)
        {
            var preferred = new List<(int, int, int)>();
            var fallback = new List<(int, int, int)>();
            for (int itemIndex = 0; itemIndex < state.Items.Length; itemIndex++)
            {
                InventoryItem item = state.Items[itemIndex];
                if (targets.TryGetValue(item.Id, out int target) &&
                    (item.Zone != 0 || item.Position != target))
                {
                    bool[] without = BuildOccupied(state, itemIndex);
                    if (Fits(state, without, item.Size, 0, target))
                    {
                        preferred.Add((itemIndex, 0, target));
                    }
                }
            }
            for (int itemIndex = 0; itemIndex < state.Items.Length; itemIndex++)
            {
                InventoryItem item = state.Items[itemIndex];
                bool[] without = BuildOccupied(state, itemIndex);
                for (int zone = 0; zone <= 1; zone++)
                {
                    for (int position = 0; position + item.Size <= 10; position++)
                    {
                        if (zone == item.Zone && position == item.Position) continue;
                        if (!Fits(state, without, item.Size, zone, position)) continue;
                        var move = (itemIndex, zone, position);
                        bool useful = (targets.ContainsKey(item.Id) && zone == 0) ||
                            (!targets.ContainsKey(item.Id) && item.Zone == 0 && zone == 1) ||
                            BlocksTarget(state, targets, item);
                        (useful ? preferred : fallback).Add(move);
                    }
                }
            }
            return preferred.Concat(fallback);
        }

        private static bool BlocksTarget(InventoryState state,
            IReadOnlyDictionary<string, int> targets, InventoryItem item)
        {
            if (item.Zone != 0) return false;
            int end = item.Position + item.Size;
            foreach (KeyValuePair<string, int> target in targets)
            {
                InventoryItem targetItem = state.Find(target.Key)!;
                if (!string.Equals(item.Id, target.Key, StringComparison.Ordinal) &&
                    item.Position < target.Value + targetItem.Size && target.Value < end)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool[] BuildOccupied(InventoryState state, int excluded)
        {
            bool[] occupied = new bool[20];
            for (int index = 0; index < state.Items.Length; index++)
            {
                if (index == excluded) continue;
                InventoryItem item = state.Items[index];
                int start = item.Zone * 10 + item.Position;
                for (int cell = 0; cell < item.Size; cell++) occupied[start + cell] = true;
            }
            return occupied;
        }

        private static bool Fits(InventoryState state, bool[] occupied, int size,
            int zone, int position)
        {
            int start = zone * 10 + position;
            for (int cell = 0; cell < size; cell++)
            {
                if (!state.Unlocked[start + cell] || occupied[start + cell]) return false;
            }
            return true;
        }
    }
}
