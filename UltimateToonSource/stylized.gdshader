shader_type spatial;

group_uniforms Shader.Albedo;
uniform vec4 albedo_color : source_color  = vec4(1.0);
uniform sampler2D albedo_texture;
uniform float albedo_affect : hint_range(0.0, 1.0, 0.05) = 0.0;

group_uniforms Shader.Light;
uniform bool use_stepped = true;
uniform float steps = 3.0;
uniform float step_smoothness : hint_range(0.0, 1.0, 0.05) = 0.3;
uniform vec4 shadow_tint : source_color;
uniform float shadow_tint_amount : hint_range(0.0, 1.0, 0.1) = 0.4;

group_uniforms Shader.Specular;
uniform float specular : hint_range(0.0, 1.0, 0.05) = 0.0;

group_uniforms Shader.Normal;
uniform float normal_strength : hint_range(0.0, 1.0, 0.05) = 0.0;
uniform sampler2D normal_texture;

group_uniforms Shader.Rim;
uniform bool use_rim = true;
uniform vec4 rim_color : source_color = vec4(1.0);
uniform float rim_amount = 2.0;
uniform float rim_smoothness = 0.2;
uniform float rim_mask_shadow : hint_range(0.0, 1.0, 0.05) = 1.0;
uniform float rim_blend : hint_range(0.0, 1.0, 0.05) = 1.0;

group_uniforms Shader.Pattern;
uniform bool use_pattern = true;
uniform sampler2D pattern_texture;
uniform int pattern_uv_mode : hint_enum("UV", "Screen", "Local Screen") = 2;
uniform float pattern_tiling = 32.0;
uniform float pattern_amount : hint_range(0.0, 1.0, 0.05) = 0.5;
uniform float pattern_smoothness : hint_range(0.0, 1.0, 0.05) = 0.5;
uniform float pattern_blend : hint_range(0.0, 1.0, 0.05) = 0.2;

// MATERIALES DEL TERRENO (el agua tiene su propio shader: water.gdshader)
uniform vec4 grass_color : source_color = vec4(0.51, 0.72, 0.36, 1.0); // #82b95c Hierba de pradera
uniform vec4 cliff_color : source_color = vec4(0.83, 0.60, 0.45, 1.0); // #d49b74 Colina (acantilados y orillas)
uniform vec4 sand_color  : source_color = vec4(0.89, 0.78, 0.59, 1.0); // #e3c896 Arena de playa
uniform float sand_height = -1.0;      // solo sin materiales por vértice: por debajo → arena
uniform float cliff_threshold : hint_range(0.0, 1.0, 0.01) = 0.85; // normal.y por debajo → colina (0.85 ≈ 32°)
uniform float cliff_blend : hint_range(0.0, 0.3, 0.005) = 0.02;    // anchura del borde hierba/colina (0 = corte duro)
uniform bool use_flat_slope = false; // true: pendiente por triángulo (bordes nítidos); false: normal suavizada

// Materiales por vértice (los rellena el código desde las casillas)
// COLOR.r = arena de playa, COLOR.g = pared de acantilado, COLOR.b = pared/fondo del cauce
uniform bool use_biome_colors = false;

// TEXTURAS PROCEDURALES (dibujadas por código en coordenadas del mundo: no se estiran)
group_uniforms Texturas;
uniform bool use_procedural_textures = true;
uniform vec4 grass_patch_color : source_color = vec4(0.45, 0.67, 0.32, 1.0);  // #73ab52 manchas de hierba
uniform vec4 grass_detail_color : source_color = vec4(0.66, 0.83, 0.46, 1.0); // #a8d475 briznas claras
uniform vec4 cliff_band_color : source_color = vec4(0.75, 0.52, 0.39, 1.0);   // #bf8563 estratos de la colina
uniform vec4 sand_dot_color : source_color = vec4(0.80, 0.68, 0.50, 1.0);     // #ccad80 puntitos de arena
uniform float grass_patch_scale = 0.35;   // tamaño de las manchas (más bajo = manchas más grandes)
uniform float grass_detail_density : hint_range(0.0, 1.0, 0.01) = 0.3;
uniform float cliff_bands_per_meter = 3.0;
group_uniforms;

varying vec4 node_position;
varying vec3 world_pos;
varying vec3 world_normal;

float fresnel(float amount, vec3 normal, vec3 view)
{
	return pow((1.0 - clamp(dot(normalize(normal), normalize(view)), 0.0, 1.0 )), amount);
}

vec2 pattern_uv(vec2 uv, vec2 screen_uv){
	vec2 p_uv = uv;
	if(pattern_uv_mode == 1) { p_uv = screen_uv; }
	if(pattern_uv_mode == 2) { p_uv = (screen_uv * 2.0 - 1.0) * node_position.w - node_position.xy; }
	return p_uv;
}

float shape_value(float value, vec2 uv, vec2 screen_size){
	float aspect = screen_size.y / screen_size.x;
	vec2 pattern_uv = uv * pattern_tiling * vec2(1.0, aspect);
	float shadow_pattern = texture(pattern_texture, pattern_uv).r;
	shadow_pattern = clamp(smoothstep(shadow_pattern - pattern_smoothness, shadow_pattern + pattern_smoothness, value + pattern_amount), 0.0, value + pattern_amount);
	return shadow_pattern;
}

// ---------- Ruido para las texturas procedurales ----------

// Número pseudoaleatorio 0..1 a partir de una posición
float hash21(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

// Ruido suave (value noise): manchas sin bordes repetitivos
float value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash21(i), hash21(i + vec2(1.0, 0.0)), u.x),
	           mix(hash21(i + vec2(0.0, 1.0)), hash21(i + vec2(1.0, 1.0)), u.x), u.y);
}

