"""Stable operational fingerprints, separate from wall-clock diagnostics and rendering."""
import hashlib
import importlib.metadata
import math
import platform
import re
import struct
from functools import lru_cache
from pathlib import Path

VERSION = "sha256-tree-v1"
TIMESTAMP = re.compile(r"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})\.(\d{1,6})Z$")


def fingerprint(value, kind):
    digest = hashlib.sha256(f"aerosynapse:{VERSION}:{kind}\n".encode())

    def string(text):
        match = TIMESTAMP.fullmatch(text)
        if match:
            fraction = match[2].rstrip('0')
            text = match[1] + ('.' + fraction if fraction else '') + 'Z'
        data = text.encode('utf-8')
        digest.update(b's' + str(len(data)).encode() + b':' + data)

    def write(item):
        if isinstance(item, dict):
            digest.update(b'{')
            for key in sorted(item):
                if key != 'runId' and not (kind == 'input' and key in ('scenarioId', 'scenarioRevision')):
                    string(key)
                    write(item[key])
            digest.update(b'}')
        elif isinstance(item, list):
            digest.update(b'[')
            for child in item:
                write(child)
            digest.update(b']')
        elif isinstance(item, str):
            string(item)
        elif item is None:
            digest.update(b'z')
        elif isinstance(item, bool):
            digest.update(b't' if item else b'f')
        elif isinstance(item, (int, float)):
            number = float(item)
            if not math.isfinite(number):
                raise ValueError('Fingerprint numbers must be finite.')
            digest.update(b'n' + struct.pack('>d', number if number else 0.0).hex().encode() + b';')
        else:
            raise TypeError('Unsupported fingerprint value.')

    write(value)
    return digest.hexdigest()


def engine_version(request):
    if request.journey is not None:
        return 'journey-1.0'
    if request.passenger_generation is not None and request.passenger_generation.flow is not None:
        return 'flow-1.0'
    return request.engine or 'legacy-1.0'


@lru_cache(maxsize=1)
def engine_build():
    # Source plus actual numerical/runtime dependencies identify the deployed implementation.
    root = Path(__file__).resolve().parents[1]
    digest = hashlib.sha256()
    for path in sorted(root.rglob('*.py')):
        digest.update(path.relative_to(root).as_posix().encode() + b'\0')
        digest.update(path.read_bytes())
    digest.update(platform.python_version().encode())
    for package in ('numpy', 'simpy', 'pydantic', 'ortools'):
        digest.update(f'{package}={importlib.metadata.version(package)}\n'.encode())
    return digest.hexdigest()
