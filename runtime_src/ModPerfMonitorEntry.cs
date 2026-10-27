using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「Mod 性能监测器」托管运行时入口（mod id: modperfmonitor）。
///
/// ── v2.0.0 形态（当前：按要求收敛为「函数耗时 + FPS」）─────────────────
///  ★ 面板（任何界面右上角常驻，CanvasLayer 层 90，MouseFilter=Ignore 不挡点击）
///    只显示 **函数耗时排行**：Top12，每 0.5s 刷新。
///    · F7 显示/隐藏面板；F8 循环切换排序：耗时 ms/s → 调用 次/s → 单次 µs
///  ★ 日志（godot.log，前缀 [ModPerf]）**只记录函数耗时和 FPS**：
///    · 触发点：启动基线(约20s) / 战斗开始 / 战斗结束 / 持续低帧(5s<30FPS) /
///      尖峰频发(10s 内>120ms 达3次) / 退出 —— 出「FPS 概览 + 函数耗时 Top10」完整报告
///    · 周期：每 60s 一行——FPS + 函数耗时 Top5
///    · 完整报告同时落盘 user://PVZHE_Logs/ModPerf/modperf_<时间>.log
///  （历史版本 v1.x 的"Mod 占用清单 / 注册面 / 场上内容归属 / 内存 GC / 钩子耗时 / [TDPerf] 汇总"
///    等分区已按要求全部移除，本文件不再引用 ModLoader / 内容目录等 API。）
///
/// ── 函数耗时从哪来 ─────────────────────────────────────────────────
///  游戏内置 [TDPerf] 分析器（TowerDefensePerfProfiler，400+ 个函数/作用域埋点，
///  如 adobeAnimate.render.crowd.collect、projectile.updateManager 等）。
///  本 Mod 启动时**静默开启**它（DumpIntervalFrames 设成超大值 ⇒ 不会打印 [TDPerf] 日志），
///  面板/日志通过反射读其私有静态 Metrics 表（Metric.Ticks/Count），
///  按刷新周期差分 → ms/s（耗时速率）与 次/s（调用速率）。
///  · 玩家按 9 仍可手动关掉剖析器（面板/日志会显示"分析器未开"提示）。
///  · FunctionProfilerDetailed=true 可开启"深路径明细"（逐精灵渲染路径打点，开销更大）。
///
/// ── 铁律（沿用本工坊已定案）────────────────────────────────────────
///  · 手写 csproj 无 Godot 源码生成器 ⇒ 不写自定义 Node 子类回调；
///    面板全部用引擎内置控件；逻辑走 SceneTree.Connect("process_frame", ...) 信号通道。
///  · Initialize / OnAllModsLoaded / Shutdown 一律 try/catch 吞异常（抛出=整包回滚）。
///  · 每帧只做"记账"（几次 ticks 读取 + 数组写），面板/报告重活按周期做。
///  · 反射取分析器私有表失败时静默降级（面板显示"采样中…"），不影响游戏。
/// </summary>
public sealed class ModPerfMonitorEntry : IXWModRuntimeEntry
{
	private const string P = "[ModPerf] ";

	// ================================================================ 行为开关（直接改这里重编译即可）

	/// <summary>日志记录（FPS + 函数耗时；触发报告 + 周期摘要 + 落盘）。</summary>
	internal static readonly bool EnableLogReports = false;   // ★ 用户要求：日志默认关闭（原 true）

	/// <summary>常驻面板（任何界面显示函数耗时排行）。</summary>
	internal static readonly bool EnablePanel = true;

	/// <summary>
	/// ★ v2.2.1（用户实测反馈："没设置启动打开面板，打开游戏还是显示"）：
	/// **面板可见性的唯一真源** —— 从 `showPanel` 配置读入（默认开）。
	///
	/// 之前只加了配置项、却从没在启动时读它 ⇒ 面板可见性仍由编译期常量决定，
	/// 配置怎么改都没用 ✗。现在：启动读一次、配置变更再读、每帧对齐一次。
	/// </summary>
	private static bool _panelWanted = true;
	/// <summary>★ v2.2.1：showPanel 的读取/订阅是否已做过。</summary>
	private static bool _panelCfgHooked;

	/// <summary>面板热键：F7 显隐面板、F8 切换函数排序依据。</summary>
	internal static readonly bool EnablePanelHotkeys = true;

	/// <summary>
	/// 由本 Mod **静默开启**游戏内置 [TDPerf] 分析器（函数耗时数据源）；
	/// DumpIntervalFrames 会设成超大值 ⇒ 不会打印 [TDPerf] 日志。
	/// </summary>
	internal static readonly bool EnableFunctionProfiler = true;

	/// <summary>深路径明细（DetailedHotPathMetrics）：逐精灵渲染路径打点，数据更细但开销明显增大。</summary>
	internal static readonly bool FunctionProfilerDetailed = false;

	/// <summary>【仅在 EnableLogReports=true 时生效】自动触发（启动基线 / 战斗开始结束 / 低帧 / 尖峰频发）。</summary>
	internal static readonly bool EnableAutoTriggers = true;

	/// <summary>【仅在 EnableLogReports=true 时生效】每 60s 一行记录（FPS + 函数耗时 Top5）。</summary>
	internal static readonly bool EnablePeriodic = true;

	/// <summary>诊断日志（排查用；正常运行关闭）。</summary>
	internal static readonly bool DiagVerbose = false;

	/// <summary>
	/// ★ v2.1.2（用户要求）：**面板里显示帧率**。
	/// 与函数耗时排行并排 —— 排查掉帧时"FPS/当帧帧时间"必须和"哪个函数在吃时间"同屏，
	/// 否则要来回翻日志。按 250ms 刷新（比函数排行 500ms 更"活"），并按帧率分档着色：
	/// ≥50 绿 / ≥30 黄 / 更低 红。
	/// </summary>
	internal static readonly bool EnablePanelFps = true;

	/// <summary>
	/// ★ v2.1.0：CPU 分项（process/physics 毫秒）+ GC/分配采样。
	///
	/// 为什么必须加：v2.0.0 的日志只能证明"埋点逻辑只占墙上时间 1~2%"，
	/// 但**无法回答"那 98% 是脚本还是渲染/GPU"**——而这是定位瓶颈的第一个岔路口。
	/// process/physics 是引擎自报的脚本侧耗时；与帧时间一比即可分流：
	///   · process+physics 占帧时间比例高 ⇒ CPU/脚本受限，继续挖 C#；
	///   · 占比很低 ⇒ 帧时间花在渲染/提交/GPU/等待上，埋点再多也挖不出来。
	/// GC 计数则用来判定"那些 130~300ms 的尖峰是不是 GC 抖动"。
	/// </summary>
	internal static readonly bool EnableCpuGcStats = true;

	/// <summary>★ v2.1.0：报告/周期行里附带"当前关卡名 + 已启用 Mod 清单"。</summary>
	internal static readonly bool EnableSceneModContext = true;

	// ================================================================ 阈值 / 常量

	private const double BootReportDelaySec = 20.0;   // 启动基线报告延迟
	private const double PeriodicSec = 60.0;          // 周期记录间隔
	private const double LowFpsWindowSec = 5.0;       // 低帧判定窗口
	private const double LowFpsThreshold = 30.0;      // 低帧阈值（5s 均 FPS）
	private const int LowFpsMinSamples = 20;          // 低帧判定最少样本
	private const double SpikeMs = 120.0;             // 单帧尖峰阈值（ms）
	private const double SpikeBurstWindowSec = 10.0;  // 尖峰频发判定窗口
	private const int SpikeBurstCount = 3;            // 窗口内尖峰次数达此值 → 触发
	private const double TriggerCooldownSec = 30.0;   // 同类触发限流
	private const double ReportMinGapSec = 20.0;      // 两份完整报告最小间隔（启动基线去重用）
	private const double LongFrameSkipMs = 2000.0;    // 超过视为暂停/加载，不计入样本
	private const int RingSize = 8192;                // 帧环形缓冲（≈136s@60fps）
	private const int SpikeLogSize = 24;              // 尖峰明细环形缓冲
	private const ulong NoTs = 0UL;

	// ── 面板参数 ──
	private const double PanelRefreshSec = 0.5;       // 面板刷新周期
	private const int FuncTopN = 12;                  // 面板显示条数
	private const int ReportFuncTopN = 10;            // 报告里函数明细条数
	private const int PeriodicFuncTopN = 5;           // 周期行里的函数条数
	private const int PanelLayerId = 90;              // CanvasLayer 层级（游戏战斗 UI 用 1/3，Mod HUD 用 5）
	private const float PanelMarginPx = 12f;          // 距屏幕边距
	private const float PanelBarMaxPx = 64f;          // 耗时条最大宽度

	/// <summary>面板热键（F3 被 Mod 编辑器占用、9 被内建分析器占用；F7/F8 普通游戏无冲突）。</summary>
	private const Key PanelToggleKey = Key.F7;





	private const Key PanelSortKey = Key.F8;

	/// <summary>分析器自动 dump 的超大帧间隔（≈永不触发）⇒ 只取数不打印 [TDPerf]。</summary>
	private const int ProfilerDumpIntervalFrames = 1000000000;

	/// <summary>函数排序依据名称（F8 循环切换）。</summary>
	private static readonly string[] FuncSortNames = { "耗时 ms/s", "调用 次/s", "单次 µs" };

	// ================================================================ 状态

	private SceneTree _tree;

	/// <summary>★ v2.2.0：运行上下文缓存（读/写 showPanel 设置用）。</summary>
	internal static XWModRuntimeContext _ctxRef;
	private Callable _tick;
	private bool _started;
	private int _faultCode;

	private ulong _startMs;
	private DateTime _wallBase = DateTime.Now;
	private ulong _lastFrameMs;
	private ulong _lastEvalMs;
	private ulong _lastFullReportMs;
	private ulong _lastLowFpsReportMs;
	private ulong _lastSpikeReportMs;
	private ulong _lastPeriodicMs;
	private bool _bootReported;
	private bool _inBattle;
	private ulong _battleStartMs;
	private long _skippedLongFrames;
	private long _spikeTotal;

	// 会话 / 本局 FPS 累计（每帧几次数值累加，零分配）
	private long _sessFrames;
	private double _sessSumMs;
	private double _sessMaxMs;
	private long _battleFrames;
	private double _battleSumMs;
	private double _battleMaxMs;

	// 帧环形缓冲
	private readonly ulong[] _fTs = new ulong[RingSize];
	private readonly float[] _fDt = new float[RingSize];
	private int _fHead;
	private int _fCount;

	private struct SpikeRec
	{
		public ulong Ts;
		public float Ms;
		public byte Scene;      // 0=非战斗 1=战斗
	}

	private readonly SpikeRec[] _spikes = new SpikeRec[SpikeLogSize];
	private int _spikeHead;
	private int _spikeCount;

	// 自身开销
	private long _selfTicks;
	private long _selfFrames;
	private double _selfMaxUs;

	// ── ★ v2.1.0：CPU 分项（process/physics）窗口累计 ──
	//   不做瞬时采样、而是按窗口累加求均：瞬时值受单帧抖动影响极大（尤其 10FPS 时），
	//   均值才能和"窗口帧均"直接对比。
	private double _procSumMs;
	private double _physSumMs;
	private double _sysFrameSumMs;
	private long _sysFrames;
	/// <summary>窗口内被跳过的长帧数（加载/暂停；不参与 CPU 分项均值，仅作说明）。</summary>
	private long _sysSkippedFrames;

