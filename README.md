<div align="center">
  <img src="public/art/game/logo-bordered.png" alt="Flotsam Launcher" width="240" />
  <h1>浮游启动器 · Flotsam Launcher</h1>
  <p>面向《浮游世界》(Flotsam) 的第三方开源桌面启动器、模组管理器与多人联机套件</p>
  <p>
    <img src="https://img.shields.io/badge/Platform-Windows%2010%2F11%20x64-0078D4" alt="Windows" />
    <img src="https://img.shields.io/badge/Host-Tauri%202%20%2B%20Rust-DEA584" alt="Tauri 2 + Rust" />
    <img src="https://img.shields.io/badge/UI-Vue%203%20%2B%20TypeScript-42B883" alt="Vue 3 + TypeScript" />
    <img src="https://img.shields.io/badge/Plugin-BepInEx%205%20%2B%20Harmony%202-512BD4" alt="BepInEx 5" />
  </p>
</div>

---

## 项目简介

**浮游启动器 (Flotsam Launcher)** 是一个专为《浮游世界》(*Flotsam*) 设计的独立桌面工具与联机扩展项目。项目参考了社区成熟游戏启动器（如 PCL2）的工程组织与交互克制原则，将**游戏启动隔离**、**BepInEx 模组全生命周期管理**、**存档元数据解析与事务级备份还原**以及**多人联机房间与同步插件**整合在单个原生桌面程序中。

本项目由两部分协同构成：
1. **桌面启动器宿主 (`src/` + `src-tauri/`)**：基于 Tauri 2、Rust 与 Vue 3 构建，负责本地文件事务管理、PE 程序集静态扫描、Nexus Mods 下载队列及联机大厅信令。
2. **游戏内联机运行时 (`multiplayer/Flotsam.Multiplayer/`)**：基于 BepInEx 5 与 Harmony 2 的 C# 插件，支持多人同船协作（共建模式）与每人一船同海航行（独立模式）。

---

## 功能特性

### 1. 启动与运行环境管理
- **原版 / 模组无损切换**：原版启动通过 Doorstop 进程级环境变量（`DOORSTOP_DISABLE=1`）在不改动、不移动本地插件文件的前提下纯净运行游戏；模组与联机启动自动按需部署 BepInEx 运行时。
- **显示模式接管与全屏修复**：支持在启动器设置中切换无边框全屏（自动探测主显示器物理原生分辨率并修复 Unity 注册表残留的小窗口状态）、窗口模式或跟随游戏内设置，同时贯通单人与多人联机启动链路。
- **Epic / 独立目录兼容**：自动识别游戏根目录下的 `EOSBootstrapper.exe` 与 `Flotsam.exe`，保持原有平台引导链路。
- **组件兼容自检**：计算游戏核心程序集、Doorstop 配置与已启用插件集合指纹，在启动或加入联机房间前给出明确的差异项与修复路径。

### 2. 模组与整包管理
- **静态元数据与依赖诊断**：无需加载运行 DLL，直接通过 `dotnetdll` 解析程序集的 `[BepInPlugin]`、`[BepInDependency]`、`[BepInIncompatibility]` 与 `[BepInProcess]` 声明，自动检测缺失前置、最低版本不满足、ID 冲突及循环依赖。
- **事务级整包生命周期**：支持 `.zip` / `.dll` 导入、覆盖更新、上一版本回退、一键启停以及卸载恢复区；文件变更均记录写前事务日志，异常中断后可自动恢复一致性。
- **模组组合 (Profiles) 与在线目录**：支持将当前模组启用状态及插件配置保存为组合并导入/导出分享；内置 Nexus Mods 目录检索与支持断点续传、并发限流、SHA-256 校验的下载队列。

### 3. 存档解析与安全备份
- **`.fs` 存档头深度解析**：原生读取 GZip 存档内的 `SaveInfo` 元数据，直观展示每个存档的船镇名称、生存天数、漂流者人数、累计游玩时长、手动/自动存档类型、精确保存时间以及单船镇下的完整存档槽时间线。
- **非破坏性原子还原**：支持手动快照与启动前自动备份；还原操作经双步核对与运行态/联机态安全锁检查后，通过同卷原子目录重命名替换存档，并将原存档完整保留为回滚副本，绝不执行递归物理删除。

