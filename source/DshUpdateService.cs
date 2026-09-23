using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DeepSeekHarnessLauncher
{
    internal sealed class DshUpdatePackage
    {
        public string Version { get; set; }
        public string TarballUrl { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public Dictionary<string, DateTimeOffset> PublishedTimes { get; } =
            new Dictionary<string, DateTimeOffset>(
                StringComparer.OrdinalIgnoreCase);
    }

    internal static class DshUpdateService
    {
        private const string PackageName = "@deepseek-ai/dsh";
        private const int DownloadTimeoutMs = 600000;
        private const int DownloadBufferSize = 81920;
        private const double ExpectedDshSizeMb = 230.0;
        private const string AcceleratedRegistry =
            "https://registry.npmmirror.com/@deepseek-ai/dsh";
        private const string OfficialRegistry =
            "https://registry.npmjs.org/@deepseek-ai%2Fdsh";

        internal static string GetInstalledVersion(string dshRoot)
        {
            try
            {
                string packagePath = Path.Combine(
                    dshRoot,
                    @"node_modules\@deepseek-ai\dsh\package.json");
                if (!File.Exists(packagePath))
                {
                    return String.Empty;
                }

                using (JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(packagePath, Encoding.UTF8)))
                {
                    JsonElement version;
                    return document.RootElement.TryGetProperty(
                            "version",
                            out version)
                        && version.ValueKind == JsonValueKind.String
                            ? version.GetString()
                            : String.Empty;
                }
            }
            catch
            {
                return String.Empty;
            }
        }

        internal static string FetchLatestVersion(
            LauncherSettings settings,
            out string error)
        {
            DshUpdatePackage package = FetchLatestPackage(settings, out error);
            return package == null ? null : package.Version;
        }

        internal static DshUpdatePackage FetchLatestPackage(
            LauncherSettings settings,
            out string error)
        {
            error = null;
            string url = settings != null
                && String.Equals(
                    settings.UpdateSource,
                    "Official",
                    StringComparison.OrdinalIgnoreCase)
                    ? OfficialRegistry
                    : AcceleratedRegistry;

            try
            {
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12;
                HttpWebRequest request =
                    (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Accept = "application/json";
                request.UserAgent = Constants.UserAgent;
                ProxySupport.Apply(request);
                request.Timeout = 20000;
                request.ReadWriteTimeout = 20000;

                using (WebResponse response = request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(
                    stream,
                    Encoding.UTF8))
                {
                    using (JsonDocument document = JsonDocument.Parse(
                        reader.ReadToEnd()))
                    {
                        JsonElement distTags;
                        JsonElement versions;
                        if (!document.RootElement.TryGetProperty(
                                "dist-tags",
                                out distTags)
                            || distTags.ValueKind != JsonValueKind.Object
                            || !distTags.TryGetProperty(
                                "latest",
                                out JsonElement latestElement)
                            || latestElement.ValueKind != JsonValueKind.String
                            || !document.RootElement.TryGetProperty(
                                "versions",
                                out versions)
                            || versions.ValueKind != JsonValueKind.Object)
                        {
                            error = "DSH 更新源没有返回完整包信息。";
                            return null;
                        }

                        string latest = latestElement.GetString();
                        JsonElement latestPackage;
                        if (String.IsNullOrWhiteSpace(latest)
                            || !versions.TryGetProperty(
                                latest,
                                out latestPackage)
                            || latestPackage.ValueKind != JsonValueKind.Object
                            || !latestPackage.TryGetProperty(
                                "dist",
                                out JsonElement dist)
                            || dist.ValueKind != JsonValueKind.Object)
                        {
                            error = "DSH 更新源没有返回最新版包信息。";
                            return null;
                        }

                        JsonElement tarball;
                        if (!dist.TryGetProperty("tarball", out tarball)
                            || tarball.ValueKind != JsonValueKind.String
                            || String.IsNullOrWhiteSpace(tarball.GetString()))
                        {
                            error = "DSH 更新源没有返回安装包地址。";
                            return null;
                        }

                        DshUpdatePackage package = new DshUpdatePackage
                        {
                            Version = latest,
                            TarballUrl = tarball.GetString()
                        };
                        ReadPublishedTimes(
                            document.RootElement,
                            package);
                        return package;
                    }
                }

            }
            catch (Exception exception)
            {
                error = "DSH 更新检查失败：" + exception.Message;
            }

            return null;
        }

        internal static bool IsNewer(
            DshUpdatePackage package,
            string installedVersion)
        {
            if (package == null
                || String.IsNullOrWhiteSpace(package.Version))
            {
                return false;
            }

            if (String.IsNullOrWhiteSpace(installedVersion))
            {
                return true;
            }

            DateTimeOffset remotePublishedAt;
            DateTimeOffset localPublishedAt;
            if (package.PublishedAt.HasValue
                && package.PublishedTimes.TryGetValue(
                    installedVersion,
                    out localPublishedAt))
            {
                remotePublishedAt = package.PublishedAt.Value;
                if (remotePublishedAt > localPublishedAt)
                {
                    return true;
                }

                if (remotePublishedAt < localPublishedAt)
                {
                    return false;
                }
            }

            return UpdateSupport.IsNewer(
                package.Version,
                installedVersion);
        }

        internal static DateTimeOffset? GetPublishedAt(
            DshUpdatePackage package,
            string version)
        {
            DateTimeOffset publishedAt;
            if (package != null
                && package.PublishedTimes.TryGetValue(
                    version ?? String.Empty,
                    out publishedAt))
            {
                return publishedAt;
            }

            return null;
        }

        private static void ReadPublishedTimes(
            JsonElement root,
            DshUpdatePackage package)
        {
            JsonElement times;
            if (!root.TryGetProperty("time", out times)
                || times.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (JsonProperty property in times.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                DateTimeOffset publishedAt;
                if (!DateTimeOffset.TryParse(
                        property.Value.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal
                            | DateTimeStyles.AdjustToUniversal,
                        out publishedAt))
                {
                    continue;
                }

                package.PublishedTimes[property.Name] = publishedAt;
                if (String.Equals(
                    property.Name,
                    package.Version,
                    StringComparison.OrdinalIgnoreCase))
                {
                    package.PublishedAt = publishedAt;
                }
            }
        }

        internal static bool DownloadPackage(
            DshUpdatePackage package,
            Action<long, long> progress,
            out string packagePath,
            out string error)
        {
            packagePath = null;
            error = null;
            if (package == null
                || String.IsNullOrWhiteSpace(package.Version)
                || String.IsNullOrWhiteSpace(package.TarballUrl))
            {
                error = "DSH 更新包信息不完整。";
                return false;
            }

            string stagingDirectory = Path.Combine(
                Path.GetTempPath(),
                "DeepSeekHarnessUpdate",
                "dsh");
            string targetPath = Path.Combine(
                stagingDirectory,
                "dsh-" + SanitizeFileName(package.Version) + ".tgz");

            try
            {
                Directory.CreateDirectory(stagingDirectory);
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12;
                long total = GetRemoteContentLength(package.TarballUrl);
                using (TimeoutWebClient client =
                    new TimeoutWebClient(DownloadTimeoutMs))
                {
                    client.Headers[HttpRequestHeader.UserAgent] =
                        Constants.UserAgent;
                    ProxySupport.Apply(client);
                    using (Stream source = client.OpenRead(package.TarballUrl))
                    using (FileStream target = new FileStream(
                        targetPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None))
                    {
                        if (total <= 0)
                        {
                            string contentLength =
                                client.ResponseHeaders[
                                    HttpRequestHeader.ContentLength.ToString()];
                            if (!String.IsNullOrWhiteSpace(contentLength))
                            {
                                Int64.TryParse(contentLength, out total);
                            }
                        }

                        if (progress != null)
                        {
                            progress(0, total);
                        }

                        byte[] buffer = new byte[DownloadBufferSize];
                        long received = 0;
                        int read;
                        while ((read = source.Read(
                                buffer,
                                0,
                                buffer.Length)) > 0)
                        {
                            target.Write(buffer, 0, read);
                            received += read;
                            if (progress != null)
                            {
                                progress(received, total);
                            }
                        }

                        if (progress != null && total <= 0)
                        {
                            progress(received, received);
                        }
                    }
                }

                packagePath = targetPath;
                return true;
            }
            catch (Exception exception)
            {
                TryDeleteFile(targetPath);
                error = "DSH 更新包下载失败：" + exception.Message;
                return false;
            }
        }

        private static long GetRemoteContentLength(string url)
        {
            try
            {
                HttpWebRequest request =
                    (HttpWebRequest)WebRequest.Create(url);
                request.Method = "HEAD";
                request.UserAgent = Constants.UserAgent;
                ProxySupport.Apply(request);
                request.Timeout = 20000;
                request.ReadWriteTimeout = 20000;
                request.AllowAutoRedirect = true;
                using (WebResponse response = request.GetResponse())
                {
                    return response.ContentLength;
                }
            }
            catch
            {
                return 0;
            }
        }

        internal static bool InstallVersion(
            string dshRoot,
            string nodePath,
            string version,
            out string error)
        {
            return InstallVersion(
                dshRoot,
                nodePath,
                version,
                null,
                out error);
        }

        internal static bool InstallVersion(
            string dshRoot,
            string nodePath,
            string version,
            Action<string, double> progress,
            out string error)
        {
            if (String.IsNullOrWhiteSpace(version))
            {
                error = "DSH 更新参数不完整。";
                return false;
            }

            return RunNpmInstall(
                dshRoot,
                nodePath,
                PackageName + "@" + version,
                progress,
                out error);
        }

        internal static bool InstallPackage(
            string dshRoot,
            string nodePath,
            string packagePath,
            out string error)
        {
            return InstallPackage(
                dshRoot,
                nodePath,
                packagePath,
                null,
                out error);
        }

        internal static bool InstallPackage(
            string dshRoot,
            string nodePath,
            string packagePath,
            Action<string, double> progress,
            out string error)
        {
            if (String.IsNullOrWhiteSpace(packagePath)
                || !File.Exists(packagePath))
            {
                error = "DSH 更新包不存在。";
                return false;
            }

            return RunNpmInstall(
                dshRoot,
                nodePath,
                packagePath,
                progress,
                out error);
        }

        private static bool RunNpmInstall(
            string dshRoot,
            string nodePath,
            string packageSpec,
            Action<string, double> progress,
            out string error)
        {
            error = null;
            if (String.IsNullOrWhiteSpace(dshRoot)
                || String.IsNullOrWhiteSpace(nodePath)
                || String.IsNullOrWhiteSpace(packageSpec))
            {
                error = "DSH 更新参数不完整。";
                return false;
            }

            string nodeDirectory =
                Path.GetDirectoryName(nodePath) ?? String.Empty;
            string npmCli = FindNpmCli(nodeDirectory, dshRoot);
            string npmCmd = FindNpmCmd(nodeDirectory, dshRoot);

            string fileName;
            string arguments;
            string npmDirectory = null;
            string installArguments = " install "
                + Quote(packageSpec)
                + " --prefix " + Quote(dshRoot)
                + " --no-audit --no-fund --progress=true --loglevel=http";
            if (!String.IsNullOrWhiteSpace(npmCli))
            {
                fileName = nodePath;
                arguments = Quote(npmCli) + installArguments;
            }
            else if (!String.IsNullOrWhiteSpace(npmCmd))
            {
                fileName = "cmd.exe";
                arguments = "/d /s /c \""
                    + Quote(npmCmd)
                    + installArguments
                    + "\"";
                npmDirectory = Path.GetDirectoryName(npmCmd);
            }
            else
            {
                // 最后一层交给 cmd 自己按 PATH 解析 npm.cmd。
                fileName = "cmd.exe";
                arguments = "/d /s /c \"npm.cmd"
                    + installArguments
                    + "\"";
            }

            try
            {
                Encoding consoleEncoding = GetConsoleEncoding();
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = dshRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = consoleEncoding,
                    StandardErrorEncoding = consoleEncoding
                };
                SetPath(startInfo, nodeDirectory, npmDirectory);

                using (Process process = Process.Start(startInfo))
                using (NpmInstallProgress tracker =
                    new NpmInstallProgress(dshRoot, progress))
                {
                    StringBuilder standardOutput = new StringBuilder();
                    StringBuilder standardError = new StringBuilder();
                    object outputLock = new object();
                    process.OutputDataReceived += delegate(
                        object sender,
                        DataReceivedEventArgs args)
                    {
                        if (args.Data == null)
                        {
                            return;
                        }

                        lock (outputLock)
                        {
                            standardOutput.AppendLine(args.Data);
                        }

                        tracker.OnLine(args.Data);
                    };
                    process.ErrorDataReceived += delegate(
                        object sender,
                        DataReceivedEventArgs args)
                    {
                        if (args.Data == null)
                        {
                            return;
                        }

                        lock (outputLock)
                        {
                            standardError.AppendLine(args.Data);
                        }
                    };

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(600000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }

                        error = "DSH 更新安装超时。";
                        return false;
                    }

                    process.WaitForExit();

                    string stdoutText;
                    string stderrText;
                    lock (outputLock)
                    {
                        stdoutText = standardOutput.ToString();
                        stderrText = standardError.ToString();
                    }

                    if (process.ExitCode != 0)
                    {
                        error = "DSH 更新安装失败（退出码 "
                            + process.ExitCode.ToString(
                                CultureInfo.InvariantCulture)
                            + "）："
                            + (String.IsNullOrWhiteSpace(stderrText)
                                ? stdoutText
                                : stderrText);
                        return false;
                    }

                    tracker.Complete();
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "DSH 更新安装失败：" + exception.Message;
                return false;
            }
        }

        private sealed class NpmInstallProgress : IDisposable
        {
            private readonly Action<string, double> _report;
            private readonly string _modulesDirectory;
            private readonly DateTime _startedUtc = DateTime.UtcNow;
            private readonly object _gate = new object();
            private readonly Timer _timer;
            private double _lastPercent;
            private string _lastDetail = String.Empty;
            private volatile bool _completed;

            public NpmInstallProgress(
                string dshRoot,
                Action<string, double> report)
            {
                _report = report;
                _modulesDirectory = Path.Combine(
                    dshRoot ?? String.Empty,
                    "node_modules");
                if (_report != null)
                {
                    _timer = new Timer(
                        delegate { Publish(false); },
                        null,
                        500,
                        750);
                    Publish(true);
                }
            }

            public void OnLine(string line)
            {
                if (String.IsNullOrWhiteSpace(line) || _completed)
                {
                    return;
                }

                if (line.IndexOf(
                        "npm info ok",
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf(
                        "added ",
                        StringComparison.OrdinalIgnoreCase) >= 0
                        && line.IndexOf(
                            " packages",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Publish(true, "正在完成安装", 97);
                    return;
                }

                Publish(false);
            }

            public void Complete()
            {
                Publish(true, "安装完成", 100);
            }

            public void Dispose()
            {
                if (_timer != null)
                {
                    _timer.Dispose();
                }
            }

            private void Publish(
                bool force,
                string detailOverride = null,
                double percentOverride = -1)
            {
                if (_report == null || _completed)
                {
                    return;
                }

                double elapsedSeconds =
                    Math.Max(0.0, (DateTime.UtcNow - _startedUtc).TotalSeconds);
                double sizeMb = DirectorySizeMb(_modulesDirectory);
                double sizeProgress =
                    5.0 + Math.Min(0.95, sizeMb / ExpectedDshSizeMb) * 90.0;
                double timeProgress =
                    5.0 + 90.0 * (1.0 - Math.Exp(-elapsedSeconds / 90.0));
                double percent = percentOverride >= 0
                    ? percentOverride
                    : Math.Max(sizeProgress, timeProgress);
                if (percent > 99.0 && percentOverride < 0)
                {
                    percent = 99.0;
                }

                string detail = detailOverride;
                if (String.IsNullOrWhiteSpace(detail))
                {
                    detail = sizeMb < 0.5
                        ? "正在初始化 npm，可能需要一些时间"
                        : "正在部署 DSH 核心 "
                            + sizeMb.ToString(
                                "0.0",
                                CultureInfo.InvariantCulture)
                            + " / "
                            + ExpectedDshSizeMb.ToString(
                                "0",
                                CultureInfo.InvariantCulture)
                            + " MB";
                }

                lock (_gate)
                {
                    if (_completed)
                    {
                        return;
                    }

                    if (percentOverride >= 100)
                    {
                        _completed = true;
                    }

                    if (!force
                        && percent - _lastPercent < 0.5
                        && String.Equals(
                            detail,
                            _lastDetail,
                            StringComparison.Ordinal))
                    {
                        return;
                    }

                    _lastPercent = percent;
                    _lastDetail = detail;
                }

                _report(detail, percent);
            }

            private static double DirectorySizeMb(string directory)
            {
                if (String.IsNullOrWhiteSpace(directory)
                    || !Directory.Exists(directory))
                {
                    return 0.0;
                }

                try
                {
                    long bytes = 0;
                    string[] files = Directory.GetFiles(
                        directory,
                        "*",
                        SearchOption.AllDirectories);
                    for (int index = 0; index < files.Length; index++)
                    {
                        try
                        {
                            bytes += new FileInfo(files[index]).Length;
                        }
                        catch
                        {
                        }
                    }

                    return bytes / 1048576.0;
                }
                catch
                {
                    return 0.0;
                }
            }
        }

        private static string FindNpmCli(
            string nodeDirectory,
            string dshRoot)
        {
            string[] candidates = new string[]
            {
                Path.Combine(
                    nodeDirectory,
                    @"node_modules\npm\bin\npm-cli.js"),
                Path.Combine(
                    nodeDirectory,
                    @"..\node_modules\npm\bin\npm-cli.js"),
                Path.Combine(
                    nodeDirectory,
                    @"..\lib\node_modules\npm\bin\npm-cli.js"),
                Path.Combine(
                    nodeDirectory,
                    @"..\..\lib\node_modules\npm\bin\npm-cli.js"),
                Path.Combine(
                    dshRoot,
                    @"node_modules\npm\bin\npm-cli.js"),
                Path.Combine(
                    dshRoot,
                    @"node_modules\node\node_modules\npm\bin\npm-cli.js"),
                Path.Combine(
                    dshRoot,
                    @"node_modules\node\lib\node_modules\npm\bin\npm-cli.js")
            };

            for (int index = 0; index < candidates.Length; index++)
            {
                string candidate = Path.GetFullPath(candidates[index]);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string FindNpmCmd(
            string nodeDirectory,
            string dshRoot)
        {
            string[] candidates = new string[]
            {
                Path.Combine(nodeDirectory, "npm.cmd"),
                Path.Combine(nodeDirectory, @"..\npm.cmd"),
                Path.Combine(dshRoot, @"node_modules\.bin\npm.cmd")
            };

            for (int index = 0; index < candidates.Length; index++)
            {
                string candidate = Path.GetFullPath(candidates[index]);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return FindOnPath("npm.cmd");
        }

        private static string FindOnPath(string fileName)
        {
            string path = Environment.GetEnvironmentVariable("Path");
            if (String.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string[] directories = path.Split(';');
            for (int index = 0; index < directories.Length; index++)
            {
                string directory = directories[index].Trim().Trim('"');
                if (directory.Length == 0)
                {
                    continue;
                }

                try
                {
                    directory = Environment.ExpandEnvironmentVariables(
                        directory);
                    string candidate = Path.Combine(directory, fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static Encoding GetConsoleEncoding()
        {
            try
            {
                return Encoding.GetEncoding(
                    CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch
            {
                return Encoding.Default;
            }
        }

        private static string SanitizeFileName(string value)
        {
            StringBuilder builder = new StringBuilder(value ?? String.Empty);
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int index = 0; index < builder.Length; index++)
            {
                if (Array.IndexOf(invalid, builder[index]) >= 0)
                {
                    builder[index] = '_';
                }
            }

            return builder.ToString();
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!String.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void SetPath(
            ProcessStartInfo startInfo,
            string nodeDirectory,
            string npmDirectory)
        {
            string existing = startInfo.Environment["Path"];
            string prefix = nodeDirectory ?? String.Empty;
            if (!String.IsNullOrWhiteSpace(npmDirectory)
                && !String.Equals(
                    nodeDirectory,
                    npmDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                prefix = npmDirectory
                    + ";"
                    + prefix;
            }

            startInfo.Environment["Path"] =
                prefix.Trim(';')
                + ";"
                + (existing ?? String.Empty);
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? String.Empty).Replace("\"", "\\\"") + "\"";
        }

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
                        http.AllowAutoRedirect = true;
                    }
                }

                return request;
            }
        }
    }
}
