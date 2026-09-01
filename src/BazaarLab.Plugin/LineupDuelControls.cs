using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using BazaarGameClient.Domain.Models;
using BazaarGameClient.Domain.Models.Cards;
using BazaarPlusPlus.Game.PvpBattles;
using BepInEx;
using TheBazaar;
using UnityEngine;

namespace BazaarLab.Plugin;

public sealed partial class Plugin
{
    private const string LineupCodePrefix = "BL1:";
    private const string LegacyLineupCodePrefix = "LIL1:";
    private const int LineupWindowId = 191108;
    private const float LineupWindowWidth = 620f;
    private const int MaxLineupCodeLength = 262144;
    private const int MaxLineupJsonLength = 2097152;

    private Rect _lineupWindowRect;
    private bool _lineupWindowInitialized;
    private bool _lineupWindowMinimized = true;
    private Vector2 _lineupScroll;
    private string _lineupCodeA = string.Empty;
    private string _lineupCodeB = string.Empty;
    private string _lineupSeed = "20260831";
    private string _lineupStatus = "导出阵容，或粘贴两个 BL1/LIL1 阵容码";
    private string _lineupClipboardToast = string.Empty;
    private float _lineupClipboardToastUntil;
    private LineupEnvelopeDto? _lastStableLineup;
    private LineupEnvelopeDto? _combatOpeningPlayerLineup;
    private LineupEnvelopeDto? _combatOpeningOpponentLineup;
    private object? _pvpLineupArchiveRun;
    private string? _pvpLineupArchiveSessionId;
    private readonly HashSet<string> _archivedPvpOpeningIds = new(StringComparer.Ordinal);
    private string? _stableCandidateFingerprint;
    private float _stableCandidateSince;
    private float _nextStableProbeAt;
    private string _catalogFingerprint = "missing";
    private Process? _lineupDuelProcess;
    private StringBuilder? _lineupDuelLog;
    private string? _lineupDuelInputPath;
    private string? _lineupDuelResultPath;
    private string? _lineupDuelTracePath;
    private string? _lineupDuelSimulationPath;
    private string _lineupDuelPhase = string.Empty;
    private int _lineupDuelTraceEvents;
    private bool _lineupAutoPlayRequested;
    private LineupEnvelopeDto? _lineupReplayA;
    private LineupEnvelopeDto? _lineupReplayB;
    private MonsterPredictionDto? _lineupDuelResult;

    private bool IsLocalDuelCalculating => _lineupDuelProcess is not null;

    private void InitializeLineupDuelControls()
    {
        string catalog = Path.Combine(Paths.GameRootPath, ".reverse", "catalog",
            "official-cards.jsonl");
        if (File.Exists(catalog))
        {
            var info = new FileInfo(catalog);
            _catalogFingerprint = info.Length.ToString("x") + "-" +
                info.LastWriteTimeUtc.Ticks.ToString("x");
        }
        string cache = Path.Combine(_outputDirectory, "last-stable-lineup.json");
        if (File.Exists(cache))
        {
            try
            {
                _lastStableLineup = JsonSerializer.Deserialize<LineupEnvelopeDto>(
                    File.ReadAllText(cache), LineupJsonOptions());
                if (_lastStableLineup is not null)
                {
                    ValidateEnvelope(_lastStableLineup);
                    _lineupStatus = "已载入最近的稳定阵容缓存";
                }
            }
            catch (Exception exception)
            {
                Logger.LogWarning("lineup cache ignored: " + exception.Message);
                _lastStableLineup = null;
            }
        }
    }