### 4. 多人联机系统（共建模式 / 独立多船模式）
- **内置 TCP 房间大厅与智能网卡选路**：支持 `flotsam://` 邀请码鉴权（HMAC-SHA256）、最多 8 人房间、延迟探测、房主公告、成员管理与实时聊天；自动过滤代理 TUN 网卡（`198.18.0.0/15` 等）与虚拟机交换机（WSL / Hyper-V），支持一键切换异地虚拟局域网（Tailscale / Radmin VPN / ZeroTier / EasyTier 等）或局域网网卡，并内置联机方式与端口转发指南。
- **房主存档统一与客机偏好继承**：房主联机直接读写系统标准存档目录，与启动器存档管理、一键备份完全互通；客机使用独立运行目录隔离，同时自动继承本机游戏语言、音量与界面偏好。
- **共建模式 (`shared`)**：所有玩家共同经营同一艘船镇，由房主权威同步建筑放置/拆除/移动、生产队列、库存物资、居民调度、科技研发、海上打捞与航行路线。
- **独立模式 (`independent`)**：每位玩家在海域中拥有各自独立的船镇、居民、电力、库存与科研树，支持重连昵称绑定与随房主存档一并持久化；玩家可通过海图或 `WASD` 独立操舵航行，并在游戏内按 `F8` 呼出舰队面板执行跨船观察、物资赠予、掠夺或交火（由房主房间规则控制开关）。

---

## 目录结构

```text
Flotsam Launcher/
├── public/                         # 静态资源与界面图标素材
├── src/                            # 前端源码 (Vue 3 + TypeScript + Pinia)
│   ├── components/                 # 模块化界面组件（房间大厅、模组列表、在线目录、存档备份、抽屉与浮层）
│   ├── App.vue                     # 固定视口主框架与路由视图
│   ├── bridge.ts                   # Tauri IPC 调用封装与浏览器预览降级层
│   ├── i18n.ts                     # 中 / 英双语词条
│   └── store.ts                    # 全局状态与通知队列
├── src-tauri/                      # 桌面端原生宿主 (Rust + Tauri 2)
│   └── src/
│       ├── service.rs              # 游戏启动、路径规范化、.fs 存档解析与快照汇总
│       ├── mods.rs                 # .NET 程序集元数据离线读取与依赖图校验
│       ├── packages.rs             # 模组整包事务安装、更新、回退与恢复
│       ├── profiles.rs             # 模组组合快照切换与导入导出
│       ├── online.rs / queue.rs    # Nexus API 客户端与持久化下载队列
│       ├── save_restore.rs         # 存档整目录原子还原与回滚代管理
│       ├── network.rs              # TCP 房间信令服务、邀请码鉴权与延迟探测
│       ├── multiplayer.rs          # BepInEx 与联机插件按需部署及会话配置下发
│       └── compat.rs               # 游戏与模组环境指纹比对
├── multiplayer/                    # 游戏内联机模块 (.NET / C#)
│   ├── Flotsam.Multiplayer/        # BepInEx 5 + Harmony 2 联机同步核心插件
│   ├── Protocol.Tests/             # 协议序列化、规则鉴权与多客户端回环传输单元测试
│   └── Metadata.Fixture/           # 供元数据扫描测试使用的示例程序集源码
├── scripts/                        # 构建、打包与本地双开联机沙盒脚本
├── tests/                          # Playwright 离线端到端界面与交互测试套件
└── THIRD-PARTY/                    # 第三方运行时许可文本 (BepInEx LICENSE)
```

---

## 开发环境与编译指南

### 环境前置要求
- **操作系统**：Windows 10 / 11 (x64)，已安装 Microsoft Edge WebView2 Runtime
- **Node.js**：`>= 20.0`（含 `npm`）
- **Rust 工具链**：`stable-x86_64-pc-windows-msvc`（含 Visual Studio C++ 生成工具）
- **.NET SDK**：`>= 8.0`（用于编译 `netstandard2.1` 联机插件与协议测试）
- **本地游戏引用**：编译 C# 联机插件时需要读取正版《浮游世界》的托管程序集（默认路径为 `D:\Epic Games\Flotsam`，可通过参数覆盖）

