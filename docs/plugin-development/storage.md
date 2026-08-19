---
title: 存储与工具
parent: 插件开发
nav_order: 3
---

## 插件存储 - PluginStorage

对于每个插件，都会分配一个独立的存储服务（依赖 PluginTag 设置的插件 id），以 object 为单位进行储存与读取，现阶段的实现依赖于 NoSQL：

| API | Description |
|:---:|:---|
| Task\<T\> Load\<T\>() | 异步加载对象；不存在时返回 null |
| Task\<T\> Load\<T\>(T defaultValue) | 异步加载对象，如果不存在则返回默认值 |
| Task Save\<T\>(T data) | 异步存储对象 |

## Scoped 数据库 - PluginDatabase

`PluginStorage` 适合保存一个简单的插件对象或群级对象。需要多个表、索引或复杂查询时，可使用 `Interop.PluginDatabase`；每个插件只会访问以自身 `PluginTag.Id` 为 scope 的 collection。

```csharp
public sealed class Todo
{
    public int Id { get; set; }
    public long GroupId { get; set; }
    public string Content { get; set; } = "";
}

var todos = Interop.PluginDatabase.GetCollection<Todo>("todos");
await todos.EnsureIndexAsync(x => x.GroupId);
await todos.UpsertAsync(new Todo { Id = 1, GroupId = 123, Content = "example" });
```

`GetCollection<T>(name)` 会按需创建当前插件的表；`DropCollectionAsync(name)` 只能删除当前插件 scope 内的表。底层数据库由 Core 管理，插件不需要、也不能自行释放连接。

## 工具类 - `MessageUtils`

| API | Description |
|:---:|:---|
| bool IsEqual(IReadOnlyList\<TypedMessage\>? a, IReadOnlyList\<TypedMessage\>? b) | 比较两个消息链是否相等（逐条比较类型与内容，忽略发送者；任一为空或长度不等返回 false） |

## 日志记录器 `logger`

| API | Description |
|:---:|:---|
| void Trace(string message) | 记录踪迹日志 |
| void Debug(string message) | 记录调试日志 |
| void Info(string message) | 记录消息日志 |
| void Warn(string message) | 记录警告日志 |
| void Error(string message) | 记录错误日志 |
| void Fatal(string message) | 记录崩溃日志 |
