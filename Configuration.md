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

### 1. AI 机器人插件 (`[variables.agent]`)

```toml
llm-model = "deepseek-chat"               # 默认选择的 LLM 模型名称
ai-token-zhipu = "xxxxxxxxxx"             # 智谱 AI 的 API Token
ai-token-deepseek = "xxxxxxxxxx"          # DeepSeek 的 API Token
ai-token-ali = "xxxxxxxxxx"               # 阿里通义千问的 API Token
ai-token-xianyv = "xxxxxxxxxx"            # 咸鱼 API 等其他自定义中转的 Token
ai-prompt = "你是一个助人为乐的AI助手"    # AI 的 System Prompt（人设提示词）
use_function_call_reply = false           # 是否启用 Function Call 回复
webview-summarizer-model = "xianyv/qwen3.5-plus" # 用于网页总结的特定模型
shell-user = "merrybot"                   # Shell 终端使用的 Linux 用户名（默认 merrybot）
```
> **注意**：`llm-model` 支持的参数定义在 `ZhipuAi/ModelPreset.cs` 枚举中，也可以通过 `extra-models.toml` 添加自定义模型。

### 2. 存储管理插件 (`[variables.storage-manager]`)

```toml
machine-code = 0                          # 机器码（随便填写，如果以后要合并记录，会更方便）
web-address = "http://0.0.0.0:5000"       # 后台 Web 服务监听地址
```

### 3. 群刊插件 (`[variables.highlights]`)

负责整理群聊消息并生成有趣的群刊。

```toml
message-count = 500                       # 触发生成群刊的消息数量阈值
section-count = 3                         # 群刊包含的栏目/章节数量
llm = "deepseek/deepseek-reasoner"        # 生成群刊使用的专属 LLM 模型
temperature = 1.3                         # 温度参数，控制生成文本的随机性和创造性
enable-header = false                     # 是否启用群刊页眉
enable-footer = true                      # 是否启用群刊页脚
highlights-prompt = """你是一个有趣的群刊编辑...""" # 群刊 AI 生成的系统提示词（支持多行文本）
```
                                                                                                                                                                                                                                                                        