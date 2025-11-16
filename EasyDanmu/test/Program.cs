using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using EasyDANMU.src;   // WebClient 所在命名空间

await TestHostServerAsync(7777);   // 任意房间号

static async Task TestHostServerAsync(int tmpRoomId)
{

    try
    {

        /* ---------- 1. 一次性拿齐所有初始化数据 ---------- */
        using var http = new HttpClient();

        // ① 手动给的 SESSDATA
        string sessData = "";
        if (!string.IsNullOrEmpty(sessData))
        {
            http.DefaultRequestHeaders.Add("Cookie", sessData);  // 先放 SESSDATA

        }
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
         var ws = new DanmuWsClient(
            host: host,
            wssPort: wssPort,
            roomId: init.RealRoomId,
            token: init.HostServerToken,
            buvid: init.Buvid,
            uid: init.Uid);

        Console.WriteLine("[WS] 连接中...");

        // 1. lambda 快速测试
        HashSet<string> WantCmds = new()
{
    "DANMU_MSG","SEND_GIFT","SUPER_CHAT_MESSAGE"
};

        ws.MessageReceived += (c, cmd) =>
{
    if (cmd.TryGetValue("cmd", out var o) && o is string cmdName &&
        WantCmds.Contains(cmdName))
        Console.WriteLine($"[{cmdName}]");
};

// 2. 正式业务：挂任意 BaseHandler 派生类
ws.set_handler(new MyHandler());

        // 3. 生命周期
        ws.Disconnected += (c, ex) => Console.WriteLine(ex == null ? "正常断开" : $"异常：{ex.Message}");

        await ws.StartAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"异常：{ex.Message}");
    }
}


internal sealed class MyHandler : BaseHandler
{

    // ===================== 心跳 =====================
    protected override void _on_heartbeat(DanmuWsClient client, HeartbeatMessage msg)
        => Console.WriteLine($"❤ 人气值：{msg.popularity}");

    // ===================== 弹幕 =====================
    protected override void _on_danmaku(DanmuWsClient client, DanmakuMessage msg)
    {
        var medal = msg.medal_level > 0 ? $"【{msg.medal_name} Lv.{msg.medal_level}】" : "";
        Console.WriteLine($"💬 {medal}{msg.uname}：{msg.msg}");
    }

    // ===================== 礼物 =====================
    protected override void _on_gift(DanmuWsClient client, GiftMessage msg)
        => Console.WriteLine($"🎁 {msg.uname} 赠送 {msg.gift_name} ×{msg.num}  （{msg.total_coin / 100.0:F2}元）");

    // ===================== 上舰 =====================
    protected override void _on_user_toast_v2(DanmuWsClient client, UserToastV2Message msg)
        => Console.WriteLine($"🚢 {msg.username} 上舰 guard_level={msg.guard_level}");

    // ===================== 醒目留言 =====================
    protected override void _on_super_chat(DanmuWsClient client, SuperChatMessage msg)
        => Console.WriteLine($"💰 醒目留言 ¥{msg.price / 100.0:F2}  {msg.uname}：{msg.message}");

    // ===================== 进入直播间 =====================
    public new void Handle(DanmuWsClient client, Dictionary<string, object> command)
    {
        var cmd = command.GetValueOrDefault("cmd", "")!.ToString()!;
        if (cmd == "INTERACT_WORD")
        {
            var data = command["data"] as Dictionary<string, object>;
            var uname = data?["uname"]?.ToString() ?? "";
            var msg_type = Convert.ToInt32(data?["msg_type"] ?? 0);
            var typeStr = msg_type switch
            {
                1 => "进入",
                2 => "关注",
                3 => "分享",
                4 => "特别关注",
                5 => "互关",
                6 => "点赞",
                _ => "互动"
            };
            Console.WriteLine($"👏 {typeStr}：{uname}");
            return;
        }

        // 继续走原始分发链
        base.Handle(client, command);
    }

    // ===================== 连接断开 =====================
    public override void OnClientStopped(DanmuWsClient client, Exception? exception)
        => Console.WriteLine($"🔌 连接断开：{exception?.Message ?? "正常关闭"}");
}

