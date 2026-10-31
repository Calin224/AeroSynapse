"""Readable operational interior, retaining the supplied shell for exterior.

Run on the previous adapted airport (before-US013-2026-10-13.blend), once.
No downloaded geometry, fonts or textures are introduced.
"""
import bpy, json, math, os, sys, uuid
from mathutils import Vector, Matrix

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '../..'))
OUT = os.path.join(ROOT, 'AeroSynapse.Client/public/models/aerosynapse')
sys.path.insert(0, os.path.dirname(__file__))
from annotation_glyphs import GLYPHS

scene = bpy.context.scene
assert not scene.get('operationalLayoutVersion'), 'Use the pre-US013 backup to regenerate.'
manifest_path = os.path.join(OUT, 'manifest.json')
manifest = json.load(open(manifest_path))
entities = {e['code']: e for e in manifest['entities']}
terminal = next(c for c in scene.collection.children if c.name.startswith('01'))
terminal.name = '01 · Terminal DEMO · interior operational'
scene.unit_settings.scale_length = 1
FLOOR = .45
NS = uuid.UUID(manifest['sceneId'])

def enum(owner, prop, wanted):
    properties=owner.properties if hasattr(owner,'properties') else owner.bl_rna.properties
    return next(i.identifier for i in properties[prop].enum_items if i.identifier == wanted)

