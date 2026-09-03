using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BazaarGameClient.Domain.Models.Cards;
using BepInEx;
using TheBazaar;
using UnityEngine;

namespace BazaarLab.Plugin;

public sealed partial class Plugin
{
    private Process? _monsterProcess;
    private StringBuilder? _monsterProcessLog;
    private string? _monsterInputPath;
    private string? _monsterResultPath;
    private string? _monsterInputPayload;
    private MonsterPredictionDto? _monsterResult;
    private string _monsterStatus = "Select a monster encounter, then CALCULATE";
    private string? _monsterCandidatePayload;
    private string? _monsterCompletedPayload;
    private float _monsterPayloadChangedAt;
    private Guid? _monsterObservedEncounter;
    private bool _monsterCombatSuppressed;
    private MonsterPredictionAudit? _pendingMonsterPrediction;
    private readonly Dictionary<string, MonsterPredictionAudit> _monsterPredictionsByCapture =
        new Dictionary<string, MonsterPredictionAudit>(StringComparer.Ordinal);
    private readonly Dictionary<string, MonsterPredictionAudit> _encounterPredictionsByEncounter =
        new Dictionary<string, MonsterPredictionAudit>(StringComparer.OrdinalIgnoreCase);

    private sealed class MonsterPredictionAudit
    {
        public string EncounterId { get; set; } = string.Empty;
        public string Predicted { get; set; } = string.Empty;
        public int PlayerWins { get; set; }
        public int OpponentWins { get; set; }
        public int Draws { get; set; }
        public string InputJson { get; set; } = string.Empty;
        public string ResultJson { get; set; } = string.Empty;
    }

    private bool IsMonsterCalculating => _monsterProcess is not null;

    private void InitializeMonsterCombatControls()
    {
        _monsterStatus = "Select a monster encounter, then CALCULATE";
    }

    private void DisposeMonsterCombatControls()
    {
        if (_monsterProcess is null)
        {
            return;
        }
        try
        {
            if (!_monsterProcess.HasExited) _monsterProcess.Kill();
        }
        catch (Exception)
        {
            // Process may have exited between checks.
        }
        _monsterProcess.Dispose();
        _monsterProcess = null;
        _monsterProcessLog = null;
        FinishMonsterArtifacts(false, "plugin shutdown", null);
    }

    private void UpdateMonsterCombatControls()
    {
        if (IsCombatOrReplayActive())
        {
            if (!_monsterCombatSuppressed)
            {
                _monsterCombatSuppressed = true;
                CancelMonsterCalculationForCombat();
            }
            return;
        }
        _monsterCombatSuppressed = false;
        PollMonsterCalculation();
        Guid? encounter = Data.CurrentEncounterId;
        if (encounter != _monsterObservedEncounter)
        {
            _monsterObservedEncounter = encounter;
            _monsterCandidatePayload = null;
            _monsterCompletedPayload = null;
            _monsterResult = null;
            _pendingMonsterPrediction = null;
            _monsterPayloadChangedAt = Time.realtimeSinceStartup;
        }
        bool ready = IsLiveMonsterEncounterReady();
        if (!ready || string.IsNullOrEmpty(_lastLiveInventoryPayload))
        {
            return;
        }
        if (!string.Equals(_monsterCandidatePayload, _lastLiveInventoryPayload,
                StringComparison.Ordinal))
        {
            _monsterCandidatePayload = _lastLiveInventoryPayload;
            _pendingMonsterPrediction = null;
            _monsterPayloadChangedAt = Time.realtimeSinceStartup;
        }
        if (!IsMonsterCalculating && !IsSearching && !IsMoving && !IsBaselineCalculating &&
            !IsLocalDuelCalculating &&
            !CardController.IsAnyCardDragging && !AppState.IsWaitingForServerResponse &&
            Time.realtimeSinceStartup - _monsterPayloadChangedAt >= 1f &&
            !string.Equals(_monsterCompletedPayload, _monsterCandidatePayload,
                StringComparison.Ordinal))
        {
            StartMonsterCalculation(automatic: true);
        }
    }

    private void DrawMonsterCombatControls()
    {
        if (Data.Run?.Player is null) return;
        bool encounterReady = !IsCombatOrReplayActive() && IsLiveMonsterEncounterReady();
        DrawMonsterHeadOverlay(encounterReady);
    }

