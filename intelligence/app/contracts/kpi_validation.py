"""KPI structural invariants mirrored in KpiContractValidation.cs."""
from decimal import Decimal

def near(actual,expected):
    if actual is None or expected is None: return actual is None and expected is None
    actual,expected=float(actual),float(expected)
    return abs(actual-expected)<=1e-8+1e-12*max(abs(actual),abs(expected))


def above(actual,maximum):
    return actual>maximum and not near(actual,maximum)


def validate_snapshot(row,path,errors,error):
    # The strict HTTP parser uses Decimal; tolerances and ratios use the same finite double domain as .NET.
    row={key:float(value) if isinstance(value,Decimal) else value for key,value in row.items()}
    def invalid(): errors.append(error(path,'invalid_result'))
    from app.contracts.validation import utc
    try: utc(row['recordedAtUtc'])
    except ValueError: errors.append(error(path+'/recordedAtUtc','invalid_format'))
    minute,start=row['minute'],row['windowStartMinute']; width=minute-start
    if (row['elapsedMinutes']!=minute or start>minute
        or row['arrivedPassengers']+row['notArrivedPassengers']!=row['populationCount']
        or sum(row[key] for key in ('queuedPassengers','processingPassengers','walkingPassengers','atGatePassengers','unreachablePassengers'))!=row['arrivedPassengers']
        or row['processedPassengers']!=row['walkingPassengers']+row['atGatePassengers']
        or row['waitSampleCount']!=row['processedPassengers'] or row['completedInWindow']>row['processedPassengers']
        or row['processingPassengers']>row['capacity'] or row['peakBusy']>row['capacity'] or row['processingPassengers']>row['peakBusy']
        or row['maximumQueue']<row['queuedPassengers'] or row['maximumQueue']>row['arrivedPassengers']
        or (row['firstQueueMinute'] is None)!=(row['maximumQueue']==0)
        or row['firstQueueMinute'] is not None and row['firstQueueMinute']>minute
        or above(row['busyMinutesInWindow'],row['busyMinutes'])
        or above(row['busyMinutes'],row['capacity']*minute) or above(row['busyMinutesInWindow'],row['capacity']*width)
        or row['boardingAtRiskCount']>row['boardingRiskPopulationCount'] or row['boardingRiskPopulationCount']>row['populationCount']
        or row['lateAtGateCount']+row['missedDeadlineUnfinishedCount']!=row['boardingAtRiskCount']
        or row.get('effectiveCapacity',0)>row['capacity']): invalid()
    for key in ('meanWaitMinutes','medianWaitMinutes','p95WaitMinutes'):
        if (row[key] is None)!=(row['waitSampleCount']==0): invalid()
    if row['medianWaitMinutes'] is not None and row['p95WaitMinutes']<row['medianWaitMinutes']: invalid()
    for key,expected in [('throughputPerMinute',row['completedInWindow']/width if width else None),
        ('cumulativeThroughputPerMinute',row['processedPassengers']/minute if minute else None),
        ('utilization',row['busyMinutesInWindow']/(row['capacity']*width) if row['capacity'] and width else None),
        ('cumulativeUtilization',row['busyMinutes']/(row['capacity']*minute) if row['capacity'] and minute else None),
        ('boardingLateRisk',row['boardingAtRiskCount']/row['boardingRiskPopulationCount'] if row['boardingRiskPopulationCount'] else None)]:
        if not near(row[key],expected): invalid()


def validate_kpis(value,path,errors,error):
    def invalid(field=''): errors.append(error(path+field,'invalid_result'))
    if (value['processedCount']+value['unprocessedCount']!=value['populationCount']
        or value['arrivedCount']+value['notArrivedCount']!=value['populationCount']
        or value['processedCount']+value['unreachableCount']>value['arrivedCount']
        or value['returnedSnapshotCount']!=len(value['snapshots'])
        or value['matchedSnapshotCount']>value['snapshotCount']
        or value['returnedSnapshotCount']>max(0,value['matchedSnapshotCount']-value['offset'])
        or value['hasMore']!=(value['offset']+value['returnedSnapshotCount']<value['matchedSnapshotCount'])): invalid()
    seen=set(); groups={}
    for index,row in enumerate(value['snapshots']):
        validate_snapshot(row,f'{path}/snapshots/{index}',errors,error)
        key=(row['zoneId'],row['minute'])
        if key in seen or row['runId']!=value['runId'] or row['minute']>value['horizonMinutes']: invalid(f'/snapshots/{index}')
        seen.add(key); groups.setdefault(row['zoneId'],[]).append(row)
    if value['offset']!=0 or value['returnedSnapshotCount']!=value['snapshotCount'] or value['matchedSnapshotCount']!=value['snapshotCount']: return
    horizon=int(value['horizonMinutes']); interval=int(value['intervalMinutes'])
    grid=[*range(0,horizon,interval),horizon]; final=[]
    for rows in groups.values():
        rows.sort(key=lambda row:row['minute'])
        if [row['minute'] for row in rows]!=grid: invalid('/snapshots'); continue
        previous=None
        for row in rows:
            start=previous['minute'] if previous else 0
            if (row['windowStartMinute']!=start or row['completedInWindow']!=row['processedPassengers']-(previous['processedPassengers'] if previous else 0)
                or not near(row['busyMinutesInWindow'],row['busyMinutes']-(previous['busyMinutes'] if previous else 0))
                or previous and (row['processedPassengers']<previous['processedPassengers'] or row['maximumQueue']<previous['maximumQueue']
                    or row['capacity']!=previous['capacity']
                    # Without events demand is fixed; with them a zone population moves only at event minutes and arrivals.
                    or row['populationCount']!=previous['populationCount'] and 'eventCount' not in value
                    or ('effectiveCapacity' in row)!=('eventCount' in value))): invalid('/snapshots')
            previous=row
        final.append(rows[-1])
    for root,field in [('populationCount','populationCount'),('processedCount','processedPassengers'),('arrivedCount','arrivedPassengers'),
        ('notArrivedCount','notArrivedPassengers'),('unreachableCount','unreachablePassengers')]:
        if sum(row[field] for row in final)!=value[root]: invalid('/'+root)


