'use strict';
/* Вкладка «Комната» в настройках: слушать музыку вдвоём. Подключается из C# (AddScriptToExecuteOnDocumentCreatedAsync).
   Лидер создаёт комнату и управляет треками; гость вводит приглашение и слушает тот же трек в своём сервисе.
   Звук между людьми не передаётся: передаются только название трека и состояние (играет / пауза). */
(() => {
  if (location.origin !== 'https://liquid.local') return;
  const api = () => window.liquid && window.liquid.api;
  const NAME_KEY = 'liquid-room-name';
  let state = {role: 'none'}, busy = false, els = null, badge = null, timer = 0;

  const css = `
    .room-card{display:flex;flex-direction:column;gap:10px;padding:14px;border-radius:16px;
      background:linear-gradient(145deg,rgba(255,255,255,.10),rgba(255,255,255,.03));
      box-shadow:inset 0 1px 0 rgba(255,255,255,.22),inset 0 0 0 .5px rgba(255,255,255,.10)}
    .room-card h3{margin:0;font-size:13px;font-weight:600;color:var(--text,#f5f8ff)}
    .room-card p{margin:0;font-size:11.5px;line-height:1.45;color:var(--text-2,rgba(235,242,255,.66))}
    .room-card[hidden],.room-card [hidden]{display:none}
    .room-card input[type=text]{width:100%;padding:9px 11px;border-radius:11px;border:1px solid var(--stroke,rgba(255,255,255,.16));
      background:rgba(255,255,255,.06);color:var(--text,#f5f8ff);font:inherit;box-sizing:border-box}
    .room-card input[type=text]:focus-visible{outline:2px solid rgba(255,255,255,.55);outline-offset:1px}
    .room-btn{padding:10px 14px;border:0;border-radius:12px;font-size:13px;font-weight:600;color:var(--on-accent,#fff);cursor:pointer;
      background:var(--accent,linear-gradient(120deg,#ff9ccb,#d65fb0));box-shadow:inset 0 1px 1px rgba(255,255,255,.45);transition:transform .2s,filter .2s}
    .room-btn:hover:not(:disabled){filter:brightness(1.1)}.room-btn:active:not(:disabled){transform:scale(.98)}
    .room-btn:disabled{opacity:.5;cursor:default}
    .room-btn.quiet{font-weight:500;color:var(--text,#f5f8ff);background:rgba(255,255,255,.09);box-shadow:inset 0 0 0 1px rgba(255,255,255,.14)}
    .room-code{font:600 26px/1.1 "Cascadia Mono",Consolas,monospace;letter-spacing:.18em;text-align:center;padding:8px 0;color:var(--text,#f5f8ff)}
    .room-invite{display:flex;gap:8px;align-items:center}
    .room-invite code{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;padding:8px 10px;border-radius:10px;
      background:rgba(255,255,255,.06);font:12px "Cascadia Mono",Consolas,monospace;color:var(--text,#f5f8ff)}
    .room-invite button{padding:7px 11px;border:0;border-radius:10px;font-size:12px;color:var(--text-2,rgba(235,242,255,.66));background:rgba(255,255,255,.09);cursor:pointer}
    .room-invite button:hover{background:rgba(255,255,255,.18);color:var(--text,#f5f8ff)}
    .room-divider{height:1px;background:rgba(255,255,255,.10);margin:2px 0}
    .room-error{color:#ffb4a0}
    .header .brand{min-width:0;overflow:hidden}.header .brand strong{overflow:hidden;text-overflow:ellipsis}
    .room-badge{display:none;flex:none;max-width:112px;align-items:center;gap:6px;padding:4px 10px;margin-right:4px;border-radius:999px;font-size:11px;white-space:nowrap;
      color:var(--text,#f5f8ff);background:rgba(255,255,255,.10);box-shadow:inset 0 0 0 .5px rgba(255,255,255,.22)}
    .room-badge span{overflow:hidden;text-overflow:ellipsis}
    .room-badge i{flex:none;width:7px;height:7px;border-radius:50%;background:var(--teal,#5ef0d4);box-shadow:0 0 8px var(--teal,#5ef0d4)}
    body[data-room="leader"] .room-badge,body[data-room="guest"] .room-badge{display:inline-flex}
    /* гость только слушает: кнопки управления треками и список выбора недоступны */
    body[data-room="guest"] :is(.transport,.genres-section){pointer-events:none;opacity:.5}`;

  const el = (tag, props = {}, ...children) => {
    const node = Object.assign(document.createElement(tag), props);
    node.append(...children);
    return node;
  };
  const say = text => { if (els) { els.error.textContent = text || ''; els.error.hidden = !text; } };

  async function call(method, ...args) {
    const a = api(); if (!a || !a[method]) throw new Error('Запустите виджет через приложение.');
    const result = await a[method](...args);
    if (result && result.ok === false) throw new Error(result.error || 'Не удалось выполнить действие');
    return result;
  }
  async function act(button, work) {
    if (busy) return;
    busy = true; say(''); button.disabled = true;
    try { state = await work() || state; } catch (error) { say(error.message); }
    finally { busy = false; button.disabled = false; paint(); }
  }
  const myName = () => (els.name.value.trim() || 'Гость').slice(0, 24);

  function build() {
    const panel = document.getElementById('settings-room');
    if (!panel || panel.dataset.ready) return !!panel;
    panel.dataset.ready = '1';
    const style = el('style', {textContent: css}); document.head.append(style);

    const name = el('input', {type: 'text', maxLength: 24, placeholder: 'Ваше имя в комнате', autocomplete: 'off', spellcheck: false});
    try { name.value = localStorage.getItem(NAME_KEY) || ''; } catch (_) {}
    name.addEventListener('change', () => { try { localStorage.setItem(NAME_KEY, name.value.trim()); } catch (_) {} });
    const create = el('button', {type: 'button', className: 'room-btn', textContent: 'Создать комнату'});
    const invite = el('input', {type: 'text', placeholder: '192.168.1.5:8788/123456', autocomplete: 'off', spellcheck: false});
    const join = el('button', {type: 'button', className: 'room-btn quiet', textContent: 'Подключиться'});
    const idle = el('div', {className: 'room-card', id: 'room-idle'},
      el('h3', {textContent: 'Слушать вместе'}),
      el('p', {textContent: 'Лидер включает музыку и управляет треками. У гостя тот же трек находится и запускается в его собственном сервисе, звук между вами не передаётся.'}),
      name, create, el('div', {className: 'room-divider'}),
      el('p', {textContent: 'У вас есть приглашение от лидера? Вставьте его сюда:'}), invite, join);

    const code = el('div', {className: 'room-code'});
    const invites = el('div', {style: 'display:flex;flex-direction:column;gap:6px'});
    const peer = el('p');
    const close = el('button', {type: 'button', className: 'room-btn quiet', textContent: 'Закрыть комнату'});
    const leader = el('div', {className: 'room-card', id: 'room-leader', hidden: true},
      el('h3', {textContent: 'Вы лидер комнаты'}), code,
      el('p', {textContent: 'Отправьте другу приглашение (он вставит его в поле «Подключиться»). Нужна общая сеть: одна Wi-Fi/LAN либо VPN вроде Tailscale или Radmin. Порт 8788 разрешите в брандмауэре Windows.'}),
      invites, peer, close);

    const track = el('p');
    const leave = el('button', {type: 'button', className: 'room-btn quiet', textContent: 'Выйти из комнаты'});
    const guest = el('div', {className: 'room-card', id: 'room-guest', hidden: true},
      el('h3', {id: 'room-guest-title'}), track,
      el('p', {textContent: 'Вы только слушаете: треками управляет лидер. Громкость и лайк остаются у вас. Трек ищется в выбранном у вас сервисе: если его там нет, виджет покажет ошибку.'}), leave);

    const error = el('p', {className: 'room-error', hidden: true, role: 'alert'});
    panel.append(idle, leader, guest, error);
    els = {name, invite, code, invites, peer, track, guestTitle: guest.querySelector('#room-guest-title'), idle, leader, guest, error};

    create.addEventListener('click', () => act(create, () => call('room_create', myName())));
    join.addEventListener('click', () => act(join, () => call('room_join', invite.value.trim(), myName())));
    invite.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); join.click(); } });
    close.addEventListener('click', () => act(close, async () => { await call('room_leave'); return {role: 'none'}; }));
    leave.addEventListener('click', () => act(leave, async () => { await call('room_leave'); return {role: 'none'}; }));

    // значок в шапке главного окна
    const header = document.querySelector('.header'), actions = header && header.querySelector('.window-actions');
    if (header && actions) { badge = el('span', {className: 'room-badge'}, el('i'), el('span')); header.insertBefore(badge, actions); }
    return true;
  }

  function paint() {
    if (!els) return;
    const role = state.role || 'none';
    document.body.dataset.room = role;
    els.idle.hidden = role !== 'none'; els.leader.hidden = role !== 'leader'; els.guest.hidden = role !== 'guest';
    if (role === 'leader') {
      els.code.textContent = state.code || '';
      els.invites.textContent = '';
      for (const address of state.addresses || []) {
        const copy = el('button', {type: 'button', textContent: 'Копировать'});
        copy.addEventListener('click', async () => { try { await navigator.clipboard.writeText(address); copy.textContent = 'Скопировано'; setTimeout(() => { copy.textContent = 'Копировать'; }, 1500); } catch (_) { say('Не удалось скопировать: выделите приглашение вручную.'); } });
        els.invites.append(el('div', {className: 'room-invite'}, el('code', {textContent: address, title: address}), copy));
      }
      els.peer.textContent = state.peer ? 'Слушает: ' + state.peer : 'Ждём слушателя…';
    }
    if (role === 'guest') {
      els.guestTitle.textContent = 'Вы слушаете: ' + (state.leader || 'лидер');
      const t = state.track;
      els.track.textContent = t ? (t.playing ? '▶ ' : '❚❚ ') + [t.artist, t.title].filter(Boolean).join(' — ') : 'Ждём трек от лидера…';
    }
    if (state.error && !busy) say(state.error);
    if (badge) badge.lastChild.textContent = role === 'leader' ? (state.peer ? 'Лидер · ' + state.peer : 'Лидер') : role === 'guest' ? 'Слушаю' : '';
    if (badge) badge.title = role === 'leader' ? 'Вы лидер комнаты' + (state.peer ? ', слушает ' + state.peer : '') : role === 'guest' ? 'Вы слушаете: ' + (state.leader || 'лидер') : '';
  }

  async function refresh() {
    if (busy || document.hidden || !els) return;
    try { const value = await call('get_room'); if (value) { state = value; paint(); } } catch (_) {}
  }

  function init() {
    if (!build()) return;
    refresh();
    clearInterval(timer); timer = setInterval(refresh, 1500);
  }
  window.addEventListener('liquidready', init);
  if (document.readyState !== 'loading' && window.liquid) init();
})();
