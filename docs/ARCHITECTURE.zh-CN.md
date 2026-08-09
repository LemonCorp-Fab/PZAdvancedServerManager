# 架构与可行性研究

[English](ARCHITECTURE.md) · [Français](ARCHITECTURE.fr.md) · [Español](ARCHITECTURE.es.md) · [Deutsch](ARCHITECTURE.de.md) · [Português (Brasil)](ARCHITECTURE.pt-BR.md) · [简体中文](ARCHITECTURE.zh-CN.md)

## 结论

Project Zomboid 可以从一个 Workshop 条目加载多个逻辑模组：

```text
一个 Workshop PublishedFileId
└── mods/
    ├── ModA/          → mod.info: id=ModA
    ├── ModB/          → mod.info: id=ModB
    └── PZASM_Notice/  → mod.info: id=PZASM_Notice_SUFFIX
```

游戏会看到多个 **Mod ID**，但只需同步**一个 Workshop ID**。这样可以避免多个独立来源条目之间的版本错位，同时不承担物理合并所有文件的风险。

## 客户端与服务器检查

对本地 42.20.2 版本的分析表明：客户端先比较 Workshop ID 与时间戳，然后按 Mod ID 加载 `Mods=`。同一个 Workshop 条目内的逻辑模组不会分别获得 Workshop 时间戳。

包括 `DoLuaChecksum` 在内的正常完整性检查仍然有效。Project Zomboid 重大更新后应重新验证此行为。

## 目录结构与冲突

```text
steamapps/workshop/content/108600/<WorkshopId>/
└── mods/<逻辑目录>/
    ├── mod.info
    ├── media/
    ├── common/mod.info + media/
    └── 42.x/mod.info + media/
```

`media` 可能包含 Lua、脚本、地图、纹理、模型、动画、声音、广播、翻译和界面。不同模组可能重复使用 Lua 全局名称、脚本 ID、地图单元、资源名称或翻译键。仅重命名文件路径无法修复所有内部引用。

## 打包模式

推荐使用 **Bundle**：在一个 Workshop ID 下保留原始目录与 Mod ID，从而获得最高兼容性。

高级 **Strict Fusion** 会生成 `PZASM_Pack_<suffix>`，合并有效内容，对相同文件去重，并在不同内容发生路径冲突时停止构建。它只适合受控且充分测试的模组集合。

## 项目与固定版本

每个项目都有不可变 GUID 和独立 `publishedfileid`。值为 `0` 时 SteamCMD 创建新条目，之后 PZASM 保存返回的 ID，并在后续发布中更新同一条目。

添加来源时，PZASM 创建私有快照并计算 SHA-256。构建使用固定副本，而不是可变化的 Steam 缓存。显式刷新会以原子方式替换快照。`pack.lock.json` 精确记录发布内容。

## 发布与服务器

