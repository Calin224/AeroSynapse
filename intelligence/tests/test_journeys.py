"""Full-day journeys: independently check physical queues and rolling gate invariants."""
import copy
import json
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from app.contracts.models import SimulationRequest
from app.contracts.validation import CONTRACTS, validate
from app.main import app
from app.simulation.engine import simulate


def identifier(n):
    return f'71000000-0000-0000-0000-{n:012d}'


def example():
    value=json.loads((CONTRACTS/'kpis/v1/examples/kpi-request.json').read_text())
    value.pop('navigation',None)
    value.update(durationMinutes=720, passengerCount=16, arrivalRatePerMinute=16/720, snapshotIntervalMinutes=5)
    generation=value['passengerGeneration']
    generation['durationMinutes']=720
    flight, batch=copy.deepcopy(generation['flights'][0]),copy.deepcopy(generation['batches'][0])
    generation['flights'],generation['batches']=[],[]
    gates=[identifier(10),identifier(11)]
    j={'journeyVersion':'1.0','passportStaffCount':1,'passportServiceMinutes':2,
       'boardingStaffPerGate':1,'boardingServiceMinutes':1,'gateChangeWalkingMinutes':4,'riskBufferMinutes':10,
       'flights':[],'connections':[],'routes':[{'checkpointId':flight['checkpointId'],'gateId':g,'walkingMinutes':2} for g in gates]}
    for i,(arrival,departure) in enumerate([(60,120),(120,180),(180,240),(420,480)],1):
        key=identifier(i)
        generation['flights'].append(flight|{'flightId':key,'boardingCloseMinute':departure-5,'passengerCapacity':4})
        generation['batches'].append(copy.deepcopy(batch)|{'flightId':key,'modeledCount':4})
        j['flights'].append({'flightId':key,'gateId':gates[0],'arrivalMinute':arrival,'boardingOpenMinute':arrival+5,
            'boardingCloseMinute':departure-5,'departureMinute':departure,'delayMinutes':0,'passengerCapacity':4,
            'isCancelled':False,'requiresPassport':True,'previousFlightId':identifier(3) if i==4 else None,
            'rotationMinutes':180 if i==4 else 0,'eligibleGateIds':gates})
    value['journey']=j
    j['connections']=[{'id':identifier(20),'inboundFlightId':identifier(1),'outboundFlightId':identifier(2),
                       'passengerCount':2,'transferMinutes':5,'requiresPassport':True}]
    return value


def delay(flight,minute,minutes,event=30):
    return {'eventId':identifier(event),'type':'flight_change','minute':minute,'flightId':identifier(flight),'delayMinutes':minutes}


def run(value):
    return simulate(SimulationRequest.model_validate(value)).model_dump(mode='json',by_alias=True)


def test_full_day_queues_and_actual_boarding_are_reproducible():
    value=example()
    a,b=run(value),run(value)
    assert a==b
    r=a['journeyResult']
    assert r['populationCount']==16 and r['connectionPassengerCount']==2 and r['boardedCount']==16
    assert r['upstreamResult']['processResult']['populationCount']==14
    assert len(r['snapshots'])==145
    assert max(s['passportQueuedCount'] for s in r['snapshots'])>0
    assert max(s['gateQueuedCount'] for s in r['snapshots'])>0
    for p in r['passengers']:
        visits={v['stage']:v for v in p['visits']}
        assert visits['boarding']['finishedMinute']==p['boardedMinute']>p['gateArrivalMinute']
        assert visits['boarding']['startedMinute']>=p['gateArrivalMinute']
        assert visits['passport']['finishedMinute']-visits['passport']['startedMinute']==2
        if p['connectionId']:
            assert 'security' not in visits and p['arrivalMinute']==60
        else:
            assert visits['passport']['arrivalMinute']==visits['security']['finishedMinute']