    private void DisposeLineupDuelControls()
    {
        Process? process = _lineupDuelProcess;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception) { }
        process.Dispose();
        _lineupDuelProcess = null;
        _lineupDuelLog = null;
    }

    private void UpdateLineupDuelControls()
    {
        PollLocalDuel();
        if (Time.realtimeSinceStartup < _nextStableProbeAt) return;
        _nextStableProbeAt = Time.realtimeSinceStartup + 0.3f;

        Player? player = Data.Run?.Player;
        if (player is null || Data.IsInCombat || CardController.IsAnyCardDragging ||
            AppState.IsWaitingForServerResponse || IsMoving)
        {
            _stableCandidateFingerprint = null;
            return;
        }
        try
        {
            LineupEnvelopeDto candidate = BuildLiveLineup(player, "live-stable");
            string fingerprint = candidate.checksum;
            if (!string.Equals(_stableCandidateFingerprint, fingerprint,
                    StringComparison.Ordinal))
            {
                _stableCandidateFingerprint = fingerprint;
                _stableCandidateSince = Time.realtimeSinceStartup;
                return;
            }
            if (Time.realtimeSinceStartup - _stableCandidateSince < 0.5f ||
                string.Equals(_lastStableLineup?.checksum, fingerprint,
                    StringComparison.Ordinal)) return;

            _lastStableLineup = candidate;
            PublishAtomic("last-stable-lineup.json", JsonSerializer.Serialize(candidate,
                new JsonSerializerOptions { WriteIndented = true }));
            PublishAtomic("last-stable-lineup.code.txt", EncodeLineup(candidate));
            _lineupStatus = "稳定阵容缓存已更新";
        }
        catch (Exception exception)
        {
            Logger.LogWarning("stable lineup capture skipped: " + exception.Message);
        }
    }

    private void CaptureOpeningLineups(PvpBattleSnapshots snapshots, string playerHero,
        string opponentHero, IReadOnlyDictionary<string, int> playerAttributes,
        IReadOnlyDictionary<string, int> opponentAttributes, uint day, uint hour,
        string messageId, string captureId)
    {
        _combatOpeningPlayerLineup = BuildSnapshotLineup(playerHero, playerAttributes,
            snapshots.PlayerHand, snapshots.PlayerSkills, "combat-opening-player");
        _combatOpeningOpponentLineup = BuildSnapshotLineup(opponentHero, opponentAttributes,
            snapshots.OpponentHand, snapshots.OpponentSkills, "combat-opening-opponent");
        PublishAtomic("combat-opening-player.json", JsonSerializer.Serialize(
            _combatOpeningPlayerLineup, new JsonSerializerOptions { WriteIndented = true }));
        PublishAtomic("combat-opening-opponent.json", JsonSerializer.Serialize(
            _combatOpeningOpponentLineup, new JsonSerializerOptions { WriteIndented = true }));
        ArchivePvpOpeningLineupCodes(day, hour, messageId, captureId);
    }

    private void ArchivePvpOpeningLineupCodes(
        uint day, uint hour, string messageId, string captureId)
    {
        if (_combatOpeningPlayerLineup is null || _combatOpeningOpponentLineup is null) return;
        try
        {
            object? run = Data.Run;
            if (!ReferenceEquals(run, _pvpLineupArchiveRun) ||
                string.IsNullOrEmpty(_pvpLineupArchiveSessionId))
            {
                _pvpLineupArchiveRun = run;
                _pvpLineupArchiveSessionId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                _archivedPvpOpeningIds.Clear();
            }
            if (!_archivedPvpOpeningIds.Add(messageId)) return;
            string root = Path.Combine(_outputDirectory, "pvp-lineups",
                "run-" + _pvpLineupArchiveSessionId);
            string dayDirectory = Path.Combine(root, "day-" + day.ToString("00"));
            Directory.CreateDirectory(dayDirectory);
            string playerCode = EncodeLineup(_combatOpeningPlayerLineup);
            string opponentCode = EncodeLineup(_combatOpeningOpponentLineup);
            string safeCaptureId = SafeFileId(captureId);
            var archive = new
            {
                schema = "bazaarlab-pvp-lineup-codes-v1",
                plugin_version = PluginVersion,
                run_session = _pvpLineupArchiveSessionId,
                day,
                hour,
                message_id = messageId,
                capture_id = captureId,
                recorded_at_utc = DateTime.UtcNow.ToString("O"),
                player = new
                {
                    hero = _combatOpeningPlayerLineup.payload.hero,
                    checksum = _combatOpeningPlayerLineup.checksum,
                    lineup_code = playerCode,
                },
                opponent = new
                {
                    hero = _combatOpeningOpponentLineup.payload.hero,
                    checksum = _combatOpeningOpponentLineup.checksum,
                    lineup_code = opponentCode,
                },
            };
            string archiveJson = JsonSerializer.Serialize(archive,
                new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
            File.WriteAllText(Path.Combine(dayDirectory,
                safeCaptureId + ".lineups.json"), archiveJson);
            WriteLineupArchiveLatest(Path.Combine(dayDirectory, "latest.lineups.json"), archiveJson);
            WriteLineupArchiveLatest(Path.Combine(dayDirectory, "player.code.txt"),
                playerCode + Environment.NewLine);
            WriteLineupArchiveLatest(Path.Combine(dayDirectory, "opponent.code.txt"),
                opponentCode + Environment.NewLine);
            Logger.LogInfo($"archived PvP lineup codes: day {day}, {dayDirectory}");
        }
        catch (Exception exception)
        {
            _archivedPvpOpeningIds.Remove(messageId);
            Logger.LogWarning("PvP lineup-code archive skipped: " + exception.Message);
        }
    }

    private static void WriteLineupArchiveLatest(string path, string content)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    private void DrawLineupDuelControls()
    {
        float height = _lineupWindowMinimized ? 30f : 520f;
        if (!_lineupWindowInitialized)
        {
            _lineupWindowRect = new Rect(18f, 70f, LineupWindowWidth, height);
            _lineupWindowInitialized = true;
        }
        _lineupWindowRect.width = LineupWindowWidth;
        _lineupWindowRect.height = height;
        _lineupWindowRect = GUI.Window(LineupWindowId, _lineupWindowRect,
            DrawLineupDuelWindow, "本地阵容码对战");
        _lineupWindowRect.x = Mathf.Clamp(_lineupWindowRect.x,
            -LineupWindowWidth + 46f, Screen.width - 46f);
        _lineupWindowRect.y = Mathf.Clamp(_lineupWindowRect.y, 0f,
            Mathf.Max(0f, Screen.height - 30f));
        SetFloatingWindowBlocker(2, _lineupWindowRect, true);
        DrawLineupClipboardToast();
    }

    private void DrawLineupDuelWindow(int windowId)
    {
        if (GUI.Button(new Rect(LineupWindowWidth - 292f, 3f, 126f, 21f),
                "打开历史目录"))
            OpenLineupHistoryDirectory();
        if (GUI.Button(new Rect(LineupWindowWidth - 158f, 3f, 126f, 21f),
                "复制当前阵容"))
            CopyPreferredLineup();
        if (GUI.Button(new Rect(LineupWindowWidth - 28f, 3f, 24f, 21f),
                _lineupWindowMinimized ? "+" : "-"))
            _lineupWindowMinimized = !_lineupWindowMinimized;
        GUI.DragWindow(new Rect(0f, 0f, LineupWindowWidth - 296f, 25f));
        if (_lineupWindowMinimized) return;

        GUILayout.BeginArea(new Rect(10f, 27f, LineupWindowWidth - 20f, 484f));
        _lineupScroll = GUILayout.BeginScrollView(_lineupScroll);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("当前阵容 → A")) ExportPreferredTo(ref _lineupCodeA);
        if (GUILayout.Button("当前阵容 → B")) ExportPreferredTo(ref _lineupCodeB);
        if (GUILayout.Button("对手阵容 → B")) ExportOpponentToB();
        GUILayout.EndHorizontal();

        GUILayout.Label("阵容 A");
        _lineupCodeA = GUILayout.TextArea(_lineupCodeA, GUILayout.Height(62f));
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("粘贴 A")) _lineupCodeA = GUIUtility.systemCopyBuffer.Trim();
        GUILayout.Label(DescribeCode(_lineupCodeA), GUILayout.Width(475f));
        GUILayout.EndHorizontal();
        GUILayout.Label("阵容 B");
        _lineupCodeB = GUILayout.TextArea(_lineupCodeB, GUILayout.Height(62f));
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("粘贴 B")) _lineupCodeB = GUIUtility.systemCopyBuffer.Trim();
        GUILayout.Label(DescribeCode(_lineupCodeB), GUILayout.Width(475f));
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("交换", GUILayout.Width(80f)))
            (_lineupCodeA, _lineupCodeB) = (_lineupCodeB, _lineupCodeA);
        bool duelControlsAvailable = !IsLocalDuelCalculating && !IsSearching && !IsMoving &&
            !IsMonsterCalculating && !IsBaselineCalculating &&
            !IsEncounterPreviewCalculating;
        GUI.enabled = duelControlsAvailable;
        if (GUILayout.Button("清空输入", GUILayout.Width(90f)))
            ClearLineupDuelInputs();
        GUI.enabled = true;
        GUILayout.Label("种子", GUILayout.Width(35f));
        _lineupSeed = GUILayout.TextField(_lineupSeed, GUILayout.Width(105f));
        GUI.enabled = duelControlsAvailable;
        if (GUILayout.Button(IsLocalDuelCalculating ? "正在准备回放……" :
                "确认并播放", GUILayout.Height(32f)))
        {
            _lineupAutoPlayRequested = true;
            StartLocalDuel();
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        GUILayout.Label(_lineupStatus);
        if (_lineupDuelResult is not null)
        {
            GUILayout.Label($"A 胜率 {_lineupDuelResult.PlayerOutcomeProbability:P1}   " +
                $"胜/负/平 {_lineupDuelResult.PlayerWins}/{_lineupDuelResult.OpponentWins}/" +
                $"{_lineupDuelResult.Draws}   样本 {_lineupDuelResult.Samples}");
            if (_lineupDuelResult.UnsupportedActions is { Count: > 0 })
                GUILayout.Label("未支持动作：" + string.Join(", ",
                    _lineupDuelResult.UnsupportedActions.Select(pair =>
                        pair.Key + "=" + pair.Value)));
            if (_lineupDuelTraceEvents > 0)
                GUILayout.Label($"回放源轨迹：{_lineupDuelTraceEvents} 个完整事件");
        }
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void OpenLineupHistoryDirectory()
    {
        try
        {
            string directory = Path.Combine(_outputDirectory, "pvp-lineups");
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
            _lineupStatus = "已打开本地 PvP 历史阵容码目录";
            ShowLineupClipboardToast("已打开历史目录", true);
        }
        catch (Exception exception)
        {
            _lineupStatus = "无法打开历史目录：" + exception.Message;
            ShowLineupClipboardToast("无法打开历史目录", false);
            Logger.LogWarning("opening PvP lineup history directory failed: " +
                exception.Message);
        }
    }

    private void ClearLineupDuelInputs()
    {
        _lineupCodeA = string.Empty;
        _lineupCodeB = string.Empty;
        _lineupDuelResult = null;
        _lineupReplayA = null;
        _lineupReplayB = null;
        _lineupDuelTraceEvents = 0;
        _lineupAutoPlayRequested = false;
        _lineupStatus = "阵容 A、B 输入和上一场结果已清空";
        GUI.FocusControl(null);
    }

    private void ExportPreferredTo(ref string target)
    {
        LineupEnvelopeDto? lineup = Data.IsInCombat
            ? _combatOpeningPlayerLineup ?? _lastStableLineup
            : TryBuildCurrentLineup() ?? _lastStableLineup;
        if (lineup is null) { _lineupStatus = "没有可用的当前阵容或缓存阵容"; return; }
        target = EncodeLineup(lineup);
        _lineupStatus = "已导出 " + lineup.source + "（" + lineup.payload.board.Count +
            " 件上阵物品，" + lineup.payload.skills.Count + " 个技能）";
    }

    private void CopyPreferredLineup()
    {
        LineupEnvelopeDto? lineup = Data.IsInCombat
            ? _combatOpeningPlayerLineup ?? _lastStableLineup
            : TryBuildCurrentLineup() ?? _lastStableLineup;
        if (lineup is null)
        {
            _lineupStatus = "没有可用的当前阵容或缓存阵容";
            ShowLineupClipboardToast("没有可导出的阵容", false);
            return;
        }
        string code = EncodeLineup(lineup);
        GUIUtility.systemCopyBuffer = code;
        _lineupStatus = "已将 " + lineup.source + " 阵容码复制到剪贴板";
        ShowLineupClipboardToast("已复制到剪贴板  |  " +
            lineup.payload.board.Count + " 件物品，" + lineup.payload.skills.Count +
            " 个技能", true);
    }

    private void ShowLineupClipboardToast(string message, bool success)
    {
        _lineupClipboardToast = success ? "成功  " + message : "注意  " + message;
        _lineupClipboardToastUntil = Time.realtimeSinceStartup + 2.5f;
    }

    private void DrawLineupClipboardToast()
    {
        if (string.IsNullOrEmpty(_lineupClipboardToast) ||
            Time.realtimeSinceStartup >= _lineupClipboardToastUntil) return;
        const float width = 330f;
        const float height = 34f;
        float x = Mathf.Clamp(_lineupWindowRect.x +
            (_lineupWindowRect.width - width) * 0.5f, 4f,
            Mathf.Max(4f, Screen.width - width - 4f));
        float y = Mathf.Clamp(_lineupWindowRect.y + 34f, 4f,
            Mathf.Max(4f, Screen.height - height - 4f));
        Color previous = GUI.color;
        GUI.color = new Color(0.72f, 1f, 0.72f, 1f);
        GUI.Box(new Rect(x, y, width, height), _lineupClipboardToast);
        GUI.color = previous;
    }

    private void ExportOpponentToB()
    {
        if (_combatOpeningOpponentLineup is null)
        {
            _lineupStatus = "没有可用的对手开战阵容快照";
            return;
        }
        _lineupCodeB = EncodeLineup(_combatOpeningOpponentLineup);
        _lineupStatus = "已导出最近一次开战时的对手阵容";
    }

    private LineupEnvelopeDto? TryBuildCurrentLineup()
    {
        Player? player = Data.Run?.Player;
        if (player is null || CardController.IsAnyCardDragging ||
            AppState.IsWaitingForServerResponse || IsMoving) return null;
        try { return BuildLiveLineup(player, "live-current"); }
        catch (Exception) { return null; }
    }

    private string DescribeCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "empty";
        try
        {
            LineupEnvelopeDto envelope = DecodeLineup(code);
            string warning = envelope.catalog_fingerprint == _catalogFingerprint
                ? string.Empty : " [catalog differs]";
            return $"{envelope.payload.hero}: {envelope.payload.board.Count} board, " +
                $"{envelope.payload.skills.Count} skills{warning}";
        }
        catch (Exception exception) { return "invalid: " + exception.Message; }
    }

    private void StartLocalDuel()
    {
        if (IsLocalDuelCalculating || IsSearching || IsMoving || IsMonsterCalculating ||
            IsBaselineCalculating || IsEncounterPreviewCalculating) return;
        try
        {
            LineupEnvelopeDto a = DecodeLineup(_lineupCodeA);
            LineupEnvelopeDto b = DecodeLineup(_lineupCodeB);
            _lineupReplayA = a;
            _lineupReplayB = b;
            if (!int.TryParse(_lineupSeed, out int seed))
                throw new InvalidDataException("seed must be a 32-bit integer");

            string core = GetRuntimeFile("BazaarLab.Combat.dll");
            string catalog = GetCatalogFile();
            if (!File.Exists(core) || !File.Exists(catalog))
                throw new FileNotFoundException("local combat runtime or catalog is missing");
            string directory = Path.Combine(_outputDirectory, "local-duels");
            Directory.CreateDirectory(directory);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            string input = Path.Combine(directory, "duel-input-" + stamp + ".json");
            _lineupDuelInputPath = input;
            _lineupDuelResultPath = Path.Combine(directory, "duel-result-" + stamp + ".json");
            _lineupDuelTracePath = Path.Combine(directory, "duel-trace-" + stamp + ".json");
            _lineupDuelSimulationPath = Path.Combine(directory,
                "duel-simulation-" + stamp + ".json");
            File.WriteAllText(input, JsonSerializer.Serialize(BuildDuelSnapshot(a, b, stamp),
                new JsonSerializerOptions { WriteIndented = true }));

            if (_lineupAutoPlayRequested)
            {
                _lineupDuelResult = null;
                _lineupDuelTraceEvents = 0;
                StartLocalDuelTrace();
                return;
            }

            string dotnet = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (!File.Exists(dotnet)) dotnet = "dotnet";
            var log = new StringBuilder();
            var start = new ProcessStartInfo
            {
                FileName = dotnet,
                Arguments = Quote(core) + " predict-bpp-adaptive " + Quote(catalog) + " " +
                    Quote(input) + " " + seed + " 21 101 20 2400 " +
                    Quote(_lineupDuelResultPath),
                WorkingDirectory = Path.GetDirectoryName(core) ?? Paths.GameRootPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, args) =>
            { if (args.Data is not null) lock (log) log.AppendLine(args.Data); };
            process.ErrorDataReceived += (_, args) =>
            { if (args.Data is not null) lock (log) log.AppendLine(args.Data); };
            if (!process.Start()) { process.Dispose(); throw new IOException("process did not start"); }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _lineupDuelProcess = process;
            _lineupDuelLog = log;
            _lineupDuelPhase = "prediction";
            _lineupDuelResult = null;
            _lineupDuelTraceEvents = 0;
            _lineupStatus = "正在模拟本地对战……";
        }
        catch (Exception exception) { _lineupStatus = "无法模拟：" + exception.Message; }
    }

    private void PollLocalDuel()
    {
        Process? process = _lineupDuelProcess;
        if (process is null || !process.HasExited) return;
        try
        {
            process.WaitForExit();
            int exit = process.ExitCode;
            string log = string.Empty;
            if (_lineupDuelLog is not null) lock (_lineupDuelLog) log = _lineupDuelLog.ToString();
            process.Dispose();
            _lineupDuelProcess = null;
            _lineupDuelLog = null;
            string? expectedOutput = _lineupDuelPhase == "prediction"
                ? _lineupDuelResultPath : _lineupDuelTracePath;
            if (exit != 0 || string.IsNullOrEmpty(expectedOutput) ||
                !File.Exists(expectedOutput))
                throw new InvalidDataException("combat process exit " + exit + ": " + LastLine(log));
            if (_lineupDuelPhase == "prediction")
            {
                _lineupDuelResult = JsonSerializer.Deserialize<MonsterPredictionDto>(
                    File.ReadAllText(_lineupDuelResultPath), LineupJsonOptions()) ??
                    throw new InvalidDataException("empty result");
                StartLocalDuelTrace();
                return;
            }
            using JsonDocument trace = JsonDocument.Parse(File.ReadAllText(
                _lineupDuelTracePath ?? throw new InvalidDataException("trace path missing")));
            _lineupDuelTraceEvents = 0;
            if (trace.RootElement.TryGetProperty("Frames", out JsonElement frames))
            {
                foreach (JsonElement frame in frames.EnumerateArray())
                {
                    if (frame.TryGetProperty("Effects", out JsonElement effects))
                        _lineupDuelTraceEvents += effects.GetArrayLength();
                    if (frame.TryGetProperty("PlayerHealth", out JsonElement playerHealth))
                        _lineupDuelTraceEvents += playerHealth.GetArrayLength();
                    if (frame.TryGetProperty("OpponentHealth", out JsonElement opponentHealth))
                        _lineupDuelTraceEvents += opponentHealth.GetArrayLength();
                    if (frame.TryGetProperty("CardAttributes", out JsonElement cards))
                        _lineupDuelTraceEvents += cards.GetArrayLength();
                }
            }
            _lineupDuelPhase = string.Empty;
            _lineupStatus = "本地对战和完整回放轨迹已生成";
            if (_lineupAutoPlayRequested)
            {
                _lineupAutoPlayRequested = false;
                StartNativeLineupReplay(_lineupReplayA ??
                        throw new InvalidDataException("lineup A was lost"),
                    _lineupReplayB ?? throw new InvalidDataException("lineup B was lost"),
                    _lineupDuelInputPath ?? throw new InvalidDataException("input was lost"),
                    _lineupDuelTracePath ?? throw new InvalidDataException("trace was lost"));
            }
        }
        catch (Exception exception)
        {
            process.Dispose();
            _lineupDuelProcess = null;
            _lineupDuelLog = null;
            _lineupAutoPlayRequested = false;
            _lineupStatus = "无法读取对战结果：" + exception.Message;
        }
    }

    private void StartLocalDuelTrace()
    {
        string input = _lineupDuelInputPath ?? throw new InvalidDataException("input path missing");
        string outputPath = _lineupDuelTracePath ??
            throw new InvalidDataException("trace path missing");
        string simulationPath = _lineupDuelSimulationPath ??
            throw new InvalidDataException("simulation path missing");
        string core = GetRuntimeFile("BazaarLab.Combat.dll");
        string catalog = GetCatalogFile();
        if (!int.TryParse(_lineupSeed, out int seed))
            throw new InvalidDataException("seed must be a 32-bit integer");
        string dotnet = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!File.Exists(dotnet)) dotnet = "dotnet";
        var log = new StringBuilder();
        var start = new ProcessStartInfo
        {
            FileName = dotnet,
            Arguments = Quote(core) + " project-bpp-replay " + Quote(catalog) + " " +
                Quote(input) + " " + seed + " 2400 " + Quote(outputPath) + " " +
                Quote(simulationPath),
            WorkingDirectory = Path.GetDirectoryName(core) ?? Paths.GameRootPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, args) =>
        { if (args.Data is not null) lock (log) log.AppendLine(args.Data); };
        process.ErrorDataReceived += (_, args) =>
        { if (args.Data is not null) lock (log) log.AppendLine(args.Data); };
        if (!process.Start()) { process.Dispose(); throw new IOException("trace process did not start"); }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _lineupDuelProcess = process;
        _lineupDuelLog = log;
        _lineupDuelPhase = "trace";
        _lineupStatus = _lineupAutoPlayRequested
            ? "正在结算确定性战斗并准备 BPP 回放……"
            : "预测完成，正在生成完整回放轨迹……";
    }

    private object BuildDuelSnapshot(LineupEnvelopeDto a, LineupEnvelopeDto b, string id) => new
    {
        schema = "bazaarlab-combat-snapshot-v1",
        capture = new { plugin_version = PluginVersion, source = "lineup-code-duel" },
        battle = new { id = "local-duel-" + id, combat_kind = "local-duel",
            recorded_at_utc = DateTime.UtcNow.ToString("O"), player_hero = a.payload.hero,
            opponent_hero = b.payload.hero },
        input_quality = new { prediction_ready = true, errors = Array.Empty<string>(),
            warnings = CatalogWarnings(a, b) },
        combatants = new object[]
        {
            new { id = "player", hero = a.payload.hero, attributes = a.payload.attributes },
            new { id = "opponent", hero = b.payload.hero, attributes = b.payload.attributes },
        },
        card_sets = new object[]
        {
            DuelSet("PlayerHand", 0, "Hand", a.payload.board, "p"),
            DuelSet("PlayerSkills", 0, "Skills", a.payload.skills, "ps"),
            DuelSet("OpponentHand", 1, "Hand", b.payload.board, "o"),
            DuelSet("OpponentSkills", 1, "Skills", b.payload.skills, "os"),
        },
    };

    private string[] CatalogWarnings(LineupEnvelopeDto a, LineupEnvelopeDto b) =>
        a.catalog_fingerprint == _catalogFingerprint && b.catalog_fingerprint == _catalogFingerprint
            ? Array.Empty<string>() : new[] { "lineup code catalog fingerprint differs" };

    private static object DuelSet(string label, int owner, string section,
        IReadOnlyList<LineupCardDto> cards, string prefix) => new
    {
        label, owner, section, status = "Captured", source = "LineupCode",
        items = cards.Select((card, index) => new
        {
            instance_id = "duel:" + prefix + ":" + index + ":" + card.instance_id,
            template_id = card.template_id, type = card.type, size = card.size,
            section, socket = card.socket, name = card.name, tier = card.tier,
            enchant = card.enchant, tags = card.tags, attributes = card.attributes,
        }).ToArray(),
    };

    private LineupEnvelopeDto BuildLiveLineup(Player player, string source) =>
        CreateEnvelope(source, player.Hero.ToString(), ConvertAttributes(player.Attributes),
            player.Hand.GetItemsAsEnumerable().OfType<Card>().Select(ConvertLiveLineupCard),
            player.Skills.Cast<Card>().Select(ConvertLiveLineupCard));

    private LineupEnvelopeDto BuildSnapshotLineup(string hero,
        IReadOnlyDictionary<string, int> attributes, PvpBattleCardSetCapture board,
        PvpBattleCardSetCapture skills, string source) => CreateEnvelope(source, hero,
            new Dictionary<string, int>(attributes, StringComparer.Ordinal),
            board.Items.Where(card => string.Equals(card.Type.ToString(), "Item",
                StringComparison.OrdinalIgnoreCase)).Select(ConvertSnapshotLineupCard),
            skills.Items.Select(ConvertSnapshotLineupCard));

    private LineupEnvelopeDto CreateEnvelope(string source, string hero,
        Dictionary<string, int> attributes, IEnumerable<LineupCardDto> board,
        IEnumerable<LineupCardDto> skills)
    {
        var envelope = new LineupEnvelopeDto
        {
            plugin_version = PluginVersion,
            game_runtime_version = typeof(Data).Assembly.GetName().Version?.ToString() ?? string.Empty,
            catalog_fingerprint = _catalogFingerprint,
            captured_at_utc = DateTime.UtcNow.ToString("O"),
            source = source,
            payload = new LineupPayloadDto
            {
                hero = NormalizeHeroId(hero),
                attributes = attributes,
                board = board.OrderBy(card => SocketIndex(card.socket)).ThenBy(card =>
                    card.instance_id, StringComparer.Ordinal).ToList(),
                skills = skills.OrderBy(card => card.template_id, StringComparer.Ordinal)
                    .ThenBy(card => card.instance_id, StringComparer.Ordinal).ToList(),
            },
        };
        envelope.checksum = PayloadChecksum(envelope.payload);
        ValidateEnvelope(envelope);
        return envelope;
    }

    private static LineupCardDto ConvertLiveLineupCard(Card card) => new()
    {
        instance_id = card.InstanceId.Value,
        template_id = card.TemplateId.ToString(),
        type = card.Type.ToString(), size = card.Size.ToString(), section =
            card.Type.ToString().Equals("Skill", StringComparison.OrdinalIgnoreCase)
                ? "Skills" : "Hand",
        socket = card.LeftSocketId?.ToString() ?? string.Empty,
        name = card.Name ?? string.Empty, tier = card.Tier.ToString(),
        enchant = (card as ItemCard)?.Enchantment?.ToString() ?? string.Empty,
        tags = card.Tags.Select(tag => tag.ToString()).OrderBy(value => value,
            StringComparer.Ordinal).ToList(),
        attributes = ConvertAttributes(card.Attributes),
    };

    private static LineupCardDto ConvertSnapshotLineupCard(PvpBattleCardSnapshot card) => new()
    {
        instance_id = card.InstanceId ?? string.Empty,
        template_id = card.TemplateId.ToString(), type = card.Type.ToString(),
        size = card.Size.ToString(), section = card.Section?.ToString() ?? string.Empty,
        socket = card.Socket?.ToString() ?? string.Empty, name = card.Name ?? string.Empty,
        tier = card.Tier?.ToString() ?? string.Empty, enchant = card.Enchant ?? string.Empty,
        tags = card.Tags?.OrderBy(value => value, StringComparer.Ordinal).ToList() ?? new(),
        attributes = card.Attributes is null ? new() :
            new Dictionary<string, int>(card.Attributes, StringComparer.Ordinal),
    };

    private string EncodeLineup(LineupEnvelopeDto envelope)
    {
        ValidateEnvelope(envelope);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope);
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output,
            System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(json, 0, json.Length);
        return LineupCodePrefix + Convert.ToBase64String(output.ToArray()).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');
    }

    public static string ProbeLineupCode(string code)
    {
        LineupEnvelopeDto envelope = DecodeLineup(code);
        return JsonSerializer.Serialize(new
        {
            envelope.payload.hero,
            Board = envelope.payload.board.Count,
            Skills = envelope.payload.skills.Count,
            envelope.checksum,
            envelope.source,
        });
    }

    private static LineupEnvelopeDto DecodeLineup(string code)
    {
        code = code.Trim().Replace("\\_", "_").Replace("\\-", "-");
        if (code.Length > MaxLineupCodeLength) throw new InvalidDataException("code is too long");
        string prefix = code.StartsWith(LineupCodePrefix, StringComparison.Ordinal)
            ? LineupCodePrefix
            : code.StartsWith(LegacyLineupCodePrefix, StringComparison.Ordinal)
                ? LegacyLineupCodePrefix
                : throw new InvalidDataException("expected BL1 or legacy LIL1 prefix");
        string value = code.Substring(prefix.Length).Replace('-', '+').Replace('_', '/');
        value += new string('=', (4 - value.Length % 4) % 4);
        byte[] compressed;
        try { compressed = Convert.FromBase64String(value); }
        catch (FormatException) { throw new InvalidDataException("invalid base64 payload"); }
        using var input = new MemoryStream(compressed);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaxLineupJsonLength)
                throw new InvalidDataException("expanded lineup is too large");
            output.Write(buffer, 0, read);
        }
        LineupEnvelopeDto envelope = JsonSerializer.Deserialize<LineupEnvelopeDto>(
            output.ToArray(), LineupJsonOptions()) ?? throw new InvalidDataException("empty payload");
        ValidateEnvelope(envelope);
        string normalizedHero = NormalizeHeroId(envelope.payload.hero);
        if (!string.Equals(normalizedHero, envelope.payload.hero, StringComparison.Ordinal))
        {
            envelope.payload.hero = normalizedHero;
            envelope.checksum = PayloadChecksum(envelope.payload);
        }
        return envelope;
    }

    private static string NormalizeHeroId(string hero) =>
        string.Equals(hero, "TheDragons", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(hero, "The Dragons", StringComparison.OrdinalIgnoreCase)
            ? "Hero8" : hero;

    private static void ValidateEnvelope(LineupEnvelopeDto envelope)
    {
        if (envelope.schema is not ("bazaarlab-lineup-v1" or "lookingin-lineup-v1") ||
            envelope.codec_version != 1)
            throw new InvalidDataException("unsupported schema or codec version");
        if (envelope.payload is null || string.IsNullOrWhiteSpace(envelope.payload.hero))
            throw new InvalidDataException("hero is missing");
        if (envelope.payload.board.Count > 10 || envelope.payload.skills.Count > 100)
            throw new InvalidDataException("card count exceeds limits");
        foreach (LineupCardDto card in envelope.payload.board.Concat(envelope.payload.skills))
        {
            if (!Guid.TryParse(card.template_id, out _))
                throw new InvalidDataException("invalid template id");
            if (card.attributes.Count > 512 || card.tags.Count > 256)
                throw new InvalidDataException("card metadata exceeds limits");
        }
        bool[] occupied = new bool[10];
        foreach (LineupCardDto card in envelope.payload.board)
        {
            int socket = SocketIndex(card.socket);
            int span = CardSpan(card.size);
            if (socket < 0 || socket + span > occupied.Length)
                throw new InvalidDataException("board card has an invalid socket/span");
            for (int index = socket; index < socket + span; index++)
            {
                if (occupied[index]) throw new InvalidDataException("board cards overlap");
                occupied[index] = true;
            }
        }
        if (!string.Equals(envelope.checksum, PayloadChecksum(envelope.payload),
                StringComparison.Ordinal))
            throw new InvalidDataException("checksum mismatch");
    }

    private static string PayloadChecksum(LineupPayloadDto payload) =>
        StableDecisionHash(JsonSerializer.Serialize(payload));

    private static int SocketIndex(string? socket)
    {
        if (string.IsNullOrEmpty(socket)) return -1;
        int separator = socket!.LastIndexOf('_');
        string value = separator >= 0 ? socket.Substring(separator + 1) : socket;
        return int.TryParse(value, out int index) ? index : -1;
    }

    private static int CardSpan(string? size) => size?.ToLowerInvariant() switch
    {
        "large" => 3,
        "medium" => 2,
        _ => 1,
    };

    private static JsonSerializerOptions LineupJsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class LineupEnvelopeDto
    {
        public string schema { get; set; } = "bazaarlab-lineup-v1";
        public int codec_version { get; set; } = 1;
        public string plugin_version { get; set; } = string.Empty;
        public string game_runtime_version { get; set; } = string.Empty;
        public string catalog_fingerprint { get; set; } = string.Empty;
        public string captured_at_utc { get; set; } = string.Empty;
        public string source { get; set; } = string.Empty;
        public LineupPayloadDto payload { get; set; } = new();
        public string checksum { get; set; } = string.Empty;
    }

    private sealed class LineupPayloadDto
    {
        public string hero { get; set; } = string.Empty;
        public Dictionary<string, int> attributes { get; set; } = new(StringComparer.Ordinal);
        public List<LineupCardDto> board { get; set; } = new();
        public List<LineupCardDto> skills { get; set; } = new();
    }

    private sealed class LineupCardDto
    {
        public string instance_id { get; set; } = string.Empty;
        public string template_id { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty;
        public string size { get; set; } = string.Empty;
        public string section { get; set; } = string.Empty;
        public string socket { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public string tier { get; set; } = string.Empty;
        public string enchant { get; set; } = string.Empty;
        public List<string> tags { get; set; } = new();
        public Dictionary<string, int> attributes { get; set; } = new(StringComparer.Ordinal);
    }
}
