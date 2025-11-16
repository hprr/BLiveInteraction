using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Threading.Tasks;

namespace EasyDANMU.src
{

    public class HeartbeatMessage
    {
        public int popularity = 0;

        public static HeartbeatMessage FromCommand(JsonObject data)
        {
            return new HeartbeatMessage
            {
                popularity = data["popularity"]?.GetValue<int>() ?? 0
            };
        }
    }
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
    //operationID
    public enum Operation : uint
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
    //负载格式
    public enum ProtoVer
    {
        NORMAL = 0,
        HEARTBEAT = 1,
        DEFLATE = 2,
        BROTLI = 3
    }
    //AUTH回包错误码
    public enum AuthReplyCode
    {
        OK = 0,
        TOKEN_ERROR = -101
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
    //AUTH失败捕获code
    public class AuthError : Exception
    {
        public AuthError(string msg) : base(msg) { }
    }

    public class DanmakuMessage
    {
        public int mode = 0;
        public int font_size = 0;
        public int color = 0;
        public long timestamp = 0;
        public int rnd = 0;
        public string uid_crc32 = "";
        public int msg_type = 0;
        public int bubble = 0;
        public int dm_type = 0;
        public object emoticon_options = "";
        public object voice_config = "";
        public Dictionary<string, object> mode_info = new();

        public string msg = "";
        public long uid = 0;
        public string uname = "";
        public string face = "";
        public int admin = 0;
        public int vip = 0;
        public int svip = 0;
        public int urank = 0;
        public int mobile_verify = 0;
        public string uname_color = "";

        public int medal_level = 0;
        public string medal_name = "";
        public string runame = "";
        public int medal_room_id = 0;
        public int mcolor = 0;
        public string special_medal = "";

        public int user_level = 0;
        public int ulevel_color = 0;
        public string ulevel_rank = "";

        public string old_title = "";
        public string title = "";

        public int privilege_type = 0;
        public int wealth_level = 0;

        public static DanmakuMessage FromCommand(JsonArray info)
        {
            var info0 = info[0]!.AsArray();
            var modeInfoElement = info0.Count > 15 ? info0[15] : null;
            var mode_info = modeInfoElement is JsonObject mo
                ? JsonSerializer.Deserialize<Dictionary<string, object>>(mo.ToJsonString()) ?? new()
                : new Dictionary<string, object>();

            string face = "";
            try
            {
                face = modeInfoElement?["user"]?["base"]?["face"]?.GetValue<string>() ?? "";
            }
            catch { }

            var medal = info[3]!.AsArray();
            int medal_level = 0, medal_room_id = 0, mcolor = 0;
            string medal_name = "", runame = "", special_medal = "0";
            if (medal.Count > 0)
            {
                medal_level = medal[0]?.GetValue<int>() ?? 0;
                medal_name = medal[1]?.GetValue<string>() ?? "";
                runame = medal[2]?.GetValue<string>() ?? "";
                medal_room_id = medal[3]?.GetValue<int>() ?? 0;
                mcolor = medal[4]?.GetValue<int>() ?? 0;
                special_medal = medal[5]?.GetValue<string>() ?? "0";
            }

            var titleInfo = info[5]!.AsArray();
            string old_title = "", title = "";
            if (titleInfo.Count > 1)
            {
                old_title = titleInfo[0]?.GetValue<string>() ?? "";
                title = titleInfo[1]?.GetValue<string>() ?? "";
            }

            return new DanmakuMessage
            {
                mode = info0[1]?.GetValue<int>() ?? 0,
                font_size = info0[2]?.GetValue<int>() ?? 0,
                color = info0[3]?.GetValue<int>() ?? 0,
                timestamp = info0[4]?.GetValue<long>() ?? 0,
                rnd = info0[5]?.GetValue<int>() ?? 0,
                uid_crc32 = info0[7]?.GetValue<string>() ?? "",
                msg_type = info0[9]?.GetValue<int>() ?? 0,
                bubble = info0[10]?.GetValue<int>() ?? 0,
                dm_type = info0[12]?.GetValue<int>() ?? 0,
                emoticon_options = info0[13] ?? "",
                voice_config = info0[14] ?? "",
                mode_info = mode_info,

                msg = info[1]?.GetValue<string>() ?? "",
                uid = info[2]!.AsArray()[0]?.GetValue<long>() ?? 0,
                uname = info[2]!.AsArray()[1]?.GetValue<string>() ?? "",
                face = face,
                admin = info[2]!.AsArray()[2]?.GetValue<int>() ?? 0,
                vip = info[2]!.AsArray()[3]?.GetValue<int>() ?? 0,
                svip = info[2]!.AsArray()[4]?.GetValue<int>() ?? 0,
                urank = info[2]!.AsArray()[5]?.GetValue<int>() ?? 0,
                mobile_verify = info[2]!.AsArray()[6]?.GetValue<int>() ?? 0,
                uname_color = info[2]!.AsArray()[7]?.GetValue<string>() ?? "",

                medal_level = medal_level,
                medal_name = medal_name,
                runame = runame,
                medal_room_id = medal_room_id,
                mcolor = mcolor,
                special_medal = special_medal,

                user_level = info[4]!.AsArray()[0]?.GetValue<int>() ?? 0,
                ulevel_color = info[4]!.AsArray()[2]?.GetValue<int>() ?? 0,
                ulevel_rank = info[4]!.AsArray()[3]?.GetValue<string>() ?? "",

                old_title = old_title,
                title = title,

                privilege_type = info[7]?.GetValue<int>() ?? 0,
                wealth_level = info[16]!.AsArray()[0]?.GetValue<int>() ?? 0
            };
        }

