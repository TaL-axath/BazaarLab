using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BepInEx;
using SQLite;
using TheBazaar;
using UnityEngine;

namespace BazaarLab.Plugin;

public sealed partial class Plugin
{
    private const string CatalogManifestSchema = "bazaarlab-active-catalog-v1";
    private static string? _activeCatalogPath;
    private static string _activeCatalogFingerprint = "missing";
    private string _catalogRoot = string.Empty;
    private string _catalogManifestPath = string.Empty;
    private CatalogManifest? _catalogManifest;
    private Task<CatalogBuildResult>? _catalogBuildTask;
    private DateTime _nextCatalogProbeUtc;
    private CatalogState _catalogState = CatalogState.Checking;
    private string _catalogStatus = "正在检查官方卡表";
    private bool _catalogSourceVerifiedThisSession;

    private enum CatalogState
    {
        Checking,
        Refreshing,
        Ready,
        Failed,
    }

    private sealed class CatalogManifest
    {
        public string Schema { get; set; } = CatalogManifestSchema;
        public string SourceDatabase { get; set; } = string.Empty;
        public long SourceLength { get; set; }
        public long SourceWriteTicksUtc { get; set; }
        public string SourceSha256 { get; set; } = string.Empty;
        public string RulesetSha256 { get; set; } = string.Empty;
        public string CatalogRelativePath { get; set; } = string.Empty;
        public int CardCount { get; set; }
        public string GeneratedAtUtc { get; set; } = string.Empty;
        public string PluginVersion { get; set; } = string.Empty;
    }

    private sealed class CatalogSource
    {
        public CatalogSource(string path, long length, long writeTicksUtc)
        {
            Path = path;
            Length = length;
            WriteTicksUtc = writeTicksUtc;
        }

        public string Path { get; }
        public long Length { get; }
        public long WriteTicksUtc { get; }
    }

    private sealed class CatalogBuildResult
    {
        public CatalogBuildResult(CatalogManifest manifest, string catalogPath, bool generated)
        {
            Manifest = manifest;
            CatalogPath = catalogPath;
            Generated = generated;
        }

        public CatalogManifest Manifest { get; }
        public string CatalogPath { get; }
        public bool Generated { get; }
    }

    private sealed class CatalogCardRow
    {
        public string Id { get; set; } = string.Empty;
        public byte[] Data { get; set; } = Array.Empty<byte>();
    }

    private void InitializeCatalogManager()
    {
        _catalogRoot = Path.Combine(_outputDirectory, "catalogs");
        _catalogManifestPath = Path.Combine(_catalogRoot, "active-catalog.json");
        Directory.CreateDirectory(_catalogRoot);

        if (TryLoadCatalogManifest(out CatalogManifest? manifest, out string? catalogPath))
        {
            _catalogManifest = manifest;
            ActivateCatalog(catalogPath!, manifest!.RulesetSha256, notifyConsumers: false);
            _catalogStatus = "正在核对官方游戏数据";
        }
        else
        {
            string fallback = PackagedCatalogPath();
            if (File.Exists(fallback))
            {
                // The packaged file is only a bootstrap source. Its full hash is
                // deliberately deferred to the background database verification.
                ActivateCatalog(fallback, "bootstrap", notifyConsumers: false);
                _catalogStatus = "正在从官方游戏数据生成版本化卡表";
            }
            else
            {
                _catalogStatus = "缺少随包卡表，等待从官方游戏数据生成";
            }
        }
        _catalogState = CatalogState.Checking;
        _nextCatalogProbeUtc = DateTime.MinValue;
    }

    private void DisposeCatalogManager()
    {
        _catalogBuildTask = null;
    }

