# **Tshock插件, 用于监听B站直播信息**

- 作者: PIENNNNN

- 出处: [hprr/BLiveInteraction: 基于websocket监听B站直播间弹幕信息的小模块](https://github.com/hprr/BLiveInteraction)

- 注: 该项目使用websocket实现监听，不走B站官方API，无需额外申请。

## 指令

| 语法               | 权限          | 说明                   |
| ---------------- |:-----------:|:--------------------:|
| /blive           | blive.admin | 获取指令帮助               |
| /blive set <房间号> | blive.admin | 设置直播间房号(该步骤不会自动启动监听) |
| /blive on        | blive.admin | 开始监听B站直播信息           |
| /blive off       | blive.admin | 停止监听B站直播信息           |
| /blive info      | blive.admin | 查看当前直播监听状态           |

## 使用方法

- /blive set <直播间号> 设置需要监听的直播房间

- /blive on 启动直播间监听即可

## 配置

> 配置文件: B站直播.json 

```json5
{
  "全局功能": true, //插件的全局开关
  "SESSDATA": "", //SESSDATA, B站的登录标识
  "弹幕进游戏开关": true, //设置为true时，会自动将弹幕信息转发到游戏内
  "礼物进游戏开关": true, //同理
  "上舰进游戏开关": true, //同理
  "SC进游戏开关": true, //同理
  "进场进游戏开关": true, //同理
  "弹幕颜色": "3498db", //弹幕消息颜色调整
  "礼物颜色": "FF69B4", //礼物消息颜色调整
  "SC颜色": "FFA500", //SC消息颜色调整
  "消息最大长度": 60 
}
```

## 注意事项!

配置中的**SESSDATA**是B站的登录标识之一，强烈建议填写，否则启动监听后一分钟内就会出现用户名被屏蔽的情况。

> **获取SESSDATA:**
> 
> 在已登录B站的情况下，访问B站首页[哔哩哔哩 (゜-゜)つロ 干杯~-bilibili](https://www.bilibili.com/)
> 
> 按**F12**打开开发者工具，选中**网络**，刷新一次网页。
> 
> 找到第一条名为www.bilibili.com的请求
> 
> 在**标头-请求标头**找到**Cookie**, 在**Cookie**中找到**SESSDATA=XXXXXXXX**字段
> 
> 把等号后面的那串超长的字符复制下来(到;之前为止), 填写到配置的SESSDATA即可

- 注: 填写完成后游戏内/reload即可, SESSDATA与B站用户绑定, 往往1~2个月内就会失效，因此需要经常手动更新。

## 更新日志

### v1.0.0

- 完成基本功能框架，跑通弹幕监听

### v1.1.0

- 添加礼物/SC/上舰信息

- 完善配置信息

### v1.1.1

- 解决短号直播间连接失败BUG

- 引入SESSDATA解决用户名屏蔽问题
