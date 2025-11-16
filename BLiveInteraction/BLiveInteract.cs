using Microsoft.Xna.Framework;
//using BLiveInteract;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;

namespace BLiveInteract;

[ApiVersion(2, 1)]
public class BLiveInsteract : TerrariaPlugin
{
    #region 插件信息
    public override string Name => "BLiveInteract(B站直播互动)";
    public override string Author => "PIENNNNN";
    public override Version Version => new(1, 1, 2);
    public override string Description => "Tshock插件, 用于监听指定B站直播间信息并与游戏服务器产生互动";
    #endregion

    #region 全局变量

    internal static Configuration Config = new();  //访问配置文件

    #endregion

    #region 注册与释放
    public BLiveInsteract(Main game) : base(game) { }
    public override void Initialize()
    {
        LoadConfig();
        //LoadAllPlayerData(); //从数据库里写入到内存中
        GeneralHooks.ReloadEvent += ReloadConfig;
        //ServerApi.Hooks.NetGreetPlayer.Register(this, this.OnGreetPlayer);
        //ServerApi.Hooks.ServerLeave.Register(this, this.OnServerLeave);
        //ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);

        TShockAPI.Commands.ChatCommands.Add(new Command(new List<string> { "blive.admin", "blive.test" }, Commands.BLiveCommand, "blive", "直播"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            GeneralHooks.ReloadEvent -= ReloadConfig;
            //ServerApi.Hooks.NetGreetPlayer.Deregister(this, this.OnGreetPlayer);
            //ServerApi.Hooks.ServerLeave.Deregister(this, this.OnServerLeave);
            //ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
            TShockAPI.Commands.ChatCommands.RemoveAll(x => x.CommandDelegate == Commands.BLiveCommand);
        }
        base.Dispose(disposing);
    }
    #endregion

    #region 配置重载读取与写入方法
    private static void ReloadConfig(ReloadEventArgs args = null!)
    {
        LoadConfig();
        args.Player.SendInfoMessage("[BLiveInteract]重新加载配置完毕。");
    }
    private static void LoadConfig()
    {
        Config = Configuration.Read();
        Config.Write();
    }
    #endregion


}