        public Dictionary<string, object> emoticon_options_dict =>
            emoticon_options is Dictionary<string, object> d ? d
            : JsonSerializer.Deserialize<Dictionary<string, object>>(emoticon_options.ToString() ?? "{}") ?? new();

        public Dictionary<string, object> voice_config_dict =>
            voice_config is Dictionary<string, object> d ? d
            : JsonSerializer.Deserialize<Dictionary<string, object>>(voice_config.ToString() ?? "{}") ?? new();

        public Dictionary<string, object> extra_dict
        {
            get
            {
                try
                {
                    var extra = mode_info["extra"];
                    return extra is Dictionary<string, object> d ? d
                         : JsonSerializer.Deserialize<Dictionary<string, object>>(extra.ToString() ?? "{}") ?? new();
                }
                catch { return new Dictionary<string, object>(); }
            }
        }
    }

    public class GiftMessage
    {
        public string gift_name = "";
        public int num = 0;
        public string uname = "";
        public string face = "";
        public int guard_level = 0;
        public long uid = 0;
        public long timestamp = 0;
        public int gift_id = 0;
        public int gift_type = 0;
        public string gift_img_basic = "";
        public string action = "";
        public int price = 0;
        public string rnd = "";
        public string coin_type = "";
        public int total_coin = 0;
        public string tid = "";
        public int medal_level = 0;
        public string medal_name = "";
        public int medal_room_id = 0;
        public long medal_ruid = 0;

