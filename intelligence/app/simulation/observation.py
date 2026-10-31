"""What an agent sees at a decision minute: the run paused after every event of that minute.

Agent decisions only take effect after the minute they were taken, so observing a prefix with the decisions so far
and rerunning the whole day with all of them give the same history up to every decision minute.
"""
from app.simulation.journeys import gate_options, upstream_request
from app.simulation.processes import simulate


def observe(request, minute, gate_event_id=None):
    security = upstream_request(request) if request.journey is not None else request
    state = simulate(security, stop_at=minute)
    if gate_event_id is not None:
        state['gate'] = gate_options(request, gate_event_id)
    return state
