# -*- coding: utf-8 -*-
"""「Mod 性能监测器」Mod —— 编译 → 打包 → 装机 一条龙。

用法：
    python build_mod.py             # 只编译 + 打包
    python build_mod.py --install   # 继续装机（覆盖 Mods/ModPerfMonitor.pmod + 合并启用列表）

硬护栏（照技能文档）：
  · mod.json 必须在【根】且【唯一】
  · Runtime/ 下只允许 ModAssembly.dll（多一个可执行文件 → 整包被拒）
  · 包内绝不允许出现 .cs（ModLoader.IsExecutablePackageFile 白名单拒收）
  · 打包前后核对 编译产物 DLL md5 == 包内 DLL md5（防"新清单 + 旧 DLL"）

本机路径适配：与 TimeStop/build_and_install.py 同款 —— 全部路径来自 mods/_modenv.py，
换机器只改那一个文件。dotnet 必须显式传 -p:GodotRefDir=...（csproj 里默认值已清空）。
"""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile

# --- 本机路径适配（自动注入；换机器只改 mods/_modenv.py）---
_MROOT = os.path.dirname(os.path.abspath(__file__))
while not os.path.isfile(os.path.join(_MROOT, "_modenv.py")):
    _p = os.path.dirname(_MROOT)
    if _p == _MROOT:
        break
    _MROOT = _p
if _MROOT not in sys.path:
    sys.path.insert(0, _MROOT)
from _modenv import (  # noqa: E402
    CACHE, DOTNET, DOTNET_ROOT, MODS_DIR, WORKSPACE, ensure_mods, require_ref_dir,
)
# --- 适配块结束 ---

BASE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(BASE, "runtime_src")
DIST = os.path.join(BASE, "dist", "ModPerfMonitor.pmod")
LOG_FILE = os.path.join(WORKSPACE, ".cache", "build_logs", "modperfmonitor_build.log")

HOME = os.path.join(WORKSPACE, ".cache", "dotnet_home")
TMPD = os.path.join(HOME, "tmp")
NUGET = os.path.join(WORKSPACE, ".cache", "nuget")

MODS = MODS_DIR
MOD_ID = "modperfmonitor"
ASSEMBLY_NAME = "JTYModPerfMonitor"          # == csproj 的 <AssemblyName>
BINSRC = os.path.join(SRC, "bin", "Release", ASSEMBLY_NAME + ".dll")

out = []


def log(s):
    out.append(str(s))
    # 每条实时落盘：脚本被挂住时也能看到进度
    try:
        os.makedirs(os.path.dirname(LOG_FILE), exist_ok=True)
        with open(LOG_FILE, "w", encoding="utf-8") as f:
            f.write("\n".join(out))
    except Exception:
        pass


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for c in iter(lambda: f.read(65536), b""):
            h.update(c)
    return h.hexdigest()