        public static GiftMessage FromCommand(JsonObject data)
        {
            var medalInfo = data["medal_info"]?.AsObject();
            int medal_level = 0, medal_room_id = 0;
            long medal_ruid = 0;
            string medal_name = "";
            if (medalInfo != null)
            {
                medal_level = medalInfo["medal_level"]?.GetValue<int>() ?? 0;
                medal_name = medalInfo["medal_name"]?.GetValue<string>() ?? "";
                medal_room_id = medalInfo["anchor_room_id"]?.GetValue<int>() ?? 0;
                medal_ruid = medalInfo["target_id"]?.GetValue<long>() ?? 0;
            }

            var giftInfo = data["gift_info"]?.AsObject();
            string gift_img_basic = giftInfo?["img_basic"]?.GetValue<string>() ?? "";

            return new GiftMessage
            {
                gift_name = data["giftName"]?.GetValue<string>() ?? "",
                num = data["num"]?.GetValue<int>() ?? 0,
                uname = data["uname"]?.GetValue<string>() ?? "",
                face = data["face"]?.GetValue<string>() ?? "",
                guard_level = data["guard_level"]?.GetValue<int>() ?? 0,
                uid = data["uid"]?.GetValue<long>() ?? 0,
                timestamp = data["timestamp"]?.GetValue<long>() ?? 0,
                gift_id = data["giftId"]?.GetValue<int>() ?? 0,
                gift_type = data["giftType"]?.GetValue<int>() ?? 0,
                gift_img_basic = gift_img_basic,
                action = data["action"]?.GetValue<string>() ?? "",
                price = data["price"]?.GetValue<int>() ?? 0,
                rnd = data["rnd"]?.GetValue<string>() ?? "",
                coin_type = data["coin_type"]?.GetValue<string>() ?? "",
                total_coin = data["total_coin"]?.GetValue<int>() ?? 0,
                tid = data["tid"]?.GetValue<string>() ?? "",
                medal_level = medal_level,
                medal_name = medal_name,
                medal_room_id = medal_room_id,
                medal_ruid = medal_ruid
            };
        }
    }

    public class GuardBuyMessage
    {
        public long uid = 0;
        public string username = "";
        public int guard_level = 0;
        public int num = 0;
        public int price = 0;
        public int gift_id = 0;
        public string gift_name = "";
        public long start_time = 0;
        public long end_time = 0;

        public static GuardBuyMessage FromCommand(JsonObject data)
        {
            return new GuardBuyMessage
            {
                uid = data["uid"]?.GetValue<long>() ?? 0,
                username = data["username"]?.GetValue<string>() ?? "",
                guard_level = data["guard_level"]?.GetValue<int>() ?? 0,
                num = data["num"]?.GetValue<int>() ?? 0,
                price = data["price"]?.GetValue<int>() ?? 0,
                gift_id = data["gift_id"]?.GetValue<int>() ?? 0,
                gift_name = data["gift_name"]?.GetValue<string>() ?? "",
                start_time = data["start_time"]?.GetValue<long>() ?? 0,
                end_time = data["end_time"]?.GetValue<long>() ?? 0
            };
        }
    }

    public class UserToastV2Message
    {
        public long uid = 0;
        public string username = "";
        public int guard_level = 0;
        public int num = 0;
        public int price = 0;
        public string unit = "";
        public int gift_id = 0;
        public long start_time = 0;
        public long end_time = 0;
        public int source = 0;
        public string toast_msg = "";

        public static UserToastV2Message FromCommand(JsonObject data)
        {
            var senderInfo = data["sender_uinfo"]?.AsObject();
            var guardInfo = data["guard_info"]?.AsObject();
            var payInfo = data["pay_info"]?.AsObject();
            var giftInfo = data["gift_info"]?.AsObject();
            var option = data["option"]?.AsObject();

            var baseInfo = senderInfo?["base"]?.AsObject();

            return new UserToastV2Message
            {
                uid = baseInfo?["uid"]?.GetValue<long>() ?? 0,
                username = baseInfo?["name"]?.GetValue<string>() ?? "",
                guard_level = guardInfo?["guard_level"]?.GetValue<int>() ?? 0,
                num = payInfo?["num"]?.GetValue<int>() ?? 0,
                price = payInfo?["price"]?.GetValue<int>() ?? 0,
                unit = payInfo?["unit"]?.GetValue<string>() ?? "",
                gift_id = giftInfo?["gift_id"]?.GetValue<int>() ?? 0,
                start_time = guardInfo?["start_time"]?.GetValue<long>() ?? 0,
                end_time = guardInfo?["end_time"]?.GetValue<long>() ?? 0,
                source = option?["source"]?.GetValue<int>() ?? 0,
                toast_msg = data["toast_msg"]?.GetValue<string>() ?? ""
            };
        }
    }

