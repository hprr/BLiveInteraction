using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EasyDANMU.src;
using Microsoft.Xna.Framework;
using Org.BouncyCastle.Ocsp;
using Terraria;
using TShockAPI;

namespace BLiveInteract
{
    /// <summary>
    /// 弹幕监听后台单例，生命周期随插件启停。<br/>
    /// </summary>
    public sealed class BLiveListener
    {
        private static BLiveListener? _instance;
        private static readonly object _lock = new();

        private DanmuWsClient _wsClient = default!;
        private CancellationTokenSource _cts = default!;
        private bool _running;
        private int _roomId;

        private HttpClient _http = default!;   // 生命周期跟随 Listener
        private BroadcastManager? _broadcast;
        private int _broadcastMaxPerSecond;



        /* ------------- 单例 ------------- */
        public static BLiveListener Instance
        {
            get
            {
                lock (_lock)
                {
                    return _instance ??= new BLiveListener();
                }
            }
        }

        public bool IsRunning => _running;

        /* ------------- 生命周期 ------------- */
        public void Start(int roomId, string sessdata)
        {
            lock (_lock)
            {
                if (_running) return;
                _running = true;
                _roomId = roomId;
                _cts = new CancellationTokenSource();
            }

            // 在这里 new，不 using
            _http = new HttpClient();
            // 仅当配置提供了有效 SESSDATA 时才设置 Cookie
            if (!string.IsNullOrWhiteSpace(sessdata))
            {
                _http.DefaultRequestHeaders.Add("Cookie", sessdata);
            }
            // 创建广播管理器，依据配置
            var cfg = BLiveInsteract.Config;
            if (cfg.EnableBroadcastThrottle)
            {
                _broadcastMaxPerSecond = Math.Max(1, cfg.MaxBroadcastPerSecond);
                _broadcast = new BroadcastManager(_broadcastMaxPerSecond);
            }
            else
            {
                _broadcastMaxPerSecond = 0;
                _broadcast = null; // 禁用限速时直接走 SendMessage
            }

            // 后台跑整个流程
            _ = Task.Run(async () =>
            {
                try
                {
                    await WorkAsync(_http, _cts.Token);
                }
                catch (Exception ex)
                {
                    TShock.Log.Error($"[BLive] 监听异常：{ex}");
                }
                finally
                {
                    Stop();
                }
            });
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_running) return;
                _cts.Cancel();
                _wsClient?.Dispose();
                _http?.Dispose();   // 与 Listener 同生命周期
                // 关闭广播管理器
                _broadcast?.Dispose();
                _broadcast = null;
                _broadcastMaxPerSecond = 0;
                _running = false;
                TShock.Log.ConsoleInfo("[BLive] 弹幕监听已停止");
            }
        }

        internal void ApplyBroadcastConfigFrom(Configuration cfg)
         {
             // 运行中也允许调整限速策略
             if (!_running)
             {
                 // 未运行无需处理，Start 会按新配置创建
                 return;
             }

            var enabled = cfg.EnableBroadcastThrottle;
            var desired = Math.Max(1, cfg.MaxBroadcastPerSecond);

            if (!enabled)
            {
                // 关闭限速
                _broadcast?.Dispose();
                _broadcast = null;
                _broadcastMaxPerSecond = 0;
                TShock.Log.ConsoleInfo("[BLive] 已关闭广播限速");
                return;
            }

            // 开启限速
            if (_broadcast == null)
            {
                _broadcastMaxPerSecond = desired;
                _broadcast = new BroadcastManager(_broadcastMaxPerSecond);
                TShock.Log.ConsoleInfo($"[BLive] 已启用广播限速：{_broadcastMaxPerSecond}/s");
                return;
            }

            if (desired != _broadcastMaxPerSecond)
            {
                _broadcast?.Dispose();
                _broadcastMaxPerSecond = desired;
                _broadcast = new BroadcastManager(_broadcastMaxPerSecond);
                TShock.Log.ConsoleInfo($"[BLive] 已调整广播限速：{_broadcastMaxPerSecond}/s");
            }
         }

        /* ------------- 核心流程（完全沿用你原来 Program.cs 逻辑） ------------- */
        private async Task WorkAsync(HttpClient http, CancellationToken token)
        {
            /* ---------- 1. 初始化房间 ---------- */

            //// ① 手动给的 SESSDATA
            //string sessData = "";
            //                                                                                                                                                                                                                                                               // ② 程序继续自动拿 buvid3 / uid / token
            //http.DefaultRequestHeaders.Add("Cookie", sessData);  // 先放 SESSDATA

            var client = new EasyDANMU.src.WebClient(http);
            var init = await client.InitRoomAsync(_roomId);

            TShock.Log.ConsoleInfo($"[BLive] 房间初始化完成，真实房间号={init.RealRoomId}");

            /* ---------- 2. 选第一台服务器 ---------- */
            var first = init.HostServerList.FirstOrDefault();
            if (first == null)
            {
                TShock.Log.Warn("[BLive] 未获取到直播服务器列表");
                throw new InvalidOperationException("没有可用的直播服务器");
            }
            var hostObj = first.GetValueOrDefault("host");
            var portObj = first.GetValueOrDefault("wss_port");
            var host = hostObj?.ToString() ?? string.Empty;
            var portStr = portObj?.ToString();
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(portStr) || !int.TryParse(portStr, out var port))
            {
                TShock.Log.Warn("[BLive] 服务器信息不完整，host/port 无法解析");
                throw new InvalidOperationException("直播服务器信息无效");
            }

            /* ---------- 3. 建 WebSocket ---------- */
            _wsClient = new DanmuWsClient(
                host: host,
                wssPort: port,
                roomId: init.RealRoomId,
                token: init.HostServerToken,
                buvid: init.Buvid,
                uid: init.Uid);


            /* ---------- 4. 挂事件（改用 BaseHandler 解析，保留现有广播逻辑） ---------- */
            // ① 先准备一个继承官方 BaseHandler 的内部类
            _wsClient.set_handler(new TShockHandler(this));   // 把当前 BLiveListener 实例传进去，方便调 Broadcast

            _wsClient.Disconnected += (c, ex) =>
            {
                TShock.Log.ConsoleInfo(ex == null ? "[BLive] 弹幕连接已正常关闭" : $"[BLive] 弹幕连接异常：{ex.Message}");
            };

            /* ---------- 5. 启动（阻塞到断线） ---------- */
            await _wsClient.StartAsync();
        }


        // 放在 BLiveListener 类内部，private 即可
        private sealed class TShockHandler : EasyDANMU.src.BaseHandler
        {
            private readonly BLiveListener _parent;
            public TShockHandler(BLiveListener parent) => _parent = parent;

            // 弹幕
            protected override void _on_danmaku(DanmuWsClient client, DanmakuMessage msg)
            {
                var cfg = BLiveInsteract.Config;
                if (!cfg.DanmuToGame) return;
                var medal = msg.medal_level > 0 ? $"[c/7734db:<{msg.medal_name} Lv.{msg.medal_level}>]" : "";
                _parent.Broadcast($"[弹幕] {medal}[c/98db34:{msg.uname}]: {msg.msg}", cfg.DanmuColor, cfg.MaxMsgLen);
            }

            // 礼物
            protected override void _on_gift(DanmuWsClient client, GiftMessage msg)
            {
                var cfg = BLiveInsteract.Config;
                if (!cfg.GiftToGame) return;
                _parent.Broadcast($"[礼物] [c/98db34:{msg.uname}] 赠送 [c/db7734:{msg.gift_name}×{msg.num}]  {(msg.total_coin / 100.0):F2}元",
                                  cfg.GiftColor, cfg.MaxMsgLen);
            }

            // 上舰
            protected override void _on_buy_guard(DanmuWsClient client, GuardBuyMessage msg)
            {
                var cfg = BLiveInsteract.Config;
                if (!cfg.GuardToGame) return;
                _parent.Broadcast($"[上舰] {msg.username} 购买 {msg.gift_name}  guard_level={msg.guard_level}",
                                  cfg.GiftColor, cfg.MaxMsgLen);
            }

            // 醒目留言
            protected override void _on_super_chat(DanmuWsClient client, SuperChatMessage msg)
            {
                var cfg = BLiveInsteract.Config;
                if (!cfg.SCToGame) return;
                _parent.Broadcast($"[SC] [c/db3455:¥{msg.price}]  [c/98db34:{msg.uname}]: {msg.message}",
                                  cfg.SCColor, cfg.MaxMsgLen);
            }

            // 进场/关注/点赞等互动
            // 隐藏基类方法，而不是 override
            public new void Handle(DanmuWsClient client, Dictionary<string, object?> command)
            {
                const string INTERACT_WORD = "INTERACT_WORD";
                var cmd = command.GetValueOrDefault("cmd", "")!.ToString()!;
                var pos = cmd.IndexOf(':');
                if (pos != -1) cmd = cmd[..pos];

                if (cmd == INTERACT_WORD)
                {
                    var cfg = BLiveInsteract.Config;
                    if (!cfg.EntryToGame) return;

                    var data = command["data"] as System.Collections.Generic.Dictionary<string, object>;
                    var uname = data?["uname"]?.ToString() ?? "";
                    var msgType = Convert.ToInt32(data?["msg_type"] ?? 0);
                    var typeStr = msgType switch
                    {
                        1 => "进入",
                        2 => "关注",
                        3 => "分享",
                        4 => "特别关注",
                        5 => "互关",
                        6 => "点赞",
                        _ => "互动"
                    };
                    // 静态方法直接用类名
                    _parent.Broadcast($"[进场] {typeStr}: {uname}", cfg.DanmuColor, cfg.MaxMsgLen);
                    return;
                }

                // 其余走官方分发
                base.Handle(client, command);
            }
        }

        /* ------------- 工具 ------------- */
        public void Broadcast(string text, string hexColor, int maxLen, MsgPriority prio = MsgPriority.Normal)
        {
            if (text.Length > maxLen) text = text[..maxLen] + "...";
            var color = HexToColor(hexColor);
            BroadcastRaw(text, color, prio);
        }

        public void BroadcastRaw(string text, Color color, MsgPriority prio = MsgPriority.Normal)
        {
            if (_broadcast != null) _broadcast.Enqueue(text, color, prio);
            else TSPlayer.All.SendMessage(text, color);
        }

        private static Color HexToColor(string hex)
        {
            hex = hex.Replace("#", "");
            if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var u))
                return new Color((int)((u >> 16) & 0xFF), (int)((u >> 8) & 0xFF), (int)(u & 0xFF));
            return Color.Yellow;
        }
    }
}