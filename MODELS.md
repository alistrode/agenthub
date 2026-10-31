# AgentHub 模型下载清单

AgentHub 不打包模型。它取 Ollama `/api/tags` 返回的**第一个**模型，所以本机装哪个就用哪个；没有模型时会提示「先在本机 Ollama 安装一个模型」。

## 1. 通用前提：本机 Ollama

所有模型都由**本机 Ollama** 提供，服务地址 `http://127.0.0.1:11434`（程序强制只接受环回地址，不允许指向远程）。

```powershell
ollama serve      # 没设成开机自启时手动起
ollama list       # 确认已安装的模型
```

Ollama 下载：<https://ollama.com/download>

## 2. 官方模型（可直接拉取）

| 模型 | 拉取命令 | 体积 | 说明 |
|---|---|---|---|
| `qwen3-vl:4b` | `ollama pull qwen3-vl:4b` | 约 2.8 GB | **推荐首选**，支持图像输入，本项目出厂默认 |
| `qwen3:1.7b` | `ollama pull qwen3:1.7b` | 约 1.4 GB | 轻量替代，纯 CPU 也能跑 |

只需要装**至少一个**，程序取 `/api/tags` 返回的第一个可用模型。

## 3. 本机原有的三个自建模型（可选，pull 不到）

`news-editor`、`news-editor-light`、`news-qwen` **不是 ollama.com 上的公开模型**，
是本地用 GGUF + 自定义参数层创建的，`ollama pull` 拉不到。要复现必须自备 GGUF 再写 Modelfile。

| 模型名 | 底座 | 模型文件 | 参数层 |
|---|---|---|---|
| `news-editor` | Qwen3 1.7B Instruct | 1749.4 MB | `num_ctx 4096`、`num_gpu 0`、`num_thread 4`、`temperature 0.3`、`use_mmap true` |
| `news-editor-light` | Qwen3 1.7B Instruct | 1056.1 MB | 同上 |
| `news-qwen` | Qwen3 0.6B Instruct | 609.8 MB | `num_ctx 4096`、`temperature 0.2` |

重建示例：

```
# Modelfile
FROM ./Qwen3-1.7B-Q8_0.gguf
PARAMETER num_ctx 4096
PARAMETER num_gpu 0
PARAMETER num_thread 4
PARAMETER temperature 0.3
PARAMETER use_mmap true
```

```powershell
ollama create news-editor -f Modelfile
```

注意 `num_gpu 0` 是刻意设定：纯 CPU、限 4 线程，不抢显存，6 GB 显存的机器也能跑。
代价是慢，且 `num_ctx 4096` 装不下长输入。

## 4. 没有模型会怎样

| 情况 | 表现 |
|---|---|
| Ollama 没启动 | 健康检查仍通过（`/api/health` 不依赖 Ollama），但生成类请求会失败并提示「本机 Ollama 未连接」 |
| Ollama 在跑但没有模型 | 提示「先在本机 Ollama 安装一个模型」 |
| 装了模型但太小 | 能跑，但输出质量、格式遵从度明显下降，长输入会被 4096 上下文截断 |

**健康接口 200 只代表服务起来了，不代表模型可用。** 界面里的状态区会单独显示 Ollama 连接与模型列表。
