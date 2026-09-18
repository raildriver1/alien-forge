"""AlienForge: материал оболочки купола Чужого (SHELL01) в Blender — порт
shaders/alien/alien_shell.gdshader из Alien: Together.

Запуск: Blender → Scripting → Open → этот файл → Run Script (Alt+P).
Выдели меш купола (или ничего — тогда материал возьмут все объекты, у которых
в имени материала есть SHELL). Текстуры берутся из TEX_DIR (png из проекта Godot).

Что собирается (1:1 c gdshader):
  Base Color = alien_body[d] × tint(0.137,0.122,0.114) × lerp(1, drt_metal_3(uv×5), dirt)
  Alpha      = 0.7 в лоб → 1.0 на краю (Layer Weight.Facing → Map Range smoothstep)
  Normal     = alien[n] + chippedmetal[n](uv×10) × 0.25
  Roughness  = 1 − drt_plastic_1.g × 0.92 ; Specular = drt_plastic_1.r × 0.4 ; Metallic 0.35
Прозрачность включается настройками материала (Blended / Alpha Blend, без backface).
"""
import os
import bpy

TEX_DIR = r"C:\Users\lelil\Desktop\alien-blocky\assets\textures\alien"
MAT_NAME = "ALIEN_SHELL"

TINT = (0.137, 0.122, 0.114)
DIRT_UV, DIRT_AMOUNT = 5.0, 0.5
OPACITY, RAMP_MIN, RAMP_MAX = 0.7, 1.0, 0.5
SEC_NORMAL_UV, SEC_NORMAL_STRENGTH = 10.0, 0.25
SPEC_UV, SPEC_TINT, SPEC_POWER, METALLIC = 3.5, 0.4, 0.92, 0.35


def load_image(name, non_color=False):
    path = os.path.join(TEX_DIR, name)
    img = bpy.data.images.get(name)
    if img is None:
        if not os.path.exists(path):
            raise FileNotFoundError(path)
        img = bpy.data.images.load(path)
    if non_color:
        img.colorspace_settings.name = "Non-Color"
    return img


def tex(nodes, links, img, uv_mult, x, y, label):
    n = nodes.new("ShaderNodeTexImage")
    n.image = img
    n.label = label
    n.location = (x, y)
    n.interpolation = "Linear"
    n.extension = "REPEAT"
    if uv_mult != 1.0:
        m = nodes.new("ShaderNodeMapping")
        m.location = (x - 200, y)
        m.inputs["Scale"].default_value = (uv_mult, uv_mult, 1.0)
        uv = nodes.new("ShaderNodeTexCoord")
        uv.location = (x - 400, y)
        links.new(uv.outputs["UV"], m.inputs["Vector"])
        links.new(m.outputs["Vector"], n.inputs["Vector"])
    return n


