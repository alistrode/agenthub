---
name: agenthub
description: 运行、调试与维护 AgentHub 本地多智能体协作台时使用；覆盖自包含包启动、健康检查、端口与 Ollama 连接排障、会话数据定位。
---

# AgentHub

## 何时使用

- 需要在本机启动、重启或停止 AgentHub（默认 http://127.0.0.1:5178）。
- AgentHub 起不来、页面打不开、模型列表为空、会话卡在 Running。
- 需要从源码构建，或重新生成自包含免安装分发包。

## 关键事实

| 项 | 值 |
|---|---|
| 监听地址 | `http://127.0.0.1:5178`（硬编码，仅回环） |
| 运行时 | Windows x64；分发包自带 .NET 10.0.12，无需安装 .NET |
| 外部依赖 | 本机 Ollama（默认 `http://127.0.0.1:11434`） |
| 环境变量 | `AGENTHUB_OLLAMA_URL`，只接受 `127.0.0.1` 或 `localhost` |
| 会话数据 | `%LOCALAPPDATA%\AgentHub\sessions\*.json` |
| 日志 | `%LOCALAPPDATA%\AgentHub\logs\server.log`、`error.log` |

## 常用命令与等效文件索引

| 用途 | 命令 | 等效代码 / 脚本 |
|---|---|---|
| 启动（自动选用分发包） | `.\scripts\start.ps1` | `scripts/start.ps1` → `scripts/launch.ps1` |
| 健康检查 | `Invoke-RestMethod http://127.0.0.1:5178/api/health` | `src/Program.cs` 的 `/api/health` 端点 |
| 列出本机 Ollama 模型 | `Invoke-RestMethod http://127.0.0.1:11434/api/tags` | `src/Program.cs` → `OllamaClient.ListModelsAsync` |
| 从源码运行 | `dotnet run --project src\AgentHub.csproj -c Release` | `src/AgentHub.csproj` |
| 重新生成自包含包 | `dotnet publish src\AgentHub.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish` | `src/AgentHub.csproj` |
| 查看会话文件 | `Get-ChildItem "$env:LOCALAPPDATA\AgentHub\sessions"` | `src/Program.cs` → `SessionStore._root` |
| 停止进程 | `Get-Process AgentHub -ErrorAction SilentlyContinue \| Stop-Process -Force` | — |

## 排障顺序

1. `launch.ps1` 先探测 `/api/health`，返回 `product=AgentHub` 就复用已运行实例，不会重复起进程。
2. 报 `AgentHub executable not found` 说明 `publish\AgentHub.exe` 缺失——解压 Release 的 `agenthub-v*-win-x64.zip` 覆盖即可。
3. 模型列表为空 → Ollama 未启动或未装模型，先跑 `ollama list` 确认。
4. 端口被占用 → 端口写在 `src/Program.cs` 的 `UseUrls` 里，改完需重新发布。

## 目录说明

- `src/` — 全部源码（`Program.cs`、`AgentHub.csproj`、`wwwroot/`、`assets/`）
- `scripts/` — 启动脚本
- `README.md` — 产品说明
- Release 附件 `agenthub-v*-win-x64.zip` — 自包含免安装分发包
