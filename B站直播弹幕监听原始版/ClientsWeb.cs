#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ws_base;
using BliveDM;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using WebSocketSharp;


namespace client_web
{
    public static class Utils
    {
        public const string USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    }

    public class BLiveClient : WebSocketClientBase
    {
        private const string UID_INIT_URL = "https://api.bilibili.com/x/web-interface/nav";
        public const string WBI_INIT_URL = UID_INIT_URL;
        private const string BUVID_INIT_URL = "https://www.bilibili.com/";
        private const string ROOM_INIT_URL = "https://api.live.bilibili.com/room/v1/Room/get_info";
        private const string DANMAKU_SERVER_CONF_URL = "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo";
        protected string _buvid = "";

        private static readonly List<Dictionary<string, object>> DEFAULT_DANMAKU_SERVER_LIST = new()
        {
            new()
            {
                ["host"] = "broadcastlv.chat.bilibili.com",
                ["port"] = 2243,
                ["wss_port"] = 443,
                ["ws_port"] = 2244
            }
        };

        private static readonly ConditionalWeakTable<HttpClient, _WbiSigner> _sessionToWbiSigner = new();

        private static _WbiSigner GetWbiSigner(HttpClient session, ILogger logger)
        {
            if (!_sessionToWbiSigner.TryGetValue(session, out var signer))
            {
                signer = new _WbiSigner(session, logger);
                _sessionToWbiSigner.Add(session, signer);
            }
            return signer;
        }

        private readonly ILogger _logger;
        protected readonly _WbiSigner _wbiSigner;
        private readonly int _tmpRoomId;
        private long? _uid;

        private long? _roomOwnerUid;
        private List<Dictionary<string, object>> _hostServerList;
        private string _hostServerToken;
        private HttpClient _session;

        public BLiveClient(int roomId, long? uid = null, HttpClient session = null,
            int heartbeatInterval = 30, ILogger logger = null)
            : base(null, heartbeatInterval)
        {
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

            if (session == null)
            {
                var handler = new HttpClientHandler
                {
                    UseCookies = true,
                    CookieContainer = new CookieContainer()
                };

                // ✅ 把登录 Cookie 塞进去
                handler.CookieContainer.Add(new Uri("https://bilibili.com"),
                                            new Cookie("SESSDATA", "6a7c440a%2C1778211780%2C67ab1%2Ab1CjDTHCFZMqbcTU1pL9fVD7N8ta5qjIzjAA-NTdcQb0x7vh2kpWBbVxAAUYAgzkVNl_8SVkU4c1JBeUxNWXZ5dEJadHA0UXlMUEE2dWk0dXh1YVp2VEw3V0NmSmNZYTY0NHpBNmhsM2piN1ZLMjNOaDh6LXNLMm5GaXpIeC1wZ1NuMXNOUzdhZFhnIIEC"));

                _session = new HttpClient(handler);
                _ownSession = true;
            }
            else
            {
                _session = session;
                _ownSession = false;
            }

            _wbiSigner = GetWbiSigner(_session, _logger);
            _tmpRoomId = roomId;
            _uid = uid;
        }

        public int TmpRoomId => _tmpRoomId;
        public long? RoomOwnerUid => _roomOwnerUid;
        public long? Uid => _uid;

        public override async Task<bool> init_room()
        {
            _logger.LogInformation("【init_room】开始，room={RoomId}", _tmpRoomId);

            if (_uid == null)
            {
                _logger.LogInformation("【init_room】准备 _InitUid");
                if (!await _InitUid())
                {
                    _logger.LogWarning("【init_room】_InitUid 失败，设为 UID=0");
                    _uid = 0;
                }
            }

            if (string.IsNullOrEmpty(_GetBuvid()))
            {
                _logger.LogInformation("【init_room】准备 _InitBuvid");
                if (!await _InitBuvid())
                    _logger.LogWarning("【init_room】_InitBuvid 失败，继续");
            }

            bool res = true;

            _logger.LogInformation("【init_room】准备 _InitRoomIdAndOwner");
            if (!await _InitRoomIdAndOwner())
            {
                _logger.LogError("【init_room】_InitRoomIdAndOwner 失败，降级");
                res = false;
                _room_id = _tmpRoomId;
                _roomOwnerUid = 0;
            }

            _logger.LogInformation("【init_room】准备 _InitHostServer");
            if (!await _InitHostServer())
            {
                _logger.LogError("【init_room】_InitHostServer 失败，降级");
                res = false;
                _hostServerList = DEFAULT_DANMAKU_SERVER_LIST;
                _hostServerToken = null;
            }

            _logger.LogInformation("【init_room】完成，返回值={Res}", res);
            return res;
        }

