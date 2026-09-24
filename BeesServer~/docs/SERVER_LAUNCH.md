# Bees server launch options

`npm start` now runs `start-server.js`, which forwards normal server arguments to `server.js` and adds launcher-only background/logging options.

## Normal foreground server

```sh
npm start
```

Existing direct launches such as `node server.js` still work.

## Run after SSH disconnect

```sh
npm start -- --background
```

Short form:

```sh
npm start -- -b
```

Background mode starts the Node server as a detached process, disconnects it from the SSH session's standard streams, prints its PID, and lets the launcher exit. The server therefore continues running after a normal SSH logout.

## Write stdout and stderr to a log

Use the default `logs/bees-server.log`:

```sh
npm start -- --log
```

Use a custom file:

```sh
npm start -- --log=logs/development.log
```

Log output is appended rather than replacing the previous file. The log directory is created automatically.

## Background server with logging

Recommended for an SSH-launched long-running server:

```sh
npm start -- --background --log
```

or:

```sh
npm start -- --background --log=logs/development.log
```

For the existing test-server launch:

```sh
npm run start:test -- --background --log=logs/test-server.log
```

To watch a log:

```sh
tail -f logs/bees-server.log
```

To stop a background server, use the PID printed by the launcher:

```sh
kill <pid>
```

The launcher does not automatically restart a crashed process or start it after a machine reboot. A system service such as systemd should be used if those behaviours are required.


## Server-controlled distributed training

When `BEES_RL_TRAINER_CONTROL=1`, BeesServer also owns the desired state for dedicated rollout machines. The control service defaults to loopback port 7148 and is intended to be reached through SSH forwarding. Each rollout node runs `Training/bees_trainer_agent.py`, renews a short server lease, and stops its dedicated training process if that lease expires. When BeesServer restarts, its new server epoch causes connected agents to reconcile the current build/configuration and restart training.

The server publishes a content-addressed manifest for the canonical training build configured by `BEES_RL_TRAINING_ENV` / `BEES_RL_TRAINER_BUILD_DIR`. Remote nodes download missing files, verify every SHA-256 and size, stage the complete build, then switch atomically. If the central trainer is not Linux, `BEES_RL_LINUX_TRAINER_BUILD_DIR` is required by default so Linux rollout nodes receive the equivalent build.

The trainer-control bearer token comes from `BEES_RL_CONTROL_TOKEN_FILE`, or falls back to `BEES_RL_WAN_AUTH_TOKEN_FILE`. Keep the HTTP control endpoint on loopback and use SSH forwarding rather than exposing it directly.

Background continual-learning supervision is tied to the BeesServer process. If the spawned BeesServer PID disappears, its watchdog stops the central continual-learning child instead of continuing training independently.
