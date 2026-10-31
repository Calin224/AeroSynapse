"""Frozen v1 queue/forecast engine retained for exact historical replay."""
import heapq
import random
from datetime import timedelta

from app.contracts.models import SimulationRequest, SimulationResponse
from app.passengers.generator import generate_population
from app.navigation.planner import plan_population, summarize


def simulate(request: SimulationRequest, on_progress=None) -> SimulationResponse:
    envelope = {"contractVersion": "1.0", "runId": str(request.run_id), "status": "completed",
                "simulatedMinutes": request.duration_minutes, "metrics": [], "snapshots": [], "failure": None}
    # Bound the work before allocating or sampling a passenger list.
    if request.passenger_generation is None and request.passenger_count * len(request.checkpoints) > 1_000_000:
        return SimulationResponse.model_validate(envelope | {
            "status": "failed", "simulatedMinutes": 0,
            "failure": {"code": "simulation_limit_exceeded", "message": "The v1 queue engine supports at most 1000000 passenger-checkpoint operations."}})
    arrivals = []
    by_checkpoint = None
    navigation_result, journeys = None, {}
    if request.passenger_generation is not None:
        population = generate_population(request.passenger_generation)
        if request.navigation is not None:
            navigation = request.navigation.model_dump(mode='json', by_alias=True)
            navigation_result = plan_population(population, request.passenger_generation, navigation)
            journeys = {person['id']: person for person in navigation_result['journeys']}
        by_checkpoint = {}
        for passenger in population['passengers']:
            if navigation_result is not None and journeys[passenger['id']]['error'] is not None:
                continue
            by_checkpoint.setdefault(passenger['checkpointId'], []).append(passenger)
    else:
        rng = random.Random(request.seed)
        elapsed = 0.0
        for _ in range(request.passenger_count):
            elapsed += rng.expovariate(request.arrival_rate_per_minute)
            if elapsed > request.duration_minutes:
                break
            arrivals.append(elapsed)
    stations = []
    for checkpoint in request.checkpoints:
        people = by_checkpoint.get(str(checkpoint.checkpoint_id), []) if by_checkpoint is not None else None
        stations.append({"input": checkpoint, "services": random.Random((request.seed << 128) ^ checkpoint.checkpoint_id.int),
                         "arrivals": [person['arrivalMinute'] for person in people] if people is not None else arrivals,
                         "sampled_services": [person['serviceMinutes'] for person in people] if people is not None else None,
                         "people": people,
                         "servers": [0.0] * checkpoint.staff_count, "index": 0, "starts": [], "finishes": [],
                         "processed": 0, "wait": 0.0})
    # At most 60 progress frames; the final result contract keeps one snapshot per zone.
    step = max(1, (request.duration_minutes + 59) // 60)
    minutes = list(range(step, request.duration_minutes, step)) + [request.duration_minutes]
    for minute in minutes:
        zones = []
        for station in stations:
            checkpoint = station["input"]
            arrivals = station['arrivals']
            while station["index"] < len(arrivals) and arrivals[station["index"]] <= minute:
                arrival = arrivals[station["index"]]
                station["index"] += 1
                journey = journeys.get(station['people'][station['index'] - 1]['id']) if journeys else None
                if not station["servers"]:
                    if journey is not None: journey['boardingRisk'] = 'unserved'
                    continue
                available = heapq.heappop(station["servers"])
                start = max(arrival, available)
                service = (station['sampled_services'][station['index'] - 1] if station['sampled_services'] is not None
                           else station["services"].expovariate(checkpoint.service_rate_per_minute))
                finish = start + service
                if journey is not None:
                    journey['queueWaitMinutes'] = round(start - arrival, 12)
                    journey['gateArrivalMinute'] = round(finish + journey['walkingMinutes'], 12)
                    journey['boardingRisk'] = 'safe' if journey['gateArrivalMinute'] <= journey['boardingCloseMinute'] else 'late'
                heapq.heappush(station["servers"], finish)
                heapq.heappush(station["starts"], start)
                heapq.heappush(station["finishes"], (finish, start - arrival))
            while station["starts"] and station["starts"][0] < minute:
                heapq.heappop(station["starts"])
            while station["finishes"] and station["finishes"][0][0] <= minute:
                _, wait = heapq.heappop(station["finishes"])
                station["processed"] += 1
                station["wait"] += wait
            zones.append({"zoneId": str(checkpoint.checkpoint_id),
                          "queuedPassengers": len(station["starts"]) if station["servers"] else station["index"],
                          "processedPassengers": station["processed"]})
        if on_progress is not None:
            on_progress({"simulationMinute": minute, "zones": zones})
    recorded = (request.starts_at_utc + timedelta(minutes=request.duration_minutes)).isoformat().replace("+00:00", "Z")
    for station, zone in zip(stations, zones):
        processed = station["processed"]
        envelope["metrics"].append({"zoneId": zone["zoneId"], "recordedAtUtc": recorded,
            "meanWaitMinutes": round(station["wait"] / processed, 6) if processed else 0,
            "throughputPerMinute": round(processed / request.duration_minutes, 6), "processedPassengers": processed})
        envelope["snapshots"].append({"contractVersion": "1.0", "zoneId": zone["zoneId"],
            "elapsedMinutes": request.duration_minutes, "queuedPassengers": zone["queuedPassengers"], "processedPassengers": processed})
    if navigation_result is not None:
        navigation_result['assessed'] = True
        summarize(navigation_result, navigation)
        envelope['navigationResult'] = navigation_result
    return SimulationResponse.model_validate(envelope)
