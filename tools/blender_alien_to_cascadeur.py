"""AlienForge: подготовка Чужого (fan-rig «Xenomorph») к Cascadeur.

  blender -b --python blender_alien_to_cascadeur.py -- "<файл.blend>" "<out.fbx>" "<out.qrigcasc>"

Что делает:
  1. Чинит иерархию деформ-скелета под риг Cascadeur: пальцы ног (MCH-toes.*)
     цепляет к плюсне (MCH-ankle.*) — в исходнике они висят в корне; позвоночник
     (spine lower) и хвост (root_tail) — под pelvis, чтобы ехали за тазом.
  2. Ставит скелет в rest-позу и экспортирует FBX только с арматурой и телом
     (виджеты WGT-* не берёт), без анимации, масштаб запечён.
  3. Пишет шаблон Quick Rigging Tool (.qrigcasc) с раскладкой костей, включая
     дигитиградную ногу: thigh = бедро, calf = голень, foot = плюсна (длинная,
     стоит под углом), toe = пальцы. Так Cascadeur ставит «пятку» рига на
     сустав плюсны, а IK-стопа — это пальцы; колено гнётся как надо.
"""
import json
import os
import sys

import bpy

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
BLEND = argv[0] if len(argv) > 0 else r"C:\Users\lelil\Desktop\Alien Isolation - Alien.blend"
OUT_FBX = argv[1] if len(argv) > 1 else r"C:\Users\lelil\Desktop\Alien_cascadeur.fbx"
OUT_RIG = argv[2] if len(argv) > 2 else r"C:\Users\lelil\Desktop\Alien_cascadeur.qrigcasc"

ARMATURE = "Alien"
MESH = "BODY"
REPARENT = {
    "MCH-toes.L": "MCH-ankle.L",
    "MCH-toes.R": "MCH-ankle.R",
    "spine lower": "pelvis",
    "root_tail": "pelvis",
}

# Слот рига Cascadeur → кость Чужого
MAP = {
    "Body": {
        "Body": [("pelvis", "pelvis"), ("stomach", "spine lower"), ("chest", "spine middle"),
                 ("neck", "head neck lower"), ("head", "Head")],
        "Left arm": [("clavicle_l", "Shoulder.L"), ("arm_l", "MCH-arm_shoulder.L"),
                     ("forearm_l", "MCH-forearm.L"), ("hand_l", "MCH-hand.L")],
        "Right arm": [("clavicle_r", "Shoulder.R"), ("arm_r", "MCH-arm_shoulder.R"),
                      ("forearm_r", "MCH-forearm.R"), ("hand_r", "MCH-hand.R")],
        # дигитиградная нога: бедро / голень / плюсна / пальцы
        "Left leg": [("thigh_l", "MCH-thigh.L"), ("calf_l", "MCH-knee.L"),
                     ("foot_l", "MCH-ankle.L"), ("toe_l", "MCH-toes.L")],
        "Right leg": [("thigh_r", "MCH-thigh.R"), ("calf_r", "MCH-knee.R"),
                      ("foot_r", "MCH-ankle.R"), ("toe_r", "MCH-toes.R")],
    },
    "Left hand": {
        "Thumb": [(f"thumb_l_{i}", f"thumb-{i}.L") for i in (1, 2, 3)],
        "Index finger": [(f"index_finger_l_{i}", f"ptr-{i}.L") for i in (1, 2, 3)],
        "Middle finger": [(f"middle_finger_l_{i}", f"middle-{i}.L") for i in (1, 2, 3)],
        "Ring finger": [(f"ring_finger_l_{i}", f"noname-{i}.L") for i in (1, 2, 3)],
        "Pinky": [(f"pinky_l_{i}", f"pinky-{i}.L") for i in (1, 2, 3)],
    },
    "Right hand": {
        "Thumb": [(f"thumb_r_{i}", f"thumb-{i}.R") for i in (1, 2, 3)],
        "Index finger": [(f"index_finger_r_{i}", f"ptr-{i}.R") for i in (1, 2, 3)],
        "Middle finger": [(f"middle_finger_r_{i}", f"middle-{i}.R") for i in (1, 2, 3)],
        "Ring finger": [(f"ring_finger_r_{i}", f"noname-{i}.R") for i in (1, 2, 3)],
        "Pinky": [(f"pinky_r_{i}", f"pinky-{i}.R") for i in (1, 2, 3)],
    },
}
# Twist-кости: (секция, слот, кость) — по одной записи на секцию, как в шаблонах Cascadeur
TWISTS = [
    ("Left arm", "arm_l", "Twist-Shoulderarm.L"), ("Left arm", "forearm_l", "Twist-forearm.L"),
    ("Right arm", "arm_r", "Twist-Shoulderarm.R"), ("Right arm", "forearm_r", "Twist-forearm.R"),
]


