# PZ Advanced Server Manager

[English](README.md) · [Français](README.fr.md) · [Español](README.es.md) · [Deutsch](README.de.md) · [Português (Brasil)](README.pt-BR.md) · [简体中文](README.zh-CN.md)

PZ Advanced Server Manager（PZASM）是 Project Zomboid 及其专用服务器的本地管理工具。它把一组版本一致的模组通过**唯一的 Workshop ID**发布，使服务器同步整个模组包，而不是分别同步每个来源条目。

> 状态：Windows 与 Linux 版本均已可用。Bundle、固定快照、多项目、Workshop 导入、SteamCMD、协调计划任务、连接提示、服务器管理和无界面 CLI 均已实现。首次实际发布应始终使用私有条目测试。

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
- 通过私有 SHA-256 快照精确固定来源版本；
- 按 Workshop ID 导入，并加入可用的 `require=` 依赖；
- Bundle 不重写 manifest、Lua、脚本、地图或资源；
- Strict Fusion 对相同文件去重，并报告冲突；
- 完整生成 Workshop 描述、公开清单和锁定文件；
- 记录作者、许可证、授权和不会公开的私有证明；
- 默认启用、可关闭的连接提示窗口；
- 创建 Workshop 条目并持续更新同一条目；
- 带备份和编码保留的服务器配置编辑器；
- 通过 RCON 执行 `save`/`quit` 并协调重启；
- Windows/Linux 本地 UI 与无界面 CLI；
- 带跨进程锁的 `automation run` 守护进程。

## 启动

从源码构建需要 [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)。CI 生成的自包含文件无需预装 .NET 运行时。

```powershell
Start-PZASM.cmd
```

```bash
chmod +x Start-PZASM.sh
./Start-PZASM.sh
```

UI 默认仅监听本机的 `http://localhost:5160`。使用 `--data-root <路径>` 可让 UI 与 CLI 共用指定数据目录。

## 推荐流程

1. 创建项目并保留 **Bundle** 模式。
2. 添加检测到的模组或导入 Workshop ID。
3. 为每个来源记录作者和授权信息。
4. 检查模组与地图顺序。
5. 本地构建并检查 `pack.lock.json` 和 `server-config.txt`。
6. 配置 SteamCMD，并先以私有可见性发布。
7. 在投入生产前使用测试服务器验证。

## 无界面 CLI

```bash
dotnet run --project src/PZAdvancedServerManager.Cli -- scan
dotnet run --project src/PZAdvancedServerManager.Cli -- project create --name "主服务器"
dotnet run --project src/PZAdvancedServerManager.Cli -- project import-workshop --id <guid> --workshop-id 1234567890
dotnet run --project src/PZAdvancedServerManager.Cli -- project validate --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project build --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project publish --id <guid> --yes
dotnet run --project src/PZAdvancedServerManager.Cli -- automation run --interval 30
```

每个项目都是独立的全局模组包。管理员未明确启用自动化前，不会自动更新。`deploy/systemd/` 中提供了 systemd 服务示例。

## 权利与责任

PZASM 不授予任何所包含模组的权利。[Project Zomboid 官方模组政策](https://projectzomboid.com/blog/modding-policy/)要求公开或不公开列出的模组包取得适当许可并列出完整来源。Steam 还要求接受其 [Workshop 法律协议](https://steamcommunity.com/workshop/workshopsubmitinfo/)。

模组包创建者和发布者对授权、许可证、署名和第三方内容承担全部责任。LemonCorp 与 PZASM 贡献者不对用户构建或发布的模组包负责。

## 开发

```powershell
dotnet restore
dotnet test PZAdvancedServerManager.sln
dotnet publish src/PZAdvancedServerManager.App -c Release -o publish
```

不要将 PZASM 端口暴露到互联网。该界面是本地管理工具，不提供网络身份验证。
