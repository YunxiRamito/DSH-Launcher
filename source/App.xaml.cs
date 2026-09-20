using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Text;

namespace DeepSeekHarnessLauncher
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            UnhandledException += App_UnhandledException;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs arguments)
        {
            try
            {
                CornerRadiusHelper.ApplyApplicationResources(Resources);
            }
            catch (Exception exception)
            {
                WriteAppLog("[theme] corner radius setup failed: " + exception);
            }

            Program.OnApplicationLaunched();
        }

        private static void App_UnhandledException(
            object sender,
            Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
        {
            WriteAppLog("[unhandled] " + args.Exception);
        }

        private static void WriteAppLog(string message)
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "launcher-boot.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + "  "
                    + message
                    + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }
}
