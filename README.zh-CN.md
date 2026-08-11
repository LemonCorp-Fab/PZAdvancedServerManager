# PZ Advanced Server Manager

[English](README.md) · [Français](README.fr.md) · [Español](README.es.md) · [Deutsch](README.de.md) · [Português (Brasil)](README.pt-BR.md) · [简体中文](README.zh-CN.md)

PZ Advanced Server Manager（PZASM）是 Project Zomboid 及其专用服务器的本地管理工具。它把一组版本一致的模组通过**唯一的 Workshop ID**发布，使服务器同步整个模组包，而不是分别同步每个来源条目。

> 状态：Windows 与 Linux 版本均已可用。Bundle、固定快照、内置 Workshop 目录、SteamCMD、独立或协调计划任务、连接提示、服务器管理和无界面 CLI 均已实现。首次实际发布应始终使用私有条目测试。

## 技术结论

一个 Workshop 条目可以在 `mods/` 下包含多个目录，每个目录都有自己的 `mod.info` 和 `id=`：

```ini
WorkshopItems=唯一模组包ID
Mods=ModIdA;ModIdB;ModIdC;PZASM_Notice_SUFFIX
```

服务器和客户端只比较这个全局 Workshop 条目的版本，之后再通过内部 Mod ID 加载内容。Project Zomboid 原有的 Lua 与校验和检查仍然有效。

推荐使用 **Bundle** 模式，它保留原始目录和 Mod ID。**Strict Fusion** 会生成单一 Mod ID，但遇到内容不同的同路径文件时会拒绝构建。

请阅读完整的[架构与可行性研究](docs/ARCHITECTURE.zh-CN.md)。

## 主要功能

- 检测游戏、专用服务器、Steam 库、SteamCMD 以及本地和 Workshop 模组；
- 支持 Build 41/42 目录结构和兼容版本目录；
- 可重新打开的独立项目，每个项目拥有自己的 GUID 和 Workshop ID；
- 默认使用轻量 `.pzasm-pack` 导出/导入（配置、ID、权限、顺序、资源、发布状态和自动化，导入后需下载源文件），并提供显式的完整去重模式以携带字节完全一致的固定源文件和构建；
- 使用 AES-256-GCM 加密的 `.pzasm-servers` 远程连接导出/导入，包含 API/RCON 机密和可选 SSH 私钥，并在目标端使用本地密钥重新加密，冲突替换必须明确确认；
- 通过私有 SHA-256 快照精确固定来源版本；
- 按 Workshop ID 导入，并加入可用的 `require=` 依赖；
- 内置 Workshop 目录，支持搜索、排序、标签、预览、分页、直接输入 ID，以及跨页面保留并可逐项移除的选择清单；
- 模组包与本地/专用服务器的 `WorkshopItems`、`Mods` 共用可视化选择器，同时保留原始编辑；
- 在 Windows 和 Linux 上首次需要时自动从 Valve 安装托管便携式 SteamCMD，也可通过界面或 `pzasm steamcmd install` 提前准备；
- 匿名下载公开的 Workshop 来源内容，并与发布所用的认证账号分离；
- Bundle 不重写 manifest、Lua、脚本、地图或资源；
- Strict Fusion 对相同文件去重，并报告冲突；
- 完整生成 Workshop 描述、公开清单和锁定文件；
- 记录作者、许可证、授权和不会公开的私有证明；
- 授权状态和警告仅供参考，绝不会阻止构建、发布或自动化；管理员保留完整控制权并承担相应责任；
- 默认启用且可关闭的多语言连接提示窗口，完整显示模组列表、声明版本、PZ 配置和固定修订；
- 创建 Workshop 条目并持续更新同一条目；
- 现代化响应式项目工作区，采用更清晰的分组、默认折叠的权限卡片、持久保存的六语言选择，以及明暗主题（默认浅色）；
- Workshop 导入显示详细进度，包括当前项目、阶段、数量、百分比、分析结果和可恢复错误；
- 地图优先级助手会分析 `map.info`、`lots=` 依赖和 `.lotheader` 单元格冲突，并支持拖放与原始 `Map=` 编辑；
- 服务器引导编辑器覆盖身份、访问、RCON、会话、备份和内容，同时保留完整 INI 原始编辑器；本地启动时会读取 SQLite `whitelist` 表，仅在确实缺少 `admin` 账户时要求输入初始密码；
- 通过 `zombie.network.GameServer` 和 `-servername` 动态重新发现服务器，即使服务器早于管理器启动也能识别；`-coop` 进程与本地专用服务器会分别显示，单独运行的图形客户端不会被误判，同一配置的重复实例会标记为冲突。分页式服务器视图提供清晰的 `server-console.txt` 或 `coop-console.txt` 日志、搜索与严重级别筛选、容量受限且已脱敏的 stdout/stderr、网络、RCON 以及命令/响应控制台；
- 发布、SteamCMD 登录和模组更新均提供详细且可取消的进度、实时输出和超时保护；
- Windows/Linux 本地 UI 与无界面 CLI；
- 带跨进程锁的 `automation run` 守护进程。

