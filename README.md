# MarryBot

MarryBot是基于以napcat为上游的机器人框架，使用C#编写，支持插件化开发。

# 配置文件`setting.json`

```json
{
  "napcat_server": "ws://<address>:<port>", //napcat websocket地址
  "napcat_token": "<token>", //napcat token
  "qq_groups": [ //要监听的qq群号列表
    114514,
    1919810
  ],
  "variables": {
    "ai-token": "xxxxxxxxxx", //质谱api token
    "ai-prompt": "你是一个助人为乐的AI助手" //ai 提示词
  }
}
```

# 主要内置插件

## AI机器人
使用质谱的API进行开发，

内置了如下function call:
- bing搜索
- 网页浏览
- 查看时间
- 发送语音
- 查看微博热搜

*: 网络访问相关funtion call 通过seleium操纵chrome（需要提前安装）实现。

## 锐言锐语
随机返回一句herui老师的谆谆教诲

# 插件开发
1. 一个插件应当放在`plugins`项目的一个文件中
2. 应当继承于`Plugin`抽象类
3. 至少存在一个构造函数，参数与抽象类构造函数相同`public Plugin(PluginConfig config)`
4. 在类前面使用属性`PluginTag(string name,string descption,[bool isIgnore=false])`

主程序会通过反射加载`plugins`项目下的所有插件类，因此需要满足上述条件。

## 示例
```csharp
[PluginTag("About","使用 /about 来查看关于")]
public class About : Plugin
{
    private const string aboutMessage=
"""
# -------About-------

Marry Bot

本程序的目的是实现QQ机器人的模块化开发，以插件的形式增加功能

访问Github仓库 https://github.com/57UU/MarryBot 以获取更多信息
""";

    public About(PluginInterop interop) : base(interop)
    {
        Logger.Info("about plugin start");
    }
    public override void OnGroupMessageMentioned(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        if (IsStartsWith(chain, "/about"))
        {
            Actions.SendGroupMessage(groupId, aboutMessage);
        }
    } 
}
```
更多示例请查看`plugins`目录下的文件。

## 事件
| 函数 | 描述 |
| --- | --- |
| `OnGroupMessage` 函数 | 当收到新消息时，此函数会被调用 |
|`OnGroupMessageMentioned`|当收到新消息时bot被@，此函数会被调用|
|`OnGroupMessageNotMentioned`|当收到新消息时bot未被@，此函数会被调用|
| `OnLoaded` 函数 | 当插件全部被加载完后会执行的函数，可以放一些互操作性的初始化代码。 |



## API/属性
这些 API/属性 在抽象父类中被定义

|API|Description|Note
|:---:|:---|:---
|Actions Actions{get;}|获取`Actions`，用于发送消息
|Task SaveData\<T>(T data)|异步存储并序列化对象|依赖于[插件存储](#插件存储-pluginstorage)
|Task\<T> LoadData\<T>()|异步加载并反序列化对象|依赖于[插件存储](#插件存储-pluginstorage)
|bool IsEnable {set;protected get;}|是否启用|无论是否启用，插件都会被加载，当为假时OnMessageReceived函数不会被调用
|string? StartsWith {set;get;}|该项是属性，若设置，那么只有以`StartsWith`开头的消息会触发`OnMessageReceived`函数
|ISimpleLogger logger {get;}|获取`logger`，用于记录日志
|Interop interop {get;}|获取互操作性|

### 互操作性-interop
**注意** 对于互操作性，请不要在构造函数中使用（此时插件没有加载完），建议在`OnLoaded`函数中使用

|API/属性|Description|
|:---:|:---|
|T? FindPlugin\<T\>()|查找类型为T的插件，用于插件互操作性 [示例](https://github.com/57UU/MarryBot/blob/master/plugins/ViewDialog.cs)|
|IEnumerable<PluginInfo> PluginInfoGetter()|获取所有插件的PluginInfo|
|PluginStorage PluginStorage {get;}|获取插件存储|
|T? GetVariable<T>(string key)|获取设置中`Variable`自定义属性中的内容|

### 插件存储-PluginStorage

对于每个插件，都会分配一个独立的存储服务（依赖PluginTag设置的插件名），以字符串为单位进行储存于读取，现阶段的实现依赖于`SQLite3`

|API|Description|
|:---:|:---|
|Task Saver(string data)|异步存储字符串|
|Task<string> Getter()|异步读取字符串|

### 工具类-`MessageUtils`
|API|Description|
|:---:|:---|
|bool IsEqual(MessageChain? a,MessageChain? b)|判断两个消息链是否相同

### 日志记录器`logger`

|API|Description|
|:---:|:---|
|void Trace(string message)|记录踪迹日志
|void Debug(string message)|记录踪迹日志
|void Info(string message)|记录消息日志
|void Warn(string message)|记录警告日志
|void Error(string message)|记录错误日志
|void Fatal(string message)|记录崩溃日志


### PluginTag类属性标签

构造函数为`(string name, string description, bool isIgnore=false)`

分别对应插件名称，插件描述，是否忽略

当`isIgnore==true`时，插件不会被加载