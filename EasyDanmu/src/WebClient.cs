using System.Text.Json;
using System.Text.RegularExpressions;


namespace EasyDANMU.src
{
    public readonly record struct RoomInitResult(
        long Uid,
        string Buvid,
        int RealRoomId,
        long OwnerUid,
        List<Dictionary<string, object>> HostServerList,
        string HostServerToken
    );

    public sealed class WebClient
    {
        private readonly HttpClient _http;
        private readonly WbiSigner _wbi;

        public WebClient(HttpClient http)
        {
            _http = http;
            _wbi = WbiSigner.ForHttpClient(http);
        }

        /* ---------- 公共入口 ---------- */
        public async Task<RoomInitResult> InitRoomAsync(int tmpRoomId, CancellationToken ct = default)
        {

            // ① 先把短号换成真实房间号
            var roomTask = LoadRealRoomIdAsync(tmpRoomId, ct);
            await roomTask;                         // 必须先完成，才能拿到 RealRoomId
            int realRoomId = roomTask.Result.roomId;

            // ② 后续全部用 realRoomId
            var uidTask = LoadUidAsync(ct);
            var buvidTask = LoadBuvidAsync(ct);
            var hostTask = LoadHostServerAsync(realRoomId, ct);   // 原来是 tmpRoomId

            await Task.WhenAll(uidTask, buvidTask, roomTask, hostTask);

            Console.WriteLine("realroomid : " + roomTask.Result.roomId.ToString());
            return new RoomInitResult(
                Uid: uidTask.Result,
                Buvid: buvidTask.Result,
                RealRoomId: roomTask.Result.roomId,
                OwnerUid: roomTask.Result.ownerUid,
                HostServerList: hostTask.Result.hostList ?? new(),
                HostServerToken: hostTask.Result.token ?? string.Empty
            );
        }

        /* ---------- 内部数据源 ---------- */
        public async Task<long> LoadUidAsync(CancellationToken ct = default)
        {
            try
            {
                var json = await _http.GetStringAsync("https://api.bilibili.com/x/web-interface/nav", ct);
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                return data.GetProperty("isLogin").GetBoolean() ? data.GetProperty("mid").GetInt64() : 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] LoadUidAsync: {ex.Message}");
                return 0;
            }
        }

        public async Task<string> LoadBuvidAsync(CancellationToken ct = default)
        {
            try
            {
                using var handler = new HttpClientHandler { UseCookies = false };
                using var temp = new HttpClient(handler);
                temp.DefaultRequestHeaders.Add("User-Agent", WbiSigner.UserAgent);

                var resp = await temp.GetAsync("https://www.bilibili.com/", ct);
                if (!resp.Headers.TryGetValues("Set-Cookie", out var cookies)) return string.Empty;

                foreach (var c in cookies)
                {
                    var m = Regex.Match(c, @"buvid3=([^;]+)");
                    if (m.Success) return m.Groups[1].Value;
                }
                return string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] LoadBuvidAsync: {ex.Message}");
                return string.Empty;
            }
        }

        public async Task<(int roomId, long ownerUid)> LoadRealRoomIdAsync(int tmpRoomId, CancellationToken ct = default)
        {
            try
            {
                var json = await _http.GetStringAsync(
                    $"https://api.live.bilibili.com/room/v1/Room/get_info?room_id={tmpRoomId}", ct);
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                return (data.GetProperty("room_id").GetInt32(),
                        data.GetProperty("uid").GetInt64());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] LoadRealRoomIdAsync: {ex.Message}");
                return (0, 0);
            }
        }

        public async Task<(List<Dictionary<string, object>> hostList, string token)>
            LoadHostServerAsync(int roomId, CancellationToken ct = default)
        {
            try
            {
                // 删掉临时客户端，直接复用 _http（已带 SESSDATA）
                var json = await GetAsync("/xlive/web-room/v1/index/getDanmuInfo",
                                          new Dictionary<string, object> { ["id"] = roomId, ["type"] = 0 }, ct);

                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.GetProperty("code").GetInt32() == -352)
                {
                    Console.WriteLine("[WebClient] 收到 -352，刷新 wbi key 后重试");
                    _wbi.Reset();          // 让下次 Sign 重新拉 key
                    json = await GetAsync("/xlive/web-room/v1/index/getDanmuInfo",
                                          new Dictionary<string, object> { ["id"] = roomId, ["type"] = 0 }, ct);
                    doc = JsonDocument.Parse(json);
                    root = doc.RootElement;
                }

                var data = root.GetProperty("data");
                var list = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
                                 data.GetProperty("host_list").GetRawText());
                var token = data.GetProperty("token").ToString();
                return (list, token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] LoadHostServerAsync: {ex.Message}");
                return (null, null);
            }
        }

        /* ---------- WBI 通用 GET ---------- */
        public async Task<string> GetAsync(string path,
                                           Dictionary<string, object> param,
                                           CancellationToken ct = default)
        {
            var signed = _wbi.Sign(param);
            var query = string.Join("&",
                signed.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value.ToString())}"));
            var url = $"https://api.live.bilibili.com{path}?{query}";

            Console.WriteLine($"[WebClient] 请求路由: {url}");

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", WbiSigner.UserAgent);
            //req.Headers.Add("Referer", $"https://live.bilibili.com/{param.GetValueOrDefault("id")}");
            //req.Headers.Add("Origin", "https://live.bilibili.com");

            var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }
    }
}