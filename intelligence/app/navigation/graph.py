"""Time-weighted Dijkstra; graph coordinates only describe exported operational waypoints."""
import hashlib
import heapq
import json


def canonical(value):
    if isinstance(value, float) and value.is_integer():
        return int(value)
    if isinstance(value, dict):
        return {key: canonical(child) for key, child in sorted(value.items())}
    if isinstance(value, list):
        children = [canonical(child) for child in value]
        return sorted(children, key=lambda child: json.dumps(child, sort_keys=True, separators=(',', ':')))
    return value


def fingerprint(value):
    return hashlib.sha256(json.dumps(canonical(value), sort_keys=True, separators=(',', ':'), allow_nan=False).encode()).hexdigest()


class NavigationGraph:
    def __init__(self, navigation):
        self.nodes = {node['id']: node for node in navigation['graph']['nodes']}
        self.closed_nodes = set(navigation['closedZoneIds'])
        self.closed_edges = set(navigation['closedEdgeIds'])
        self.profiles = {profile['groupKind']: profile for profile in navigation['accessProfiles']}
        self.adjacency = {identifier: [] for identifier in self.nodes}
        for edge in navigation['graph']['edges']:
            self.adjacency[edge['fromZoneId']].append((edge['toZoneId'], edge))
            if edge['isBidirectional']:
                self.adjacency[edge['toZoneId']].append((edge['fromZoneId'], edge))
        for edges in self.adjacency.values():
            edges.sort(key=lambda pair: (pair[1]['id'], pair[0]))
        self.cache = {}

    def route(self, source, target, kind):
        key = (source, target, kind)
        if key not in self.cache:
            self.cache[key] = self.search(source, target, kind)
        return self.cache[key]

    def search(self, source, target, kind):
        def failure(code, message):
            return {'error': {'code': code, 'message': message}}
        profile = self.profiles.get(kind)
        if not profile:
            return failure('access_profile_missing', f'No access profile is configured for {kind}.')
        tags, forbidden = set(profile['accessTags']), set(profile['forbiddenZoneIds'])
        def allowed_node(identifier):
            node = self.nodes.get(identifier)
            return bool(node and node['isActive'] and identifier not in self.closed_nodes and identifier not in forbidden
                        and set(node['requiredTags']) <= tags
                        and (not profile['requireStepFree'] or node['isStepFree'] and node['kind'] != 'Stairs'))
        if not allowed_node(source) or not allowed_node(target):
            return failure('zone_unavailable', 'The origin or destination is closed or prohibited for this access profile.')
        # Positive edge times make cycles noncompetitive. Time, distance, hops and edge IDs break ties deterministically.
        best = {source: (0.0, 0.0, 0, ())}
        queue = [(best[source], source, (source,))]
        while queue:
            cost, node, nodes = heapq.heappop(queue)
            if best[node] != cost:
                continue
            if node == target:
                minutes = cost[0] / 60
                if minutes > 10080 or minutes * profile['walkingMultiplier'] > 10080:
                    return failure('route_time_limit_exceeded', 'The route exceeds the operational walking-time limit.')
                return {'zoneIds': list(nodes), 'edgeIds': list(cost[3]), 'distanceMeters': round(cost[1], 9),
                        'baseWalkingMinutes': minutes,
                        'waypoints': [{'zoneId': identifier, **{axis: self.nodes[identifier][axis] for axis in ('x', 'y', 'z')}} for identifier in nodes],
                        'error': None}
            for destination, edge in self.adjacency[node]:
                if (not allowed_node(destination) or not edge['isActive'] or edge['id'] in self.closed_edges
                        or not set(edge['requiredTags']) <= tags or profile['requireStepFree'] and not edge['isStepFree']):
                    continue
                next_cost = (cost[0] + float(edge['travelSeconds']), cost[1] + float(edge['distanceMeters']), cost[2] + 1, cost[3] + (edge['id'],))
                if destination not in best or next_cost < best[destination]:
                    best[destination] = next_cost
                    heapq.heappush(queue, (next_cost, destination, nodes + (destination,)))
        return failure('route_unreachable', 'No navigable path reaches the assigned gate under the captured closures and access filters.')
