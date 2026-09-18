@tool
extends EditorScenePostImport
## AlienForge → Godot: пост-импорт уровня из alien-forge (.glb + <файл>.glb.alienforge.json).
##
## Подключение: в настройках импорта .glb (вкладка «Импорт») → Import Script → этот файл,
## затем «Переимпортировать». Что делает:
##   • PFX_* узлы → GPUParticles3D по параметрам ParticleEmitterReference игры
##     (спрайт берётся из крошечного квада PFXTEX, который потом удаляется);
##   • FOG_* узлы → FogVolume с шейдером alienforge_fog.gdshader
##     (стелется сверху вниз, плотность из DENSITY/OPACITY, цвет из COLOUR_TINT);
##   • FogSetting → WorldEnvironment с объёмным туманом (без него FogVolume не виден);
##   • LIGHT_* → уже нативные Omni/SpotLight3D: выставляет bake mode STATIC и тени
##     только там, где cast_shadow в игре;
##   • ZONE_* → на корень вешается alienforge_zone_streaming.gd: рендерятся только
##     зоны рядом с камерой (как в OpenCAGE и в игре);
##   • DECAL_* (проекторы CA_DECAL игры: кровь, потёртости, подпалины) → узел Decal
##     с размером куба-проектора и текстурой материала.
## Суффиксы -col / -occonly / -rigid Godot обрабатывает сам, до этого скрипта.

const FOG_SHADER := "res://tools/godot/alienforge_fog.gdshader"
const ZONE_SCRIPT := "res://tools/godot/alienforge_zone_streaming.gd"

## Сливать статическую геометрию по зонам (см. _merge_zones)
const merge_static := true
## Не больше вершин в одном слитом меше (иначе режем на несколько)
const MERGE_MAX_VERTS := 400000
## Размер текселя лайтмапа для слитых мешей (м на тексель); 0 — не разворачивать UV2
const LIGHTMAP_TEXEL := 0.5

var _side: Dictionary = {}
var _by_node: Dictionary = {}
var _fog_by_node: Dictionary = {}
var _noise: NoiseTexture3D


func _post_import(scene: Node) -> Object:
	var src: String = get_source_file()
	var side_path := src + ".alienforge.json"
	if FileAccess.file_exists(side_path):
		var f := FileAccess.open(side_path, FileAccess.READ)
		var parsed = JSON.parse_string(f.get_as_text())
		if parsed is Dictionary:
			_side = parsed
	else:
		push_warning("AlienForge: нет %s — частицы и туман не собрать, остальное сделаю" % side_path)

	for p in _side.get("particles", []):
		_by_node[str(p.get("node", ""))] = p
	for fg in _side.get("fog", []):
		_fog_by_node[str(fg.get("node", ""))] = fg

	var stats := {"particles": 0, "fog": 0, "lights": 0, "zones": 0, "decals": 0}
	_walk(scene, scene, stats)

	# Слияние статики по зонам: один меш на (зона, материал) + одна тримеш-коллизия
	# на зону. Без этого уровень — 60 тысяч узлов, и редактор/игра/бак стоят колом.
	if merge_static:
		var t0 := Time.get_ticks_msec()
		_merge_zones(scene)
		print("[AlienForge] слияние заняло %.1f с" % ((Time.get_ticks_msec() - t0) / 1000.0))

	if _side.has("fog_settings") and not _side["fog_settings"].is_empty():
		_add_environment(scene, _side["fog_settings"])

	if stats["zones"] > 0 and ResourceLoader.exists(ZONE_SCRIPT):
		scene.set_script(load(ZONE_SCRIPT))

	print("[AlienForge] импорт %s: частиц %d, тумана %d, света %d, зон %d, декалей %d" % [src.get_file(), stats["particles"], stats["fog"], stats["lights"], stats["zones"], stats["decals"]])
	return scene


