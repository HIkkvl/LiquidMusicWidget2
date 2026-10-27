'use strict';
/* Liquid Glass: фон-«афиша» + стеклянные элементы интерфейса.
   Эффект перенесён из шейдера со стеклянным кубом (самого куба в приложении нет): стекло преломляет то, что лежит за ним, каждый цвет R/G/B
   преломляется чуть по-разному (дисперсия), на кромке блик и эффект Френеля.
   Здесь стекло — это сами панели/кнопки (скруглённые прямоугольники из DOM):
   у края толстая «линза» сильно искажает фон, внутри он размыт и притемнён, чтобы текст читался.
   Текст и иконки остаются обычным DOM поверх canvas.
   Экономия: не рисуем, пока окно скрыто; в покое ~10 к/с; «Среднее» = меньше разрешение и 30 к/с. */
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
uniform vec2 uStage;        // размер афиши, css px
uniform vec2 uOff;          // смещение афиши в окне, css px
uniform vec2 uShift;        // параллакс афиши, css px
uniform vec2 uMouse;        // -1..1, двигает свет
uniform int uCount;
uniform vec4 uRect[MAX];    // x, y, w, h (css px, y вниз)
uniform vec4 uPar[MAX];     // радиус, фаска, толщина стекла (px), тёмность интерьера
uniform vec4 uTint[MAX];    // rgb подкраска интерьера, a = блур интерьера (lod)
uniform vec4 uClip[MAX];    // x0, y0, x1, y1
uniform sampler2D uPoster;
uniform float uBackdrop;    // 1 = в текстуре снимок рабочего стола под окном, 0 = своя афиша
uniform float uGlassA;      // непрозрачность интерьера стекла, когда снимка нет
uniform float uSolid;       // 1 = рисуем афишу сплошным фоном, 0 = окно прозрачное
out vec4 outColor;

const float IOR = 1.5;      // как у куба: показатель преломления
const float DISP = 0.16;    // и разброс по каналам R/G/B (дисперсия)

vec3 P(vec2 pc, float lod){
  vec2 uv = (pc - uOff + uShift) / uStage;
  return textureLod(uPoster, clamp(uv, 0.001, 0.999), lod).rgb;
}
float sdRound(vec2 p, vec2 b, float r){
  vec2 q = abs(p) - b + r;
  return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}
// один цветовой канал: луч преломляется на скошенной кромке (refract, как в кубе)
// и приходит на афишу в точку, смещённую тем сильнее, чем круче нормаль
float refr(vec2 pc, vec3 n3, float ior, float th, float lod, int c){
  vec3 r = refract(vec3(0.0, 0.0, -1.0), n3, 1.0 / ior);
  vec2 sh = r.xy / max(-r.z, 0.25) * th;
  vec3 s = P(pc - sh, lod);
  return c == 0 ? s.r : (c == 1 ? s.g : s.b);
}

