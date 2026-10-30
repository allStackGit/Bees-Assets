"""Run only after explicit approval of a fresh 15-minute shot-experiment window.
Uses an existing Unity installation/cache and one CPU-only inference child. Never starts training.
"""
import os, sys, pathlib, json, time, ctypes, subprocess, shutil, hashlib
from ctypes import wintypes as w
BASE = pathlib.Path(__file__).resolve().parent
PROJECT = pathlib.Path(r"B:\Bees")
UNITY = pathlib.Path(r"C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe")
if sys.argv[1:] != ["--approved-15-minute-window"]:
    raise SystemExit("Explicit approval and --approved-15-minute-window are required; this is not an automatic continuation.")
started = time.time()
deadline = started + 900
root = BASE / time.strftime("run-%Y%m%dT%H%M%SZ", time.gmtime())
root.mkdir()
tmp = root / "tmp"
tmp.mkdir()
os.environ.update(CUDA_VISIBLE_DEVICES="", OMP_NUM_THREADS="1", MKL_NUM_THREADS="1",
                  OPENBLAS_NUM_THREADS="1", TMP=str(tmp), TEMP=str(tmp))
k = ctypes.WinDLL("kernel32", use_last_error=True)
k.SetPriorityClass(ctypes.c_void_p(-1), 0x40)
k.SetProcessAffinityMask(ctypes.c_void_p(-1), ctypes.c_size_t(15))
class BASIC(ctypes.Structure):
    _fields_=[("PerProcessUserTimeLimit",ctypes.c_int64),("PerJobUserTimeLimit",ctypes.c_int64),
              ("LimitFlags",w.DWORD),("MinimumWorkingSetSize",ctypes.c_size_t),("MaximumWorkingSetSize",ctypes.c_size_t),
              ("ActiveProcessLimit",w.DWORD),("Affinity",ctypes.c_size_t),("PriorityClass",w.DWORD),("SchedulingClass",w.DWORD)]
class IO(ctypes.Structure):
    _fields_=[(n,ctypes.c_uint64) for n in ["ReadOperationCount","WriteOperationCount","OtherOperationCount","ReadTransferCount","WriteTransferCount","OtherTransferCount"]]
class EXT(ctypes.Structure):
    _fields_=[("BasicLimitInformation",BASIC),("IoInfo",IO),("ProcessMemoryLimit",ctypes.c_size_t),
              ("JobMemoryLimit",ctypes.c_size_t),("PeakProcessMemoryUsed",ctypes.c_size_t),("PeakJobMemoryUsed",ctypes.c_size_t)]
k.CreateJobObjectW.restype=w.HANDLE
k.SetInformationJobObject.argtypes=[w.HANDLE,ctypes.c_int,ctypes.c_void_p,w.DWORD]
k.AssignProcessToJobObject.argtypes=[w.HANDLE,w.HANDLE]
k.TerminateJobObject.argtypes=[w.HANDLE,w.UINT]
k.QueryInformationJobObject.argtypes=[w.HANDLE,ctypes.c_int,ctypes.c_void_p,w.DWORD,ctypes.c_void_p]
k.CloseHandle.argtypes=[w.HANDLE]
job=k.CreateJobObjectW(None,None)
info=EXT()
info.BasicLimitInformation.LimitFlags=0x2000|0x200|0x10|0x20
info.BasicLimitInformation.Affinity=15
info.BasicLimitInformation.PriorityClass=0x40
info.JobMemoryLimit=16*1024**3
assert k.SetInformationJobObject(job,9,ctypes.byref(info),ctypes.sizeof(info)),ctypes.get_last_error()
def sizes(folder):
    output={}
    for parent,dirs,files in os.walk(folder):
        for name in files:
            path=os.path.join(parent,name)
            try: output[path]=os.stat(path).st_size
            except OSError: pass
    return output
def process_snapshot():
    command = "Get-CimInstance Win32_Process | Where-Object { $_.Name -like '*Bees*' -or $_.Name -eq 'Unity.exe' } | Select-Object ProcessId,Name,ExecutablePath,CreationDate | ConvertTo-Json -Compress"
    result=subprocess.run(["powershell","-NoProfile","-NonInteractive","-Command",command],
                          capture_output=True,text=True,creationflags=0x08000000,timeout=15)
    if result.returncode:
        raise RuntimeError("Cannot verify process isolation: "+result.stderr[:500])
    value=json.loads(result.stdout) if result.stdout.strip() else []
    return value if isinstance(value,list) else [value]
initial_processes=process_snapshot()
(root/"initial-processes.json").write_text(json.dumps(initial_processes,indent=2))
if any(p["Name"].lower()=="unity.exe" for p in initial_processes):
    raise SystemExit("An existing Unity Editor is running; do not interfere with its project.")
