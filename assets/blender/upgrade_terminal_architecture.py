"""Architectural terminal: continuous glazing, vaulted structure and daylight."""
import bpy, bmesh, json, math, os
from mathutils import Vector

ROOT=os.path.abspath(os.path.join(os.path.dirname(__file__),'../..'))
MAPS=os.path.join(os.path.dirname(__file__),'materials')
OUT=os.path.join(ROOT,'AeroSynapse.Client/public/models/aerosynapse')
scene=bpy.context.scene
assert scene.get('materialRefinementVersion')==1
assert not scene.get('architecturalUpgradeVersion'), 'Use the pre-upgrade backup to regenerate.'
terminal=next(c for c in scene.collection.children if c.name.startswith('01'))
roots={o.get('code'):o for o in terminal.objects if o.type=='EMPTY'}
F=.45

def enum(owner,prop,wanted):
    props=owner.properties if hasattr(owner,'properties') else owner.bl_rna.properties
    return next(i.identifier for i in props[prop].enum_items if i.identifier==wanted)

def mat(name,color,rough=.45,metal=0,emission=0):
    m=bpy.data.materials.new('Terminal Design · '+name);m.use_nodes=True;m.diffuse_color=(*color,1)
    p=next(n for n in m.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    for name,value in [('Base Color',(*color,1)),('Roughness',rough),('Metallic',metal),('Emission Color',(*color,1)),('Emission Strength',emission)]:p.inputs[name].default_value=value
    m.use_backface_culling=False;m['tileMeters']=1
    return m

def image_link(m,name,input_name,color=True):
    n=m.node_tree.nodes.new('ShaderNodeTexImage');n.image=bpy.data.images.load(os.path.join(MAPS,name),check_existing=True)
    if not color:n.image.colorspace_settings.name='Non-Color'
    p=next(n for n in m.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    m.node_tree.links.new(n.outputs['Color'],p.inputs[input_name]);return n

floor_mat=mat('large format terrazzo',(.58,.62,.6),.3)
image_link(floor_mat,'terminal-terrazzo.jpg','Base Color')
image_link(floor_mat,'terminal-terrazzo-rough.png','Roughness',False)
nm=floor_mat.node_tree.nodes.new('ShaderNodeNormalMap');nm.inputs['Strength'].default_value=.35
tex=floor_mat.node_tree.nodes.new('ShaderNodeTexImage');tex.image=bpy.data.images.load(os.path.join(MAPS,'terminal-terrazzo-normal.png'),check_existing=True);tex.image.colorspace_settings.name='Non-Color'
floor_mat.node_tree.links.new(tex.outputs['Color'],nm.inputs['Color'])
floor_mat.node_tree.links.new(nm.outputs['Normal'],next(n for n in floor_mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED').inputs['Normal'])
floor_mat['tileMeters']=3.6
ivory=mat('satin ivory structure',(.72,.76,.76),.34,.08)
steel=bpy.data.materials['Architectural · brushed stainless steel']
wood=bpy.data.materials['Architectural · natural oak veneer']
dark=bpy.data.materials['Architectural · dark aluminium frames']
brass=bpy.data.materials['Architectural · champagne brass']
glass=bpy.data.materials['Architectural · blue architectural glazing']
gp=next(n for n in glass.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
gp.inputs['Alpha'].default_value=.14;gp.inputs['Roughness'].default_value=.09;gp.inputs['Metallic'].default_value=.1
glass.diffuse_color=(*glass.diffuse_color[:3],.14)
led=mat('daylight LED strips',(.91,.94,1),.25,emission=4)
skin=mat('architectural figure skin',(.44,.29,.21),.72)
shirt=mat('architectural figure jacket',(.045,.12,.17),.75)
shirt2=mat('architectural figure coat',(.28,.19,.12),.82)
trousers=mat('architectural figure trousers',(.035,.043,.049),.84)
bag=mat('carry-on shell',(.045,.19,.22),.4,.12)
asphalt=mat('apron asphalt',(.1,.12,.13),.9)
image_link(asphalt,'terminal-apron.jpg','Base Color');asphalt['tileMeters']=4
white=mat('apron markings',(.85,.9,.86),.8)
font=bpy.data.fonts.load(os.path.join(MAPS,'Lato-Bold.ttf'))

def uv(obj,m):
    data=obj.data;layer=data.uv_layers[0] if data.uv_layers else data.uv_layers.new(name='UVMap');tile=m.get('tileMeters',1)
    for face in data.polygons:
        axis=max(range(3),key=lambda i:abs(face.normal[i]));axes=(1,2) if axis==0 else (0,2) if axis==1 else (0,1)
        for loop in face.loop_indices:
            v=data.vertices[data.loops[loop].vertex_index].co+obj.parent.location
            layer.data[loop].uv=(v[axes[0]]/tile,v[axes[1]]/tile)

def mesh(parent,name,verts,faces,m,bevel=0,ignore=False):
    data=bpy.data.meshes.new(name);data.from_pydata([tuple(Vector(v)-parent.location) for v in verts],[],faces);data.materials.append(m);data.update()
    obj=bpy.data.objects.new(parent.name+'/'+name,data);terminal.objects.link(obj);obj.parent=parent
    obj['generatedFor']='US013-architecture';obj['synthetic']=True
    if ignore or m==glass:obj['raycastIgnore']=True
    if bevel:
        bpy.context.view_layer.objects.active=obj;obj.select_set(True)
        mod=obj.modifiers.new('edge radius',enum(bpy.types.Modifier,'type','BEVEL'));mod.width=bevel;mod.segments=1
        bpy.ops.object.modifier_apply(modifier=mod.name);obj.select_set(False)
    uv(obj,m);return obj

def box(parent,name,c,s,m,bevel=0,ignore=False):
    x,y,z=c;a,b,d=[v/2 for v in s]
    return mesh(parent,name,[(x-a,y-b,z-d),(x+a,y-b,z-d),(x+a,y+b,z-d),(x-a,y+b,z-d),(x-a,y-b,z+d),(x+a,y-b,z+d),(x+a,y+b,z+d),(x-a,y+b,z+d)],[(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)],m,bevel,ignore)

def plane(parent,name,b,m,z=F+.035,ignore=False):
    x0,y0,x1,y1=b
    return mesh(parent,name,[(x0,y0,z),(x1,y0,z),(x1,y1,z),(x0,y1,z)],[(0,1,2,3)],m,ignore=ignore)

def cylinder(parent,name,c,r,h,m,n=10):
    x,y,z=c;verts=[(x+math.cos(i*math.tau/n)*r,y+math.sin(i*math.tau/n)*r,z+off) for off in [-h/2,h/2] for i in range(n)]
    faces=[tuple(reversed(range(n))),tuple(range(n,n*2))]+[(i,(i+1)%n,(i+1)%n+n,i+n) for i in range(n)]
    o=mesh(parent,name,verts,faces,m)
    for p in list(o.data.polygons)[2:]:p.use_smooth=True
    return o

def ellipsoid(parent,name,c,s,m):
    verts=[];faces=[];segments=10;rings=6
    for j in range(rings+1):
        phi=math.pi*j/rings
        for i in range(segments):
            a=math.tau*i/segments
            verts.append((c[0]+s[0]*math.sin(phi)*math.cos(a),c[1]+s[1]*math.sin(phi)*math.sin(a),c[2]+s[2]*math.cos(phi)))
    for j in range(rings):
        for i in range(segments):faces.append((j*segments+i,j*segments+(i+1)%segments,(j+1)*segments+(i+1)%segments,(j+1)*segments+i))
    o=mesh(parent,name,verts,faces,m)
    for p in o.data.polygons:p.use_smooth=True
    return o

def text(parent,body,c,size,m=white,flat=False):
    kind=next(i.identifier for i in bpy.data.curves.bl_rna.functions['new'].parameters['type'].enum_items if i.identifier=='FONT')
    curve=bpy.data.curves.new(body,kind);curve.font=font;curve.body=body;curve.size=size;curve.resolution_u=2;curve.align_x=enum(curve,'align_x','CENTER')
    o=bpy.data.objects.new(parent.name+'/'+body,curve);terminal.objects.link(o);o.parent=parent;o.location=Vector(c)-parent.location
    if not flat:o.rotation_euler.x=math.pi/2
    curve.materials.append(m);bpy.context.view_layer.objects.active=o;o.select_set(True)
    bpy.ops.object.convert(target=enum(bpy.ops.object.convert.get_rna_type(),'target','MESH'));o.select_set(False)
    o['generatedFor']='US013-architecture';o['synthetic']=True
    return o

def remove_faces(obj,predicate):
    bm=bmesh.new();bm.from_mesh(obj.data)
    remove=[f for f in bm.faces if predicate([obj.matrix_world@v.co for v in f.verts])]
    if remove:bmesh.ops.delete(bm,geom=remove,context='FACES')
    bm.to_mesh(obj.data);bm.free();obj.data.update()

layout=roots['T-LAYOUT'];layout['viewMode']='both'
# Original architectural geometry is retained for provenance, outside the active design.
for code in ['T-DEMO','ROOF','INTERIOR-FIXTURES','BAGGAGE-CLAIM']:
    roots[code]['viewMode']='reference'
for obj in list(layout.children):
    if obj.type!='MESH':continue
    if obj.data.materials and obj.data.materials[0]==glass:bpy.data.objects.remove(obj,do_unlink=True);continue
    if obj.data.materials and obj.data.materials[0]==dark:bpy.data.objects.remove(obj,do_unlink=True);continue
    if obj.data.materials and obj.data.materials[0].name=='Architectural · polished marble':
        obj.data.materials[0]=floor_mat;uv(obj,floor_mat)
    if obj.data.materials and obj.data.materials[0].name=='Architectural · white plaster':obj.data.materials[0]=ivory
    if obj.data.materials and obj.data.materials[0]==dark:
        remove_faces(obj,lambda pts:all(F+.001<p.z<F+.02 for p in pts))
for obj in roots['CORRIDOR'].children:
    if obj.type=='MESH' and obj.data.materials and obj.data.materials[0].name=='Architectural · polished marble':
        obj.data.materials[0]=floor_mat;uv(obj,floor_mat)
for i,x in enumerate([-80,-66,-52,-38,-24],1):
    gate=roots[f'G-{i}']
    for obj in list(gate.children):
        if obj.type!='MESH' or not obj.data.materials:continue
        if obj.data.materials[0].name=='Architectural · white plaster':
            remove_faces(obj,lambda pts:max(p.z for p in pts)<F+1.1)
            obj.data.materials[0]=ivory
        elif obj.data.materials[0]==wood:
            remove_faces(obj,lambda pts:max(p.z for p in pts)<F+1.2 and min(abs(p.x-x) for p in pts)>6)
    # Low transparent fins define gates without enclosing the lounge in stone cells.
    for dx in [-6.3,6.3]:
        mesh(gate,'glass lounge fin',[(x+dx,46,F+.15),(x+dx,52.6,F+.15),(x+dx,52.6,F+1.4),(x+dx,46,F+1.4)],[(0,1,2,3)],glass)
        box(gate,'fin handrail',(x+dx,49.3,F+1.43),(.07,6.6,.05),steel)
    # Boarding pedestals with gates and card readers, at a physical human scale.
    for dx in [-1.0,1.0]:
        box(gate,'boarding pedestal',(x+dx,49.5,F+.55),(.35,.9,1.1),steel,.05)
        box(gate,'boarding card reader',(x+dx,49.45,F+1.16),(.28,.35,.12),dark,.035)
    mesh(gate,'boarding access wing',[(x-.8,49.5,F+.55),(x+.8,49.5,F+.55),(x+.8,49.5,F+1.15),(x-.8,49.5,F+1.15)],[(0,1,2,3)],glass)

# Arch-shaped ribs; roof surfaces share their construction geometry.
roof=bpy.data.objects.new('AS|terminal|ARCH-ROOF',None);terminal.objects.link(roof)
roof['viewMode']='immersive';roof['kind']='ceiling';roof['clickable']=False;roof['synthetic']=True
roof.hide_set(True);roof.hide_render=True
def arch_point(t,start,end):return start+(end-start)*t,F+4.2+4.3*math.sin(math.pi*t)
def rib(parent,fixed,start,end,along_x=False):
    verts=[];n=20
    for j in range(n+1):
        t=j/n;a,z=arch_point(t,start,end)
        tangent=Vector((end-start,4.3*math.pi*math.cos(math.pi*t)))
        normal=Vector((-tangent.y,tangent.x)).normalized()
        for off,side in [(-.17,-.18),(.17,-.18),(.17,.18),(-.17,.18)]:
            p=(a+normal.x*side,fixed+off,z+normal.y*side) if along_x else (fixed+off,a+normal.x*side,z+normal.y*side)
            verts.append(p)
    faces=[(0,3,2,1),(n*4,n*4+1,n*4+2,n*4+3)]
    faces += [(j*4+k,j*4+(k+1)%4,(j+1)*4+(k+1)%4,(j+1)*4+k) for j in range(n) for k in range(4)]
    mesh(parent,'vaulted structural rib',verts,faces,ivory)
    for a in [start,end]:
        c=(a,fixed,F+2.1) if along_x else (fixed,a,F+2.1)
        box(parent,'structural column',c,(.35,.35,4.2),ivory,.025)
for x in [-87,-73,-59,-45,-31,-17]:rib(layout,x,21,54)
for y in [4,18,32,46,60,72]:rib(layout,y,-17,24,True)
def vault_surface(parent,start,end,fixed0,fixed1,along_x=False):
    verts=[];n=24
    for j in range(n+1):
        a,z=arch_point(j/n,start,end)
        for f in [fixed0,fixed1]:verts.append((a,f,z+.22) if along_x else (f,a,z+.22))
    mesh(parent,'vaulted ceiling',verts,[(j*2,j*2+1,j*2+3,j*2+2) for j in range(n)],ivory,ignore=True)
vault_surface(roof,21,54,-87.5,-17)
vault_surface(roof,-17,24,3,73,True)
# Roof is visible in eye-level/exterior views and removed from the operational cutaway.
for obj in roof.children:obj.hide_set(True);obj.hide_render=True
for x in [-79,-65,-51,-37,-23]:
    box(layout,'concourse pendant light',(x,25.9,F+5.9),(10.8,.12,.1),led)
    for dx in [-4.8,4.8]:cylinder(layout,'pendant suspension',(x+dx,25.9,F+6.75),.012,1.7,steel,6)

# Continuous airside curtain wall, with clear glazing rather than opaque blue boxes.
for x in range(-87,-17,7):
    mesh(layout,'airside glazing',[(x,53.8,F),(x+7,53.8,F),(x+7,53.8,F+4.2),(x,53.8,F+4.2)],[(0,1,2,3)],glass)
    box(layout,'airside facade mullion',(x,53.8,F+2.1),(.09,.15,4.2),dark)
    box(layout,'airside facade transom',(x+3.5,53.8,F+2.1),(7,.1,.07),steel)
for x in range(-17,24,4):
    top0=arch_point((x+17)/41,-17,24)[1];top1=arch_point((min(x+4,24)+17)/41,-17,24)[1]
    mesh(layout,'main hall glazed gable',[(x,72.7,F),(min(x+4,24),72.7,F),(min(x+4,24),72.7,top1),(x,72.7,top0)],[(0,1,2,3)],glass)
    box(layout,'main hall facade mullion',(x,72.7,(top0+F)/2),(.09,.15,top0-F),dark)
for y in [10,20,56,66]:
    mesh(layout,'entrance glass curtain',[(23.8,y-4.3,F),(23.8,y+4.3,F),(23.8,y+4.3,F+4.2),(23.8,y-4.3,F+4.2)],[(0,1,2,3)],glass)
    box(layout,'entrance facade mullion',(23.8,y-4.4,F+2.1),(.13,.09,4.2),dark)

# The apron and five covered walking piers reinforce the actual boarding exits.
plane(layout,'airside apron',(-91,54,29,78),asphalt,-.03,True)
for i,x in enumerate([-80,-66,-52,-38,-24],1):
    gate=roots[f'G-{i}']
    box(gate,'covered boarding walkway',(x,58.7,F-.1),(3.2,9.5,.2),ivory)
    box(gate,'boarding walkway roof',(x,58.7,F+3.1),(3.5,9.8,.18),ivory,.025)
    for y in [55,58.5,62]:
        for dx in [-1.55,1.55]:box(gate,'walkway column',(x+dx,y,F+1.55),(.075,.075,3.1),steel)
    for dx in [-1.55,1.55]:
        mesh(gate,'walkway glazing',[(x+dx,54,F+.4),(x+dx,63.4,F+.4),(x+dx,63.4,F+2.9),(x+dx,54,F+2.9)],[(0,1,2,3)],glass)
    text(gate,f'G-{i}',(x,65.8,.015),.9,white,True)
    plane(gate,'apron access marking',(x-.08,63.5,x+.08,71),brass,.015)

# Information island and readable concourse landmark.
corridor=roots['CORRIDOR']
box(corridor,'flight information kiosk',(-61,24.1,F+1.7),(1.1,.7,3.4),dark,.08)
text(corridor,'i',(-61,23.73,F+2.65),.6,white)
text(corridor,'DEPARTURES',(-61,23.73,F+2.15),.1,white)
box(corridor,'information screen',(-61,23.73,F+1.45),(.85,.035,.95),shirt)

def traveller(parent,x,y,index):
    coat=shirt2 if index%3==0 else shirt
    ellipsoid(parent,'demo traveller torso',(x,y,F+1.13),(.19,.115,.31),coat)
    ellipsoid(parent,'demo traveller head',(x,y,F+1.64),(.12,.11,.15),skin)
    for dx in [-.105,.105]:
        cylinder(parent,'demo traveller leg',(x+dx,y,F+.47),.067,.73,trousers,8)
        box(parent,'demo traveller shoe',(x+dx,y-.045,F+.07),(.15,.27,.11),dark,.03)
    for dx in [-.235,.235]:cylinder(parent,'demo traveller arm',(x+dx,y,F+1.1),.055,.45,coat,8)
    if index%2==0:
        bx=x+.44
        box(parent,'carry-on luggage',(bx,y+.12,F+.33),(.34,.23,.55),bag,.05)
        for dx in [-.12,.12]:
            ellipsoid(parent,'luggage wheel',(bx+dx,y+.12,F+.055),(.045,.045,.045),dark)
            box(parent,'luggage handle',(bx+dx,y+.12,F+.73),(.016,.02,.35),steel)
        box(parent,'luggage handle grip',(bx,y+.12,F+.91),(.25,.025,.025),dark)
positions=[('G-1',-82,44),('G-1',-78,45),('G-2',-66,44),('G-3',-54,44),('G-3',-51,43),('G-4',-36,44),('G-5',-26,43),('CP-1',3,24),('CP-1',-4,24),('CP-2',3,36),('CP-2',-4,36),('CP-3',3,48),('CHECK-IN',-8,62),('CHECK-IN',0,62),('CHECK-IN',7,62),('CHECK-IN',15,62)]
for n,(code,x,y) in enumerate(positions):traveller(roots[code],x,y,n)

# Batch each owner/material/interaction class; make all refinements part of the actual GLB.
for parent in [layout,roof]+[roots[c] for c in ['CORRIDOR','CHECK-IN']+[f'G-{i}' for i in range(1,6)]+[f'CP-{i}' for i in range(1,4)]]:
    by={}
    for obj in parent.children:
        if obj.type=='MESH' and obj.get('generatedFor')=='US013-architecture':
            by.setdefault((obj.data.materials[0].name,bool(obj.get('raycastIgnore'))),[]).append(obj)
    for (name,ignore),objects in by.items():
        if len(objects)<2:continue
        for o in scene.objects:o.select_set(False)
        for o in objects:o.hide_set(False);o.select_set(True)
        bpy.context.view_layer.objects.active=objects[0];bpy.ops.object.join()
        objects[0].name=parent.name+'/'+name
        if parent==roof:objects[0].hide_set(True);objects[0].hide_render=True
for o in scene.objects:o.select_set(False)
bpy.context.view_layer.update()

# Same physical daylight map in Blender and in the application.
world=scene.world;nodes=world.node_tree.nodes;links=world.node_tree.links
bg=next(n for n in nodes if n.type=='BACKGROUND')
for link in list(bg.inputs['Color'].links):links.remove(link)
env=nodes.new('ShaderNodeTexEnvironment');env.image=bpy.data.images.load(os.path.join(OUT,'terminal-daylight.hdr'),check_existing=True)
links.new(env.outputs['Color'],bg.inputs['Color']);bg.inputs['Strength'].default_value=.5
for light in [o for o in scene.objects if o.type=='LIGHT']:light.data.energy=45000;light.data.size=70
scene.cycles.samples=48
scene.render.resolution_x=1800;scene.render.resolution_y=1200
scene.camera.location=(40,-82,100);scene.camera.rotation_euler=(Vector((-31,38,2))-scene.camera.location).to_track_quat('-Z','Y').to_euler();scene.camera.data.ortho_scale=144
scene.render.filepath=os.path.join(ROOT,'assets/blender/terminal-architecture-overview.png')

# Self-contained geometry and maps; the only separate runtime file is the 1 MiB daylight HDRI.
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
manifest_path=os.path.join(OUT,'manifest.json');manifest=json.load(open(manifest_path))
asset=manifest['assets']['terminal'];asset.update(bytes=os.path.getsize(os.path.join(OUT,'terminal.glb')),triangles=triangles,drawCallsUpperBound=calls)
assert asset['bytes']<manifest['performance']['maxAssetBytes'] and triangles<manifest['performance']['maxTriangles'],asset
manifest['version']=5
manifest['source']['geometry']='Original supplied architecture retained verbatim as reference; active demo terminal adapted to the main hall and west wing footprint'
manifest['operationalLayout']['shell']='Original architecture retained as hidden references; active fictional terminal has vaulted structure, glazing and project-generated equipment'
manifest['operationalLayout']['newGeometryLicense']='Original project additions follow project terms; external maps and fonts retain their documented licenses'
manifest['operationalLayout']['boundsBlenderMeters']=[[-91,2.925,-.03],[29,78,9.17]]
manifest['environment']=json.load(open(os.path.join(MAPS,'daylight-credit.json')))
manifest['architecturalUpgrade']={'version':1,'roofElevationMeters':9.17,'roofHeightAboveFloorMeters':8.72,'terrazzoTileMeters':1.8,'travellersDemo':16,
    'features':['vaulted steel structure','continuous airside glazing','open gate lounges','five covered boarding walkways','boarding card readers','carry-on luggage','human scale figures','daylight HDRI'],
    'originalArchitecture':'Retained verbatim as hidden references; active architecture follows the operational footprint',
    'roofVisibility':'Hidden for operational cutaway; visible for immersive and exterior views',
    'generatedMaps':['terminal-terrazzo.jpg','terminal-terrazzo-rough.png','terminal-terrazzo-normal.png','terminal-apron.jpg'],
    'generatedAssetLicense':'Original project additions; project terms apply'}
for e in manifest['entities']:
    if e['code'] in roots and roots[e['code']].get('viewMode'):e['viewMode']=roots[e['code']]['viewMode']
manifest['performance']['maxVisibleInteriorTriangles']=85000
assert sum(a['bytes'] for a in manifest['assets'].values())+manifest['environment']['bytes']<manifest['performance']['maxTotalBytes']
with open(manifest_path,'w') as f:json.dump(manifest,f,ensure_ascii=False,indent=2)
scene['architecturalUpgradeVersion']=1;scene.name='AeroSynapse · Terminal DEMO · Architecture'
bpy.data.orphans_purge(do_local_ids=True,do_linked_ids=False,do_recursive=True)
bpy.ops.file.pack_all();bpy.context.preferences.filepaths.save_version=0
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'assets/blender/aerosynapse-user-airport.blend'))
print('ARCHITECTURAL_TERMINAL',json.dumps(asset),flush=True)