func _walk(node: Node, owner: Node, stats: Dictionary) -> void:
	# копия списка: дети меняются по ходу
	for child in node.get_children():
		_walk(child, owner, stats)

	var n := node.name
	if n.begins_with("PFX_") and _by_node.has(n):
		_make_particles(node, _by_node[n], owner)
		stats["particles"] += 1
	elif n.begins_with("FOG_") and _fog_by_node.has(n):
		_make_fog(node, _fog_by_node[n], owner)
		stats["fog"] += 1
	elif n.begins_with("ZONE_"):
		stats["zones"] += 1
	elif n.begins_with("DECAL_") and node is MeshInstance3D and (node as MeshInstance3D).mesh != null:
		_make_decal(node as MeshInstance3D, owner)
		stats["decals"] += 1
		return
	if node is Light3D:
		_setup_light(node as Light3D)
		stats["lights"] += 1
	# без слияния: коллизия на каждый «+col» меш отдельно
	if not merge_static and n.contains("+col") and node is MeshInstance3D and (node as MeshInstance3D).mesh != null:
		var body := StaticBody3D.new()
		var cs := CollisionShape3D.new()
		cs.shape = (node as MeshInstance3D).mesh.create_trimesh_shape()
		body.add_child(cs)
		node.add_child(body)
		body.owner = owner
		cs.owner = owner


# ---------------------------------------------------------------- декали

## CA_DECAL в игре — куб ±1 × decal_scale, проецирующий текстуру на то, что под
## ним (по своей оси Z). Godot Decal проецирует по своей оси −Y и размер задаёт
## в метрах — поворачиваем на 90° вокруг X и переносим размер из масштаба куба.
func _make_decal(mi: MeshInstance3D, owner: Node) -> void:
	var d := Decal.new()
	d.name = mi.name
	var ext: Vector3 = mi.mesh.get_aabb().size * mi.transform.basis.get_scale()
	d.size = Vector3(maxf(ext.x, 0.05), maxf(ext.z, 0.05), maxf(ext.y, 0.05))
	var mat: Material = mi.get_active_material(0)
	if mat is BaseMaterial3D:
		var bm := mat as BaseMaterial3D
		d.texture_albedo = bm.albedo_texture
		d.modulate = bm.albedo_color
		if bm.normal_enabled:
			d.texture_normal = bm.normal_texture
		if bm.emission_enabled:
			d.texture_emission = bm.emission_texture
			d.emission_energy = bm.emission_energy_multiplier
	d.albedo_mix = 1.0
	d.normal_fade = 0.4
	d.upper_fade = 1.0
	d.lower_fade = 1.0
	var b: Basis = mi.transform.basis.orthonormalized() * Basis(Vector3.RIGHT, PI / 2.0)
	d.transform = Transform3D(b, mi.transform.origin)
	var parent: Node = mi.get_parent()
	parent.add_child(d)
	d.owner = owner
	mi.free()


# ---------------------------------------------------------------- свет

func _setup_light(light: Light3D) -> void:
	# Свет в игре в основном запекается (радиосити) — для LightmapGI ставим STATIC:
	# после Bake он не считается в рантайме. Тени — только у shadow-источников.
	light.light_bake_mode = Light3D.BAKE_STATIC
	light.shadow_enabled = false
	if light is OmniLight3D:
		(light as OmniLight3D).omni_attenuation = 1.5
	elif light is SpotLight3D:
		(light as SpotLight3D).spot_attenuation = 1.5


# ---------------------------------------------------------------- частицы

func _pf(p: Dictionary, key: String, def: float) -> float:
	var v = p.get(key, def)
	if v is bool:
		return 1.0 if v else 0.0
	return float(v) if (v is float or v is int) else def


func _pcolor(p: Dictionary, key: String, def: Color) -> Color:
	var v = p.get(key)
	if v is Array and v.size() >= 3:
		var c := Color(float(v[0]), float(v[1]), float(v[2]))
		# цвета игры в 0..255, но часть параметров уже 0..1
		if c.r > 1.0 or c.g > 1.0 or c.b > 1.0:
			c = Color(c.r / 255.0, c.g / 255.0, c.b / 255.0)
		return c
	return def


