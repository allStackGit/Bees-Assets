# Isolated frozen-policy inference process for the shot characterization harness.
# Launched only by the explicitly invoked Unity diagnostic, inside its Windows resource job.
import os, sys, pathlib, json, time, hashlib
os.environ.update(CUDA_VISIBLE_DEVICES="", OMP_NUM_THREADS="1", MKL_NUM_THREADS="1", OPENBLAS_NUM_THREADS="1")
config = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
sys.path.insert(0, config["deps"])
import numpy as np
import onnxruntime as ort
model = pathlib.Path(config["model"])
if hashlib.sha256(model.read_bytes()).hexdigest() != config["model_sha256"]:
    raise RuntimeError("Frozen model hash mismatch")
options = ort.SessionOptions()
options.intra_op_num_threads = 1
options.inter_op_num_threads = 1
options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
session = ort.InferenceSession(str(model), sess_options=options, providers=["CPUExecutionProvider"])
if session.get_providers() != ["CPUExecutionProvider"]:
    raise RuntimeError("Unexpected inference provider")
print(json.dumps({"ready": True, "providers": session.get_providers()}), flush=True)
calls = 0
for line in sys.stdin:
    if time.time() >= config["deadline_epoch"]:
        raise RuntimeError("Approved deadline reached")
    request = json.loads(line)
    if request.get("stop"):
        break
    calls += 1
    if calls > 1600:
        raise RuntimeError("1600-decision experiment cap reached")
    obs = np.asarray(request["observations"], dtype=np.float32).reshape(1, 7743)
    mask = np.asarray(request["action_masks"], dtype=np.float32).reshape(1, 15)
    if not np.isfinite(obs).all():
        raise RuntimeError("Non-finite Unity observation")
    actions, discrete = session.run(
        ["deterministic_continuous_actions", "deterministic_discrete_actions"],
        {"obs_0": obs, "action_masks": mask},
    )
    result = {"sequence": request["sequence"], "continuous": actions[0].astype(float).tolist(),
              "discrete": np.asarray(discrete[0]).astype(int).ravel().tolist()}
    print(json.dumps(result, separators=(",", ":")), flush=True)
