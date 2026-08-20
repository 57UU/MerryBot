---
title: 存储与工具
parent: 插件开发
nav_order: 5
---

## 插件存储 - PluginStorage

每个插件按 `PluginTag.Id` 获得独立存储。`PluginStorage` 适合保存一个简单对象：

| API | Description |
|:---:|:---|
| `Task<T?> Load<T>()` | 异步加载对象；不存在时返回 `null` |
| `Task<T> Load<T>(T defaultValue)` | 异步加载；不存在时返回传入默认值 |
| `Task Save<T>(T data)` | 异步保存对象 |

## Scoped 数据库 - PluginDatabase

需要多个集合、索引或复杂查询时，使用 `Interop.PluginStorage.PluginDatabaseScope`。它只会访问当前插件的 collection，插件不能跨 scope 读取或删除其他插件的数据。

```csharp
public sealed class Todo
{
    public int Id { get; set; }
    public long GroupId { get; set; }
    public string Content { get; set; } = "";
}

var todos = Interop.PluginStorage.PluginDatabaseScope.GetCollection<Todo>("todos");
await todos.EnsureIndexAsync(x => x.GroupId);
await todos.UpsertAsync(new Todo { Id = 1, GroupId = 123, Content = "example" });
```

`GetCollection<T>(name)` 会按需创建当前插件的表；`DropCollectionAsync(name)` 只能删除当前 scope 内的表。底层数据库由宿主管理，插件不应自行释放连接。

## 工具类 - `MessageUtils`

| API | Description |
|:---:|:---|
| bool IsEqual(IReadOnlyList\<TypedMessage\>? a, IReadOnlyList\<TypedMessage\>? b) | 比较两个消息链是否相等（逐条比较类型与内容，忽略发送者；任一为空或长度不等返回 false） |

## 日志记录器 `Logger`

| API | Description |
|:---:|:---|
| `Trace` / `Debug` / `Info` | 诊断、调试和常规日志 |
| `Warn` / `Error` / `Fatal` | 警告、异常和致命错误 |

使用基类受保护字段 `Logger`，不要直接写控制台。宿主会将插件日志写入 NLog，logger 名为 `plugin:<插件 Id>`。
