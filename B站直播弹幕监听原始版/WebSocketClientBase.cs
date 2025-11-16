using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BliveDM;

namespace ws_base
{
    public enum ProtoVer
    {
        NORMAL = 0,
        HEARTBEAT = 1,
        DEFLATE = 2,
        BROTLI = 3
    }

    public enum Operation
    {
        HANDSHAKE = 0,
        HANDSHAKE_REPLY = 1,
        HEARTBEAT = 2,
        HEARTBEAT_REPLY = 3,
        SEND_MSG = 4,
        SEND_MSG_REPLY = 5,
        DISCONNECT_REPLY = 6,
        AUTH = 7,
        AUTH_REPLY = 8,
        RAW = 9,
        PROTO_READY = 10,
        PROTO_FINISH = 11,
        CHANGE_ROOM = 12,
        CHANGE_ROOM_REPLY = 13,
        REGISTER = 14,
        REGISTER_REPLY = 15,
        UNREGISTER = 16,
        UNREGISTER_REPLY = 17
    }

    public enum AuthReplyCode
    {
        OK = 0,
        TOKEN_ERROR = -101
    }

    public class InitError : Exception
    {
        public InitError(string msg) : base(msg) { }
    }

    public class AuthError : Exception
    {
        public AuthError(string msg) : base(msg) { }
    }

    public interface HandlerInterface
    {
        void Handle(WebSocketClientBase client, Dictionary<string, object> command);
        void OnClientStopped(WebSocketClientBase client, Exception exc);
    }

    public static class Utils
    {
        public static Func<int, int, double> MakeConstantRetryPolicy(double seconds)
            => (_, _) => seconds;

        public const string USER_AGENT =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    }

    public readonly struct HeaderTuple
    {
        public readonly uint pack_len;
        public readonly ushort raw_header_size;
        public readonly ushort ver;
        public readonly uint operation;
        public readonly uint seq_id;

        public HeaderTuple(uint packLen, ushort rawHeaderSize, ushort ver,
                           uint operation, uint seqId)
        {
            this.pack_len = packLen;
            this.raw_header_size = rawHeaderSize;
            this.ver = ver;
            this.operation = operation;
            this.seq_id = seqId;
        }
    }

    public abstract class WebSocketClientBase : IDisposable
    {
        private static readonly System.Buffers.ArrayPool<byte> Pool =
            System.Buffers.ArrayPool<byte>.Shared;

        protected ClientWebSocket _ws;
        private Func<int, int, double> _getReconnectInterval;
        private readonly double _heartbeatInterval;
        private readonly CancellationTokenSource _cts;
        private Task _networkTask;
        private PeriodicTimer _heartbeatTimer;
        protected bool _ownSession;

        protected bool _needInitRoom = true;
        protected int? _room_id;

        public HandlerInterface _handler { get; set; }

        public bool is_running => _networkTask != null &&
                                  !_networkTask.IsCompleted;

        public int? room_id => _room_id;

        private void _recreate_ws()
        {
            if (_ownSession)
                _ws?.Dispose();
            _ws = new ClientWebSocket();
            _ownSession = true;
        }

        protected WebSocketClientBase(ClientWebSocket session = null,
                                      double heartbeatInterval = 30)
        {
            _heartbeatInterval = heartbeatInterval;
            _getReconnectInterval = Utils.MakeConstantRetryPolicy(1);
            _cts = new CancellationTokenSource();

            if (session == null)
            {
                _ws = new ClientWebSocket();
                _ownSession = true;
            }
            else
            {
                _ws = session;
                _ownSession = false;
            }
        }

        public void set_handler(HandlerInterface handler) => _handler = handler;

        public void set_reconnect_policy(Func<int, int, double> policy) =>
            _getReconnectInterval = policy ?? throw new ArgumentNullException(nameof(policy));

        public void start()
        {
            if (is_running)
            {
                Console.WriteLine($"room={room_id} client is running, cannot start() again");
                return;
            }
            _networkTask = Task.Run(() => _network_coroutine_wrapper());
        }

        public void stop()
        {
            if (!is_running) return;
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public async Task stop_and_close()
        {
            stop();
            try { await join(); }
            catch { /* ignored */ }
            await close();
        }

        public Task join() => is_running ? _networkTask : Task.CompletedTask;

        public async Task close()
        {
            if (is_running)
                Console.WriteLine($"room={room_id} is calling close(), but client is running");

            if (_ownSession)
            {
                if (_ws.State == WebSocketState.Open ||
                    _ws.State == WebSocketState.CloseReceived)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure,
                                         "shutdown", CancellationToken.None);
                _ws.Dispose();
            }
            _cts.Dispose();
        }

        public void Dispose() => close().GetAwaiter().GetResult();

        public abstract Task<bool> init_room();

        protected abstract string _get_ws_url(int retryCount);

        protected abstract Task _send_auth();

