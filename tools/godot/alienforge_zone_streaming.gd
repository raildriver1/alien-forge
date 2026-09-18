@tool
extends Node3D
## AlienForge: стриминг зон уровня, как в OpenCAGE / в игре — рендерятся только
## зоны рядом с камерой. Вешается пост-импортом на корень уровня.
##
## Зона = узел ZONE_<имя> (инстанс композита с сущностью Zone). Для каждой
## считается AABB всей её геометрии; зона включена, если камера внутри AABB,
## расширенного на margin. В игре камера — локальный игрок (человек или
## Чужой), в редакторе — камера вьюпорта (editor_streaming), так что и там
## виден только тот кусок, где стоишь. Коллизия остаётся всегда.

@export var enabled: bool = true
## Насколько расширять AABB зоны, м (запас на дверные проёмы и коридоры)
@export var margin: float = 6.0
## Как часто пересчитывать, сек
@export var interval: float = 0.25
## Зоны без геометрии (свет/частицы) — всегда видны
@export var keep_empty_visible: bool = true
## В редакторе тоже прятать далёкие зоны (по камере вьюпорта). Выключи, чтобы
## увидеть весь уровень (или нажми «Показать все зоны» ниже).
@export var editor_streaming: bool = true
## Кнопка: показать всё (сбрасывается сама)
@export var show_all_zones: bool = false:
	set(v):
		if v:
			show_all()
		show_all_zones = false
@export var debug_log: bool = false

@export_category("Детали по дистанции")
## Свет дальше этого расстояния (плюс его собственный range) выключается.
## Тысячи ламп по уровню нельзя считать каждый кадр — и не надо: их не видно.
@export var light_distance: float = 30.0
## Частицы дальше — не эмитят и не рисуются
@export var particle_distance: float = 40.0
## Объёмный туман дальше — выключен
@export var fog_distance: float = 45.0
## Жёсткие потолки: не больше N ближайших (у ламп игры range по 30–50 м, и
## «в радиусе» оказывалось по 1 300 штук — Forward+ на этом умирает)
@export var max_lights: int = 48
@export var max_particles: int = 64
@export var max_fog: int = 24

var _zones: Array[Node3D] = []
var _bounds: Array[AABB] = []
var _timer: float = 0.0
var _lights: Array[Light3D] = []
var _light_pos: PackedVector3Array = PackedVector3Array()
var _light_range: PackedFloat32Array = PackedFloat32Array()
var _particles: Array[GPUParticles3D] = []
var _particle_pos: PackedVector3Array = PackedVector3Array()
var _fogs: Array[FogVolume] = []
var _fog_pos: PackedVector3Array = PackedVector3Array()


func _ready() -> void:
	_collect()


func _collect() -> void:
	_zones.clear()
	_bounds.clear()
	for c in find_children("ZONE_*", "Node3D", true, false):
		_zones.append(c)
		_bounds.append(_subtree_aabb(c))
	_lights.clear(); _light_pos.clear(); _light_range.clear()
	for l in find_children("*", "Light3D", true, false):
		_lights.append(l)
		_light_pos.append((l as Node3D).global_position)
		var r := 10.0
		if l is OmniLight3D: r = (l as OmniLight3D).omni_range
		elif l is SpotLight3D: r = (l as SpotLight3D).spot_range
		_light_range.append(r)
	_particles.clear(); _particle_pos.clear()
	for gp in find_children("*", "GPUParticles3D", true, false):
		_particles.append(gp)
		_particle_pos.append((gp as Node3D).global_position)
	_fogs.clear(); _fog_pos.clear()
	for fv in find_children("*", "FogVolume", true, false):
		_fogs.append(fv)
		_fog_pos.append((fv as Node3D).global_position)
	if debug_log:
		print("[ZoneStreaming] зон: %d, света %d, частиц %d, тумана %d" % [_zones.size(), _lights.size(), _particles.size(), _fogs.size()])


func _process(delta: float) -> void:
	if not enabled:
		return
	if Engine.is_editor_hint() and not editor_streaming:
		return
	_timer -= delta
	if _timer > 0.0:
		return
	_timer = interval
	if _zones.is_empty():
		_collect()
	var cam: Camera3D = _camera()
	if cam == null:
		return
	_apply(cam.global_position)


func _camera() -> Camera3D:
	if Engine.is_editor_hint():
		var vp = EditorInterface.get_editor_viewport_3d(0)
		return vp.get_camera_3d() if vp != null else null
	return get_viewport().get_camera_3d()


func _apply(p: Vector3) -> void:
	var shown := 0
	for i in _zones.size():
		var b: AABB = _bounds[i]
		var vis: bool
		if b.size == Vector3.ZERO:
			vis = keep_empty_visible
		else:
			vis = b.grow(margin).has_point(p)
		if _zones[i].visible != vis:
			_zones[i].visible = vis
		if vis:
			shown += 1
	# свет / частицы / туман — N ближайших в пределах дистанции
	var lights_on := _apply_nearest(p, _lights, _light_pos, light_distance, max_lights, false)
	_apply_nearest(p, _particles, _particle_pos, particle_distance, max_particles, true)
	_apply_nearest(p, _fogs, _fog_pos, fog_distance, max_fog, false)
	if debug_log:
		print("[ZoneStreaming] видно зон: %d / %d, света %d / %d" % [shown, _zones.size(), lights_on, _lights.size()])


## Включить только limit ближайших (в радиусе dist) узлов из списка. Возвращает сколько включено.
func _apply_nearest(p: Vector3, nodes: Array, positions: PackedVector3Array, dist, limit_v, particles: bool) -> int:
	# после горячей перезагрузки скрипта новые @export бывают null до перезапуска сцены
	var limit: int = int(limit_v) if limit_v != null else 48
	if dist == null:
		dist = 30.0
	var cand: Array = []
	var d2max: float = float(dist) * float(dist)
	for i in nodes.size():
		var d2: float = positions[i].distance_squared_to(p)
		if d2 < d2max:
			cand.append([d2, i])
	cand.sort_custom(func(a, b): return a[0] < b[0])
	var keep := {}
	for k in mini(limit, cand.size()):
		keep[cand[k][1]] = true
	var on_count := 0
	for i in nodes.size():
		var on: bool = keep.has(i)
		var n = nodes[i]
		if n.visible != on:
			n.visible = on
			if particles:
				(n as GPUParticles3D).emitting = on
		if on:
			on_count += 1
	return on_count


## Показать всё (для редактора/отладки)
func show_all() -> void:
	if _zones.is_empty():
		_collect()
	for z in _zones:
		z.visible = true


func _subtree_aabb(root: Node3D) -> AABB:
	var acc := AABB()
	var first := true
	for vi in root.find_children("*", "VisualInstance3D", true, false):
		if vi is GPUParticles3D or vi is FogVolume or vi is Light3D:
			continue
		var v := vi as VisualInstance3D
		var local: AABB = v.get_aabb()
		if local.size == Vector3.ZERO:
			continue
		var g: AABB = v.global_transform * local
		if first:
			acc = g
			first = false
		else:
			acc = acc.merge(g)
	return acc
