# OllamaAiProxy

OllamaAiProxy 是一个轻量级 ASP.NET Core 代理服务，用来把国内外大模型厂商的 OpenAI 兼容接口转换成 Ollama 风格的本地接口，同时保留 `/v1/chat/completions` 这样的 OpenAI 兼容聊天接口。

项目默认监听 `http://localhost:11434`，这个端口也是 Ollama 的默认端口。因此，当某些 IDE、插件或工具只会发现本机 Ollama 服务时，可以用本项目把 DeepSeek 等国内大模型“伪装”为本地 Ollama 模型列表，让客户端能够选择并调用这些模型。

## 可以解决的问题

- Visual Studio 2026 中 GitHub Copilot 添加国内大模型时，如果 Copilot 只能识别本地 Ollama 或 OpenAI 兼容模型端点，可以通过本项目提供一个本地代理入口。
- 将 DeepSeek、兼容 OpenAI API 的国内模型网关，统一暴露为 `provider/model` 格式的模型名，例如 `deepseek/deepseek-chat`。
- 为 Copilot、聊天测试工具、AI 插件等客户端提供模型发现、模型详情和聊天补全接口。
- 避免客户端直接保存国内模型厂商 API Key，统一由本地代理读取环境变量或配置文件。

## 当前功能

- Ollama 兼容接口：
  - `GET /api/tags`：返回可用模型列表。
  - `POST /api/show`：返回指定模型详情。
- OpenAI 兼容接口：
  - `GET /v1/models`：返回可用模型列表（OpenAI 兼容格式）。
  - `GET /v1/models/{provider}/{model}`：查询单个模型详情。
  - `POST /v1/chat/completions`：转发非流式和流式聊天请求。
  - `POST /v1/responses`：Responses API 兼容接口，转发到上游 `/v1/responses`，仅把 `model` 从 `provider/model` 重写为上游模型名；勾选了「图片中继」的模型会先把 `input_image` 块经视觉中继转成文字再转发。支持非流式和流式（按字节透传，保留上游 SSE 帧）。
  - `GET /v1/responses/{id}`：查询缓存的非流式 Responses 响应（内存存储，重启后清空，最多保留 200 条；流式响应不做缓存）。
- 内置 provider：
  - `DeepSeek`：默认读取 `DEEPSEEK_API_KEY`，Base URL 为 `https://api.deepseek.com`。
  - `OpenAI`：默认读取 `OPENAI_API_KEY`，Base URL 为 `https://api.openai.com`。
  - `VolcengineCodingPlan`：火山方舟（VolcengineCodingPlan）编程场景入口，默认读取 `VOLCENGINE_CODING_PLAN_API_KEY`，Base URL 为 `https://ark.cn-beijing.volces.com/api/coding/v3`。
- 支持多个同类型 provider，通过不同 `Name` 区分。
- **每个 provider 可配置多个 ApiKey，429 限流时自动轮换到下一个可用 Key。**
- 自带浏览器测试页：启动后访问 `http://localhost:11434/`。
- **`/v1/responses` 说明**：响应原样透传上游（仅重写模型名），能力完全取决于上游是否支持 Responses 接口（如内置工具、`previous_response_id` 多轮续接等）；上游错误原样返回。请求侧支持图片视觉中继（勾选了「图片中继」的模型会把 `input_image` 转成文字再转发）。模型名必须使用 `provider/model` 格式。
- **图片视觉中继**：可按模型显式启用——勾选后纯文本模型收到图片会先用视觉模型转成文字（OCR + 画面描述）再转发，详见下文「图片视觉中继」一节。
- **思考强度档位**：可按模型单独设置思考强度默认档位（关闭/低/中/高），客户端未显式指定时代理自动注入 `reasoning_effort`（chat）或 `reasoning.effort`（responses），详见下文「思考强度档位」一节。
- 可选请求日志：通过 `RequestLogging` 配置启用。

## 运行要求

- .NET 10 SDK
- 至少一个可用的大模型 API Key

## 快速开始

1. 配置 API Key。

   推荐使用环境变量，避免把密钥写入仓库：

   ```powershell
   $env:DEEPSEEK_API_KEY="你的 DeepSeek API Key"
   ```

   如果使用 OpenAI 或兼容 OpenAI 的国内网关：

   ```powershell
   $env:OPENAI_API_KEY="你的 API Key"
   ```

   > 也可以在 `appsettings.json` 的 `ApiKeys` 数组中配置多个 Key（见下文配置说明）。

2. 启动服务。

   ```powershell
   dotnet run --project .\OllamaAiProxy\OllamaAiProxy.csproj
   ```

