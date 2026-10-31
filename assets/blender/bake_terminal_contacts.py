"""Bake portable seating contact occlusion and add actual interior light fixtures."""
import bpy, os, json, math
from mathutils import Vector

ROOT=os.path.abspath(os.path.join(os.path.dirname(__file__),'../..'))
OUT=os.path.join(ROOT,'AeroSynapse.Client/public/models/aerosynapse')
MAPS=os.path.join(os.path.dirname(__file__),'materials')
scene=bpy.context.scene
assert scene.get('architecturalUpgradeVersion')==1
assert not scene.get('contactBakeVersion')
terminal=next(c for c in scene.collection.children if c.name.startswith('01'))
preview=next(c for c in scene.collection.children if c.name.startswith('04'))

def enum(owner,prop,wanted):
    props=owner.properties if hasattr(owner,'properties') else owner.bl_rna.properties
    return next(i.identifier for i in props[prop].enum_items if i.identifier==wanted)

group=bpy.data.node_groups.new('glTF Material Output','ShaderNodeTree')
socket_mode=next(i.identifier for i in group.interface.bl_rna.functions['new_socket'].parameters['in_out'].enum_items if i.identifier=='INPUT')
group.interface.new_socket(name='Occlusion',in_out=socket_mode,socket_type='NodeSocketFloat')
scene.cycles.samples=16
maps=[]
bpy.context.view_layer.update()
for n in range(1,6):
    obj=next(o for o in terminal.objects if o.type=='MESH' and o.name==f'AS|terminal|G-{n}/woven waiting carpet')
    m=obj.data.materials[0].copy();m.name=f'Architectural · carpet with contact AO G-{n}';obj.data.materials[0]=m
    uv=m.node_tree.nodes.new('ShaderNodeUVMap');uv.uv_map='UVMap'
    for node in list(m.node_tree.nodes):
        if node.type=='TEX_IMAGE' and not node.inputs['Vector'].is_linked:m.node_tree.links.new(uv.outputs['UV'],node.inputs['Vector'])
    layer=obj.data.uv_layers.new(name='AOMap')
    pts=[obj.matrix_world@v.co for v in obj.data.vertices]
    x0=min(p.x for p in pts);x1=max(p.x for p in pts);y0=min(p.y for p in pts);y1=max(p.y for p in pts)
    for loop in obj.data.loops:
        p=pts[loop.vertex_index];layer.data[loop.index].uv=((p.x-x0)/(x1-x0),(p.y-y0)/(y1-y0))
    obj.data.uv_layers.active=layer
    image=bpy.data.images.new(f'Seating contact AO G-{n}',width=512,height=512,alpha=False)
    image.colorspace_settings.name='Non-Color'
    tex=m.node_tree.nodes.new('ShaderNodeTexImage');tex.image=image
    ao_uv=m.node_tree.nodes.new('ShaderNodeUVMap');ao_uv.uv_map='AOMap';m.node_tree.links.new(ao_uv.outputs['UV'],tex.inputs['Vector'])
    m.node_tree.nodes.active=tex
    for o in scene.objects:o.select_set(False)
    obj.hide_set(False);obj.select_set(True);bpy.context.view_layer.objects.active=obj
    print('BAKING CONTACT',n,flush=True)
    r=bpy.ops.object.bake.get_rna_type()
    bpy.ops.object.bake(type=enum(r,'type','AO'),target=enum(r,'target','IMAGE_TEXTURES'),margin=4,use_clear=True)
    name=f'terminal-contact-g{n}.png';image.filepath_raw=os.path.join(MAPS,name)
    image.file_format=enum(image,'file_format','PNG');image.save()
    node=m.node_tree.nodes.new('ShaderNodeGroup');node.node_tree=group
    m.node_tree.links.new(tex.outputs['Color'],node.inputs['Occlusion'])
    obj.data.uv_layers[0].active_render=True
    maps.append(name)

for n,x in enumerate([-80,-66,-52,-38,-24],1):
    data=bpy.data.lights.new(f'Terminal interior area {n}',enum(bpy.types.Light,'type','AREA'))
    data.energy=6000;data.shape=enum(data,'shape','RECTANGLE');data.size=10;data.size_y=12;data.color=(.88,.93,1)
    obj=bpy.data.objects.new(data.name,data);preview.objects.link(obj);obj.location=(x,39,6.8)
    obj.rotation_euler=(Vector((x,39,.45))-obj.location).to_track_quat('-Z','Y').to_euler()
data=bpy.data.lights.new('Terminal hall area',enum(bpy.types.Light,'type','AREA'));data.energy=18000
data.shape=enum(data,'shape','RECTANGLE');data.size=25;data.size_y=40;data.color=(.88,.93,1)
obj=bpy.data.objects.new(data.name,data);preview.objects.link(obj);obj.location=(3.5,38,7)
obj.rotation_euler=(Vector((3.5,38,.45))-obj.location).to_track_quat('-Z','Y').to_euler()
data=bpy.data.lights.new('Vault indirect fill',enum(bpy.types.Light,'type','AREA'));data.energy=12000
data.shape=enum(data,'shape','RECTANGLE');data.size=55;data.size_y=18;data.color=(.93,.95,1)
obj=bpy.data.objects.new(data.name,data);preview.objects.link(obj);obj.location=(-51,39,3.3)
obj.rotation_euler=(math.pi,0,0)

saved=[]
for o in scene.objects:o.select_set(False)
for obj in terminal.objects:
    saved.append((obj,obj.hide_get(),obj.hide_render));obj.hide_set(False);obj.hide_render=False;obj.select_set(True)
from io_scene_gltf2 import get_format_items
rna=bpy.ops.export_scene.gltf.get_rna_type()
bpy.ops.export_scene.gltf(filepath=os.path.join(OUT,'terminal.glb'),export_format=next(i[0] for i in get_format_items(scene,bpy.context) if i[0]=='GLB'),use_selection=True,
    export_yup=True,export_extras=True,export_apply=True,export_animations=False,export_cameras=False,export_lights=False,
    export_meshopt_compression_enable=True,export_meshopt_extension=enum(rna,'export_meshopt_extension','EXT_meshopt_compression'))
for obj,h,r in saved:obj.hide_set(h);obj.hide_render=r;obj.select_set(False)
p=os.path.join(OUT,'manifest.json');manifest=json.load(open(p));manifest['version']=6
manifest['contactOcclusion']={'version':1,'maps':maps,'resolution':[512,512],'uvSet':'AOMap','gltfTexCoord':1,'method':'Cycles AO bake from actual seating geometry','license':'Original project-generated maps'}
manifest['assets']['terminal']['bytes']=os.path.getsize(os.path.join(OUT,'terminal.glb'))
assert manifest['assets']['terminal']['bytes']<manifest['performance']['maxAssetBytes']
with open(p,'w') as f:json.dump(manifest,f,ensure_ascii=False,indent=2)
scene['contactBakeVersion']=1
bpy.ops.file.pack_all();bpy.context.preferences.filepaths.save_version=0
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'assets/blender/aerosynapse-user-airport.blend'))
print('CONTACT BAKE COMPLETE',manifest['assets']['terminal']['bytes'],flush=True)