        protected static byte[] _make_packet(object data, Operation op)
        {
            byte[] body;

            if (data is Dictionary<string, object> d)
            {
                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = false,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var json = JsonSerializer.Serialize(d, jsonOptions)
                           .Replace("{\"", "{ \"")
                           .Replace("\",", "\", ")
                           .Replace(":", ": ");

                Console.WriteLine("[AUTH JSON FINAL] len=" + Encoding.UTF8.GetBytes(json).Length);
                body = Encoding.UTF8.GetBytes(json);
            }
            else if (data is string s)
            {
                body = Encoding.UTF8.GetBytes(s);
            }
            else if (data is byte[] b)
            {
                body = b;
            }
            else
            {
                throw new ArgumentException(nameof(data));
            }

            var header = new HeaderTuple(
                packLen: (uint)(16 + body.Length),
                rawHeaderSize: 16,
                ver: 1,
                operation: (uint)op,
                seqId: 1);

            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.Default, true))
            {
                w.Write(BinaryPrimitives.ReverseEndianness(header.pack_len));      // 4
                w.Write(BinaryPrimitives.ReverseEndianness(header.raw_header_size)); // 2
                w.Write(BinaryPrimitives.ReverseEndianness(header.ver));            // 2
                                                                                     //w.Write((ushort)0);                                              // ← 删除这行
                w.Write(BinaryPrimitives.ReverseEndianness(header.operation));      // 4
                w.Write(BinaryPrimitives.ReverseEndianness(header.seq_id));         // 4
                w.Write(body);
            }

