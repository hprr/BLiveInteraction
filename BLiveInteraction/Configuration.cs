using TShockAPI;
using Newtonsoft.Json;

namespace BLiveInteract;

internal class Configuration
{
    public static readonly string FilePath = Path.Combine(TShock.SavePath, "B站直播.json");

    [JsonProperty("全局功能", Order = 0)]
    public bool Enabled { get; set; } = false;

    [JsonProperty("SESSDATA(B站cookie中手动获取)", Order = 1)]
    public string SESSDATA { get; set; } = "";

    [JsonProperty("弹幕进游戏开关", Order = 2)]
    public bool DanmuToGame { get; set; } = true;

    [JsonProperty("礼物进游戏开关", Order = 3)]
    public bool GiftToGame { get; set; } = true;

    [JsonProperty("上舰进游戏开关", Order = 4)]
    public bool GuardToGame { get; set; } = true;

    [JsonProperty("SC进游戏开关", Order = 5)]
    public bool SCToGame { get; set; } = true;

    [JsonProperty("进场进游戏开关", Order = 6)]
    public bool EntryToGame { get; set; } = true;

    [JsonProperty("弹幕颜色", Order = 7)]
    public string DanmuColor { get; set; } = "00D1F1";

    [JsonProperty("礼物颜色", Order = 8)]
    public string GiftColor { get; set; } = "FF69B4";

    [JsonProperty("SC颜色", Order = 9)]
    public string SCColor { get; set; } = "FFA500";

    [JsonProperty("消息最大长度", Order = 10)]
    public int MaxMsgLen { get; set; } = 60;

    #region 预设参数方法
    public void SetDefault()
    {
    }
    #endregion

    #region 读取与创建配置文件方法
    public void Write()
    {
        string json = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(FilePath, json);
    }

    public static Configuration Read()
    {
        if (!File.Exists(FilePath))
        {
            var NewConfig = new Configuration();
            NewConfig.SetDefault();
            new Configuration().Write();
            return NewConfig;
        }
        else
        {
            string jsonContent = File.ReadAllText(FilePath);
            return JsonConvert.DeserializeObject<Configuration>(jsonContent)!;
        }
    }
    #endregion
}