def build(mat):
    mat.use_nodes = True
    nt = mat.node_tree
    nodes, links = nt.nodes, nt.links
    nodes.clear()

    out = nodes.new("ShaderNodeOutputMaterial")
    out.location = (900, 0)
    bsdf = nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.location = (600, 0)
    links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])

    # --- цвет: diffuse² × tint × грязь
    d = tex(nodes, links, load_image("alien_body[d].png"), 1.0, -600, 500, "alien_body[d]")
    tint = nodes.new("ShaderNodeMix")
    tint.data_type = "RGBA"
    tint.blend_type = "MULTIPLY"
    tint.location = (-250, 500)
    tint.inputs["Factor"].default_value = 1.0
    tint.inputs[7].default_value = (*TINT, 1.0)
    links.new(d.outputs["Color"], tint.inputs[6])

    dirt = tex(nodes, links, load_image("drt_metal_3.png"), DIRT_UV, -600, 200, "drt_metal_3")
    dirt_mix = nodes.new("ShaderNodeMix")
    dirt_mix.data_type = "RGBA"
    dirt_mix.blend_type = "MULTIPLY"
    dirt_mix.location = (0, 400)
    dirt_mix.label = "dirt amount"
    dirt_mix.inputs["Factor"].default_value = DIRT_AMOUNT
    links.new(tint.outputs[2], dirt_mix.inputs[6])
    links.new(dirt.outputs["Color"], dirt_mix.inputs[7])
    links.new(dirt_mix.outputs[2], bsdf.inputs["Base Color"])

    # --- альфа по углу: Facing = 1 − N·V; 0.7 в лоб → 1.0 на краю
    lw = nodes.new("ShaderNodeLayerWeight")
    lw.location = (-250, 100)
    lw.inputs["Blend"].default_value = 0.5
    ramp = nodes.new("ShaderNodeMapRange")
    ramp.location = (0, 100)
    ramp.label = "angular opacity"
    ramp.interpolation_type = "SMOOTHSTEP"
    ramp.inputs["From Min"].default_value = 1.0 - RAMP_MIN   # N·V = 1
    ramp.inputs["From Max"].default_value = 1.0 - RAMP_MAX   # N·V = 0.5
    ramp.inputs["To Min"].default_value = OPACITY
    ramp.inputs["To Max"].default_value = 1.0
    ramp.clamp = True
    links.new(lw.outputs["Facing"], ramp.inputs["Value"])
    links.new(ramp.outputs["Result"], bsdf.inputs["Alpha"])

    # --- спекуляр: r → F0, g → глянец
    s = tex(nodes, links, load_image("drt_plastic_1.png", non_color=True), SPEC_UV, -600, -200, "drt_plastic_1")
    sep = nodes.new("ShaderNodeSeparateColor")
    sep.location = (-250, -200)
    links.new(s.outputs["Color"], sep.inputs["Color"])
    f0 = nodes.new("ShaderNodeMath")
    f0.operation = "MULTIPLY"
    f0.location = (0, -150)
    f0.label = "F0 = r × SPECULAR_TINT"
    f0.inputs[1].default_value = SPEC_TINT
    links.new(sep.outputs["Red"], f0.inputs[0])
    # Specular IOR Level: 0.5 ≈ F0 0.04; F0 0.37 → уровень ~1.0 (обрезаем сверху)
    f0_lvl = nodes.new("ShaderNodeMath")
    f0_lvl.operation = "MULTIPLY"
    f0_lvl.location = (200, -150)
    f0_lvl.inputs[1].default_value = 12.5
    f0_lvl.use_clamp = True
    links.new(f0.outputs[0], f0_lvl.inputs[0])
    spec_in = bsdf.inputs.get("Specular IOR Level") or bsdf.inputs.get("Specular")
    links.new(f0_lvl.outputs[0], spec_in)
    gloss = nodes.new("ShaderNodeMath")
    gloss.operation = "MULTIPLY"
    gloss.location = (0, -300)
    gloss.label = "gloss = g × SPECULAR_POWER"
    gloss.inputs[1].default_value = SPEC_POWER
    links.new(sep.outputs["Green"], gloss.inputs[0])
    rough = nodes.new("ShaderNodeMath")
    rough.operation = "SUBTRACT"
    rough.location = (200, -300)
    rough.label = "roughness = 1 − gloss"
    rough.inputs[0].default_value = 1.0
    links.new(gloss.outputs[0], rough.inputs[1])
    links.new(rough.outputs[0], bsdf.inputs["Roughness"])
    bsdf.inputs["Metallic"].default_value = METALLIC

    # --- нормали: основная + вторичная (RG-складываются до реконструкции Z)
    n1 = tex(nodes, links, load_image("alien[n].png", non_color=True), 1.0, -600, -600, "alien[n]")
    n2 = tex(nodes, links, load_image("chippedmetal[n].png", non_color=True), SEC_NORMAL_UV, -600, -900, "chippedmetal[n]")
    nmix = nodes.new("ShaderNodeMix")
    nmix.data_type = "RGBA"
    nmix.blend_type = "MIX"
    nmix.location = (-250, -700)
    nmix.label = "secondary normal"
    nmix.inputs["Factor"].default_value = SEC_NORMAL_STRENGTH / (1.0 + SEC_NORMAL_STRENGTH)
    links.new(n1.outputs["Color"], nmix.inputs[6])
    links.new(n2.outputs["Color"], nmix.inputs[7])
    nm = nodes.new("ShaderNodeNormalMap")
    nm.location = (200, -700)
    nm.inputs["Strength"].default_value = 1.0 + SEC_NORMAL_STRENGTH
    links.new(nmix.outputs[2], nm.inputs["Color"])
    links.new(nm.outputs["Normal"], bsdf.inputs["Normal"])

    # --- прозрачность: без этого Alpha в EEVEE игнорируется
    if hasattr(mat, "surface_render_method"):          # Blender 4.2+
        mat.surface_render_method = "BLENDED"
    else:
        mat.blend_method = "BLEND"
        if hasattr(mat, "shadow_method"):
            mat.shadow_method = "NONE"
    if hasattr(mat, "use_transparency_overlap"):
        mat.use_transparency_overlap = True
    mat.use_backface_culling = True
    if hasattr(mat, "show_transparent_back"):
        mat.show_transparent_back = False
    # видно и в Solid-режиме
    mat.diffuse_color = (*TINT, OPACITY)


def main():
    mat = bpy.data.materials.get(MAT_NAME) or bpy.data.materials.new(MAT_NAME)
    build(mat)
    targets = [o for o in bpy.context.selected_objects if o.type == "MESH"]
    if not targets:
        targets = [o for o in bpy.data.objects if o.type == "MESH"
                   and any(ms.material and "SHELL" in ms.material.name.upper() for ms in o.material_slots)]
    assigned = 0
    for o in targets:
        if not o.material_slots:
            o.data.materials.append(mat)
            assigned += 1
            continue
        for ms in o.material_slots:
            if ms.material is None or ms.material == mat or "SHELL" in ms.material.name.upper() or o in bpy.context.selected_objects:
                ms.material = mat
                assigned += 1
    print(f"[AlienForge] {MAT_NAME}: назначен на {assigned} слот(ов) у {len(targets)} объектов")


main()
