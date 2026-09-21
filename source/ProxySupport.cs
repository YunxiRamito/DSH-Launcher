using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 启动器自己发起的出网请求统一从这里取代理。
    ///
    /// 三种模式：
    ///   None   —— 明确直连（把请求的 Proxy 置空）；
    ///   System —— 什么都不改，.NET 默认就是跟随 Windows 的 Internet 选项；
    ///   Custom —— 用设置里的协议 + 地址 + 端口。
    ///
    /// 本地回环一律绕开代理，否则探活自己的服务也会被丢进代理。
    /// DSH 本体进程不走这里（用户明确要求不接管）。
    /// </summary>
    internal static class ProxySupport
    {
        private static readonly string[] LocalBypass =
        {
            "localhost",
            "127.0.0.1",
            "::1"
        };

        internal static string Mode
        {
            get
            {
                LauncherSettings settings = Program.Settings;
                return settings == null
                    ? "None"
                    : Normalize(settings.ProxyMode);
            }
        }

        internal static bool IsDirect
        {
            get { return Mode == "None"; }
        }

        /// <summary>拼出代理地址。返回 false 表示当前不该用代理。</summary>
        internal static bool TryBuildProxyUri(out Uri uri)
        {
            return TryBuildProxyUri(Program.Settings, out uri);
        }

        internal static IWebProxy CreateProxy()
        {
            Uri uri;
            return TryBuildProxyUri(Program.Settings, out uri)
                ? CreateProxy(uri)
                : null;
        }

        internal static bool TryBuildProxyUri(
            LauncherSettings settings,
            out Uri uri)
        {
            uri = null;
            if (settings == null
                || Normalize(settings.ProxyMode) != "Custom")
            {
                return false;
            }

            string host = (settings.ProxyHost ?? String.Empty).Trim();
            if (host.Length == 0
                || settings.ProxyPort < 1
                || settings.ProxyPort > 65535)
            {
                return false;
            }

            string scheme = "http";
            string protocol = (settings.ProxyProtocol ?? "Http").Trim();
            if (String.Equals(protocol, "Https", StringComparison.OrdinalIgnoreCase))
            {
                scheme = "https";
            }
            else if (String.Equals(protocol, "Socks5", StringComparison.OrdinalIgnoreCase))
            {
                scheme = "socks5";
            }

            string hostPart = host.IndexOf(':') >= 0
                && !host.StartsWith("[", StringComparison.Ordinal)
                    ? "[" + host + "]"
                    : host;
            return Uri.TryCreate(
                scheme + "://" + hostPart + ":" + settings.ProxyPort,
                UriKind.Absolute,
                out uri);
        }

        private static IWebProxy CreateProxy(Uri uri)
        {
            WebProxy proxy = new WebProxy(uri)
            {
                BypassProxyOnLocal = true,
                BypassList = LocalBypass
            };
            return proxy;
        }

        /// <summary>HttpWebRequest：None 要显式置空，System 保持默认。</summary>
        internal static void Apply(HttpWebRequest request)
        {
            if (request == null)
            {
                return;
            }

            switch (Mode)
            {
                case "None":
                    request.Proxy = null;
                    break;
                case "Custom":
                    request.Proxy = CreateProxy();
                    break;
            }
        }

        /// <summary>
        /// 按调用方持有的设置对象应用代理，避免后台服务读取到旧实例。
        /// </summary>
        internal static void Apply(
            HttpWebRequest request,
            LauncherSettings settings)
        {
            if (request == null || settings == null)
            {
                return;
            }

            switch (Normalize(settings.ProxyMode))
            {
                case "None":
                    request.Proxy = null;
                    break;
                case "System":
                    request.Proxy = WebRequest.GetSystemWebProxy();
                    break;
                case "Custom":
                    Uri uri;
                    request.Proxy = TryBuildProxyUri(settings, out uri)
                        ? CreateProxy(uri)
                        : null;
                    break;
            }
        }

        /// <summary>WebClient：同样三种口径。</summary>
        internal static void Apply(WebClient client)
        {
            if (client == null)
            {
                return;
            }

            switch (Mode)
            {
                case "None":
                    client.Proxy = null;
                    break;
                case "Custom":
                    client.Proxy = CreateProxy();
                    break;
            }
        }

        internal static void Apply(HttpClientHandler handler)
        {
            if (handler == null)
            {
                return;
            }

            switch (Mode)
            {
                case "None":
                    handler.UseProxy = false;
                    break;
                case "System":
                    handler.UseProxy = true;
                    handler.Proxy = null;
                    break;
                case "Custom":
                    handler.UseProxy = true;
                    handler.Proxy = CreateProxy();
                    break;
            }
        }

        /// <summary>
        /// 给子进程（pnpm / npm / git）准备代理环境变量。
        /// DSH 本体进程不调用这个方法。
        /// </summary>
        internal static void ApplyProcessEnvironment(ProcessStartInfo startInfo)
        {
            if (startInfo == null)
            {
                return;
            }

            string[] names = { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" };
            for (int index = 0; index < names.Length; index++)
            {
                RemoveVariable(startInfo, names[index]);
                RemoveVariable(startInfo, names[index].ToLowerInvariant());
            }

            string noProxy = "localhost,127.0.0.1,::1";
            switch (Mode)
            {
                case "None":
                    SetVariable(startInfo, "NO_PROXY", noProxy);
                    break;
                case "System":
                    Uri system = ResolveSystemProxy();
                    if (system != null)
                    {
                        SetVariable(startInfo, "HTTP_PROXY", system.AbsoluteUri);
                        SetVariable(startInfo, "HTTPS_PROXY", system.AbsoluteUri);
                        SetVariable(startInfo, "NO_PROXY", noProxy);
                    }
                    break;
                case "Custom":
                    Uri custom;
                    if (TryBuildProxyUri(out custom))
                    {
                        SetVariable(startInfo, "HTTP_PROXY", custom.AbsoluteUri);
                        SetVariable(startInfo, "HTTPS_PROXY", custom.AbsoluteUri);
                        SetVariable(startInfo, "ALL_PROXY", custom.AbsoluteUri);
                        SetVariable(startInfo, "NO_PROXY", noProxy);
                    }
                    break;
            }
        }

        internal static string Describe()
        {
            return Describe(Program.Settings);
        }

        internal static string Describe(LauncherSettings settings)
        {
            string mode = settings == null
                ? "None"
                : Normalize(settings.ProxyMode);
            switch (mode)
            {
                case "None":
                    return "直连（不使用代理）";
                case "System":
                    return "使用系统代理";
                default:
                    Uri uri;
                    return TryBuildProxyUri(settings, out uri)
                        ? "自定义代理 " + uri.AbsoluteUri
                        : "自定义代理（配置不完整，已按直连处理）";
            }
        }

        private static Uri ResolveSystemProxy()
        {
            try
            {
                IWebProxy proxy = WebRequest.GetSystemWebProxy();
                if (proxy == null)
                {
                    return null;
                }

                Uri probe = new Uri("https://registry.npmjs.org/");
                Uri resolved = proxy.GetProxy(probe);
                if (resolved == null
                    || resolved == probe
                    || String.Equals(
                        resolved.Host,
                        probe.Host,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return resolved;
            }
            catch
            {
                return null;
            }
        }

        private static void SetVariable(ProcessStartInfo startInfo, string name, string value)
        {
            try
            {
                startInfo.EnvironmentVariables[name] = value;
            }
            catch
            {
            }
        }

        private static void RemoveVariable(ProcessStartInfo startInfo, string name)
        {
            try
            {
                if (startInfo.EnvironmentVariables.ContainsKey(name))
                {
                    startInfo.EnvironmentVariables.Remove(name);
                }
            }
            catch
            {
            }
        }

        private static string Normalize(string value)
        {
            if (String.Equals(value, "System", StringComparison.OrdinalIgnoreCase))
            {
                return "System";
            }

            if (String.Equals(value, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                return "Custom";
            }

            return "None";
        }
    }
}
