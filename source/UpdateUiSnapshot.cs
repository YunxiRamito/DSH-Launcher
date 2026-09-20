namespace DeepSeekHarnessLauncher
{
    internal enum UpdateUiActivity
    {
        Idle,
        Checking,
        Installing,
        UpToDate,
        Available,
        Failed
    }

    internal sealed class UpdateUiSnapshot
    {
        public UpdateUiActivity Activity { get; set; } =
            UpdateUiActivity.Idle;
        public string Version { get; set; } = string.Empty;
        public string ProgressText { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public double Progress { get; set; }
        public bool IsIndeterminate { get; set; }
    }
}