def test_active_flight_keeps_its_gate_and_delay_reaches_the_afternoon_rotation():
    value=example()
    value['events']=[delay(1,90,120),delay(2,130,90,31)]
    r=run(value)['journeyResult']
    baseline=run(example())['journeyResult']
    assert r['snapshots'][:18]==baseline['snapshots'][:18]  # All instants strictly before the event are unchanged.
    original,first,last=[{f['flightId']:f for f in p['flights']} for p in r['plans']]
    assert first[identifier(1)]['gateId']==original[identifier(1)]['gateId']
    assert first[identifier(1)]['arrivalMinute']==60 and first[identifier(1)]['departureMinute']==240
    assert first[identifier(2)]['gateId']!=original[identifier(2)]['gateId']
    assert last[identifier(3)]['delayMinutes']>=60
    assert last[identifier(4)]['arrivalMinute']>=last[identifier(3)]['departureMinute']+180
    assert last[identifier(4)]['delayMinutes']>=60
    for plan in r['plans']:
        for g in [identifier(10),identifier(11)]:
            flights=sorted((f for f in plan['flights'] if f['gateId']==g),key=lambda f:f['arrivalMinute'])
            assert all(a['departureMinute']<=b['arrivalMinute'] for a,b in zip(flights,flights[1:]))
    # Waiting travelers physically walk after the gate changes.
    changed=[p for p in r['passengers'] if p['flightId']==identifier(2)]
    assert all(any(h['minute']==90 and h['stage']=='walking' for h in p['history']) for p in changed)
    assert all(p['gateArrivalMinute']>=94 for p in changed)


def test_delayed_inbound_creates_risk_then_missed_connections_without_extra_passengers():
    value=example(); value['events']=[delay(1,20,130)]
    r=run(value)['journeyResult']
    assert r['populationCount']==16 and r['missedConnectionCount']==2
    assert next(s for s in r['snapshots'] if s['minute']==20)['atRiskConnectionCount']==2
    assert next(s for s in r['snapshots'] if s['minute']==175)['missedConnectionCount']==2
    transfers=[p for p in r['passengers'] if p['connectionId']]
    assert all(p['arrivalMinute']==190 and p['boardedMinute'] is None and p['connectionStatus']=='missed' for p in transfers)


@pytest.mark.parametrize('field,stage',[('passportStaffCount','passport'),('boardingStaffPerGate','gate_queue')])
def test_zero_capacity_never_completes_an_unserved_stage(field,stage):
    value=example(); value['journey'][field]=0
    r=run(value)['journeyResult']
    assert r['boardedCount']==0 and r['missedConnectionCount']==2
    if stage=='passport':
        assert all(p['stage']=='passport' and p['visits'][-1]['startedMinute'] is None for p in r['passengers'])


def test_delay_after_the_horizon_keeps_future_completions_null_and_plan_visible():
    value=example(); value['events']=[delay(1,20,800)]
    r=run(value)['journeyResult']
    assert max(f['departureMinute'] for f in r['plans'][-1]['flights'])>720
    assert all(p['arrivalMinute'] is None for p in r['passengers'] if p['connectionId'])


def test_boarding_can_finish_exactly_at_close_and_queues_can_make_passengers_miss():
    value=example()
    value['journey']['boardingServiceMinutes']=10
    for f in value['journey']['flights']:
        f['boardingOpenMinute']=f['boardingCloseMinute']-20
    r=run(value)['journeyResult']
    for f in value['journey']['flights']:
        people=[p for p in r['passengers'] if p['flightId']==f['flightId']]
        assert sum(p['boardedMinute'] is not None for p in people)==2
        assert any(p['boardedMinute']==f['boardingCloseMinute'] for p in people)


def test_departure_delay_after_boarding_closed_preserves_the_closed_boarding_and_its_passengers():
    value=example(); value['events']=[delay(1,118,60)]
    r=run(value)['journeyResult']
    flight=next(f for f in r['plans'][-1]['flights'] if f['flightId']==identifier(1))
    assert flight['boardingCloseMinute']==115 and flight['departureMinute']==180
    assert flight['arrivalMinute']==60
    baseline=run(example())['journeyResult']
    assert [p for p in r['passengers'] if p['flightId']==identifier(1)]==[p for p in baseline['passengers'] if p['flightId']==identifier(1)]


def test_a_delay_cannot_rewrite_a_departed_flight_and_failure_has_no_partial_results():
    value=example(); value['events']=[delay(1,121,60)]
    response=run(value)
    assert response['status']=='failed' and response['failure']['code']=='event_conflict'
    assert response['metrics']==[] and response['snapshots']==[] and 'journeyResult' not in response