void main(){
  vec2 pc = vec2(gl_FragCoord.x, uRes.y - gl_FragCoord.y) / uDpr;
  vec3 col = uSolid > 0.5 ? P(pc, 0.0) : vec3(0.0);      // premultiplied; вне стекла окно прозрачное
  float alpha = uSolid > 0.5 ? 1.0 : 0.0;
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
    float s = 1.0 - t;

    vec2 n = vec2(sdRound(q + vec2(1.0, 0.0), b, pr.x) - sdRound(q - vec2(1.0, 0.0), b, pr.x),
                  sdRound(q + vec2(0.0, 1.0), b, pr.x) - sdRound(q - vec2(0.0, 1.0), b, pr.x));
    n = normalize(n + vec2(1e-5));                  // наружу от кромки

    // 3D-нормаль скруглённой фаски: плоско в середине, круто у самой кромки
    vec3 n3 = normalize(vec3(n * (0.2 * s + 2.4 * pow(s, 5.0)), 1.0));

    // преломление с дисперсией: у каждого канала свой IOR
    float lod = uTint[i].a * smoothstep(0.25, 1.0, t);
    vec3 g = vec3(refr(pc, n3, IOR - DISP, pr.z, lod, 0),
                  refr(pc, n3, IOR,        pr.z, lod, 1),
                  refr(pc, n3, IOR + DISP, pr.z, lod, 2));

    // интерьер: притемнён и подкрашен, чтобы текст читался
    float inner = smoothstep(0.12, 0.9, t);
    g *= mix(1.0, pr.w, inner);
    g += uTint[i].rgb * inner;

    // Френель + отражение комнаты (градиент + светлое «окно»), как у куба
    vec3 R = reflect(vec3(0.0, 0.0, -1.0), n3);
    float fres = (0.04 + 0.96 * pow(1.0 - n3.z, 5.0)) * 2.6 * smoothstep(0.05, 0.5, s);
    vec3 room = mix(vec3(0.96, 0.96, 0.98), vec3(0.60, 0.62, 0.68), clamp(R.y * 0.5 + 0.5, 0.0, 1.0)) * 0.55;
    g = mix(g, room, clamp(fres, 0.0, 0.8));
    float win = smoothstep(0.90, 0.99, dot(R, W));
    g += vec3(win * 1.05) * smoothstep(0.15, 0.55, s);

    // свечение кромки (холодное, как edge у куба), свет «насквозь» и волосок-обводка
    float back = pow(max(dot(n, -L), 0.0), 4.0) * pow(s, 2.0) * 0.30;
    g += vec3(0.40, 0.50, 0.90) * (pow(s, 3.0) * 0.16 + back);
    g += vec3(smoothstep(1.5, 0.0, d) * 0.30);
    // со снимком рабочего стола интерьер непрозрачен (это и есть «видимый сквозь стекло» фон),
    // без снимка — полупрозрачный, и живой рабочий стол просвечивает сквозь окно
    float ia = uBackdrop > 0.5 ? 1.0 : uGlassA;
    float ga = cov * mix(0.96, ia, inner);
    col = col * (1.0 - ga) + g * ga;
    alpha = alpha * (1.0 - ga) + ga;
  }
  outColor = vec4(col, alpha);
}`;

  let cfg = null, canvas = null, gl = null, program = null, texture = null, loc = {};
  let quality = 'low', active = true, ready = false, failed = false, raf = 0, last = 0;
  let boostUntil = 0, lastPointer = 0, backdrop = false, solid = true;
  let elements = [], dirtyList = true, observer = null, resizeTimer = 0;
  const radiusCache = new WeakMap();
  const mouse = [0, 0], target = [0, 0];
  const buffers = {};
  const start = performance.now();

  const settings = () => quality === 'high'
    ? {dpr: Math.min(1.5, window.devicePixelRatio || 1), fps: 0, idle: 10}
    : {dpr: 1, fps: 30, idle: 8};

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
    ['uRes', 'uDpr', 'uStage', 'uOff', 'uShift', 'uMouse', 'uCount', 'uRect', 'uPar', 'uTint', 'uClip', 'uPoster', 'uBackdrop', 'uGlassA', 'uSolid']
      .forEach(n => loc[n] = gl.getUniformLocation(program, n));
    buffers.rect = new Float32Array(MAX * 4); buffers.par = new Float32Array(MAX * 4);
    buffers.tint = new Float32Array(MAX * 4); buffers.clip = new Float32Array(MAX * 4);
    texture = gl.createTexture();
    gl.useProgram(program);
    gl.uniform1i(loc.uPoster, 0);
    canvas.addEventListener('webglcontextlost', e => { e.preventDefault(); ready = false; failed = true; apply(); });
    ready = true;
  }

  function stageSize() {
    return cfg.stage || {w: Math.max(1, window.innerWidth), h: Math.max(1, window.innerHeight)};
  }

  function uploadTexture(source) {
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
    gl.generateMipmap(gl.TEXTURE_2D);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  }

  function paintPoster() {
    if (!ready || backdrop) return;   // пока есть снимок рабочего стола, афишу не трогаем
    const s = stageSize(), dpr = settings().dpr;
    const c = document.createElement('canvas');
    c.width = Math.round(s.w * dpr); c.height = Math.round(s.h * dpr);
    const x = c.getContext('2d');
    x.scale(dpr, dpr);
    cfg.poster(x, s.w, s.h);
    uploadTexture(c);
    gl.uniform2f(loc.uStage, s.w, s.h);
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
    let r = radiusCache.get(el);
    if (r === undefined) {
      const value = getComputedStyle(el).borderTopLeftRadius || '0';
      r = value.endsWith('%') ? {pct: parseFloat(value) / 100} : {px: parseFloat(value) || 0};
      radiusCache.set(el, r);
    }
    const m = Math.min(w, h) / 2;
    return Math.min(m, r.pct !== undefined ? r.pct * Math.min(w, h) : r.px);
  }

  function gather() {
    if (dirtyList) collect();
    const vw = window.innerWidth, vh = window.innerHeight;
    const rect = buffers.rect, par = buffers.par, tint = buffers.tint, clip = buffers.clip;
    let n = 0;
    for (const item of elements) {
      if (n >= MAX) break;
      const el = item.el;
      if (cfg.filter && !cfg.filter(el)) continue;
      const r = el.getBoundingClientRect();
      if (r.width < 2 || r.height < 2 || r.bottom < 0 || r.right < 0 || r.top > vh || r.left > vw) continue;
      const k = item.kind;
      let c = [0, 0, vw, vh];
      if (k.clip) {
        const box = el.closest(k.clip);
        if (box) { const b = box.getBoundingClientRect(); c = [b.left, b.top, b.right, b.bottom]; }
      }
      if (k.clip && (c[0] >= r.right || c[2] <= r.left || c[1] >= r.bottom || c[3] <= r.top)) continue;   // прокручено за край списка
      const tn = typeof k.tint === 'function' ? k.tint() : k.tint;
      rect.set([r.left, r.top, r.width, r.height], n * 4);
      par.set([radiusOf(el, r.width, r.height), k.bevel, k.strength, k.dark], n * 4);
      tint.set([tn[0], tn[1], tn[2], k.blur], n * 4);
      clip.set(c, n * 4);
      n++;
    }
    return n;
  }

  function draw(now) {
    const s = settings();
    const w = Math.max(16, Math.round(canvas.clientWidth * s.dpr)), h = Math.max(16, Math.round(canvas.clientHeight * s.dpr));
    if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; gl.viewport(0, 0, w, h); }
    mouse[0] += (target[0] - mouse[0]) * 0.07; mouse[1] += (target[1] - mouse[1]) * 0.07;
    const t = (now - start) / 1000;
    const cw = canvas.clientWidth, ch = canvas.clientHeight;
    const st = backdrop ? {w: cw, h: ch} : stageSize();   // снимок = ровно окно
    const count = gather();
    gl.uniform2f(loc.uRes, w, h);
    gl.uniform1f(loc.uDpr, w / Math.max(1, cw));
    gl.uniform2f(loc.uStage, st.w, st.h);
    gl.uniform2f(loc.uOff, backdrop ? 0 : (cw - st.w) / 2, backdrop ? 0 : (ch - st.h) / 2);
    gl.uniform2f(loc.uShift, backdrop ? 0 : mouse[0] * 7 + Math.sin(t * 0.35) * 3.5, backdrop ? 0 : mouse[1] * 7 + Math.cos(t * 0.28) * 3.5);
    gl.uniform1f(loc.uBackdrop, backdrop ? 1 : 0);
    gl.uniform1f(loc.uSolid, solid ? 1 : 0);
    gl.uniform1f(loc.uGlassA, cfg.glassAlpha || 0.8);
    gl.uniform2f(loc.uMouse, mouse[0], mouse[1]);
    gl.uniform1i(loc.uCount, count);
    gl.uniform4fv(loc.uRect, buffers.rect); gl.uniform4fv(loc.uPar, buffers.par);
    gl.uniform4fv(loc.uTint, buffers.tint); gl.uniform4fv(loc.uClip, buffers.clip);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  }

  function frame(now) {
    raf = 0;
    if (!ready || !active || quality === 'low' || document.hidden) return;   // скрытое окно: ноль работы
    raf = requestAnimationFrame(frame);
    const s = settings();
    const busy = now < boostUntil;
    const minGap = busy ? (s.fps ? 1000 / s.fps : 0) : 1000 / s.idle;
    if (now - last < minGap - 1) return;
    last = now;
    draw(now);
  }

  function kick() { if (ready && active && quality !== 'low' && !document.hidden && !raf) raf = requestAnimationFrame(frame); }
  function boost() { boostUntil = performance.now() + 1600; kick(); }

  function apply() {
    const on = !failed && quality !== 'low';
    if (on && !ready && canvas) {
      try { setup(); paintPoster(); } catch (error) { failed = true; console.warn('LiquidGlass:', error.message); }
    }
    const show = ready && on;
    document.body.classList.toggle('lg-on', show);
    if (show) { dirtyList = true; boost(); }
    else if (raf) { cancelAnimationFrame(raf); raf = 0; }
  }

  return {
    mount(options) {
      cfg = options; canvas = options.canvas;
      observer = new MutationObserver(() => { dirtyList = true; boost(); });
      observer.observe(cfg.root, {childList: true, subtree: true});
      document.addEventListener('visibilitychange', () => { if (!document.hidden) boost(); });
      window.addEventListener('resize', () => {
        clearTimeout(resizeTimer);
        boost();
        resizeTimer = setTimeout(() => { if (!cfg.stage) paintPoster(); boost(); }, 180);
      });
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
      if (value === 'low') this.clearBackdrop();
      const next = ['high', 'medium', 'low'].includes(value) ? value : 'high';
      if (next === quality) return;
      const retex = ready && (next === 'high') !== (quality === 'high');
      quality = next; apply();
      if (retex) paintPoster();
    },
    setActive(value) { active = !!value; if (active) boost(); else if (raf) { cancelAnimationFrame(raf); raf = 0; } },
    refresh() { dirtyList = true; boost(); },
    // снимок рабочего стола под окном (data: URL от Python): стекло преломляет реальный фон
    setBackdrop(src) {
      if (!ready || !src) return;
      const image = new Image();
      image.onload = () => { if (ready) { backdrop = true; uploadTexture(image); boost(); } };
      image.src = src;
    },
    clearBackdrop() { if (!backdrop) return; backdrop = false; if (ready) { paintPoster(); boost(); } },
    get hasBackdrop() { return backdrop; },
    setSolid(value) { solid = !!value; if (solid) this.clearBackdrop(); boost(); },
    get on() { return ready && quality !== 'low'; }
  };
})();

/* ---------- афиши ---------- */
window.LiquidPosters = (() => {
  const C = {ink: '#0a0b0e', red: '#e2271c', blue: '#1c3ad8', yellow: '#f7c72e', bone: '#ece8df'};

  function brushed(x, w, h) {
    // графитовый «шлифованный металл»: диагональный блик, мелкое зерно, виньетка
    const g = x.createLinearGradient(0, 0, w, h);
    g.addColorStop(0, '#2a303b'); g.addColorStop(0.5, '#171b23'); g.addColorStop(1, '#0d1017');
    x.fillStyle = g; x.fillRect(0, 0, w, h);
    const s = x.createLinearGradient(0, h * 0.05, w, h * 0.5);
    s.addColorStop(0, 'rgba(255,255,255,0)'); s.addColorStop(0.45, 'rgba(255,255,255,.08)'); s.addColorStop(1, 'rgba(255,255,255,0)');
    x.fillStyle = s; x.fillRect(0, 0, w, h);
    let seed = 7;
    const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
    for (let i = 0; i < Math.round(h / 3); i++) {
      x.fillStyle = rnd() > 0.5 ? 'rgba(255,255,255,' + (0.012 + rnd() * 0.03) + ')' : 'rgba(0,0,0,' + (0.03 + rnd() * 0.05) + ')';
      x.fillRect(0, rnd() * h, w, 1);
    }
  }

  function word(x, text, cx, cy, size, angle, color, alpha) {
    x.save(); x.translate(cx, cy); x.rotate(angle);
    x.globalAlpha = alpha; x.fillStyle = color;
    x.font = '900 ' + size + 'px "Segoe UI Black","Arial Black","Segoe UI",Arial,sans-serif';
    x.textAlign = 'center'; x.textBaseline = 'middle';
    x.fillText(text, 0, 0); x.restore();
  }

  function shade(x, w, h, top, bottom) {
    // тёмные края под шапку и подсказку: текст вне стекла остаётся читаемым
    let g = x.createLinearGradient(0, 0, 0, top);
    g.addColorStop(0, 'rgba(6,8,13,.72)'); g.addColorStop(1, 'rgba(6,8,13,0)');
    x.fillStyle = g; x.fillRect(0, 0, w, top);
    g = x.createLinearGradient(0, h - bottom, 0, h);
    g.addColorStop(0, 'rgba(6,8,13,0)'); g.addColorStop(1, 'rgba(6,8,13,.78)');
    x.fillStyle = g; x.fillRect(0, h - bottom, w, bottom);
  }

  // главное окно (портрет ~414×1000)
  function main(x, w, h) {
    brushed(x, w, h);
    x.globalAlpha = 0.5; x.fillStyle = C.bone; x.beginPath(); x.arc(w * 0.16, h * 0.15, w * 0.36, 0, 7); x.fill(); x.globalAlpha = 1;
    x.fillStyle = C.ink; x.beginPath(); x.arc(w * 0.84, h * 0.40, w * 0.22, 0, 7); x.fill();
    x.fillStyle = C.red; x.fillRect(w * 0.66, h * 0.05, w * 0.13, h * 0.74);
    x.fillStyle = C.blue; x.fillRect(w * 0.84, h * 0.60, w * 0.10, w * 0.10);
    x.fillRect(w * 0.12, h * 0.50, w * 0.05, w * 0.05);
    x.fillStyle = C.yellow; x.fillRect(w * 0.0, h * 0.90, w * 0.34, 6);
    x.save(); x.translate(w * 0.5, h * 0.46); x.rotate(0.46);
    x.fillStyle = C.yellow; x.fillRect(-w * 0.9, -2.5, w * 1.8, 5); x.restore();
    x.fillStyle = 'rgba(255,255,255,.55)'; x.fillRect(w * 0.06, h * 0.355, w * 0.7, 1.5);
    x.fillRect(w * 0.06, h * 0.715, w * 0.88, 1.5);
    word(x, 'MUSIC', w * 0.5, h * 0.30, w * 0.50, 0, C.bone, 0.10);
    word(x, 'LIQUID', w * 0.955, h * 0.5, w * 0.12, Math.PI / 2, C.bone, 0.85);
    word(x, 'SOUND', w * 0.30, h * 0.60, w * 0.16, 0, C.bone, 0.92);
    word(x, 'FORM', w * 0.40, h * 0.80, w * 0.24, 0, C.ink, 0.0);
    word(x, 'MOTION', w * 0.62, h * 0.67, w * 0.09, -0.32, C.blue, 1);
    word(x, '01', w * 0.84, h * 0.93, w * 0.16, 0, C.bone, 0.85);
    shade(x, w, h, h * 0.10, h * 0.10);
  }

  // остров (340×120)
  function island(x, w, h) {
    brushed(x, w, h);
    x.fillStyle = C.bone; x.beginPath(); x.arc(w * 0.13, h * 0.5, h * 0.62, 0, 7); x.fill();
    x.fillStyle = C.red; x.fillRect(w * 0.60, 0, w * 0.07, h);
    x.fillStyle = C.blue; x.fillRect(w * 0.86, h * 0.14, h * 0.2, h * 0.2);
    x.save(); x.translate(w * 0.5, h * 0.5); x.rotate(-0.22);
    x.fillStyle = C.yellow; x.fillRect(-w, -2, w * 2, 4); x.restore();
    word(x, 'MUSIC', w * 0.55, h * 0.5, h * 0.62, 0, C.bone, 0.10);
    word(x, '01', w * 0.92, h * 0.80, h * 0.22, 0, C.bone, 0.85);
  }

  return {main, island};
})();
