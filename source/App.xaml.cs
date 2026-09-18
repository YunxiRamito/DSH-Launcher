using Microsoft.UI.Xaml;

namespace DeepSeekHarnessLauncher
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs arguments)
        {
            Program.OnApplicationLaunched();
        }
    }
}
