import copy
import json
import time
from concurrent.futures import ThreadPoolExecutor

import pytest
from fastapi.testclient import TestClient

from app.contracts.models import SimulationRequest
from app.contracts.validation import CONTRACTS, validate
from app.main import app
from app.simulation.engine import simulate

ROOT = CONTRACTS/'processes/v1/examples'


def payload(name='simpy-request'):
    return json.loads((ROOT/(name+'.json')).read_text(encoding='utf-8'))


def run(value=None,frames=None):
    return simulate(SimulationRequest.model_validate(value or payload()),on_progress=frames.append if frames is not None else None)


def horizon(value,minutes):
    value['durationMinutes']=value['passengerGeneration']['durationMinutes']=minutes
    value['passengerGeneration']['flights'][0]['boardingCloseMinute']=minutes
    return value


def test_actual_queue_service_walk_and_deadline_match_the_analytical_two_passenger_case():
    result=run(); report=result.process_result
    first,second=report['passengers']; cp=report['checkpoints'][0]
    assert (cp['peakBusy'],cp['peakQueue'],cp['processedCount'])==(1,1,2)
    assert (first['processingStartedMinute'],first['securityClearedMinute'],first['gateArrivalMinute'])==(0,1,3)
    assert (second['processingStartedMinute'],second['securityClearedMinute'],second['gateArrivalMinute'])==(1,2,4)
    assert first['boardingRisk']=='safe' and not first['deadlineMissed']
    assert second['boardingRisk']=='late' and second['deadlineMissed']
    assert report['atGateCount']==2 and report['deadlineMissedCount']==1
    assert not validate('SimPySimulationResponse',result.model_dump(mode='json',by_alias=True))


def test_service_must_complete_before_security_and_walking_and_incomplete_jobs_are_not_forecast_as_arrived():
    wire=horizon(payload(),2)
    batch=wire['passengerGeneration']['batches'][0]
    batch['service'].update(minimum=5,maximum=5,mean=5,mode=5)
    report=run(wire).process_result
    assert (report['processingCount'],report['queuedCount'],report['walkingCount'],report['atGateCount'])==(1,1,0,0)
    assert all(person['securityClearedMinute'] is None and person['gateArrivalMinute'] is None for person in report['passengers'])
    assert all(person['boardingRisk']=='missed' for person in report['passengers'])
    assert all(person['gateArrivalMinute'] is None for person in run(wire).navigation_result['journeys'])


def test_capacity_two_serves_two_in_parallel_and_never_exceeds_two_slots():
    wire=payload()
    wire['checkpoints'][0]['staffCount']=wire['passengerGeneration']['checkpoints'][0]['staffCount']=2
    report=run(wire).process_result
    assert report['checkpoints'][0]['peakBusy']==2
    assert report['checkpoints'][0]['peakQueue']==0
    assert all(person['securityClearedMinute']==1 and person['gateArrivalMinute']==3 for person in report['passengers'])
    assert report['deadlineMissedCount']==0


def test_closed_checkpoint_keeps_everyone_queued_and_never_clears_security():
    wire=payload()
    wire['checkpoints'][0]['staffCount']=wire['passengerGeneration']['checkpoints'][0]['staffCount']=0
    report=run(wire).process_result
    cp=report['checkpoints'][0]
    assert (report['queuedCount'],cp['peakBusy'],cp['processedCount'])==(2,0,0)
    assert all(person['processingStartedMinute'] is None and person['securityClearedMinute'] is None for person in report['passengers'])
    assert report['deadlineMissedCount']==2


def test_ninety_virtual_minutes_are_fast_and_events_at_the_final_minute_are_included():
    wire=horizon(payload(),90)
    wire['passengerGeneration']['batches'][0]['arrival'].update(minimum=87,maximum=87,mean=87,mode=87)
    frames=[]; started=time.perf_counter(); report=run(wire,frames).process_result
    assert time.perf_counter()-started<5
    assert report['horizonMinutes']==90
    assert report['atGateCount']==1 and report['walkingCount']==1
    assert report['passengers'][0]['gateArrivalMinute']==90
    assert report['passengers'][0]['boardingRisk']=='safe'
    assert report['passengers'][1]['gateArrivalMinute'] is None and report['passengers'][1]['boardingRisk']=='missed'
    assert 1<=len(frames)<=60 and frames[-1]['simulationMinute']==90
    assert frames[-1]['zones'][0]['processedPassengers']==2
    wire['passengerGeneration']['batches'][0]['arrival'].update(minimum=90,maximum=90,mean=90,mode=90)
    report=run(wire).process_result
    assert report['arrivedCount']==2 and report['processingCount']==1 and report['queuedCount']==1
    assert report['atGateCount']==0


