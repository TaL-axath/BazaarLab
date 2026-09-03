using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace BazaarLab.Plugin;

internal sealed class PersistentPredictionWorker : IDisposable
{
    private readonly object _sync = new object();
    private Process? _process;
    private string _catalogPath = string.Empty;
    private string? _owner;
    private bool _completed;
    private bool _succeeded;
    private string _response = string.Empty;
    private readonly StringBuilder _log = new StringBuilder();

    public bool IsBusy
    {
        get { lock (_sync) return _owner is not null; }
    }

    public bool IsOwnedBy(string owner)
    {
        lock (_sync) return string.Equals(_owner, owner, StringComparison.Ordinal);
    }

    public void Warm(string corePath, string catalogPath)
    {
        lock (_sync)
        {
            if (_owner is null) EnsureProcess(corePath, catalogPath);
        }
    }

    public bool TryStart(string owner, string corePath, string catalogPath,
        string inputPath, string outputPath, out string error)
    {
        lock (_sync)
        {
            if (_owner is not null)
            {
                error = "prediction worker is busy";
                return false;
            }
            try
            {
                EnsureProcess(corePath, catalogPath);
                _owner = owner;
                _completed = false;
                _succeeded = false;
                _response = string.Empty;
                _log.Clear();
                string request = JsonSerializer.Serialize(new
                {
                    inputPath,
                    outputPath,
                });
                _process!.StandardInput.WriteLine(request);
                _process.StandardInput.Flush();
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                _owner = null;
                StopProcess();
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }
    }

    public bool TryTakeCompletion(string owner, out bool succeeded, out string log)
    {
        lock (_sync)
        {
            if (!string.Equals(_owner, owner, StringComparison.Ordinal))
            {
                succeeded = false;
                log = string.Empty;
                return false;
            }
            if (!_completed && (_process is null || _process.HasExited))
            {
                _completed = true;
                _succeeded = false;
                _response = "prediction worker exited unexpectedly";
            }
            if (!_completed)
            {
                succeeded = false;
                log = string.Empty;
                return false;
            }
            succeeded = _succeeded;
            log = _response + (_log.Length == 0 ? string.Empty : Environment.NewLine + _log);
            _owner = null;
            _completed = false;
            _succeeded = false;
            _response = string.Empty;
            return true;
        }
    }

    public void Cancel(string owner)
    {
        lock (_sync)
        {
            if (!string.Equals(_owner, owner, StringComparison.Ordinal)) return;
            _owner = null;
            StopProcess();
        }
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            if (_owner is null) StopProcess();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _owner = null;
            StopProcess();
        }
    }

    private void EnsureProcess(string corePath, string catalogPath)
    {
        if (_process is not null && !_process.HasExited &&
            string.Equals(_catalogPath, catalogPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        StopProcess();
        string dotnetExecutable = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!File.Exists(dotnetExecutable)) dotnetExecutable = "dotnet";
        var start = new ProcessStartInfo
        {
            FileName = dotnetExecutable,
            Arguments = Quote(corePath) + " serve-bpp-fixed-files " +
                Quote(catalogPath) + " 20260831 50 2400",
            WorkingDirectory = Path.GetDirectoryName(corePath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += OnOutput;
        process.ErrorDataReceived += OnError;
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("failed to start prediction worker");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _catalogPath = catalogPath;
    }

    private void OnOutput(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data)) return;
        lock (_sync)
        {
            if (_owner is null) return;
            try
            {
                using JsonDocument document = JsonDocument.Parse(args.Data);
                JsonElement root = document.RootElement;
                _succeeded = root.TryGetProperty("ok", out JsonElement ok) && ok.GetBoolean();
                _response = _succeeded ? args.Data :
                    root.TryGetProperty("error", out JsonElement error)
                        ? error.GetString() ?? args.Data : args.Data;
            }
            catch (Exception)
            {
                _succeeded = false;
                _response = args.Data;
            }
            _completed = true;
        }
    }

    private void OnError(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data)) return;
        lock (_sync) _log.AppendLine(args.Data);
    }

    private void StopProcess()
    {
        Process? process = _process;
        _process = null;
        _catalogPath = string.Empty;
        if (process is null) return;
        process.OutputDataReceived -= OnOutput;
        process.ErrorDataReceived -= OnError;
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (Exception)
        {
            // The worker can exit between the state check and Kill.
        }
        process.Dispose();
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
