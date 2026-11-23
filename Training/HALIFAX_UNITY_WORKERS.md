# Halifax Unity simulation worker builds

This builds the existing Unity RL simulation independently of the native
rewrite and the running native-only Experiment Hub.

## Project layout

Keep the existing Unity project's matching Packages and ProjectSettings
directories. Do not substitute a blank project's package dependencies.

```text
~/ai/projects/bees-unity/
  Assets/              # allStackGit/Bees-Assets checkout
  Packages/            # original Unity project packages
  ProjectSettings/     # original Unity project settings
  Builds/              # compiled workers, outside Assets
  Logs/                # Unity build logs, outside Assets
```

Install the Unity Editor version specified by ProjectSettings/ProjectVersion.txt
(currently 6000.5.4f1), including Linux Dedicated Server Build Support.
Install Windows Build Support to build Windows workers on Halifax.
The command discovers a Unity Hub editor under ~/Unity/Hub/Editor or accepts
an explicit --unity-editor path or BEES_UNITY_EDITOR environment variable.

Node.js, Python 3 and Git are required for the wrapper. No ML-Agents Python
environment, ROCm learner, BeesServer or Hub service is needed to compile.

## Compile without changing training state

From the Unity project's Assets directory:

```sh
node Training/bees_build_workers.js --platform linux
# With Windows Build Support installed:
node Training/bees_build_workers.js --platform both
```

Artifacts appear under ../Builds/OfflineWorkers/<build-id>/, including
Linux/ and/or Windows/ Unity players, canonical worker ZIPs, and
worker-build-manifest.json with hashes, source commit, Unity version,
platform, build identity and dirty-worktree indicator.

Close the project in the Unity Editor before a batch build. An active
project lock is checked on Linux as well as Windows. Unity logs appear
under ../Logs/Build/.

IMPORTANT: Compiling workers does not change the active Bees release, run,
checkpoints, remote installations or Experiment Hub.

## Deploy through the existing Windows Bees cluster

Build both platforms from a clean, committed Unity source revision on Halifax:

```sh
node Training/bees_build_workers.js --platform both
```

Copy worker-build-manifest.json, rl-windows.zip and rl-linux.zip together
from the generated OfflineWorkers/<build-id>/ folder to a private incoming
folder on the Windows central Bees machine (for example Exeter). Sync that
machine's Bees-Assets checkout to the exact source commit before importing.
Keep its matching Unity Packages/manifest.json and ProjectSettings.

From the existing Windows Bees Unity project root:

```powershell
.\Assets\bees.ps1 build -OfflineWorkers 'B:\Bees\Incoming\offline-unity-ID\worker-build-manifest.json'
.\Assets\bees.ps1 start
.\Assets\bees.ps1 status -Once
```

The importer refuses uncommitted sources, mismatched commit/version/packages,
missing platforms, changed archive hashes and unsafe archive entries.
It copies both worker builds into immutable release storage, runs the normal
Bees run compatibility checks and compiled Windows environment validation,
then stages/distributes the release using the existing Bees control plane.
Use -NewRun only when an intentionally fresh training lineage is required.

This is a transitional route using the existing Windows Bees learner and
worker deployment. It does not register Bees execution in the separate
Halifax native-only Hub, or make the ROCm Bees learner operational.
