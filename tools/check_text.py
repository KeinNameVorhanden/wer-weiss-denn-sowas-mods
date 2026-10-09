"""Fails if tracked text files contain em/en dashes or non-UTF-8 bytes.

Usage: python -I tools/check_text.py <root> [<root> ...]
Skips build output, decompiled sources and downloads.
"""
import pathlib
import sys

FORBIDDEN = {chr(0x2014): "em dash", chr(0x2013): "en dash"}
SKIP_DIRS = {".git", "_re", "_extract", "_downloads", "bin", "obj", "artifacts", "dist", ".vs", "lib"}
TEXT_EXT = {".cs", ".md", ".txt", ".ps1", ".py", ".json", ".xml", ".csproj", ".props", ".targets",
            ".cfg", ".ini", ".yml", ".yaml", ".sln", ".editorconfig", ".gitignore", ".bat", ".cmd"}


def scan(root: pathlib.Path) -> int:
    problems = 0
    for path in root.rglob("*"):
        if not path.is_file() or any(part in SKIP_DIRS for part in path.relative_to(root).parts):
            continue
        if path.suffix.lower() not in TEXT_EXT and path.name not in TEXT_EXT:
            continue
        data = path.read_bytes()
        try:
            text = data.decode("utf-8")
        except UnicodeDecodeError as exc:
            print(f"{path}: not UTF-8 ({exc})")
            problems += 1
            continue
        for lineno, line in enumerate(text.splitlines(), 1):
            for ch, name in FORBIDDEN.items():
                if ch in line:
                    print(f"{path}:{lineno}: {name}")
                    problems += 1
    return problems


def main() -> int:
    roots = [pathlib.Path(a) for a in sys.argv[1:]] or [pathlib.Path(".")]
    total = sum(scan(r) for r in roots)
    print(f"{total} problem(s)")
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())
