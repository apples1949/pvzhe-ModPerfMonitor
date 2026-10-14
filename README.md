# ModPerfMonitor「Mod 性能监测器」· 交付文档

> 2026-10-12 · 目标游戏构建：**0.30.0**（`DesktopNew\植物大战僵尸杂交版0.30\植物大战僵尸杂交重制版\`；
> 解包源码在 `...\植物大战僵尸杂交版0.30\unpack\`）
> 形态：纯托管运行时 Mod（`mod.json` + `Runtime/ModAssembly.dll`）
> Mod ID：`modperfmonitor` · 版本 **2.0.0** · 入口类 `ModPerfMonitorEntry`

---

## 0.1 v2.1.0（2026-10-12）新增：定位瓶颈必需的三组数据

> 起因：v2.0.0 那份真机日志能把症状量化死，却**定位不到瓶颈** ——
> 因为埋点只覆盖 C#、而实测埋点合计只占墙上时间 1~2%。
> v2.1.0 补的就是"剩下的 98% 在哪"所需的判据。

| 新增 | 输出 | 为什么需要 |
|---|---|---|
| **CPU 分项** | `CPU 分项(本窗口 N 帧): 脚本 process X.XXms + 物理 physics Y.YYms = Z.ZZms · 帧均 F.Fms ⇒ 脚本占 P% · 脚本之外 Q.Qms` | 引擎自报的脚本侧耗时 vs 帧时间 ⇒ **CPU/脚本受限 还是 渲染/GPU/等待受限**，这是第一个岔路口。数据源 `Performance.Monitor.TimeProcess / TimePhysicsProcess`（与游戏自带 [TDPerf] dump 头同一口径） |
| **GC / 分配** | `GC/分配(本窗口): gen0 +N · gen1 +N · gen2 +N · 托管分配 X MB（Y KB/帧）· 本窗口无 GC` | 判定那些 130~300ms 的尖峰**是不是 GC 抖动**。数据源 `GC.CollectionCount(0/1/2)` + `GC.GetTotalAllocatedBytes(false)` |
| **埋点覆盖率** | `埋点合计 X.XXms/s = 墙上时间 Y.Y% · 有数据 N/M 项` + 覆盖率 <10% 时的显式警告 | 把"这个工具有多瞎"直接写进报告。v2.0.0 的 Top10 看起来很大，但没人知道它只占帧预算的百分之几 |
| **上下文** | `上下文: 关卡 <名> · Mod 15 个: id,id,…` | 把某段数据对到"哪个关卡、装了哪些 Mod"。Mod 清单直接读 `user://Mods/enabled_mods.json`（**不走宿主 ModLoader API**，跨版本最稳） |

**同时修掉三个报告缺陷**：

1. **自身开销恒为「0 帧样本」**（根因）：面板刷新（0.5s）末尾把 `_selfTicks/_selfFrames/_selfMaxUs` 清零，
   而报告由 1Hz 的 `Evaluate` 触发 —— 1s 是 0.5s 的整数倍，两者**同帧**时面板先把计数清零、
   紧接着出报告 ⇒ 读到 0 帧。已在 `RefreshPanel()` 里**删掉那次重置**，自身开销窗口改由"报告发出"翻页。
2. **「尖峰 Top」不是 Top**：旧实现从环形缓冲往回取 3 条 = **最近 3 次**，标签却写 Top
   （真机日志里出现过 `Top 729ms… 151ms… 1771ms…` —— 会话最大排在最后）。改为真排序取最大 3 条。
3. **ms/s 只有 2 位小数** ⇒ 凡 <0.005ms/s 的作用域全被印成 `0.00ms/s`（非战斗场景整列贴地板）。
   改为 3 位小数，并**补一项"单次 µs"**，用于区分"调用极多每次极便宜"（`registry.rectQuery` 300/s×1.3µs）
   与"调用很少每次极贵"（`adobeAnimate.render.dispatch` 2.7/s×12ms）。

