"""Native ML-Agents distributed rollout support for Bees.

The central Python process remains the only PPO learner and optimizer owner. A configurable
suffix of ML-Agents worker IDs is marked external: those worker subprocesses open the normal
ML-Agents gRPC endpoint but do not launch a local Unity executable. Remote machines connect
through loopback-only SSH forwards and therefore participate in the exact same on-policy
SubprocessEnvManager as local environments.

This module deliberately does not define a custom trajectory format, average networks, or allow
remote machines to own optimizer/checkpoint state.
"""

from __future__ import annotations

import contextlib
from dataclasses import dataclass
from typing import Callable, List, Optional, Sequence, Tuple


EXPECTED_MLAGENTS_VERSION = "1.1.0"
EXTERNAL_ENVS_FLAG = "--bees-external-envs"
DEFAULT_NUM_ENVS = 1
DEFAULT_BASE_PORT = 5005


@dataclass(frozen=True)
class DistributedOptions:
    external_envs: int = 0

    @property
    def enabled(self) -> bool:
        return self.external_envs > 0


def _positive_or_zero_integer(value: str, flag: str) -> int:
    try:
        parsed = int(value)
    except ValueError as exc:
        raise SystemExit(f"{flag} requires a whole number; got {value!r}.") from exc
    if parsed < 0:
        raise SystemExit(f"{flag} requires a non-negative whole number; got {value!r}.")
    return parsed


def extract_distributed_options(argv: Sequence[str]) -> Tuple[List[str], DistributedOptions]:
    cleaned: List[str] = []
    external_envs: Optional[int] = None
    index = 0
    while index < len(argv):
        argument = argv[index]
        if argument == EXTERNAL_ENVS_FLAG:
            if index + 1 >= len(argv) or argv[index + 1].startswith("--"):
                raise SystemExit(f"{EXTERNAL_ENVS_FLAG} requires a value.")
            if external_envs is not None:
                raise SystemExit(f"{EXTERNAL_ENVS_FLAG} may be specified only once.")
            external_envs = _positive_or_zero_integer(argv[index + 1], EXTERNAL_ENVS_FLAG)
            index += 2
            continue

        prefix = EXTERNAL_ENVS_FLAG + "="
        if argument.startswith(prefix):
            if external_envs is not None:
                raise SystemExit(f"{EXTERNAL_ENVS_FLAG} may be specified only once.")
            value = argument[len(prefix) :]
            if not value:
                raise SystemExit(f"{EXTERNAL_ENVS_FLAG} requires a value.")
            external_envs = _positive_or_zero_integer(value, EXTERNAL_ENVS_FLAG)
            index += 1
            continue

        cleaned.append(argument)
        index += 1

    return cleaned, DistributedOptions(external_envs=external_envs or 0)


def _integer_trainer_arg(
    argv: Sequence[str],
    flag: str,
    default: int,
    *,
    minimum: int = 0,
) -> int:
    value: Optional[str] = None
    for index, argument in enumerate(argv):
        if argument == flag:
            if index + 1 >= len(argv):
                raise SystemExit(f"{flag} requires a value.")
            value = argv[index + 1]
            break
        prefix = flag + "="
        if argument.startswith(prefix):
            value = argument[len(prefix) :]
            break
    if value is None:
        return default
    try:
        parsed = int(value)
    except ValueError as exc:
        raise SystemExit(f"{flag} requires a whole number; got {value!r}.") from exc
    if parsed < minimum:
        raise SystemExit(f"{flag} must be at least {minimum}; got {parsed}.")
    return parsed


def training_topology(argv: Sequence[str], options: DistributedOptions) -> Tuple[int, int, Tuple[int, ...]]:
    total_envs = _integer_trainer_arg(argv, "--num-envs", DEFAULT_NUM_ENVS, minimum=1)
    base_port = _integer_trainer_arg(argv, "--base-port", DEFAULT_BASE_PORT, minimum=1)
    if options.external_envs > total_envs:
        raise SystemExit(
            f"{EXTERNAL_ENVS_FLAG}={options.external_envs} exceeds --num-envs={total_envs}."
        )
    first_external = total_envs - options.external_envs
    worker_ids = tuple(range(first_external, total_envs))
    return total_envs, base_port, worker_ids


def external_worker_ports(base_port: int, worker_ids: Sequence[int]) -> Tuple[int, ...]:
    return tuple(base_port + int(worker_id) for worker_id in worker_ids)