    private static bool IsLiveMonsterEncounterReady()
    {
        var opponent = Data.Run?.Opponent;
        return Data.CurrentEncounterId.HasValue && opponent is not null &&
            string.Equals(opponent.Hero.ToString(), "Common", StringComparison.OrdinalIgnoreCase) &&
            opponent.Hand.GetItemsAsEnumerable().OfType<Card>().Any();
    }

    private void CancelMonsterCalculationForCombat()
    {
        Process? process = _monsterProcess;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (Exception exception)
        {
            Logger.LogWarning("cannot stop monster calculation on combat entry: " +
                exception.Message);
        }
        process.Dispose();
        _monsterProcess = null;
        _monsterProcessLog = null;
        FinishMonsterArtifacts(false, "combat started", null);
        _monsterStatus = "战斗中，已暂停野怪计算";
    }

    private void DrawMonsterHeadOverlay(bool encounterReady)
    {
        if (!encounterReady || (!IsMonsterCalculating && _monsterResult is null))
        {
            return;
        }
        Vector2 center = new Vector2(Screen.width * 0.72f, 85f);
        try
        {
            BoardManager? boardManager = Singleton<BoardManager>.Instance;
            Camera? camera = Camera.main;
            if (boardManager is not null && camera is not null)
            {
                Transform anchor = boardManager.GetAnchor(AnchorSide.Opponent, AnchorType.Portrait);
                Vector3 screen = camera.WorldToScreenPoint(anchor.position);
                if (screen.z > 0f)
                {
                    center = new Vector2(screen.x, Screen.height - screen.y - 72f);
                }
            }
        }
        catch (Exception)
        {
            // The board can transition between the anchor lookup and OnGUI repaint.
        }
        var rect = new Rect(center.x - 120f, center.y - 30f, 240f, 60f);
        GUI.Box(rect, GUIContent.none);
        string text;
        if (IsMonsterCalculating)
        {
            text = "Calculating monster result...";
        }
        else
        {
            bool stale = !string.Equals(_monsterInputPayload, _lastLiveInventoryPayload,
                StringComparison.Ordinal);
            if (!_monsterResult!.PredictionReady)
            {
                text = "UNRELIABLE  " + (_monsterResult.ValidationErrors?.FirstOrDefault() ??
                    "snapshot is incomplete");
            }
            else
            {
                text = $"胜率 {_monsterResult.PlayerWinRate:P0}\n" +
                    $"{_monsterResult.PlayerWins}胜 {_monsterResult.OpponentWins}负 " +
                    $"{_monsterResult.Draws}平 · {_monsterResult.Samples}场" +
                    (stale ? " · 更新中" : string.Empty);
            }
        }
        GUI.Label(new Rect(rect.x + 8f, rect.y + 9f, rect.width - 16f, 42f), text);
    }

