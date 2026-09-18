# AlienForge → Blender: материалы Alien: Isolation 1:1 по данным шейдера.
#
# Как пользоваться:
#   1. File → Import → glTF 2.0: импортируй .glb из alien-forge (модель или уровень).
#   2. Text Editor → Open → этот файл → Run Script (Alt+P).
#      Или: Edit → Preferences → Add-ons → Install… → этот файл, включить,
#      потом F3 → «AlienForge: Rebuild materials».
#   3. Скрипт для каждого материала с extras от alien-forge (ubershader,
#      samplers, params, features) собирает дерево нод заново:
#        DIFFUSE_MAP.rgb (sRGB) × DIFFUSE_TINT × Color Attribute (VERTEX_COLOUR)
#            × DIRT_MAP (по весу из Color Attribute) × AO.g          → Base Color
#        DIFFUSE_MAP.a / SEPARATE_ALPHA_MAP.r|g                        → Alpha
#        NORMAL_MAP.rg (+ SECONDARY_NORMAL_MAP.rg × strength), z восстановлен → Normal
#        SPECULAR_MAP.r × SPECULAR_TINT / 0.08                           → Specular IOR Level
#        1 − SPECULAR_MAP.g × SPECULAR_POWER                             → Roughness
#        SPECULAR_MAP.b (при SPECULAR_MAPPING_METALNESS_MASKING)         → Metallic
#        GLOW_MAP / LIGHTMAP                                             → Emission
#      Что именно лежит в каналах — из дизассемблированных пиксельных
#      шейдеров игры, см. ChannelSemantics.cs и документ «Материалы AI → Blender».
#
# Откуда берутся данные: extras материала glTF. Импортёр Blender кладёт их в
# custom properties материала; на всякий случай alien-forge дублирует всё одной
# строкой JSON в extras["alienforge_json"].

bl_info = {
    "name": "AlienForge materials",
    "author": "alien-forge",
    "version": (1, 0, 0),
    "blender": (3, 6, 0),
    "location": "F3 → AlienForge: Rebuild materials",
    "description": "Пересобирает материалы, импортированные из .glb alien-forge, по данным шейдеров Alien: Isolation",
    "category": "Material",
}

import json
import bpy


# ------------------------------------------------------------------ данные

def read_extras(mat):
    """extras alien-forge из custom properties материала (или None)."""
    raw = mat.get("alienforge_json")
    if raw:
        try:
            return json.loads(raw)
        except Exception:
            pass
    if mat.get("ubershader") is None:
        return None

    def plain(v):
        if hasattr(v, "to_dict"):
            return {k: plain(x) for k, x in v.to_dict().items()}
        if hasattr(v, "to_list"):
            return [plain(x) for x in v.to_list()]
        if isinstance(v, (list, tuple)):
            return [plain(x) for x in v]
        return v

    return {k: plain(mat[k]) for k in mat.keys() if not k.startswith("_")}


def find_image(name):
    """Картинка Blender по имени из glTF (имя может быть обрезано до 63 символов и получить .001)."""
    if not name:
        return None
    img = bpy.data.images.get(name)
    if img:
        return img
    short = name[:63]
    for cand in bpy.data.images:
        n = cand.name
        base = n[:-4] if len(n) > 4 and n[-4] == "." and n[-3:].isdigit() else n
        if base == short or base == short[:len(base)] and len(base) >= 55:
            return cand
    leaf = name.replace("\\", "/").split("/")[-1]
    for cand in bpy.data.images:
        if cand.name.split("/")[-1].split("\\")[-1].startswith(leaf[:40]):
            return cand
    return None


def find_image_slot(s):
    """Картинка для слота: по имени image (экспорт уровня) или texture (экспорт модели)."""
    if not s:
        return None
    img = None
    if s.get("image") and isinstance(s.get("image"), str):
        img = find_image(s["image"])
    if img is None:
        img = find_image(s.get("texture"))
    return img


def param(extras, name, default):
    p = extras.get("params") or {}
    v = p.get(name)
    if v is None:
        return default
    if isinstance(v, (list, tuple)):
        return list(v) if len(v) > 1 else (v[0] if v else default)
    return v