func _make_particles(node: Node3D, p: Dictionary, owner: Node) -> void:
	var gp := GPUParticles3D.new()
	gp.name = node.name + "_GPU"
	node.add_child(gp)
	gp.owner = owner

	var count := int(_pf(p, "PARTICLE_COUNT", 10))
	var rate := _pf(p, "SPAWN_RATE", 0.0)
	var life := maxf(_pf(p, "LIFETIME", 2.0), 0.05)
	var life_var := _pf(p, "LIFETIME_VAR", 0.0)
	# в игре: SPAWN_RATE частиц/сек при непрерывном спавне, иначе PARTICLE_COUNT залпом
	var amount := count
	if rate > 0.0:
		amount = int(ceil(rate * (life + life_var)))
	gp.amount = clampi(amount, 1, 2000)
	gp.lifetime = life
	gp.explosiveness = 0.0 if rate > 0.0 else 0.9
	gp.randomness = 0.3
	gp.visibility_aabb = AABB(Vector3(-20, -20, -20), Vector3(40, 40, 40))
	gp.draw_order = GPUParticles3D.DRAW_ORDER_VIEW_DEPTH

	var pm := ParticleProcessMaterial.new()
	var ex := Vector3(_pf(p, "EMISSION_AREA_X", 0.0), _pf(p, "EMISSION_AREA_Y", 0.0), _pf(p, "EMISSION_AREA_Z", 0.0))
	if ex.length() > 0.001:
		pm.emission_shape = ParticleProcessMaterial.EMISSION_SHAPE_BOX
		pm.emission_box_extents = ex.abs() + Vector3.ONE * 0.01
	else:
		pm.emission_shape = ParticleProcessMaterial.EMISSION_SHAPE_POINT
	pm.lifetime_randomness = clampf(life_var / life, 0.0, 1.0)
	var s0 := _pf(p, "SPEED_START_MIN", 0.0); var s1 := _pf(p, "SPEED_START_MAX", s0)
	pm.direction = Vector3(0, 1, 0)
	pm.spread = 180.0 if _pf(p, "EMISSION_DIRECTION_SURFACE", 0.0) == 0.0 else 30.0
	pm.initial_velocity_min = minf(s0, s1)
	pm.initial_velocity_max = maxf(s0, s1)
	var g := _pf(p, "GRAVITY", 0.0) * _pf(p, "GRAVITY_STRENGTH", 1.0)
	pm.gravity = Vector3(0, -g, 0)
	var turb := maxf(_pf(p, "TURBULENCE_AMOUNT_MIN", 0.0), _pf(p, "TURBULENCE_AMOUNT_MAX", 0.0))
	if turb > 0.0:
		pm.turbulence_enabled = true
		pm.turbulence_noise_strength = clampf(turb * 0.5, 0.05, 6.0)
		pm.turbulence_noise_scale = maxf(_pf(p, "TURBULENCE_FREQUENCY_MAX", 1.0), 0.2)
		pm.turbulence_influence_min = 0.1
		pm.turbulence_influence_max = 0.4
	var sz0 := maxf(_pf(p, "SIZE_START_MIN", 0.1), 0.001); var sz1 := maxf(_pf(p, "SIZE_START_MAX", sz0), 0.001)
	var se0 := maxf(_pf(p, "SIZE_END_MIN", sz0), 0.001); var se1 := maxf(_pf(p, "SIZE_END_MAX", sz1), 0.001)
	pm.scale_min = minf(sz0, sz1)
	pm.scale_max = maxf(sz0, sz1)
	var curve := Curve.new()
	curve.add_point(Vector2(0.0, 1.0))
	curve.add_point(Vector2(1.0, clampf(((se0 + se1) * 0.5) / maxf((sz0 + sz1) * 0.5, 0.001), 0.05, 8.0)))
	var ct := CurveTexture.new(); ct.curve = curve
	pm.scale_curve = ct
	# цвет: тинт × градиент альфы (ALPHA_IN / ALPHA_OUT — доли жизни)
	var tint := _pcolor(p, "COLOUR_TINT_START", _pcolor(p, "COLOUR_TINT", Color.WHITE))
	var tint_end := _pcolor(p, "COLOUR_TINT_END", tint)
	var scale_mul := clampf(_pf(p, "COLOUR_SCALE_MAX", 1.0), 0.05, 4.0)
	var a_in := clampf(_pf(p, "ALPHA_IN", 0.0), 0.0, 0.9)
	var a_out := clampf(_pf(p, "ALPHA_OUT", 0.0), 0.0, 0.9)
	var grad := Gradient.new()
	grad.offsets = PackedFloat32Array([0.0, a_in, 1.0 - a_out, 1.0])
	grad.colors = PackedColorArray([Color(tint * scale_mul, 0.0 if a_in > 0.0 else 1.0), Color(tint * scale_mul, 1.0), Color(tint_end * scale_mul, 1.0), Color(tint_end * scale_mul, 0.0 if a_out > 0.0 else 1.0)])
	var gt := GradientTexture1D.new(); gt.gradient = grad
	pm.color_ramp = gt
	pm.angle_min = -180.0 if _pf(p, "ROTATION_RANDOM_START", 0.0) > 0.0 else 0.0
	pm.angle_max = 180.0 if _pf(p, "ROTATION_RANDOM_START", 0.0) > 0.0 else 0.0
	gp.process_material = pm

	# спрайт: unlit-квад с текстурой, экспортированный под узлом PFXTEX
	var tex: Texture2D = null
	for c in node.get_children():
		if c.name.begins_with("PFXTEX") and c is MeshInstance3D:
			var mi := c as MeshInstance3D
			var m: Material = mi.get_active_material(0) if mi.mesh and mi.mesh.get_surface_count() > 0 else null
			if m is BaseMaterial3D:
				tex = (m as BaseMaterial3D).albedo_texture
			node.remove_child(c)
			c.free()
			break
	var mat := StandardMaterial3D.new()
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mat.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	mat.blend_mode = BaseMaterial3D.BLEND_MODE_ADD if _pf(p, "BLENDING_ADDITIVE", 0.0) > 0.0 else BaseMaterial3D.BLEND_MODE_MIX
	mat.billboard_mode = BaseMaterial3D.BILLBOARD_PARTICLES
	mat.vertex_color_use_as_albedo = true
	mat.cull_mode = BaseMaterial3D.CULL_DISABLED
	mat.no_depth_test = false
	mat.albedo_texture = tex
	var frames := int(_pf(p, "TEXTURE_ANIMATION_FRAMES", 1))
	if frames > 1 and _pf(p, "TEXTURE_ANIMATION", 0.0) > 0.0:
		var side := int(ceil(sqrt(float(frames))))
		mat.particles_anim_h_frames = side
		mat.particles_anim_v_frames = side
		mat.particles_anim_loop = _pf(p, "TEXTURE_ANIMATION_LOOP_COUNT", 1.0) > 1.0
		pm.anim_speed_min = 1.0
		pm.anim_speed_max = 1.0
	if _pf(p, "SOFTNESS", 0.0) > 0.0:
		mat.proximity_fade_enabled = true
		mat.proximity_fade_distance = maxf(_pf(p, "SOFTNESS_EDGE", 0.3), 0.05)
	var quad := QuadMesh.new()
	quad.size = Vector2.ONE
	quad.material = mat
	gp.draw_pass_1 = quad


