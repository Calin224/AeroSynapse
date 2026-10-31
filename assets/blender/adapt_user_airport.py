"""Adapt the supplied airport in place spatially; never generate a replacement terminal."""
import bpy, bmesh, json, math, os, sys, uuid, hashlib
from mathutils import Vector, Matrix
sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
from annotation_glyphs import GLYPHS

ROOT=os.path.abspath(os.path.join(os.path.dirname(__file__),'../..'))
OUT=os.path.join(ROOT,'AeroSynapse.Client/public/models/aerosynapse')
SOURCE=bpy.data.filepath
SOURCE_HASH=hashlib.sha256(open(SOURCE,'rb').read()).hexdigest()
source_scene=bpy.context.scene
source_meshes=[o for o in source_scene.objects if o.type=='MESH']
scene=bpy.data.scenes.new('AeroSynapse AI · aeroportul furnizat · DEMO')
bpy.context.window.scene=scene
scene.unit_settings.system=next(i.identifier for i in scene.unit_settings.bl_rna.properties['system'].enum_items if i.identifier=='METRIC')
scene.unit_settings.scale_length=1
scene['source']='User supplied airport; existing architecture retained'
scene['sourceSha256']=SOURCE_HASH
scene['synthetic']=True
SCENE_ID='60000000-0000-0000-0000-000000000001'
TERMINAL_ID='60000000-0000-0000-0000-000000000002'
NS=uuid.UUID(SCENE_ID)
DISCLAIMER='DEMO SYNTHETIC — modelul aeroportului furnizat de utilizator; nu reproduce exact Craiova. Pozițiile și mișcările de simulare sunt demonstrative.'
entities={};preserved=[];removed=[];optimization=[]
def enum(owner,prop,wanted):return next(i.identifier for i in owner.bl_rna.properties[prop].enum_items if i.identifier==wanted)
def sid(n):return f'60000000-0000-0000-0000-{n:012d}'
def collection(name):
    c=bpy.data.collections.new(name);scene.collection.children.link(c);return c
terminal=collection('01 · Terminal original + zone AeroSynapse')
exterior=collection('02 · Exterior original / LOD detail')
low=collection('03 · Exterior original / LOD low')
preview=collection('04 · Preview / not exported')
def entity(c,code,label,kind='terminal',zone_id=TERMINAL_ID,resource='terminal',origin=(0,0,0),sources=()):
    o=bpy.data.objects.new('AS|'+resource+'|'+code,None);c.objects.link(o);o.location=origin
    data=dict(entityId=str(uuid.uuid5(NS,'aerosynapse-user-airport/'+code)),code=code,label=label,
              zoneId=zone_id,zoneType=kind,sceneId=SCENE_ID,terminalId=TERMINAL_ID,
              resource=resource,synthetic=True,status='synthetic',clickable=True,
              baseHeight=float(origin[2]),sourceObjects=list(sources))
    for k,v in data.items():o[k]=v
    entities[code]={**data,'positionBlenderMeters':list(origin),'positionThreeMeters':[origin[0],origin[2],-origin[1]],
                    'databaseMapping':{'table':{'terminal':'terminals','checkpoint':'checkpoints','gate':'gates','scene':'scenes'}[kind],'id':zone_id}}
    return o
def bbox(obj):
    pts=[obj.matrix_world@Vector(v) for v in obj.bound_box]
    return Vector([min(v[i] for v in pts) for i in range(3)]),Vector([max(v[i] for v in pts) for i in range(3)])
def centre(obj):
    lo,hi=bbox(obj);return (lo+hi)/2
def keep_faces(data,indices):
    bm=bmesh.new();bm.from_mesh(data);bm.faces.ensure_lookup_table()
    bmesh.ops.delete(bm,geom=[f for f in bm.faces if f.index not in indices],context='FACES')
    bm.to_mesh(data);bm.free();data.update()
def simplify(obj,target):
    before=len(obj.data.polygons)
    if before<=target:return
    bm=bmesh.new();bm.from_mesh(obj.data)
    bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.000001)
    bm.to_mesh(obj.data);bm.free();obj.data.update()
    if len(obj.data.polygons)>target:
        mod=obj.modifiers.new('LOD · preserve supplied shape',enum(bpy.types.Modifier,'type','DECIMATE'))
        mod.decimate_type=enum(mod,'decimate_type','COLLAPSE')
        mod.ratio=max(.002,target/len(obj.data.polygons));mod.use_collapse_triangulate=True
        bpy.context.view_layer.update()
        graph=bpy.context.evaluated_depsgraph_get()
        data=bpy.data.meshes.new_from_object(obj.evaluated_get(graph),preserve_all_data_layers=True,depsgraph=graph)
        obj.modifiers.clear();old=obj.data;obj.data=data
        if not old.users:bpy.data.meshes.remove(old)
    optimization.append(dict(source=obj.get('sourceObject',obj.name),before=before,after=len(obj.data.polygons)))
    obj.data.validate()
    obj.data.update()
