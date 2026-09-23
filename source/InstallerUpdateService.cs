using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DeepSeekHarnessLauncher
{
    internal sealed class InstallerUpdatePackage
    {
        public string Version { get; set; }
        public string SetupUrl { get; set; }
        public string Sha256 { get; set; }
    }

    /// <summary>
    /// Keeps the launcher and the installer/uninstaller on the same version.
    ///
    /// The setup EXE is a bootstrapper with the installer payload appended as a
    /// ZIP (and, in signed builds, an Authenticode certificate appended after
    /// that ZIP). We verify the release hash, extract the embedded payload,
    /// replace the installed copies, and only then allow the launcher update.
    /// </summary>
    internal static class InstallerUpdateService
    {
        private const string Repository =
            "YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Setup";
        private const string SetupAssetName =
            "DSH-Installer-Setup.exe";
        private const string InstallerFolderName = ".installer";
        private const string InstallerExeName = "DSH-Installer.exe";
        private const string UninstallerExeName = "DSH-Uninstall.exe";
        private const int DownloadTimeoutMs = 300000;

        internal static InstallerUpdatePackage FetchLatestPackage(
            LauncherSettings settings,
            string requiredVersion,
            out string error)
        {
            error = null;
            string api = "https://api.github.com/repos/"
                + Repository + "/releases/latest";
            try
            {
                string json = DownloadText(settings, api);
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;
                    JsonElement tag;
                    JsonElement assets;
                    if (!root.TryGetProperty("tag_name", out tag)
                        || tag.ValueKind != JsonValueKind.String
                        || !root.TryGetProperty("assets", out assets)
                        || assets.ValueKind != JsonValueKind.Array)
                    {
                        error = "安装器 Release 信息不完整。";
                        return null;
                    }

                    string version = (tag.GetString() ?? String.Empty)
                        .TrimStart('v', 'V');
                    if (!String.Equals(
                            version,
                            requiredVersion,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        error = "安装器 v" + version
                            + " 与启动器目标版本 v" + requiredVersion
                            + " 不一致，已停止启动器更新。";
                        return null;
                    }

                    InstallerUpdatePackage package =
                        new InstallerUpdatePackage
                        {
                            Version = version
                        };
                    foreach (JsonElement asset in assets.EnumerateArray())
                    {
                        JsonElement name;
                        JsonElement url;
                        if (!asset.TryGetProperty("name", out name)
                            || name.ValueKind != JsonValueKind.String
                            || !asset.TryGetProperty("browser_download_url", out url)
                            || url.ValueKind != JsonValueKind.String
                            || !String.Equals(
                                name.GetString(),
                                SetupAssetName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        package.SetupUrl = url.GetString();
                        JsonElement digest;
                        if (asset.TryGetProperty("digest", out digest)
                            && digest.ValueKind == JsonValueKind.String)
                        {
                            string value = digest.GetString() ?? String.Empty;
                            int separator = value.IndexOf(':');
                            package.Sha256 = separator >= 0
                                ? value.Substring(separator + 1)
                                : value;
                        }

                        break;
                    }

                    if (String.IsNullOrWhiteSpace(package.SetupUrl))
                    {
                        error = "安装器 Release 中没有 "
                            + SetupAssetName + " 资产。";
                        return null;
                    }

                    return package;
                }
            }
            catch (Exception exception)
            {
                error = "获取安装器更新信息失败：" + exception.Message;
                return null;
            }
        }

        internal static bool PrepareAndApply(
            InstallerUpdatePackage package,
            string dshRoot,
            Action<long, long> progress,
            out string error)
        {
            error = null;
            string stagingRoot = Path.Combine(
                Path.GetTempPath(),
                "DeepSeekHarnessUpdate",
                "installer");
            string setupPath = Path.Combine(
                stagingRoot,
                SetupAssetName);
            string payloadPath = Path.Combine(
                stagingRoot,
                "payload");

            try
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, true);
                }

                Directory.CreateDirectory(stagingRoot);
                DownloadFile(package.SetupUrl, setupPath, progress);

                string actualHash = UpdateSupport.ComputeSha256(setupPath);
                if (!String.IsNullOrWhiteSpace(package.Sha256)
                    && !String.Equals(
                        actualHash,
                        package.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = "安装器安装包校验失败。";
                    return false;
                }

                string extractError;
                if (!ExtractAppendedZip(setupPath, payloadPath, out extractError))
                {
                    error = extractError;
                    return false;
                }

                string payloadInstaller = Path.Combine(
                    payloadPath,
                    InstallerExeName);
                string payloadUninstaller = Path.Combine(
                    payloadPath,
                    UninstallerExeName);
                if (!File.Exists(payloadInstaller)
                    || !File.Exists(payloadUninstaller))
                {
                    error = "安装器 payload 缺少安装器或卸载器。";
                    return false;
                }

                ReplaceDirectory(
                    payloadPath,
                    Path.Combine(dshRoot, InstallerFolderName));
                File.Copy(
                    payloadUninstaller,
                    Path.Combine(dshRoot, UninstallerExeName),
                    true);
                return true;
            }
            catch (Exception exception)
            {
                error = "更新安装器失败：" + exception.Message;
                return false;
            }
            finally
            {
                TryDeleteDirectory(stagingRoot);
            }
        }

        private static string DownloadText(
            LauncherSettings settings,
            string url)
        {
            using (TimeoutWebClient client =
                new TimeoutWebClient(20000))
            {
                client.Headers[HttpRequestHeader.UserAgent] =
                    Constants.UserAgent;
                client.Headers[HttpRequestHeader.Accept] =
                    "application/vnd.github+json";
                ProxySupport.Apply(client);
                return client.DownloadString(url);
            }
        }

        private static void DownloadFile(
            string url,
            string target,
            Action<long, long> progress)
        {
            using (TimeoutWebClient client =
                new TimeoutWebClient(DownloadTimeoutMs))
            {
                client.Headers[HttpRequestHeader.UserAgent] =
                    Constants.UserAgent;
                ProxySupport.Apply(client);
                if (progress != null)
                {
                    client.DownloadProgressChanged +=
                        delegate(object sender, DownloadProgressChangedEventArgs args)
                        {
                            progress(
                                args.BytesReceived,
                                args.TotalBytesToReceive);
                        };
                }

                client.DownloadFile(url, target);
            }
        }

        private static bool ExtractAppendedZip(
            string setupPath,
            string destination,
            out string error)
        {
            error = null;
            string temporaryZip = setupPath + ".payload.zip";
            try
            {
                long start;
                long end;
                using (FileStream input = File.OpenRead(setupPath))
                {
                    start = FindZipStart(input, out end);
                    if (start < 0 || end <= start)
                    {
                        error = "安装器安装包中没有找到内嵌 payload。";
                        return false;
                    }

                    input.Seek(start, SeekOrigin.Begin);
                    using (FileStream output = File.Create(temporaryZip))
                    {
                        long remaining = end - start;
                        byte[] buffer = new byte[262144];
                        while (remaining > 0)
                        {
                            int wanted = (int)Math.Min(
                                buffer.Length,
                                remaining);
                            int read = input.Read(buffer, 0, wanted);
                            if (read <= 0)
                            {
                                error = "读取安装器 payload 时提前结束。";
                                return false;
                            }

                            output.Write(buffer, 0, read);
                            remaining -= read;
                        }
                    }
                }

                Directory.CreateDirectory(destination);
                using (ZipArchive archive =
                    ZipFile.OpenRead(temporaryZip))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string target = Path.GetFullPath(
                            Path.Combine(destination, entry.FullName));
                        string root = Path.GetFullPath(destination)
                            .TrimEnd(Path.DirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
                        if (!target.StartsWith(
                            root,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            error = "安装器 payload 含非法路径。";
                            return false;
                        }

                        if (String.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(target);
                            continue;
                        }

                        Directory.CreateDirectory(
                            Path.GetDirectoryName(target));
                        entry.ExtractToFile(target, true);
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "解包安装器 payload 失败："
                    + exception.Message;
                return false;
            }
            finally
            {
                TryDeleteFile(temporaryZip);
            }
        }

        private static long FindZipStart(
            FileStream file,
            out long zipEnd)
        {
            const int EocdMinSize = 22;
            const int MaxComment = 65535;
            const int MaxCertificateTable = 1024 * 1024;

            zipEnd = -1;
            long length = file.Length;
            int window = (int)Math.Min(
                length,
                EocdMinSize + MaxComment + MaxCertificateTable);
            byte[] buffer = new byte[window];
            file.Seek(length - window, SeekOrigin.Begin);
            int total = 0;
            while (total < window)
            {
                int read = file.Read(buffer, total, window - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            for (int index = total - EocdMinSize; index >= 0; index--)
            {
                if (buffer[index] != 0x50
                    || buffer[index + 1] != 0x4B
                    || buffer[index + 2] != 0x05
                    || buffer[index + 3] != 0x06)
                {
                    continue;
                }

                long centralSize =
                    BitConverter.ToUInt32(buffer, index + 12);
                long centralOffset =
                    BitConverter.ToUInt32(buffer, index + 16);
                int commentLength =
                    BitConverter.ToUInt16(buffer, index + 20);
                if (index + EocdMinSize + commentLength > total)
                {
                    continue;
                }

                long eocd = length - total + index;
                long start = eocd - centralSize - centralOffset;
                long end = eocd + EocdMinSize + commentLength;
                if (start < 0
                    || start >= length
                    || end <= start
                    || end > length)
                {
                    continue;
                }

                long saved = file.Position;
                file.Seek(start, SeekOrigin.Begin);
                int b0 = file.ReadByte();
                int b1 = file.ReadByte();
                int b2 = file.ReadByte();
                int b3 = file.ReadByte();
                file.Seek(saved, SeekOrigin.Begin);
                if (b0 != 0x50
                    || b1 != 0x4B
                    || b2 != 0x03
                    || b3 != 0x04)
                {
                    continue;
                }

                zipEnd = end;
                return start;
            }

            return -1;
        }

        private static void ReplaceDirectory(
            string source,
            string target)
        {
            string backup = target + ".old";
            TryDeleteDirectory(backup);
            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
            }

            try
            {
                Directory.CreateDirectory(target);
                CopyDirectory(source, target);
                TryDeleteDirectory(backup);
            }
            catch
            {
                TryDeleteDirectory(target);
                if (Directory.Exists(backup))
                {
                    Directory.Move(backup, target);
                }

                throw;
            }
        }

        private static void CopyDirectory(
            string source,
            string target)
        {
            string[] files = Directory.GetFiles(
                source,
                "*",
                SearchOption.AllDirectories);
            for (int index = 0; index < files.Length; index++)
            {
                string relative = files[index]
                    .Substring(source.Length)
                    .TrimStart(Path.DirectorySeparatorChar);
                string destination = Path.Combine(target, relative);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(destination));
                File.Copy(files[index], destination, true);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            if (String.IsNullOrWhiteSpace(path)
                || !Directory.Exists(path))
            {
                return;
            }

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(path, true);
                    return;
                }
                catch
                {
                    Thread.Sleep(250);
                }
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!String.IsNullOrWhiteSpace(path)
                    && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
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