@pytest.mark.parametrize('profiled',[False,True])
def test_zero_passengers_is_a_successful_empty_simulation(profiled):
    wire=payload('empty-request')
    if profiled:
        wire=payload(); wire['passengerCount']=wire['arrivalRatePerMinute']=0
        wire['passengerGeneration']['batches'][0].update(countMode='subset',modeledCount=0)
    result=run(wire); report=result.process_result
    assert result.status=='completed' and report['populationCount']==report['arrivedCount']==0
    assert not report['passengers']
    assert all(cp['processedCount']==cp['peakBusy']==0 for cp in report['checkpoints'])


def test_unreachable_routes_are_explicit_and_do_not_enter_resource_queues():
    wire=payload(); wire['navigation']['closedZoneIds']=[node['id'] for node in wire['navigation']['graph']['nodes'][1:3]]
    report=run(wire).process_result
    assert report['unreachableCount']==2 and report['atGateCount']==report['queuedCount']==0
    assert report['checkpoints'][0]['arrivedCount']==report['checkpoints'][0]['processedCount']==0
    assert all(person['stage']=='unreachable' and person['errorCode']=='route_unreachable' for person in report['passengers'])


def test_seeded_aggregate_demand_is_conserved_instead_of_replicated_at_each_checkpoint():
    wire=payload('empty-request'); wire.update(passengerCount=100,arrivalRatePerMinute=20)
    cp=copy.deepcopy(wire['checkpoints'][0]); cp['checkpointId']='60000000-0000-0000-0000-000000000999'
    wire['checkpoints'].append(cp)
    first=run(wire).process_result
    assert first['arrivedCount']==sum(cp['arrivedCount'] for cp in first['checkpoints'])==100
    assert first==run(wire).process_result
    wire['checkpoints'].reverse()
    assert first==run(wire).process_result
    with ThreadPoolExecutor(max_workers=4) as pool:
        assert all(result==first for result in pool.map(lambda _:run(wire).process_result,range(4)))
    wire['seed']+=1
    assert run(wire).process_result['passengers']!=first['passengers']
    wire['arrivalRatePerMinute']=0.000001
    assert run(wire).process_result['notArrivedCount']==100


def test_http_uses_the_simpy_contract_and_rejects_zero_rate_for_nonempty_demand():
    with TestClient(app) as client:
        result=client.post('/simulation/simpy-run',json=payload('empty-request'))
        assert result.status_code==200 and result.json()['processResult']['arrivedCount']==0
        wire=payload('empty-request'); wire['passengerCount']=1
        invalid=client.post('/simulation/simpy-run',json=wire)
        assert invalid.status_code==422 and any(error['field']=='/arrivalRatePerMinute' for error in invalid.json()['errors'])
        assert client.post('/simulation/v1/run',json=payload('empty-request')).status_code==422


def test_process_validation_rejects_security_bypass_and_impossible_resource_occupancy():
    result=run().process_result
    for mutate in [lambda value:value['passengers'][0].update(securityClearedMinute=.5),
                   lambda value:value['checkpoints'][0].update(peakBusy=2),
                   lambda value:value['passengers'][0].update(stage='queued')]:
        changed=copy.deepcopy(result); mutate(changed)
        assert validate('ProcessResult',changed)


def test_arrival_clock_never_rounds_below_the_planned_minute():
    # 3.7 + (13.6 - 3.7) rounds to 13.599999999999998: service would otherwise start before arrival.
    value=horizon(payload(),20)
    value.pop('navigation',None)
    generation=value['passengerGeneration']
    flight,batch=generation['flights'][0],generation['batches'][0]
    generation['flights']=[]; generation['batches']=[]
    for number,minute in enumerate((3.7,13.6)):
        identifier=f'70000000-0000-0000-0000-00000000010{number}'
        generation['flights'].append(flight|{'flightId':identifier,'passengerCapacity':1,'boardingCloseMinute':20})
        generation['batches'].append(copy.deepcopy(batch)|{'flightId':identifier,'modeledCount':1,
            'arrival':batch['arrival']|{'minimum':minute,'maximum':minute}})
    value['passengerCount']=2
    result=run(value)
    people=sorted(result.process_result['passengers'],key=lambda person:person['arrivalMinute'])
    assert [person['arrivalMinute'] for person in people]==[3.7,13.6]
    assert all(person['queueEnteredMinute']==person['arrivalMinute']<=person['processingStartedMinute'] for person in people)
    assert not validate('SimPySimulationResponse',result.model_dump(mode='json',by_alias=True))
