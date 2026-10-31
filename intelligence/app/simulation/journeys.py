"""Complete journeys downstream of the existing check-in/security simulation.

Downstream queues never block upstream resources. We can therefore reuse their exact
captured trace, then execute passports, transfers, boarding and rolling gate plans in
one SimPy clock. Only this report assesses actual boarding and connection outcomes.
"""
import simpy
from simpy.events import URGENT

from app.contracts.models import SimulationRequest, SimulationResponse
from app.navigation.graph import NavigationGraph
from app.optimization.gates import GatePlanningError, initial, solve
from app.passengers.generator import generate_population
from app.simulation.events import ordered
from app.simulation.metrics import sample_minutes
from app.simulation.processes import simulate as simulate_security, surge_samples


def upstream_request(request):
    wire = request.model_dump(mode='json', by_alias=True)
    journey = wire.pop('journey')
    wire.pop('navigation', None)
    wire['events'] = [e for e in wire.get('events', []) if e['type'] not in ('flight_change','corridor_closure')
                      and not (e['type']=='resume' and 'zoneId' in e)]
    transfers = {}
    for c in journey['connections']:
        transfers[c['outboundFlightId']] = transfers.get(c['outboundFlightId'],0) + c['passengerCount']
    for b in wire['passengerGeneration']['batches']:
        b['modeledCount'] -= transfers.get(b['flightId'],0)
        if transfers.get(b['flightId'],0): b['countMode'] = 'subset'
    wire['passengerCount'] -= sum(transfers.values())
    wire['arrivalRatePerMinute'] = wire['passengerCount']/wire['durationMinutes']
    return SimulationRequest.model_validate(wire)


def absorb(flights, plan, minute, event, mode='optimal'):
    """Apply a delay: the rolling optimum, or with keep_gates every flight at the gate it holds when such a plan exists."""
    if mode == 'keep_gates':
        pinned = [f | {'eligibleGateIds': [plan[f['flightId']]['gateId']]} for f in flights]
        try:
            return solve(pinned, plan, minute, event)
        except GatePlanningError:
            pass
    return solve(flights, plan, minute, event)


def gate_options(request, event_id):
    """The plan before a scheduled delay and both ways to absorb it, honoring the choices taken for earlier delays."""
    flights = request.journey['flights']
    choices = {c['eventId']: c['mode'] for c in request.journey.get('gateChoices', [])}
    plan, _ = solve(flights, initial(flights), 0)
    for event in ordered([e.wire() for e in request.events or [] if e.type == 'flight_change']):
        if event['eventId'] != event_id:
            plan, _ = absorb(flights, plan, event['minute'], event, choices.get(event['eventId'], 'optimal'))
            continue
        optimal, _ = solve(flights, plan, event['minute'], event)
        try:
            kept, _ = solve([f | {'eligibleGateIds': [plan[f['flightId']]['gateId']]} for f in flights], plan, event['minute'], event)
        except GatePlanningError:
            kept = None
        def summary(after):
            moved = [{'flightId': key, 'fromGateId': plan[key]['gateId'], 'toGateId': value['gateId']}
                     for key, value in sorted(after.items()) if value['gateId'] != plan[key]['gateId'] and not value['isCancelled']]
            return {'moves': moved, 'totalDelayMinutes': round(sum(v['delayMinutes'] for v in after.values() if not v['isCancelled']), 3)}
        return {'eventId': event_id, 'minute': event['minute'], 'flightId': event['flightId'], 'delayMinutes': event['delayMinutes'],
                'optimal': summary(optimal), 'keepGates': summary(kept) if kept is not None else None}
    return None


def simulate(request, on_progress=None):
    envelope = {'contractVersion':'1.0','runId':str(request.run_id),'status':'failed','simulatedMinutes':0,
                'metrics':[],'snapshots':[],'failure':None}
    try:
        return run(request, on_progress)
    except GatePlanningError as error:
        return SimulationResponse.model_validate(envelope | {'failure':{'code':error.code,'message':str(error)}})


