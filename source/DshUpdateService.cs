using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    internal static class DshUpdateService
    {
        private const string PackageName = "@deepseek-ai/dsh";
        private const string AcceleratedRegistry =
            "https://registry.npmmirror.com/@deepseek-ai/dsh/latest";
        private const string OfficialRegistry =
            "https://registry.npmjs.org/@deepseek-ai/dsh/latest";

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
                request.UserAgent = "DeepSeek-Harness-Launcher/1.4.0";
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
                        JsonElement version;
                        if (document.RootElement.TryGetProperty(
                                "version",
                                out version)
                            && version.ValueKind == JsonValueKind.String)
                        {
                            return version.GetString();
                        }
                    }
                }

                error = "DSH 更新源没有返回版本号。";
            }
            catch (Exception exception)
            {
                error = "DSH 更新检查失败：" + exception.Message;
            }

            return null;
        }

        internal static bool InstallVersion(
            string dshRoot,
            string nodePath,
            string version,
            out string error)
        {
            error = null;
            if (String.IsNullOrWhiteSpace(dshRoot)
                || String.IsNullOrWhiteSpace(nodePath)
                || String.IsNullOrWhiteSpace(version))
            {
                error = "DSH 更新参数不完整。";
                return false;
            }

            string nodeDirectory = Path.GetDirectoryName(nodePath);
            string npmCmd = Path.Combine(nodeDirectory, "npm.cmd");
            string npmCli = Path.Combine(
                nodeDirectory,
                @"node_modules\npm\bin\npm-cli.js");
            string rootNpmCli = Path.Combine(
                dshRoot,
                @"node_modules\npm\bin\npm-cli.js");

            string fileName;
            string arguments;
            if (File.Exists(npmCli))
            {
                fileName = nodePath;
                arguments = Quote(npmCli);
            }
            else if (File.Exists(rootNpmCli))
            {
                fileName = nodePath;
                arguments = Quote(rootNpmCli);
            }
            else if (File.Exists(npmCmd))
            {
                fileName = "cmd.exe";
                arguments = "/d /s /c " + Quote(npmCmd);
            }
            else
            {
                error = "没有找到 npm，无法安装 DSH 更新。";
                return false;
            }

            arguments += " install "
                + Quote(PackageName + "@" + version)
                + " --prefix " + Quote(dshRoot)
                + " --no-audit --no-fund";

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = dshRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                SetPath(startInfo, nodeDirectory);

                using (Process process = Process.Start(startInfo))
                {
                    string standardOutput = process.StandardOutput.ReadToEnd();
                    string standardError = process.StandardError.ReadToEnd();
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

                    if (process.ExitCode != 0)
                    {
                        error = "DSH 更新安装失败（退出码 "
                            + process.ExitCode.ToString(
                                CultureInfo.InvariantCulture)
                            + "）："
                            + (String.IsNullOrWhiteSpace(standardError)
                                ? standardOutput
                                : standardError);
                        return false;
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "DSH 更新安装失败：" + exception.Message;
                return false;
            }
        }

        private static void SetPath(
            ProcessStartInfo startInfo,
            string nodeDirectory)
        {
            string existing = startInfo.Environment["Path"];
            startInfo.Environment["Path"] =
                nodeDirectory + ";" + (existing ?? String.Empty);
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? String.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
