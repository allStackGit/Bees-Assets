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
sys.path.insert(0,config["deps"])
import numpy as np
import onnx
from onnx import helper,TensorProto
import onnxruntime as ort
first=None;prior=0
with (root/"trace.jsonl").open() as f:
 for line in f:
  d=json.loads(line)
  if d["kind"]=="decision":
   prior+=1
   if first is None:first=d
assert prior+160<=1600
model=pathlib.Path(config["model"])
assert hashlib.sha256(model.read_bytes()).hexdigest()==config["model_sha256"]
graph=onnx.load(str(model))
extra=["/Concat_6_output_0","/Concat_7_output_0","/Softmax_output_0"]
available={name for node in graph.graph.node for name in node.output}
for name in extra:
 assert name in available,name
 graph.graph.output.append(helper.make_tensor_value_info(name,TensorProto.FLOAT,None))
options=ort.SessionOptions();options.intra_op_num_threads=1;options.inter_op_num_threads=1;options.execution_mode=ort.ExecutionMode.ORT_SEQUENTIAL
ort.set_seed(config["seed"])
session=ort.InferenceSession(graph.SerializeToString(),sess_options=options,providers=["CPUExecutionProvider"])
assert session.get_providers()==["CPUExecutionProvider"]
base=np.asarray(first["observations"],np.float32);mask=np.asarray([1,1,1,0,1,0,1,0,1,0,0,0,0],np.float32)[None,:]
names=["deterministic_continuous_actions","continuous_actions"]+extra
used=0
def evaluate(obs):
 global used
 assert time.time()<config["deadline_epoch"]
 used+=1
 assert used<=160
 return session.run(names,{"obs_0":obs[None,:],"action_masks":mask})
def angle(action):
 return math.degrees(math.atan2(float(action[3]),float(action[2])))%360
conditions=[]
for distance in [40,100]:
 for bearing in range(0,360,45):
  obs=base.copy();rad=math.radians(bearing)
  x=distance*math.cos(rad);y=distance*math.sin(rad)
  obs[2960]=x/(abs(x)+40);obs[2961]=y/(abs(y)+40)
  det,_,mu,std,fire=evaluate(obs)
  item={"distance":distance,"bearing":bearing,"deterministic_angle":angle(det[0]),
        "error":abs((angle(det[0])-bearing+180)%360-180),"fire_probability":float(fire[0,1]),
        "raw_mu":mu[0,2:4].astype(float).tolist(),"raw_std":std[0,2:4].astype(float).tolist(),"stochastic_angles":[]}
  for _ in range(8):
   _,sample,_,_,_=evaluate(obs);item["stochastic_angles"].append(angle(sample[0]))
  conditions.append(item)
identities=[]
for self_id in [-.75,-.25,.25,.75]:
 for enemy_id in [-.75,-.25,.25,.75]:
  obs=base.copy();obs[0]=self_id;obs[2959]=enemy_id;obs[2960]=0;obs[2961]=100/140
  det,_,mu,std,fire=evaluate(obs)
  identities.append({"self_id":self_id,"enemy_id":enemy_id,"angle":angle(det[0]),"fire_probability":float(fire[0,1])})
k.QueryInformationJobObject(job,9,ctypes.byref(info),ctypes.sizeof(info),None)
result={"prior_decisions":prior,"new_observation_evaluations":used,"total_evaluations":prior+used,
        "conditions":conditions,"identity_conditions":identities,"peak_job_commit_bytes":info.PeakJobMemoryUsed,
        "finished_epoch":time.time(),"deadline_epoch":config["deadline_epoch"],"providers":session.get_providers(),
        "limitation":"Input perturbations of one captured Unity vector. Only enemy relative position changes in bearing/distance comparisons; target hull heading and other channels remain fixed. These are not new simulated encounters or hit probabilities."}
(root/"input-followup.json").write_text(json.dumps(result,indent=2))
print(json.dumps(result,indent=2))
