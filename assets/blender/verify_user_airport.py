"""Read-only comparison of supplied architecture against the adapted Blender copy."""
import bpy, json, os, hashlib
from mathutils import Vector

ROOT=os.path.abspath(os.path.join(os.path.dirname(__file__),'../..'))
SOURCE='/Users/calin/Documents/Blender Creations/Airport Modification/airport-transparent-roof-no-lounge.blend'
manifest=json.load(open(os.path.join(ROOT,'AeroSynapse.Client/public/models/aerosynapse/manifest.json')))
assert hashlib.sha256(open(SOURCE,'rb').read()).hexdigest()==manifest['source']['sha256']
bpy.context.view_layer.update()
copied={}
for obj in bpy.context.scene.objects:
    source=obj.get('sourceObject','')
    if obj.type!='MESH' or not source.startswith(('07.Modulo','02.','04.','06.')):continue
    if any(c.name.startswith('03') for c in obj.users_collection):continue
    copied[source]={
        'vertices':[obj.matrix_world@v.co for v in obj.data.vertices],
        'faces':[tuple(p.vertices) for p in obj.data.polygons],
        'uvs':[[tuple(v.uv) for v in layer.data] for layer in obj.data.uv_layers],
    }
assert len(copied)==11, len(copied)
assert not any(o.get('sourceObject','').startswith('13.Cafe') for o in bpy.context.scene.objects)
bpy.ops.wm.open_mainfile(filepath=SOURCE)
bpy.context.view_layer.update()
for name,data in copied.items():
    obj=bpy.data.objects[name]
    original=[obj.matrix_world@v.co for v in obj.data.vertices]
    assert len(original)==len(data['vertices']),name
    deviation=max((a-b).length for a,b in zip(original,data['vertices']))
    assert deviation<.0001,(name,deviation)
    assert [tuple(p.vertices) for p in obj.data.polygons]==data['faces'],name
    assert [[tuple(v.uv) for v in layer.data] for layer in obj.data.uv_layers]==data['uvs'],name
    print('PRESERVED',name,'world deviation',deviation,flush=True)
print('PASS: original modules, roofs, tower, hangar and bridge retain vertices, faces, UVs and world placement.',flush=True)
