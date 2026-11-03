'use strict';
/* Смена дизайна: «Аврора» (розово-фиолетовая) и «Бездна» (тёмная, с тонкими голубыми контурами).
   Подключается из C# (AddScriptToExecuteOnDocumentCreatedAsync), поэтому index.html и app.js менять не нужно.
   Дизайн меняет: фон и стекло главного окна (палитра и линии шейдера), акценты интерфейса (abyss.css) и цвета острова. */
(() => {
  if (location.origin !== 'https://liquid.local') return;
  const api = () => window.liquid && window.liquid.api;
  const STORAGE = 'liquid-design';
  const DARK_STORAGE = 'liquid-panel-dark', DARK_DEFAULT = 40;   // затемнение плашек, %
  let panelDark = DARK_DEFAULT;
  try { const v = parseInt(localStorage.getItem(DARK_STORAGE), 10); if (Number.isFinite(v)) panelDark = Math.min(100, Math.max(0, v)); } catch (_) {}
  // скрипт стартует до разбора страницы: documentElement может ещё не существовать
  const applyDark = () => { if (document.documentElement) document.documentElement.style.setProperty('--pd', String(panelDark / 100)); };
  applyDark();
  // те же палитры, что в app.js (PALETTES): нужны, если app.js выставил палитру раньше, чем мы перехватили setPalette
  const AURORA_BY_SERVICE = {
    yandex: [[.17,.04,.11],[.47,.13,.52],[.78,.33,.70],[1,.45,.52],[1,.68,.82],[1,.89,.93]],
    youtube: [[.16,.03,.05],[.50,.08,.14],[.82,.18,.25],[1,.40,.30],[1,.65,.50],[1,.88,.80]],
    spotify: [[.02,.12,.07],[.04,.25,.13],[.08,.48,.24],[.12,.72,.36],[.35,.88,.55],[.80,1,.87]],
    soundcloud: [[.16,.05,.02],[.40,.12,.03],[.72,.26,.05],[1,.40,.12],[1,.67,.32],[1,.9,.75]]
  };
  const DESIGNS = {
    aurora: {
      name: 'Аврора', island: 'aurora',
      preview: [[.17, .04, .11], [.47, .13, .52], [.78, .33, .70], [1, .45, .52], [1, .68, .82], [1, .89, .93]]
    },
    abyss: {
      name: 'Бездна', island: 'abyss',
      preview: [[.003, .006, .012], [.010, .028, .058], [.030, .085, .150], [.15, .20, .28], [.36, .45, .56], [.64, .73, .83]],
      look: {line: [.20, .72, 1, .62], glow: [.45, .80, 1], tint: [.2, 1, .6], tintLum: [.5, 2, 3.8, 1], range: .8}
    },
    neon: {
      name: 'Неон', island: 'neon',
      preview: [[.01, .01, .03], [.03, .03, .13], [.11, .09, .46], [.33, .29, .95], [.63, .59, 1], [.90, .88, 1]],
      look: {line: [.50, .46, 1, .55], glow: [.58, .54, 1], tint: [.3, .3, 1], tintLum: [.9, 1.0, 3.6, 1], range: .8}
    },
    grain: {
      name: 'Зерно', island: 'grain',
      preview: [[.01, .01, .01], [.05, .05, .05], [.16, .16, .16], [.38, .38, .38], [.68, .68, .68], [.93, .93, .93]],
      look: {line: [.95, .95, .95, .30], glow: [.90, .90, .90], tint: [1, 1, 1], tintLum: [0, 0, 0, 0], range: 1}
    }
  };
  let design = 'aurora', requestedPalette = null, wrapped = false, els = null;

  try { const saved = localStorage.getItem(STORAGE); if (saved && DESIGNS[saved]) design = saved; } catch (_) {}

  function loadCss() {
    if (!document.head) return;   // скрипт стартует до разбора страницы
    for (const file of ['abyss.css', 'neon.css', 'grain.css']) {
      if (document.querySelector('link[href$="' + file + '"]')) continue;
      const link = document.createElement('link'); link.rel = 'stylesheet'; link.href = file; document.head.append(link);
    }
  }

  // app.js выставляет палитру по сервису (Яндекс / YouTube): пока выбрана «Бездна», эти вызовы запоминаем, но не применяем
  function wrapEngine() {
    const engine = window.LiquidGlass;
    if (wrapped || !engine || !engine.setPalette || !engine.setLook) return !!wrapped;
    const original = engine.setPalette.bind(engine);
    engine.setPalette = value => { requestedPalette = value; if (!DESIGNS[design].look) original(value); };
    engine.__applyDesign = () => {
      if (DESIGNS[design].look) { original(DESIGNS[design].preview); engine.setLook(DESIGNS[design].look); }
      else { engine.setLook(null); original(requestedPalette || AURORA_BY_SERVICE[document.body && document.body.dataset.service] || AURORA_BY_SERVICE.yandex); }
    };
    wrapped = true;
    return true;
  }

  function apply() {
    if (document.body) document.body.dataset.design = design;
    document.documentElement.dataset.design = design;
    if (wrapEngine()) window.LiquidGlass.__applyDesign();
    if (els) paint();
  }

  const rgb = color => 'rgb(' + color.map(v => Math.round(Math.max(0, Math.min(1, v)) * 255)).join(',') + ')';
  const gradient = palette => 'linear-gradient(120deg,' + palette.map((c, i) => rgb(c) + ' ' + i * 20 + '%').join(',') + ')';

  function build() {
    if (document.getElementById('design-settings')) return;
    const panel = document.querySelector('#settings-dialog .settings-panel'); if (!panel) return;
    const style = document.createElement('style');
    style.textContent = '.design-settings .theme-swatches{grid-template-columns:repeat(2,1fr)}.design-settings .swatch i{height:34px}'
      + '.design-settings .design-hint{margin:0;font-size:11.5px;line-height:1.45;color:var(--text-2,rgba(235,242,255,.66))}'
      + `.design-dark{display:flex;flex-direction:column;gap:10px;margin-top:4px;padding:12px 14px 12px;border-radius:16px;
          background:linear-gradient(145deg,rgba(255,255,255,.10),rgba(255,255,255,.03));
          box-shadow:inset 0 1px 0 rgba(255,255,255,.22),inset 0 0 0 .5px rgba(255,255,255,.10)}
        .design-dark-head{display:flex;align-items:center;gap:8px}
        .design-dark-head strong{flex:1;font-size:13px;font-weight:600;color:var(--text,#f5f8ff)}
        .design-dark-value{min-width:46px;padding:3px 10px;border-radius:999px;text-align:center;font-size:12px;font-weight:600;font-variant-numeric:tabular-nums;color:#fff;
          background:linear-gradient(120deg,color-mix(in srgb,var(--blue,#ff9ccb) 55%,transparent),color-mix(in srgb,var(--violet,#a85bd6) 50%,transparent));
          box-shadow:inset 0 1px 0 rgba(255,255,255,.35)}
        .design-dark-reset{padding:3px 9px;border:0;border-radius:999px;font-size:11.5px;color:var(--text-2,rgba(235,242,255,.66));background:rgba(255,255,255,.08);transition:background .2s,color .2s}
        .design-dark-reset:hover{background:rgba(255,255,255,.16);color:var(--text,#f5f8ff)}
        .design-dark-scale{display:flex;justify-content:space-between;margin-top:-4px;font-size:10.5px;letter-spacing:.2px;color:var(--text-3,rgba(225,235,255,.42))}
        .lm-range{--v:40;-webkit-appearance:none;appearance:none;width:100%;height:28px;margin:0;background:transparent;cursor:pointer}
        .lm-range:focus{outline:none}
        .lm-range::-webkit-slider-runnable-track{height:10px;border-radius:999px;
          background:linear-gradient(90deg,var(--blue,#ff9ccb),var(--indigo,#d65fb0) 60%,var(--violet,#a85bd6)) 0 0/calc(12px + (100% - 24px) * var(--v) / 100) 100% no-repeat,rgba(255,255,255,.13);
          box-shadow:inset 0 1px 3px rgba(0,0,0,.4),0 0 0 .5px rgba(255,255,255,.14)}
        .lm-range::-webkit-slider-thumb{-webkit-appearance:none;appearance:none;width:26px;height:26px;margin-top:-8px;border-radius:50%;border:1px solid rgba(255,255,255,.75);
          background:radial-gradient(circle at 32% 24%,#fff,rgba(255,255,255,.88) 42%,rgba(255,226,244,.72));
          box-shadow:0 3px 10px rgba(0,0,0,.42),0 0 16px color-mix(in srgb,var(--indigo,#d65fb0) 65%,transparent),inset 0 -3px 5px rgba(160,50,140,.28);
          transition:transform .25s var(--spring,ease),box-shadow .2s}
        .lm-range:hover::-webkit-slider-thumb{transform:scale(1.1)}
        .lm-range:active::-webkit-slider-thumb{transform:scale(.94);box-shadow:0 2px 6px rgba(0,0,0,.4),0 0 22px color-mix(in srgb,var(--indigo,#d65fb0) 80%,transparent),inset 0 -3px 5px rgba(160,50,140,.28)}
        .lm-range:focus-visible::-webkit-slider-thumb{outline:2px solid rgba(255,255,255,.85);outline-offset:3px}`;
    document.head.append(style);
    const section = document.createElement('section');
    section.id = 'design-settings'; section.className = 'island-theme design-settings'; section.setAttribute('aria-label', 'Оформление');
    const title = document.createElement('h3'); title.textContent = 'Оформление';
    const hint = document.createElement('p'); hint.className = 'design-hint';
    hint.textContent = 'Меняет фон и стекло виджета и цвета острова. Цвета острова можно поменять отдельно ниже.';
    const list = document.createElement('div'); list.className = 'theme-swatches'; list.setAttribute('role', 'radiogroup'); list.setAttribute('aria-label', 'Оформление');
    for (const key of Object.keys(DESIGNS)) {
      const button = document.createElement('button');
      button.type = 'button'; button.className = 'swatch'; button.dataset.design = key; button.setAttribute('role', 'radio'); button.title = DESIGNS[key].name;
      const sample = document.createElement('i'), label = document.createElement('span'); label.textContent = DESIGNS[key].name;
      sample.style.background = gradient(DESIGNS[key].preview);
      button.append(sample, label); list.append(button);
    }
    const dark = document.createElement('div'); dark.className = 'design-dark';
    const darkHead = document.createElement('div'); darkHead.className = 'design-dark-head';
    const darkName = document.createElement('strong'); darkName.textContent = 'Затемнение плашек';
    const darkValue = document.createElement('span'); darkValue.className = 'design-dark-value';
    const reset = document.createElement('button'); reset.type = 'button'; reset.className = 'design-dark-reset'; reset.textContent = 'Сбросить';
    darkHead.append(darkName, darkValue, reset);
    const range = document.createElement('input'); range.type = 'range'; range.className = 'lm-range'; range.min = '0'; range.max = '100'; range.step = '1';
    range.setAttribute('aria-label', 'Затемнение плашек');
    const scale = document.createElement('div'); scale.className = 'design-dark-scale';
    const lo = document.createElement('span'); lo.textContent = 'Прозрачнее'; const hi = document.createElement('span'); hi.textContent = 'Темнее';
    scale.append(lo, hi);
    const setDark = (value, save) => {
      panelDark = Math.min(100, Math.max(0, Math.round(value)));
      range.value = String(panelDark); range.style.setProperty('--v', String(panelDark)); darkValue.textContent = panelDark + '%';
      applyDark();
      if (save) { try { localStorage.setItem(DARK_STORAGE, String(panelDark)); } catch (_) {} }
    };
    range.addEventListener('input', () => setDark(Number(range.value), true));
    reset.addEventListener('click', () => setDark(DARK_DEFAULT, true));
    setDark(panelDark, false);
    dark.append(darkHead, range, scale);
    section.append(title, hint, list, dark);
    const anchor = (document.getElementById('island-themes') || {}).closest && document.getElementById('island-themes').closest('.island-theme')
      || panel.querySelector('.settings-note') || panel.querySelector('#save-shortcuts');
    const appearance = document.getElementById('settings-appearance');
    if (appearance) appearance.prepend(section); else if (anchor) anchor.before(section); else panel.append(section);
    list.addEventListener('click', event => { const button = event.target.closest('.swatch'); if (button) choose(button.dataset.design); });
    els = {list};
    paint();
  }

  function paint() {
    if (!els) return;
    els.list.querySelectorAll('.swatch').forEach(button => {
      const on = button.dataset.design === design;
      button.classList.toggle('active', on); button.setAttribute('aria-checked', String(on));
    });
  }

  async function choose(next) {
    if (!DESIGNS[next] || next === design) return;
    design = next;
    try { localStorage.setItem(STORAGE, design); } catch (_) {}
    apply();
    const a = api(); if (!a) return;
    try {
      if (a.set_design) await a.set_design(design);
      // остров следует за дизайном: меняем его схему и показываем остров, чтобы результат был виден сразу
      if (a.get_island_theme && a.set_island_theme) {
        const current = (await a.get_island_theme()) || {};
        await a.set_island_theme({preset: DESIGNS[design].island, custom: current.custom || {}});
        if (a.preview_island) await a.preview_island();
      }
    } catch (_) {}
  }

  async function sync() {
    const a = api(); if (!a || !a.get_design) return;
    try {
      const value = await a.get_design();
      if (value && DESIGNS[value.design] && value.design !== design) {
        design = value.design; try { localStorage.setItem(STORAGE, design); } catch (_) {}
        apply();
      }
    } catch (_) {}
  }

  function init() { applyDark(); loadCss(); build(); apply(); sync(); ['settings-button', 'keys-button'].forEach(id => { const el = document.getElementById(id); if (el) el.addEventListener('click', sync); }); }

  if (document.documentElement) document.documentElement.dataset.design = design;
  window.addEventListener('liquidready', init);
  window.addEventListener('DOMContentLoaded', () => { applyDark(); loadCss(); if (document.body) document.body.dataset.design = design; });
  window.addEventListener('load', apply);
})();
