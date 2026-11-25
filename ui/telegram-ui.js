'use strict';
/* Кнопка «В Telegram» (на месте кнопки «Стоп») и вкладка «Telegram» в настройках.
   Отправляется только карточка трека: название, артист, альбом, обложка и ссылка.
   Вкладка также включает управление плеером из бота (команды из вашего чата). Подключается из C#. */
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
    .tg-card label.preference{flex-direction:row;gap:10px;font-size:14px;color:var(--text,#f5f8ff)}
    .tg-card input:not(.switch){width:100%;padding:9px 11px;border-radius:11px;border:1px solid var(--stroke,rgba(255,255,255,.16));
      background:rgba(255,255,255,.06);color:var(--text,#f5f8ff);font:inherit;box-sizing:border-box}
    .tg-card input:not(.switch):focus-visible{outline:2px solid rgba(255,255,255,.55);outline-offset:1px}
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

  // ---- управление плеером из бота ----
  function paintControl(value) {
    if (!els || !value) return;
    const live = value.status || {}, enabled = !!value.enabled;
    els.control.checked = enabled;
    if (typeof value.proxy === 'string' && document.activeElement !== els.proxy) els.proxy.value = value.proxy;
    let text = 'Выключено', kind = '';
    if (enabled) {
      switch (live.status) {
        case 'connected': text = 'Бот на связи. Откройте его в Telegram и напишите /start.'; kind = 'ok'; break;
        case 'no_config': text = 'Сначала сохраните токен бота и Chat ID.'; kind = 'bad'; break;
        case 'auth': case 'conflict': case 'error': text = live.detail || 'Не удалось подключиться к Telegram.'; kind = 'bad'; break;
        default: text = 'Подключение…';
      }
    }
    els.controlStatus.textContent = text;
    els.controlStatus.className = 'tg-p tg-status' + (kind ? ' ' + kind : '');
  }
  async function loadControl() { try { paintControl(await call('get_telegram_control')); } catch (_) {} }

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

    const proxy = el('input', {type: 'text', autocomplete: 'off', spellcheck: false, placeholder: 'socks5://127.0.0.1:1080'});
    const proxySave = el('button', {type: 'button', className: 'tg-btn', textContent: 'Сохранить прокси'});
    const controlRow = el('label', {className: 'preference'});
    controlRow.innerHTML = '<svg><use href="#i-telegram"/></svg><span>Управлять плеером из Telegram</span><input type="checkbox" class="switch"><span class="switch-track" aria-hidden="true"></span>';
    const control = controlRow.querySelector('input');
    const controlStatus = el('p', {className: 'tg-p tg-status', role: 'status'});
    panel.append(el('div', {className: 'tg-card'},
      el('h3', {textContent: 'Управление из Telegram'}),
      el('p', {textContent: 'Бот принимает команды только из вашего чата (Chat ID выше): пауза, следующий трек, громкость, лайк и поиск трека. Напишите ему /help. Нужен интернет; открывать порты не требуется.'}),
      el('label', {}, 'Прокси (если Telegram без VPN не открывается)', proxy),
      el('div', {className: 'tg-row'}, proxySave),
      el('p', {textContent: 'Например socks5://127.0.0.1:1080 или http://127.0.0.1:8080. Эти данные есть в Telegram: Настройки → Продвинутые → Тип соединения → Использовать прокси. Подойдёт SOCKS5 или HTTP; MTProto не подходит. Оставьте пустым, чтобы работать напрямую.'}),
      controlRow, controlStatus));

    els = {token, chat, save, clear, status, control, controlStatus, proxy};
    save.addEventListener('click', async () => {
      save.disabled = true; say('');
      try { state = await call('set_telegram', {token: token.value.trim(), chat_id: chat.value.trim()}); token.value = ''; paint(); say('Сохранено. Токен хранится зашифрованным.', 'ok'); }
      catch (error) { say(error.message, 'bad'); }
      finally { save.disabled = false; }
    });
    clear.addEventListener('click', async () => {
      try { state = await call('set_telegram', {clear_token: true}); token.value = ''; paint(); say('Токен удалён.', 'ok'); loadControl(); } catch (error) { say(error.message, 'bad'); }
    });
    proxySave.addEventListener('click', async () => {
      proxySave.disabled = true;
      try { paintControl(await call('set_telegram_proxy', proxy.value.trim())); controlStatus.textContent = 'Прокси сохранён.'; controlStatus.className = 'tg-p tg-status ok'; setTimeout(loadControl, 2500); }
      catch (error) { controlStatus.textContent = error.message; controlStatus.className = 'tg-p tg-status bad'; }
      finally { proxySave.disabled = false; }
    });
    control.addEventListener('change', async () => {
      const wanted = control.checked; control.disabled = true;
      try { paintControl(await call('set_telegram_control', wanted)); }
      catch (error) {
        control.checked = !wanted;
        controlStatus.textContent = error.message; controlStatus.className = 'tg-p tg-status bad';
      }
      finally { control.disabled = false; setTimeout(loadControl, 1500); }
    });
    ['settings-button'].forEach(id => { const b = document.getElementById(id); if (b) b.addEventListener('click', () => { load(); loadControl(); }); });
    return true;
  }
  async function load() { try { state = await call('get_telegram'); paint(); } catch (_) {} }

  function init() {
    const button = document.getElementById('telegram-button');
    if (button && !button.dataset.bound) { button.dataset.bound = '1'; button.addEventListener('click', () => send(button)); }
    if (build()) { load(); loadControl(); }
  }
  window.addEventListener('liquidready', init);
  if (document.readyState !== 'loading' && window.liquid) init();
})();
