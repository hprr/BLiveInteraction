using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using EasyDANMU.src;   // WebClient 所在命名空间

await TestHostServerAsync(5513659);   // 任意房间号

static async Task TestHostServerAsync(int tmpRoomId)
{
    try
    {
        //var client = new WebClient(new HttpClient());
        //var (hostList, token) = await client.LoadHostServerAsync(tmpRoomId);

        //Console.WriteLine("=== 原始响应 ===");
        //// 再发一次拿完整 JSON（含 token 与 host_list）
        //var json = await client.GetAsync("/xlive/web-room/v1/index/getDanmuInfo",
        //                                 new Dictionary<string, object> { ["id"] = tmpRoomId, ["type"] = 0 });
        //Console.WriteLine(json);

        //Console.WriteLine("\n=== 解析后 ===");
        //Console.WriteLine($"Token: {token}");
        //if (hostList != null)
        //    foreach (var h in hostList)
        //        Console.WriteLine($"host={h["host"]}, wss_port={h["wss_port"]}");
        //else
        //    Console.WriteLine("hostList 为 null");
        /* ---------- 1. 一次性拿齐所有初始化数据 ---------- */
        using var http = new HttpClient();
        var client = new WebClient(http);

        //await client.InitRoomAsync(tmpRoomId);

        var init = await client.InitRoomAsync(tmpRoomId);

        Console.WriteLine($"[Init] 真实房间号 = {init.RealRoomId}");
        Console.WriteLine($"[Init] 用户UID      = {init.Uid}");
        Console.WriteLine($"[Init] Buvid3       = {init.Buvid}");
        Console.WriteLine($"[Init] Token        = {init.HostServerToken[..20]}...");
        Console.WriteLine($"[Init] 弹幕服务器   = {init.HostServerList.Count} 台");

        /* ---------- 2. 选第一台服务器 ---------- */
        var first = init.HostServerList[0];
        var host = first["host"].ToString()!;
        var wssPort = ((JsonElement)first["wss_port"]).GetInt32();

        /* ---------- 3. 建立 WebSocket ---------- */
        using var ws = new DanmuWsClient(
            host: host,
            wssPort: wssPort,
            roomId: init.RealRoomId,
            token: init.HostServerToken,
            buvid: init.Buvid,
            uid: init.Uid);

        Console.WriteLine("[WS] 连接中...");
        await ws.StartAsync();   // 阻塞直到 Ctrl-C
    }
    catch (Exception ex)
    {
        Console.WriteLine($"异常：{ex.Message}");
    }
}