def test_delay_at_the_departure_instant_runs_before_the_departure():
    value=example(); value['events']=[delay(1,120,60)]
    response=run(value)
    assert response['status']=='completed'
    f=next(f for f in response['journeyResult']['plans'][-1]['flights'] if f['flightId']==identifier(1))
    assert f['departureMinute']==180 and f['boardingCloseMinute']==115


def test_navigation_and_surge_keep_access_rules_and_wait_for_a_closed_corridor():
    value=example()
    nav=json.loads((CONTRACTS/'navigation/v1/examples/route-query.json').read_text())['navigation']
    gate=nav['gateBindings'][0]['zoneId']
    nav['gateBindings']=[{'resourceId':identifier(i),'zoneId':gate} for i in (10,11)]
    nav['flightGates']=[{'flightId':f['flightId'],'gateId':f['gateId']} for f in value['journey']['flights']]
    profile=next(p for p in nav['accessProfiles'] if p['groupKind']=='family')
    profile['requireStepFree']=True
    profile['walkingMultiplier']=2
    for batch in value['passengerGeneration']['batches']:
        batch['groups'][0]['kind']='family'
    closed=nav['graph']['nodes'][1]['id']
    value['navigation']=nav
    value['events']=[{'eventId':identifier(30),'type':'corridor_closure','minute':0,'zoneId':closed},
                     {'eventId':identifier(31),'type':'resume','minute':40,'zoneId':closed},
                     {'eventId':identifier(32),'type':'passenger_surge','minute':1,'passengerCount':2,
                      'windowMinutes':1,'flightId':identifier(1),'checkpointId':value['checkpoints'][0]['checkpointId']}]
    assert not validate('KpiSimulationRequest',value)
    result=run(value)['journeyResult']
    assert result['populationCount']==18
    origin=[p for p in result['passengers'] if p['connectionId'] is None]
    assert any(p['id'].startswith('demo-surge-') for p in origin)
    # Every originating passenger, including surge families, waits rather than using stairs.
    assert all(any(h['stage']=='unreachable' for h in p['history']) for p in origin)
    assert all(p['gateArrivalMinute']==44 for p in origin)


def test_contract_rejects_cycles_doubled_cohorts_missing_routes_and_negative_delays():
    invalid=[]
    v=example(); v['journey']['flights'][2]['previousFlightId']=identifier(4); invalid.append(v)
    v=example(); v['journey']['flights'][1]['previousFlightId']=identifier(3); invalid.append(v)
    v=example(); v['journey']['connections'][0]['passengerCount']=5; invalid.append(v)
    v=example(); v['journey']['routes'].pop(); invalid.append(v)
    v=example(); v['events']=[delay(1,20,-10)]; invalid.append(v)
    v=example(); v['journey']['flights'][0]['boardingOpenMinute']=50; invalid.append(v)
    v=example(); v['passengerGeneration']['flights'].append(copy.deepcopy(v['passengerGeneration']['flights'][0])); invalid.append(v)
    for value in invalid:
        assert validate('KpiSimulationRequest',value)


def test_http_and_shared_examples_match_the_engine():
    value=example(); value['events']=[delay(1,90,120),delay(2,130,90,31)]
    response=TestClient(app).post('/simulation/kpi-run',json=value)
    assert response.status_code==200,response.text
    assert response.json()==run(value)
    for name in ('day','connection-delay','check-in'):
        root=CONTRACTS/'journeys/v1/examples'
        request=json.loads((root/f'{name}-request.json').read_text())
        expected=json.loads((root/f'{name}-response.json').read_text())
        assert run(request)==expected


if __name__=='__main__':
    root=CONTRACTS/'journeys/v1/examples'
    a=example(); a['events']=[delay(1,90,120),delay(2,130,90,31),delay(1,238,5,32)]
    b=example(); b['events']=[delay(1,20,130)]
    c=example(); c['checkIn']={'zoneId':identifier(80),'staffCount':1,'serviceRatePerMinute':.02,'holdBaggageShare':1,'walkingMinutes':3}
    for name,value in [('day',a),('connection-delay',b),('check-in',c)]:
        (root/f'{name}-request.json').write_text(json.dumps(value,indent=2)+'\n')
        (root/f'{name}-response.json').write_text(json.dumps(run(value),indent=2)+'\n')
