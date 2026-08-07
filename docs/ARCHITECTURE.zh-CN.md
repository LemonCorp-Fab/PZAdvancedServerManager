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

计划任务会记录权限、验证依赖、按需刷新来源、构建、发布，并在需要时通过 RCON 协调服务器。受监督的登录只通过标准输入把密码交给 SteamCMD，不会保存密码。如果 Steam Guard 保护该账户，界面会请求当前验证码，并通过标准输入使用官方 `set_steam_guard_code` 命令重新登录。SteamCMD 随后在便携目录中保留自己的令牌；手动和计划发布仅使用此会话。管理器仅记录上次验证成功的时间。会话过期时会立即失败并要求重新连接，不会停在不可见的输入提示上。界面会实时显示 SteamCMD 输出、执行超时限制，并可取消外部进程。

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
- 发布后必须重启服务器。

## 本地与远程服务器编排

配置可以指向本地 INI 文件，也可以连接远程 VPS/独立服务器。远程配置可以仅使用 RCON；SSH 与远程 INI 管理均为可选。状态检查会执行真实的 RCON 身份验证，控制台可发送游戏支持的管理命令，正常停止依次使用 `save` 和 `quit`。

如果 systemd、Docker、托管面板或其他监督程序会在 `quit` 后重启 Project Zomboid，仅 RCON 的配置也能协调发布：Workshop 上传先完成，随后管理器发送 `save` 和 `quit`。SSH 只用于可选的 INI 管理或显式的游戏启动命令。管理器会拒绝主机级别的 `reboot`、`shutdown` 和 `poweroff` 命令。为支持无人值守操作，RCON 密钥保存在管理器本地配置数据中，因此必须妥善保护该目录。