**真机实测后追加的两处修正**：

- **CPU 分项必须跳过长帧**：首测打出「脚本占 425.5%」—— 启动那 20 秒里单帧 `_process` 可能长达数秒，
  几帧就把均值带飞。现在与帧时间环形缓冲**共用同一个 `LongFrameSkipMs` 闸门**，分子分母口径一致。
- **占比 >100% 时不再输出百分比**，改为打印原始读数 + 说明（引擎该监视器是**平滑量**，
  会话前段仍受启动期长帧影响，需在稳定战斗场景复核）；并加了**分配计数器自检**
  （分配已知 4MB 看计数器涨多少 ⇒ 报告里的分配数字是否可信当场自证）。

---

## 0. v2.0.0 形态：只做「函数耗时 + FPS」

按要求收敛：

| 输出 | 内容 |
|---|---|
| **面板**（任何界面右上角常驻，不挡点击） | **只显示「函数耗时」排行**：Top12，每 0.5s 刷新；F7 显隐面板，F8 切换排序（耗时 ms/s → 调用 次/s → 单次 µs） |
| **日志**（godot.log，前缀 `[ModPerf]`） | **只记录函数耗时和 FPS**：触发报告（启动基线/战斗开始/战斗结束/低帧/尖峰/退出）+ 每 60s 一行；同时落盘 `user://PVZHE_Logs/ModPerf/` |

**已移除**（v1.x 有、v2.0.0 按要求删除）：Mod 占用清单 / 注册面统计 / 场上内容归属 / 内存与 GC / 钩子耗时 /
[TDPerf] 汇总段 / F6 分区开关 —— 代码里相关 API 引用（ModLoader、内容目录等）已一并删净。

## 1. 数据从哪来

**两类数据源**：
① 函数耗时 = 游戏内置 `TowerDefensePerfProfiler`；② CPU 分项/GC = Godot `Performance` + .NET `GC`（见 §0.1）。

游戏内置 `TowerDefensePerfProfiler`（`[TDPerf]`，源码里 **310 个唯一作用域名**，README 旧文写"400+"偏乐观，如
`adobeAnimate.render.crowd.collect`、`projectile.updateManager`）。
本 Mod 启动时**静默开启**它，并把自动 dump 间隔压成 10 亿帧 ⇒ **不会往日志刷 `[TDPerf]`**；
随后反射读其私有 `Metrics` 表（`Metric.Ticks / Count`），按刷新周期差分换算成：

- **ms/s**：该函数每秒累计耗时（占 CPU 的程度）
- **次/s**：每秒调用次数
- **单次 µs**：平均每次耗时

玩家按 **9** 仍可手动关掉剖析器（面板会显示"分析器未开"）。

## 2. 面板长这样

```
┌────────────────────────────────────────┐
│ 函数耗时 Top12 · 排序:耗时 ms/s（F8 换 / F7 隐藏） │
│ 1. adobeAnimate.render.crowd.collect  3.42ms/s·23.8µs×144/s │
│ 2. projectile.updateManager           1.08ms/s·18.0µs×60/s  │
│ …（Top 12，绿色耗时条）                    │
└────────────────────────────────────────┘
```

## 3. 日志长这样

**触发报告**（启动基线约 20s / 战斗开始 / 战斗结束 / 低帧<30FPS / 尖峰>120ms×3 / 退出）：

**v2.1.0 实机格式（2026-10-12 真机抓取，逐行都经过核对）**：