def slot(extras, sampler):
    for s in extras.get("shader") or []:
        if s.get("sampler") == sampler:
            return s
    return None


# ------------------------------------------------------------------ ноды

def link(tree, a, a_out, b, b_in):
    tree.links.new(a.outputs[a_out], b.inputs[b_in])


def new(tree, kind, x, y, label=None):
    n = tree.nodes.new(kind)
    n.location = (x, y)
    if label:
        n.label = label
    return n


def image_node(tree, img, x, y, label, srgb, uv_mult=1.0):
    n = new(tree, "ShaderNodeTexImage", x, y, label)
    n.image = img
    if img:
        img.colorspace_settings.name = "sRGB" if srgb else "Non-Color"
    if uv_mult and abs(uv_mult - 1.0) > 1e-4:
        uv = new(tree, "ShaderNodeUVMap", x - 380, y)
        mp = new(tree, "ShaderNodeMapping", x - 200, y, f"UV × {uv_mult:g}")
        mp.inputs["Scale"].default_value = (uv_mult, uv_mult, 1.0)
        link(tree, uv, "UV", mp, "Vector")
        link(tree, mp, "Vector", n, "Vector")
    return n


def mix_rgb(tree, x, y, blend, fac=1.0, label=None):
    """Mix Color, совместимо с 3.x и 4.x."""
    if hasattr(bpy.types, "ShaderNodeMix"):
        n = new(tree, "ShaderNodeMix", x, y, label)
        n.data_type = "RGBA"
        n.blend_type = blend
        n.inputs["Factor"].default_value = fac
        return n, "Factor", "A", "B", "Result"
    n = new(tree, "ShaderNodeMixRGB", x, y, label)
    n.blend_type = blend
    n.inputs["Fac"].default_value = fac
    return n, "Fac", "Color1", "Color2", "Color"


