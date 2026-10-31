"""The anticipatory staffing plan: a fluid-queue model that opens servers before a wave and closes them after."""
from app.contracts.models import SimulationRequest
from app.optimization.planning import BLOCK_MINUTES, block_arrivals, recommend, solve
from app.simulation.observation import observe
from tests import test_check_in
from tests.test_agent_observation import observed


def resource(key, queue, arrivals, rate=0.5, current=1, high=6):
    return {'key': key, 'queue': queue, 'arrivals': arrivals, 'rate': rate, 'current': current, 'low': 1, 'high': high}


def test_arrivals_are_counted_in_the_block_they_reach_after_the_decision():
    assert block_arrivals([10, 11, 12, 15.5, 16, 17, 80], 10, 3) == [3, 2, 0]


def test_servers_open_before_a_wave_and_close_after_it():
    arrivals = [0, 0, 30, 30, 0, 0, 0, 0]
    levels = solve([resource('desks', 0, arrivals)])['desks']
    assert max(levels) > 1 and levels[-1] <= levels[2]
    # Open only for the wave: before and after it the single desk is enough.
    assert levels[0] == 1 and levels[-1] == 1


def test_more_servers_never_lengthen_the_final_queue_than_the_current_level():
    arrivals = [20] * 6
    planned = solve([resource('desks', 5, arrivals)])['desks']

    def final_queue(levels):
        queue = 5
        for level, arrived in zip(levels, arrivals):
            queue = max(0, queue + arrived - level * 0.5 * BLOCK_MINUTES)
        return queue
    assert final_queue(planned) < final_queue([1] * 6)


def test_a_shared_pool_goes_to_the_control_that_needs_it():
    busy, calm = resource('busy', 40, [30] * 6, current=2), resource('calm', 0, [2] * 6, current=2)
    plan = solve([busy, calm], total=6)
    assert plan['busy'][-1] > 2 and plan['calm'][-1] >= 1
    assert all(plan['busy'][k] + plan['calm'][k] <= 6 for k in range(6))


def test_blocks_inside_the_transfer_keep_the_current_level():
    plan = solve([resource('busy', 40, [30] * 6, current=2, high=6)], total=6, locked=2)
    assert plan['busy'][:2] == [2, 2]


def test_nothing_to_plan_without_demand():
    limits = {'deskLimit': 4, 'reserve': 2, 'transferMinutes': 5}
    assert recommend(30, 120, limits, [{'key': 'a', 'queue': 0, 'times': [], 'rate': 0.5, 'current': 2}],
                     {'queue': 0, 'times': [], 'rate': 0.5, 'current': 2}) is None


def test_recommendation_reinforces_a_loaded_control_from_the_reserve_and_the_desks():
    limits = {'deskLimit': 5, 'reserve': 3, 'transferMinutes': 5}
    times = [31 + index * 0.2 for index in range(120)]
    plan = recommend(30, 120, limits, [{'key': 'a', 'queue': 20, 'times': times, 'rate': 0.5, 'current': 2}],
                     {'queue': 10, 'times': times, 'rate': 0.5, 'current': 1})
    assert plan['desks'][0]['startMinute'] == 31 and plan['desks'][0]['staffCount'] > 1
    assert all(step['staffCount'] <= 5 for step in plan['desks'])
    target = plan['staff'][0]
    assert target['target'] > target['current'] and plan['reserveUsed'] == target['target'] - target['current'] <= 3


def test_frozen_controls_are_left_out_of_the_plan():
    limits = {'reserve': 3, 'transferMinutes': 0, 'frozen': ['a']}
    plan = recommend(30, 120, limits, [{'key': 'a', 'queue': 50, 'times': [40] * 50, 'rate': 0.5, 'current': 1}], None)
    assert plan is None


def test_the_observation_carries_the_plan_only_when_limits_are_given():
    value = test_check_in.base(passengers=120, check_in={'staffCount': 1})
    request = SimulationRequest.model_validate(value)
    assert observed(value, 20)['recommendation'] is None
    state = observe(request, 20, None, {'deskLimit': 4, 'reserve': 0, 'transferMinutes': 0})
    assert state['recommendation']['desks'][0]['staffCount'] > 1