    private void UpdateCatalogManager()
    {
        if (_catalogBuildTask is not null)
        {
            if (!_catalogBuildTask.IsCompleted) return;
            Task<CatalogBuildResult> completed = _catalogBuildTask;
            _catalogBuildTask = null;
            try
            {
                CatalogBuildResult result = completed.GetAwaiter().GetResult();
                _catalogManifest = result.Manifest;
                SaveCatalogManifest(result.Manifest);
                ActivateCatalog(result.CatalogPath, result.Manifest.RulesetSha256,
                    notifyConsumers: true);
                _catalogSourceVerifiedThisSession = true;
                _catalogState = CatalogState.Ready;
                _catalogStatus = result.Generated
                    ? $"官方卡表已更新：{result.Manifest.CardCount} 张"
                    : $"官方卡表已核对：{result.Manifest.CardCount} 张";
                _nextCatalogProbeUtc = DateTime.UtcNow.AddSeconds(30);
                Logger.LogInfo("catalog-manager: " + _catalogStatus + ", ruleset=" +
                    result.Manifest.RulesetSha256);
            }
            catch (Exception exception)
            {
                _catalogState = CatalogState.Failed;
                _catalogStatus = "官方卡表更新失败：" + exception.Message;
                _nextCatalogProbeUtc = DateTime.UtcNow.AddSeconds(10);
                Logger.LogError("catalog-manager: " + _catalogStatus);
            }
            return;
        }

        if (DateTime.UtcNow < _nextCatalogProbeUtc) return;
        _nextCatalogProbeUtc = DateTime.UtcNow.AddSeconds(2);
        CatalogSource? source = TryResolveCatalogSource();
        if (source is null) return;

        bool metadataMatches = _catalogManifest is not null &&
            PathsEqual(_catalogManifest.SourceDatabase, source.Path) &&
            _catalogManifest.SourceLength == source.Length &&
            _catalogManifest.SourceWriteTicksUtc == source.WriteTicksUtc;
        if (_catalogState == CatalogState.Ready && metadataMatches &&
            _catalogSourceVerifiedThisSession)
        {
            _nextCatalogProbeUtc = DateTime.UtcNow.AddSeconds(30);
            return;
        }

        _catalogState = metadataMatches ? CatalogState.Checking : CatalogState.Refreshing;
        _catalogStatus = metadataMatches
            ? "正在核对官方卡表版本"
            : "检测到游戏数据变化，正在重新生成卡表";
        CatalogManifest? existing = _catalogManifest;
        _catalogBuildTask = Task.Run(() => BuildOrVerifyCatalog(source, existing));
    }

    private bool CanUseCatalog(out string reason)
    {
        if (_catalogState == CatalogState.Ready && File.Exists(GetCatalogFile()))
        {
            reason = string.Empty;
            return true;
        }
        reason = _catalogStatus;
        return false;
    }

    private static string GetCatalogFile() =>
        !string.IsNullOrEmpty(_activeCatalogPath) && File.Exists(_activeCatalogPath)
            ? _activeCatalogPath!
            : PackagedCatalogPath();

    private static string GetCatalogFingerprint() => _activeCatalogFingerprint;

    private static string PackagedCatalogPath() =>
        Path.Combine(Paths.PluginPath, "BazaarLab", "data", "official-cards.jsonl");

    private bool TryResolveCatalogForLineups(
        string fingerprintA,
        string fingerprintB,
        out string catalogPath,
        out string error)
    {
        catalogPath = string.Empty;
        error = string.Empty;
        if (!CanUseCatalog(out error)) return false;

        bool modernA = IsSha256(fingerprintA);
        bool modernB = IsSha256(fingerprintB);
        if (!modernA || !modernB)
        {
            catalogPath = GetCatalogFile();
            return true;
        }
        if (!string.Equals(fingerprintA, fingerprintB, StringComparison.OrdinalIgnoreCase))
        {
            error = "双方阵容码属于不同游戏规则版本，无法进行可复现对战";
            return false;
        }
        if (string.Equals(fingerprintA, GetCatalogFingerprint(),
                StringComparison.OrdinalIgnoreCase))
        {
            catalogPath = GetCatalogFile();
            return true;
        }
        string candidate = Path.Combine(_catalogRoot, fingerprintA.ToLowerInvariant(),
            "official-cards.jsonl");
        if (File.Exists(candidate))
        {
            catalogPath = candidate;
            return true;
        }
        error = "本地缺少该阵容码对应的历史规则集：" + fingerprintA;
        return false;
    }

