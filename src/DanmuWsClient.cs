using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers.Binary;

namespace EasyDANMU.src
{
    public class DanmuWsClient : IDisposable
    {
        #region --- 原有字段 ---
        private readonly ClientWebSocket _ws = new();
        private readonly string _url;
        private readonly AuthPacket _auth;
        private readonly byte[] _buffer = new byte[4096];
        private readonly CancellationTokenSource _cts = new();
        #endregion

        public DanmuWsClient(string host, int wssPort, int roomId, string token, string buvid, long uid = 0)
        {
            _url = $"wss://{host}:{wssPort}/sub";
            _auth = new AuthPacket { roomid = roomId, key = token, buvid = buvid, uid = uid };
        }

        #region --- 生命周期 ---
        public async Task StartAsync()
        {
            await _ws.ConnectAsync(new Uri(_url), _cts.Token);
            Console.WriteLine($"[WS] 连接成功 -> {_url}");
            await SendAuthAsync();
            _ = Task.Run(HeartbeatLoop, _cts.Token);
            await ReceiveLoop();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _ws.Dispose();
        }
        #endregion

        #region --- 发包/心跳 ---
        private async Task SendAuthAsync()
        {
            var p = new Dictionary<string, object>
            {
                ["uid"] = _auth.uid, ["roomid"] = _auth.roomid, ["protover"] = 3,
                ["buvid"] = _auth.buvid, ["platform"] = "web", ["key"] = _auth.key, ["type"] = 2
            };
            var json = JsonSerializer.Serialize(p)
                        .Replace("{\"", "{ \"").Replace("\",", "\", ").Replace(":", ": ");
            var body = Encoding.UTF8.GetBytes(json);
            var pkt  = MakePacketRaw(body, 7);
            await _ws.SendAsync(pkt, WebSocketMessageType.Binary, true, _cts.Token);
            Console.WriteLine("[SENT] AUTH");
        }