            return ms.ToArray();
        }

        private async Task _network_coroutine_wrapper()
        {
            Exception exc = null;
            try { await _network_coroutine(_cts.Token); }
            catch (OperationCanceledException) { /* 正常停止 */ }
            catch (Exception e)
            {
                Console.WriteLine($"room={room_id} _network_coroutine() finished with exception: {e}");
                exc = e;
            }
            if (_handler != null)
                _handler.OnClientStopped(this, exc);
        }

        private async Task _network_coroutine(CancellationToken token)
        {
            int retryCount = 0, totalRetryCount = 0;
            while (!token.IsCancellationRequested)
            {
                _recreate_ws();
                try
                {
                    Console.WriteLine($"【网络协程】重连开始 retryCount={retryCount}");
                    await _on_before_ws_connect(retryCount);
                    Console.WriteLine($"【网络协程】_on_before_ws_connect 完成，准备 Connect");

                    var url = _get_ws_url(retryCount);
                    Console.WriteLine($"【网络协程】准备连接 WebSocket：{url}");
                    await _ws.ConnectAsync(new Uri(url), token);
                    Console.WriteLine($"【网络协程】WebSocket 连接成功");

                    await _on_ws_connect(token);

                    retryCount = 0;
                    await _receive_loop(token);
                }
                catch (WebSocketException wsEx)
                {
                    Console.WriteLine($"【网络协程】WebSocketException：{wsEx.Message}");
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("[网络协程] 正常取消，外部调用 stop()/Dispose()");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"【网络协程】其他异常：{ex}");
                }
                finally
                {
                    await _on_ws_close();
                }
                retryCount++;
                totalRetryCount++;
                Console.WriteLine($"【网络协程】即将等待重连 interval={_getReconnectInterval(retryCount, totalRetryCount)}s");
                await Task.Delay(TimeSpan.FromSeconds(_getReconnectInterval(retryCount, totalRetryCount)), token);
            }
        }

        private async Task _receive_loop(CancellationToken token)
        {
            Console.WriteLine("【接收循环】已启动，等待消息...");
            const int chunk = 4096;
            var buffer = new ArraySegment<byte>(new byte[chunk]);
            using var ms = new MemoryStream();
            while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, token);
                    //Console.WriteLine($"【收到消息】类型={result.MessageType}，长度={result.Count}");
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    ms.Write(buffer.Array, buffer.Offset, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Binary)
                    await _parse_ws_message(ms.ToArray());
                else
                    Console.WriteLine($"room={room_id} unknown message type={result.MessageType}");

                ms.SetLength(0);
            }
        }

        protected virtual async Task _on_before_ws_connect(int retryCount)
        {
            if (!_needInitRoom) return;
            if (!await init_room())
                throw new InitError("init_room() failed");
            _needInitRoom = false;
        }

        private async Task _on_ws_connect(CancellationToken token)
        {
            Console.WriteLine("【_on_ws_connect】开始，准备发送 AUTH 包");
            await _send_auth();

        }


        private async Task _on_ws_close()
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            if (_ws.State == WebSocketState.Open ||
                _ws.State == WebSocketState.CloseReceived)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure,
                                     "shutdown", CancellationToken.None);
        }


        private async Task _send_heartbeat()
        {
            if (_ws.State != WebSocketState.Open) return;
            try
            {
                var pkt = _make_packet(
                    new Dictionary<string, object> { ["object"] = "object" },
                    Operation.HEARTBEAT);
                Console.WriteLine("[RAW HEARBEAT]" + Convert.ToHexString(pkt));
                await _ws.SendAsync(new ArraySegment<byte>(pkt),
                                    WebSocketMessageType.Binary,
                                    true, CancellationToken.None);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[HEARTBEAT EXCEPTION] {e}");
                // 不要 throw，否则 PeriodicTimer 循环会停
            }
        }

        private async Task _parse_ws_message(byte[] data)
        {
            //Console.WriteLine($"【收到原始消息】长度={data.Length} 字节");
            int offset = 0;
            HeaderTuple header;

            try
            {
                header = _unpack_header(data, offset);
            }
            catch
            {
                Console.WriteLine($"room={room_id} parsing header failed, offset={offset}");
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
                    await _parse_business_message(header, body);

                    //重置偏移量
                    offset += (int)header.pack_len;
                    if (offset >= data.Length) break;

                    //走到这一步说明原始数据还有>=1个数据包没读完, 继续重读头部然后进循环
                    try 
                    { 
                        header = _unpack_header(data, offset); 
                    } catch {
                        Console.WriteLine($"room={room_id} parsing header failed, offset={offset}");
                        break;
                    }
                }
            }
            else if (header.operation == (uint)Operation.HEARTBEAT_REPLY)
            {
                Console.WriteLine($"[RAW HEARTBEAT_REPLY] {Convert.ToHexString(data)}");
                var popBytes = new byte[4];
                Buffer.BlockCopy(data, offset + (int)header.raw_header_size,
                                 popBytes, 0, 4);
                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(popBytes);
                var popularity = BitConverter.ToUInt32(popBytes, 0);

                var cmd = new Dictionary<string, object>
                {
                    ["cmd"] = "_HEARTBEAT",
                    ["data"] = new Dictionary<string, object>
                    {
                        ["popularity"] = popularity
                    }
                };
                _handle_command(cmd);
            }
            else
            {
                Console.WriteLine($"room={room_id} unknown message operation={header.operation}");
            }
        }

        private async Task _parse_business_message(HeaderTuple header, byte[] body)
        {
            if (header.operation == (uint)Operation.SEND_MSG_REPLY)
            {
                if (header.ver == (ushort)ProtoVer.BROTLI)
                {
                    body = await Task.Run(() => DecompressBrotli(body));
                    await _parse_ws_message(body);
                }
                else if (header.ver == (ushort)ProtoVer.DEFLATE)
                {
                    body = await Task.Run(() => DecompressZlib(body));
                    await _parse_ws_message(body);
                }
                else if (header.ver == (ushort)ProtoVer.NORMAL)
                {
                    if (body.Length != 0)
                    {
                        try
                        {
                            var json = JsonSerializer.Deserialize<Dictionary<string, object>>(
                                Encoding.UTF8.GetString(body));
                            _handle_command(json);
                        }
                        catch
                        {
                            Console.WriteLine($"room={room_id} body parse error");
                            throw;
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"room={room_id} unknown protocol version={header.ver}");
                }
            }
            else if (header.operation == (uint)Operation.AUTH_REPLY)
            {
                var json = JsonSerializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
                Console.WriteLine($"【收到】AUTH_REPLY：{JsonSerializer.Serialize(json)}");

                if (json.ContainsKey("code") && ((JsonElement)json["code"]).GetInt32() != (int)AuthReplyCode.OK)
                {
                    throw new AuthError($"认证失败: code={json["code"]}");
                }

                // 1. 立刻发第一条
                await _send_heartbeat();

                // 2. 再启动 30 s 周期
                _heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
                _ = Task.Run(async () =>
                {
                    while (await _heartbeatTimer.WaitForNextTickAsync(_cts.Token))
                        await _send_heartbeat();
                }, _cts.Token);

            }

            else
            {
                Console.WriteLine($"room={room_id} unknown message operation={header.operation}");
            }
        }

        private void _handle_command(Dictionary<string, object> command)
        {
            if (_handler == null) return;
            try { _handler.Handle(this, command); }
            catch (Exception e)
            {
                Console.WriteLine($"room={room_id} _handle_command() failed: {e}");
            }
        }

        private static HeaderTuple _unpack_header(byte[] data, int offset)
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

        private static byte[] DecompressZlib(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            deflate.CopyTo(outMs);
            return outMs.ToArray();
        }

        private static byte[] DecompressBrotli(byte[] data)
        {
            // 需要引入 Brotli.NET 或 System.IO.Compression.Brotli（.NET 6+）
            using var ms = new MemoryStream(data);
            using var brotli = new System.IO.Compression.BrotliStream(
                ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            brotli.CopyTo(outMs);
            return outMs.ToArray();
        }
    }
}