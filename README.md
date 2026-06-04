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
  - `POST /v1/chat/completions`：转发非流式和流式聊天请求。
- 内置 provider：
  - `DeepSeek`：默认读取 `DEEPSEEK_API_KEY`，Base URL 为 `https://api.deepseek.com`。
  - `OpenAI`：默认读取 `OPENAI_API_KEY`，Base URL 为 `https://api.openai.com`。
- 支持多个同类型 provider，通过不同 `Name` 区分。
- 自带浏览器测试页：启动后访问 `http://localhost:11434/`。
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

如果你的国内模型服务兼容 OpenAI API，可以把它配置到 `Providers:OpenAI`，并给它一个更明确的 provider 名称。例如：

```json
{
  "Providers": {
    "OpenAI": [
      {
        "Name": "aliyun",
        "BaseUrl": "https://dashscope.aliyuncs.com/compatible-mode",
        "ApiKey": "你的 API Key"
      },
      {
        "Name": "openai",
        "BaseUrl": "https://api.openai.com",
        "ApiKey": ""
      }
    ],
    "DeepSeek": [
      {
        "Name": "deepseek",
        "BaseUrl": "https://api.deepseek.com",
        "ApiKey": ""
      }
    ]
  }
}
```

配置后，客户端看到的模型名会带 provider 前缀，例如：

```text
deepseek/deepseek-chat
deepseek/deepseek-reasoner
aliyun/qwen-plus
aliyun/qwen-max
```

请求 `/v1/chat/completions` 时也必须使用这种 `provider/model` 格式，本项目会在转发给上游前自动去掉 provider 前缀。

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
- DeepSeek provider 当前不支持图片输入；如果 Copilot 发送多模态图片消息，代理会返回不支持图片的错误。
- Copilot 使用工具调用、流式响应或模型详情探测时，本项目会尽量透传 OpenAI 兼容请求，但最终能力仍取决于上游模型厂商。

## 常用环境变量

| 变量 | 说明 | 默认值 |
| --- | --- | --- |
| `PORT` | 本地监听端口 | `11434` |
| `DEEPSEEK_API_KEY` | DeepSeek API Key | 空 |
| `OPENAI_API_KEY` | OpenAI 或兼容服务 API Key | 空 |

## 请求示例

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
