# MerryBot 配置文件说明 (setting.toml)

`setting.toml` 是 MerryBot 的主配置文件。程序默认会在工作目录下的 `data` 文件夹（或由 `MERRY_BOT` 环境变量指定的目录）中寻找或生成该文件。

## 基础配置

```toml
napcat-server = "ws://localhost:30001"    # Napcat WebSocket 地址
napcat-token = "xxxxxxxx"                 # Napcat Token
qq-groups = [                             # 要监听并处理消息的 QQ 群号列表
    114514,
    1919810
]
authorized-user = 114514                  # 授权用户（Bot 管理员）QQ号，部分插件高危操作会验证此 QQ 号
```

## 插件配置 (`[variables]`)

每个插件在 `setting.toml` 中都有独立的命名空间，表名即为插件的 ID。各插件的配置项相互隔离。

### 1. LLM Provider（数据库管理）

打开历史后台的“LLM 配置”页，先搜索并选择 models.dev Provider，再从该 Provider 的模型目录中添加所需模型；也可手工维护 OpenAI Chat Completions 兼容的 Provider、模型和默认模型。

Provider、模型和 API Key 都保存于 `plugin_data.db` 的 `llm-provider` 作用域表中；`setting.toml` 不再保存 Token。Key 写入数据库前会用本机 Data Protection 密钥加密，列表和 API 响应只会显示末四位及指纹，无法读取原文。

models.dev 只提供目录元数据，并会缓存在机器人运行目录的 `models.dev-api.json`：平时目录搜索优先使用本地缓存，点击“刷新 models.dev”才联网更新。导入时会带入模型上下文/输出上限和能力标签，但不会覆盖已手工设置的 API 地址、格式或启用状态；请确认 API 地址兼容 OpenAI `/chat/completions` 并填入自己的 Key。

> 如果后台监听在 `0.0.0.0`，请只经受控内网或 HTTPS 反向代理访问“LLM 配置”页。Key 虽不会由页面读回，但写入时仍会通过浏览器提交。

### 2. AI 机器人插件 (`[variables.agent]`)

```toml
ai-prompt = "你是一个助人为乐的AI助手"    # AI 的 System Prompt（人设提示词）
```

### 3. 存储管理插件 (`[variables.storage-manager]`)

```toml
machine-code = 0                          # 机器码（随便填写，如果以后要合并记录，会更方便）
web-address = "http://0.0.0.0:5000"       # 后台 Web 服务监听地址
```

### 4. 群刊插件 (`[variables.highlights]`)

负责整理群聊消息并生成有趣的群刊。

```toml
message-count = 500                       # 触发生成群刊的消息数量阈值
section-count = 3                         # 群刊包含的栏目/章节数量
llm = "deepseek/deepseek-v4-pro"          # 生成群刊使用的 LLM 模型（留空则使用全局默认模型）
temperature = 1.3                         # 温度参数，控制生成文本的随机性和创造性
response-timeout = 120                    # AI 响应超时时间（秒），默认 120 秒
enable-header = false                     # 是否启用群刊页眉
enable-footer = true                      # 是否启用群刊页脚
highlights-prompt = """你是一个有趣的群刊编辑...""" # 群刊 AI 生成的系统提示词（支持多行文本）
```

### 5. Shell 终端插件 (`[variables.run-command]`)

提供 Linux Shell 终端功能，仅在 Linux 环境下可用。

```toml
shell-user = "merrybot"                   # Shell 终端使用的 Linux 用户名（默认 merrybot）
```