[Steamworks Workshop 指南](https://partner.steamgames.com/doc/features/workshop/implementation)说明了如何通过 `workshop_build_item` 创建和更新条目。

发布在两个层面采用增量方式。PZASM 分别计算已交付内容、元数据和预览图的指纹，并从 VDF 中省略未更改的部分。随后 SteamCMD 和 Steam 会将提交的清单与上一个清单比较，只传输缺失的分块。上传后，PZASM 绝不会再次下载整个包。

只有三个本地指纹，以及通过公共 API 重新读取的远程内容句柄、预览句柄、文件大小、更新时间、标题、描述和可见性都与上次确认的发布一致时，才会判定“无更改”。任何证据缺失或过期都会触发保守发布。强制模式会把所有部分交给 SteamCMD，但 Steam 仍会复用相同的远程分块。仅有进程退出码 `0` 不代表成功：当前 SteamCMD 活动必须明确包含 `Upload finished ... : OK`，任何明确的 Workshop 错误都优先判定为失败。

协调服务器在构建和整个上传期间保持在线。如果已交付内容发生变化，管理器会在确认后等待已配置的延迟（最少五分钟），然后发送 `save` 和 `quit`，并执行已配置的重启策略。经过验证的无更改，或仅元数据、预览图发生变化，都不会重启服务器。

计划任务会记录权限、验证依赖、按需刷新来源、构建、发布，并在需要时通过 RCON 协调服务器。受监督的登录只通过标准输入把密码交给 SteamCMD，不会保存密码。未启用 Steam Guard 的账户会直接继续。对于受保护的账户，SteamCMD 会向 Steam 手机应用发送批准请求并自动轮询结果，界面同时显示活动等待状态。只有手机批准过期或用户主动选择备用方式时，才会请求当前验证码；随后 PZASM 通过标准输入使用 SteamCMD 文档中的 `set_steam_guard_code` 命令重试。Steam 客户端和网页支持二维码登录，但 SteamCMD 没有公开文档化的二维码数据或二维码登录命令，因此单独的网页二维码无法建立发布会话。SteamCMD 随后在便携目录中保留自己的令牌；手动和计划发布仅使用此会话。管理器仅记录上次验证成功的时间。会话过期时会要求重新连接，不会停在不可见的输入提示上。界面会实时显示进度、执行超时限制，并可取消外部进程。

SteamCMD 会打开独立的 Steam 会话，因此自动化应使用拥有 Project Zomboid 的专用发布账户，而不是桌面客户端中正在使用的账户。首次登录会创建便携令牌；之后的检查使用 `steamcmd verify`，无需密码，也不会创建新令牌。PZASM 绝不会导入 Steam 客户端的 Cookie 或登录文件。若要通过桌面会话发布，必须使用获授权的 Steamworks 应用：Project Zomboid 发行方需要把工具 AppID 加入 Workshop 的 App Publish Permissions 以使用 `ISteamUGC`；OAuth 还要求 Valve 分配客户端 ID，并授予限定到该 AppID 的 `write_cloud` 权限。外部工具无法自行获得这些权限。

## 为什么需要外部程序

游戏内模组无法可靠管理 SteamCMD、游戏未运行时的计划任务、私有文件或多个服务器配置。因此 PZASM 使用共享核心的本地 ASP.NET Core 程序和无界面 CLI。只有生成的 Lua 连接提示会在 Project Zomboid 中运行。

## 安全与权利

[官方模组政策](https://projectzomboid.com/blog/modding-policy/)会展示给管理员，最终决定及责任由管理员自行承担。授权状态、证明和已读确认仅用于记录，绝不会阻止构建、发布或自动化。未知、缺少证明或已拒绝的情况仍会清晰显示为警告；私有证明不会进入 `Contents`，公开描述始终列出所有来源。

在接受 [Workshop 法律协议](https://steamcommunity.com/workshop/workshopsubmitinfo/)之前，Steam 可能隐藏新条目。

## 剩余风险

- 协议或 Build 42 结构未来发生变化；
- 作者修改 Mod ID、依赖、地图或许可证；
- 未声明依赖与需要手动调整的地图顺序；
- 静态分析无法发现的逻辑冲突；
- SteamCMD 偶尔需要人工操作；
- 仅当已交付内容发生变化时才需要重启服务器，并且只会在确认上传成功且等待配置的延迟后执行。

## 本地与远程服务器编排

配置可以指向本地 INI 文件，也可以连接远程 VPS/独立服务器。远程配置可以仅使用 RCON；SSH 与远程 INI 管理均为可选。状态检查会执行真实的 RCON 身份验证，控制台可发送游戏支持的管理命令，正常停止依次使用 `save` 和 `quit`。

本地配置具有明确的运行模式。**本地主机（Host）**配置从游戏客户端的 Host 菜单启动，使用 `zombie.network.GameServer -coop` 进程和 `coop-console.txt`；**本地独立服务器（Dedicated）**配置通过单独的 Steam 工具 Project Zomboid Dedicated Server（AppID 380870）启动，并使用 `server-console.txt`。两种模式会有意共享原生的 `Zomboid/Server/<名称>.ini` 文件，管理器仅单独保存所选用途。仅存在 `-coop` 辅助进程并不代表 Host 服务器在线：必须检测到有效的近期启动进度或就绪标记；之后出现启动失败时会将其忽略，避免误报冲突。

如果 systemd、Docker、托管面板或其他监督程序会在 `quit` 后重启 Project Zomboid，仅 RCON 的配置也能协调发布：Workshop 上传先完成，随后管理器发送 `save` 和 `quit`。SSH 只用于可选的 INI 管理或显式的游戏启动命令。管理器会拒绝主机级别的 `reboot`、`shutdown` 和 `poweroff` 命令。为支持无人值守操作，RCON 密钥保存在管理器本地配置数据中，因此必须妥善保护该目录。

## 兼容性与冲突处理工作台

模组包编辑器和服务器部署视图共用一个带缓存的静态分析器。它会读取实际生效的 Build 42 结构（`common` 加最佳兼容版本目录）、`require`、`loadAfter`、`loadBefore`、`incompatible`、重复 Mod ID、Lua/脚本/资源虚拟路径、地图依赖以及重叠的 `.lotheader` 单元。只有在路径和文件大小相同后才会对不同文件计算哈希；完全相同的内容会记录为已解决信息。

工作台会给出稳定的拓扑模组顺序和地图顺序，展示精确证据，并允许管理员选择优先内容、确认有意保留的冲突或禁用来源。手动优先级会成为明确的顺序约束，绝不会重写第三方源文件。服务器审计还会将模组包与 `WorkshopItems`、`Mods`、`Map` 和近期运行日志错误关联起来。静态分析无法证明任意 Lua 模组一定兼容，因此仍必须进行游戏内测试。

由强依赖导致的顺序违规属于阻断问题。分析器使用强连通分量，只列出真实循环中的模组，不会把所有下游模组一并计入。如果循环仅由手动选择的冲突优先项造成，并且该选择违背 `require`、`loadAfter` 或 `loadBefore`，工作台可以一键修复：只移除已确认无效的手动约束，重新构建并验证依赖图，然后应用稳定的拓扑顺序。如果验证仍然失败，已移除的约束会自动恢复。完全由模组自身声明约束构成的循环仍需手动处理。

文件冲突还会按运行时影响分类：翻译和被动媒体为低风险，客户端界面为中等风险，共享玩法或脚本为高风险，服务器 Lua 或地图数据为严重风险。诊断会分开这些类型，在每个标题中显示首个冲突虚拟路径，并且只有在确认物理源副本仍位于受管理的模组快照内时才会打开它。

兼容的文本冲突会提供只读差异编辑器。管理员可选择任意两个源模组、交换两侧、忽略空白，在并排与统一视图之间切换，搜索内容，仅保留带上下文的更改，并在更改块之间导航。行内高亮会显示确切改动的字符。读取前会重新验证路径，二进制内容会被拒绝，每个文件限制为 2 MiB，每侧最多渲染 12,000 行。

兼容性现在拥有独立的项目标签页。主面板只显示精简的健康摘要，并可在不重新运行分析的情况下打开该标签页。批量规则有意保持严格：它们只能禁用经验证缺少目标版本结构的模组、禁用来源或有效 `mod.info` 不可用的条目，以及应用计算出的模组和地图顺序。每个批次都会显示确切目标、保留快照，并将有歧义的文件冲突留给管理员明确审查。

## 依赖感知导入

每次本地或 Workshop 导入都会在修改项目之前进行预检。管理器会规范化从 `mod.info` 读取的 `require=` Mod ID，与当前模组包比较，并在应用内确认框中列出缺少的依赖。管理员可以添加所选模组及所有可解析依赖，也可以明确选择只添加所选模组。

本地依赖通过精确 Mod ID 匹配。对于 Workshop 来源，PZASM 还会读取该物品官方的 **Required Items** 列表；推荐项目永远不会被视为依赖。缺少依赖的诊断项和受影响的模组卡片都提供一键修复。下载的 Workshop 子项目只有在其有效 `mod.info` 确实提供所需 Mod ID 时才会被接受。如果没有经过验证的来源，管理器会报告未解析 ID，而不会猜测。添加的依赖会放在请求它的模组之前，并再次验证完整顺序。

## Workshop 发现筛选

公开 Workshop 浏览器将 Steam Community 排序与公开项目详情的确定性筛选结合起来。搜索可以同时针对标题和描述，也可只针对其中一项。支持多个必需和排除标签，必需标签可选择全部匹配或至少匹配一个。其他筛选包括发布/更新时间、作者 SteamID64、当前与累计订阅、收藏、浏览量、最小/最大文件大小、图片/描述可用性，以及项目是否已添加到所选目标。

搜索深度是明确的：管理器每个批次检查一、三或五个 Steam 结果页面。候选 ID 会在批量请求公开详情前去重，浏览结果会短暂缓存。数值和元数据筛选在 Steam 发现之后执行，因此即使公开 Workshop 页面忽略某个可选 URL 参数，其行为仍保持确定。
