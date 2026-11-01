from types import SimpleNamespace as NS

from app.simulation.balancing import balance

A, B = 'a', 'b'


def controls(staff_a=1, staff_b=1):
    return {A: NS(staff_count=staff_a, service_rate_per_minute=1.0), B: NS(staff_count=staff_b, service_rate_per_minute=1.0)}


def flight(spread=True):
    routes = [NS(checkpoint_id=A, route_travel_minutes=3.0), NS(checkpoint_id=B, route_travel_minutes=3.0)] if spread else None
    return {'f': NS(checkpoint_routes=routes)}


def person(number, group=None, arrival=0.0, flight_id='f'):
    return {'id': f'p{number:02d}', 'flightId': flight_id, 'groupId': group or f'g{number:02d}', 'checkpointId': A,
            'arrivalMinute': arrival, 'serviceMinutes': 2.0, 'walkingMinutes': 3.0, 'errorCode': None}


def test_a_crowd_at_one_control_is_shared_with_the_free_one():
    people = [person(number) for number in range(12)]
    moved = balance(people, set(), controls(), flight(), {})
    assert moved and {item['id'] for item in people if item['checkpointId'] == B} == moved
    assert abs(sum(item['checkpointId'] == A for item in people) - sum(item['checkpointId'] == B for item in people)) <= 2


def test_a_move_is_not_worth_it_when_the_queue_is_short():
    people = [person(0), person(1, arrival=10.0)]
    assert balance(people, set(), controls(), flight(), {}) == set()


def test_only_spread_flights_pinned_people_and_errors_stay_put():
    people = [person(number) for number in range(12)]
    assert balance(people, set(), controls(), flight(spread=False), {}) == set()
    assert balance(people, {item['id'] for item in people}, controls(), flight(), {}) == set()
    for item in people: item['errorCode'] = 'route_unreachable'
    assert balance(people, set(), controls(), flight(), {}) == set()


def test_a_group_moves_together():
    people = [person(number, group=f'g{number // 3}') for number in range(12)]
    balance(people, set(), controls(), flight(), {})
    for group in {item['groupId'] for item in people}:
        assert len({item['checkpointId'] for item in people if item['groupId'] == group}) == 1


def test_a_closed_control_is_never_chosen_and_balancing_is_deterministic():
    people = [person(number) for number in range(12)]
    assert balance(people, set(), controls(staff_b=0), flight(), {}) == set()
    first, second = [person(number) for number in range(12)], [person(number) for number in range(12)]
    balance(first, set(), controls(), flight(), {})
    balance(second, set(), controls(), flight(), {})
    assert first == second