        private async Task HeartbeatLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(30_000, _cts.Token);
                var pkt = MakePacket(Encoding.UTF8.GetBytes("{}"), 2);
                await _ws.SendAsync(pkt, WebSocketMessageType.Binary, true, _cts.Token);
                Console.WriteLine("[SENT] HEARTBEAT");
            }
        }
        #endregion

        #region --- 收包 ---
        private async Task ReceiveLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(_buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                ParseMessage(_buffer.AsSpan(0, result.Count));
            }
        }
        #endregion

        #region --- 解析链路：切包→解压→长度+JSON→打印 ---
        private void ParseMessage(ReadOnlySpan<byte> raw)
        {

            if (raw.Length < 16) return;

            // 1. 切包（可能一帧多包）
            var packets = SplitPackets(raw);
            foreach (var (h, body) in packets)
            {
                // 2. 解压（ver=2 zlib  ver=3 brotli  ver=1 不压）
                var decompressed = h.ver switch
                {
                    2 => DecompressZlib(body),
                    3 => DecompressBrotli(body),
                    _ => body
                };

                // 3. 长度+JSON 协议（单条/多条）
                foreach (var json in ExtractJsons(decompressed))
                    ProcessCommand(json);
                //Console.WriteLine($"[DEBUG] decompressed len={decompressed.Length}, first16={Convert.ToHexString(decompressed.AsSpan(0, Math.Min(16, decompressed.Length)))}");
            }
        }

        /// <summary>
        /// 把 raw 切成 (header,body) 列表，body 已复制成 byte[]
        /// </summary>
        private static List<(HeaderTuple h, byte[] body)> SplitPackets(ReadOnlySpan<byte> raw)
        {
            var list = new List<(HeaderTuple, byte[])>();
            while (raw.Length >= 16)
            {
                var h = new HeaderTuple(
                    ReadU32BE(raw[0..4]), ReadU16BE(raw[4..6]), ReadU16BE(raw[6..8]),
                    ReadU32BE(raw[8..12]), ReadU32BE(raw[12..16]));
                if (raw.Length < h.pack_len) break;
                list.Add((h, raw[16..(int)h.pack_len].ToArray()));
                raw = raw[(int)h.pack_len..];
            }
            return list;
        }

        /// <summary>
        /// 只过滤“明显非 JSON”段，保留所有潜在 JSON
        /// </summary>
        private static IEnumerable<string> ExtractJsons(byte[] data)
        {
            int p = 0;
            while (p < data.Length)
            {
                // 情况 1：长度+JSON（前 3 字节 00 00 00）
                if (p + 4 <= data.Length && data[p] == 0 && data[p + 1] == 0 && data[p + 2] == 0)
                {
                    uint len = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
                    p += 4;
                    if (len == 0 || p + len > data.Length) { p = (int)Math.Min(p + (int)len, data.Length); continue; }
                    var segment = data.AsSpan(p, (int)len);
                    if (segment.Length > 5 && segment[0] == (byte)'{')
                        yield return Encoding.UTF8.GetString(segment);
                    p += (int)len;
                }
                // 情况 2：纯 JSON（首字节 '{'）
                else if (p < data.Length && data[p] == (byte)'{')
                {
                    int nl = Array.IndexOf(data, (byte)'\n', p);
                    if (nl == -1) nl = data.Length;
                    var segment = data.AsSpan(p, nl - p);
                    if (segment.Length > 5 && segment[^1] == (byte)'}')
                        yield return Encoding.UTF8.GetString(segment);
                    p = nl + 1;
                }
                else
                {
                    p++;   // 非法字节，跳过
                }
            }
        }
        /// <summary>
        /// 单行 JSON → 控制台打印
        /// </summary>
        private void ProcessCommand(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("cmd", out var cmd)) return;
                var cmdStr = cmd.GetString();

                switch (cmdStr)
                {
                    case "DANMU_MSG":
                        var info = root.GetProperty("info");
                        var msg = info[1].GetString();
                        var uname = info[2][1].GetString();
                        Console.WriteLine($"💬 {uname}：{msg}");
                        break;
                    case "SUPER_CHAT_MESSAGE":
                        var sc = root.GetProperty("data");
                        Console.WriteLine($"🔔 醒目留言 ¥{sc.GetProperty("price").GetInt32() / 100.0:F2}  {sc.GetProperty("uname").GetString()}：{sc.GetProperty("message").GetString()}");
                        break;
                    case "SEND_GIFT":
                        var g = root.GetProperty("data");
                        Console.WriteLine($"🎁 {g.GetProperty("uname").GetString()} 赠送 {g.GetProperty("giftName").GetString()} ×{g.GetProperty("num").GetInt32()}");
                        break;
                    case "INTERACT_WORD":
                    case "INTERACT_WORD_V2":
                        var iw = root.GetProperty("data");
                        var iwName = iw.GetProperty("uname").GetString();
                        var iwType = iw.GetProperty("msg_type").GetInt32();
                        var action = iwType switch
                        {
                            1 => "进入",
                            2 => "关注",
                            3 => "分享",
                            4 => "特别关注",
                            5 => "互关",
                            6 => "点赞",
                            _ => "互动"
                        };
                        Console.WriteLine($"👏 {action}：{iwName}");
                        break;
                    default:
                        // 心跳等不打印
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProcessCommand-ERR] {ex.Message}  raw={json}");
            }
        }
        #endregion

        #region --- 工具方法 ---
        private static uint ReadU32BE(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt32BigEndian(s);
        private static ushort ReadU16BE(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt16BigEndian(s);

        private static byte[] DecompressZlib(ReadOnlySpan<byte> src)
        {
            using var ms = new MemoryStream(src.ToArray());
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            ds.CopyTo(outMs);
            return outMs.ToArray();
        }

        private static byte[] DecompressBrotli(ReadOnlySpan<byte> src)
        {
            using var ms = new MemoryStream(src.ToArray());
            using var bs = new BrotliStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            bs.CopyTo(outMs);
            return outMs.ToArray();
        }

        private static ArraySegment<byte> MakePacket(byte[] body, int op)
        {
            const ushort HeaderSize = 16;
            uint packLen = (uint)(HeaderSize + body.Length);
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.Default, true))
            {
                w.Write(BinaryPrimitives.ReverseEndianness(packLen));
                w.Write(BinaryPrimitives.ReverseEndianness(HeaderSize));
                w.Write(BinaryPrimitives.ReverseEndianness((ushort)1));
                w.Write(BinaryPrimitives.ReverseEndianness((uint)op));
                w.Write(BinaryPrimitives.ReverseEndianness(1u));
                w.Write(body);
            }
            return new ArraySegment<byte>(ms.ToArray());
        }

        private static byte[] MakePacketRaw(byte[] body, int op)
        {
            const ushort HeaderSize = 16;
            uint packLen = (uint)(HeaderSize + body.Length);
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.Default, true))
            {
                w.Write(BinaryPrimitives.ReverseEndianness(packLen));
                w.Write(BinaryPrimitives.ReverseEndianness(HeaderSize));
                w.Write(BinaryPrimitives.ReverseEndianness((ushort)1));
                w.Write(BinaryPrimitives.ReverseEndianness((uint)op));
                w.Write(BinaryPrimitives.ReverseEndianness(1u));
                w.Write(body);
            }
            return ms.ToArray();
        }

        private readonly struct HeaderTuple
        {
            public readonly uint pack_len;
            public readonly ushort raw_header_size;
            public readonly ushort ver;
            public readonly uint operation;
            public readonly uint seq_id;
            public HeaderTuple(uint packLen, ushort rawHeaderSize, ushort ver, uint operation, uint seqId)
            {
                this.pack_len = packLen;
                this.raw_header_size = rawHeaderSize;
                this.ver = ver;
                this.operation = operation;
                this.seq_id = seqId;
            }
        }
        #endregion
    }
}