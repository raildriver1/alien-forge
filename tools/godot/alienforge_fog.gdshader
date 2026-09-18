shader_type fog;

// Туман Alien: Isolation для FogVolume. Плотность дышит 3D-шумом, который
// медленно ползёт ВНИЗ (в игре туман стелется сверху вниз), внизу объёма
// гуще (height_fade), к краям сходит на нет по SDF.

uniform vec3 albedo : source_color = vec3(0.8, 0.85, 0.9);
uniform float density : hint_range(0.0, 8.0) = 1.0;
uniform float drift_speed : hint_range(0.0, 2.0) = 0.12;  // м/с, вниз
uniform float noise_scale : hint_range(0.01, 2.0) = 0.18;
uniform float noise_contrast : hint_range(0.0, 1.0) = 0.6;
uniform float height_fade : hint_range(0.0, 1.0) = 1.0;   // 1 — внизу густо, вверху пусто
uniform float edge_fade : hint_range(0.05, 3.0) = 0.6;     // метры плавного края
uniform sampler3D noise;

void fog() {
	// шум едет вверх по координате объёма — значит клубы визуально опускаются
	vec3 p = WORLD_POSITION * noise_scale + vec3(0.0, TIME * drift_speed * noise_scale, 0.0);
	float n = texture(noise, fract(p)).r;
	float shape = mix(1.0 - noise_contrast, 1.0 + noise_contrast, n);
	// UVW: 0..1 внутри объёма, y снизу вверх
	float h = mix(1.0, clamp(1.0 - UVW.y, 0.0, 1.0), height_fade);
	float edge = clamp(-SDF / edge_fade, 0.0, 1.0);
	DENSITY = density * shape * h * edge;
	ALBEDO = albedo;
}
