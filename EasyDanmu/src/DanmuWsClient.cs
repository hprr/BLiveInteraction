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
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Runtime.InteropServices;
using System.Reflection.Metadata;

namespace EasyDANMU.src
{
    public class DanmuWsClient : IDisposable
    {
        #region --- 原有字段 ---
        private readonly ClientWebSocket _ws = new();
        private readonly string _url;
        public readonly AuthPacket _auth;
        private readonly byte[] _buffer = new byte[4096];
        //取消令牌
        private readonly CancellationTokenSource _cts = new();
        //回调处理接口
        public HandlerInterface _handler { get; set; }
        public event Action<DanmuWsClient, Dictionary<string, object>> MessageReceived;
        public event Action<DanmuWsClient, Exception> Disconnected;

        // 如果后台还想等收包任务，就留一个字段
        private Task _receiveTask;
        #endregion

        public void set_handler(HandlerInterface handler) => _handler = handler;

        public interface HandlerInterface
        {
            void Handle(DanmuWsClient client, Dictionary<string, object> command);
            void OnClientStopped(DanmuWsClient client, Exception exc);
        }


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

            // 这里把 Task 记下来，后面 Dispose 可以 Join
            _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token), _cts.Token);

            _ = Task.Run(HeartbeatLoop, _cts.Token);
            // 若你想等收包循环完成，也可以 await _receiveTask;
            await _receiveTask;
        }

        //循环接收
        private async Task ReceiveLoop(CancellationToken token)
        {
            Console.WriteLine("【接收循环】已启动，等待消息...");

            using var ms = new MemoryStream();          // 拼包缓存
            var segment = new ArraySegment<byte>(_buffer); // 每次都复用同一块内存

            while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                do
                {
                    // 用类字段 _buffer 接收
                    result = await _ws.ReceiveAsync(segment, token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;

                    ms.Write(_buffer, 0, result.Count);   // 拼包
                }
                while (!result.EndOfMessage);


                if (result.MessageType == WebSocketMessageType.Binary)
                    await ParseWsMessage(ms.ToArray());
                else
                    Console.WriteLine($"room={_auth.roomid} unknown message type={result.MessageType}");

                ms.SetLength(0); // 重置流，准备下一条消息
                // ReceiveLoop 末尾（while 结束后）加一句
                //Disconnected?.Invoke(this, null);   // 正常关闭
            }
        }
        //解析ws原始数据(可能包含多个包)
        private async Task ParseWsMessage(byte[] data)
        {
            //Console.WriteLine($"【收到原始消息】长度={data.Length} 字节");
            int offset = 0;
            HeaderTuple header;

            try
            {
                header = UnpackHeader(data, offset);
            }
            catch
            {
                Console.WriteLine($"[ParseWsMessage] room={_auth.roomid} parsing header failed, offset={offset}");
                return;
            }

            //Console.WriteLine($"[ParseWsMessage] pack_len={header.pack_len}, op={header.operation}, ver={header.ver}, data.length={data.Length}");
            if (header.operation == (uint)Operation.SEND_MSG_REPLY ||
                header.operation == (uint)Operation.AUTH_REPLY)
            {
                while (true)
                {
                    //读取出当前原始字节中第一个完整包并解析
                    var body = new byte[header.pack_len - header.raw_header_size];
                    Buffer.BlockCopy(data, offset + (int)header.raw_header_size,
                                     body, 0, body.Length);
                    await ParseBusinessMessage(header, body);

                    //重置偏移量
                    offset += (int)header.pack_len;
                    if (offset >= data.Length) break;

                    //走到这一步说明原始数据还有>=1个数据包没读完, 继续重读头部然后进循环
                    try
                    {
                        header = UnpackHeader(data, offset);
                    }
                    catch
                    {
                        Console.WriteLine($"[ParseWsMessage] room={_auth.roomid} parsing header failed, offset={offset}");
                        break;
                    }
                }
            }
            else if (header.operation == (uint)Operation.HEARTBEAT_REPLY)
            {
                //Console.WriteLine($"[RAW HEARTBEAT_REPLY] {Convert.ToHexString(data)}");
                var popBytes = new byte[4];
                Buffer.BlockCopy(data, offset + (int)header.raw_header_size,
                                 popBytes, 0, 4);
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(popBytes);
                var popularity = BitConverter.ToUInt32(popBytes, 0);
                //Console.WriteLine($"人气值:{popularity}");
                var cmd = new Dictionary<string, object>
                {
                    ["cmd"] = "_HEARTBEAT",
                    ["data"] = new Dictionary<string, object>
                    {
                        ["popularity"] = popularity
                    }
                };
                HandleCommand(cmd);
            }
            else
            {
                Console.WriteLine($"[ParseWsMessage] room={_auth.roomid} unknown message operation={header.operation}");
            }
        }

        private async Task ParseBusinessMessage(HeaderTuple header, byte[] body)
        {
            if (header.operation == (uint)Operation.SEND_MSG_REPLY)
            {
                if (header.ver == (ushort)ProtoVer.BROTLI)
                {
                    body = await Task.Run(() => DecompressBrotli(body));
                    await ParseWsMessage(body);
                }
                else if (header.ver == (ushort)ProtoVer.DEFLATE)
                {
                    body = await Task.Run(() => DecompressZlib(body));
                    await ParseWsMessage(body);
                }
                else if (header.ver == (ushort)ProtoVer.NORMAL)
                {
                    if (body.Length != 0)
                    {
                        try
                        {
                            var json = JsonSerializer.Deserialize<Dictionary<string, object>>(
                                Encoding.UTF8.GetString(body));
                            HandleCommand(json);
                        }
                        catch
                        {
                            Console.WriteLine($"[ParseBusinessMessage] room={_auth.roomid} body parse error");
                            throw;
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"room={_auth.roomid} unknown protocol version={header.ver}");
                }
            }
            else if (header.operation == (uint)Operation.AUTH_REPLY)
            {
                var json = JsonSerializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
                Console.WriteLine($"[ParseBusinessMessage] AUTH_REPLY：{JsonSerializer.Serialize(json)}");

                if (json.ContainsKey("code") && ((JsonElement)json["code"]).GetInt32() != (int)AuthReplyCode.OK)
                {
                    throw new AuthError($"认证失败: code={json["code"]}");
                }

            }

            else
            {
                Console.WriteLine($"[ParseBusinessMessage] room={_auth.roomid} unknown message operation={header.operation}");
            }

        }

        private void HandleCommand(Dictionary<string, object> command)
        {
            // 1. 先给用户 lambda 事件（保持兼容）
            MessageReceived?.Invoke(this, command);

            // 2. 再走 BaseHandler 风格
            if (_handler != null)
                try { _handler.Handle(this, command); }
                catch (Exception e)
                {
                    Console.WriteLine($"[HandleCommand] handler 异常：{e}");
                }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                Task.WaitAll(new[] { _receiveTask }, 5_000); // 若有后台任务
            }
            catch { /* 忽略超时 */ }

            // 通知外部“我已彻底停止”
            Disconnected?.Invoke(this, null);
            _ws?.Dispose();
            _cts?.Dispose();
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
                //Console.WriteLine("[SENT] HEARTBEAT");
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

        private static HeaderTuple UnpackHeader(byte[] data, int offset)
        {
            var span = new ReadOnlySpan<byte>(data, offset, 16);
            if (BitConverter.IsLittleEndian)
            {
                var tmp = new byte[16];
                span.CopyTo(tmp);
                Array.Reverse(tmp, 0, 4);
                Array.Reverse(tmp, 4, 2);
                Array.Reverse(tmp, 6, 2);
                Array.Reverse(tmp, 8, 4);
                Array.Reverse(tmp, 12, 4);
                span = tmp;
            }

            return new HeaderTuple(
                packLen: MemoryMarshal.Read<uint>(span.Slice(0, 4)),
                rawHeaderSize: MemoryMarshal.Read<ushort>(span.Slice(4, 2)),
                ver: MemoryMarshal.Read<ushort>(span.Slice(6, 2)),
                operation: MemoryMarshal.Read<uint>(span.Slice(8, 4)),
                seqId: MemoryMarshal.Read<uint>(span.Slice(12, 4)));
        }

        public readonly struct HeaderTuple
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