def run(request, on_progress):
    j = request.journey
    upstream_input = upstream_request(request)
    upstream = simulate_security(upstream_input).model_dump(mode='json',by_alias=True)
    if upstream['status'] != 'completed':
        return SimulationResponse.model_validate(upstream)
    env = simpy.Environment()
    horizon = request.duration_minutes
    flights = {f['flightId']: f for f in j['flights']}
    plan, status = solve(j['flights'], initial(j['flights']), 0)
    plans = [{'minute':0,'eventId':None,'solverStatus':status,'flights':list(plan.values())}]
    choices = {c['eventId']: c['mode'] for c in j.get('gateChoices', [])}
    changed = env.event()
    passports = simpy.Resource(env,j['passportStaffCount']) if j['passportStaffCount'] else None
    gate_ids = {g for f in j['flights'] for g in f['eligibleGateIds']}
    gates = {g: simpy.Resource(env,j['boardingStaffPerGate']) if j['boardingStaffPerGate'] else None for g in gate_ids}
    routes = {(r['checkpointId'],r['gateId']):r['walkingMinutes'] for r in j['routes']}
    base_gates = {f['flightId']: f['gateId'] for f in j['flights']}
    nav = request.navigation.model_dump(mode='json',by_alias=True) if request.navigation else None
    closed = set(nav['closedZoneIds']) if nav else set()
    graph_cache = {}
    checkpoint_zones = {b['resourceId']:b['zoneId'] for b in nav['checkpointBindings']} if nav else {}
    gate_zones = {b['resourceId']:b['zoneId'] for b in nav['gateBindings']} if nav else {}
    records, snapshots = [], []
    metadata = {}
    # Preserve the access profile and sampled walking multiplier of each originating
    # passenger when navigating to a reassigned gate. Transfers use their explicit time.
    samples = {p['id']:p for p in generate_population(upstream_input.passenger_generation)['passengers']} if nav else {}
    profiles = {str(f.flight_id): f for f in upstream_input.passenger_generation.flights}
    if nav:
        for event in upstream_input.events or []:
            if event.type == 'passenger_surge':
                samples.update(surge_samples(upstream_input, event.wire(), profiles[str(event.flight_id)]))

    def notify():
        nonlocal changed
        previous, changed = changed, env.event()
        previous.succeed()

    def apply(event):
        nonlocal plan
        if event['type']=='flight_change':
            plan, solved = absorb(j['flights'],plan,env.now,event,choices.get(event['eventId'],'optimal'))
            plans.append({'minute':env.now,'eventId':event['eventId'],'solverStatus':solved,'flights':list(plan.values())})
        elif event['type']=='corridor_closure': closed.add(event['zoneId'])
        elif event['type']=='resume': closed.discard(event['zoneId'])
        notify()

    for event in ordered([e.wire() for e in request.events or []]):
        if event['type'] not in ('flight_change','corridor_closure') and not (event['type']=='resume' and 'zoneId' in event):
            continue
        trigger = simpy.Event(env)
        trigger._ok, trigger._value = True, None
        trigger.callbacks.append(lambda _,event=event: apply(event))
        env.schedule(trigger, URGENT, event['minute'])

    def passenger(identifier,flight,connection=None,inbound=None):
        p = {'id':identifier,'flightId':flight,'connectionId':connection,'inboundFlightId':inbound,
             'arrivalMinute':None,'gateId':None,'gateArrivalMinute':None,'boardedMinute':None,
             'stage':'not_arrived','connectionStatus':'pending' if connection else None,'visits':[],
             'history':[{'minute':0,'stage':'not_arrived','gateId':None}]}
        records.append(p)
        return p

    def set_stage(p,name):
        p['stage']=name
        p['history'].append({'minute':env.now,'stage':name,'gateId':p['gateId']})

    def visit(p,stage,resource,arrival):
        v = {'stage':stage,'resourceId':resource,'arrivalMinute':arrival,'startedMinute':None,'finishedMinute':None}
        p['visits'].append(v)
        return v

    def observed(p,stage,resource,arrival,start,finish):
        yield env.timeout(max(0,arrival-env.now))
        set_stage(p,stage)
        v = visit(p,stage,resource,env.now)
        if start is None:
            yield env.event()
        yield env.timeout(max(0,start-env.now))
        v['startedMinute'] = env.now
        if finish is None:
            yield env.event()
        yield env.timeout(max(0,finish-env.now))
        v['finishedMinute'] = env.now

    def walk_minutes(p,checkpoint,previous_gate,gate,travel):
        if nav:
            source = gate_zones.get(previous_gate) if previous_gate else checkpoint_zones.get(checkpoint)
            target = gate_zones.get(gate)
            if source is None or target is None: return None
            key = frozenset(closed)
            if key not in graph_cache:
                graph_cache[key] = NavigationGraph(nav | {'closedZoneIds':sorted(closed)})
            kind = samples.get(p['id'],{}).get('groupKind','solo')
            graph = graph_cache[key]
            route = graph.route(source,target,kind)
            if route['error']: return None
            sampled = samples.get(p['id'])
            base = profiles[p['flightId']].travel_minutes(sampled['routeId']) if sampled else None
            return route['baseWalkingMinutes'] * (travel/base if base else 1) * graph.profiles[kind]['walkingMultiplier']
        if previous_gate:
            return j['gateChangeWalkingMinutes'] if previous_gate != gate else 0
        base = routes.get((checkpoint,base_gates[p['flightId']]))
        distance = routes.get((checkpoint,gate))
        if distance is None: return None
        return distance * travel/base if base else distance

    def downstream(p,checkpoint,travel,passport,transfer=False):
        if passport:
            set_stage(p,'passport')
            v = visit(p,'passport',str(request.scenario_id),env.now)
            if passports is None: yield env.event()
            with passports.request() as slot:
                yield slot
                v['startedMinute']=env.now
                yield env.timeout(j['passportServiceMinutes'])
                v['finishedMinute']=env.now
        previous_gate = None
        first_walk = True
        while True:
            f = plan[p['flightId']]
            gate = f['gateId']
            set_stage(p,'walking')
            # Airside transfer time is explicit; subsequent changes use gate-to-gate walking.
            duration = travel if transfer and first_walk else walk_minutes(p,checkpoint,previous_gate,gate,travel)
            if duration is None:
                set_stage(p,'unreachable')
                yield changed
                continue
            yield env.timeout(duration)
            first_walk=False
            previous_gate=gate
            if plan[p['flightId']]['gateId'] != gate: continue
            p['gateId'],p['gateArrivalMinute'] = gate,env.now
            set_stage(p,'gate_queue')
            while plan[p['flightId']]['gateId']==gate:
                f=plan[p['flightId']]
                if env.now>=f['boardingCloseMinute']:
                    set_stage(p,'missed')
                    return
                if env.now<f['boardingOpenMinute']:
                    yield env.timeout(f['boardingOpenMinute']-env.now) | changed
                    continue
                resource=gates[gate]
                if resource is None:
                    yield env.timeout(f['boardingCloseMinute']-env.now) | changed
                    continue
                signal=changed
                with resource.request() as slot:
                    yield slot | signal | env.timeout(f['boardingCloseMinute']-env.now)
                    f=plan[p['flightId']]
                    if f['gateId']!=gate: break
                    if not slot.triggered: continue
                    if env.now+j['boardingServiceMinutes']>f['boardingCloseMinute']:
                        # No partial boarding: release the resource and wait for closure or a delay.
                        resource.release(slot)
                        yield env.timeout(max(0,f['boardingCloseMinute']-env.now)) | changed
                        continue
                    set_stage(p,'boarding')
                    v=visit(p,'boarding',gate,p['gateArrivalMinute'])
                    v['startedMinute']=env.now
                    yield env.timeout(j['boardingServiceMinutes'])
                    v['finishedMinute']=p['boardedMinute']=env.now
                    set_stage(p,'boarded')
                    return
            # A waiting passenger must physically walk to the new gate; old snapshots stay intact.

    def origin(p,security,desk):
        arrival=desk['terminalArrivalMinute'] if desk else security['arrivalMinute']
        yield env.timeout(arrival)
        p['arrivalMinute']=env.now
        if desk:
            yield env.process(observed(p,'check_in',upstream['checkInResult']['zoneId'],arrival,desk['startedMinute'],desk['finishedMinute']))
            set_stage(p,'walking')
            if desk['securityArrivalMinute'] is None: yield env.event()
            yield env.timeout(max(0,desk['securityArrivalMinute']-env.now))
        if security is None: yield env.event()
        yield env.process(observed(p,'security',security['checkpointId'],security['arrivalMinute'],
                                   security['processingStartedMinute'],security['securityClearedMinute']))
        yield env.process(downstream(p,security['checkpointId'],security['walkingMinutes'],flights[p['flightId']]['requiresPassport']))

    def connection(p,c):
        while env.now<plan[c['inboundFlightId']]['arrivalMinute']:
            yield env.timeout(plan[c['inboundFlightId']]['arrivalMinute']-env.now) | changed
        p['arrivalMinute']=env.now
        yield env.process(downstream(p,None,c['transferMinutes'],c['requiresPassport'] or flights[p['flightId']]['requiresPassport'],True))

    desks={p['id']:p for p in upstream.get('checkInResult',{}).get('passengers',[])}
    security={p['id']:p for p in upstream['processResult']['passengers']}
    for identifier in sorted(security.keys() | desks.keys()):
        s,desk=security.get(identifier),desks.get(identifier)
        if s: flight=s['flightId']
        else:
            flight=next(f for f in flights if identifier.startswith('demo-pax-'+f.replace('-','')+'-')) if identifier.startswith('demo-pax-') else next(
                str(e.flight_id) for e in request.events or [] if identifier.startswith('demo-surge-'+e.event_id.hex+'-'))
        p=passenger(identifier,flight)
        env.process(origin(p,s,desk))
    for c in sorted(j['connections'],key=lambda c:c['id']):
        for i in range(c['passengerCount']):
            p=passenger(f"demo-transfer-{c['id'].replace('-','')}-{i+1:06d}",c['outboundFlightId'],c['id'],c['inboundFlightId'])
            metadata[p['id']]=c
            env.process(connection(p,c))

    def risk(p):
        if p['connectionId'] is None: return None
        if p['boardedMinute'] is not None: return 'boarded'
        close=plan[p['flightId']]['boardingCloseMinute']
        if close<=env.now: return 'missed'
        c=metadata[p['id']]
        remaining=j['boardingServiceMinutes']
        if p['stage'] in ('not_arrived','passport','walking','unreachable'):
            remaining+=c['transferMinutes']
        if p['stage']=='not_arrived' and (c['requiresPassport'] or flights[p['flightId']]['requiresPassport']):
            remaining+=j['passportServiceMinutes']
        ready=max(env.now,plan[c['inboundFlightId']]['arrivalMinute'])+remaining
        return 'at_risk' if ready+j['riskBufferMinutes']>=close else 'pending'

    grid=sample_minutes(horizon,request.snapshot_interval_minutes)
    progress_step=max(1,(horizon+59)//60)
    for minute in sorted(set(grid)|set(range(progress_step,horizon,progress_step))|{horizon}):
        env.timeout(minute-env.now)
        while env.peek()<=minute: env.step()
        for p in records: p['connectionStatus']=risk(p)
        if minute in grid:
            snapshots.append({'minute':minute,'notArrivedCount':sum(p['arrivalMinute'] is None for p in records),
                'passportQueuedCount':len(passports.queue) if passports else sum(p['stage']=='passport' for p in records),
                'passportBusyCount':passports.count if passports else 0,
                'gateQueuedCount':sum(p['stage']=='gate_queue' for p in records),
                'boardingCount':sum(p['stage']=='boarding' for p in records),
                'boardedCount':sum(p['boardedMinute'] is not None for p in records),
                'missedConnectionCount':sum(p['connectionStatus']=='missed' for p in records),
                'atRiskConnectionCount':sum(p['connectionStatus']=='at_risk' for p in records),
                'delayedFlightCount':sum(f['delayMinutes']>0 and not f['isCancelled'] for f in plan.values()),
                'totalDelayMinutes':sum(f['delayMinutes'] for f in plan.values() if not f['isCancelled'])})
        if on_progress and (minute%progress_step==0 or minute==horizon):
            rows=[r for r in upstream['kpiResult']['snapshots'] if r['minute']<=minute]
            last={r['zoneId']:r for r in rows}
            on_progress({'simulationMinute':minute,'zones':[{'zoneId':r['zoneId'],'queuedPassengers':r['queuedPassengers'],
                'processedPassengers':r['processedPassengers']} for r in last.values()]})
    result={'journeyVersion':'1.0','horizonMinutes':horizon,'populationCount':len(records),
        'connectionPassengerCount':len(metadata),'boardedCount':snapshots[-1]['boardedCount'],
        'missedConnectionCount':snapshots[-1]['missedConnectionCount'],'plans':plans,'snapshots':snapshots,
        'passengers':records,'upstreamResult':upstream}
    return SimulationResponse.model_validate({'contractVersion':'1.0','runId':str(request.run_id),'status':'completed',
        'simulatedMinutes':horizon,'metrics':upstream['metrics'],'snapshots':upstream['snapshots'],'failure':None,'journeyResult':result})
