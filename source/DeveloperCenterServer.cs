using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 本机开发者管理中心。监听回环地址，页面和 API 由启动器自己提供。
    /// GitHub Token 只保存在进程内存里，页面只能通过设备码流程换到短会话。
    /// </summary>
    internal static class DeveloperCenterServer
    {
        private const string Prefix = "http://127.0.0.1:8788/";
        private const string Repository = Constants.Repository;
        private const string Branch = "main";
        private const string FeaturedPath = "featured-plugins.json";
        private const string RolesPath = "developer-roles.json";
        private const string SessionCookie = "dsh_admin_session";

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, AdminSession> Sessions =
            new Dictionary<string, AdminSession>(StringComparer.Ordinal);
        private static HttpListener _listener;
        private static Thread _thread;
        private static LauncherSettings _settings;
        private static Action<string> _log;

        internal static bool IsRunning
        {
            get
            {
                lock (Sync)
                {
                    return _listener != null && _listener.IsListening;
                }
            }
        }

        internal static bool EnsureStarted(
            LauncherSettings settings,
            Action<string> log)
        {
            lock (Sync)
            {
                _settings = settings;
                _log = log;
                if (_listener != null && _listener.IsListening)
                {
                    return true;
                }

                try
                {
                    HttpListener listener = new HttpListener();
                    listener.Prefixes.Add(Prefix);
                    listener.Start();
                    _listener = listener;
                    _thread = new Thread(ListenLoop);
                    _thread.IsBackground = true;
                    _thread.Name = "DeepSeekHarnessDeveloperCenter";
                    _thread.Start();
                    WriteLog("开发者管理中心已启动：" + Prefix);
                    return true;
                }
                catch (Exception exception)
                {
                    _listener = null;
                    WriteLog("开发者管理中心启动失败：" + exception.Message);
                    return false;
                }
            }
        }

        internal static void Stop()
        {
            HttpListener listener;
            lock (Sync)
            {
                listener = _listener;
                _listener = null;
                Sessions.Clear();
            }

            try
            {
                if (listener != null)
                {
                    listener.Stop();
                    listener.Close();
                }
            }
            catch
            {
            }
        }

        private static void ListenLoop()
        {
            while (true)
            {
                HttpListener listener;
                lock (Sync)
                {
                    listener = _listener;
                }

                if (listener == null || !listener.IsListening)
                {
                    return;
                }

                HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch
                {
                    return;
                }

                ThreadPool.QueueUserWorkItem(delegate
                {
                    Handle(context);
                });
            }
        }

        private static void Handle(HttpListenerContext context)
        {
            try
            {
                if (!IsLoopback(context.Request.RemoteEndPoint))
                {
                    WriteJson(context, 403, new JsonObject
                    {
                        ["error"] = "只允许本机访问。"
                    });
                    return;
                }

                string path = context.Request.Url.AbsolutePath.TrimEnd('/');
                if (path.Length == 0)
                {
                    path = "/";
                }

                if (String.Equals(path, "/", StringComparison.Ordinal)
                    && String.Equals(
                        context.Request.HttpMethod,
                        "GET",
                        StringComparison.OrdinalIgnoreCase))
                {
                    WriteHtml(context, PageHtml);
                    return;
                }

                switch (path)
                {
                    case "/api/auth/start":
                        HandleAuthStart(context);
                        return;
                    case "/api/auth/poll":
                        HandleAuthPoll(context);
                        return;
                    case "/api/auth/token":
                        HandleTokenLogin(context);
                        return;
                    case "/api/auth/me":
                        HandleMe(context);
                        return;
                    case "/api/auth/logout":
                        HandleLogout(context);
                        return;
                    case "/api/featured":
                        HandleFeatured(context);
                        return;
                    case "/api/featured/save":
                        HandleFeaturedSave(context);
                        return;
                    case "/api/featured/commit":
                        HandleFeaturedCommit(context);
                        return;
                    case "/api/proxy-test":
                        HandleProxyTest(context);
                        return;
                    default:
                        WriteJson(context, 404, new JsonObject
                        {
                            ["error"] = "接口不存在。"
                        });
                        return;
                }
            }
            catch (Exception exception)
            {
                WriteLog("开发者管理中心请求失败：" + exception);
                try
                {
                    WriteJson(context, 500, new JsonObject
                    {
                        ["error"] = exception.Message
                    });
                }
                catch
                {
                }
            }
        }

        private static void HandleAuthStart(HttpListenerContext context)
        {
            if (!RequirePost(context))
            {
                return;
            }

            JsonObject body = ReadJsonBody(context);
            string clientId = body?["clientId"]?.GetValue<string>() ?? String.Empty;
            if (String.IsNullOrWhiteSpace(clientId))
            {
                clientId = Environment.GetEnvironmentVariable(
                    "DSH_GITHUB_CLIENT_ID") ?? String.Empty;
            }

            if (String.IsNullOrWhiteSpace(clientId))
            {
                WriteJson(context, 400, new JsonObject
                {
                    ["error"] = "还没配置 GitHub OAuth App Client ID。"
                });
                return;
            }

            string error;
            JsonNode response = GitHubFormPost(
                "https://github.com/login/device/code",
                "client_id=" + Uri.EscapeDataString(clientId.Trim())
                    + "&scope=" + Uri.EscapeDataString("public_repo read:user"),
                null,
                out error);
            if (response == null)
            {
                string friendly = DescribeDeviceFlowError(error);
                WriteLog("设备码创建失败：" + friendly);
                WriteJson(context, 502, new JsonObject
                {
                    ["error"] = friendly ?? "GitHub 设备码接口不可用。"
                });
                return;
            }

            WriteLog("设备码已创建，网络=" + ProxySupport.Describe(_settings));
            WriteJson(context, 200, new JsonObject
            {
                ["deviceCode"] = response["device_code"]?.GetValue<string>() ?? String.Empty,
                ["userCode"] = response["user_code"]?.GetValue<string>() ?? String.Empty,
                ["verificationUri"] = response["verification_uri"]?.GetValue<string>() ?? String.Empty,
                ["verificationUriComplete"] = response["verification_uri_complete"]?.GetValue<string>() ?? String.Empty,
                ["expiresIn"] = response["expires_in"]?.GetValue<int>() ?? 900,
                ["interval"] = response["interval"]?.GetValue<int>() ?? 5,
                ["proxy"] = ProxySupport.Describe(_settings)
            });
        }

        private static string DescribeDeviceFlowError(string error)
        {
            if (String.IsNullOrWhiteSpace(error))
            {
                return null;
            }

            string text = error.Trim();
            if (text.IndexOf(
                    "device_flow_disabled",
                    StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf(
                    "device flow is not enabled",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "这个 OAuth App 没有启用 Device Flow。请到 GitHub OAuth App 设置里勾选 Enable Device Flow 后重试。";
            }

            if (text.IndexOf(
                    "client_id",
                    StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf(
                    "incorrect client credentials",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Client ID 不正确。请填写 OAuth App 的 Client ID，不要填 Client Secret。GitHub 原始错误：" + text;
            }

            if (text.IndexOf(
                    "unsupported_grant_type",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "GitHub 拒绝了设备码授权类型，请确认使用的是 OAuth App 并且已启用 Device Flow。原始错误：" + text;
            }

            return "GitHub 设备码请求失败：" + text;
        }

        private static void HandleAuthPoll(HttpListenerContext context)
        {
            if (!RequirePost(context))
            {
                return;
            }

            JsonObject body = ReadJsonBody(context);
            string clientId = body?["clientId"]?.GetValue<string>() ?? String.Empty;
            if (String.IsNullOrWhiteSpace(clientId))
            {
                clientId = Environment.GetEnvironmentVariable(
                    "DSH_GITHUB_CLIENT_ID") ?? String.Empty;
            }

            string deviceCode = body?["deviceCode"]?.GetValue<string>() ?? String.Empty;
            if (String.IsNullOrWhiteSpace(clientId)
                || String.IsNullOrWhiteSpace(deviceCode))
            {
                WriteJson(context, 400, new JsonObject
                {
                    ["error"] = "设备码参数不完整。"
                });
                return;
            }

            string error;
            JsonNode response = GitHubFormPost(
                "https://github.com/login/oauth/access_token",
                "client_id=" + Uri.EscapeDataString(clientId.Trim())
                    + "&device_code=" + Uri.EscapeDataString(deviceCode)
                    + "&grant_type="
                    + Uri.EscapeDataString(
                        "urn:ietf:params:oauth:grant-type:device_code"),
                null,
                out error);
            if (response == null)
            {
                string friendly = DescribeDeviceFlowError(error);
                WriteLog("设备码轮询失败：" + friendly);
                WriteJson(context, 502, new JsonObject
                {
                    ["error"] = friendly ?? "设备码校验失败。"
                });
                return;
            }

            string pending = response["error"]?.GetValue<string>() ?? String.Empty;
            if (!String.IsNullOrWhiteSpace(pending))
            {
                WriteLog("设备码轮询状态：" + pending);
                WriteJson(context, 200, new JsonObject
                {
                    ["pending"] = true,
                    ["code"] = pending,
                    ["error"] = response["error_description"]?.GetValue<string>()
                        ?? pending
                });
                return;
            }

            string token = response["access_token"]?.GetValue<string>() ?? String.Empty;
            if (String.IsNullOrWhiteSpace(token))
            {
                WriteJson(context, 502, new JsonObject
                {
                    ["error"] = "GitHub 没有返回访问令牌。"
                });
                return;
            }

            CompleteLogin(context, token);
        }

        private static void HandleTokenLogin(HttpListenerContext context)
        {
            if (!RequirePost(context))
            {
                return;
            }

            JsonObject body = ReadJsonBody(context);
            string token = body?["token"]?.GetValue<string>() ?? String.Empty;
            if (String.IsNullOrWhiteSpace(token))
            {
                WriteJson(context, 400, new JsonObject
                {
                    ["error"] = "Token 不能为空。"
                });
                return;
            }

            CompleteLogin(context, token.Trim());
        }

        private static void HandleProxyTest(HttpListenerContext context)
        {
            DateTime startedAt = DateTime.UtcNow;
            string error;
            JsonNode response = GitHubApi(
                "https://api.github.com/",
                String.Empty,
                "GET",
                null,
                out error);
            long elapsed = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            if (response == null)
            {
                WriteJson(context, 502, new JsonObject
                {
                    ["error"] = DescribeException(
                        new InvalidOperationException(
                            error ?? "GitHub API 请求失败。")),
                    ["proxy"] = ProxySupport.Describe(_settings),
                    ["elapsedMs"] = elapsed
                });
                return;
            }

            WriteJson(context, 200, new JsonObject
            {
                ["ok"] = true,
                ["proxy"] = ProxySupport.Describe(_settings),
                ["elapsedMs"] = elapsed
            });
        }

        private static void CompleteLogin(
            HttpListenerContext context,
            string token)
        {
            string error;
            JsonNode user = GitHubApi(
                "https://api.github.com/user",
                token,
                "GET",
                null,
                out error);
            if (user == null)
            {
                WriteJson(context, 502, new JsonObject
                {
                    ["error"] = error ?? "无法读取 GitHub 用户。"
                });
                return;
            }

            string login = user["login"]?.GetValue<string>() ?? String.Empty;
            string role = ResolveRole(token, login, out string roleError);
            if (role == "none")
            {
                WriteLog("开发者登录被拒绝：" + login + " 不在角色名单中");
                WriteJson(context, 403, new JsonObject
                {
                    ["error"] = "GitHub 账号 "
                        + login
                        + " 不在开发者角色名单中。"
                        + (String.IsNullOrWhiteSpace(roleError)
                            ? String.Empty
                            : " " + roleError)
                });
                return;
            }

            WriteLog("开发者登录成功：" + login + "，角色=" + role);
            string sessionToken = CreateToken();
            lock (Sync)
            {
                Sessions[sessionToken] = new AdminSession
                {
                    Login = login,
                    AvatarUrl = user["avatar_url"]?.GetValue<string>() ?? String.Empty,
                    Role = role,
                    GitHubToken = token
                };
            }

            context.Response.Headers.Add(
                "Set-Cookie",
                SessionCookie + "=" + sessionToken
                    + "; HttpOnly; SameSite=Strict; Path=/");
            WriteJson(context, 200, new JsonObject
            {
                ["login"] = login,
                ["avatarUrl"] = user["avatar_url"]?.GetValue<string>() ?? String.Empty,
                ["role"] = role
            });
        }

        private static void HandleMe(HttpListenerContext context)
        {
            AdminSession session = GetSession(context);
            if (session == null)
            {
                WriteJson(context, 200, new JsonObject
                {
                    ["authenticated"] = false
                });
                return;
            }

            WriteJson(context, 200, new JsonObject
            {
                ["authenticated"] = true,
                ["login"] = session.Login,
                ["avatarUrl"] = session.AvatarUrl,
                ["role"] = session.Role
            });
        }

        private static void HandleLogout(HttpListenerContext context)
        {
            if (!RequirePost(context))
            {
                return;
            }

            string token = GetSessionToken(context);
            if (!String.IsNullOrWhiteSpace(token))
            {
                lock (Sync)
                {
                    Sessions.Remove(token);
                }
            }

            context.Response.Headers.Add(
                "Set-Cookie",
                SessionCookie
                    + "=; HttpOnly; SameSite=Strict; Path=/; Max-Age=0");
            WriteJson(context, 200, new JsonObject { ["ok"] = true });
        }

        private static void HandleFeatured(HttpListenerContext context)
        {
            if (!String.Equals(
                context.Request.HttpMethod,
                "GET",
                StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 405, new JsonObject
                {
                    ["error"] = "只支持 GET。"
                });
                return;
            }

            FeaturedPluginResult result = FeaturedPluginService.Load(
                _settings,
                false,
                WriteLog);
            EnrichFeaturedItems(result == null ? null : result.Items, null);
            WriteJsonText(
                context,
                200,
                FeaturedPluginService.Serialize(
                    result == null
                        ? new List<PluginCatalogItem>()
                        : result.Items));
        }

        private static void EnrichFeaturedItems(
            List<PluginCatalogItem> items,
            string token)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            if (String.IsNullOrWhiteSpace(token))
            {
                token = LauncherSettingsStore.ReadGitHubToken(_settings);
            }

            bool changed = false;
            for (int index = 0; index < items.Count; index++)
            {
                PluginCatalogItem item = items[index];
                if (item.Stars > 0
                    && !String.IsNullOrWhiteSpace(item.ImageUrl))
                {
                    continue;
                }

                string error;
                JsonNode repository = GitHubApi(
                    "https://api.github.com/repos/" + item.FullName,
                    token,
                    "GET",
                    null,
                    out error);
                if (repository == null)
                {
                    WriteLog("推荐条目补全失败：" + item.FullName
                        + "：" + (error ?? "未知错误"));
                    continue;
                }

                item.Stars = repository["stargazers_count"]?.GetValue<int>()
                    ?? item.Stars;
                item.Language = repository["language"]?.GetValue<string>()
                    ?? item.Language;
                item.DefaultBranch = repository["default_branch"]?.GetValue<string>()
                    ?? item.DefaultBranch;
                item.PushedAt = repository["pushed_at"]?.GetValue<string>()
                    ?? item.PushedAt;
                JsonNode owner = repository["owner"];
                item.ImageUrl = owner?["avatar_url"]?.GetValue<string>()
                    ?? item.ImageUrl;
                JsonNode license = repository["license"];
                item.License = license?["spdx_id"]?.GetValue<string>()
                    ?? item.License;
                if (String.IsNullOrWhiteSpace(item.Version))
                {
                    item.Version = PluginCatalogService.DeriveVersion(item);
                }

                changed = true;
            }

            if (changed)
            {
                FeaturedPluginService.SaveLocal(items);
            }
        }

        private static void HandleFeaturedSave(HttpListenerContext context)
        {
            if (!RequirePost(context))
            {
                return;
            }

            AdminSession session = GetSession(context);
            if (session == null)
            {
                WriteJson(context, 401, new JsonObject
                {
                    ["error"] = "请先登录。"
                });
                return;
            }

            if (!String.Equals(
                session.Role,
                "superadmin",
                StringComparison.Ordinal))
            {
                WriteJson(context, 403, new JsonObject
                {
                    ["error"] = "只有超级管理员可以修改推荐列表。"
                });
                return;
            }

            string json = ReadBody(context);
            string error;
            List<PluginCatalogItem> items =
                FeaturedPluginService.ParseJson(json, out error);
            if (!String.IsNullOrWhiteSpace(error))
            {
                WriteJson(context, 400, new JsonObject
                {
                    ["error"] = error
                });
                return;
            }

            EnrichFeaturedItems(items, session.GitHubToken);
            if (!FeaturedPluginService.SaveLocal(items))
            {
                WriteJson(context, 500, new JsonObject
                {
                    ["error"] = "本地推荐缓存写入失败。"
                });
                return;
            }

            WriteJson(context, 200, new JsonObject
            {
                ["ok"] = true,
                ["count"] = items.Count,
                ["featured"] = JsonNode.Parse(
                    FeaturedPluginService.Serialize(items))
            });
        }

        private static void HandleFeaturedCommit(HttpListenerContext context)
        {
            if (!RequirePost(context))
            {
                return;
            }

            AdminSession session = GetSession(context);
            if (session == null)
            {
                WriteJson(context, 401, new JsonObject
                {
                    ["error"] = "请先登录。"
                });
                return;
            }

            if (!String.Equals(
                session.Role,
                "superadmin",
                StringComparison.Ordinal))
            {
                WriteJson(context, 403, new JsonObject
                {
                    ["error"] = "只有超级管理员可以提交推荐列表。"
                });
                return;
            }

            JsonObject body = ReadJsonBody(context);
            string json = body?["json"]?.GetValue<string>() ?? ReadBody(context);
            string parseError;
            List<PluginCatalogItem> items =
                FeaturedPluginService.ParseJson(json, out parseError);
            if (!String.IsNullOrWhiteSpace(parseError))
            {
                WriteJson(context, 400, new JsonObject
                {
                    ["error"] = parseError
                });
                return;
            }

            string error;
            string commitUrl = CommitFeatured(session.GitHubToken, items, out error);
            if (String.IsNullOrWhiteSpace(commitUrl))
            {
                WriteJson(context, 502, new JsonObject
                {
                    ["error"] = error ?? "提交失败。"
                });
                return;
            }

            FeaturedPluginService.SaveLocal(items);
            WriteJson(context, 200, new JsonObject
            {
                ["ok"] = true,
                ["url"] = commitUrl,
                ["count"] = items.Count
            });
        }

        private static string CommitFeatured(
            string token,
            List<PluginCatalogItem> items,
            out string error)
        {
            error = null;
            string contentsUrl = "https://api.github.com/repos/"
                + Repository + "/contents/" + FeaturedPath;
            JsonNode existing = GitHubApi(
                contentsUrl,
                token,
                "GET",
                null,
                out error);
            string sha = existing?["sha"]?.GetValue<string>() ?? String.Empty;
            string content = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                    FeaturedPluginService.Serialize(items)
                    .Replace("\r\n", "\n")));
            JsonObject payload = new JsonObject
            {
                ["message"] = "content: update featured plugins",
                ["content"] = content,
                ["branch"] = Branch
            };
            if (!String.IsNullOrWhiteSpace(sha))
            {
                payload["sha"] = sha;
            }

            JsonNode response = GitHubApi(
                contentsUrl,
                token,
                "PUT",
                payload.ToJsonString(),
                out error);
            return response?["commit"]?["html_url"]?.GetValue<string>()
                ?? String.Empty;
        }

        private static string ResolveRole(
            string token,
            string login,
            out string error)
        {
            error = null;
            if (String.Equals(
                login,
                "YunxiRamito",
                StringComparison.OrdinalIgnoreCase))
            {
                return "superadmin";
            }

            string raw = "https://raw.githubusercontent.com/"
                + Repository + "/" + Branch + "/" + RolesPath;
            string mirror = "https://cdn.jsdelivr.net/gh/"
                + Repository + "@" + Branch + "/" + RolesPath;
            JsonNode roles = GitHubApi(raw, token, "GET", null, out error);
            if (roles == null)
            {
                roles = GitHubApi(mirror, token, "GET", null, out error);
            }

            if (roles == null)
            {
                return "none";
            }

            if (ContainsString(roles["superAdmins"] as JsonArray, login))
            {
                return "superadmin";
            }

            if (ContainsString(roles["members"] as JsonArray, login))
            {
                return "member";
            }

            return "none";
        }

        private static bool ContainsString(JsonArray array, string value)
        {
            if (array == null)
            {
                return false;
            }

            for (int index = 0; index < array.Count; index++)
            {
                if (String.Equals(
                    array[index]?.GetValue<string>(),
                    value,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static JsonNode GitHubApi(
            string url,
            string token,
            string method,
            string body,
            out string error)
        {
            error = null;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = method ?? "GET";
                request.Accept = "application/vnd.github+json";
                request.UserAgent = Constants.UserAgent;
                request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
                request.KeepAlive = false;
                request.Pipelined = false;
                request.ConnectionGroupName =
                    "DSH-GitHubApi-" + Guid.NewGuid().ToString("N");
                request.ServicePoint.Expect100Continue = false;
                if (!String.IsNullOrWhiteSpace(token))
                {
                    request.Headers["Authorization"] = "Bearer " + token;
                }

                ProxySupport.Apply(request, _settings);
                if (body != null)
                {
                    byte[] payload = Encoding.UTF8.GetBytes(body);
                    request.ContentType = "application/json";
                    request.ContentLength = payload.Length;
                    using (Stream stream = request.GetRequestStream())
                    {
                        stream.Write(payload, 0, payload.Length);
                    }
                }

                using (WebResponse response = request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    return String.IsNullOrWhiteSpace(json)
                        ? new JsonObject()
                        : JsonNode.Parse(json);
                }
            }
            catch (WebException exception)
            {
                try
                {
                    using (WebResponse response = exception.Response)
                    using (Stream stream = response?.GetResponseStream())
                    using (StreamReader reader = stream == null
                        ? null
                        : new StreamReader(stream, Encoding.UTF8))
                    {
                        error = reader == null
                            ? exception.Message
                            : reader.ReadToEnd();
                    }
                }
                catch
                {
                    error = exception.Message;
                }

                return null;
            }
            catch (Exception exception)
            {
                error = DescribeException(exception);
                return null;
            }
        }

        private static JsonNode GitHubFormPost(
            string url,
            string body,
            string token,
            out string error)
        {
            JsonNode response = null;
            error = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                response = GitHubFormPostOnce(url, body, token, out error);
                if (response != null || !IsTransientNetworkError(error))
                {
                    return response;
                }

                Thread.Sleep(250);
            }

            return response;
        }

        private static JsonNode GitHubFormPostOnce(
            string url,
            string body,
            string token,
            out string error)
        {
            error = null;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.Accept = "application/json";
                request.UserAgent = Constants.UserAgent;
                request.ContentType = "application/x-www-form-urlencoded";
                request.KeepAlive = false;
                request.Pipelined = false;
                request.ConnectionGroupName =
                    "DSH-Device-" + Guid.NewGuid().ToString("N");
                request.ServicePoint.Expect100Continue = false;
                if (!String.IsNullOrWhiteSpace(token))
                {
                    request.Headers["Authorization"] = "Bearer " + token;
                }

                ProxySupport.Apply(request, _settings);
                byte[] payload = Encoding.UTF8.GetBytes(body);
                request.ContentLength = payload.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(payload, 0, payload.Length);
                }

                using (WebResponse response = request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return JsonNode.Parse(reader.ReadToEnd());
                }
            }
            catch (WebException exception)
            {
                error = ReadWebExceptionBody(exception);
                return null;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return null;
            }
        }

        private static bool IsTransientNetworkError(string error)
        {
            if (String.IsNullOrWhiteSpace(error))
            {
                return false;
            }

            string text = error.ToLowerInvariant();
            return text.Contains("ssl connection")
                || text.Contains("unexpected eof")
                || text.Contains("connection was closed")
                || text.Contains("connection reset")
                || text.Contains("while sending the request")
                || text.Contains("timed out");
        }

        private static string ReadWebExceptionBody(WebException exception)
        {
            try
            {
                using (WebResponse response = exception.Response)
                using (Stream stream = response == null
                    ? null
                    : response.GetResponseStream())
                using (StreamReader reader = stream == null
                    ? null
                    : new StreamReader(stream, Encoding.UTF8))
                {
                    string body = reader == null ? null : reader.ReadToEnd();
                    return String.IsNullOrWhiteSpace(body)
                        ? DescribeException(exception)
                        : body.Trim();
                }
            }
            catch
            {
                return DescribeException(exception);
            }
        }

        private static string DescribeException(Exception exception)
        {
            if (exception == null)
            {
                return "未知错误。";
            }

            StringBuilder builder = new StringBuilder();
            Exception current = exception;
            int depth = 0;
            while (current != null && depth < 6)
            {
                if (depth > 0)
                {
                    builder.Append(" <- ");
                }

                builder.Append(current.GetType().Name)
                    .Append(": ")
                    .Append(current.Message);
                current = current.InnerException;
                depth++;
            }

            return builder.ToString();
        }

        private static bool RequirePost(HttpListenerContext context)
        {
            if (String.Equals(
                context.Request.HttpMethod,
                "POST",
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            WriteJson(context, 405, new JsonObject
            {
                ["error"] = "只支持 POST。"
            });
            return false;
        }

        private static AdminSession GetSession(HttpListenerContext context)
        {
            string token = GetSessionToken(context);
            if (String.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            lock (Sync)
            {
                AdminSession session;
                return Sessions.TryGetValue(token, out session)
                    ? session
                    : null;
            }
        }

        private static string GetSessionToken(HttpListenerContext context)
        {
            Cookie cookie = context.Request.Cookies[SessionCookie];
            return cookie == null ? String.Empty : (cookie.Value ?? String.Empty);
        }

        private static string CreateToken()
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator generator =
                RandomNumberGenerator.Create())
            {
                generator.GetBytes(bytes);
            }

            StringBuilder builder = new StringBuilder(bytes.Length * 2);
            for (int index = 0; index < bytes.Length; index++)
            {
                builder.Append(bytes[index].ToString("x2"));
            }

            return builder.ToString();
        }

        private static JsonObject ReadJsonBody(HttpListenerContext context)
        {
            string body = ReadBody(context);
            if (String.IsNullOrWhiteSpace(body))
            {
                return new JsonObject();
            }

            try
            {
                return JsonNode.Parse(body) as JsonObject ?? new JsonObject();
            }
            catch
            {
                return new JsonObject();
            }
        }

        private static string ReadBody(HttpListenerContext context)
        {
            using (Stream stream = context.Request.InputStream)
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static void WriteJson(
            HttpListenerContext context,
            int status,
            JsonNode body)
        {
            WriteJsonText(
                context,
                status,
                body == null
                    ? "{}"
                    : body.ToJsonString());
        }

        private static void WriteJsonText(
            HttpListenerContext context,
            int status,
            string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json ?? "{}");
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = payload.Length;
            context.Response.OutputStream.Write(payload, 0, payload.Length);
            context.Response.Close();
        }

        private static void WriteHtml(
            HttpListenerContext context,
            string html)
        {
            byte[] payload = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = payload.Length;
            context.Response.OutputStream.Write(payload, 0, payload.Length);
            context.Response.Close();
        }

        private static bool IsLoopback(IPEndPoint endpoint)
        {
            if (endpoint == null || endpoint.Address == null)
            {
                return false;
            }

            return IPAddress.IsLoopback(endpoint.Address);
        }

        private static void WriteLog(string message)
        {
            Action<string> log = _log;
            if (log != null && !String.IsNullOrWhiteSpace(message))
            {
                log(message);
            }
        }

        private sealed class AdminSession
        {
            public string Login { get; set; } = String.Empty;
            public string AvatarUrl { get; set; } = String.Empty;
            public string Role { get; set; } = String.Empty;
            public string GitHubToken { get; set; } = String.Empty;
        }

        private const string PageHtml = """
<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>DSH 开发者管理中心</title>
  <style>
    :root{color-scheme:light dark;--bg:#f5f7fb;--card:#fff;--text:#172033;--muted:#667085;--line:#d8deea;--accent:#0969da;--danger:#c7382d}
    @media(prefers-color-scheme:dark){:root{--bg:#11151d;--card:#1a202b;--text:#eef3fb;--muted:#9aa7ba;--line:#303a4a;--accent:#4da3ff;--danger:#ff776d}}
    *{box-sizing:border-box} body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
    header{display:flex;align-items:center;justify-content:space-between;gap:16px;padding:18px 24px;border-bottom:1px solid var(--line);background:var(--card)}
    h1{margin:0;font-size:20px;font-weight:650} h2{margin:0 0 12px;font-size:16px} main{max-width:1180px;margin:0 auto;padding:20px 24px 48px}
    .card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:16px;margin-bottom:16px}
    .row{display:flex;align-items:center;gap:10px;flex-wrap:wrap}.grow{flex:1;min-width:220px}.muted{color:var(--muted)}.hidden{display:none}
    button{border:1px solid var(--line);border-radius:6px;padding:8px 12px;background:transparent;color:var(--text);cursor:pointer}button:hover{border-color:var(--accent)}
    button.primary{background:var(--accent);border-color:var(--accent);color:#fff}button.danger{color:var(--danger)}
    input{min-height:36px;border:1px solid var(--line);border-radius:6px;padding:7px 10px;background:var(--bg);color:var(--text)} input:focus{outline:2px solid color-mix(in srgb,var(--accent) 45%,transparent);border-color:var(--accent)}
    textarea{min-height:76px;border:1px solid var(--line);border-radius:6px;padding:8px 10px;background:var(--bg);color:var(--text);font:12px/1.45 Consolas,monospace;resize:vertical}
    table{width:100%;border-collapse:collapse}th,td{text-align:left;vertical-align:middle;padding:9px 8px;border-bottom:1px solid var(--line)}th{color:var(--muted);font-size:12px;font-weight:600}
    td input{width:100%}td.note{min-width:220px}td.order{width:78px}td.actions{width:118px;text-align:right}.pill{padding:3px 8px;border:1px solid var(--line);border-radius:99px;color:var(--muted);font-size:12px}
    #toast{position:fixed;right:20px;bottom:20px;max-width:440px;padding:12px 14px;border-radius:7px;background:#222a36;color:#fff;box-shadow:0 8px 30px #0004;transform:translateY(20px);opacity:0;transition:.2s}
    #toast.show{transform:none;opacity:1}#toast.error{background:#8f2c27}
    code{background:color-mix(in srgb,var(--text) 8%,transparent);padding:2px 5px;border-radius:4px}
  </style>
</head>
<body>
<header><div><h1>DSH 开发者管理中心</h1><div class="muted">推荐列表、角色与设备码登录</div></div><div id="account" class="row"></div></header>
<main>
  <section id="loginCard" class="card">
    <h2>GitHub 登录</h2>
    <p class="muted">需要启用 Device Flow 的 OAuth App Client ID。Token 只保存在启动器进程内存中。</p>
    <div class="row"><input id="clientId" class="grow" placeholder="GitHub OAuth App Client ID"><button id="startLogin" class="primary">开始登录</button><button id="tokenLogin">使用 Token</button><button id="testProxy">测试代理</button><a class="pill" href="https://github.com/settings/applications/new" target="_blank" rel="noreferrer">创建 OAuth App ↗</a></div>
    <p class="muted">创建时：Homepage URL 和 Authorization callback URL 都填 <code>http://127.0.0.1:8788/</code>，勾选 <strong>Enable Device Flow</strong>，创建后复制 Client ID。已有 OAuth App 可在 <a href="https://github.com/settings/developers" target="_blank" rel="noreferrer">GitHub Developer settings</a> 查看。</p>
    <div id="deviceFlow" class="row hidden" style="margin-top:14px"><span>设备码</span><code id="userCode"></code><button id="copyCode">复制代码</button><a id="verifyLink" target="_blank" rel="noreferrer">打开 GitHub</a><span id="loginState" class="muted">等待授权…</span><div id="networkState" class="muted" style="flex-basis:100%"></div></div>
  </section>
  <section id="featuredCard" class="card hidden">
    <div class="row"><div class="grow"><h2>官方推荐</h2><div id="featuredMeta" class="muted"></div></div><button id="save">保存本地草稿</button><button id="commit" class="primary">提交到仓库</button></div>
    <div class="row" style="margin-top:12px;align-items:flex-start"><textarea id="importConfig" class="grow" placeholder="在 DSH 在线插件详情中点“复制配置方式”，把完整 JSON 粘贴到这里"></textarea><button id="import">粘贴配置导入</button></div>
    <div style="overflow:auto"><table><thead><tr><th>顺序</th><th>仓库</th><th>运营说明</th><th>来源</th><th>操作</th></tr></thead><tbody id="featuredBody"></tbody></table></div>
  </section>
</main>
<div id="toast"></div>
<script>
let state={items:[],me:null,device:null,pollTimer:null,clientId:localStorage.getItem('dsh.clientId')||''};
const $=id=>document.getElementById(id);
function toast(message,isError){const el=$('toast');el.textContent=message;el.className=(isError?'error ':'')+'show';clearTimeout(el._t);el._t=setTimeout(()=>el.className='',3600)}
async function api(path,body,method='POST'){const r=await fetch(path,{method,headers:{'Content-Type':'application/json'},body:body===undefined?undefined:JSON.stringify(body)});const text=await r.text();let data={};try{data=text?JSON.parse(text):{}}catch{}if(!r.ok)throw new Error(data.error||text||('HTTP '+r.status));return data}
function esc(v){return String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]))}
function note(msg,err){toast(msg,!!err)}
function renderAccount(){const a=$('account');if(!state.me?.authenticated){a.innerHTML='<span class="pill">未登录</span>';$('loginCard').classList.remove('hidden');$('featuredCard').classList.add('hidden');return}a.innerHTML=`<span class="pill">${esc(state.me.login)} · ${state.me.role==='superadmin'?'超级管理员':'普通管理员'}</span><button id="logout">退出</button>`;$('logout').onclick=logout;$('loginCard').classList.add('hidden');$('featuredCard').classList.remove('hidden');$('commit').disabled=state.me.role!=='superadmin';$('save').disabled=state.me.role!=='superadmin';$('add').disabled=state.me.role!=='superadmin'}
function renderItems(){const tbody=$('featuredBody');tbody.innerHTML='';state.items.forEach((item,i)=>{const tr=document.createElement('tr');tr.innerHTML=`<td class="order"><input data-k="order" type="number" min="1" value="${item.order||i+1}"></td><td><input data-k="fullName" value="${esc(item.owner+'/'+item.repository)}"></td><td class="note"><input data-k="note" value="${esc(item.note||'')}"></td><td><span class="pill">${esc(item.installSource||'github')}</span></td><td class="actions"><button data-a="up">上移</button> <button class="danger" data-a="del">删除</button></td>`;tr.querySelectorAll('input').forEach(input=>input.onchange=()=>updateItem(i,input.dataset.k,input.value));tr.querySelector('[data-a="up"]').onclick=()=>{if(i>0){[state.items[i-1],state.items[i]]=[state.items[i],state.items[i-1]];renderItems()}};tr.querySelector('[data-a="del"]').onclick=()=>{state.items.splice(i,1);renderItems()};tbody.appendChild(tr)});$('featuredMeta').textContent=`${state.items.length} 个推荐条目`}
function updateItem(i,key,value){const item=state.items[i];if(key==='fullName'){const p=value.split('/');item.owner=p.shift()||'';item.repository=p.join('/')||''}else if(key==='order'){item.order=Number(value)||i+1}else{item.note=value}}
function candidates(item){if(Array.isArray(item.installCandidates)&&item.installCandidates.length)return item.installCandidates;const spec=item.installSpecifier||('github:'+item.owner+'/'+item.repository);return[{source:item.installSource||'github',target:item.owner+'/'+item.repository,action:'add',specifier:spec,executable:true,evidenceSource:'launcher'}]}
function payload(){const items=state.items.map((item,i)=>({owner:item.owner,repository:item.repository,description:item.description||'',language:item.language||'',license:item.license||'',pushedAt:item.pushedAt||'',category:item.category||'',version:item.version||'',stars:item.stars||0,verified:!!item.verified,defaultBranch:item.defaultBranch||'main',installSpecifier:item.installSpecifier||('github:'+item.owner+'/'+item.repository),installSource:item.installSource||'github',installExecutable:item.installExecutable!==false,installStatus:item.installStatus||'',installCandidates:candidates(item),sourceSha:item.sourceSha||'',imageUrl:item.imageUrl||'',note:item.note||'',order:item.order||i+1}));return{schemaVersion:1,updatedAtUtc:new Date().toISOString(),items}}
async function me(){try{state.me=await api('/api/auth/me',undefined,'GET')}catch{state.me={authenticated:false}}renderAccount();if(state.me.authenticated)await loadFeatured()}
async function loadFeatured(){const r=await fetch('/api/featured');state.items=(await r.json()).items||[];renderItems()}
async function logout(){await api('/api/auth/logout');state.me=null;renderAccount()}
async function startLogin(){const clientId=$('clientId').value.trim();if(!clientId)return note('请先填写 Client ID',true);localStorage.setItem('dsh.clientId',clientId);try{state.device=await api('/api/auth/start',{clientId});state.device.startedAt=Date.now();$('deviceFlow').classList.remove('hidden');$('userCode').textContent=state.device.userCode;$('verifyLink').href=state.device.verificationUriComplete||state.device.verificationUri;$('networkState').textContent='当前网络：'+(state.device.proxy||'未知')+'。请在 GitHub 页面输入设备码并确认授权。';$('loginState').textContent='等待授权…';clearInterval(state.pollTimer);state.pollTimer=setInterval(pollLogin,Math.max(3,state.device.interval||5)*1000);await pollLogin()}catch(e){note(e.message,true)}}
async function pollLogin(){if(!state.device)return;const expires=state.device.expiresAt||(state.device.startedAt+(state.device.expiresIn||900)*1000);state.device.expiresAt=expires;const remain=Math.max(0,Math.ceil((expires-Date.now())/1000));if(remain<=0){clearInterval(state.pollTimer);$('loginState').textContent='设备码已过期，请重新开始登录';return}try{const r=await api('/api/auth/poll',{clientId:$('clientId').value.trim(),deviceCode:state.device.deviceCode});if(r.pending){$('loginState').textContent=(r.code==='slow_down'?'请稍候…':'等待授权…')+' 剩余 '+remain+' 秒';return}clearInterval(state.pollTimer);state.me=r;renderAccount();await loadFeatured();note('登录成功')}catch(e){const msg=String(e.message||e);if(/SSL|EOF|连接|超时|transport|network/i.test(msg)&&remain>0){$('loginState').textContent='网络暂时中断，自动重试… 剩余 '+remain+' 秒';return}clearInterval(state.pollTimer);$('loginState').textContent=msg;note(msg,true)}}
async function tokenLogin(){const token=prompt('粘贴具有 public_repo 权限的 GitHub Token');if(!token)return;try{state.me=await api('/api/auth/token',{token});renderAccount();await loadFeatured();note('登录成功')}catch(e){note(e.message,true)}}
async function testProxy(){try{const r=await api('/api/proxy-test');note('代理正常：'+r.proxy+' · '+r.elapsedMs+' ms')}catch(e){note(e.message,true)}}
async function save(){try{const p=payload();const r=await api('/api/featured/save',p);state.items=r.featured?.items||p.items;renderItems();note('本地草稿已保存，图标和收藏数已自动补全')}catch(e){note(e.message,true)}}
async function commit(){try{await save();const r=await api('/api/featured/commit',{json:JSON.stringify(payload())});note('已提交：'+r.url);open(r.url,'_blank')}catch(e){note(e.message,true)}}
function importItem(){try{const raw=$('importConfig').value.trim();if(!raw)return note('请先粘贴配置',true);let plugin=null;try{const data=JSON.parse(raw);plugin=data.plugin||data}catch{if(/^https?:\/\/github\.com\//i.test(raw)){const u=new URL(raw);const p=u.pathname.replace(/^\/|\.git$/g,'').split('/');plugin={owner:p[0]||'',repository:p[1]||'',repositoryUrl:raw}}else if(/^[\w.-]+\/[\w.-]+$/.test(raw)){plugin={owner:raw.split('/')[0],repository:raw.split('/')[1]}}}if(!plugin?.owner||!plugin?.repository)throw new Error('无法识别仓库信息');const item={owner:plugin.owner,repository:plugin.repository,description:plugin.description||'',language:plugin.language||'',license:plugin.license||'',pushedAt:plugin.pushedAt||'',category:plugin.category||'',version:plugin.version||'',stars:plugin.stars||0,verified:!!plugin.verified,defaultBranch:plugin.defaultBranch||'main',sourceSha:plugin.sourceSha||'',imageUrl:plugin.imageUrl||'',installStatus:plugin.installStatus||'',installSpecifier:plugin.selectedSpecifier||plugin.installSpecifier||('github:'+plugin.owner+'/'+plugin.repository),installSource:plugin.selectedSource||plugin.installSource||'github',installCandidates:candidates(plugin),note:plugin.note||'',order:state.items.length+1};const i=state.items.findIndex(x=>x.owner===item.owner&&x.repository===item.repository);if(i>=0)state.items[i]={...item,note:state.items[i].note||item.note,order:state.items[i].order||item.order};else state.items.push(item);$('importConfig').value='';renderItems();note('配置已导入，请填写运营说明')}catch(e){note(e.message,true)}}
$('clientId').value=state.clientId;$('startLogin').onclick=startLogin;$('tokenLogin').onclick=tokenLogin;$('testProxy').onclick=testProxy;$('copyCode').onclick=async()=>{try{await navigator.clipboard.writeText($('userCode').textContent);note('设备码已复制')}catch{note('复制失败，请手动选中设备码',true)}};$('save').onclick=save;$('commit').onclick=commit;$('import').onclick=importItem;me();
</script>
</body>
</html>
""";
    }
}
