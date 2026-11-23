#!/usr/bin/env python3
"""Run an approved remote TinyBrains collector with Hub GPU execution available.

The existing native remote-rollout CLI owns device identity verification,
resource limits and its authenticated listener. This wrapper changes none of
those arguments or their order. It only exposes the Hub's local, trusted
project-neutral GPU module to an installed collector that supports it.
"""
from importlib.util import find_spec
import os
from pathlib import Path
import runpy
import sys


def main():
    # Running a Python file rather than -m changes sys.path[0]. Restore the
    # approved worker project's editable source discovery when it uses src/.
    source = Path.cwd() / "src"
    if (source / "ants_mapoca").is_dir():
        sys.path.insert(0,str(source))
    args = sys.argv[1:]
    # The trusted worker adapter creates the complete, already-validated
    # invocation. Never activate GPU features in CPU-only rollout services.
    gpu = any(a == "--device" and value == "cuda"
              for a,value in zip(args,args[1:]))
    if gpu and find_spec("ants_mapoca.shared_gpu_inference") is not None:
        hub_dir = str(Path(__file__).resolve().parent)
        sys.path.insert(0,hub_dir)
        if find_spec("experiment_hub_gpu_fusion") is not None:
            os.environ["TINYBRAINS_SHARED_GPU_EXECUTION"] = "1"
    sys.argv[0] = "ants_mapoca.remote_rollout"
    runpy.run_module("ants_mapoca.remote_rollout",run_name="__main__",alter_sys=True)


if __name__ == "__main__":
    main()
