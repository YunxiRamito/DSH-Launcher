using System;

namespace DeepSeekHarnessLauncher
{
    internal sealed class LauncherSettings
    {
        public int SchemaVersion { get; set; } = 2;

        public string PortMode { get; set; } = "Fixed";
        public int FixedPort { get; set; } = 8787;
        public bool StartWithWindows { get; set; }
        public string SilentStart { get; set; } = "StartupOnly";

        public string Theme { get; set; } = "System";
        public string WindowStyle { get; set; } = "System";
        public string AccentSource { get; set; } = "System";
        public string AccentColor { get; set; } = "#0A84FF";
        public string Material { get; set; } = "Mica";

        public string UpdateSource { get; set; } = "Accelerated";

        /// <summary>在线插件的来源：Market = DSH 插件市场（api.dshmk.com），GitHub = GitHub 搜索接口。</summary>
        public string PluginSource { get; set; } = "Market";
        public string LauncherUpdateMode { get; set; } = "Install";
        public string DshUpdateMode { get; set; } = "Check";
        public string PluginUpdateMode { get; set; } = "Check";
        public string UpdateInterval { get; set; } = "EveryStart";
        public DateTime? LastUpdateCheckUtc { get; set; }
        public string LastNotifiedLauncherVersion { get; set; } = String.Empty;
        public string LastNotifiedDshVersion { get; set; } = String.Empty;
        public string LastNotifiedPluginSignature { get; set; } = String.Empty;

        // ---- 代理设置。None = 直连,System = 跟随 Windows,Custom = 用下面三个字段。
        public string ProxyMode { get; set; } = "None";
        public string ProxyProtocol { get; set; } = "Http";
        public string ProxyHost { get; set; } = "127.0.0.1";
        public int ProxyPort { get; set; } = 7890;

        public string ApiKeyProtected { get; set; } = String.Empty;
        public string GitHubTokenProtected { get; set; } = String.Empty;
        public bool DeveloperModeUnlocked { get; set; }
        public bool LegacyApiKeyMigrationCompleted { get; set; }
        public DateTime? ApiKeyValidatedUtc { get; set; }
        public bool ApiKeyLastValidationSucceeded { get; set; }

        public bool UpdateReminder { get; set; } = true;
        public bool PluginUpdateReminder { get; set; } = true;
        public bool ServiceStartReminder { get; set; } = true;
        public bool RechargeReminder { get; set; } = true;

        public bool SpendAlert5 { get; set; } = true;
        public bool SpendAlert10 { get; set; } = true;
        public bool SpendAlert20 { get; set; } = true;
        public bool SpendAlert50 { get; set; } = true;
        public bool SpendAlertCustom { get; set; } = true;
        public decimal SpendCustomAmount { get; set; } = 15.0m;

        public bool BalanceAlert20 { get; set; } = true;
        public bool BalanceAlert10 { get; set; } = true;
        public bool BalanceAlert5 { get; set; } = true;
        public bool BalanceAlert1 { get; set; } = true;
        public bool BalanceAlertCustom { get; set; } = true;
        public decimal BalanceCustomAmount { get; set; } = 50.0m;

        public string DshRoot { get; set; } = String.Empty;
        public string NodePath { get; set; } = String.Empty;
    }
}
