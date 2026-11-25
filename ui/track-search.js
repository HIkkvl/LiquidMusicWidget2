'use strict';
/* Поиск трека по сочетанию клавиш (по умолчанию Ctrl+F, меняется в «Настройки → Управление» → «Поиск трека»).
   Работает, когда окно виджета активно. Enter — найти трек в выбранном сервисе и включить, Esc — закрыть.
   Подключается из C# (AddScriptToExecuteOnDocumentCreatedAsync). */
(() => {
  if (location.origin !== 'https://liquid.local') return;
  const api = () => window.liquid && window.liquid.api;
  let dialog = null, input = null, go = null, busy = false;

  const css = `
    .track-search{width:min(440px,calc(100vw - 32px));padding:0;border:0;border-radius:22px;color:var(--text,#f5f8ff);background:transparent;overflow:visible}
    .track-search::backdrop{background:rgba(0,0,0,.66)}   /* затемнение всего, что за окном поиска */
    .track-search form{position:relative;display:flex;flex-direction:column;gap:12px;padding:18px;border-radius:20px;
      background:color-mix(in srgb,var(--violet,#a85bd6) 9%,#100d16);border:1px solid rgba(255,255,255,.10);
      box-shadow:0 20px 50px rgba(0,0,0,.55)}
    .track-search h2{margin:0;padding-right:36px;font-size:17px;font-weight:600}
    .ts-close{position:absolute;top:12px;right:12px;display:grid;place-items:center;width:30px;height:30px;padding:0;border:0;border-radius:50%;
      color:var(--text-2,rgba(235,242,255,.66));background:rgba(255,255,255,.07);cursor:pointer;transition:background .2s,color .2s}
    .ts-close:hover{background:rgba(255,255,255,.16);color:var(--text,#f5f8ff)}
    .ts-close svg{width:15px;height:15px;fill:none;stroke:currentColor;stroke-width:2;stroke-linecap:round}
    .track-search input{width:100%;padding:12px 14px;border-radius:14px;border:1px solid var(--stroke,rgba(255,255,255,.16));
      background:rgba(255,255,255,.06);color:inherit;font:inherit;font-size:15px;box-sizing:border-box}
    .track-search input:focus-visible{outline:2px solid rgba(255,255,255,.6);outline-offset:1px}
    .ts-row{display:flex;gap:8px}
    .ts-go,.ts-cancel{padding:11px 16px;border:0;border-radius:13px;font-size:13.5px;font-weight:600;cursor:pointer;transition:filter .2s,transform .2s}
    .ts-go{flex:1;color:var(--on-accent,#fff);background:var(--accent,linear-gradient(120deg,#ff9ccb,#d65fb0));box-shadow:inset 0 1px 1px rgba(255,255,255,.45)}
    .ts-cancel{color:var(--text,#f5f8ff);background:rgba(255,255,255,.09);box-shadow:inset 0 0 0 1px rgba(255,255,255,.14)}
    .ts-go:hover:not(:disabled){filter:brightness(1.1)}.ts-go:active:not(:disabled){transform:scale(.98)}.ts-go:disabled{opacity:.55;cursor:default}
    .ts-hint{margin:0;font-size:11.5px;color:var(--text-2,rgba(235,242,255,.66))}`;

  const note = message => { if (typeof window.toast === 'function') window.toast(message); };
  const el = (tag, props = {}, ...kids) => { const n = Object.assign(document.createElement(tag), props); n.append(...kids); return n; };

  function build() {
    if (dialog) return;
    document.head.append(el('style', {textContent: css}));
    input = el('input', {type: 'search', maxLength: 200, autocomplete: 'off', spellcheck: false, placeholder: 'Например: madk1d сердце'});
    input.setAttribute('aria-label', 'Исполнитель и трек');
    go = el('button', {type: 'submit', className: 'ts-go', textContent: 'Найти и включить'});
    const cancel = el('button', {type: 'button', className: 'ts-cancel', textContent: 'Закрыть'});
    const close = el('button', {type: 'button', className: 'ts-close', title: 'Закрыть (Esc или повторный Ctrl+F)'});
    close.setAttribute('aria-label', 'Закрыть поиск');
    close.innerHTML = '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3 3l10 10M13 3L3 13"/></svg>';
    const form = el('form', {}, close, el('h2', {textContent: 'Найти трек'}), input, el('div', {className: 'ts-row'}, go, cancel),
      el('p', {className: 'ts-hint', textContent: 'Введите исполнителя и название. Enter — включить, Esc или повторный Ctrl+F — закрыть.'}));
    dialog = el('dialog', {className: 'track-search'}, form);
    document.body.append(dialog);
    cancel.addEventListener('click', () => dialog.close());
    close.addEventListener('click', () => dialog.close());
    form.addEventListener('submit', async event => {
      event.preventDefault();
      const query = input.value.trim();
      if (!query || busy) return;
      const a = api(); if (!a) { note('Запустите виджет через приложение.'); return; }
      busy = true; go.disabled = true; go.textContent = 'Ищу…';
      try {
        const result = await a.command('search_play', query, null);
        if (result && result.ok === false) note(result.error || 'Не удалось найти трек');
        else { note('Трек найден'); dialog.close(); input.value = ''; }
      } catch (error) { note('Не удалось выполнить поиск: ' + error.message); }
      finally { busy = false; go.disabled = false; go.textContent = 'Найти и включить'; }
    });
  }

  // event.code -> имя клавиши из списка настроек (A–Z, F1–F12, Num0–Num9, Space, Home…)
  function keyName(event) {
    const c = event.code;
    if (/^Key[A-Z]$/.test(c)) return c.slice(3);
    if (/^F([1-9]|1[0-2])$/.test(c)) return c;
    if (/^Numpad[0-9]$/.test(c)) return 'Num' + c.slice(6);
    return ['Space', 'Home', 'End', 'PageUp', 'PageDown', 'Insert'].includes(c) ? c : '';
  }
  const binding = () => (window.configuration && window.configuration.bindings && window.configuration.bindings.search) || {key: 'F', ctrl: true, alt: false, shift: false};

  function open() { build(); if (!dialog.open) dialog.showModal(); input.focus(); input.select(); }

  document.addEventListener('keydown', event => {
    if (event.repeat || event.metaKey) return;
    const b = binding();
    if (!b.key || keyName(event) !== b.key) return;
    if (!!b.ctrl !== event.ctrlKey || !!b.alt !== event.altKey || !!b.shift !== event.shiftKey) return;
    const tag = (document.activeElement && document.activeElement.tagName) || '';
    if (!b.ctrl && !b.alt && ['INPUT', 'TEXTAREA', 'SELECT'].includes(tag)) return;   // обычная клавиша не должна мешать вводу текста
    const settings = document.getElementById('settings-dialog');
    if (settings && settings.open) return;
    event.preventDefault(); event.stopPropagation();
    if (dialog && dialog.open) dialog.close(); else open();   // повторное нажатие закрывает поиск
  }, true);
})();
