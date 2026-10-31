"""Dispatch captured engine versions without changing historical run semantics."""
from app.simulation.flows import simulate as simulate_flows
from app.simulation.legacy import simulate as simulate_legacy
from app.simulation.processes import simulate as simulate_processes


def simulate(request, on_progress=None):
    if request.journey is not None:
        from app.simulation.journeys import simulate as simulate_journeys
        return simulate_journeys(request, on_progress)
    # Stage routes are a new input, so no captured run without them changes engine.
    if request.passenger_generation is not None and request.passenger_generation.flow is not None:
        engine = simulate_flows
    else:
        engine = simulate_processes if request.engine in ('simpy-1.0','simpy-kpi-1.0') else simulate_legacy
    return engine(request, on_progress=on_progress)
