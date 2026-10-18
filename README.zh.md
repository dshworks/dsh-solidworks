<div align="center">

```
   ___ _      __     dsh-solidworks
  / __| \    / /
  \__ \\ \/\/ /      DeepSeek Harness 的 SOLIDWORKS 连接器：
  |___/ \_/\_/       读取、修改、重建、渲染、导出你桌面上的模型
```

**让 dsh 直接操作你已经打开的 SOLIDWORKS：2016 及以后版本，同一台 PC 或 Mac 上的 Windows 虚拟机都行。**

</div>

<p align="center"><strong>
13 个工具 · 已在 SOLIDWORKS 2016 SP1 和 2019 SP0 上实测 · 一个 bridge exe，无需安装运行时、无需管理员权限 · 默认只读 · MIT
</strong></p>

<p align="center">
<a href="https://github.com/dshworks/dsh-solidworks/actions/workflows/ci.yml"><img src="https://github.com/dshworks/dsh-solidworks/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
<a href="https://www.npmjs.com/package/@dshworks/dsh-solidworks"><img src="https://img.shields.io/npm/v/@dshworks/dsh-solidworks?color=CB3837&logo=npm&logoColor=white" alt="npm"></a>
<a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-3DA639" alt="MIT"></a>
<img src="https://img.shields.io/badge/SOLIDWORKS-2016%2B-DA291C" alt="SOLIDWORKS 2016 及以后">
<img src="https://img.shields.io/badge/dsh-0.2.0--rc.2%20verified-1E90FF" alt="已在 dsh 0.2.0-rc.2 上验证">
</p>

<p align="center">
<a href="#一次会话">一次会话</a> ·
<a href="#开始使用">开始使用</a> ·
<a href="#工具">工具</a> ·
<a href="#它不会做什么">它不会做什么</a> ·
<a href="#工作原理">工作原理</a> ·
<a href="#证据">证据</a> ·
<a href="README.md">English</a>
</p>

---

你向 dsh 询问一个零件、装配体或工程图，它会在你的 SOLIDWORKS 里以只读方式打开文件，
直接从模型本身作答：尺寸与方程式、自定义属性、质量、特征树、哪些特征重建失败。它可以在内存中
修改数值并重建，以渲染图的形式看到修改结果，再把 STEP、PDF 或原生格式的副本写回你的项目。
除非你主动开启，磁盘上的源文件不会被改动。

## 一次会话

一次真实运行（dsh 0.2.0-rc.2、`deepseek-flash`，SOLIDWORKS 2019 SP0 跑在 Parallels 虚拟机里，
dsh 在宿主机一侧）。提示词：

> 先检查连接，然后打开 C:\dshsw\samples\box.sldprt，告诉我它的包围盒尺寸（mm）、质量（kg）和材料；
> 渲染一张等轴测视图，用一句话描述零件外观；导出为 box.step；最后关闭零件。

dsh 依次调用了 `sw_status`、`sw_open`、`sw_inspect`、`sw_mass_properties`、`sw_view`（等轴测和
前视各一次）、`sw_features`、`sw_export`、`sw_close`，回答：

> **包围盒：** 100 × 100 × 100 mm。**质量：** 0.2305 kg（体积 230,500 mm³，密度 1000 kg/m³）。
> **材料：** 未指定。**等轴测视图：** 一个前面开口、薄壁的 100 mm 空心立方体，像去掉一面的盒子。
> **导出：** `C:\dshsw\out\box.step`，25,726 字节。

“前面开口”不在任何工具返回的数字里，是模型从渲染图上看出来的。STEP 文件以 `solidworks/box.step`
落到了 dsh 的工作区，并与 bridge 报告的 SHA-256 校验一致。

## 开始使用

需要 dsh 0.2.0-rc.2 或更高版本（它的 `dsh plugin add` 依赖 pnpm，PATH 里要有 pnpm），以及一个正在
运行的 SOLIDWORKS 2016 或更高版本。

