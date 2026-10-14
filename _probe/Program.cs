// check_entry.cs —— 离线探针：验证 ModPerfMonitor.dll 能被游戏用反射找到并实例化。
// 用法： dotnet run --file check_entry.cs -- <refDir> <modDllPath>
//   refDir = 游戏 data_PlantsVsZombies_windows_x86_64 目录（GodotSharp.dll / PlantsVsZombies.dll 所在）
//
// 复刻 ModLoader 的入口发现逻辑（XWModCharacterCompanionRuntime.TryInitializeRuntimeEntry）：
//   type != null && !type.IsAbstract && typeof(IXWModRuntimeEntry).IsAssignableFrom(type)
//   && type.FullName == manifest.RuntimeEntryType
// 这里用反射复刻（不字面引用 Godot 类型），并额外核对三个接口方法签名。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

internal static class CheckEntry
{
	private static int fails;
	private static int checks;

	private static void Chk(bool ok, string name, string detail = "")
	{
		checks++;
		if (!ok)
		{
			fails++;
			Console.WriteLine("[FAIL] " + name + (detail.Length > 0 ? "  | " + detail : ""));
		}
		else
		{
			Console.WriteLine("[ ok ] " + name);
		}
	}

	private static int Main(string[] args)
	{
		try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
		if (args.Length < 2)
		{
			Console.WriteLine("usage: check_entry <refDir> <modDllPath>");
			return 2;
		}
		string refDir = args[0];
		string modDll = args[1];

		var refs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		refs["GodotSharp"] = Path.Combine(refDir, "GodotSharp.dll");
		refs["PlantsVsZombies"] = Path.Combine(refDir, "PlantsVsZombies.dll");
		foreach (var kv in refs)
		{
			Chk(File.Exists(kv.Value), "引用存在 " + kv.Key, kv.Value);
		}

		var alc = new AssemblyLoadContext("probe", true);
		alc.Resolving += (ctx, name) =>
		{
			string p;
			if (refs.TryGetValue(name.Name, out p) && File.Exists(p))
			{
				return ctx.LoadFromAssemblyPath(p);
			}
			return null;
		};

		Assembly godot = alc.LoadFromAssemblyPath(refs["GodotSharp"]);
		Assembly game = alc.LoadFromAssemblyPath(refs["PlantsVsZombies"]);
		Assembly mod = alc.LoadFromAssemblyPath(modDll);

		Type iface = game.GetType("PVZHE.ModEditor.ModSystem.IXWModRuntimeEntry");
		Chk(iface != null, "找到 IXWModRuntimeEntry 接口");

		// ── 入口发现（复刻 SingleOrDefault 过滤条件）──
		var candidates = mod.GetTypes()
			.Where(t => t != null && !t.IsAbstract && iface.IsAssignableFrom(t))
			.ToList();
		Console.WriteLine("       IXWModRuntimeEntry 实现类: " + string.Join(", ", candidates.Select(t => t.FullName)));
		var entry = candidates.Where(t => t.FullName == "ModPerfMonitorEntry").ToList();
		Chk(entry.Count == 1, "入口类唯一（FullName == ModPerfMonitorEntry）", "实际 " + entry.Count + " 个");
		if (entry.Count != 1)
		{
			Console.WriteLine("FAILS=" + fails + "/" + checks);
			return 1;
		}
		Type e = entry[0];
		Chk(e.IsPublic && !e.IsAbstract && e.IsSealed, "入口类 public sealed 非抽象");
		Chk(e.GetConstructor(Type.EmptyTypes) != null, "公开无参构造函数");

		// ── 接口方法签名 ──
		foreach (var m in iface.GetMethods())
		{
			var impl = e.GetMethod(m.Name, BindingFlags.Public | BindingFlags.Instance);
			Chk(impl != null, "实现方法 " + m.Name);
			if (impl != null)
			{
				Chk(impl.ReturnType == m.ReturnType, "返回类型 " + m.Name,
					impl.ReturnType.Name + " vs " + m.ReturnType.Name);
				var pa = impl.GetParameters();
				var pb = m.GetParameters();
				Chk(pa.Length == pb.Length, "参数个数 " + m.Name, pa.Length + " vs " + pb.Length);
			}
		}

		// ── 依赖面：只应引用 GodotSharp / PlantsVsZombies / 框架程序集 ──
		var refNames = mod.GetReferencedAssemblies().Select(a => a.Name).OrderBy(x => x).ToList();
		Console.WriteLine("       引用: " + string.Join(", ", refNames));
		Chk(refNames.Contains("PlantsVsZombies"), "引用 PlantsVsZombies");
		Chk(refNames.Contains("GodotSharp"), "引用 GodotSharp");
		var unexpected = refNames.Where(n => !n.StartsWith("System") && !n.StartsWith("Microsoft") && n != "netstandard"
			&& n != "mscorlib" && n != "GodotSharp" && n != "PlantsVsZombies").ToList();
		Chk(unexpected.Count == 0, "无额外托管依赖", string.Join(",", unexpected));

		// ── 实例化（Activator.CreateInstance，复刻游戏路径）──
		try
		{
			object inst = Activator.CreateInstance(e);
			Chk(inst != null, "Activator 实例化成功");
			Chk(iface.IsInstanceOfType(inst), "实例可转为 IXWModRuntimeEntry");
		}
		catch (Exception ex)
		{
			Chk(false, "Activator 实例化成功", ex.GetBaseException().Message);
		}

		Console.WriteLine("FAILS=" + fails + "/" + checks);
		return fails == 0 ? 0 : 1;
	}
}
