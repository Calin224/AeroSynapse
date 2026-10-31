"""Graph semantics, mirrored in NavigationContractValidation.cs."""

def validate_navigation(value, path, errors, error):
    def add(field, code='invalid_value'):
        errors.append(error(path + field, code))
    def unique(items, key, field):
        seen = set()
        for index, item in enumerate(items):
            identifier = item[key] if key else item
            if identifier in seen:
                add(f'{field}/{index}' + ('/' + key if key else ''), 'duplicate_id')
            seen.add(identifier)
    nodes, edges = value['graph']['nodes'], value['graph']['edges']
    unique(nodes, 'id', '/graph/nodes'); unique(edges, 'id', '/graph/edges')
    node_ids, edge_ids = {item['id'] for item in nodes}, {item['id'] for item in edges}
    node_kinds = {}
    for item in nodes:
        node_kinds.setdefault(item['id'], item['kind'])
    for index, edge in enumerate(edges):
        for name in ('fromZoneId', 'toZoneId'):
            if edge[name] not in node_ids:
                add(f'/graph/edges/{index}/{name}')
        if edge['fromZoneId'] == edge['toZoneId']:
            add(f'/graph/edges/{index}/toZoneId')
        unique(edge['requiredTags'], None, f'/graph/edges/{index}/requiredTags')
    for index, node in enumerate(nodes):
        unique(node['requiredTags'], None, f'/graph/nodes/{index}/requiredTags')
    for name in ('checkpointBindings', 'gateBindings'):
        unique(value[name], 'resourceId', '/' + name)
        for index, binding in enumerate(value[name]):
            if binding['zoneId'] not in node_ids:
                add(f'/{name}/{index}/zoneId')
            elif name == 'gateBindings' and node_kinds[binding['zoneId']] != 'Gate':
                add(f'/{name}/{index}/zoneId')
    for name, known in [('closedZoneIds', node_ids), ('closedEdgeIds', edge_ids)]:
        unique(value[name], None, '/' + name)
        for index, identifier in enumerate(value[name]):
            if identifier not in known:
                add(f'/{name}/{index}')
    unique(value['accessProfiles'], 'groupKind', '/accessProfiles')
    for index, profile in enumerate(value['accessProfiles']):
        unique(profile['accessTags'], None, f'/accessProfiles/{index}/accessTags')
        unique(profile['forbiddenZoneIds'], None, f'/accessProfiles/{index}/forbiddenZoneIds')
        for zone_index, identifier in enumerate(profile['forbiddenZoneIds']):
            if identifier not in node_ids:
                add(f'/accessProfiles/{index}/forbiddenZoneIds/{zone_index}')
    if 'flightGates' in value:
        unique(value['flightGates'], 'flightId', '/flightGates')


def validate_navigation_envelope(value, path, errors, error):
    from app.contracts.passenger_validation import validate_passengers
    validate_passengers(value['passengerGeneration'], path + '/passengerGeneration', errors, error)
    validate_navigation(value['navigation'], path + '/navigation', errors, error)
    if {item['flightId'] for item in value['navigation']['flightGates']} != {item['flightId'] for item in value['passengerGeneration']['flights']}:
        errors.append(error(path + '/navigation/flightGates', 'comparison_mismatch'))
