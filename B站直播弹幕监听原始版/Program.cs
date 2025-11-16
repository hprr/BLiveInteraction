using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using client_web;
using ws_base;
using models_web;
using Handlers;

namespace BliveDM.Sample
{
    internal static class Program
    {
        private static readonly List<int> TestRoomIds = new() { 7777 };
        private const string SessData = "";

        private static ILoggerFactory _loggerFactory;
        private static HttpClient _sharedClient;

        private static async Task Main()
        {
            InitServices();
            try
            {
                await RunSingleClient();
                //await RunMultiClients();
            }
            catch(Exception e)
            {
                Console.WriteLine("发生错误{error}",e.Message);
            }
            finally
            {
                _sharedClient?.Dispose();
                _loggerFactory?.Dispose();
            }
        }

        private static void InitServices()
        {
            _loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
            var handler = new HttpClientHandler { UseCookies = true };
            _sharedClient = new HttpClient(handler);
            _sharedClient.DefaultRequestHeaders.Add("User-Agent", BliveDM.Utils.USER_AGENT);
            var cookie = new System.Net.Cookie("SESSDATA", SessData, "/", "bilibili.com");
            handler.CookieContainer.Add(new Uri("https://bilibili.com"), cookie);
        }

        private static async Task RunSingleClient()
        {
            var roomId = TestRoomIds[new Random().Next(TestRoomIds.Count)];
            var logger = _loggerFactory.CreateLogger($"Room-{roomId}");
            var client = new BLiveClient(roomId, session: _sharedClient, logger: logger);
            var handler = new MyHandler(_loggerFactory.CreateLogger<MyHandler>());
            client.set_handler(handler);

            client.start();

            // 阻塞到 Ctrl-C 或进程被 Kill
            await Task.Delay(Timeout.Infinite);
        }

        private static async Task RunMultiClients()
        {
            var clients = new List<BLiveClient>();
            var handler = new MyHandler(_loggerFactory.CreateLogger<MyHandler>());
            foreach (var roomId in TestRoomIds)
            {
                var logger = _loggerFactory.CreateLogger($"Room-{roomId}");
                var client = new BLiveClient(roomId, session: _sharedClient, logger: logger);
                client.set_handler(handler);

                client.start();
                clients.Add(client);
            }

            try
            {
                await Task.WhenAll(clients.Select(c => c.join()));
            }
            finally
            {
                await Task.WhenAll(clients.Select(c => c.stop_and_close()));
            }
            }
        }

    internal sealed class MyHandler : BaseHandler
    {
        private readonly ILogger _log;
        public MyHandler(ILogger<MyHandler> log) => _log = log;

        // ===================== 心跳 =====================
        protected override void _on_heartbeat(WebSocketClientBase client, HeartbeatMessage msg)
            => Console.WriteLine($"❤ 人气值：{msg.popularity}");

        // ===================== 弹幕 =====================
        protected override void _on_danmaku(WebSocketClientBase client, DanmakuMessage msg)
        {
            var medal = msg.medal_level > 0 ? $"【{msg.medal_name} Lv.{msg.medal_level}】" : "";
            Console.WriteLine($"💬 {medal}{msg.uname}：{msg.msg}");
        }

        // ===================== 礼物 =====================
        protected override void _on_gift(WebSocketClientBase client, GiftMessage msg)
            => Console.WriteLine($"🎁 {msg.uname} 赠送 {msg.gift_name} ×{msg.num}  （{msg.total_coin / 100.0:F2}元）");

        // ===================== 上舰 =====================
        protected override void _on_user_toast_v2(WebSocketClientBase client, UserToastV2Message msg)
            => Console.WriteLine($"🚢 {msg.username} 上舰 guard_level={msg.guard_level}");

        // ===================== 醒目留言 =====================
        protected override void _on_super_chat(WebSocketClientBase client, SuperChatMessage msg)
            => Console.WriteLine($"💰 醒目留言 ¥{msg.price / 100.0:F2}  {msg.uname}：{msg.message}");

        // ===================== 进入直播间 =====================
        public new void Handle(WebSocketClientBase client, Dictionary<string, object> command)
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
        public override void OnClientStopped(WebSocketClientBase client, Exception? exception)
            => Console.WriteLine($"🔌 连接断开：{exception?.Message ?? "正常关闭"}");
    }

}