def normal_rg_group():
    """Группа: RG normal-карты (0..1) → нормаль с восстановленным Z, как в шейдере игры."""
    name = "AF Normal RG→XYZ"
    g = bpy.data.node_groups.get(name)
    if g:
        return g
    g = bpy.data.node_groups.new(name, "ShaderNodeTree")
    iface = g.interface if hasattr(g, "interface") else None
    if iface:
        iface.new_socket("Color", in_out="INPUT", socket_type="NodeSocketColor")
        iface.new_socket("Strength", in_out="INPUT", socket_type="NodeSocketFloat")
        iface.new_socket("Color2", in_out="INPUT", socket_type="NodeSocketColor")
        iface.new_socket("Strength2", in_out="INPUT", socket_type="NodeSocketFloat")
        iface.new_socket("Normal", in_out="OUTPUT", socket_type="NodeSocketColor")
    else:
        g.inputs.new("NodeSocketColor", "Color")
        g.inputs.new("NodeSocketFloat", "Strength")
        g.inputs.new("NodeSocketColor", "Color2")
        g.inputs.new("NodeSocketFloat", "Strength2")
        g.outputs.new("NodeSocketColor", "Normal")
    gi = g.nodes.new("NodeGroupInput"); gi.location = (-900, 0)
    go = g.nodes.new("NodeGroupOutput"); go.location = (700, 0)

    def sep(inp, y):
        s = g.nodes.new("ShaderNodeSeparateColor" if hasattr(bpy.types, "ShaderNodeSeparateColor") else "ShaderNodeSeparateRGB")
        s.location = (-700, y)
        g.links.new(gi.outputs[inp], s.inputs[0])
        return s

    def unpack(sock, strength_sock, y):
        # (c*2-1)*strength
        m = g.nodes.new("ShaderNodeMath"); m.operation = "MULTIPLY_ADD"; m.location = (-500, y)
        m.inputs[1].default_value = 2.0; m.inputs[2].default_value = -1.0
        g.links.new(sock, m.inputs[0])
        k = g.nodes.new("ShaderNodeMath"); k.operation = "MULTIPLY"; k.location = (-330, y)
        g.links.new(m.outputs[0], k.inputs[0]); g.links.new(strength_sock, k.inputs[1])
        return k.outputs[0]

    s1 = sep("Color", 200); s2 = sep("Color2", -200)
    x1 = unpack(s1.outputs[0], gi.outputs["Strength"], 300)
    y1 = unpack(s1.outputs[1], gi.outputs["Strength"], 150)
    x2 = unpack(s2.outputs[0], gi.outputs["Strength2"], -100)
    y2 = unpack(s2.outputs[1], gi.outputs["Strength2"], -250)
    ax = g.nodes.new("ShaderNodeMath"); ax.operation = "ADD"; ax.location = (-150, 250)
    ay = g.nodes.new("ShaderNodeMath"); ay.operation = "ADD"; ay.location = (-150, 50)
    g.links.new(x1, ax.inputs[0]); g.links.new(x2, ax.inputs[1])
    g.links.new(y1, ay.inputs[0]); g.links.new(y2, ay.inputs[1])
    # z = sqrt(max(0, 1 - x² - y²))
    xx = g.nodes.new("ShaderNodeMath"); xx.operation = "MULTIPLY"; xx.location = (0, 250)
    yy = g.nodes.new("ShaderNodeMath"); yy.operation = "MULTIPLY"; yy.location = (0, 50)
    g.links.new(ax.outputs[0], xx.inputs[0]); g.links.new(ax.outputs[0], xx.inputs[1])
    g.links.new(ay.outputs[0], yy.inputs[0]); g.links.new(ay.outputs[0], yy.inputs[1])
    s = g.nodes.new("ShaderNodeMath"); s.operation = "ADD"; s.location = (150, 150)
    g.links.new(xx.outputs[0], s.inputs[0]); g.links.new(yy.outputs[0], s.inputs[1])
    one = g.nodes.new("ShaderNodeMath"); one.operation = "SUBTRACT"; one.location = (300, 150)
    one.inputs[0].default_value = 1.0; g.links.new(s.outputs[0], one.inputs[1])
    mx = g.nodes.new("ShaderNodeMath"); mx.operation = "MAXIMUM"; mx.location = (430, 150); mx.inputs[1].default_value = 0.0
    g.links.new(one.outputs[0], mx.inputs[0])
    sq = g.nodes.new("ShaderNodeMath"); sq.operation = "SQRT"; sq.location = (560, 150)
    g.links.new(mx.outputs[0], sq.inputs[0])
    # обратно в 0..1 для ноды Normal Map
    def pack(sock, y):
        m = g.nodes.new("ShaderNodeMath"); m.operation = "MULTIPLY_ADD"; m.location = (450, y)
        m.inputs[1].default_value = 0.5; m.inputs[2].default_value = 0.5
        g.links.new(sock, m.inputs[0]); return m.outputs[0]
    comb = g.nodes.new("ShaderNodeCombineColor" if hasattr(bpy.types, "ShaderNodeCombineColor") else "ShaderNodeCombineRGB")
    comb.location = (600, -50)
    g.links.new(pack(ax.outputs[0], 380), comb.inputs[0])
    g.links.new(pack(ay.outputs[0], -20), comb.inputs[1])
    g.links.new(pack(sq.outputs[0], -120), comb.inputs[2])
    g.links.new(comb.outputs[0], go.inputs[0])
    return g


def set_alpha_mode(mat, mode, cutoff):
    if hasattr(mat, "surface_render_method"):  # 4.2+
        mat.surface_render_method = "BLENDED" if mode == "BLEND" else "DITHERED"
    else:
        mat.blend_method = {"BLEND": "BLEND", "MASK": "CLIP"}.get(mode, "OPAQUE")
        if mode == "MASK":
            mat.alpha_threshold = cutoff
        mat.shadow_method = "HASHED" if mode != "OPAQUE" else "OPAQUE"


# ------------------------------------------------------------------ сборка

