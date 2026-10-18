<div align="center">

```
   ___ _      __     dsh-solidworks
  / __| \    / /
  \__ \\ \/\/ /      DeepSeek Harness 的 SOLIDWORKS 连接器：
  |___/ \_/\_/       在你桌面上的 SOLIDWORKS 里建模、装配、检查、出图、导出
```

**让 dsh 直接操作你已经打开的 SOLIDWORKS：2016 及以后版本，同一台 PC 或 Mac 上的 Windows 虚拟机都行。**

</div>

<p align="center"><strong>
21 个工具 · 已在 SOLIDWORKS 2016 SP1 和 2019 SP0 上实测 · 一个 bridge exe，无需安装运行时、无需管理员权限 · 你的文件默认只读 · MIT
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

你向 dsh 要一个零件，它就在你的 SOLIDWORKS 里把它建出来：草图、拉伸、旋转、打孔、圆角、倒角、抽壳。
它会拿结果和它本想做出的几何对照、渲染出来看一眼，用配合把零件装起来、检查干涉、汇总成物料清单，
再出一张带尺寸的工程图。它同样能处理你已有的文件：以只读方式打开，直接从模型本身作答，在内存中试新数值，
把 STEP、PDF 或原生格式的副本写回你的项目。除非你主动开启，磁盘上的文件不会被改动。

## 一次会话

一次真实运行（dsh 0.2.0-rc.2、`deepseek-flash`，SOLIDWORKS 2019 SP0 跑在 Parallels 虚拟机里，
dsh 在链路另一端）。提示词（原文为英文）：

> 新建一个零件：一块 100 x 60 x 20 mm 的板，平放在上视基准面上、以原点为中心，中心有一个 20 mm 的通孔，
> 四条竖直棱边倒 5 mm 圆角。检查它的体积是否等于这个几何应有的值。在一张图里从四个方向展示它，并说说你看到了什么。
> 保存为 plate.sldprt，出一张工程图并导出为 plate.pdf。最后关闭你建的所有东西。

dsh 依次调用了 `sw_status`、`sw_new`、`sw_build`（一次调用完成草图、两侧对称拉伸、打孔，并用棱边上的点选中四条边倒圆角）、
`sw_view`（四个视图拼在一张图上）、`sw_save`、`sw_drawing`、`sw_view`（看工程图）、`sw_export`、两次 `sw_close` 和 `sw_status`。
共 11 次调用，全部完成。它的回答节选（原文为英文）：

> **体积：** 工具返回 **113287.61102 mm³**。按几何核对：100×60×20 = 120000；减去 ⌀20 通孔 π·10²·20 = 6283.185；
> 减去四个 5 mm 圆角 4·(25 − π·25/4)·20 = 429.204 → 应为 **113287.611 mm³**。完全一致。
>
> **上视：** 100 × 60 的轮廓，四角倒圆，⌀20 的孔正在中心。**前视：** 一个 100 × 20 的矩形；从这个方向看，竖直的孔在内部，
> 所以没有孔的轮廓。
>
> **工程图：** 第三角投影，比例 1:1，图纸 279.4 × 215.9 mm；前视、上视、右视和等轴测四个视图；导入 2 个尺寸
> （图纸上显示 R5 和 20.00）。**PDF：** 本地副本 `solidworks/plate.pdf`。**已关闭：** 工程图（未保存的修改已丢弃）和
> `plate.sldprt`；documents: []。

<p align="center">
<img src="docs/images/plate-views.png" width="49%" alt="sw_view：板的前视、上视、右视和等轴测拼在一张图上">
<img src="docs/images/plate-drawing.png" width="49%" alt="sw_drawing 导出的 PDF：1:1 第三角投影视图和模型尺寸">
</p>

体积是模型用自己的算术核对的，而不是相信工具。它对每个视图的描述来自那张图片：工具返回的任何数字都没说前视图里看不到孔。
PDF 已落到 dsh 的工作区，并与 bridge 报告的 SHA-256 校验一致。

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

`swbridge.exe mcp` 是一个普通的 stdio MCP server，提供同样的 21 个工具：

```
claude mcp add solidworks -- C:\tools\swbridge.exe mcp
```