    private CatalogBuildResult BuildOrVerifyCatalog(
        CatalogSource source,
        CatalogManifest? existing)
    {
        string sourceSha256 = ComputeFileSha256(source.Path);
        FileInfo afterHash = new FileInfo(source.Path);
        EnsureSourceUnchanged(source, afterHash);

        if (existing is not null &&
            string.Equals(existing.Schema, CatalogManifestSchema, StringComparison.Ordinal) &&
            PathsEqual(existing.SourceDatabase, source.Path) &&
            existing.SourceLength == source.Length &&
            existing.SourceWriteTicksUtc == source.WriteTicksUtc &&
            string.Equals(existing.SourceSha256, sourceSha256,
                StringComparison.OrdinalIgnoreCase) &&
            TryResolveManifestCatalog(existing, out string? existingPath))
        {
            string actualRuleset = ComputeFileSha256(existingPath!);
            if (string.Equals(actualRuleset, existing.RulesetSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new CatalogBuildResult(existing, existingPath!, false);
            }
        }

        string temporary = Path.Combine(_catalogRoot,
            ".building-" + Guid.NewGuid().ToString("N") + ".jsonl");
        int cardCount = 0;
        try
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var connection = new SQLiteConnection(source.Path,
                SQLiteOpenFlags.ReadOnly))
            {
                using var output = new StreamWriter(temporary, false,
                    new UTF8Encoding(false), 65536);
                foreach (CatalogCardRow row in connection.Query<CatalogCardRow>(
                    "SELECT Id, Data FROM cards ORDER BY Id", Array.Empty<object>()))
                {
                    string rowId = row.Id;
                    string payload = Encoding.UTF8.GetString(row.Data);
                    using JsonDocument document = JsonDocument.Parse(payload);
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("Id", out JsonElement idElement) ||
                        !string.Equals(idElement.GetString(), rowId,
                            StringComparison.OrdinalIgnoreCase) ||
                        !root.TryGetProperty("$type", out JsonElement typeElement) ||
                        typeElement.ValueKind != JsonValueKind.String ||
                        !ids.Add(rowId))
                    {
                        throw new InvalidDataException(
                            "GameData.db contains an invalid or duplicate card row: " + rowId);
                    }
                    output.Write(root.GetRawText());
                    output.Write('\n');
                    cardCount++;
                }
            }
            if (cardCount < 1000)
            {
                throw new InvalidDataException(
                    "official card export was unexpectedly small: " + cardCount);
            }
            FileInfo afterExport = new FileInfo(source.Path);
            EnsureSourceUnchanged(source, afterExport);

            string rulesetSha256 = ComputeFileSha256(temporary);
            string destinationDirectory = Path.Combine(_catalogRoot, rulesetSha256);
            string destination = Path.Combine(destinationDirectory, "official-cards.jsonl");
            Directory.CreateDirectory(destinationDirectory);
            if (File.Exists(destination))
            {
                if (!string.Equals(ComputeFileSha256(destination), rulesetSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "existing versioned catalog failed its content hash");
                }
                File.Delete(temporary);
            }
            else
            {
                File.Move(temporary, destination);
            }