3. 打开测试页。

   ```text
   http://localhost:11434/
   ```

4. 检查模型列表。

   ```powershell
   curl http://localhost:11434/api/tags
   ```

## 配置国内 OpenAI 兼容网关

如果你的国内模型服务兼容 OpenAI API，可以把它配置到 `Providers:OpenAI`，并给它一个更明确的 provider 名称。每个 provider 可以配置多个 ApiKey，当一个 Key 触发 HTTP 429 限流时会自动轮换到下一个可用 Key。
例如：

```json
{
  "Providers": {
    "OpenAI": [
      {
        "Name": "aliyun",
        "BaseUrl": "https://dashscope.aliyuncs.com/compatible-mode",
        "ApiKeys": [ "你的 API Key" ]
      },
      {
        "Name": "openai",
        "BaseUrl": "https://api.openai.com",
        "ApiKeys": [ "sk-xxx", "sk-yyy" ]
      }
    ],
    "DeepSeek": [
      {
        "Name": "deepseek",
        "BaseUrl": "https://api.deepseek.com",
        "ApiKeys": [
          "你的第一个 DeepSeek Key",
          "你的第二个 DeepSeek Key"
        ]
      }
    ]
  }
}
```

> 当 `ApiKeys` 数组为空时，会回退到读取对应名称的环境变量（`DEEPSEEK_API_KEY` / `OPENAI_API_KEY`）作为单个 Key，保持与旧版本兼容。

配置后，客户端看到的模型名会带 provider 前缀，例如：

```text
deepseek/deepseek-chat
deepseek/deepseek-reasoner
aliyun/qwen-plus
aliyun/qwen-max
```

请求 `/v1/chat/completions` 时也必须使用这种 `provider/model` 格式，本项目会在转发给上游前自动去掉 provider 前缀。

## 火山方舟（VolcengineCodingPlan）provider

火山方舟 Coding Plan 提供兼容 OpenAI 的聊天补全接口，Base URL 必须使用 `https://ark.cn-beijing.volces.com/api/coding/v3`（请勿使用 `/api/v3`，否则不消耗套餐额度并会产生额外费用）。模型列表固定为 Coding Plan 支持的模型（来源：方舟 Coding Plan 文档），不再从 `/models` 接口拉取，避免误用非套餐模型。聊天补全走 `POST /chat/completions`，`model` 字段直接使用下列 Model Name。

内置模型（共 10 个，均支持函数调用与深度思考；模型名前缀为 `VolcengineCodingPlan/`，例如 `VolcengineCodingPlan/doubao-seed-2.1-turbo`）：

```text
ark-code-latest           控制台路由（上下文/输出随所选模型而定）
doubao-seed-2.1-turbo     上下文 256K / 输出 64K / 视觉
doubao-seed-evolving      上下文 1M / 输出 256K / 视觉
doubao-seed-2.0-lite      上下文 256K / 输出 128K / 视觉
minimax-m3                上下文 1M / 输出 128K / 视觉
kimi-k2.7-code            上下文 256K / 输出 32K / 视觉
glm-5.3                   上下文 1M / 输出 128K
glm-5.3-flash             上下文 1M / 输出 128K / 视觉
deepseek-v4-flash         上下文 1M / 输出 384K
deepseek-v4-pro           上下文 1M / 输出 384K
```

配置示例：

```json
{
  "Providers": {
    "VolcengineCodingPlan": [
      {
        "Name": "VolcengineCodingPlan",
        "BaseUrl": "https://ark.cn-beijing.volces.com/api/coding/v3",
        "ApiKeys": [ "ark-你的ApiKey" ]
      }
    ]
  }
}
```

客户端请求 `/v1/chat/completions` 时使用 `VolcengineCodingPlan/<Model Name>` 格式，代理会去掉前缀后转发给上游。

## 图片视觉中继（让纯文本模型“看图”）

`deepseek-v4-pro`、`deepseek-v4-flash`、`glm-5.3` 等纯文本模型无法处理图片，直接收到 `image_url` 会报错并中断会话。图片视觉中继会先用一个支持视觉的模型把图片描述成文字（OCR 文字 + 画面描述），再把消息里的 `image_url` 块替换成文本块转发给纯文本模型，让它也能“看图”回答。

**中继按模型显式启用（opt-in）**：只有在模型详情里勾选了「图片中继」的模型才会走中继。视觉模型（capabilities 含 `vision`）收到图片直接原生放行；纯文本模型未勾选时不拦截，图片请求原样转发给上游（上游可能因 `image_url` 自行报错）。默认情况下纯文本模型不会自动中继。

