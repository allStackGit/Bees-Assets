"""Bounded, CPU-only input perturbations from the completed shot experiment; no Unity or training."""
import os,sys,pathlib,json,time,ctypes,math,hashlib
from ctypes import wintypes as w
config=json.loads(pathlib.Path(sys.argv[1]).read_text())
root=pathlib.Path(config["root"])
os.environ.update(CUDA_VISIBLE_DEVICES="",OMP_NUM_THREADS="1",MKL_NUM_THREADS="1",OPENBLAS_NUM_THREADS="1")
k=ctypes.WinDLL("kernel32",use_last_error=True)
k.SetPriorityClass(ctypes.c_void_p(-1),0x40)
k.SetProcessAffinityMask(ctypes.c_void_p(-1),ctypes.c_size_t(15))
class BASIC(ctypes.Structure):
 _fields_=[("PerProcessUserTimeLimit",ctypes.c_int64),("PerJobUserTimeLimit",ctypes.c_int64),("LimitFlags",w.DWORD),("MinimumWorkingSetSize",ctypes.c_size_t),("MaximumWorkingSetSize",ctypes.c_size_t),("ActiveProcessLimit",w.DWORD),("Affinity",ctypes.c_size_t),("PriorityClass",w.DWORD),("SchedulingClass",w.DWORD)]
class IO(ctypes.Structure):
 _fields_=[(n,ctypes.c_uint64) for n in ["ReadOperationCount","WriteOperationCount","OtherOperationCount","ReadTransferCount","WriteTransferCount","OtherTransferCount"]]
class EXT(ctypes.Structure):
 _fields_=[("BasicLimitInformation",BASIC),("IoInfo",IO),("ProcessMemoryLimit",ctypes.c_size_t),("JobMemoryLimit",ctypes.c_size_t),("PeakProcessMemoryUsed",ctypes.c_size_t),("PeakJobMemoryUsed",ctypes.c_size_t)]
k.CreateJobObjectW.restype=w.HANDLE
k.SetInformationJobObject.argtypes=[w.HANDLE,ctypes.c_int,ctypes.c_void_p,w.DWORD]
k.AssignProcessToJobObject.argtypes=[w.HANDLE,w.HANDLE]
k.QueryInformationJobObject.argtypes=[w.HANDLE,ctypes.c_int,ctypes.c_void_p,w.DWORD,ctypes.c_void_p]
job=k.CreateJobObjectW(None,None);info=EXT()
info.BasicLimitInformation.LimitFlags=0x200|0x10|0x20
info.BasicLimitInformation.Affinity=15;info.BasicLimitInformation.PriorityClass=0x40;info.JobMemoryLimit=15*1024**3
assert k.SetInformationJobObject(job,9,ctypes.byref(info),ctypes.sizeof(info))
assert k.AssignProcessToJobObject(job,w.HANDLE(-1)),ctypes.get_last_error()
if time.time()>=config["deadline_epoch"]:raise SystemExit("Original approved deadline expired")

sys.path.append(config["deps"])
import numpy as np, torch, onnx, onnxruntime as ort
# Fail closed before ML-Agents imports its global default-device configuration.
torch.cuda.is_available=lambda: False
def deny_cuda(*args,**kwargs): raise RuntimeError("CUDA forbidden in this diagnostic")
torch._C._cuda_init=deny_cuda
torch.cuda._lazy_init=deny_cuda
torch.set_default_device("cpu")
import importlib.util, zipfile, io, pickle, collections
from onnx import helper,TensorProto
from mlagents_envs.base_env import ObservationSpec,DimensionProperty,ObservationType,ActionSpec
from mlagents.trainers.settings import NetworkSettings
torch.set_num_threads(1);torch.set_num_interop_threads(1)
module_path=root.parent.parent/"source"/"Training"/"bees_mlagents_structured_policy.py"
spec=importlib.util.spec_from_file_location("diagnostic_structured_policy",module_path)
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
def rebuild(storage,offset,shape,stride,*args):
 return {"storage":storage,"offset":offset,"shape":shape,"stride":stride}
class Reader(pickle.Unpickler):
 def find_class(self,module,name):
  if (module,name)==("collections","OrderedDict"):return collections.OrderedDict
  if (module,name)==("torch._utils","_rebuild_tensor_v2"):return rebuild
  if module=="torch" and name in ["FloatStorage","LongStorage"]:return name
  raise ValueError("Unexpected checkpoint global "+module+"."+name)
 def persistent_load(self,t):
  assert t[0]=="storage";return t