def compile_asm():
    for d in (HOME, TMPD, NUGET):
        os.makedirs(d, exist_ok=True)
    env = dict(os.environ)
    env["DOTNET_ROOT"] = DOTNET_ROOT
    env["DOTNET_CLI_HOME"] = HOME
    env["TEMP"] = TMPD
    env["TMP"] = TMPD
    env["TMPDIR"] = TMPD
    env["NUGET_PACKAGES"] = NUGET
    env["DOTNET_NOLOGO"] = "1"
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    env["MSBUILDDISABLENODEREUSE"] = "1"

    csproj = next((f for f in sorted(os.listdir(SRC)) if f.endswith(".csproj")), None)
    if not csproj:
        log("[compile] %s 下找不到 .csproj" % SRC)
        return False
    assets = os.path.join(SRC, "obj", "project.assets.json")
    logs = []

    def run(args, timeout):
        p = subprocess.run([DOTNET] + args, cwd=SRC, env=env, capture_output=True,
                           text=True, encoding="utf-8", errors="replace", timeout=timeout)
        logs.append("$ dotnet " + " ".join(args) + "  -> RC=%d" % p.returncode)
        if p.stdout:
            logs.append(p.stdout.strip())
        if p.stderr:
            logs.append(p.stderr.strip())
        return p.returncode

    try:
        subprocess.run(["taskkill", "/F", "/IM", "dotnet.exe"], capture_output=True, timeout=30)
    except Exception:
        pass

    rc = 0
    if not os.path.isfile(assets):
        log("[compile] 无 obj/project.assets.json → 先 restore")
        rc = run(["restore", os.path.join(SRC, csproj)], 900)
    if rc == 0:
        rc = run(["build", os.path.join(SRC, csproj), "-c", "Release",
                  "-p:GodotRefDir=" + require_ref_dir(),
                  "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false",
                  "-v:q", "-nologo"], 600)
    with open(os.path.join(SRC, "build.log"), "w", encoding="utf-8") as f:
        f.write("\n".join(logs))
    log("[compile] RC=%d" % rc)

    # 兜底：前台被 SIGTERM 但 obj 产物已生成 —— 日志无 error CS 且 obj 存在就用它
    if rc != 0:
        obj_dll = os.path.join(SRC, "obj", "Release", ASSEMBLY_NAME + ".dll")
        joined = "\n".join(logs)
        if os.path.isfile(obj_dll) and ("error CS" not in joined):
            log("[compile] 前台 rc!=0 且无 error CS ⇒ 尝试用 obj 产物兜底")
            os.makedirs(os.path.dirname(BINSRC), exist_ok=True)
            shutil.copyfile(obj_dll, BINSRC)
            rc = 0
    return rc == 0


def package():
    if not os.path.isfile(BINSRC):
        log("[package] 缺编译产物 " + BINSRC)
        return False
    runtime_dir = os.path.join(BASE, "Runtime")
    os.makedirs(runtime_dir, exist_ok=True)
    dll = os.path.join(runtime_dir, "ModAssembly.dll")
    shutil.copyfile(BINSRC, dll)
    bin_md5 = md5(BINSRC)

    man = json.load(open(os.path.join(BASE, "mod.json"), encoding="utf-8"))
    names = sorted(os.listdir(runtime_dir))
    bad = [n for n in names if n != "ModAssembly.dll"]
    if bad:
        log("[package] ❌ Runtime/ 有非法文件（会整包拒收）: %s" % bad)
        return False
    actual = sorted(["Runtime/ModAssembly.dll"], key=str.lower)
    declared = sorted(man.get("resources", []), key=str.lower)
    if actual != declared:
        log("[package] ❌ resources 声明与实际不一致: %s vs %s" % (declared, actual))
        return False
    if man.get("runtimeAssembly") != "Runtime/ModAssembly.dll" or man.get("runtimeApiVersion") != 1:
        log("[package] ❌ runtime 字段不对")
        return False
    if man.get("runtimeEntryType") != "ModPerfMonitorEntry":
        log("[package] ❌ runtimeEntryType 与入口类名不一致")
        return False

    os.makedirs(os.path.dirname(DIST), exist_ok=True)
    # 固定时间戳 → 内容相同则字节稳定（幂等）
    with zipfile.ZipFile(DIST, "w", zipfile.ZIP_DEFLATED) as z:
        zi = zipfile.ZipInfo("mod.json", (1980, 1, 1, 0, 0, 0))
        zi.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(zi, open(os.path.join(BASE, "mod.json"), "rb").read())
        zi2 = zipfile.ZipInfo("Runtime/ModAssembly.dll", (1980, 1, 1, 0, 0, 0))
        zi2.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(zi2, open(dll, "rb").read())
    with zipfile.ZipFile(DIST) as z:
        nl = z.namelist()
        inpack_md5 = hashlib.md5(z.read("Runtime/ModAssembly.dll")).hexdigest()
    log("[package] %s  %d B  条目=%s" % (DIST, os.path.getsize(DIST), nl))
    log("[package] DLL md5 bin=%s 包内=%s" % (bin_md5, inpack_md5))
    if nl.count("mod.json") != 1 or nl[0] != "mod.json":
        log("[package] ❌ mod.json 不在根或不止一个")
        return False
    if any(n.endswith(".cs") for n in nl):
        log("[package] ❌ 包内含 .cs（会被判 undeclared executable 整包拒收）")
        return False
    if inpack_md5 != bin_md5:
        log("[package] ❌ 包内 DLL 与编译产物 md5 不一致")
        return False
    log("[package] ✅ 护栏全过（包内 DLL md5 == bin）")
    return True