	// ── ★ v2.1.0：GC / 分配（窗口结算，报告里报增量）──
	private long _gc0Base;
	private long _gc1Base;
	private long _gc2Base;
	private long _allocBase;
	private long _sysBaseFrames;
	private ulong _sysBaseMs;
	private bool _sysBaseSet;
	/// <summary>本会话是否已经建立过 GC 基线（首次报告无增量，需说明而不是显示 0）。</summary>
	private bool _sysHadBaseline;
	private long _gc0Delta;
	private long _gc1Delta;
	private long _gc2Delta;
	private double _allocKbDelta;
	private double _allocKbPerFrame;

	// ── ★ v2.1.0：埋点覆盖率（CollectFunctionMetrics 结算；报告里报"埋点合计占墙上时间"）──
	private double _funcTotalMsPerSec;
	private int _funcActiveCount;
	private int _funcRegisteredCount;

	// ── ★ v2.1.0：关卡名 / Mod 清单（低频缓存，别每次报告都读盘）──
	private string _mapNameCache = "";
	private ulong _mapNameMs;
	private string _modListCache = "";
	private ulong _modListMs;

	// 热键边沿（F7/F8，事件通道 + 轮询兜底去重）
	private bool _f7PollPrev;
	private bool _f8PollPrev;
	private ulong _f7LastMs;
	private ulong _f8LastMs;

	// ── 面板 ──
	private CanvasLayer _panelLayer;
	private PanelContainer _panel;
	private VBoxContainer _panelBox;
	private Label _panelTitle;
	private readonly List<HBoxContainer> _funcRowBoxes = new List<HBoxContainer>(FuncTopN);
	private readonly List<Label> _funcNames = new List<Label>(FuncTopN);
	private readonly List<ColorRect> _funcBars = new List<ColorRect>(FuncTopN);
	private readonly List<Label> _funcVals = new List<Label>(FuncTopN);
	private int _funcSortMode;
	private ulong _lastPanelMs;
	private Theme _gameTheme;

	/// <summary>函数耗时一行。</summary>
	private struct FuncRank
	{
		public string Name;
		public double MsPerSec;     // 最近窗口内累计耗时 ÷ 秒
		public double CallsPerSec;  // 最近窗口内调用次数 ÷ 秒
	}

	private readonly List<FuncRank> _funcBuf = new List<FuncRank>(96);
	private readonly List<FuncRank> _funcSorted = new List<FuncRank>(96);
	private readonly Dictionary<string, double> _funcPrevMs = new Dictionary<string, double>(StringComparer.Ordinal);
	private readonly Dictionary<string, long> _funcPrevCalls = new Dictionary<string, long>(StringComparer.Ordinal);
	private ulong _lastFuncSampleMs;

	private static bool _profProbed;
	private static FieldInfo _profMetricsField;
	private static Type _profMetricType;
	private static FieldInfo _profTicksField;
	private static FieldInfo _profCountField;

	/// <summary>★ v2.1.1：面板隐藏时的"多久查一次可见性"计时（避免隐藏后每帧白跑刷新）。</summary>
	private ulong _panelHiddenCheckedMs;

	/// <summary>★ v2.1.2：面板里的帧率行 + 上次刷新时刻。</summary>
	private Label _panelFps;
	private ulong _lastFpsLabelMs;

	private string _reportFile;
	private bool _reportFileTried;

	private readonly StringBuilder _sb = new StringBuilder(4096);
	private readonly List<float> _scratch = new List<float>(RingSize);

