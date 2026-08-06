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

计划任务会验证权限与依赖，可选刷新来源，在临时目录构建，通过 RCON 执行 `save` 和 `quit`，发布条目，并在服务器原本运行时重新启动。密码和 Steam Guard 验证码不会保存。

## 为什么需要外部程序

游戏内模组无法可靠管理 SteamCMD、游戏未运行时的计划任务、私有文件或多个服务器配置。因此 PZASM 使用共享核心的本地 ASP.NET Core 程序和无界面 CLI。只有生成的 Lua 连接提示会在 Project Zomboid 中运行。

## 安全与权利

[官方模组政策](https://projectzomboid.com/blog/modding-policy/)要求公开和不公开列出的模组包取得适当许可。未知权利允许本地构建但阻止发布；明确拒绝会阻止构建；私有证明不会进入 `Contents`；公开描述始终列出所有来源。

在接受 [Workshop 法律协议](https://steamcommunity.com/workshop/workshopsubmitinfo/)之前，Steam 可能隐藏新条目。

## 剩余风险

- 协议或 Build 42 结构未来发生变化；
- 作者修改 Mod ID、依赖、地图或许可证；
- 未声明依赖与需要手动调整的地图顺序；
- 静态分析无法发现的逻辑冲突；
- SteamCMD 偶尔需要人工操作；
- 发布后必须重启服务器。
