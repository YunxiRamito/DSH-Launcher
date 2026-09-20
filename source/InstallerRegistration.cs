using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// Keeps the installer's uninstall metadata in sync when the launcher's
    /// DSH directory changes. The launcher never creates or deletes the
    /// uninstall registration; it only updates an existing one.
    /// </summary>
    internal static class InstallerRegistration
    {
        private const string RegistryUninstallKey =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DeepSeekHarness";

        internal static void SynchronizeDshRoot(string dshRoot)
        {
            if (String.IsNullOrWhiteSpace(dshRoot))
            {
                return;
            }

            UpdateInstallerState(dshRoot);
            UpdateUninstallRegistry(dshRoot);
        }

        private static void UpdateInstallerState(string dshRoot)
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness",
                    "installer-state.json");
                if (!File.Exists(path))
                {
                    return;
                }

                JsonNode node = JsonNode.Parse(
                    File.ReadAllText(path, Encoding.UTF8));
                if (node is not JsonObject root)
                {
                    return;
                }

                root["DshRoot"] = dshRoot;
                string temporaryPath = path + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    root.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }),
                    new UTF8Encoding(false));
                File.Replace(temporaryPath, path, null);
            }
            catch
            {
            }
        }

        private static void UpdateUninstallRegistry(string dshRoot)
        {
            if (TryUpdateRegistry(
                Registry.LocalMachine,
                dshRoot))
            {
                return;
            }

            TryUpdateRegistry(Registry.CurrentUser, dshRoot);
        }

        private static bool TryUpdateRegistry(
            RegistryKey root,
            string dshRoot)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(
                    RegistryUninstallKey,
                    true))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    key.SetValue(
                        "DshRoot",
                        dshRoot,
                        RegistryValueKind.String);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