def validate_check_in_request(value,path,errors,error):
    """Check-in reshapes generated arrivals, so it needs flight profiles and the single-control route."""
    if 'checkIn' not in value: return
    generation=value.get('passengerGeneration')
    if generation is None: errors.append(error(path+'/passengerGeneration','required'))
    elif generation.get('flow') is not None: errors.append(error(path+'/checkIn','invalid_value'))
    if value['checkIn']['zoneId'] in {cp['checkpointId'] for cp in value['checkpoints']}:
        errors.append(error(path+'/checkIn/zoneId','duplicate_id'))
    steps=value['checkIn'].get('deskSteps')
    if steps is not None:
        # Desks only open during a run: a nondecreasing step function from minute 0 ending at staffCount.
        minutes=[step['startMinute'] for step in steps]; counts=[step['staffCount'] for step in steps]
        if (minutes[0]!=0 or any(b<=a for a,b in zip(minutes,minutes[1:])) or minutes[-1]>=int(value['durationMinutes'])
            or any(b<a for a,b in zip(counts,counts[1:])) or counts[-1]!=value['checkIn']['staffCount']):
            errors.append(error(path+'/checkIn/deskSteps','invalid_value'))


def validate_staff_schedules(value,path,errors,error):
    """Each schedule is a step function from minute 0 inside the horizon at a known checkpoint, and that
    checkpoint's staffCount is its maximum. `events` must be present so effective capacity is reported."""
    if 'staffSchedules' not in value: return
    if 'events' not in value: errors.append(error(path+'/events','required'))
    staff={cp['checkpointId']:cp['staffCount'] for cp in value['checkpoints']}
    unique_ids=set()
    for index,schedule in enumerate(value['staffSchedules']):
        field=f'{path}/staffSchedules/{index}'
        key=schedule['checkpointId']
        if key in unique_ids: errors.append(error(field+'/checkpointId','duplicate_id'))
        unique_ids.add(key)
        if key not in staff:
            errors.append(error(field+'/checkpointId','invalid_value')); continue
        minutes=[step['startMinute'] for step in schedule['steps']]
        if minutes[0]!=0 or any(b<=a for a,b in zip(minutes,minutes[1:])) or minutes[-1]>=int(value['durationMinutes']):
            errors.append(error(field+'/steps','invalid_value'))
        if staff[key]!=max(step['staffCount'] for step in schedule['steps']):
            errors.append(error(field+'/steps','comparison_mismatch'))


def validate_check_in_result(value,path,errors,error):
    """Check-in result invariants mirrored in KpiContractValidation.cs."""
    def invalid(field=''): errors.append(error(path+field,'invalid_result'))
    people=value['passengers']
    if (value['populationCount']!=len(people) or len({person['id'] for person in people})!=len(people)
        or value['processedCount']!=sum(person['finishedMinute'] is not None for person in people)
        or value['unfinishedCount']!=sum(person['securityArrivalMinute'] is None for person in people)): invalid()
    for index,person in enumerate(people):
        start,finish,joins=(person[key] for key in ('startedMinute','finishedMinute','securityArrivalMinute'))
        if (start is None and finish is not None or finish is None and joins is not None
            or start is not None and start<person['terminalArrivalMinute']
            or finish is not None and not near(finish,start+person['serviceMinutes'])
            or joins is not None and joins<finish): invalid(f'/passengers/{index}')
    previous=None
    for index,row in enumerate(value['snapshots']):
        if (row['zoneId']!=value['zoneId'] or row['capacity']>value['capacity']
            or row['queuedPassengers']+row['processingPassengers']+row['processedPassengers']!=row['arrivedPassengers']
            or row['walkingPassengers']>row['processedPassengers'] or row['processingPassengers']>row['capacity']
            or row['maximumQueue']<row['queuedPassengers'] or row['maximumQueue']>row['arrivedPassengers']
            or (row['utilization'] is None)!=(row['capacity']==0 or row['minute']==0)
            or above(row['busyMinutes'],row['capacity']*row['minute'])
            or any((row[key] is None)!=(row['processedPassengers']==0) for key in ('meanWaitMinutes','medianWaitMinutes','p95WaitMinutes'))
            or previous and (row['minute']<=previous['minute'] or row['processedPassengers']<previous['processedPassengers']
                             or row['maximumQueue']<previous['maximumQueue'])): invalid(f'/snapshots/{index}')
        previous=row