### 快速开始

1. **安装前端依赖**
   ```powershell
   npm install
   ```

2. **编译 C# 联机插件并准备内嵌载荷**
   > `src-tauri` 在编译时会通过 `include_bytes!` 内嵌 `multiplayer/payload/` 下的插件与 BepInEx 运行时压缩包，首次编译 Rust 宿主前请先执行此步。若游戏安装在非默认路径，可传入 `-GameRoot`：
   ```powershell
   pwsh -NoProfile -File scripts/build-multiplayer.ps1 -GameRoot "你的Flotsam安装目录"
   ```
   默认路径下可直接运行快捷命令：
   ```powershell
   npm run plugin:build
   ```

3. **启动桌面开发模式（支持前端热重载）**
   ```powershell
   npm run desktop
   ```

4. **打包独立发行可执行文件**
   ```powershell
   npm run desktop:build
   ```
   构建完成后将在仓库根目录输出单文件桌面程序 `浮游启动器.exe`。

### 自动化测试与本地联机沙盒

本项目所有单元与界面测试均为纯本地离线测试，无需外部网络连接即可在数秒内跑完：

```powershell
# 1. C# 联机协议与 8 客户端回环传输测试
npm run multiplayer:test

# 2. Rust 宿主单元测试（模组事务、存档原子还原、TCP 握手、元数据扫描等）
npm run test:native

# 3. 前端类型检查、生产构建与 Playwright 端到端交互测试
npm run build
npm test

# 4. 本地双实例联机沙盒（在 multiplayer/lab/ 下生成隔离的 host 与 client 副本用于本机双开调试）
pwsh -NoProfile -File scripts/prepare-multiplayer-lab.ps1 -Source "D:\Epic Games\Flotsam" -Mode independent -Launch
```

---

## 贡献与开发约定

欢迎提交 Issue 或 Pull Request 参与改进。在提交代码前请留意以下工程准则：

1. **文件安全优先**：任何涉及游戏目录或用户存档目录的写入操作，必须经过路径边界校验（防止目录穿越与符号链接逃逸）并保持事务可回滚，严禁对用户存档目录执行无备份的递归删除。
2. **离线可重复测试**：新增协议指令、Rust IPC 接口或前端关键交互流程时，请补充对应的确定性本地单元测试或 Playwright 用例，避免在自动化测试中引入外网请求或长耗时轮询。
3. **桌面视口与交互一致性**：前端保持固定视口（外层零纵向滚动），按轻重程度严格区分 `Toast`（瞬时结果）、`Popover`（轻量锚定配置）、`Drawer`（上下文详情抽屉）与 `Modal`（仅限高危破坏性操作确认）。

---

## 许可说明与免责声明

- **非官方声明**：本项目为玩家社区独立开发的非商业开源工具，与《浮游世界》(*Flotsam*) 开发商 **Pajama Llama Games**、发行商 ** Stray Fawn Publishing**、**Epic Games** 以及 **Plain Craft Launcher (PCL)** 均无任何隶属、授权或合作关系。
- **第三方组件与素材说明**：
  - 游戏名称 *Flotsam* 及界面引用的游戏内图标、纸张纹理等视觉资产版权归 **Pajama Llama Games** 所有，仅用于对应正版游戏的本地启动与辅助展示。
  - 联机模块依赖的 [BepInEx](https://github.com/BepInEx/BepInEx) 与 [Harmony](https://github.com/pardeike/Harmony) 遵循各自开源许可证（详见 [`THIRD-PARTY/BepInEx-LICENSE.txt`](THIRD-PARTY/BepInEx-LICENSE.txt)）。
- **使用风险提示**：多人联机与第三方模组会通过 Harmony 补丁介入游戏运行时逻辑。尽管启动器内置了存档备份与回滚机制，仍建议在体验大型模组或联机测试前妥善备份重要存档。
