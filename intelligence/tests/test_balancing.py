from app.simulation.balancing import MOVE_MINUTES, decide


def person(identifier, arrival=1.0, own='a', group=None):
    return {'id': identifier, 'flightId': 'f', 'groupId': group, 'arrivalMinute': arrival, 'checkpointId': own,
            'serviceMinutes': 1.0, 'walkingMinutes': 3.0}


def state(a_ahead, b_ahead, a_capacity=1, b_capacity=1):
    return {'a': {'capacity': a_capacity, 'rate': 1.0, 'ahead': a_ahead},
            'b': {'capacity': b_capacity, 'rate': 1.0, 'ahead': b_ahead}}


LANES = {'a': 3.0, 'b': 3.0}
EVEN = lambda _: LANES


def test_moves_to_the_shorter_real_queue():
    assert decide([person('p')], 0, state(30, 0), EVEN, {}) == {'p': 'b'}


def test_stays_when_the_gain_is_small():
    assert decide([person('p')], 0, state(3, 2), EVEN, {}) == {}


def test_never_picks_a_control_without_an_operator():
    assert decide([person('p')], 0, state(30, 0, b_capacity=0), EVEN, {}) == {}


def test_leaves_a_closed_own_control():
    assert decide([person('p')], 0, state(0, 0, a_capacity=0), EVEN, {}) == {'p': 'b'}


def test_a_window_does_not_all_pick_the_same_control():
    moved = decide([person(f'p{i}', own='a') for i in range(40)], 0, state(20, 0), EVEN, {})
    assert 0 < len(moved) < 40


def test_a_group_moves_together():
    batch = [person('p1', 1.0, group='g'), person('p2', 1.2, group='g')]
    assert set(decide(batch, 0, state(30, 0), EVEN, {})) == {'p1', 'p2'}


def test_walking_cost_counts():
    lanes = {'a': 3.0, 'b': 3.0 + MOVE_MINUTES + 10}
    assert decide([person('p')], 0, state(4, 0), lambda _: lanes, {}) == {}
