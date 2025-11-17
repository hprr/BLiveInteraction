using TShockAPI;
using static BLiveInteract.BLiveInsteract;
using Microsoft.Xna.Framework;

namespace BLiveInteract;

internal class Commands
{
    private static int roomid = -1;   // 当前要监听的房间号
    //public static bool roomStarted = BLiveListener.Instance.IsRunning;

    #region 主指令方法
    internal static void BLiveCommand(CommandArgs args)
    {
        if (!Config.Enabled)
        {
            args.Player.SendInfoMessage("插件[BLiveInteract]功能未启用 !");
            args.Player.SendInfoMessage("如需使用该插件功能, 请打开B站直播.json, 设置 全局功能 = true");
            return;
        }

        TSPlayer plr = args.Player;

        bool admin = plr.HasPermission("blive.admin");

        //直播监听房间号
        //int  roomid = -1;
        //标记直播间是否已经开启
        //var data = BLiveInsteract.MyData.FirstOrDefault(d => d != null && d.Name == plr.Name);

        Color color = new(255, 250, 205);

        //子命令数量为0时
        if (args.Parameters.Count == 0)
        {
            HelpCmd(plr);
        }

        //子命令数量超过1个时 
        if (args.Parameters.Count >= 1)
        {
            switch (args.Parameters[0].ToLower())
            {

                case "on":
                    {
                        // 没有管理权限 返回
                        if (!admin)
                        {
                            plr.SendErrorMessage("你没有权限执行此命令！");
                            return;
                        }
                        if(BLiveListener.Instance.IsRunning)
                        {
                            plr.SendWarningMessage($"B站直播间[{roomid}]互动功能正在运行!");
                            return;
                        }
                        else if (roomid == -1)
                        {
                            plr.SendWarningMessage($"B站直播间号缺失! 请先使用/blive set <直播间号> 设置直播间!");
                            return;
                        }
                        // ① 从配置文件拿 SESSDATA（仅在非空时传递）
                        string sessDataRaw = Config.SESSDATA;
                        string sessDataHeader = "";
                        if (string.IsNullOrWhiteSpace(sessDataRaw))
                        {
                            plr.SendWarningMessage("如果想要解决用户名屏蔽问题, 请填写配置项 SESSDATA !");
                        }
                        else
                        {
                            sessDataHeader = "SESSDATA=" + sessDataRaw;
                        }

                        // ③ 交给 Listener
                        BLiveListener.Instance.Start(roomid, sessDataHeader);
                       
                        if (BLiveListener.Instance.IsRunning)
                        {
                            TSPlayer.All.SendMessage($"[BLiveInteract] 已开启 B站直播间 [{roomid}] 的互动功能! ", color);
                        }
                        else
                        {
                            plr.SendErrorMessage("未知错误! 启动实例失败! 请联系开发者!");
                        }


                    }
                    break;

                case "off":
                    {
                        // 没有管理权限 返回
                        if (!admin)
                        {
                            plr.SendErrorMessage("你没有权限执行此命令！");
                            return;
                        }

                        if (!BLiveListener.Instance.IsRunning)
                        {
                            plr.SendWarningMessage("弹幕监听当前未运行！");
                            return;
                        }
                        //string sessData = Config.SESSDATA;
                        BLiveListener.Instance.Stop();
                        
                        if (!BLiveListener.Instance.IsRunning)
                        {
                            TSPlayer.All.SendMessage($"[BLiveInteract] 已关闭 B站直播间 [{roomid}] 的互动功能!", color);

                        }
                        else
                        {
                            plr.SendErrorMessage("未知错误! 关闭实例失败! 请联系开发者!");

                        }

                    }
                    break;

                case "info":
                    {
                        // 没有管理权限 返回
                        if (!admin)
                        {
                            plr.SendErrorMessage("你没有权限执行此命令！");
                            return;
                        }


                        plr.SendMessage("=== B站直播间互动情况 ===", color);
                        plr.SendMessage("这是一个基于ws监听B站直播间实时信息的小工具", color);
                        plr.SendMessage($"直播间号: {(roomid == -1? "未设置" : roomid.ToString())}" , color);
                        plr.SendMessage($"监听状态：{(BLiveListener.Instance.IsRunning ? "已开启" : "未开启")}", color);
                        plr.SendMessage("强烈建议手动配置json中的SESSDATA", color);

                    }
                    break;

                case "set":
                    {
                        // 没有管理权限 返回
                        if (!admin)
                        {
                            plr.SendErrorMessage("你没有权限执行此命令！");
                            return;
                        }

                        if (BLiveListener.Instance.IsRunning)
                        {
                            plr.SendWarningMessage($"[BLive] 当前正在监听B站直播间 [{roomid}] , 请先/blive off关闭监听");
                            return;
                        }

                        // 参数检查
                        if (args.Parameters.Count < 2 || !int.TryParse(args.Parameters[1], out int rid) || rid <= 0)
                        {
                            plr.SendErrorMessage("用法: /blive set <正整数房间号>");
                            return;
                        }

                        roomid = rid;
                        plr.SendSuccessMessage($"当前直播间号设置为 [{roomid}]");
                    }
                    break;



                case "reset":
                    {
                        // 没有管理权限 返回
                        if (!admin)
                        {
                            plr.SendErrorMessage("你没有权限执行此命令！");
                            return;
                        }

                        //先停止监听
                        if (BLiveListener.Instance.IsRunning)
                        {
                            BLiveListener.Instance.Stop();
                        }
                        roomid = -1;
                        plr.SendMessage("[BliveInteract] 已重置并关闭B站直播互动!", color);
                        
                    }
                    break;
                default:
                    HelpCmd(plr);
                    break;
            }
        }
    }
    #endregion

    #region 菜单方法
    private static void HelpCmd(TSPlayer plr)
    {
        var color = new Color(240, 250, 150);
        plr.SendMessage("=== B站直播互动 作者: PIENNNNN ===", color);
        plr.SendMessage($"/blive on ——开启直播监听", color);
        plr.SendMessage($"/blive off ——关闭直播监听", color);
        plr.SendMessage($"/blive set <直播间ID> ——修改直播间号", color);
        plr.SendMessage($"/blive info ——查看直播间状态", color);

        //plr.SendMessage($"/cmd del 名字 ——移除指定数据", color);
        //plr.SendMessage($"/cmd list ——列出所有数据", color);
        //plr.SendMessage($"/cmd reset ——清空所有数据", color);
    }
    #endregion

}
