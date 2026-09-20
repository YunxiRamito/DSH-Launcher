using System;

namespace DeepSeekHarnessLauncher
{
    internal sealed class LauncherSettings
    {
        public int SchemaVersion { get; set; } = 1;

        public string PortMode { get; set; } = "Fixed";
        public int FixedPort { get; set; } = 8787;
        public bool StartWithWindows { get; set; }
        public string SilentStart { get; set; } = "StartupOnly";

        public string Theme { get; set; } = "System";
        public string AccentSource { get; set; } = "System";
        public string AccentColor { get; set; } = "#0A84FF";
        public string Material { get; set; } = "Mica";

        public string UpdateSource { get; set; } = "Accelerated";
        public string LauncherUpdateMode { get; set; } = "Install";
        public string DshUpdateMode { get; set; } = "Check";
        public string UpdateInterval { get; set; } = "EveryStart";
        public DateTime? LastUpdateCheckUtc { get; set; }
        public string LastNotifiedLauncherVersion { get; set; } = String.Empty;
        public string LastNotifiedDshVersion { get; set; } = String.Empty;

        public string ApiKeyProtected { get; set; } = String.Empty;
        public bool LegacyApiKeyMigrationCompleted { get; set; }
        public DateTime? ApiKeyValidatedUtc { get; set; }
        public bool ApiKeyLastValidationSucceeded { get; set; }

        public bool UpdateReminder { get; set; } = true;
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
