'use strict';

const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const test = require('node:test');

const {
    terminateProcessTree,
    installContinualLearningSupervisor,
    installServerLifetimeGuard,
} = require('../rlContinualLearningSupervisor');

function fakeChild(pid) {
    const child = new EventEmitter();
    child.pid = pid;
    child.exitCode = null;
    child.signalCode = null;
    child.killed = false;
    child.kill = () => {
        child.killed = true;
        child.signalCode = 'SIGTERM';
        return true;
    };
    return child;
}

test('supervisor restarts an unexpectedly exited continual-learning process', () => {
    const children = [];
    const scheduled = [];
    const spawnProcess = (executable, args, options) => {
        assert.equal(executable, 'python-test');
        assert.deepEqual(args, ['service.py']);
        assert.equal(options.cwd, '/assets');
        const child = fakeChild(100 + children.length);
        children.push(child);
        return child;
    };
    const schedule = (callback, delay) => {
        scheduled.push({ callback, delay });
        return scheduled.length;
    };
    const cancel = () => {};
    const server = {};
    const supervisor = installContinualLearningSupervisor(server, {
        spec: { executable: 'python-test', args: ['service.py'], cwd: '/assets' },
        env: {},
        spawnProcess,
        schedule,
        cancel,
        restartMs: 25,
        terminateProcessTree: child => child.kill(),
    });

    assert.equal(children.length, 1);
    children[0].exitCode = 7;
    children[0].emit('exit', 7, null);
    assert.equal(scheduled.length, 1);
    assert.equal(scheduled[0].delay, 25);

    scheduled[0].callback();
    assert.equal(children.length, 2);
    assert.equal(supervisor.child, children[1]);
    supervisor.stop();
    assert.equal(children[1].killed, true);
    assert.equal(supervisor.stopped, true);
});

test('disabled supervisor does not spawn a process', () => {
    const server = {};
    const supervisor = installContinualLearningSupervisor(server, {
        spec: null,
        spawnProcess: () => {
            throw new Error('must not spawn');
        },
    });
    assert.equal(supervisor, null);
    assert.equal(server.__beesContinualLearningSupervisor, null);
});


test('server lifetime guard stops continual learning when the supervised BeesServer exits', () => {
    let callback = null;
    let cancelled = false;
    let exited = false;
    let stopped = false;
    const supervisor = { stop() { stopped = true; } };
    const guard = installServerLifetimeGuard(supervisor, 4242, {
        pollMs: 10,
        schedule: scheduled => {
            callback = scheduled;
            return { unref() {} };
        },
        cancel: () => { cancelled = true; },
        isAlive: () => false,
        onServerExit: () => { exited = true; },
    });

    assert.equal(typeof callback, 'function');
    callback();
    assert.equal(stopped, true);
    assert.equal(cancelled, true);
    assert.equal(exited, true);
    guard.stop();
});

test('POSIX process-tree shutdown targets the service process group', () => {
    const calls = [];
    terminateProcessTree({ pid: 4242, kill() { calls.push(['fallback']); } }, {
        platform: 'linux',
        killProcess: (pid, signal) => calls.push([pid, signal]),
    });
    assert.deepEqual(calls, [[-4242, 'SIGTERM']]);
});

test('Windows process-tree shutdown uses taskkill tree mode', () => {
    const calls = [];
    terminateProcessTree({ pid: 5252, kill() { calls.push(['fallback']); } }, {
        platform: 'win32',
        spawnSyncProcess: (command, args) => {
            calls.push([command, ...args]);
            return { status: 0 };
        },
    });
    assert.deepEqual(calls, [['taskkill', '/PID', '5252', '/T', '/F']]);
});
