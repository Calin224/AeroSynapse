"""Architectural studio presentation, additive and repeatable; glTF metres/Y-up stay unchanged."""
import bpy, json, math, os, ast
from mathutils import Vector, Matrix
ROOT=os.path.abspath(os.path.join(os.path.dirname(__file__),'../..'))
OUT=os.path.join(ROOT,'AeroSynapse.Client/public/models/aerosynapse')
MAPS=os.path.join(os.path.dirname(__file__),'materials')
scene=bpy.context.scene
terminal=next(c for c in scene.collection.children if c.name.startswith('01'))
roots={o.get('code'):o for o in terminal.objects if o.type=='EMPTY'}
F=.45
white=bpy.data.materials['Architectural · white signage']
# Reuse the same authored mesh/UV/text conventions without rerunning earlier scene mutations.
source=ast.parse(open(os.path.join(os.path.dirname(__file__),'upgrade_terminal_architecture.py')).read())
names={'enum','mat','uv','mesh','box','plane','cylinder','ellipsoid','text'}
exec(compile(ast.Module(body=[n for n in source.body if isinstance(n,ast.FunctionDef) and n.name in names],type_ignores=[]),__file__,'exec'))
def tint_texture(material,factor):
    tree=material.node_tree
    shader=next(n for n in tree.nodes if n.type=='BSDF_PRINCIPLED')
    socket=shader.inputs['Base Color']
    old=socket.links[0].from_socket
    mix=next((n for n in tree.nodes if n.type=='MIX' and n.label=='Studio finish tint'),None)
    if mix is None:
        mix=tree.nodes.new('ShaderNodeMix');mix.label='Studio finish tint'
        mix.data_type=enum(mix,'data_type','RGBA');mix.blend_type=enum(mix,'blend_type','MULTIPLY')
        next(s for s in mix.inputs if s.name=='Factor' and s.type=='VALUE').default_value=1
        tree.links.new(old,next(s for s in mix.inputs if s.name=='A' and s.type=='RGBA'))
        tree.links.new(next(s for s in mix.outputs if s.type=='RGBA'),socket)
    next(s for s in mix.inputs if s.name=='B' and s.type=='RGBA').default_value=(*factor,1)
tint_texture(bpy.data.materials['Terminal Design · large format terrazzo'],(.68,.60,.48))
tint_texture(bpy.data.materials['Architectural · white plaster'],(.75,.78,.76))
ivory=bpy.data.materials['Terminal Design · satin ivory structure']
next(n for n in ivory.node_tree.nodes if n.type=='BSDF_PRINCIPLED').inputs['Base Color'].default_value=(.55,.59,.58,1)
for o in list(terminal.objects):
    if o.get('studioAddition'): bpy.data.objects.remove(o,do_unlink=True)
layout=roots['T-LAYOUT']
parent=bpy.data.objects.new('AS|terminal|PUBLIC-LOUNGE',None)
terminal.objects.link(parent);parent.parent=layout
parent['studioAddition']=True;parent['synthetic']=True;parent['clickable']=False;parent['raycastIgnore']=True
wood=bpy.data.materials['Architectural · natural oak veneer']
brass=bpy.data.materials['Architectural · champagne brass']
glass=bpy.data.materials['Architectural · blue architectural glazing']
steel=bpy.data.materials['Architectural · brushed stainless steel']
white=bpy.data.materials['Architectural · white signage']
dark=bpy.data.materials['Architectural · dark aluminium frames']
leaves=bpy.data.materials['Architectural · planter foliage']
soil=bpy.data.materials['Architectural · planter soil']
font=bpy.data.fonts.load(os.path.join(MAPS,'Lato-Bold.ttf'))
base=mat('studio graphite foundation',(.018,.027,.032),.58,.22)
upholstery=mat('lounge teal upholstery',(.025,.105,.112),.85)
ceramic=mat('sandstone planter',(.37,.32,.24),.8)
rug=mat('lounge warm woven inset',(.12,.145,.13),.94)
led=mat('studio amber edge light',(.65,.34,.12),.25,emission=1.8)
# A single connected foundation follows the actual L shaped operational footprint.
outline=[(-17.7,2.7),(24.3,2.7),(24.3,73.3),(-17.7,73.3),(-17.7,54.3),(-87.8,54.3),(-87.8,20.7),(-17.7,20.7)]
v=[(x,y,z) for z in [-.95,.075] for x,y in outline];n=len(outline)
faces=[tuple(reversed(range(n))),tuple(range(n,n*2))]+[(i,(i+1)%n,(i+1)%n+n,i+n) for i in range(n)]
mesh(parent,'connected graphite foundation',v,faces,base,.12,True)
# Lobby island: a curved seat faces a landscaped focal point, with clear circulation around it.
cx,cy=9,12.2
def orient(obj,angle):
    # Generated mesh origins belong to the zone; rotate the geometry around its own centre.
    center=sum((v.co for v in obj.data.vertices),Vector())/len(obj.data.vertices)
    rotation=Matrix.Rotation(angle,3,'Z')
    for v in obj.data.vertices:v.co=center+rotation@(v.co-center)
def foliage(center,radius,height,count):
    verts=[];faces=[]
    for i in range(count):
        a=i*2.399963;vertical=math.sin(i*1.731)
        r=radius*math.sqrt(max(0,1-vertical*vertical))*math.sqrt((i*.618034)%1)
        c=Vector((center[0]+math.cos(a)*r,center[1]+math.sin(a)*r,center[2]+vertical*height))
        u=Vector((math.cos(a),math.sin(a),math.sin(i*.717))).normalized()*(.075+.025*math.sin(i*2.17))
        v=Vector((-math.sin(a),math.cos(a),0))*.027
        start=len(verts);verts.extend([c-u,c+v,c+u,c-v])
        faces.extend([(start,start+1,start+2),(start,start+2,start+3)])
    mesh(parent,'individual botanical leaves',verts,faces,leaves,ignore=True)