### dsh 和 SOLIDWORKS 在同一台 Windows PC 上

```
dsh plugin --profile web add @dshworks/dsh-solidworks
```

启动 SOLIDWORKS、启动 dsh、直接提问。插件会在第一次涉及 SOLIDWORKS 的提问时运行自带的
`swbridge.exe`，不需要配对，也不需要安装任何东西。

### dsh 在 Mac 上，SOLIDWORKS 在 Windows 虚拟机里

1. 在 Mac 上执行 `dsh plugin --profile web add @dshworks/dsh-solidworks`，然后启动一次 dsh。
   它会开始监听 bridge，并写出一个配对包。
2. 在虚拟机里，以使用 SOLIDWORKS 的那个用户登录，打开 `\\Mac\Home\.dsh\dsh-solidworks\windows\`
   （Parallels 把你的 Mac 主目录共享在这里），双击 `pair.cmd`。

这一步会为该 Windows 用户安装 bridge、连上 dsh，并设置为每次登录自动启动。不需要管理员，不需要
防火墙规则：bridge 主动连向 dsh，Windows 一侧不开任何端口。配对前就向 dsh 提问的话，回答会告诉你
配对包在哪里。

在 VMware、另一台 PC，或没有开启 Parallels 共享的 Mac 上，把 `~/.dsh/dsh-solidworks/windows/` 文件夹
复制到 Windows 上再运行 `pair.cmd`。如果虚拟机访问 Mac 用的地址不在配对包列出的地址里，手动指定：

```yaml
# ~/.dsh/profiles/web/cordis.patch.yml
- id: dsh-solidworks
  config:
    advertise: ['10.211.55.2']   # Parallels 常见的宿主机地址