class _LoopbackRpcCommunicatorMixin:
    """Bind external-worker gRPC endpoints to loopback so SSH/VPN forwarding is mandatory."""

    def create_server(self) -> None:
        import grpc
        from concurrent.futures import ThreadPoolExecutor
        from mlagents_envs.communicator_objects.unity_to_external_pb2_grpc import (
            add_UnityToExternalProtoServicer_to_server,
        )
        from mlagents_envs.rpc_communicator import UnityToExternalServicerImplementation
        from mlagents_envs.exception import UnityWorkerInUseException

        self.check_port(self.port)
        try:
            self.server = grpc.server(
                thread_pool=ThreadPoolExecutor(max_workers=10),
                options=(("grpc.so_reuseport", 1),),
            )
            self.unity_to_external = UnityToExternalServicerImplementation()
            add_UnityToExternalProtoServicer_to_server(self.unity_to_external, self.server)
            bound_port = self.server.add_insecure_port("127.0.0.1:" + str(self.port))
            if bound_port != self.port:
                raise RuntimeError(
                    f"ML-Agents external worker could not bind loopback port {self.port}."
                )
            self.server.start()
            self.is_open = True
        except Exception as exc:
            raise UnityWorkerInUseException(self.worker_id) from exc


def _external_environment_type():
    from mlagents_envs.environment import UnityEnvironment
    from mlagents_envs.rpc_communicator import RpcCommunicator

    class LoopbackRpcCommunicator(_LoopbackRpcCommunicatorMixin, RpcCommunicator):
        pass

    class ExternalUnityEnvironment(UnityEnvironment):
        @staticmethod
        def _get_communicator(worker_id, base_port, timeout_wait):
            return LoopbackRpcCommunicator(worker_id, base_port, timeout_wait)

    return ExternalUnityEnvironment


@contextlib.contextmanager
def _suppress_executable_launch():
    """Let UnityEnvironment build its normal protocol state without starting a local player."""
    from mlagents_envs import env_utils

    original = env_utils.launch_executable
    env_utils.launch_executable = lambda *_args, **_kwargs: None
    try:
        yield
    finally:
        env_utils.launch_executable = original


def _create_external_environment(
    *,
    env_path: str,
    worker_id: int,
    seed: int,
    num_areas: int,
    no_graphics: bool,
    no_graphics_monitor: bool,
    base_port: Optional[int],
    env_args: Optional[List[str]],
    side_channels,
    timeout_wait: int,
):
    ExternalUnityEnvironment = _external_environment_type()
    with _suppress_executable_launch():
        return ExternalUnityEnvironment(
            file_name=env_path,
            worker_id=worker_id,
            seed=seed + worker_id,
            num_areas=num_areas,
            no_graphics=no_graphics,
            no_graphics_monitor=no_graphics_monitor,
            base_port=base_port,
            additional_args=env_args,
            side_channels=side_channels,
            log_folder=None,
            timeout_wait=timeout_wait,
        )


def install_external_worker_factory(
    *,
    total_envs: int,
    external_worker_ids: Sequence[int],
) -> Callable:
    """Patch ML-Agents environment creation so selected IDs wait for remote Unity players."""
    import mlagents.trainers
    import mlagents.trainers.learn as learn

    actual = mlagents.trainers.__version__
    if actual != EXPECTED_MLAGENTS_VERSION:
        raise RuntimeError(
            "Distributed Bees training patches ML-Agents environment creation for version "
            f"{EXPECTED_MLAGENTS_VERSION}, but version {actual} is installed."
        )

    normalized = tuple(sorted(set(int(value) for value in external_worker_ids)))
    if any(worker_id < 0 or worker_id >= total_envs for worker_id in normalized):
        raise RuntimeError("External ML-Agents worker IDs are outside the configured environment range.")
    external_set = frozenset(normalized)
    original = learn.create_environment_factory

    def distributed_create_environment_factory(
        env_path,
        no_graphics,
        no_graphics_monitor,
        seed,
        num_areas,
        timeout_wait,
        start_port,
        env_args,
        log_folder,
    ):
        if external_set and not env_path:
            raise RuntimeError(
                "Distributed external workers require --env so remote Unity executables share the "
                "same build as local workers."
            )
        local_factory = original(
            env_path,
            no_graphics,
            no_graphics_monitor,
            seed,
            num_areas,
            timeout_wait,
            start_port,
            env_args,
            log_folder,
        )

        def create_environment(worker_id, side_channels):
            if worker_id not in external_set:
                return local_factory(worker_id, side_channels)
            return _create_external_environment(
                env_path=env_path,
                worker_id=worker_id,
                seed=seed,
                num_areas=num_areas,
                no_graphics=no_graphics,
                no_graphics_monitor=no_graphics_monitor,
                base_port=start_port,
                env_args=env_args,
                side_channels=side_channels,
                timeout_wait=timeout_wait,
            )

        return create_environment

    learn.create_environment_factory = distributed_create_environment_factory
    return original


def restore_external_worker_factory(original: Optional[Callable]) -> None:
    if original is None:
        return
    import mlagents.trainers.learn as learn

    learn.create_environment_factory = original


def describe_topology(base_port: int, total_envs: int, external_worker_ids: Sequence[int]) -> str:
    external = tuple(external_worker_ids)
    local_count = total_envs - len(external)
    if not external:
        return f"local_envs={total_envs} external_envs=0"
    mappings = ", ".join(
        f"worker {worker_id}->127.0.0.1:{base_port + worker_id}"
        for worker_id in external
    )
    return f"local_envs={local_count} external_envs={len(external)} [{mappings}]"
