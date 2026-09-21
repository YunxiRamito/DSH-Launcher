using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    internal sealed class BilibiliAvatarResult
    {
        public byte[] Bytes;
        public string AvatarUrl;
        public string Error;
        public bool Ok
        {
            get { return Bytes != null && Bytes.Length > 0; }
        }
    }

    internal static class BilibiliProfileService
    {
        private const string ProfileApi =
            "https://api.bilibili.com/x/web-interface/card?mid=32823052&photo=true";
        private const string ProfilePage =
            "https://space.bilibili.com/32823052";
        private static Task<BilibiliAvatarResult> _avatarTask;

        internal static Task<BilibiliAvatarResult> FetchAvatarOnceAsync()
        {
            if (_avatarTask == null)
            {
                _avatarTask = FetchAvatarAsync();
            }

            return _avatarTask;
        }

        internal static async Task<BilibiliAvatarResult> FetchAvatarAsync()
        {
            BilibiliAvatarResult result = new BilibiliAvatarResult();
            try
            {
                using (HttpClientHandler handler = new HttpClientHandler
                {
                    UseProxy = false
                })
                using (HttpClient client = new HttpClient(handler))
                {
                    ProxySupport.Apply(handler);
                    client.Timeout = TimeSpan.FromSeconds(20);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                        + "AppleWebKit/537.36 Chrome/140.0 Safari/537.36");
                    client.DefaultRequestHeaders.Referrer =
                        new Uri(ProfilePage);
                    client.DefaultRequestHeaders.Accept.ParseAdd(
                        "application/json,text/plain,*/*");

                    string json = await client.GetStringAsync(ProfileApi);
                    using (JsonDocument document = JsonDocument.Parse(json))
                    {
                        JsonElement root = document.RootElement;
                        JsonElement data;
                        JsonElement card;
                        JsonElement face;
                        if (!root.TryGetProperty("data", out data)
                            || !data.TryGetProperty("card", out card)
                            || !card.TryGetProperty("face", out face)
                            || face.ValueKind != JsonValueKind.String)
                        {
                            result.Error = "B 站资料接口没有返回头像地址。";
                            return result;
                        }

                        result.AvatarUrl = face.GetString();
                    }

                    if (String.IsNullOrWhiteSpace(result.AvatarUrl))
                    {
                        result.Error = "B 站头像地址为空。";
                        return result;
                    }

                    result.Bytes = await client.GetByteArrayAsync(
                        result.AvatarUrl);
                }
            }
            catch (Exception exception)
            {
                result.Error = exception.Message;
            }

            return result;
        }
    }
}