    private void StartMonsterCalculation(bool automatic)
    {
        if (IsMonsterCalculating || IsSearching || IsMoving || IsBaselineCalculating ||
            IsLocalDuelCalculating)
        {
            return;
        }
        if (!CanUseCatalog(out string catalogReason))
        {
            SetMonsterStatus(catalogReason);
            return;
        }
        var opponent = Data.Run?.Opponent;
        if (!IsLiveMonsterEncounterReady() || IsCombatOrReplayActive() || opponent is null)
        {
            SetMonsterStatus("No selected pre-combat monster encounter");
            return;
        }
        Card[] monsterItems = opponent.Hand.GetItemsAsEnumerable()
            .OfType<Card>().ToArray();
        if (monsterItems.Length == 0)
        {
            SetMonsterStatus("Monster board is not available yet");
            return;
        }
        try
        {
            CaptureLiveInventory(DateTime.UtcNow);
            string core = GetRuntimeFile("BazaarLab.Combat.dll");
            string catalog = GetCatalogFile();
            string liveInput = StateFile("live-inventory.json");
            if (!File.Exists(core) || !File.Exists(catalog) || !File.Exists(liveInput))
            {
                SetMonsterStatus("Combat runtime, catalog, or live snapshot is missing");
                return;
            }

            _monsterResult = null;
            _monsterInputPayload = _lastLiveInventoryPayload;
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            _monsterInputPath = TemporaryArtifactFile("monster",
                "monster-input-" + stamp + ".json");
            File.Copy(liveInput, _monsterInputPath, overwrite: false);
            _monsterResultPath = TemporaryArtifactFile("monster",
                "monster-result-" + stamp + ".json");
            string dotnetExecutable = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (!File.Exists(dotnetExecutable)) dotnetExecutable = "dotnet";
            var output = new StringBuilder();
            var start = new ProcessStartInfo
            {
                FileName = dotnetExecutable,
                Arguments = Quote(core) + " predict-bpp " + Quote(catalog) + " " +
                    Quote(_monsterInputPath) + " 20260831 50 2400 " +
                    Quote(_monsterResultPath),
                WorkingDirectory = Path.GetDirectoryName(core) ?? Paths.GameRootPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var process = new Process { StartInfo = start, EnableRaisingEvents = false };
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null) lock (output) output.AppendLine(args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null) lock (output) output.AppendLine(args.Data);
            };
            if (!process.Start())
            {
                process.Dispose();
                SetMonsterStatus("Failed to start local combat process");
                _monsterCompletedPayload = _monsterInputPayload;
                FinishMonsterArtifacts(true, _monsterStatus, null);
                return;
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _monsterProcess = process;
            _monsterProcessLog = output;
            SetMonsterStatus((automatic ? "Auto: " : string.Empty) +
                $"simulating {monsterItems.Length} monster items...");
        }
        catch (Exception exception)
        {
            SetMonsterStatus("Calculation failed: " + exception.Message);
            _monsterCompletedPayload = _monsterInputPayload;
            FinishMonsterArtifacts(true, _monsterStatus, exception.ToString());
        }
    }

    private void PollMonsterCalculation()
    {
        Process? process = _monsterProcess;
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
            string log = string.Empty;
            if (_monsterProcessLog is not null)
            {
                lock (_monsterProcessLog) log = _monsterProcessLog.ToString();
            }
            process.Dispose();
            _monsterProcess = null;
            _monsterProcessLog = null;
            if (exitCode != 0 || string.IsNullOrEmpty(_monsterResultPath) ||
                !File.Exists(_monsterResultPath))
            {
                Logger.LogWarning("monster-combat process failed (exit " + exitCode + "):\n" + log);
                SetMonsterStatus("Combat process failed (exit " + exitCode + "): " +
                    LastLine(log));
                _monsterCompletedPayload = _monsterInputPayload;
                FinishMonsterArtifacts(true, _monsterStatus, log);
                return;
            }
            string resultJson = File.ReadAllText(_monsterResultPath);
            _monsterResult = JsonSerializer.Deserialize<MonsterPredictionDto>(
                resultJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            if (_monsterResult is null)
            {
                SetMonsterStatus("Combat result was empty");
                _monsterCompletedPayload = _monsterInputPayload;
                FinishMonsterArtifacts(true, _monsterStatus, log);
                return;
            }
            if (!_monsterResult.PredictionReady)
            {
                SetMonsterStatus("UNRELIABLE: " +
                    (_monsterResult.ValidationErrors?.FirstOrDefault() ??
                     "snapshot is incomplete"));
                _monsterCompletedPayload = _monsterInputPayload;
                FinishMonsterArtifacts(true, _monsterStatus, log);
                return;
            }
            string inputJson = _monsterInputPath is not null && File.Exists(_monsterInputPath)
                ? File.ReadAllText(_monsterInputPath) : string.Empty;
            bool stale = !string.Equals(_monsterInputPayload, _lastLiveInventoryPayload,
                StringComparison.Ordinal);
            if (!stale)
            {
                _pendingMonsterPrediction = new MonsterPredictionAudit
                {
                    EncounterId = Data.CurrentEncounterId?.ToString("D") ?? string.Empty,
                    Predicted = NormalizePredictedOutcome(_monsterResult),
                    PlayerWins = _monsterResult.PlayerWins,
                    OpponentWins = _monsterResult.OpponentWins,
                    Draws = _monsterResult.Draws,
                    InputJson = inputJson,
                    ResultJson = resultJson,
                };
            }
            _monsterCompletedPayload = _monsterInputPayload;
            SetMonsterStatus($"完成：{_monsterResult.Samples} 场，" +
                $"胜率 {_monsterResult.PlayerWinRate:P0}" +
                (stale ? "; state changed" : string.Empty));
            FinishMonsterArtifacts(false, "success", null);
        }
        catch (Exception exception)
        {
            process.Dispose();
            _monsterProcess = null;
            _monsterProcessLog = null;
            SetMonsterStatus("Cannot load combat result: " + exception.Message);
            _monsterCompletedPayload = _monsterInputPayload;
            FinishMonsterArtifacts(true, _monsterStatus, exception.ToString());
        }
    }

