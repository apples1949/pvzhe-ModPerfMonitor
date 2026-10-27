# -*- coding: utf-8 -*-
"""ModPerfMonitor 的构建入口 —— 转发给本目录的 build_mod.py。

为什么需要这个文件：`python mods/_build_locked.py <Mod> --install` 是**唯一安全**的构建入口
（它串行加锁；各 Mod 的构建脚本开头都会 `taskkill /F /IM dotnet.exe`，并行跑会互相杀）。
本 Mod 是唯一没有 build_and_install.py / build_pmod.py 的老工程，构建逻辑全在 build_mod.py，
所以这里只做参数转发，保证锁入口可用。

用法：
    python mods/_build_locked.py ModPerfMonitor --install
    python build_and_install.py [--install]        # 直接跑也行，但没有锁保护
"""
import os
import subprocess
import sys

BASE = os.path.dirname(os.path.abspath(__file__))

if __name__ == "__main__":
    sys.exit(subprocess.call(
        [sys.executable, os.path.join(BASE, "build_mod.py")] + sys.argv[1:],
        cwd=BASE))