def rebuild(mat, extras, report):
    uber = extras.get("ubershader") or "?"
    features = set(extras.get("features") or [])
    if isinstance(extras.get("features"), str):
        features = set(x for x in extras["features"].split(",") if x)
    if uber in ("CA_PARTICLE", "CA_RIBBON", "CA_FOGPLANE", "CA_FOGSPHERE", "CA_VOLUME_LIGHT",
                "CA_OCCLUSION_CULLING", "CA_SHADOWCASTER", "CA_DEBUG", "CA_LIGHT_DECAL"):
        report.append(f"{mat.name}: {uber} — служебный/эффектный шейдер, пропущен")
        return False

    mat.use_nodes = True
    tree = mat.node_tree
    tree.nodes.clear()
    out = new(tree, "ShaderNodeOutputMaterial", 900, 0)
    bsdf = new(tree, "ShaderNodeBsdfPrincipled", 560, 0, uber)
    link(tree, bsdf, "BSDF", out, "Surface")
    ins = bsdf.inputs
    spec_in = "Specular IOR Level" if "Specular IOR Level" in ins else "Specular"
    emis_in = "Emission Color" if "Emission Color" in ins else "Emission"

    # --- вода / преломление: прозрачная поверхность с рябью, диффуза нет
    if uber in ("CA_SIMPLEWATER", "CA_NONINTERACTIVE_WATER", "CA_REFRACTION", "CA_SIMPLE_REFRACTION"):
        ins["Base Color"].default_value = (0.02, 0.04, 0.05, 1.0)
        ins["Roughness"].default_value = 0.05
        ins["Alpha"].default_value = 0.45
        if "Transmission Weight" in ins:
            ins["Transmission Weight"].default_value = 0.6
        nm = slot(extras, "NORMAL_MAP")
        if nm and img_ok(nm):
            n = image_node(tree, find_image_slot(nm), -600, -300, "NORMAL_MAP (RG)", False)
            grp = new(tree, "ShaderNodeGroup", -150, -300, "RG → XYZ")
            grp.node_tree = normal_rg_group()
            grp.inputs["Strength"].default_value = 1.0
            grp.inputs["Color2"].default_value = (0.5, 0.5, 1.0, 1.0)
            grp.inputs["Strength2"].default_value = 0.0
            link(tree, n, "Color", grp, "Color")
            nmap = new(tree, "ShaderNodeNormalMap", 150, -300)
            link(tree, grp, "Normal", nmap, "Color")
            link(tree, nmap, "Normal", bsdf, "Normal")
        set_alpha_mode(mat, "BLEND", 0.5)
        mat.use_backface_culling = False
        return True

    tint = param(extras, "DIFFUSE_TINT", [1, 1, 1, 1])
    if not isinstance(tint, list):
        tint = [tint, tint, tint, 1]
    while len(tint) < 4:
        tint.append(1.0)

    y = 600
    color_out = None

    # --- диффуз
    d = slot(extras, "DIFFUSE_MAP") or slot(extras, "TEXTURE_MAP") or slot(extras, "FACE_MAP") or slot(extras, "IRIS_MAP")
    if d:
        img = find_image_slot(d)
        n = image_node(tree, img, -600, y, d.get("sampler"), True, param(extras, "DIFFUSE_UV_MULT", 1.0))
        if img is None:
            report.append(f"{mat.name}: картинка не найдена {d.get('texture')}")
        m, fac, a, b, res = mix_rgb(tree, -250, y, "MULTIPLY", 1.0, "× DIFFUSE_TINT")
        m.inputs[b].default_value = (tint[0], tint[1], tint[2], 1.0)
        link(tree, n, "Color", m, a)
        color_out = (m, res)
        diffuse_node = n
    else:
        diffuse_node = None
        rgb = new(tree, "ShaderNodeRGB", -250, y, "DIFFUSE_TINT")
        rgb.outputs[0].default_value = (tint[0], tint[1], tint[2], 1.0)
        color_out = (rgb, "Color")

    # --- цвет вершин: у окружения (CA_ENVIRONMENT и родня) COLOR0.rgb умножает
    # альбедо; у персонажей COLOR0.r — вес грязи (1 = чистый), на цвет не влияет
    character = uber in ("CA_CHARACTER", "CA_SKIN", "CA_HAIR", "CA_EYE", "CA_LOW_LOD_CHARACTER")
    if "VERTEX_COLOUR" in features and not character:
        ca = new(tree, "ShaderNodeVertexColor", -250, y - 200, "Color Attribute (VERTEX_COLOUR)")
        m, fac, a, b, res = mix_rgb(tree, 0, y - 100, "MULTIPLY", 1.0, "× vertex colour")
        link(tree, color_out[0], color_out[1], m, a)
        link(tree, ca, "Color", m, b)
        color_out = (m, res)

    # --- грязь
    dirt = slot(extras, "DIRT_MAP")
    if dirt and "DIRT_MAPPING" in features:
        img = find_image_slot(dirt)
        n = image_node(tree, img, -600, y - 400, "DIRT_MAP", True, param(extras, "DIRT_UV_MULT", 1.0))
        m, fac, a, b, res = mix_rgb(tree, 150, y - 300, "MULTIPLY", 0.0, "× грязь (вес 1 − COLOR0.r²)")
        link(tree, color_out[0], color_out[1], m, a)
        link(tree, n, "Color", m, b)
        # шейдер: dirt' = lerp(dirt, 1, r²), r = COLOR0.r  →  фактор смешивания = 1 − r²
        ca = new(tree, "ShaderNodeVertexColor", -450, y - 450, "COLOR0 (вес грязи)")
        sepc = new(tree, "ShaderNodeSeparateColor" if hasattr(bpy.types, "ShaderNodeSeparateColor") else "ShaderNodeSeparateRGB", -250, y - 450)
        link(tree, ca, "Color", sepc, 0)
        sq = new(tree, "ShaderNodeMath", -80, y - 450, "r²"); sq.operation = "MULTIPLY"
        link(tree, sepc, 0, sq, 0); link(tree, sepc, 0, sq, 1)
        inv = new(tree, "ShaderNodeMath", 60, y - 450, "1 − r²"); inv.operation = "SUBTRACT"
        inv.inputs[0].default_value = 1.0; inv.use_clamp = True
        link(tree, sq, 0, inv, 1)
        link(tree, inv, 0, m, fac)
        color_out = (m, res)

    # --- AO
    ao = slot(extras, "AMBIENT_OCCLUSION_MAP")
    if ao:
        img = find_image_slot(ao)
        n = image_node(tree, img, -600, y - 700, "AMBIENT_OCCLUSION_MAP (G)", False)
        sepc = new(tree, "ShaderNodeSeparateColor" if hasattr(bpy.types, "ShaderNodeSeparateColor") else "ShaderNodeSeparateRGB", -350, y - 700)
        link(tree, n, "Color", sepc, 0)
        m, fac, a, b, res = mix_rgb(tree, 300, y - 500, "MULTIPLY", 1.0, "× AO.g")
        link(tree, color_out[0], color_out[1], m, a)
        link(tree, sepc, 1, m, b)
        color_out = (m, res)

    link(tree, color_out[0], color_out[1], bsdf, "Base Color")

    # --- альфа
    alpha_mode = extras.get("alpha") or "OPAQUE"
    cutoff = 0.5
    if alpha_mode != "OPAQUE":
        sa = slot(extras, "SEPARATE_ALPHA_MAP")
        if sa and "SEPARATE_ALPHA" in features:
            img = find_image_slot(sa)
            n = image_node(tree, img, -600, y + 250, "SEPARATE_ALPHA_MAP", False, param(extras, "SEPARATE_ALPHA_UV_MULT", 1.0))
            sepc = new(tree, "ShaderNodeSeparateColor" if hasattr(bpy.types, "ShaderNodeSeparateColor") else "ShaderNodeSeparateRGB", -350, y + 250)
            link(tree, n, "Color", sepc, 0)
            ch = 1 if "SEPARATE_ALPHA_MAP_USE_GREEN_CHANNEL" in features else 0
            k = new(tree, "ShaderNodeMath", -150, y + 250, "× DIFFUSE_TINT.a"); k.operation = "MULTIPLY"
            k.inputs[1].default_value = tint[3]
            link(tree, sepc, ch, k, 0)
            link(tree, k, 0, bsdf, "Alpha")
        elif diffuse_node is not None:
            k = new(tree, "ShaderNodeMath", -150, y + 250, "× DIFFUSE_TINT.a"); k.operation = "MULTIPLY"
            k.inputs[1].default_value = tint[3]
            link(tree, diffuse_node, "Alpha", k, 0)
            link(tree, k, 0, bsdf, "Alpha")
        else:
            ins["Alpha"].default_value = tint[3]
        set_alpha_mode(mat, alpha_mode, cutoff)
    mat.use_backface_culling = "DOUBLE_SIDED" not in features

    # --- блик / глянец / металл
    sp = slot(extras, "SPECULAR_MAP")
    spec_tint = param(extras, "SPECULAR_TINT", 1.0)
    if isinstance(spec_tint, list):
        spec_tint = sum(spec_tint[:3]) / 3.0
    spec_power = param(extras, "SPECULAR_POWER", 1.0)
    if isinstance(spec_power, list):
        spec_power = spec_power[0]
    if sp and sp.get("derived_mr"):
        # Экспорт уровня кладёт уже собранную MR-карту: R = F0, G = roughness, B = металл
        img = find_image_slot(sp)
        n = image_node(tree, img, -600, -300, "SPECULAR (MR: R=F0, G=rough, B=metal)", False, param(extras, "SPECULAR_UV_MULT", 1.0))
        sepc = new(tree, "ShaderNodeSeparateColor" if hasattr(bpy.types, "ShaderNodeSeparateColor") else "ShaderNodeSeparateRGB", -350, -300)
        link(tree, n, "Color", sepc, 0)
        k = new(tree, "ShaderNodeMath", -150, -250, "F0 / 0.08 → Specular"); k.operation = "MULTIPLY"
        k.inputs[1].default_value = 1.0 / 0.08; k.use_clamp = True
        link(tree, sepc, 0, k, 0); link(tree, k, 0, bsdf, spec_in)
        link(tree, sepc, 1, bsdf, "Roughness")
        if "SPECULAR_MAPPING_METALNESS_MASKING" in features:
            link(tree, sepc, 2, bsdf, "Metallic")
    elif sp:
        img = find_image_slot(sp)
        n = image_node(tree, img, -600, -300, "SPECULAR_MAP", False, param(extras, "SPECULAR_UV_MULT", 1.0))
        sepc = new(tree, "ShaderNodeSeparateColor" if hasattr(bpy.types, "ShaderNodeSeparateColor") else "ShaderNodeSeparateRGB", -350, -300)
        link(tree, n, "Color", sepc, 0)
        if uber == "CA_HAIR":
            k = new(tree, "ShaderNodeMath", -150, -250, "R → specular"); k.operation = "MULTIPLY"
            k.inputs[1].default_value = spec_tint / 0.08
            link(tree, sepc, 0, k, 0); link(tree, k, 0, bsdf, spec_in)
        else:
            k = new(tree, "ShaderNodeMath", -150, -250, "R × SPECULAR_TINT / 0.08 → F0"); k.operation = "MULTIPLY"
            k.inputs[1].default_value = spec_tint / 0.08
            k.use_clamp = True
            link(tree, sepc, 0, k, 0); link(tree, k, 0, bsdf, spec_in)
            g = new(tree, "ShaderNodeMath", -150, -400, "G × SPECULAR_POWER → gloss"); g.operation = "MULTIPLY"
            g.inputs[1].default_value = spec_power
            link(tree, sepc, 1, g, 0)
            r = new(tree, "ShaderNodeMath", 50, -400, "1 − gloss → Roughness"); r.operation = "SUBTRACT"
            r.inputs[0].default_value = 1.0; r.use_clamp = True
            link(tree, g, 0, r, 1); link(tree, r, 0, bsdf, "Roughness")
            if "SPECULAR_MAPPING_METALNESS_MASKING" in features:
                link(tree, sepc, 2, bsdf, "Metallic")
    else:
        ins[spec_in].default_value = min(1.0, 0.08 * spec_tint / 0.08) if spec_tint else 0.5
        ins["Roughness"].default_value = max(0.0, min(1.0, 1.0 - 0.5 * spec_power))

    # --- нормали
    nm = slot(extras, "NORMAL_MAP")
    if nm and img_ok(nm):
        img = find_image_slot(nm)
        n = image_node(tree, img, -600, -800, "NORMAL_MAP (RG)", False, param(extras, "NORMAL_UV_MULT", 1.0))
        grp = new(tree, "ShaderNodeGroup", -150, -800, "RG → XYZ")
        grp.node_tree = normal_rg_group()
        grp.inputs["Strength"].default_value = float(param(extras, "NORMAL_MAP_STRENGTH_DIFFUSE", 1.0) or 1.0)
        grp.inputs["Color2"].default_value = (0.5, 0.5, 1.0, 1.0)
        grp.inputs["Strength2"].default_value = 0.0
        link(tree, n, "Color", grp, "Color")
        sn = slot(extras, "SECONDARY_NORMAL_MAP")
        if sn and "SECONDARY_NORMAL_MAPPING" in features and img_ok(sn):
            img2 = find_image_slot(sn)
            n2 = image_node(tree, img2, -600, -1100, "SECONDARY_NORMAL_MAP (RG)", False, param(extras, "SECONDARY_NORMAL_UV_MULT", 1.0))
            link(tree, n2, "Color", grp, "Color2")
            s2 = param(extras, "SECONDARY_NORMAL_MAP_STRENGTH_DIFFUSE", 1.0)
            grp.inputs["Strength2"].default_value = float(s2 if not isinstance(s2, list) else s2[0])
        nmap = new(tree, "ShaderNodeNormalMap", 150, -800)
        nmap.space = "TANGENT"
        link(tree, grp, "Normal", nmap, "Color")
        link(tree, nmap, "Normal", bsdf, "Normal")

    # --- свечение
    gl = slot(extras, "GLOW_MAP") or slot(extras, "INTENSITY_MAP")  # LIGHTMAP_MAP — запечённый свет, в Emission не идёт
    if gl:
        img = find_image_slot(gl)
        n = image_node(tree, img, -600, -1400, gl.get("sampler"), True)
        link(tree, n, "Color", bsdf, emis_in)
        if "Emission Strength" in ins:
            ins["Emission Strength"].default_value = 1.0
    elif "EMISSIVE" in features:
        # лампы, экраны, надписи: светится сам диффуз × DIFFUSE_TINT × EMISSIVE_TINT × EMISSIVE_MULT
        mult = param(extras, "EMISSIVE_MULT", 1.0)
        mult = float(mult[0] if isinstance(mult, list) else mult)
        et = param(extras, "EMISSIVE_TINT", [1, 1, 1, 1])
        if not isinstance(et, list):
            et = [et, et, et, 1]
        if mult > 0.001 and color_out is not None:
            m, fac, a, b, res = mix_rgb(tree, 300, -1400, "MULTIPLY", 1.0, "× EMISSIVE_TINT")
            m.inputs[b].default_value = (et[0], et[1], et[2], 1.0)
            link(tree, color_out[0], color_out[1], m, a)
            link(tree, m, res, bsdf, emis_in)
            if "Emission Strength" in ins:
                ins["Emission Strength"].default_value = max(1.0, mult * 4.0)

    # --- кожа: подповерхностное рассеивание как намёк
    if uber == "CA_SKIN" and "Subsurface Weight" in ins:
        ins["Subsurface Weight"].default_value = 0.1
    if uber == "CA_EYE":
        ins["Roughness"].default_value = 0.1
    # Активная нода — диффуз: в Solid-режиме с цветом «Texture» Blender рисует
    # картинку АКТИВНОЙ Image Texture ноды, и после импорта glTF с normal/spec
    # картами это оказывалась последняя созданная (normal map) — отсюда синие
    # «как будто нормали» поверхности во вьюпорте
    if diffuse_node is not None:
        for n in tree.nodes:
            n.select = False
        diffuse_node.select = True
        tree.nodes.active = diffuse_node
    return True