```
[ModPerf] ════ 报告 · 启动基线 ════
[ModPerf] 时间 20:47:14 · 会话 20s · 场景 非战斗
[ModPerf] 上下文: 关卡 （未识别） · Mod 15 个: communitylevelsave,…,timestop,modperfmonitor
[ModPerf] FPS 现58.0 · 5s 均58.1 · 60s 均57.9
[ModPerf] CPU 分项(本窗口 1180 帧，另跳过 1 个长帧): 脚本 process 48.66ms + 物理 physics 0.39ms
          = 49.05ms · 帧均 16.9ms ⇒ 脚本占 19.4% · 脚本之外 12.8ms
[ModPerf] GC/分配(本窗口): gen0 +3 · gen1 +0 · gen2 +0 · 托管分配 1.20 MB（61.4 KB/帧）
[ModPerf] 帧时间 60s/1170帧: 均17.3 中位17.0 p95 46.0 p99 57.0 max 106.0ms
[ModPerf] 尖峰(>120ms): 本会话 0 次 · 近60s 0 次 · 近10s 0 次 · Top(最大) 无
[ModPerf] 函数耗时 Top1（ms/s · 次/s · 单次；排序:耗时 ms/s）:
[ModPerf]   埋点合计 0.019ms/s = 墙上时间 0.00% · 有数据 1/31 项（…）
[ModPerf]   ⚠ 埋点覆盖面很低：本次帧时间主要不在这些埋点里，请对照上面的「CPU 分项」判断是脚本还是渲染/GPU。
[ModPerf]   01. projectile.updateManager  0.019ms/s·57.5/s·0.3µs/次
[ModPerf] 本监测器自身开销: 均 46.67µs/帧 · 峰值 16141.9µs（1170 帧样本）
[ModPerf] ════ 报告结束 ════
```

⚠️ 首份「启动基线」里的 CPU 分项可能仍报 >100%（引擎该监视器是平滑量，被启动期长帧抬着）；
报告会**打印原始读数并提示到稳定战斗场景复核**，不会给一个会误导人的百分比。
分配数字若自检未过，行尾会标 `★ 计数器自检未过，分配数字不可信`。

**周期记录**（每 60s 一行）：

```
[ModPerf] 记录 60s: FPS 均58.9 现59 | 帧 均16.9 p95 17.9 max 33.4ms | 尖峰(>120ms)×0 | 函数Top5: crowd.collect=3.42ms/s updateManager=1.08ms/s …
```

## 4. 开关总表（`runtime_src/ModPerfMonitorEntry.cs` 顶部）

| 开关 | 默认 | 作用 |
|---|---|---|
| `EnablePanel` | true | 常驻函数耗时面板 |
| `EnablePanelHotkeys` | true | F7 显隐 / F8 换排序 |
| `EnableLogReports` | true | 日志记录（函数耗时 + FPS；关掉则只留面板） |
| `EnableFunctionProfiler` | true | 静默开启游戏内置剖析器（关掉 = 零侵入但无数据） |
| `FunctionProfilerDetailed` | false | 深路径明细（逐精灵渲染路径打点，开销明显增大） |
| `EnableCpuGcStats` | true | ★ v2.1.0：CPU 分项（process/physics）+ GC/分配 + 分配计数器自检 |
| `EnableSceneModContext` | true | ★ v2.1.0：报告/周期行里附带关卡名 + 已启用 Mod 清单 |
| `EnableAutoTriggers` / `EnablePeriodic` | true | （仅日志开时）触发报告 / 60s 记录 |
| `DiagVerbose` | false | 诊断日志 |

阈值：低帧 30FPS/5s、尖峰 120ms、尖峰频发 3 次/10s、限流 30s、面板刷新 0.5s、面板 Top12、报告 Top10、周期 Top5。

## 5. 验收与指纹（2026-10-12）

| 版本 | 指纹 |
|---|---|
| **v2.1.0（当前装机）** | DLL md5 **`124bd45cff7a129664953fcc4c8cf05f`**；pmod **18 526 B**；已实测 `package applied: modperfmonitor; diagnostics=0` |
| v2.0.0 | DLL 28 672 B md5 `296b911306a12501002d873149428541`；pmod 14 393 B |
| v1.2.1 | DLL md5 `3aac3885241757d5d504e26af6f870e7`；pmod 22 883 B |
| v1.2.0 | DLL md5 `452d6e9877983ab1fff3590ecf53ff87`；pmod 22 845 B |
| v1.1.0 | DLL md5 `7adc5e873635116a8042a6ad15d206d6`；pmod 20 923 B |
| v1.0.0 | DLL md5 `ba38ee0c35e6c918a92fc5af0ba35f9c`；pmod 16 320 B |