def adopt(src,parent,target=None,indices=None):
    data=src.data.copy()
    if indices is not None:keep_faces(data,indices)
    transform=Matrix.Translation(-Vector(parent.location))@src.matrix_world
    data.transform(transform)
    if transform.determinant()<0:data.flip_normals()
    data.update()
    obj=bpy.data.objects.new(parent.name+'/'+src.name,data);parent.users_collection[0].objects.link(obj);obj.parent=parent
    obj['sourceObject']=src.name;obj['synthetic']=True
    if target:simplify(obj,target)
    else:preserved.append(dict(source=src.name,faces=len(data.polygons),bboxBlenderMeters=[list(v) for v in bbox(src)]))
    return obj
def flat_material(name,color,alpha=1):
    m=bpy.data.materials.new(name);m.use_nodes=True;m.diffuse_color=(*color,alpha)
    p=next(n for n in m.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    p.inputs['Base Color'].default_value=(*color,1);p.inputs['Alpha'].default_value=alpha
    p.inputs['Roughness'].default_value=.8
    if alpha<1:m.surface_render_method=enum(m,'surface_render_method','BLENDED')
    return m
white=flat_material('AeroSynapse · annotation white',(.9,.96,1))
gold=flat_material('AeroSynapse · gate marker',(.95,.57,.08))
cyan=flat_material('AeroSynapse · control marker',(.02,.7,.78))
dark=flat_material('AeroSynapse · annotation board',(.02,.045,.06))
apron_material=flat_material('AeroSynapse · demonstrative apron',(.11,.16,.20))
def new_mesh(parent,name,verts,faces,mat):
    data=bpy.data.meshes.new(name);data.from_pydata(verts,[],faces);data.update();data.materials.append(mat)
    obj=bpy.data.objects.new(parent.name+'/'+name,data);parent.users_collection[0].objects.link(obj);obj.parent=parent;obj['synthetic']=True
    return obj
def annotation(parent,text,world_origin,height=.7,color=gold):
    size=height/7;total=(6*len(text)-1)*size;verts=[];faces=[]
    for j,ch in enumerate(text):
        for row,bits in enumerate(GLYPHS[ch]):
            for col,b in enumerate(bits):
                if b!='1':continue
                y=j*6*size+col*size-total/2;z=(6-row)*size
                n=len(verts);verts.extend([(0,y,z),(0,y+size*.91,z),(0,y+size*.91,z+size*.91),(0,y,z+size*.91)]);faces.append((n,n+1,n+2,n+3))
    o=new_mesh(parent,'metadata label '+text,verts,faces,color);o.location=Vector(world_origin)-parent.location
    board=new_mesh(parent,'label backing',[(0,-total/2-.16,-.12),(0,total/2+.16,-.12),(0,total/2+.16,height+.12),(0,-total/2-.16,height+.12)],[(0,1,2,3)],dark)
    board.location=o.location+Vector((-.01,0,0));o['annotationOnly']=True;board['annotationOnly']=True
def proxy(parent,world_vertices,color):
    return new_mesh(parent,'clickable zone overlay',[tuple(Vector(v)-parent.location) for v in world_vertices],[(0,1,2,3)],color)

# Source architecture: all four modules and the existing separated roofs remain exact.
architecture=entity(terminal,'T-DEMO','Terminalul aeroportului furnizat',sources=[o.name for o in source_meshes if o.name.startswith('07.Modulo') and 'Transparent Roof' not in o.name])
for src in source_meshes:
    if src.name.startswith('07.Modulo') and 'Transparent Roof' not in src.name:
        adopt(src,architecture)
roof_root=entity(terminal,'ROOF','Acoperis original transparent',sources=[o.name for o in source_meshes if 'Transparent Roof' in o.name])
roof_root['clickable']=False;roof_root['kind']='roof';entities.pop('ROOF')
roof_material=bpy.data.materials['AIRPORT'].copy();roof_material.name='Original airport roof · 8% opacity'
p=next(n for n in roof_material.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
for link in list(p.inputs['Alpha'].links):roof_material.node_tree.links.remove(link)
p.inputs['Alpha'].default_value=.08
output=next(n for n in roof_material.node_tree.nodes if n.type=='OUTPUT_MATERIAL' and n.is_active_output)
roof_material.node_tree.links.new(p.outputs['BSDF'],output.inputs['Surface'])
roof_material.surface_render_method=enum(roof_material,'surface_render_method','BLENDED')
roof_material.diffuse_color=(*roof_material.diffuse_color[:3],.08)
for src in source_meshes:
    if 'Transparent Roof' in src.name:
        obj=adopt(src,roof_root);obj.data.materials.clear();obj.data.materials.append(roof_material)
        for face in obj.data.polygons:face.material_index=0

# Existing security equipment is partitioned semantically, without moving it.
security=next(o for o in source_meshes if o.name=='09.SecurityCheck_AIRPORT_0')
world=[security.matrix_world@v.co for v in security.data.vertices]
centres_y=(41.9,48.7,55.6)
parts=[set() for _ in range(3)]
for face in security.data.polygons:
    y=sum(world[i].y for i in face.vertices)/len(face.vertices)
    parts[min(range(3),key=lambda i:abs(y-centres_y[i]))].add(face.index)
assert sum(map(len,parts))==len(security.data.polygons)
for n,(y,part) in enumerate(zip(centres_y,parts),1):
    cp=entity(terminal,f'CP-{n}',f'Control securitate {n} · zona existenta','checkpoint',sid(100+n),origin=(-.96,y,2.23),sources=[security.name])
    adopt(security,cp,1600,part)
    annotation(cp,f'CP-{n}',(2.1,y,8.1),.72,cyan)
    # Annotation above the existing mezzanine; the physical scanners stay below it.
    proxy(cp,[(1.8,y-2.7,5.72),(2.05,y-2.7,5.72),(2.05,y+2.7,5.72),(1.8,y+2.7,5.72)],cyan)
gates=sorted([o for o in source_meshes if o.name.startswith('11.BoardingAreaDesk')],key=lambda o:o.name)
assert len(gates)==5
for n,src in enumerate(gates,1):
    origin=centre(src);gate=entity(terminal,f'G-{n}',f'Poarta G-{n} · birou existent','gate',sid(200+n),origin=origin,sources=[src.name])
    adopt(src,gate,1700);annotation(gate,f'G-{n}',(origin.x+1.15,origin.y,9.45),.6,gold)

checkin_src=next(o for o in source_meshes if o.name=='08.CheckInDesk_AIRPORT_0')
checkin=entity(terminal,'CHECK-IN','Check-in existent',origin=centre(checkin_src),sources=[checkin_src.name]);adopt(checkin_src,checkin,5000)
entry=entity(terminal,'ENTRY','Intrarea terminalului existent',origin=(17.2,37.57,.5))
proxy(entry,[(16.95,32.5,.46),(19,32.5,.46),(19,42.5,.46),(16.95,42.5,.46)],cyan)
corridor=entity(terminal,'CORRIDOR','Circulatie in terminalul existent',origin=(7.5,37.57,.5))
proxy(corridor,[(7.3,8,.46),(7.5,8,.46),(7.5,68,.46),(7.3,68,.46)],cyan)
baggage=entity(terminal,'BAGGAGE-CLAIM','Benzi de bagaje existente',resource='baggage')
fixtures=entity(terminal,'INTERIOR-FIXTURES','Echipamente si mobilier aeroportuar existente')
aircraft_sources=[]
for src in source_meshes:
    name=src.name
    if name.startswith('07.Modulo') or 'Transparent Roof' in name or name.startswith(('08.','09.','11.')):continue
    if name=='Cube' or name.startswith(('13.Cafe','15.Advertising','17.10.')):
        removed.append(name);continue
    if name.startswith('12.'):
        adopt(src,baggage,900);continue
    if name.startswith(('10.Chairs','14.','16.','17.')):
        target=700 if name.startswith('10.Chairs') else (650 if name.startswith('16.') else 300)
        adopt(src,fixtures,target);continue
    if name.startswith('18.'):
        aircraft_sources.append(src);continue
    if name.startswith('LandingTrack'):
        root=entity(exterior,'RUNWAY','Pista originala · context optional','scene',SCENE_ID,'runway',centre(src),[name]);root['optional']=True
        adopt(src,root);continue
    resource='baggage' if name.startswith(('21.','05.')) else 'vehicles' if name.startswith(('03.','19.','20.')) else 'infrastructure'
    code='SRC-'+name.split('_AIRPORT')[0]
    root=entity(exterior,code,'Context aeroportuar existent · '+name.split('_AIRPORT')[0],'scene',SCENE_ID,resource,centre(src),[name])
    # Tower, hangar and boarding bridge retain their exact source geometry.
    target=None if name.startswith(('02.','04.','06.')) else 1300
    adopt(src,root,target)
for n,src in enumerate(sorted(aircraft_sources,key=lambda o:o.name),1):
    root=entity(exterior,f'AC-{n:02}',f'Aeronava originala {n}','scene',SCENE_ID,'aircraft',centre(src),[src.name])
    root['flightCode']=('AS 201','AS 412','AS 305','AS 508')[n-1]
    entities[f'AC-{n:02}']['flightCode']=root['flightCode']
    adopt(src,root,11000)

# The only new ground geometry is a demonstrative apron and five stand markings.
apron=entity(exterior,'APRON','Platforma demonstrativa peste amplasamentul modelului','scene',SCENE_ID,'apron')
new_mesh(apron,'apron',[(-170,-150,-.06),(32,-150,-.06),(32,180,-.06),(-170,180,-.06)],[(0,1,2,3)],apron_material)
stands=[(-49,-13),(-47,91),(-65,-95),(-95,50),(-95,-20)]
for n,(x,y) in enumerate(stands,1):
    stand=entity(exterior,f'STAND-{n}',f'Stand demonstrativ {n}','scene',SCENE_ID,'stands',(x,y,0))
    stand['gateId']=sid(200+n);entities[f'STAND-{n}']['gateId']=sid(200+n)
    for off in (-16,16):proxy(stand,[(x+off-.05,y-25,.02),(x+off+.05,y-25,.02),(x+off+.05,y+25,.02),(x+off-.05,y+25,.02)],gold)
    proxy(stand,[(x-.07,y-25,.02),(x+.07,y-25,.02),(x+.07,y+25,.02),(x-.07,y+25,.02)],white)

# Duplicate the supplied exterior for a cheaper LOD; IDs, origins and orientations match.
bpy.context.view_layer.update()
for root in [o for o in exterior.objects if o.type=='EMPTY']:
    clone=bpy.data.objects.new(root.name+'|LOD-low',None);low.objects.link(clone);clone.location=root.location.copy()
    for k,v in root.items():clone[k]=v
    clone['lod']='low';root['lod']='detail'
    for original in root.children:
        if original.type!='MESH':continue
        obj=bpy.data.objects.new(original.name+'|LOD-low',original.data.copy());low.objects.link(obj);obj.parent=clone
        for k,v in original.items():obj[k]=v
        if not str(original.get('sourceObject','')).startswith(('02.','04.','06.')):
            simplify(obj,1400 if root.get('resource')=='aircraft' else 700)

def export(c,filename):
    for o in scene.objects:o.select_set(False)
    for o in c.objects:o.select_set(True)
    r=bpy.ops.export_scene.gltf.get_rna_type()
    bpy.ops.export_scene.gltf(filepath=os.path.join(OUT,filename),export_format='GLB',use_selection=True,
        export_yup=True,export_extras=True,export_apply=True,export_animations=False,
        export_cameras=False,export_lights=False,export_materials='EXPORT',
        export_meshopt_compression_enable=True,
        export_meshopt_extension=next(i.identifier for i in r.properties['export_meshopt_extension'].enum_items if i.identifier=='EXT_meshopt_compression'))
    triangles=0;calls=0
    for o in c.objects:
        if o.type=='MESH':o.data.calc_loop_triangles();triangles+=len(o.data.loop_triangles);calls+=max(1,len(o.data.materials))
    return dict(url='models/aerosynapse/'+filename,bytes=os.path.getsize(os.path.join(OUT,filename)),triangles=triangles,drawCallsUpperBound=calls)
os.makedirs(OUT,exist_ok=True)
assets={'terminal':export(terminal,'terminal.glb'),'exteriorLow':export(low,'exterior-low.glb'),'exteriorDetail':export(exterior,'exterior-detail.glb')}
terminal_sources=[o for o in source_meshes if o.name.startswith('07.Modulo')]
lo=Vector([min(bbox(o)[0][i] for o in terminal_sources) for i in range(3)])
hi=Vector([max(bbox(o)[1][i] for o in terminal_sources) for i in range(3)])
manifest=dict(version=2,model='user-supplied-airport',sceneId=SCENE_ID,terminalId=TERMINAL_ID,synthetic=True,status='synthetic',disclaimer=DISCLAIMER,
    license='User supplied source; original license not identified. No CC0 reassignment.',units='meters',blenderUp='Z',gltfUp='Y',
    transform='Three.js (x,y,z) = Blender (x,z,-y); original world coordinates and scale retained',
    terminalDimensionsMeters={'width':hi.x-lo.x,'depth':hi.y-lo.y,'roofHeight':hi.z-lo.z},
    terminalBoundsBlenderMeters=[list(lo),list(hi)],
    source={'file':os.path.basename(SOURCE),'sha256':SOURCE_HASH,'sourceLicense':'unverified','geometry':'Existing supplied airport, not a procedural replacement'},
    preservedGeometry=preserved,removedObjects=removed,optimization=optimization,
    performance={'maxAssetBytes':8000000,'maxTotalBytes':14000000,'maxTriangles':180000,'pixelRatioCap':1.5,'lodDistanceMeters':260,'exteriorLazy':True,'detailsLazy':True},
    assets=assets,entities=list(entities.values()),compression='EXT_meshopt_compression',
    simulationIsolation='Exterior visibility and selection never modify terminal simulation inputs.')
assert all(a['bytes']<8000000 and a['triangles']<180000 for a in assets.values()),assets
assert sum(a['bytes'] for a in assets.values())<14000000
with open(os.path.join(OUT,'manifest.json'),'w') as f:json.dump(manifest,f,ensure_ascii=False,indent=2)

# Remove the old imported scene only from this generated file, never from its source file.
bpy.data.scenes.remove(source_scene)
bpy.data.orphans_purge(do_local_ids=True,do_linked_ids=False,do_recursive=True)
for o in low.objects:o.hide_render=True;o.hide_set(True)
for o in scene.objects:o.select_set(False)
camera_data=bpy.data.cameras.new('User airport · Preview')
camera=bpy.data.objects.new(camera_data.name,camera_data);preview.objects.link(camera)
camera.location=(175,-200,205);camera.rotation_euler=(Vector((-47,25,15))-camera.location).to_track_quat('-Z','Y').to_euler()
camera_data.type=enum(camera_data,'type','ORTHO');camera_data.ortho_scale=340;camera_data.clip_end=2500;scene.camera=camera
for name,location,power,size in [('Key',(60,-120,230),240000,160),('Fill',(-160,120,180),240000,150)]:
    d=bpy.data.lights.new(name,type=enum(bpy.types.Light,'type','AREA'));d.energy=power;d.size=size
    o=bpy.data.objects.new(name,d);preview.objects.link(o);o.location=location;o.rotation_euler=(Vector((-20,30,5))-o.location).to_track_quat('-Z','Y').to_euler()
world=bpy.data.worlds.new('User airport · Preview world');world.use_nodes=True;scene.world=world
background=next(n for n in world.node_tree.nodes if n.type=='BACKGROUND')
background.inputs['Color'].default_value=(.07,.10,.14,1);background.inputs['Strength'].default_value=.7
try:scene.render.engine='CYCLES'
except TypeError:pass
scene.cycles.samples=32;scene.cycles.use_denoising=True;scene.cycles.transparent_max_bounces=24
scene.render.resolution_x=1500;scene.render.resolution_y=1200;scene.render.resolution_percentage=100
scene.render.filepath=os.path.join(os.path.dirname(__file__),'user-airport-preview.png')
scene['disclaimer']=DISCLAIMER
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='CONSOLE':area.type=enum(area,'type','VIEW_3D')
        for space in area.spaces:
            if space.type=='VIEW_3D':
                space.shading.type=enum(space.shading,'type','MATERIAL');space.overlay.show_overlays=False
                space.region_3d.view_perspective=enum(space.region_3d,'view_perspective','CAMERA')
bpy.context.preferences.filepaths.save_version=0
bpy.ops.file.pack_all()
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(os.path.dirname(__file__),'aerosynapse-user-airport.blend'))
print('USER_AIRPORT_ASSETS',json.dumps(assets),flush=True)
print('PRESERVED_ARCHITECTURE',sum(x['faces'] for x in preserved if x['source'].startswith('07.Modulo')),flush=True)
bpy.ops.render.render(write_still=True)
