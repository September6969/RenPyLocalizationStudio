"""Ren'Py RPA 安全读取器：不导入第三方代码，不允许 pickle 构造对象。"""
from __future__ import annotations

import hashlib
import io
import os
import pathlib
import pickle
import sys
import tempfile
import zlib

MAX_ENTRIES = 100_000
MAX_ENTRY_BYTES = 2 * 1024 * 1024 * 1024
MAX_TOTAL_BYTES = 16 * 1024 * 1024 * 1024
MAX_INDEX_BYTES = 256 * 1024 * 1024
COPY_BUFFER_BYTES = 1024 * 1024
WINDOWS_RESERVED_NAMES = {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))}


class RestrictedUnpickler(pickle.Unpickler):
    """RPA 索引只允许基础容器，禁止导入类和执行 reduce。"""

    def find_class(self, module, name):
        if module == "_codecs" and name == "encode":
            return safe_latin1_encode
        raise pickle.UnpicklingError(f"global object rejected: {module}.{name}")


def safe_latin1_encode(value, encoding="latin1", errors="strict"):
    if encoding.lower().replace("-", "") not in {"latin1", "iso88591"} or errors != "strict" or not isinstance(value, str):
        raise pickle.UnpicklingError("unsafe byte reconstruction rejected")
    return value.encode("latin1")


def decompress_index(stream):
    decoder, chunks, total = zlib.decompressobj(), [], 0
    while compressed := stream.read(COPY_BUFFER_BYTES):
        output = decoder.decompress(compressed, MAX_INDEX_BYTES - total + 1)
        total += len(output)
        if total > MAX_INDEX_BYTES or decoder.unconsumed_tail:
            raise ValueError("archive index exceeds safety limit")
        chunks.append(output)
    tail = decoder.flush(MAX_INDEX_BYTES - total + 1)
    total += len(tail)
    if total > MAX_INDEX_BYTES or not decoder.eof or decoder.unused_data:
        raise ValueError("invalid or oversized compressed archive index")
    chunks.append(tail)
    return b"".join(chunks)


def read_index(archive_path):
    archive_size = os.path.getsize(archive_path)
    with open(archive_path, "rb") as stream:
        header = stream.readline(4096)
        if len(header) >= 4096 or not header.endswith(b"\n"):
            raise ValueError("invalid RPA header")
        fields = header.split()
        if not fields or fields[0] not in {b"RPA-2.0", b"RPA-3.0", b"RPA-3.2"} or len(fields) < 2:
            raise ValueError("unsupported or malformed RPA header")
        index_offset = int(fields[1], 16)
        if index_offset < len(header) or index_offset >= archive_size:
            raise ValueError("archive index offset is outside the file")
        key_fields = fields[2:] if fields[0] == b"RPA-3.0" else fields[3:] if fields[0] == b"RPA-3.2" else []
        key = 0
        for value in key_fields:
            key ^= int(value, 16)
        stream.seek(index_offset)
        raw_index = RestrictedUnpickler(io.BytesIO(decompress_index(stream)), encoding="latin1").load()

    if not isinstance(raw_index, dict) or len(raw_index) > MAX_ENTRIES:
        raise ValueError("archive index has invalid type or too many entries")
    entries, total_size = [], 0
    for raw_name, segments in raw_index.items():
        if not isinstance(raw_name, (str, bytes)):
            raise ValueError("archive entry name has invalid type")
        name = raw_name.decode("utf-8") if isinstance(raw_name, bytes) else raw_name
        if not isinstance(segments, (list, tuple)) or not segments:
            raise ValueError("archive entry segments are malformed")
        parsed_segments, entry_size = [], 0
        for segment in segments:
            if not isinstance(segment, (list, tuple)) or len(segment) not in (2, 3):
                raise ValueError("archive entry index is malformed")
            raw_offset, raw_length = segment[:2]
            prefix = segment[2] if len(segment) == 3 else b""
            if not isinstance(raw_offset, int) or not isinstance(raw_length, int) or not isinstance(prefix, (str, bytes)):
                raise ValueError("archive entry metadata has invalid type")
            offset = raw_offset ^ key if fields[0] != b"RPA-2.0" else raw_offset
            length = raw_length ^ key if fields[0] != b"RPA-2.0" else raw_length
            prefix_bytes = prefix.encode("latin1") if isinstance(prefix, str) else prefix
            if offset < 0 or length < len(prefix_bytes):
                raise ValueError("archive entry segment exceeds safety limits")
            stored_length = length - len(prefix_bytes)
            if offset + stored_length > index_offset:
                raise ValueError("archive entry points outside the data region")
            entry_size += length
            if entry_size > MAX_ENTRY_BYTES:
                raise ValueError("archive entry exceeds safety limits")
            parsed_segments.append((offset, stored_length, prefix_bytes))
        total_size += entry_size
        if total_size > MAX_TOTAL_BYTES:
            raise ValueError("archive total output exceeds safety limit")
        entries.append((name, parsed_segments, entry_size))
    return entries


