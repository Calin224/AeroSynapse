"""Check-in desks before security: an upstream FIFO stage that moves control arrivals."""
import copy
import json

import pytest

from app.contracts.models import SimulationRequest
from app.contracts.validation import CONTRACTS, validate
from app.simulation.processes import simulate

CHECK_IN = '60000000-0000-0000-0000-000000000901'


def base(passengers=60, check_in=None):
    """One control, passengers arriving over the first 40 minutes of a two-hour horizon."""
    value = json.loads((CONTRACTS / 'kpis/v1/examples/kpi-request.json').read_text(encoding='utf-8'))
    value.pop('navigation')
    value |= {'durationMinutes': 120, 'passengerCount': passengers, 'arrivalRatePerMinute': passengers / 120}
    generation = value['passengerGeneration']
    generation['durationMinutes'] = 120
    generation['flights'][0] |= {'boardingCloseMinute': 110, 'passengerCapacity': passengers}
    batch = generation['batches'][0]
    batch |= {'modeledCount': passengers, 'countMode': 'full'}
    batch['arrival'] = {'kind': 'uniform', 'minimum': 0, 'maximum': 40, 'mean': 20, 'stdDev': 10, 'mode': 20}
    if check_in is not None:
        value['checkIn'] = {'zoneId': CHECK_IN, 'staffCount': 2, 'serviceRatePerMinute': 0.5,
                            'holdBaggageShare': 0.6, 'walkingMinutes': 1.5} | check_in
    return value


def run(value):
    return simulate(SimulationRequest.model_validate(value)).model_dump(mode='json', by_alias=True)


def people(response):
    return {person['id']: person for person in response['processResult']['passengers']}


def test_hold_baggage_passengers_join_their_control_after_check_in_and_the_walk():
    baseline, routed = run(base()), run(base(check_in={}))
    before, after = people(baseline), people(routed)
    records = {record['id']: record for record in routed['checkInResult']['passengers']}
    assert 0 < len(records) < len(before)
    for identifier, person in before.items():
        record = records.get(identifier)
        if record is None:
            assert after[identifier]['arrivalMinute'] == person['arrivalMinute']
            continue
        assert record['terminalArrivalMinute'] == person['arrivalMinute']
        assert record['startedMinute'] >= record['terminalArrivalMinute']
        assert after[identifier]['arrivalMinute'] == pytest.approx(record['finishedMinute'] + 1.5)
        assert after[identifier]['arrivalMinute'] == record['securityArrivalMinute']


def test_desks_serve_first_come_first_served_and_never_more_than_their_staff():
    records = run(base(passengers=120, check_in={'staffCount': 3}))['checkInResult']['passengers']
    ordered = sorted(records, key=lambda record: (record['terminalArrivalMinute'], record['id']))
    starts = [record['startedMinute'] for record in ordered if record['startedMinute'] is not None]
    assert starts == sorted(starts)
    changes = sorted([(record['startedMinute'], 1) for record in records if record['startedMinute'] is not None]
                     + [(record['finishedMinute'], -1) for record in records if record['finishedMinute'] is not None],
                     key=lambda item: (item[0], item[1]))
    busy = peak = 0
    for _, change in changes:
        busy += change
        peak = max(peak, busy)
    assert peak == 3


def test_no_hold_baggage_leaves_the_control_simulation_unchanged():
    baseline, routed = run(base()), run(base(check_in={'holdBaggageShare': 0}))
    assert routed['processResult'] == baseline['processResult']
    assert routed['kpiResult'] == baseline['kpiResult']
    assert routed['checkInResult']['populationCount'] == 0


def test_unstaffed_desks_keep_their_passengers_as_not_arrived_demand_of_the_control():
    response = run(base(check_in={'staffCount': 0}))
    result = response['checkInResult']
    assert result['processedCount'] == 0 and result['unfinishedCount'] == result['populationCount'] > 0
    final = response['kpiResult']['snapshots'][-1]
    assert final['populationCount'] == 60
    assert final['notArrivedPassengers'] == result['unfinishedCount']
    assert response['processResult']['notArrivedCount'] == result['unfinishedCount']
    # Stranded at the desks, they never reach the gate: boarding closes at 110, so all of them are at risk.
    assert final['boardingRiskPopulationCount'] == 60
    assert final['missedDeadlineUnfinishedCount'] >= result['unfinishedCount']
    assert result['snapshots'][-1]['queuedPassengers'] == result['populationCount']
    assert result['snapshots'][-1]['utilization'] is None


