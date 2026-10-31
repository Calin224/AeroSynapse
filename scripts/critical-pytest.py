"""The critical suite cannot silently succeed by skipping an unavailable service."""
import sys
from pathlib import Path
from xml.etree import ElementTree

import pytest

report = Path('/artifacts/pytest.xml')
status = pytest.main([
    '-q', 'critical', 'intelligence/tests/test_engine.py', 'intelligence/tests/test_processes.py',
    'intelligence/tests/test_kpis.py', 'intelligence/tests/test_events.py', 'intelligence/tests/test_staffing.py', 'intelligence/tests/test_resources.py',
    'contracts/tests',
    f'--junitxml={report}', '-o', 'cache_dir=/tmp/pytest-cache',
])
if status == 0:
    suites = list(ElementTree.parse(report).iter('testsuite'))
    if not suites or sum(int(s.get('tests', 0)) for s in suites) == 0 or any(int(s.get('skipped', 0)) for s in suites):
        print('Critical tests must be collected and executed without skips.', file=sys.stderr)
        status = 1
sys.exit(status)
