using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 跑一段 PowerShell 拿结果。
    ///
    /// 用系统自带的 Windows PowerShell 5.1(路径固定),不依赖用户装没装 pwsh。
    /// 脚本从标准输入喂进去,避开命令行转义地狱;输出以 UTF-8 收。
    /// </summary>
    internal static class PowerShellRunner
    {
        private static readonly string Executable = ResolveExecutable();

        private static string ResolveExecutable()
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

        /// <summary>执行脚本,返回标准输出。失败返回 null。</summary>
        public static string Run(string script, int timeoutMs)
        {
            if (string.IsNullOrEmpty(script))
            {
                return null;
            }

            try
            {
                ProcessStartInfo info = new ProcessStartInfo
                {
                    FileName = Executable,
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                using (Process process = Process.Start(info))
                {
                    if (process == null)
                    {
                        return null;
                    }

                    // 用 UTF-8 写脚本,配合 -Command - 让 PS 从 stdin 读
                    byte[] payload = Encoding.UTF8.GetBytes(script);
                    process.StandardInput.BaseStream.Write(payload, 0, payload.Length);
                    process.StandardInput.BaseStream.Flush();
                    process.StandardInput.Close();

                    StringBuilder stdout = new StringBuilder();
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
                    {
                        if (args.Data != null)
                        {
                            lock (stdout)
                            {
                                stdout.AppendLine(args.Data);
                            }
                        }
                    };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        return null;
                    }

                    process.WaitForExit();

                    lock (stdout)
                    {
                        return stdout.ToString();
                    }
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
