using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 找 DSH 根目录和 node.exe。
    ///
    /// 历史上这里写死了开发机路径(G:\DeepSeek DSH / E:\Nodejs\node.exe),
    /// 结果装到别人机器上时,只要目录不同就会去开一个不存在的路径。
    /// 现在按下面的顺序找,写死的路径只当最后的兜底:
    ///
    /// 1. 环境变量 DSH_ROOT / DSH_NODE(安装器建快捷方式时会写进去)
    /// 2. 同目录的 launcher.json(安装器落地的一份配置)
    /// 3. 从自己所在目录往上找 node_modules\@deepseek-ai\dsh\lib\bin.js
    /// 4. 常见位置扫一圈
    /// 5. 注册表里上一版启动器记下的路径
    /// </summary>
    internal static class LauncherLocator
    {
        private const string BinRelative = @"node_modules\@deepseek-ai\dsh\lib\bin.js";
        private const string ConfigFileName = "launcher.json";
        private const string StateFileName = "launcher-path.txt";
        private const int MaxWalkUpLevels = 4;

        private static string _cachedRoot;

        /// <summary>DSH 根目录。找不到返回 null。</summary>
        public static string FindRoot()
        {
            if (_cachedRoot != null)
            {
                return _cachedRoot;
            }

            List<string> candidates = new List<string>();

            string fromEnvironment = Environment.GetEnvironmentVariable("DSH_ROOT");
            AddIfValid(candidates, fromEnvironment);

            AddIfValid(candidates, ReadConfigValue("dshRoot"));
            AddIfValid(candidates, ReadStateFile());

            // 从自己所在目录往上找
            try
            {
                string current = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                for (int level = 0; level < MaxWalkUpLevels && !string.IsNullOrEmpty(current); level++)
                {
                    AddIfValid(candidates, current);
                    DirectoryInfo parent = Directory.GetParent(current);
                    if (parent == null)
                    {
                        break;
                    }

                    current = parent.FullName.TrimEnd('\\');
                }
            }
            catch
            {
            }

            // 常见位置
            foreach (string drive in EnumerateDrives())
            {
                AddIfValid(candidates, Path.Combine(drive, "DeepSeek DSH"));
                AddIfValid(candidates, Path.Combine(drive, "DeepSeek Harness"));
            }

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
            {
                AddIfValid(candidates, Path.Combine(profile, "DeepSeek DSH"));
            }

            if (candidates.Count > 0)
            {
                _cachedRoot = candidates[0];
                return _cachedRoot;
            }

            return null;
        }

        /// <summary>node.exe。1) 环境变量 2) 配置 3) DSH 目录里的 node_modules\node 4) PATH 5) 常见位置。</summary>
        public static string FindNode()
        {
            string fromEnvironment = Environment.GetEnvironmentVariable("DSH_NODE");
            if (!string.IsNullOrEmpty(fromEnvironment) && File.Exists(fromEnvironment))
            {
                return fromEnvironment;
            }

            string fromConfig = ReadConfigValue("nodePath");
            if (!string.IsNullOrEmpty(fromConfig) && File.Exists(fromConfig))
            {
                return fromConfig;
            }

            // DSH 自己那份 node 依赖(npm 装的 node 包里有 node.exe)
            string root = FindRoot();
            if (!string.IsNullOrEmpty(root))
            {
                string bundled = Path.Combine(root, @"node_modules\node\bin\node.exe");
                if (File.Exists(bundled))
                {
                    return bundled;
                }
            }

            string pathValue = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathValue))
            {
                string[] directories = pathValue.Split(';');
                for (int index = 0; index < directories.Length; index++)
                {
                    string directory = directories[index].Trim().Trim('"');
                    if (directory.Length == 0)
                    {
                        continue;
                    }

                    try
                    {
                        string candidate = Path.Combine(directory, "node.exe");
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            string[] common =
            {
                @"C:\Program Files\nodejs\node.exe",
                @"C:\Program Files (x86)\nodejs\node.exe",
            };
            for (int index = 0; index < common.Length; index++)
            {
                if (File.Exists(common[index]))
                {
                    return common[index];
                }
            }

            // 注册表:安装器写过的路径
            string recorded = ReadRegistryValue("NodePath");
            if (!string.IsNullOrEmpty(recorded) && File.Exists(recorded))
            {
                return recorded;
            }

            return null;
        }

        /// <summary>把找到的路径记下来,下次省得再找。</summary>
        public static void Remember(string dshRoot, string nodePath)
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness");
                Directory.CreateDirectory(directory);
                File.WriteAllText(
                    Path.Combine(directory, StateFileName),
                    (dshRoot ?? string.Empty) + Environment.NewLine + (nodePath ?? string.Empty),
                    new System.Text.UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static string ReadStateFile()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness",
                    StateFileName);
                if (!File.Exists(path))
                {
                    return null;
                }

                string[] lines = File.ReadAllLines(path);
                if (lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[0]))
                {
                    return lines[0].Trim();
                }
            }
            catch
            {
            }

            return null;
        }

        /// <summary>读同目录 launcher.json 里的某个字符串字段(不引 JSON 库,正则够了)。</summary>
        private static string ReadConfigValue(string key)
        {
            try
            {
                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string path = Path.Combine(baseDirectory, ConfigFileName);
                if (!File.Exists(path))
                {
                    return null;
                }

                string text = File.ReadAllText(path);
                Match match = Regex.Match(
                    text,
                    "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?<value>(?:[^\"\\\\]|\\\\.)*)\"",
                    RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    return null;
                }

                return match.Groups["value"].Value.Replace("\\\\", "\\").Trim();
            }
            catch
            {
            }

            return null;
        }

        private static string ReadRegistryValue(string name)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\DeepSeekHarness"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue(name);
                        if (value != null)
                        {
                            return value.ToString();
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static void AddIfValid(List<string> candidates, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                string trimmed = path.Trim().TrimEnd('\\');
                if (trimmed.Length == 0)
                {
                    return;
                }

                if (!File.Exists(Path.Combine(trimmed, BinRelative)))
                {
                    return;
                }

                if (!candidates.Contains(trimmed))
                {
                    candidates.Add(trimmed);
                }
            }
            catch
            {
            }
        }

        private static IEnumerable<string> EnumerateDrives()
        {
            List<string> drives = new List<string>();
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                        {
                            drives.Add(drive.RootDirectory.FullName.TrimEnd('\\'));
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return drives;
        }
    }
}
