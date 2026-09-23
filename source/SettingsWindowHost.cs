using System;

namespace DeepSeekHarnessLauncher
{
    internal sealed class SettingsWindowHost
    {
        public bool IsPreview { get; set; }

        public LauncherSettings Settings { get; set; }

        public Func<string> GetServiceStatus { get; set; } =
            delegate { return "状态未知"; };

        public Action RestartService { get; set; } = delegate { };
        public Action StopService { get; set; } = delegate { };
        public Action RecheckEnvironment { get; set; } = delegate { };
        public Action CheckLauncherUpdate { get; set; } = delegate { };
        public Action InstallLauncherUpdate { get; set; } = delegate { };
        public Action CheckDshUpdate { get; set; } = delegate { };
        public Action InstallDshUpdate { get; set; } = delegate { };
        public Action CheckPluginUpdates { get; set; } = delegate { };
        public Action InstallPluginUpdates { get; set; } = delegate { };
        public Action<string> InstallPluginUpdate { get; set; } = delegate { };
        public Func<UpdateUiSnapshot> GetLauncherUpdateState { get; set; } =
            delegate { return new UpdateUiSnapshot(); };
        public Func<UpdateUiSnapshot> GetDshUpdateState { get; set; } =
            delegate { return new UpdateUiSnapshot(); };
        public Func<UpdateUiSnapshot> GetPluginUpdateState { get; set; } =
            delegate { return new UpdateUiSnapshot(); };
        public Action<string> ApplyApiKey { get; set; } = delegate { };
        public Action RefreshBalance { get; set; } = delegate { };
        public Action SynchronizeInstallerPaths { get; set; } = delegate { };
        public Action<string> Log { get; set; } = delegate { };

        public event Action UpdateStateChanged = delegate { };
        public event Action ServiceStateChanged = delegate { };

        public void RaiseUpdateStateChanged()
        {
            UpdateStateChanged();
        }

        public void RaiseServiceStateChanged()
        {
            ServiceStateChanged();
        }
    }
}
