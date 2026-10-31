"""Check-in desks before security, in simulated minutes.

The desks are upstream of every control and nothing downstream blocks them, so the stage is solved before the
control simulation: a passenger with hold baggage queues FIFO for the first free desk, is served, walks to the
control and joins it at check-in finish + walkingMinutes. That join time is the control's arrivalMinute, so the
control semantics stay those of the v1 engine. Passengers who reach no control by the horizon stay in their
control's population as not arrived.
"""
import math
import random

from app.simulation.metrics import sample_minutes, wait_statistics


def checks_in(seed, person_id, share):
    """Hold baggage is drawn per passenger, so the choice does not depend on population order."""
    return random.Random(f'{seed}/check-in/{person_id}').random() < share


def service_minutes(seed, person_id, rate):
    return random.Random(f'{seed}/check-in-service/{person_id}').expovariate(rate)


def levels(check_in):
    """Desks open over time as (startMinute, count) steps; a request without steps keeps every desk open from minute 0."""
    steps = check_in.desk_steps
    return [(0, check_in.staff_count)] if not steps else [(step.start_minute, step.staff_count) for step in steps]


def windows(check_in):
    """The spans each desk is open: desk i is open while the step level is above i. A desk that closes finishes its passenger."""
    steps = levels(check_in)
    result = []
    for desk in range(check_in.staff_count):
        spans, begin = [], None
        for start, count in steps:
            if count > desk and begin is None:
                begin = start
            elif count <= desk and begin is not None:
                spans.append((begin, start))
                begin = None
        if begin is not None:
            spans.append((begin, math.inf))
        result.append(spans)
    return result


def next_open(spans, minute):
    """The first minute at or after `minute` the desk is open, or None when it never is again."""
    for begin, end in spans:
        if minute < end:
            return max(minute, begin)
    return None


def open_at(check_in, minute):
    return next((count for start, count in reversed(levels(check_in)) if start <= minute), 0)


def desk_minutes(check_in, minute):
    return sum(max(0, min(end, minute) - begin) for spans in windows(check_in) for begin, end in spans)


def route(check_in, seed, horizon, people):
    """Queue the hold-baggage passengers at the desks and move their control arrival.

    Generated and surge passengers share the desks; only passengers with a flight can check bags.
    Returns the passengers reaching a control by the horizon, those who do not, and one record per check-in journey."""
    walking = check_in.walking_minutes
    queue = sorted((person for person in people if person['flightId'] is not None
                    and checks_in(seed, person['id'], check_in.hold_baggage_share)),
                   key=lambda person: (person['arrivalMinute'], person['id']))
    spans = windows(check_in)
    free = [0.0] * len(spans)
    records, late = [], []
    for person in queue:
        arrival, service = person['arrivalMinute'], service_minutes(seed, person['id'], check_in.service_rate_per_minute)
        start = finish = joins = None
        # First come, first served: the earliest start over every desk, given the passengers already placed.
        best = None
        for desk, desk_spans in enumerate(spans):
            begin = next_open(desk_spans, max(arrival, free[desk]))
            if begin is not None and (best is None or begin < best[0]):
                best = (begin, desk)
        if best is not None:
            start = float(best[0])
            finish = start + service
            free[best[1]] = finish
            joins = finish + walking
        within = lambda minute: minute if minute is not None and minute <= horizon else None
        records.append({'id': person['id'], 'checkpointId': person['checkpointId'], 'terminalArrivalMinute': arrival,
                        'serviceMinutes': service, 'startedMinute': within(start), 'finishedMinute': within(finish),
                        'securityArrivalMinute': within(joins)})
        if within(joins) is None:
            late.append(person)
        else:
            person['arrivalMinute'] = joins
    absent = {person['id'] for person in late}
    return [person for person in people if person['id'] not in absent], late, records


def snapshot(check_in, records, minute):
    """Desk state at a minute: events at the same minute have all happened, like the control snapshots."""
    def by(field):
        return [record for record in records if record[field] is not None and record[field] <= minute]
    arrived = [record for record in records if record['terminalArrivalMinute'] <= minute]
    started, finished = by('startedMinute'), by('finishedMinute')
    waits = [record['startedMinute'] - record['terminalArrivalMinute'] for record in finished]
    mean, median, p95 = wait_statistics(waits)
    busy = sum(min(minute, record['finishedMinute'] if record['finishedMinute'] is not None else minute) - record['startedMinute']
               for record in started)
    capacity = open_at(check_in, minute)
    # Desk-minutes open so far: staffCount × minute when every desk is open from 0.
    offered = desk_minutes(check_in, minute)
    return {'zoneId': str(check_in.zone_id), 'zoneType': 'checkIn', 'minute': minute,
            'arrivedPassengers': len(arrived), 'queuedPassengers': len(arrived) - len(started),
            'processingPassengers': len(started) - len(finished), 'processedPassengers': len(finished),
            'walkingPassengers': sum(record['securityArrivalMinute'] is None or record['securityArrivalMinute'] > minute
                                     for record in finished),
            'maximumQueue': maximum_queue(records, minute), 'meanWaitMinutes': mean, 'medianWaitMinutes': median,
            'p95WaitMinutes': p95, 'capacity': capacity, 'busyMinutes': round(busy, 12),
            'utilization': (round(min(1, busy / offered), 12) if offered else 0.0) if capacity and minute else None}


def maximum_queue(records, minute):
    """Longest queue after all arrivals and service starts of the same instant, up to the minute."""
    changes = {}
    for record in records:
        if record['terminalArrivalMinute'] <= minute:
            changes[record['terminalArrivalMinute']] = changes.get(record['terminalArrivalMinute'], 0) + 1
        if record['startedMinute'] is not None and record['startedMinute'] <= minute:
            changes[record['startedMinute']] = changes.get(record['startedMinute'], 0) - 1
    length = peak = 0
    for time in sorted(changes):
        length += changes[time]
        peak = max(peak, length)
    return peak


def result(check_in, records, horizon, interval):
    return {'zoneId': str(check_in.zone_id), 'capacity': check_in.staff_count, 'populationCount': len(records),
            'processedCount': sum(record['finishedMinute'] is not None for record in records),
            'unfinishedCount': sum(record['securityArrivalMinute'] is None for record in records),
            'snapshots': [snapshot(check_in, records, minute) for minute in sample_minutes(horizon, interval)],
            'passengers': records}
