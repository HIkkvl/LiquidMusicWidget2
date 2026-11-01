'use strict';
/* Настройка «Цвета острова»: выбор схемы (пресеты + «Свои»), сохранение через C# и показ острова для проверки. */
(() => {
  const P = window.IslandPalettes;
  if (!P) return;
  const $ = id => document.getElementById(id);
  const list = $('island-themes'), custom = $('island-custom'), status = $('theme-status');
  if (!list || !custom) return;
  const inputs = {dark: $('ic-dark'), accent: $('ic-accent'), light: $('ic-light')};
  const api = () => window.liquid && window.liquid.api;
  let theme = {preset: P.DEFAULT.preset, custom: {...P.DEFAULT.custom}}, saveTimer = 0, statusTimer = 0;

  const rgb = color => 'rgb(' + P.css(color).replace(/ /g, ',') + ')';
  const gradient = palette => 'linear-gradient(120deg,' + palette.map((c, i) => rgb(c) + ' ' + i * 20 + '%').join(',') + ')';
  function say(text, bad) {
    status.textContent = text; status.classList.toggle('bad', !!bad);
    clearTimeout(statusTimer); statusTimer = setTimeout(() => { status.textContent = ''; }, 2500);
  }
  function build() {
    list.textContent = '';
    for (const name of [...Object.keys(P.PRESETS), 'custom']) {
      const button = document.createElement('button');
      button.type = 'button'; button.className = 'swatch'; button.dataset.preset = name; button.setAttribute('role', 'radio');
      button.title = P.NAMES[name];
      const sample = document.createElement('i'), label = document.createElement('span');
      label.textContent = P.NAMES[name];
      button.append(sample, label); list.append(button);
    }
  }
  function paint() {
    list.querySelectorAll('.swatch').forEach(button => {
      const name = button.dataset.preset, on = name === theme.preset;
      button.classList.toggle('active', on); button.setAttribute('aria-checked', String(on));
      button.firstChild.style.background = gradient(P.resolve(name === 'custom' ? {preset: 'custom', custom: theme.custom} : {preset: name}));
    });
    custom.hidden = theme.preset !== 'custom';
    for (const key of Object.keys(inputs)) inputs[key].value = theme.custom[key];
  }
  async function load() {
    try {
      const a = api(); if (!a || !a.get_island_theme) return;
      const value = await a.get_island_theme();
      if (value && value.preset) { theme = {preset: value.preset, custom: {...P.DEFAULT.custom, ...(value.custom || {})}}; paint(); }
    } catch (_) {}
  }
  async function save(show) {
    try {
      const a = api(); if (!a) throw new Error('Запустите виджет через START_WIDGET.bat');
      const result = await a.set_island_theme(theme);
      if (!result || !result.ok) throw new Error((result && result.error) || 'Не удалось сохранить');
      say('Сохранено');
      if (show) await a.preview_island();
    } catch (error) { say(error.message, true); }
  }
  list.addEventListener('click', event => {
    const button = event.target.closest('.swatch'); if (!button) return;
    theme.preset = button.dataset.preset; paint(); save(true);
  });
  for (const key of Object.keys(inputs)) inputs[key].addEventListener('input', () => {
    theme.custom[key] = inputs[key].value; theme.preset = 'custom'; paint();
    clearTimeout(saveTimer); saveTimer = setTimeout(() => save(true), 350);   // пока тянут ползунок цвета, не дёргаем остров
  });
  $('island-preview').addEventListener('click', async () => {
    try { const a = api(); if (a) await a.preview_island(); } catch (_) {}
  });
  build(); paint();
  ['settings-button', 'keys-button'].forEach(id => { const el = $(id); if (el) el.addEventListener('click', load); });
  window.addEventListener('liquidready', load);
  load();
})();
