"""
LabPet Blender 5.2 고품질 소프트 클레이 3D 에셋 생성 스크립트.

사용 예시:
  blender.exe --background --python tools/blender/generate_labpet_assets.py -- --targets fluffy_puppy
  blender.exe --background --python tools/blender/generate_labpet_assets.py -- --targets all

WPF 3D 좌표계 규약:
  - +X: 화면 오른쪽 (캐릭터 왼쪽)
  - +Y: 위 (Up)
  - +Z: 정면 (Forward, 카메라 방향)
  - 단위 스케일 1.0, 바닥 Y ≈ -0.92 ~ -0.95

조형 파이프라인:
  - Metaball 융합 -> Voxel Remesh -> Smooth -> Decimate로 몸통·머리·다리·구름 경계선이 없는
    일체형 소프트 클레이 / 디자이너 아트토이 피규어 메쉬를 생성한다.
  - ray_cast 표면 투영으로 눈·입·볼터치·크림 배/주둥이 패치를 실제 굴곡진 곡면에 매끄럽게 밀착시킨다.
"""

import argparse
import json
import math
import os
import sys
import bpy
import bmesh
from mathutils import Vector, Matrix, Euler


SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
sys.path.insert(0, SCRIPT_DIR)
from character_skin import export_character

MODELS_ROOT = os.path.join(REPO_ROOT, "src", "LabPet.Client", "Assets", "Models")

# Blender 5.2 Metaball threshold=0.60, stiffness=2.0 기준 실측 반지름 역보정 배율
MB_THRESHOLD = 0.60
MB_RADIUS_MULT = 1.7397
C_WPF_TO_BL = Euler((math.radians(90.0), 0.0, 0.0), "XYZ").to_matrix()
C_BL_TO_WPF = C_WPF_TO_BL.inverted()


def to_bl(x, y, z):
    """WPF (+X Right, +Y Up, +Z Front) -> Blender (+X Right, -Y Front, +Z Up)."""
    return Vector((float(x), float(-z), float(y)))


def to_bl_vec(v):
    return to_bl(v[0], v[1], v[2])


def to_wpf(bl_v):
    """Blender -> WPF 변환."""
    return Vector((bl_v.x, bl_v.z, -bl_v.y))


def clear_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def ensure_material(slot_name):
    if slot_name in bpy.data.materials:
        return bpy.data.materials[slot_name]
    return bpy.data.materials.new(name=slot_name)


def set_smooth_and_recalc(obj):
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.normals_make_consistent(inside=False)
    bpy.ops.object.mode_set(mode="OBJECT")
    if len(obj.data.polygons) > 0:
        obj.data.polygons.foreach_set("use_smooth", [True] * len(obj.data.polygons))
    obj.select_set(False)


def apply_wpf_vertex_fn(obj, fn):
    for v in obj.data.vertices:
        wp = to_wpf(v.co)
        nx, ny, nz = fn(wp.x, wp.y, wp.z)
        v.co = to_bl(nx, ny, nz)


def soft_cushion_floor(y, floor_y=-0.92, blend_band=0.14):
    """바닥 y <= floor_y 근방을 날카로운 컷 없이 둥글고 폭신한 클레이 쿠션처럼 평탄화한다."""
    if y >= floor_y + blend_band:
        return y
    if y <= floor_y:
        return floor_y + (y - floor_y) * 0.06
    t = (y - floor_y) / blend_band
    # 부드러운 S커브 블렌딩으로 밑바닥 모서리 각짐 방지
    smooth_t = t * t * (3.0 - 2.0 * t)
    target = floor_y + (y - floor_y) * 0.06
    return target + (y - target) * smooth_t


def remesh_smooth_decimate(
    obj,
    voxel_size=0.028,
    smooth_iters=14,
    target_tris=1200,
    pre_remesh_fn=None,
    post_smooth_fn=None,
    final_smooth_iters=2,
):
    """Voxel Remesh -> Smooth -> Decimate -> 미세 Smooth 순서로 이음새 없는 매끈한 클레이 메시 완성."""
    if pre_remesh_fn is not None:
        apply_wpf_vertex_fn(obj, pre_remesh_fn)

    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)

    if voxel_size and voxel_size > 0:
        rem = obj.modifiers.new(name="VoxelRemesh", type="REMESH")
        rem.mode = "VOXEL"
        rem.voxel_size = float(voxel_size)
        rem.use_smooth_shade = True
        bpy.ops.object.modifier_apply(modifier=rem.name)

    if smooth_iters > 0:
        sm = obj.modifiers.new(name="ClaySmooth", type="SMOOTH")
        sm.factor = 0.5
        sm.iterations = int(smooth_iters)
        bpy.ops.object.modifier_apply(modifier=sm.name)

    if post_smooth_fn is not None:
        apply_wpf_vertex_fn(obj, post_smooth_fn)

    obj.data.calc_loop_triangles()
    cur_tris = len(obj.data.loop_triangles)
    if target_tris is not None and cur_tris > target_tris * 1.04:
        ratio = max(0.02, min(0.98, float(target_tris) / float(cur_tris)))
        dec = obj.modifiers.new(name="ClayDecimate", type="DECIMATE")
        dec.decimate_type = "COLLAPSE"
        dec.ratio = ratio
        dec.use_collapse_triangulate = True
        bpy.ops.object.modifier_apply(modifier=dec.name)

    if final_smooth_iters > 0:
        sm2 = obj.modifiers.new(name="FinalPolish", type="SMOOTH")
        sm2.factor = 0.28
        sm2.iterations = int(final_smooth_iters)
        bpy.ops.object.modifier_apply(modifier=sm2.name)

    obj.select_set(False)
    set_smooth_and_recalc(obj)
    return obj


def create_metaball_clay(
    part_name,
    material_slot,
    elements,
    mb_res=0.036,
    voxel_size=0.028,
    smooth_iters=14,
    target_tris=1200,
    pre_remesh_fn=None,
    post_smooth_fn=None,
    final_smooth_iters=2,
):
    """Metaball로 여러 덩어리를 부드러운 필렛으로 하나로 융합한 일체형 소프트 클레이 메시 생성.
    elements 항목: (center_wpf, radii_wpf[, rot_euler_wpf[, stiffness[, negative]]])
    """
    mb = bpy.data.metaballs.new(part_name + "_MB")
    mb.resolution = float(mb_res)
    mb.render_resolution = float(mb_res)
    mb.threshold = MB_THRESHOLD

    mb_obj = bpy.data.objects.new(part_name + "_MB", mb)
    bpy.context.collection.objects.link(mb_obj)

    for spec in elements:
        center_wpf = spec[0]
        radii_wpf = spec[1]
        rot_euler_wpf = spec[2] if len(spec) > 2 else None
        stiffness = spec[3] if len(spec) > 3 else 2.0
        negative = spec[4] if len(spec) > 4 else False

        el = mb.elements.new(type="ELLIPSOID")
        el.co = to_bl(*center_wpf)
        el.radius = 1.0
        el.stiffness = float(stiffness)
        el.use_negative = bool(negative)

        rx, ry, rz = radii_wpf
        el.size_x = float(rx) * MB_RADIUS_MULT
        el.size_y = float(rz) * MB_RADIUS_MULT
        el.size_z = float(ry) * MB_RADIUS_MULT

        if rot_euler_wpf is not None:
            r_wpf = Euler(rot_euler_wpf, "XYZ").to_matrix()
            el.rotation = (C_WPF_TO_BL @ r_wpf @ C_BL_TO_WPF).to_quaternion()

    bpy.ops.object.select_all(action="DESELECT")
    bpy.context.view_layer.objects.active = mb_obj
    mb_obj.select_set(True)
    bpy.ops.object.convert(target="MESH")
    mesh_obj = bpy.context.active_object
    mesh_obj.name = part_name
    mesh_obj.select_set(False)

    # 남아 있는 Metaball 데이터 블록 정리
    if mb.users == 0:
        bpy.data.metaballs.remove(mb)

    mesh_obj.data.materials.clear()
    mesh_obj.data.materials.append(ensure_material(material_slot))

    return remesh_smooth_decimate(
        mesh_obj,
        voxel_size=voxel_size,
        smooth_iters=smooth_iters,
        target_tris=target_tris,
        pre_remesh_fn=pre_remesh_fn,
        post_smooth_fn=post_smooth_fn,
        final_smooth_iters=final_smooth_iters,
    )


def create_voxel_fused_clay(
    part_name,
    material_slot,
    source_objs,
    voxel_size=0.018,
    smooth_iters=12,
    target_tris=280,
    pre_remesh_fn=None,
    post_smooth_fn=None,
    final_smooth_iters=2,
):
    """여러 프리미티브/곡선 튜브를 Voxel Remesh로 완전히 녹여 단일 클레이 파츠로 융합."""
    joined = join_part_group(part_name, source_objs)
    joined.data.materials.clear()
    joined.data.materials.append(ensure_material(material_slot))
    return remesh_smooth_decimate(
        joined,
        voxel_size=voxel_size,
        smooth_iters=smooth_iters,
        target_tris=target_tris,
        pre_remesh_fn=pre_remesh_fn,
        post_smooth_fn=post_smooth_fn,
        final_smooth_iters=final_smooth_iters,
    )


def create_sculpted_quad_mesh(
    part_name,
    material_slot,
    center_wpf,
    radii_wpf,
    cubes_cuts=0,
    subsurf_levels=2,
    sculpt_fn=None,
    rotation_euler_wpf=None,
    rotation_quat_wpf=None,
):
    """부피 수축과 UV 극점 왜곡이 없는 Quad 기반 서브디비전 소프트 클레이 메시 (눈·작은 포인트·액세서리용)."""
    mesh = bpy.data.meshes.new(part_name)
    obj = bpy.data.objects.new(part_name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.data.materials.append(ensure_material(material_slot))

    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=2.0)
    if cubes_cuts > 0:
        bmesh.ops.subdivide_edges(
            bm,
            edges=bm.edges[:],
            cuts=cubes_cuts,
            use_grid_fill=True,
        )
    bm.to_mesh(mesh)
    bm.free()

    if subsurf_levels > 0:
        mod = obj.modifiers.new(name="Subsurf", type="SUBSURF")
        mod.levels = subsurf_levels
        mod.render_levels = subsurf_levels
        bpy.context.view_layer.objects.active = obj
        obj.select_set(True)
        bpy.ops.object.modifier_apply(modifier=mod.name)
        obj.select_set(False)

    rx, ry, rz = radii_wpf
    cx, cy, cz = center_wpf
    if rotation_quat_wpf is not None:
        rot_m = rotation_quat_wpf.to_matrix()
    elif rotation_euler_wpf is not None:
        rot_m = Euler(rotation_euler_wpf, "XYZ").to_matrix()
    else:
        rot_m = None

    for v in obj.data.vertices:
        n = v.co.normalized()
        px, py, pz = n.x * rx, n.y * ry, n.z * rz
        if sculpt_fn is not None:
            px, py, pz = sculpt_fn(px, py, pz, n.x, n.y, n.z)
        if rot_m is not None:
            vec = rot_m @ Vector((px, py, pz))
            px, py, pz = vec.x, vec.y, vec.z
        v.co = to_bl(cx + px, cy + py, cz + pz)

    set_smooth_and_recalc(obj)
    return obj


def create_curve_tube(
    part_name,
    material_slot,
    points_with_radius_wpf,
    resolution_u=8,
    bevel_resolution=3,
    closed=False,
    fill_caps=True,
    scale_x=1.0,
    scale_y=1.0,
    scale_z=1.0,
):
    """두께 변화(테이퍼)가 있는 매끈한 튜브/곡선 메시 생성."""
    curve_data = bpy.data.curves.new(part_name + "_Curve", type="CURVE")
    curve_data.dimensions = "3D"
    curve_data.resolution_u = resolution_u
    curve_data.bevel_depth = 1.0
    curve_data.bevel_resolution = bevel_resolution
    curve_data.use_fill_caps = fill_caps and not closed

    spline = curve_data.splines.new("NURBS" if len(points_with_radius_wpf) >= 4 else "POLY")
    spline.points.add(len(points_with_radius_wpf) - 1)
    for idx, (x, y, z, r) in enumerate(points_with_radius_wpf):
        bl_p = to_bl(x, y, z)
        spline.points[idx].co = (bl_p.x, bl_p.y, bl_p.z, 1.0)
        spline.points[idx].radius = float(r)

    if spline.type == "NURBS":
        spline.use_endpoint_u = not closed
        spline.use_cyclic_u = closed
        spline.order_u = min(4, len(points_with_radius_wpf))
    else:
        spline.use_cyclic_u = closed

    curve_obj = bpy.data.objects.new(part_name + "_CurveObj", curve_data)
    bpy.context.collection.objects.link(curve_obj)

    bpy.context.view_layer.objects.active = curve_obj
    curve_obj.select_set(True)
    bpy.ops.object.convert(target="MESH")
    mesh_obj = bpy.context.active_object
    mesh_obj.name = part_name
    mesh_obj.select_set(False)

    if abs(scale_x - 1.0) > 1e-4 or abs(scale_y - 1.0) > 1e-4 or abs(scale_z - 1.0) > 1e-4:
        for v in mesh_obj.data.vertices:
            wx, wy, wz = to_wpf(v.co)
            v.co = to_bl(wx * scale_x, wy * scale_y, wz * scale_z)

    mesh_obj.data.materials.clear()
    mesh_obj.data.materials.append(ensure_material(material_slot))
    set_smooth_and_recalc(mesh_obj)
    return mesh_obj


def sample_surface_wpf(target_obj, origin_wpf, dir_wpf=(0.0, 0.0, -1.0)):
    """조형된 타겟 메시 표면 좌표(WPF)와 외향 법선을 레이캐스트로 정밀 추출."""
    bpy.context.view_layer.update()
    bl_origin = to_bl(*origin_wpf)
    bl_dir = to_bl(*dir_wpf).normalized()
    hit, loc, norm, _ = target_obj.ray_cast(bl_origin, bl_dir)
    if hit:
        wp_norm = to_wpf(norm).normalized()
        return True, to_wpf(loc), wp_norm

    ok, loc2, norm2, _ = target_obj.closest_point_on_mesh(to_bl(origin_wpf[0], origin_wpf[1], 0.25))
    if ok:
        wp_norm = to_wpf(norm2).normalized()
        return True, to_wpf(loc2), wp_norm
    return False, Vector(origin_wpf), Vector((0.0, 0.0, 1.0))


def sample_aimed_surface_wpf(target_obj, anchor_wpf, dir_wpf=(0.0, 0.0, -1.0), distance=1.8):
    """목표 앵커 지점을 정확히 통과하도록 후퇴한 원점에서 dir_wpf 방향으로 레이캐스트한다."""
    d = Vector(dir_wpf).normalized()
    origin = Vector(anchor_wpf) - d * float(distance)
    return sample_surface_wpf(target_obj, origin, d)


