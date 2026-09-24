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