monitored=[PROJECT/"Library",PROJECT/"Temp",PROJECT/"UserSettings",
           pathlib.Path(os.environ.get("LOCALAPPDATA",""))/"Unity"]
baseline={}
for folder in monitored:
    baseline.update(sizes(folder))
config={"root":str(root),"python":sys.executable,"server_script":str(BASE/"frozen_shot_policy.py"),
        "model":str(BASE.parent/"probe"/"model.onnx"),"deps":str(BASE.parent/"probe"/"deps"),
        "model_sha256":"a5cee1b1dc7b145a5c71c2f9772249248d08425092920ae2e782da74725ae3b7",
        "deadline_epoch":deadline,"seed":13791}
config_path=root/"config.json"
config_path.write_text(json.dumps(config,indent=2))
source=BASE/"BeesShotExperiment.cs"
installed=PROJECT/"Assets"/"Editor"/"BeesShotExperiment.cs"
metadata=pathlib.Path(str(installed)+".meta")
if installed.exists() or metadata.exists():
    raise SystemExit("Diagnostic source/metadata path already exists; refusing to replace it.")
source_hash=hashlib.sha256(source.read_bytes()).hexdigest()
shutil.copyfile(source,installed)
args=[str(UNITY),"-batchmode","-nographics","-projectPath",str(PROJECT),"-logFile",str(root/"unity.log"),
      "-executeMethod","BeesShotExperiment.Begin","--bees-shot-experiment="+str(config_path),
      "--rl-matchup-mode=fixed","--rl-bee-ship-types=Bumblebee","--rl-human-ship-types=Gunship",
      "--rl-map-size=224","--rl-episode-timeout=120","--rl-health-ratio=0.00001",
      "--rl-static-obstacles=false","--rl-mining-asteroids=false","--rl-log-episodes=false"]
process=None
peak_disk=0
reason="completed"
exit_code=None
try:
    if time.time()>=deadline:
        raise RuntimeError("Deadline expired during preflight")
    with (root/"launcher.log").open("w") as log:
        process=subprocess.Popen(args,stdout=log,stderr=subprocess.STDOUT,env=os.environ,creationflags=0x08000004)
        if not k.AssignProcessToJobObject(job,w.HANDLE(process._handle)):
            process.kill()
            raise RuntimeError("Cannot enforce process resource limits")
        nt=ctypes.WinDLL("ntdll")
        nt.NtResumeProcess.argtypes=[w.HANDLE]
        if nt.NtResumeProcess(w.HANDLE(process._handle))!=0:
            raise RuntimeError("Cannot resume owned Unity process")
        (root/"owned-process.json").write_text(json.dumps({"pid":process.pid,"args":args,"started":started,"deadline":deadline,"source_sha256":source_hash},indent=2))
        while process.poll() is None:
            now=time.time()
            current={}
            for folder in monitored:
                current.update(sizes(folder))
            growth=sum(max(0,value-baseline.get(path,0)) for path,value in current.items())
            growth+=sum(sizes(root).values())
            peak_disk=max(peak_disk,growth)
            k.QueryInformationJobObject(job,9,ctypes.byref(info),ctypes.sizeof(info),None)
            (root/"progress.json").write_text(json.dumps({"utc":time.time(),"owned_pid":process.pid,"peak_commit_bytes":info.PeakJobMemoryUsed,"additional_disk_bytes":growth}))
            if now>=deadline or growth>=900*1024**2:
                reason="deadline" if now>=deadline else "disk guard"
                k.TerminateJobObject(job,99)
                break
            time.sleep(1)
        exit_code=process.wait()
finally:
    if process is not None and process.poll() is None:
        k.TerminateJobObject(job,99)
        process.wait()
    k.QueryInformationJobObject(job,9,ctypes.byref(info),ctypes.sizeof(info),None)
    k.CloseHandle(job)
    # Remove only files created by this invocation; never restore over other source or live timer data.
    if installed.exists() and hashlib.sha256(installed.read_bytes()).hexdigest()==source_hash:
        installed.unlink()
        if metadata.exists():
            metadata.unlink()
    result={"exit_code":exit_code,"reason":reason,"started_epoch":started,"finished_epoch":time.time(),
            "deadline_epoch":deadline,"peak_job_commit_bytes":info.PeakJobMemoryUsed,
            "peak_additional_disk_bytes":peak_disk,"cpu_affinity":15,"memory_limit_bytes":16*1024**3,
            "source_sha256":source_hash,"gpu":False,"complete_marker":(root/"complete.json").exists()}
    (root/"resources.json").write_text(json.dumps(result,indent=2))
    try:
        final=process_snapshot()
        (root/"final-processes.json").write_text(json.dumps(final,indent=2))
    except Exception as error:
        (root/"process-check-error.txt").write_text(str(error))
    print(json.dumps(result,indent=2))