def main():
    bpy.ops.wm.open_mainfile(filepath=BLEND)
    arm = bpy.data.objects[ARMATURE]
    body = bpy.data.objects[MESH]

    # --- 1. иерархия
    for ob in bpy.data.objects:
        ob.select_set(False)
    arm.hide_set(False)
    arm.hide_viewport = False
    arm.select_set(True)
    bpy.context.view_layer.objects.active = arm
    if bpy.context.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    bpy.ops.object.mode_set(mode="EDIT")
    eb = arm.data.edit_bones
    # в исходнике есть кость " pinky-3.L" с пробелом в начале — чистим имена
    for b in eb:
        if b.name != b.name.strip():
            print(f"[cascadeur] имя '{b.name}' -> '{b.name.strip()}'")
            b.name = b.name.strip()
    for child, parent in REPARENT.items():
        if child in eb and parent in eb:
            eb[child].use_connect = False
            eb[child].parent = eb[parent]
            print(f"[cascadeur] {child} -> {parent}")
        else:
            print(f"[cascadeur] нет кости: {child} / {parent}")
    bpy.ops.object.mode_set(mode="OBJECT")
    arm.data.pose_position = "REST"
    arm.animation_data_clear()

    missing = [b for sec in MAP.values() for names in sec.values() for _, b in names if b not in arm.data.bones]
    missing += [b for _, _, b in TWISTS if b not in arm.data.bones]
    if missing:
        print("[cascadeur] ВНИМАНИЕ, нет костей:", missing)

    # --- 2. FBX: только скелет + тело; масштаб тела (1.172) запекаем в вершины
    for ob in bpy.data.objects:
        ob.select_set(False)
    body.hide_set(False)
    body.hide_viewport = False
    body.select_set(True)
    bpy.context.view_layer.objects.active = body
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    arm.select_set(True)
    bpy.context.view_layer.objects.active = arm
    bpy.ops.export_scene.fbx(
        filepath=OUT_FBX,
        use_selection=True,
        object_types={"ARMATURE", "MESH"},
        apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z", axis_up="Y",
        use_armature_deform_only=True,
        add_leaf_bones=False,
        armature_nodetype="NULL",
        primary_bone_axis="Y", secondary_bone_axis="X",
        mesh_smooth_type="FACE",
        use_mesh_modifiers=True,
        bake_anim=False,
        path_mode="AUTO", embed_textures=False,
    )
    print("[cascadeur] FBX:", OUT_FBX)

    # --- 3. шаблон Quick Rigging Tool
    def path_of(name):
        b = arm.data.bones[name]
        out = []
        p = b.parent
        while p is not None:
            out.append(p.name)
            p = p.parent
        out.reverse()
        return out

    def entry(slot, bone):
        return {"Bone name": slot, "Joint name": bone, "Joint path": path_of(bone)}

    doc = []
    for title, sections in MAP.items():
        doc.append({"Title": title, "Sections": [
            {"Section": sec, "Names": [entry(s, b) for s, b in names if b in arm.data.bones]}
            for sec, names in sections.items()]})
    doc.append({"Title": "Twist bones", "Sections": [
        {"Section": sec, "Names": [entry(slot, bone)]} for sec, slot, bone in TWISTS if bone in arm.data.bones]})
    rig = {"Document": doc, "Settings": {"Is align pelvis": True, "Is create layers": True}}
    with open(OUT_RIG, "w", encoding="utf-8") as f:
        json.dump(rig, f, indent="\t", ensure_ascii=False)
    print("[cascadeur] шаблон:", OUT_RIG)

    # копия сцены с починенной иерархией — на всякий случай, оригинал не трогаем
    out_blend = os.path.splitext(OUT_FBX)[0] + ".blend"
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
    print("[cascadeur] blend:", out_blend)


main()
