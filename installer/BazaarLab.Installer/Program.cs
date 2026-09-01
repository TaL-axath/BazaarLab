using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BazaarLab.Installer
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string requested = ReadArgument(args, "--game-dir");
            if (args.Any(value => string.Equals(value, "--silent", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    DependencyReport report = DependencyChecker.Check(requested);
                    if (!report.CanInstall) return 2;
                    PackageInstaller.Install(Path.GetFullPath(requested), InstallerLog.Write);
                    return 0;
                }
                catch (Exception exception)
                {
                    InstallerLog.Write("Silent install failed: " + exception);
                    return 1;
                }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerForm(requested));
            return 0;
        }

        private static string ReadArgument(string[] args, string name)
        {
            for (int index = 0; index + 1 < args.Length; index++)
                if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                    return args[index + 1];
            return string.Empty;
        }
    }

    internal sealed class InstallerForm : Form
    {
        private const string InstallerVersion = "1.0.1";
        private const string BppUrl = "https://github.com/BazaarPlusPlus/BazaarPlusPlus";
        private const string DotNetUrl = "https://dotnet.microsoft.com/download/dotnet/8.0";

        private readonly ComboBox _gamePath = new ComboBox();
        private readonly ListView _checks = new ListView();
        private readonly Label _summary = new Label();
        private readonly Button _install = new Button();
        private readonly Button _dotnet = new Button();
        private readonly Button _bpp = new Button();
        private readonly TextBox _log = new TextBox();
        private bool _checksPassed;

        public InstallerForm(string requestedPath)
        {
            Text = "BazaarLab 安装器 v" + InstallerVersion;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 570);
            Size = new Size(820, 640);
            Font = new Font("Microsoft YaHei UI", 9F);
            BuildUi();
            LoadCandidates(requestedPath);
            RunChecks();
        }

        private void BuildUi()
        {
            var title = new Label
            {
                Text = "BazaarLab 本地战斗实验室",
                Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(18, 16),
            };
            var subtitle = new Label
            {
                Text = "自动查找 Steam 游戏目录、检查 BPP 环境并安装 BazaarLab。BPP 安装包已自带 BepInEx。",
                AutoSize = true,
                Location = new Point(21, 52),
            };
            var pathLabel = new Label
            {
                Text = "游戏根目录（应直接包含 TheBazaar.exe）：",
                AutoSize = true,
                Location = new Point(20, 85),
            };
            _gamePath.Location = new Point(22, 108);
            _gamePath.Size = new Size(620, 28);
            _gamePath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _gamePath.DropDownStyle = ComboBoxStyle.DropDown;
            _gamePath.SelectedIndexChanged += delegate { RunChecks(); };

            var browse = new Button
            {
                Text = "浏览…",
                Location = new Point(652, 106),
                Size = new Size(72, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            browse.Click += BrowseClick;
            var scan = new Button
            {
                Text = "重新扫描",
                Location = new Point(730, 106),
                Size = new Size(72, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            scan.Click += delegate { LoadCandidates(_gamePath.Text); RunChecks(); };

            _checks.Location = new Point(22, 150);
            _checks.Size = new Size(780, 150);
            _checks.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _checks.View = View.Details;
            _checks.FullRowSelect = true;
            _checks.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _checks.Columns.Add("组件", 130);
            _checks.Columns.Add("状态", 90);
            _checks.Columns.Add("详情", 540);

            _summary.Location = new Point(22, 310);
            _summary.Size = new Size(780, 42);
            _summary.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _dotnet.Text = "安装 .NET 8";
            _dotnet.Location = new Point(22, 355);
            _dotnet.Size = new Size(120, 32);
            _dotnet.Click += InstallDotNetClick;
            _bpp.Text = "获取 BPP（含 BepInEx）";
            _bpp.Location = new Point(150, 355);
            _bpp.Size = new Size(180, 32);
            _bpp.Click += delegate { OpenUrl(BppUrl); };
            _install.Text = "安装 / 更新 BazaarLab";
            _install.Location = new Point(590, 350);
            _install.Size = new Size(212, 42);
            _install.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _install.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            _install.Click += InstallClick;

            _log.Location = new Point(22, 405);
            _log.Size = new Size(780, 180);
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom |
                AnchorStyles.Left | AnchorStyles.Right;
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = Color.White;

            Controls.Add(title);
            Controls.Add(subtitle);
            Controls.Add(pathLabel);
            Controls.Add(_gamePath);
            Controls.Add(browse);
            Controls.Add(scan);
            Controls.Add(_checks);
            Controls.Add(_summary);
            Controls.Add(_dotnet);
            Controls.Add(_bpp);
            Controls.Add(_install);
            Controls.Add(_log);
        }

        private void LoadCandidates(string preferred)
        {
            string current = NormalizePath(preferred);
            List<string> values = GameLocator.FindGameDirectories().ToList();
            if (!string.IsNullOrEmpty(current) && Directory.Exists(current)) values.Insert(0, current);
            values = values.Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _gamePath.BeginUpdate();
            _gamePath.Items.Clear();
            foreach (string value in values) _gamePath.Items.Add(value);
            _gamePath.EndUpdate();
            string validPreferred = values.FirstOrDefault(GameLocator.IsGameDirectory);
            if (!string.IsNullOrEmpty(current) && GameLocator.IsGameDirectory(current))
                validPreferred = current;
            if (!string.IsNullOrEmpty(validPreferred)) _gamePath.Text = validPreferred;
            else if (!string.IsNullOrEmpty(current)) _gamePath.Text = current;
        }

        private void BrowseClick(object sender, EventArgs args)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择包含 TheBazaar.exe 的游戏根目录";
                dialog.ShowNewFolderButton = false;
                if (Directory.Exists(_gamePath.Text)) dialog.SelectedPath = _gamePath.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _gamePath.Text = dialog.SelectedPath;
                    RunChecks();
                }
            }
        }

        private void RunChecks()
        {
            string root = NormalizePath(_gamePath.Text);
            DependencyReport report = DependencyChecker.Check(root);
            _checks.BeginUpdate();
            _checks.Items.Clear();
            AddCheck("The Bazaar", report.GameOk, false, report.GameDetail);
            AddCheck("BepInEx", report.BepInExOk, false, report.BepInExDetail);
            AddCheck("BazaarPlusPlus", report.BppOk, report.BppWarning, report.BppDetail);
            AddCheck(".NET 8 Runtime", report.DotNetOk, false, report.DotNetDetail);
            AddCheck("安装包载荷", report.PayloadOk, false, report.PayloadDetail);
            _checks.EndUpdate();
            _checksPassed = report.CanInstall;
            _install.Enabled = _checksPassed;
            _dotnet.Enabled = !report.DotNetOk;
            _summary.ForeColor = _checksPassed ? Color.DarkGreen : Color.DarkRed;
            _summary.Text = _checksPassed
                ? "检查通过，可以安装。现有 BazaarLab 会先备份，再原子替换。"
                : "检查未通过。若缺少 BPP 或 BepInEx，只需安装最新版 BPP（已自带 BepInEx）。";
        }

        private void AddCheck(string component, bool ok, bool warning, string detail)
        {
            string status = ok ? (warning ? "警告" : "通过") : "缺失";
            var item = new ListViewItem(new[] { component, status, detail ?? string.Empty });
            item.ForeColor = ok ? (warning ? Color.DarkOrange : Color.DarkGreen) : Color.DarkRed;
            _checks.Items.Add(item);
        }

        private void InstallDotNetClick(object sender, EventArgs args)
        {
            try
            {
                string winget = DependencyChecker.FindOnPath("winget.exe");
                if (string.IsNullOrEmpty(winget))
                {
                    AppendLog("系统未找到 winget，正在打开微软 .NET 8 下载页。");
                    OpenUrl(DotNetUrl);
                    return;
                }
                AppendLog("正在通过 winget 安装 Microsoft .NET 8 Runtime…");
                var start = new ProcessStartInfo
                {
                    FileName = winget,
                    Arguments = "install --id Microsoft.DotNet.Runtime.8 --exact --silent " +
                        "--accept-package-agreements --accept-source-agreements",
                    UseShellExecute = true,
                };
                using (Process process = Process.Start(start))
                {
                    if (process != null) process.WaitForExit();
                }
                RunChecks();
            }
            catch (Exception exception)
            {
                AppendLog(".NET 安装启动失败：" + exception.Message);
                OpenUrl(DotNetUrl);
            }
        }

        private void InstallClick(object sender, EventArgs args)
        {
            RunChecks();
            if (!_checksPassed) return;
            if (Process.GetProcessesByName("TheBazaar").Length > 0)
            {
                MessageBox.Show(this, "请先完全退出 The Bazaar，再执行安装。",
                    "BazaarLab", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _install.Enabled = false;
            UseWaitCursor = true;
            try
            {
                string result = PackageInstaller.Install(NormalizePath(_gamePath.Text), AppendLog);
                AppendLog(result);
                MessageBox.Show(this, result + "\n\n启动游戏后即可使用 BazaarLab。",
                    "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception)
            {
                AppendLog("安装失败：" + exception);
                MessageBox.Show(this, "安装失败：\n" + exception.Message +
                    "\n\n原版本已尽量保留或恢复。详情见窗口日志。",
                    "BazaarLab", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UseWaitCursor = false;
                RunChecks();
            }
        }

        private void AppendLog(string value)
        {
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) +
                "  " + value + Environment.NewLine);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
            InstallerLog.Write(value);
            Application.DoEvents();
        }

        private static string NormalizePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            try { return Path.GetFullPath(value.Trim().Trim('"')); }
            catch { return value.Trim().Trim('"'); }
        }

        private static void OpenUrl(string url)
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
    }

    internal sealed class DependencyReport
    {
        public bool GameOk;
        public bool BepInExOk;
        public bool BppOk;
        public bool BppWarning;
        public bool DotNetOk;
        public bool PayloadOk;
        public string GameDetail = string.Empty;
        public string BepInExDetail = string.Empty;
        public string BppDetail = string.Empty;
        public string DotNetDetail = string.Empty;
        public string PayloadDetail = string.Empty;
        public bool CanInstall { get { return GameOk && BepInExOk && BppOk && DotNetOk && PayloadOk; } }
    }

    internal static class DependencyChecker
    {
        public static DependencyReport Check(string root)
        {
            var report = new DependencyReport();
            report.GameOk = GameLocator.IsGameDirectory(root);
            report.GameDetail = report.GameOk ? root : "未找到 TheBazaar.exe 或游戏 Managed 目录";

            string bepinex = Path.Combine(root ?? string.Empty, "BepInEx", "core", "BepInEx.dll");
            Version bepinexVersion = ReadAssemblyVersion(bepinex);
            report.BepInExOk = bepinexVersion != null && bepinexVersion.Major == 5 &&
                bepinexVersion >= new Version(5, 4, 0, 0);
            report.BepInExDetail = bepinexVersion == null
                ? "未找到 BepInEx 5.4；请安装最新版 BPP，其安装包已自带 BepInEx"
                : "版本 " + bepinexVersion;

            string bpp = Path.Combine(root ?? string.Empty, "BepInEx", "plugins", "BazaarPlusPlus.dll");
            Version bppVersion = ReadAssemblyVersion(bpp);
            report.BppOk = bppVersion != null && bppVersion.Major == 5;
            report.BppWarning = report.BppOk && bppVersion < new Version(5, 2, 1, 0);
            report.BppDetail = bppVersion == null
                ? "未找到 BazaarPlusPlus.dll；请使用 BPP 官方安装器（同时安装 BepInEx）"
                : "版本 " + bppVersion + (report.BppWarning ? "；推荐 5.2.1 或更高版本" : string.Empty);

            string runtimeDetail;
            report.DotNetOk = HasDotNet8(out runtimeDetail);
            report.DotNetDetail = runtimeDetail;

            string package = AppDomain.CurrentDomain.BaseDirectory;
            string manifest = Path.Combine(package, "payload.manifest");
            string plugin = Path.Combine(package, "payload", "BazaarLab", "BazaarLab.dll");
            report.PayloadOk = File.Exists(manifest) && File.Exists(plugin);
            report.PayloadDetail = report.PayloadOk
                ? "安装载荷与校验清单存在"
                : "安装包不完整；请完整解压 ZIP 后再运行";
            return report;
        }

        public static Version ReadAssemblyVersion(string path)
        {
            try { return File.Exists(path) ? AssemblyName.GetAssemblyName(path).Version : null; }
            catch { return null; }
        }

        public static bool HasDotNet8(out string detail)
        {
            string dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "dotnet.exe");
            if (!File.Exists(dotnet)) dotnet = FindOnPath("dotnet.exe");
            if (string.IsNullOrEmpty(dotnet))
            {
                detail = "未找到 64 位 dotnet.exe";
                return false;
            }
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = dotnet,
                    Arguments = "--list-runtimes",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                using (Process process = Process.Start(start))
                {
                    string output = process == null ? string.Empty : process.StandardOutput.ReadToEnd();
                    if (process != null) process.WaitForExit(5000);
                    string match = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(line => line.StartsWith("Microsoft.NETCore.App 8.",
                            StringComparison.OrdinalIgnoreCase));
                    detail = match ?? "已找到 dotnet，但缺少 Microsoft.NETCore.App 8.x";
                    return match != null;
                }
            }
            catch (Exception exception)
            {
                detail = "无法检查 .NET：" + exception.Message;
                return false;
            }
        }

        public static string FindOnPath(string fileName)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string part in path.Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(part.Trim(), fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return string.Empty;
        }
    }

    internal static class GameLocator
    {
        public static IEnumerable<string> FindGameDirectories()
        {
            var libraries = new List<string>();
            AddRegistrySteamPath(libraries, Registry.CurrentUser, @"Software\Valve\Steam");
            AddRegistrySteamPath(libraries, Registry.LocalMachine,
                @"SOFTWARE\WOW6432Node\Valve\Steam");
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(programFiles)) libraries.Add(Path.Combine(programFiles, "Steam"));

            foreach (string steam in libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            {
                yield return Path.Combine(steam, "steamapps", "common", "The Bazaar");
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                string text;
                try { text = File.ReadAllText(vdf); }
                catch { continue; }
                foreach (Match match in Regex.Matches(text, "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\""))
                {
                    string library = match.Groups[1].Value.Replace("\\\\", "\\");
                    yield return Path.Combine(library, "steamapps", "common", "The Bazaar");
                }
            }
        }

        public static bool IsGameDirectory(string root)
        {
            return !string.IsNullOrEmpty(root) &&
                File.Exists(Path.Combine(root, "TheBazaar.exe")) &&
                Directory.Exists(Path.Combine(root, "TheBazaar_Data", "Managed"));
        }

        private static void AddRegistrySteamPath(List<string> paths, RegistryKey root, string keyName)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(keyName))
                {
                    if (key == null) return;
                    object value = key.GetValue("SteamPath") ?? key.GetValue("InstallPath");
                    if (value != null) paths.Add(value.ToString().Replace('/', '\\'));
                }
            }
            catch { }
        }
    }

    internal static class PackageInstaller
    {
        public static string Install(string gameRoot, Action<string> log)
        {
            string packageRoot = AppDomain.CurrentDomain.BaseDirectory;
            string payloadRoot = Path.Combine(packageRoot, "payload");
            string payloadPlugin = Path.Combine(payloadRoot, "BazaarLab");
            string manifestPath = Path.Combine(packageRoot, "payload.manifest");
            VerifyManifest(payloadRoot, manifestPath, log);

            string pluginsRoot = Path.GetFullPath(Path.Combine(gameRoot, "BepInEx", "plugins"));
            string destination = Path.GetFullPath(Path.Combine(pluginsRoot, "BazaarLab"));
            EnsureChildPath(pluginsRoot, destination);
            string token = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string staging = Path.Combine(pluginsRoot, ".BazaarLab.installing-" + Guid.NewGuid().ToString("N"));
            string backupRoot = Path.GetFullPath(Path.Combine(gameRoot, "BepInEx", "config",
                "BazaarLab", "install-backups"));
            string backup = Path.Combine(backupRoot, "BazaarLab-" + token);
            string legacy = Path.Combine(pluginsRoot, "LookingIN.LocalCapture");
            string legacyBackup = Path.Combine(backupRoot,
                "LookingIN.LocalCapture-disabled-" + token);
            EnsureChildPath(pluginsRoot, staging);
            Directory.CreateDirectory(backupRoot);
            EnsureChildPath(backupRoot, backup);
            MoveLegacyPluginBackups(pluginsRoot, backupRoot, log);

            log("正在复制并校验安装载荷…");
            CopyDirectory(payloadPlugin, staging);
            VerifyInstalledManifest(staging, manifestPath, log);
            bool movedExisting = false;
            try
            {
                if (Directory.Exists(destination))
                {
                    Directory.Move(destination, backup);
                    movedExisting = true;
                    log("旧版已备份到 " + backup);
                }
                if (Directory.Exists(legacy))
                {
                    Directory.Move(legacy, legacyBackup);
                    log("旧 LookingIN.LocalCapture 插件已停用并备份。");
                }
                Directory.Move(staging, destination);
                string receipt = Path.Combine(destination, "install-receipt.txt");
                File.WriteAllText(receipt,
                    "BazaarLab 1.0.1" + Environment.NewLine +
                    "InstalledAt=" + DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) +
                    Environment.NewLine + "GameRoot=" + gameRoot + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                try
                {
                    if (Directory.Exists(staging)) Directory.Delete(staging, true);
                    if (!Directory.Exists(destination) && movedExisting && Directory.Exists(backup))
                        Directory.Move(backup, destination);
                }
                catch { }
                throw;
            }
            return "BazaarLab 1.0.1 已安装到：" + destination;
        }

        private static void MoveLegacyPluginBackups(string pluginsRoot, string backupRoot,
            Action<string> log)
        {
            foreach (string directory in Directory.GetDirectories(pluginsRoot,
                "BazaarLab.backup-*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(directory);
                string target = Path.Combine(backupRoot, name);
                int suffix = 1;
                while (Directory.Exists(target))
                    target = Path.Combine(backupRoot, name + "-" + suffix++);
                EnsureChildPath(pluginsRoot, directory);
                EnsureChildPath(backupRoot, target);
                Directory.Move(directory, target);
                log("已将旧插件备份移出 BepInEx 扫描目录：" + target);
            }
        }

        private static void VerifyManifest(string payloadRoot, string manifestPath, Action<string> log)
        {
            if (!File.Exists(manifestPath)) throw new FileNotFoundException("缺少 payload.manifest");
            foreach (ManifestEntry entry in ReadManifest(manifestPath))
            {
                string path = Path.GetFullPath(Path.Combine(payloadRoot, entry.RelativePath));
                EnsureChildPath(payloadRoot, path);
                if (!File.Exists(path)) throw new FileNotFoundException("载荷缺少文件：" + entry.RelativePath);
                if (!string.Equals(HashFile(path), entry.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("载荷校验失败：" + entry.RelativePath);
            }
            log("安装包 SHA-256 校验通过。");
        }

        private static void VerifyInstalledManifest(string staging, string manifestPath, Action<string> log)
        {
            foreach (ManifestEntry entry in ReadManifest(manifestPath))
            {
                string relative = entry.RelativePath.Replace('/', '\\');
                const string prefix = "BazaarLab\\";
                if (!relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("清单路径非法：" + entry.RelativePath);
                string path = Path.GetFullPath(Path.Combine(staging, relative.Substring(prefix.Length)));
                EnsureChildPath(staging, path);
                if (!File.Exists(path) ||
                    !string.Equals(HashFile(path), entry.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装后校验失败：" + entry.RelativePath);
            }
            log("暂存目录校验通过，准备切换版本。");
        }

        private static IEnumerable<ManifestEntry> ReadManifest(string path)
        {
            foreach (string line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                int separator = line.IndexOf('|');
                if (separator != 64) throw new InvalidDataException("清单行格式错误");
                yield return new ManifestEntry(line.Substring(0, separator),
                    line.Substring(separator + 1));
            }
        }

        private static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                string relative = directory.Substring(source.Length).TrimStart('\\', '/');
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(source.Length).TrimStart('\\', '/');
                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private static void EnsureChildPath(string parent, string child)
        {
            string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(child);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝访问安装目录之外的路径：" + target);
        }

        private sealed class ManifestEntry
        {
            public readonly string Hash;
            public readonly string RelativePath;
            public ManifestEntry(string hash, string relativePath)
            {
                Hash = hash;
                RelativePath = relativePath;
            }
        }
    }

    internal static class InstallerLog
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BazaarLab", "installer.log");

        public static void Write(string value)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath,
                    DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) + " " + value +
                    Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
