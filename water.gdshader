shader_type spatial;
// Agua estilizada. Es casi opaca (deja intuir el fondo) y lee la profundidad de la escena
// para dibujar una línea de espuma donde el agua toca la orilla.
render_mode cull_disabled, diffuse_toon, specular_toon, depth_draw_always;

uniform vec4 shallow_color : source_color = vec4(0.47, 0.78, 0.88, 1.0); // #78c7e0
uniform vec4 deep_color : source_color = vec4(0.25, 0.56, 0.76, 1.0);    // #408fc2
uniform vec4 foam_color : source_color = vec4(0.95, 0.98, 1.0, 1.0);
uniform bool is_waterfall = false;
uniform float flow_speed = 1.4;
uniform float foam_width = 0.25;          // anchura de la espuma de la orilla (m)
uniform float opacity : hint_range(0.0, 1.0, 0.01) = 0.92;

uniform sampler2D depth_texture : hint_depth_texture, filter_linear_mipmap;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	if (is_waterfall) {
		// Franjas de espuma que caen (UV.y: 0 arriba, 1 abajo)
		float s = fract(UV.y * 2.5 - TIME * flow_speed + sin(UV.x * 9.0) * 0.15);
		float stripe = smoothstep(0.55, 0.65, s);
		ALBEDO = mix(shallow_color.rgb, foam_color.rgb, 0.35 + 0.55 * stripe);
		ALPHA = 1.0;
	} else {
		// Más claro en los bordes de la vista (fresnel) y brillos que se mueven despacio
		float fres = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 3.0);
		float ripple = sin(world_pos.x * 1.7 + TIME * 1.1) * sin(world_pos.z * 1.3 - TIME * 0.8);
		float shine = smoothstep(0.9, 0.96, ripple);
		vec3 color = mix(deep_color.rgb, shallow_color.rgb, 0.55 + 0.25 * fres) + foam_color.rgb * shine * 0.12;

		// Espuma de orilla: distancia entre la superficie del agua y el terreno que hay detrás
		float depth = texture(depth_texture, SCREEN_UV).r;
		vec4 scene_view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
		float scene_dist = -scene_view.z / scene_view.w;
		float water_dist = -VERTEX.z;
		float gap = scene_dist - water_dist;
		// El ancho de la espuma "respira" un poco para que no parezca una línea fija
		float wobble = 0.75 + 0.25 * sin(TIME * 1.6 + world_pos.x * 2.3 + world_pos.z * 1.7);
		float foam = 1.0 - step(foam_width * wobble, gap);

		ALBEDO = mix(color, foam_color.rgb, foam);
		ALPHA = mix(opacity, 1.0, foam);
	}
	ROUGHNESS = 0.6;
	SPECULAR = 0.0;  // sin brillo especular: a contraluz el agua se volvía casi blanca
}