工作流程：

```text
客户端发送图片 -> 代理检测到 image_url
  -> 该模型勾选了「图片中继」？是 -> 调用视觉模型识图 -> 用文字描述替换 image_url -> 转发
                                 否 -> 原样转发给上游（视觉模型原生处理；纯文本模型上游可能报错）
```

**任务感知描述（聚焦提示）**：识图前中继会先提取当轮意图作为聚焦提示--优先取图片所在消息里的文字，没有则取最近一条**用户**消息的文字（system/assistant 不参与，避免历史回答污染意图；截断到 500 字符），追加到视觉提示词末尾。这样画面描述会围绕当前问题展开，而不是泛泛 OCR；未取到任何用户文字时退化为默认提示词，行为不变。`/v1/chat/completions` 取消息文字，`/v1/responses` 取 `input_text` 文字。

作用于 `POST /v1/chat/completions` 与 `POST /v1/responses`：前者替换 `messages` 里的 `image_url` 块，后者替换 `input` 里的 `input_image` 块，均换成文字描述。中继用的视觉模型通过本代理已有的 provider 体系调用，复用 ApiKey 轮换与 429 重试。

### 按模型启用

中继需要在**每个模型**上单独开启，方式有两种：

- 测试页：选中模型 ->「编辑」-> 勾选「图片中继」->「保存」。勾选时会自动把 `vision` 能力一并勾上（仅作标记，不参与中继判断）。
- 直接调用覆盖接口：`PUT /api/model-overrides/{provider}/{model}`，请求体里设 `"imageRelay": true`。

覆盖值持久化到 `model-overrides.json`，重启后保留。全局还需在 `appsettings.json` 配置 `ImageVisionRelay:VisionModel`（见下），否则勾选了中继也会因未配置视觉模型而报错。

### 配置

在 `appsettings.json` 中配置 `ImageVisionRelay`：

```json
{
  "ImageVisionRelay": {
    "Enabled": true,
    "VisionModel": "VolcengineCodingPlan/doubao-seed-2.0-lite"
  }
}
```

| 字段 | 说明 | 默认值 |
| --- | --- | --- |
| `Enabled` | 是否启用中继。`true` 时若未配置 `VisionModel` 仍不生效（回退到拒绝图片）。 | `true` |
| `VisionModel` | 用于识图的视觉模型，使用 `provider/model` 格式，例如 `VolcengineCodingPlan/doubao-seed-2.0-lite`、`VolcengineCodingPlan/doubao-seed-2.1-turbo`、`OpenAI/gpt-4o`。留空则中继关闭。 | 空 |

> `VisionModel` 必须是一个 capabilities 含 `vision` 的模型。火山方舟 Coding Plan 下的 `doubao-seed-2.1-turbo`、`doubao-seed-2.0-lite`、`doubao-seed-evolving`、`glm-5.3-flash`、`kimi-k2.7-code` 等均支持视觉；`glm-5.3`、`deepseek-v4-*` 是纯文本模型，不能用作 `VisionModel`。
>
> 识图调用始终走非流式，即使最终请求是流式。若识图失败（视觉模型不可用、Key 无效等），对应图片会被替换为 `(recognition failed)` 占位文本，请求仍会转发，避免会话中断。识图结果在内存中缓存 30 分钟（最多 200 条），同一图片在多轮对话里不会重复识图；缓存键包含聚焦提示，不同意图会分别缓存，失败结果不缓存以便下次重试。单张图片识图有 60 秒超时，对 5xx 和超时会自动重试（最多 2 次、间隔递增）；4xx 等不可重试错误立即放弃。远程 `http(s)` 图片会先由代理主动拉取并转成 data URL 再交给视觉模型（15 秒超时、10MB 上限），这样内网/localhost 等上游不可达的地址也能识图；拉取失败或过大则回退原地址交给上游处理。

## 思考强度档位（按模型默认值）

可为每个模型单独设置「思考强度」档位，作为该模型的默认值。代理转发该模型的 `/v1/chat/completions` 或 `/v1/responses` 请求时，如果客户端**没有显式指定**思考参数，就自动注入对应档位；客户端显式指定时以客户端为准。

### 档位

| 档位 | 取值 | 说明 |
| --- | --- | --- |
| 未设置 | （空） | 不注入，透传上游默认行为 |
| 关闭思考 | `none` | 禁用思考（仅对支持该取值的上游有效，如 DeepSeek） |
| 低 | `low` | 思考强度低 |
| 中 | `medium` | 思考强度中 |
| 高 | `high` | 思考强度高 |