# ---------------------------------------------------------------- туман

func _make_fog(node: Node3D, f: Dictionary, owner: Node) -> void:
	var fv := FogVolume.new()
	fv.name = node.name + "_Volume"
	node.add_child(fv)
	fv.owner = owner
	var kind := str(f.get("kind", "sphere"))
	var col := _pcolor(f, "COLOUR_TINT", Color(0.8, 0.85, 0.9))
	var density := 1.0
	if kind == "box":
		var hd = f.get("half_dimensions", [1, 1, 1])
		var size := Vector3(float(hd[0]), maxf(float(hd[1]), 0.25), float(hd[2])) * 2.0
		fv.shape = RenderingServer.FOG_VOLUME_SHAPE_BOX
		fv.size = size
		density = clampf(_pf(f, "HEIGHT_MAX_DENSITY", 0.4) * 2.0, 0.05, 8.0)
	else:
		var r := maxf(_pf(f, "radius", 1.5), 0.2)
		fv.shape = RenderingServer.FOG_VOLUME_SHAPE_ELLIPSOID
		fv.size = Vector3.ONE * r * 2.0
		# DENSITY у игры — 0..30, INTENSITY/OPACITY — 0..1: плотность как произведение
		density = clampf(_pf(f, "DENSITY", 5.0) * 0.08 * maxf(_pf(f, "INTENSITY", 0.3), 0.05) * 4.0, 0.05, 6.0)
	var sh := load(FOG_SHADER) if ResourceLoader.exists(FOG_SHADER) else null
	if sh is Shader:
		var sm := ShaderMaterial.new()
		sm.shader = sh
		sm.set_shader_parameter("albedo", col)
		sm.set_shader_parameter("density", density)
		sm.set_shader_parameter("noise", _noise_tex())
		sm.set_shader_parameter("height_fade", 1.0 if kind == "box" else 0.3)
		fv.material = sm
	else:
		var fm := FogMaterial.new()
		fm.albedo = col
		fm.density = density
		fm.edge_fade = 0.2
		fv.material = fm


