"""Numerical definitions and temporal snapshot validation shared by both runtimes."""
import copy,json,os,urllib.error,urllib.request
from pathlib import Path
import pytest
from fastapi.testclient import TestClient
from app.contracts.validation import schema,validate
from app.main import app
ROOT=Path(__file__).resolve().parents[1]/'kpis/v1'
CASES=json.loads((ROOT/'validation-cases.json').read_text(encoding='utf-8'))
PATHS={'KpiSimulationRequest':'kpis/simulation/validate','KpiResult':'kpis/result/validate','KpiSnapshot':'kpis/snapshot/validate'}
@pytest.fixture(scope='module')
def client():
    with TestClient(app) as client: yield client
@pytest.mark.parametrize('case',CASES,ids=lambda case:case['name'])
def test_kpi_cases_agree_across_runtime_and_http(case,client):
    payload=json.loads((ROOT/'examples'/(case['example']+'.json')).read_text(encoding='utf-8'))
    for mutation in case['mutations']:
        parts=mutation['path'].split('/')[1:]; parent=payload
        for part in parts[:-1]: parent=parent[int(part)] if isinstance(parent,list) else parent[part]
        parent[int(parts[-1]) if isinstance(parent,list) else parts[-1]]=copy.deepcopy(mutation['value'])
    errors=validate(case['definition'],payload)
    assert [(item['field'],item['code']) for item in errors]==[(item['field'],item['code']) for item in case['errors']]
    if case['definition'] not in PATHS: return
    path='/contracts/v1/'+PATHS[case['definition']]; reply=client.post(path,json=payload)
    assert reply.status_code==(422 if errors else 200)
    for base in (os.getenv('CONTRACT_API_URL'),os.getenv('CONTRACT_PYTHON_URL')):
        if not base: continue
        request=urllib.request.Request(base+path,data=json.dumps(payload).encode(),headers={'Content-Type':'application/json'})
        try: response=urllib.request.urlopen(request,timeout=10)
        except urllib.error.HTTPError as error: response=error
        with response:
            assert response.status==reply.status_code
            assert json.load(response)==reply.json()
def test_kpi_schemas_match_on_all_boundaries(client):
    for definition in ('KpiSimulationRequest','KpiScenarioSnapshot','KpiSimulationResponse','KpiResult','KpiSnapshot'):
        path='/contracts/v1/schemas/'+definition; expected=schema(definition)
        assert client.get(path).json()==expected
        for base in (os.getenv('CONTRACT_API_URL'),os.getenv('CONTRACT_PYTHON_URL')):
            if base:
                with urllib.request.urlopen(base+path,timeout=10) as reply: assert json.load(reply)==expected
