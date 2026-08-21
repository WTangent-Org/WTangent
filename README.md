# WtAgent

`agent` 启动器（空壳）：self-contained 单 exe（含 .NET 运行时），不内置功能——组件（dll）按需下载，由空壳进程加载（共享运行时）。

## 安装

Windows（PowerShell）：

```powershell
irm https://raw.githubusercontent.com/wtommy932/WtAgent/main/install.ps1 | iex
```

Linux / macOS：

```bash
curl -fsSL https://raw.githubusercontent.com/wtommy932/WtAgent/main/install.sh | bash
```

## 用法

`wtagent --help`（install / remove / upgrade / update / serve / run / remote / git / web…）

- `wtagent install serve` → 装 serve 组件（命令自动注册，wsl 式扩展）
- `agent` → 顶级启动客户端（有桌面选 GUI，无桌面选 TUI）
- 组件元数据来自 GitHub `components.json` 索引（apt 模式），`wtagent update` 刷新

## 架构

| 仓库 | 组件 | 说明 |
|---|---|---|
| **本仓（WtAgent）** | 空壳 | 启动器 + 组件管理 |
| [WtAgent.Server](https://github.com/wtommy932/WtAgent.Server) | serve | 服务端（会话 API / git 仓库 / Web UI） |
| [WtAgent.Client](https://github.com/wtommy932/WtAgent.Client) | tui | 终端客户端 |
| [WtAgent.Components](https://github.com/wtommy932/WtAgent.Components) | — | 共享源生成器（`[AgentComponent]` → Entry 自动生成） |
| [WtAgent.Core](https://github.com/wtommy932/WtAgent.Core) | — | 共享设施（统一 HttpClient 等） |

组件为 framework-dependent dll（zip 发布），空壳自带运行时 → 组件共享，不重复打包。
