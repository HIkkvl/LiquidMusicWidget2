'use strict';
/* Цветовые схемы острова: 6 цветов от тёмного к светлому (как палитра авроры в liquid-glass.js).
   Подключается и в настройках (index.html), и на странице острова (island.html). */
window.IslandPalettes = (() => {
  const PRESETS = {
    aurora:   [[.17, .04, .11], [.47, .13, .52], [.78, .33, .70], [1, .45, .52], [1, .68, .82], [1, .89, .93]],
    ocean:    [[.02, .07, .16], [.06, .22, .45], [.12, .45, .78], [.20, .70, .90], [.55, .88, .97], [.88, .97, 1]],
    sunset:   [[.16, .04, .06], [.45, .10, .18], [.80, .22, .20], [1, .50, .22], [1, .74, .40], [1, .93, .78]],
    mint:     [[.02, .12, .10], [.05, .33, .30], [.12, .60, .50], [.30, .85, .62], [.66, .96, .80], [.92, 1, .95]],
    violet:   [[.08, .04, .18], [.25, .12, .50], [.48, .28, .85], [.68, .48, .98], [.82, .72, 1], [.94, .90, 1]],
    graphite: [[.05, .05, .06], [.16, .16, .19], [.34, .34, .40], [.55, .55, .62], [.78, .78, .84], [.95, .95, .98]],
    abyss:    [[.003, .006, .012], [.010, .028, .058], [.030, .085, .150], [.15, .20, .28], [.36, .45, .56], [.64, .73, .83]],
    neon:     [[.01, .01, .04], [.04, .04, .16], [.12, .10, .48], [.34, .30, .95], [.63, .59, 1], [.90, .88, 1]],
    grain:    [[.01, .01, .01], [.05, .05, .05], [.16, .16, .16], [.38, .38, .38], [.68, .68, .68], [.93, .93, .93]]
  };
  // дополнительные параметры шейдера: светящиеся контуры (r, g, b, сила), свечение кромки, оттенок стекла
  const LOOKS = {grain: {line: [.95, .95, .95, .30], glow: [.90, .90, .90], tint: [1, 1, 1], tintLum: [0, 0, 0, 0], range: 1}, abyss: {line: [.20, .72, 1, .62], glow: [.45, .80, 1], tint: [.2, 1, .6], tintLum: [.5, 2, 3.8, 1], range: .8}};
  const NAMES = {aurora: 'Аврора', ocean: 'Океан', sunset: 'Закат', mint: 'Мята', violet: 'Фиалка', graphite: 'Графит', abyss: 'Бездна', neon: 'Неон', grain: 'Зерно', custom: 'Свои'};
  const DEFAULT = {preset: 'aurora', custom: {dark: '#2b0a1d', accent: '#ff5ea8', light: '#ffe4f1'}};
  const isHex = v => typeof v === 'string' && /^#[0-9a-f]{6}$/i.test(v);
  const hex = h => [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16) / 255);
  const mix = (a, b, t) => a.map((v, i) => v + (b[i] - v) * t);
  function fromCustom(c) {
    const d = hex(isHex(c.dark) ? c.dark : DEFAULT.custom.dark), a = hex(isHex(c.accent) ? c.accent : DEFAULT.custom.accent),
          l = hex(isHex(c.light) ? c.light : DEFAULT.custom.light);
    return [d, mix(d, a, .42), a, mix(a, l, .38), mix(a, l, .72), l];
  }
  function resolve(theme) {
    const t = theme || {};
    if (t.preset === 'custom') return fromCustom(t.custom || {});
    return PRESETS[t.preset] || PRESETS.aurora;
  }
  function look(theme) {
    const palette = resolve(theme), special = LOOKS[theme && theme.preset];
    return special || {line: [0, 0, 0, 0], glow: palette[4], tint: [1, 1, 1], tintLum: [0, 0, 0, 0], range: 1};
  }
  const css = color => color.map(v => Math.round(Math.max(0, Math.min(1, v)) * 255)).join(' ');   // "255 156 203" для rgb(var(--p3) / .6)
  return {PRESETS, NAMES, DEFAULT, resolve, look, css};
})();