def fix_active_images():
    """Для всех материалов: активная нода = та, что подключена к Base Color.
    Лечит синие/серые поверхности в Solid-режиме после обычного импорта glTF."""
    fixed = 0
    for mat in bpy.data.materials:
        if not mat.use_nodes or mat.node_tree is None:
            continue
        bsdf = next((n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if bsdf is None or not bsdf.inputs["Base Color"].is_linked:
            continue
        node = bsdf.inputs["Base Color"].links[0].from_node
        # через Mix/Separate — идём вверх до Image Texture
        depth = 0
        while node.type != "TEX_IMAGE" and depth < 6:
            src = next((i for i in node.inputs if i.is_linked), None)
            if src is None:
                break
            node = src.links[0].from_node
            depth += 1
        if node.type == "TEX_IMAGE":
            for n in mat.node_tree.nodes:
                n.select = False
            node.select = True
            mat.node_tree.nodes.active = node
            fixed += 1
    return fixed


def img_ok(s):
    return bool(s and (s.get("texture") or s.get("image")))


def rebuild_all(only_selected=False):
    report = []
    mats = set()
    if only_selected:
        for ob in bpy.context.selected_objects:
            for ms in getattr(ob, "material_slots", []):
                if ms.material:
                    mats.add(ms.material)
    else:
        mats = set(bpy.data.materials)
    done = 0
    for mat in sorted(mats, key=lambda m: m.name):
        extras = read_extras(mat)
        if not extras:
            continue
        try:
            if rebuild(mat, extras, report):
                done += 1
        except Exception as ex:  # noqa
            report.append(f"{mat.name}: ошибка {ex}")
    fix_active_images()
    hide_decal_boxes()
    return done, report


def hide_decal_boxes():
    """DECAL_* — кубы-проекторы CA_DECAL (кровь, потёртости). В Blender проекции
    нет, поэтому показываем их каркасом и не рендерим (в Godot они станут Decal)."""
    for ob in bpy.data.objects:
        if ob.name.startswith("DECAL_") and ob.type == "MESH":
            ob.display_type = "WIRE"
            ob.hide_render = True


class AF_OT_fix_active(bpy.types.Operator):
    bl_idname = "alienforge.fix_active_images"
    bl_label = "AlienForge: Fix viewport textures (Solid mode)"
    bl_description = "Сделать активной нодой диффуз во всех материалах — чтобы Solid-режим показывал цвет, а не normal map"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        n = fix_active_images()
        self.report({"INFO"}, f"AlienForge: исправлено материалов {n}")
        return {"FINISHED"}


class AF_OT_rebuild(bpy.types.Operator):
    bl_idname = "alienforge.rebuild_materials"
    bl_label = "AlienForge: Rebuild materials"
    bl_description = "Пересобрать материалы из .glb alien-forge по данным шейдеров игры"
    bl_options = {"REGISTER", "UNDO"}

    only_selected: bpy.props.BoolProperty(name="Только выделенные объекты", default=False)

    def execute(self, context):
        done, report = rebuild_all(self.only_selected)
        for line in report:
            print("[alienforge]", line)
        self.report({"INFO"}, f"AlienForge: пересобрано материалов {done}; замечаний {len(report)} (см. консоль)")
        return {"FINISHED"}


def register():
    bpy.utils.register_class(AF_OT_rebuild)
    bpy.utils.register_class(AF_OT_fix_active)


def unregister():
    bpy.utils.unregister_class(AF_OT_rebuild)
    bpy.utils.unregister_class(AF_OT_fix_active)


if __name__ == "__main__":
    try:
        register()
    except Exception:
        pass
    done, report = rebuild_all()
    print(f"[alienforge] пересобрано материалов: {done}")
    for line in report:
        print("[alienforge]", line)
