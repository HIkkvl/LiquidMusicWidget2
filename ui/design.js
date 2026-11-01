'use strict';
/* Смена дизайна: «Аврора» (розово-фиолетовая) и «Бездна» (тёмная, с тонкими голубыми контурами).
   Подключается из C# (AddScriptToExecuteOnDocumentCreatedAsync), поэтому index.html и app.js менять не нужно.
   Дизайн меняет: фон и стекло главного окна (палитра и линии шейдера), акценты интерфейса (abyss.css) и цвета острова. */
(() => {
  if (location.origin !== 'https://liquid.local') return;
  const api = () => window.liquid && window.liquid.api;
  const STORAGE = 'liquid-design';
  const DESIGNS = {
    aurora: {
      name: 'Аврора', island: 'aurora',
      preview: [[.17, .04, .11], [.47, .13, .52], [.78, .33, .70], [1, .45, .52], [1, .68, .82], [1, .89, .93]]
    },
    abyss: {
      name: 'Бездна', island: 'abyss',
      preview: [[.003, .006, .012], [.010, .028, .058], [.030, .085, .150], [.15, .20, .28], [.36, .45, .56], [.64, .73, .83]],
      look: {line: [.20, .72, 1, .62], glow: [.45, .80, 1], tint: [.2, 1, .6], tintLum: [.5, 2, 3.8, 1], range: .8}
    }
  };
  let design = 'aurora', requestedPalette = null, wrapped = false, els = null;

  try { const saved = localStorage.getItem(STORAGE); if (saved && DESIGNS[saved]) design = saved; } catch (_) {}

  function loadCss() {
    if (!document.head || document.querySelector('link[href$="abyss.css"]')) return;   // скрипт стартует до разбора страницы
    const link = document.createElement('link'); link.rel = 'stylesheet'; link.href = 'abyss.css'; document.head.append(link);
  }

  // app.js выставляет палитру по сервису (Яндекс / YouTube): пока выбрана «Бездна», эти вызовы запоминаем, но не применяем
  function wrapEngine() {
    const engine = window.LiquidGlass;
    if (wrapped || !engine || !engine.setPalette || !engine.setLook) return !!wrapped;
    const original = engine.setPalette.bind(engine);
    engine.setPalette = value => { requestedPalette = value; if (design === 'aurora') original(value); };
    engine.__applyDesign = () => {
      if (design === 'abyss') { original(DESIGNS.abyss.preview); engine.setLook(DESIGNS.abyss.look); }
      else { engine.setLook(null); if (requestedPalette) original(requestedPalette); }
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
      + '.design-settings .design-hint{margin:0;font-size:11.5px;line-height:1.45;color:var(--text-2,rgba(235,242,255,.66))}';
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
    section.append(title, hint, list);
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

  function init() { loadCss(); build(); apply(); sync(); ['settings-button', 'keys-button'].forEach(id => { const el = document.getElementById(id); if (el) el.addEventListener('click', sync); }); }

  if (document.documentElement) document.documentElement.dataset.design = design;
  window.addEventListener('liquidready', init);
  window.addEventListener('DOMContentLoaded', () => { loadCss(); if (document.body) document.body.dataset.design = design; });
  window.addEventListener('load', apply);
})();