        private async Task<bool> _InitUid()
        {
            var cookies = _session.GetCookies(new Uri(UID_INIT_URL));
            var sessData = cookies["SESSDATA"];
            if (sessData == null)
            {
                _uid = 0;
                return true;
            }

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, UID_INIT_URL);
                req.Headers.Add("User-Agent", Utils.USER_AGENT);
                var res = await _session.SendAsync(req);
                _ = await res.Content.ReadAsStringAsync(); // ← 必须读响应体，cookie 才会被设置
                if (!res.IsSuccessStatusCode)
                {
                    _logger.LogWarning("room={RoomId} _InitUid() failed, status={Status}", _tmpRoomId, res.StatusCode);
                    return false;
                }

                var json = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.GetProperty("code").GetInt32() != 0)
                {
                    if (root.GetProperty("code").GetInt32() == -101)
                    {
                        _uid = 0;
                        return true;
                    }
                    _logger.LogWarning("room={RoomId} _InitUid() failed, message={Message}", _tmpRoomId, root.GetProperty("message").GetString());
                    return false;
                }

                var data = root.GetProperty("data");
                if (!data.GetProperty("isLogin").GetBoolean())
                {
                    _uid = 0;
                }
                else
                {
                    _uid = data.GetProperty("mid").GetInt64();
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "room={RoomId} _InitUid() exception", _tmpRoomId);
                return false;
            }
        }




        private async Task<bool> _InitBuvid()
        {
            try
            {
                // 使用独立的HttpClient获取buvid，避免CookieContainer干扰
                using var handler = new HttpClientHandler { UseCookies = false };
                using var tempClient = new HttpClient(handler);
                tempClient.DefaultRequestHeaders.Add("User-Agent", Utils.USER_AGENT);

                var response = await tempClient.GetAsync("https://www.bilibili.com/");

                if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                {
                    foreach (var cookie in cookies)
                    {
                        var match = Regex.Match(cookie, @"buvid3=([^;]+)");
                        if (match.Success)
                        {
                            _buvid = match.Groups[1].Value;
                            _logger.LogInformation("【_InitBuvid】成功获取 buvid3: {buvid3}", _buvid);
                            return true;
                        }
                    }
                }

                _logger.LogWarning("【_InitBuvid】未找到 buvid3");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "【_InitBuvid】异常");
                return false;
            }
        }

        private string _GetBuvid()
        {
            // 优先使用已缓存的值
            if (!string.IsNullOrEmpty(_buvid))
                return _buvid;

            // 如果缓存为空，尝试从CookieContainer获取（备用方案）
            var cookies = _session.GetCookies(new Uri(BUVID_INIT_URL));
            return cookies["buvid3"]?.Value ?? "";
        }



        private async Task<bool> _InitRoomIdAndOwner()
        {
            _logger.LogInformation("【_InitRoomIdAndOwner】开始");
            try
            {
                var url = $"{ROOM_INIT_URL}?room_id={_tmpRoomId}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent", Utils.USER_AGENT);
                var res = await _session.SendAsync(req);
                if (!res.IsSuccessStatusCode)
                {
                    _logger.LogError("【_InitRoomIdAndOwner】HTTP 状态码：{Status}", res.StatusCode);
                    return false;
                }

                var json = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.GetProperty("code").GetInt32() != 0)
                {
                    _logger.LogError("【_InitRoomIdAndOwner】B站返回 code={Code}, message={Msg}",
                                     root.GetProperty("code").GetInt32(),
                                     root.GetProperty("message").GetString());
                    return false;
                }

                var data = root.GetProperty("data");
                _room_id = data.GetProperty("room_id").GetInt32();
                _roomOwnerUid = data.GetProperty("uid").GetInt32();
                _logger.LogInformation("【_InitRoomIdAndOwner】成功，真实 room_id={RealRoomId}", _room_id);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "【_InitRoomIdAndOwner】异常");
                return false;
            }
        }

        private async Task<bool> _InitHostServer()
        {
            
            _logger.LogInformation("【_InitHostServer】开始");
            if (_wbiSigner.NeedRefreshWbiKey)
            {
                await _wbiSigner.RefreshWbiKey();
                if (string.IsNullOrEmpty(_wbiSigner.WbiKey))
                {
                    _logger.LogError("【_InitHostServer】没有 wbi key");
                    return false;
                }
            }

            try
            {
                var param = _wbiSigner.AddWbiSign(new Dictionary<string, object>
                {
                    ["id"] = _room_id,
                    ["type"] = 0
                });

                var url = $"https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?{BuildQueryString(param)}";
                _logger.LogInformation("【_InitHostServer】请求 URL：{Url}", url);
                
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent", Utils.USER_AGENT);
                //req.Headers.Add("Referer", $"https://live.bilibili.com/{_room_id}");
                //req.Headers.Add("Origin", "https://live.bilibili.com");
                var res = await _session.SendAsync(req);
                if (!res.IsSuccessStatusCode)
                {
                    _logger.LogError("【_InitHostServer】HTTP 状态码：{Status}", res.StatusCode);
                    return false;
                }

                var json = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.GetProperty("code").GetInt32() != 0)
                {
                    _logger.LogError("【_InitHostServer】B站返回 code={Code}, message={Msg}",
                                     root.GetProperty("code").GetInt32(),
                                     root.GetProperty("message").GetString());
                    if (root.GetProperty("code").GetInt32() == -352)
                        _wbiSigner.Reset();
                    return false;
                }

                var data = root.GetProperty("data");
                _hostServerList = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(data.GetProperty("host_list").GetRawText());
                _hostServerToken = data.GetProperty("token").ToString();
                _logger.LogInformation("【_InitHostServer】成功，获取到 {Count} 台弹幕服务器", _hostServerList?.Count ?? 0);
                return _hostServerList != null && _hostServerList.Count > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "【_InitHostServer】异常");
                return false;
            }
        }
        private static string BuildQueryString(Dictionary<string, object> param)
        {
            var parts = param.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value.ToString())}");
            return string.Join("&", parts);
        }

        protected override async Task _on_before_ws_connect(int retryCount)
        {
            if (retryCount > 0)
            {
                _logger.LogInformation("重连时重新获取 token");
                _hostServerToken = null;          // 强制重新拿
                await _InitHostServer();          // 会重新带 WBI 请求
            }
            int reinitPeriod = Math.Max(3, _hostServerList?.Count ?? 1);
            if (retryCount > 0 && retryCount % reinitPeriod == 0)
                _needInitRoom = true;
            await base._on_before_ws_connect(retryCount);
        }

        protected override string _get_ws_url(int retryCount)
        {
            var server = _hostServerList[retryCount % _hostServerList.Count];
            return $"wss://{server["host"]}:{server["wss_port"]}/sub";
        }

        protected override async Task _send_auth()
        {
            // 确保buvid不为空
            var buvid = _GetBuvid();
            //buvid = "";
            if (string.IsNullOrEmpty(buvid))
            {
                _logger.LogError("【致命】buvid为空，AUTH包将被服务器拒绝");
            }

            var authParams = new Dictionary<string, object>
            {
                ["uid"] = _uid ?? 0,
                ["roomid"] = _room_id,
                ["protover"] = 3,  // 确保使用协议版本3
                ["buvid"] = buvid,
                ["platform"] = "web",
                ["type"] = 2,
            };

            // 关键修复：必须包含token参数
            if (!string.IsNullOrEmpty(_hostServerToken))
            {
                authParams["key"] = _hostServerToken;
                _logger.LogInformation("【_send_auth】添加token: {token}", _hostServerToken);
            }
            else
            {
                _logger.LogError("【_send_auth】token为空，认证可能失败");
            }

            Console.WriteLine("【C#】使用 token = " + _hostServerToken);
            Console.WriteLine("【C#】使用服务器 = " + JsonSerializer.Serialize(_hostServerList[0]));

            Console.WriteLine("[AUTH JSON] " + JsonSerializer.Serialize(authParams));

            var pkt = _make_packet(authParams, Operation.AUTH);
            Console.WriteLine("[AUTH RAW] " + Convert.ToHexString(pkt));
            await _ws.SendAsync(new ArraySegment<byte>(pkt), WebSocketMessageType.Binary, true, CancellationToken.None);
        }


    }

    public class _WbiSigner
    {
        private const string UID_INIT_URL = "https://api.bilibili.com/x/web-interface/nav";
        public const string WBI_INIT_URL = UID_INIT_URL;
        private const string BUVID_INIT_URL = "https://www.bilibili.com";
        private const string ROOM_INIT_URL = "https://api.live.bilibili.com/room/v1/Room/get_info";
        private const string DANMAKU_SERVER_CONF_URL = "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo";
        private static readonly int[] WBI_KEY_INDEX_TABLE = {
            46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
            27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13
        };

        private static readonly TimeSpan WBI_KEY_TTL = TimeSpan.FromHours(11) + TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(30);

        private readonly HttpClient _session;
        private readonly ILogger _logger;
        private string _wbiKey = "";
        private DateTime? _lastRefreshTime;

        public string WbiKey => _wbiKey;
        public bool NeedRefreshWbiKey =>
            string.IsNullOrEmpty(_wbiKey) ||
            (_lastRefreshTime.HasValue && DateTime.Now - _lastRefreshTime.Value >= WBI_KEY_TTL);

        public _WbiSigner(HttpClient session, ILogger logger)
        {
            _session = session;
            _logger = logger;
        }

        public void Reset()
        {
            _wbiKey = "";
            _lastRefreshTime = null;
        }

        public async Task RefreshWbiKey()
        {
            var key = await _GetWbiKey();
            if (!string.IsNullOrEmpty(key))
            {
                _wbiKey = key;
                _lastRefreshTime = DateTime.Now;
            }
        }

        private async Task<string> _GetWbiKey()
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, WBI_INIT_URL);
                req.Headers.Add("User-Agent", Utils.USER_AGENT);
                var res = await _session.SendAsync(req);
                if (!res.IsSuccessStatusCode)
                {
                    _logger.LogWarning("WbiSigner failed to get wbi key: status={Status}", res.StatusCode);
                    return "";
                }

                var json = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data").GetProperty("wbi_img");
                var imgKey = data.GetProperty("img_url").GetString().Split('/')[^1].Split('.')[0];
                var subKey = data.GetProperty("sub_url").GetString().Split('/')[^1].Split('.')[0];

                var shuffled = imgKey + subKey;
                var keyChars = new char[WBI_KEY_INDEX_TABLE.Length];
                for (int i = 0; i < WBI_KEY_INDEX_TABLE.Length; i++)
                {
                    int idx = WBI_KEY_INDEX_TABLE[i];
                    if (idx < shuffled.Length)
                        keyChars[i] = shuffled[idx];
                }

                return new string(keyChars).TrimEnd('\0');
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WbiSigner failed to get wbi key");
                return "";
            }
        }

        public Dictionary<string, object> AddWbiSign(Dictionary<string, object> param)
        {
            if (string.IsNullOrEmpty(_wbiKey)) return param;

            var wts = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
            var toSign = new Dictionary<string, object>(param) { ["wts"] = wts };

            var sorted = new SortedDictionary<string, object>(toSign);
            var filtered = new Dictionary<string, object>();
            foreach (var kv in sorted)
            {
                var sb = new StringBuilder();
                foreach (var ch in kv.Value.ToString())
                {
                    if ("!'()*".Contains(ch)) continue;
                    sb.Append(ch);
                }
                filtered[kv.Key] = sb.ToString();
            }

            var query = string.Join("&", filtered.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value.ToString())}"));
            var strToSign = query + _wbiKey;
            var w_rid = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(strToSign))).ToLowerInvariant();

            return new Dictionary<string, object>(param)
            {
                ["wts"] = wts,
                ["w_rid"] = w_rid
            };
        }
    }

    public static class DictExt
    {
        public static System.Collections.Specialized.NameValueCollection ToNameValueCollection(this Dictionary<string, object> dict)
        {
            var nvc = new System.Collections.Specialized.NameValueCollection();
            foreach (var kv in dict)
                nvc[kv.Key] = kv.Value?.ToString();
            return nvc;
        }
    }

    public static class HttpExt
    {
        public static System.Net.CookieCollection GetCookies(this HttpClient client, Uri uri)
        {
            if (client.GetType().GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(client) is HttpClientHandler handler)
                return handler.CookieContainer.GetCookies(uri);
            return new System.Net.CookieCollection();
        }
    }
}