def safe_target(output_path, name):
    normalized, parts = name.replace("\\", "/"), name.replace("\\", "/").split("/")
    if normalized.startswith("/") or any(part in {"", ".", ".."} for part in parts):
        raise ValueError("unsafe archive path rejected: " + name)
    for part in parts:
        if any(ord(ch) < 32 or ch in '<>:"|?*' for ch in part) or part.endswith((" ", ".")):
            raise ValueError("invalid Windows archive path rejected: " + name)
        if part.split(".", 1)[0].upper() in WINDOWS_RESERVED_NAMES:
            raise ValueError("reserved Windows archive path rejected: " + name)
    target = os.path.abspath(os.path.join(output_path, *parts))
    output_real, parent_real = os.path.realpath(output_path), os.path.realpath(os.path.dirname(target))
    if os.path.commonpath((output_real, parent_real)) != output_real:
        raise ValueError("archive path or link escape rejected: " + name)
    return target


def validate_parent(output_path, target):
    output_real, parent_real = os.path.realpath(output_path), os.path.realpath(os.path.dirname(target))
    if os.path.commonpath((output_real, parent_real)) != output_real:
        raise ValueError("archive parent changed to an unsafe link target")


def copy_entry(archive_path, output_path, target, segments):
    parent = pathlib.Path(target).parent
    validate_parent(output_path, target)
    parent.mkdir(parents=True, exist_ok=True)
    validate_parent(output_path, target)
    descriptor, temporary = tempfile.mkstemp(prefix=f".{pathlib.Path(target).name}.", suffix=".tmp", dir=parent)
    try:
        with os.fdopen(descriptor, "wb") as output, open(archive_path, "rb") as source:
            for offset, stored_length, prefix in segments:
                output.write(prefix)
                source.seek(offset)
                remaining = stored_length
                while remaining:
                    chunk = source.read(min(COPY_BUFFER_BYTES, remaining))
                    if not chunk:
                        raise ValueError("archive entry ended unexpectedly")
                    output.write(chunk)
                    remaining -= len(chunk)
            output.flush()
            os.fsync(output.fileno())
        if os.path.exists(target):
            return False
        validate_parent(output_path, target)
        os.link(temporary, target)  # 同卷硬链接提供 create-new 语义，避免覆盖竞态。
        return True
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        while chunk := stream.read(COPY_BUFFER_BYTES):
            digest.update(chunk)
    return digest.hexdigest().upper()


def main():
    plan_only = len(sys.argv) == 4 and sys.argv[1] == "--plan"
    expected_hash = None
    if not plan_only and len(sys.argv) == 5 and sys.argv[1] == "--expected-sha256":
        expected_hash = sys.argv[2].upper()
        arguments = sys.argv[3:]
    else:
        arguments = sys.argv[2:] if plan_only else sys.argv[1:]
    if len(arguments) != 2:
        raise SystemExit("usage: safe_rpa_extract.py [--plan | --expected-sha256 HASH] <archive> <output>")
    archive_path, output_path = map(os.path.abspath, arguments)
    initial_hash = sha256_file(archive_path)
    if expected_hash is not None and initial_hash != expected_hash:
        raise ValueError("archive changed after plan confirmation")
    planned, targets = [], set()
    for name, segments, length in read_index(archive_path):
        target = safe_target(output_path, name)
        target_key = os.path.normcase(target)
        if target_key in targets:
            raise ValueError("duplicate archive target rejected: " + name)
        targets.add(target_key)
        planned.append((name, target, segments, length))
    archive_hash = sha256_file(archive_path)
    if archive_hash != initial_hash:
        raise ValueError("archive changed while its index was being read")
    if plan_only:
        print("ARCHIVE\t" + archive_hash)
        digest = hashlib.sha256(("ARCHIVE\t" + archive_hash + "\n").encode("utf-8"))
        for name, target, _segments, length in planned:
            row = f"PLAN\t{'SKIP' if os.path.exists(target) else 'WRITE'}\t{name}\t{target}\t{length}"
            digest.update((row + "\n").encode("utf-8"))
            print(row)
        print(f"SUMMARY\t{len(planned)}\t{digest.hexdigest().upper()}")
        return
    for name, target, segments, _length in planned:
        if expected_hash is not None and sha256_file(archive_path) != expected_hash:
            raise ValueError("archive changed during extraction")
        if os.path.exists(target):
            print("SKIP existing: " + target)
        elif copy_entry(archive_path, output_path, target, segments):
            print("OK " + target)


if __name__ == "__main__":
    main()
