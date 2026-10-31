import copy
import json
from concurrent.futures import ThreadPoolExecutor

import pytest
from fastapi.testclient import TestClient

from app.contracts.models import SimulationRequest
from app.contracts.validation import CONTRACTS, validate
from app.main import app
from app.simulation.engine import simulate
from app.simulation.metrics import wait_statistics

ROOT=CONTRACTS/'kpis/v1/examples'
def payload(name='kpi-request'):
    return json.loads((ROOT/(name+'.json')).read_text(encoding='utf-8'))
def run(value=None): return simulate(SimulationRequest.model_validate(value or payload()))
def frames(value=None): return run(value).kpi_result['snapshots']


def test_two_passenger_formulas_and_temporal_reconciliation_are_calculable_by_hand():
    result=run().kpi_result; rows=result['snapshots']
    assert [row['minute'] for row in rows]==list(range(11))
    assert rows[0]['queuedPassengers']==rows[0]['processingPassengers']==1
    assert rows[0]['waitSampleCount']==0 and rows[0]['meanWaitMinutes'] is None
    assert (rows[1]['meanWaitMinutes'],rows[1]['medianWaitMinutes'],rows[1]['p95WaitMinutes'],rows[1]['waitSampleCount'])==(0,0,0,1)
    assert (rows[2]['meanWaitMinutes'],rows[2]['medianWaitMinutes'],rows[2]['p95WaitMinutes'],rows[2]['waitSampleCount'])==(.5,.5,1,2)
    assert rows[1]['throughputPerMinute']==rows[2]['throughputPerMinute']==1
    assert rows[2]['busyMinutes']==2 and rows[2]['utilization']==rows[2]['cumulativeUtilization']==1
    assert rows[3]['boardingLateRisk']==.5 and rows[3]['missedDeadlineUnfinishedCount']==1 and rows[3]['lateAtGateCount']==0
    assert rows[4]['lateAtGateCount']==1 and rows[4]['missedDeadlineUnfinishedCount']==0
    final=rows[-1]
    assert final['cumulativeThroughputPerMinute']==final['cumulativeUtilization']==.2
    assert final['throughputPerMinute']==final['utilization']==0
    assert final['maximumQueue']==1 and final['firstQueueMinute']==0
    assert result['populationCount']==result['processedCount']+result['unprocessedCount']==2
    for row in rows:
        assert row['populationCount']==sum(row[key] for key in ('notArrivedPassengers','queuedPassengers','processingPassengers','walkingPassengers','atGatePassengers','unreachablePassengers'))
        assert row['runId']==result['runId'] and row['zoneId']==payload()['checkpoints'][0]['checkpointId']
        assert row['elapsedMinutes']==row['minute']


def test_percentiles_use_nearest_rank_and_even_median_not_linear_interpolation():
    assert wait_statistics([])==(None,None,None)
    assert wait_statistics([7])==(7,7,7)
    assert wait_statistics([0,1])==(.5,.5,1)
    assert wait_statistics(list(range(1,21)))==(10.5,10.5,19)


def test_unfinished_services_are_excluded_from_waits_but_labeled_utilization_includes_occupied_time():
    wire=payload(); wire['durationMinutes']=wire['passengerGeneration']['durationMinutes']=1
    wire['passengerGeneration']['flights'][0]['boardingCloseMinute']=1
    row=frames(wire)[-1]
    assert row['processedPassengers']==row['waitSampleCount']==1 and row['processingPassengers']==1
    assert row['meanWaitMinutes']==row['p95WaitMinutes']==0  # The second passenger waited one minute but has not finished.
    assert row['boardingAtRiskCount']==2 and row['boardingLateRisk']==1
    wire['passengerGeneration']['batches'][0]['service'].update(minimum=5,maximum=5,mean=5,mode=5)
    row=frames(wire)[-1]
    assert row['waitSampleCount']==0 and row['meanWaitMinutes'] is None and row['p95WaitMinutes'] is None
    assert row['busyMinutes']==1 and row['utilization']==1 and row['processedPassengers']==0
    wire['passengerGeneration']['batches'][0]['arrival'].update(minimum=.5,maximum=.5,mean=.5,mode=.5)
    row=frames(wire)[-1]
    assert row['busyMinutes']==.5 and row['utilization']==.5


