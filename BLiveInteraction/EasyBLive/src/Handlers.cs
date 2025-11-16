
using System.Text.Json.Nodes;
using System.Text.Json;
using static EasyDANMU.src.DanmuWsClient;


namespace EasyDANMU.src
{
    #region ---------- 接口 ----------
    //public interface HandlerInterface
    //{
    //    void handle(DanmuWsClient client, Dictionary<string, object?> command);
    //    void on_client_stopped(DanmuWsClient client, Exception? exception);
    //}
    #endregion

    #region ---------- BaseHandler ----------
    public abstract class BaseHandler : HandlerInterface
    {
        private static 
            
            
            HashSet<string> logged_unknown_cmds = new()
        {
            "COMBO_SEND","ENTRY_EFFECT","HOT_RANK_CHANGED","HOT_RANK_CHANGED_V2",
            "LIVE","LIVE_INTERACTIVE_GAME","NOTICE_MSG","ONLINE_RANK_COUNT",
            "ONLINE_RANK_TOP3","ONLINE_RANK_V2","PK_BATTLE_END","PK_BATTLE_FINAL_PROCESS",
            "PK_BATTLE_PROCESS","PK_BATTLE_PROCESS_NEW","PK_BATTLE_SETTLE","PK_BATTLE_SETTLE_USER",
            "PK_BATTLE_SETTLE_V2","PREPARING","ROOM_REAL_TIME_MESSAGE_UPDATE","STOP_LIVE_ROOM_LIST",
            "SUPER_CHAT_MESSAGE_JPN","USER_TOAST_MSG","WIDGET_BANNER","WATCHED_CHANGE",
            "INTERACT_WORD_V2","ONLINE_RANK_V3","LOG_IN_NOTICE"
        };

        /// <summary>Python: _CMD_CALLBACK_DICT</summary>
        private readonly Dictionary<string, Callback?> _CMD_CALLBACK_DICT;

        private delegate void Callback(BaseHandler self, DanmuWsClient client, Dictionary<string, object?> command);


        protected BaseHandler()
        {
            _CMD_CALLBACK_DICT = new()
            {
                ["DANMU_MSG"] = (s, c, cmd) =>
                {
                    // cmd["info"] 是 object → 先序列化成字符串 → JsonNode
                    var json = JsonSerializer.Serialize(cmd["info"]);
                    var info = JsonNode.Parse(json)!.AsArray();
                    s._on_danmaku(c, DanmakuMessage.FromCommand(info));
                },
                ["_HEARTBEAT"] = (s, c, cmd) =>
                {
                    var json = JsonSerializer.Serialize(cmd["data"]);
                    var data = JsonNode.Parse(json)!.AsObject();
                    s._on_heartbeat(c, HeartbeatMessage.FromCommand(data));
                },
                ["SEND_GIFT"] = (s, c, cmd) =>
                {
                    var json = JsonSerializer.Serialize(cmd["data"]);
                    s._on_gift(c, GiftMessage.FromCommand(JsonNode.Parse(json)!.AsObject()));
                },
                ["GUARD_BUY"] = (s, c, cmd) =>
                {
                    var json = JsonSerializer.Serialize(cmd["data"]);
                    s._on_buy_guard(c, GuardBuyMessage.FromCommand(JsonNode.Parse(json)!.AsObject()));
                },
                ["USER_TOAST_MSG_V2"] = (s, c, cmd) =>
                {
                    var json = JsonSerializer.Serialize(cmd["data"]);
                    s._on_user_toast_v2(c, UserToastV2Message.FromCommand(JsonNode.Parse(json)!.AsObject()));
                },
                ["SUPER_CHAT_MESSAGE"] = (s, c, cmd) =>
                {
                    var json = JsonSerializer.Serialize(cmd["data"]);
                    s._on_super_chat(c, SuperChatMessage.FromCommand(JsonNode.Parse(json)!.AsObject()));
                },
                ["SUPER_CHAT_MESSAGE_DELETE"] = (s, c, cmd) =>
                {
                    var json = JsonSerializer.Serialize(cmd["data"]);
                    s._on_super_chat_delete(c, SuperChatDeleteMessage.FromCommand(JsonNode.Parse(json)!.AsObject()));
                },
            };
        }

        public void Handle(DanmuWsClient client, Dictionary<string, object?> command)
        {
            var cmd = command.GetValueOrDefault("cmd", "")!.ToString()!;
            var pos = cmd.IndexOf(':');          // 2019-5-29 B站弹幕升级新增了参数
            if (pos != -1) cmd = cmd[..pos];

            if (!_CMD_CALLBACK_DICT.ContainsKey(cmd))
            {
                if (logged_unknown_cmds.Add(cmd))
                    Console.WriteLine($"[WARN] room={client._auth.roomid} unknown cmd={cmd}");
                return;
            }
            var callback = _CMD_CALLBACK_DICT[cmd];
            callback?.Invoke(this, client, command);
        }

        public virtual void OnClientStopped(DanmuWsClient client, Exception? exception) { }

        #region ---------- 虚方法（用户 override） ----------
        protected virtual void _on_heartbeat(DanmuWsClient client, HeartbeatMessage message) { }
        protected virtual void _on_danmaku(DanmuWsClient client, DanmakuMessage message) { }
        protected virtual void _on_gift(DanmuWsClient client, GiftMessage message) { }
        protected virtual void _on_buy_guard(DanmuWsClient client, GuardBuyMessage message) { }
        protected virtual void _on_user_toast_v2(DanmuWsClient client, UserToastV2Message message) { }
        protected virtual void _on_super_chat(DanmuWsClient client, SuperChatMessage message) { }
        protected virtual void _on_super_chat_delete(DanmuWsClient client, SuperChatDeleteMessage message) { }

        #endregion
    }

}
#endregion
