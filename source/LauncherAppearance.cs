using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using WinRT;

namespace DeepSeekHarnessLauncher
{
    internal enum LauncherMaterialKind
    {
        Mica,
        MicaAlt,
        AcrylicThin,
        AcrylicBase,
        Solid
    }

    /// <summary>
    /// Shared visual state for every launcher-owned window.
    ///
    /// The settings window changes the material or theme once; the tray menu,
    /// update progress window and notification windows all update from the
    /// same controller instead of keeping their own copies of the backdrop
    /// setup.
    /// </summary>
    internal static class LauncherAppearance
    {
        private sealed class WindowRegistration
        {
            public Window Window;
            public FrameworkElement Surface;
            public Action<Brush> BackgroundSetter;
            public MicaController MicaController;
            public DesktopAcrylicController AcrylicController;
            public SystemBackdropConfiguration Configuration;
        }

        private static readonly List<WindowRegistration> Registrations =
            new List<WindowRegistration>();

        private static LauncherMaterialKind _material = LauncherMaterialKind.Mica;
        private static ElementTheme _theme = ElementTheme.Default;
        private static bool _shuttingDown;

        internal static LauncherMaterialKind Material
        {
            get { return _material; }
        }

        internal static ElementTheme Theme
        {
            get { return _theme; }
        }

        internal static void Register(
            Window window,
            FrameworkElement surface,
            Action<Brush> backgroundSetter)
        {
            if (window == null)
            {
                return;
            }

            WindowRegistration registration = Find(window);
            if (registration == null)
            {
                registration = new WindowRegistration
                {
                    Window = window,
                    Surface = surface ?? window.Content as FrameworkElement,
                    BackgroundSetter = backgroundSetter
                };
                Registrations.Add(registration);
            }
            else
            {
                registration.Surface = surface ?? registration.Surface;
                registration.BackgroundSetter = backgroundSetter ?? registration.BackgroundSetter;
            }

            Apply(registration);
        }

        internal static void Unregister(Window window)
        {
            if (_shuttingDown)
            {
                return;
            }

            WindowRegistration registration = Find(window);
            if (registration == null)
            {
                return;
            }

            DisposeControllers(registration);
            Registrations.Remove(registration);
        }

        internal static void BeginShutdown()
        {
            _shuttingDown = true;
            Registrations.Clear();
        }

        internal static void SetTheme(ElementTheme theme)
        {
            _theme = theme;
            ApplyAll();
        }

        internal static void SetMaterial(LauncherMaterialKind material)
        {
            _material = material;
            ApplyAll();
        }

        internal static void ApplyAll()
        {
            for (int index = 0; index < Registrations.Count; index++)
            {
                Apply(Registrations[index]);
            }
        }

        private static WindowRegistration Find(Window window)
        {
            for (int index = 0; index < Registrations.Count; index++)
            {
                if (ReferenceEquals(Registrations[index].Window, window))
                {
                    return Registrations[index];
                }
            }

            return null;
        }

        private static void Apply(WindowRegistration registration)
        {
            if (registration == null || registration.Window == null)
            {
                return;
            }

            FrameworkElement root = registration.Window.Content as FrameworkElement;
            if (root != null)
            {
                root.RequestedTheme = _theme;
            }

            if (registration.Surface != null)
            {
                registration.Surface.RequestedTheme = _theme;
            }

            DisposeControllers(registration);
            registration.Window.SystemBackdrop = null;

            if (_material == LauncherMaterialKind.Solid || IsHighContrast())
            {
                ApplySolid(registration);
                return;
            }

            try
            {
                ICompositionSupportsSystemBackdrop target =
                    registration.Window.As<ICompositionSupportsSystemBackdrop>();
                registration.Configuration ??= new SystemBackdropConfiguration
                {
                    IsInputActive = true
                };
                registration.Configuration.Theme = ResolveBackdropTheme(_theme);

                if (_material == LauncherMaterialKind.Mica
                    || _material == LauncherMaterialKind.MicaAlt)
                {
                    if (MicaController.IsSupported())
                    {
                        registration.MicaController = new MicaController
                        {
                            Kind = _material == LauncherMaterialKind.MicaAlt
                                ? MicaKind.BaseAlt
                                : MicaKind.Base
                        };
                        if (registration.MicaController.AddSystemBackdropTarget(target))
                        {
                            registration.MicaController.SetSystemBackdropConfiguration(
                                registration.Configuration);
                            SetTransparent(registration);
                            return;
                        }

                        DisposeControllers(registration);
                    }
                }

                if (registration.AcrylicController == null
                    && DesktopAcrylicController.IsSupported())
                {
                    registration.AcrylicController = new DesktopAcrylicController
                    {
                        Kind = _material == LauncherMaterialKind.AcrylicThin
                            ? DesktopAcrylicKind.Thin
                            : DesktopAcrylicKind.Base
                    };
                    if (registration.AcrylicController.AddSystemBackdropTarget(target))
                    {
                        registration.AcrylicController.SetSystemBackdropConfiguration(
                            registration.Configuration);
                        SetTransparent(registration);
                        return;
                    }

                    DisposeControllers(registration);
                }
            }
            catch
            {
                DisposeControllers(registration);
            }

            ApplySolid(registration);
        }

        private static void ApplySolid(WindowRegistration registration)
        {
            if (registration.BackgroundSetter == null)
            {
                return;
            }

            Windows.UI.Color background = IsDark()
                ? ColorHelper.FromArgb(255, 32, 32, 32)
                : ColorHelper.FromArgb(255, 243, 243, 243);

            if (IsHighContrast())
            {
                try
                {
                    background = new UISettings().GetColorValue(
                        UIColorType.Background);
                }
                catch
                {
                }
            }

            registration.BackgroundSetter(new SolidColorBrush(background));
        }

        private static void SetTransparent(WindowRegistration registration)
        {
            if (registration.BackgroundSetter != null)
            {
                registration.BackgroundSetter(
                    new SolidColorBrush(Colors.Transparent));
            }
        }

        private static void DisposeControllers(WindowRegistration registration)
        {
            if (_shuttingDown)
            {
                registration.MicaController = null;
                registration.AcrylicController = null;
                return;
            }

            if (registration.MicaController != null)
            {
                try
                {
                    registration.MicaController.RemoveAllSystemBackdropTargets();
                    registration.MicaController.Dispose();
                }
                catch
                {
                }

                registration.MicaController = null;
            }

            if (registration.AcrylicController != null)
            {
                try
                {
                    registration.AcrylicController.RemoveAllSystemBackdropTargets();
                    registration.AcrylicController.Dispose();
                }
                catch
                {
                }

                registration.AcrylicController = null;
            }
        }

        private static bool IsDark()
        {
            if (_theme == ElementTheme.Dark)
            {
                return true;
            }

            if (_theme == ElementTheme.Light)
            {
                return false;
            }

            try
            {
                return new UISettings().GetColorValue(UIColorType.Background).R < 128;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsHighContrast()
        {
            try
            {
                return new AccessibilitySettings().HighContrast;
            }
            catch
            {
                return false;
            }
        }

        private static SystemBackdropTheme ResolveBackdropTheme(ElementTheme theme)
        {
            if (theme == ElementTheme.Light)
            {
                return SystemBackdropTheme.Light;
            }

            if (theme == ElementTheme.Dark)
            {
                return SystemBackdropTheme.Dark;
            }

            return SystemBackdropTheme.Default;
        }
    }
}
