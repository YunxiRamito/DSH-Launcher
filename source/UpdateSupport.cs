using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DeepSeekHarnessLauncher
{
    /// <summary>远端发布清单里的一条版本信息。</summary>
    internal sealed class UpdateManifest
    {
        public string Version { get; set; }
        public string Sha256 { get; set; }
        public string SubDirectory { get; set; }
        public string Notes { get; set; }
        public List<string> Urls { get; } = new List<string>();
    }

    /// <summary>自更新。</summary>
    internal static class UpdateSupport
    {
        /// <summary>版本清单地址。启动器仓库根目录的 manifest.json。</summary>
        private const string Repository = "YunxiRamito/DSH-Launcher";
        private const string Branch = "main";
        private const string ManifestFile = "manifest.json";

        /// <summary>GitHub 资产在国内的加速前缀,按顺序试。</summary>
        private static readonly string[] GitHubProxies = new string[]
        {
            "https://ghfast.top/",
            "https://ghproxy.net/",
        };

        private const int FetchTimeoutMs = 20000;
        private const int DownloadTimeoutMs = 600000;

        // ---------------------------------------------------------------- 清单

        /// <summary>
        /// 版本清单地址。默认用启动器仓库根目录的 manifest.json;
        /// 同目录的 launcher.json 可以通过 updateManifestUrls 覆盖(镜像 / 自建源 / 内网用)。
        /// </summary>
        private static string[] ResolveManifestUrls()
        {
            List<string> configured = ReadConfiguredManifestUrls();
            if (configured.Count > 0)
            {
                return configured.ToArray();
            }

            string jsdelivr = "https://cdn.jsdelivr.net/gh/" + Repository + "@" + Branch + "/" + ManifestFile;
            string raw = "https://raw.githubusercontent.com/" + Repository + "/" + Branch + "/" + ManifestFile;

            // 国内 jsDelivr 更快,国外 raw 更快
            return RegionInfo.IsChinaMainland
                ? new string[] { jsdelivr, raw }
                : new string[] { raw, jsdelivr };
        }

        /// <summary>从同目录 launcher.json 里读 updateManifestUrls(字符串或数组都认)。</summary>
        private static List<string> ReadConfiguredManifestUrls()
        {
            List<string> urls = new List<string>();
            try
            {
                string path = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "launcher.json");
                if (!File.Exists(path))
                {
                    return urls;
                }

                string text = File.ReadAllText(path, Encoding.UTF8);

                // 数组形式
                Match arrayMatch = Regex.Match(
                    text,
                    "\"updateManifestUrls\"\\s*:\\s*\\[(?<body>[^\\]]*)\\]",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (arrayMatch.Success)
                {
                    MatchCollection items = Regex.Matches(arrayMatch.Groups["body"].Value, "\"([^\"]+)\"");
                    for (int index = 0; index < items.Count; index++)
                    {
                        string value = items[index].Groups[1].Value.Trim();
                        if (value.Length > 0 && !urls.Contains(value))
                        {
                            urls.Add(value);
                        }
                    }
                }
                else
                {
                    // 单个字符串形式
                    Match singleMatch = Regex.Match(
                        text,
                        "\"updateManifestUrl\"\\s*:\\s*\"(?<value>[^\"]+)\"",
                        RegexOptions.IgnoreCase);
                    if (singleMatch.Success)
                    {
                        string value = singleMatch.Groups["value"].Value.Trim();
                        if (value.Length > 0)
                        {
                            urls.Add(value);
                        }
                    }
                }
            }
            catch
            {
            }

            return urls;
        }

        /// <summary>拉版本清单。国内优先 jsDelivr,失败退 raw.githubusercontent。</summary>
        public static UpdateManifest FetchManifest(out string error)
        {
            error = null;

            string[] urls = ResolveManifestUrls();

            string json = null;
            List<string> failures = new List<string>();
            for (int index = 0; index < urls.Length; index++)
            {
                try
                {
                    json = HttpGetText(urls[index], FetchTimeoutMs);
                    if (!string.IsNullOrEmpty(json))
                    {
                        break;
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(urls[index] + " -> " + exception.Message);
                }
            }

            if (string.IsNullOrEmpty(json))
            {
                error = failures.Count > 0
                    ? string.Join("; ", failures.ToArray())
                    : "版本清单拿不到(网络不通?)";
                return null;
            }

            UpdateManifest manifest = ParseManifest(json, out error);
            return manifest;
        }

        internal static UpdateManifest ParseManifest(string json, out string error)
        {
            error = null;
            UpdateManifest manifest = new UpdateManifest();

            manifest.Version = MatchString(json, "version");
            manifest.Sha256 = MatchString(json, "sha256");
            manifest.SubDirectory = MatchString(json, "subDirectory");
            manifest.Notes = MatchString(json, "notes");

            string github = MatchNestedString(json, "assets", "github");
            if (!string.IsNullOrEmpty(github))
            {
                manifest.Urls.AddRange(Mirrorize(github));
            }

            MatchCollection mirrors = Regex.Matches(
                json,
                "\"mirrors\"\\s*:\\s*\\[(?<list>[^\\]]*)\\]",
                RegexOptions.IgnoreCase);
            for (int index = 0; index < mirrors.Count; index++)
            {
                MatchCollection items = Regex.Matches(mirrors[index].Groups["list"].Value, "\"([^\"]+)\"");
                for (int item = 0; item < items.Count; item++)
                {
                    string url = items[item].Groups[1].Value;
                    if (!manifest.Urls.Contains(url))
                    {
                        manifest.Urls.Add(url);
                    }
                }
            }

            // 兼容最简格式:{ "version": "...", "url": "..." }
            if (manifest.Urls.Count == 0)
            {
                string simple = MatchString(json, "url");
                if (!string.IsNullOrEmpty(simple))
                {
                    manifest.Urls.AddRange(Mirrorize(simple));
                }
            }

            if (string.IsNullOrEmpty(manifest.Version))
            {
                error = "清单里没有 version 字段";
                return null;
            }

            if (manifest.Urls.Count == 0)
            {
                error = "清单里没有可用的下载地址";
                return null;
            }

            return manifest;
        }

        /// <summary>
        /// 给 GitHub 资产地址配上加速前缀:只有在中国大陆才套 CDN 加速,
        /// 在国外直接用 GitHub 原始地址(反而更快,也少一跳)。
        /// 与安装器里的判断保持一致。
        /// </summary>
        private static List<string> Mirrorize(string assetUrl)
        {
            List<string> urls = new List<string>();
            if (string.IsNullOrEmpty(assetUrl))
            {
                return urls;
            }

            if (RegionInfo.IsChinaMainland)
            {
                for (int index = 0; index < GitHubProxies.Length; index++)
                {
                    urls.Add(GitHubProxies[index] + assetUrl);
                }
            }

            urls.Add(assetUrl);

            if (!RegionInfo.IsChinaMainland)
            {
                // 国外也留一条加速兜底,GitHub 抽风时能救一下
                urls.Add(GitHubProxies[0] + assetUrl);
            }

            return urls;
        }

        private static string MatchString(string json, string key)
        {
            Match match = Regex.Match(
                json,
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?<value>(?:[^\"\\\\]|\\\\.)*)\"",
                RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return null;
            }

            return match.Groups["value"].Value.Replace("\\/", "/").Replace("\\\\", "\\").Trim();
        }

        private static string MatchNestedString(string json, string section, string key)
        {
            Match sectionMatch = Regex.Match(
                json,
                "\"" + Regex.Escape(section) + "\"\\s*:\\s*\\{(?<body>[^}]*)\\}",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!sectionMatch.Success)
            {
                return null;
            }

            return MatchString(sectionMatch.Groups["body"].Value, key);
        }

        // ---------------------------------------------------------------- 版本比较

        /// <summary>远端比本机新就返回 true。</summary>
        public static bool IsNewer(string remoteVersion, string localVersion)
        {
            Version remote = ParseVersion(remoteVersion);
            Version local = ParseVersion(localVersion);
            if (remote == null || local == null)
            {
                return false;
            }

            return remote > local;
        }

        internal static Version ParseVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            Match match = Regex.Match(text, @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!match.Success)
            {
                return null;
            }

            int major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int minor = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            int build = match.Groups[3].Success
                ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)
                : 0;
            return new Version(major, minor, build);
        }

        // ---------------------------------------------------------------- 下载 + 落地

        /// <summary>更新包下载到暂存区并解压。返回解压出来的目录(里面就是启动器的文件)。</summary>
        public static string PrepareStaging(UpdateManifest manifest, string installDirectory, Action<long, long> progress, out string error)
        {
            error = null;

            string stagingRoot = Path.Combine(Path.GetTempPath(), "DeepSeekHarnessUpdate");
            string packagePath = Path.Combine(stagingRoot, "launcher.zip");
            string extractPath = Path.Combine(stagingRoot, "new");

            try
            {
                if (Directory.Exists(stagingRoot))
                {
                    safeDeleteDirectory(stagingRoot);
                }

                Directory.CreateDirectory(stagingRoot);

                string usedUrl = DownloadWithFallback(manifest.Urls, packagePath, progress);
                InstallLoggerLight("更新包已下载: " + usedUrl);

                if (!string.IsNullOrEmpty(manifest.Sha256))
                {
                    string actual = ComputeSha256(packagePath);
                    if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        error = "下载的更新包校验失败(期望 " + manifest.Sha256 + ",实际 " + actual + ")";
                        return null;
                    }
                }

                Directory.CreateDirectory(extractPath);
                System.IO.Compression.ZipFile.ExtractToDirectory(packagePath, extractPath, true);

                string effective = extractPath;
                if (!string.IsNullOrEmpty(manifest.SubDirectory))
                {
                    effective = Path.Combine(extractPath, manifest.SubDirectory.Trim('\\', '/'));
                }

                string coreExe = Path.Combine(effective, "DeepSeek Harness.Core.exe");
                if (!File.Exists(coreExe))
                {
                    error = "更新包里没有 DeepSeek Harness.Core.exe";
                    return null;
                }

                return effective;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return null;
            }
        }

        /// <summary>
        /// 应用更新:拉起一个独立的替换脚本,然后退出自己。
        /// 必须由脚本干替换的活 —— 正在运行的程序没法覆盖自己的 exe。
        /// </summary>
        public static void ApplyUpdateAndExit(string newFilesDirectory, string installDirectory)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), "DeepSeekHarnessUpdate", "apply-update.ps1");
            string logPath = Path.Combine(installDirectory, "logs", "update.log");

            // 把「可执行文件 + 参数」拼成一条完整的命令行。
            // 路径可能有空格,所以整体套双引号;里面的反斜杠原样保留,不做 C 风格转义。
            string launcherCommand =
                "\"" + Path.Combine(installDirectory, "DeepSeek Harness.exe") + "\" "
                + "--no-browser --updated=" + Constants.Version;

            string script = BuildApplyScript(
                installDirectory,
                newFilesDirectory,
                Program.PreviousProcessId,
                logPath,
                launcherCommand,
                Constants.Version);

            Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
            File.WriteAllText(scriptPath, script, new UTF8Encoding(false));

            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = ResolvePowerShell(),
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \""
                    + scriptPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            Process.Start(info);
            InstallLoggerLight("已拉起替换脚本,本进程退出");
        }

        /// <summary>
        /// 生成替换脚本。
        ///
        /// 注意:脚本里只用英文,注释也别写中文 —— 脚本是不带 BOM 的 UTF-8,
        /// PowerShell 5.1 会按 ANSI 读,中文字面量会直接把脚本读坏。
        /// 需要给用户看的中文,交给重启后的启动器去弹通知。
        /// </summary>
        internal static string BuildApplyScript(
            string installDirectory,
            string newFilesDirectory,
            int processId,
            string logPath,
            string launcherCommand,
            string newVersion)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("$ErrorActionPreference = 'Continue'");
            builder.AppendLine("$log = " + Quote(logPath));
            builder.AppendLine("function W([string]$m) {");
            builder.AppendLine("  try {");
            builder.AppendLine("    $d = Split-Path -Parent $log;");
            builder.AppendLine("    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }");
            builder.AppendLine("    \"$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m\" | Add-Content -Path $log -Encoding UTF8");
            builder.AppendLine("  } catch { }");
            builder.AppendLine("}");
            builder.AppendLine("$dir = " + Quote(installDirectory));
            builder.AppendLine("$new = " + Quote(newFilesDirectory));
            builder.AppendLine("$launcher = " + Quote(Path.Combine(installDirectory, "DeepSeek Harness.exe")));
            builder.AppendLine("$procId = " + processId.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("$newVersion = " + Quote(newVersion));
            builder.AppendLine("W \"apply-update started, waiting for pid $procId to exit\"");
            builder.AppendLine("$deadline = (Get-Date).AddSeconds(90)");
            builder.AppendLine("while ((Get-Date) -lt $deadline) {");
            builder.AppendLine("  if (-not (Get-Process -Id $procId -ErrorAction SilentlyContinue)) { break }");
            builder.AppendLine("  Start-Sleep -Milliseconds 400");
            builder.AppendLine("}");
            builder.AppendLine("Start-Sleep -Seconds 2");
            builder.AppendLine("$backup = Join-Path $env:TEMP ('DeepSeekHarnessBackup-' + (Get-Date -Format 'yyyyMMddHHmmss'))");
            builder.AppendLine("W \"backup to $backup\"");
            builder.AppendLine("Copy-Item -Path $dir -Destination $backup -Recurse -Force -ErrorAction SilentlyContinue");
            builder.AppendLine("$fail = 0");
            builder.AppendLine("$total = 0");
            builder.AppendLine("Get-ChildItem -Path $new -Recurse -File | ForEach-Object {");
            builder.AppendLine("  $total++");
            builder.AppendLine("  $rel = $_.FullName.Substring($new.Length).TrimStart('\\')");
            builder.AppendLine("  $target = Join-Path $dir $rel");
            builder.AppendLine("  $targetDir = Split-Path -Parent $target");
            builder.AppendLine("  if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }");
            builder.AppendLine("  try { Copy-Item -LiteralPath $_.FullName -Destination $target -Force -ErrorAction Stop }");
            builder.AppendLine("  catch { $fail++; W (\"copy failed: $rel -> \" + $_.Exception.Message) }");
            builder.AppendLine("}");
            builder.AppendLine("W \"files copied: total=$total failed=$fail\"");
            builder.AppendLine("Start-Sleep -Seconds 1");
            // Restart the launcher only. DSH service (node) is a separate process and stays alive.
            //
            // Why a scheduled task instead of shell.Run: the launcher manifest requires
            // administrator, and this script runs unelevated, so a direct start would be
            // blocked by UAC (invisible when running hidden). A one-shot task with
            // /RL HIGHEST is started by the Task Scheduler service with the admin token,
            // so no UAC prompt and the tray really comes back.
            builder.AppendLine("W 'restarting launcher via scheduled task (no browser, keep DSH running)'");
            builder.AppendLine("$task = 'DeepSeekHarnessUpdateRestart'");
            builder.AppendLine("$tr = '\"' + $launcher + '\" --no-browser --updated=' + $newVersion");
            builder.AppendLine("schtasks.exe /delete /tn $task /f 2>&1 | Out-Null");
            builder.AppendLine("$out = schtasks.exe /create /tn $task /tr $tr /sc once /st 23:59 /it /f /rl highest 2>&1");
            builder.AppendLine("W ('create task: ' + ($out -join ' '))");
            builder.AppendLine("$out2 = schtasks.exe /run /tn $task 2>&1");
            builder.AppendLine("W ('run task: ' + ($out2 -join ' '))");
            builder.AppendLine("Start-Sleep -Seconds 3");
            builder.AppendLine("schtasks.exe /delete /tn $task /f 2>&1 | Out-Null");
            builder.AppendLine("W 'restart command issued'");
            builder.AppendLine("W 'done'");
            return builder.ToString();
        }

        // ---------------------------------------------------------------- HTTP / 文件

        private static string HttpGetText(string url, int timeoutMs)
        {
            using (TimeoutWebClient client = new TimeoutWebClient(timeoutMs))
            {
                client.Headers[HttpRequestHeader.UserAgent] = "DSH-Launcher/" + Constants.Version;
                return client.DownloadString(url);
            }
        }

        private static string DownloadWithFallback(List<string> urls, string targetPath, Action<long, long> progress)
        {
            List<string> failures = new List<string>();
            for (int index = 0; index < urls.Count; index++)
            {
                try
                {
                    using (TimeoutWebClient client = new TimeoutWebClient(DownloadTimeoutMs))
                    {
                        client.Headers[HttpRequestHeader.UserAgent] = "DSH-Launcher/" + Constants.Version;
                        if (progress != null)
                        {
                            client.DownloadProgressChanged += delegate(object sender, DownloadProgressChangedEventArgs args)
                            {
                                progress(args.BytesReceived, args.TotalBytesToReceive);
                            };
                        }

                        client.DownloadFile(urls[index], targetPath);
                    }

                    return urls[index];
                }
                catch (Exception exception)
                {
                    failures.Add(urls[index] + " -> " + exception.Message);
                    InstallLoggerLight("下载源失败: " + urls[index] + " : " + exception.Message);
                }
            }

            throw new InvalidOperationException("所有下载源都失败:\r\n" + string.Join("\r\n", failures.ToArray()));
        }

        public static string ComputeSha256(string path)
        {
            try
            {
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                using (FileStream stream = File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(stream);
                    StringBuilder builder = new StringBuilder(hash.Length * 2);
                    for (int index = 0; index < hash.Length; index++)
                    {
                        builder.Append(hash[index].ToString("x2"));
                    }

                    return builder.ToString();
                }
            }
            catch
            {
                return null;
            }
        }

        private static string ResolvePowerShell()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    @"System32\WindowsPowerShell\v1.0\powershell.exe");
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
            }

            return "powershell.exe";
        }

        private static string Quote(string value)
        {
            if (value == null)
            {
                return "''";
            }

            return "'" + value.Replace("'", "''") + "'";
        }

        private static void safeDeleteDirectory(string path)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch
            {
            }
        }

        private static void InstallLoggerLight(string message)
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "launcher.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [update] " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        /// <summary>WebClient 的同步下载没法设超时,套一层。</summary>
        private sealed class TimeoutWebClient : WebClient
        {
            private readonly int _timeoutMs;

            public TimeoutWebClient(int timeoutMs)
            {
                _timeoutMs = timeoutMs;
            }

            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);
                if (request != null)
                {
                    request.Timeout = _timeoutMs;
                    HttpWebRequest http = request as HttpWebRequest;
                    if (http != null)
                    {
                        http.ReadWriteTimeout = _timeoutMs;
                    }
                }

                return request;
            }
        }
    }
}
