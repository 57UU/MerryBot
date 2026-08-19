---
title: 启动配置
parent: 配置说明
nav_order: 1
---

`setting.toml` 位于数据目录（`<data>/setting.toml`），首次启动时自动生成带注释的默认模板。文件内容采用 YAML 语法（由 YamlDotNet 解析，与 `Agent.Tui` 的配置同一套库），目前仅支持 `web-address` 一个键：

```yaml
# WebUI 监听地址（默认 http://localhost:5000）
web-address: "http://localhost:5000"
```

- 支持 `#` 注释、带引号或不带引号的值；未知键会被忽略。
- 值为非法 URL 或 YAML 解析失败时回退默认 `http://localhost:5000`。
- 此文件中的配置项不在 WebUI 中显示，修改后需重启 MerryBot 生效。
