'use strict';
/* Настройки «Discord» в окне настроек виджета. Подключается из C# (AddScriptToExecuteOnDocumentCreatedAsync),
   поэтому index.html менять не нужно. Хранит настройки через window.liquid.api.get_discord / set_discord. */
(() => {
  if (location.origin !== 'https://liquid.local') return;
  const api = () => window.liquid && window.liquid.api;
  const STATUS = {
    disabled: 'Выключено',
    no_client_id: 'Укажите Application ID, чтобы статус появился в Discord',
    waiting: 'Discord не найден. Запустите приложение Discord на этом компьютере',
    connected: 'Подключено',
    error: 'Не удалось подключиться'
  };
  let state = {enabled: true, client_id: '', show_artwork: true, show_paused: false, status: 'disabled', detail: '', user: '', active: false};
  let els = null, saveTimer = 0, busy = false;

  const css = `
    .discord-settings{display:flex;flex-direction:column;gap:10px;padding:12px;border-radius:var(--radius-s,13px);
      background:rgba(255,255,255,.05);box-shadow:inset 0 1px 0 rgba(255,255,255,.12)}
    .discord-settings h3{margin:0;font-size:13px;font-weight:600;color:var(--text,#f5f8ff)}
    .discord-settings .discord-hint{margin:0;font-size:11.5px;line-height:1.45;color:var(--text-2,rgba(235,242,255,.66))}
    .discord-settings .discord-id{display:flex;flex-direction:column;gap:5px;font-size:11.5px;color:var(--text-2,rgba(235,242,255,.66))}
    .discord-settings input[type=text]{width:100%;padding:8px 10px;border-radius:10px;border:1px solid var(--stroke,rgba(255,255,255,.16));
      background:rgba(255,255,255,.06);color:var(--text,#f5f8ff);font:inherit;letter-spacing:.04em;font-variant-numeric:tabular-nums}
    .discord-settings input[type=text]:focus-visible{outline:2px solid rgba(255,255,255,.55);outline-offset:1px}
    .discord-settings .discord-status{min-height:1.3em;margin:0;font-size:11.5px;color:var(--text-2,rgba(235,242,255,.66))}
    .discord-settings .discord-status.ok{color:#9be8b4}
    .discord-settings .discord-status.bad{color:#ffb4a0}
    .discord-settings[data-off] .discord-extra{opacity:.45;pointer-events:none}`;

  function switchRow(id, text) {
    const label = document.createElement('label');
    label.className = 'preference';
    label.innerHTML = '<svg><use href="#i-music"/></svg><span></span><input type="checkbox" class="switch"><span class="switch-track" aria-hidden="true"></span>';
    label.querySelector('span').textContent = text;
    label.querySelector('input').id = id;
    return label;
  }

  function build() {
    if (document.getElementById('discord-settings')) return document.getElementById('discord-settings');
    const panel = document.querySelector('#settings-dialog .settings-panel');
    if (!panel) return null;
    const style = document.createElement('style'); style.textContent = css; document.head.append(style);
    const section = document.createElement('section');
    section.id = 'discord-settings'; section.className = 'discord-settings'; section.setAttribute('aria-label', 'Discord');
    const title = document.createElement('h3'); title.textContent = 'Discord';
    const hint = document.createElement('p'); hint.className = 'discord-hint';
    hint.textContent = 'Показывает в вашем профиле Discord, какая музыка играет сейчас: название, исполнитель, обложка и время. Приложение Discord должно быть запущено на этом компьютере.';
    const extra = document.createElement('div'); extra.className = 'discord-extra'; extra.style.cssText = 'display:flex;flex-direction:column;gap:10px';
    const idLabel = document.createElement('label'); idLabel.className = 'discord-id';
    const idText = document.createElement('span'); idText.textContent = 'Application ID';
    const input = document.createElement('input'); input.type = 'text'; input.id = 'discord-client-id'; input.inputMode = 'numeric';
    input.maxLength = 20; input.autocomplete = 'off'; input.spellcheck = false; input.placeholder = '17–20 цифр';
    idLabel.append(idText, input);
    const idHint = document.createElement('p'); idHint.className = 'discord-hint';
    idHint.textContent = 'Создайте приложение на discord.com/developers, назовите его, например, «Liquid Music» (это название покажется после слова «Слушает»), и вставьте сюда его Application ID.';
    const artwork = switchRow('discord-artwork', 'Показывать обложку');
    const paused = switchRow('discord-paused', 'Оставлять статус на паузе');
    const enabled = switchRow('discord-enabled', 'Показывать трек в Discord');
    const status = document.createElement('p'); status.className = 'discord-status'; status.id = 'discord-status'; status.setAttribute('role', 'status');
    extra.append(idLabel, idHint, artwork, paused);
    section.append(title, enabled, hint, extra, status);
    const anchor = panel.querySelector('.settings-note') || panel.querySelector('#settings-error') || panel.querySelector('#save-shortcuts');
    if (anchor) anchor.before(section); else panel.append(section);
    els = {section, enabled: enabled.querySelector('input'), id: input, artwork: artwork.querySelector('input'), paused: paused.querySelector('input'), status};
    enabled.querySelector('input').addEventListener('change', () => commit());
    artwork.querySelector('input').addEventListener('change', () => commit());
    paused.querySelector('input').addEventListener('change', () => commit());
    input.addEventListener('input', () => { input.value = input.value.replace(/\D/g, '').slice(0, 20); clearTimeout(saveTimer); saveTimer = setTimeout(() => commit(), 700); });
    input.addEventListener('change', () => commit());
    input.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); commit(); } });
    return section;
  }

  function say(text, kind) { if (!els) return; els.status.textContent = text; els.status.className = 'discord-status' + (kind ? ' ' + kind : ''); }

  function paint() {
    if (!els) return;
    if (document.activeElement !== els.id) els.id.value = state.client_id || '';
    els.enabled.checked = !!state.enabled; els.artwork.checked = !!state.show_artwork; els.paused.checked = !!state.show_paused;
    els.section.toggleAttribute('data-off', !state.enabled);
    if (!state.enabled) return say(STATUS.disabled);
    if (state.status === 'connected') {
      const who = state.user ? ' как ' + state.user : '';
      return say(state.active ? 'Статус показан' + who : 'Подключено' + who + ', сейчас музыка не играет', 'ok');
    }
    if (state.status === 'error') return say(state.detail || STATUS.error, 'bad');
    say(STATUS[state.status] || state.detail || '', state.status === 'waiting' ? 'bad' : '');
  }

  async function load() {
    const a = api(); if (!a || !a.get_discord) return;
    try { const value = await a.get_discord(); if (value && value.ok !== false) { state = {...state, ...value}; paint(); } } catch (_) {}
  }

  async function commit() {
    if (!els || busy) return;
    clearTimeout(saveTimer);
    const id = els.id.value.trim();
    if (id && !/^\d{17,20}$/.test(id)) { say('Application ID состоит из 17–20 цифр', 'bad'); return; }
    const a = api(); if (!a || !a.set_discord) return;
    busy = true;
    try {
      const result = await a.set_discord({enabled: els.enabled.checked, client_id: id, show_artwork: els.artwork.checked, show_paused: els.paused.checked});
      if (result && result.ok === false) throw new Error(result.error || 'Не удалось сохранить');
      state = {...state, ...result}; paint();
    } catch (error) { say(error.message, 'bad'); }
    finally { busy = false; }
  }

  function init() {
    if (!build()) return;
    load();
    ['settings-button', 'keys-button'].forEach(id => { const el = document.getElementById(id); if (el) el.addEventListener('click', load); });
    setInterval(() => { const dialog = document.getElementById('settings-dialog'); if (dialog && dialog.open && !document.hidden) load(); }, 2000);
  }

  window.addEventListener('liquidready', init);
  if (document.readyState !== 'loading' && window.liquid) init();
})();
