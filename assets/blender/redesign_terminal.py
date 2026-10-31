"""Readable operator overview: section cutaway, clean finishes, checkpoint queue lanes and site context.

Destructive steps (cutaway, removal of static lane figures, foliage thinning) run once per model;
queue lanes, site context and finishes are rebuilt on every run. IDs and zone transforms stay unchanged.
"""
import bpy, bmesh, json, math, os, ast
from mathutils import Vector
ROOT=os.path.abspath(os.path.join(os.path.dirname(__file__),'../..'))
OUT=os.path.join(ROOT,'AeroSynapse.Client/public/models/aerosynapse')
MAPS=os.path.join(os.path.dirname(__file__),'materials')
scene=bpy.context.scene
terminal=next(c for c in scene.collection.children if c.name.startswith('01'))
roots={o.get('code'):o for o in terminal.objects if o.type=='EMPTY' and o.get('code')}
F=.45
CUT=2.9  # Section height: walls and glazing stay legible, arches and beams move to the eye-level view.
white=bpy.data.materials['Architectural · white signage']
glass=bpy.data.materials['Architectural · blue architectural glazing']
font=bpy.data.fonts.load(os.path.join(MAPS,'Lato-Bold.ttf'),check_existing=True)
source=ast.parse(open(os.path.join(os.path.dirname(__file__),'upgrade_terminal_architecture.py')).read())
names={'enum','mat','uv','mesh','box','plane','cylinder'}
exec(compile(ast.Module(body=[n for n in source.body if isinstance(n,ast.FunctionDef) and n.name in names],type_ignores=[]),__file__,'exec'))