def test_desk_steps_can_close_desks_without_starting_new_service_on_a_closed_desk():
    steps = [{'startMinute': 0, 'staffCount': 3}, {'startMinute': 15, 'staffCount': 1}]
    result = run(base(passengers=120, check_in={'staffCount': 3, 'deskSteps': steps}))['checkInResult']
    assert [row['capacity'] for row in result['snapshots'] if row['minute'] in (10, 30)] == [3, 1]
    starts = sorted(record['startedMinute'] for record in result['passengers'] if record['startedMinute'] is not None)
    # After the close one desk serves, so starts at or after minute 15 cannot overlap each other.
    late = [record for record in result['passengers'] if record['startedMinute'] is not None and record['startedMinute'] >= 15]
    late.sort(key=lambda record: record['startedMinute'])
    assert starts and all(second['startedMinute'] >= first['finishedMinute'] or first['startedMinute'] < 15
                          for first, second in zip(late, late[1:]))
    always = run(base(passengers=120, check_in={'staffCount': 3}))['checkInResult']
    assert result['snapshots'][-1]['maximumQueue'] >= always['snapshots'][-1]['maximumQueue']


def test_desk_snapshots_follow_the_kpi_grid_and_add_up():
    response = run(base(check_in={}))
    result = response['checkInResult']
    assert [row['minute'] for row in result['snapshots']] == [row['minute'] for row in response['kpiResult']['snapshots']]
    final = result['snapshots'][-1]
    assert final['arrivedPassengers'] == result['populationCount']
    assert final['processedPassengers'] == result['processedCount']
    assert final['zoneType'] == 'checkIn' and final['zoneId'] == CHECK_IN
    assert 0 < final['utilization'] <= 1


def test_check_in_runs_are_reproducible():
    assert run(base(check_in={})) == run(base(check_in={}))


def test_check_in_needs_flight_profiles_and_its_own_zone():
    value = base(check_in={})
    without = copy.deepcopy(value)
    without.pop('passengerGeneration')
    assert {'field': '/passengerGeneration', 'code': 'required'} in [
        {key: item[key] for key in ('field', 'code')} for item in validate('KpiSimulationRequest', without)]
    clash = copy.deepcopy(value)
    clash['checkIn']['zoneId'] = clash['checkpoints'][0]['checkpointId']
    assert [item['code'] for item in validate('KpiSimulationRequest', clash)] == ['duplicate_id']
    assert validate('KpiSimulationRequest', value) == []


def test_shared_example_is_the_engine_output_for_its_request():
    example = lambda name: json.loads((CONTRACTS / f'kpis/v1/examples/{name}.json').read_text(encoding='utf-8'))
    response = run(example('check-in-request'))
    assert response == example('check-in-response')
    assert response['checkInResult']['unfinishedCount'] > 0
    # Check-in combined with events and navigation: surges, redirects and route holds still correlate.
    combined = run(example('check-in-events-request'))
    assert combined == example('check-in-events-response')
    people = combined['processResult']['passengers']
    assert combined['checkInResult']['populationCount'] > 0
    assert any('assignedCheckpointId' in person for person in people)
    assert any(person['id'].startswith('demo-surge-') for person in people)
    # Surge passengers of a flight share the desks with the generated ones.
    assert any(record['id'].startswith('demo-surge-') for record in combined['checkInResult']['passengers'])


def test_contradictory_check_in_results_are_rejected():
    response = run(base(check_in={}))
    broken = copy.deepcopy(response)
    broken['checkInResult']['processedCount'] += 1
    assert {item['field'] for item in validate('KpiSimulationResponse', broken)} == {'/checkInResult'}
    assert validate('KpiSimulationResponse', response) == []
