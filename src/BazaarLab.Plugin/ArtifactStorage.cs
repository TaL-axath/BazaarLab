using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TheBazaar;

namespace BazaarLab.Plugin;

public sealed partial class Plugin
{
    private string _stateDirectory = string.Empty;
    private string _temporaryArtifactDirectory = string.Empty;
    private string _diagnosticDirectory = string.Empty;
    private string _combatOpeningDirectory = string.Empty;
    private string _combatResultDirectory = string.Empty;
    private object? _observedArtifactRun;
    private DateTime? _runMissingSinceUtc;
    private bool _runArtifactsCleaned;

    private void InitializeArtifactStorage()
    {
        _stateDirectory = Path.Combine(_outputDirectory, "state");
        _temporaryArtifactDirectory = Path.Combine(_outputDirectory, "temp");
        _diagnosticDirectory = Path.Combine(_outputDirectory, "diagnostics");
        _combatOpeningDirectory = Path.Combine(_outputDirectory, "combat-records", "openings");
        _combatResultDirectory = Path.Combine(_outputDirectory, "combat-records", "results");
        Directory.CreateDirectory(_stateDirectory);
        Directory.CreateDirectory(_temporaryArtifactDirectory);
        Directory.CreateDirectory(_diagnosticDirectory);
        Directory.CreateDirectory(_combatOpeningDirectory);
        Directory.CreateDirectory(_combatResultDirectory);
        MigrateLooseArtifacts();
        PreserveInterruptedArtifacts();
        _observedArtifactRun = Data.Run;
        _runArtifactsCleaned = _observedArtifactRun is null;
        if (_runArtifactsCleaned)
            DeleteArtifacts(StateFile("live-inventory.json"));
    }

    private string StateFile(string fileName) => Path.Combine(_stateDirectory, fileName);