// Puntitos repartidos al azar: uno como mucho por celda, con probabilidad 'density'
float speckles(vec2 p, float density, float radius) {
	vec2 cell = floor(p);
	vec2 f = fract(p);
	vec2 center = vec2(hash21(cell + 17.1), hash21(cell + 31.7)) * 0.6 + 0.2;
	float present = step(hash21(cell), density);
	return present * (1.0 - step(radius, length(f - center)));
}

vec3 grass_texture(vec2 xz) {
	// Manchas de dos tonos con borde duro (estilo toon) + briznas claras
	float n = value_noise(xz * grass_patch_scale) * 0.65 + value_noise(xz * grass_patch_scale * 3.1) * 0.35;
	vec3 c = mix(grass_color.rgb, grass_patch_color.rgb, step(0.56, n));
	float blades = speckles(xz * 2.5, grass_detail_density, 0.13);
	return mix(c, grass_detail_color.rgb, blades);
}

vec3 cliff_texture(vec3 p) {
	// Estratos horizontales algo ondulados, como los acantilados de los juegos cozy
	float y = p.y + 0.06 * sin(p.x * 2.1 + p.z * 0.7) + 0.06 * sin(p.z * 1.9 - p.x * 0.5);
	float band = step(0.55, fract(y * cliff_bands_per_meter));
	return mix(cliff_color.rgb, cliff_band_color.rgb, band);
}

vec3 sand_texture(vec2 xz) {
	return mix(sand_color.rgb, sand_dot_color.rgb, speckles(xz * 4.0, 0.35, 0.12));
}

void vertex() {
	node_position = (PROJECTION_MATRIX * vec4(NODE_POSITION_VIEW, 1.0));
	// Altura y normal en espacio del MUNDO (no dependen de la cámara)
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = (MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz;
}

void fragment() {
    // 1. Pendiente: 1 = plano, 0 = pared vertical
    //    La normal "plana" sale del propio triángulo (derivadas de la posición entre píxeles),
    //    así no se mezcla con la meseta vecina y el color no se emborrona.
    vec3 flat_normal = normalize(cross(dFdy(world_pos), dFdx(world_pos)));
    float slope = use_flat_slope ? abs(flat_normal.y) : normalize(world_normal).y;

    // 2. Suelo plano: hierba de pradera, o arena de playa donde el código lo marque
    vec4 base_mix;
    float hill;
    if (use_biome_colors) {
        // Con casillas, el material lo decide el código: superficies arriba = hierba (o arena),
        // paredes laterales y cauces = colina. Corte duro, sin mezclas.
        base_mix = mix(grass_color, sand_color, step(0.5, COLOR.r));
        hill = max(step(0.5, COLOR.g), step(0.5, COLOR.b));
    } else {
        // Sin casillas: arena por altura y colina por pendiente
        base_mix = mix(sand_color, grass_color,
                       smoothstep(sand_height - 0.25, sand_height + 0.25, world_pos.y));
        hill = 1.0 - smoothstep(cliff_threshold - cliff_blend, cliff_threshold + cliff_blend, slope);
    }

    // 3. Colina encima del color base
    vec4 terrain_palette = mix(base_mix, cliff_color, hill);

    // 4. Texturas procedurales (sustituyen a los colores lisos)
    if (use_biome_colors && use_procedural_textures) {
        vec3 ground = mix(grass_texture(world_pos.xz), sand_texture(world_pos.xz), step(0.5, COLOR.r));
        terrain_palette = vec4(mix(ground, cliff_texture(world_pos), hill), 1.0);
    }

    // 3. Aplicación con tus texturas originales
    vec4 albedo = texture(albedo_texture, UV) * terrain_palette;
    vec4 normal_map = texture(normal_texture, UV);

    ALBEDO = albedo.rgb;

    NORMAL_MAP = mix(vec3(0.5, 0.5, 1.0), normal_map.rgb, normal_strength);

    SPECULAR = specular;
}

void light () {
	float ndotl = clamp(dot(NORMAL,LIGHT),0.0,1.0) * ATTENUATION;

	float light = ndotl;

	if(use_stepped){
		float light_mult = light * steps;
		float step_base = floor(light_mult);
		float light_factor = light_mult - step_base;

		light_factor = smoothstep(0.5 - step_smoothness, 0.5 + step_smoothness, light_factor);
		light = (step_base + light_factor) / steps;
	}

	if(use_pattern){
		vec2 p_uv = pattern_uv(UV, SCREEN_UV);

		light = mix(light, shape_value(light, p_uv, VIEWPORT_SIZE), pattern_blend);
	}

	vec3 diffuse = vec3(pow(light, 1.0)) * (LIGHT_COLOR / PI);
	diffuse = (shadow_tint.rgb * (1.0 - (light)) * shadow_tint_amount) + diffuse * light;

	if(use_rim){
		float rim_value = fresnel(rim_amount, NORMAL, VIEW);
		rim_value = smoothstep(0.5 - (rim_smoothness / 2.0), 0.5 + (rim_smoothness / 2.0), rim_value);
		rim_value = mix(rim_value, rim_value * light, rim_mask_shadow);

		diffuse += rim_color.rgb * rim_value * rim_blend;
	}

	DIFFUSE_LIGHT += mix(diffuse, ALBEDO * diffuse, albedo_affect);
	SPECULAR_LIGHT += vec3(pow(light, 50.0)) * specular;
}