"""블렌더의 정점 그룹과 바인드 자세를 포함한 배포용 GLB를 만든다."""

import json
import math
import os

import bpy
from mathutils import Quaternion, Vector


def blender_point(point):
    x, y, z = point
    return Vector((x, -z, y))


def smooth(value):
    t = min(1.0, max(0.0, value))
    return t * t * (3 - 2 * t)


def export_character(directory, objects, meta):
    species = os.path.basename(directory)
    parts = {obj.name: obj for obj in objects}
    for obj in objects:
        obj.data.transform(obj.matrix_world)
        obj.matrix_world.identity()
        obj.data.name = obj.name

    def pivot(key):
        p = meta[key]
        return blender_point((p["x"], p["y"], p["z"]))

    data = bpy.data.armatures.new("Skeleton")
    rig = bpy.data.objects.new("Skeleton", data)
    bpy.context.collection.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")

    def bone(name, origin, end, parent=None):
        b = data.edit_bones.new(name)
        b.head, b.tail = origin, end
        if parent:
            b.parent = data.edit_bones[parent]

    up = Vector((0, 0, 0.12))
    origin = Vector((0, 0, 0))
    bone("Root", origin, up)
    bone("Pelvis", blender_point((0, -0.6, 0)), blender_point((0, -0.3, 0)), "Root")
    neck = pivot("headPivot")
    bone("Head", neck, neck + up, "Pelvis")
    chains = {}
    for part, obj in parts.items():
        if part in ("LeftPaw", "RightPaw"):
            start = blender_point(((-1 if part == "LeftPaw" else 1) * 0.24, -0.32, 0.13))
        elif part in ("LeftArm", "RightArm", "LeftEar", "RightEar", "Tail"):
            start = pivot(part[0].lower() + part[1:] + "Pivot")
        else:
            continue
        parent = "Head" if part.endswith("Ear") or "Body" not in parts else "Pelvis"
        coords = [v.co.copy() for v in obj.data.vertices]
        end = max(coords, key=lambda p: (p - start).length_squared)
        # 끝단의 표면 평균을 취해 비대칭 삼각분할이 관절축을 기울이지 않도록 한다.
        axis = (end - start).normalized()
        extent = (end - start).length
        cap = [p for p in coords if (p - start).dot(axis) > extent * 0.80]
        end = sum(cap, Vector()) / len(cap)
        chains[part] = (start, end, parent)

    for left, right in (("LeftEar", "RightEar"), ("LeftArm", "RightArm"), ("LeftPaw", "RightPaw")):
        if left in chains:
            ls, le, parent = chains[left]
            rs, re, _ = chains[right]
            left_end = (le + Vector((-re.x, re.y, re.z))) * 0.5
            chains[left] = (ls, left_end, parent)
            chains[right] = (rs, Vector((-left_end.x, left_end.y, left_end.z)), parent)

    for part, (start, end, parent) in chains.items():
        mid = start.lerp(end, 0.52)
        bone(part, start, mid, parent)
        bone(part + "Tip", mid, end, part)
    if "Body" in parts:
        for name, side in (("LeftLeg", -1), ("RightLeg", 1)):
            ankle = blender_point((side * 0.26, -0.82, 0.07))
            bone(name, blender_point((side * 0.26, -0.61, 0.02)), ankle, "Pelvis")
            bone(name.replace("Leg", "Foot"), ankle, blender_point((side * 0.26, -0.91, 0.16)), name)
    bpy.ops.object.mode_set(mode="OBJECT")

    for part, obj in parts.items():
        groups = {}
        for v in obj.data.vertices:
            if part in chains:
                start, end, parent = chains[part]
                axis = end - start
                t = (v.co - start).dot(axis) / axis.length_squared
                base = smooth((t + 0.13) / 0.50)
                tip = smooth((t - 0.32) / 0.55)
                weights = {parent: 1-base, part: base*(1-tip), part+"Tip": base*tip}
            elif part == "Head" and "Body" not in parts:
                side = "LeftArm" if v.co.x < 0 else "RightArm"
                start, end, _ = chains[side]
                radius = (end - start).length
                influence = 0.45 * smooth(1.0 - (v.co - start).length / radius)
                weights = {"Head": 1.0 - influence, side: influence}
            elif part == "Body":
                w = smooth((-v.co.z - 0.61) / 0.24) * smooth(abs(v.co.x) / 0.15)
                side = "Left" if v.co.x < 0 else "Right"
                foot = smooth((-v.co.z - 0.78) / 0.13)
                weights = {"Pelvis": 1-w, side+"Leg": w*(1-foot), side+"Foot": w*foot}
            else:
                weights = {"Head": 1.0}
            for name, weight in weights.items():
                if weight > 0:
                    if name not in groups:
                        groups[name] = obj.vertex_groups.new(name=name)
                    groups[name].add([v.index], weight, "REPLACE")
        obj.parent = rig
        modifier = obj.modifiers.new("Skin", "ARMATURE")
        modifier.object = rig

    # Assimp가 메시 노드와 관절을 이름으로 혼동하지 않게 조인트 이름을 분리한다.
    for b in data.bones:
        end = b.tail_local
        b["end"] = [end.x, end.z, -end.y]
        limit = {"mochi_spirit": 62, "fluffy_cloud": 52, "chiikawa": 60, "pink_reference": 60, "usagi": 85}.get(species, 78)
        if b.name.endswith(("Ear", "EarTip")):
            limit = 38 if species == "usagi" else 48
        b["limit"] = limit
        constraint = rig.pose.bones[b.name].constraints.new("LIMIT_ROTATION")
        constraint.owner_space = "LOCAL"
        for axis in "xyz":
            setattr(constraint, "use_limit_" + axis, True)
            setattr(constraint, "min_" + axis, -math.radians(limit))
            setattr(constraint, "max_" + axis, math.radians(limit))
        b.name = "Joint_" + b.name

    # 독립 뷰어에서도 굽힘 및 바인드 자세 복원을 확인할 수 있는 관절 인사 클립.
    wave = rig.pose.bones.get("Joint_LeftArm") or rig.pose.bones["Joint_LeftPaw"]
    axis = wave.bone.matrix_local.to_quaternion().inverted() @ blender_point((0, 0, 1))
    for frame, angle in ((1, 0), (7, -46), (12, -25), (17, -48), (24, 0)):
        wave.rotation_quaternion = Quaternion(axis, math.radians(angle))
        wave.keyframe_insert("rotation_quaternion", frame=frame)
    rig.animation_data.action.name = "Wave"
    bpy.context.scene.frame_set(1)
    rig["metadata"] = json.dumps(meta, ensure_ascii=False)
    bpy.ops.object.select_all(action="DESELECT")
    for obj in [rig, *objects]:
        obj.select_set(True)
    os.makedirs(directory, exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=os.path.join(directory, "model.glb"), export_format="GLB",
                              use_selection=True, export_extras=True, export_texcoords=False,
                              export_force_sampling=False, export_animation_mode="ACTIONS")