有关归档内容、加密、原子替换、磁盘去重、清理、限制和 CLI 用法，请参阅[便携传输](docs/PORTABLE-TRANSFERS.md)。

### 项目命令与更新流程

构建、更新模组和发布会作为项目的主要命令突出显示。敏感操作始终使用界面内的确认弹窗，不使用浏览器原生对话框。若源模组的 `mod.info` 提供作者信息，作者和权利持有人字段会自动预填，同时仍可编辑。每个模组都可排除在全局更新之外，也可单独更新；被排除模组的快照会保持固定，直到管理员明确执行单独更新。

## 启动

从源码构建需要 [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)。CI 生成的自包含文件无需预装 .NET 运行时。

```powershell
Start-PZASM.cmd
```

```bash
chmod +x Start-PZASM.sh
./Start-PZASM.sh
```

UI 默认仅监听本机的 `http://127.0.0.1:5160`。使用 `--data-root <路径>` 可让 UI 与 CLI 共用指定数据目录。
首次需要时，SteamCMD 会自动下载到管理器目录，经过安全解压并完成初始化。也可以从仪表板、“分发”标签或 CLI 立即准备或重新安装。公开的 Project Zomboid 来源默认匿名下载；只有发布操作需要发布者账号。

SteamCMD 可以下载已知 Workshop ID，但不提供完整搜索。内置目录会枚举 Steam Community 的公开结果、获取公开元数据，再把所选条目交给 SteamCMD。定时发布不要求游戏服务器位于本机；RCON 协调是可选功能。

## 推荐流程

1. 创建项目并保留 **Bundle** 模式。
2. 添加检测到的模组或导入 Workshop ID。
3. 为每个来源记录作者和授权信息。
4. 检查模组与地图顺序。
5. 本地构建并检查 `pack.lock.json` 和 `server-config.txt`。
6. 让管理器自动准备 SteamCMD（或在“分发”中立即准备），配置发布者账号，先执行**连接 / 更新会话**，再以私有可见性发布。
7. 在投入生产前使用测试服务器验证。

## 无界面 CLI

```bash
dotnet run --project src/PZAdvancedServerManager.Cli -- scan
dotnet run --project src/PZAdvancedServerManager.Cli -- steamcmd install
dotnet run --project src/PZAdvancedServerManager.Cli -- steamcmd login --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project create --name "主服务器"
dotnet run --project src/PZAdvancedServerManager.Cli -- project import-workshop --id <guid> --workshop-id 1234567890
dotnet run --project src/PZAdvancedServerManager.Cli -- project validate --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project build --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project publish --id <guid> --yes
dotnet run --project src/PZAdvancedServerManager.Cli -- automation run --interval 30
```

每个项目都是独立的全局模组包。管理员未明确启用自动化前，不会自动更新。`deploy/systemd/` 中提供了 systemd 服务示例。

