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

namespace EasyDANMU.src
{
    public class DanmuWsClient : IDisposable
    {
        #region --- 原有字段 ---
        private readonly ClientWebSocket _ws = new();
        private readonly string _url;
        private readonly AuthPacket _auth;
        private readonly byte[] _buffer = new byte[4096];
        //取消令牌
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
            await ReceiveLoop(_cts.Token);
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
            }
        }
        //解析ws原始数据(可能包含多个包)
        private async Task ParseWsMessage(byte[] data)
        {
            //Console.WriteLine($"[ParseMessage] 原始数据长度={data.Length} 字节");
            int offset = 0;
            while(offset < data.Length)
            {
                try
                {
                    //1.读取头部
                    HeaderTuple header = UnpackHeader(data, offset);
                    Console.WriteLine($"[ParseWsMessage] pack_len={header.pack_len}, op={header.operation}, ver={header.ver}");
                    //2.裁剪包体&获取偏移
                    int bodyLen = (int)(header.pack_len - header.raw_header_size);
                    int packEnd = offset + (int)header.pack_len;

                    //3.边界保护
                    if(packEnd > data.Length)
                    {
                        Console.WriteLine($"[ParseWsMessage] room={_auth.roomid} 包长度越界");
                        break;
                    }
                    //走到这里说明所有参数已经初始化结束了
                    //4.按照operation/ver 分发解析任务
                    switch (header.operation, header.ver) 
                    {
                        //心跳回复处理方法, 传入原始data和对应的偏移量
                        case ((uint)Operation.HEARTBEAT_REPLY, (ushort)1):
                            //HandleHeartbeat(data, offset + (int)header.raw_header_size);
                            break;
                        
                        case ((uint)Operation.SEND_MSG_REPLY, (ushort)0):
                        case ((uint)Operation.AUTH_REPLY, (ushort)1):
                            //await HandleJsonBody(header, data, offset + (int)header.raw_header_size, bodyLen);
                            break;

                        case ((uint)Operation.SEND_MSG_REPLY, 3):
                        case ((uint)Operation.AUTH_REPLY, 3):
                            //await HandleZlibBody(header, data, offset + (int)header.raw_header_size, bodyLen);
                            break;

                        default:
                            Console.WriteLine($"[ParseWsMessage] room={_auth.roomid} 未处理 op={header.operation}, ver={header.ver}");
                            break;
                    }
                    //重置偏移量
                    offset = packEnd;
                }
                catch (Exception)
                {
                    Console.WriteLine($"[ParseWsMessage] room={_auth.roomid} 头部解析失败, offset={offset}");
                    break;
                }

            }

        }

        private async Task ParseBusinessMessage(HeaderTuple header, byte[] body)
        {
            throw new NotImplementedException();
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