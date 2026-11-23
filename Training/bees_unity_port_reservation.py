"""One OS-portable reservation for a whole Unity communicator worker block.

A process-held advisory file lock protects inter-job allocations, while an
OS socket bind checks that no unmanaged Unity/ML-Agents instance already owns
one of the reserved ports.  Both Linux and Windows release the lock on close.
"""
from __future__ import annotations

import hashlib
import os
from pathlib import Path
import socket

PORT_BASE = 22000
PORT_WIDTH = 128
PORT_LAST = 54000


def _try_lock(handle) -> None:
    if os.name == "nt":
        import msvcrt
        if handle.seek(0, os.SEEK_END) == 0:
            handle.write(b"\\0")
            handle.flush()
        handle.seek(0)
        msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
    else:
        import fcntl
        fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)


def reserve_base_port(root: Path, job_id: str, num_envs: int):
    if not root.is_absolute() or not 1 <= num_envs <= 64:
        raise ValueError("Unity communicator allocation needs an absolute owner and 1–64 environments.")
    lockroot = root.parent / "_bees_native_ports"
    lockroot.mkdir(mode=0o700, exist_ok=True)
    if lockroot.is_symlink():
        raise RuntimeError("Unity communicator lock directory must not be redirected.")
    capacity = (PORT_LAST - PORT_BASE) // PORT_WIDTH
    start = int(hashlib.sha256(job_id.encode("ascii")).hexdigest()[:8], 16) % capacity
    for offset in range(capacity):
        slot = (start + offset) % capacity
        port = PORT_BASE + slot * PORT_WIDTH
        lock = (lockroot / f"{port}.lock").open("a+b")
        try:
            _try_lock(lock)
        except OSError:
            lock.close()
            continue
        checks = []
        try:
            for number in range(num_envs + 2):
                sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                checks.append(sock)
                sock.bind(("127.0.0.1", port + number))
            return port, lock
        except OSError:
            lock.close()
            continue
        finally:
            for sock in checks:
                sock.close()
    raise RuntimeError("No isolated Unity communicator port block remains available.")
