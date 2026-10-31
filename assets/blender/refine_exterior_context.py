"""Additive, repeatable demo apron finishes; original source geometry/IDs preserved.
Run against the project .blend in background Blender, not the user's active document.
"""
import bpy, ast, os, json
from mathutils import Vector
ROOT=os.path.abspath(os.path.join(os.path.dirname(__file__),'../..'))
OUT=os.path.join(ROOT,'AeroSynapse.Client/public/models/aerosynapse')
scene=bpy.context.scene
F=.45
source=ast.parse(open(os.path.join(os.path.dirname(__file__),'upgrade_terminal_architecture.py')).read())
helpers={'enum','mat','uv','mesh','plane'}
exec(compile(ast.Module(body=[n for n in source.body if isinstance(n,ast.FunctionDef) and n.name in helpers],type_ignores=[]),__file__,'exec'))
glass=None
asphalt=bpy.data.materials['Terminal Design · apron asphalt']
paint=bpy.data.materials.get('Terminal Design · US017 · safety paint') or mat('US017 · safety paint',(.72,.47,.12),.85)
patch=bpy.data.materials.get('Terminal Design · US017 · stand surface') or mat('US017 · stand surface',(.085,.115,.14),.92)
manifest_path=os.path.join(OUT,'manifest.json'); manifest=json.load(open(manifest_path))
for prefix,asset_key in [('02','exteriorDetail'),('03','exteriorLow')]:
    terminal=next(c for c in scene.collection.children if c.name.startswith(prefix))
    for obj in list(terminal.objects):
        if obj.get('exteriorContextAddition'):bpy.data.objects.remove(obj,do_unlink=True)
    roots={o.get('code'):o for o in terminal.objects if o.type=='EMPTY'}
    apron=roots['APRON']
    for child in apron.children:
        if child.type=='MESH':
            child.data=child.data.copy();child.data.materials.clear();child.data.materials.append(asphalt)
            uv(child,asphalt)
    for n in range(1,6):
        root=roots[f'STAND-{n}'];x,y,_=root.location
        obj=plane(root,'stand selection surface',(x-16,y-25,x+16,y+25),patch,.009)
        obj['exteriorContextAddition']=True
        # Broad end bars make the parking envelope legible without introducing trajectories.
        for off in [-25,24.75]:
            obj=plane(root,'stand safety end line',(x-16,y+off,x+16,y+off+.25),paint,.025)
            obj['exteriorContextAddition']=True;obj['annotationOnly']=True
    # Discrete service strip, explicitly illustrative. One merged mesh per LOD.
    verts=[];faces=[]
    for y in range(-140,166,10):
        i=len(verts);verts.extend([(25,y,.002),(25.2,y,.002),(25.2,y+5,.002),(25,y+5,.002)]);faces.append((i,i+1,i+2,i+3))
    obj=mesh(apron,'demo service strip',verts,faces,paint);obj['exteriorContextAddition']=True;obj['annotationOnly']=True
    for o in scene.objects:o.select_set(False)
    saved=[]
    for o in terminal.objects:
        saved.append((o,o.hide_get(),o.hide_render));o.hide_set(False);o.hide_render=False;o.select_set(True)
    from io_scene_gltf2 import get_format_items
    rna=bpy.ops.export_scene.gltf.get_rna_type()
    filename='exterior-detail.glb' if prefix=='02' else 'exterior-low.glb'
    bpy.ops.export_scene.gltf(filepath=os.path.join(OUT,filename),export_format=next(i[0] for i in get_format_items(scene,bpy.context) if i[0]=='GLB'),use_selection=True,
        export_yup=True,export_extras=True,export_apply=True,export_animations=False,export_cameras=False,export_lights=False,
        export_meshopt_compression_enable=True,export_meshopt_extension=enum(rna,'export_meshopt_extension','EXT_meshopt_compression'))
    for o,h,r in saved:o.hide_set(h);o.hide_render=r;o.select_set(False)
    triangles=0;calls=0
    for o in terminal.objects:
        if o.type=='MESH':o.data.calc_loop_triangles();triangles+=len(o.data.loop_triangles);calls+=max(1,len(o.data.materials))
    asset=manifest['assets'][asset_key];asset.update(bytes=os.path.getsize(os.path.join(OUT,filename)),triangles=triangles,drawCallsUpperBound=calls)
    assert asset['bytes']<manifest['performance']['maxAssetBytes'] and triangles<manifest['performance']['maxTriangles']
    print('EXTERIOR_CONTEXT',asset_key,json.dumps(asset),flush=True)
manifest['version']=8
manifest['exteriorContext']={'version':1,'status':'synthetic','positions':'static demonstrative layout / local demo scenario','movement':'No exterior trajectories or live telemetry supplied',
    'standGateLinks':'Illustrative only; not operational adjacency or a validated cross-domain simulator',
    'features':['textured asphalt apron','five selectable stand envelopes','service markings','stable IDs shared by both LODs'],
    'dimensionsMeters':{'apron':[202,330],'stand':[32,50]},'license':'Original project additions and project-generated apron map; supplied source license remains unverified',
    'lod':'Auto distance to camera target, 260 m enter / 290 m leave; economic pins low; minimum adaptive quality pins low'}
assert sum(a['bytes'] for a in manifest['assets'].values())+manifest['environment']['bytes']<manifest['performance']['maxTotalBytes']
with open(manifest_path,'w') as f:json.dump(manifest,f,ensure_ascii=False,indent=2)
bpy.ops.file.pack_all();bpy.context.preferences.filepaths.save_version=0
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'assets/blender/aerosynapse-user-airport.blend'))