    private string TemporaryArtifactFile(string kind, string fileName)
    {
        string directory = Path.Combine(_temporaryArtifactDirectory, kind);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, fileName);
    }

    private void DeleteArtifacts(params string?[] paths)
    {
        foreach (string path in paths.Where(path => !string.IsNullOrWhiteSpace(path))!)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception exception)
            {
                Logger.LogWarning("could not remove temporary artifact " + path + ": " +
                    exception.Message);
            }
        }
    }

    private void PreserveArtifacts(string kind, string reason, string? log,
        params string?[] paths)
    {
        try
        {
            string caseId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" +
                Guid.NewGuid().ToString("N")[..6];
            string directory = Path.Combine(_diagnosticDirectory, kind, caseId);
            Directory.CreateDirectory(directory);
            var preserved = new List<string>();
            var errors = new List<string>();
            foreach (string path in paths.Where(path => !string.IsNullOrWhiteSpace(path))!)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    string destination = Path.Combine(directory, Path.GetFileName(path));
                    File.Move(path, destination);
                    preserved.Add(Path.GetFileName(destination));
                }
                catch (Exception exception)
                {
                    errors.Add(Path.GetFileName(path) + ": " + exception.Message);
                }
            }
            File.WriteAllText(Path.Combine(directory, "diagnostic.json"),
                JsonSerializer.Serialize(new
                {
                    schema = "bazaarlab-diagnostic-artifact-v1",
                    kind,
                    recorded_at_utc = DateTime.UtcNow.ToString("O"),
                    reason,
                    log = string.IsNullOrWhiteSpace(log) ? null : log,
                    files = preserved,
                    preservation_errors = errors,
                }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            Logger.LogWarning("preserved " + kind + " diagnostic artifacts: " + directory);
        }
        catch (Exception exception)
        {
            Logger.LogWarning("could not preserve " + kind + " diagnostic artifacts: " +
                exception.Message);
        }
    }

    private void PreserveArtifactText(string kind, string reason,
        IReadOnlyDictionary<string, string> files, object? details = null)
    {
        try
        {
            string caseId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" +
                Guid.NewGuid().ToString("N")[..6];
            string directory = Path.Combine(_diagnosticDirectory, kind, caseId);
            Directory.CreateDirectory(directory);
            foreach (KeyValuePair<string, string> file in files)
                File.WriteAllText(Path.Combine(directory, file.Key), file.Value);
            File.WriteAllText(Path.Combine(directory, "diagnostic.json"),
                JsonSerializer.Serialize(new
                {
                    schema = "bazaarlab-diagnostic-artifact-v1",
                    kind,
                    recorded_at_utc = DateTime.UtcNow.ToString("O"),
                    reason,
                    details,
                    files = files.Keys.ToArray(),
                }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            Logger.LogWarning("preserved " + kind + " diagnostic artifacts: " + directory);
        }
        catch (Exception exception)
        {
            Logger.LogWarning("could not preserve " + kind + " diagnostic text: " +
                exception.Message);
        }
    }

    private void MigrateLooseArtifacts()
    {
        try
        {
            foreach (string path in Directory.GetFiles(_outputDirectory, "*",
                         SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path);
                string? destination = ClassifyLooseArtifact(name);
                if (destination is null) continue;
                Directory.CreateDirectory(destination);
                string target = Path.Combine(destination, name);
                if (File.Exists(target))
                {
                    target = Path.Combine(destination,
                        Path.GetFileNameWithoutExtension(name) + "-migrated-" +
                        Guid.NewGuid().ToString("N")[..6] + Path.GetExtension(name));
                }
                File.Move(path, target);
            }
        }
        catch (Exception exception)
        {
            Logger.LogWarning("could not fully organize legacy BazaarLab artifacts: " +
                exception.Message);
        }
    }

    private void PreserveInterruptedArtifacts()
    {
        foreach (string kind in new[] { "baseline", "monster", "encounter-preview", "placement" })
        {
            string directory = Path.Combine(_temporaryArtifactDirectory, kind);
            if (!Directory.Exists(directory)) continue;
            string[] files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
            if (files.Length > 0)
                PreserveArtifacts(kind, "interrupted calculation recovered at plugin startup",
                    null, files);
        }
    }

    private void UpdateRunArtifactLifecycle()
    {
        object? current = Data.Run;
        if (current is not null)
        {
            _runMissingSinceUtc = null;
            if (_observedArtifactRun is not null &&
                !ReferenceEquals(_observedArtifactRun, current) && !_runArtifactsCleaned)
                CleanupCompletedRunArtifacts("new run replaced the previous run");
            _observedArtifactRun = current;
            _runArtifactsCleaned = false;
            return;
        }
        if (_observedArtifactRun is null || _runArtifactsCleaned) return;
        _runMissingSinceUtc ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _runMissingSinceUtc.Value < TimeSpan.FromSeconds(3)) return;
        CleanupCompletedRunArtifacts("run ended");
        _observedArtifactRun = null;
        _runMissingSinceUtc = null;
        _runArtifactsCleaned = true;
    }

    private void CleanupCompletedRunArtifacts(string reason)
    {
        if (_baselineProcess is not null) CancelBaselineCalculationForCombat();
        else FinishBaselineArtifacts(false, reason, null);
        if (_monsterProcess is not null) CancelMonsterCalculationForCombat();
        else FinishMonsterArtifacts(false, reason, null);
        if (_encounterPreviewProcess is not null || _encounterPreviewQueue.Count > 0)
            CancelEncounterPreviewsForCombat();
        else
            FinishEncounterPreviewArtifacts(false, reason, null);
        if (_placementProcess is not null)
            CancelPlacementSearch("本局已结束，摆位规划已中断", preserveArtifacts: false);
        else
            FinishPlacementArtifacts(false, reason, null);
        StopMovePlan();
        DeleteArtifacts(StateFile("live-inventory.json"));
        _lastLiveInventoryPayload = null;
        _baselineCandidateFingerprint = null;
        _baselineRunningFingerprint = null;
        _baselineResult = null;
        _monsterCandidatePayload = null;
        _monsterCompletedPayload = null;
        _monsterResult = null;
        _pendingMonsterPrediction = null;
        _monsterPredictionsByCapture.Clear();
        _encounterPredictionsByEncounter.Clear();
        Logger.LogInfo("cleared completed-run temporary artifacts: " + reason);
    }

    private string? ClassifyLooseArtifact(string name)
    {
        if (name is "latest.json" or "status.json" or "live-inventory.json" or
            "last-stable-lineup.json" or "last-stable-lineup.code.txt" or
            "combat-opening-player.json" or "combat-opening-opponent.json" or
            "monster-calibrations.json")
            return _stateDirectory;
        if (name.EndsWith(".actual.json", StringComparison.OrdinalIgnoreCase))
            return _combatResultDirectory;
        if (name.StartsWith("baseline-", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(_diagnosticDirectory, "baseline", "legacy");
        if (name.StartsWith("monster-", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(name, "monster-calibrations.json", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(_diagnosticDirectory, "monster", "legacy");
        if (name.StartsWith("encounter-preview-", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(_diagnosticDirectory, "encounter-preview", "legacy");
        if (name.StartsWith("placement-", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(_diagnosticDirectory, "placement", "legacy");
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            name.Length > 9 && char.IsDigit(name[0]) && char.IsDigit(name[7]))
            return _combatOpeningDirectory;
        return null;
    }
}
