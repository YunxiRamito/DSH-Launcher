using System;
using System.Collections.Generic;

namespace DeepSeekHarnessLauncher
{
    internal sealed class PluginUpdateMatch
    {
        public DshProfilePlugin Plugin { get; set; }
        public PluginCatalogItem CatalogItem { get; set; }
        public PluginInstallRecord Record { get; set; }

        public string Key
        {
            get
            {
                return Plugin == null ? String.Empty : Plugin.Key;
            }
        }
    }

    internal sealed class PluginUpdateCheckResult
    {
        public List<PluginUpdateMatch> Updates { get; set; } =
            new List<PluginUpdateMatch>();
        public bool RateLimited { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// 对比 PluginInstalls.json 的 pushedAt 和在线目录的 pushedAt。
    /// 只有启动器自己安装且来源可识别的插件才会进入结果。
    /// </summary>
    internal static class PluginUpdateService
    {
        internal static PluginUpdateCheckResult Check(
            LauncherSettings settings,
            bool forceRefresh,
            Action<string> log)
        {
            PluginUpdateCheckResult result = new PluginUpdateCheckResult();
            if (settings == null || String.IsNullOrWhiteSpace(settings.DshRoot))
            {
                result.Error = "未配置 DSH 目录。";
                return result;
            }

            PluginCatalogService.CatalogResult catalog =
                PluginCatalogService.Load(settings, forceRefresh, log);
            result.RateLimited = catalog != null && catalog.RateLimited;
            if (catalog == null || catalog.Items.Count == 0)
            {
                result.Error = catalog == null
                    ? "插件目录为空。"
                    : (catalog.Error ?? "插件目录为空。");
                return result;
            }

            Dictionary<string, PluginInstallRecord> records =
                PluginInstallStore.Load();
            List<DshProfilePlugin> installed = DshProfileService.ReadPlugins(
                settings.DshRoot,
                records);
            for (int pluginIndex = 0;
                pluginIndex < installed.Count;
                pluginIndex++)
            {
                DshProfilePlugin plugin = installed[pluginIndex];
                if (plugin.Record == null)
                {
                    continue;
                }

                PluginCatalogItem item = FindCatalogItem(
                    catalog.Items,
                    plugin.Record);
                if (item == null
                    || !IsNewer(item.PushedAt, plugin.Record.PushedAt))
                {
                    continue;
                }

                result.Updates.Add(new PluginUpdateMatch
                {
                    Plugin = plugin,
                    CatalogItem = item,
                    Record = plugin.Record
                });
            }

            return result;
        }

        internal static PluginStoreService.InstallResult Install(
            LauncherSettings settings,
            PluginUpdateMatch match,
            Action<string, double> progress,
            Action<string> log)
        {
            if (match == null || match.CatalogItem == null || match.Record == null)
            {
                return new PluginStoreService.InstallResult
                {
                    Error = "更新参数不完整。"
                };
            }

            PluginCatalogItem item = match.CatalogItem;
            PluginSpec spec = PluginSpec.Parse(ResolveSpecifier(item));
            return PluginStoreService.Install(
                settings,
                spec,
                match.Record.Key,
                item.PushedAt,
                item.DefaultBranch,
                item.SourceSha,
                progress,
                log);
        }

        internal static string ResolveSpecifier(PluginCatalogItem item)
        {
            if (item == null)
            {
                return String.Empty;
            }

            if (!String.IsNullOrWhiteSpace(item.InstallSpecifier))
            {
                return item.InstallSpecifier;
            }

            string fallback = item.Spec;
            if (!String.IsNullOrWhiteSpace(item.SourceSha))
            {
                fallback += "#" + item.SourceSha;
            }

            return fallback;
        }

        private static PluginCatalogItem FindCatalogItem(
            List<PluginCatalogItem> items,
            PluginInstallRecord record)
        {
            PluginSpec parsed = PluginSpec.Parse(
                String.IsNullOrWhiteSpace(record.InstallSpecifier)
                    ? record.Spec
                    : record.InstallSpecifier);
            for (int index = 0; index < items.Count; index++)
            {
                PluginCatalogItem item = items[index];
                if (!String.IsNullOrWhiteSpace(record.Owner)
                    && !String.IsNullOrWhiteSpace(record.Repository)
                    && String.Equals(
                        item.Owner,
                        record.Owner,
                        StringComparison.OrdinalIgnoreCase)
                    && String.Equals(
                        item.Repository,
                        record.Repository,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }

                if (parsed.IsGitHub
                    && String.Equals(
                        item.Owner,
                        parsed.Owner,
                        StringComparison.OrdinalIgnoreCase)
                    && String.Equals(
                        item.Repository,
                        parsed.Repository,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }

            return null;
        }

        private static bool IsNewer(string remote, string installed)
        {
            DateTime remoteTime;
            if (!DateTime.TryParse(remote, out remoteTime))
            {
                return false;
            }

            DateTime installedTime;
            if (!DateTime.TryParse(installed, out installedTime))
            {
                return true;
            }

            return remoteTime.ToUniversalTime() > installedTime.ToUniversalTime();
        }
    }
}
