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
    private string? _monsterResultPath;
    private string? _monsterInputPayload;
    private MonsterPredictionDto? _monsterResult;
    private string _monsterStatus = "Select a monster encounter, then CALCULATE";
    private string? _monsterCandidatePayload;
    private string? _monsterCompletedPayload;
    private float _monsterPayloadChangedAt;
    private Guid? _monsterObservedEncounter;
    private bool _monsterCombatSuppressed;

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
        _monsterResultPath = null;
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
                string decision = _monsterResult.ConfidentPrediction ??
                    (_monsterResult.Predicted ?? "uncertain");
                text = $"{decision.ToUpperInvariant()}  " +
                    $"{_monsterResult.ConservativePlayerProbabilityLower95:P0}–" +
                    $"{_monsterResult.ConservativePlayerProbabilityUpper95:P0}" +
                    (stale ? "  updating..." : string.Empty);
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
            string liveInput = Path.Combine(_outputDirectory, "live-inventory.json");
            if (!File.Exists(core) || !File.Exists(catalog) || !File.Exists(liveInput))
            {
                SetMonsterStatus("Combat runtime, catalog, or live snapshot is missing");
                return;
            }

            _monsterResult = null;
            _monsterInputPayload = _lastLiveInventoryPayload;
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            string input = Path.Combine(_outputDirectory,
                "monster-input-" + stamp + ".json");
            File.Copy(liveInput, input, overwrite: false);
            _monsterResultPath = Path.Combine(_outputDirectory,
                "monster-result-" + stamp + ".json");
            string dotnetExecutable = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (!File.Exists(dotnetExecutable)) dotnetExecutable = "dotnet";
            var output = new StringBuilder();
            var start = new ProcessStartInfo
            {
                FileName = dotnetExecutable,
                Arguments = Quote(core) + " predict-bpp-adaptive " + Quote(catalog) + " " +
                    Quote(input) + " 20260831 21 101 20 2400 " + Quote(_monsterResultPath),
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
                return;
            }
            _monsterResult = JsonSerializer.Deserialize<MonsterPredictionDto>(
                File.ReadAllText(_monsterResultPath), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            if (_monsterResult is null)
            {
                SetMonsterStatus("Combat result was empty");
                return;
            }
            string confidence = _monsterResult.ConfidentPrediction is null
                ? "low confidence" : "confident " + _monsterResult.ConfidentPrediction;
            bool stale = !string.Equals(_monsterInputPayload, _lastLiveInventoryPayload,
                StringComparison.Ordinal);
            _monsterCompletedPayload = _monsterInputPayload;
            SetMonsterStatus($"Completed: {_monsterResult.Samples} samples, {confidence}" +
                (stale ? "; state changed" : string.Empty));
        }
        catch (Exception exception)
        {
            process.Dispose();
            _monsterProcess = null;
            _monsterProcessLog = null;
            SetMonsterStatus("Cannot load combat result: " + exception.Message);
        }
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
