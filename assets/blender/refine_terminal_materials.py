"""Textured architectural cutaway of the terminal. Run on the operational layout once."""
import bpy, json, math, os
from mathutils import Vector

ROOT=os.path.abspath(os.path.join(os.path.dirname(__file__),'../..'))
MAPS=os.path.join(os.path.dirname(__file__),'materials')
OUT=os.path.join(ROOT,'AeroSynapse.Client/public/models/aerosynapse')
scene=bpy.context.scene
assert scene.get('operationalLayoutVersion')==1
assert not scene.get('materialRefinementVersion'), 'Regenerate from the before-material-refinement backup.'
terminal=next(c for c in scene.collection.children if c.name.startswith('01'))
roots={o.get('code'):o for o in terminal.objects if o.type=='EMPTY'}
F=.45

def enum(owner,prop,wanted):
    properties=owner.properties if hasattr(owner,'properties') else owner.bl_rna.properties
    return next(i.identifier for i in properties[prop].enum_items if i.identifier==wanted)

def material(name,color,metal=0,rough=.55,alpha=1,emission=0):
    m=bpy.data.materials.new('Architectural · '+name);m.use_nodes=True
    m.diffuse_color=(*color,alpha);m.use_backface_culling=False
    p=next(n for n in m.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    p.inputs['Base Color'].default_value=(*color,1)
    p.inputs['Metallic'].default_value=metal;p.inputs['Roughness'].default_value=rough
    p.inputs['Alpha'].default_value=alpha
    p.inputs['Emission Color'].default_value=(*color,1);p.inputs['Emission Strength'].default_value=emission
    if alpha<1:m.surface_render_method=enum(m,'surface_render_method','BLENDED')
    m['tileMeters']=1
    return m

def image_node(m,file,color=True):
    n=m.node_tree.nodes.new('ShaderNodeTexImage')
    n.image=bpy.data.images.load(os.path.join(MAPS,file),check_existing=True)
    if not color:n.image.colorspace_settings.name='Non-Color'
    return n

def pbr(name,id,tile=1.5,color=(1,1,1),diffuse=True):
    m=material(name,color);p=next(n for n in m.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    if diffuse:
        n=image_node(m,id+'_Diffuse.jpg');m.node_tree.links.new(n.outputs['Color'],p.inputs['Base Color'])
    arm=image_node(m,id+'_arm.jpg',False)
    separate=m.node_tree.nodes.new('ShaderNodeSeparateColor')
    m.node_tree.links.new(arm.outputs['Color'],separate.inputs['Color'])
    m.node_tree.links.new(separate.outputs['Green'],p.inputs['Roughness'])
    m.node_tree.links.new(separate.outputs['Blue'],p.inputs['Metallic'])
    normal=image_node(m,id+'_nor_gl.jpg',False)
    n=m.node_tree.nodes.new('ShaderNodeNormalMap');n.inputs['Strength'].default_value=.45
    m.node_tree.links.new(normal.outputs['Color'],n.inputs['Color'])
    m.node_tree.links.new(n.outputs['Normal'],p.inputs['Normal'])
    m['tileMeters']=tile;m['license']='CC0-1.0';m['sourceUrl']='https://polyhaven.com/a/'+id
    return m

stone=pbr('polished marble','marble_01',1.5)
wood=pbr('natural oak veneer','oak_veneer_01',1.83)
plaster=pbr('white plaster','white_plaster_02',1)
leather=pbr('navy leather upholstery','fabric_leather_02',.5,(.024,.045,.075),False)
carpet=material('charcoal woven carpet',(.045,.07,.09),rough=.95)
carpet_map=image_node(carpet,'terminal-carpet.jpg')
carpet.node_tree.links.new(carpet_map.outputs['Color'],next(n for n in carpet.node_tree.nodes if n.type=='BSDF_PRINCIPLED').inputs['Base Color'])
carpet['tileMeters']=.7
metal=material('brushed stainless steel',(.48,.55,.6),.86,.28)
dark=material('dark aluminium frames',(.025,.035,.045),.76,.32)
white=material('white signage',(.88,.93,.96),rough=.4,emission=.08)
amber=material('champagne brass',(.63,.36,.105),.72,.28)
turquoise=material('security accent',(.025,.27,.32),.22,.36)
rubber=material('black rubber conveyor',(.02,.025,.03),rough=.85)
glass=material('blue architectural glazing',(.43,.66,.76),.12,.12,.26)
led=material('warm linear lighting',(.97,.79,.53),rough=.2,emission=3)
leaves=material('planter foliage',(.025,.16,.065),rough=.75)
soil=material('planter soil',(.04,.022,.012),rough=1)
screen=material('airport digital displays',(.85,.9,1),rough=.3,emission=.55)
screen_map=image_node(screen,'terminal-display-atlas.png')
principled=next(n for n in screen.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
screen.node_tree.links.new(screen_map.outputs['Color'],principled.inputs['Base Color'])
screen.node_tree.links.new(screen_map.outputs['Color'],principled.inputs['Emission Color'])
font=bpy.data.fonts.load(os.path.join(MAPS,'Lato-Bold.ttf'))

def uv_project(obj,material):
    layer=obj.data.uv_layers.new(name='UVMap') if not obj.data.uv_layers else obj.data.uv_layers[0]
    tile=material.get('tileMeters',1)
    for face in obj.data.polygons:
        axis=max(range(3),key=lambda i:abs(face.normal[i]))
        axes=(1,2) if axis==0 else (0,2) if axis==1 else (0,1)
        for loop in face.loop_indices:
            v=obj.data.vertices[obj.data.loops[loop].vertex_index].co+obj.parent.location
            layer.data[loop].uv=(v[axes[0]]/tile,v[axes[1]]/tile)

def mesh(parent,name,verts,faces,mat,bevel=0):
    data=bpy.data.meshes.new(name)
    data.from_pydata([tuple(Vector(v)-parent.location) for v in verts],[],faces);data.update()
    data.materials.append(mat)
    obj=bpy.data.objects.new(parent.name+'/'+name,data);terminal.objects.link(obj);obj.parent=parent
    obj['synthetic']=True;obj['generatedFor']='US013-material-refinement'
    if mat==glass:obj['raycastIgnore']=True
    if bevel:
        bpy.context.view_layer.objects.active=obj;obj.select_set(True)
        mod=obj.modifiers.new('manufactured edge radius',enum(bpy.types.Modifier,'type','BEVEL'))
        mod.width=bevel;mod.segments=1
        bpy.ops.object.modifier_apply(modifier=mod.name)
        for f in obj.data.polygons:f.use_smooth=max(abs(v) for v in f.normal)<.999
        obj.select_set(False)
    uv_project(obj,mat)
    return obj

def box(parent,name,c,size,mat,bevel=0):
    x,y,z=c;a,b,d=[v/2 for v in size]
    return mesh(parent,name,[(x-a,y-b,z-d),(x+a,y-b,z-d),(x+a,y+b,z-d),(x-a,y+b,z-d),
        (x-a,y-b,z+d),(x+a,y-b,z+d),(x+a,y+b,z+d),(x-a,y+b,z+d)],
        [(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)],mat,bevel)

def floor(parent,name,b,mat,z=F+.022):
    x0,y0,x1,y1=b
    return mesh(parent,name,[(x0,y0,z),(x1,y0,z),(x1,y1,z),(x0,y1,z)],[(0,1,2,3)],mat)

def cylinder(parent,name,c,r,height,mat,segments=12):
    x,y,z=c;v=[(x+math.cos(i*math.tau/segments)*r,y+math.sin(i*math.tau/segments)*r,z+off) for off in [-height/2,height/2] for i in range(segments)]
    faces=[tuple(reversed(range(segments))),tuple(range(segments,segments*2))]
    faces += [(i,(i+1)%segments,(i+1)%segments+segments,i+segments) for i in range(segments)]
    obj=mesh(parent,name,v,faces,mat)
    for f in list(obj.data.polygons)[2:]:f.use_smooth=True
    return obj

def text(parent,body,c,size,mat=white,floor_text=False):
    curve_type=next(i.identifier for i in bpy.data.curves.bl_rna.functions['new'].parameters['type'].enum_items if i.identifier=='FONT')
    curve=bpy.data.curves.new(body,curve_type)
    curve.body=body;curve.font=font;curve.size=size;curve.align_x=enum(curve,'align_x','CENTER')
    curve.resolution_u=2;curve.extrude=0
    obj=bpy.data.objects.new(parent.name+'/'+body,curve);terminal.objects.link(obj);obj.parent=parent
    obj.location=Vector(c)-parent.location
    if not floor_text:obj.rotation_euler.x=math.pi/2
    curve.materials.append(mat)
    bpy.context.view_layer.objects.active=obj;obj.select_set(True)
    bpy.ops.object.convert(target=enum(bpy.ops.object.convert.get_rna_type(),'target','MESH'))
    obj.select_set(False);obj['generatedFor']='US013-material-refinement';obj['synthetic']=True
    return obj

def display(parent,c,width,height,row):
    x,y,z=c
    box(parent,'digital display housing',c,(width+.2,.18,height+.16),dark,.04)
    obj=mesh(parent,'digital display screen',[(x-width/2,y-.101,z-height/2),(x+width/2,y-.101,z-height/2),
        (x+width/2,y-.101,z+height/2),(x-width/2,y-.101,z+height/2)],[(0,1,2,3)],screen)
    v0=1-(row+1)/8;v1=1-row/8
    for loop,uv in zip(obj.data.polygons[0].loop_indices,[(0,v0),(1,v0),(1,v1),(0,v1)]):obj.data.uv_layers[0].data[loop].uv=uv

def planter(parent,x,y):
    cylinder(parent,'architectural planter',(x,y,F+.43),.55,.86,plaster,16)
    cylinder(parent,'planter soil',(x,y,F+.89),.47,.06,soil)
    cylinder(parent,'plant trunk',(x,y,F+1.6),.055,1.5,wood,8)
    for j in range(12):
        a=j*math.tau/12;length=1.25 if j%2 else .95;v=[]
        for k in range(6):
            t=k/5;r=length*t;z=F+2.6+.5*math.sin(t*math.pi)-.65*t
            width=.17*math.sin(t*math.pi)
            v.extend([(x+math.cos(a)*r+math.sin(a)*width,y+math.sin(a)*r-math.cos(a)*width,z),
                      (x+math.cos(a)*r-math.sin(a)*width,y+math.sin(a)*r+math.cos(a)*width,z)])
        mesh(parent,'broad leaf',v,[(k*2,k*2+1,k*2+3,k*2+2) for k in range(5)],leaves)

# Replace schematic blocks while retaining supplied equipment as hidden references.
codes=['T-LAYOUT','ENTRY','CHECK-IN','CORRIDOR']+[f'G-{i}' for i in range(1,6)]+[f'CP-{i}' for i in range(1,4)]
for code in codes:
    for obj in list(roots[code].children):
        if obj.get('generatedFor')=='US-013':bpy.data.objects.remove(obj,do_unlink=True)
        elif obj.get('sourceObject'):
            obj['viewMode']='reference';obj.hide_set(True);obj.hide_render=True

layout=roots['T-LAYOUT']
box(layout,'main marble floor',(3.25,38,F-.18),(41.5,70,.36),stone)
box(layout,'west wing marble floor',(-52.5,37.5,F-.18),(70,33,.36),stone)
# Construction joints and brass trim provide a physically scaled architectural rhythm.
for x in range(-84,24,4):
    if x < -16:b=(x-.012,21,x+.012,54)
    else:b=(x-.012,3,x+.012,73)
    floor(layout,'stone expansion joint',b,dark,F+.005)
for y in range(5,73,4):
    floor(layout,'stone expansion joint',(-17,y-.012,24,y+.012),dark,F+.006)
for c,s in [((3.25,3.1,F+.5),(41.5,.35,1)),((-87.3,37.5,F+.5),(.35,33,1)),((-52.5,21.1,F+.5),(70,.35,1))]:
    box(layout,'cutaway concrete wall',c,s,plaster,.04)
    box(layout,'brass wall cap',(c[0],c[1],F+1.04),(s[0],s[1],.07),amber,.02)
# Rear curtain wall; narrow overhead ribbons keep the working floor unobstructed.
for x in range(-87,-16,7):
    box(layout,'curtain wall mullion',(x,53.75,F+2.55),(.14,.18,5.1),dark)
for x in range(-84,-16,7):
    box(layout,'rear facade glass',(x,53.75,F+2.55),(6.75,.035,4.9),glass)
box(layout,'rear structural beam',(-52.5,53.7,F+5.15),(70,.5,.45),plaster,.08)
for x in range(-14,25,6):
    box(layout,'main hall facade glass',(x,72.6,F+2.6),(5.8,.035,5.2),glass)
    box(layout,'main hall facade mullion',(x-3,72.6,F+2.6),(.12,.18,5.2),dark)
box(layout,'main hall upper frame',(3.25,72.6,F+5.25),(41.5,.4,.3),metal,.04)
for y in [10,20,56,66]:
    box(layout,'entrance facade panel',(23.8,y,F+2.45),(.035,8.6,4.9),glass)
    box(layout,'entrance facade post',(23.8,y-4.4,F+2.45),(.18,.16,4.9),dark)
text(layout,'AEROSYNAPSE  /  TERMINAL DEMO',(-53,21.3,F+.55),.7,white)
for x,y in [(-85,50),(-18,50),(21,69),(20,8)]:planter(layout,x,y)

corridor=roots['CORRIDOR']
floor(corridor,'concourse inset',(-86,22,-8,31.8),stone)
floor(corridor,'brass concourse strip',(-85,26.84,-8,26.94),amber,F+.035)
text(corridor,'BOARDING  G-1 - G-5',(-48,24,F+.048),.95,white,True)
for x in [-77,-63,-49,-35,-21]:
    mesh(corridor,'direction arrow',[(x+.7,26.4,F+.05),(x-.7,26.9,F+.05),(x+.7,27.4,F+.05)],[(0,1,2)],amber)

entry=roots['ENTRY']
floor(entry,'entrance carpet',(17,32,24,44),carpet)
text(entry,'ENTRANCE',(20.5,34,F+.045),.68,white,True)
for y in [31,45]:box(entry,'entry door surround',(23.5,y,F+2.25),(.5,.5,4.5),metal,.06)
box(entry,'entry lintel',(23.5,38,F+4.55),(.5,14.5,.4),plaster,.05)
for y in [35.5,40.5]:box(entry,'entry sliding glass door',(23.6,y,F+1.7),(.03,4.8,3.4),glass)

checkin=roots['CHECK-IN']
floor(checkin,'check-in carpet',(-15,56,22,70.7),carpet)
box(checkin,'oak backdrop',(3.5,70.7,F+1.7),(37,.24,3.4),wood,.06)
box(checkin,'check-in canopy',(3.5,69,F+4.55),(38,3.8,.24),plaster,.04)
box(checkin,'check-in light',(3.5,67.6,F+4.38),(35,.08,.08),led)
text(checkin,'CHECK-IN',(3.5,70.54,F+2.5),1.2,white)
box(checkin,'check-in header',(3.5,66.9,F+5.5),(15,.2,1.5),dark,.03)
text(checkin,'CHECK-IN',(3.5,66.78,F+5.1),1.2,white)
for n,x in enumerate([-10,-6,-2,2,6,10,14,18],1):
    box(checkin,'oak check-in counter',(x,65,F+.6),(3.6,1.5,1.2),wood,.14)
    box(checkin,'counter marble top',(x,65,F+1.26),(3.75,1.64,.12),stone,.05)
    box(checkin,'counter toe kick',(x,64.25,F+.13),(3.3,.04,.22),dark)
    box(checkin,'staff monitor',(x,65.5,F+1.75),(.9,.08,.58),dark,.035)
    display(checkin,(x,68.9,F+3.3),2.5,.3125,(n-1)%5)
    text(checkin,f'{n:02}',(x,64.23,F+.68),.4,white)
for x in [-10,-3,4,11,18]:
    for y in [58.5,61.5]:
        cylinder(checkin,'queue stanchion',(x,y,F+.53),.055,1.06,metal)
        cylinder(checkin,'stanchion base',(x,y,F+.04),.23,.08,metal)
    box(checkin,'queue belt',(x,60,F+.95),(.03,3,.09),dark)

for i,y in enumerate([27,39,51],1):
    cp=roots[f'CP-{i}']
    floor(cp,'security lane mat',(-6.8,y-4.3,4.8,y+4.3),carpet)
    floor(cp,'security accent border',(-7,y-4.5,5,y-4.34),turquoise,F+.035)
    # Scanner with a conveyor and recognisable walk-through metal detector.
    box(cp,'X-ray scanner body',(-1,y+1.5,F+1.25),(3.2,2.0,2.5),plaster,.12)
    box(cp,'scanner tunnel',(-1,y+.475,F+1.32),(2.7,.04,1.3),rubber,.04)
    box(cp,'conveyor chassis',(-1,y-.8,F+.75),(3.1,4.6,.45),metal,.08)
    box(cp,'conveyor belt',(-1,y-.8,F+1.01),(2.6,4.45,.1),rubber,.025)
    for yy in [y-2.8,y+1.15]:
        for xx in [-2,0]:box(cp,'conveyor legs',(xx,yy,F+.42),(.12,.14,.84),metal)
    for xx in [2.3,4.1]:box(cp,'metal detector column',(xx,y,F+1.38),(.24,.42,2.76),metal,.055)
    box(cp,'metal detector lintel',(3.2,y,F+2.83),(2.05,.42,.27),turquoise,.045)
    display(cp,(-.8,y+3.75,F+4.3),6.4,.8,4+i)
    for xx in [-5.8,4.4]:
        for yy in [y-3.5,y+3.5]:cylinder(cp,'security queue post',(xx,yy,F+.48),.05,.96,metal)
        box(cp,'security queue rail',(xx,y,F+.9),(.04,7,.09),dark)
    text(cp,f'CP-{i}',(-3.5,y-3.5,F+.045),.7,turquoise,True)

for i,x in enumerate([-80,-66,-52,-38,-24],1):
    gate=roots[f'G-{i}']
    floor(gate,'woven waiting carpet',(x-6.2,33,x+6.2,53.4),carpet)
    floor(gate,'brass zone edge',(x-6.2,33,x+6.2,33.13),amber,F+.035)
    text(gate,f'G-{i}',(x,34.6,F+.046),1.15,white,True)
    # Timber canopy over the boarding exit, leaving seating visible from above.
    box(gate,'boarding canopy',(x,51.7,F+4.9),(12.6,3.6,.22),plaster,.04)
    for xx in [x-5.8+j*.62 for j in range(20)]:
        box(gate,'oak ceiling slat',(xx,51.7,F+4.7),(.2,3.4,.24),wood,.025)
    box(gate,'boarding linear light',(x,50.05,F+4.56),(11.6,.1,.08),led)
    for dx in [-2.4,2.4]:box(gate,'exit door post',(x+dx,52.8,F+1.9),(.16,.25,3.8),metal,.035)
    box(gate,'exit glazed door',(x,52.8,F+1.8),(4.6,.035,3.6),glass)
    display(gate,(x,50,F+5.65),9.3,1.1625,i-1)
    box(gate,'boarding counter',(x,47.2,F+.55),(3.3,1.15,1.1),wood,.14)
    box(gate,'boarding counter top',(x,47.2,F+1.16),(3.45,1.3,.12),stone,.045)
    box(gate,'boarding monitor',(x+.55,47.3,F+1.52),(.85,.085,.55),dark,.025)
    for dx in [-6.3,6.3]:
        box(gate,'low waiting divider',(x+dx,44.2,F+.47),(.2,18,.94),plaster,.04)
        box(gate,'divider handrail',(x+dx,44.2,F+1),(.24,18,.065),wood,.02)
    # Airport seating: curved cushions, aluminium cross beams, and armrests.
    for yy in [37.2,39.4,41.6]:
        for side in [-1,1]:
            center=x+side*3.2
            box(gate,'seat beam',(center,yy,F+.34),(3.95,.18,.18),metal,.03)
            for xx in [center-1.45,center+1.45]:box(gate,'bench leg',(xx,yy,F+.18),(.15,.28,.36),metal,.03)
            for xx in [center-1.25,center,center+1.25]:
                box(gate,'seat cushion',(xx,yy,F+.55),(1.08,.85,.19),leather,.065)
                box(gate,'seat backrest',(xx,yy+.36,F+1.02),(1.08,.18,.9),leather,.07)
                for dx in [-.56,.56]:
                    box(gate,'seat armrest',(xx+dx,yy,F+.82),(.065,.7,.07),metal,.025)
                    box(gate,'arm support',(xx+dx,yy+.24,F+.62),(.065,.07,.36),metal)
    for dx in [-4.5,4.5]:
        cylinder(gate,'boarding bollard',(x+dx,48.7,F+.55),.05,1.1,metal)
        cylinder(gate,'bollard base',(x+dx,48.7,F+.03),.2,.06,metal)
    gate['labelAnchorBlender']=[x,50,7.4]

# Merge generated geometry by zone/material and keep glass separately for picking.
for code in codes:
    by_material={}
    for obj in roots[code].children:
        if obj.type=='MESH' and obj.get('generatedFor')=='US013-material-refinement':
            by_material.setdefault(obj.data.materials[0].name,[]).append(obj)
    for name,objects in by_material.items():
        if len(objects)<2:continue
        for obj in scene.objects:obj.select_set(False)
        for obj in objects:obj.select_set(True)
        bpy.context.view_layer.objects.active=objects[0];bpy.ops.object.join()
        objects[0].name=roots[code].name+'/'+name
for obj in scene.objects:obj.select_set(False)
bpy.context.view_layer.update()

# Export the same identity graph; embedded maps remain available offline in Three.js.
hidden=[]
for obj in terminal.objects:
    hidden.append((obj,obj.hide_get(),obj.hide_render))
    obj.hide_set(False);obj.hide_render=False;obj.select_set(True)
from io_scene_gltf2 import get_format_items
rna=bpy.ops.export_scene.gltf.get_rna_type()
bpy.ops.export_scene.gltf(filepath=os.path.join(OUT,'terminal.glb'),
    export_format=next(i[0] for i in get_format_items(scene,bpy.context) if i[0]=='GLB'),
    use_selection=True,export_yup=True,export_extras=True,export_apply=True,
    export_animations=False,export_cameras=False,export_lights=False,
    export_meshopt_compression_enable=True,
    export_meshopt_extension=enum(rna,'export_meshopt_extension','EXT_meshopt_compression'))
for obj,h,r in hidden:obj.hide_set(h);obj.hide_render=r;obj.select_set(False)
triangles=0;calls=0
for obj in terminal.objects:
    if obj.type=='MESH':
        obj.data.calc_loop_triangles();triangles+=len(obj.data.loop_triangles);calls+=max(1,len(obj.data.materials))
manifest_path=os.path.join(OUT,'manifest.json');manifest=json.load(open(manifest_path))
asset=manifest['assets']['terminal'];asset.update(bytes=os.path.getsize(os.path.join(OUT,'terminal.glb')),triangles=triangles,drawCallsUpperBound=calls)
assert asset['bytes']<manifest['performance']['maxAssetBytes'],asset
assert triangles<manifest['performance']['maxTriangles'],asset
manifest['version']=4
manifest['materialRefinement']={
    'version':1,'textures':json.load(open(os.path.join(MAPS,'asset-credits.json'))),
    'physicalMaterials':['marble','oak veneer','plaster','leather','woven carpet','stainless steel','architectural glazing'],
    'features':['manufactured rounded edges','18 seats per gate','timber ceiling ribbons','curtain wall glazing','digital airport screens','queue stanchions','X-ray scanners','metal detectors','planters'],
    'textureMaxResolution':1024,'textureNormalOrmResolution':512,
    'sourceEquipment':'Retained as hidden references (viewMode=reference); displayed furniture and equipment are original procedural meshes',
}
for e in manifest['entities']:
    obj=roots.get(e['code'])
    if obj and obj.get('labelAnchorBlender'):e['labelAnchorBlender']=list(obj['labelAnchorBlender'])
with open(manifest_path,'w') as f:json.dump(manifest,f,ensure_ascii=False,indent=2)
scene['materialRefinementVersion']=1
scene.name='AeroSynapse · Terminal DEMO · Materiale PBR'
for light in [o for o in scene.objects if o.type=='LIGHT']:
    light.data.energy=180000;light.data.size=100
world=scene.world
background=next(n for n in world.node_tree.nodes if n.type=='BACKGROUND')
background.inputs['Color'].default_value=(.13,.17,.23,1);background.inputs['Strength'].default_value=.5
scene.cycles.samples=48
scene.render.resolution_x=1800;scene.render.resolution_y=1200
scene.render.filepath=os.path.join(ROOT,'assets/blender/terminal-textured-preview.png')
scene.camera.data.ortho_scale=142
bpy.data.orphans_purge(do_local_ids=True,do_linked_ids=False,do_recursive=True)
bpy.ops.file.pack_all()
bpy.context.preferences.filepaths.save_version=0
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'assets/blender/aerosynapse-user-airport.blend'))
print('TEXTURED_TERMINAL',json.dumps(asset),flush=True)
