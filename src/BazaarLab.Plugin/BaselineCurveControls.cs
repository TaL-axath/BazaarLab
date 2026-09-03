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
    private Process? _baselineProcess;
    private StringBuilder? _baselineProcessLog;
    private string? _baselineInputPath;
    private string? _baselineResultPath;
    private string? _baselineCandidateFingerprint;
    private string? _baselineRunningFingerprint;
    private float _baselineChangedAt;
    private float _nextBaselineProbeAt;
    private BaselineReportDto? _baselineResult;
    private Texture2D? _curveTexture;
    private string _baselineStatus = "等待布阵区稳定……";
    private Rect _baselineWindowRect;
    private bool _baselineWindowInitialized;
    private bool _baselineWindowMinimized;
    private bool _baselineUpdatesSuppressed;

    private const int BaselineWindowId = 191105;
    private const float BaselineWindowWidth = 570f;
    private const float BaselineWindowHeight = 270f;

    private bool IsBaselineCalculating => _baselineProcess is not null;

    private void InitializeBaselineCurveControls()
    {
        _curveTexture = new Texture2D(1, 1);
        _curveTexture.SetPixel(0, 0, Color.white);
        _curveTexture.Apply();
        _baselineChangedAt = Time.realtimeSinceStartup;
    }

    private void DisposeBaselineCurveControls()
    {
        if (_baselineProcess is not null)
        {
            try
            {
                if (!_baselineProcess.HasExited) _baselineProcess.Kill();
            }
            catch (Exception)
            {
                // Process may have exited between checks.
            }
            _baselineProcess.Dispose();
            _baselineProcess = null;
        }
        _baselineProcessLog = null;
        FinishBaselineArtifacts(false, "plugin shutdown", null);
        if (_curveTexture is not null)
        {
            Destroy(_curveTexture);
            _curveTexture = null;
        }
    }

    private void UpdateBaselineCurveControls()
    {
        if (ShouldSuppressBaselineUpdates())
        {
            if (!_baselineUpdatesSuppressed)
            {
                _baselineUpdatesSuppressed = true;
                CancelBaselineCalculationForCombat();
                _baselineCandidateFingerprint = null;
                _baselineStatus = "战斗或回放中，已暂停自动更新";
            }
            return;
        }
        if (_baselineUpdatesSuppressed)
        {
            _baselineUpdatesSuppressed = false;
            _baselineCandidateFingerprint = null;
            _baselineRunningFingerprint = null;
            _baselineChangedAt = Time.realtimeSinceStartup;
            _baselineStatus = "战斗已结束，等待布阵区稳定……";
        }
        PollBaselineCalculation();
        if (Time.realtimeSinceStartup < _nextBaselineProbeAt)
        {
            return;
        }
        _nextBaselineProbeAt = Time.realtimeSinceStartup + 0.25f;
        string? fingerprint = ComputePlayerBoardFingerprint();
        if (!string.Equals(fingerprint, _baselineCandidateFingerprint, StringComparison.Ordinal))
        {
            _baselineCandidateFingerprint = fingerprint;
            _baselineChangedAt = Time.realtimeSinceStartup;
            if (_baselineResult is not null) _baselineStatus = "布阵区已变化，正在重新计算……";
        }
        if (fingerprint is null || IsBaselineCalculating || Data.IsInCombat ||
            Time.realtimeSinceStartup - _baselineChangedAt < 0.9f ||
            CardController.IsAnyCardDragging || AppState.IsWaitingForServerResponse ||
            IsMoving || IsSearching || IsMonsterCalculating || IsEncounterPreviewCalculating ||
            IsLocalDuelCalculating ||
            string.Equals(fingerprint, _baselineRunningFingerprint, StringComparison.Ordinal))
        {
            return;
        }
        StartBaselineCalculation(fingerprint);
    }

    private static bool ShouldSuppressBaselineUpdates()
    {
        return IsCombatOrReplayActive() || AppState.BlockInput;
    }

    private void CancelBaselineCalculationForCombat()
    {
        Process? process = _baselineProcess;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (Exception exception)
        {
            Logger.LogWarning("cannot stop baseline calculation on combat entry: " +
                exception.Message);
        }
        process.Dispose();
        _baselineProcess = null;
        _baselineProcessLog = null;
        _baselineRunningFingerprint = null;
        FinishBaselineArtifacts(false, "combat started", null);
    }

    private string? ComputePlayerBoardFingerprint()
    {
        if (Data.Run?.Player is null)
        {
            return null;
        }
        var player = Data.Run.Player;
        return JsonSerializer.Serialize(new
        {
            attributes = ConvertPlanningFingerprintAttributes(player.Attributes),
            hand = player.Hand.GetItemsAsEnumerable().OfType<Card>()
                .OrderBy(card => card.LeftSocketId.HasValue ? (int)card.LeftSocketId.Value : 99)
                .ThenBy(card => card.InstanceId.Value, StringComparer.Ordinal)
                .Select(card => new
                {
                    id = card.InstanceId.Value,
                    template = card.TemplateId,
                    position = card.LeftSocketId?.ToString(),
                    size = card.Size.ToString(),
                    tier = card.Tier.ToString(),
                    enchant = (card as ItemCard)?.Enchantment?.ToString(),
                    attributes = ConvertPlanningFingerprintAttributes(card.Attributes),
                }).ToArray(),
            skills = player.Skills.OrderBy(card => card.InstanceId.Value, StringComparer.Ordinal)
                .Select(card => new
                {
                    id = card.InstanceId.Value,
                    template = card.TemplateId,
                    tier = card.Tier.ToString(),
                    attributes = ConvertPlanningFingerprintAttributes(card.Attributes),
                }).ToArray(),
        });
    }

    private static Dictionary<string, int> ConvertPlanningFingerprintAttributes(object attributes)
    {
        Dictionary<string, int> result = ConvertAttributeObject(attributes);
        foreach (string key in result.Keys.Where(IsTransientCombatAttribute).ToArray())
            result.Remove(key);
        return result;
    }

    private static bool IsTransientCombatAttribute(string key) => key is
        "Cooldown" or "Haste" or "Freeze" or "Slow" or "Flying" or "Ammo" or
        "Charge" or "Burn" or "Poison" or "Shield" or "Regen" or "Health" ||
        key.EndsWith("Remaining", StringComparison.Ordinal) ||
        key.EndsWith("Duration", StringComparison.Ordinal);

    private void StartBaselineCalculation(string fingerprint)
    {
        if (!CanUseCatalog(out string catalogReason))
        {
            _baselineStatus = catalogReason;
            return;
        }
        _baselineRunningFingerprint = fingerprint;
        try
        {
            CaptureLiveInventory(DateTime.UtcNow);
            string gameRoot = Paths.GameRootPath;
            string runtime = GetRuntimeFile("BazaarLab.BaselineMetrics.dll");
            string catalog = GetCatalogFile();
            string liveInput = StateFile("live-inventory.json");
            if (!File.Exists(runtime) || !File.Exists(catalog) || !File.Exists(liveInput))
            {
                _baselineStatus = "缺少曲线运行库、卡牌目录或阵容快照";
                return;
            }
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            _baselineInputPath = TemporaryArtifactFile("baseline",
                "baseline-input-" + stamp + ".json");
            File.Copy(liveInput, _baselineInputPath, overwrite: false);
            _baselineResultPath = TemporaryArtifactFile("baseline",
                "baseline-result-" + stamp + ".json");
            string dotnetExecutable = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (!File.Exists(dotnetExecutable)) dotnetExecutable = "dotnet";
            var output = new StringBuilder();
            var start = new ProcessStartInfo
            {
                FileName = dotnetExecutable,
                Arguments = Quote(runtime) + " " + Quote(catalog) + " " +
                    Quote(_baselineInputPath) + " " +
                    Quote(_baselineResultPath) + " 20260831 7 600 20",
                WorkingDirectory = Path.GetDirectoryName(runtime) ?? gameRoot,
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
                _baselineStatus = "无法启动曲线计算进程";
                FinishBaselineArtifacts(true, _baselineStatus, null);
                return;
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _baselineProcess = process;
            _baselineProcessLog = output;
            _baselineRunningFingerprint = fingerprint;
            _baselineStatus = "正在计算 30 秒白板曲线……";
        }
        catch (Exception exception)
        {
            _baselineStatus = "曲线计算失败：" + exception.Message;
            _baselineRunningFingerprint = fingerprint;
            FinishBaselineArtifacts(true, _baselineStatus, exception.ToString());
        }
    }

    private void PollBaselineCalculation()
    {
        Process? process = _baselineProcess;
        if (process is null || !process.HasExited)
        {
            return;
        }
        try
        {
            process.WaitForExit();
            int exitCode = process.ExitCode;
            string log = string.Empty;
            if (_baselineProcessLog is not null)
            {
                lock (_baselineProcessLog) log = _baselineProcessLog.ToString();
            }
            process.Dispose();
            _baselineProcess = null;
            _baselineProcessLog = null;
            if (exitCode != 0 || string.IsNullOrEmpty(_baselineResultPath) ||
                !File.Exists(_baselineResultPath))
            {
                _baselineStatus = "曲线进程失败：" + LastLine(log);
                FinishBaselineArtifacts(true, _baselineStatus, log);
                return;
            }
            _baselineResult = JsonSerializer.Deserialize<BaselineReportDto>(
                File.ReadAllText(_baselineResultPath), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            if (_baselineResult is null)
            {
                _baselineStatus = "曲线结果为空";
                FinishBaselineArtifacts(true, _baselineStatus, log);
                return;
            }
            bool stale = !string.Equals(_baselineCandidateFingerprint,
                _baselineRunningFingerprint, StringComparison.Ordinal);
            _baselineStatus = stale ? "布阵区已变化，正在重新计算……" :
                $"已更新：{_baselineResult.Samples} 个样本 / {_baselineResult.DurationSeconds:0} 秒";
            FinishBaselineArtifacts(false, "success", null);
        }
        catch (Exception exception)
        {
            process.Dispose();
            _baselineProcess = null;
            _baselineProcessLog = null;
            _baselineStatus = "无法读取曲线结果：" + exception.Message;
            FinishBaselineArtifacts(true, _baselineStatus, exception.ToString());
        }
    }

    private void FinishBaselineArtifacts(bool preserve, string reason, string? log)
    {
        if (preserve)
            PreserveArtifacts("baseline", reason, log, _baselineInputPath, _baselineResultPath);
        else
            DeleteArtifacts(_baselineInputPath, _baselineResultPath);
        _baselineInputPath = null;
        _baselineResultPath = null;
    }

    private void DrawBaselineCurveControls()
    {
        if (Data.Run?.Player is null || _baselineResult?.Points is not { Count: > 1 } points)
        {
            SetFloatingWindowBlocker(1, default, false);
            return;
        }
        float height = _baselineWindowMinimized ? 30f : BaselineWindowHeight;
        if (!_baselineWindowInitialized)
        {
            _baselineWindowRect = new Rect(18f, Screen.height - BaselineWindowHeight - 18f,
                BaselineWindowWidth, height);
            _baselineWindowInitialized = true;
        }
        _baselineWindowRect.width = BaselineWindowWidth;
        _baselineWindowRect.height = height;
        _baselineWindowRect = GUI.Window(BaselineWindowId, _baselineWindowRect,
            DrawBaselineWindow, "30 秒无限血白板——累计输出");
        _baselineWindowRect.x = Mathf.Clamp(_baselineWindowRect.x,
            -BaselineWindowWidth + 46f, Screen.width - 46f);
        _baselineWindowRect.y = Mathf.Clamp(_baselineWindowRect.y, 0f,
            Mathf.Max(0f, Screen.height - 30f));
        SetFloatingWindowBlocker(1, _baselineWindowRect, true);
    }

    private void DrawBaselineWindow(int windowId)
    {
        if (GUI.Button(new Rect(BaselineWindowWidth - 28f, 3f, 24f, 21f),
                _baselineWindowMinimized ? "+" : "—"))
        {
            _baselineWindowMinimized = !_baselineWindowMinimized;
        }
        GUI.DragWindow(new Rect(0f, 0f, BaselineWindowWidth - 32f, 25f));
        if (_baselineWindowMinimized || _baselineResult?.Points is not { Count: > 1 } points)
            return;

        GUILayout.BeginArea(new Rect(10f, 23f, BaselineWindowWidth - 20f, 38f));
        GUILayout.Label($"伤害 {_baselineResult.TotalDamage:0}   " +
            $"护盾 {_baselineResult.TotalShield:0}   治疗 {_baselineResult.TotalHealing:0}   " +
            _baselineStatus);
        GUILayout.EndArea();

        var graph = new Rect(48f, 63f, BaselineWindowWidth - 68f,
            BaselineWindowHeight - 91f);
        GUI.Box(graph, GUIContent.none);
        double maximum = Math.Max(1d, points.Max(point => Math.Max(point.Damage,
            Math.Max(point.Shield, point.Healing))));
        DrawGrid(graph, maximum);
        DrawCurve(graph, points.Select(point => point.Damage).ToArray(), maximum,
            new Color(1f, 0.3f, 0.25f));
        DrawCurve(graph, points.Select(point => point.Shield).ToArray(), maximum,
            new Color(0.25f, 0.75f, 1f));
        DrawCurve(graph, points.Select(point => point.Healing).ToArray(), maximum,
            new Color(0.3f, 1f, 0.45f));
        DrawCurveHover(graph, points);
        GUI.Label(new Rect(graph.x, graph.yMax + 2f, 250f, 22f),
            "红：伤害   蓝：护盾   绿：治疗");
        GUI.Label(new Rect(graph.xMax - 70f, graph.yMax + 2f, 70f, 22f), "30 秒");
    }

    private void DrawCurveHover(Rect graph, IReadOnlyList<BaselinePointDto> points)
    {
        Vector2 mouse = Event.current.mousePosition;
        if (!graph.Contains(mouse) || points.Count < 2 || _curveTexture is null) return;
        double duration = Math.Max(0.001d, points[^1].TimeSeconds);
        double time = Mathf.Clamp01((mouse.x - graph.x) / graph.width) * duration;
        int upper = 1;
        while (upper < points.Count && points[upper].TimeSeconds < time) upper++;
        upper = Math.Min(upper, points.Count - 1);
        int lower = Math.Max(0, upper - 1);
        BaselinePointDto a = points[lower];
        BaselinePointDto b = points[upper];
        double span = Math.Max(0.000001d, b.TimeSeconds - a.TimeSeconds);
        double ratio = Math.Clamp((time - a.TimeSeconds) / span, 0d, 1d);
        double damage = a.Damage + (b.Damage - a.Damage) * ratio;
        double shield = a.Shield + (b.Shield - a.Shield) * ratio;
        double healing = a.Healing + (b.Healing - a.Healing) * ratio;

        DrawLine(new Vector2(mouse.x, graph.y), new Vector2(mouse.x, graph.yMax),
            new Color(1f, 1f, 1f, 0.7f), 1f);
        const float width = 172f;
        const float height = 72f;
        float x = Mathf.Clamp(mouse.x + 10f, graph.x + 2f, graph.xMax - width - 2f);
        float y = Mathf.Clamp(mouse.y - height - 8f, graph.y + 2f, graph.yMax - height - 2f);
        var tooltip = new Rect(x, y, width, height);
        GUI.Box(tooltip, GUIContent.none);
        GUI.Label(new Rect(x + 7f, y + 4f, width - 14f, height - 8f),
            $"时间：{time:0.0} 秒\n伤害：{damage:0}\n护盾：{shield:0}    治疗：{healing:0}");
    }

    private void DrawGrid(Rect graph, double maximum)
    {
        if (_curveTexture is null) return;
        Color previous = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.16f);
        for (int index = 0; index <= 4; index++)
        {
            float y = graph.y + graph.height * index / 4f;
            GUI.DrawTexture(new Rect(graph.x, y, graph.width, 1f), _curveTexture);
            double label = maximum * (4 - index) / 4d;
            GUI.color = Color.white;
            GUI.Label(new Rect(graph.x - 44f, y - 10f, 42f, 20f),
                FormatCurveAxisValue(label));
            GUI.color = new Color(1f, 1f, 1f, 0.16f);
        }
        GUI.color = previous;
    }

    private static string FormatCurveAxisValue(double value)
    {
        double absolute = Math.Abs(value);
        if (absolute >= 1_000_000_000d) return (value / 1_000_000_000d).ToString("0.#") + "b";
        if (absolute >= 1_000_000d) return (value / 1_000_000d).ToString("0.#") + "m";
        if (absolute >= 10_000d) return (value / 1_000d).ToString("0.#") + "k";
        return value.ToString("0");
    }

    private void DrawCurve(Rect graph, IReadOnlyList<double> values, double maximum, Color color)
    {
        if (_curveTexture is null || values.Count < 2) return;
        for (int index = 1; index < values.Count; index++)
        {
            float x0 = graph.x + graph.width * (index - 1) / (values.Count - 1f);
            float x1 = graph.x + graph.width * index / (values.Count - 1f);
            float y0 = graph.yMax - graph.height * (float)(values[index - 1] / maximum);
            float y1 = graph.yMax - graph.height * (float)(values[index] / maximum);
            DrawLine(new Vector2(x0, y0), new Vector2(x1, y1), color, 2f);
        }
    }

    private void DrawLine(Vector2 start, Vector2 end, Color color, float thickness)
    {
        if (_curveTexture is null) return;
        Matrix4x4 previousMatrix = GUI.matrix;
        Color previousColor = GUI.color;
        Vector2 delta = end - start;
        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        GUI.color = color;
        GUIUtility.RotateAroundPivot(angle, start);
        GUI.DrawTexture(new Rect(start.x, start.y - thickness / 2f,
            delta.magnitude, thickness), _curveTexture);
        GUI.matrix = previousMatrix;
        GUI.color = previousColor;
    }

    private sealed class BaselineReportDto
    {
        public int Samples { get; set; }
        public double DurationSeconds { get; set; }
        public List<BaselinePointDto>? Points { get; set; }
        public double TotalDamage { get; set; }
        public double TotalShield { get; set; }
        public double TotalHealing { get; set; }
        public Dictionary<string, int>? UnsupportedActions { get; set; }
    }

    private sealed class BaselinePointDto
    {
        public double TimeSeconds { get; set; }
        public double Damage { get; set; }
        public double Shield { get; set; }
        public double Healing { get; set; }
    }
}