func _noise_tex() -> NoiseTexture3D:
	if _noise == null:
		_noise = NoiseTexture3D.new()
		var n := FastNoiseLite.new()
		n.noise_type = FastNoiseLite.TYPE_SIMPLEX_SMOOTH
		n.frequency = 0.08
		n.fractal_octaves = 3
		_noise.noise = n
		_noise.width = 48; _noise.height = 48; _noise.depth = 48
		_noise.seamless = true
	return _noise


func _add_environment(scene: Node, settings: Array) -> void:
	# берём самый «густой» FogSetting как глобальный
	var best: Dictionary = settings[0]
	for st in settings:
		if float(st.get("exponential_density", 0.0)) > float(best.get("exponential_density", 0.0)):
			best = st
	var we := WorldEnvironment.new()
	we.name = "AlienForgeEnvironment"
	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color(0.01, 0.01, 0.015)
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color(0.25, 0.27, 0.3)
	env.ambient_light_energy = 0.35
	env.volumetric_fog_enabled = true
	env.volumetric_fog_density = clampf(float(best.get("exponential_density", 0.05)) * 0.25, 0.005, 0.1)
	var fc = best.get("far_colour", [0.05, 0.05, 0.08])
	env.volumetric_fog_albedo = Color(float(fc[0]), float(fc[1]), float(fc[2])).lightened(0.6)
	env.volumetric_fog_emission_energy = 0.0
	env.volumetric_fog_gi_inject = 0.5
	env.volumetric_fog_length = 96.0
	env.volumetric_fog_ambient_inject = 0.2
	env.glow_enabled = true
	env.glow_intensity = 0.4
	env.glow_bloom = 0.05
	we.environment = env
	scene.add_child(we)
	we.owner = scene


# ---------------------------------------------------------------- слияние зон

