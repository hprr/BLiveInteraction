using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.WebSockets;
using System.Text.Json;
using System.Buffers.Binary;

namespace EasyDANMU.src
{
    public class DanmuWsClient : IDisposable
    {
        #region ---原有字段---
        //创建websocket客户端
        private readonly ClientWebSocket _ws = new();
        //ws服务器URL
        private readonly string _url;
        //鉴权包
        private readonly AuthPacket _auth;
        //缓冲区
        private readonly byte[] _buffer = new byte[4096];
        private readonly CancellationTokenSource _cts = new();
        #endregion
        //构造
        public DanmuWsClient(string host, int wssPort, int roomId, string token, string buvid, long uid = 0)
        {
            _url = $"wss://{host}:{wssPort}/sub";
            _auth = new AuthPacket
            {
                roomid = roomId,
                key = token,
                buvid = buvid,
                uid = uid
            };
        }
        #region ---生命周期---
        //监听所有异步事件
        public async Task StartAsync()
        {
            //连接到ws服务器
            await _ws.ConnectAsync(new Uri(_url), _cts.Token);

            // ====== 新增：连接成功提示 ======
            Console.WriteLine($"[WS] 连接成功 -> {_url}");
            // ===============================

            //发送鉴权包
            await SendAuthAsync();

            //启动心跳循环
            _ = Task.Run(HeartbeatLoop, _cts.Token);

            //启动等待接收事件
            await ReceiveLoop();
        }
        //发送鉴权包
        private async Task SendAuthAsync()
        {
            var authParams = new Dictionary<string, object>
            {
                ["uid"] = _auth.uid,
                ["roomid"] = _auth.roomid,
                ["protover"] = 3,
                ["buvid"] = _auth.buvid,
                ["platform"] = "web",
                ["key"] = _auth.key,
                ["type"] = 2

            };

            // 1. 完全复刻老代码的“带空格”JSON
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            var json = JsonSerializer.Serialize(authParams, jsonOptions)
                            .Replace("{\"", "{ \"")
                            .Replace("\",", "\", ")
                            .Replace(":", ": ");



            var body = Encoding.UTF8.GetBytes(json);
            Console.WriteLine("[AUTH JSON] " + json);
            Console.WriteLine("[AUTH BODY] len=" + body.Length); // 必须 = 399

            // 2. 完全复刻 _make_packet //这很蠢, 但很有效
            var pkt = MakePacketRaw(body, 7);
            Console.WriteLine("[AUTH RAW] " + Convert.ToHexString(pkt));
            // ====== 新增：打印头部 16 字节 ======
            Console.WriteLine($"[AUTH-HEADER] {Convert.ToHexString(pkt.AsSpan(0, 16))}");
            await _ws.SendAsync(new ArraySegment<byte>(pkt), WebSocketMessageType.Binary, true, _cts.Token);
            Console.WriteLine("[SENT] AUTH");
        }

        private static byte[] MakePacketRaw(byte[] body, int op)
        {
            const ushort HeaderSize = 16;
            uint packLen = (uint)(HeaderSize + body.Length);

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.Default, true))
            {
                w.Write(BinaryPrimitives.ReverseEndianness(packLen));      // 0-3
                w.Write(BinaryPrimitives.ReverseEndianness(HeaderSize));   // 4-5
                w.Write(BinaryPrimitives.ReverseEndianness((ushort)1));    // 6-7
                w.Write(BinaryPrimitives.ReverseEndianness((uint)op));     // 8-11
                w.Write(BinaryPrimitives.ReverseEndianness(1u));           // 12-15
                //Console.WriteLine(sizeof(uint).ToString() + " " + sizeof(ushort) + " " + sizeof(UInt32)+ " " + sizeof(UInt16));
                w.Write(body);                                             // 16+
            }
            return ms.ToArray(); // 老代码返回 byte[]
        }

        //心跳包循环
        private async Task HeartbeatLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(30_000, _cts.Token);
                var pkt = MakePacket(Encoding.UTF8.GetBytes("{}"), 2); // HEARTBEAT = 2
                await _ws.SendAsync(pkt, WebSocketMessageType.Binary, true, _cts.Token);
                Console.WriteLine("[SENT] HEARTBEAT");
            }
        }
        //异步接收
        private async Task ReceiveLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(_buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                // 打印 RAW
                //var rawSlice = _buffer.AsSpan(0, result.Count);
                //Console.WriteLine($"[RAW-RECV] {Convert.ToHexString(rawSlice)}");
                
                ParseMessage(_buffer.AsSpan(0, result.Count));
            }
        }
        #endregion

        //解析收到的数据
        private void ParseMessage(ReadOnlySpan<byte> raw)
        {
            if (raw.Length < 16) return;

            // 统一用大端
            var packLen = BinaryPrimitives.ReadUInt32BigEndian(raw[0..4]);
            var headerLen = BinaryPrimitives.ReadUInt16BigEndian(raw[4..6]);
            var ver = BinaryPrimitives.ReadUInt16BigEndian(raw[6..8]);
            var op = BinaryPrimitives.ReadUInt32BigEndian(raw[8..12]);

            var body = raw[headerLen..(int)packLen];
            var json = Encoding.UTF8.GetString(body);

            switch (op)
            {
                case 3: // HEARTBEAT_REPLY
                    var pop = BinaryPrimitives.ReadUInt32BigEndian(body);   // ← 改这里
                    Console.WriteLine($"❤ 人气值：{pop}");
                    break;

                case 5: // SEND_MSG_REPLY
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        // 先取 cmd
                        if (!root.TryGetProperty("cmd", out var cmd)) break;
                        var cmdStr = cmd.GetString();

                        if (cmdStr == "DANMU_MSG")
                        {
                            var info = root.GetProperty("info");
                            var msg = info[1].GetString();
                            var uname = info[2][1].GetString();
                            Console.WriteLine($"💬 {uname}：{msg}");
                        }
                        // 以后想加 SUPER_CHAT_MESSAGE、GUARD_BUY 等继续 else if 即可
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Parse-ERR] {ex.Message} in {json}");
                    }
                    break;

                case 8: // AUTH_REPLY
                    Console.WriteLine($"✅ AUTH_REPLY：{json}");
                    break;
            }
        }
        //数据包打包方法
        private static ArraySegment<byte> MakePacket(byte[] body, int op)
        {
            const ushort HeaderSize = 16;
            uint packLen = (uint)(HeaderSize + body.Length);

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.Default, true))
            {
                w.Write(BinaryPrimitives.ReverseEndianness(packLen));      // 4
                w.Write(BinaryPrimitives.ReverseEndianness(HeaderSize));   // 2
                w.Write(BinaryPrimitives.ReverseEndianness((ushort)1));    // 2
                w.Write(BinaryPrimitives.ReverseEndianness((uint)op));     // 4
                w.Write(BinaryPrimitives.ReverseEndianness(1u));           // 4
                w.Write(body);                                             // N
            }

            return new ArraySegment<byte>(ms.ToArray());
        }

        public void Dispose()
        {
            _cts.Cancel();
            _ws.Dispose();
        }

    }
}
