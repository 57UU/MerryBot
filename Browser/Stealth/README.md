# Stealth — 浏览器隐身模块

本目录下的代码来自 [SeleniumStealth.NET](https://www.nuget.org/packages/SeleniumStealth.NET/) 包，因该包的依赖问题无法直接使用，故反编译后移入项目本地维护。

## 来源

- **原始包**: `SeleniumStealth.NET` v1.0.0
- **反编译方式**: 使用 dotnet ILSpy/dnSpy 反编译得到 `StealthService`、模型类和 JS 资源
- **迁移原因**: 该包依赖的某些库版本与项目 .NET 10 目标框架不兼容

## 文件说明

| 文件 | 对应原包位置 | 说明 |
|------|-------------|------|
| `Stealth.cs` | `SeleniumStealth.NET.Clients.Stealth` | 入口类，提供 `Instantiate()` 和 `ApplyStealth()` 扩展方法 |
| `StealthService.cs` | `SeleniumStealth.NET.Services.StealthService` | 核心逻辑：创建 ChromeDriver + CDP 注入脚本 |
| `StealthInstanceSettings.cs` | `SeleniumStealth.NET.Clients.Models.StealthInstanceSettings` | 隐身功能开关配置 |
| `ChromeRuntimeSettings.cs` | `SeleniumStealth.NET.Clients.Models.ChromeRuntimeSettings` | Chrome Runtime 伪装配置 |
| `EStealthMode.cs` | `SeleniumStealth.NET.Clients.Enums.EStealthMode` | 隐身模式枚举 |
| `NavigatorInfo.cs` | `SeleniumStealth.NET.Clients.Models.NavigatorInfo` | 随机化的浏览器环境信息 |
| `JsFunctions.cs` | `SeleniumStealth.NET.Resources.JsFunctions` | 注入到每个新页面的 JS 隐身脚本 |

## 修改说明

与原包相比，做了以下适配：

1. **命名空间**从 `SeleniumStealth.NET.*` 改为 `BrowserService.Stealth`
2. **类名**避免与命名空间冲突，入口类改名为 `StealthClient`
3. **字典类型**改为 `Dictionary<string, object?>` 以匹配新版 Selenium API
4. **JS 脚本**根据功能和原包源码重新编写（非二进制提取），行为一致

## 使用方式

```csharp
using BrowserService.Stealth;

var options = new ChromeOptions();
options.ConfigureForWebScraping();  // BrowserUtility 中的本地方法
options.ApplyStealth();             // 本模块的扩展方法

var settings = new StealthInstanceSettings { ... };
var driver = StealthClient.Instantiate(options, settings);
```
