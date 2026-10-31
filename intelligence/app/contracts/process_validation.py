"""Process invariants mirrored by ProcessContractValidation.cs."""
def validate_result(value, path, errors, error):
    def invalid(field=''):
        errors.append(error(path+field,'invalid_result'))
    if (value['arrivedCount']+value['notArrivedCount'] != value['populationCount']
        or sum(value[key] for key in ('queuedCount','processingCount','walkingCount','atGateCount','unreachableCount')) != value['arrivedCount']
        or value['returnedCount'] != len(value['passengers'])
        or value['returnedCount'] > max(0,value['arrivedCount']-value['offset'])
        or value['hasMore'] != (value['offset']+value['returnedCount']<value['arrivedCount'])
        or value['deadlineMissedCount'] > value['arrivedCount']): invalid()
    if len({cp['checkpointId'] for cp in value['checkpoints']}) != len(value['checkpoints']): invalid('/checkpoints')
    if len({person['id'] for person in value['passengers']}) != len(value['passengers']): invalid('/passengers')
    for index,cp in enumerate(value['checkpoints']):
        if (cp['peakBusy'] > cp['capacity'] or cp['processingCount'] > cp['peakBusy'] or cp['peakQueue'] < cp['queuedCount']
            or cp['queuedCount']+cp['processingCount']+cp['processedCount'] != cp['arrivedCount']): invalid(f'/checkpoints/{index}')
    for index,person in enumerate(value['passengers']):
        prefix=f'/passengers/{index}'
        start,clear,gate = (person[key] for key in ('processingStartedMinute','securityClearedMinute','gateArrivalMinute'))
        stage=person['stage']; horizon=value['horizonMinutes']
        present=lambda actual,wanted: (actual is not None)==wanted
        if (person['arrivalMinute']>horizon or not present(person['queueEnteredMinute'],stage!='unreachable')
            or not present(start,stage in ('processing','walking','at_gate'))
            or not present(clear,stage in ('walking','at_gate')) or not present(gate,stage=='at_gate')
            or not present(person['queueWaitMinutes'],start is not None)
            or not present(person['errorCode'],stage=='unreachable')): invalid(prefix); continue
        if person['queueEnteredMinute'] is not None and person['queueEnteredMinute']!=person['arrivalMinute']: invalid(prefix)
        if start is not None and (start<person['arrivalMinute'] or start>horizon
            or abs(start-person['arrivalMinute']-person['queueWaitMinutes'])>1e-8): invalid(prefix)
        if clear is not None and (clear>horizon or abs(clear-start-person['serviceMinutes'])>1e-8): invalid(prefix)
        # A corridor closure can hold a passenger at the control exit before walking starts.
        hold=person.get('routeHoldMinutes',0); awaiting=person.get('awaitingRoute',False)
        if gate is not None and (gate>horizon or abs(gate-clear-hold-person['walkingMinutes'])>1e-8): invalid(prefix)
        if stage=='processing' and start+person['serviceMinutes']<=horizon: invalid(prefix)
        if awaiting:
            if stage!='walking' or person['walkingMinutes']!=0 or abs(clear+hold-horizon)>1e-8: invalid(prefix)
        elif stage=='walking' and clear+hold+person['walkingMinutes']<=horizon: invalid(prefix)
        if 'routeHoldMinutes' in person and clear is None: invalid(prefix)
        if person.get('assignedCheckpointId')==person['checkpointId']: invalid(prefix)
        close=person['boardingCloseMinute']
        missed=close is not None and (gate>close if gate is not None else horizon>=close)
        risk=('unreachable' if stage=='unreachable' else 'not_applicable' if close is None else
              ('late' if missed else 'safe') if gate is not None else ('missed' if missed else 'pending'))
        if person['deadlineMissed']!=missed or person['boardingRisk']!=risk: invalid(prefix)
    if value['offset']==0 and value['returnedCount']==value['arrivedCount']:
        people=value['passengers']
        for key,stage in [('queuedCount','queued'),('processingCount','processing'),('walkingCount','walking'),('atGateCount','at_gate'),('unreachableCount','unreachable')]:
            if value[key]!=sum(person['stage']==stage for person in people): invalid('/'+key)
        if value['deadlineMissedCount']!=sum(person['deadlineMissed'] for person in people): invalid('/deadlineMissedCount')
        known={cp['checkpointId'] for cp in value['checkpoints']}
        if any(person['checkpointId'] not in known for person in people): invalid('/passengers')
        groups={key:[] for key in known}
        for person in people:
            if person['checkpointId'] in groups and person['stage']!='unreachable': groups[person['checkpointId']].append(person)
        for index,cp in enumerate(value['checkpoints']):
            members=groups[cp['checkpointId']]
            if (cp['arrivedCount']!=len(members) or cp['queuedCount']!=sum(person['stage']=='queued' for person in members)
                or cp['processingCount']!=sum(person['stage']=='processing' for person in members)
                or cp['processedCount']!=sum(person['stage'] in ('walking','at_gate') for person in members)): invalid(f'/checkpoints/{index}')
