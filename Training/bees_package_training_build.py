"""Create a canonical ZIP artifact for BeesServer distributed build control."""

from __future__ import annotations

import argparse
import os
import shutil
import stat
import zipfile
from pathlib import Path
from typing import Optional, Sequence


def package_build(source: Path, output: Path, entrypoint: str) -> None:
    source = source.expanduser().resolve()
    output = output.expanduser().resolve()
    entry = (source / entrypoint).resolve()
    try:
        entry.relative_to(source)
    except ValueError as exc:
        raise ValueError("entrypoint must be inside the build directory") from exc
    if not source.is_dir():
        raise ValueError(f"build directory does not exist: {source}")
    if not entry.is_file():
        raise ValueError(f"build entrypoint does not exist: {entry}")

    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".tmp")
    if temporary.exists():
        temporary.unlink()

    with zipfile.ZipFile(
        temporary,
        "w",
        compression=zipfile.ZIP_DEFLATED,
        compresslevel=6,
        strict_timestamps=False,
    ) as bundle:
        for file_path in sorted(
            (item for item in source.rglob("*") if item.is_file()),
            key=lambda item: item.as_posix(),
        ):
            relative = file_path.relative_to(source).as_posix()
            info = zipfile.ZipInfo(relative)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = (file_path.stat().st_mode & 0xFFFF) << 16
            with file_path.open("rb") as source_handle, bundle.open(info, "w") as archive_handle:
                shutil.copyfileobj(source_handle, archive_handle, length=1024 * 1024)

    os.replace(temporary, output)


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Package one compiled Bees build for server distribution.")
    parser.add_argument("--source", required=True, help="Compiled build directory.")
    parser.add_argument("--output", required=True, help="Destination .zip path.")
    parser.add_argument(
        "--entrypoint",
        required=True,
        help="Executable path relative to --source, for example Bees.exe or Bees.x86_64.",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _parser().parse_args(argv)
    try:
        package_build(Path(args.source), Path(args.output), args.entrypoint)
    except (OSError, ValueError, zipfile.BadZipFile) as exc:
        print(f"error: {exc}")
        return 2
    output = Path(args.output).expanduser().resolve()
    print(f"Created canonical build archive: {output} ({output.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