def mat(name, rgb, emission=0):
    m = bpy.data.materials.new('US013 · ' + name)
    m.use_nodes = True
    m.diffuse_color = (*rgb, 1)
    p = next(n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    p.inputs['Base Color'].default_value = (*rgb, 1)
    p.inputs['Roughness'].default_value = .72
    p.inputs['Emission Color'].default_value = (*rgb, 1)
    p.inputs['Emission Strength'].default_value = emission
    m.use_backface_culling = False
    return m

white = mat('white wayfinding', (.92, .96, 1), .2)
dark = mat('navy signage', (.014, .031, .062))
floor_mat = mat('light floor', (.54, .62, .69))
wall_mat = mat('cutaway walls', (.75, .82, .88))
gold = mat('boarding amber', (.95, .48, .04), .08)
gate_floor = mat('boarding zone', (.27, .19, .09))
cyan = mat('security turquoise', (.015, .69, .72), .08)
security_floor = mat('security zone', (.045, .24, .28))
blue = mat('check-in blue', (.035, .29, .8))
checkin_floor = mat('check-in zone', (.09, .18, .34))
green = mat('entry green', (.035, .64, .39))
seat_mat = mat('waiting seats', (.1, .25, .39))
route_mat = mat('passenger path', (.25, .78, .57), .1)

def root(code, label, location=(0, 0, 0)):
    o = bpy.data.objects.new('AS|terminal|' + code, None)
    terminal.objects.link(o)
    o.location = location
    data = dict(entityId=str(uuid.uuid5(NS, 'aerosynapse-user-airport/' + code)),
                code=code, label=label, zoneId=manifest['terminalId'], zoneType='terminal',
                sceneId=manifest['sceneId'], terminalId=manifest['terminalId'],
                resource='terminal', synthetic=True, status='synthetic', clickable=True,
                baseHeight=location[2], sourceObjects=[])
    for k, v in data.items(): o[k] = v
    entities[code] = dict(data, databaseMapping=dict(table='terminals', id=manifest['terminalId']))
    return o

def mesh(parent, name, vertices, faces, material):
    data = bpy.data.meshes.new(name)
    data.from_pydata([tuple(Vector(v) - parent.location) for v in vertices], [], faces)
    data.materials.append(material)
    data.update()
    o = bpy.data.objects.new(parent.name + '/' + name, data)
    terminal.objects.link(o)
    o.parent = parent
    o['synthetic'] = True
    o['generatedFor'] = 'US-013'
    return o

def box(parent, name, center, size, material):
    x, y, z = center; a, b, c = [v / 2 for v in size]
    vertices = [(x-a,y-b,z-c),(x+a,y-b,z-c),(x+a,y+b,z-c),(x-a,y+b,z-c),
                (x-a,y-b,z+c),(x+a,y-b,z+c),(x+a,y+b,z+c),(x-a,y+b,z+c)]
    return mesh(parent, name, vertices, [(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)], material)

def rectangle(parent, name, bounds, material, z=FLOOR+.025):
    x0,y0,x1,y1=bounds
    return mesh(parent,name,[(x0,y0,z),(x1,y0,z),(x1,y1,z),(x0,y1,z)],[(0,1,2,3)],material)

def label(parent, text, center, height, material=white, upright=False):
    unit=height/7; width=(6*len(text)-1)*unit
    vertices=[]; faces=[]
    for j,ch in enumerate(text):
        for row,bits in enumerate(GLYPHS[ch]):
            for col,bit in enumerate(bits):
                if bit!='1': continue
                x=(j*6+col)*unit-width/2; y=(6-row)*unit-height/2
                corners=[(x,y),(x+unit*.88,y),(x+unit*.88,y+unit*.88),(x,y+unit*.88)]
                n=len(vertices)
                vertices.extend([(center[0]+a,center[1],center[2]+b) if upright
                                 else (center[0]+a,center[1]+b,center[2]) for a,b in corners])
                faces.append((n,n+1,n+2,n+3))
    obj=mesh(parent, ('sign ' if upright else 'floor label ')+text, vertices, faces, material)
    obj['annotationOnly']=True
    return obj

def sign(parent, text, center, height, material):
    width=(6*len(text)-1)*height/7+.8
    box(parent,'wayfinding board '+text,center,(width,.18,height+.6),dark)
    label(parent,text,(center[0],center[1]-.105,center[2]),height,material,True)

def bounds(obj):
    pts=[obj.matrix_world@Vector(v) for v in obj.bound_box]
    return Vector([min(v[i] for v in pts) for i in range(3)]), Vector([max(v[i] for v in pts) for i in range(3)])

def equipment(parent, location, scale=1, rotate=0):
    """Keep the source mesh, relocate its geometry and ground it on a common level."""
    children=[c for c in parent.children if c.type=='MESH' and c.get('sourceObject')]
    if not children: return
    bpy.context.view_layer.update()
    allpoints=[c.matrix_world@Vector(v) for c in children for v in c.bound_box]
    lo=Vector([min(v[i] for v in allpoints) for i in range(3)])
    hi=Vector([max(v[i] for v in allpoints) for i in range(3)])
    pivot=Vector(((lo.x+hi.x)/2,(lo.y+hi.y)/2,lo.z))
    targets=[]
    for child in children:
        transform=Matrix.Rotation(rotate,4,'Z') @ Matrix.Scale(scale,4) @ Matrix.Translation(-pivot) @ child.matrix_world
        data=child.data.copy(); data.transform(transform); data.update()
        targets.append((child,data))
    for child,data in targets:
        child.data=data; child.location=(0,0,0); child.rotation_euler=(0,0,0); child.scale=(1,1,1)
    parent.location=location

# The complex original shell remains exact and is exported, with an exterior-only flag.
for code in ['T-DEMO','ROOF','INTERIOR-FIXTURES','BAGGAGE-CLAIM']:
    obj=next(o for o in scene.objects if o.get('code')==code)
    obj['viewMode']='exterior'
    obj.hide_render=True
    obj.hide_set(True)
    for child in obj.children: child.hide_render=True; child.hide_set(True)
    if code in entities: entities[code]['viewMode']='exterior'

# Remove the tiny existing annotations, keeping the identifiable source equipment.
for code in ['ENTRY','CORRIDOR','CHECK-IN']+[f'CP-{n}' for n in range(1,4)]+[f'G-{n}' for n in range(1,6)]:
    obj=next(o for o in scene.objects if o.get('code')==code)
    for child in list(obj.children):
        if not child.get('sourceObject'): bpy.data.objects.remove(child,do_unlink=True)

layout=root('T-LAYOUT','Terminal demonstrativ · plan operational')
layout['viewMode']='interior'
# Plan follows the main hall and west wing of the supplied T-shaped terminal.
box(layout,'main hall floor',(3.25,38,FLOOR-.18),(41.5,70,.36),floor_mat)
box(layout,'boarding wing floor',(-52.5,37.5,FLOOR-.18),(70,33,.36),floor_mat)
for center,size in [((3.25,3.1,FLOOR+.55),(41.5,.35,1.1)),
                    ((3.25,72.9,FLOOR+.55),(41.5,.35,1.1)),
                    ((24,17,FLOOR+.55),(.35,28,1.1)),
                    ((24,59,FLOOR+.55),(.35,28,1.1)),
                    ((-87.3,37.5,FLOOR+.55),(.35,33,1.1)),
                    ((-52.5,21.15,FLOOR+.55),(70,.35,1.1))]:
    box(layout,'low perimeter wall',center,size,wall_mat)
sign(layout,'TERMINAL DEMO',(-46,19,4.9),1.6,white)
label(layout,'DEMO',(-11,8,FLOOR+.03),2.8,white)

entry=next(o for o in scene.objects if o.get('code')=='ENTRY')
entry.location=(20,38,FLOOR)
entry['label']='Intrare · acces public'
rectangle(entry,'entry zone',(16,31,24,45),green)
label(entry,'INTRARE',(20,38,FLOOR+.05),1,white)
for y in [30.8,45.2]: box(entry,'entry frame',(23.7,y,2.6),(.5,.5,4.3),green)
box(entry,'entry lintel',(23.7,38,4.8),(.5,14.9,.5),green)

checkin=next(o for o in scene.objects if o.get('code')=='CHECK-IN')
equipment(checkin,(3.5,65,FLOOR),.95,math.pi/2)
checkin['label']='Check-in · ghisee si asteptare'
rectangle(checkin,'check-in zone',(-15,56,22,71),checkin_floor)
label(checkin,'CHECK-IN',(3.5,59,FLOOR+.05),1.8,white)
sign(checkin,'CHECK-IN',(3.5,69,4.8),1.5,blue)
# Queue dividers with visible pedestrian passages.
for x in [-9,0,9,18]:
    box(checkin,'queue rail',(x,62,1.2),(.09,4,.09),blue)
    for y in [60,64]: box(checkin,'queue post',(x,y,.85),(.14,.14,.8),dark)

for n,y in enumerate([27,39,51],1):
    cp=next(o for o in scene.objects if o.get('code')==f'CP-{n}')
    equipment(cp,(0,y,FLOOR),.82)
    cp['label']=f'CP-{n} · control securitate'
    cp['labelAnchorBlender']=[0,y,6.7]
    rectangle(cp,'security lane',(-7,y-4.7,5,y+4.7),security_floor)
    rectangle(cp,'lane border',(-7,y-4.7,5,y-4.45),cyan)
    label(cp,f'CP-{n}',(-3,y-3.3,FLOOR+.055),1.35,cyan)
    sign(cp,f'CP-{n}',(0,y+3.6,4.8),1.6,cyan)
    for x in [-5,3]:
        box(cp,'queue guide',(x,y,1.2),(.09,7,.09),cyan)

corridor=next(o for o in scene.objects if o.get('code')=='CORRIDOR')
corridor.location=(-45,27,FLOOR)
corridor['label']='Coridor · distributie catre G-1 ... G-5'
rectangle(corridor,'boarding concourse',(-86,22,-8,32),seat_mat)
rectangle(corridor,'route line',(-84,26.6,-5,27.4),route_mat)
label(corridor,'PORTI G-1 - G-5',(-46,24,FLOOR+.055),1.1,white)
# Demonstrative wayfinding route, all on the public concourse (no walls crossed).
for b in [(19.6,45,20.4,54),(7,53.6,20,54.4),(6.6,39,7.4,54),(2,38.6,7,39.4),(-5.4,27,-4.6,39)]:
    rectangle(corridor,'passenger route',b,route_mat,FLOOR+.06)
for x in [-75,-61,-47,-33,-19]:
    mesh(corridor,'direction arrow',[(x+1,26,.53),(x-1,27,.53),(x+1,28,.53)],[(0,1,2)],white)

for n,x in enumerate([-80,-66,-52,-38,-24],1):
    gate=next(o for o in scene.objects if o.get('code')==f'G-{n}')
    equipment(gate,(x,47,FLOOR),.55,math.pi/2)
    gate['label']=f'G-{n} · poarta de imbarcare'
    gate['labelAnchorBlender']=[x,47,6.7]
    rectangle(gate,'boarding room',(x-6.3,33,x+6.3,53.5),gate_floor)
    rectangle(gate,'boarding border',(x-6.3,33,x+6.3,33.3),gold)
    rectangle(gate,'gate approach',(x-.3,28,x+.3,42),gold,FLOOR+.045)
    label(gate,f'G-{n}',(x,35.7,FLOOR+.06),2.2,gold)
    # Tall portal makes every gate recognizable as an exit, beyond its desk.
    for dx in [-2.4,2.4]: box(gate,'boarding door pillar',(x+dx,52,2.65),(.5,.5,4.4),gold)
    box(gate,'boarding door lintel',(x,52,4.9),(5.3,.5,.45),gold)
    sign(gate,f'G-{n}',(x,52,5.9),1.8,gold)
    for dx in [-6.4,6.4]: box(gate,'low gate divider',(x+dx,43.2,1),(.22,20,1.1),wall_mat)
    for dx in [-4.7,-3.3,3.3,4.7]:
        for y in [40,43]:
            box(gate,'waiting seat',(x+dx,y,1.1),(.9,.85,.18),seat_mat)
            box(gate,'seat back',(x+dx,y+.38,1.55),(.9,.12,.85),seat_mat)
            box(gate,'seat support',(x+dx,y,.78),(.18,.2,.56),dark)

# Join generated geometry by parent and material to control draw calls.
for parent in [layout,entry,checkin,corridor]+[o for o in scene.objects if o.type=='EMPTY' and o.get('zoneType') in ['checkpoint','gate']]:
    materials={}
    for obj in parent.children:
        if obj.type=='MESH' and obj.get('generatedFor')=='US-013':
            materials.setdefault(obj.data.materials[0].name,[]).append(obj)
    for name,objects in materials.items():
        if len(objects)<2: continue
        for obj in scene.objects: obj.select_set(False)
        for obj in objects: obj.select_set(True)
        bpy.context.view_layer.objects.active=objects[0]
        bpy.ops.object.join()
        objects[0].name=parent.name+'/DEMO '+name

bpy.context.view_layer.update()
for obj in terminal.objects:
    code=obj.get('code')
    if code not in entities: continue
    e=entities[code]
    if code not in ['T-DEMO','INTERIOR-FIXTURES','BAGGAGE-CLAIM']:
        obj['viewMode']='interior'; e['viewMode']='interior'
    e['label']=obj['label']
    e['positionBlenderMeters']=list(obj.location)
    e['positionThreeMeters']=[obj.location.x,obj.location.z,-obj.location.y]
    obj['baseHeight']=float(obj.location.z); e['baseHeight']=float(obj.location.z)
    if obj.get('zoneType') in ['gate','checkpoint']:
        e['demoDimensionsMeters']={'roomWidth':12.6,'roomDepth':20.5} if e['zoneType']=='gate' else {'laneWidth':9.4,'laneDepth':12}

def export(collection, filename):
    hidden=[]
    for obj in scene.objects: obj.select_set(False)
    for obj in collection.objects:
        hidden.append((obj,obj.hide_get(),obj.hide_render))
        obj.hide_set(False); obj.hide_render=False; obj.select_set(True)
    r=bpy.ops.export_scene.gltf.get_rna_type()
    # Dynamic format enums expose their identifiers through the registered callback.
    from io_scene_gltf2 import get_format_items
    glb_format=next(item[0] for item in get_format_items(scene,bpy.context) if item[0]=='GLB')
    bpy.ops.export_scene.gltf(filepath=os.path.join(OUT,filename),export_format=glb_format,
        use_selection=True,export_yup=True,export_extras=True,export_apply=True,
        export_animations=False,export_cameras=False,export_lights=False,
        export_meshopt_compression_enable=True,
        export_meshopt_extension=enum(r,'export_meshopt_extension','EXT_meshopt_compression'))
    triangles=0; calls=0
    for obj in collection.objects:
        if obj.type=='MESH':
            obj.data.calc_loop_triangles(); triangles+=len(obj.data.loop_triangles); calls+=max(1,len(obj.data.materials))
    for obj,h,r in hidden: obj.hide_set(h); obj.hide_render=r
    return dict(url='models/aerosynapse/'+filename,bytes=os.path.getsize(os.path.join(OUT,filename)),triangles=triangles,drawCallsUpperBound=calls)

manifest['assets']['terminal']=export(terminal,'terminal.glb')
manifest['version']=3
manifest['disclaimer']='TERMINAL DEMONSTRATIV — configurație fictivă pentru AeroSynapse AI; nu reproduce exact Craiova. Traseele și capacitățile sunt demonstrative.'
manifest['operationalLayout']={
    'version':1,'units':'meters','floorElevation':FLOOR,
    'boundsBlenderMeters':[[-87.5,3,.27],[24,73,7.1]],
    'footprint':'Main hall 41.5 x 70 m plus boarding wing 70 x 33 m; common floor 0.45 m',
    'gateSpacingMeters':14,'gateRoomDimensionsMeters':[12.6,20.5],
    'securityLaneCentersBlender':[[0,27,FLOOR],[0,39,FLOOR],[0,51,FLOOR]],
    'flow':['ENTRY','CHECK-IN','CP-1|CP-2|CP-3','CORRIDOR','G-1|G-2|G-3|G-4|G-5'],
    'shell':'Original architecture retained exactly, exterior-only; fictional operational layout reuses relocated source equipment',
    'newGeometryLicense':'Original project additions; follows the project terms. No external asset/font dependencies.',
}
manifest['entities']=list(entities.values())
assert manifest['assets']['terminal']['bytes']<manifest['performance']['maxAssetBytes']
assert manifest['assets']['terminal']['triangles']<manifest['performance']['maxTriangles']
with open(manifest_path,'w') as f: json.dump(manifest,f,ensure_ascii=False,indent=2)
scene['operationalLayoutVersion']=1
scene['disclaimer']=manifest['disclaimer']
scene.name='AeroSynapse · Terminal DEMO'

# Default Blender view is the functional interior, with external scenery hidden.
for collection in scene.collection.children:
    if collection.name.startswith(('02','03')):
        for obj in collection.objects: obj.hide_set(True); obj.hide_render=True
camera=scene.camera
camera.location=(45,-95,155)
camera.rotation_euler=(Vector((-31.5,38,1))-camera.location).to_track_quat('-Z','Y').to_euler()
camera.data.ortho_scale=145
for obj in scene.objects: obj.select_set(False)
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='CONSOLE': area.type='VIEW_3D'
        for space in area.spaces:
            if space.type=='VIEW_3D':
                space.shading.type=enum(space.shading,'type','MATERIAL')
                space.overlay.show_overlays=False
                space.region_3d.view_perspective=enum(space.region_3d,'view_perspective','CAMERA')
scene.render.resolution_x=1600; scene.render.resolution_y=1100
scene.render.filepath=os.path.join(ROOT,'assets/blender/terminal-operational-preview.png')
scene.cycles.samples=24
bpy.data.orphans_purge(do_local_ids=True,do_linked_ids=False,do_recursive=True)
bpy.context.preferences.filepaths.save_version=0
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'assets/blender/aerosynapse-user-airport.blend'))
print('US013_OPERATIONAL',json.dumps(manifest['assets']['terminal']),flush=True)