archive=root.parent.parent.parent/"bees-training-diagnostic-bees-v25-r5-s1-20261023T123535Z-f7eeaf83-20261030T100834Z.zip"
with zipfile.ZipFile(archive) as outer:
 info=outer.getinfo("checkpoint/BeesRL1v1/checkpoint.pt")
 assert info.file_size<128*1024**2
 payload=outer.read(info)
with zipfile.ZipFile(io.BytesIO(payload)) as ck:
 policy=Reader(io.BytesIO(ck.read("checkpoint/data.pkl"))).load()["Policy"]
 state={}
 for name,t in policy.items():
  token=t["storage"];dtype=np.dtype("<f4" if token[1]=="FloatStorage" else "<i8")
  data=np.frombuffer(ck.read("checkpoint/data/"+token[2]),dtype=dtype)
  values=np.lib.stride_tricks.as_strided(data[t["offset"]:],shape=t["shape"],strides=tuple(int(v)*dtype.itemsize for v in t["stride"])).copy()
  state[name]=torch.from_numpy(values)
observation=ObservationSpec(shape=(7743,),dimension_property=(DimensionProperty.NONE,),observation_type=ObservationType.DEFAULT,name="obs_0")
settings=NetworkSettings(normalize=True,hidden_units=128,num_layers=3)
body=module.BeesStructuredNetworkBody([observation],settings)
actions=module.BeesStructuredActionModel(128,ActionSpec(continuous_size=16,discrete_branches=(2,2,2,2,2,5)))
body.load_state_dict({k[len("network_body."):]:v for k,v in state.items() if k.startswith("network_body.")},strict=True)
actions.load_state_dict({k[len("action_model."):]:v for k,v in state.items() if k.startswith("action_model.")},strict=True)
body.eval();actions.eval()
assert all(v.device.type=="cpu" for v in body.state_dict().values())
assert all(v.device.type=="cpu" for v in actions.state_dict().values())
obs=[];trials=[]
with (root/"trace.jsonl").open() as f:
 for line in f:
  d=json.loads(line)
  if d["kind"]=="decision" and d["scenario"]["trial"] in [0,4,8,12,16,20,24,28] and d["step"]==0:
   obs.append(d["observations"]);trials.append(d["scenario"]["trial"])
assert len(obs)==8
x=np.asarray(obs,np.float32);mask=np.tile(np.asarray([1,1]+[1,0]*4+[1,0,0,0,0],np.float32),(8,1))
graph=onnx.load(config["model"])
for name in ["/Concat_6_output_0","/Concat_7_output_0"]:
 graph.graph.output.append(helper.make_tensor_value_info(name,TensorProto.FLOAT,None))
opts=ort.SessionOptions();opts.intra_op_num_threads=1;opts.inter_op_num_threads=1;opts.execution_mode=ort.ExecutionMode.ORT_SEQUENTIAL
session=ort.InferenceSession(graph.SerializeToString(),sess_options=opts,providers=["CPUExecutionProvider"])
assert time.time()<config["deadline_epoch"]
mu,std=session.run(["/Concat_6_output_0","/Concat_7_output_0"],{"obs_0":x,"action_masks":mask})
results=[]
for grad in [False,True]:
 assert time.time()<config["deadline_epoch"]
 with torch.set_grad_enabled(grad):
  encoded,_=body([torch.from_numpy(x)])
  distribution=actions._get_dists(encoded,torch.from_numpy(mask)).continuous
  actual=distribution.mean.detach().numpy();sigma=distribution.std.detach().numpy()
 results.append({"grad_enabled":grad,"max_mu_difference":float(np.max(np.abs(actual-mu))),"max_std_difference":float(np.max(np.abs(sigma-std))),
                 "aim_angles":[float(np.degrees(np.arctan2(row[3],row[2]))%360) for row in actual]})
k.QueryInformationJobObject(job,9,ctypes.byref(info),ctypes.sizeof(info),None)
report={"trials":trials,"comparisons":results,"torch_version":torch.__version__,"all_tensors_cpu":all(v.device.type=="cpu" for v in body.state_dict().values()),
        "additional_evaluations":24,"finished_epoch":time.time(),"deadline_epoch":config["deadline_epoch"],
        "peak_job_commit_bytes":info.PeakJobMemoryUsed,"source_sha256":hashlib.sha256(module_path.read_bytes()).hexdigest()}
(root/"torch-onnx-parity.json").write_text(json.dumps(report,indent=2));print(json.dumps(report,indent=2))