## Всё, что статично и лежит под зоной (или под корнем без зоны), сливается:
## MeshInstance3D с одинаковым материалом → один ArrayMesh (SurfaceTool.append_from
## с трансформом относительно зоны), все тримеш-коллизии зоны → один
## ConcavePolygonShape3D под одним StaticBody3D. RigidBody, окклюдеры, свет,
## частицы и туман не трогаются. Пустые Node3D после этого вычищаются.
func _merge_zones(scene: Node) -> void:
	var zones: Array = scene.find_children("ZONE_*", "Node3D", true, false)
	# геометрия вне зон — под синтетическую зону при корне
	var loose := Node3D.new()
	loose.name = "ZONE__ROOT"
	scene.add_child(loose)
	loose.owner = scene
	var moved := 0
	for c in scene.get_children():
		if c == loose or c.name.begins_with("ZONE_") or c is WorldEnvironment:
			continue
		if c.find_children("ZONE_*", "Node3D", true, false).size() > 0:
			continue  # внутри есть зоны — не трогаем, их части сольются сами
		# сцена при пост-импорте не в дереве: global_transform не работает, поэтому
		# без keep_global (loose стоит в начале координат — локальный трансформ тот же)
		c.reparent(loose, false)
		_fix_owner(c, scene)
		moved += 1
	zones.append(loose)

	var total_meshes := 0
	var total_bodies := 0
	for z in zones:
		var r: Array = _merge_zone(z as Node3D, scene)
		total_meshes += r[0]
		total_bodies += r[1]
	# Меши, слитые ещё экспортёром (GEO_*): развёртка UV2 под лайтмап + static GI
	var pre := 0
	var to_unwrap: Array = []
	for mi in scene.find_children("GEO_*", "MeshInstance3D", true, false):
		var m := mi as MeshInstance3D
		if m.mesh is ArrayMesh:
			m.gi_mode = GeometryInstance3D.GI_MODE_STATIC
			to_unwrap.append(m.mesh)
			pre += 1
	if LIGHTMAP_TEXEL > 0.0 and not to_unwrap.is_empty():
		# (параллельно через WorkerThreadPool xatlas виснет — только последовательно)
		var t0 := Time.get_ticks_msec()
		for am in to_unwrap:
			(am as ArrayMesh).lightmap_unwrap(Transform3D.IDENTITY, LIGHTMAP_TEXEL)
		print("[AlienForge] UV2 для %d мешей за %.1f с" % [to_unwrap.size(), (Time.get_ticks_msec() - t0) / 1000.0])
	_prune_empty(scene, scene)
	print("[AlienForge] слияние: зон %d, слитых мешей %d (+%d из экспортёра), коллизий %d" % [zones.size(), total_meshes, pre, total_bodies])


func _fix_owner(n: Node, owner: Node) -> void:
	n.owner = owner
	for c in n.get_children():
		_fix_owner(c, owner)


## Статичный меш ЭТОЙ зоны: не под RigidBody/окклюдером/частицами и его ближайшая
## зона — именно zone (зоны бывают вложенными, каждый меш сливается один раз)
func _is_static_mesh(mi: MeshInstance3D, zone: Node = null) -> bool:
	# GEO_* — уже слито экспортёром, второй раз не сливаем (только UV2 ниже);
	# «+colonly» — чистая коллизия, в визуал не идёт
	if mi.mesh == null or mi.name.begins_with("PFXTEX") or mi.name.begins_with("GEO_") or mi.name.begins_with("DECAL_") or mi.name.contains("+colonly"):
		return false
	var p: Node = mi.get_parent()
	while p != null:
		if p is RigidBody3D or p is OccluderInstance3D or p is GPUParticles3D:
			return false
		if p.name.begins_with("ZONE_"):
			return zone == null or p == zone
		p = p.get_parent()
	return zone == null


func _nearest_zone(n: Node) -> Node:
	var p: Node = n.get_parent()
	while p != null and not p.name.begins_with("ZONE_"):
		p = p.get_parent()
	return p


## Трансформ узла относительно предка — без дерева (global_transform недоступен)
func _rel_transform(node: Node3D, ancestor: Node) -> Transform3D:
	var xf := Transform3D.IDENTITY
	var n: Node = node
	while n != null and n != ancestor:
		if n is Node3D:
			xf = (n as Node3D).transform * xf
		n = n.get_parent()
	return xf