def create_conformed_clay_patch(
    part_name,
    material_slot,
    target_obj,
    ray_origin_wpf,
    ray_dir_wpf,
    rx,
    ry,
    center_offset=0.015,
    rim_offset=-0.011,
    rings=4,
    segs=22,
    wrap_focus_depth=1.8,
    shape_fn=None,
):
    """타겟 곡면에 정점을 직접 투영해 Z-fighting·들뜸·젤리빈 돌출 없이 인레이처럼 밀착된 클레이 패치를 생성."""
    hit, center_p, center_n = sample_surface_wpf(target_obj, ray_origin_wpf, ray_dir_wpf)
    if not hit:
        raise RuntimeError(f"Surface hit failed for patch {part_name} at {ray_origin_wpf}")

    # 표면 법선 기준 직교 접평면 (u_axis ≈ +X, v_axis ≈ +Y) 구축
    world_up = Vector((0.0, 1.0, 0.0))
    if abs(center_n.dot(world_up)) > 0.96:
        world_up = Vector((0.0, 0.0, -1.0))
    u_axis = world_up.cross(center_n).normalized()
    v_axis = center_n.cross(u_axis).normalized()
    focus_pt = center_p - center_n * wrap_focus_depth

    def place_point(ux_norm, vy_norm, radial_t):
        if shape_fn is not None:
            ux_norm, vy_norm = shape_fn(ux_norm, vy_norm)
        sample_plane_pt = center_p + u_axis * (ux_norm * rx) + v_axis * (vy_norm * ry)
        cast_dir = (focus_pt - sample_plane_pt).normalized()
        cast_origin = sample_plane_pt - cast_dir * 0.90
        ok, surf_p, surf_n = sample_surface_wpf(target_obj, cast_origin, cast_dir)
        if not ok:
            low, high = 0.0, 1.0
            surf_p, surf_n = center_p, center_n
            for _ in range(6):
                mid = (low + high) * 0.5
                sample = center_p.lerp(sample_plane_pt, mid)
                direction = (focus_pt - sample).normalized()
                hit, pos, normal = sample_surface_wpf(target_obj, sample - direction * 0.90, direction)
                if hit:
                    low, surf_p, surf_n = mid, pos, normal
                else:
                    high = mid
        if radial_t >= 0.999:
            offset = rim_offset
        else:
            # 0..0.88 구간은 +center_offset ~ +0.006으로 부드러운 곡면을 유지, 1.0 테두리는 본체 내부로 말아 넣음
            inner_edge = max(0.005, center_offset * 0.42)
            dome = 0.5 * (1.0 + math.cos(math.pi * min(1.0, (radial_t / 0.88)) ** 1.35))
            offset = inner_edge + (center_offset - inner_edge) * dome
        return surf_p + surf_n * offset

    mesh = bpy.data.meshes.new(part_name)
    obj = bpy.data.objects.new(part_name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.data.materials.append(ensure_material(material_slot))

    bm = bmesh.new()
    v_center = bm.verts.new(to_bl_vec(place_point(0.0, 0.0, 0.0)))
    ring_radii = [((i + 1) / float(rings)) * 0.88 for i in range(rings - 1)] + [1.0]

    ring_verts = []
    for r_val in ring_radii:
        cur_ring = []
        for s in range(segs):
            ang = 2.0 * math.pi * s / float(segs)
            ux = math.cos(ang) * r_val
            vy = math.sin(ang) * r_val
            cur_ring.append(bm.verts.new(to_bl_vec(place_point(ux, vy, r_val))))
        ring_verts.append(cur_ring)

    # 중심 팬 (면 방향이 center_n 외향과 일치하도록 반시계 순서 유지)
    first_ring = ring_verts[0]
    for s in range(segs):
        s_next = (s + 1) % segs
        bm.faces.new((v_center, first_ring[s], first_ring[s_next]))

    # 동심원 링 쿼드 연결
    for r_idx in range(len(ring_verts) - 1):
        r0 = ring_verts[r_idx]
        r1 = ring_verts[r_idx + 1]
        for s in range(segs):
            s_next = (s + 1) % segs
            bm.faces.new((r0[s], r1[s], r1[s_next], r0[s_next]))

    bm.to_mesh(mesh)
    bm.free()
    set_smooth_and_recalc(obj)
    return obj


def join_part_group(part_name, objects):
    """같은 논리 파츠 이름에 속한 여러 서브 오브젝트를 재질 슬롯을 유지하며 하나로 병합."""
    objects = [o for o in objects if o is not None]
    if not objects:
        return None
    if len(objects) == 1:
        objects[0].name = part_name
        return objects[0]

    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.object.join()
    joined = bpy.context.active_object
    joined.name = part_name
    set_smooth_and_recalc(joined)
    return joined


def measure_head_crown_and_radius(head_obj, head_center_z=0.0):
    """실제 생성된 Head 메시에서 머리 꼭대기 좌표(HeadSocket.Y)와 최대 좌우 반폭(HeadRadiusX)을 정확히 측정."""
    bpy.context.view_layer.update()
    hit, pos, _ = sample_surface_wpf(head_obj, (0.0, 2.5, head_center_z), (0.0, -1.0, 0.0))
    crown_y = float(pos.y) if hit else max(to_wpf(v.co).y for v in head_obj.data.vertices)
    max_x = max(abs(to_wpf(v.co).x) for v in head_obj.data.vertices)
    return round(crown_y - 0.025, 4), round(max_x, 4)


def export_obj(filepath, objects):
    os.makedirs(os.path.dirname(filepath), exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    total_tris = 0
    for obj in objects:
        if obj is not None:
            obj.select_set(True)
            obj.data.calc_loop_triangles()
            total_tris += len(obj.data.loop_triangles)

    bpy.ops.wm.obj_export(
        filepath=filepath,
        forward_axis="NEGATIVE_Z",
        up_axis="Y",
        export_uv=False,
        export_normals=True,
        export_materials=True,
        export_triangulated_mesh=True,
        apply_modifiers=True,
        export_selected_objects=True,
    )

    mtl_path = os.path.splitext(filepath)[0] + ".mtl"
    if os.path.exists(mtl_path):
        os.remove(mtl_path)

    with open(filepath, "r", encoding="utf-8") as f:
        lines = [line for line in f if not line.startswith("mtllib ")]
    with open(filepath, "w", encoding="utf-8", newline="\n") as f:
        f.writelines(lines)

    return total_tris



# ---------------------------------------------------------------------------
# 공통 표면 밀착 얼굴/표정/볼터치 빌더 (소프트 클레이 비즈 눈 & 인레이 라인)
# ---------------------------------------------------------------------------

def build_conformed_face_expressions(
    head_clay,
    eye_x=0.255,
    eye_y=0.33,
    eye_rx=0.106,
    eye_ry=0.122,
    eye_depth=0.056,
    mouth_y=0.19,
    is_puppy=False,
    cheek_x=0.40,
    cheek_y=0.20,
    cheek_rx=0.132,
    cheek_ry=0.078,
):
    parts = {}

    # 각 눈의 실제 표면 위치 및 외향 법선 추출 (목표 앵커 기준 정밀 레이캐스트)
    eye_hits = {}
    for side in (-1, 1):
        _, pos, norm = sample_aimed_surface_wpf(
            head_clay,
            anchor_wpf=(side * eye_x, eye_y, 0.45),
            dir_wpf=(-side * 0.14, 0.0, -0.99),
        )
        eye_hits[side] = (pos, norm)

    eye_blink_z = round(sum(eye_hits[s][0].z for s in (-1, 1)) * 0.5, 4)

    # 볼터치 (Cheeks) — 목표 볼 좌표를 향해 법선 방향으로 반듯하게 투영
    cheek_objs = []
    for side in (-1, 1):
        cheek_dir = Vector((-side * 0.28, -0.04, -0.96)).normalized()
        cheek_origin = Vector((side * cheek_x, cheek_y, 0.35)) - cheek_dir * 1.8
        cheek_objs.append(
            create_conformed_clay_patch(
                f"_Cheek_{side}",
                "accent",
                head_clay,
                ray_origin_wpf=cheek_origin,
                ray_dir_wpf=cheek_dir,
                rx=cheek_rx,
                ry=cheek_ry,
                center_offset=0.011,
                rim_offset=-0.010,
                rings=4,
                segs=20,
                wrap_focus_depth=1.4,
            )
        )
    parts["Cheeks"] = join_part_group("Cheeks", cheek_objs)

    def build_bead_eye_pair(name_suffix, rx, ry, rz, with_sparkle=False):
        objs = []
        for side in (-1, 1):
            pos, norm = eye_hits[side]
            q_wpf = Vector((0.0, 0.0, 1.0)).rotation_difference(norm)
            eye_basis = q_wpf.to_matrix()

            # 비즈 구슬을 절반 가까이 점토 안쪽에 묻어 3/4 측면에서도 과도하게 튀어나오지 않게 함
            eye_center = pos - norm * (rz * 0.48)
            objs.append(
                create_sculpted_quad_mesh(
                    f"_Eye_{side}_{name_suffix}",
                    "eye_dark",
                    center_wpf=(eye_center.x, eye_center.y, eye_center.z),
                    radii_wpf=(rx, ry, rz),
                    subsurf_levels=2,
                    rotation_quat_wpf=q_wpf,
                )
            )

            # 비즈 곡면 위 정확한 지점에 하이라이트 밀착
            def add_eye_highlight(tag, u_frac, v_frac, hr_x, hr_y, hr_z):
                local_u = u_frac * rx
                local_v = v_frac * ry
                r2 = (local_u / rx) ** 2 + (local_v / ry) ** 2
                local_w = rz * math.sqrt(max(0.06, 1.0 - r2)) + 0.0035
                hl_pos = eye_center + eye_basis @ Vector((local_u, local_v, local_w))
                objs.append(
                    create_sculpted_quad_mesh(
                        f"_{tag}_{side}_{name_suffix}",
                        "eye_highlight",
                        center_wpf=(hl_pos.x, hl_pos.y, hl_pos.z),
                        radii_wpf=(hr_x, hr_y, hr_z),
                        subsurf_levels=1,
                        rotation_quat_wpf=q_wpf,
                    )
                )

            # 상단 메인 반사광 (양쪽 동일한 오른쪽-위 방향 빛 반사로 생동감 표현)
            add_eye_highlight("HlMain", 0.30, 0.34, rx * 0.27, ry * 0.27, 0.011)
            # 하단 서브 반사광
            sub_scale = 0.20 if with_sparkle else 0.145
            add_eye_highlight("HlSub", -0.26, -0.32, rx * sub_scale, ry * sub_scale, 0.009)
            if with_sparkle:
                add_eye_highlight("HlStar", -0.26, 0.40, rx * 0.135, ry * 0.135, 0.008)

        return join_part_group(f"Eyes_{name_suffix}", objs)

    parts["Eyes_Default"] = build_bead_eye_pair(
        "Default",
        rx=eye_rx,
        ry=eye_ry,
        rz=eye_depth,
        with_sparkle=False,
    )
    parts["Eyes_Sparkle"] = build_bead_eye_pair(
        "Sparkle",
        rx=eye_rx * 1.07,
        ry=eye_ry * 1.08,
        rz=eye_depth * 1.04,
        with_sparkle=True,
    )

    def project_curve_points(pts_xy_rad, normal_embed=0.006, cast_dir=(0.0, 0.0, -1.0), anchor_z=0.45):
        out = []
        for px, py, rad in pts_xy_rad:
            _, sp, sn = sample_aimed_surface_wpf(head_clay, (px, py, anchor_z), cast_dir)
            p = sp + sn * normal_embed
            out.append((p.x, p.y, p.z, rad))
        return out

    # Happy (초승달 반달 웃음 — 얼굴 곡면을 완벽히 따라 밀착)
    happy_objs = []
    for side in (-1, 1):
        cx = side * eye_x
        w = eye_rx * 0.96
        arc_xy = [
            (cx - w, eye_y - 0.018, 0.014),
            (cx - w * 0.50, eye_y + 0.036, 0.022),
            (cx, eye_y + 0.052, 0.024),
            (cx + w * 0.50, eye_y + 0.036, 0.022),
            (cx + w, eye_y - 0.018, 0.014),
        ]
        happy_objs.append(
            create_curve_tube(
                f"_HappyArc_{side}",
                "eye_dark",
                project_curve_points(arc_xy, normal_embed=0.006, cast_dir=(-side * 0.14, 0.0, -0.99)),
                resolution_u=4,
                bevel_resolution=1,
            )
        )
    parts["Eyes_Happy"] = join_part_group("Eyes_Happy", happy_objs)

    # Sleepy (나른하게 감긴 반달 눈)
    sleepy_objs = []
    for side in (-1, 1):
        cx = side * eye_x
        w = eye_rx * 0.95
        arc_xy = [
            (cx - w, eye_y + 0.006, 0.013),
            (cx - w * 0.48, eye_y - 0.022, 0.020),
            (cx, eye_y - 0.028, 0.021),
            (cx + w * 0.48, eye_y - 0.022, 0.020),
            (cx + w, eye_y + 0.006, 0.013),
        ]
        sleepy_objs.append(
            create_curve_tube(
                f"_SleepyArc_{side}",
                "eye_dark",
                project_curve_points(arc_xy, normal_embed=0.006, cast_dir=(-side * 0.14, 0.0, -0.99)),
                resolution_u=4,
                bevel_resolution=1,
            )
        )
    parts["Eyes_Sleepy"] = join_part_group("Eyes_Sleepy", sleepy_objs)

    # 입 (Mouth_* — 주둥이 하부 곡면 법선 방향을 반영해 턱 밑으로 말려 숨지 않게 밀착)
    mouth_cast_dir = Vector((0.0, 0.24, -0.971)).normalized() if is_puppy else Vector((0.0, 0.08, -0.996)).normalized()
    pts_l = [(-0.088, mouth_y + 0.006, 0.012), (-0.044, mouth_y - 0.028, 0.014), (0.0, mouth_y + 0.014, 0.014)]
    pts_r = [(0.0, mouth_y + 0.014, 0.014), (0.044, mouth_y - 0.028, 0.014), (0.088, mouth_y + 0.006, 0.012)]
    w_mouth_l = project_curve_points(pts_l, normal_embed=0.007, cast_dir=mouth_cast_dir, anchor_z=0.55)
    w_mouth_r = project_curve_points(pts_r, normal_embed=0.007, cast_dir=mouth_cast_dir, anchor_z=0.55)

    def make_w_mouth(suffix):
        return join_part_group(
            f"Mouth_{suffix}",
            [
                create_curve_tube(f"_ML_{suffix}", "eye_dark", w_mouth_l, 4, 1),
                create_curve_tube(f"_MR_{suffix}", "eye_dark", w_mouth_r, 4, 1),
            ],
        )

    def make_tongue_patch(suffix):
        t_dir = Vector((0.0, 0.34, -0.940)).normalized() if is_puppy else Vector((0.0, 0.10, -0.995)).normalized()
        t_orig = Vector((0.0, mouth_y - 0.026, 0.55)) - t_dir * 1.6
        return create_conformed_clay_patch(
            f"_Tongue_{suffix}",
            "tongue",
            head_clay,
            ray_origin_wpf=t_orig,
            ray_dir_wpf=t_dir,
            rx=0.050 if not is_puppy else 0.062,
            ry=0.046 if not is_puppy else 0.056,
            center_offset=0.016,
            rim_offset=-0.008,
            rings=3,
            segs=16,
            wrap_focus_depth=0.8,
        )

    if is_puppy:
        # 강아지는 Default에서도 살짝 보이는 앙증맞은 미소 + 작은 혀, Sparkle/Happy는 더 선명하게!
        for suffix in ("Default", "Happy", "Sparkle"):
            m_l = create_curve_tube(f"_PupML_{suffix}", "eye_dark", w_mouth_l, 4, 1)
            m_r = create_curve_tube(f"_PupMR_{suffix}", "eye_dark", w_mouth_r, 4, 1)
            tongue = make_tongue_patch(suffix)
            parts[f"Mouth_{suffix}"] = join_part_group(f"Mouth_{suffix}", [m_l, m_r, tongue])

        # 졸릴 때는 얌전한 W 미소만
        s_pts_l = [(-0.072, mouth_y + 0.004, 0.011), (-0.036, mouth_y - 0.020, 0.013), (0.0, mouth_y + 0.008, 0.012)]
        s_pts_r = [(0.0, mouth_y + 0.008, 0.012), (0.036, mouth_y - 0.020, 0.013), (0.072, mouth_y + 0.004, 0.011)]
        parts["Mouth_Sleepy"] = join_part_group(
            "Mouth_Sleepy",
            [
                create_curve_tube("_PupSleepML", "eye_dark", project_curve_points(s_pts_l, 0.005), 4, 1),
                create_curve_tube("_PupSleepMR", "eye_dark", project_curve_points(s_pts_r, 0.005), 4, 1),
            ],
        )
    else:
        parts["Mouth_Default"] = make_w_mouth("Default")
        parts["Mouth_Sparkle"] = make_w_mouth("Sparkle")
        parts["Mouth_Happy"] = join_part_group(
            "Mouth_Happy",
            [
                create_curve_tube("_HML", "eye_dark", w_mouth_l, 4, 1),
                create_curve_tube("_HMR", "eye_dark", w_mouth_r, 4, 1),
                make_tongue_patch("Happy"),
            ],
        )
        s_pts = [(-0.048, mouth_y + 0.003, 0.010), (0.0, mouth_y - 0.013, 0.012), (0.048, mouth_y + 0.003, 0.010)]
        parts["Mouth_Sleepy"] = create_curve_tube(
            "Mouth_Sleepy",
            "eye_dark",
            project_curve_points(s_pts, normal_embed=0.005),
            4,
            1,
        )

    return parts, (0.0, eye_y, eye_blink_z)


# ---------------------------------------------------------------------------
# 1A. 솜뭉치 강아지 (fluffy_puppy) — 일체형 앉은 아기 강아지 클레이 피규어
# ---------------------------------------------------------------------------

def build_fluffy_puppy():
    clear_scene()
    objects = []

    # 1) Body: 목 연결부 + 통통한 배 + 포동한 앉은 허벅지 + 앉은 뒷발이 하나로 녹아든 단일 Metaball 클레이
    body_elements = [
        # 부드러운 목 연결 돔 (머리 밑으로 자연스럽게 들어가 고개 갸웃 시 틈새 없음)
        ((0.0, -0.02, 0.06), (0.34, 0.24, 0.30), None, 2.0),
        # 상체 / 가슴 볼륨
        ((0.0, -0.32, 0.04), (0.42, 0.34, 0.38), None, 2.1),
        # 아래 통통한 배 / 엉덩이 (안정감 있게 둥글고 묵직함)
        ((0.0, -0.58, -0.02), (0.48, 0.32, 0.43), None, 2.2),
        # 포동포동 좌우 앉은 허벅지 (몸통 옆에 부드럽게 융합)
        ((-0.34, -0.66, -0.02), (0.24, 0.25, 0.28), (0.0, 0.22, -0.15), 2.0),
        ((0.34, -0.66, -0.02), (0.24, 0.25, 0.28), (0.0, -0.22, 0.15), 2.0),
        # 앞으로 아담하게 뻗은 앉은 뒷발 (몸통·허벅지와 단일 곡면으로 이어짐)
        ((-0.37, -0.78, 0.18), (0.16, 0.13, 0.21), (0.0, 0.28, 0.0), 2.0),
        ((0.37, -0.78, 0.18), (0.16, 0.13, 0.21), (0.0, -0.28, 0.0), 2.0),
    ]
    body_clay = create_metaball_clay(
        "_PuppyBodyClay",
        "body",
        body_elements,
        mb_res=0.036,
        voxel_size=0.028,
        smooth_iters=14,
        target_tris=1150,
        post_smooth_fn=lambda x, y, z: (x, soft_cushion_floor(y, -0.92, 0.15), z),
    )

    # 가슴 중앙 크림색 클레이 패치 + 앉은 뒷발 크림 발끝 패치 (표면 밀착)
    chest_dir = Vector((0.0, 0.05, -0.998)).normalized()
    chest_patch = create_conformed_clay_patch(
        "_PuppyChestPatch",
        "body_light",
        body_clay,
        ray_origin_wpf=Vector((0.0, -0.38, 0.35)) - chest_dir * 1.6,
        ray_dir_wpf=chest_dir,
        rx=0.33,
        ry=0.33,
        center_offset=0.015,
        rim_offset=-0.012,
        rings=4,
        segs=24,
        wrap_focus_depth=1.5,
        shape_fn=lambda ux, vy: (ux * (0.90 - 0.16 * max(0.0, vy)), vy),
    )
    back_paw_patches = []
    for side in (-1, 1):
        bp_dir = Vector((-side * 0.22, -0.05, -0.974)).normalized()
        back_paw_patches.append(
            create_conformed_clay_patch(
                f"_PuppyBackToe_{side}",
                "body_light",
                body_clay,
                ray_origin_wpf=Vector((side * 0.37, -0.79, 0.26)) - bp_dir * 1.4,
                ray_dir_wpf=bp_dir,
                rx=0.115,
                ry=0.095,
                center_offset=0.012,
                rim_offset=-0.009,
                rings=3,
                segs=16,
                wrap_focus_depth=0.9,
            )
        )
    objects.append(join_part_group("Body", [body_clay, chest_patch] + back_paw_patches))

    # 2) LeftPaw / RightPaw (어깨 뿌리부터 앞발 끝까지 한 덩어리 일체형 클레이 앞발)
    for side, name in ((-1, "LeftPaw"), (1, "RightPaw")):
        paw_elements = [
            ((side * 0.24, -0.34, 0.14), (0.145, 0.145, 0.16), None, 2.0),
            ((side * 0.23, -0.50, 0.25), (0.135, 0.145, 0.15), (-0.42, 0.0, side * 0.08), 2.1),
            ((side * 0.21, -0.66, 0.35), (0.132, 0.125, 0.165), (0.10, -side * 0.12, 0.0), 2.1),
        ]
        paw_clay = create_metaball_clay(
            f"_{name}_Clay",
            "body",
            paw_elements,
            mb_res=0.022,
            voxel_size=0.017,
            smooth_iters=14,
            target_tris=240,
        )
        paw_dir = Vector((0.0, -0.08, -0.996)).normalized()
        paw_tip = create_conformed_clay_patch(
            f"_{name}_Tip",
            "body_light",
            paw_clay,
            ray_origin_wpf=Vector((side * 0.21, -0.67, 0.45)) - paw_dir * 1.4,
            ray_dir_wpf=paw_dir,
            rx=0.105,
            ry=0.088,
            center_offset=0.011,
            rim_offset=-0.009,
            rings=3,
            segs=16,
            wrap_focus_depth=0.7,
        )
        objects.append(join_part_group(name, [paw_clay, paw_tip]))

    # 3) Tail (엉덩이 뒤에서 몽실하게 말려 올라온 일체형 클레이 꼬리)
    tail_pivot = (0.0, -0.56, -0.38)
    tail_elements = [
        ((0.0, -0.56, -0.38), (0.12, 0.12, 0.14), None, 2.0),
        ((0.02, -0.44, -0.53), (0.125, 0.135, 0.135), (0.45, 0.0, 0.0), 2.1),
        ((0.06, -0.26, -0.60), (0.140, 0.155, 0.140), (0.35, 0.0, -0.22), 2.1),
        ((0.10, -0.10, -0.54), (0.125, 0.130, 0.125), None, 2.0),
    ]
    tail_clay = create_metaball_clay(
        "_PuppyTailClay",
        "body",
        tail_elements,
        mb_res=0.022,
        voxel_size=0.018,
        smooth_iters=14,
        target_tris=240,
    )
    tail_dir = Vector((-0.15, -0.45, 0.88)).normalized()
    tail_tip = create_conformed_clay_patch(
        "_PuppyTailTip",
        "body_light",
        tail_clay,
        ray_origin_wpf=Vector((0.10, -0.06, -0.54)) - tail_dir * 1.2,
        ray_dir_wpf=tail_dir,
        rx=0.105,
        ry=0.105,
        center_offset=0.012,
        rim_offset=-0.009,
        rings=3,
        segs=16,
        wrap_focus_depth=0.6,
    )
    objects.append(join_part_group("Tail", [tail_clay, tail_tip]))

    # 4) Head (볼살 넓은 치비 아기 강아지 두상 + 부드럽게 녹아든 일체형 주둥이!)
    head_elements = [
        # 두개골 메인 + 이마
        ((0.0, 0.39, 0.06), (0.68, 0.56, 0.60), None, 2.0),
        # 아래 통통한 양볼 볼륨
        ((-0.26, 0.22, 0.18), (0.46, 0.34, 0.44), (0.0, 0.15, 0.08), 2.1),
        ((0.26, 0.22, 0.18), (0.46, 0.34, 0.44), (0.0, -0.15, -0.08), 2.1),
        # 부드럽게 앞으로 솟아오른 일체형 주둥이(머즐) — 3/4 측면에서도 귀여운 강아지 코 라인 강조
        ((0.0, 0.165, 0.60), (0.315, 0.185, 0.29), (-0.12, 0.0, 0.0), 2.2),
    ]
    head_clay = create_metaball_clay(
        "_PuppyHeadClay",
        "body",
        head_elements,
        mb_res=0.034,
        voxel_size=0.026,
        smooth_iters=14,
        target_tris=1300,
    )

    # 주둥이 크림색 패치 (실제 머즐 곡면에 매끄럽게 밀착!)
    muzzle_dir = Vector((0.0, 0.02, -1.0)).normalized()
    muzzle_patch = create_conformed_clay_patch(
        "_PuppyMuzzlePatch",
        "body_light",
        head_clay,
        ray_origin_wpf=Vector((0.0, 0.155, 0.65)) - muzzle_dir * 1.5,
        ray_dir_wpf=muzzle_dir,
        rx=0.275,
        ry=0.185,
        center_offset=0.015,
        rim_offset=-0.011,
        rings=4,
        segs=24,
        wrap_focus_depth=1.1,
        shape_fn=lambda ux, vy: (ux * (1.02 - 0.14 * max(0.0, vy)), vy * (0.94 + 0.08 * (ux * ux))),
    )

    # 초콜릿 버튼 코 (주둥이 최전방 곡면에 자연스럽게 반쯤 안착)
    _, nose_pos, nose_n = sample_aimed_surface_wpf(head_clay, (0.0, 0.225, 0.65), (0.0, 0.0, -1.0))
    nose_quat = Vector((0.0, 0.0, 1.0)).rotation_difference(nose_n)
    nose_c = nose_pos + nose_n * 0.008
    nose_obj = create_sculpted_quad_mesh(
        "_PuppyNose",
        "nose_dark",
        center_wpf=(nose_c.x, nose_c.y, nose_c.z),
        radii_wpf=(0.072, 0.050, 0.044),
        subsurf_levels=2,
        rotation_quat_wpf=nose_quat,
    )
    hl_pos = nose_c + nose_quat.to_matrix() @ Vector((-0.022, 0.018, 0.038))
    nose_hl = create_sculpted_quad_mesh(
        "_PuppyNoseHl",
        "eye_highlight",
        center_wpf=(hl_pos.x, hl_pos.y, hl_pos.z),
        radii_wpf=(0.019, 0.010, 0.007),
        subsurf_levels=1,
        rotation_quat_wpf=nose_quat,
    )
    objects.append(join_part_group("Head", [head_clay, muzzle_patch, nose_obj, nose_hl]))

    # 5) LeftEar / RightEar (볼을 포근하게 감싸며 단정하게 내려앉은 처진 클레이 귀)
    for side, ear_name in ((-1, "LeftEar"), (1, "RightEar")):
        ear_elements = [
            # 귀 뿌리는 머리 두개골 측면 내부(x ≈ side*0.52)까지 자연스럽게 들어감
            ((side * 0.53, 0.57, 0.07), (0.155, 0.145, 0.155), (0.0, 0.0, side * 0.28), 2.0),
            ((side * 0.65, 0.39, 0.11), (0.135, 0.195, 0.175), (0.06, -side * 0.14, side * 0.18), 2.1),
            ((side * 0.77, 0.14, 0.21), (0.157, 0.225, 0.18), (0.08, -side * 0.16, side * 0.08), 2.2),
            ((side * 0.77, 0.00, 0.21), (0.15, 0.14, 0.165), None, 2.0),
        ]
        ear_obj = create_metaball_clay(
            ear_name,
            "ear_dark",
            ear_elements,
            mb_res=0.022,
            voxel_size=0.018,
            smooth_iters=14,
            target_tris=280,
        )
        objects.append(ear_obj)

    # 6) 얼굴 표정 4종 + 볼터치
    expr_parts, blink_center = build_conformed_face_expressions(
        head_clay,
        eye_x=0.255,
        eye_y=0.335,
        eye_rx=0.104,
        eye_ry=0.118,
        eye_depth=0.052,
        mouth_y=0.125,
        is_puppy=True,
        cheek_x=0.415,
        cheek_y=0.185,
        cheek_rx=0.122,
        cheek_ry=0.070,
    )
    objects.extend(expr_parts.values())

    head_socket_y, head_radius_x = measure_head_crown_and_radius(head_clay, head_center_z=0.10)
    puppy_dir = os.path.join(MODELS_ROOT, "Characters", "fluffy_puppy")
    rig_meta = {
        "coordinateSystem": "WPF_Y_UP_Z_FRONT",
        "unitScale": 1.0,
        "originOffset": {"x": 0.0, "y": 0.0, "z": 0.0},
        "headPivot": {"x": 0.0, "y": -0.02, "z": 0.08},
        "headSocket": {"x": 0.0, "y": head_socket_y, "z": 0.10},
        "headRadiusX": head_radius_x,
        "auraSocket": {"x": 0.0, "y": 0.0, "z": 0.0},
        "shadowY": -0.93,
        "shadowRadiusX": 0.72,
        "shadowRadiusZ": 0.64,
        "eyeBlinkCenter": {"x": 0.0, "y": blink_center[1], "z": blink_center[2]},
        "nameplateAnchor": {"x": 0.0, "y": -0.93, "z": 0.35},
        "leftEarPivot": {"x": -0.55, "y": 0.56, "z": 0.08},
        "rightEarPivot": {"x": 0.55, "y": 0.56, "z": 0.08},
        "leftEarBaseAngle": 0.0,
        "rightEarBaseAngle": 0.0,
        "tailPivot": {"x": tail_pivot[0], "y": tail_pivot[1], "z": tail_pivot[2]},
    }
    export_character(puppy_dir, objects, rig_meta)


# ---------------------------------------------------------------------------
# 1B. 치비 고양이 (chibi_cat) — 일체형 앉은 네코 클레이 피규어
# ---------------------------------------------------------------------------

def build_chibi_cat():
    clear_scene()
    objects = []

    # 1) Body: 목-어깨-통통배-앉은허벅지-앉은뒷발 단일 클레이 융합
    body_elements = [
        ((0.0, -0.02, 0.02), (0.36, 0.24, 0.32), None, 2.0),
        ((0.0, -0.34, 0.01), (0.43, 0.33, 0.39), None, 2.1),
        ((0.0, -0.58, -0.02), (0.49, 0.33, 0.43), None, 2.2),
        ((-0.31, -0.68, 0.02), (0.22, 0.23, 0.26), (0.0, 0.20, -0.10), 2.0),
        ((0.31, -0.68, 0.02), (0.22, 0.23, 0.26), (0.0, -0.20, 0.10), 2.0),
        ((-0.34, -0.79, 0.18), (0.155, 0.12, 0.21), (0.0, 0.24, 0.0), 2.0),
        ((0.34, -0.79, 0.18), (0.155, 0.12, 0.21), (0.0, -0.24, 0.0), 2.0),
    ]
    body_clay = create_metaball_clay(
        "_CatBodyClay",
        "body",
        body_elements,
        mb_res=0.036,
        voxel_size=0.028,
        smooth_iters=14,
        target_tris=1100,
        post_smooth_fn=lambda x, y, z: (x, soft_cushion_floor(y, -0.92, 0.15), z),
    )
    belly_dir = Vector((0.0, 0.04, -0.998)).normalized()
    belly_patch = create_conformed_clay_patch(
        "_CatBellyPatch",
        "body_light",
        body_clay,
        ray_origin_wpf=Vector((0.0, -0.42, 0.35)) - belly_dir * 1.5,
        ray_dir_wpf=belly_dir,
        rx=0.31,
        ry=0.32,
        center_offset=0.015,
        rim_offset=-0.012,
        rings=4,
        segs=24,
        wrap_focus_depth=1.5,
        shape_fn=lambda ux, vy: (ux * (0.90 - 0.14 * max(0.0, vy)), vy),
    )
    objects.append(join_part_group("Body", [body_clay, belly_patch]))

    # 2) LeftPaw / RightPaw (통통한 일체형 벙어리 고양이 앞발 + 은은한 핑크 젤리 포인트)
    for side, name in ((-1, "LeftPaw"), (1, "RightPaw")):
        paw_elements = [
            ((side * 0.24, -0.36, 0.12), (0.135, 0.135, 0.15), None, 2.0),
            ((side * 0.25, -0.52, 0.24), (0.130, 0.135, 0.145), (-0.42, 0.0, -side * 0.12), 2.1),
            ((side * 0.22, -0.63, 0.36), (0.130, 0.115, 0.155), (0.15, -side * 0.14, 0.0), 2.1),
        ]
        paw_clay = create_metaball_clay(
            f"_{name}_Clay",
            "body",
            paw_elements,
            mb_res=0.022,
            voxel_size=0.017,
            smooth_iters=14,
            target_tris=230,
        )
        pad_dir = Vector((0.0, -0.05, -0.998)).normalized()
        paw_pad = create_conformed_clay_patch(
            f"_{name}_Pad",
            "accent",
            paw_clay,
            ray_origin_wpf=Vector((side * 0.22, -0.64, 0.45)) - pad_dir * 1.3,
            ray_dir_wpf=pad_dir,
            rx=0.056,
            ry=0.046,
            center_offset=0.010,
            rim_offset=-0.008,
            rings=3,
            segs=14,
            wrap_focus_depth=0.5,
        )
        objects.append(join_part_group(name, [paw_clay, paw_pad]))

    # 3) Tail (엉덩이 뒤에서 부드럽게 S자로 올라오는 클레이 고양이 꼬리)
    tail_pivot = (0.0, -0.60, -0.36)
    tail_tube = create_curve_tube(
        "_CatTailCurve",
        "body",
        [
            (0.0, -0.60, -0.32, 0.108),
            (0.08, -0.57, -0.50, 0.102),
            (0.19, -0.45, -0.66, 0.105),
            (0.26, -0.25, -0.76, 0.110),
            (0.35, -0.04, -0.79, 0.108),
            (0.38, 0.18, -0.64, 0.095),
        ],
        resolution_u=5,
        bevel_resolution=2,
    )
    tail_tip_ball = create_sculpted_quad_mesh("_CatTailTipSphere", "body", (0.38, 0.18, -0.64), (0.10, 0.10, 0.10), subsurf_levels=1)
    tail_clay = create_voxel_fused_clay(
        "_CatTailClay",
        "body",
        [tail_tube, tail_tip_ball],
        voxel_size=0.019,
        smooth_iters=12,
        target_tris=240,
    )
    tail_tip_patch = create_conformed_clay_patch(
        "_CatTailTipPatch",
        "body_light",
        tail_clay,
        ray_origin_wpf=(0.38, 0.85, -0.64),
        ray_dir_wpf=(0.0, -1.0, 0.0),
        rx=0.095,
        ry=0.095,
        center_offset=0.011,
        rim_offset=-0.008,
        rings=3,
        segs=14,
        wrap_focus_depth=0.5,
    )
    objects.append(join_part_group("Tail", [tail_clay, tail_tip_patch]))

    # 4) Head (통통한 만두형 볼살 + 부드럽게 융합된 일체형 고양이 머즐)
    head_elements = [
        ((0.0, 0.39, 0.01), (0.77, 0.56, 0.65), None, 2.0),
        ((-0.26, 0.24, 0.14), (0.50, 0.34, 0.48), None, 2.1),
        ((0.26, 0.24, 0.14), (0.50, 0.34, 0.48), None, 2.1),
        ((0.0, 0.22, 0.56), (0.25, 0.17, 0.20), None, 2.15),
    ]
    head_clay = create_metaball_clay(
        "_CatHeadClay",
        "body",
        head_elements,
        mb_res=0.036,
        voxel_size=0.028,
        smooth_iters=15,
        target_tris=1250,
    )

    # 작은 핑크 코 (일체형 머즐 위에 안착)
    _, nose_pos, nose_n = sample_aimed_surface_wpf(head_clay, (0.0, 0.275, 0.60), (0.0, 0.0, -1.0))
    nose_quat = Vector((0.0, 0.0, 1.0)).rotation_difference(nose_n)
    nose_c = nose_pos + nose_n * 0.006
    cat_nose = create_sculpted_quad_mesh(
        "_CatNose",
        "accent",
        center_wpf=(nose_c.x, nose_c.y, nose_c.z),
        radii_wpf=(0.050, 0.036, 0.030),
        subsurf_levels=1,
        sculpt_fn=lambda x, y, z, nx, ny, nz: (x * (1.0 + max(0.0, ny) * 0.25 - max(0.0, -ny) * 0.30), y, z),
        rotation_quat_wpf=nose_quat,
    )

    # 볼 곡면을 자연스럽게 따라가는 아담한 수염 2쌍 (정확한 볼 측면 레이캐스트)
    whisker_objs = []
    for side in (-1, 1):
        for w_idx, (wy0, wy1) in enumerate(((0.265, 0.290), (0.205, 0.185))):
            pts = []
            for frac, wx_f in ((0.0, 0.52), (0.5, 0.62), (1.0, 0.72)):
                wy = wy0 + (wy1 - wy0) * frac
                w_dir = Vector((-side * 0.42, 0.0, -0.907)).normalized()
                _, sp, sn = sample_aimed_surface_wpf(head_clay, (side * wx_f, wy, 0.25), w_dir, distance=1.6)
                p = sp + sn * (0.003 if frac < 0.9 else 0.009)
                pts.append((p.x, p.y, p.z, 0.011 - frac * 0.004))
            whisker_objs.append(create_curve_tube(f"_Whisker_{side}_{w_idx}", "eye_dark", pts, resolution_u=2, bevel_resolution=0))

    objects.append(join_part_group("Head", [head_clay, cat_nose] + whisker_objs))

    # 5) LeftEar / RightEar (도톰하고 둥근 정삼각 비율 클레이 고양이 귀 + 선명한 핑크 속귀)
    for side, ear_name in ((-1, "LeftEar"), (1, "RightEar")):
        ear_elements = [
            ((side * 0.43, 0.79, 0.01), (0.22, 0.16, 0.17), (0.0, 0.0, -side * 0.22), 2.0),
            ((side * 0.49, 0.94, 0.02), (0.165, 0.145, 0.135), (0.0, 0.0, -side * 0.28), 2.1),
            ((side * 0.61, 1.13, 0.015), (0.092, 0.095, 0.092), None, 2.0),
        ]
        outer_ear = create_metaball_clay(
            f"_{ear_name}_Outer",
            "body",
            ear_elements,
            mb_res=0.022,
            voxel_size=0.018,
            smooth_iters=14,
            target_tris=230,
        )
        ear_in_dir = Vector((-side * 0.06, -0.04, -0.997)).normalized()
        inner_ear = create_conformed_clay_patch(
            f"_{ear_name}_Inner",
            "accent",
            outer_ear,
            ray_origin_wpf=Vector((side * 0.49, 0.915, 0.12)) - ear_in_dir * 1.2,
            ray_dir_wpf=ear_in_dir,
            rx=0.124,
            ry=0.126,
            center_offset=0.013,
            rim_offset=-0.010,
            rings=3,
            segs=16,
            wrap_focus_depth=0.6,
            shape_fn=lambda ux, vy: (ux * (1.0 - 0.48 * max(0.0, vy)), vy),
        )
        objects.append(join_part_group(ear_name, [outer_ear, inner_ear]))

    # 6) 얼굴 표정 4종 + 볼터치
    expr_parts, blink_center = build_conformed_face_expressions(
        head_clay,
        eye_x=0.282,
        eye_y=0.365,
        eye_rx=0.104,
        eye_ry=0.118,
        eye_depth=0.052,
        mouth_y=0.210,
        is_puppy=False,
        cheek_x=0.415,
        cheek_y=0.230,
        cheek_rx=0.124,
        cheek_ry=0.072,
    )
    objects.extend(expr_parts.values())

    head_socket_y, head_radius_x = measure_head_crown_and_radius(head_clay, head_center_z=0.01)
    cat_dir = os.path.join(MODELS_ROOT, "Characters", "chibi_cat")
    rig_meta = {
        "coordinateSystem": "WPF_Y_UP_Z_FRONT",
        "unitScale": 1.0,
        "originOffset": {"x": 0.0, "y": 0.0, "z": 0.0},
        "headPivot": {"x": 0.0, "y": 0.0, "z": 0.01},
        "headSocket": {"x": 0.0, "y": head_socket_y, "z": 0.02},
        "headRadiusX": head_radius_x,
        "auraSocket": {"x": 0.0, "y": 0.0, "z": 0.0},
        "shadowY": -0.93,
        "shadowRadiusX": 0.72,
        "shadowRadiusZ": 0.64,
        "eyeBlinkCenter": {"x": 0.0, "y": blink_center[1], "z": blink_center[2]},
        "nameplateAnchor": {"x": 0.0, "y": -0.93, "z": 0.35},
        "leftEarPivot": {"x": -0.44, "y": 0.80, "z": 0.0},
        "rightEarPivot": {"x": 0.44, "y": 0.80, "z": 0.0},
        "leftEarBaseAngle": 0.0,
        "rightEarBaseAngle": 0.0,
        "tailPivot": {"x": tail_pivot[0], "y": tail_pivot[1], "z": tail_pivot[2]},
    }
    export_character(cat_dir, objects, rig_meta)


# ---------------------------------------------------------------------------
# 1C. 솜구름 (fluffy_cloud) — 선명한 몽실 봉우리가 살아 있는 일체형 마시멜로 구름
# ---------------------------------------------------------------------------

def build_fluffy_cloud():
    clear_scene()
    objects = []

    # 1) Head: 양옆·위·아래가 뭉게구름 3봉우리 아치로 둥글게 퍼진 매끈한 클레이 ☁️ 구름
    cloud_elements = [
        # 중앙 포동한 얼굴 쿠션
        ((0.0, -0.02, 0.22), (0.69, 0.44, 0.60), None, 2.0),
        # 좌우 통통한 볼 구름 봉우리
        ((-0.72, -0.03, 0.00), (0.37, 0.34, 0.38), None, 2.5),
        ((0.72, -0.03, 0.00), (0.37, 0.34, 0.38), None, 2.5),
        # 상단 3봉우리 구름 왕관 (양옆 봉우리가 높고 풍성해서 뾰족한 유령처럼 보이지 않음)
        ((-0.44, 0.34, -0.06), (0.37, 0.33, 0.36), None, 2.5),
        ((0.48, 0.40, -0.06), (0.37, 0.33, 0.36), None, 2.5),
        ((-0.08, 0.53, -0.02), (0.43, 0.34, 0.40), None, 2.5),
        # 하단 3봉우리 구름 쿠션
        ((-0.46, -0.34, -0.02), (0.36, 0.28, 0.38), None, 2.5),
        ((0.46, -0.34, -0.02), (0.36, 0.28, 0.38), None, 2.5),
        ((0.0, -0.32, 0.04), (0.44, 0.27, 0.42), None, 2.1),
        # 후면 입체 볼륨
        ((0.0, 0.02, -0.26), (0.60, 0.37, 0.42), None, 2.0),
    ]
    # 각 방향에서 타원체들의 부드러운 합집합 표면을 찾아, 모서리 없이 봉우리가 이어지게 한다.
    def cloud_surface(x, y, z, nx, ny, nz):
        low, high = 0.0, 1.5
        blend = 0.16
        for _ in range(16):
            radius = (low + high) * 0.5
            distance = 2.0
            for (cx, cy, cz), (rx, ry, rz), _, _ in cloud_elements:
                dx, dy, dz = (nx*radius-cx)/rx, (ny*radius-cy)/ry, (nz*radius-cz)/rz
                d = (math.sqrt(dx*dx + dy*dy + dz*dz) - 1.0) * min(rx, ry, rz)
                h = max(blend - abs(distance - d), 0.0) / blend
                distance = min(distance, d) - blend * h*h * 0.25
            if distance < 0:
                low = radius
            else:
                high = radius
        return nx * radius, ny * radius, nz * radius

    cloud_clay = create_sculpted_quad_mesh("Head", "body", (0, 0, 0), (1, 1, 1),
                                            cubes_cuts=1, subsurf_levels=3, sculpt_fn=cloud_surface)
    remesh_smooth_decimate(cloud_clay, voxel_size=None, smooth_iters=2, target_tris=1450)
    objects.append(cloud_clay)

    # 2) LeftArm / RightArm (일체형 클레이 천사 날개)
    for side, name in ((-1, "LeftArm"), (1, "RightArm")):
        wing_elements = [
            ((side * 0.94, 0.05, -0.03), (0.14, 0.115, 0.105), None, 2.0),
            ((side * 1.15, 0.13, -0.04), (0.21, 0.115, 0.09), (0.0, -side * 0.10, side * 0.24), 2.1),
            ((side * 1.08, -0.02, -0.04), (0.155, 0.095, 0.085), (0.0, -side * 0.10, side * 0.04), 2.0),
        ]
        wing = create_metaball_clay(
            name,
            "body_light",
            wing_elements,
            mb_res=0.022,
            voxel_size=0.018,
            smooth_iters=14,
            target_tris=240,
        )
        objects.append(wing)

    # 3) 얼굴 표정 4종 + 볼터치
    expr_parts, blink_center = build_conformed_face_expressions(
        cloud_clay,
        eye_x=0.255,
        eye_y=0.055,
        eye_rx=0.102,
        eye_ry=0.116,
        eye_depth=0.052,
        mouth_y=-0.085,
        is_puppy=False,
        cheek_x=0.415,
        cheek_y=-0.065,
        cheek_rx=0.125,
        cheek_ry=0.072,
    )
    objects.extend(expr_parts.values())

    head_socket_y, head_radius_x = measure_head_crown_and_radius(cloud_clay, head_center_z=-0.01)
    cloud_dir = os.path.join(MODELS_ROOT, "Characters", "fluffy_cloud")
    rig_meta = {
        "coordinateSystem": "WPF_Y_UP_Z_FRONT",
        "unitScale": 1.0,
        "originOffset": {"x": 0.0, "y": 0.0, "z": 0.0},
        "headPivot": {"x": 0.0, "y": 0.0, "z": 0.0},
        "headSocket": {"x": 0.0, "y": head_socket_y, "z": -0.01},
        "headRadiusX": head_radius_x,
        "auraSocket": {"x": 0.0, "y": 0.0, "z": 0.0},
        "shadowY": -0.94,
        "shadowRadiusX": 0.78,
        "shadowRadiusZ": 0.64,
        "eyeBlinkCenter": {"x": 0.0, "y": blink_center[1], "z": blink_center[2]},
        "nameplateAnchor": {"x": 0.0, "y": -0.92, "z": 0.35},
        "leftArmPivot": {"x": -0.92, "y": 0.05, "z": -0.03},
        "rightArmPivot": {"x": 0.92, "y": 0.05, "z": -0.03},
    }
    export_character(cloud_dir, objects, rig_meta)


# ---------------------------------------------------------------------------
# 1D. 말랑 모찌 정령 (mochi_spirit) — 포동포동 눌려 앉은 찹쌀떡 클레이 피규어
# ---------------------------------------------------------------------------

def build_mochi_spirit():
    clear_scene()
    objects = []

    # 1) Head: 아래가 넉넉하고 볼록 퍼지며 위는 앙증맞은 찹쌀떡 실루엣 + 뒤 꽁지 꼬리
    mochi_elements = [
        # 바닥에 말랑하게 퍼져 앉은 넓은 하단 베이스
        ((0.0, -0.34, 0.02), (0.92, 0.41, 0.82), None, 2.2),
        # 위쪽으로 둥글게 모이는 도톰한 떡 돔
        ((0.0, 0.06, 0.00), (0.75, 0.54, 0.70), None, 2.0),
        # 뒤쪽 앙증맞은 꽁지 꼬리
        ((0.0, -0.46, -0.78), (0.16, 0.15, 0.17), None, 2.0),
    ]
    mochi_clay = create_metaball_clay(
        "Head",
        "body",
        mochi_elements,
        mb_res=0.034,
        voxel_size=0.026,
        smooth_iters=14,
        target_tris=1200,
        post_smooth_fn=lambda x, y, z: (x, soft_cushion_floor(y, -0.80, 0.16), z),
    )
    objects.append(mochi_clay)

    # 2) LeftArm / RightArm (물방울처럼 통통하고 말랑한 클레이 미니 팔)
    for side, name in ((-1, "LeftArm"), (1, "RightArm")):
        arm_elements = [
            ((side * 0.79, -0.24, 0.19), (0.17, 0.145, 0.15), None, 2.0),
            ((side * 1.02, -0.27, 0.22), (0.155, 0.12, 0.135), (0.0, -side * 0.15, side * 0.22), 2.1),
        ]
        arm = create_metaball_clay(
            name,
            "body",
            arm_elements,
            mb_res=0.022,
            voxel_size=0.017,
            smooth_iters=14,
            target_tris=200,
        )
        objects.append(arm)

    # 3) 얼굴 표정 4종 + 볼터치
    expr_parts, blink_center = build_conformed_face_expressions(
        mochi_clay,
        eye_x=0.255,
        eye_y=0.012,
        eye_rx=0.104,
        eye_ry=0.118,
        eye_depth=0.052,
        mouth_y=-0.160,
        is_puppy=False,
        cheek_x=0.425,
        cheek_y=-0.140,
        cheek_rx=0.132,
        cheek_ry=0.076,
    )
    objects.extend(expr_parts.values())

    head_socket_y, head_radius_x = measure_head_crown_and_radius(mochi_clay, head_center_z=0.0)
    mochi_dir = os.path.join(MODELS_ROOT, "Characters", "mochi_spirit")
    rig_meta = {
        "coordinateSystem": "WPF_Y_UP_Z_FRONT",
        "unitScale": 1.0,
        "originOffset": {"x": 0.0, "y": 0.0, "z": 0.0},
        "headPivot": {"x": 0.0, "y": -0.14, "z": 0.0},
        "headSocket": {"x": 0.0, "y": head_socket_y, "z": 0.0},
        "headRadiusX": head_radius_x,
        "auraSocket": {"x": 0.0, "y": 0.0, "z": 0.0},
        "shadowY": -0.82,
        "shadowRadiusX": 0.88,
        "shadowRadiusZ": 0.80,
        "eyeBlinkCenter": {"x": 0.0, "y": blink_center[1], "z": blink_center[2]},
        "nameplateAnchor": {"x": 0.0, "y": -0.86, "z": 0.35},
        "leftArmPivot": {"x": -0.82, "y": -0.25, "z": 0.18},
        "rightArmPivot": {"x": 0.82, "y": -0.25, "z": 0.18},
    }
    export_character(mochi_dir, objects, rig_meta)


# ---------------------------------------------------------------------------
# 1E. 치이카와 3종 공통 헬퍼 (눈썹, 볼터치 빗금 ////, 귀여운 w 입 + 혀)
# ---------------------------------------------------------------------------

def build_chiikawa_style_face(
    head_clay,
    eye_x=0.225,
    eye_y=0.24,
    eye_rx=0.090,
    eye_ry=0.100,
    eye_depth=0.048,
    mouth_y=0.095,
    cheek_x=0.39,
    cheek_y=0.135,
    cheek_rx=0.125,
    cheek_ry=0.072,
    brow_style="short_arc",
    open_tongue_default=True,
    include_nose_tick=True,
):
    """하치와레·치이카와·우사기 특유의 볼터치 4줄 빗금(////), 눈썹, 비즈 눈, 귀여운 입+혀 생성."""
    expr_parts, blink_center = build_conformed_face_expressions(
        head_clay,
        eye_x=eye_x,
        eye_y=eye_y,
        eye_rx=eye_rx,
        eye_ry=eye_ry,
        eye_depth=eye_depth,
        mouth_y=mouth_y,
        is_puppy=open_tongue_default,
        cheek_x=cheek_x,
        cheek_y=cheek_y,
        cheek_rx=cheek_rx,
        cheek_ry=cheek_ry,
    )

    def project_surface_tube(name, pts_xy_rad, normal_embed=0.014, cast_dir=(0.0, 0.0, -1.0)):
        out = []
        for px, py, rad in pts_xy_rad:
            _, sp, sn = sample_aimed_surface_wpf(head_clay, (px, py, 0.48), cast_dir)
            p = sp + sn * normal_embed
            out.append((p.x, p.y, p.z, rad))
        return create_curve_tube(name, "eye_dark", out, resolution_u=2, bevel_resolution=0)

    # 1) 양볼 위 4줄 대각선 빗금 (////)
    hatch_objs = []
    for side in (-1, 1):
        c_dir = Vector((-side * 0.28, -0.04, -0.96)).normalized()
        for h_idx in range(4):
            u_off = (h_idx - 1.5) * (cheek_rx * 0.36)
            hx0 = side * cheek_x + u_off - 0.011
            hy0 = cheek_y - cheek_ry * 0.34
            hx1 = side * cheek_x + u_off + 0.011
            hy1 = cheek_y + cheek_ry * 0.34
            hatch_objs.append(
                project_surface_tube(
                    f"_CheekHatch_{side}_{h_idx}",
                    [(hx0, hy0, 0.0072), ((hx0 + hx1) * 0.5, (hy0 + hy1) * 0.5, 0.0080), (hx1, hy1, 0.0072)],
                    normal_embed=0.013,
                    cast_dir=c_dir,
                )
            )
    expr_parts["Cheeks"] = join_part_group("Cheeks", [expr_parts["Cheeks"]] + hatch_objs)

    # 2) 캐릭터별 눈썹 및 작은 코/입술 포인트 (Head에 합쳐질 리스트 반환)
    extra_head_objs = []
    for side in (-1, 1):
        b_dir = Vector((-side * 0.14, 0.05, -0.99)).normalized()
        if brow_style == "tick":
            # 하치와레: 눈 위 짧은 점/틱 눈썹
            bx = side * (eye_x * 0.88)
            by = eye_y + 0.145
            extra_head_objs.append(
                project_surface_tube(
                    f"_BrowTick_{side}",
                    [
                        (bx - side * 0.022, by - 0.006, 0.009),
                        (bx, by + 0.004, 0.010),
                        (bx + side * 0.022, by + 0.010, 0.009),
                    ],
                    normal_embed=0.006,
                    cast_dir=b_dir,
                )
            )
        elif brow_style == "high_arch":
            # 우사기: 시원하게 둥근 아치형 눈썹
            bx = side * (eye_x * 1.02)
            by = eye_y + 0.175
            extra_head_objs.append(
                project_surface_tube(
                    f"_BrowArch_{side}",
                    [
                        (bx - side * 0.095, by - 0.015, 0.009),
                        (bx - side * 0.020, by + 0.032, 0.012),
                        (bx + side * 0.055, by + 0.012, 0.011),
                        (bx + side * 0.105, by - 0.048, 0.009),
                    ],
                    normal_embed=0.006,
                    cast_dir=b_dir,
                )
            )
        elif brow_style == "kani_double":
            # 카니: 각 눈 위 짧은 2줄 이중 눈썹 (살짝 안쪽 아래로 기운 단정한 틱 2개)
            bx = side * (eye_x * 0.90)
            for idx, (y_off, half_w, rad) in enumerate(((0.146, 0.032, 0.0082), (0.184, 0.026, 0.0078))):
                by = eye_y + y_off
                extra_head_objs.append(
                    project_surface_tube(
                        f"_BrowKani_{side}_{idx}",
                        [
                            (bx - side * half_w, by - 0.007, rad * 0.92),
                            (bx, by + 0.002, rad),
                            (bx + side * half_w, by + 0.011, rad * 0.92),
                        ],
                        normal_embed=0.006,
                        cast_dir=b_dir,
                    )
                )
        else:
            # 치이카와: 순하고 짧은 아치 눈썹
            bx = side * (eye_x * 0.94)
            by = eye_y + 0.150
            extra_head_objs.append(
                project_surface_tube(
                    f"_BrowSoft_{side}",
                    [
                        (bx - side * 0.048, by + 0.004, 0.009),
                        (bx, by + 0.014, 0.011),
                        (bx + side * 0.048, by - 0.006, 0.009),
                    ],
                    normal_embed=0.006,
                    cast_dir=b_dir,
                )
            )

    # 입 바로 위 아주 작고 귀여운 코 점 + 턱 아래 작은 미소 보조선
    if include_nose_tick:
        extra_head_objs.append(
            project_surface_tube(
                "_TinyNoseTick",
                [(-0.014, mouth_y + 0.032, 0.008), (0.0, mouth_y + 0.026, 0.010), (0.014, mouth_y + 0.032, 0.008)],
                normal_embed=0.007,
            )
        )
    extra_head_objs.append(
        project_surface_tube(
            "_ChinDimple",
            [(-0.022, mouth_y - 0.068, 0.007), (0.0, mouth_y - 0.073, 0.008), (0.022, mouth_y - 0.068, 0.007)],
            normal_embed=0.006,
        )
    )

    return expr_parts, blink_center, extra_head_objs


# ---------------------------------------------------------------------------
# 1F. 하치와레 (hachiware) — 파란 8자 가르마 무늬와 쫑긋한 고양이 귀·파란 꼬리
# ---------------------------------------------------------------------------

def build_hachiware():
    clear_scene()
    objects = []

    # 1) Body: 포동포동한 아랫배와 짧은 두 발이 하나로 이어진 눈사람/찹쌀떡형 바디
    body_elements = [
        ((0.0, 0.02, 0.00), (0.48, 0.28, 0.44), None, 2.0),
        ((0.0, -0.34, 0.01), (0.52, 0.38, 0.48), None, 2.15),
        ((0.0, -0.60, 0.00), (0.54, 0.30, 0.48), None, 2.1),
        # 아래쪽으로 귀엽게 톡 튀어나온 짧은 두 발
        ((-0.26, -0.82, 0.06), (0.14, 0.15, 0.16), (0.0, 0.10, -0.08), 2.0),
        ((0.26, -0.82, 0.06), (0.14, 0.15, 0.16), (0.0, -0.10, 0.08), 2.0),
    ]
    body_clay = create_metaball_clay(
        "Body",
        "body",
        body_elements,
        mb_res=0.034,
        voxel_size=0.026,
        smooth_iters=14,
        target_tris=1050,
        post_smooth_fn=lambda x, y, z: (x, soft_cushion_floor(y, -0.94, 0.10), z),
    )
    objects.append(body_clay)

    # 2) LeftArm / RightArm (몸통 양옆에 앙증맞게 달린 짧은 찹쌀 팔)
    for side, name in ((-1, "LeftArm"), (1, "RightArm")):
        arm_elements = [
            ((side * 0.54, -0.28, 0.10), (0.125, 0.115, 0.120), None, 2.0),
            ((side * 0.67, -0.33, 0.16), (0.120, 0.105, 0.115), (0.0, -side * 0.18, side * 0.25), 2.1),
        ]
        objects.append(
            create_metaball_clay(
                name,
                "body",
                arm_elements,
                mb_res=0.022,
                voxel_size=0.017,
                smooth_iters=14,
                target_tris=190,
            )
        )

    # 3) Tail (하치와레 특유의 하늘색 고양이 꼬리)
    tail_pivot = (0.0, -0.66, -0.44)
    tail_elements = [
        ((0.0, -0.66, -0.44), (0.105, 0.100, 0.120), None, 2.0),
        ((-0.14, -0.63, -0.58), (0.110, 0.105, 0.130), (0.15, -0.35, 0.0), 2.1),
        ((-0.26, -0.56, -0.64), (0.115, 0.110, 0.125), None, 2.0),
    ]
    objects.append(
        create_metaball_clay(
            "Tail",
            "hachi_blue",
            tail_elements,
            mb_res=0.022,
            voxel_size=0.017,
            smooth_iters=14,
            target_tris=200,
        )
    )

    # 4) Head (만두처럼 둥글고 통통한 두상 + 이마 양옆으로 갈라진 하늘색 8자 가르마 무늬 패치)
    head_elements = [
        ((0.0, 0.34, 0.00), (0.75, 0.58, 0.64), None, 2.05),
        ((-0.24, 0.22, 0.10), (0.45, 0.35, 0.44), None, 2.1),
        ((0.24, 0.22, 0.10), (0.45, 0.35, 0.44), None, 2.1),
    ]
    head_clay = create_metaball_clay(
        "_HachiHeadClay",
        "body",
        head_elements,
        mb_res=0.034,
        voxel_size=0.026,
        smooth_iters=15,
        target_tris=1200,
    )

    # 머리 표면을 가르마 곡선을 따라 나누어 정수리·뒤통수까지 이어지는 색 경계를 만든다.
    def hairline(x, depth):
        front = min(1.0, max(0.0, (depth + 0.12) / 0.55))
        crest = math.exp(-(abs(x) / 0.235) ** 1.55)
        return 0.26 + front * (0.20 + 0.35 * crest)

    head_clay.data.materials.append(ensure_material("hachi_blue"))
    bm = bmesh.new()
    bm.from_mesh(head_clay.data)
    for v in bm.verts:
        v.co.z -= hairline(v.co.x, -v.co.y)
    bmesh.ops.bisect_plane(bm, geom=list(bm.verts) + list(bm.edges) + list(bm.faces),
                           plane_co=(0, 0, 0), plane_no=(0, 0, 1), dist=1e-6)
    for face in bm.faces:
        face.material_index = int(face.calc_center_median().z > 0)
    for v in bm.verts:
        v.co.z += hairline(v.co.x, -v.co.y)
    bm.to_mesh(head_clay.data)
    bm.free()
    head_clay.data.update()

    expr_parts, blink_center, extra_head_objs = build_chiikawa_style_face(
        head_clay,
        eye_x=0.244,
        eye_y=0.275,
        eye_rx=0.090,
        eye_ry=0.100,
        eye_depth=0.048,
        mouth_y=0.135,
        cheek_x=0.395,
        cheek_y=0.175,
        cheek_rx=0.125,
        cheek_ry=0.070,
        brow_style="tick",
        open_tongue_default=True,
    )
    objects.append(join_part_group("Head", [head_clay] + extra_head_objs))

    # 5) LeftEar / RightEar (하늘색 쫑긋한 고양이 귀)
    for side, ear_name in ((-1, "LeftEar"), (1, "RightEar")):
        ear_elements = [
            ((side * 0.38, 0.75, 0.02), (0.21, 0.16, 0.16), (0.0, 0.0, -side * 0.18), 2.0),
            ((side * 0.45, 0.90, 0.02), (0.155, 0.145, 0.130), (0.0, 0.0, -side * 0.24), 2.1),
            ((side * 0.51, 1.02, 0.02), (0.090, 0.095, 0.088), None, 2.0),
        ]
        objects.append(
            create_metaball_clay(
                ear_name,
                "hachi_blue",
                ear_elements,
                mb_res=0.022,
                voxel_size=0.017,
                smooth_iters=14,
                target_tris=220,
            )
        )

    objects.extend(expr_parts.values())

    head_socket_y, head_radius_x = measure_head_crown_and_radius(head_clay, head_center_z=0.0)
    out_dir = os.path.join(MODELS_ROOT, "Characters", "hachiware")
    rig_meta = {
        "coordinateSystem": "WPF_Y_UP_Z_FRONT",
        "unitScale": 1.0,
        "originOffset": {"x": 0.0, "y": 0.0, "z": 0.0},
        "headPivot": {"x": 0.0, "y": 0.02, "z": 0.0},
        "headSocket": {"x": 0.0, "y": head_socket_y, "z": 0.01},
        "headRadiusX": head_radius_x,
        "auraSocket": {"x": 0.0, "y": 0.0, "z": 0.0},
        "shadowY": -0.94,
        "shadowRadiusX": 0.70,
        "shadowRadiusZ": 0.62,
        "eyeBlinkCenter": {"x": 0.0, "y": blink_center[1], "z": blink_center[2]},
        "nameplateAnchor": {"x": 0.0, "y": -0.94, "z": 0.35},
        "leftEarPivot": {"x": -0.38, "y": 0.76, "z": 0.02},
        "rightEarPivot": {"x": 0.38, "y": 0.76, "z": 0.02},
        "leftEarBaseAngle": 0.0,
        "rightEarBaseAngle": 0.0,
        "leftArmPivot": {"x": -0.54, "y": -0.28, "z": 0.10},
        "rightArmPivot": {"x": 0.54, "y": -0.28, "z": 0.10},
        "tailPivot": {"x": tail_pivot[0], "y": tail_pivot[1], "z": tail_pivot[2]},
    }
    export_character(out_dir, objects, rig_meta)


# ---------------------------------------------------------------------------
# 1G. 치이카와 (chiikawa) — 동글동글 하얀 찹쌀 몸통과 작은 둥근 곰/햄스터 귀
# ---------------------------------------------------------------------------

def build_chiikawa():
    clear_scene()
    objects = []

    # 1) Body: 치이카와 특유의 아담하고 통통한 찹쌀떡 몸통 + 가운데로 모인 짧은 두 발
    body_elements = [
        ((0.0, 0.00, 0.00), (0.48, 0.27, 0.44), None, 2.0),
        ((0.0, -0.35, 0.01), (0.49, 0.36, 0.46), None, 2.15),
        ((0.0, -0.59, 0.00), (0.47, 0.26, 0.43), None, 2.1),
        ((-0.18, -0.82, 0.05), (0.135, 0.145, 0.155), (0.0, 0.06, -0.05), 2.0),
        ((0.18, -0.82, 0.05), (0.135, 0.145, 0.155), (0.0, -0.06, 0.05), 2.0),
    ]
    body_clay = create_metaball_clay(
        "Body",
        "body",
        body_elements,
        mb_res=0.034,
        voxel_size=0.026,
        smooth_iters=14,
        target_tris=1050,
        post_smooth_fn=lambda x, y, z: (x, soft_cushion_floor(y, -0.94, 0.10), z),
    )
    objects.append(body_clay)

    # 2) LeftArm / RightArm (짧고 앙증맞은 양옆 미니 팔)
    for side, name in ((-1, "LeftArm"), (1, "RightArm")):
        arm_elements = [
            ((side * 0.49, -0.27, 0.10), (0.120, 0.110, 0.115), None, 2.0),
            ((side * 0.65, -0.31, 0.15), (0.115, 0.100, 0.110), (0.0, -side * 0.16, side * 0.22), 2.1),
        ]
        objects.append(
            create_metaball_clay(
                name,
                "body",
                arm_elements,
                mb_res=0.022,
                voxel_size=0.017,
                smooth_iters=14,
                target_tris=190,
            )
        )

    # 3) Tail (엉덩이 뒤 작고 동그란 폼폼 꼬리)
    tail_pivot = (0.0, -0.62, -0.45)
    objects.append(
        create_metaball_clay(
            "Tail",
            "body",
            [((0.0, -0.62, -0.49), (0.125, 0.125, 0.125), None, 2.0)],
            mb_res=0.022,
            voxel_size=0.017,
            smooth_iters=12,
            target_tris=160,
        )
    )

    # 4) Head (양볼이 말랑하게 부푼 둥근 만두형 머리)
    head_elements = [
        ((0.0, 0.33, 0.00), (0.75, 0.62, 0.66), None, 2.05),
        ((-0.26, 0.21, 0.10), (0.46, 0.35, 0.44), None, 2.1),
        ((0.26, 0.21, 0.10), (0.46, 0.35, 0.44), None, 2.1),
    ]
    head_clay = create_metaball_clay(
        "_ChiikawaHeadClay",
        "body",
        head_elements,
        mb_res=0.034,
        voxel_size=0.026,
        smooth_iters=15,
        target_tris=1200,
    )

    expr_parts, blink_center, extra_head_objs = build_chiikawa_style_face(
        head_clay,
        eye_x=0.220,
        eye_y=0.280,
        eye_rx=0.088,
        eye_ry=0.098,
        eye_depth=0.048,
        mouth_y=0.120,
        cheek_x=0.395,
        cheek_y=0.160,
        cheek_rx=0.126,
        cheek_ry=0.070,
        brow_style="short_arc",
        open_tongue_default=True,
    )
    objects.append(join_part_group("Head", [head_clay] + extra_head_objs))

    # 5) LeftEar / RightEar (작고 동글동글한 햄스터/곰 귀)
    for side, ear_name in ((-1, "LeftEar"), (1, "RightEar")):
        ear_elements = [
            ((side * 0.42, 0.77, 0.01), (0.145, 0.135, 0.120), None, 2.0),
            ((side * 0.46, 0.87, 0.01), (0.13, 0.13, 0.105), None, 2.1),
        ]
        outer_ear = create_metaball_clay(
            f"_{ear_name}_Outer",
            "body",
            ear_elements,
            mb_res=0.020,
            voxel_size=0.016,
            smooth_iters=14,
            target_tris=190,
        )
        ear_in_dir = Vector((-side * 0.08, -0.05, -0.995)).normalized()
        inner_ear = create_conformed_clay_patch(
            f"_{ear_name}_Inner",
            "body_shade",
            outer_ear,
            ray_origin_wpf=Vector((side * 0.45, 0.86, 0.12)) - ear_in_dir * 1.2,
            ray_dir_wpf=ear_in_dir,
            rx=0.075,
            ry=0.075,
            center_offset=0.008,
            rim_offset=-0.008,
            rings=3,
            segs=14,
            wrap_focus_depth=0.5,
        )
        objects.append(join_part_group(ear_name, [outer_ear, inner_ear]))

    objects.extend(expr_parts.values())

    head_socket_y, head_radius_x = measure_head_crown_and_radius(head_clay, head_center_z=0.0)
    out_dir = os.path.join(MODELS_ROOT, "Characters", "chiikawa")
    rig_meta = {
        "coordinateSystem": "WPF_Y_UP_Z_FRONT",
        "unitScale": 1.0,
        "originOffset": {"x": 0.0, "y": 0.0, "z": 0.0},
        "headPivot": {"x": 0.0, "y": 0.01, "z": 0.0},
        "headSocket": {"x": 0.0, "y": head_socket_y, "z": 0.01},
        "headRadiusX": head_radius_x,
        "auraSocket": {"x": 0.0, "y": 0.0, "z": 0.0},
        "shadowY": -0.94,
        "shadowRadiusX": 0.70,
        "shadowRadiusZ": 0.62,
        "eyeBlinkCenter": {"x": 0.0, "y": blink_center[1], "z": blink_center[2]},
        "nameplateAnchor": {"x": 0.0, "y": -0.94, "z": 0.35},
        "leftEarPivot": {"x": -0.42, "y": 0.77, "z": 0.01},
        "rightEarPivot": {"x": 0.42, "y": 0.77, "z": 0.01},
        "leftEarBaseAngle": 0.0,
        "rightEarBaseAngle": 0.0,
        "leftArmPivot": {"x": -0.53, "y": -0.27, "z": 0.10},
        "rightArmPivot": {"x": 0.53, "y": -0.27, "z": 0.10},
        "tailPivot": {"x": tail_pivot[0], "y": tail_pivot[1], "z": tail_pivot[2]},
    }
    export_character(out_dir, objects, rig_meta)


# ---------------------------------------------------------------------------
# 1H. 우사기 (usagi) — 크림 옐로우 통통 바디와 정수리 위 쫑긋 솟은 긴 토끼 귀
# ---------------------------------------------------------------------------

def build_usagi():
    clear_scene()
    objects = []

    # 1) Body: 포동포동한 배와 아담한 두 발
    body_elements = [
        ((0.0, 0.01, 0.00), (0.48, 0.28, 0.44), None, 2.0),
        ((0.0, -0.35, 0.01), (0.51, 0.38, 0.46), None, 2.15),
        ((0.0, -0.60, 0.00), (0.48, 0.28, 0.43), None, 2.1),
        ((-0.25, -0.82, 0.06), (0.14, 0.15, 0.16), (0.0, 0.10, -0.08), 2.0),
        ((0.25, -0.82, 0.06), (0.14, 0.15, 0.16), (0.0, -0.10, 0.08), 2.0),
    ]
    body_clay = create_metaball_clay(
        "Body",
        "body",
        body_elements,
        mb_res=0.034,
        voxel_size=0.026,
        smooth_iters=14,
        target_tris=1050,
        post_smooth_fn=lambda x, y, z: (x, soft_cushion_floor(y, -0.94, 0.10), z),
    )
    objects.append(body_clay)

    # 2) LeftArm / RightArm (양옆 짧은 팔)
    for side, name in ((-1, "LeftArm"), (1, "RightArm")):
        arm_elements = [
            ((side * 0.54, -0.28, 0.10), (0.125, 0.115, 0.120), None, 2.0),
            ((side * 0.67, -0.33, 0.15), (0.120, 0.105, 0.115), (0.0, -side * 0.18, side * 0.25), 2.1),
        ]
        objects.append(
            create_metaball_clay(
                name,
                "body",
                arm_elements,
                mb_res=0.022,
                voxel_size=0.017,
                smooth_iters=14,
                target_tris=190,
            )
        )

    # 3) Tail (보송하고 동그란 토끼 솜방울 꼬리)
    tail_pivot = (0.0, -0.61, -0.45)
    objects.append(
        create_metaball_clay(
            "Tail",
            "body_light",
            [((0.0, -0.61, -0.51), (0.155, 0.155, 0.155), None, 2.0)],
            mb_res=0.022,
            voxel_size=0.017,
            smooth_iters=12,
            target_tris=180,
        )
    )

    # 4) Head (둥글고 통통한 우사기 두상)
    head_elements = [
        ((0.0, 0.32, 0.00), (0.73, 0.56, 0.63), None, 2.05),
        ((-0.25, 0.20, 0.10), (0.45, 0.34, 0.44), None, 2.1),
        ((0.25, 0.20, 0.10), (0.45, 0.34, 0.44), None, 2.1),
    ]
    head_clay = create_metaball_clay(
        "_UsagiHeadClay",
        "body",
        head_elements,
        mb_res=0.034,
        voxel_size=0.026,
        smooth_iters=15,
        target_tris=1200,
    )

    expr_parts, blink_center, extra_head_objs = build_chiikawa_style_face(
        head_clay,
        eye_x=0.239,
        eye_y=0.250,
        eye_rx=0.090,
        eye_ry=0.100,
        eye_depth=0.048,
        mouth_y=0.123,
        cheek_x=0.395,
        cheek_y=0.165,
        cheek_rx=0.125,
        cheek_ry=0.070,
        brow_style="high_arch",
        open_tongue_default=True,
    )
    objects.append(join_part_group("Head", [head_clay] + extra_head_objs))

    # 5) LeftEar / RightEar (정수리 중앙 가까이에 나란히 쫑긋 솟은 긴 토끼 귀 + 핑크 속귀)
    for side, ear_name in ((-1, "LeftEar"), (1, "RightEar")):
        ear_elements = [
            ((side * 0.145, 0.80, 0.00), (0.115, 0.16, 0.095), (0.0, 0.0, -side * 0.04), 2.0),
            ((side * 0.18, 1.04, -0.025), (0.112, 0.20, 0.092), (0.0, 0.0, -side * 0.05), 2.1),
            ((side * 0.22, 1.33, -0.07), (0.095, 0.185, 0.082), None, 2.0),
        ]
        outer_ear = create_metaball_clay(
            f"_{ear_name}_Outer",
            "body",
            ear_elements,
            mb_res=0.020,
            voxel_size=0.016,
            smooth_iters=14,
            target_tris=240,
        )
        ear_in_dir = Vector((-side * 0.04, 0.0, -0.999)).normalized()
        inner_ear = create_conformed_clay_patch(
            f"_{ear_name}_Inner",
            "accent",
            outer_ear,
            ray_origin_wpf=Vector((side * 0.18, 1.06, 0.12)) - ear_in_dir * 1.2,
            ray_dir_wpf=ear_in_dir,
            rx=0.058,
            ry=0.245,
            center_offset=0.011,
            rim_offset=-0.008,
            rings=3,
            segs=18,
            wrap_focus_depth=0.5,
        )
        objects.append(join_part_group(ear_name, [outer_ear, inner_ear]))

    objects.extend(expr_parts.values())

    head_socket_y, head_radius_x = measure_head_crown_and_radius(head_clay, head_center_z=0.0)
    out_dir = os.path.join(MODELS_ROOT, "Characters", "usagi")
    rig_meta = {
        "coordinateSystem": "WPF_Y_UP_Z_FRONT",
        "unitScale": 1.0,
        "originOffset": {"x": 0.0, "y": 0.0, "z": 0.0},
        "headPivot": {"x": 0.0, "y": 0.01, "z": 0.0},
        "headSocket": {"x": 0.0, "y": head_socket_y, "z": 0.01},
        "headRadiusX": head_radius_x,
        "auraSocket": {"x": 0.0, "y": 0.0, "z": 0.0},
        "shadowY": -0.94,
        "shadowRadiusX": 0.70,
        "shadowRadiusZ": 0.62,
        "eyeBlinkCenter": {"x": 0.0, "y": blink_center[1], "z": blink_center[2]},
        "nameplateAnchor": {"x": 0.0, "y": -0.94, "z": 0.35},
        "leftEarPivot": {"x": -0.145, "y": 0.78, "z": 0.0},
        "rightEarPivot": {"x": 0.145, "y": 0.78, "z": 0.0},
        "leftEarBaseAngle": 0.0,
        "rightEarBaseAngle": 0.0,
        "leftArmPivot": {"x": -0.54, "y": -0.28, "z": 0.10},
        "rightArmPivot": {"x": 0.54, "y": -0.28, "z": 0.10},
        "tailPivot": {"x": tail_pivot[0], "y": tail_pivot[1], "z": tail_pivot[2]},
    }
    export_character(out_dir, objects, rig_meta)


# ---------------------------------------------------------------------------
# 1I. 카니 (pink_reference) — 파스텔 핑크 고정 머리와 게 집게발 귀, 2가닥 삐죽털
# ---------------------------------------------------------------------------

def build_pink_reference():
    clear_scene()
    objects = []

    # 1) Body: 책 등 보조 요소 없이 서 있는 2등신 치이카와 찹쌀떡 체형 + 짧은 두 발
    body_elements = [
        ((0.0, 0.00, 0.00), (0.48, 0.27, 0.44), None, 2.0),
        ((0.0, -0.35, 0.01), (0.50, 0.36, 0.46), None, 2.15),
        ((0.0, -0.59, 0.00), (0.48, 0.27, 0.43), None, 2.1),
        ((-0.19, -0.82, 0.05), (0.135, 0.145, 0.155), (0.0, 0.06, -0.05), 2.0),
        ((0.19, -0.82, 0.05), (0.135, 0.145, 0.155), (0.0, -0.06, 0.05), 2.0),
    ]
    body_clay = create_metaball_clay(
        "Body",
        "body",
        body_elements,
        mb_res=0.034,
        voxel_size=0.026,
        smooth_iters=14,
        target_tris=1050,
        post_smooth_fn=lambda x, y, z: (x, soft_cushion_floor(y, -0.94, 0.10), z),
    )
    objects.append(body_clay)

    # 2) LeftArm / RightArm (몸통 양옆 짧은 찹쌀 팔)
    for side, name in ((-1, "LeftArm"), (1, "RightArm")):
        arm_elements = [
            ((side * 0.50, -0.27, 0.10), (0.120, 0.110, 0.115), None, 2.0),
            ((side * 0.65, -0.31, 0.15), (0.115, 0.100, 0.110), (0.0, -side * 0.16, side * 0.22), 2.1),
        ]
        objects.append(
            create_metaball_clay(
                name,
                "body",
                arm_elements,
                mb_res=0.022,
                voxel_size=0.017,
                smooth_iters=14,
                target_tris=190,
            )
        )

    # 3) Tail (엉덩이 뒤 작고 동그란 폼폼 꼬리)
    tail_pivot = (0.0, -0.62, -0.45)
    objects.append(
        create_metaball_clay(
            "Tail",
            "body",
            [((0.0, -0.62, -0.49), (0.122, 0.122, 0.122), None, 2.0)],
            mb_res=0.022,
            voxel_size=0.017,
            smooth_iters=12,
            target_tris=160,
        )
    )

    # 4) Head (통통한 만두형 두상 + 톱니 없이 매끈하게 절단된 파스텔 핑크 고정 머리 캡)
    head_elements = [
        ((0.0, 0.34, 0.00), (0.75, 0.62, 0.65), None, 2.05),
        ((-0.26, 0.21, 0.10), (0.46, 0.35, 0.44), None, 2.1),
        ((0.26, 0.21, 0.10), (0.46, 0.35, 0.44), None, 2.1),
    ]
    head_clay = create_metaball_clay(
        "_KaniHeadClay",
        "body",
        head_elements,
        mb_res=0.034,
        voxel_size=0.026,
        smooth_iters=15,
        target_tris=1200,
    )

    # 이마 상단에서 양 관자놀이와 뒤통수로 부드럽게 감싸는 아치 경계(bmesh bisect)로 kani_pink 슬롯 분리
    def kani_hairline(x, depth):
        front = min(1.0, max(0.0, (depth + 0.30) / 0.85))
        arch = min(1.35, (x / 0.72) ** 2)
        return 0.59 + 0.11 * front - 0.135 * arch

    head_clay.data.materials.append(ensure_material("kani_pink"))
    bm = bmesh.new()
    bm.from_mesh(head_clay.data)
    for v in bm.verts:
        v.co.z -= kani_hairline(v.co.x, -v.co.y)
    bmesh.ops.bisect_plane(
        bm,
        geom=list(bm.verts) + list(bm.edges) + list(bm.faces),
        plane_co=(0, 0, 0),
        plane_no=(0, 0, 1),
        dist=1e-6,
    )
    for face in bm.faces:
        face.material_index = int(face.calc_center_median().z > 0)
    for v in bm.verts:
        v.co.z += kani_hairline(v.co.x, -v.co.y)
    bm.to_mesh(head_clay.data)
    bm.free()
    head_clay.data.update()
    set_smooth_and_recalc(head_clay)

    head_socket_y, head_radius_x = measure_head_crown_and_radius(head_clay, head_center_z=0.0)
    crown_y = head_socket_y + 0.025

    # 정수리 중앙의 작고 가는 2가닥 삐죽 머리털(아호게, V자 ∨ 형태)
    ahoge_left = create_curve_tube(
        "_KaniAhogeLeft",
        "eye_dark",
        [
            (-0.010, crown_y - 0.030, 0.012, 0.0115),
            (-0.032, crown_y + 0.040, 0.016, 0.0095),
            (-0.052, crown_y + 0.072, 0.014, 0.0055),
        ],
        resolution_u=3,
        bevel_resolution=1,
    )
    ahoge_right = create_curve_tube(
        "_KaniAhogeRight",
        "eye_dark",
        [
            (0.010, crown_y - 0.030, 0.012, 0.0115),
            (0.026, crown_y + 0.048, 0.016, 0.0095),
            (0.042, crown_y + 0.084, 0.014, 0.0055),
        ],
        resolution_u=3,
        bevel_resolution=1,
    )

    expr_parts, blink_center, extra_head_objs = build_chiikawa_style_face(
        head_clay,
        eye_x=0.228,
        eye_y=0.272,
        eye_rx=0.095,
        eye_ry=0.105,
        eye_depth=0.048,
        mouth_y=0.145,
        cheek_x=0.405,
        cheek_y=0.168,
        cheek_rx=0.130,
        cheek_ry=0.072,
        brow_style="kani_double",
        open_tongue_default=False,
        include_nose_tick=False,
    )
    objects.append(join_part_group("Head", [head_clay, ahoge_left, ahoge_right] + extra_head_objs))

    # 5) LeftEar / RightEar (통통하고 둥근 게 집게발 모양 귀 + 꼭대기 앙증맞은 V자 홈 — 고정 kani_pink 슬롯)
    for side, ear_name in ((-1, "LeftEar"), (1, "RightEar")):
        pivot = Vector((side * 0.43, 0.79, 0.01))
        tilt_z = -side * 0.40  # 바깥쪽으로 약 23도 대칭 기울임
        rot_m = Euler((0.0, 0.0, tilt_z), "XYZ").to_matrix()

        def ear_pt(u, v, w=0.0):
            p = pivot + rot_m @ Vector((u, v, w))
            return (p.x, p.y, p.z)

        ear_elements = [
            # 밑동~중간 통통한 게 집게 본체 (둥글고 도톰하게 모인 형태)
            (ear_pt(0.0, 0.07, 0.0), (0.145, 0.135, 0.112), (0.0, 0.0, tilt_z), 2.0),
            (ear_pt(0.0, 0.18, 0.0), (0.165, 0.145, 0.118), (0.0, 0.0, tilt_z), 2.1),
            # 상단 좌·우 둥근 집게 끝 2개 (하트처럼 과도하게 벌어지지 않고 둥글게 모임)
            (ear_pt(-0.068, 0.275, 0.0), (0.115, 0.135, 0.104), (0.0, 0.0, tilt_z + 0.14), 2.15),
            (ear_pt(0.068, 0.275, 0.0), (0.115, 0.135, 0.104), (0.0, 0.0, tilt_z - 0.14), 2.15),
            # 꼭대기 정중앙의 앙증맞은 V자 노치 카빙용 네거티브 메타볼
            (ear_pt(0.0, 0.415, 0.0), (0.052, 0.095, 0.185), (0.0, 0.0, tilt_z), 2.35, True),
        ]
        objects.append(
            create_metaball_clay(
                ear_name,
                "kani_pink",
                ear_elements,
                mb_res=0.016,
                voxel_size=0.013,
                smooth_iters=11,
                target_tris=280,
            )
        )

    objects.extend(expr_parts.values())

    out_dir = os.path.join(MODELS_ROOT, "Characters", "pink_reference")
    rig_meta = {
        "coordinateSystem": "WPF_Y_UP_Z_FRONT",
        "unitScale": 1.0,
        "originOffset": {"x": 0.0, "y": 0.0, "z": 0.0},
        "headPivot": {"x": 0.0, "y": 0.01, "z": 0.0},
        "headSocket": {"x": 0.0, "y": head_socket_y, "z": 0.01},
        "headRadiusX": head_radius_x,
        "auraSocket": {"x": 0.0, "y": 0.0, "z": 0.0},
        "shadowY": -0.94,
        "shadowRadiusX": 0.70,
        "shadowRadiusZ": 0.62,
        "eyeBlinkCenter": {"x": 0.0, "y": blink_center[1], "z": blink_center[2]},
        "nameplateAnchor": {"x": 0.0, "y": -0.94, "z": 0.35},
        "leftEarPivot": {"x": -0.43, "y": 0.80, "z": 0.01},
        "rightEarPivot": {"x": 0.43, "y": 0.80, "z": 0.01},
        "leftEarBaseAngle": 0.0,
        "rightEarBaseAngle": 0.0,
        "leftArmPivot": {"x": -0.53, "y": -0.27, "z": 0.10},
        "rightArmPivot": {"x": 0.53, "y": -0.27, "z": 0.10},
        "tailPivot": {"x": tail_pivot[0], "y": tail_pivot[1], "z": tail_pivot[2]},
    }
    export_character(out_dir, objects, rig_meta)
    with open(os.path.join(out_dir, "rig.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(rig_meta, f, indent=2, ensure_ascii=False)
        f.write("\n")


# ---------------------------------------------------------------------------
# 2. 공통 액세서리 5종 (원점 = HeadSocket (0,0,0))
# ---------------------------------------------------------------------------

def build_accessories():
    # 2.1 새싹 (sprout)
    clear_scene()
    stem = create_curve_tube(
        "_Stem",
        "acc_green",
        [(0, -0.06, 0, 0.028), (0.015, 0.08, 0.01, 0.024), (0.01, 0.20, 0.015, 0.021), (0, 0.29, 0, 0.019)],
        resolution_u=4,
        bevel_resolution=1,
    )

    def sculpt_leaf(x, y, z, nx, ny, nz):
        # 닫힌 잎의 두께는 유지하고 끝으로 갈수록 좁아지는 윤곽과 중앙 능선을 조형한다.
        tip = abs(nx)
        taper = 1.0 - 0.48 * tip ** 1.6
        midrib = 0.018 * (1.0 - tip * tip) * math.exp(-9.0 * nz * nz)
        cup = 0.020 * nz * nz * (1.0 - tip)
        arch = 0.025 * math.sin(math.pi * tip) - 0.036 * tip * tip
        twist = 0.012 * nx * nz
        return (x, y * (0.72 + 0.28 * taper) + arch + midrib + cup + twist, z * taper)

    leaf_l = create_sculpted_quad_mesh(
        "_LeafL",
        "acc_green",
        center_wpf=(-0.15, 0.31, 0.02),
        radii_wpf=(0.17, 0.055, 0.095),
        subsurf_levels=2,
        sculpt_fn=sculpt_leaf,
        rotation_euler_wpf=(0.14, 0.0, 0.22),
    )
    leaf_r = create_sculpted_quad_mesh(
        "_LeafR",
        "acc_green",
        center_wpf=(0.14, 0.29, -0.02),
        radii_wpf=(0.155, 0.050, 0.088),
        subsurf_levels=2,
        sculpt_fn=sculpt_leaf,
        rotation_euler_wpf=(-0.12, 0.0, -0.20),
    )
    sprout = join_part_group("Accessory_Sprout", [stem, leaf_l, leaf_r])
    tris = export_obj(os.path.join(MODELS_ROOT, "Accessories", "sprout.obj"), [sprout])
    print(f"[Accessory] sprout.obj ({tris} tris)")

    # 2.2 입체 리본 (ribbon)
    clear_scene()
    knot = create_sculpted_quad_mesh("_Knot", "acc_accent", (0, 0.03, 0.10), (0.092, 0.082, 0.075), subsurf_levels=2)

    def sculpt_bow_wing(x, y, z, nx, ny, nz):
        # 매듭 쪽 압축과 천 표면의 완만한 주름을 닫힌 곡면에 반영한다.
        span = abs(nx)
        flare = 0.62 + span * 0.55
        pinch_z = 0.68 + span * 0.42
        fold = 0.014 * math.cos(ny * math.pi * 2.0) * (1.0 - span * span)
        front_weight = nz * nz
        drape = -0.018 * span * span + 0.008 * nx
        return (x, y * flare + drape, z * pinch_z + fold * front_weight)

    wing_l = create_sculpted_quad_mesh(
        "_WingL", "acc_accent", (-0.175, 0.06, 0.08), (0.175, 0.12, 0.078), subsurf_levels=2,
        sculpt_fn=sculpt_bow_wing, rotation_euler_wpf=(0.08, 0.15, 0.14)
    )
    wing_r = create_sculpted_quad_mesh(
        "_WingR", "acc_accent", (0.175, 0.06, 0.08), (0.175, 0.12, 0.078), subsurf_levels=2,
        sculpt_fn=sculpt_bow_wing, rotation_euler_wpf=(0.08, -0.15, -0.14)
    )
    tail_l = create_sculpted_quad_mesh(
        "_RibTailL", "acc_accent", (-0.085, -0.04, 0.09), (0.055, 0.095, 0.028), subsurf_levels=1,
        rotation_euler_wpf=(0.0, 0.0, -0.32)
    )
    tail_r = create_sculpted_quad_mesh(
        "_RibTailR", "acc_accent", (0.085, -0.04, 0.09), (0.055, 0.095, 0.028), subsurf_levels=1,
        rotation_euler_wpf=(0.0, 0.0, 0.32)
    )
    ribbon = join_part_group("Accessory_Ribbon", [knot, wing_l, wing_r, tail_l, tail_r])
    tris = export_obj(os.path.join(MODELS_ROOT, "Accessories", "ribbon.obj"), [ribbon])
    print(f"[Accessory] ribbon.obj ({tris} tris)")

    # 2.3 3D 헤드셋 (headset, 슈퍼타원 아치로 둥근 이마 양모서리 파고듦 방지)
    clear_scene()
    rx = 0.87
    ry = 0.55
    base_y = -0.45
    band_pts = []
    segs = 14
    for i in range(segs + 1):
        ang = math.pi * i / segs
        ca = math.cos(ang)
        sa = math.sin(ang)
        sx = math.copysign(abs(ca) ** 0.88, ca) * rx
        sy = base_y + (sa ** 0.90) * ry
        thick = 0.048 + sa * 0.011
        band_pts.append((sx, sy, 0.0, thick))
    band = create_curve_tube("_Band", "acc_headband", band_pts, resolution_u=2, bevel_resolution=1)
    parts = [band]
    for side in (-1, 1):
        parts.append(
            create_sculpted_quad_mesh(
                f"_Cushion_{side}",
                "acc_headband",
                center_wpf=(side * (rx - 0.015), base_y - 0.03, 0.0),
                radii_wpf=(0.085, 0.21, 0.19),
                subsurf_levels=2,
            )
        )
        parts.append(
            create_sculpted_quad_mesh(
                f"_Cup_{side}",
                "acc_accent",
                center_wpf=(side * (rx + 0.045), base_y - 0.03, 0.0),
                radii_wpf=(0.110, 0.18, 0.17),
                subsurf_levels=2,
            )
        )
    # 밴드 안쪽 쿠션과 이어컵 외곽의 연속 몰딩으로 재질 경계를 분리한다.
    pad_pts = []
    for i in range(11):
        ang = math.pi * (0.22 + 0.56 * i / 10.0)
        ca, sa = math.cos(ang), math.sin(ang)
        pad_pts.append((math.copysign(abs(ca) ** 0.88, ca) * rx,
                        base_y + sa ** 0.90 * ry - 0.036, 0.0, 0.042))
    parts.append(create_curve_tube("_BandPadding", "acc_headband", pad_pts,
                                   resolution_u=3, bevel_resolution=1, scale_z=1.45))
    for side in (-1, 1):
        rim_pts = []
        for i in range(20):
            ang = 2.0 * math.pi * i / 20.0
            rim_pts.append((side * (rx + 0.122),
                            base_y - 0.03 + 0.135 * math.cos(ang),
                            0.125 * math.sin(ang), 0.013))
        parts.append(create_curve_tube(f"_CupTrim_{side}", "acc_headband", rim_pts,
                                       resolution_u=2, bevel_resolution=1, closed=True))
    # 얇은 이어컵 테두리만 독립 LED 파트로 유지한다.
    led = join_part_group("Accessory_HeadsetLed", parts[-2:])
    headset = join_part_group("Accessory_Headset", parts[:-2])
    tris = export_obj(os.path.join(MODELS_ROOT, "Accessories", "headset.obj"), [headset, led])
    print(f"[Accessory] headset.obj ({tris} tris)")

    # 2.4 산타 모자 (santa_hat)
    clear_scene()
    brim_pts = []
    for i in range(14):
        ang = 2.0 * math.pi * i / 14.0
        brim_pts.append((math.cos(ang) * 0.42, -0.05, math.sin(ang) * 0.42, 0.078))
    brim = create_curve_tube("_Brim", "acc_white", brim_pts, resolution_u=2, bevel_resolution=1, closed=True)
    hat_cone = create_curve_tube(
        "_Cone",
        "acc_red",
        [
            (0.0, -0.04, 0.0, 0.38),
            (0.015, 0.14, -0.015, 0.29),
            (0.07, 0.34, -0.035, 0.20),
            (0.19, 0.49, -0.055, 0.12),
            (0.34, 0.44, -0.07, 0.065),
            (0.39, 0.30, -0.065, 0.026),
        ],
        resolution_u=5,
        bevel_resolution=2,
    )
    # 밑단에서 올라오는 완만한 천 주름. 윗부분은 감쇠시켜 처진 끝의 실루엣을 보존한다.
    def sculpt_hat_folds(x, y, z):
        height = max(0.0, min(1.0, (y + 0.04) / 0.48))
        center_x = 0.015 + 0.18 * height * height
        center_z = -0.055 * height
        dx, dz = x - center_x, z - center_z
        angle = math.atan2(dz, dx)
        envelope = math.sin(math.pi * height) * (1.0 - height) ** 2
        fold = 1.0 + 0.055 * envelope * math.cos(7.0 * angle + height * 1.8)
        return (center_x + dx * fold, y, center_z + dz * fold)

    apply_wpf_vertex_fn(hat_cone, sculpt_hat_folds)
    set_smooth_and_recalc(hat_cone)
    # 끝부분이 아래로 굽어 방울에 연결되는 비대칭 천 실루엣.
    pom = create_sculpted_quad_mesh("_Pom", "acc_white", (0.39, 0.28, -0.065), (0.112, 0.125, 0.112), subsurf_levels=2)
    santa = join_part_group("Accessory_SantaHat", [brim, hat_cone])
    pom.name = "Accessory_SantaPom"
    tris = export_obj(os.path.join(MODELS_ROOT, "Accessories", "santa_hat.obj"), [santa, pom])
    print(f"[Accessory] santa_hat.obj ({tris} tris)")

    # 2.5 미니 왕관 (crown)
    clear_scene()
    ring_pts = []
    for i in range(12):
        ang = 2.0 * math.pi * i / 12.0
        ring_pts.append((math.cos(ang) * 0.30, -0.01, math.sin(ang) * 0.30, 0.048))
    crown_parts = [
        create_curve_tube("_CrownBase", "acc_gold", ring_pts, resolution_u=2, bevel_resolution=1, closed=True)
    ]
    for i in range(5):
        ang = i * (2.0 * math.pi / 5.0) + math.pi * 0.5
        cx = math.cos(ang) * 0.29
        cz = math.sin(ang) * 0.29
        crown_parts.append(
            create_curve_tube(
                f"_Prong_{i}",
                "acc_gold",
                [(cx, -0.01, cz, 0.048), (cx * 1.04, 0.11, cz * 1.04, 0.032), (cx * 1.07, 0.21, cz * 1.07, 0.016)],
                resolution_u=2,
                bevel_resolution=1,
            )
        )
        crown_parts.append(
            create_sculpted_quad_mesh(
                f"_TipPearl_{i}",
                "acc_gold",
                center_wpf=(cx * 1.07, 0.225, cz * 1.07),
                radii_wpf=(0.032, 0.032, 0.032),
                subsurf_levels=1,
            )
        )
    # 작은 테이블 면, 넓은 거들, 뒤쪽 파빌리온으로 구성한 팔각 컷 보석.
    gem_vertices, gem_faces = [], []
    for radius, depth in ((0.52, 0.038), (1.0, 0.008), (0.24, -0.026)):
        for i in range(8):
            angle = 2.0 * math.pi * i / 8.0 + math.pi / 8.0
            gem_vertices.append(to_bl(0.064 * radius * math.cos(angle),
                                      0.055 + 0.076 * radius * math.sin(angle), 0.33 + depth))
    gem_faces.append(tuple(range(8)))
    for ring in range(2):
        for i in range(8):
            j = (i + 1) % 8
            a, b = ring * 8, (ring + 1) * 8
            gem_faces.append((a + i, b + i, b + j, a + j))
    gem_faces.append(tuple(reversed(range(16, 24))))
    gem_mesh = bpy.data.meshes.new("_FrontGem")
    gem_mesh.from_pydata(gem_vertices, [], gem_faces)
    gem_mesh.update()
    gem = bpy.data.objects.new("_FrontGem", gem_mesh)
    bpy.context.collection.objects.link(gem)
    gem.data.materials.append(ensure_material("acc_accent"))
    set_smooth_and_recalc(gem)
    for face in gem.data.polygons:
        face.use_smooth = False
    crown_parts.append(gem)
    crown = join_part_group("Accessory_Crown", crown_parts)
    tris = export_obj(os.path.join(MODELS_ROOT, "Accessories", "crown.obj"), [crown])
    print(f"[Accessory] crown.obj ({tris} tris)")


# ---------------------------------------------------------------------------
# 3. 공통 3D 이펙트 메시 3종 (music_note, star_sparkle, snowflake)
# ---------------------------------------------------------------------------

def build_effects():
    clear_scene()
    head = create_sculpted_quad_mesh(
        "_NoteHead", "acc_accent", center_wpf=(-0.03, -0.05, 0), radii_wpf=(0.088, 0.068, 0.048),
        subsurf_levels=1, rotation_euler_wpf=(0.0, 0.0, 0.32)
    )
    stem = create_curve_tube(
        "_NoteStem", "acc_accent",
        [(0.042, -0.04, 0, 0.019), (0.042, 0.18, 0, 0.019)],
        resolution_u=2, bevel_resolution=0
    )
    flag = create_curve_tube(
        "_NoteFlag", "acc_accent",
        [(0.042, 0.175, 0, 0.020), (0.105, 0.155, 0.01, 0.022), (0.148, 0.105, 0.015, 0.014)],
        resolution_u=3, bevel_resolution=0
    )
    note = join_part_group("Effect_MusicNote", [head, stem, flag])
    tris = export_obj(os.path.join(MODELS_ROOT, "Effects", "music_note.obj"), [note])
    print(f"[Effect] music_note.obj ({tris} tris)")

    clear_scene()
    star_parts = [
        create_sculpted_quad_mesh("_StarV", "acc_accent", (0, 0, 0), (0.048, 0.145, 0.042), subsurf_levels=1),
        create_sculpted_quad_mesh("_StarH", "acc_accent", (0, 0, 0), (0.145, 0.048, 0.042), subsurf_levels=1),
    ]
    star = join_part_group("Effect_StarSparkle", star_parts)
    tris = export_obj(os.path.join(MODELS_ROOT, "Effects", "star_sparkle.obj"), [star])
    print(f"[Effect] star_sparkle.obj ({tris} tris)")

    clear_scene()
    snow_parts = [
        create_sculpted_quad_mesh("_SnowCore", "acc_white", (0, 0, 0), (0.036, 0.036, 0.028), subsurf_levels=1)
    ]
    for i in range(6):
        ang = i * (math.pi / 3.0)
        dx = math.cos(ang)
        dy = math.sin(ang)
        snow_parts.append(
            create_curve_tube(
                f"_SnowArm_{i}", "acc_white",
                [(0, 0, 0, 0.015), (dx * 0.105, dy * 0.105, 0, 0.011)],
                resolution_u=2, bevel_resolution=0
            )
        )
    # 육각 대칭의 짧은 곁가지를 더해 작은 크기에서도 눈결정 윤곽을 유지한다.
    for arm in range(6):
        angle = arm * math.pi / 3.0
        dx, dy = math.cos(angle), math.sin(angle)
        for level in (0.045, 0.075):
            for side in (-1, 1):
                x, y = dx * level, dy * level
                snow_parts.append(create_curve_tube(
                    f"_SnowBranch_{arm}_{level}_{side}", "acc_white",
                    [(x, y, 0, 0.007),
                     (x + dx * 0.025 - dy * side * 0.021,
                      y + dy * 0.025 + dx * side * 0.021, 0, 0.004)],
                    resolution_u=1, bevel_resolution=0))
    snow_parts.append(
        create_curve_tube(
            "_SnowCrossZ", "acc_white",
            [(0, 0, -0.065, 0.011), (0, 0, 0.065, 0.011)],
            resolution_u=2, bevel_resolution=0
        )
    )
    snow = join_part_group("Effect_Snowflake", snow_parts)
    tris = export_obj(os.path.join(MODELS_ROOT, "Effects", "snowflake.obj"), [snow])
    print(f"[Effect] snowflake.obj ({tris} tris)")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--targets", nargs="+", default=["all"])
    args = parser.parse_args(argv)
    targets = set(args.targets)

    if "all" in targets or "mochi_spirit" in targets:
        build_mochi_spirit()
    if "all" in targets or "chibi_cat" in targets:
        build_chibi_cat()
    if "all" in targets or "fluffy_cloud" in targets:
        build_fluffy_cloud()
    if "all" in targets or "fluffy_puppy" in targets:
        build_fluffy_puppy()
    if "all" in targets or "hachiware" in targets:
        build_hachiware()
    if "all" in targets or "chiikawa" in targets:
        build_chiikawa()
    if "all" in targets or "usagi" in targets:
        build_usagi()
    if "all" in targets or "pink_reference" in targets:
        build_pink_reference()
    if "all" in targets or "accessories" in targets:
        build_accessories()
    if "all" in targets or "effects" in targets:
        build_effects()


if __name__ == "__main__":
    main()