- 编译 0 警告 0 错误；离线入口探针 `_probe/` **20/20 全绿**；装机包三方 md5 一致
- v2.0.0 已确认旧代码删净（DLL 里不再有 `CollectRankData` / `AppendModInventory` / `CollectHookDeltas`）

## 6. 已知边界

1. **剖析器开销**：开启后每个埋点多一次取时+累加（数百次/帧），会带来轻微开销（面板把自身开销也报了）。
   想零侵入：`EnableFunctionProfiler=false` 重编（代价：无函数数据）。
2. **与「按 9」的关系**：本 Mod 启动即已开启 ⇒ 玩家按 9 的**第一次是关掉**；面板/日志会显示"分析器未开"。
3. 口径是"埋点作用域"（不是逐 CLR 方法）：默认级≈系统/子系统级；`FunctionProfilerDetailed=true` 才覆盖逐精灵渲染路径。
4. F7/F8 与 Mod 编辑器脚本编辑器内部快捷键可能重叠（编辑器内才有）；普通游戏无冲突。
5. ★ **CPU 分项的引擎读数未在本构建验证单位**：真机首测 `TimeProcess` 原始值 ×1000 后大于帧时间
   （物理上不可能），报告因此只在占比 ≤100% 时给百分比，否则给原始读数 + 提示。
   用法建议：**在稳定战斗场景看这一行是否随帧时间同步变化**，不要跨设备比绝对值。
6. ★ **GC/分配的窗口代表性**：窗口 = "自上次报告以来"，若窗口里含**退出/切场景**（装载与卸载阶段），
   分配量会显著偏高（实测退出窗口 2.3~20 MB/帧）。判读时优先看 **60s 周期行**里的分配数字。
7. `埋点合计` 是"全部埋点求和"，**嵌套作用域会重复计**（如 `dispatch` ⊃ `prepareGraphs`）；
   该行只用于回答"埋点覆盖了帧预算的多少"，不是互斥预算。

## 7. 进游戏排查顺序

```
1) logs\godot.log 搜 "[ModPerf]"：应有
   [ModPerf] 初始化完成（v2.0.0：面板=函数耗时排行；日志=函数耗时+FPS；日志开关=开）。
   [ModPerf] 已挂载 process_frame。面板=开(F7显隐/F8排序) · 函数剖析=开(默认级) · 日志=开(函数耗时+FPS)。
   随后约 20s 出「启动基线」报告；每 60s 一行记录
2) 右上角面板：F8 切排序（标题里"排序:"会变）；F7 隐藏/显示
3) 记录文件：%APPDATA%\Godot\app_userdata\植物大战僵尸杂交版\PVZHE_Logs\ModPerf\modperf_<时间>.log
4) 重编/装机后路（dotnet 卡收尾时）：cp obj\Release\*.dll bin\Release\ 后 python build_mod.py --package-only --install
```

## 8. 工程内文件

```
ModPerfMonitor/（工程根）
  mod.json                    # 清单（id/entry/resources）
  build_mod.py                # 编译→打包→装机（--install / --package-only / --install-only）
  runtime_src/
    ModPerfMonitor.csproj     # net9.0，GodotRefDir 指向 0.30 构建
    ModPerfMonitorEntry.cs    # 入口本体（面板 + 函数耗时 + FPS 采样 + 日志，约 1500 行）
  _probe/                     # 离线入口探针（独立小工程；dotnet exec 运行）
  dist/ModPerfMonitor.pmod    # 成品
  Runtime/ModAssembly.dll     # 打包用（脚本自动刷新）
```
