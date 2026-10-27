'use strict';
/* Стеклянный куб поверх обложки: WebGL2-порт WGSL-шейдера (рейтрейсинг SDF,
   преломление с дисперсией по каналам R/G/B, Френель, блик-«окно», свечение рёбер).
   Экономия ресурсов: рисуется только в видимом окне, не чаще 30 к/с,
   разрешение = размер обложки (~170 px), powerPreference: low-power. */
window.GlassCube = (() => {
  const VERT = `#version 300 es
void main(){
  vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
  gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}`;

  const FRAG = `#version 300 es
precision highp float;
uniform vec2 uRes;
uniform float uTime;
uniform vec2 uMouse;
uniform float uCube, uIor, uDisp;
uniform sampler2D uPoster;
out vec4 outColor;

const float ZPLANE = -1.0;
const float FOCAL = 2.6;
mat3 R;

vec3 poster(vec2 uv){ return textureLod(uPoster, clamp(uv, 0.0, 1.0), 1.0).rgb; }

float cubeSdf(vec3 p){
  vec3 q = abs(R * p) - vec3(uCube * 0.5);
  return length(max(q, vec3(0.0))) + min(max(q.x, max(q.y, q.z)), 0.0) - 0.05;
}
vec3 cubeNormal(vec3 p){
  vec2 e = vec2(0.0007, -0.0007);
  return normalize(e.xyy * cubeSdf(p + e.xyy) + e.yyx * cubeSdf(p + e.yyx)
                 + e.yxy * cubeSdf(p + e.yxy) + e.xxx * cubeSdf(p + e.xxx));
}
vec3 env(vec3 rd){
  vec3 g = mix(vec3(0.62, 0.63, 0.67), vec3(0.95, 0.95, 0.96), clamp(rd.y * 0.5 + 0.5, 0.0, 1.0));
  float win = smoothstep(0.93, 0.995, dot(rd, normalize(vec3(0.55, 0.65, 0.5))));
  return g + vec3(1.0) * win * 2.5;
}
vec2 planeUv(vec3 p, vec3 rd){
  float t = (ZPLANE - p.z) / min(rd.z, -1e-4);
  vec3 q = p + rd * t;
  vec2 ndc = q.xy * FOCAL / (3.0 - ZPLANE);
  return vec2(ndc.x / (uRes.x / uRes.y), ndc.y) * 0.5 + 0.5;
}

void main(){
  float ax = uTime * 0.21 + uMouse.y * 0.8;
  float ay = uTime * 0.17 + uMouse.x * 1.2;
  float cx = cos(ax), sx = sin(ax), cy = cos(ay), sy = sin(ay);
  mat3 rx = mat3(1.0, 0.0, 0.0, 0.0, cx, sx, 0.0, -sx, cx);
  mat3 ry = mat3(cy, 0.0, -sy, 0.0, 1.0, 0.0, sy, 0.0, cy);
  R = ry * rx;

  vec2 frag = gl_FragCoord.xy;
  vec2 uv = frag / uRes;
  vec2 ndc = (frag - 0.5 * uRes) / uRes.y * 2.0;
  vec3 ro = vec3(0.0, 0.0, 3.0);
  vec3 rd = normalize(vec3(ndc.x, ndc.y, -FOCAL));

  float t = 0.0;
  bool hit = false;
  for (int i = 0; i < 80; i++){
    float d = cubeSdf(ro + rd * t);
    if (d < 0.0008){ hit = true; break; }
    t += d;
    if (t > 6.0) break;
  }

  vec2 uvs[3] = vec2[3](uv, uv, uv);
  float fres = 0.0, edge = 0.0;
  vec3 room = vec3(0.0);
  if (hit){
    vec3 p = ro + rd * t;
    vec3 n = cubeNormal(p);
    fres = 0.04 + 0.96 * pow(1.0 - max(dot(-rd, n), 0.0), 5.0);
    edge = pow(1.0 - abs(dot(n, -rd)), 8.0);
    room = env(reflect(rd, n));
    for (int c = 0; c < 3; c++){
      float ior = uIor + (float(c) - 1.0) * uDisp;
      vec3 r1 = refract(rd, n, 1.0 / ior);
      vec3 q = p - n * 0.002;
      for (int i = 0; i < 40; i++){
        float d = -cubeSdf(q);
        if (d < 0.0008) break;
        q += r1 * max(d, 0.004);
      }
      vec3 n2 = -cubeNormal(q);
      vec3 r2 = refract(r1, n2, ior);
      if (dot(r2, r2) < 0.5) r2 = reflect(r1, n2);
      uvs[c] = planeUv(q, r2);
    }
  }
  vec3 seen = vec3(poster(uvs[0]).r, poster(uvs[1]).g, poster(uvs[2]).b);
  vec3 col = mix(seen, room, fres);
  col += vec3(0.4, 0.5, 0.9) * edge * 0.15;
  outColor = vec4(col, 1.0);
}`;

  const FRAME_MS = 1000 / 30;
  let canvas = null, gl = null, program = null, texture = null, loc = {};
  let loader = null, enabled = false, ready = false, failed = false, raf = 0, last = 0;
  let wanted = null, applied = undefined, fallback = null;
  const start = performance.now();
  const mouse = [0, 0], target = [0, 0];

  function makeFallback() {
    // Своя «афиша»: цвета приложения + контрастные формы, чтобы стеклу было что преломлять.
    const c = document.createElement('canvas'); c.width = c.height = 256;
    const x = c.getContext('2d');
    const g = x.createLinearGradient(0, 0, 256, 256);
    g.addColorStop(0, '#2a3a7a'); g.addColorStop(1, '#0b1024');
    x.fillStyle = g; x.fillRect(0, 0, 256, 256);
    x.fillStyle = '#b48cff'; x.beginPath(); x.arc(78, 92, 54, 0, 7); x.fill();
    x.fillStyle = '#ff7ab8'; x.fillRect(150, 40, 26, 176);
    x.fillStyle = '#5ec2ff'; x.fillRect(196, 168, 28, 28);
    x.strokeStyle = '#ffd23f'; x.lineWidth = 5;
    x.beginPath(); x.moveTo(10, 220); x.lineTo(246, 120); x.stroke();
    return c;
  }

  function compile(type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source); gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader));
    return shader;
  }

  function setup() {
    gl = canvas.getContext('webgl2', {alpha: false, antialias: false, powerPreference: 'low-power', preserveDrawingBuffer: false});
    if (!gl) throw new Error('WebGL2 недоступен');
    program = gl.createProgram();
    gl.attachShader(program, compile(gl.VERTEX_SHADER, VERT));
    gl.attachShader(program, compile(gl.FRAGMENT_SHADER, FRAG));
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program));
    ['uRes', 'uTime', 'uMouse', 'uCube', 'uIor', 'uDisp', 'uPoster'].forEach(n => loc[n] = gl.getUniformLocation(program, n));
    texture = gl.createTexture();
    gl.useProgram(program);
    gl.uniform1i(loc.uPoster, 0);
    gl.uniform1f(loc.uCube, 1.15); gl.uniform1f(loc.uIor, 1.5); gl.uniform1f(loc.uDisp, 0.05);
    fallback = makeFallback();
    upload(fallback);
    canvas.addEventListener('webglcontextlost', e => { e.preventDefault(); ready = false; failed = true; setVisible(false); });
    ready = true;
  }

  function upload(source) {
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
    gl.generateMipmap(gl.TEXTURE_2D);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  }

  async function applyArtwork() {
    if (!ready || wanted === applied) return;
    const url = wanted; applied = url;
    if (!url || !loader) { upload(fallback); return; }
    try {
      // Картинку отдаёт Python как data: URL — иначе WebGL откажется брать чужой источник (CORS).
      const data = await loader(url);
      if (!data || wanted !== url) { if (wanted === url) upload(fallback); return; }
      const image = new Image();
      image.onload = () => { if (wanted === url && ready) upload(image); };
      image.onerror = () => { if (wanted === url && ready) upload(fallback); };
      image.src = data;
    } catch (_) { if (wanted === url && ready) upload(fallback); }
  }

  function setVisible(on) {
    if (!canvas) return;
    canvas.parentElement.classList.toggle('gl', on);
  }

  function draw(now) {
    const dpr = Math.min(1.5, window.devicePixelRatio || 1);
    const w = Math.max(32, Math.round(canvas.clientWidth * dpr)), h = Math.max(32, Math.round(canvas.clientHeight * dpr));
    if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; gl.viewport(0, 0, w, h); }
    mouse[0] += (target[0] - mouse[0]) * 0.08; mouse[1] += (target[1] - mouse[1]) * 0.08;
    gl.uniform2f(loc.uRes, w, h);
    gl.uniform1f(loc.uTime, (now - start) / 1000);
    gl.uniform2f(loc.uMouse, mouse[0], mouse[1]);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  }

  function frame(now) {
    raf = 0;
    if (!enabled || !ready || document.hidden) return;   // скрытое окно не рисуем вообще
    raf = requestAnimationFrame(frame);
    if (now - last < FRAME_MS - 1) return;
    last = now;
    draw(now);
  }

  function kick() { if (enabled && ready && !document.hidden && !raf) raf = requestAnimationFrame(frame); }

  document.addEventListener('visibilitychange', kick);
  document.addEventListener('pointermove', e => {
    target[0] = (e.clientX / Math.max(1, window.innerWidth) - 0.5) * 2;
    target[1] = (e.clientY / Math.max(1, window.innerHeight) - 0.5) * 2;
  }, {passive: true});

  return {
    init(element, loadArtwork) { canvas = element; loader = loadArtwork; },
    enable(on) {
      enabled = !!on && !failed;
      if (enabled && !ready && canvas) {
        try { setup(); } catch (error) { failed = true; enabled = false; console.warn('GlassCube:', error.message); }
      }
      setVisible(enabled && ready);
      if (enabled) { applyArtwork(); kick(); }
      else if (raf) { cancelAnimationFrame(raf); raf = 0; }
    },
    setArtwork(url) { wanted = url || null; applyArtwork(); },
    get active() { return enabled && ready; }
  };
})();
