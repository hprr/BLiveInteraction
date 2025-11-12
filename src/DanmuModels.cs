using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EasyDANMU.src
{   
    //鉴权
    public class AuthPacket
    {
        public long uid { get; set; }
        public int roomid { get; set; }
        public int protover { get; set; } = 3;
        public string platform { get; set; } = "web";
        public int type { get; set; } = 2;
        public string key { get; set; } = "";
        public string buvid { get; set; } = "";
    }
    //心跳
    public class HeartbeatReply
    {
        public int popularity { get; set; }
    }
    //弹幕消息
    public class DanmakuMsg
    {
        public string uname { get; set; } = "";
        public string msg { get; set; } = "";
    }
    //消息ID
    public enum Operation : uint
    {
        HEARTBEAT = 2,
        HEARTBEAT_REPLY = 3,
        SEND_MSG_REPLY = 5,
        AUTH_REPLY = 8
    }
    //头部协议
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
}
