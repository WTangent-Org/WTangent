# wtangent

`wtangent` 启动器（空壳）：self-contained 单 exe（含 .NET 运行时），不内置功能——组件（dll）按需下载，由空壳进程加载（共享运行时）。

## 安装

Windows（PowerShell）：

```powershell
irm https://raw.githubusercontent.com/WTangent-Org/WTangent/main/install.ps1 | iex
```

Linux / macOS：

```bash
curl -fsSL https://raw.githubusercontent.com/WTangent-Org/WTangent/main/install.sh | bash
```

## 用法

`wtangent --help`（install / remove / upgrade / update / dev / serve / git…）

- `wtangent install serve` → 装 serve 组件（命令自动注册，wsl 式扩展）
- `wtangent`（顶级）→ 按优先级启动带 Default 的客户端（有桌面 gui 优先，无桌面 tui 优先）
- 可装组件清单来自 GitHub `components.json` 索引（apt 模式），`wtangent update` 刷新；已装状态纯本地（`components\` 目录扫描 + 安装时写入的 `.installed` 元数据：来源仓库+版本），不依赖索引
- `wtangent dev restore|build|install` → 组件开发工具：第三方开发者 clone 单仓、写 `agent-component.json`，一条命令拉齐 Core/生成器/依赖（GitHub release 直拉，免 nuget 免工作区）

## 架构

| 仓库 | 组件 | 说明 |
|---|---|---|
| **本仓（WTangent）** | 空壳 | 启动器 + 组件管理 + dev 工具 |
| [WTangent.Server](https://github.com/WTangent-Org/WTangent.Server) | serve | 服务端（会话 API / git 仓库 / Web UI） |
| [WTangent.Tui](https://github.com/WTangent-Org/WTangent.Tui) | tui | 终端客户端（Terminal.Gui） |
| [WTangent.Client](https://github.com/WTangent-Org/WTangent.Client) | client | 客户端命令（remote/run/web） |
| [WTangent.GitCmd](https://github.com/WTangent-Org/WTangent.GitCmd) | git | git 命令集成 |
| [WTangent.Components](https://github.com/WTangent-Org/WTangent.Components) | — | Core 契约 + 源生成器（所有仓源码引用；release 挂 dll 资产供 dev restore 直拉） |

组件为 framework-dependent dll（GitHub release zip 发布），空壳自带运行时 → 组件共享运行时，不重复打包。架构细节（加载模型/minCore 门禁/depends）见 [WTangent.Server/AGENTS.md](https://github.com/WTangent-Org/WTangent.Server/blob/main/AGENTS.md)。