func _merge_zone(zone: Node3D, owner: Node) -> Array:
	# --- коллизия: меши с маркером «+col» (экспортёр ставит его вместо родного
	# «-col», чтобы Godot не варил тысячи отдельных тримешей) → одна форма на зону
	var faces := PackedVector3Array()
	var colonly: Array = []
	var dbg_found := 0
	var dbg_used := 0
	# имя может получить цифру от Godot при дубликатах («…+col2») — ищем по вхождению
	for mi in zone.find_children("*+col*", "MeshInstance3D", true, false):
		dbg_found += 1
		var m := mi as MeshInstance3D
		if m.mesh == null or not (m.name.contains("+colonly") and _nearest_zone(m) == zone or _is_static_mesh(m, zone)):
			continue
		dbg_used += 1
		var xf: Transform3D = _rel_transform(m, zone)
		faces.append_array(xf * m.mesh.get_faces())
		if m.name.contains("+colonly"):
			colonly.append(m)
	var body_count := 0
	print("[AlienForge]   %s: +col найдено %d, использовано %d, граней %d" % [zone.name, dbg_found, dbg_used, faces.size() / 3])
	if faces.size() >= 3:
		var body := StaticBody3D.new()
		body.name = "Collision"
		var cs := CollisionShape3D.new()
		var shape := ConcavePolygonShape3D.new()
		shape.set_faces(faces)
		shape.backface_collision = true
		cs.shape = shape
		body.add_child(cs)
		zone.add_child(body)
		body.owner = owner
		cs.owner = owner
		body_count = 1
	for m in colonly:
		m.get_parent().remove_child(m)
		m.free()
	# --- визуал: по материалу
	var buckets: Dictionary = {}   # material -> Array[SurfaceTool]
	var counts: Dictionary = {}    # material -> verts in current tool
	var sources: Array = []
	for mi in zone.find_children("*", "MeshInstance3D", true, false):
		if not _is_static_mesh(mi as MeshInstance3D, zone):
			continue
		var m := mi as MeshInstance3D
		var xf: Transform3D = _rel_transform(m, zone)
		for si in m.mesh.get_surface_count():
			var mat: Material = m.get_active_material(si)
			var key = mat if mat != null else "null"
			var arrays: Array = m.mesh.surface_get_arrays(si)
			var nverts: int = arrays[Mesh.ARRAY_VERTEX].size() if arrays.size() > Mesh.ARRAY_VERTEX and arrays[Mesh.ARRAY_VERTEX] != null else 0
			if not buckets.has(key) or counts[key] + nverts > MERGE_MAX_VERTS:
				var st := SurfaceTool.new()
				st.begin(Mesh.PRIMITIVE_TRIANGLES)
				if not buckets.has(key):
					buckets[key] = []
				buckets[key].append(st)
				counts[key] = 0
			var st2: SurfaceTool = buckets[key].back()
			st2.append_from(m.mesh, si, xf)
			counts[key] += nverts
		sources.append(m)
	var made := 0
	for key in buckets:
		for st in buckets[key]:
			var mesh: ArrayMesh = st.commit()
			if mesh == null or mesh.get_surface_count() == 0:
				continue
			if key is Material:
				mesh.surface_set_material(0, key)
			if LIGHTMAP_TEXEL > 0.0:
				mesh.lightmap_unwrap(Transform3D.IDENTITY, LIGHTMAP_TEXEL)
			var mi := MeshInstance3D.new()
			mi.name = "geo_%03d" % made
			mi.mesh = mesh
			mi.gi_mode = GeometryInstance3D.GI_MODE_STATIC
			zone.add_child(mi)
			mi.owner = owner
			made += 1
	for m in sources:
		if not is_instance_valid(m):
			continue
		# у меша могут быть дети (коллизионный «+col», свет, частицы) — поднимаем
		# их к родителю с сохранением трансформа, потом освобождаем сам меш
		var parent: Node = m.get_parent()
		for c in m.get_children():
			var t: Transform3D = (m.transform * (c as Node3D).transform) if c is Node3D else Transform3D.IDENTITY
			m.remove_child(c)
			parent.add_child(c)
			if c is Node3D:
				(c as Node3D).transform = t
			_fix_owner(c, owner)
		parent.remove_child(m)
		m.free()
	return [made, body_count]


## Удалить пустые Node3D (после слияния от иерархии остаются тысячи пустых узлов)
func _prune_empty(n: Node, root: Node) -> bool:
	for c in n.get_children():
		_prune_empty(c, root)
	if n == root:
		return false
	if n.get_class() == "Node3D" and n.get_child_count() == 0 and not n.name.begins_with("ZONE_") and n.get_script() == null:
		n.get_parent().remove_child(n)
		n.free()
		return true
	return false