            var manifest = new CatalogManifest
            {
                SourceDatabase = source.Path,
                SourceLength = source.Length,
                SourceWriteTicksUtc = source.WriteTicksUtc,
                SourceSha256 = sourceSha256,
                RulesetSha256 = rulesetSha256,
                CatalogRelativePath = Path.Combine(rulesetSha256, "official-cards.jsonl")
                    .Replace('\\', '/'),
                CardCount = cardCount,
                GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
                PluginVersion = PluginVersion,
            };
            return new CatalogBuildResult(manifest, destination, true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception) { }
            throw;
        }
    }

    private CatalogSource? TryResolveCatalogSource()
    {
        try
        {
            object? manager = null;
            if (Data.IsManagerCreated())
            {
                manager = Data.GetStatic();
                if (manager is Task task)
                {
                    if (!task.IsCompleted) return null;
                    manager = task.GetType().GetProperty("Result")?.GetValue(task);
                }
            }
            string? path = FindField(manager?.GetType(), "_dbPath")?.GetValue(manager) as string;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(Application.persistentDataPath, "prod", "cache",
                    "GameData.db");
            }
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            return new CatalogSource(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch (Exception exception)
        {
            Logger.LogWarning("catalog-manager: game data is not ready: " + exception.Message);
            return null;
        }
    }

    private bool TryLoadCatalogManifest(
        out CatalogManifest? manifest,
        out string? catalogPath)
    {
        manifest = null;
        catalogPath = null;
        try
        {
            if (!File.Exists(_catalogManifestPath)) return false;
            manifest = JsonSerializer.Deserialize<CatalogManifest>(
                File.ReadAllText(_catalogManifestPath), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            return manifest is not null &&
                string.Equals(manifest.Schema, CatalogManifestSchema, StringComparison.Ordinal) &&
                IsSha256(manifest.RulesetSha256) &&
                TryResolveManifestCatalog(manifest, out catalogPath);
        }
        catch (Exception exception)
        {
            Logger.LogWarning("catalog-manager: active manifest ignored: " + exception.Message);
            manifest = null;
            catalogPath = null;
            return false;
        }
    }

    private bool TryResolveManifestCatalog(CatalogManifest manifest, out string? catalogPath)
    {
        catalogPath = null;
        if (string.IsNullOrWhiteSpace(manifest.CatalogRelativePath)) return false;
        string root = Path.GetFullPath(_catalogRoot).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(_catalogRoot,
            manifest.CatalogRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(candidate)) return false;
        catalogPath = candidate;
        return true;
    }

    private void SaveCatalogManifest(CatalogManifest manifest)
    {
        Directory.CreateDirectory(_catalogRoot);
        string temporary = _catalogManifestPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest,
            new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
        if (File.Exists(_catalogManifestPath))
            File.Replace(temporary, _catalogManifestPath, null);
        else
            File.Move(temporary, _catalogManifestPath);
    }

    private void ActivateCatalog(string path, string fingerprint, bool notifyConsumers)
    {
        bool changed = !string.Equals(_activeCatalogFingerprint, fingerprint,
            StringComparison.OrdinalIgnoreCase);
        _activeCatalogPath = path;
        _activeCatalogFingerprint = fingerprint.ToLowerInvariant();
        _catalogFingerprint = _activeCatalogFingerprint;
        if (notifyConsumers && changed) InvalidateCatalogConsumers();
    }

    private void InvalidateCatalogConsumers()
    {
        _baselineCandidateFingerprint = null;
        _baselineRunningFingerprint = null;
        _baselineResult = null;
        _monsterCompletedPayload = null;
        _monsterResult = null;
        _encounterPreviewCandidateFingerprint = null;
        _encounterPreviewAppliedFingerprint = null;
        _encounterPreviews.Clear();
        _encounterPreviewQueue.Clear();
        LoadMonsterCalibrations();
    }

    private static FieldInfo? FindField(Type? type, string name)
    {
        while (type is not null)
        {
            FieldInfo? field = type.GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field is not null) return field;
            type = type.BaseType;
        }
        return null;
    }

    private static string ComputeFileSha256(string path)
    {
        using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using SHA256 sha256 = SHA256.Create();
        return ToLowerHex(sha256.ComputeHash(stream));
    }

    private static string ToLowerHex(byte[] bytes)
    {
        var result = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) result.Append(value.ToString("x2"));
        return result.ToString();
    }

    private static bool IsSha256(string? value) => value is not null && value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or
            >= 'A' and <= 'F');

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void EnsureSourceUnchanged(CatalogSource source, FileInfo current)
    {
        if (current.Length != source.Length ||
            current.LastWriteTimeUtc.Ticks != source.WriteTicksUtc)
        {
            throw new IOException("GameData.db changed while BazaarLab was reading it");
        }
    }
}
