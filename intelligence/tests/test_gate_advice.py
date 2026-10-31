"""Gate advice: the delay reported now gets the plan the journey run would apply, against keeping every gate."""
from fastapi.testclient import TestClient

from app.contracts.validation import validate
from app.main import app
from app.optimization.gates import advise


def identifier(n):
    return f'72000000-0000-0000-0000-{n:012d}'


A, B, C, G1, G2 = identifier(1), identifier(2), identifier(3), identifier(10), identifier(11)


def flight(key, arrival, departure, gate=G1):
    return {'flightId': key, 'gateId': gate, 'arrivalMinute': arrival, 'boardingOpenMinute': arrival + 5,
            'boardingCloseMinute': departure - 5, 'departureMinute': departure, 'delayMinutes': 0, 'passengerCapacity': 4,
            'isCancelled': False, 'requiresPassport': False, 'previousFlightId': None, 'rotationMinutes': 0,
            'eligibleGateIds': [G1, G2]}


def request(minute=5, events=(), flights=None):
    # A leaves G1 at 30 and B arrives there at 40: a 20-minute delay of A runs into B.
    return {'adviceVersion': '1.0', 'minute': minute, 'events': list(events),
            'flights': flights or [flight(A, 10, 30), flight(B, 40, 60)], 'delay': {'flightId': A, 'delayMinutes': 20}}


def by_flight(plan):
    return {row['flightId']: row for row in plan}


def test_moves_a_flight_instead_of_delaying_the_next_one():
    value = request()
    assert validate('GateAdviceRequest', value) == []
    advice = advise(value)
    assert validate('GateAdvice', advice) == []
    before, after = by_flight(advice['before']), by_flight(advice['recommended'])
    assert before[A]['gateId'] == before[B]['gateId'] == G1
    assert after[A]['delayMinutes'] == 20 and after[B]['delayMinutes'] == 0
    assert after[A]['gateId'] != after[B]['gateId']
    assert advice['solverStatus'] == 'optimal'
    # Keeping both at G1, B can only arrive once A has left: at 50 instead of 40.
    unmoved = by_flight(advice['withoutMoves'])
    assert unmoved[A]['gateId'] == unmoved[B]['gateId'] == G1
    assert unmoved[B]['delayMinutes'] == 10 and unmoved[B]['arrivalMinute'] == 50


def test_starts_from_the_plan_the_scheduled_delays_already_made():
    # C holds G2 all along, so the second gate is no way out and B has to wait at G1.
    flights = [flight(A, 10, 30), flight(B, 40, 60), flight(C, 0, 90, gate=G2)]
    earlier = {'eventId': identifier(20), 'flightId': B, 'minute': 2, 'delayMinutes': 5}
    later = {'eventId': identifier(21), 'flightId': B, 'minute': 9, 'delayMinutes': 5}
    advice = advise(request(events=[earlier, later], flights=flights))
    before = by_flight(advice['before'])
    assert before[B]['delayMinutes'] == 5, 'only the delays scheduled up to the advice minute are replayed'
    after = by_flight(advice['recommended'])
    assert after[A]['gateId'] == after[B]['gateId'] == G1 and after[C]['gateId'] == G2
    assert after[B]['arrivalMinute'] == after[A]['departureMinute'] == 50
    assert by_flight(advice['withoutMoves']) == after


def test_api_rejects_invalid_requests_and_reports_conflicts():
    with TestClient(app) as client:
        invalid = request() | {'delay': {'flightId': A, 'delayMinutes': 0}}
        assert client.post('/optimization/gates/v1/advice', json=invalid).status_code == 422
        # A left at 30: a delay reported at 35 contradicts the timeline.
        response = client.post('/optimization/gates/v1/advice', json=request(minute=35))
        assert response.status_code == 409 and response.json()['code'] == 'event_conflict'
        response = client.post('/optimization/gates/v1/advice', json=request())
        assert response.status_code == 200 and validate('GateAdvice', response.json()) == []