```

在虚拟机里撤销：`%LOCALAPPDATA%\dsh-solidworks\swbridge.exe uninstall`。

### 在 Windows PC 上配合其他 MCP 客户端

`swbridge.exe mcp` 是一个普通的 stdio MCP server，提供同样的 13 个工具：

```
claude mcp add solidworks -- C:\tools\swbridge.exe mcp
```

exe 在本包的 `bin/swbridge.exe`，每个 [GitHub release](https://github.com/dshworks/dsh-solidworks/releases)
也附带它和 SHA-256。

## 工具

长度一律用毫米，角度用度，质量用千克，与文档自身单位无关；字段名里写明单位（`value_mm`、`mass_kg`）。
`doc` 是已打开文档的路径、文件名或标题，默认是当前活动文档。

| 工具 | 作用 |
| --- | --- |
| `sw_status` | 版本（2016 SP1、2019 SP0 ……）、所有 SOLIDWORKS 实例、已打开文档，以及 SOLIDWORKS 正在等待的对话框。首先调用。 |
| `sw_open` | 打开 `.sldprt`、`.sldasm` 或 `.slddrw`，默认只读，并设为活动文档。 |
| `sw_inspect` | 类型、单位、配置、材料、自定义属性、包围盒、实体数和零部件数。 |
| `sw_features` | 特征树，含压缩状态和重建错误。 |
| `sw_parameters` | 所有尺寸（`D1@Sketch1`、数值、单位、是否从动）和方程式，标出全局变量。 |
| `sw_components` | 装配体的零部件树：文件、配置、状态、是否固定、位置。 |
| `sw_mass_properties` | 质量、体积、表面积、密度、质心。 |
| `sw_edit` | 在内存中修改后统一重建一次：配置、压缩/解除压缩、全局变量、尺寸、自定义属性。每项修改都返回修改前后的值和各自的结果。 |
| `sw_rebuild` | 重建并列出每个失败的特征及原因。 |
| `sw_export` | 导出副本：STEP、IGES、Parasolid、ACIS、STL、PDF、DXF/DWG、eDrawings、JPG/PNG/TIF 或原生格式。返回路径、大小和 SHA-256；bridge 在另一台机器上时会自动取回到 dsh 工作区。 |
| `sw_save` | 覆盖保存源文件。除非 bridge 以 `--allow-save` 启动，否则拒绝。 |
| `sw_close` | 关闭文档；只有这个 bridge 自己做的未保存修改才会被丢弃。 |
| `sw_view` | 从标准视图、缩放到合适大小渲染一张 PNG，给模型看。之后恢复用户原来的视图。 |

## 它不会做什么

这些规则在 Windows 一侧、文件所在的地方执行。dsh 可以提出任何请求，最终由 bridge 决定。

- **不经允许修改你的文件。** 文档默认只读打开，`sw_edit` 只改内存，`sw_save` 只有在 bridge 以
  `--allow-save` 启动时才可用。想保留修改，就导出一个副本。
- **写到允许的文件夹之外。** 导出只能写到 `Documents\dsh-solidworks`，或你用 `--allow-write` 允许的
  文件夹。已存在的文件只有在明确要求时才会被替换。
- **丢掉别人的工作。** 只有当未保存的修改是这个 bridge 做的、且调用带了 `discard`，才会关闭该文档。
- **启动、关闭或点击 SOLIDWORKS。** 它连接你面前的那个 SOLIDWORKS，从不启动或退出它，也从不替你
  回答对话框，而是把对话框的文字告诉你，因为回答是你的决定。
- **执行代码。** 没有宏、脚本或“调用任意 API”的工具。它能做的每一次 SOLIDWORKS 调用都列在
  [`bridge/compat/members.tsv`](bridge/compat/members.tsv) 里。
- **把配对令牌放进模型上下文。** “未连接”的回答只说明配对包在哪里，从不包含令牌，所以对话记录里不会有它。

dsh 和另一台机器上的 bridge 之间的连接是双向认证的（双方在任何调用之前，用各自的新随机数做
HMAC-SHA256 证明自己持有令牌），但没有加密。它面向仅主机的虚拟机网络或本地网络；跨越其他网络时，
请通过 SSH 或 VPN 传输。详见 [SECURITY.md](SECURITY.md)。

## 工作原理

- **一个 exe 支持所有版本。** bridge 不引用任何 SOLIDWORKS interop 程序集，每次调用都按名字走
  `IDispatch`，所以驱动 2016 的同一个构建也能驱动 2019 及以后的版本，并且可以在 Linux CI 上构建。
  `dynamic` 和 PowerShell 的 COM 适配器在跨进程访问 SOLIDWORKS 对象时都会失败
  （`TYPE_E_ELEMENTNOTFOUND`，实测），`Type.InvokeMember` 不会。
- **2016 兼容性是检查出来的，不是假设的。** `bridge/compat/members.tsv` 列出 bridge 调用的每个成员
  及其接口；如果 bridge 调用了未列出的成员，或列出的成员在 SOLIDWORKS 2016 SP1 interop 中不存在，测试
  就会失败。`SaveAs2`（2019 SP1+）、`Get6`（2018+）、`IEquationMgr.GlobalVariable`（2016 SP4+）就是这样
  被挡在外面的。
- **连接你正在看的那个实例。** 每个 SOLIDWORKS 都会在桌面会话的运行对象表里注册
  `SolidWorks_PID_<pid>`。bridge 选择可见、且窗口层级最靠前的那个，而不是 `GetActiveObject` 随便返回的
  一个。我们见过一台机器上同时有四个实例，其中三个不可见、卡在启动对话框上。
- **忙，而不是卡死。** 所有调用都在一个带 OLE 消息过滤器的 STA 线程上执行，SOLIDWORKS 忙时会重试。
  超出预算的调用返回 `TIMEOUT`，并说明它是否还可能完成；之后的调用会立即返回 `SW_BUSY`，不会排在后面等，
  `sw_status` 依然能回答，并说出 SOLIDWORKS 正在等待的对话框。
- **主动连出，而不是监听。** 在另一台机器上时，由 bridge 连向 dsh。Windows 防火墙默认阻止入站，开端口
  需要管理员；出站连接两者都不需要，而且能穿过虚拟机的 NAT。先连上的 bridge 保持连接；它离开后，心跳会在
  几秒内释放这个位置。
- **一份契约。** `protocol/tools.json` 定义名称、参数结构、时间预算和错误码。bridge 内嵌它；插件用 dsh 自己的
  `createMcpToolDefinition` 从它构建 dsh 工具，所以参数校验、错误结果和图片准入与任何 MCP server 的行为一致。

## 证据

本页的每一条说法都在 2026-10-17 对真实 SOLIDWORKS 跑过。

| 内容 | 结果 |
| --- | --- |
| 端到端实测，SOLIDWORKS 2016 SP1（24.1.0） | 34 项检查全部通过，经由真实的主动连出链路，用的就是发布的 exe |
| 端到端实测，SOLIDWORKS 2019 SP0（27.0.0） | 34 项检查全部通过，同一个 exe |
| 真实 dsh 0.2.0-rc.2 + `deepseek-flash`，dsh 在 Linux、SOLIDWORKS 2019 在虚拟机（listen） | 9 次工具调用全部完成，图片送达模型，STEP 已取回并校验哈希 |
| 真实 dsh 0.2.0-rc.2 + `deepseek-flash`，dsh 就在那台 Windows PC 上（spawn） | 插件通过 dsh 的子进程服务启动自带 bridge；5 次工具调用全部完成；dsh 退出后没有残留 bridge 进程 |
| 发布的 `swbridge.exe` | CI 从源码重新构建，得到同一个 SHA-256（`bridge/swbridge.exe.sha256`） |
| 用生成的 `pair.cmd` 配对、登录自启、`uninstall` | 无需管理员即装好；互斥锁下只有一个实例；卸载后不留痕迹 |
| 单元测试（Linux，无需 SOLIDWORKS） | 44，包括 2016 成员检查和双方契约检查 |
| `swbridge selftest`（Windows，无需 SOLIDWORKS） | 路径、单位、JSON，以及与插件共用的 HMAC 测试向量 |

端到端检查用工具没有自己声明过的东西来检验每个回答：样例 `box.sldprt` 是一个 100 mm 立方体抽壳 5 mm、
一面开口，所以 `sw_edit` 把深度改成 150 mm 后，`sw_mass_properties` 必须报告 100·100·150 − 90²·145 =
325,500 mm³（原为 230,500）；PNG 必须按请求的尺寸解码；取回的 STEP 必须与 bridge 的 SHA-256 一致并以
`ISO-10303-21;` 开头；各种拒绝必须返回对应的错误码。2016 上的那次运行用的是一个别人开着 58 个文档的装配体的
SOLIDWORKS；有一项检查确认他们的文档、修改标记和活动文档都原样恢复。

尚未验证：SOLIDWORKS 2020 及以后、Windows 10、x64 实机。欢迎反馈，先贴 `swbridge status` 的输出。

## 何时不适合用

- **想让智能体从零建模。** 这个版本读取、修改已有数值、重建和导出，不画草图也不创建特征。
- **文件是 STEP 或 IGES。** `sw_open` 只打开 SOLIDWORKS 文档，请先在 SOLIDWORKS 里导入。
- **想在 SOLIDWORKS 窗口里对话。** 没有任务窗格，对话在 dsh 里。
- **要遍历五万个面。** 跨进程 COM 每次调用都有往返开销。工具返回摘要；重几何的工作适合插件（add-in）或导出的 STEP。
- **多台 SOLIDWORKS 机器同时接入一个 dsh。** 同一时间只有一个 bridge 持有连接。

## 开发

```
npm install && npm test              # 插件、契约和 2016 成员检查，无需 Windows
npm run build:bridge                 # 需要 .NET 8 SDK；为 net48 构建 bin/swbridge.exe
SW_LINK_TOKEN=... node tests/live/e2e.mjs   # 对一个连向本机的 bridge 做实测
```

MIT 许可，见 [LICENSE](LICENSE)。