def test_true_maximum_queue_keeps_a_peak_between_snapshot_minutes():
    wire=payload(); batch=wire['passengerGeneration']['batches'][0]
    batch['arrival'].update(minimum=.1,maximum=.1,mean=.1,mode=.1)
    batch['service'].update(minimum=.1,maximum=.1,mean=.1,mode=.1)
    rows=frames(wire)
    assert all(row['queuedPassengers']==0 for row in rows)
    assert rows[1]['maximumQueue']==1 and rows[1]['firstQueueMinute']==.1
    assert rows[1]['p95WaitMinutes']==pytest.approx(.1)


@pytest.mark.parametrize('interval',[1,2,3,4,5])
def test_cadence_is_one_to_five_minutes_with_zero_and_inclusive_final_boundary(interval):
    wire=payload(); wire['snapshotIntervalMinutes']=interval
    wire['durationMinutes']=wire['passengerGeneration']['durationMinutes']=7
    rows=frames(wire)
    assert [row['minute'] for row in rows]==[*range(0,7,interval),7]
    assert [row['windowStartMinute'] for row in rows]==[0,*[row['minute'] for row in rows[:-1]]]


def test_empty_closed_and_unreachable_populations_have_explicit_denominators():
    empty=frames(payload('empty-request'))[-1]
    assert empty['waitSampleCount']==0 and empty['meanWaitMinutes'] is None and empty['boardingLateRisk'] is None
    assert empty['throughputPerMinute']==0 and empty['cumulativeThroughputPerMinute']==0
    wire=payload(); wire['checkpoints'][0]['staffCount']=wire['passengerGeneration']['checkpoints'][0]['staffCount']=0
    row=frames(wire)[-1]
    assert row['utilization'] is None and row['cumulativeUtilization'] is None and row['maximumQueue']==2
    assert row['waitSampleCount']==0 and row['boardingLateRisk']==1
    wire['navigation']['closedZoneIds']=[node['id'] for node in wire['navigation']['graph']['nodes'][1:3]]
    result=run(wire).kpi_result; row=result['snapshots'][-1]
    assert result['processedCount']==0 and result['unprocessedCount']==result['unreachableCount']==2
    assert row['unreachablePassengers']==2 and row['queuedPassengers']==0 and row['boardingLateRisk']==1


def test_aggregate_assignment_conserves_the_entire_demand_including_not_arrived():
    wire=payload('empty-request'); wire.update(passengerCount=100,arrivalRatePerMinute=.000001)
    extra=copy.deepcopy(wire['checkpoints'][0]); extra['checkpointId']='60000000-0000-0000-0000-000000000999'; wire['checkpoints'].append(extra)
    result=run(wire).kpi_result
    assert result['processedCount']==0 and result['unprocessedCount']==result['notArrivedCount']==100
    final=[row for row in result['snapshots'] if row['minute']==90]
    assert sum(row['populationCount'] for row in final)==sum(row['notArrivedPassengers'] for row in final)==100


def test_observation_does_not_change_simpy_processes_and_results_reproduce_concurrently():
    wire=payload(); current=run(wire)
    old=copy.deepcopy(wire); old['engine']='simpy-1.0'; old.pop('snapshotIntervalMinutes')
    previous=run(old).process_result; previous['engine']=current.process_result['engine']
    assert previous==current.process_result
    with ThreadPoolExecutor(max_workers=4) as pool:
        assert all(result==current.kpi_result for result in pool.map(lambda _:run(wire).kpi_result,range(4)))


def test_http_and_validation_agree_and_frame_budget_is_explicit():
    with TestClient(app) as client:
        reply=client.post('/simulation/kpi-run',json=payload())
        assert reply.status_code==200 and not validate('KpiSimulationResponse',reply.json())
        assert client.post('/simulation/simpy-run',json=payload()).status_code==422
        wire=payload(); wire['snapshotIntervalMinutes']=6
        assert client.post('/simulation/kpi-run',json=wire).status_code==422
    wire=payload('empty-request'); wire['durationMinutes']=10080
    template=wire['checkpoints'][0]
    wire['checkpoints']=[template|{'checkpointId':f'60000000-0000-0000-0000-{index+1:012d}'} for index in range(6)]
    assert any(error['field']=='/snapshotIntervalMinutes' for error in validate('KpiSimulationRequest',wire))
