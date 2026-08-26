"""安全解包入口：先验证全部归档路径，再写入项目内目标目录。"""
import importlib.util
import os
import pathlib
import sys


def load_rpatool(path):
    spec = importlib.util.spec_from_file_location("rls_rpatool", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    if len(sys.argv) != 4:
        raise SystemExit("usage: safe_rpa_extract.py <rpatool> <archive> <output>")
    tool_path, archive_path, output_path = map(os.path.abspath, sys.argv[1:])
    output_root = output_path.rstrip(os.sep) + os.sep
    archive = load_rpatool(tool_path).RenPyArchive(archive_path)
    planned = []
    for name in archive.list():
        normalized = name.replace("\\", "/")
        if normalized.startswith("/") or ":" in normalized.split("/")[0]:
            raise ValueError("absolute archive path rejected: " + name)
        target = os.path.abspath(os.path.join(output_path, *normalized.split("/")))
        if not target.startswith(output_root):
            raise ValueError("archive path traversal rejected: " + name)
        planned.append((name, target))
    for name, target in planned:
        pathlib.Path(target).parent.mkdir(parents=True, exist_ok=True)
        if os.path.exists(target):
            print("SKIP existing: " + target)
            continue
        with open(target, "xb") as stream:
            stream.write(archive.read(name))
        print("OK " + target)


if __name__ == "__main__":
    main()
