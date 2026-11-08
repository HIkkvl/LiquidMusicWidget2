'use strict';
/* Кнопка «В Telegram» (на месте кнопки «Стоп») и вкладка «Telegram» в настройках.
   Отправляется только карточка трека: название, артист, альбом, обложка и ссылка. Подключается из C#. */
(() => {
  if (location.origin !== 'https://liquid.local') return;
  const api = () => window.liquid && window.liquid.api;
  let state = {configured: false}, busy = false, els = null;
  const css = `
    #telegram-button[data-state="sending"]{opacity:.55;pointer-events:none}
    #telegram-button[data-state="sent"]{border-color:rgba(120,230,170,.65)}
    #telegram-button[data-state="sent"] svg{color:#9be8b4}
    #telegram-button[data-state="error"]{border-color:rgba(255,150,130,.7)}
    #telegram-button[data-state="error"] svg{color:#ffb4a0}
    body[data-room="guest"] #telegram-button{pointer-events:auto}
    .tg-card{display:flex;flex-direction:column;gap:10px;padding:14px;border-radius:16px;
      background:linear-gradient(145deg,rgba(255,255,255,.10),rgba(255,255,255,.03));
      box-shadow:inset 0 1px 0 rgba(255,255,255,.22),inset 0 0 0 .5px rgba(255,255,255,.10)}
    .tg-card h3{margin:0;font-size:13px;font-weight:600;color:var(--text,#f5f8ff)}
    .tg-card p{margin:0;font-size:11.5px;line-height:1.5;color:var(--text-2,rgba(235,242,255,.66))}
    .tg-card label{display:flex;flex-direction:column;gap:5px;font-size:11.5px;color:var(--text-2,rgba(235,242,255,.66))}
    .tg-card input{width:100%;padding:9px 11px;border-radius:11px;border:1px solid var(--stroke,rgba(255,255,255,.16));
      background:rgba(255,255,255,.06);color:var(--text,#f5f8ff);font:inherit;box-sizing:border-box}
    .tg-card input:focus-visible{outline:2px solid rgba(255,255,255,.55);outline-offset:1px}
    .tg-row{display:flex;gap:8px}
    .tg-btn{flex:1;padding:10px 14px;border:0;border-radius:12px;font-size:13px;font-weight:600;color:var(--on-accent,#fff);cursor:pointer;
      background:var(--accent,linear-gradient(120deg,#ff9ccb,#d65fb0));box-shadow:inset 0 1px 1px rgba(255,255,255,.45)}
    .tg-btn.quiet{flex:none;font-weight:500;color:var(--text,#f5f8ff);background:rgba(255,255,255,.09);box-shadow:inset 0 0 0 1px rgba(255,255,255,.14)}
    .tg-btn:disabled{opacity:.5;cursor:default}
    .tg-status{min-height:1.3em}.tg-status.ok{color:#9be8b4}.tg-status.bad{color:#ffb4a0}`;
  const el = (tag, props = {}, ...kids) => { const n = Object.assign(document.createElement(tag), props); n.append(...kids); return n; };
  const note = message => { if (typeof window.toast === 'function') window.toast(message); };
  const say = (text, kind) => { if (!els) return; els.status.textContent = text || ''; els.status.className = 'tg-p tg-status' + (kind ? ' ' + kind : ''); };

  async function call(method, ...args) {
    const a = api(); if (!a || !a[method]) throw new Error('Запустите виджет через приложение.');
    const result = await a[method](...args);
    if (result && result.ok === false) throw new Error(result.error || 'Не удалось выполнить действие');
    return result;
  }
  function openSettings() {
    const open = document.getElementById('settings-button'); if (open) open.click();
    setTimeout(() => { const tab = document.querySelector('[data-settings-tab="telegram"]'); if (tab) tab.click(); }, 120);
  }

  async function send(button) {
    if (busy) return;
    try { state = await call('get_telegram'); } catch (_) {}
    if (!state.configured) { note('Сначала подключите бота: Настройки → Telegram'); openSettings(); return; }
    busy = true; button.dataset.state = 'sending';
    try { await call('send_telegram'); button.dataset.state = 'sent'; note('Трек отправлен в Telegram'); }
    catch (error) { button.dataset.state = 'error'; note(error.message); }
    finally { busy = false; setTimeout(() => { delete button.dataset.state; }, 2200); }
  }

  function paint() {
    if (!els) return;
    els.chat.value = state.chat_id || '';
    els.token.placeholder = state.has_token ? '•••• •••• ' + (state.token_tail || '') + ' (оставьте пустым, чтобы не менять)' : '123456789:AA…';
    els.clear.hidden = !state.has_token;
    say(state.configured ? 'Бот подключён. Нажмите кнопку «В Telegram» рядом с воспроизведением.' : '', state.configured ? 'ok' : '');
  }

  function build() {
    const panel = document.getElementById('settings-telegram');
    if (!panel || panel.dataset.ready) return !!panel;
    panel.dataset.ready = '1';
    document.head.append(el('style', {textContent: css}));
    const token = el('input', {type: 'password', autocomplete: 'off', spellcheck: false});
    const chat = el('input', {type: 'text', inputMode: 'numeric', autocomplete: 'off', spellcheck: false, placeholder: '123456789'});
    const save = el('button', {type: 'button', className: 'tg-btn', textContent: 'Сохранить'});
    const clear = el('button', {type: 'button', className: 'tg-btn quiet', textContent: 'Удалить токен'});
    const status = el('p', {className: 'tg-p tg-status', role: 'status'});
    panel.append(el('div', {className: 'tg-card'},
      el('h3', {textContent: 'Отправка треков в Telegram'}),
      el('p', {textContent: 'Кнопка рядом с воспроизведением отправляет вашему боту карточку трека: название, артиста, альбом, обложку и ссылку на трек в сервисе. Аудиофайлы не отправляются.'}),
      el('p', {textContent: '1) Создайте бота у @BotFather и скопируйте токен. 2) Откройте своего бота и нажмите Start. 3) Узнайте свой Chat ID (например, у @userinfobot) и вставьте ниже.'}),
      el('label', {}, 'Токен бота', token), el('label', {}, 'Chat ID', chat),
      el('div', {className: 'tg-row'}, save, clear), status));
    els = {token, chat, save, clear, status};
    save.addEventListener('click', async () => {
      save.disabled = true; say('');
      try { state = await call('set_telegram', {token: token.value.trim(), chat_id: chat.value.trim()}); token.value = ''; paint(); say('Сохранено. Токен хранится зашифрованным.', 'ok'); }
      catch (error) { say(error.message, 'bad'); }
      finally { save.disabled = false; }
    });
    clear.addEventListener('click', async () => {
      try { state = await call('set_telegram', {clear_token: true}); token.value = ''; paint(); say('Токен удалён.', 'ok'); } catch (error) { say(error.message, 'bad'); }
    });
    ['settings-button'].forEach(id => { const b = document.getElementById(id); if (b) b.addEventListener('click', load); });
    return true;
  }
  async function load() { try { state = await call('get_telegram'); paint(); } catch (_) {} }

  function init() {
    const button = document.getElementById('telegram-button');
    if (button && !button.dataset.bound) { button.dataset.bound = '1'; button.addEventListener('click', () => send(button)); }
    if (build()) load();
  }
  window.addEventListener('liquidready', init);
  if (document.readyState !== 'loading' && window.liquid) init();
})();
