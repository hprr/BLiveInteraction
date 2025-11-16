using BliveDM;
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using ws_base;
using client_web;
using models_web;
using System.Text.Json;


namespace Handlers
{
    #region ---------- 接口 ----------
    //public interface HandlerInterface
    //{
    //    void handle(WebSocketClientBase client, Dictionary<string, object?> command);
    //    void on_client_stopped(WebSocketClientBase client, Exception? exception);
    //}
    #endregion

    #region ---------- BaseHandler ----------
    public abstract class BaseHandler : ws_base.HandlerInterface
    {
        private static 
            
            
            HashSet<string> logged_unknown_cmds = new()
        {
            "COMBO_SEND","ENTRY_EFFECT","HOT_RANK_CHANGED","HOT_RANK_CHANGED_V2",
            "LIVE","LIVE_INTERACTIVE_GAME","NOTICE_MSG","ONLINE_RANK_COUNT",
            "ONLINE_RANK_TOP3","ONLINE_RANK_V2","PK_BATTLE_END","PK_BATTLE_FINAL_PROCESS",
            "PK_BATTLE_PROCESS","PK_BATTLE_PROCESS_NEW","PK_BATTLE_SETTLE","PK_BATTLE_SETTLE_USER",
            "PK_BATTLE_SETTLE_V2","PREPARING","ROOM_REAL_TIME_MESSAGE_UPDATE","STOP_LIVE_ROOM_LIST",
            "SUPER_CHAT_MESSAGE_JPN","USER_TOAST_MSG","WIDGET_BANNER"
        };

        /// <summary>Python: _CMD_CALLBACK_DICT</summary>
        private readonly Dictionary<string, Callback?> _CMD_CALLBACK_DICT;

        private delegate void Callback(BaseHandler self, WebSocketClientBase client, Dictionary<string, object?> command);


        protected BaseHandler()
        {
            _CMD_CALLBACK_DICT = new()
            {
                ["DANMU_MSG"] = (s, c, cmd) =>
                {
                    var info = JsonNode.Parse(((JsonElement)cmd["info"]!).GetRawText()).AsArray();
                    s._on_danmaku(c, DanmakuMessage.FromCommand(info));
                },
                ["_HEARTBEAT"] = (s, c, cmd) =>
                {
                    var data = JsonNode.Parse(((JsonElement)cmd["data"]!).GetRawText()).AsObject();
                    s._on_heartbeat(c, HeartbeatMessage.FromCommand(data));
                },
                ["SEND_GIFT"] = (s, c, cmd) => s._on_gift(c, GiftMessage.FromCommand(((JsonNode)cmd["data"]!).AsObject())),
                ["GUARD_BUY"] = (s, c, cmd) => s._on_buy_guard(c, GuardBuyMessage.FromCommand(((JsonNode)cmd["data"]!).AsObject())),
                ["USER_TOAST_MSG_V2"] = (s, c, cmd) => s._on_user_toast_v2(c, UserToastV2Message.FromCommand(((JsonNode)cmd["data"]!).AsObject())),
                ["SUPER_CHAT_MESSAGE"] = (s, c, cmd) => s._on_super_chat(c, SuperChatMessage.FromCommand(((JsonNode)cmd["data"]!).AsObject())),
                ["SUPER_CHAT_MESSAGE_DELETE"] = (s, c, cmd) => s._on_super_chat_delete(c, SuperChatDeleteMessage.FromCommand(((JsonNode)cmd["data"]!).AsObject())),
            };
        }

        public void Handle(WebSocketClientBase client, Dictionary<string, object?> command)
        {
            var cmd = command.GetValueOrDefault("cmd", "")!.ToString()!;
            var pos = cmd.IndexOf(':');          // 2019-5-29 B站弹幕升级新增了参数
            if (pos != -1) cmd = cmd[..pos];

            if (!_CMD_CALLBACK_DICT.ContainsKey(cmd))
            {
                if (logged_unknown_cmds.Add(cmd))
                    Console.WriteLine($"[WARN] room={client.room_id} unknown cmd={cmd}");
                return;
            }
            var callback = _CMD_CALLBACK_DICT[cmd];
            callback?.Invoke(this, client, command);
        }

        public virtual void OnClientStopped(WebSocketClientBase client, Exception? exception) { }

        #region ---------- 虚方法（用户 override） ----------
        protected virtual void _on_heartbeat(WebSocketClientBase client, HeartbeatMessage message) { }
        protected virtual void _on_danmaku(WebSocketClientBase client, DanmakuMessage message) { }
        protected virtual void _on_gift(WebSocketClientBase client, GiftMessage message) { }
        protected virtual void _on_buy_guard(WebSocketClientBase client, GuardBuyMessage message) { }
        protected virtual void _on_user_toast_v2(WebSocketClientBase client, UserToastV2Message message) { }
        protected virtual void _on_super_chat(WebSocketClientBase client, SuperChatMessage message) { }
        protected virtual void _on_super_chat_delete(WebSocketClientBase client, SuperChatDeleteMessage message) { }

        #endregion
    }

}
#endregion