档位词表与 [DeepSeek 思考模式文档](https://api-docs.deepseek.com/zh-cn/guides/thinking_mode/) 的 `reasoning_effort` 取值一致。

### 注入格式

- `/v1/chat/completions`：顶层 `"reasoning_effort": "low" | "medium" | "high" | "none"`。
- `/v1/responses`：`"reasoning": { "effort": "low" | "medium" | "high" | "none" }`。

> `none`（关闭思考）对 OpenAI o 系等不接受该取值的上游可能报错，请按模型实际支持情况设置。最终能力取决于上游模型厂商，与项目「尽量透传」的既有哲学一致。

### 按模型设置

- 测试页：选中模型 ->「编辑」->「思考强度」下拉选择档位 ->「保存」。
- 直接调用覆盖接口：`PUT /api/model-overrides/{provider}/{model}`，请求体里设 `"thinkingStrength": "high"`（不设或设 `null` 表示清除）。

设置持久化到 `model-overrides.json`，重启后保留。未设置档位的模型请求行为与原来完全一致。

## Visual Studio 2026 + GitHub Copilot 接入国内大模型

本项目的核心用途之一，是给 Visual Studio 2026 中的 GitHub Copilot 提供一个本地模型代理，让 Copilot 可以通过本机 Ollama/OpenAI 兼容入口发现并调用国内大模型。

推荐流程：

1. 先启动 OllamaAiProxy，并保持控制台窗口运行。

   ```powershell
   $env:DEEPSEEK_API_KEY="你的 DeepSeek API Key"
   dotnet run --project .\OllamaAiProxy\OllamaAiProxy.csproj
   ```

2. 在浏览器确认代理可用：

   ```text
   http://localhost:11434/api/tags
   ```

3. 打开 Visual Studio 2026 的 GitHub Copilot 模型设置。

4. 如果 Copilot 提供 Ollama、本地模型或自定义 OpenAI 兼容端点选项，填写：

   ```text
   Base URL: http://localhost:11434
   OpenAI-compatible Base URL: http://localhost:11434/v1
   API Key: 任意非空字符串，或按 Copilot 设置要求填写
   ```

5. 在模型列表中选择带 provider 前缀的模型，例如：

   ```text
   deepseek/deepseek-chat
   deepseek/deepseek-reasoner
   ```

6. 在 Copilot Chat 中发送一条简单消息验证，例如“用中文介绍一下当前项目”。

注意事项：

- 如果 Visual Studio 2026 的 Copilot 只扫描 Ollama 默认地址，请确保本服务监听 `11434` 端口。
- 如果本机已经运行 Ollama，可以先关闭 Ollama，或通过 `PORT` 修改本项目端口后，在 Copilot 中填写对应地址。
- 纯文本模型（如 DeepSeek 系列、`glm-5.3`）默认不支持图片输入；需要在模型详情里勾选「图片中继」并配置 `ImageVisionRelay:VisionModel` 后，代理才会先把图片转成文字再转发，否则图片请求会原样转发给上游（上游可能因 `image_url` 报错）。
- Copilot 使用工具调用、流式响应或模型详情探测时，本项目会尽量透传 OpenAI 兼容请求，但最终能力仍取决于上游模型厂商。

## 常用环境变量

| 变量 | 说明 | 默认值 |
| --- | --- | --- |
| `PORT` | 本地监听端口 | `11434` |
| `DEEPSEEK_API_KEY` | DeepSeek API Key（仅当配置文件中的 `ApiKeys` 为空时生效） | 空 |
| `OPENAI_API_KEY` | OpenAI 或兼容服务 API Key（仅当配置文件中的 `ApiKeys` 为空时生效） | 空 |
| `VOLCENGINE_CODING_PLAN_API_KEY` | 火山方舟 VolcengineCodingPlan API Key（仅当配置文件中的 `ApiKeys` 为空时生效） | 空 |

## 请求示例

查看模型列表：

`powershell
curl http://localhost:11434/v1/models
``r

聊天补全：

```powershell
curl http://localhost:11434/v1/chat/completions `
  -H "Content-Type: application/json" `
  -d '{
    "model": "deepseek/deepseek-chat",
    "messages": [
      { "role": "user", "content": "你好，用一句话介绍你自己。" }
    ],
    "stream": false
  }'
```

查看模型详情：

```powershell
curl http://localhost:11434/api/show `
  -H "Content-Type: application/json" `
  -d '{ "model": "deepseek/deepseek-chat" }'
```

## 日志

默认不记录请求日志。如需排查 Copilot 或其他客户端请求，可以在 `appsettings.json` 中开启：

```json
{
  "RequestLogging": {
    "Enabled": true,
    "Directory": "logs"
  }
}
```

日志可能包含提示词、响应内容或敏感信息，排查完成后建议关闭。

## 自动更新

默认开启：每次启动时，在服务开始监听前，程序会先检查 GitHub `GHYHC/OllamaAiProxy` 的最新**稳定版** Release。如果比当前版本新，会自动下载匹配当前系统的安装包、校验 SHA256、解压暂存，然后替换可执行文件并自动重启到新版本。

- 支持全部 6 个目标平台：`win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`（按运行时的 OS + 架构自动选择，也可用 `PlatformRid` 覆盖）。
- 更新时**不会覆盖**用户数据：`appsettings.json`、`model-overrides.json`、`logs/` 目录都会保留。
- 校验不通过、下载失败、网络不可达、找不到对应平台的安装包等情况，都会记日志并**继续用当前版本正常启动**，不会中断服务。
- 通过 `dotnet run`（非单文件发布）运行时自动跳过自更新。
- 更新重启会导致数秒服务中断，这是"重启时更新"的固有特性。

配置（`appsettings.json` 的 `AutoUpdate` 节）：

```json
{
  "AutoUpdate": {
    "Enabled": true,
    "Repository": "GHYHC/OllamaAiProxy",
    "CheckTimeoutSeconds": 10,
    "RestartAfterUpdate": true,
    "ApiBaseUrl": "https://api.github.com",
    "PlatformRid": ""
  }
}
```

| 字段 | 说明 | 默认值 |
| --- | --- | --- |
| `Enabled` | 是否在启动时检查并自动更新。设为 `false` 完全关闭。 | `true` |
| `Repository` | GitHub 仓库，格式 `owner/repo`。 | `GHYHC/OllamaAiProxy` |
| `CheckTimeoutSeconds` | 查询最新版本的超时秒数（下载安装包不受此限制）。 | `10` |
| `RestartAfterUpdate` | 更新后是否自动重启到新版本。**用 systemd 等进程管理器托管时建议设为 `false`**，避免脚本内的后台重启与管理器拉起产生双实例。 | `true` |
| `ApiBaseUrl` | GitHub API 根地址。测试或自建 GitHub Enterprise 时可覆盖。 | `https://api.github.com` |
| `PlatformRid` | 平台标识覆盖，例如 `linux-arm64`。留空则自动检测。用于特殊环境或手工测试。 | 空 |

> 平台与安装包对应关系（与 GitHub Release 附件命名一致）：Windows x64/ARM64 → `.zip`；Linux/macOS x64/ARM64 → `.tar.gz`。
>
> Windows 上执行自动更新时会短暂弹出控制台窗口（用于替换可执行文件并重启），属一次性正常现象。

**回滚**：更新成功后会保留旧可执行文件为 `*.old` 备份，下次成功启动时自动清理。如需手动回滚，把 `*.old` 改回原名即可。

**注意**：
- macOS 下若首次运行被 Gatekeeper 拦截，可在「系统设置 → 隐私与安全性」允许，或执行 `xattr -dr com.apple.quarantine OllamaAiProxy`。
- Windows 下若杀毒软件拦截新版本可执行文件，请将程序目录加入白名单。

## ApiKey 多 Key 与 429 自动切换

每个 provider 的 `ApiKeys` 支持配置多个 API Key。当一个 Key 触发上游 HTTP 429（Rate Limit）时，代理会自动将该 Key 标记为不可用，并切换到下一个可用 Key 重试请求。所有 Key 都被标记后，最后一个 429 响应会被返回给客户端。

### 配置示例：多个 DeepSeek Key

```json
{
  "Providers": {
    "DeepSeek": [
      {
        "Name": "deepseek",
        "BaseUrl": "https://api.deepseek.com",
        "ApiKeys": [
          "sk-第一个Key",
          "sk-第二个Key",
          "sk-第三个Key"
        ]
      }
    ]
  }
}
```

### 常见问题

**Q: 配置了多个 Key，但请求还是返回 429？**
A: 所有 Key 都达到限流时会返回最终的 429。如果这种情况频繁出现，建议增加更多 Key 或降低请求频率。

**Q: 环境变量和配置文件可以混用吗？**
A: 配置文件的 `ApiKeys` 优先级高于环境变量。当 `ApiKeys` 为空数组时才会读取环境变量。