    public class SuperChatMessage
    {
        public int price = 0;
        public string message = "";
        public string message_trans = "";
        public long start_time = 0;
        public long end_time = 0;
        public int time = 0;
        public long id = 0;
        public int gift_id = 0;
        public string gift_name = "";
        public long uid = 0;
        public string uname = "";
        public string face = "";
        public int guard_level = 0;
        public int user_level = 0;
        public string background_bottom_color = "";
        public string background_color = "";
        public string background_icon = "";
        public string background_image = "";
        public string background_price_color = "";
        public int medal_level = 0;
        public string medal_name = "";
        public int medal_room_id = 0;
        public long medal_ruid = 0;

        public static SuperChatMessage FromCommand(JsonObject data)
        {
            var medalInfo = data["medal_info"]?.AsObject();
            int medal_level = 0, medal_room_id = 0;
            long medal_ruid = 0;
            string medal_name = "";
            if (medalInfo != null)
            {
                medal_level = medalInfo["medal_level"]?.GetValue<int>() ?? 0;
                medal_name = medalInfo["medal_name"]?.GetValue<string>() ?? "";
                medal_room_id = medalInfo["anchor_room_id"]?.GetValue<int>() ?? 0;
                medal_ruid = medalInfo["target_id"]?.GetValue<long>() ?? 0;
            }

            var userInfo = data["user_info"]?.AsObject();
            var gift = data["gift"]?.AsObject();

            return new SuperChatMessage
            {
                price = data["price"]?.GetValue<int>() ?? 0,
                message = data["message"]?.GetValue<string>() ?? "",
                message_trans = data["message_trans"]?.GetValue<string>() ?? "",
                start_time = data["start_time"]?.GetValue<long>() ?? 0,
                end_time = data["end_time"]?.GetValue<long>() ?? 0,
                time = data["time"]?.GetValue<int>() ?? 0,
                id = data["id"]?.GetValue<long>() ?? 0,
                gift_id = gift?["gift_id"]?.GetValue<int>() ?? 0,
                gift_name = gift?["gift_name"]?.GetValue<string>() ?? "",
                uid = data["uid"]?.GetValue<long>() ?? 0,
                uname = userInfo?["uname"]?.GetValue<string>() ?? "",
                face = userInfo?["face"]?.GetValue<string>() ?? "",
                guard_level = userInfo?["guard_level"]?.GetValue<int>() ?? 0,
                user_level = userInfo?["user_level"]?.GetValue<int>() ?? 0,
                background_bottom_color = data["background_bottom_color"]?.GetValue<string>() ?? "",
                background_color = data["background_color"]?.GetValue<string>() ?? "",
                background_icon = data["background_icon"]?.GetValue<string>() ?? "",
                background_image = data["background_image"]?.GetValue<string>() ?? "",
                background_price_color = data["background_price_color"]?.GetValue<string>() ?? "",
                medal_level = medal_level,
                medal_name = medal_name,
                medal_room_id = medal_room_id,
                medal_ruid = medal_ruid
            };
        }
    }

    public class SuperChatDeleteMessage
    {
        public List<long> ids = new();

        public static SuperChatDeleteMessage FromCommand(JsonObject data)
        {
            var arr = data["ids"]?.AsArray();
            return new SuperChatDeleteMessage
            {
                ids = arr != null
                    ? arr.Select(t => t.GetValue<long>()).ToList()
                    : new List<long>()
            };
        }
    }

    public class InteractWordV2Message
    {
        public long uid = 0;
        public string username = "";
        public string face = "";
        public long timestamp = 0;
        public int msg_type = 0;

        // Proto 解析部分已注释，保持与 Python 一致
        /*
        public static InteractWordV2Message FromCommand(JsonObject data)
        {
            var proto = InteractWordV2Proto.Loads(Convert.FromBase64String(data["pb"]!.GetValue<string>()));
            return new InteractWordV2Message
            {
                uid = proto.uid,
                username = proto.uname,
                face = proto.uinfo.@base.face,
                timestamp = proto.timestamp,
                msg_type = proto.msg_type
            };
        }
        */
    }

}