exe 在本包的 `bin/swbridge.exe`，每个 [GitHub release](https://github.com/dshworks/dsh-solidworks/releases)
也附带它和 SHA-256。

## 工具

长度一律用毫米，角度用度，质量用千克，与文档自身单位无关；字段名里写明单位（`value_mm`、`mass_kg`）。
几何在所有工具里都用同一个坐标空间：模型空间的点 `[x, y, z]`，单位 mm，Y 轴朝上，前视基准面是 z = 0，
上视是 y = 0，右视是 x = 0。面和边用其上的一个点来选取。`doc` 是已打开文档的路径、文件名或标题，
默认是当前活动文档。

| 工具 | 作用 |
| --- | --- |
| `sw_status` | 版本（2016 SP1、2019 SP0 ……）、所有 SOLIDWORKS 实例、已打开文档，以及 SOLIDWORKS 正在等待的对话框。首先调用。 |
| `sw_open` | 打开 `.sldprt`、`.sldasm` 或 `.slddrw`，默认只读，并设为活动文档。 |
| `sw_new` | 用 SOLIDWORKS 的默认模板新建一个空的零件或装配体。 |
| `sw_inspect` | 类型、单位、配置、材料、自定义属性、包围盒、实体数和零部件数。 |
| `sw_features` | 特征树，含压缩状态和重建错误。 |
| `sw_parameters` | 所有尺寸（`D1@Sketch1`、数值、单位、是否从动）和方程式，标出全局变量。 |
| `sw_components` | 装配体的零部件树：文件、配置、状态、是否固定、位置。 |
| `sw_mass_properties` | 质量、体积、表面积、密度、质心。 |
| `sw_probe` | 某个点上是什么：那里的面、边或顶点，及其曲面或曲线类型、面积或长度、法向或轴线、直径和生成它的特征。不给点时，读取用户此刻在 SOLIDWORKS 里选中的东西。两个对象还会给出它们之间的距离和夹角。 |
| `sw_edit` | 在内存中修改后统一重建一次：配置、压缩/解除压缩、全局变量、尺寸、自定义属性。每项修改都返回修改前后的值和各自的结果。 |
| `sw_build` | 分步建模一个零件：在基准面或面上画草图（直线、多段线、矩形、圆、圆弧、中心线），拉伸或切除（给定深度、完全贯穿、两侧对称、拔模），旋转，圆角，倒角，抽壳，偏移基准面，删除。某一步失败时撤销本次调用。返回每个特征及其尺寸，以及零件的体积和包围盒。 |
| `sw_study` | 一次调用里试最多 50 组尺寸和变量取值，每组都重建并测量（质量、体积、面积、质心、尺寸）；结束后把每个值恢复原样并核对。 |
| `sw_rebuild` | 重建并列出每个失败的特征及原因。 |
| `sw_assemble` | 把已保存的零件插入到指定原点位置，配合基准面、面、边或顶点（重合、同心、平行、垂直、相切、距离、角度），固定或浮动。 |
| `sw_interference` | 所有相互重叠的零部件对及重叠体积，按体积从大到小。 |
| `sw_bom` | 物料清单：每个文件和配置的数量及自定义属性，可展开到零件或只看顶层；可选输出 CSV。 |
| `sw_drawing` | 为已保存的模型出图：按第一角或第三角投影布置视图，取能放进图纸的最大标准比例，并导入模型尺寸。 |
| `sw_export` | 导出副本：STEP、IGES、Parasolid、ACIS、STL、PDF、DXF/DWG、eDrawings、JPG/PNG/TIF 或原生格式。返回路径、大小和 SHA-256；bridge 在另一台机器上时会自动取回到 dsh 工作区。 |
| `sw_save` | 另存为允许的文件夹里的新文件（新文档就是这样得到文件的），或在 bridge 以 `--allow-save` 启动时覆盖源文件。 |
| `sw_close` | 关闭文档；只有这个 bridge 自己做的未保存修改才会被丢弃。 |
| `sw_view` | 渲染一张 PNG：一个标准视图，或最多六个视图拼在一张带标注的图上，着色、线框或消隐。之后恢复用户原来的视图。 |

## 它不会做什么

这些规则在 Windows 一侧、文件所在的地方执行。dsh 可以提出任何请求，最终由 bridge 决定。

- **不经允许修改你的文件。** 文档默认只读打开；`sw_edit`、`sw_build`、`sw_study` 和 `sw_assemble`
  只改内存；`sw_save` 把新文件写到允许的文件夹里，只有 bridge 以 `--allow-save` 启动时才覆盖源文件。
- **删掉你的工作。** `sw_build` 只删除它在本次 bridge 会话里建的特征，被这些特征吸收的你自己的草图会保留；
  你的特征只能压缩，不能删除。退回控制棒不在特征树末尾时它拒绝建模，因为新特征会落在你的特征中间。
  `sw_study` 会把每个值恢复原样并核对。
- **写到允许的文件夹之外。** 导出、另存的副本和 BOM 文件只能写到 `Documents\dsh-solidworks`，
  或你用 `--allow-write` 允许的文件夹。已存在的文件只有在明确要求时才会被替换。
- **丢掉别人的工作。** 只有当文档是这个 bridge 打开或新建的、且调用带了 `discard`，才会关闭有未保存修改的文档。
- **启动、关闭或点击 SOLIDWORKS。** 它连接你面前的那个 SOLIDWORKS，从不启动或退出它，也从不替你
  回答对话框，而是把对话框的文字告诉你，因为回答是你的决定。
- **执行代码。** 没有宏、脚本或“调用任意 API”的工具。它能做的每一次 SOLIDWORKS 调用都列在
  [`bridge/compat/members.tsv`](bridge/compat/members.tsv) 里。
- **把配对令牌放进模型上下文。** “未连接”的回答只说明配对包在哪里，从不包含令牌，所以对话记录里不会有它。

dsh 和另一台机器上的 bridge 之间的连接是双向认证的（双方在任何调用之前，用各自的新随机数做
HMAC-SHA256 证明自己持有令牌），但没有加密。它面向仅主机的虚拟机网络或本地网络；跨越其他网络时，
请通过 SSH 或 VPN 传输。详见 [SECURITY.md](SECURITY.md)。

## 工作原理

- **几何只有一个坐标空间。** 草图点、选取点、探测点、零部件原点和包围盒都是模型空间的毫米值。bridge 用草图
  自身的变换把每个草图点映射进去，所以面上的圆会出现在模型说的位置，不管 SOLIDWORKS 给那个面的草图取了什么轴。
  选取时会让每个面或边给出离目标最近的点，所以结果不依赖视角、缩放或前面挡着什么，也从不改动你的选择。
- **对照意图检查。** `sw_build` 返回零件的体积和包围盒，`sw_probe` 返回面积和距离，`sw_interference`
  返回重叠体积：都是模型可以拿来和它想做的东西对照的数字。做到一半失败的建模会删掉它做过的部分，所以零件要么建好，
  要么保持原样。
- **一个 exe 支持所有版本。** bridge 不引用任何 SOLIDWORKS interop 程序集，每次调用都按名字走
  `IDispatch`，所以驱动 2016 的同一个构建也能驱动 2019 及以后的版本，并且可以在 Linux CI 上构建。
  `dynamic` 和 PowerShell 的 COM 适配器在跨进程访问 SOLIDWORKS 对象时都会失败
  （`TYPE_E_ELEMENTNOTFOUND`，实测），`Type.InvokeMember` 不会。
- **2016 兼容性是检查出来的，不是假设的。** `bridge/compat/members.tsv` 列出 bridge 调用的
  200 个成员及其接口。如果 bridge 调用了未列出的成员、没有用字面量写成员名，或列出的成员在 SOLIDWORKS 2016 SP1
  interop 中不存在，测试就会失败。`SaveAs2`（2019 SP1+）、`Get6`（2018+）、`IEquationMgr.GlobalVariable`
  （2016 SP4+）就是这样被挡在外面的。
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

本页的每一条说法都在 2026-10-18 对真实 SOLIDWORKS 跑过。

| 内容 | 结果 |
| --- | --- |
| 端到端实测，SOLIDWORKS 2016 SP1（24.1.0） | 64 项检查全部通过，经由真实的主动连出链路，用的就是发布的 exe |
| 端到端实测，SOLIDWORKS 2019 SP0（27.0.0） | 64 项检查全部通过，同一个 exe |
| 真实 dsh 0.2.0-rc.2 + `deepseek-flash`，dsh 在 Linux、SOLIDWORKS 2019 在虚拟机（listen） | 即上面那次会话：11 次工具调用全部完成；一句话建出零件、核对体积、导出并取回工程图 |
| 真实 dsh 0.2.0-rc.2 + `deepseek-flash`，dsh 就在那台 Windows PC 上（spawn） | 在 0.1.0 上跑过，此后未改动：插件通过 dsh 的子进程服务启动自带 bridge；5 次工具调用全部完成；dsh 退出后没有残留 bridge 进程 |
| 发布的 `swbridge.exe` | CI 从源码重新构建，得到同一个 SHA-256（`bridge/swbridge.exe.sha256`） |
| 用生成的 `pair.cmd` 配对、登录自启、`uninstall` | 在 0.1.0 上跑过，此后未改动：无需管理员即装好；互斥锁下只有一个实例；卸载后不留痕迹 |
| 单元测试（Linux，无需 SOLIDWORKS） | 48，包括 2016 成员检查和双方契约检查 |
| `swbridge selftest`（Windows，无需 SOLIDWORKS） | 路径、单位、JSON、工程图布局和比例、图片留边、CSV，以及与插件共用的 HMAC 测试向量 |

端到端检查用工具没有自己声明过的东西来检验每个回答。建模类检查的标准答案都由所要求的几何算出：上面那块板必须是
6000·20 − π·10²·20 − 4·25·(1 − π/4)·20 mm³，顶面面积是它除以 20；50 mm 立方体抽壳 2 mm、一面开口必须是
50³ − 46²·48 = 23,432 mm³；内外径 10/20 mm、长 30 mm 的旋转管加 1 mm 倒角必须是 π(20² − 10²)·30 − π·(20 − 1/3)；
设计研究取 10 mm 和 40 mm 时必须是顶面面积乘以 10 和 40，结束后板厚回到 20；两块面对面配合的板不能干涉，第三块压进
两者各 10 mm 时，与每块的重叠必须正好是顶面面积乘以 10；BOM 必须数出 3 件；工程图必须有四个与图纸比例一致的视图，
并能导出为 `%PDF-`。归属类检查基于一个不是 bridge 建的草图（保存、关闭再打开，bridge 已不记得它）：在它上面建模失败、
以及删除在它上面建的特征，都必须让这个草图保留；第二个图元就失败的草图必须让特征树保持原样。0.1.0 的读取类检查也照常运行：样例 `box.sldprt` 在 `sw_edit` 把深度改成 150 mm 后必须是
325,500 mm³，取回的 STEP 必须与 bridge 的 SHA-256 一致，各种拒绝必须返回对应的错误码。2016 上的那次运行用的是一个
别人开着 58 个文档的装配体的 SOLIDWORKS；有一项检查确认他们的文档、修改标记、活动文档，以及他们装配体加载的 57 个零件都原样恢复。

经由链路在最后两次运行中实测，SOLIDWORKS 在 Windows 11 ARM64 上以 x64 仿真运行，虚拟机当时也在跑别的工作
（更早的运行最多快一倍）：五步建板 4.2-6.8 s，一次探测 0.5-1.6 s，四视图拼图 2.7-4.5 s，出一张工程图
3.4-4.2 s，四零件装配体干涉检查 1.5-3.0 s，打开零件 1.8-2.3 s，导出 STEP 0.5-0.7 s。

尚未验证：SOLIDWORKS 2020 及以后、Windows 10、x64 实机。欢迎反馈，先贴 `swbridge status` 的输出。

## 何时不适合用

- **需要放样、扫描、阵列、异型孔向导或钣金。** `sw_build` 只覆盖草图、拉伸、切除、旋转、圆角、倒角、抽壳和
  基准面。草图几何是精确的，但不带草图尺寸和几何关系；特征自身的尺寸（深度、半径、角度）有名字，可以驱动。
- **文件是 STEP 或 IGES。** `sw_open` 只打开 SOLIDWORKS 文档，请先在 SOLIDWORKS 里导入。
- **想在 SOLIDWORKS 窗口里对话。** 没有任务窗格，对话在 dsh 里。
- **要遍历五万个面。** 跨进程 COM 每次调用都有往返开销，一次探测要问遍点附近的每个面。工具返回摘要；
  重几何的工作适合插件（add-in）或导出的 STEP。
- **多台 SOLIDWORKS 机器同时接入一个 dsh。** 同一时间只有一个 bridge 持有连接。

## 开发

```
npm install && npm test              # 插件、契约和 2016 成员检查，无需 Windows
npm run build:bridge                 # 需要 .NET 8 SDK；为 net48 构建 bin/swbridge.exe
SW_LINK_TOKEN=... node tests/live/e2e.mjs   # 对一个连向本机的 bridge 做实测
```

`node scripts/format-contract.mjs` 让 `protocol/tools.json` 保持既定排版。MIT 许可，见 [LICENSE](LICENSE)。