def branch(a,b,radius):
    a,b=Vector(a),Vector(b);direction=(b-a).normalized()
    u=direction.cross(Vector((0,0,1))).normalized()*radius;v=direction.cross(u).normalized()*radius
    verts=[p+math.cos(i*math.tau/8)*u+math.sin(i*math.tau/8)*v for p in [a,b] for i in range(8)]
    faces=[(i,(i+1)%8,(i+1)%8+8,i+8) for i in range(8)]
    mesh(parent,'olive branch',verts,faces,wood,ignore=True)
cylinder(parent,'lounge carpet disc',(cx,cy,F+.027),5.15,.03,rug,64)
cylinder(parent,'planter stone drum',(cx,cy,F+.48),1.48,.95,ceramic,40)
cylinder(parent,'planter soil bed',(cx,cy,F+.97),1.39,.04,soil,32)
cylinder(parent,'olive trunk',(cx,cy,F+2.05),.095,2.2,wood,12)
foliage((cx,cy,F+3.15),1.45,1.05,2000)
for i in range(16):
    a=i*2.39996
    branch((cx,cy,F+2.1+(i%3)*.2),(cx+math.cos(a)*1.1,cy+math.sin(a)*1.1,F+3.0+math.sin(i*1.73)*.65),.023)
for i in range(24):
    a=math.radians(25+i*11)
    # Local tangential segments keep the seating visibly curved at a modest triangle budget.
    o=box(parent,'curved lounge seat',(cx+math.cos(a)*3.6,cy+math.sin(a)*3.6,F+.48),(1.02,.85,.24),upholstery,.07)
    orient(o,a+math.pi/2)
    o=box(parent,'curved lounge back',(cx+math.cos(a)*3.93,cy+math.sin(a)*3.93,F+.88),(1.04,.18,.74),upholstery,.06)
    orient(o,a+math.pi/2)
    o=box(parent,'lounge timber plinth',(cx+math.cos(a)*3.65,cy+math.sin(a)*3.65,F+.24),(1,.72,.36),wood,.04)
    orient(o,a+math.pi/2)
# Public information desk in the unused foyer, with fine vertical oak detailing.
box(parent,'concierge core',(-9.5,12,F+.56),(4.8,1.5,1.12),dark,.14)
box(parent,'concierge stone top',(-9.5,12,F+1.15),(4.95,1.6,.12),ceramic,.08)
for i in range(32):
    box(parent,'concierge oak fluting',(-11.8+i*.148,11.22,F+.58),(.07,.06,.93),wood,.012)
box(parent,'information monitor',(-9.5,12.2,F+1.55),(.8,.07,.55),dark,.03)
text(parent,'INFORMATION',(-9.5,11.165,F+.57),.28,white)
# Floor wayfinding aligns with the actual entrance/check-in/security layout.
text(parent,'AEROSYNAPSE',(-4,6.2,F+.047),1.1,brass,True)
text(parent,'TERMINAL DEMONSTRATIV',(-4,5.15,F+.047),.35,white,True)
for x in [-13,19]:
    for y in [24,58]:
        cylinder(parent,'public stone planter',(x,y,F+.45),.62,.9,ceramic,24)
        cylinder(parent,'public plant soil',(x,y,F+.91),.55,.025,soil,16)
        cylinder(parent,'public plant trunk',(x,y,F+1.5),.045,1.2,wood,8)
        foliage((x,y,F+2.15),.64,.65,220)
# Batch by material so decorative detail adds nine meshes rather than hundreds of draw calls.
by={}
for o in list(parent.children):
    if o.type=='MESH':
        o['studioAddition']=True;o['generatedFor']='Architectural studio';o['raycastIgnore']=True
        by.setdefault(o.data.materials[0].name,[]).append(o)
for name,objects in by.items():
    for o in scene.objects:o.select_set(False)
    for o in objects:o.hide_set(False);o.select_set(True)
    bpy.context.view_layer.objects.active=objects[0]
    if len(objects)>1:bpy.ops.object.join()
    objects[0].name=parent.name+'/'+name;objects[0]['studioAddition']=True
for o in scene.objects:o.select_set(False)
bpy.context.view_layer.update()
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
manifest['version']=7
manifest['operationalLayout']['boundsBlenderMeters'][0]=[-91,2.7,-.95]
manifest['studioPresentation']={'version':1,'features':['connected L shaped graphite foundation','curved upholstered public lounge','landscaped olive island with individual leaf geometry','oak fluted concierge desk','public wayfinding'],
    'foundationElevationMeters':[-.95,.075],'loungeCenterBlenderMeters':[9,12.2,.45],'loungeDiameterMeters':10.3,'license':'Original procedural project additions; existing texture and font licenses unchanged',
    'renderer':'Three.js architectural light rig with adaptive GTAO; analytic rounded zone contours and aggregate occupancy discs'}
with open(p,'w') as f:json.dump(manifest,f,ensure_ascii=False,indent=2)
scene['studioPresentationVersion']=1
bpy.data.orphans_purge(do_local_ids=True,do_linked_ids=False,do_recursive=True)
bpy.ops.file.pack_all();bpy.context.preferences.filepaths.save_version=0
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'assets/blender/aerosynapse-user-airport.blend'))
print('STUDIO_TERMINAL',json.dumps(asset),flush=True)