	// ================================================================ 生命周期

	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			_ctxRef = context;   // ★ v2.2.0：配置页按钮要读写 showPanel
			// ★ v2.2.1：启动时读「打开游戏时显示面板」，并订阅配置变更（宿主 applyMode=immediate 会推过来）
			ReadPanelWanted();
			try
			{
				if (!_panelCfgHooked && context?.Settings != null)
				{
					context.Settings.Changed += OnPanelCfgChanged;
					_panelCfgHooked = true;
				}
			}
			catch { }
			Log("初始化完成（v2.2.0：面板=函数耗时排行；日志=函数耗时 + FPS + CPU 分项(process/physics) + GC/分配 + 埋点覆盖率 + 关卡/Mod 上下文；日志开关=" + (EnableLogReports ? "开" : "关") + "）。");
		}
		catch (Exception ex)
		{
			TryErr("Initialize 异常（已吞）：" + ex.Message);
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started)
			{
				return;
			}
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null)
			{
				Log("拿不到 SceneTree，本 Mod 不生效。");
				return;
			}
			_wallBase = DateTime.Now;
			_startMs = Time.GetTicksMsec();
			_lastFrameMs = NoTs;
			_lastEvalMs = _startMs;
			_lastPeriodicMs = _startMs;
			_tick = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tick);
			HookWindowInput();
			ApplyFunctionProfilerSettings();
			if (EnablePanel)
			{
				EnsurePanel();
			}
			if (EnableLogReports)
			{
				InitReportFile();
				AppendFileHeader();
			}
			_started = true;
			// 只留这一条启动自检行（不是"报告"）：确认加载成功 + 开关状态
			Log("已挂载 process_frame。面板=" + (EnablePanel ? "开(F7显隐/F8排序)" : "关")
				+ " · 函数剖析=" + (EnableFunctionProfiler ? ("开" + (FunctionProfilerDetailed ? "(深)" : "(默认级)")) : "关")
				+ " · 日志=" + (EnableLogReports ? "开(函数耗时+FPS)" : "关") + "。");
			// ★ v2.1.0：分配计数器自检（结论级数字必须先自证可信）
			if (EnableCpuGcStats)
			{
				SelfTestAllocCounter();
			}
		}
		catch (Exception ex)
		{
			TryErr("OnAllModsLoaded 异常（已吞）：" + ex.Message);
		}
	}

	public void Shutdown()
	{
		try
		{
			if (_started)
			{
				EmitFullReport("退出");
			}
			if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tick);
			}
		}
		catch (Exception ex)
		{
			TryErr("Shutdown 异常（已吞）：" + ex.Message);
		}
		finally
		{
			UnhookWindowInput();
			try
			{
				if (_panelLayer != null && GodotObject.IsInstanceValid(_panelLayer))
				{
					_panelLayer.QueueFree();   // 允许运行时卸载：面板不能漏
				}
			}
			catch { }
			_panelLayer = null;
			_panel = null;
			_panelBox = null;
			_started = false;
		}
	}

	// ================================================================ 每帧驱动（信号通道）

	private void OnFrame()
	{
		// ★ v2.2.1：配置页「关于」栏收款码（共享组件 SponsorQrInjector.cs；内部自带 30 帧节流 +
		//   插好后停止全树查找，摊到每帧 ≈ 几微秒）。modName 必须与 mod.json 的 name 逐字一致 ——
		//   注入器靠「该富文本是否以本 Mod 名开头」判定归属，不匹配就一个字都不会插。
		try { SponsorQrInjector.Tick(Engine.GetMainLoop() as SceneTree, "Mod 性能监测器", _ctxRef?.PackageRoot ?? "", "Resources/SponsorQR.png"); } catch { }

		long t0 = Stopwatch.GetTimestamp();
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			{
				return;
			}

			// ── 帧记账（FPS/帧时间/尖峰）────────────────────────────
			ulong now = Time.GetTicksMsec();
			bool normalFrame = false;
			if (_lastFrameMs != NoTs)
			{
				double dtMs = (double)(now - _lastFrameMs);
				if (dtMs < LongFrameSkipMs)
				{
					normalFrame = true;
					PushFrame(now, (float)dtMs);
					if (dtMs >= SpikeMs)
					{
						RegisterSpike(now, (float)dtMs);
					}
				}
				else
				{
					_skippedLongFrames++;   // 暂停/加载/切场景造成的长间隔：不污染样本
				}
			}
			_lastFrameMs = now;

			// ── ★ v2.1.0：CPU 分项窗口累计（引擎自报 process/physics）────
			//   与帧时间环形缓冲**共用同一个"正常帧"判据**：否则启动期几帧几秒的
			//   加载帧会把均值带飞（实测打出「脚本占 425.5%」，见 SampleCpuSplit 注释）。
			if (EnableCpuGcStats)
			{
				SampleCpuSplit((float)((_fCount > 0) ? _fDt[(_fHead - 1 + RingSize) % RingSize] : 0f), normalFrame);
			}

			// ── ★ v2.2.1：把面板可见性对齐到配置（F7/配置页/配置项三条路最终都归到 _panelWanted）──
			try
			{
				if (_panelLayer != null && GodotObject.IsInstanceValid(_panelLayer) && _panelLayer.Visible != _panelWanted)
				{
					_panelLayer.Visible = _panelWanted;
				}
			}
			catch { }

			// ★ v2.2.3：配置页**不再注入任何控件** —— 见 TickConfigToggleButton 的说明。

			// ── ★ v2.1.2：帧率行刷新（250ms；面板不可见时不做）─────
			if (EnablePanel && EnablePanelFps && now - _lastFpsLabelMs >= 250UL
				&& _panelLayer != null && GodotObject.IsInstanceValid(_panelLayer) && _panelLayer.Visible)
			{
				_lastFpsLabelMs = now;
				RefreshFpsLabel(now);
			}

			// ── 热键（事件通道见 OnWindowInput；这里是轮询兜底）─────
			if (EnablePanelHotkeys)
			{
				PollPanelKeyFallback();
			}

			// ── 面板刷新（0.5s）────────────────────────────────────
			// ★ v2.1.1：**面板不可见就不刷新**。单次刷新要反射读 326 个指标（每个都装箱），
			//   安卓实测单帧 4ms+（PC 才 0.1ms）；玩家按 F7 收起面板后这些活全是白干。
			//   隐藏时改为 1s 才查一次可见性，避免每帧白跑。
			if (EnablePanel && now - _lastPanelMs >= (ulong)(PanelRefreshSec * 1000.0))
			{
				_lastPanelMs = now;
				bool panelVisible = _panelLayer != null && GodotObject.IsInstanceValid(_panelLayer) && _panelLayer.Visible;
				if (panelVisible || _panelHiddenCheckedMs == NoTs || now - _panelHiddenCheckedMs >= 1000UL)
				{
					_panelHiddenCheckedMs = panelVisible ? NoTs : now;
					RefreshPanel(now);
				}
			}

			// ── 低频判定（≈1Hz）────────────────────────────────────
			if (now - _lastEvalMs >= 1000UL)
			{
				_lastEvalMs = now;
				Evaluate(now);
			}
		}
		catch (Exception ex)
		{
			if (_faultCode < 100)
			{
				_faultCode = 100;
				Log("每帧驱动异常（本条只报一次）：" + ex.Message);
			}
		}
		finally
		{
			long spent = Stopwatch.GetTimestamp() - t0;
			_selfTicks += spent;
			_selfFrames++;
			double us = spent * 1000000.0 / Stopwatch.Frequency;
			if (us > _selfMaxUs)
			{
				_selfMaxUs = us;
			}
		}
	}

	private void PushFrame(ulong now, float dt)
	{
		_fTs[_fHead] = now;
		_fDt[_fHead] = dt;
		_fHead = (_fHead + 1 == RingSize) ? 0 : _fHead + 1;
		if (_fCount < RingSize)
		{
			_fCount++;
		}
		_sessFrames++;
		_sessSumMs += dt;
		if (dt > _sessMaxMs)
		{
			_sessMaxMs = dt;
		}
		if (_inBattle)
		{
			_battleFrames++;
			_battleSumMs += dt;
			if (dt > _battleMaxMs)
			{
				_battleMaxMs = dt;
			}
		}
	}

	private void RegisterSpike(ulong now, float ms)
	{
		_spikeTotal++;
		SpikeRec r;
		r.Ts = now;
		r.Ms = ms;
		r.Scene = _inBattle ? (byte)1 : (byte)0;
		_spikes[_spikeHead] = r;
		_spikeHead = (_spikeHead + 1 == SpikeLogSize) ? 0 : _spikeHead + 1;
		if (_spikeCount < SpikeLogSize)
		{
			_spikeCount++;
		}
	}

	// ================================================================ ★ v2.1.0 CPU 分项 / GC 结算

	/// <summary>
	/// 每帧累计引擎自报的脚本侧耗时（process / physics，单位秒 → 毫秒）。
	///
	/// `Performance.Monitor.TimeProcess` = 本帧脚本 `_process` 总耗时；
	/// `TimePhysicsProcess` = 本帧物理步总耗时。两者都**不含**渲染/提交/GPU/等待，
	/// 所以 `(process+physics) / 帧时间` 就是"这一帧有多少是 CPU 脚本干的"。
	/// 这里同时累计帧时间，报告时直接给出占比——不用读者自己算。
	///
	/// ★★ v2.1.0 实测修正（真机启动基线打出「脚本占 425.5%」后定位）：
	///   **必须跳过长帧**。启动那 20 秒里包含资源加载 / 着色器编译，单帧 `_process`
	///   可能长达数秒；把这种帧算进均值 ⇒ 分子被几帧几秒的样本彻底带飞
	///   （实测 1171 帧里几帧就贡献了 85 秒的"process 时间"，于是均值 73ms &gt; 帧均 17.3ms，
	///   算出 425% 这种不可能的数）。
	///   帧时间环形缓冲本来就有 `LongFrameSkipMs` 这个闸门，CPU 累加器此前漏了它
	///   —— 现在用**同一个判据**、同一个样本集，分子分母口径一致。
	/// </summary>
	private void SampleCpuSplit(float frameMs, bool normalFrame)
	{
		try
		{
			if (!normalFrame)
			{
				_sysSkippedFrames++;
				return;
			}
			_procSumMs += Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
			_physSumMs += Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
			_sysFrameSumMs += frameMs;
			_sysFrames++;
		}
		catch { }
	}

	/// <summary>
	/// 结算窗口内的 GC 次数与托管分配增量（报告/周期行发出前调用）。
	/// 口径与游戏内置 [TDPerf] 的 dump 头一致（GC.CollectionCount / GetTotalAllocatedBytes）。
	/// </summary>
	private void SettleSysStats(ulong now)
	{
		try
		{
			long alloc = GC.GetTotalAllocatedBytes(false);
			long g0 = GC.CollectionCount(0);
			long g1 = GC.CollectionCount(1);
			long g2 = GC.CollectionCount(2);
			if (_sysBaseSet)
			{
				_sysHadBaseline = true;
				_gc0Delta = Math.Max(0L, g0 - _gc0Base);
				_gc1Delta = Math.Max(0L, g1 - _gc1Base);
				_gc2Delta = Math.Max(0L, g2 - _gc2Base);
				long db = Math.Max(0L, alloc - _allocBase);
				long df = Math.Max(1L, _sessFrames - _sysBaseFrames);
				_allocKbDelta = db / 1024.0;
				_allocKbPerFrame = _allocKbDelta / df;
			}
			_gc0Base = g0;
			_gc1Base = g1;
			_gc2Base = g2;
			_allocBase = alloc;
			_sysBaseFrames = _sessFrames;
			_sysBaseMs = now;
			_sysBaseSet = true;
		}
		catch { }
	}

	/// <summary>把 CPU 分项窗口累计清零（报告/周期行发出后调用，保证下个窗口独立）。</summary>
	private void ResetCpuSplitWindow()
	{
		_procSumMs = 0.0;
		_physSumMs = 0.0;
		_sysFrameSumMs = 0.0;
		_sysFrames = 0L;
		_sysSkippedFrames = 0L;
	}

	/// <summary>分配计数器自检是否已跑过 / 是否可信。</summary>
	private bool _allocTestDone;
	private bool _allocCounterOk = true;

	/// <summary>
	/// ★ v2.1.0：**分配计数器自检**（启动后一次，代价 ~4MB 临时数组）。
	///
	/// 为什么值得加：报告里的「托管分配 X MB / Y KB 每帧」是**结论级**数字
	/// （真机首测就打出 ~20MB/帧、gen0+30/19帧），一旦计数器语义与预期不符，
	/// 整条结论就是错的。这里用"分配已知 4MB 再看计数器涨了多少"来当场证伪：
	/// 涨够了 ⇒ 报告里的分配数字可用；涨不够 ⇒ 明确标注"不可信，请忽略"。
	/// </summary>
	private void SelfTestAllocCounter()
	{
		if (_allocTestDone)
		{
			return;
		}
		_allocTestDone = true;
		try
		{
			long a = GC.GetTotalAllocatedBytes(false);
			var hold = new List<byte[]>(4);
			for (int i = 0; i < 4; i++)
			{
				hold.Add(new byte[1024 * 1024]);
			}
			long sum = 0L;
			foreach (byte[] b in hold)
			{
				sum += b.Length;
			}
			long b2 = GC.GetTotalAllocatedBytes(false);
			long delta = b2 - a;
			_allocCounterOk = delta >= (3L * 1024L * 1024L);
			GC.KeepAlive(hold);
			Log("分配计数器自检：实际分配 4096 KB，计数器报告 " + (delta / 1024L) + " KB ⇒ "
				+ (_allocCounterOk ? "可信（报告里的 GC/分配数字可用）" : "★ 不可信（报告里的分配数字请忽略）")
				+ "。sum=" + sum);
		}
		catch { }
	}

	/// <summary>
	/// ★ v2.1.0 核心新增行：CPU 分项 + 脚本占帧比例。
	/// 这一行直接回答"帧时间花在脚本上还是脚本之外"——v2.0.0 的报告最大的盲点。
	/// </summary>
	private void AppendSysBlock()
	{
		try
		{
			if (!EnableCpuGcStats)
			{
				return;
			}
			if (_sysFrames <= 0)
			{
				_sb.Append(P).Append("CPU 分项: 采样中（尚无窗口样本）").Append('\n');
				return;
			}
			double n = _sysFrames;
			double proc = _procSumMs / n;
			double phys = _physSumMs / n;
			double total = proc + phys;
			double frame = _sysFrameSumMs / n;
			double pct = (frame > 0.001) ? (total / frame * 100.0) : 0.0;
			_sb.Append(P).Append("CPU 分项(本窗口 ").Append(_sysFrames).Append(" 帧");
			if (_sysSkippedFrames > 0)
			{
				_sb.Append("，另跳过 ").Append(_sysSkippedFrames).Append(" 个长帧");
			}
			_sb.Append("): 脚本 process ").Append(proc.ToString("F2", CultureInfo.InvariantCulture))
				.Append("ms + 物理 physics ").Append(phys.ToString("F2", CultureInfo.InvariantCulture))
				.Append("ms = ").Append(total.ToString("F2", CultureInfo.InvariantCulture))
				.Append("ms · 帧均 ").Append(frame.ToString("F1", CultureInfo.InvariantCulture)).Append("ms");
			// 防御：占比 > 100% 在物理上不可能（`process` 是单线程的、本来就含在帧内）。
			//   真机实测确实出现过（启动基线 230%、退出窗口 174%）——原因是引擎这个监视器
			//   是**平滑量**，会话前段仍被启动期那几帧几秒的加载帧抬着；且窗口含退出/切场景时更明显。
			//   ⇒ 宁可只报原始数字 + 说明，也不要打出一个会误导人的百分比。
			if (pct > 100.0)
			{
				double rawProc = 0.0;
				double rawPhys = 0.0;
				try
				{
					rawProc = Performance.GetMonitor(Performance.Monitor.TimeProcess);
					rawPhys = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess);
				}
				catch { }
				_sb.Append(" ⇒ ⚠ 占比 ").Append(pct.ToString("F1", CultureInfo.InvariantCulture))
					.Append("%>100% 物理上不可能：引擎该监视器为平滑量，会话前段仍受启动期长帧影响")
					.Append("（原始读数 TimeProcess=").Append(rawProc.ToString("R", CultureInfo.InvariantCulture))
					.Append(" · TimePhysicsProcess=").Append(rawPhys.ToString("R", CultureInfo.InvariantCulture))
					.Append("）。请在**稳定战斗场景**里复核这一行。").Append('\n');
			}
			else
			{
				_sb.Append(" ⇒ 脚本占 ").Append(pct.ToString("F1", CultureInfo.InvariantCulture))
					.Append("% · 脚本之外 ").Append(Math.Max(0.0, frame - total).ToString("F1", CultureInfo.InvariantCulture))
					.Append("ms").Append('\n');
			}

			// GC / 分配行
			_sb.Append(P).Append("GC/分配(本窗口): ");
			if (!_sysHadBaseline)
			{
				// 首次报告只建立基线，增量必然是全 0 —— 直接说明，别让人误读成"没有 GC"
				_sb.Append("首次报告仅建立基线（下一次报告起给出 gen0/1/2 与分配增量）").Append('\n');
				return;
			}
			// ★ v2.1.1：**GC 计数各平台都可用；托管分配量在 Mono/安卓上不可用**
			//   （`GC.GetTotalAllocatedBytes` 在 Mono 上不实现 ⇒ 自检必然不过）。
			//   旧版在这种情况下仍会打印一个由 0 差值算出的垃圾数字（真机日志里出现过
			//   "45824.7 KB/帧"），只会误导 —— 改为明确说"本平台不可用"，不报数。
			_sb.Append("gen0 +").Append(_gc0Delta)
				.Append(" · gen1 +").Append(_gc1Delta)
				.Append(" · gen2 +").Append(_gc2Delta);
			if (_allocCounterOk)
			{
				_sb.Append(" · 托管分配 ").Append(FmtKb(_allocKbDelta))
					.Append("（").Append(_allocKbPerFrame.ToString("F1", CultureInfo.InvariantCulture)).Append(" KB/帧）");
			}
			else
			{
				_sb.Append(" · 托管分配：本平台不可用（Mono 未实现 GC.GetTotalAllocatedBytes，自检未过）");
			}
			// gen2 一次都没有 + 分配量很低 ⇒ 明确写出来，省得误判"尖峰是 GC"
			if (_gc0Delta == 0L && _gc1Delta == 0L && _gc2Delta == 0L)
			{
				_sb.Append(" · 本窗口无 GC");
			}
			_sb.Append('\n');
		}
		catch { }
	}

	private static string FmtKb(double kb)
	{
		if (kb >= 1024.0)
		{
			return (kb / 1024.0).ToString("F2", CultureInfo.InvariantCulture) + " MB";
		}
		return kb.ToString("F0", CultureInfo.InvariantCulture) + " KB";
	}

	/// <summary>周期行内联的 CPU 分项 + GC（一行装下，供 60s 记录用）。</summary>
	private void AppendSysInline()
	{
		try
		{
			if (!EnableCpuGcStats || _sysFrames <= 0)
			{
				_sb.Append("CPU 采样中");
				return;
			}
			double n = _sysFrames;
			double proc = _procSumMs / n;
			double phys = _physSumMs / n;
			double total = proc + phys;
			double frame = _sysFrameSumMs / n;
			double pct = (frame > 0.001) ? (total / frame * 100.0) : 0.0;
			_sb.Append("脚本 ").Append(proc.ToString("F2", CultureInfo.InvariantCulture)).Append('+')
				.Append(phys.ToString("F2", CultureInfo.InvariantCulture)).Append('=')
				.Append(total.ToString("F2", CultureInfo.InvariantCulture)).Append("ms/帧(占")
				.Append((pct > 100.0 ? -1.0 : pct).ToString("F1", CultureInfo.InvariantCulture)).Append("%)")
				.Append(" · GC gen0+").Append(_sysHadBaseline ? _gc0Delta : 0L)
				.Append(" gen1+").Append(_sysHadBaseline ? _gc1Delta : 0L)
				.Append(" gen2+").Append(_sysHadBaseline ? _gc2Delta : 0L)
				.Append(_allocCounterOk
					? (" · 分配 " + FmtKb(_sysHadBaseline ? _allocKbDelta : 0.0))
					: " · 分配(本平台不可用)")
				.Append(_sysHadBaseline ? "" : "(首次仅基线)");
		}
		catch { }
	}

	/// <summary>
	/// ★ v2.1.0：埋点覆盖率（内联）。把"这个工具有多瞎"直接写进报告 ——
	/// v2.0.0 的日志里 Top10 看起来很大，但没人知道它只占墙上时间 1~2%。
	/// </summary>
	private string FuncCoverageInline()
	{
		try
		{
			if (!ProfilerEnabled())
			{
				return "埋点: 分析器未开";
			}
			if (_funcRegisteredCount <= 0 || _funcTotalMsPerSec <= 0.0)
			{
				return "埋点: 采样中";
			}
			double pct = _funcTotalMsPerSec / 10.0;   // ms/s → 占墙上时间百分比（1000ms/s = 100%）
			return "埋点合计 " + _funcTotalMsPerSec.ToString("F2", CultureInfo.InvariantCulture)
				+ "ms/s(占墙上 " + pct.ToString("F2", CultureInfo.InvariantCulture) + "%)"
				+ " 有数据 " + _funcActiveCount + "/" + _funcRegisteredCount + " 项";
		}
		catch
		{
			return "埋点: ?";
		}
	}

	/// <summary>当前关卡/地图显示名（缓存 10s；取不到就回落场景名）。</summary>
	private string MapLabel()
	{
		try
		{
			ulong now = Time.GetTicksMsec();
			if (_mapNameMs != NoTs && now - _mapNameMs < 10000UL)
			{
				return _mapNameCache;
			}
			_mapNameMs = now;
			string s = "";
			try
			{
				TowerDefenseManager mgr = TowerDefenseManager.Instance;
				if (mgr != null && GodotObject.IsInstanceValid(mgr))
				{
					TowerDefenseMapConfig mc = mgr.GetCurrentMapConfig();
					if (mc != null && GodotObject.IsInstanceValid(mc))
					{
						s = mc.translate;
						if (string.IsNullOrWhiteSpace(s))
						{
							s = mc.mapScenePath;
						}
						// translate 多数是翻译键（如 MAP_VAMPIRE），过一遍翻译器；中文名原样返回
						if (!string.IsNullOrWhiteSpace(s))
						{
							string tr = TranslationServer.Translate(s);
							if (!string.IsNullOrWhiteSpace(tr))
							{
								s = tr;
							}
						}
					}
				}
			}
			catch { }
			_mapNameCache = (s ?? "").Trim();
			return _mapNameCache;
		}
		catch
		{
			return "";
		}
	}

	/// <summary>
	/// 已启用 Mod 清单（读 user://Mods/enabled_mods.json，缓存 30s）。
	/// 刻意不走宿主 ModLoader API：**只读一个 JSON 文件**，跨版本最稳、也不会因为
	/// API 变动把整包拖挂。用途：把日志里某段时间对到"当时装了哪些 Mod"。
	/// </summary>
	private string ModListLabel()
	{
		try
		{
			ulong now = Time.GetTicksMsec();
			if (_modListMs != NoTs && now - _modListMs < 30000UL)
			{
				return _modListCache;
			}
			_modListMs = now;
			string s = "";
			try
			{
				// 不用 using：Godot 的 FileAccess 是 RefCounted，显式 Close 最稳。
				// ⚠ 必须写全名 `Godot.FileAccess`：本文件 `using System.IO;` ⇒ 与
				//   `System.IO.FileAccess`（枚举）撞名，写短名会 CS0104 不明确引用。
				Godot.FileAccess f = Godot.FileAccess.Open("user://Mods/enabled_mods.json", Godot.FileAccess.ModeFlags.Read);
				if (f != null)
				{
					string txt = f.GetAsText();
					f.Close();
					int i = txt.IndexOf("\"enabledIds\"", StringComparison.Ordinal);
					if (i >= 0)
					{
						int lb = txt.IndexOf('[', i);
						int rb = (lb >= 0) ? txt.IndexOf(']', lb) : -1;
						if (lb >= 0 && rb > lb)
						{
							string body = txt.Substring(lb + 1, rb - lb - 1);
							var ids = new List<string>();
							foreach (string raw in body.Split(','))
							{
								string t = raw.Trim().Trim('"').Trim();
								if (t.Length > 0)
								{
									ids.Add(t);
								}
							}
							s = ids.Count + " 个: " + string.Join(",", ids.ToArray());
						}
					}
				}
			}
			catch { }
			_modListCache = (s ?? "").Trim();
			return _modListCache;
		}
		catch
		{
			return "";
		}
	}

	// ================================================================ 触发判定

	private void Evaluate(ulong now)
	{
		// ① 战斗开始/结束边沿（报告里"场景"要用）
		try
		{
			bool inBattleNow = IsInBattle();
			if (inBattleNow != _inBattle)
			{
				_inBattle = inBattleNow;
				if (inBattleNow)
				{
					_battleStartMs = now;
					_battleFrames = 0L;
					_battleSumMs = 0.0;
					_battleMaxMs = 0.0;
				}
				if (EnableLogReports && EnableAutoTriggers)
				{
					EmitFullReport(inBattleNow ? "战斗开始" : "战斗结束");
				}
			}
		}
		catch (Exception ex)
		{
			if (_faultCode < 101)
			{
				_faultCode = 101;
				Log("战斗边沿判定异常（本条只报一次）：" + ex.Message);
			}
		}

		if (true)    /* ★ 用户要求：日志默认关闭（原：!EnableLogReports） */
		{
			return;
		}

		try
		{
			if (EnableAutoTriggers)
			{
				// 启动基线
				if (!_bootReported && now - _startMs >= (ulong)(BootReportDelaySec * 1000.0))
				{
					_bootReported = true;
					if (_lastFullReportMs == NoTs || now - _lastFullReportMs > (ulong)(ReportMinGapSec * 1000.0))
					{
						EmitFullReport("启动基线");
					}
				}

				// 持续低帧
				double avg5;
				int n5;
				WindowAvgFps(now, LowFpsWindowSec, out avg5, out n5);
				if (n5 >= LowFpsMinSamples && avg5 < LowFpsThreshold
					&& (_lastLowFpsReportMs == NoTs || now - _lastLowFpsReportMs > (ulong)(TriggerCooldownSec * 1000.0)))
				{
					_lastLowFpsReportMs = now;
					EmitFullReport("持续低帧(" + avg5.ToString("F0", CultureInfo.InvariantCulture) + "FPS/" + (int)LowFpsWindowSec + "s)");
				}

				// 尖峰频发
				int spike10 = CountSpikesSince(now, SpikeBurstWindowSec);
				if (spike10 >= SpikeBurstCount
					&& (_lastSpikeReportMs == NoTs || now - _lastSpikeReportMs > (ulong)(TriggerCooldownSec * 1000.0)))
				{
					_lastSpikeReportMs = now;
					EmitFullReport("尖峰频发(" + spike10 + "次/" + (int)SpikeBurstWindowSec + "s)");
				}
			}
		}
		catch (Exception ex)
		{
			if (_faultCode < 102)
			{
				_faultCode = 102;
				Log("触发判定异常（本条只报一次）：" + ex.Message);
			}
		}

		// ② 周期记录（一行：FPS + 函数耗时 Top5）
		if (EnablePeriodic)
		{
			try
			{
				if (now - _lastPeriodicMs >= (ulong)(PeriodicSec * 1000.0))
				{
					_lastPeriodicMs = now;
					EmitPeriodicLine(now);
				}
			}
			catch { }
		}
	}

	// ================================================================ 面板热键（F7/F8）

	private void PollPanelKeyFallback()
	{
		try
		{
			bool f7 = Input.IsKeyPressed(PanelToggleKey);
			if (f7 && !_f7PollPrev)
			{
				PanelKeyDown(PanelToggleKey);
			}
			_f7PollPrev = f7;
			bool f8 = Input.IsKeyPressed(PanelSortKey);
			if (f8 && !_f8PollPrev)
			{
				PanelKeyDown(PanelSortKey);
			}
			_f8PollPrev = f8;
		}
		catch { }
	}

	private void PanelKeyDown(Key key)
	{
		ulong now = Time.GetTicksMsec();
		if (key == PanelToggleKey)
		{
			if (now - _f7LastMs < 300UL)
			{
				return;                    // 事件通道与轮询通道去重
			}
			_f7LastMs = now;
			TogglePanel();
		}
		else if (key == PanelSortKey)
		{
			if (now - _f8LastMs < 300UL)
			{
				return;
			}
			_f8LastMs = now;
			_funcSortMode = (_funcSortMode + 1) % FuncSortNames.Length;
			_lastPanelMs = NoTs;           // 下一帧立刻刷新
		}
	}

	private void TogglePanel()
	{
		try
		{
			if (_panelLayer == null || !GodotObject.IsInstanceValid(_panelLayer))
			{
				return;
			}
			// ★ v2.2.1：F7 与配置页按钮走同一条路 —— 翻转 _panelWanted 并写回配置，
			//   否则下一帧的"对齐"会把它翻回去（之前 F7 直接改 Visible，配置却不知道）。
			_panelWanted = !_panelWanted;
			_panelLayer.Visible = _panelWanted;
			try
			{
				var ch = new System.Collections.Generic.Dictionary<string, object>();
				ch["showPanel"] = _panelWanted;
				_ctxRef?.Settings?.TrySave(ch, out XWModSettingsSaveResult _);
			}
			catch { }
		}
		catch { }
	}

	// ================================================================ 窗口输入（F7/F8 事件通道）

	private bool _windowInputHooked;

	private void HookWindowInput()
	{
		try
		{
			if (_windowInputHooked || !EnablePanelHotkeys)
			{
				return;
			}
			Window root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root == null)
			{
				return;
			}
			root.WindowInput += OnWindowInput;
			_windowInputHooked = true;
		}
		catch (Exception ex)
		{
			TryErr("挂载窗口输入异常：" + ex.Message);
		}
	}

	private void UnhookWindowInput()
	{
		try
		{
			if (!_windowInputHooked)
			{
				return;
			}
			Window root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root != null)
			{
				root.WindowInput -= OnWindowInput;
			}
			_windowInputHooked = false;
		}
		catch { }
	}

	private void OnWindowInput(InputEvent ev)
	{
		try
		{
			if (!EnablePanelHotkeys || ev == null)
			{
				return;
			}
			if (ev is InputEventKey k && k.Pressed && !k.Echo
				&& (k.Keycode == PanelToggleKey || k.Keycode == PanelSortKey))
			{
				PanelKeyDown(k.Keycode);
			}
		}
		catch { }
	}

	// ================================================================ 日志输出（只记 FPS + 函数耗时）

	/// <summary>完整报告：FPS 概览 + 函数耗时 TopN（触发点调用）。</summary>
	private void EmitFullReport(string reason)
	{
		if (true)    /* ★ 用户要求：日志默认关闭（原：!EnableLogReports） */
		{
			return;
		}
		try
		{
			ulong now = Time.GetTicksMsec();
			_lastFullReportMs = now;
			EnsureFreshFunctionSamples(now);
			// ★ v2.1.0：结算本窗口的 GC/分配增量（必须在构建报告之前）
			SettleSysStats(now);

			_sb.Length = 0;
			_sb.Append(P).Append("════ 报告 · ").Append(reason).Append(" ════").Append('\n');
			AppendFpsBlock(now);
			AppendFunctionBlock(ReportFuncTopN);
			AppendSelfCost();
			_sb.Append(P).Append("════ 报告结束 ════");
			Emit(_sb.ToString());
			_selfTicks = 0L;
			_selfFrames = 0L;
			_selfMaxUs = 0.0;
			// ★ v2.1.0：CPU 分项窗口随报告一起翻页（下个窗口独立统计）
			ResetCpuSplitWindow();
		}
		catch (Exception ex)
		{
			if (_faultCode < 103)
			{
				_faultCode = 103;
				Log("报告生成异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>周期记录（一行）：FPS + 函数耗时 Top5。</summary>
	private void EmitPeriodicLine(ulong now)
	{
		if (true)    /* ★ 用户要求：日志默认关闭（原：!EnableLogReports） */
		{
			return;
		}
		try
		{
			EnsureFreshFunctionSamples(now);
			SettleSysStats(now);   // ★ v2.1.0
			double avg60;
			int n60;
			WindowAvgFps(now, 60.0, out avg60, out n60);
			double avgMs;
			double p95;
			double maxMs;
			WindowStats(now, 60.0, out avgMs, out p95, out maxMs);

			_sb.Length = 0;
			_sb.Append(P).Append("记录 60s: FPS 均").Append(avg60.ToString("F1", CultureInfo.InvariantCulture))
				.Append(" 现").Append(SafeFps().ToString("F0", CultureInfo.InvariantCulture))
				.Append(" | 帧 均").Append(avgMs.ToString("F1", CultureInfo.InvariantCulture))
				.Append(" p95 ").Append(p95.ToString("F1", CultureInfo.InvariantCulture))
				.Append(" max ").Append(maxMs.ToString("F1", CultureInfo.InvariantCulture)).Append("ms")
				.Append(" | 尖峰(>").Append((int)SpikeMs).Append("ms)×").Append(CountSpikesSince(now, 60.0))
				.Append(" | ");
			AppendSysInline();
			_sb.Append(" | ").Append(FuncCoverageInline());
			_sb.Append(" | ");
			AppendFunctionTopInline(PeriodicFuncTopN);
			// ★ v2.1.0：周期行也带上下文（关卡 + Mod 清单），便于事后对齐
			if (EnableSceneModContext)
			{
				string map2 = MapLabel();
				if (!string.IsNullOrEmpty(map2))
				{
					_sb.Append(" | 关卡 ").Append(map2);
				}
			}
			Emit(_sb.ToString());
			_selfTicks = 0L;
			_selfFrames = 0L;
			_selfMaxUs = 0.0;
			ResetCpuSplitWindow();   // ★ v2.1.0
		}
		catch { }
	}

	private void Emit(string text)
	{
		// ★ 2026-10-14：原先无条件输出（还写文件），现统一受日志总开关控制
		if (true)    /* ★ 用户要求：日志默认关闭（原：!EnableLogReports） */ { return; }
		try { GD.Print(text); } catch { }
		AppendFile(text);
	}

	private void AppendFpsBlock(ulong now)
	{
		try
		{
			double avg60;
			int n60;
			WindowAvgFps(now, 60.0, out avg60, out n60);
			double avg5;
			int n5;
			WindowAvgFps(now, LowFpsWindowSec, out avg5, out n5);
			double avgMs;
			double p50;
			double p95;
			double p99;
			double maxMs;
			int n;
			WindowStatsFull(now, 60.0, out avgMs, out p50, out p95, out p99, out maxMs, out n);

			// 概览行
			_sb.Append(P).Append("时间 ").Append(FmtClock(now))
				.Append(" · 会话 ").Append(FmtDur(now - _startMs))
				.Append(" · 场景 ").Append(_inBattle ? "战斗" : "非战斗");
			if (_inBattle && _battleStartMs != NoTs)
			{
				_sb.Append(" · 本局 ").Append(FmtDur(now - _battleStartMs));
			}
			_sb.Append('\n');

			// ★ v2.1.0：关卡名 + 已启用 Mod 清单（把这段数据对到"哪个关卡、装了哪些 Mod"）
			if (EnableSceneModContext)
			{
				string map = MapLabel();
				string mods = ModListLabel();
				_sb.Append(P).Append("上下文: 关卡 ").Append(string.IsNullOrEmpty(map) ? "（未识别）" : map);
				if (!string.IsNullOrEmpty(mods))
				{
					_sb.Append(" · Mod ").Append(mods);
				}
				_sb.Append('\n');
			}

			// FPS 行
			_sb.Append(P).Append("FPS 现").Append(SafeFps().ToString("F1", CultureInfo.InvariantCulture))
				.Append(" · ").Append((int)LowFpsWindowSec).Append("s 均").Append(avg5.ToString("F1", CultureInfo.InvariantCulture))
				.Append(" · 60s 均").Append(avg60.ToString("F1", CultureInfo.InvariantCulture)).Append('\n');

			// ★ v2.1.0：CPU 分项（process/physics）+ GC/分配 —— 报告里最关键的两行
			AppendSysBlock();

			// 帧时间行
			_sb.Append(P).Append("帧时间 60s/").Append(n).Append("帧: 均").Append(avgMs.ToString("F1", CultureInfo.InvariantCulture))
				.Append(" 中位").Append(p50.ToString("F1", CultureInfo.InvariantCulture))
				.Append(" p95 ").Append(p95.ToString("F1", CultureInfo.InvariantCulture))
				.Append(" p99 ").Append(p99.ToString("F1", CultureInfo.InvariantCulture))
				.Append(" max ").Append(maxMs.ToString("F1", CultureInfo.InvariantCulture)).Append("ms");
			if (_battleFrames > 0)
			{
				_sb.Append(" | 本局累计 ").Append(_battleFrames).Append("帧 均")
					.Append((_battleSumMs / Math.Max(1L, _battleFrames)).ToString("F1", CultureInfo.InvariantCulture))
					.Append("ms 最大").Append(_battleMaxMs.ToString("F1", CultureInfo.InvariantCulture)).Append("ms");
			}
			_sb.Append('\n');

			// 尖峰行
			_sb.Append(P).Append("尖峰(>").Append((int)SpikeMs).Append("ms): 本会话 ").Append(_spikeTotal)
				.Append(" 次 · 近60s ").Append(CountSpikesSince(now, 60.0))
				.Append(" 次 · 近10s ").Append(CountSpikesSince(now, SpikeBurstWindowSec)).Append(" 次");
			AppendTopSpikes();
			_sb.Append('\n');
		}
		catch { }
	}

	private void AppendTopSpikes()
	{
		try
		{
			// ★ v2.1.0 修复：旧实现是 `_spikeHead - 1 - i` 往回取 —— 那是**最近 3 次**，
			//   不是"最大 3 次"，可标签却写着 Top。后果是会话最大尖峰常被排在最后甚至挤出：
			//   v2.0.0 那份日志里出现过 `Top 729ms@10:15:15 151ms@10:15:14 1771ms@10:15:14`
			//   —— 1771ms（会话最大）排在最后，一眼就会误读成"最大只有 729ms"。
			//   现在真排序：取缓冲内最大的 3 条，降序输出。
			if (_spikeCount <= 0)
			{
				_sb.Append(" · Top 无");
				return;
			}
			_scratchSpikes.Clear();
			for (int i = 0; i < _spikeCount; i++)
			{
				int idx = (_spikeHead - 1 - i + SpikeLogSize * 2) % SpikeLogSize;
				_scratchSpikes.Add(_spikes[idx]);
			}
			_scratchSpikes.Sort(delegate (SpikeRec a, SpikeRec b)
			{
				return b.Ms.CompareTo(a.Ms);   // 降序
			});
			_sb.Append(" · Top(最大)");
			int shown = 0;
			foreach (SpikeRec r in _scratchSpikes)
			{
				if (shown >= 3)
				{
					break;
				}
				_sb.Append(' ');
				_sb.Append(r.Ms.ToString("F0", CultureInfo.InvariantCulture)).Append("ms@").Append(FmtClock(r.Ts));
				_sb.Append('(').Append(r.Scene == 1 ? "战斗" : "非战").Append(')');
				shown++;
			}
		}
		catch { }
	}

	/// <summary>尖峰 Top 排序用的临时表（复用，避免每次分配）。</summary>
	private readonly List<SpikeRec> _scratchSpikes = new List<SpikeRec>(SpikeLogSize);

	/// <summary>报告里的函数耗时明细块。</summary>
	private void AppendFunctionBlock(int topN)
	{
		try
		{
			if (!ProfilerEnabled())
			{
				_sb.Append(P).Append("函数耗时: 分析器未开（按 9 或在代码里开 EnableFunctionProfiler）").Append('\n');
				return;
			}
			BuildSortedFunctions();
			if (_funcSorted.Count == 0)
			{
				_sb.Append(P).Append("函数耗时: 采样中（暂无数据）").Append('\n');
				return;
			}
			_sb.Append(P).Append("函数耗时 Top").Append(Math.Min(topN, _funcSorted.Count))
				.Append("（ms/s · 次/s · 单次；排序:").Append(FuncSortNames[_funcSortMode]).Append("）:").Append('\n');
			AppendCoverageLine();   // ★ v2.1.0：先给"这些埋点总共占多少"，再看排行
			int idx = 0;
			foreach (FuncRank r in _funcSorted)
			{
				if (idx >= topN)
				{
					break;
				}
				idx++;
				_sb.Append(P).Append("  ").Append(idx.ToString("D2", CultureInfo.InvariantCulture)).Append(". ")
					.Append(r.Name).Append("  ").Append(FmtMsRate(r)).Append('\n');
			}
		}
		catch { }
	}

	/// <summary>周期行里的函数 TopN（内联一段）。</summary>
	private void AppendFunctionTopInline(int topN)
	{
		try
		{
			if (!ProfilerEnabled())
			{
				_sb.Append("函数: 分析器未开");
				return;
			}
			BuildSortedFunctions();
			if (_funcSorted.Count == 0)
			{
				_sb.Append("函数: 采样中");
				return;
			}
			_sb.Append("函数Top").Append(Math.Min(topN, _funcSorted.Count)).Append(": ");
			int idx = 0;
			foreach (FuncRank r in _funcSorted)
			{
				if (idx >= topN)
				{
					break;
				}
				if (idx > 0)
				{
					_sb.Append(' ');
				}
				_sb.Append(ShortName(r.Name)).Append('=')
					.Append(r.MsPerSec.ToString("F3", CultureInfo.InvariantCulture)).Append("ms/s");
				idx++;
			}
		}
		catch { }
	}

	private void AppendSelfCost()
	{
		try
		{
			double avg = 0.0;
			if (_selfFrames > 0)
			{
				avg = _selfTicks * 1000000.0 / Stopwatch.Frequency / _selfFrames;
			}
			_sb.Append(P).Append("本监测器自身开销: 均 ").Append(avg.ToString("F2", CultureInfo.InvariantCulture))
				.Append("µs/帧 · 峰值 ").Append(_selfMaxUs.ToString("F1", CultureInfo.InvariantCulture))
				.Append("µs（").Append(_selfFrames).Append(" 帧样本）").Append('\n');
		}
		catch { }
	}

	/// <summary>取一段短名（周期行用；取最后两段，过长截断）。</summary>
	private static string ShortName(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return "?";
		}
		int a = name.LastIndexOf('.');
		if (a > 0)
		{
			int b = name.LastIndexOf('.', a - 1);
			if (b >= 0 && name.Length - b - 1 <= 34)
			{
				return name.Substring(b + 1);
			}
		}
		return Trunc(name, 34);
	}

	// ================================================================ 面板（只显示函数耗时）

	private void EnsurePanel()
	{
		try
		{
			if (!EnablePanel)
			{
				return;
			}
			if (_panelLayer != null && GodotObject.IsInstanceValid(_panelLayer)
				&& _panel != null && GodotObject.IsInstanceValid(_panel))
			{
				return;
			}
			Window root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root == null)
			{
				return;
			}

			_panelLayer = new CanvasLayer();
			_panelLayer.Visible = _panelWanted;   // ★ v2.2.1：建好后立刻按配置决定显示与否
			_panelLayer.Name = "ModPerfPanelLayer";
			_panelLayer.Layer = PanelLayerId;
			root.AddChild(_panelLayer);

			_panel = new PanelContainer();
			_panel.Name = "ModPerfPanel";
			_panel.MouseFilter = Control.MouseFilterEnum.Ignore;
			_panelLayer.AddChild(_panel);

			Theme theme = LoadGameTheme();
			if (theme != null)
			{
				_panel.Theme = theme;    // 中文靠游戏主主题字体（fzjz.ttf）；取不到则退回项目主题
			}

			StyleBoxFlat bg = new StyleBoxFlat();
			bg.BgColor = new Color(0.04f, 0.05f, 0.07f, 0.78f);
			bg.BorderColor = new Color(0.45f, 0.85f, 0.6f, 0.5f);
			bg.SetBorderWidthAll(1);
			bg.SetCornerRadiusAll(6);
			bg.ContentMarginLeft = 10f;
			bg.ContentMarginRight = 10f;
			bg.ContentMarginTop = 6f;
			bg.ContentMarginBottom = 6f;
			_panel.AddThemeStyleboxOverride("panel", bg);

			_panelBox = new VBoxContainer();
			_panelBox.MouseFilter = Control.MouseFilterEnum.Ignore;
			_panelBox.AddThemeConstantOverride("separation", 2);
			_panel.AddChild(_panelBox);

			_panelTitle = MakePanelLabel(14, new Color(0.66f, 0.98f, 0.76f, 1f));
			_panelBox.AddChild(_panelTitle);

			// ★ v2.1.2：帧率行（FPS · 当帧帧时间 · 60s 均），字号比标题大一点，方便远处看
			if (EnablePanelFps)
			{
				_panelFps = MakePanelLabel(15, new Color(0.55f, 0.95f, 0.60f, 1f));
				_panelBox.AddChild(_panelFps);
			}

			_funcRowBoxes.Clear();
			_funcNames.Clear();
			_funcBars.Clear();
			_funcVals.Clear();
			for (int i = 0; i < FuncTopN; i++)
			{
				HBoxContainer row = new HBoxContainer();
				row.MouseFilter = Control.MouseFilterEnum.Ignore;
				row.AddThemeConstantOverride("separation", 6);
				_panelBox.AddChild(row);
				_funcRowBoxes.Add(row);

				Label name = MakePanelLabel(13, new Color(0.86f, 0.96f, 0.9f, 1f));
				name.CustomMinimumSize = new Vector2(208f, 0f);
				name.ClipText = true;
				name.VerticalAlignment = VerticalAlignment.Center;
				row.AddChild(name);
				_funcNames.Add(name);

				ColorRect bar = new ColorRect();
				bar.MouseFilter = Control.MouseFilterEnum.Ignore;
				bar.CustomMinimumSize = new Vector2(4f, 9f);
				bar.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
				bar.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
				bar.Color = new Color(0.4f, 0.85f, 0.58f, 0.85f);
				row.AddChild(bar);
				_funcBars.Add(bar);

				Label val = MakePanelLabel(13, new Color(1f, 0.86f, 0.55f, 1f));
				val.CustomMinimumSize = new Vector2(112f, 0f);
				val.HorizontalAlignment = HorizontalAlignment.Right;
				val.VerticalAlignment = VerticalAlignment.Center;
				row.AddChild(val);
				_funcVals.Add(val);
			}

			RefreshPanel(Time.GetTicksMsec());   // 先填一版内容，避免空框闪一下
		}
		catch (Exception ex)
		{
			if (_faultCode < 104)
			{
				_faultCode = 104;
				Log("面板构建异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>
	/// ★ v2.1.2：刷新面板帧率行。
	/// 三个数各有用途：`FPS`（引擎自报瞬时）、`帧`（我自己按 process_frame 间隔测的当帧毫秒，
	/// 与日志里的统计同源）、`60s均`（看趋势；瞬时值在掉帧时抖得厉害）。
	/// </summary>
	/// <summary>
	/// ★ v2.2.2（用户要求「切换显示状态的按钮改成复选框」）：配置页里放一个**复选框**「显示性能面板」。
	///
	/// 锚点：宿主配置页每个设置项都会渲染出一行，行内有 `Reset_&lt;设置键&gt;`（KeybindPanelManager 同款做法）；
	/// 本 Mod 在 mod.json 里声明了 `showPanel`，所以 `Reset_showPanel` 必然存在。
	/// 找不到时再退两级锚点，并在全都找不到时打一条日志（便于定位宿主改名）。
	///
	/// 行为：勾选/取消 = 立即显隐面板 + 写回 `showPanel` 配置；
	/// 与游戏内 F7、以及宿主自己那一行原生复选框**共用同一个真源** `_panelWanted`，三者永远一致。
	/// </summary>
	private void TickConfigToggleButton()
	{
		// ★★ v2.2.3（用户实测反馈："点那个按钮会改配置保存，导致没法正确改配置"）：
		//   **本 Mod 不再往配置页注入任何控件**。
		//
		// 原因：宿主配置页是「草稿 + 统一保存」机制（XWModToolsPanel 的 `_playerSettingsDraft`
		//   / `_playerSettingsWriters`），而注入控件只能自己调 `Settings.TrySave(...)` 直写配置 ——
		//   两者叠加会把页面草稿搞脏：点一下注入控件之后，页面上其它项就再也改不对了 ✗。
		//
		// 而且**根本不需要**：`showPanel`（「打开游戏时显示面板」，bool）在宿主的原生长里
		//   本身就是渲染成一个**复选框**（宿主 AddPlayerSettingsField 的 `case 'b'`），
		//   它天然参与草稿/保存流程，改完按页面正常保存即可 —— 这已经满足
		//   "配置页里有个复选框控制面板显隐"的需求。
		//
		// 面板仍可随时用**游戏内 F7** 即时显隐（F7 会写回同一个配置）。
	}

	/// <summary>把"是否显示面板"写入真源 + 立刻套用 + 持久化（配置页复选框与 F7 都走这里）。</summary>
	private void SetPanelWanted(bool want)
	{
		try
		{
			_panelWanted = want;
			if (_panelLayer != null && GodotObject.IsInstanceValid(_panelLayer))
			{
				_panelLayer.Visible = want;
			}
			try
			{
				var ch = new System.Collections.Generic.Dictionary<string, object>();
				ch["showPanel"] = want;
				_ctxRef?.Settings?.TrySave(ch, out XWModSettingsSaveResult _);
			}
			catch { }
		}
		catch { }
	}

	/// <summary>★ v2.2.1：把 `showPanel` 读进 `_panelWanted`（默认开）。</summary>
	private static void ReadPanelWanted()
	{
		try
		{
			object v = (_ctxRef == null) ? null : _ctxRef.Settings.Get<object>("showPanel");
			if (v is bool b) { _panelWanted = b; }
			else if (v is string str && bool.TryParse(str, out bool sb)) { _panelWanted = sb; }
			else if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.True) { _panelWanted = true; }
				else if (je.ValueKind == System.Text.Json.JsonValueKind.False) { _panelWanted = false; }
			}
		}
		catch { }
	}

	/// <summary>★ v2.2.1：配置变更回调（配置页改「打开游戏时显示面板」立即生效，不必重启）。</summary>
	private void OnPanelCfgChanged(System.Collections.Generic.IReadOnlyList<string> keys)
	{
		try
		{
			ReadPanelWanted();
			if (_panelLayer != null && GodotObject.IsInstanceValid(_panelLayer))
			{
				_panelLayer.Visible = _panelWanted;
			}
		}
		catch { }
	}

	/// <summary>面板当前是否显示。</summary>
	private bool PanelShown()
	{
		return _panelWanted;
	}


	private void RefreshFpsLabel(ulong now)
	{
		try
		{
			if (_panelFps == null || !GodotObject.IsInstanceValid(_panelFps))
			{
				return;
			}
			double fps = SafeFps();
			double curMs = (_fCount > 0) ? _fDt[(_fHead - 1 + RingSize) % RingSize] : 0.0;
			double avg60;
			int n60;
			WindowAvgFps(now, 60.0, out avg60, out n60);
			Color c = (fps >= 50.0)
				? new Color(0.55f, 0.95f, 0.60f, 1f)
				: (fps >= 30.0 ? new Color(0.98f, 0.85f, 0.40f, 1f) : new Color(1f, 0.42f, 0.38f, 1f));
			_panelFps.Text = "FPS " + fps.ToString("F1", CultureInfo.InvariantCulture)
				+ " · 帧 " + curMs.ToString("F1", CultureInfo.InvariantCulture) + "ms"
				+ " · 60s均 " + avg60.ToString("F1", CultureInfo.InvariantCulture);
			_panelFps.AddThemeColorOverride("font_color", c);
		}
		catch { }
	}

	private Label MakePanelLabel(int size, Color color)
	{
		Label l = new Label();
		l.MouseFilter = Control.MouseFilterEnum.Ignore;
		l.AddThemeFontSizeOverride("font_size", size);
		l.AddThemeColorOverride("font_color", color);
		l.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.85f));
		l.AddThemeConstantOverride("shadow_offset_x", 1);
		l.AddThemeConstantOverride("shadow_offset_y", 1);
		return l;
	}

	private Theme LoadGameTheme()
	{
		try
		{
			if (_gameTheme != null && GodotObject.IsInstanceValid(_gameTheme))
			{
				return _gameTheme;
			}
			_gameTheme = ResourceLoader.Load<Theme>("res://Asset/Theme/MainTheme.tres");
			return _gameTheme;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>面板刷新（0.5s）：函数耗时 TopN。</summary>
	private void RefreshPanel(ulong now)
	{
		try
		{
			EnsurePanel();
			if (_panel == null || !GodotObject.IsInstanceValid(_panel)
				|| _panelBox == null || !GodotObject.IsInstanceValid(_panelBox)
				|| _funcNames.Count != FuncTopN)
			{
				return;
			}

			bool enabled = ProfilerEnabled();
			CollectFunctionMetrics(now);
			BuildSortedFunctions();

			if (_panelTitle != null && GodotObject.IsInstanceValid(_panelTitle))
			{
				if (!enabled)
				{
					_panelTitle.Text = "函数耗时（分析器未开：按 9 或开 EnableFunctionProfiler）";
				}
				else if (_funcSorted.Count == 0)
				{
					_panelTitle.Text = "函数耗时 · 采样中…" + (FunctionProfilerDetailed ? "（深路径）" : "");
				}
				else
				{
					string shortSort = FuncSortNames[_funcSortMode];
					int sp = shortSort.IndexOf(' ');
					if (sp > 0)
					{
						shortSort = shortSort.Substring(0, sp);
					}
					_panelTitle.Text = "函数耗时 Top" + Math.Min(FuncTopN, _funcSorted.Count)
						+ " · " + shortSort
						+ (EnablePanelHotkeys ? "（F8换/F7隐）" : "");
				}
			}

			double maxMetric = 0.0;
			foreach (FuncRank r in _funcSorted)
			{
				double v = FuncSortValue(r);
				if (v > maxMetric)
				{
					maxMetric = v;
				}
			}

			for (int i = 0; i < FuncTopN; i++)
			{
				HBoxContainer row = _funcRowBoxes[i];
				if (row == null || !GodotObject.IsInstanceValid(row))
				{
					continue;
				}
				if (i >= _funcSorted.Count)
				{
					row.Visible = false;
					continue;
				}
				row.Visible = true;
				FuncRank r = _funcSorted[i];
				Label name = _funcNames[i];
				Label val = _funcVals[i];
				ColorRect bar = _funcBars[i];
				if (name != null && GodotObject.IsInstanceValid(name))
				{
					name.Text = (i + 1) + ". " + Trunc(r.Name, 28);
					name.AddThemeColorOverride("font_color",
						i == 0 ? new Color(0.62f, 1f, 0.72f, 1f) : new Color(0.86f, 0.96f, 0.9f, 1f));
				}
				if (val != null && GodotObject.IsInstanceValid(val))
				{
					val.Text = FmtFuncValue(r);
				}
				if (bar != null && GodotObject.IsInstanceValid(bar))
				{
					double v = FuncSortValue(r);
					float w = (maxMetric > 0.0) ? (float)(PanelBarMaxPx * v / maxMetric) : 0f;
					if (w > 0f && w < 3f)
					{
						w = 3f;
					}
					bar.Size = new Vector2(w, 9f);
					bar.CustomMinimumSize = new Vector2(Math.Max(2f, w), 9f);
					bar.Color = i == 0 ? new Color(0.44f, 0.95f, 0.58f, 0.95f) : new Color(0.4f, 0.85f, 0.58f, 0.8f);
				}
			}

			PlacePanel();

			// ★★ v2.1.0 修复（"0 帧样本"的根因）：**这里绝不能再清 `_self*`**。
			//   旧实现在面板刷新（0.5s）末尾把 `_selfTicks/_selfFrames/_selfMaxUs` 归零，
			//   而报告是由 1Hz 的 Evaluate 触发的 —— 1s 是 0.5s 的整数倍，两者**同帧**时：
			//   面板刷新先把计数清零 → 紧接着 Evaluate 出报告 → 读到 `_selfFrames == 0`
			//   ⇒ 日志里大量 "本监测器自身开销: 均 0.00µs/帧（0 帧样本）"。
			//   自身开销窗口现在只由"报告发出"翻页，语义就是"自上次报告以来"，
			//   与 README §3 声称的口径一致。
		}
		catch (Exception ex)
		{
			if (_faultCode < 105)
			{
				_faultCode = 105;
				Log("面板刷新异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	private void PlacePanel()
	{
		try
		{
			if (_panel == null || !GodotObject.IsInstanceValid(_panel))
			{
				return;
			}
			Window root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root == null)
			{
				return;
			}
			Vector2 vp = root.GetVisibleRect().Size;
			Vector2 sz = _panel.Size;
			if (sz.X < 1f || sz.Y < 1f)
			{
				sz = _panel.GetCombinedMinimumSize();
			}
			_panel.Position = new Vector2(Math.Max(0f, vp.X - sz.X - PanelMarginPx), PanelMarginPx);
		}
		catch { }
	}

	// ================================================================ 函数耗时（数据源：内置 [TDPerf] 分析器）

	/// <summary>静默开启游戏内置分析器（DumpIntervalFrames 压成超大值 ⇒ 不打印 [TDPerf] 日志）。</summary>
	private void ApplyFunctionProfilerSettings()
	{
		if (!EnableFunctionProfiler)
		{
			return;
		}
		try
		{
			TowerDefensePerfProfiler.DumpIntervalFrames = ProfilerDumpIntervalFrames;  // 先压掉自动 dump
			TowerDefensePerfProfiler.DetailedHotPathMetrics = FunctionProfilerDetailed;
			TowerDefensePerfProfiler.Enabled = true;
		}
		catch (Exception ex)
		{
			TryErr("开启函数剖析失败（已吞）：" + ex.Message);
		}
	}

	private static bool ProfilerEnabled()
	{
		try
		{
			return TowerDefensePerfProfiler.Enabled;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>反射缓存：分析器私有静态 Metrics 字典 + Metric 结构体的 Ticks/Count 字段。</summary>
	private static bool EnsureProfilerReflection()
	{
		if (!_profProbed)
		{
			_profProbed = true;
			try
			{
				_profMetricsField = typeof(TowerDefensePerfProfiler).GetField("Metrics",
					BindingFlags.NonPublic | BindingFlags.Static);
			}
			catch
			{
				_profMetricsField = null;
			}
		}
		if (_profMetricsField == null)
		{
			return false;
		}
		if (_profTicksField == null)
		{
			try
			{
				Type[] args = _profMetricsField.FieldType.GetGenericArguments();
				_profMetricType = (args.Length == 2) ? args[1] : null;
				if (_profMetricType != null)
				{
					_profTicksField = _profMetricType.GetField("Ticks", BindingFlags.Public | BindingFlags.Instance);
					_profCountField = _profMetricType.GetField("Count", BindingFlags.Public | BindingFlags.Instance);
				}
			}
			catch
			{
				_profTicksField = null;
			}
		}
		return _profTicksField != null;
	}

	private static long ReadMetricLong(object boxedMetric, FieldInfo f)
	{
		try
		{
			if (f != null && boxedMetric != null)
			{
				object v = f.GetValue(boxedMetric);
				if (v != null)
				{
					return Convert.ToInt64(v);
				}
			}
		}
		catch { }
		return 0L;
	}

	/// <summary>采集函数耗时：读指标表 → 与上次快照差分 → 折成 ms/s（首个样本只做基线）。</summary>
	private void CollectFunctionMetrics(ulong now)
	{
		_funcBuf.Clear();
		try
		{
			if (!EnsureProfilerReflection())
			{
				_lastFuncSampleMs = now;
				return;
			}
			object raw = _profMetricsField.GetValue(null);
			IDictionary dict = raw as IDictionary;
			if (dict == null || dict.Count == 0)
			{
				_lastFuncSampleMs = now;
				return;
			}
			bool baselineOnly = (_lastFuncSampleMs == NoTs);
			double elapsedSec = (baselineOnly || _lastFuncSampleMs > now)
				? 0.5
				: Math.Max(0.25, (now - _lastFuncSampleMs) / 1000.0);

			// ★ v2.1.0：埋点覆盖率 —— 本窗口"全部埋点加起来"的耗时与有数据的项数。
			//   必须用**全部**条目求和，而不是 TopN 求和：报告里的 TopN 只是前几名，
			//   没有这个总和，读者无法判断"这个工具到底覆盖了帧预算的多少"。
			double windowTotalMs = 0.0;
			int windowActive = 0;

			foreach (DictionaryEntry e in dict)
			{
				string name = e.Key as string;
				if (string.IsNullOrEmpty(name) || e.Value == null)
				{
					continue;
				}
				double ms = ReadMetricLong(e.Value, _profTicksField) * 1000.0 / Stopwatch.Frequency;
				long calls = ReadMetricLong(e.Value, _profCountField);
				double prevMs;
				long prevCalls;
				_funcPrevMs.TryGetValue(name, out prevMs);
				_funcPrevCalls.TryGetValue(name, out prevCalls);
				_funcPrevMs[name] = ms;
				_funcPrevCalls[name] = calls;

				if (baselineOnly)
				{
					continue;   // 首个样本只记基线，避免把"自启用以来的累计"误报成瞬时速率
				}
				double dMs = Math.Max(0.0, ms - prevMs);
				long dCalls = Math.Max(0L, calls - prevCalls);
				// ★ 计数放在过滤之前：低于显示阈值的条目也是"真实发生的耗时"
				if (dMs > 0.0 || dCalls > 0L)
				{
					windowTotalMs += dMs;
					windowActive++;
				}
				if (dMs <= 0.0005 && dCalls <= 0L)
				{
					continue;
				}
				FuncRank r;
				r.Name = name;
				r.MsPerSec = dMs / elapsedSec;
				r.CallsPerSec = dCalls / elapsedSec;
				_funcBuf.Add(r);
			}
			_funcRegisteredCount = dict.Count;
			_funcActiveCount = windowActive;
			_funcTotalMsPerSec = baselineOnly ? 0.0 : (windowTotalMs / elapsedSec);
			_lastFuncSampleMs = now;
		}
		catch { }
	}

	/// <summary>报告/周期行用：函数样本过旧（>2.5s）就补采一次，避免用陈旧数据。</summary>
	private void EnsureFreshFunctionSamples(ulong now)
	{
		if (_funcBuf.Count == 0 || _lastFuncSampleMs == NoTs || now - _lastFuncSampleMs > 2500UL)
		{
			CollectFunctionMetrics(now);
		}
	}

	/// <summary>按当前排序依据排好序（_funcSorted）。</summary>
	private void BuildSortedFunctions()
	{
		_funcSorted.Clear();
		foreach (FuncRank r in _funcBuf)
		{
			_funcSorted.Add(r);
		}
		_funcSorted.Sort(delegate (FuncRank a, FuncRank b)
		{
			double av = FuncSortValue(a);
			double bv = FuncSortValue(b);
			int c = bv.CompareTo(av);
			if (c != 0)
			{
				return c;
			}
			return b.MsPerSec.CompareTo(a.MsPerSec);
		});
	}

	/// <summary>当前排序依据对应的数值（用于排序与条宽）。</summary>
	private double FuncSortValue(FuncRank r)
	{
		switch (_funcSortMode)
		{
			case 1:
				return r.CallsPerSec;
			case 2:
				return (r.CallsPerSec > 0.0001) ? (r.MsPerSec * 1000.0 / r.CallsPerSec) : 0.0;   // 单次 µs
			default:
				return r.MsPerSec;
		}
	}

	private static string FmtMsRate(FuncRank r)
	{
		// ★ v2.1.0 修复：ms/s 原来只保留 2 位小数 ⇒ 凡是 < 0.005ms/s 的作用域全被印成 "0.00ms/s"，
		//   而非战斗场景里整列都在这条线下（v2.0.0 那份日志的"函数列"就是这么失去意义的）。
		//   改为 3 位有效小数，并补一项 **单次 µs** —— 用于区分
		//   "调用极多但每次极便宜"（如 registry.rectQuery 300/s×1.3µs）与
		//   "调用很少但每次很贵"（如 adobeAnimate.render.dispatch 2.7/s×12ms）。
		string s = r.MsPerSec.ToString("F3", CultureInfo.InvariantCulture) + "ms/s";
		if (r.CallsPerSec >= 0.05)
		{
			s += "·" + FmtRate(r.CallsPerSec);
			if (r.MsPerSec > 0.0)
			{
				double us = r.MsPerSec * 1000.0 / r.CallsPerSec;
				s += "·" + ((us >= 1000.0)
					? (us / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + "ms/次"
					: us.ToString("F1", CultureInfo.InvariantCulture) + "µs/次");
			}
		}
		return s;
	}

	/// <summary>
	/// ★ v2.1.0：埋点覆盖率行（报告用）。
	/// 报告里 TopN 只是"前几名"，没有这行，读者会误以为埋点就是瓶颈所在。
	/// </summary>
	private void AppendCoverageLine()
	{
		try
		{
			if (_funcRegisteredCount <= 0)
			{
				return;
			}
			if (_funcTotalMsPerSec <= 0.0)
			{
				_sb.Append(P).Append("  埋点合计: 采样中（首个窗口只做基线）").Append('\n');
				return;
			}
			double pct = _funcTotalMsPerSec / 10.0;   // ms/s ÷ 1000ms × 100%
			_sb.Append(P).Append("  埋点合计 ").Append(_funcTotalMsPerSec.ToString("F2", CultureInfo.InvariantCulture))
				.Append("ms/s = 墙上时间 ").Append(pct.ToString("F2", CultureInfo.InvariantCulture))
				.Append("% · 有数据 ").Append(_funcActiveCount).Append("/").Append(_funcRegisteredCount)
				.Append(" 项（其余为未触发的埋点；Top").Append(Math.Min(ReportFuncTopN, _funcSorted.Count))
				.Append(" 之外未列出的部分已计入合计）").Append('\n');
			if (pct < 10.0)
			{
				_sb.Append(P).Append("  ⚠ 埋点覆盖面很低：本次帧时间主要不在这些埋点里，")
					.Append("请对照上面的「CPU 分项」判断是脚本还是渲染/GPU。").Append('\n');
			}
		}
		catch { }
	}

	/// <summary>面板数值列：按当前排序依据决定显示哪种数值在前（避免超宽）。</summary>
	private string FmtFuncValue(FuncRank r)
	{
		switch (_funcSortMode)
		{
			case 1:
				return (r.CallsPerSec >= 0.5 ? FmtRate(r.CallsPerSec) : "0/s")
					+ "·" + r.MsPerSec.ToString("F2", CultureInfo.InvariantCulture) + "ms/s";
			case 2:
				double avgUs = (r.CallsPerSec > 0.0001) ? (r.MsPerSec * 1000.0 / r.CallsPerSec) : 0.0;
				return FmtUs(avgUs) + (r.CallsPerSec >= 0.5 ? ("·" + FmtRate(r.CallsPerSec)) : "");
			default:
				return FmtMsRate(r);
		}
	}

	private static string FmtUs(double us)
	{
		if (us >= 1000.0)
		{
			return (us / 1000.0).ToString("F2", CultureInfo.InvariantCulture) + "ms";
		}
		return us.ToString("F1", CultureInfo.InvariantCulture) + "µs";
	}

	private static string FmtRate(double perSec)
	{
		if (perSec >= 1000.0)
		{
			return (perSec / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + "k/s";
		}
		if (perSec >= 100.0)
		{
			return ((int)Math.Round(perSec)).ToString(CultureInfo.InvariantCulture) + "/s";
		}
		return perSec.ToString("F1", CultureInfo.InvariantCulture) + "/s";
	}

	private static string Trunc(string s, int max)
	{
		if (string.IsNullOrEmpty(s) || s.Length <= max)
		{
			return s ?? "";
		}
		return s.Substring(0, max) + "…";
	}

	// ================================================================ 统计工具（FPS / 帧时间）

	private void WindowAvgFps(ulong now, double windowSec, out double avgFps, out int n)
	{
		avgFps = 0.0;
		n = 0;
		double sum = 0.0;
		ulong from = now >= (ulong)(windowSec * 1000.0) ? now - (ulong)(windowSec * 1000.0) : 0UL;
		for (int i = 0; i < _fCount; i++)
		{
			int idx = (_fHead - 1 - i + RingSize * 2) % RingSize;
			if (_fTs[idx] < from)
			{
				break;
			}
			sum += _fDt[idx];
			n++;
		}
		if (n > 0 && sum > 0.0)
		{
			avgFps = n * 1000.0 / sum;
		}
	}

	private void WindowStats(ulong now, double windowSec, out double avgMs, out double p95, out double maxMs)
	{
		double p50;
		double p99;
		int n;
		WindowStatsFull(now, windowSec, out avgMs, out p50, out p95, out p99, out maxMs, out n);
	}

	private void WindowStatsFull(ulong now, double windowSec, out double avgMs, out double p50, out double p95, out double p99, out double maxMs, out int n)
	{
		avgMs = 0.0;
		p50 = 0.0;
		p95 = 0.0;
		p99 = 0.0;
		maxMs = 0.0;
		n = 0;
		_scratch.Clear();
		ulong from = now >= (ulong)(windowSec * 1000.0) ? now - (ulong)(windowSec * 1000.0) : 0UL;
		double sum = 0.0;
		for (int i = 0; i < _fCount; i++)
		{
			int idx = (_fHead - 1 - i + RingSize * 2) % RingSize;
			if (_fTs[idx] < from)
			{
				break;
			}
			float dt = _fDt[idx];
			_scratch.Add(dt);
			sum += dt;
			if (dt > maxMs)
			{
				maxMs = dt;
			}
			n++;
		}
		if (n == 0)
		{
			return;
		}
		avgMs = sum / n;
		_scratch.Sort();
		p50 = Pct(_scratch, 0.50);
		p95 = Pct(_scratch, 0.95);
		p99 = Pct(_scratch, 0.99);
	}

	private static double Pct(List<float> sorted, double q)
	{
		if (sorted.Count == 0)
		{
			return 0.0;
		}
		int idx = (int)Math.Floor(q * (sorted.Count - 1));
		if (idx < 0)
		{
			idx = 0;
		}
		if (idx >= sorted.Count)
		{
			idx = sorted.Count - 1;
		}
		return sorted[idx];
	}

	private int CountSpikesSince(ulong now, double windowSec)
	{
		int n = 0;
		ulong from = now >= (ulong)(windowSec * 1000.0) ? now - (ulong)(windowSec * 1000.0) : 0UL;
		for (int i = 0; i < _spikeCount; i++)
		{
			int idx = (_spikeHead - 1 - i + SpikeLogSize * 2) % SpikeLogSize;
			if (_spikes[idx].Ts == NoTs || _spikes[idx].Ts < from)
			{
				break;
			}
			n++;
		}
		return n;
	}

	private static double SafeFps()
	{
		try
		{
			return Engine.GetFramesPerSecond();
		}
		catch
		{
			return 0.0;
		}
	}

	private bool IsInBattle()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return false;
			}
			TowerDefenseInGameSeedBank sb = mgr.GetSeedBank();
			if (sb == null || !GodotObject.IsInstanceValid(sb))
			{
				return false;
			}
			return sb.hasGameStarted;
		}
		catch
		{
			return false;
		}
	}

	private static string FmtDur(ulong ms)
	{
		ulong s = ms / 1000UL;
		if (s >= 3600UL)
		{
			return (s / 3600UL) + "h" + ((s / 60UL) % 60UL) + "m";
		}
		if (s >= 60UL)
		{
			return (s / 60UL) + "m" + (s % 60UL) + "s";
		}
		return s + "s";
	}

	/// <summary>把"引擎启动后的毫秒时点"折算成本地钟表时间（基于会话起点）。</summary>
	private string FmtClock(ulong ts)
	{
		try
		{
			if (ts >= _startMs)
			{
				return _wallBase.AddMilliseconds((double)(ts - _startMs)).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
			}
		}
		catch { }
		return "?";
	}

	// ================================================================ 报告文件

	private void InitReportFile()
	{
		if (true)    /* ★ 用户要求：日志默认关闭（原：!EnableLogReports） */
		{
			return;
		}
		if (_reportFileTried)
		{
			return;
		}
		_reportFileTried = true;
		try
		{
			string baseDir = ProjectSettings.GlobalizePath("user://PVZHE_Logs");
			if (string.IsNullOrEmpty(baseDir))
			{
				return;
			}
			string dir = Path.Combine(baseDir, "ModPerf");
			Directory.CreateDirectory(dir);
			_reportFile = Path.Combine(dir, "modperf_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".log");
		}
		catch (Exception ex)
		{
			TryErr("报告文件初始化失败：" + ex.Message);
		}
	}

	private void AppendFileHeader()
	{
		if (string.IsNullOrEmpty(_reportFile))
		{
			return;
		}
		try
		{
			StringBuilder sb = new StringBuilder();
			sb.Append("# ModPerf 记录文件（v2.1.2：函数耗时 + FPS + CPU 分项 + GC + 埋点覆盖率）· ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
			sb.Append("# 游戏 ").Append(SafeGameVersion()).Append(" · OS ").Append(SafeOsName()).Append('\n');
			sb.Append("# 说明：完整报告同时打印到 godot.log；本文件按时间顺序累积本次会话的全部记录。\n");
			AppendFile(sb.ToString());
		}
		catch { }
	}

	private static string SafeGameVersion()
	{
		try
		{
			Godot.Collections.Dictionary info = Engine.GetVersionInfo();
			if (info != null && info.ContainsKey("string"))
			{
				return "Godot " + info["string"].AsString();
			}
		}
		catch { }
		return "Godot ?";
	}

	private static string SafeOsName()
	{
		try
		{
			return OS.GetName();
		}
		catch
		{
			return "?";
		}
	}

	private void AppendFile(string text)
	{
		if (string.IsNullOrEmpty(_reportFile))
		{
			InitReportFile();
			if (string.IsNullOrEmpty(_reportFile))
			{
				return;
			}
		}
		try
		{
			File.AppendAllText(_reportFile, text + "\n", new UTF8Encoding(false));
		}
		catch { }
	}

	// ================================================================ 日志

	private void Log(string msg)
	{
		// ★ 2026-10-14：原先无条件输出，现统一受日志总开关控制
		if (true)    /* ★ 用户要求：日志默认关闭（原：!EnableLogReports） */ { return; }

		try { GD.Print(P + msg); } catch { }
	}

	private void TryErr(string msg)
	{
		// ★ 2026-10-14：原先无条件输出，现统一受日志总开关控制
		if (true)    /* ★ 用户要求：日志默认关闭（原：!EnableLogReports） */ { return; }

		try { GD.PrintErr(P + msg); } catch { }
	}
}
