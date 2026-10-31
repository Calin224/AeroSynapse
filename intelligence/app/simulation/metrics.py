"""Observed KPI populations; no unfinished service enters the wait percentiles."""
import math
from datetime import timedelta

import numpy as np

ENGINE='simpy-kpi-1.0'
MAX_SNAPSHOTS=50000
DEFINITIONS={'waitPopulation':'service_completed_by_minute','percentileMethod':'nearest_rank',
    'throughputPopulation':'service_completed_in_window','queuePopulation':'waiting_for_resource',
    'utilizationPopulation':'observed_busy_slots_including_unfinished_service',
    'boardingPopulation':'modeled_passengers_with_boarding_deadline','reconciliationPopulation':'configured_modeled_demand'}


def sample_minutes(horizon,interval):
    return [*range(0,horizon,interval),horizon]


def wait_statistics(values):
    if not values: return None,None,None
    count=len(values); middle=count//2; rank=math.ceil(.95*count)-1
    indices=sorted({middle,(count-1)//2,rank})
    ordered=np.partition(np.asarray(values,dtype=float),indices)
    median=(ordered[middle]+ordered[(count-1)//2])/2
    return tuple(round(float(value),12) for value in (math.fsum(values)/count,median,ordered[rank]))


class KpiCollector:
    def __init__(self,request,stations,people,absent=()):
        self.request=request; self.snapshots=[]; self.last={}; self.wait_cache={}
        if request.passenger_generation is not None:
            populations={key:0 for key in stations}
            for person in people:
                populations[person['checkpointId']]+=1
                if person['boardingCloseMinute'] is not None: stations[person['checkpointId']]['riskPopulation']+=1
            # Still at check-in at the horizon: demand of their control that never arrives, and boarding risk.
            for person in absent:
                populations[person['checkpointId']]+=1
                if person['boardingCloseMinute'] is not None: stations[person['checkpointId']]['riskPopulation']+=1
        else:
            count=len(stations)
            populations={key:request.passenger_count//count+(index<request.passenger_count%count) for index,key in enumerate(stations)}
        self.populations=populations

    def add(self,checkpoint):
        """Surge demand joins its control's population when the event fires, never earlier."""
        self.populations[checkpoint]+=1

    def move(self,source,target):
        """A redirected arrival belongs to its assigned control until it arrives, then to the serving one."""
        self.populations[source]-=1; self.populations[target]+=1

    def sample(self,minute,stations,capacities):
        for key,station in stations.items():
            # Integrate actual occupied slots, including service still in progress at this minute.
            station['busyArea']+=station['processingCount']*(minute-station['busyLastMinute'])
            station['busyLastMinute']=minute
            previous=self.last.get(key,(0,0,0.0))
            start,finished_before,busy_before=previous
            width=minute-start
            completed=station['processedCount']; capacity=station['capacity']
            cached=self.wait_cache.get(key)
            if cached is None or cached[0]!=completed:
                cached=(completed,wait_statistics(station['completedWaits'])); self.wait_cache[key]=cached
            mean,median,p95=cached[1]
            busy=station['busyArea']; window_busy=max(0,busy-busy_before)
            arrived=station['arrivedCount']+station['unreachableCount']
            risk_population=station['riskPopulation']; at_risk=station['atRiskCount']; late=station['lateGateCount']
            ratio=lambda numerator,denominator: round(min(1,max(0,numerator/denominator)),12) if denominator else None
            self.snapshots.append({'kpiVersion':'1.0','runId':str(self.request.run_id),'zoneId':key,'zoneType':'checkpoint',
                'minute':minute,'elapsedMinutes':minute,'windowStartMinute':start,
                'recordedAtUtc':(self.request.starts_at_utc+timedelta(minutes=minute)).isoformat().replace('+00:00','Z'),
                'populationCount':self.populations[key],'arrivedPassengers':arrived,'notArrivedPassengers':self.populations[key]-arrived,
                'queuedPassengers':station['queuedCount'],'processingPassengers':station['processingCount'],'processedPassengers':completed,
                'walkingPassengers':station['walkingCount'],'atGatePassengers':station['atGateCount'],'unreachablePassengers':station['unreachableCount'],
                'waitSampleCount':completed,'meanWaitMinutes':mean,'medianWaitMinutes':median,'p95WaitMinutes':p95,
                'completedInWindow':completed-finished_before,
                'throughputPerMinute':round((completed-finished_before)/width,12) if width else None,
                'cumulativeThroughputPerMinute':round(completed/minute,12) if minute else None,
                'maximumQueue':station['peakQueue'],'firstQueueMinute':station['firstQueueMinute'],
                'capacity':capacity,'peakBusy':station['peakBusy'],'busyMinutes':round(busy,12),'busyMinutesInWindow':round(window_busy,12),
                'utilization':ratio(window_busy,capacity*width),'cumulativeUtilization':ratio(busy,capacity*minute),
                'boardingRiskPopulationCount':risk_population,'boardingAtRiskCount':at_risk,'boardingLateRisk':ratio(at_risk,risk_population),
                'lateAtGateCount':late,'missedDeadlineUnfinishedCount':at_risk-late})
            # Capacity and utilization stay nominal; the staffing in effect is reported beside them.
            if capacities is not None: self.snapshots[-1]['effectiveCapacity']=capacities[key]
            self.last[key]=(minute,completed,busy)

    def result(self,process_result):
        count=len(self.snapshots); processed=sum(cp['processedCount'] for cp in process_result['checkpoints'])
        return {'kpiVersion':'1.0','engine':ENGINE,'runId':str(self.request.run_id),'intervalMinutes':self.request.snapshot_interval_minutes,
            'horizonMinutes':self.request.duration_minutes,'populationCount':process_result['populationCount'],
            'processedCount':processed,'unprocessedCount':process_result['populationCount']-processed,
            'arrivedCount':process_result['arrivedCount'],'notArrivedCount':process_result['notArrivedCount'],
            'unreachableCount':process_result['unreachableCount'],'snapshotCount':count,'matchedSnapshotCount':count,
            'offset':0,'returnedSnapshotCount':count,'hasMore':False,'definitions':DEFINITIONS.copy(),'snapshots':self.snapshots,
            **({'eventCount':len(process_result['events'])} if 'events' in process_result else {})}