def install():
    ensure_mods()
    dst = os.path.join(MODS, "ModPerfMonitor.pmod")
    shutil.copyfile(DIST, dst)
    log("[install] %s (%d B)" % (dst, os.path.getsize(dst)))
    # 清 ModsCache（0.30 按 sha+guid 分目录，一般不必；换了 DLL 身份名时清掉更稳）
    try:
        c = os.path.join(CACHE, MOD_ID)
        if os.path.isdir(c):
            import time
            ts = time.strftime("%H%M%S")
            target = c + ".bak_" + ts
            i = 1
            while os.path.exists(target):
                i += 1
                target = "%s.bak_%s_%d" % (c, ts, i)
            r = subprocess.run(["cmd", "/c", "move", c, target],
                               capture_output=True, errors="replace", encoding="gbk", timeout=60)
            log("[cache] move rc=%d" % r.returncode)
    except Exception as ex:
        log("[cache] 清缓存失败（已忽略）：%r" % ex)

    # ── 启用列表合并：0.30 对象格式 / 0.29 纯数组，两种都兼容 ──
    en = os.path.join(MODS, "enabled_mods.json")
    try:
        data = None
        if os.path.isfile(en):
            with open(en, encoding="utf-8") as f:
                data = json.load(f)
        if isinstance(data, dict):
            ids = data.get("enabledIds") or []
            low = [str(i).lower() for i in ids]
            if MOD_ID.lower() not in low:
                ids.append(MOD_ID)
                data["enabledIds"] = ids
                log("[install] enabled_mods.json(enabledIds) += " + MOD_ID)
            else:
                log("[install] enabled_mods.json 已含 " + MOD_ID)
            data.setdefault("schemaVersion", 2)
            data.setdefault("preferredOrder", [])
        elif isinstance(data, list):
            low = [str(i).lower() for i in data]
            if MOD_ID.lower() not in low:
                data.append(MOD_ID)
                log("[install] enabled_mods.json(数组) += " + MOD_ID)
            else:
                log("[install] enabled_mods.json 已含 " + MOD_ID)
        else:
            data = {"schemaVersion": 2, "enabledIds": [MOD_ID], "preferredOrder": []}
            log("[install] enabled_mods.json 不存在 → 新建（schemaVersion=2）")
        with open(en, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
    except Exception as e:
        log("[install] ❌ enabled_mods.json 合并失败: %r" % e)
        return False
    return True


def main():
    if "--install-only" in sys.argv:
        if not os.path.isfile(DIST):
            log("❌ 缺 dist 产物，先跑一次完整构建：" + DIST)
            return 1
        with zipfile.ZipFile(DIST) as z:
            log("[install-only] dist 包内 DLL md5=%s" % hashlib.md5(z.read("Runtime/ModAssembly.dll")).hexdigest())
        return 0 if install() else 1
    if not compile_asm():
        log("❌ 编译失败，终止")
        return 1
    if not package():
        return 1
    if "--install" in sys.argv:
        if not install():
            return 1
    return 0


if __name__ == "__main__":
    rc = 0
    try:
        rc = main()
    except Exception as e:
        import traceback
        log("EXC: %r" % e)
        log(traceback.format_exc())
        rc = 1
    os.makedirs(os.path.dirname(LOG_FILE), exist_ok=True)
    txt = "\n".join(out)
    with open(LOG_FILE, "w", encoding="utf-8") as f:
        f.write(txt)
    # 控制台是 GBK 时 print 含 ❌/µ 会 UnicodeEncodeError（脚本会以"异常"收尾，
    # 看起来像构建失败）。这里降到 errors="replace"，退出码仍由 rc 决定。
    try:
        sys.stdout.reconfigure(encoding=sys.stdout.encoding or "utf-8", errors="replace")
    except Exception:
        pass
    try:
        print(txt)
    except Exception:
        print(txt.encode("utf-8", "replace").decode("utf-8", "replace"))
    sys.exit(rc)
