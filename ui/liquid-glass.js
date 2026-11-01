'use strict';
/* Liquid Glass: анимированная «аврора» (текучие размытые пятна + плёночное зерно) и стеклянные элементы.
   Всё рисует один WebGL2-шейдер. Эффект стекла перенесён из шейдера со стеклянным кубом:
   рефракция по нормалям скошенной кромки, дисперсия R/G/B, Френель, отражение-«окно», свечение кромки.
   Элементы — это обычные DOM-блоки (скруглённые прямоугольники); текст и иконки остаются поверх canvas.
   Режимы:
     solid=true   окно непрозрачное: фон — аврора (если есть снимок рабочего стола, аврора чуть просвечивает им);
     solid=false  окно прозрачное: фон — полупрозрачная аврора поверх живого рабочего стола.
   Кромка окна (kind.frame) — линза, которая искажает рабочий стол за краем окна (нужен снимок от Python).
   Экономия: ничего не рисуем в скрытом окне; «Среднее» = меньше разрешение и 20 к/с; анимацию можно выключить. */
window.LiquidGlass = (() => {
  const MAX = 32;
  const VERT = `#version 300 es
void main(){
  vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
  gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}`;
  const FRAG = `#version 300 es
precision highp float;
#define MAX ${MAX}
uniform vec2 uRes;          // размер canvas, px
uniform float uDpr;
uniform float uTime;
uniform vec2 uMouse;        // -1..1, двигает свет
uniform int uCount;
uniform vec4 uRect[MAX];    // x, y, w, h (css px, y вниз, от левого верхнего угла canvas)
uniform vec4 uPar[MAX];     // радиус, фаска, толщина стекла (px), тёмность интерьера (<0 = рамка окна)
uniform vec4 uTint[MAX];    // rgb подкраска интерьера, a = блур интерьера (lod)
uniform vec4 uClip[MAX];    // x0, y0, x1, y1
uniform vec3 uPal[6];       // палитра авроры: от тёмного к светлому
uniform vec4 uLine;         // rgb + сила тонких светящихся контуров поля (0 = нет; у дизайна «Бездна» — голубые линии)
uniform vec3 uGlow;         // цвет свечения кромки стекла
uniform vec3 uTintMul;      // множитель оттенка интерьера стекла (у «Бездны» холодный)
uniform vec4 uTintLum;      // если a > 0.5: оттенок интерьера берётся по яркости исходного и красится этим цветом (rgb)
uniform float uRange;       // насколько «высоко» по палитре поднимается поле (<1 = светлые зоны реже и темнее)
uniform sampler2D uBd;      // снимок рабочего стола под окном
uniform float uBackdrop;    // 1 = снимок есть
uniform vec2 uBdOff;        // где в canvas лежит левый верхний угол снимка, css px
uniform vec2 uBdSize;       // размер снимка, css px
uniform float uSolid;       // 1 = окно непрозрачное
uniform float uBgA;         // непрозрачность авроры-фона
uniform float uGlassA;      // непрозрачность интерьера стекла, когда снимка нет
uniform float uAur;         // доля авроры внутри стекла (остальное — рабочий стол)
out vec4 outColor;

const float IOR = 1.5;      // как у куба: показатель преломления
const float DISP = 0.16;    // и разброс по каналам R/G/B (дисперсия)

float h21(vec2 p){ return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vn(vec2 x){
  vec2 i = floor(x), f = fract(x);
  f = f * f * (3.0 - 2.0 * f);
  return mix(mix(h21(i), h21(i + vec2(1.0, 0.0)), f.x), mix(h21(i + vec2(0.0, 1.0)), h21(i + vec2(1.0, 1.0)), f.x), f.y);
}
vec3 ramp(float v){
  v = clamp(v, 0.0, 1.0) * 5.0;
  int i = int(min(floor(v), 4.0));
  return mix(uPal[i], uPal[i + 1], smoothstep(0.0, 1.0, v - float(i)));
}

// аврора: двойное искажение шума даёт текучие пятна, цветовая шкала — розово-фиолетовый «акварельный» вид
vec3 scene(vec2 pc, float lod){
  vec2 p = pc / 340.0;
  float t = uTime * 0.14;
  vec2 q = vec2(vn(p * 1.1 + vec2(t, -t * 0.7)), vn(p * 1.1 + vec2(5.2, 1.3) - vec2(t * 0.8, t)));
  vec2 r = vec2(vn(p * 1.5 + 2.2 * q + vec2(1.7, 9.2) + t * 0.6), vn(p * 1.5 + 2.2 * q + vec2(8.3, 2.8) - t * 0.5));
  float f = vn(p * 0.85 + 2.6 * r + q * 1.2 + t * 0.35);
  f = pow(smoothstep(0.16, 0.84, f * 1.12), 1.35);   // светлые «кремовые» зоны реже: белый текст читается
  vec3 c = ramp(f * uRange);
  // тонкие светящиеся контуры на границах пятен (изолинии поля) + лёгкое гало
  float l1 = exp(-pow((f - 0.30) / 0.015, 2.0));
  float l2 = exp(-pow((f - 0.52) / 0.017, 2.0));
  float halo = exp(-pow((f - 0.41) / 0.13, 2.0));
  c += uLine.rgb * uLine.a * (l1 + 0.85 * l2 + 0.07 * halo);
  float grain = h21(floor(pc) + floor(uTime * 9.0) * vec2(17.0, 31.0)) - 0.5;
  return c + grain * 0.13 * (1.0 - 0.35 * min(lod, 2.0) * 0.5);
}
vec3 desk(vec2 pc, float lod){
  vec2 uv = (pc - uBdOff) / uBdSize;
  return textureLod(uBd, clamp(uv, 0.001, 0.999), lod).rgb;
}
// что «лежит» в точке: аврора, смешанная с рабочим столом (aur = доля авроры)
vec3 S(vec2 pc, float lod, float aur){
  vec3 a = scene(pc, lod);
  return uBackdrop > 0.5 ? mix(desk(pc, lod), a, aur) : a;
}
float sdRound(vec2 p, vec2 b, float r){
  vec2 q = abs(p) - b + r;
  return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}
// один цветовой канал: луч преломляется на скошенной кромке (refract, как в кубе)
float refr(vec2 pc, vec3 n3, float ior, float th, float lod, float aur, int c){
  vec3 r = refract(vec3(0.0, 0.0, -1.0), n3, 1.0 / ior);
  vec2 sh = r.xy / max(-r.z, 0.25) * th;
  vec3 s = S(pc - sh, lod, aur);
  return c == 0 ? s.r : (c == 1 ? s.g : s.b);
}

void main(){
  vec2 pc = vec2(gl_FragCoord.x, uRes.y - gl_FragCoord.y) / uDpr;
  vec3 a0 = scene(pc, 0.0);
  vec3 col; float alpha;
  if (uSolid > 0.5){ col = uBackdrop > 0.5 ? mix(desk(pc, 0.0), a0, uBgA) : a0; alpha = 1.0; }
  else { col = a0 * uBgA; alpha = uBgA; }        // premultiplied: вне стекла просвечивает рабочий стол

  vec2 L = normalize(vec2(-0.55, -0.83) + uMouse * 0.28);   // откуда падает свет (y вниз)
  vec3 W = normalize(vec3(L, 0.5));                          // направление «окна» для блика

  for (int i = 0; i < MAX; i++){
    if (i >= uCount) break;
    vec4 cl = uClip[i];
    if (pc.x < cl.x || pc.y < cl.y || pc.x > cl.z || pc.y > cl.w) continue;
    vec4 rc = uRect[i]; vec4 pr = uPar[i];
    vec2 b = rc.zw * 0.5;
    vec2 q = pc - (rc.xy + b);
    float sd = sdRound(q, b, pr.x);
    if (sd > 1.0) continue;
    float cov = clamp(0.5 - sd, 0.0, 1.0);
    float d = max(-sd, 0.0);
    float bevel = max(1.0, min(pr.y, min(b.x, b.y) * 0.9));
    float t = clamp(d / bevel, 0.0, 1.0);          // 0 на кромке, 1 в плоской середине
    bool frame = pr.w < 0.0;                       // рамка окна: только линза по краю
    if (frame && t >= 1.0) continue;
    float s = 1.0 - t;
    float dk = abs(pr.w);

    vec2 n = vec2(sdRound(q + vec2(1.0, 0.0), b, pr.x) - sdRound(q - vec2(1.0, 0.0), b, pr.x),
                  sdRound(q + vec2(0.0, 1.0), b, pr.x) - sdRound(q - vec2(0.0, 1.0), b, pr.x));
    n = normalize(n + vec2(1e-5));                  // наружу от кромки

    // 3D-нормаль скруглённой фаски: плоско в середине, круто у самой кромки
    // у рамки окна профиль круче и шире: по краю заметная «жидкая» деформация, а не только тонкий ободок
    float prof = frame ? (1.1 * s + 1.7 * pow(s, 3.0)) : (0.2 * s + 2.4 * pow(s, 5.0));
    vec3 n3 = normalize(vec3(n * prof, 1.0));

    // преломление с дисперсией: у каждого канала свой IOR
    float lod = frame ? 0.0 : uTint[i].a * smoothstep(0.25, 1.0, t);
    float aur = frame ? 0.0 : uAur;
    vec3 g = vec3(refr(pc, n3, IOR - DISP, pr.z, lod, aur, 0),
                  refr(pc, n3, IOR,        pr.z, lod, aur, 1),
                  refr(pc, n3, IOR + DISP, pr.z, lod, aur, 2));

    // интерьер: притемнён и подкрашен, чтобы текст читался
    float inner = frame ? 0.0 : smoothstep(0.12, 0.9, t);
    g *= mix(1.0, dk, inner);
    vec3 tn = uTint[i].rgb * uTintMul;
    if (uTintLum.a > 0.5) tn = dot(uTint[i].rgb, vec3(0.30, 0.59, 0.11)) * uTintLum.rgb;   // красный/розовый оттенок кнопок -> цвет дизайна
    g += tn * inner;

    // Френель + отражение комнаты (градиент + светлое «окно»), как у куба
    vec3 R = reflect(vec3(0.0, 0.0, -1.0), n3);
    float fres = (0.04 + 0.96 * pow(1.0 - n3.z, 5.0)) * 2.6 * smoothstep(0.05, 0.5, s);
    vec3 room = mix(vec3(1.0, 0.95, 0.98), vec3(0.66, 0.56, 0.68), clamp(R.y * 0.5 + 0.5, 0.0, 1.0)) * 0.55;
    g = mix(g, room, clamp(fres, 0.0, 0.8));
    float win = smoothstep(0.90, 0.99, dot(R, W));
    g += vec3(win * 1.05) * smoothstep(0.15, 0.55, s);

    // свечение кромки, свет «насквозь» и волосок-обводка
    float back = pow(max(dot(n, -L), 0.0), 4.0) * pow(s, 2.0) * 0.30;
    g += uGlow * (pow(s, 3.0) * 0.16 + back);
    g += vec3(smoothstep(1.5, 0.0, d) * 0.30);

    float ia = uBackdrop > 0.5 ? 1.0 : uGlassA;
    float ga = frame ? cov * (1.0 - smoothstep(0.62, 1.0, t)) * (uBackdrop > 0.5 ? 1.0 : 0.6)
                     : cov * mix(0.96, ia, inner);
    col = col * (1.0 - ga) + g * ga;
    alpha = alpha * (1.0 - ga) + ga;
  }
  outColor = vec4(col, alpha);
}`;

  const PINK = [[.17, .04, .11], [.47, .13, .52], [.78, .33, .70], [1, .45, .52], [1, .68, .82], [1, .89, .93]];
  let look = {line: [0, 0, 0, 0], glow: [1, 0.62, 0.86], tint: [1, 1, 1], tintLum: [0, 0, 0, 0], range: 1};   // по умолчанию как раньше: без линий, розовое свечение

  let cfg = null, canvas = null, gl = null, program = null, texture = null, loc = {};
  let quality = 'low', active = true, ready = false, failed = false, raf = 0, last = 0;
  let boostUntil = 0, lastPointer = 0, hurryUntil = 0;
  let backdrop = false, bdMeta = null, solid = true, animated = true, palette = PINK, winPos = null, bgOverride = null;
  let elements = [], dirtyList = true, observer = null;
  const radiusCache = new WeakMap();
  const mouse = [0, 0], target = [0, 0];
  const buffers = {};
  const start = performance.now();

  const settings = () => quality === 'high'
    ? {dpr: Math.min(1.5, window.devicePixelRatio || 1), fps: 30, idle: 10}
    : {dpr: 1, fps: 20, idle: 8};

  function compile(type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source); gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader));
    return shader;
  }

  function setup() {
    gl = canvas.getContext('webgl2', {alpha: true, premultipliedAlpha: true, antialias: false, powerPreference: 'low-power', preserveDrawingBuffer: false});
    if (!gl) throw new Error('WebGL2 недоступен');
    program = gl.createProgram();
    gl.attachShader(program, compile(gl.VERTEX_SHADER, VERT));
    gl.attachShader(program, compile(gl.FRAGMENT_SHADER, FRAG));
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program));
    ['uRes', 'uDpr', 'uTime', 'uMouse', 'uCount', 'uRect', 'uPar', 'uTint', 'uClip', 'uPal', 'uBd', 'uBackdrop',
     'uBdOff', 'uBdSize', 'uSolid', 'uBgA', 'uGlassA', 'uAur', 'uLine', 'uGlow', 'uTintMul', 'uTintLum', 'uRange'].forEach(n => loc[n] = gl.getUniformLocation(program, n));
    buffers.rect = new Float32Array(MAX * 4); buffers.par = new Float32Array(MAX * 4);
    buffers.tint = new Float32Array(MAX * 4); buffers.clip = new Float32Array(MAX * 4);
    texture = gl.createTexture();
    gl.useProgram(program);
    gl.uniform1i(loc.uBd, 0);
    uploadTexture(new Uint8Array([0, 0, 0, 255]), 1, 1);          // заглушка, пока снимка нет
    canvas.addEventListener('webglcontextlost', e => { e.preventDefault(); ready = false; failed = true; apply(); });
    ready = true;
  }

  function uploadTexture(source, w, h) {
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
    if (w) gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, source);
    else gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
    gl.generateMipmap(gl.TEXTURE_2D);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  }

  function collect() {
    const list = [];
    cfg.kinds.forEach((kind, order) => {
      cfg.root.querySelectorAll(kind.selector).forEach(el => list.push({el, kind, order}));
    });
    // порядок документа: родитель рисуется раньше потомка
    list.sort((a, b) => (a.el === b.el ? a.order - b.order
      : (a.el.compareDocumentPosition(b.el) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1)));
    elements = list; dirtyList = false;
  }

  function radiusOf(el, w, h) {
    // элемент с data-live-radius меняет скругление на лету (остров): кэш не используем
    if (el.dataset.liveRadius) return Math.min(Math.min(w, h) / 2, parseFloat(el.style.borderRadius) || 0);
    let r = radiusCache.get(el);
    if (r === undefined) {
      const value = getComputedStyle(el).borderTopLeftRadius || '0';
      r = value.endsWith('%') ? {pct: parseFloat(value) / 100} : {px: parseFloat(value) || 0};
      radiusCache.set(el, r);
    }
    const m = Math.min(w, h) / 2;
    return Math.min(m, r.pct !== undefined ? r.pct * Math.min(w, h) : r.px);
  }

  function gather(cr) {
    if (dirtyList) collect();
    const cw = cr.width, ch = cr.height;
    const rect = buffers.rect, par = buffers.par, tint = buffers.tint, clip = buffers.clip;
    let n = 0;
    for (const item of elements) {
      if (n >= MAX) break;
      const el = item.el;
      if (cfg.filter && !cfg.filter(el)) continue;
      const r = el.getBoundingClientRect();
      const x = r.left - cr.left, y = r.top - cr.top;
      if (r.width < 2 || r.height < 2 || x + r.width < 0 || y + r.height < 0 || x > cw || y > ch) continue;
      const k = item.kind;
      let c = [0, 0, cw, ch];
      if (k.clip) {
        const box = el.closest(k.clip);
        if (box) { const b = box.getBoundingClientRect(); c = [b.left - cr.left, b.top - cr.top, b.right - cr.left, b.bottom - cr.top]; }
      }
      if (k.clip && (c[0] >= x + r.width || c[2] <= x || c[1] >= y + r.height || c[3] <= y)) continue;   // прокручено за край списка
      const tn = typeof k.tint === 'function' ? k.tint() : k.tint;
      const ev = v => (typeof v === 'function' ? v() : v);   // bevel/strength могут зависеть от формы
      rect.set([x, y, r.width, r.height], n * 4);
      par.set([radiusOf(el, r.width, r.height), ev(k.bevel), ev(k.strength), k.frame ? -(ev(k.dark) || 1) : ev(k.dark)], n * 4);
      tint.set([tn[0], tn[1], tn[2], k.blur || 0], n * 4);
      clip.set(c, n * 4);
      n++;
    }
    return n;
  }

  function draw(now) {
    const s = settings();
    const cr = canvas.getBoundingClientRect();
    const cw = canvas.clientWidth, ch = canvas.clientHeight;
    const w = Math.max(16, Math.round(cw * s.dpr)), h = Math.max(16, Math.round(ch * s.dpr));
    if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; gl.viewport(0, 0, w, h); }
    mouse[0] += (target[0] - mouse[0]) * 0.07; mouse[1] += (target[1] - mouse[1]) * 0.07;
    const count = gather(cr);
    gl.uniform2f(loc.uRes, w, h);
    gl.uniform1f(loc.uDpr, w / Math.max(1, cw));
    gl.uniform1f(loc.uTime, animated ? (now - start) / 1000 : 9.0);
    gl.uniform2f(loc.uMouse, mouse[0], mouse[1]);
    gl.uniform1i(loc.uCount, count);
    gl.uniform4fv(loc.uRect, buffers.rect); gl.uniform4fv(loc.uPar, buffers.par);
    gl.uniform4fv(loc.uTint, buffers.tint); gl.uniform4fv(loc.uClip, buffers.clip);
    gl.uniform3fv(loc.uPal, new Float32Array(palette.flat()));
    gl.uniform4f(loc.uLine, look.line[0], look.line[1], look.line[2], look.line[3]);
    gl.uniform3f(loc.uGlow, look.glow[0], look.glow[1], look.glow[2]);
    gl.uniform3f(loc.uTintMul, look.tint[0], look.tint[1], look.tint[2]);
    gl.uniform4f(loc.uTintLum, look.tintLum[0], look.tintLum[1], look.tintLum[2], look.tintLum[3]);
    gl.uniform1f(loc.uRange, look.range);
    // снимок рабочего стола: покрывает окно + поле pad со всех сторон (физические px -> css px)
    gl.uniform1f(loc.uBackdrop, backdrop ? 1 : 0);
    if (backdrop && bdMeta) {
      // снимок привязан к экрану: если окно сдвинули после снимка, текстура остаётся на месте (как настоящее стекло)
      const dpr = window.devicePixelRatio || 1, pad = bdMeta.pad || 0;
      let ox = -pad / dpr, oy = -pad / dpr;
      if (winPos && bdMeta.left != null) {
        ox = ((bdMeta.left - pad) - (winPos[0] + cr.left * dpr)) / dpr;
        oy = ((bdMeta.top - pad) - (winPos[1] + cr.top * dpr)) / dpr;
      }
      gl.uniform2f(loc.uBdOff, ox, oy);
      gl.uniform2f(loc.uBdSize, (bdMeta.w + 2 * pad) / dpr, (bdMeta.h + 2 * pad) / dpr);
    }
    gl.uniform1f(loc.uSolid, solid ? 1 : 0);
    gl.uniform1f(loc.uBgA, bgOverride != null ? bgOverride : (cfg.bgAlpha != null ? cfg.bgAlpha : 0.6));
    gl.uniform1f(loc.uGlassA, cfg.glassAlpha || 0.82);
    gl.uniform1f(loc.uAur, cfg.aurora || 0.8);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  }

  function frame(now) {
    raf = 0;
    if (!ready || !active || quality === 'low' || document.hidden) return;   // скрытое окно: ноль работы
    raf = requestAnimationFrame(frame);
    const s = settings();
    const busy = now < boostUntil;
    // во время анимации формы рисуем каждый кадр: иначе стекло отстаёт от окна
    const minGap = now < hurryUntil ? 0 : (animated ? 1000 / s.fps : (busy ? 1000 / s.fps : 1000 / s.idle));
    if (now - last < minGap - 1) return;
    last = now;
    draw(now);
  }

  function kick() { if (ready && active && quality !== 'low' && !document.hidden && !raf) raf = requestAnimationFrame(frame); }
  function boost() { boostUntil = performance.now() + 1600; kick(); }

  function apply() {
    const on = !failed && quality !== 'low';
    if (on && !ready && canvas) {
      try { setup(); } catch (error) { failed = true; console.warn('LiquidGlass:', error.message); }
    }
    const show = ready && on;
    document.body.classList.toggle('lg-on', show);
    if (show) { dirtyList = true; boost(); }
    else if (raf) { cancelAnimationFrame(raf); raf = 0; }
  }

  return {
    mount(options) {
      cfg = options; canvas = options.canvas;
      if (options.palette) palette = options.palette;
      if (options.solid != null) solid = !!options.solid;
      // Track/time/status text changes do not change the set of glass surfaces.
      // Recollecting every surface on every poll needlessly forced layout work.
      observer = new MutationObserver(records => {
        if(records.some(record=>[...record.addedNodes,...record.removedNodes].some(node=>node.nodeType===Node.ELEMENT_NODE))){dirtyList=true;boost();}
      });
      observer.observe(cfg.root, {childList: true, subtree: true});
      document.addEventListener('visibilitychange', () => { if (!document.hidden) boost(); });
      window.addEventListener('resize', boost);
      ['pointerdown', 'pointerup', 'wheel', 'keydown', 'transitionrun', 'animationstart'].forEach(name =>
        document.addEventListener(name, boost, {passive: true, capture: true}));
      document.addEventListener('scroll', boost, {passive: true, capture: true});
      document.addEventListener('pointermove', e => {
        target[0] = (e.clientX / Math.max(1, window.innerWidth) - 0.5) * 2;
        target[1] = (e.clientY / Math.max(1, window.innerHeight) - 0.5) * 2;
        if (performance.now() - lastPointer > 60) { lastPointer = performance.now(); boost(); }
      }, {passive: true});
      apply();
    },
    setQuality(value) {
      const next = ['high', 'medium', 'low'].includes(value) ? value : 'high';
      if (next === 'low') this.clearBackdrop();
      if (next === quality) return;
      quality = next; apply();
    },
    hurry(ms) { hurryUntil = performance.now() + ms; kick(); },
    setActive(value) { active = !!value; if (active) boost(); else if (raf) { cancelAnimationFrame(raf); raf = 0; } },
    setSolid(value) { solid = !!value; boost(); },
    setAnimated(value) { animated = !!value; boost(); },
    setBgAlpha(value) { bgOverride = value; boost(); },
    // дополнительные параметры дизайна: line = [r, g, b, сила] светящихся контуров, glow = [r, g, b] свечения кромок стекла
    setLook(value) {
      const next = value || {};
      look = {line: (next.line || [0, 0, 0, 0]).slice(0, 4), glow: (next.glow || [1, 0.62, 0.86]).slice(0, 3), tint: (next.tint || [1, 1, 1]).slice(0, 3), tintLum: (next.tintLum || [0, 0, 0, 0]).slice(0, 4), range: next.range || 1};
      boost();
    },
    setPalette(value) { if (value) { palette = value; boost(); } },
    refresh() { dirtyList = true; boost(); },
    // положение окна на экране (физические px): C# присылает при каждом перемещении
    setPos(x, y) { winPos = [x, y]; boost(); },
    // снимок рабочего стола под окном (data: URL) + {w, h, pad, left, top} в физических px
    setBackdrop(src, meta) {
      if (!ready || !src || !meta) return;
      const image = new Image();
      image.onload = () => { if (ready) { uploadTexture(image); bdMeta = meta; backdrop = true; boost(); } };
      image.src = src;
    },
    clearBackdrop() { if (backdrop) { backdrop = false; boost(); } },
    get hasBackdrop() { return backdrop; },
    get on() { return ready && quality !== 'low'; }
  };
})();