def finish(name,color,rough,metal=0):
    m=bpy.data.materials.get('Terminal Design · '+name) or mat(name,color,rough,metal)
    p=next(n for n in m.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    p.inputs['Base Color'].default_value=(*color,1);p.inputs['Roughness'].default_value=rough;p.inputs['Metallic'].default_value=metal
    m.diffuse_color=(*color,1);return m
def empty(name,parent=None,**props):
    o=bpy.data.objects.new(name,None);terminal.objects.link(o);o.parent=parent
    for k,v in {'synthetic':True,'clickable':False,'raycastIgnore':True,'redesignAddition':True,**props}.items():o[k]=v
    return o
def world_bounds(o):
    w=[o.matrix_world@Vector(c) for c in o.bound_box]
    return [min(p[i] for p in w) for i in range(3)],[max(p[i] for p in w) for i in range(3)]
def join(parts,group,**props):
    # Batch by material so each addition costs a handful of draw calls.
    by={}
    for o in parts:
        o['redesignAddition']=True;o['synthetic']=True
        for k,v in props.items():o[k]=v
        by.setdefault(o.data.materials[0].name,[]).append(o)
    for objects in by.values():
        for o in scene.objects:o.select_set(False)
        for o in objects:o.select_set(True)
        bpy.context.view_layer.objects.active=objects[0]
        if len(objects)>1:bpy.ops.object.join()
        objects[0].name=f'{group.name}/{objects[0].data.materials[0].name}'
    for o in scene.objects:o.select_set(False)

for o in [o for o in terminal.objects if o.get('redesignAddition')]:
    if o.name in bpy.data.objects:bpy.data.objects.remove(o,do_unlink=True)
layout=roots['T-LAYOUT']

# 1 · Section cutaway. Upper halves keep their materials/UVs and remain visible at eye level and outside.
if not scene.get('redesignCutaway'):
    upper=bpy.data.objects.new('AS|terminal|UPPER-STRUCTURE',None);terminal.objects.link(upper)
    for k,v in {'viewMode':'immersive','kind':'ceiling','clickable':False,'raycastIgnore':True,'synthetic':True}.items():upper[k]=v
    for o in [o for o in layout.children if o.type=='MESH']:
        low,high=world_bounds(o)
        # Planters are furniture, not structure; their trees keep full height.
        if high[2]<=CUT+.01 or 'planter' in o.name or 'oak veneer' in o.name:continue
        o['raycastIgnore']=True
        if low[2]>=CUT-.01:
            o.parent=upper;continue
        top=o.copy();top.data=o.data.copy();terminal.objects.link(top);top.parent=upper;top.name=o.name+' · upper'
        inverse=o.matrix_world.inverted()
        co=inverse@Vector((0,0,CUT));no=(inverse.to_3x3().transposed().inverted()@Vector((0,0,1))).normalized()
        for target,keep_low in [(o,True),(top,False)]:
            bm=bmesh.new();bm.from_mesh(target.data)
            cut=bmesh.ops.bisect_plane(bm,geom=bm.verts[:]+bm.edges[:]+bm.faces[:],plane_co=co,plane_no=no,clear_outer=keep_low,clear_inner=not keep_low)
            if keep_low:
                # Cap the section so posts and walls read as solid cut elements from above.
                bmesh.ops.holes_fill(bm,edges=[e for e in cut['geom_cut'] if isinstance(e,bmesh.types.BMEdge) and e.is_valid and e.is_boundary],sides=0)
            bm.to_mesh(target.data);bm.free();target.data.update()
    scene['redesignCutaway']=1

# 2 · Static figures standing in the security lanes looked like a queue even when the simulator reports zero.
if not scene.get('redesignLaneFiguresRemoved'):
    for code in ['CP-1','CP-2','CP-3']:
        c=roots[code].matrix_world.translation.y
        for o in list(roots[code].children):
            if o.type!='MESH' or o.get('sourceObject'):continue
            low,high=world_bounds(o)
            if high[2]<2.3 and c-3.3<low[1] and high[1]<c-2.7:bpy.data.objects.remove(o,do_unlink=True)
    scene['redesignLaneFiguresRemoved']=1
# The same holds at the check-in desks now that their queue is simulated.
if not scene.get('redesignCheckInFiguresRemoved'):
    for o in list(roots['CHECK-IN'].children):
        if o.type=='MESH' and ('architectural figure' in o.name or 'carry-on shell' in o.name):bpy.data.objects.remove(o,do_unlink=True)
    scene['redesignCheckInFiguresRemoved']=1

# 3 · Halve the lobby leaf cards to buy triangle budget for the queue lanes and site context.
if not scene.get('redesignFoliageThinned'):
    for o in terminal.objects:
        if o.type=='MESH' and o.name.endswith('PUBLIC-LOUNGE/Architectural · planter foliage'):
            bm=bmesh.new();bm.from_mesh(o.data);bm.faces.ensure_lookup_table()
            bmesh.ops.delete(bm,geom=[f for f in bm.faces if (f.index//2)%2],context='FACES')
            bm.to_mesh(o.data);bm.free()
    scene['redesignFoliageThinned']=1

# 4 · Finishes: brighter floor and clean scanner housings instead of stone-textured blocks.
terrazzo=bpy.data.materials['Terminal Design · large format terrazzo']
tint=next(n for n in terrazzo.node_tree.nodes if n.type=='MIX' and n.label=='Studio finish tint')
next(s for s in tint.inputs if s.name=='B' and s.type=='RGBA').default_value=(.9,.87,.81,1)
housing=finish('scanner housing',(.80,.83,.84),.3)
for code in ['CP-1','CP-2','CP-3']:
    for o in roots[code].children:
        if o.type=='MESH' and o.name.endswith('X-ray scanner body'):o.data.materials[0]=housing

# 5 · Serpentine queue lanes in front of each checkpoint. The path is exported so playback stands people in them.
queue_floor=finish('queue zone floor',(.2,.25,.29),.85)
belt=finish('queue belt',(.02,.23,.33),.45)
post_finish=bpy.data.materials['Architectural · brushed stainless steel']
X0,X1,LANES,WIDTH=5.7,15.3,5,1.2
queue_paths={};flow_paths={}
# Checked-in passengers leave the desks at their east end and walk on to a control.
CHECK_IN_EXIT=(19.0,62.3)
for code in ['CP-1','CP-2','CP-3']:
    zone=roots[code];c=zone.matrix_world.translation.y
    group=empty(f'AS|terminal|{code}|QUEUE',zone,raycastIgnore=False)
    group.matrix_parent_inverse=zone.matrix_world.inverted()  # Helpers author in world metres.
    parts=[plane(group,'queue zone floor',(X0-.25,c-3.25,X1+.3,c+3.25),queue_floor,F+.012)]
    lane=[c-(LANES-1)*WIDTH/2+i*WIDTH for i in range(LANES)]
    line=[c-LANES*WIDTH/2+k*WIDTH for k in range(LANES+1)]
    segments=[((X0,line[0]),(X1,line[0])),((X0,line[-1]),(X1,line[-1]))]
    for k in range(1,LANES):
        # Each inner barrier leaves a turn opening on the side where the previous lane ends.
        segments.append(((X0,line[k]),(X1-WIDTH,line[k])) if (k-1)%2==0 else ((X0+WIDTH,line[k]),(X1,line[k])))
    segments+=[((X1,line[0]),(X1,line[-2])),((X0,line[1]),(X0,line[-1]))]
    posts=set()
    for a,b in segments:
        n=max(1,math.ceil(math.dist(a,b)/2.4))
        points=[(a[0]+(b[0]-a[0])*i/n,a[1]+(b[1]-a[1])*i/n) for i in range(n+1)]
        posts.update((round(x,3),round(y,3)) for x,y in points)
        for p,q in zip(points,points[1:]):
            dx,dy=q[0]-p[0],q[1]-p[1]
            parts.append(box(group,'queue belt',((p[0]+q[0])/2,(p[1]+q[1])/2,F+.93),(abs(dx) or .03,abs(dy) or .03,.05),belt))
    for x,y in sorted(posts):parts.append(cylinder(group,'queue post',(x,y,F+.49),.045,.98,post_finish,6))
    path=[(X0+.3,lane[0])]
    for i,y in enumerate(lane):
        if i:path.append((path[-1][0],y))
        path.append(((X1-.55) if i%2==0 else (X0+.55),y))
    path[-1]=(X1+.9,lane[-1])  # The tail opens into the landside hall.
    queue_paths[code]=[[round(x,3),round(y,3),F] for x,y in path]
    zone['queuePathBlender']=[v for p in queue_paths[code] for v in p]
    # Walking routes for flows derived from snapshot deltas: arrivals reach the queue tail from the
    # entrance and check-in; screened passengers leave the head, cross the lane and walk to each gate.
    tail=(X1+.9,lane[-1]);side=X1+1.9
    arrive=[[(24.8,38),(side,38),(side,tail[1]),tail],[CHECK_IN_EXIT,(CHECK_IN_EXIT[0],57),(side,57),(side,tail[1]),tail]]
    depart=[[(X0+.3,lane[0]),(4.2,lane[0]),(-7.5,lane[0]),(-13,27),(gx,27),(gx,33.5),(gx,39.5)]
            for gx in (roots[f'G-{n}'].matrix_world.translation.x for n in range(1,6))]
    flows={'arrive':arrive,'depart':depart}
    encoded={k:[[v for x,y in p for v in (round(x,3),round(y,3),F)] for p in paths] for k,paths in flows.items()}
    # Gate zone per departure route lets playback send walkers to their flights' gates.
    encoded['departGates']=[roots[f'G-{n}']['zoneId'] for n in range(1,6)]
    zone['flowPathsBlender']=json.dumps(encoded)
    flow_paths[code]=json.loads(zone['flowPathsBlender'])
    join(parts,group)
# The check-in line folds once inside the existing stanchions: the head waits in front of the middle desks,
# the tail opens east towards the entrance. Arrivals walk in from the entrance; served passengers walk along
# the desk fronts to the exit, where the controls' arrival route takes over.
checkin=roots['CHECK-IN']
path=[(3.5,61.1),(-9.6,61.1),(-9.6,59.3),(17.6,59.3),(17.6,57.8)]
queue_paths['CHECK-IN']=[[x,y,F] for x,y in path]
checkin['queuePathBlender']=[v for p in queue_paths['CHECK-IN'] for v in p]
flows={'arrive':[[(24.8,38),(20.6,38),(20.6,57.8),path[-1]]],'depart':[[(3.5,62.3),CHECK_IN_EXIT]]}
checkin['flowPathsBlender']=json.dumps({k:[[v for x,y in p for v in (round(x,3),round(y,3),F)] for p in paths] for k,paths in flows.items()})
flow_paths['CHECK-IN']=json.loads(checkin['flowPathsBlender'])

# 6 · Site context so the terminal sits on an airport plot instead of floating in a void.
# Layers sit 10–20 cm apart: overview distances turn centimetre gaps into depth fighting.
site=empty('AS|terminal|SITE-CONTEXT',None,viewMode='interior')
# A pale sage lawn: the viewer's studio is light, so the plot should not read as a dark backdrop.
lawn=finish('site lawn',(.30,.37,.27),.95)
paving=finish('forecourt paving',(.3,.3,.29),.9)
asphalt=finish('site asphalt',(.065,.07,.075),.88)
marking=finish('road marking',(.75,.74,.68),.6)
canopy=finish('tree canopy',(.10,.20,.11),.85)
bark=finish('tree bark',(.18,.13,.09),.9)
parts=[
    plane(site,'site lawn',(-260,-180,220,260),lawn,-.4),
    plane(site,'airside apron extension',(-100,78,30,98),asphalt,-.2),
    plane(site,'landside forecourt',(24.3,-6,36,82),paving,.0),
    plane(site,'access road',(36,-22,48,98),asphalt,-.2),
]
for y in range(-20,96,6):parts.append(plane(site,'road marking',(41.88,y,42.12,y+3),marking,-.1))
def tree(x,y,s=1):
    parts.append(cylinder(site,'tree trunk',(x,y,1.1*s),.14*s,2.2*s,bark,6))
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=1,radius=1.9*s,location=(x,y,3.4*s))
    crown=bpy.context.active_object;crown.scale.z=.82;bpy.ops.object.transform_apply(scale=True)
    for col in list(crown.users_collection):col.objects.unlink(crown)
    terminal.objects.link(crown);crown.parent=site;crown.data.materials.append(canopy);crown.name=site.name+'/tree crown'
    for p in crown.data.polygons:p.use_smooth=True
    parts.append(crown)
for y in range(6,76,9):tree(33.2,y)
for x,y,s in [(54,8,1.2),(58,30,1),(55,52,1.3),(60,74,1.1),(-30,-12,1.2),(-58,-10,1),(-86,-14,1.3),(10,-14,1.1)]:tree(x,y,s)
join(parts,site,raycastIgnore=True,contextOnly=True)  # contextOnly: excluded from camera framing.
bpy.context.view_layer.update()

# 7 · Overview labels: check-in is on the passenger path like the controls, so it is labelled too.
# Each labelled zone carries its caption; the viewer draws what the asset says it is.
labels={'CHECK-IN':([3.5,68.0,6.7],'GHIȘEE CHECK-IN')}
# Control labels sit low and over the south half of their lanes: at 6.7 m the overview tilt lifted CP-3's
# label onto the check-in queue. Absolute anchors keep re-runs of this script idempotent.
labels|={f'CP-{n}':([0.0,24.0+12*(n-1),2.6],'CONTROL SECURITATE') for n in range(1,4)}
for code,(anchor,caption) in labels.items():
    roots[code]['labelAnchorBlender']=anchor;roots[code]['labelCaption']=caption
# The desks are a simulated zone of their own (twin.check_in_areas, seeded as ID 901), not part of the terminal shell.
CHECK_IN_ZONE='60000000-0000-0000-0000-000000000901'
roots['CHECK-IN']['zoneId']=CHECK_IN_ZONE;roots['CHECK-IN']['zoneType']='checkIn'

# 8 · Export, budgets and provenance, mirroring the studio stage.
saved=[]
for obj in terminal.objects:
    saved.append((obj,obj.hide_get(),obj.hide_render));obj.hide_set(False);obj.hide_render=False;obj.select_set(True)
from io_scene_gltf2 import get_format_items
rna=bpy.ops.export_scene.gltf.get_rna_type()
bpy.ops.export_scene.gltf(filepath=os.path.join(OUT,'terminal.glb'),export_format=next(i[0] for i in get_format_items(scene,bpy.context) if i[0]=='GLB'),use_selection=True,
    export_yup=True,export_extras=True,export_apply=True,export_animations=False,export_cameras=False,export_lights=False,
    export_meshopt_compression_enable=True,export_meshopt_extension=enum(rna,'export_meshopt_extension','EXT_meshopt_compression'))
for obj,h,r in saved:obj.hide_set(h);obj.hide_render=r;obj.select_set(False)
triangles=0;calls=0
for obj in terminal.objects:
    if obj.type=='MESH':obj.data.calc_loop_triangles();triangles+=len(obj.data.loop_triangles);calls+=max(1,len(obj.data.materials))
p=os.path.join(OUT,'manifest.json');manifest=json.load(open(p))
asset=manifest['assets']['terminal'];asset.update(bytes=os.path.getsize(os.path.join(OUT,'terminal.glb')),triangles=triangles,drawCallsUpperBound=calls)
assert asset['bytes']<manifest['performance']['maxAssetBytes'] and triangles<manifest['performance']['maxTriangles'],asset
manifest['version']=9
for entity in manifest['entities']:
    if entity['code'] in labels:entity['labelAnchorBlender'],entity['labelCaption']=labels[entity['code']]
    if entity['code']=='CHECK-IN':
        entity.update(zoneId=CHECK_IN_ZONE,zoneType='checkIn',databaseMapping={'table':'check_in_areas','id':CHECK_IN_ZONE})
manifest['operatorRedesign']={'version':1,'sectionCutBlenderZ':CUT,
    'features':['section cutaway: arches, beams and upper glazing shown only at eye level and outside','solid section caps on walls and posts',
        'clean scanner housings','serpentine belt-barrier queue lanes before CP-1…CP-3','check-in queue path and walking routes; static figures removed from the desks','illustrative walking routes for snapshot-derived arrival and departure flows','static figures removed from security lanes',
        'site context: lawn, forecourt, access road, airside apron extension and trees'],
    'queuePathsBlenderMeters':queue_paths,
    'flowPathsBlenderMeters':flow_paths,
    'queueLane':{'xBlenderMeters':[X0,X1],'lanes':LANES,'laneWidthMeters':WIDTH},
    'license':'Original procedural project additions; existing texture and font licenses unchanged'}
with open(p,'w') as f:json.dump(manifest,f,ensure_ascii=False,indent=2)
scene['operatorRedesignVersion']=1
bpy.data.orphans_purge(do_local_ids=True,do_linked_ids=False,do_recursive=True)
bpy.ops.file.pack_all();bpy.context.preferences.filepaths.save_version=0
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'assets/blender/aerosynapse-user-airport.blend'))
print('OPERATOR_REDESIGN',json.dumps(asset),flush=True)
