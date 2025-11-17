// 文件：WbiSigner.cs
// 框架：.NET 9 （global using 已含 System.Net.Http）

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TShockAPI;

namespace EasyDANMU.src
{
    public sealed class WbiSigner
    {
        /* ========== 常量 ========== */
        private const string WbiUrl = "https://api.bilibili.com/x/web-interface/nav";
        private static readonly TimeSpan KeyTtl = TimeSpan.FromHours(12);

        /* ========== 缓存相关 ========== */
        private static readonly Dictionary<HttpClient, WbiSigner> Cache = new();
        private static readonly object Locker = new();

        /* ========== 实例状态 ========== */
        private readonly HttpClient _client;
        private string _key = string.Empty;
        private DateTime? _updatedAt;

        /* ========== 公开接口 ========== */
        public static WbiSigner ForHttpClient(HttpClient client)
        {
            lock (Locker)
            {
                if (!Cache.TryGetValue(client, out var signer))
                {
                    signer = new WbiSigner(client);
                    Cache.Add(client, signer);
                }
                return signer;
            }
        }

        /* ========== 内部实现 ========== */
        public void Reset()
        {
            _key = string.Empty;
            _updatedAt = DateTime.MinValue;   // 强制过期
        }
        private WbiSigner(HttpClient client) => _client = client;

        public async ValueTask<string> GetKeyAsync()
        {
            if (!string.IsNullOrEmpty(_key) &&
                _updatedAt.HasValue &&
                DateTime.UtcNow - _updatedAt.Value < KeyTtl)
            {
                return _key;
            }

            await RefreshKeyAsync();
            return _key;
        }

        private async Task RefreshKeyAsync()
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, WbiUrl);
                req.Headers.Add("User-Agent", UserAgent);
                var resp = await _client.SendAsync(req);   // .NET 9 自带
                resp.EnsureSuccessStatusCode();

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data").GetProperty("wbi_img");

                var imgUrl = data.GetProperty("img_url").GetString();
                var subUrl = data.GetProperty("sub_url").GetString();
                if (string.IsNullOrEmpty(imgUrl) || string.IsNullOrEmpty(subUrl))
                {
                    throw new InvalidOperationException("[WbiSigner] 缺失 wbi_img url 字段");
                }

                var imgKey = imgUrl.Split('/')[^1].Split('.')[0];
                var subKey = subUrl.Split('/')[^1].Split('.')[0];

                _key = MixKeys(imgKey, subKey);
                _updatedAt = DateTime.UtcNow;

                TShock.Log.ConsoleInfo($"[WbiSigner] 刷新成功 key={_key}");
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleInfo($"[WbiSigner] 刷新失败 {ex.Message}");
                throw;
            }
        }

        private static string MixKeys(string img, string sub)
        {
            ReadOnlySpan<int> map = stackalloc int[]
            {
                46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
    27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13
            };
            var sb = new StringBuilder(32);
            var src = img + sub;
            foreach (var idx in map)
                if (idx < src.Length) sb.Append(src[idx]);
            return sb.ToString();
        }

        public Dictionary<string, object> Sign(Dictionary<string, object> param)
        {
            var key = GetKeyAsync().AsTask().Result;
            var wts = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();

            var tmp = new Dictionary<string, object>(param) { ["wts"] = wts };
            var sorted = new SortedDictionary<string, object>(tmp);
            var filtered = sorted.ToDictionary(
                kv => kv.Key,
                kv => string.Concat((kv.Value?.ToString() ?? string.Empty).Where(c => !"!'()*".Contains(c))));

            var query = string.Join("&",
                filtered.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(query + key));
            var w_rid = Convert.ToHexString(hash).ToLowerInvariant();

            var signed = new Dictionary<string, object>(param)
            {
                ["wts"] = wts,
                ["w_rid"] = w_rid
            };
            TShock.Log.ConsoleInfo($"[WbiSigner] 生成签名 {w_rid}");
            return signed;
        }

        //internal void Reset()
        //{
        //    _key = string.Empty;
        //}

        /* 把之前缺失的 Consts 内容直接内嵌 */
        public const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    }
}