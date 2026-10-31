"""Scheduled events: one set of field rules and conflicts in both runtimes."""
import copy,json,os,urllib.error,urllib.request
from pathlib import Path
import pytest
from fastapi.testclient import TestClient
from app.contracts.validation import validate
from app.main import app
ROOT=Path(__file__).resolve().parents[1]/'events/v1'
CASES=json.loads((ROOT/'validation-cases.json').read_text(encoding='utf-8'))
PATH='/contracts/v1/kpis/simulation/validate'
def example(name): return json.loads((ROOT/'examples'/(name+'.json')).read_text(encoding='utf-8'))
@pytest.fixture(scope='module')
def client():
    with TestClient(app) as client: yield client
@pytest.mark.parametrize('case',CASES,ids=lambda case:case['name'])
def test_event_cases_agree_across_runtime_and_http(case,client):
    payload=example(case['example'])
    for mutation in case['mutations']: payload[mutation['path'][1:]]=copy.deepcopy(mutation['value'])
    errors=validate(case['definition'],payload)
    assert [(item['field'],item['code']) for item in errors]==[(item['field'],item['code']) for item in case['errors']]
    reply=client.post(PATH,json=payload)
    assert reply.status_code==(422 if errors else 200)
    for base in (os.getenv('CONTRACT_API_URL'),os.getenv('CONTRACT_PYTHON_URL')):
        if not base: continue
        request=urllib.request.Request(base+PATH,data=json.dumps(payload).encode(),headers={'Content-Type':'application/json'})
        try: response=urllib.request.urlopen(request,timeout=10)
        except urllib.error.HTTPError as error: response=error
        with response:
            assert response.status==reply.status_code
            assert json.load(response)==reply.json()
def test_example_response_is_the_engine_output_for_the_example_request(client):
    reply=client.post('/simulation/kpi-run',json=example('event-request'))
    assert reply.status_code==200 and reply.json()==example('event-response')
    assert validate('KpiSimulationResponse',example('event-response'))==[]
def test_schedule_example_response_is_the_engine_output_for_its_request(client):
    reply=client.post('/simulation/kpi-run',json=example('schedule-request'))
    assert reply.status_code==200 and reply.json()==example('schedule-response')
    assert validate('KpiSimulationResponse',example('schedule-response'))==[]
