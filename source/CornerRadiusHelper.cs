using System;
using Microsoft.UI.Xaml;

namespace DeepSeekHarnessLauncher
{
    internal static class CornerRadiusHelper
    {
        private static readonly bool _isWindows11 =
            Environment.OSVersion.Version.Build >= 22000;

        internal static bool IsWindows11
        {
            get { return _isWindows11; }
        }

        internal static CornerRadius ControlRadius
        {
            get { return new CornerRadius(_isWindows11 ? 4 : 0); }
        }

        internal static CornerRadius CardRadius
        {
            get { return new CornerRadius(_isWindows11 ? 6 : 0); }
        }

        internal static CornerRadius SurfaceRadius
        {
            get { return new CornerRadius(_isWindows11 ? 8 : 0); }
        }

        internal static CornerRadius BadgeRadius
        {
            get { return new CornerRadius(_isWindows11 ? 4 : 0); }
        }

        internal static void ApplyApplicationResources(
            ResourceDictionary resources)
        {
            if (resources == null)
            {
                return;
            }

            resources["ControlCornerRadius"] = ControlRadius;
            resources["OverlayCornerRadius"] = SurfaceRadius;
        }
    }
}
