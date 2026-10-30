# AgentHub · 本地多智能体协作台

AgentHub 是一个独立的本地优先协作产品：把目标拆成有负责人和验收标准的工作项，由不同角色依次处理、独立复核，再交给用户审核。所有模型请求只访问本机 Ollama；会话以检查点形式保存在本机。

## 运行

需要 Windows 和已启动的 Ollama。运行 `start.ps1` 会优先启动随项目发布的自包含 `publish\AgentHub.exe`（约 96 MB，不需要额外安装 .NET）；源码开发需要 .NET 10 SDK；发布包可直接运行。默认使用 Ollama 中第一个已安装模型，也可以在新会话中指定模型。

```powershell
.\start.ps1
```

浏览器打开 <http://127.0.0.1:5178>。也可直接运行：

```powershell
dotnet run --project .\AgentHub.csproj -c Release
```

可选环境变量 `AGENTHUB_OLLAMA_URL` 用于指定 Ollama 地址，但程序只接受 `127.0.0.1` 或 `localhost` 上的 HTTP 服务。会话和任务分工保存在 `%LOCALAPPDATA%\AgentHub\sessions`。

## 当前工作流

1. **规划师**生成结构化工作项，指定分析师或执行专家，并为每项写验收标准。
2. **分析师**完成背景、假设和风险分析。
3. **执行专家**基于任务分工和分析结果给出方案。
4. **审查员**独立检查目标覆盖、依据、执行性和遗漏。
5. **协调员**综合并标记待解决项；整个会话暂停，等待用户通过或要求修改。

每完成一个角色就持久化阶段状态和输出。停止或意外关闭后，使用原会话从最后阶段继续；追加对话支持选择单个角色并保留该角色的对话上下文。

## 开源社区做法与取舍

- [CrewAI 官方概念文档](https://docs.crewai.com/core-concepts/Agents)把 agent role、任务分配与可控 Flow 分开；AgentHub 用显式工作项、负责人和验收标准表达分工，用固定流程控制可审计顺序。
- [LangGraph 子代理文档](https://docs.langchain.com/oss/python/langchain/multi-agent/subagents)强调子代理上下文隔离；[中断与持久化文档](https://docs.langchain.com/oss/python/langgraph/interrupts)把可恢复 checkpoint 和人工中断作为审核基础。AgentHub 为每个角色建立独立上下文，并在每个阶段保存本地检查点；人审是显式会话状态，不会被模型自动略过。
- [OpenHands 本地部署文档](https://docs.all-hands.dev/modules/usage/installation)展示了本地运行和自带模型的路径。AgentHub 采用本机 Ollama，但首版刻意不让代理直接写文件或执行 shell；这些能力需要另加工作区隔离与逐操作授权。

AgentHub 当前**借鉴上述开源项目的架构模式，没有把 LangGraph/CrewAI 运行时作为依赖**。实现保持 .NET 单服务和静态前端，避免额外 Python 环境与服务。代理是同一款本地模型下的不同角色提示和上下文，不是多份模型实例，也不会同时占用显存；串行调度面向 6GB 显存电脑。

## 边界

这是可运行的协作流程 MVP：支持开/重开会话、任务拆分、角色分工、阶段进度、角色追问、阶段恢复、独立审核和人工通过/退回。首版提供分析和方案，不声称已经修改项目，也不做网页检索。进入代码代理阶段前，还需增加 Git worktree 隔离、受限工具执行、变更 diff 审批、测试结果回传和操作审计；避免多个代理同时改同一工作区。

