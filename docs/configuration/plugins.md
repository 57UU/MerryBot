---
title: 插件配置
parent: 配置说明
nav_order: 3
---

每个插件拥有独立的配置，通过 WebUI「配置中心」（`/config`）维护，修改后若提示需要重启则重启生效。

## 1. Agent 插件（`agent`）

群聊 AI 机器人，支持工具调用、定时任务、技能（Skills）与记忆。配置类型 `AgentConfig`：

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `LlmModel` | `opencode-go/deepseek-v4-flash` | Agent 使用的主模型 ID（必须是「LLM 配置」页中已添加并启用的模型） |
| `AiPrompt` | `你是一个乐于助人、回答简洁的群聊助手。` | 发送给主模型的系统提示词（人设） |
| `MaxIterations` | `20` | 单次请求允许的最大工具调用迭代次数（实际钳制到 1–150） |
| `ContextCompactRatio` | `0.7` | 上下文达到模型窗口的该比例后自动压缩（钳制到 0.1–0.9） |
| `VisionLlmModels` | `[]` | 辅助视觉模型 ID 列表。主模型不具备视觉能力时按顺序逐个尝试，某个失效自动切换下一个；留空则禁用看图 |
| `VisionPrompt` | `请详细描述这张图片的内容。` | 交给辅助视觉模型的图片描述提示词 |
| `IdleSessionTimeoutHours` | `12` | 群聊会话空闲超过该时长（小时，支持小数如 0.5）后自动清理释放内存；非正数回退默认值 |
| `AllowShell` | `false` | 是否注册 bash/终端工具集。**默认关闭**；开启后模型可在常驻 shell 中执行任意命令，请确认信任该群的用户 |
| `ShellUser` | 空 | `AllowShell` 开启后，shell 命令以该 Linux 用户身份（`sudo -u user`）执行；留空则以机器人进程所属用户执行。仅 Linux 生效 |
| `MaxImageSizeMb` | `10` | `load_image` 等工具允许下载的图片大小上限（MB） |
| `MaxConcurrentToolCalls` | `4` | 模型单次迭代中并行执行的工具调用数上限（钳制到 1–64），防止并发工具导致资源/成本失控 |
| `MaxSubagents` | `3` | 同时运行中的子 Agent 任务数上限（钳制到 1–64） |
| `MaxBackgroundTasks` | `5` | 同时运行中的后台 shell 任务数上限（钳制到 1–64） |

**交互方式**：@机器人 后直接说话即进入对话；`@机器人 /new [内容]` 或 `@机器人 #新对话 [内容]` 清空上下文开新对话（后接内容会作为新对话第一条消息）；`@机器人 /compact [主题]` 手动压缩上下文。

**内置工具**：查看消息/图片（`get_message_image`）、待办清单、网页搜索/抓取、技能（Skills）、定时任务（cron）、记忆读写、子任务派发；`AllowShell` 开启时额外提供 bash 终端（可用 `image_path` 参数查看命令生成的图片）。每条会话消息（user/assistant/tool）会写入 `ai_messages` 审计记录，仅保存文本，不受上下文压缩影响。

> 💡 **视觉能力说明**：主模型具备 `ImageInput` 能力时（如 GLM-4V、Qwen-VL），群聊图片会直接注入对话供主模型查看；主模型无视觉能力时（如 DeepSeek），配置 `VisionLlmModels` 后由辅助模型生成图片描述，未配置则图片工具提示不可用。

## 2. LLM Provider 插件（`llm-provider`）

管理可执行 LLM Provider、模型和 API Key。**没有 TOML 配置项**，全部通过 WebUI「LLM 配置」页（`/llmproviders`）维护：

- Provider、模型和 Key 保存在 `plugin_data.db` 的 `llm-provider` 作用域集合中（providers / models / keys / meta），不在配置表中。
- **Key 加密**：写入前使用本机 Data Protection 加密，密钥环位于数据目录下的 `llm-provider-key-ring/`；列表和 API 响应只会显示末四位及指纹（`…{末四位} ({SHA256 前 8 位})`），无法读取原文。Key 仅可写入，不会再次显示。
- **models.dev 目录**：只提供目录元数据，缓存在机器人数据目录的 `models.dev-api.json`（首次自动下载，升级后沿用）。平时目录搜索优先使用本地缓存，点击「刷新 models.dev」才联网更新。导入时会带入模型上下文/输出上限和能力标签，但不会覆盖已手工设置的 API 地址、格式或启用状态。
- **API 格式**：支持 OpenAI Chat Completions、OpenAI Responses、Anthropic Messages 三种；请确认 API 地址与所选格式兼容并填入自己的 Key。
- 默认模型：可在「LLM 配置」页指定；未指定时取第一个启用的模型。

## 3. 自动+1 插件（`auto-increase`）

有刷屏消息时自动 +1。配置类型 `AutoIncreaseConfig`：

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `RepeatTime` | `3` | 相同消息重复出现该次数后触发自动 +1（最小 2，小于 2 会被钳制） |

## 4. 版本管理插件（`view-version`）

管理员插件，命令：`/version` 查看当前版本；`/update [-f]` 检测并更新软件（`-f` 强制）；`/reload` 重载程序。`/update` 和 `/reload` 仅 `AuthorizedUser` 可用，其余用户会收到 `401 Unauthorized`。

## 5. 其他简单插件

| 插件 | 命令 | 说明 |
| --- | --- | --- |
| `help` | `/help` | 列出已加载插件 |
| `about` | `/about` | 查看关于信息 |
| `herui-saying` | `/hr` | 获取一条"锐言锐语"（数据每小时自动更新） |