    private void FinishMonsterArtifacts(bool preserve, string reason, string? log)
    {
        if (preserve)
            PreserveArtifacts("monster", reason, log, _monsterInputPath, _monsterResultPath);
        else
            DeleteArtifacts(_monsterInputPath, _monsterResultPath);
        _monsterInputPath = null;
        _monsterResultPath = null;
    }

    private static string NormalizePredictedOutcome(MonsterPredictionDto result)
    {
        if (!string.IsNullOrWhiteSpace(result.Predicted))
            return result.Predicted!.Trim().ToLowerInvariant();
        if (result.PlayerWins > result.OpponentWins) return "win";
        if (result.OpponentWins > result.PlayerWins) return "loss";
        return "draw";
    }

    private void AttachMonsterPredictionToCapture(string captureId, string opponentHero)
    {
        if (!string.Equals(opponentHero, "Common", StringComparison.OrdinalIgnoreCase))
            return;
        MonsterPredictionAudit? prediction = _pendingMonsterPrediction;
        string encounterId = Data.CurrentEncounterId?.ToString("D") ?? string.Empty;
        if (prediction is null && !string.IsNullOrEmpty(encounterId))
            _encounterPredictionsByEncounter.TryGetValue(encounterId, out prediction);
        if (prediction is null) return;
        _monsterPredictionsByCapture[captureId] = prediction;
        _pendingMonsterPrediction = null;
        if (!string.IsNullOrEmpty(encounterId))
            _encounterPredictionsByEncounter.Remove(encounterId);
    }

    private void AuditMonsterPrediction(string captureId, string actualWinner)
    {
        if (!_monsterPredictionsByCapture.Remove(captureId,
                out MonsterPredictionAudit? prediction))
            return;
        string actual = actualWinner.Equals("Player", StringComparison.OrdinalIgnoreCase)
            ? "win" : actualWinner.Equals("Opponent", StringComparison.OrdinalIgnoreCase)
                ? "loss" : "draw";
        if (string.Equals(prediction.Predicted, actual, StringComparison.OrdinalIgnoreCase))
            return;
        PreserveArtifactText(Path.Combine("monster", "mismatches"),
            "monster prediction did not match the official combat result",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["monster-input.json"] = prediction.InputJson,
                ["monster-result.json"] = prediction.ResultJson,
            }, new
            {
                capture_id = captureId,
                encounter_id = prediction.EncounterId,
                predicted = prediction.Predicted,
                actual,
                prediction.PlayerWins,
                prediction.OpponentWins,
                prediction.Draws,
            });
    }

    private void SetMonsterStatus(string status)
    {
        _monsterStatus = status;
        Logger.LogInfo("monster-combat: " + status);
    }

    private sealed class MonsterPredictionDto
    {
        public string BattleId { get; set; } = string.Empty;
        public int Samples { get; set; }
        public int PlayerWins { get; set; }
        public int OpponentWins { get; set; }
        public int Draws { get; set; }
        public double PlayerWinRate { get; set; }
        public double PlayerOutcomeProbability { get; set; }
        public double ConservativePlayerProbabilityLower95 { get; set; }
        public double ConservativePlayerProbabilityUpper95 { get; set; }
        public string? ConfidentPrediction { get; set; }
        public string? Predicted { get; set; }
        public bool StoppedEarly { get; set; }
        public Dictionary<string, int>? UnsupportedActions { get; set; }
        public bool PredictionReady { get; set; }
        public string[]? ValidationErrors { get; set; }
        public string[]? ValidationWarnings { get; set; }
        public string[]? SkippedCards { get; set; }
    }
}
