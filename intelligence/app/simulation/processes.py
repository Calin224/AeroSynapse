"""SimPy processes and bounded resources. One time unit is one virtual minute."""
import math
import random
from collections import deque
from datetime import timedelta
from uuid import UUID

import simpy
from simpy.events import URGENT

from app.contracts.models import SimulationResponse
from app.navigation.graph import NavigationGraph
from app.navigation.planner import plan_population, summarize
from app.passengers.generator import generate_population
from app.simulation import check_in
from app.simulation.events import ordered, planned_at
from app.simulation.metrics import ENGINE, KpiCollector, sample_minutes


def simulate(request, on_progress=None, stop_at=None):
    """Run the day. With stop_at, stop after every event of that minute and return the observed state instead.

    Changes that take effect after stop_at cannot alter anything up to it, so an agent can observe, decide and
    rerun with its decisions added: the full run with every decision gives the same history."""
    envelope = {'contractVersion':'1.0','runId':str(request.run_id),'status':'completed',
                'simulatedMinutes':request.duration_minutes,'metrics':[],'snapshots':[],'failure':None}
    if request.passenger_count > 100000:
        return SimulationResponse.model_validate(envelope | {'status':'failed','simulatedMinutes':0,
            'failure':{'code':'simulation_limit_exceeded','message':'The SimPy engine supports at most 100000 passengers.'}})
    env = simpy.Environment()
    events = ordered([event.wire() for event in request.events or []])
    stations = {}
    schedules = {str(item.checkpoint_id): item.steps for item in getattr(request, 'staff_schedules', None) or []}
    for checkpoint in sorted(request.checkpoints, key=lambda item: str(item.checkpoint_id)):
        identifier = str(checkpoint.checkpoint_id)
        stations[identifier] = {'capacity':checkpoint.staff_count,'peakBusy':0,'peakQueue':0,'arrivedCount':0,'queuedCount':0,
            'processingCount':0,'processedCount':0,'waitTotal':0.0,'input':checkpoint,
            'busyArea':0.0,'busyLastMinute':0.0,'completedWaits':[],'walkingCount':0,'atGateCount':0,
            'unreachableCount':0,'firstQueueMinute':None,'riskPopulation':0,'atRiskCount':0,'lateGateCount':0,
            # Capacity in effect: staff on duty while available. 'capacity' stays the nominal staffing,
            # the most people a schedule ever puts on duty; 'planned' is the scheduled staffing now.
            'staff':planned_at(checkpoint,schedules.get(identifier),0),'planned':planned_at(checkpoint,schedules.get(identifier),0),'available':True,'granted':0,'waiting':deque()}
    people, navigation_result, journeys, samples, late, check_in_records = [], None, {}, {}, [], []
    flights, navigation, gates = {}, None, {}
    if request.passenger_generation is not None:
        population = generate_population(request.passenger_generation)
        flights = {str(flight.flight_id):flight for flight in request.passenger_generation.flights}
        if request.navigation is not None:
            navigation = request.navigation.model_dump(mode='json', by_alias=True)
            navigation_result = plan_population(population, request.passenger_generation, navigation)
            journeys = {person['id']:person for person in navigation_result['journeys']}
            gates = {item['flightId']:item['gateId'] for item in navigation['flightGates']}
        for sampled in population['passengers']:
            flight = flights[sampled['flightId']]
            journey = journeys.get(sampled['id'])
            samples[sampled['id']] = sampled
            people.append(passenger(sampled['id'],sampled['checkpointId'],sampled['arrivalMinute'],sampled['serviceMinutes'],
                journey['walkingMinutes'] if journey and journey['error'] is None else sampled['travelMinutes'],
                sampled['flightId'],journey['gateId'] if journey else None,flight.boarding_close_minute,
                journey['error']['code'] if journey and journey['error'] else None))
    else:
        # Aggregate mode assigns each arrival once; flight profiles supply synthetic gate/deadline associations.
        arrivals = random.Random(request.seed)
        services = {key:random.Random((request.seed << 128) ^ station['input'].checkpoint_id.int) for key,station in stations.items()}
        minute = 0.0
        keys = list(stations)
        for index in range(request.passenger_count):
            minute += arrivals.expovariate(request.arrival_rate_per_minute)
            if minute > request.duration_minutes: break
            key = keys[index % len(keys)]
            service = services[key].expovariate(stations[key]['input'].service_rate_per_minute)
            people.append(passenger(f'demo-queue-{key.replace("-", "")}-{index+1:06d}',key,minute,service,0))
    surges = {event['eventId']:surge(request,event,stations,flights,gates,samples) for event in events if event['type']=='passenger_surge'}
    surged = {person['id'] for group in surges.values() for person in group}
    people.extend(person for group in surges.values() for person in group)
    if request.check_in is not None:
        # Generated and surge arrivals are terminal arrivals; check-in moves the control arrival of hold-baggage passengers.
        people, late, check_in_records = check_in.route(request.check_in, request.seed, request.duration_minutes, people)
    people.sort(key=lambda person:(person['arrivalMinute'],person['id']))
    collector=KpiCollector(request,stations,[person for person in people if person['id'] not in surged],
                           [person for person in late if person['id'] not in surged]) if request.engine==ENGINE else None
    routing = Routing(navigation, samples, flights, journeys) if events and navigation is not None else None
    close_now = {key:flight.boarding_close_minute for key,flight in flights.items()}
    deadlines = {}
    held = set()
    log = []

    def observe(minute):
        """Each control and the desks at a decision minute: queues, staffing, recent waits and the next 30 minutes' arrivals."""
        upcoming, recent, longest = {}, {}, {}
        for person in people:
            key = person['checkpointId']
            if minute < person['arrivalMinute'] <= minute+30: upcoming[key] = upcoming.get(key,0)+1
            started = person['processingStartedMinute']
            if started is not None and minute-15 < started <= minute: recent.setdefault(key,[]).append(person['queueWaitMinutes'])
            if person['stage'] == 'queued' and person['queueEnteredMinute'] is not None:
                longest[key] = max(longest.get(key,0.0), minute-person['arrivalMinute'])
        mean = lambda values: round(sum(values)/len(values),3) if values else None
        checkpoints = [{'checkpointId':key,'staffOnDuty':capacity_now(station),'plannedStaff':station['planned'],
            'available':station['available'],'serviceRatePerMinute':station['input'].service_rate_per_minute,
            'queued':station['queuedCount'],'processing':station['processingCount'],'processed':station['processedCount'],
            'arrivingNext30Minutes':upcoming.get(key,0),'recentMeanWaitMinutes':mean(recent.get(key,[])),
            'longestWaitMinutes':round(longest.get(key,0.0),3),'boardingAtRisk':station['atRiskCount'],
            'notArrived':collector.populations[key]-station['arrivedCount']-station['unreachableCount'] if collector is not None else None}
            for key,station in stations.items()]
        desks = None
        if request.check_in is not None:
            arrived = [r for r in check_in_records if r['terminalArrivalMinute'] <= minute]
            started = [r for r in check_in_records if r['startedMinute'] is not None and r['startedMinute'] <= minute]
            desks = {'zoneId':str(request.check_in.zone_id),'desksOpen':check_in.open_at(request.check_in,minute),
                'serviceRatePerMinute':request.check_in.service_rate_per_minute,'queued':len(arrived)-len(started),
                'arrivingNext30Minutes':sum(minute < r['terminalArrivalMinute'] <= minute+30 for r in check_in_records),
                'recentMeanWaitMinutes':mean([r['startedMinute']-r['terminalArrivalMinute'] for r in started if minute-15 < r['startedMinute']])}
        return {'observationVersion':'1.0','minute':minute,'checkpoints':checkpoints,'checkIn':desks}

    def set_missed(person,missed):
        if person['deadlineMissed']!=missed:
            stations[person['checkpointId']]['atRiskCount']+=1 if missed else -1
        person['deadlineMissed']=missed

    def record_queue(station,length):
        station['peakQueue']=max(station['peakQueue'],length)
        if length and station['firstQueueMinute'] is None: station['firstQueueMinute']=env.now

    def change_busy(station,amount):
        station['busyArea']+=station['processingCount']*(env.now-station['busyLastMinute'])
        station['busyLastMinute']=env.now
        station['processingCount']+=amount

    def capacity_now(station):
        return station['staff'] if station['available'] else 0

    # FIFO slots with mutable capacity, granted at the same points as simpy.Resource grants them.
    def dispatch(station):
        while station['waiting'] and station['granted'] < capacity_now(station):
            station['granted']+=1
            station['waiting'].popleft().succeed()

    def request_slot(station):
        grant = env.event()
        station['waiting'].append(grant)
        dispatch(station)
        return grant

    def release(station):
        station['granted']-=1
        freed = env.event()
        freed.callbacks.append(lambda _: dispatch(station))
        freed.succeed()

    def deadline(person):
        flight = person['flightId']
        while True:
            target = close_now[flight]
            try:
                yield env.timeout(max(0,target-env.now))
            except simpy.Interrupt:
                continue  # A flight change moved the deadline; wait for the new one.
            if close_now[flight] == target:
                set_missed(person,person['gateArrivalMinute'] is None or person['gateArrivalMinute'] > env.now)
                return

    def watch(person):
        deadlines.setdefault(person['flightId'],[]).append(env.process(deadline(person)))

    def redirect(person,station):
        """New demand for an unavailable control goes to the open control with the shortest queue."""
        candidates = [key for key,item in stations.items() if capacity_now(item) > 0
                      and (routing is None or routing.bound(key))]
        if not candidates: return station
        target = min(candidates,key=lambda key:(len(stations[key]['waiting']),key))
        source = person['checkpointId']
        person['assignedCheckpointId'] = source
        person['checkpointId'] = target
        moved = stations[target]
        if collector is not None: collector.move(source,target)
        if person['boardingCloseMinute'] is not None:
            station['riskPopulation']-=1; moved['riskPopulation']+=1
            if person['deadlineMissed']: station['atRiskCount']-=1; moved['atRiskCount']+=1
        return moved

    def process(person):
        station = stations[person['checkpointId']]
        if person['errorCode']:
            person['stage'] = 'unreachable'
            station['unreachableCount']+=1
            return
        if not station['available']:
            station = redirect(person,station)
        station['arrivedCount'] += 1
        person['queueEnteredMinute'] = person['arrivalMinute']
        person['stage'] = 'queued'
        station['queuedCount'] += 1
        grant = request_slot(station)
        record_queue(station,len(station['waiting']))
        yield grant  # A control without capacity never grants a slot until capacity returns.
        station['queuedCount'] -= 1
        change_busy(station,1)
        station['peakBusy'] = max(station['peakBusy'],station['processingCount'])
        # Reductions never preempt service, so only nominal staffing bounds simultaneous service.
        assert station['processingCount'] <= station['capacity']
        person['stage'] = 'processing'
        person['processingStartedMinute'] = env.now
        person['queueWaitMinutes'] = env.now-person['arrivalMinute']
        yield env.timeout(person['serviceMinutes'])
        person['securityClearedMinute'] = env.now
        change_busy(station,-1)
        station['processedCount'] += 1
        station['waitTotal'] += person['queueWaitMinutes']
        if collector is not None: station['completedWaits'].append(person['queueWaitMinutes'])
        release(station)
        # Walking starts only after the service timeout and the resource release.
        person['stage'] = 'walking'
        station['walkingCount']+=1
        if routing is not None and person['id'] in samples and person['errorCode'] is None:
            walking = routing.walk(person, env.now)
            while walking is None:
                # No navigable route now: wait at the control exit until a corridor event changes the graph.
                held.add(person['id'])
                yield routing.changed
                walking = routing.walk(person, env.now)
            if person['id'] in held:
                held.discard(person['id'])
                person['routeHoldMinutes'] = env.now-person['securityClearedMinute']
            person['walkingMinutes'] = walking
        yield env.timeout(person['walkingMinutes'])
        person['gateArrivalMinute'] = env.now
        person['stage'] = 'at_gate'
        station['walkingCount']-=1
        station['atGateCount']+=1
        if person['boardingCloseMinute'] is not None:
            set_missed(person,env.now > person['boardingCloseMinute'])
            if person['deadlineMissed']: station['lateGateCount']+=1

    def apply(batch):
        for event in batch:
            kind = event['type']
            if kind in ('checkpoint_unavailable','staff_reduction') or kind == 'resume' and 'checkpointId' in event:
                station = stations[event['checkpointId']]
                if kind == 'checkpoint_unavailable': station['available'] = False
                elif kind == 'staff_reduction': station['staff'] = event['staffCount']  # In-service passengers finish.
                else:
                    station['available'], station['staff'] = True, station['planned']
                    dispatch(station)
            elif kind in ('corridor_closure','resume'):
                routing.toggle(event['zoneId'], kind == 'corridor_closure')
                current, routing.changed = routing.changed, env.event()
                current.succeed()
            elif kind == 'flight_change':
                flight = event['flightId']
                close_now[flight] += event['delayMinutes']
                for person in (*people, *late):
                    if person['flightId'] == flight: person['boardingCloseMinute'] = close_now[flight]
                for waiting in deadlines.get(flight,[]):
                    if waiting.is_alive: waiting.interrupt()
            else:
                for person in surges[event['eventId']]:
                    if collector is not None: collector.add(person['checkpointId'])
                    if person['boardingCloseMinute'] is not None:
                        stations[person['checkpointId']]['riskPopulation']+=1
                        watch(person)
            log.append({'eventId':event['eventId'],'type':kind,'minute':event['minute'],'sequence':len(log)+1})

    # Event batches are scheduled first and urgently: they act before anything else at their minute.
    for minute in sorted({event['minute'] for event in events}):
        batch = [event for event in events if event['minute'] == minute]
        trigger = simpy.Event(env)
        trigger._ok, trigger._value = True, None
        trigger.callbacks.append(lambda _, batch=batch: apply(batch))
        env.schedule(trigger, URGENT, minute)

    def restaff(station,count):
        # Like staff_reduction, a smaller schedule step never preempts passengers already in service.
        station['planned']=station['staff']=count
        dispatch(station)

    # Scheduled staffing steps act at the start of their minute, after that minute's events.
    for key,station in stations.items():
        for step in (schedules.get(key) or [])[1:]:
            trigger = simpy.Event(env)
            trigger._ok, trigger._value = True, None
            trigger.callbacks.append(lambda _, station=station, count=step.staff_count: restaff(station,count))
            env.schedule(trigger, URGENT, step.start_minute)
    if routing is not None: routing.changed = env.event()

    def arrivals():
        for person in people:
            # now+(arrival-now) can round one ulp below the plan; step up so service never precedes arrival.
            delay = max(0.0,person['arrivalMinute']-env.now)
            while env.now+delay < person['arrivalMinute']: delay = math.nextafter(delay,math.inf)
            yield env.timeout(delay)
            env.process(process(person))

    env.process(arrivals())
    # Passengers still at check-in never reach their gate: at boarding close they count as missed.
    for person in (*people, *late):
        if person['boardingCloseMinute'] is not None and person['id'] not in surged: watch(person)
    step = max(1,(request.duration_minutes+59)//60)
    progress_minutes=set([*range(step,request.duration_minutes,step),request.duration_minutes])
    snapshot_minutes=set(sample_minutes(request.duration_minutes,request.snapshot_interval_minutes)) if collector is not None else set()
    for minute in sorted(progress_minutes | snapshot_minutes | ({stop_at} if stop_at is not None else set())):
        env.timeout(minute-env.now)
        # Drain every event at the boundary, including grants, zero walking and completion at minute 90.
        while env.peek() <= minute: env.step()
        zones = [{'zoneId':key,'queuedPassengers':station['queuedCount'],'processedPassengers':station['processedCount']} for key,station in stations.items()]
        if collector is not None:
            for zone in zones:
                station = stations[zone['zoneId']]
                zone['notArrivedPassengers'] = collector.populations[zone['zoneId']] - station['arrivedCount'] - station['unreachableCount']
        if collector is not None and minute in snapshot_minutes:
            collector.sample(minute,stations,{key:capacity_now(station) for key,station in stations.items()} if request.events is not None else None)
        if on_progress is not None and minute in progress_minutes: on_progress({'simulationMinute':minute,'zones':zones})
        if minute == stop_at: return observe(minute)
    for person in people:
        if person['id'] in held:
            # Still waiting for a navigable route at the horizon: no walk has started.
            person.update(awaitingRoute=True,routeHoldMinutes=request.duration_minutes-person['securityClearedMinute'],walkingMinutes=0)
        if person['errorCode']: person['boardingRisk'] = 'unreachable'
        elif person['boardingCloseMinute'] is None: person['boardingRisk'] = 'not_applicable'
        elif person['gateArrivalMinute'] is not None: person['boardingRisk'] = 'late' if person['deadlineMissed'] else 'safe'
        else: person['boardingRisk'] = 'missed' if person['deadlineMissed'] else 'pending'
    population_count = request.passenger_count+len(surged)
    result = {'processVersion':'1.0','engine':request.engine,'mode':'flight_profiles' if request.passenger_generation else 'aggregate',
        'horizonMinutes':request.duration_minutes,'populationCount':population_count,'arrivedCount':len(people),
        'notArrivedCount':population_count-len(people),'deadlineMissedCount':sum(person['deadlineMissed'] for person in people),
        'checkpoints':[],'passengers':people,'offset':0,'returnedCount':len(people),'hasMore':False}
    if request.events is not None: result['events'] = log
    for field,stage in [('queuedCount','queued'),('processingCount','processing'),('walkingCount','walking'),('atGateCount','at_gate'),('unreachableCount','unreachable')]:
        result[field] = sum(person['stage']==stage for person in people)
    recorded = (request.starts_at_utc+timedelta(minutes=request.duration_minutes)).isoformat().replace('+00:00','Z')
    for key,station in stations.items():
        processed = station['processedCount']
        mean = round(station['waitTotal']/processed,6) if processed else 0
        result['checkpoints'].append({'checkpointId':key,**{field:station[field] for field in ('capacity','peakBusy','peakQueue','arrivedCount','queuedCount','processingCount','processedCount')},'meanWaitMinutes':mean})
        envelope['metrics'].append({'zoneId':key,'recordedAtUtc':recorded,'meanWaitMinutes':mean,'throughputPerMinute':round(processed/request.duration_minutes,6),'processedPassengers':processed})
        envelope['snapshots'].append({'contractVersion':'1.0','zoneId':key,'elapsedMinutes':request.duration_minutes,'queuedPassengers':station['queuedCount'],'processedPassengers':processed})
    if navigation_result is not None:
        for person in people:
            journey = journeys.get(person['id'])
            if journey is None: continue
            journey['boardingCloseMinute'] = person['boardingCloseMinute']
            if journey['error']: continue
            if person['gateArrivalMinute'] is not None:
                journey.update(queueWaitMinutes=person['queueWaitMinutes'],gateArrivalMinute=person['gateArrivalMinute'],boardingRisk=person['boardingRisk'])
            else:
                journey['boardingRisk'] = 'unserved'
        navigation_result['assessed'] = True
        summarize(navigation_result,navigation)
        envelope['navigationResult'] = navigation_result
    envelope['processResult'] = result
    if collector is not None: envelope['kpiResult']=collector.result(result)
    if request.check_in is not None:
        envelope['checkInResult']=check_in.result(request.check_in,check_in_records,request.duration_minutes,request.snapshot_interval_minutes)
    return SimulationResponse.model_validate(envelope)


def surge_samples(request, event, flight):
    """The same sampled access/service/travel profiles for upstream and downstream stages."""
    count = event['passengerCount']
    if flight is not None:
        # Reuse passenger generation for group access, service and travel; the event supplies count, control and arrivals,
        # so its passengers stay at the event's checkpoint even when the flight's batch is spread.
        batch = next(item for item in request.passenger_generation.batches if item.flight_id == flight.flight_id)
        generation = request.passenger_generation.model_copy(update={
            'seed': request.seed ^ UUID(event['eventId']).int,
            'flights': [flight.model_copy(update={'passenger_capacity': count, 'checkpoint_id': UUID(event['checkpointId']),
                                                  'checkpoint_routes': None})],
            'batches': [batch.model_copy(update={'modeled_count': count, 'count_mode': 'full',
                                               'checkpoint_id': UUID(event['checkpointId']), 'surges': [],
                                               'checkpoint_mode': None})]})
        generated = generate_population(generation)['passengers']
        generated.sort(key=lambda person: person['id'])
    else:
        generated = [None] * count
    return {f'demo-surge-{UUID(event["eventId"]).hex}-{number:06d}': sampled
            for number, sampled in enumerate(generated, 1)}


def surge(request, event, stations, flights, gates, samples):
    """Extra demand over [minute, minute+window), with an independent stream and the flight's profiles."""
    count, window, start = event['passengerCount'], event['windowMinutes'], event['minute']
    rate = stations[event['checkpointId']]['input'].service_rate_per_minute
    services = random.Random(request.seed ^ UUID(event['eventId']).int)
    flight = flights.get(event.get('flightId'))
    people = []
    for number, (identifier, sampled) in enumerate(surge_samples(request, event, flight).items(), 1):
        arrival = start + window*(number-0.5)/count
        if sampled is not None: samples[identifier] = sampled
        people.append(passenger(identifier, event['checkpointId'], arrival,
            sampled['serviceMinutes'] if sampled else services.expovariate(rate), sampled['travelMinutes'] if sampled else 0,
            event.get('flightId'), gates.get(event.get('flightId')), flight.boarding_close_minute if flight else None))
    return people


class Routing:
    """Time-dependent walks for event runs: the route is chosen when walking starts, under the closures then active."""

    def __init__(self, navigation, samples, flights, journeys):
        self.navigation, self.samples, self.flights = navigation, samples, flights
        self.planned = journeys.keys()
        self.static = set(navigation['closedZoneIds'])
        self.closed = set(self.static)
        self.graphs = {}
        self.checkpoints = {item['resourceId']: item['zoneId'] for item in navigation['checkpointBindings']}
        self.gates = {item['resourceId']: item['zoneId'] for item in navigation['gateBindings']}
        self.destinations = {item['flightId']: item['gateId'] for item in navigation['flightGates']}
        self.changed = None

    def bound(self, checkpoint):
        return checkpoint in self.checkpoints

    def toggle(self, zone, closed):
        (self.closed.add if closed else self.closed.discard)(zone)

    def graph(self):
        key = frozenset(self.closed)
        if key not in self.graphs:
            self.graphs[key] = NavigationGraph(self.navigation | {'closedZoneIds': sorted(self.closed)})
        return self.graphs[key]

    def walk(self, person, now):
        """Walking minutes from the serving control under current closures, or None while no route exists."""
        if self.closed == self.static and 'assignedCheckpointId' not in person and person['id'] in self.planned:
            return person['walkingMinutes']  # The captured plan is still valid.
        sampled = self.samples[person['id']]
        source = self.checkpoints.get(person['checkpointId'])
        target = self.gates.get(self.destinations[person['flightId']])
        if source is None or target is None:
            return None
        route = self.graph().route(source, target, sampled['groupKind'])
        if route['error'] is not None:
            return None
        flight = self.flights[person['flightId']]
        graph = self.graph()
        walking = route['baseWalkingMinutes'] * sampled['travelMinutes'] / flight.travel_minutes(sampled['routeId']) \
            * graph.profiles[sampled['groupKind']]['walkingMultiplier']
        return round(walking, 12) if walking <= 10080 else None


def passenger(identifier,checkpoint,arrival,service,walking,flight=None,gate=None,close=None,error=None):
    return {'id':identifier,'checkpointId':checkpoint,'flightId':flight,'gateId':gate,'arrivalMinute':arrival,
        'serviceMinutes':service,'walkingMinutes':walking,'boardingCloseMinute':close,'stage':'queued',
        'queueEnteredMinute':None,'processingStartedMinute':None,'securityClearedMinute':None,'gateArrivalMinute':None,
        'queueWaitMinutes':None,'boardingRisk':'pending','deadlineMissed':False,'errorCode':error}