## Docker、Coolify 与安全访问

生产容器包含 Web 管理器、计划任务、SSH 客户端、SteamCMD 所需的 Linux 32 位库以及自动安装功能。所有管理页面都必须登录。管理员可管理用户并撤销会话；操作员可管理模组包和服务器，但不能管理账户。

在 Windows 上，`just docker-secret-setup` 会使用 DPAPI 在仓库外保护初始密码和独立的数据密钥；RCON 密码和 API 令牌会使用 AES-GCM 加密存储。在 Linux 上，请使用权限模式为 `600` 的 `.env` 文件或外部密钥管理器。在 Coolify 中，请把 `PZASM_ADMIN_PASSWORD` 和至少含 32 个随机字符且保持稳定的 `PZASM_DATA_ENCRYPTION_KEY` 配置为受保护变量；Compose 会将它们挂载为只读密钥文件。请通过 HTTPS 发布端口 `5160`，并始终保留 `pzasm-data` 卷。详见 [Docker 与 Coolify](docs/DOCKER-COOLIFY.md)。

## SteamCMD 与远程服务器

Pine Hosting 现已作为独立 API 后端支持。只需 API 密钥和服务器 ID，即可在无需 SSH 的情况下复用统一的 INI、SandboxVars 与 Lua 编辑器、整合包部署、控制台、进程控制和供应商备份。恢复和全新开档要求服务器处于停止状态，并默认提供事前安全备份。详见 [Pine Hosting provider](docs/PINE-HOSTING.md)。

Steam 密码和 Steam Guard 验证码只在当前请求中通过标准输入发送给 SteamCMD；PZASM 不会把它们放入命令行，也不会保存。SteamCMD 会在便携目录中保存自己的令牌供计划任务复用。如果会话过期或缺少验证信息，发布会立即停止并给出明确说明，不会在隐藏提示上无限等待。界面会实时显示输出，并可取消外部进程。

远程配置可以仅使用 RCON：无需 SSH 即可获得已验证状态、命令控制台、`save`、`quit` 和发布协调。若 systemd、Docker、管理面板或托管服务会在 `quit` 后重新启动 Project Zomboid，PZASM 会先完成发布，再通过 RCON 请求正常重启。SSH 仅在需要读写远程 INI 或显式启动游戏进程时使用。PZASM 永远不会重启整个 VPS 或独立服务器。

## 权利与责任

PZASM 不授予任何所包含模组的权利。[Project Zomboid 官方模组政策](https://projectzomboid.com/blog/modding-policy/)要求公开或不公开列出的模组包取得适当许可并列出完整来源。Steam 还要求接受其 [Workshop 法律协议](https://steamcommunity.com/workshop/workshopsubmitinfo/)。

模组包创建者和发布者对授权、许可证、署名和第三方内容承担全部责任。LemonCorp 与 PZASM 贡献者不对用户构建或发布的模组包负责。

## 开发

仓库提供跨平台 `Justfile`。安装 [just](https://github.com/casey/just) 后可使用：

```text
just                 # 列出全部任务
just check           # 检查格式、执行 Release 构建和测试
just build           # 构建整个解决方案
just test            # 运行全部测试
just run-ui           # 启动 UI 并打开浏览器
just run-cli help     # 执行 CLI 命令
just automation      # 启动无界面计划任务
just publish          # 为当前系统发布
just publish-all      # 发布 win-x64 与 linux-x64
```

可通过 `CONFIGURATION` 和 `PUBLISH_DIR` 环境变量覆盖默认的 `Release` 配置与 `publish` 输出目录。相关任务也支持传递额外参数。

```powershell
dotnet restore
dotnet test PZAdvancedServerManager.sln
dotnet publish src/PZAdvancedServerManager.App -c Release -o publish
```

不要将 PZASM 端口暴露到互联网。该界面是本地管理工具，不提供网络身份验证。
